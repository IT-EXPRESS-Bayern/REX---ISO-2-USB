# Mitgelieferte Drittkomponenten

Alle Dateien in diesem Ordner werden unverändert eingebunden. Die Quelle, die Lizenz und der Hash stehen hier,
damit sich jede Datei nachprüfen lässt.

## uefi-ntfs/uefi-ntfs.img

- Inhalt: FAT-Image (1 MiB) mit dem UEFI:NTFS-Bootloader (v2.8, von Microsoft für Secure Boot signiert),
  signierten NTFS-Treibern (abgeleitet von ntfs-3g 1.9) und unsignierten exFAT-/ARM-Treibern aus EfiFs 1.12.
- Herkunft: https://github.com/pbatard/rufus/tree/master/res/uefi (`uefi-ntfs.img`, `readme.txt`)
- Quellen: https://github.com/pbatard/uefi-ntfs (GPLv2), https://github.com/pbatard/ntfs-3g (GPLv2),
  https://github.com/pbatard/efifs (GPLv3)
- SHA-256: `72683fa1250eeea772d3399277b434d4e55ba8dd0dc926e52d817e701fc2eb9e`
- Die Binärdateien liegen als eigenständige Programme auf einer eigenen Partition des Zielmediums und sind
  keine Bestandteile von Bootrix. Die Quelltexte sind unter den oben genannten Adressen verfügbar.

## syslinux-mbr/*.bin

- Inhalt: 440-Byte-MBR-Startcode (`mbr.bin`, `mbr_f.bin`, `mbr_c.bin`), GPT-Variante (`gptmbr.bin`) und
  `altmbr.bin` aus Syslinux.
- Herkunft: Debian-Paket `syslinux-common` 6.04~git20190206.bf6db5b4+dfsg1-3ubuntu3, aus dem Syslinux-Quelltext
  (`mbr/*.S`, Lizenz Expat/MIT, siehe `syslinux-mbr/LICENSE`).
- Die Varianten: `mbr.bin` Standard, `mbr_f.bin` erzwingt Laufwerksnummer 0x80 (für alte BIOS), `mbr_c.bin`
  prüft CHS-Start, `gptmbr.bin` startet GPT-Partitionen mit Legacy-BIOS-Flag.

| Datei | SHA-256 |
|---|---|
| `syslinux-mbr/altmbr.bin` | `2bdbb935ac1c41dd9f2a8a96f2adac34540833df148bc32b8e06f0ddb137acc7` |
| `syslinux-mbr/gptmbr.bin` | `d2a9081727f91f4c38494e52cdeb86ebd9009fead17a739effbad4011c581d1f` |
| `syslinux-mbr/mbr.bin` | `4746f74bc9b9d3d579c41988a4a29bb7ac932ad1c70470ea779ea161eb799b64` |
| `syslinux-mbr/mbr_c.bin` | `66a1c4f8c67cff93326d4290a6991ff798fd304ac70c3e10cb192473cf5560f3` |
| `syslinux-mbr/mbr_f.bin` | `045aa462391c89e05375d7c45f3052fe3ab0472b5b97100e04fc1812621985e2` |

## wimlib/win-x64/libwim-15.dll, wimlib/win-arm64/libwim-15.dll

- Inhalt: wimlib 1.14.5 (Windows-Build der Bibliothek) zum Teilen, Exportieren und Konvertieren von WIM/ESD-Dateien.
- Herkunft: https://wimlib.net/downloads/wimlib-1.14.5-windows-x86_64-bin.zip und
  https://wimlib.net/downloads/wimlib-1.14.5-windows-aarch64-bin.zip
- Lizenz: wimlib steht wahlweise unter GPLv3+ oder (für die Bibliothek libwim) unter LGPLv2.1+. Die Windows-Builds
  verwenden kein ntfs-3g, daher gilt die LGPL-Option. Lizenztexte: `wimlib/COPYING*.txt`.
- Quelltext: https://wimlib.net/downloads/wimlib-1.14.5.tar.gz
- SHA-256 x64: `ba853ee1e3fc5f5798581f02e8e066ba07a0a2375f0bf444fe981431fd508495`
- SHA-256 arm64: `b34549c6eff728a2f2fe68903a9fde28d8ac717f1b521e7f38b1e4106320c992`
- Die DLL liegt neben der Programmdatei und wird nicht in die Einzeldatei eingebettet; vor dem Laden wird der Hash geprüft.

## syslinux/6.03, syslinux/6.04-pre1

- Inhalt: Kern (`ldlinux.sys`), Bootsektor-Vorlage (`ldlinux.bss`) und einige Module (`ldlinux.c32`, `libcom32.c32`,
  `libutil.c32`, `menu.c32`, `vesamenu.c32`, `mboot.c32`) des offiziellen Syslinux-Releases, unverändert aus dem
  Release-Archiv (`bios/core/`, `bios/com32/…`). Bootrix schreibt sie für Linux-Medien im ISO-Modus (BIOS) und
  trägt die Sektorkarte nach dem Vorbild von `libinstaller/syslxmod.c` ein.
- Herkunft: https://mirrors.edge.kernel.org/pub/linux/utils/boot/syslinux/6.xx/syslinux-6.03.tar.xz und
  https://mirrors.edge.kernel.org/pub/linux/utils/boot/syslinux/Testing/6.04/syslinux-6.04-pre1.tar.xz
