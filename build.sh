#!/usr/bin/env bash
# Builds SwbsJailbreak and produces a drop-in server package:
#   artifacts/SwbsJailbreak/addons/counterstrikesharp/plugins/SwbsJailbreak/...
#   artifacts/SwbsJailbreak-<version>.zip
# Requires the .NET 10 SDK (CounterStrikeSharp 1.0.376 targets net10.0).
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${1:-Release}"
OUT="artifacts/SwbsJailbreak"
PLUGIN_DIR="$OUT/addons/counterstrikesharp/plugins/SwbsJailbreak"

rm -rf artifacts
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c "$CONFIG"
mkdir -p "$PLUGIN_DIR"
BIN="src/SwbsJailbreak/bin/$CONFIG/net10.0"
cp "$BIN/SwbsJailbreak.dll" "$PLUGIN_DIR/"
[ -f "$BIN/SwbsJailbreak.pdb" ] && cp "$BIN/SwbsJailbreak.pdb" "$PLUGIN_DIR/"
[ -f "$BIN/SwbsJailbreak.deps.json" ] && cp "$BIN/SwbsJailbreak.deps.json" "$PLUGIN_DIR/"
cp -r "$BIN/lang" "$PLUGIN_DIR/"
# Optional example configs / map data shipped with the plugin
if [ -d package ]; then cp -r package/. "$OUT/"; fi

VERSION=$(grep -oP 'PluginVersion = "\K[^"]+' src/SwbsJailbreak/JailbreakPlugin.cs || echo dev)
(cd artifacts && if command -v zip >/dev/null; then zip -qr "SwbsJailbreak-$VERSION.zip" SwbsJailbreak; else python3 -c "import shutil; shutil.make_archive('SwbsJailbreak-$VERSION','zip','.','SwbsJailbreak')"; fi)
echo "Package: artifacts/SwbsJailbreak-$VERSION.zip"
