using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Keycloak
{
    /// <summary>
    /// Enforces Keycloak-based library access permissions.
    /// Hooks into: UserUpdated (drift detection), AuthenticationResult (all logins incl. Quick Connect),
    /// and ILibraryPostScanTask (after library scans).
    /// Falls back to Keycloak Admin API when no cached roles are available.
    /// </summary>
    public class LibraryAccessEnforcer :
        IEventConsumer<UserUpdatedEventArgs>,
        IEventConsumer<AuthenticationResultEventArgs>,
        ILibraryPostScanTask
    {
        private static readonly ConcurrentDictionary<string, HashSet<string>> _userRolesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<LibraryAccessEnforcer> _logger;

        // Prevent re-entrancy when we update the user ourselves
        private static readonly ConcurrentDictionary<string, bool> _updating = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryAccessEnforcer"/> class.
        /// </summary>
        public LibraryAccessEnforcer(
            IUserManager userManager,
            IHttpClientFactory httpClientFactory,
            ILogger<LibraryAccessEnforcer> logger)
        {
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Caches the Keycloak roles for a user (called from auth provider on login).
        /// </summary>
        public static void CacheUserRoles(string username, HashSet<string> roles)
        {
            _userRolesCache[username] = roles;
        }

        /// <summary>
        /// Handles any successful authentication (including Quick Connect).
        /// Ensures library access is enforced even without Keycloak password auth.
        /// </summary>
        public async Task OnEvent(AuthenticationResultEventArgs e)
        {
            var username = e.User?.Name;
            if (string.IsNullOrEmpty(username))
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableLibrarySync)
            {
                return;
            }

            var roles = await GetUserRoles(username).ConfigureAwait(false);
            if (roles == null || roles.Count == 0)
            {
                _logger.LogWarning("No roles available for user {Username} on auth event, skipping library enforcement", username);
                return;
            }

            var user = _userManager.GetUserByName(username);
            if (user == null)
            {
                return;
            }

            _logger.LogInformation("Auth event for user {Username}, enforcing library access", username);
            await EnforceLibraryAccess(user, roles).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles UserUpdated events - re-enforces library access if permissions drifted.
        /// </summary>
        public async Task OnEvent(UserUpdatedEventArgs e)
        {
            var username = e.Argument.Username;

            // Skip if we're the ones updating
            if (_updating.TryGetValue(username, out var isUpdating) && isUpdating)
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableLibrarySync)
            {
                return;
            }

            var roles = await GetUserRoles(username).ConfigureAwait(false);
            if (roles == null || roles.Count == 0)
            {
                return;
            }

            var user = _userManager.GetUserByName(username);
            if (user == null)
            {
                return;
            }

            await EnforceLibraryAccess(user, roles).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs after a library scan completes - re-enforces library access for all known users.
        /// </summary>
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableLibrarySync)
            {
                return;
            }

            // Get all Jellyfin users that use Keycloak auth
            var allUsers = _userManager.Users
                .Where(u => u.AuthenticationProviderId == typeof(KeyCloakAuthenticationProviderPlugin).FullName)
                .ToList();

            _logger.LogInformation("Post-scan: enforcing library access for {Count} Keycloak users", allUsers.Count);

            for (int i = 0; i < allUsers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var user = allUsers[i];

                try
                {
                    var roles = await GetUserRoles(user.Username).ConfigureAwait(false);
                    if (roles != null && roles.Count > 0)
                    {
                        await EnforceLibraryAccess(user, roles).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enforce library access for user {Username}", user.Username);
                }

                progress.Report((double)(i + 1) / allUsers.Count * 100);
            }
        }

        /// <summary>
        /// Manually syncs library access for all Keycloak users (called from admin API).
        /// </summary>
        public async Task<SyncResult> SyncAllUsers(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var result = new SyncResult();

            if (config == null || !config.EnableLibrarySync)
            {
                return result;
            }

            var allUsers = _userManager.Users
                .Where(u => u.AuthenticationProviderId == typeof(KeyCloakAuthenticationProviderPlugin).FullName)
                .ToList();

            _logger.LogInformation("Manual sync: processing {Count} Keycloak users", allUsers.Count);

            foreach (var user in allUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Always fetch fresh from Keycloak for manual sync
                    var roles = await FetchRolesFromKeycloak(user.Username).ConfigureAwait(false);
                    if (roles != null && roles.Count > 0)
                    {
                        _userRolesCache[user.Username] = roles;
                        await EnforceLibraryAccess(user, roles).ConfigureAwait(false);
                        result.SyncedUsers++;
                    }
                    else
                    {
                        _logger.LogWarning("Could not fetch roles for user {Username}", user.Username);
                        result.Errors++;
                    }
                }
                catch (System.Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync user {Username}", user.Username);
                    result.Errors++;
                }
            }

            _logger.LogInformation("Manual sync complete: {Synced} synced, {Errors} errors", result.SyncedUsers, result.Errors);
            return result;
        }

        /// <summary>
        /// Gets user roles from cache, or fetches them from Keycloak Admin API if not cached.
        /// </summary>
        private async Task<HashSet<string>?> GetUserRoles(string username)
        {
            // Try cache first
            if (_userRolesCache.TryGetValue(username, out var cachedRoles))
            {
                return cachedRoles;
            }

            // Fetch from Keycloak
            var roles = await FetchRolesFromKeycloak(username).ConfigureAwait(false);
            if (roles != null && roles.Count > 0)
            {
                _userRolesCache[username] = roles;
            }

            return roles;
        }

        /// <summary>
        /// Fetches user roles from Keycloak using client credentials grant + admin API.
        /// </summary>
        private async Task<HashSet<string>?> FetchRolesFromKeycloak(string username)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.AuthServerUrl) ||
                string.IsNullOrWhiteSpace(config.Realm) || string.IsNullOrWhiteSpace(config.ClientSecret))
            {
                return null;
            }

            try
            {
                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);

                // Step 1: Get service account token via client credentials grant
                var tokenUrl = $"{config.AuthServerUrl}/realms/{config.Realm}/protocol/openid-connect/token";
                var tokenRequest = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string?>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string?>("client_id", config.Resource),
                    new KeyValuePair<string, string?>("client_secret", config.ClientSecret),
                });

                var tokenResponse = await httpClient.PostAsync(tokenUrl, tokenRequest).ConfigureAwait(false);
                if (!tokenResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to get service account token from Keycloak: {Status}", tokenResponse.StatusCode);
                    return null;
                }

                var tokenJson = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var tokenObj = JObject.Parse(tokenJson);
                var accessToken = tokenObj["access_token"]?.ToString();
                if (string.IsNullOrEmpty(accessToken))
                {
                    return null;
                }

                // Step 2: Find user by username
                var usersUrl = $"{config.AuthServerUrl}/admin/realms/{config.Realm}/users?username={Uri.EscapeDataString(username)}&exact=true";
                var usersRequest = new HttpRequestMessage(HttpMethod.Get, usersUrl);
                usersRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var usersResponse = await httpClient.SendAsync(usersRequest).ConfigureAwait(false);
                if (!usersResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query Keycloak users API: {Status}", usersResponse.StatusCode);
                    return null;
                }

                var usersJson = await usersResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var users = JArray.Parse(usersJson);
                if (users.Count == 0)
                {
                    _logger.LogWarning("User {Username} not found in Keycloak", username);
                    return null;
                }

                var keycloakUserId = users[0]["id"]?.ToString();
                if (string.IsNullOrEmpty(keycloakUserId))
                {
                    return null;
                }

                // Step 3: Get realm role mappings for user
                var rolesUrl = $"{config.AuthServerUrl}/admin/realms/{config.Realm}/users/{keycloakUserId}/role-mappings/realm/composite";
                var rolesRequest = new HttpRequestMessage(HttpMethod.Get, rolesUrl);
                rolesRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var rolesResponse = await httpClient.SendAsync(rolesRequest).ConfigureAwait(false);
                if (!rolesResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query Keycloak role mappings: {Status}", rolesResponse.StatusCode);
                    return null;
                }

                var rolesJson = await rolesResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var rolesArray = JArray.Parse(rolesJson);
                var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var role in rolesArray)
                {
                    var roleName = role["name"]?.ToString();
                    if (!string.IsNullOrEmpty(roleName))
                    {
                        roles.Add(roleName.ToLowerInvariant());
                    }
                }

                _logger.LogInformation(
                    "Fetched Keycloak roles for user {Username}: {Roles}",
                    username,
                    string.Join(", ", roles));

                return roles;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch roles from Keycloak for user {Username}", username);
                return null;
            }
        }

        private async Task EnforceLibraryAccess(User user, HashSet<string> userRoles)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.LibraryRoleMapping))
            {
                return;
            }

            var libraryMapping = ParseLibraryRoleMapping(config.LibraryRoleMapping);
            if (libraryMapping.Count == 0)
            {
                return;
            }

            var lowerRoles = userRoles.Select(r => r.ToLowerInvariant()).ToHashSet();

            // Calculate allowed libraries
            var allowedLibraries = new List<Guid>();
            foreach (var (libraryId, requiredRoles) in libraryMapping)
            {
                if (lowerRoles.Overlaps(requiredRoles))
                {
                    allowedLibraries.Add(libraryId);
                }
            }

            var newFolders = allowedLibraries.ToArray();
            var currentFolders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
            bool enableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders);

            // Check if permissions drifted
            if (enableAllFolders || !currentFolders.SequenceEqual(newFolders))
            {
                _logger.LogWarning(
                    "Library access drift detected for user {Username} (EnableAllFolders={EnableAll}). Re-enforcing.",
                    user.Username,
                    enableAllFolders);

                _updating[user.Username] = true;
                try
                {
                    user.SetPermission(PermissionKind.EnableAllFolders, false);
                    user.SetPreference(PreferenceKind.EnabledFolders, newFolders);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Re-enforced library access for user {Username}: {Libraries}",
                        user.Username,
                        string.Join(", ", allowedLibraries));
                }
                finally
                {
                    _updating[user.Username] = false;
                }
            }
        }

        private static Dictionary<Guid, HashSet<string>> ParseLibraryRoleMapping(string mappingJson)
        {
            var result = new Dictionary<Guid, HashSet<string>>();

            try
            {
                var mapping = JObject.Parse(mappingJson);
                foreach (var prop in mapping.Properties())
                {
                    if (Guid.TryParse(prop.Name, out var libraryId))
                    {
                        var roles = prop.Value.ToString()
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(r => r.Trim().ToLowerInvariant())
                            .Where(r => !string.IsNullOrEmpty(r))
                            .ToHashSet();

                        if (roles.Count > 0)
                        {
                            result[libraryId] = roles;
                        }
                    }
                }
            }
            catch
            {
                // Silently fail - ParseLibraryRoleMapping in auth provider already logs
            }

            return result;
        }
    }

    /// <summary>
    /// Result of a sync operation.
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Gets or sets the number of successfully synced users.
        /// </summary>
        public int SyncedUsers { get; set; }

        /// <summary>
        /// Gets or sets the number of errors.
        /// </summary>
        public int Errors { get; set; }
    }
}
