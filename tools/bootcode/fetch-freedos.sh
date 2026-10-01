#!/usr/bin/env bash
# Recreates the FreeDOS programs in assets/third-party/freedos from the official FreeDOS 1.4 release and
# fails if a single byte differs from the checked-in copies. Needs curl, unzip and mtools.
#
# The release hash is the one the FreeDOS project publishes in
# https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/verify.txt
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
assets="$root/assets/third-party/freedos"

release_url="https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/FD14-LiteUSB.zip"
release_sha256="857dcd2ebf9d3d094320154db5fb5b830acba6fb98f981a95a0ca7ab3350338b"

# The first partition of FD14LITE.img starts at LBA 63.
partition_offset=$((63 * 512))

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -sSfL -o "$work/release.zip" "$release_url"
echo "$release_sha256  $work/release.zip" | sha256sum --check --quiet

unzip -q -o "$work/release.zip" FD14LITE.img -d "$work"
mcopy -i "$work/FD14LITE.img@@$partition_offset" ::/PACKAGES/BASE/kernel.zip ::/PACKAGES/BASE/freecom.zip "$work/"

unzip -q -o "$work/kernel.zip" BIN/KERNL386.SYS BIN/KERNL86.SYS APPINFO/KERNEL.LSM DOC/KERNEL/COPYING -d "$work/kernel"
unzip -q -o "$work/freecom.zip" BIN/COMMAND.COM APPINFO/FREECOM.LSM -d "$work/freecom"

mkdir "$work/out"
cp "$work/kernel/BIN/KERNL386.SYS" "$work/kernel/BIN/KERNL86.SYS" "$work/freecom/BIN/COMMAND.COM" "$work/out/"
cp "$work/kernel/DOC/KERNEL/COPYING" "$work/out/COPYING.kernel.txt"
cp "$work/kernel/APPINFO/KERNEL.LSM" "$work/out/kernel.lsm"
cp "$work/freecom/APPINFO/FREECOM.LSM" "$work/out/freecom.lsm"

(cd "$work/out" && sha256sum --check --quiet <(grep -E ' (KERNL386\.SYS|KERNL86\.SYS|COMMAND\.COM|COPYING\.kernel\.txt|kernel\.lsm|freecom\.lsm)$' "$assets/SHA256SUMS"))
echo "the FreeDOS programs in assets/third-party/freedos are the ones of the FreeDOS 1.4 release"