- Quelltext (Archiv) und Prüfsummen laut `sha256sums.asc` von kernel.org:
  - `syslinux-6.03.tar.xz` `26d3986d2bea109d5dc0e4f8c4822a459276cf021125e8c9f23c3cca5d8c850e`
  - `syslinux-6.04-pre1.tar.xz` `3f6d50a57f3ed47d8234fd0ab4492634eb7c9aaf7dd902f33d3ac33564fd631d`
- Lizenz: GPL-2.0-or-later (Text: `syslinux/COPYING`); die COM32-Bibliothek (`libcom32.c32`, `libutil.c32`) steht
  überwiegend unter BSD-artigen Lizenzen, Einzelheiten in den Quelldateien des Archivs.
- Warum zwei Versionen: `ldlinux.sys` und `ldlinux.c32` müssen zusammenpassen. Bootrix wählt die Version, die der
  `isolinux.bin` des Images am nächsten kommt (siehe `SyslinuxBundle.Select`); ausprobiert sind die Paarungen der
  Kerne 6.03 und 6.04-pre1 mit `ldlinux.c32`/`menu.c32` von 6.03, 6.04-pre1 und Debian 6.04~git20190206 (QEMU).

| Datei | 6.03 SHA-256 | 6.04-pre1 SHA-256 |
|---|---|---|
| `ldlinux.sys` | `3f1206e0cc45dbe180e73adaeb221bfc7d5a800095738549390379d7d0282ac3` | `73b62767a16200b9af193a7d5c94e9c294c6dbb6d5b17c15038c9f3173c9a7bc` |
| `ldlinux.bss` | `8814e576abc1aa44dde943b0caaee833a5810142614adeeb4cc725e78a5045b7` | `cc40ba0349782cb4c9021e54dcc0a4540c3a8b96088b3a5648671926ef44d2f0` |
| `ldlinux.c32` | `5cef9ad0d0ca04097262241686c6c3a7306ab9b9cdf24b9d4ee3b16af01a5af2` | `d3472c02263acf9cd1da5db51e263c5484bad13ea68618c403d9cb01ca070aee` |
| `libcom32.c32` | `f315b464ff29749ec31c54d3b34a107fbfed7a8b4232d37d397ded15f1f6de3a` | `d5265a7d87e84141e64e41303e7930c1fded22fd33305097106c79a8d29fb8db` |
| `libutil.c32` | `7158e8047691153f2feb24f3e5a7e72842ccd47802f4c63fe74a26f3238c4d7e` | `d98010c67cd6470f46a6db550711208f1466da7449331d8db8a38d15c8638178` |
| `menu.c32` | `7afc5a8fd04a2a44a725ffac50554e5253f016f6b4908656dcec5d806fd279d3` | `ae4e519db9a2703d965a526c2475a9e84cafa22b5285ce25cd40ea010b074763` |
| `vesamenu.c32` | `3c10fff4d9553c93e35f58b820100fe533dbec6ef48e0757f717ccb3b1c26a26` | `ba2bc4e6a0b4b08a920ea9416814e4c6ec3533d984a3a7fcd8dbeb9fc477ba28` |
| `mboot.c32` | `c1ce3b26303ef7fab4be58f5ebb44dfb106c856b573f7f54ee725ab2c5b96883` | `1e402b9d858c316d83726d517257b9f0b6ff7d6c500675734c4908ca4ca1119b` |

## grub/2.12

- Inhalt: `boot.img` (512-Byte-Bootsektor, unverändert aus dem Paket) und zwei vorgebaute Kernabbilder
  (`core-msdos.img` für MBR mit Wurzel `(,msdos1)/boot/grub`, `core-gpt.img` für GPT mit `(,gpt2)/boot/grub`).
  Die Kernabbilder enthalten alle Module, die Menüs der Distributionen laden (Dateisysteme FAT, exFAT, NTFS, ext2,
  ISO 9660, `normal`, `linux`, `chain`, `loopback`, `gfxterm`, `syslinuxcfg` …), damit die GRUB-Version auf dem
  Stick nicht zu den Modulen im Image passen muss.
- Herkunft: Debian/Ubuntu-Paket `grub-pc-bin` 2.12-1ubuntu7.3 (GRUB 2.12, https://ftp.gnu.org/gnu/grub/grub-2.12.tar.xz,
  Quelle des Pakets: https://launchpad.net/ubuntu/+source/grub2/2.12-1ubuntu7.3).
- Bau (reproduzierbar, gleiche Prüfsummen bei jedem Lauf): `tools/bootcode/grub/build-core.sh <Ausgabeordner>`.
- Lizenz: GPL-3.0-or-later (Text: `grub/COPYING`). Der Quelltext ist unter den obigen Adressen erhältlich.

| Datei | SHA-256 |
|---|---|
| `grub/2.12/boot.img` | `7720690bdaf25fd7e3ade7e01b8d0de91e0991653e2a6900011ab23daf6e6032` |
| `grub/2.12/core-msdos.img` | `8e79bae9f727fcfdd2abafe5958345b3f99eab00711793a08377e088ab651842` |
| `grub/2.12/core-gpt.img` | `90648c3d1461ed53e926edc1aa26ecd3b501d1e7470debb7b33675236ca12fcd` |
