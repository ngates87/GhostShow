#!/usr/bin/env bash
# Builds the Windows installer: artifacts/GhostShow-Setup-<version>.exe. Takes about 6 minutes, nearly all of it the
# browser build. Usage: installer/build.sh [version], e.g. installer/build.sh 1.2.0 (three numbers; the default is 0.1.0).
#
# Runs on Linux and needs:
#   - NSIS, which makes the installer: sudo apt install nsis
#   - Node.js, for the web remote's page. dotnet would carry on without it and ship a page saying it wasn't built.
#   - the private SDK in ~/.dotnet with "wasm-tools" (see GhostShow.Browser/publish.sh)
#   - the internet, the first time, for Microsoft's Visual C++ runtime (see ghostshow.nsi for why it's included)
set -euo pipefail
cd "$(dirname "$0")/.."

version="${1:-0.1.0}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "The version must be three numbers, like 1.2.0 (got: $version)" >&2
    exit 1
fi
for tool in makensis node; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool wasn't found. See the top of this script for what it needs." >&2
        exit 1
    fi
done

work_dir="$PWD/artifacts/installer"
app_dir="$work_dir/app"
vc_redist="$work_dir/VC_redist.x64.exe"
setup_file="$PWD/artifacts/GhostShow-Setup-$version.exe"

# Starts clean, so that files from an older build don't end up in the installer.
rm -rf "$app_dir"
mkdir -p "$work_dir"

echo "== The browser app (what the show computer serves at /app/)"
GhostShow.Browser/publish.sh

echo "== The desktop app for 64-bit Windows"
# Self-contained, so the show computer doesn't need .NET. ReadyToRun makes it start faster.
DOTNET_ROOT="$HOME/.dotnet" "$HOME/.dotnet/dotnet" publish GhostShow.App -c Release -r win-x64 --self-contained \
    -p:PublishReadyToRun=true -p:Version="$version" -o "$app_dir"
# Debug symbols for Skia and HarfBuzz: 100 MB, and only useful for debugging those libraries.
rm -f "$app_dir"/lib*.pdb
# RemoteViewModel.FindBrowserApp looks for this folder next to the program.
cp -r GhostShow.Browser/bin/Release/net10.0-browser/publish/wwwroot "$app_dir/browser-app"

if [[ ! -f "$vc_redist" ]]; then
    echo "== Downloading the Visual C++ runtime from Microsoft"
    curl --fail --location --proto '=https' --output "$vc_redist.part" https://aka.ms/vs/17/release/vc_redist.x64.exe
    mv "$vc_redist.part" "$vc_redist"
fi

echo "== The installer"
makensis -V2 -DVERSION="$version" -DAPP_DIR="$app_dir" -DVC_REDIST="$vc_redist" -DSETUP_FILE="$setup_file" \
    installer/ghostshow.nsi

echo
echo "Built: $setup_file"
