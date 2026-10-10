#!/usr/bin/env bash
# Builds SwbsJailbreak for both supported CounterStrikeSharp lines and produces drop-in server packages:
#   artifacts/SwbsJailbreak-<version>-net10.zip  -> CounterStrikeSharp 1.0.370 .. 1.0.376 (.NET 10)  [recommended]
#   artifacts/SwbsJailbreak-<version>-net8.zip   -> CounterStrikeSharp <= 1.0.368 (.NET 8)
# Each zip contains  addons/counterstrikesharp/plugins/SwbsJailbreak/...  ready to copy into game/csgo/.
# Requires the .NET 10 SDK (it can also build the net8.0 target).
set -euo pipefail
cd "$(dirname "$0")"
CONFIG="${1:-Release}"
VERSION=$(grep -oP 'PluginVersion = "\K[^"]+' src/SwbsJailbreak/JailbreakPlugin.cs || echo dev)

rm -rf artifacts
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c "$CONFIG"

for TFM in net10.0 net8.0; do
  TAG="${TFM%%.*}"                      # net10 / net8
  OUT="artifacts/$TAG"
  PLUGIN_DIR="$OUT/addons/counterstrikesharp/plugins/SwbsJailbreak"
  BIN="src/SwbsJailbreak/bin/$CONFIG/$TFM"
  mkdir -p "$PLUGIN_DIR"
  cp "$BIN/SwbsJailbreak.dll" "$PLUGIN_DIR/"
  [ -f "$BIN/SwbsJailbreak.pdb" ] && cp "$BIN/SwbsJailbreak.pdb" "$PLUGIN_DIR/"
  [ -f "$BIN/SwbsJailbreak.deps.json" ] && cp "$BIN/SwbsJailbreak.deps.json" "$PLUGIN_DIR/"
  cp -r "$BIN/lang" "$PLUGIN_DIR/"
  if [ -d package ]; then cp -r package/. "$OUT/"; fi
  ZIP="SwbsJailbreak-$VERSION-$TAG.zip"
  (cd "$OUT" && if command -v zip >/dev/null; then zip -qr "../$ZIP" addons; else python3 -c "import shutil; shutil.make_archive('../SwbsJailbreak-$VERSION-$TAG','zip','.','addons')"; fi)
  echo "Package: artifacts/$ZIP"
done
