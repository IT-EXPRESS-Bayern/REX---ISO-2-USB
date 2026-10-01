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

## freedos/

FreeDOS-Startsektoren und die Programme für FreeDOS-Datenträger. Sie werden zur Laufzeit auf den Zielträger
geschrieben (jeweils als eigenständige Programme, nicht mit Bootrix verbunden); Bootrix selbst linkt keinen
dieser Codes.

### Startsektoren: `fat12com.bin`, `fat16com.bin`, `fat32lba.bin`

- Aus dem Quelltext gebaut: `tools/bootcode/freedos/boot.asm` (FAT12 und FAT16, je mit `-dISFAT12` bzw. `-dISFAT16`)
  und `boot32lb.asm` (FAT32, LBA), unverändert aus https://github.com/FDOS/kernel, Tag `ke2043`
  (Commit `4f7bdda16a84c416a82a2616aa67335ca4f2bd74`), Verzeichnis `boot/`.
- Lizenz: GNU GPL, Version 2 oder (nach Wahl) jede spätere Version; Text in `freedos/COPYING.kernel.txt`.
- Bauen und prüfen: `tools/bootcode/build.sh` (nasm 2.16), `tools/bootcode/build.sh --check` vergleicht mit den
  eingecheckten Dateien. Die Übersetzung von `ke2043` ergibt dieselben Bytes wie der Startsektor, den `SYS.COM`
  von FreeDOS 1.4 schreibt.
- Die BPB-Felder und der Rest des Sektors kommen vom FAT-Formatter von Bootrix; die Dateien enthalten nur den Code.

### Kernel und Kommandointerpreter

- `KERNL386.SYS` (FAT32-fähig, ab 386), `KERNL86.SYS` (für 8086/286, FAT32-fähig): Paket `kernel.zip` der
  FreeDOS-1.4-Distribution (Kernel 2043, Quelle https://github.com/FDOS/kernel). Lizenz: GNU GPL, Version 2
  (`COPYING.kernel.txt`, `kernel.lsm`).
- `COMMAND.COM`: FreeCOM 0.86a, Paket `freecom.zip` derselben Distribution (Quelle https://github.com/FDOS/freecom).
  Lizenz: GNU GPL, Version 2 (`COPYING.freecom.txt`, `freecom.lsm`).
- Herkunft: `FD14-LiteUSB.zip` (SHA-256 `857dcd2ebf9d3d094320154db5fb5b830acba6fb98f981a95a0ca7ab3350338b`, die vom
  Projekt in `verify.txt` veröffentlichte Prüfsumme), Pfad `PACKAGES/BASE/kernel.zip` bzw. `freecom.zip` im Abbild
  `FD14LITE.img`. `tools/bootcode/fetch-freedos.sh` lädt die Distribution, prüft die Summe und vergleicht die
  entpackten Dateien Byte für Byte mit diesen.
- Quelltexte: liegen in denselben Paketen unter `SOURCE/KERNEL/SOURCES.ZIP` und `SOURCE/FREECOM/SOURCES.ZIP`
  (https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.4/base/kernel.zip und `freecom.zip`).
- Der Kernel wird beim Schreiben im Konfigurationsbereich angepasst (Byte 0x0D, `FORCELBA`); das ist die
  Einstellung, die das FreeDOS-eigene `SYS CONFIG` setzt.

| Datei | SHA-256 |
|---|---|
| `freedos/fat12com.bin` | `0742aecaf453713e1895ee835143a133a6fb8f90374d1050e7064d6e4e290eeb` |
| `freedos/fat16com.bin` | `39586cd0f55f7732791a990770163d4af9827623e418203921d6f23819b94cb5` |
| `freedos/fat32lba.bin` | `eb86f238f70d559f3dc66d082fc55c8459c77b4fe9e2fa9d48c440cf4a7c9a74` |
| `freedos/KERNL386.SYS` | `932c0c155701eddb7b902f7269a1b2ce31f5c82a6dc195172f2336d18a74e1fb` |
| `freedos/KERNL86.SYS` | `f34a7483c575fcf2709d9a7d0bc3db81c6211c279530f9e1bf78576b9233924d` |
| `freedos/COMMAND.COM` | `077808379e896476f7f69d62e6c8989d8fc23e8ef58d1c8492db1ac106784107` |

## MS-DOS 8.0 (Startdiskette von Windows ME)

Nichts davon liegt im Repository oder wird von Bootrix verteilt. Auf ausdrücklichen Wunsch des Benutzers lädt
Bootrix `diskcopy.dll` zur Laufzeit vom Microsoft-Symbolserver
(`https://msdl.microsoft.com/download/symbols/diskcopy.dll/54505118173000/diskcopy.dll`), prüft die Größe, den
SHA-256 (`95fc0786f5bc0a6db5c0604b31ac18fbed0502a2c6858e5fb02a647983ae03c7`) und die Authenticode-Signatur von
Microsoft und entnimmt die Dateien dem darin eingebetteten 1,44-MB-Diskettenabbild.
