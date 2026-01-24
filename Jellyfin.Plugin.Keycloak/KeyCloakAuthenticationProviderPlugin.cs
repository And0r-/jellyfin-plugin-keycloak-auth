using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JWT.Builder;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Keycloak
{
    /// <summary>
    /// KeyCloak Authentication Provider Plugin.
    /// Supports authentication via Keycloak Direct Access Grants and
    /// optional library access sync based on Keycloak roles.
    /// </summary>
    public class KeyCloakAuthenticationProviderPlugin : IAuthenticationProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<KeyCloakAuthenticationProviderPlugin> _logger;
        private readonly IApplicationHost _applicationHost;
        private readonly string _twoFactorPattern = @"(.*)_2FA=(.*)$";

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyCloakAuthenticationProviderPlugin"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="applicationHost">Instance of the <see cref="IApplicationHost"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
        public KeyCloakAuthenticationProviderPlugin(
            IHttpClientFactory httpClientFactory,
            IApplicationHost applicationHost,
            ILogger<KeyCloakAuthenticationProviderPlugin> logger)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _applicationHost = applicationHost;
        }

        private static bool CreateUser => Plugin.Instance?.Configuration.CreateUser ?? false;

        private static string AuthServerUrl => Plugin.Instance?.Configuration.AuthServerUrl ?? string.Empty;

        private static string Realm => Plugin.Instance?.Configuration.Realm ?? string.Empty;

        private static string Resource => Plugin.Instance?.Configuration.Resource ?? string.Empty;

        private static string ClientSecret => Plugin.Instance?.Configuration.ClientSecret ?? string.Empty;

        private static bool Enable2Fa => Plugin.Instance?.Configuration.Enable2Fa ?? false;

        private static bool EnableLibrarySync => Plugin.Instance?.Configuration.EnableLibrarySync ?? false;

        private static Configuration.RoleSource RoleSource => Plugin.Instance?.Configuration.RoleSource ?? Configuration.RoleSource.ClientRoles;

        private static string RoleSourceClient => Plugin.Instance?.Configuration.RoleSourceClient ?? "jellyfin";

        private static string AdminRoles
        {
            get
            {
                var config = Plugin.Instance?.Configuration;
                // Fallback to legacy AdminRole if AdminRoles is empty (backwards compatibility)
                if (string.IsNullOrWhiteSpace(config?.AdminRoles) && !string.IsNullOrWhiteSpace(config?.AdminRole))
                {
                    return config.AdminRole;
                }

                return config?.AdminRoles ?? "administrator";
            }
        }

        private static string AllowedAccessRoles => Plugin.Instance?.Configuration.AllowedAccessRoles ?? "allowed_access";

        private static string DownloadRoles => Plugin.Instance?.Configuration.DownloadRoles ?? "allow_media_downloads";

        private static string LibraryRoleMapping => Plugin.Instance?.Configuration.LibraryRoleMapping ?? string.Empty;

        private string TokenUri => $"{AuthServerUrl}/realms/{Realm}/protocol/openid-connect/token";

        /// <inheritdoc />
        public string Name => "Keycloak-Authentication";

        /// <inheritdoc />
        public bool IsEnabled => true;

        private HttpClient GetHttpClient()
        {
            return _httpClientFactory.CreateClient(NamedClient.Default);
        }

        private async Task<KeycloakUser?> GetKeycloakUser(string username, string password, string? totp)
        {
            var httpClient = GetHttpClient();
            var keyValues = new List<KeyValuePair<string, string?>>
            {
                new KeyValuePair<string, string?>("username", username),
                new KeyValuePair<string, string?>("password", password),
                new KeyValuePair<string, string?>("grant_type", "password"),
                new KeyValuePair<string, string?>("client_id", Resource)
            };

            if (!string.IsNullOrWhiteSpace(ClientSecret))
            {
                keyValues.Add(new KeyValuePair<string, string?>("client_secret", ClientSecret));
            }

            if (!string.IsNullOrWhiteSpace(totp))
            {
                keyValues.Add(new KeyValuePair<string, string?>("totp", totp));
            }

            var content = new FormUrlEncodedContent(keyValues);
            var response = await httpClient.PostAsync(TokenUri, content).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Keycloak authentication failed with status: {Status}", response.StatusCode);
                return null;
            }

            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            KeycloakTokenResponse? parsed = await JsonSerializer.DeserializeAsync<KeycloakTokenResponse>(responseStream).ConfigureAwait(false);

            if (parsed?.AccessToken == null)
            {
                return null;
            }

            try
            {
                var jwtToken = JwtBuilder.Create().Decode<IDictionary<string, object>>(parsed.AccessToken);
                var allRoles = new HashSet<string>();

                // Read roles based on configured source
                if (RoleSource == Configuration.RoleSource.ClientRoles)
                {
                    // Read client roles from resource_access.<client>.roles
                    try
                    {
                        if (jwtToken.TryGetValue("resource_access", out var resourceAccessObj) && resourceAccessObj is JObject resourceAccess)
                        {
                            var clientName = RoleSourceClient;
                            if (resourceAccess[clientName] is JObject clientAccess && clientAccess["roles"] is JArray clientRoles)
                            {
                                foreach (var role in clientRoles.ToObject<List<string>>() ?? new List<string>())
                                {
                                    allRoles.Add(role);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not parse client roles for client: {Client}", RoleSourceClient);
                    }
                }
                else
                {
                    // Read realm roles from realm_access.roles
                    try
                    {
                        if (jwtToken.TryGetValue("realm_access", out var realmAccessObj) && realmAccessObj is JObject realmAccess)
                        {
                            if (realmAccess["roles"] is JArray realmRoles)
                            {
                                foreach (var role in realmRoles.ToObject<List<string>>() ?? new List<string>())
                                {
                                    allRoles.Add(role);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not parse realm roles");
                    }
                }

                _logger.LogInformation(
                    "Keycloak user {Username} authenticated with {RoleSource} roles: {Roles}",
                    username,
                    RoleSource,
                    string.Join(", ", allRoles));

                KeycloakUser user = new KeycloakUser(username);
                foreach (var role in allRoles)
                {
                    user.Permissions.Add(role);
                }

                return user;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing jwt token");
            }

            return null;
        }

        /// <summary>
        /// Parses the library role mapping from JSON config.
        /// </summary>
        /// <returns>Dictionary mapping library IDs to required roles.</returns>
        private Dictionary<Guid, HashSet<string>> ParseLibraryRoleMapping()
        {
            var result = new Dictionary<Guid, HashSet<string>>();

            if (string.IsNullOrWhiteSpace(LibraryRoleMapping))
            {
                return result;
            }

            try
            {
                var mapping = JObject.Parse(LibraryRoleMapping);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse LibraryRoleMapping JSON");
            }

            return result;
        }

        /// <summary>
        /// Parses a comma-separated role string into a HashSet.
        /// </summary>
        private static HashSet<string> ParseRoleList(string roleList)
        {
            if (string.IsNullOrWhiteSpace(roleList))
            {
                return new HashSet<string>();
            }

            return roleList
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim().ToLowerInvariant())
                .Where(r => !string.IsNullOrEmpty(r))
                .ToHashSet();
        }

        /// <summary>
        /// Checks if user has any of the allowed access roles.
        /// </summary>
        private bool HasAllowedAccess(KeycloakUser keycloakUser)
        {
            var allowedRoles = ParseRoleList(AllowedAccessRoles);
            if (allowedRoles.Count == 0)
            {
                // No allowed roles configured = everyone allowed
                return true;
            }

            var userRoles = keycloakUser.Permissions
                .Select(p => p.ToLowerInvariant())
                .ToHashSet();

            return userRoles.Overlaps(allowedRoles);
        }

        /// <summary>
        /// Syncs user permissions based on Keycloak roles.
        /// </summary>
        private async Task SyncUserPermissions(User user, KeycloakUser keycloakUser, IUserManager userManager)
        {
            var userRoles = keycloakUser.Permissions
                .Select(p => p.ToLowerInvariant())
                .ToHashSet();

            _logger.LogDebug("Syncing permissions for user {Username} with roles: {Roles}", user.Username, string.Join(", ", userRoles));

            // Check for admin roles
            var adminRoles = ParseRoleList(AdminRoles);
            bool shouldBeAdmin = userRoles.Overlaps(adminRoles);
            if (user.HasPermission(PermissionKind.IsAdministrator) != shouldBeAdmin)
            {
                user.SetPermission(PermissionKind.IsAdministrator, shouldBeAdmin);
                _logger.LogInformation("Set admin status for user {Username} to {IsAdmin}", user.Username, shouldBeAdmin);
            }

            // Check for download roles
            var downloadRoles = ParseRoleList(DownloadRoles);
            bool canDownload = userRoles.Overlaps(downloadRoles);
            if (user.HasPermission(PermissionKind.EnableContentDownloading) != canDownload)
            {
                user.SetPermission(PermissionKind.EnableContentDownloading, canDownload);
                _logger.LogInformation("Set download permission for user {Username} to {CanDownload}", user.Username, canDownload);
            }

            // Skip library sync if disabled
            if (!EnableLibrarySync)
            {
                await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                return;
            }

            // Parse library mappings
            var libraryMapping = ParseLibraryRoleMapping();
            if (libraryMapping.Count == 0)
            {
                _logger.LogDebug("No library role mappings configured, skipping library sync");
                await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                return;
            }

            // Calculate which libraries the user should have access to
            var allowedLibraries = new List<Guid>();
            foreach (var (libraryId, requiredRoles) in libraryMapping)
            {
                bool hasAccess = userRoles.Overlaps(requiredRoles);
                if (hasAccess)
                {
                    allowedLibraries.Add(libraryId);
                }

                _logger.LogDebug(
                    "Library {LibraryId}: required roles [{RequiredRoles}], user has access: {HasAccess}",
                    libraryId,
                    string.Join(", ", requiredRoles),
                    hasAccess);
            }

            // Disable "enable all folders" and set specific folder access
            user.SetPermission(PermissionKind.EnableAllFolders, false);

            // Clear existing folder preferences and set new ones
            var currentFolders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
            var newFolders = allowedLibraries.ToArray();

            // Only update if different
            if (!currentFolders.SequenceEqual(newFolders))
            {
                user.SetPreference(PreferenceKind.EnabledFolders, newFolders);
                _logger.LogInformation(
                    "Updated library access for user {Username}: {Libraries}",
                    user.Username,
                    string.Join(", ", allowedLibraries));
            }

            await userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            var userManager = _applicationHost.Resolve<IUserManager>();
            string? totp = null;

            if (Enable2Fa)
            {
                var match = Regex.Match(password, _twoFactorPattern);
                if (match.Success)
                {
                    password = match.Groups[1].Value;
                    totp = match.Groups[2].Value;
                }
            }

            User? user = null;
            try
            {
                user = userManager.GetUserByName(username);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "User Manager could not find user {Username}, will create if enabled", username);
            }

            KeycloakUser? keycloakUser = await GetKeycloakUser(username, password, totp).ConfigureAwait(false);
            if (keycloakUser == null)
            {
                throw new AuthenticationException("Error completing Keycloak login. Invalid username or password.");
            }

            // Check if user has required access roles
            if (!HasAllowedAccess(keycloakUser))
            {
                _logger.LogWarning(
                    "Keycloak user {Username} denied: missing required access roles. User roles: [{UserRoles}], Required: [{RequiredRoles}]",
                    username,
                    string.Join(", ", keycloakUser.Permissions),
                    AllowedAccessRoles);
                throw new AuthenticationException("Access denied. You don't have the required roles to access Jellyfin.");
            }

            if (user == null)
            {
                if (CreateUser)
                {
                    _logger.LogInformation("Creating Jellyfin user for Keycloak user: {Username}", username);
                    user = await userManager.CreateUserAsync(username).ConfigureAwait(false);
                    var userAuthenticationProviderId = GetType().FullName;
                    if (userAuthenticationProviderId != null)
                    {
                        user.AuthenticationProviderId = userAuthenticationProviderId;
                        await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    }

                    // Sync permissions for new user
                    await SyncUserPermissions(user, keycloakUser, userManager).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError("Keycloak User not configured for Jellyfin: {Username}", username);
                    throw new AuthenticationException(
                        $"Automatic User Creation is disabled and there is no Jellyfin user for authorized Uid: {username}");
                }
            }
            else
            {
                // Sync permissions for existing user on each login
                await SyncUserPermissions(user, keycloakUser, userManager).ConfigureAwait(false);
            }

            return new ProviderAuthenticationResult { Username = username };
        }

        /// <inheritdoc />
        public bool HasPassword(User user)
        {
            return true;
        }

        /// <inheritdoc />
        public Task ChangePassword(User user, string newPassword)
        {
            throw new NotImplementedException("Password changes must be done in Keycloak");
        }
    }
}
