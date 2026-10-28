#!/bin/bash
# Builds the plugin and writes what a Jellyfin plugin repository needs:
#   dist/rink-board_<version>.zip   the plugin, as Jellyfin installs it
#   manifest.json                   the catalog entry that points at that zip
# Usage: ./package.sh [download-base-url]
#   download-base-url defaults to this version's GitHub release:
#   https://github.com/bbene/jellyfin-plugin-rinkboard/releases/download/v<version>
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Jellyfin.Plugin.RinkBoard/Jellyfin.Plugin.RinkBoard.csproj)
GUID=7b311deb-536a-415d-baa6-00abdaa6e566
TARGET_ABI=10.11.0.0
BASE_URL=${1:-https://github.com/bbene/jellyfin-plugin-rinkboard/releases/download/v$VERSION}
CHANGELOG=${CHANGELOG:-"Rink Board $VERSION"}
STAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)
ZIP=rink-board_$VERSION.zip

dotnet publish Jellyfin.Plugin.RinkBoard -c Release -o build/publish --nologo -v quiet
rm -rf build/zip dist/"$ZIP" && mkdir -p build/zip dist
cp build/publish/Jellyfin.Plugin.RinkBoard.dll build/zip/
cat > build/zip/meta.json <<JSON
{
  "category": "General",
  "changelog": "$CHANGELOG",
  "description": "Build a hockey soundboard in the browser and play it offline on tablets with the Rink Board app.",
  "guid": "$GUID",
  "name": "Rink Board",
  "overview": "Hockey soundboard: board editor and tablet sync",
  "owner": "bbene",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$STAMP",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": ["Jellyfin.Plugin.RinkBoard.dll"]
}
JSON
(cd build/zip && zip -q -X ../../dist/"$ZIP" Jellyfin.Plugin.RinkBoard.dll meta.json)
SUM=$(md5 -q dist/"$ZIP" 2>/dev/null || md5sum dist/"$ZIP" | cut -d' ' -f1)

# Add this version to manifest.json (newest first), replacing an older entry for it.
python3 - "$VERSION" "$TARGET_ABI" "$BASE_URL/$ZIP" "$SUM" "$STAMP" "$CHANGELOG" <<'PY'
import json, os, sys
version, abi, url, checksum, stamp, changelog = sys.argv[1:]
path = 'manifest.json'
plugins = json.load(open(path)) if os.path.exists(path) else []
if not plugins:
    plugins = [{
        "guid": "7b311deb-536a-415d-baa6-00abdaa6e566",
        "name": "Rink Board",
        "description": "Build a hockey soundboard in the browser and play it offline on tablets with the Rink Board app.",
        "overview": "Hockey soundboard: board editor and tablet sync",
        "owner": "bbene",
        "category": "General",
        "versions": [],
    }]
entry = {"version": version, "changelog": changelog, "targetAbi": abi, "sourceUrl": url, "checksum": checksum, "timestamp": stamp}
plugins[0]["versions"] = [entry] + [v for v in plugins[0]["versions"] if v["version"] != version]
json.dump(plugins, open(path, "w"), indent=2)
print(f"dist/rink-board_{version}.zip  md5 {checksum}")
PY
