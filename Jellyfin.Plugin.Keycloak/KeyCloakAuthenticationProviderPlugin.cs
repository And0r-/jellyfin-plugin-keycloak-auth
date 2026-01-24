using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
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
    /// Phase 1: Basic authentication only (no role-based permissions yet).
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
                Collection<string> perms = new Collection<string>();

                try
                {
                    if (jwtToken.TryGetValue("resource_access", out var resourceAccessObj) && resourceAccessObj is JObject resourceAccess)
                    {
                        if (resourceAccess[Resource] is JObject clientAccess && clientAccess["roles"] is JArray roles)
                        {
                            var rolesList = roles.ToObject<Collection<string>>();
                            if (rolesList != null)
                            {
                                perms = rolesList;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not parse permissions for resource: {Resource}", Resource);
                }

                KeycloakUser user = new KeycloakUser(username);
                foreach (var perm in perms)
                {
                    user.Permissions.Add(perm);
                }

                _logger.LogInformation("Keycloak user {Username} authenticated with permissions: {Permissions}", username, string.Join(", ", perms));
                return user;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing jwt token");
            }

            return null;
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

                    // TODO Phase 2: Set permissions based on Keycloak roles
                }
                else
                {
                    _logger.LogError("Keycloak User not configured for Jellyfin: {Username}", username);
                    throw new AuthenticationException(
                        $"Automatic User Creation is disabled and there is no Jellyfin user for authorized Uid: {username}");
                }
            }

            // TODO Phase 2: Update permissions based on Keycloak roles on each login

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
