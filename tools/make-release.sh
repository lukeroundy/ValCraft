#!/usr/bin/env bash
# Build all artifacts and stage the GitHub release assets in dist/release-<ver>/ (does NOT publish anything).
set -euo pipefail
cd "$(dirname "$0")/.."
VER=$(sed -n 's/^mod_version=//p' neoforge-mod/gradle.properties)
export PATH=/home/box/tools/dotnet:$PATH DOTNET_ROOT=/home/box/tools/dotnet
(cd neoforge-mod && ./gradlew build -q)
(cd valheim-plugin && dotnet build -c Release -p:ValheimDir=${VALHEIM_DIR:-/tmp/vh/g} -v q)
(cd installer && dotnet build -c Release -v q)
OUT=dist/release-$VER; mkdir -p "$OUT"
cp valheim-plugin/bin/Release/netstandard2.1/ValCraft.Valheim.dll "$OUT/"
cp neoforge-mod/build/libs/valcraft-$VER.jar "$OUT/"
cp installer/bin/Release/net48/ValCraft-Installer.exe "$OUT/ValCraft-Installer-$VER.exe"
cp installer/bin/Release/net48/ValCraft-Installer.exe "$OUT/ValCraft-Installer.exe" # stable name for releases/latest/download links
cp release/RELEASE_NOTES-$VER.md "$OUT/"
(cd "$OUT" && sha256sum *.dll *.jar *.exe > SHA256SUMS.txt)
echo "staged $OUT"; ls -la "$OUT"
echo "publish (after the repo exists): gh release create v$VER $OUT/*.dll $OUT/*.jar $OUT/*.exe $OUT/SHA256SUMS.txt -F $OUT/RELEASE_NOTES-$VER.md"
