#!/bin/bash
# Regenerates the STARDEW_REFASM_B64_NNN repo secrets that CI's build-mod job
# assembles back into the game/SMAPI reference DLLs it needs to compile
# against. Run this once now, and again whenever the game or SMAPI version
# changes (i.e. whenever these DLLs change).
#
# See ../README.md#ci-builds for the full explanation of why this exists.
#
# Usage:
#   ./generate-ci-secrets.sh "<path to Stardew Valley folder containing 'Stardew Valley.dll'>" <owner>/<repo>
#
# Requires: dotnet SDK (any recent version - only uses System.Reflection.Metadata,
# no NuGet packages needed), and the GitHub CLI (`gh`, already authenticated)
# to actually upload the secrets. If `gh` isn't available/authenticated, the
# chunk files are still written to ./secretparts/ for you to upload by hand.

set -euo pipefail

GAME_PATH="${1:?Usage: $0 <game path> <owner>/<repo>}"
REPO="${2:?Usage: $0 <game path> <owner>/<repo>}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "== Building refstrip =="
dotnet build "$SCRIPT_DIR/refstrip/refstrip.csproj" -c Release -o "$WORK_DIR/refstrip-bin" >/dev/null

echo "== Stripping method bodies from game/SMAPI DLLs =="
mkdir -p "$WORK_DIR/stripped/smapi-internal"
STRIP="dotnet $WORK_DIR/refstrip-bin/refstrip.dll"
$STRIP "$GAME_PATH/Stardew Valley.dll" "$WORK_DIR/stripped/Stardew Valley.dll"
$STRIP "$GAME_PATH/StardewValley.GameData.dll" "$WORK_DIR/stripped/StardewValley.GameData.dll"
$STRIP "$GAME_PATH/MonoGame.Framework.dll" "$WORK_DIR/stripped/MonoGame.Framework.dll"
$STRIP "$GAME_PATH/xTile.dll" "$WORK_DIR/stripped/xTile.dll"
$STRIP "$GAME_PATH/StardewModdingAPI.dll" "$WORK_DIR/stripped/StardewModdingAPI.dll"
$STRIP "$GAME_PATH/smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll" "$WORK_DIR/stripped/smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll"

echo "== Packing =="
( cd "$WORK_DIR/stripped" && tar czf "$WORK_DIR/stripped-assemblies.tar.gz" \
    "Stardew Valley.dll" StardewValley.GameData.dll MonoGame.Framework.dll xTile.dll \
    StardewModdingAPI.dll smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll )

base64 -i "$WORK_DIR/stripped-assemblies.tar.gz" 2>/dev/null | tr -d '\n' > "$WORK_DIR/payload.b64" \
  || base64 -w0 "$WORK_DIR/stripped-assemblies.tar.gz" > "$WORK_DIR/payload.b64"  # macOS vs Linux base64 flags

PAYLOAD_SIZE=$(wc -c < "$WORK_DIR/payload.b64" | tr -d ' ')
echo "Gzipped+base64 payload: $PAYLOAD_SIZE bytes"

OUT_DIR="$SCRIPT_DIR/secretparts"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"
split -b 40000 -d -a 3 "$WORK_DIR/payload.b64" "$OUT_DIR/STARDEW_REFASM_B64_"

CHUNK_COUNT=$(ls "$OUT_DIR" | wc -l | tr -d ' ')
echo "Split into $CHUNK_COUNT chunks in $OUT_DIR/"

if [ "$CHUNK_COUNT" -gt 100 ]; then
  echo "!! ERROR: $CHUNK_COUNT chunks exceeds GitHub's 100-secrets-per-repo limit." >&2
  echo "!! The DLLs grew too much since this technique was set up - rethink the approach." >&2
  exit 1
fi

echo ""
echo "IMPORTANT: if this chunk count differs from what's currently in"
echo ".github/workflows/release-build.yml's 'Reconstruct game reference"
echo "assemblies' step, you MUST update that step's printf lines to match"
echo "$CHUNK_COUNT (STARDEW_REFASM_B64_000 .. STARDEW_REFASM_B64_$((CHUNK_COUNT - 1))"
echo "zero-padded to 3 digits)."
echo ""

if command -v gh >/dev/null 2>&1; then
  echo "== Uploading secrets via gh =="
  count=0
  for f in "$OUT_DIR"/STARDEW_REFASM_B64_*; do
    name="$(basename "$f")"
    gh secret set "$name" --repo "$REPO" < "$f"
    count=$((count+1))
    echo "[$count/$CHUNK_COUNT] $name"
  done
  echo "Done - set $count secrets on $REPO."
else
  echo "gh CLI not found - upload the files in $OUT_DIR/ yourself, one secret"
  echo "per file, named after the file (e.g. gh secret set STARDEW_REFASM_B64_000"
  echo "--repo $REPO < $OUT_DIR/STARDEW_REFASM_B64_000)."
fi
