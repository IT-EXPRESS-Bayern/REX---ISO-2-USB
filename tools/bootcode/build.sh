#!/usr/bin/env bash
# Assembles the FreeDOS boot sectors from the pinned sources in tools/bootcode/freedos.
#
#   build.sh            writes fat12com.bin, fat16com.bin and fat32lba.bin to assets/third-party/freedos
#   build.sh --check    builds into a temporary folder and fails if the result differs from the checked-in files
#
# The sources are the unchanged files of FDOS/kernel at tag ke2043 (see freedos/README.md). They only
# assemble with the default optimizer level of nasm; the build refuses to run on a modified source.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
sources="$root/tools/bootcode/freedos"
assets="$root/assets/third-party/freedos"

command -v nasm >/dev/null || { echo "nasm is required" >&2; exit 2; }

(cd "$sources" && sha256sum --check --quiet SHA256SUMS)

check=false
output="$assets"
if [[ "${1:-}" == "--check" ]]; then
  check=true
  output="$(mktemp -d)"
  trap 'rm -rf "$output"' EXIT
fi

mkdir -p "$output"
nasm -dISFAT12 "$sources/boot.asm" -o "$output/fat12com.bin"
nasm -dISFAT16 "$sources/boot.asm" -o "$output/fat16com.bin"
nasm "$sources/boot32lb.asm" -o "$output/fat32lba.bin"

for name in fat12com fat16com fat32lba; do
  size="$(stat -c %s "$output/$name.bin")"
  [[ "$size" -eq 512 ]] || { echo "$name.bin is $size bytes, expected 512" >&2; exit 1; }
done

if $check; then
  (cd "$output" && sha256sum --check --quiet <(grep -E ' (fat12com|fat16com|fat32lba)\.bin$' "$assets/SHA256SUMS"))
  echo "boot sectors match the checked-in assets ($(nasm -v))"
else
  echo "built $(ls "$output"/fat*.bin | wc -l) boot sectors with $(nasm -v)"
fi
