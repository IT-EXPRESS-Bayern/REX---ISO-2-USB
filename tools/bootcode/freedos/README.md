# FreeDOS boot sector sources

`boot.asm` (FAT12/FAT16) and `boot32lb.asm` (FAT32, LBA) are unchanged copies from the FreeDOS kernel
repository, directory `boot/`:

- Repository: https://github.com/FDOS/kernel
- Tag: `ke2043` (commit `4f7bdda16a84c416a82a2616aa67335ca4f2bd74`)
- License: GNU General Public License, version 2 or (at your option) any later version. Both files say so in
  their headers; `COPYING` is the license text of the kernel package.

Why this revision: it is the one of the FreeDOS 1.4 release. Assembled with nasm it gives exactly the boot
code that `SYS.COM` of FreeDOS 1.4 writes (compared against the FAT16 volume of `FD14LITE.img`, bytes 0x3E to 0x1FD).
Later revisions of `boot.asm` changed the loader's memory layout; this one has been in use since 2021.

`boot32.asm` (CHS-only FAT32) is not used: it carries no license header, and the geometry of a USB stick
is whatever its BIOS makes up, which the LBA loader does not depend on.

Build with `tools/bootcode/build.sh`; `--check` fails when the result differs from `assets/third-party/freedos`.
The sources only assemble with nasm's default optimizer level. `SHA256SUMS` pins the source files.
