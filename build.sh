#!/usr/bin/env bash
# Builds SwbsJailbreak (.NET 10 / CounterStrikeSharp 1.0.376) and produces the drop-in server package:
#   artifacts/SwbsJailbreak-<version>-net10.zip  ->  contains addons/counterstrikesharp/plugins/SwbsJailbreak/...
# Unzip it into game/csgo/. Requires the .NET 10 SDK.
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${1:-Release}"
VERSION=$(grep -oP 'PluginVersion = "\K[^"]+' src/SwbsJailbreak/JailbreakPlugin.cs || echo dev)

rm -rf artifacts
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c "$CONFIG"

OUT="artifacts/net10"
PLUGIN_DIR="$OUT/addons/counterstrikesharp/plugins/SwbsJailbreak"
BIN="src/SwbsJailbreak/bin/$CONFIG/net10.0"
mkdir -p "$PLUGIN_DIR"
cp "$BIN/SwbsJailbreak.dll" "$PLUGIN_DIR/"
[ -f "$BIN/SwbsJailbreak.pdb" ] && cp "$BIN/SwbsJailbreak.pdb" "$PLUGIN_DIR/"
[ -f "$BIN/SwbsJailbreak.deps.json" ] && cp "$BIN/SwbsJailbreak.deps.json" "$PLUGIN_DIR/"
cp "$BIN/MySqlConnector.dll" "$PLUGIN_DIR/" # shared points / store items on MySQL (configs/swbs_shared.json)
cp -r "$BIN/lang" "$PLUGIN_DIR/"
if [ -d package ]; then cp -r package/. "$OUT/"; fi
ZIP="SwbsJailbreak-$VERSION-net10.zip"
(cd "$OUT" && if command -v zip >/dev/null; then zip -qr "../$ZIP" addons; else python3 -c "import shutil; shutil.make_archive('../SwbsJailbreak-$VERSION-net10','zip','.','addons')"; fi)
echo "Package: artifacts/$ZIP"
