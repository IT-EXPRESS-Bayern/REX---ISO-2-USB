# Bootrix

Bootrix erstellt bootfähige Medien: USB-Sticks, Festplatten, SD-Karten, Disketten, CDs, DVDs und Blu-rays aus ISO- und Disk-Images. Es richtet sich an IT-Service und Werkstatt, ist aber auch für Einzelnutzer gedacht.

Bootrix ist der Nachfolger von REX und komplett neu entwickelt (.NET 10, WPF, Win32-Engine, eigene Kommandozeile). Es läuft unter Windows 10 und 11 (x64 und Arm64).

> **Stand:** in Entwicklung. Core, Engine, Katalog, Downloader und Teile der Oberfläche sind umgesetzt und werden bei jedem Push auf Linux und Windows getestet. Die Schreibwege für Windows-, Linux-, DOS- und Apple-Medien kommen schrittweise dazu; was bereits geht und was noch fehlt, steht unter [Funktionsstand](#funktionsstand). Die Pfade, die echte Hardware brauchen, stehen in der [Hardware-Checkliste](docs/hardware-checkliste.md).

## Was Bootrix anders macht

- **Physische Laufwerke statt Laufwerksbuchstaben.** Jede Platte wird über Gerätepfad, Seriennummer und Größe erkannt. System-, Boot- und Auslagerungsplatte sind hart gesperrt. Vor jedem Schreibzugriff wird die Identität erneut geprüft, und die Bestätigung verlangt die Seriennummer des Zielgeräts.
- **Ein Plan vor dem ersten Byte.** Aus Image und Gerät entsteht ein Plan (Partitionstabelle, Dateisysteme, Startweg, Warnungen), den die Oberfläche vorab zeigt. Dasselbe Planungsmodul trägt die Schreibwege.
- **Gültige Medien.** FAT12/16/32 in beliebiger Größe und mit voller Kontrolle über die Geometrie, MBR und GPT, UEFI:NTFS, Split-WIM für FAT32, 4Kn-Laufwerke, Alt-BIOS-Anpassungen (klassischer Offset, CHS-Werte), ext2/3/4 für Persistenz.
- **Echte Prüfung.** Nach dem Schreiben wird das Ziel zurückgelesen und gegen die Prüfsumme des Images verglichen; Fälschungs- und Fehlerblock-Test für Sticks mit falscher Kapazität.
- **Sicher aufgebaut.** Die Oberfläche läuft ohne Administratorrechte. Nur ein kleiner, per UAC gestarteter Broker-Prozess fasst Datenträger an, über eine abgesicherte Named Pipe, die nur die startende Sitzung erreicht.
- **Hochleistungs-Downloader.** Parallele Segmente mit Work-Stealing, Fortsetzen nach Abbruch, Mirror-Listen, erneuerte Links bei 403/410, Stückprüfung und SHA-256, signierte Prüfsummen (OpenPGP) und signierte Metadaten.
- **Tiny11, Tiny11 Core und Tiny10** als eigener, nachvollziehbarer Builder: die Profile sind Daten, der Ablauf ist Code mit Rollback bei Fehler und Abbruch.
- **Optische Medien.** Brennen, Löschen und Auslesen über IMAPI2; das Auslesen wiederholt fehlerhafte Sektoren, führt eine Fehlerkarte und lässt sich fortsetzen. Kopiergeschützte Discs werden abgelehnt.
- **Zielgeräte-Check.** Analyse eines Rechners vor der Installation: Windows-11-Anforderungen einzeln, TPM, Secure Boot, VMD/RAID-Treiberbedarf, OEM-Schlüssel.
- **Keine Telemetrie.** Es werden keine Nutzungsdaten gesendet.

## Funktionsstand

| Bereich | Stand |
| --- | --- |
| Laufwerkserkennung, Schutz, Identitätsprüfung, Hotplug | umgesetzt |
| Abbild 1:1 schreiben (DD), mehrere Ziele, Rücklesen | umgesetzt |
| Planer (MBR/GPT, FAT/NTFS/exFAT, UEFI:NTFS, Alt-BIOS) und Vorschau | umgesetzt |
| FAT-/ext-Formatierer, MBR-/GPT-Builder | umgesetzt, gegen fsck/sfdisk/e2fsck geprüft |
| Image-Analyse (ISO, El Torito, WIM, Partitionstabellen, komprimierte Images, Apple UDIF) | umgesetzt |
| Secure-Boot-/EFI-Analyse mit Sperrdaten | umgesetzt |
| Downloader, Katalog (Windows, 18 Linux-/BSD-Anbieter, Rettungsmedien), Image-Bibliothek | umgesetzt |
| Tiny11 / Tiny11 Core / Tiny10 | umgesetzt (DISM-Pfad nur auf Windows prüfbar) |
| CD/DVD/BD brennen, löschen, auslesen | umgesetzt (echte Brenner noch ungeprüft) |
| Zielgeräte-Check, Kunden-PC-Erfassung (verschlüsseltes Kundenblatt) | umgesetzt |
| Windows-Setup-Medien (Dateien kopieren, Split-WIM, BIOS-/UEFI-Start, Rücklesen) | umgesetzt (Startcode unter QEMU geprüft, echte Hardware offen) |
| Antwortdatei, Treiber, Bypässe, Secure-Boot-Zertifikat 2023 | umgesetzt (Dateien und DISM-Pfad nur auf Windows prüfbar) |
| Linux-ISO-Modus, Syslinux/GRUB, Persistenz | umgesetzt, unter QEMU (BIOS und UEFI) geprüft |
| FreeDOS, MS-DOS, Disketten, Laufwerk formatieren | umgesetzt, FreeDOS unter QEMU geprüft |
| Laufwerk wiederherstellen, Medium prüfen, komprimierte Images, bmap, Persistenz bei DD | umgesetzt |
| Oberfläche: Schreiben, Downloads, Disc, Tiny-Builder, Werkzeuge (Medium prüfen, Laufwerk wiederherstellen), Werkstatt (Zielgeräte-Check) | umgesetzt |
| Stick-Test (gefälschte Kapazität, fehlerhafte Blöcke) | umgesetzt (echte Sticks ungeprüft) |
| Backup/Restore von Datenträgern, sichere Löschung, Windows To Go, Multiboot | in Arbeit |

## Kommandozeile

`bootrix-cli` bietet dieselben Funktionen wie die Oberfläche, mit JSON-Ausgabe (`--json`) und stabilen Exit-Codes.

```
bootrix-cli disks [--all] [--usb-hdd] [--json]
bootrix-cli write <image> -d <disk> [-d <disk> ...] [--confirm <seriennummer>] [--no-verify]
bootrix-cli write <image> -d <disk> [--mode extract] [--scheme gpt] [--fs ntfs] [--persistence 4096] [--bypass-tpm] ...
bootrix-cli format -d <disk> [--fs fat32] [--label NAME]       bootrix-cli dos -d <disk> [--system freedos|msdos]
bootrix-cli plan <image> --size-gb 16 [--json]                 bootrix-cli inspect <image> [--json]
bootrix-cli verify <image> -d <disk> [--mode auto|raw|files]   bootrix-cli restore-drive -d <disk> [--scheme mbr|gpt] [--fs fat32]
bootrix-cli stick-test -d <disk> [--mode capacity|quick|thorough]
bootrix-cli disc drives|burn|rip|erase|eject
bootrix-cli hash <datei> [-a sha256]
bootrix-cli catalog products [-f windows|linux|bsd|dos|rescue|utility]
bootrix-cli catalog variants <produkt>
bootrix-cli download <produkt|url> [-v <variante>] [-a x64] [-l de-de] [-o <ordner>]
bootrix-cli library list [suche]        bootrix-cli library cleanup [--max-size-gb N] [--apply]
bootrix-cli customer capture -o kunde.bootrixsheet [--wlan-keys] [--bitlocker-keys] [--product-key] [--password-env VAR]
bootrix-cli customer show kunde.bootrixsheet [--password-env VAR]
bootrix-cli tiny profiles
bootrix-cli tiny editions <iso>
bootrix-cli tiny build <iso> -p tiny11 -e Pro -o tiny11.iso [--keep edge] [--local-account Name]
```

Ohne `--confirm` fragt `write` nach der Seriennummer des Ziels (oder `diskN`, wenn es keine gibt). Nicht interaktiv ist `--confirm` Pflicht.

## Bauen

```
dotnet build Bootrix.slnx -c Release
dotnet test tests/Bootrix.Core.Tests -c Release
dotnet test tests/Bootrix.Windows.Tests -c Release
```

Benötigt wird das .NET 10 SDK. Die Windows-Projekte lassen sich auch unter Linux kompilieren, ausgeführt wird Bootrix nur unter Windows. Viele Formattests nutzen Systemwerkzeuge (`dosfstools`, `mtools`, `e2fsprogs`, `gdisk`, `xorriso`, `nasm`, `qemu`); fehlt eines, wird der Test übersprungen.

```
src/Bootrix.Core      plattformneutrale Logik (Planer, Formate, Downloader, Katalog, Jobs)
src/Bootrix.Windows   Win32-Engine (Datenträger, DISM, IMAPI2, Broker)
src/Bootrix.App       WPF-Oberfläche (Fluent)
src/Bootrix.Cli       Kommandozeile
tests/                xUnit-Tests; Windows- und Linux-CI
assets/third-party/   eingebundene Drittkomponenten mit Herkunft und Lizenz
```

## Sicherheit und Datenschutz

- Die Oberfläche, der Downloader und alle Datei-Parser laufen ohne Administratorrechte.
- Der Administrator-Prozess arbeitet nur in `C:\ProgramData\Bootrix`, einem Ordner, den allein Administratoren und SYSTEM ändern dürfen; gehört er jemand anderem, startet der Prozess nicht. Dateien des Benutzers (Quell-ISO, fertige ISO) liest und schreibt er im Namen des Benutzers.
- Der Broker prüft jede Anfrage und öffnet Abbilder mit den Rechten des Benutzers, nicht mit seinen eigenen.
- Heruntergeladene Daten werden gegen Prüfsummen und, wo der Hersteller sie anbietet, gegen Signaturen geprüft. Katalogaktualisierungen sind nur mit einem eingebauten Schlüssel zulässig.
- Passwörter und Schlüssel aus Aufträgen landen in keinem Protokoll.
- Bootrix verteilt keine Microsoft-Binärdateien. `oscdimg`, `efisys` und Boot-Code für Windows-Medien stammen zur Laufzeit vom System oder aus dem Image des Benutzers.
- Die Rettungsmedien-Liste verweist auf bekannte, offiziell verbreitete Werkzeuge. Passwort-Reset-Werkzeuge sind für eigene oder ausdrücklich freigegebene Geräte gedacht.

## Lizenz

GPL-3.0-or-later, siehe [LICENSE](LICENSE). Drittkomponenten und ihre Lizenzen: [assets/third-party/SOURCES.md](assets/third-party/SOURCES.md).

---

# Bootrix (English)

Bootrix creates bootable media from ISO and disk images: USB sticks, hard disks, SD cards, floppies, CDs, DVDs and Blu-rays. It is aimed at IT service and workshop use but works for everyone. It replaces REX and is a complete rewrite (.NET 10, WPF, a Win32 engine and a command line). It runs on Windows 10 and 11 (x64 and Arm64).

**Status:** under development. The core, engine, catalog, downloader and parts of the user interface are implemented and tested on Linux and Windows with every push. The write paths for Windows, Linux, DOS and Apple media are arriving step by step; see the table above ("Funktionsstand") and the [hardware checklist](docs/hardware-checkliste.md) for what still needs real hardware.

Highlights: physical-drive selection with serial-number confirmation and hard protection of the system disk; a plan that is shown before anything is written; valid FAT, MBR/GPT, UEFI:NTFS and split-WIM media including 4Kn and old-BIOS workarounds; read-back verification; an unprivileged UI with a small elevated broker; a segmented downloader with resume and signed checksums; a catalog of Windows, Linux/BSD and rescue images; Tiny11/Tiny11 Core/Tiny10 builder; disc burning and ripping with bad-sector handling; target-PC analysis; no telemetry.

Build: `dotnet build Bootrix.slnx -c Release`, tests: `dotnet test tests/Bootrix.Core.Tests -c Release` (.NET 10 SDK). License: GPL-3.0-or-later.
