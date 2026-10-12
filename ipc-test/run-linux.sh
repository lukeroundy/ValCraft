#!/usr/bin/env bash
# Cross-language IPC test. Needs: dotnet 8 SDK, JDK 21 (java/javac on PATH or JAVA_HOME).
set -euo pipefail
cd "$(dirname "$0")"
JAVA_BIN="${JAVA_HOME:+$JAVA_HOME/bin/}"
SHM="${1:-/tmp/valcraft-test.shm}"; rm -f "$SHM"
mkdir -p build/java
"${JAVA_BIN}javac" -d build/java ../neoforge-mod/src/main/java/dev/valcraft/link/ValCraftLink.java ../neoforge-mod/src/main/java/dev/valcraft/link/BlockCodec.java java/IpcTestPeer.java java/BlockCodecTest.java java/ItemCodecTest.java
dotnet build csharp -c Release -o build/cs -v q --nologo >/dev/null
"${JAVA_BIN}java" -Dvalcraft.unpremultiply=false -cp build/java IpcTestPeer "$SHM" & JPID=$!
dotnet build/cs/IpcTest.dll "$SHM" 20000; CS=$?
wait $JPID; JV=$?
"${JAVA_BIN}java" -cp build/java BlockCodecTest /tmp/valcraft-blocks.bin && dotnet build/cs/IpcTest.dll blocks /tmp/valcraft-blocks.bin; BL=$?
"${JAVA_BIN}java" -cp build/java ItemCodecTest /tmp/valcraft-items.bin && dotnet build/cs/IpcTest.dll items /tmp/valcraft-items.bin; IT=$?
echo "csharp exit=$CS java exit=$JV blocks exit=$BL items exit=$IT"; [ $CS -eq 0 ] && [ $JV -eq 0 ] && [ $BL -eq 0 ] && [ $IT -eq 0 ]
