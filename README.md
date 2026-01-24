# Keycloak Authentication Plugin

A plugin for Jellyfin to authenticate users against a Keycloak instance using Direct Access Grants.

## Compatibility

| Plugin Version | Jellyfin Version | .NET Version |
|----------------|------------------|--------------|
| 3.0.x          | 10.11.x          | .NET 9       |
| 2.0.x          | 10.8.x           | .NET 6       |
| 1.x            | 10.7.x           | .NET 5       |

## Features

- **Flexible Role Source:** Choose between Keycloak Client Roles or Realm Roles
- **Configurable Permission Mapping:** Map any Keycloak role to Jellyfin permissions
- **Library Access Sync:** Automatically sync library access based on Keycloak roles
- **Backwards Compatible:** Default configuration works with existing setups

## Requirements

* Keycloak client with `Direct Access Grants Enabled`
* Roles configured in Keycloak (Client Roles or Realm Roles)

### Default Role Names (Backwards Compatible)

If you don't change the default configuration:
- `administrator` - Grants admin access in Jellyfin
- `allowed_access` - Required to log in to Jellyfin
- `allow_media_downloads` - Allows media downloads

### Custom Role Names

You can configure any role names in the plugin settings. For example:
- Admin Roles: `admin, superuser`
- Allowed Access Roles: `user, member, admin`
- Download Roles: `premium, admin`

## Limitations

* **No token renewal/revoking:** If you delete/invalidate a user's session in Keycloak, the Jellyfin session remains active. However, if you remove the access role and the user logs in again, all Jellyfin sessions are revoked.

* **No true SSO:** Users must authenticate to Jellyfin even if already signed into the Keycloak realm.

* **Not OAuth2/OIDC:** This plugin validates username/password via Direct Access Grants (Resource Owner Password Credentials). This allows authentication from ALL clients (TV apps, mobile apps, web) without browser redirects.

## Build

### Using Docker (recommended)

```bash
# Build with .NET 9 SDK container
docker run --rm -v "$(pwd):/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish -c Release Jellyfin.Plugin.Keycloak/Jellyfin.Plugin.Keycloak.csproj -o /src/publish
```

### Using local .NET SDK

```bash
# Requires .NET 9 SDK installed
dotnet publish -c Release Jellyfin.Plugin.Keycloak/Jellyfin.Plugin.Keycloak.csproj -o publish
```

## Installation

### Option 1: Plugin Repository (Recommended)

Add one of these repository URLs in Jellyfin under **Dashboard → Plugins → Repositories → Add**:

| Channel | URL | Description |
|---------|-----|-------------|
| **Stable** | `https://raw.githubusercontent.com/And0r-/jellyfin-plugin-keycloak-auth/master/manifest.json` | Stable releases only |
| **Dev** | `https://raw.githubusercontent.com/And0r-/jellyfin-plugin-keycloak-auth/master/manifest-dev.json` | Latest development builds |

Then install from **Catalog → Authentication → Keycloak Authentication**.

### Option 2: Manual Installation

1. Create a directory called `Keycloak` in your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/Keycloak/`
   - Windows: `%localappdata%\jellyfin\plugins\Keycloak\`
   - Docker: `<config-volume>/plugins/Keycloak/`

2. Copy the following files from `publish/`:
   - `Jellyfin.Plugin.Keycloak.dll`
   - `JWT.dll`
   - `Newtonsoft.Json.dll`

3. Restart Jellyfin

4. Configure the plugin in: **Admin Dashboard -> Plugins -> Keycloak-Auth**

## Configuration

### Keycloak Connection

| Setting | Description |
|---------|-------------|
| Auth Server URL | Your Keycloak server URL (e.g., `https://auth.example.com/auth`) |
| Realm | The Keycloak realm name |
| Resource (Client ID) | The Keycloak client ID |
| Client Secret | The client secret (leave empty for public clients) |

### User Settings

| Setting | Description |
|---------|-------------|
| Create User | Auto-create Jellyfin users for new Keycloak users |
| Enable 2FA | Append `_2FA=CODE` to password for TOTP |

### Role Configuration

| Setting | Description |
|---------|-------------|
| Role Source | Where to read roles from: Client Roles or Realm Roles |
| Client Name for Roles | Client name when using Client Roles (default: same as Resource) |

### Permission Mapping

| Setting | Description | Default |
|---------|-------------|---------|
| Admin Roles | Roles that grant Jellyfin admin status | `administrator` |
| Allowed Access Roles | Roles required to log in | `allowed_access` |
| Download Roles | Roles that allow media downloads | `allow_media_downloads` |

### Library Access Sync

| Setting | Description |
|---------|-------------|
| Enable Library Sync | Sync library access based on roles on each login |
| Library Role Mapping | For each library, specify which roles have access |

## Upgrading from v2.x

The plugin is backwards compatible. Your existing configuration will continue to work.

New features are opt-in:
- Role Source defaults to Client Roles
- Permission roles default to the original role names
- Library Sync is disabled by default

## Credits

- **Original Author:** [Ugrend](https://github.com/Ugrend) - Created the initial plugin
- **Maintained by:** [And0r-](https://github.com/And0r-) - Jellyfin 10.11+ support, configurable roles, library sync

This is an actively maintained fork of the original [jellyfin-plugin-keycloak](https://github.com/Ugrend/jellyfin-plugin-keycloak) plugin.

## License

GPL-3.0 - See [LICENSE](LICENSE) for details.
