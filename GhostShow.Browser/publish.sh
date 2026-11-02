#!/usr/bin/env bash
# Builds the full app for browsers (what the show computer serves at /app/). Takes about 5 minutes.
#
# Needs an SDK with the WebAssembly build tools ("wasm-tools"). Ubuntu's own SDK can't have them, so this uses the
# private one in ~/.dotnet.
#
# Always AOT-compiled: the interpreted build is too slow to edit with (see docs/plan-remote.md, "Measured").
# Starts clean: publishing over an old build leaves its files behind, and the folder keeps growing.
set -euo pipefail
cd "$(dirname "$0")"

rm -rf bin/Release obj/Release
DOTNET_ROOT="$HOME/.dotnet" "$HOME/.dotnet/dotnet" publish -c Release -p:RunAOTCompilation=true

echo
echo "Built: $PWD/bin/Release/net10.0-browser/publish/wwwroot"
