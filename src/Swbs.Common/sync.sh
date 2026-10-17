#!/usr/bin/env bash
# src/Swbs.Common is the same, byte for byte, in every SWBS repo (cs2-bhop-dev, cs2-mg-dev, jb-cs2-dev).
#
#   src/Swbs.Common/sync.sh                     refresh MANIFEST after editing the folder in this repo
#   src/Swbs.Common/sync.sh ../cs2-mg-dev ...   ...and copy the folder into the other repos
#
# Each repo's tests check the folder against MANIFEST, so an edit made in one repo only (without running this) fails
# the build until it is synced.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"

(cd "$here" && find . -type f ! -name MANIFEST ! -path './bin/*' ! -path './obj/*' | LC_ALL=C sort | xargs sha256sum) > "$here/MANIFEST"
echo "MANIFEST: $(wc -l < "$here/MANIFEST") files"

for repo in "$@"; do
  [ -d "$repo/src" ] || { echo "not an SWBS repo: $repo" >&2; exit 1; }
  rm -rf "$repo/src/Swbs.Common"
  cp -r "$here" "$repo/src/Swbs.Common"
  echo "synced -> $repo/src/Swbs.Common"
done
