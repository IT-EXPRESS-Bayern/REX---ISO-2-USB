#!/usr/bin/env bash
# Builds the GRUB 2 BIOS boot images that Bootrix writes in front of the first partition.
#
#   boot.img         512-byte boot sector code, copied unchanged from the GRUB package
#   core-msdos.img   core image for MBR sticks: root is the first partition of the boot disk
#   core-gpt.img     core image for GPT sticks: root is the second partition (the first is the BIOS boot partition)
#
# Both core images carry the modules that distribution boot menus normally load, so the version of GRUB on the
# stick never has to match the (older or newer) modules inside an ISO. Run it on Debian or Ubuntu with
# grub-pc-bin installed; the expected package version and the hashes of the results are in
# assets/third-party/SOURCES.md. Usage: build-core.sh <output directory>
set -euo pipefail

out="${1:?usage: build-core.sh <output directory>}"
dir="${GRUB_I386_PC_DIR:-/usr/lib/grub/i386-pc}"

modules=(
  # disks and file systems
  biosdisk part_msdos part_gpt fat exfat ntfs ntfscomp ext2 iso9660
  # booting
  normal configfile boot linux linux16 chain loopback multiboot multiboot2
  # finding things
  search search_fs_file search_fs_uuid search_label
  # commands used by distribution menus
  echo cat ls test true regexp read sleep reboot halt help probe minicmd keystatus lsmmap gettext extcmd
  loadenv datetime date cpuid hexdump syslinuxcfg
  # decompression
  gzio xzio lzopio
  # video and terminals
  all_video vbe vga video video_bochs video_cirrus gfxterm gfxmenu font png jpeg tga bitmap bitmap_scale
  gfxterm_background terminal serial terminfo videoinfo
)

mkdir -p "$out"
cp "$dir/boot.img" "$out/boot.img"

# -C xz keeps the images small; the decompressor stub is part of the image.
grub-mkimage -O i386-pc -d "$dir" -C xz -p '(,msdos1)/boot/grub' -o "$out/core-msdos.img" "${modules[@]}"
grub-mkimage -O i386-pc -d "$dir" -C xz -p '(,gpt2)/boot/grub' -o "$out/core-gpt.img" "${modules[@]}"

sha256sum "$out"/boot.img "$out"/core-msdos.img "$out"/core-gpt.img
