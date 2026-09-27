#!/usr/bin/env bash
# Materialize the reference clone the vendored-parity tests compare against, at the exact revision
# external/ffxiv_navmesh.pin names.
#
# external/ is gitignored, so a fresh clone of this repo has nothing to compare the vendored copies
# against and five parity tests skip. This script is the other half of the pin: run it from a fresh
# clone (or after a machine move) and the comparison is against a revision somebody chose, not
# whatever happened to be on disk.
#
# Usage:  tools/fetch-vendored.sh            # clone or check out the pinned revision
#         VENDORED_REMOTE=<url> tools/fetch-vendored.sh
#
# Run it in Git Bash, not PowerShell.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
pin_file="$root/external/ffxiv_navmesh.pin"
clone="$root/external/ffxiv_navmesh"
remote="${VENDORED_REMOTE:-https://github.com/awgil/ffxiv_navmesh.git}"

[ -f "$pin_file" ] || { echo "no pin at $pin_file" >&2; exit 1; }
rev="$(grep -v '^[[:space:]]*#' "$pin_file" | grep -v '^[[:space:]]*$' | head -1 | awk '{print $1}')"
[ -n "$rev" ] || { echo "pin $pin_file names no revision" >&2; exit 1; }

# `cd` before git, never `git -C <msys-path>`: MSYS does not translate the path argument for a
# native binary, and the failure is silent.
if [ ! -d "$clone/.git" ]; then
    echo "cloning $remote"
    mkdir -p "$(dirname "$clone")"
    git clone "$remote" "$clone"
fi

head_rev="$(cd "$clone" && git rev-parse HEAD)"
if [ "$head_rev" = "$rev" ]; then
    echo "external/ffxiv_navmesh is already at the pinned revision ($rev)"
    exit 0
fi

echo "moving external/ffxiv_navmesh from $head_rev to the pinned $rev"
(cd "$clone" && git fetch origin && git checkout --detach "$rev")

head_rev="$(cd "$clone" && git rev-parse HEAD)"
if [ "$head_rev" != "$rev" ]; then
    echo "checkout did not land on the pinned revision ($head_rev != $rev)" >&2
    exit 1
fi
echo "done — external/ffxiv_navmesh is at $rev"
