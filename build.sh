#!/bin/bash
# Build Jellyfin Keycloak Auth Plugin using Docker
# Usage: ./build.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Jellyfin Keycloak Auth Plugin Build ==="
echo ""

# Clean previous build
echo "[1/2] Cleaning previous build..."
rm -rf "$SCRIPT_DIR/publish" "$SCRIPT_DIR/Jellyfin.Plugin.Keycloak/bin" "$SCRIPT_DIR/Jellyfin.Plugin.Keycloak/obj"

# Build with Docker
echo "[2/2] Building with .NET 9 SDK (Docker)..."
docker run --rm -v "$SCRIPT_DIR:/src" -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish -c Release Jellyfin.Plugin.Keycloak/Jellyfin.Plugin.Keycloak.csproj -o /src/publish

# Check build success
if [ ! -f "$SCRIPT_DIR/publish/Jellyfin.Plugin.Keycloak.dll" ]; then
  echo "Build failed!"
  exit 1
fi

echo ""
echo "=== Build successful ==="
echo ""
echo "Output files in publish/:"
ls -la "$SCRIPT_DIR/publish/"*.dll
echo ""
echo "Copy these files to your Jellyfin plugins directory:"
echo "  - Jellyfin.Plugin.Keycloak.dll"
echo "  - JWT.dll"
echo "  - Newtonsoft.Json.dll"
