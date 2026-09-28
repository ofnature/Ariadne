#!/usr/bin/env bash
# Put the Mnemosyne service inside Ariadne's plugin package.
#
# Why: Ariadne is useless without a service on the other end of the pipe, and until now the only
# way to get one was to clone Mnemosyne and build it. Someone installing Ariadne from the
# pluginmaster got a bridge with nothing to bridge to — the plugin loads, detects zones, and can
# never answer a path request. So the package carries the service, as a self-contained publish of
# Mnemosyne.Service and its CLI under `service/`, sharing one runtime.
#
# Symbols and doc XML are dropped: on a normal publish they are 63 MB of the 80 MB (37 MB of .pdb,
# 26 MB of FFXIVClientStructs doc XML). Expect ~95 MB, against ~130 KB for the plugin files.
#
# ORDER MATTERS. DalamudPackager zips the whole output directory from a target that runs
# AfterTargets="Build", so the payload has to be in place BEFORE `dotnet build -c Release`:
#
#     bash tools/bundle-mnemosyne.sh && dotnet build Ariadne/Ariadne.csproj -c Release
#
# A build without this step still succeeds and still packages — it just ships a plugin-only zip.
# That is what CI produces (no Mnemosyne checkout there) and exactly why RELEASING.md verifies the
# zip's contents instead of trusting the build.
#
# Usage: bash tools/bundle-mnemosyne.sh [path to Mnemosyne checkout]
#        (default: $MNEMOSYNE_SRC, else the sibling ../Mnemosyne)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Native tools (dotnet, git) do not understand MSYS paths like /d/Dev/...: they need D:/Dev/...
to_win() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

mnemo="$(to_win "${1:-${MNEMOSYNE_SRC:-$(dirname "$here")/Mnemosyne}}")"
out="$(to_win "$here/Ariadne/bin/Release/service")"

if [ ! -f "$mnemo/src/Mnemosyne.Service/Mnemosyne.Service.csproj" ]; then
  echo "no Mnemosyne checkout at: $mnemo" >&2
  echo "pass one explicitly: bash tools/bundle-mnemosyne.sh D:/Dev/Mnemosyne" >&2
  echo "(or set MNEMOSYNE_SRC)" >&2
  exit 1
fi

echo "publishing into $out"
rm -rf "$out"
mkdir -p "$out"

for proj in Mnemosyne.Service Mnemosyne.Cli; do
  echo "  $proj (win-x64, self-contained)"
  dotnet publish "$mnemo/src/$proj" -c Release -r win-x64 --self-contained true \
    -o "$out" -v quiet --nologo
done

for exe in Mnemosyne.Service.exe Mnemosyne.Cli.exe; do
  [ -f "$out/$exe" ] || { echo "publish did not produce $exe" >&2; exit 1; }
done

# The publish leaves symbols and doc XML behind; neither is loaded at runtime.
find "$out" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.xml' \) -delete
# The bundled runtime ships a crash-dump helper no user of this plugin will run.
rm -f "$out/createdump.exe"

# What Ariadne reads to name a stage directory (%APPDATA%\Mnemosyne\service\<version>\): the
# service's own reported version plus the commit it was built from, so two payloads of the same
# version are still distinguishable and an update stages beside the running service.
# sed, not grep -P: Perl mode refuses to run outside a UTF-8 locale ("supports only unibyte and
# UTF-8 locales"), and under `set -e` that ended the script here - payload published, VERSION
# never written, and a caller piping the output saw no failure at all (release v0.1.2).
app_version="$(sed -n 's/.*AppVersion = "\([^"]*\)".*//p' "$mnemo/src/Mnemosyne.Service/ZoneService.cs" | head -1)"
sha="$(git -C "$mnemo" rev-parse --short HEAD)"
printf '%s+%s\n' "${app_version:-0.0.0}" "$sha" > "$out/VERSION"

echo
echo "bundled Mnemosyne $(cat "$out/VERSION")"
echo "  files:  $(find "$out" -type f | wc -l | tr -d ' ')"
echo "  size:   $(du -sh "$out" | cut -f1)"
echo "  from:   $(git -C "$mnemo" log -1 --format='%h %s' | cut -c1-72)"
echo
echo "next: dotnet build Ariadne/Ariadne.csproj -c Release"
