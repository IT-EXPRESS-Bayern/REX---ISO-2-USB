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
