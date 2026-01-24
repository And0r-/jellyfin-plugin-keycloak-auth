using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Keycloak.Configuration;

/// <summary>
/// Role source options.
/// </summary>
public enum RoleSource
{
    /// <summary>
    /// Read roles from resource_access.{client}.roles (Client Roles).
    /// </summary>
    ClientRoles = 0,

    /// <summary>
    /// Read roles from realm_access.roles (Realm Roles).
    /// </summary>
    RealmRoles = 1
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // Keycloak Connection
        this.AuthServerUrl = string.Empty;
        this.Realm = string.Empty;
        this.Resource = string.Empty;
        this.ClientSecret = string.Empty;

        // User Settings
        this.CreateUser = true;
        this.Enable2Fa = false;

        // Role Configuration (defaults for backwards compatibility)
        this.RoleSource = RoleSource.ClientRoles;
        this.RoleSourceClient = "jellyfin";

        // Permission Mapping (defaults match old plugin behavior)
        this.AdminRole = string.Empty; // Legacy field, migrated to AdminRoles
        this.AdminRoles = "administrator";
        this.AllowedAccessRoles = "allowed_access";
        this.DownloadRoles = "allow_media_downloads";

        // Library Sync
        this.EnableLibrarySync = false;
        this.LibraryRoleMapping = string.Empty;
    }

    /// <summary>
    /// Gets or sets the Keycloak auth server URL.
    /// </summary>
    public string AuthServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the Keycloak realm.
    /// </summary>
    public string Realm { get; set; }

    /// <summary>
    /// Gets or sets the Keycloak resource/client ID for authentication.
    /// </summary>
    public string Resource { get; set; }

    /// <summary>
    /// Gets or sets the client secret.
    /// </summary>
    public string ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to create users automatically.
    /// </summary>
    public bool CreateUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 2FA is enabled.
    /// </summary>
    public bool Enable2Fa { get; set; }

    /// <summary>
    /// Gets or sets the role source (Client Roles or Realm Roles).
    /// </summary>
    public RoleSource RoleSource { get; set; }

    /// <summary>
    /// Gets or sets the client name for reading client roles.
    /// Only used when RoleSource is ClientRoles.
    /// </summary>
    public string RoleSourceClient { get; set; }

    /// <summary>
    /// Gets or sets the admin role (legacy, use AdminRoles instead).
    /// Kept for backwards compatibility with older configs.
    /// </summary>
    public string AdminRole { get; set; }

    /// <summary>
    /// Gets or sets the roles that grant Jellyfin administrator status (comma-separated).
    /// </summary>
    public string AdminRoles { get; set; }

    /// <summary>
    /// Gets or sets the roles that allow login to Jellyfin (comma-separated).
    /// </summary>
    public string AllowedAccessRoles { get; set; }

    /// <summary>
    /// Gets or sets the roles that allow media downloads (comma-separated).
    /// </summary>
    public string DownloadRoles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether library access should be synced based on roles.
    /// </summary>
    public bool EnableLibrarySync { get; set; }

    /// <summary>
    /// Gets or sets the JSON mapping of library IDs to required roles.
    /// Format: {"libraryId": "role1,role2", ...}
    /// </summary>
    public string LibraryRoleMapping { get; set; }
}
