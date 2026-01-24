# Keycloak Authentication Plugin

A plugin for Jellyfin to authenticate users against a Keycloak instance using Direct Access Grants.

## Compatibility

| Plugin Version | Jellyfin Version | .NET Version |
|----------------|------------------|--------------|
| 2.1.x          | 10.11.x          | .NET 9       |
| 2.0.x          | 10.8.x           | .NET 6       |
| 1.x            | 10.7.x           | .NET 5       |

## Requirements

* Keycloak client with `Direct Access Grants Enabled`
* The following roles defined on your Keycloak client:
  - `administrator` - Grants admin access in Jellyfin
  - `allowed_access` - Required to log in to Jellyfin
  - `allow_media_downloads` - Allows media downloads
* Map at least `allowed_access` to users who should access Jellyfin (directly or via group)

## Limitations

* **No token renewal/revoking:** If you delete/invalidate a user's session in Keycloak, the Jellyfin session remains active. However, if you remove the `allowed_access` role and the user logs in again, all Jellyfin sessions are revoked.

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

| Setting | Description |
|---------|-------------|
| Auth Server URL | Your Keycloak server URL (e.g., `https://auth.example.com/auth`) |
| Realm | The Keycloak realm name |
| Resource (Client ID) | The Keycloak client ID |
| Client Secret | The client secret (if confidential client) |
| Create User | Auto-create Jellyfin users for new Keycloak users |
| Enable 2FA | Require 2FA for login (if configured in Keycloak) |

## License

GPL-3.0 - See [LICENSE](LICENSE) for details.
