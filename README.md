# Bootrix

Bootrix erstellt bootfähige Medien: USB-Sticks, Festplatten, SD-Karten, CDs, DVDs und Blu-rays aus ISO- und Disk-Images. Es richtet sich an IT-Service und Werkstatt, ist aber auch für Einzelnutzer gedacht.

Bootrix ist der Nachfolger von REX und wird komplett neu entwickelt (.NET 10, WPF, Win32-Engine). Der Stand ist in Arbeit.

## Geplant

- Physische Laufwerke statt Laufwerksbuchstaben, Schutz der System- und Bootplatte, Bestätigung per Seriennummer
- MBR und GPT, FAT12/16/32, exFAT, NTFS, UEFI:NTFS, auch für ältere BIOS-Rechner und alte Sticks
- Windows-Setup-Medien mit WIM-Split, Unattend-Optionen, Treibern und Secure-Boot-Zertifikat 2023
- Linux-, BSD-, DOS-, Apple-Images und DD-Modus mit Verifikation
- CD/DVD/BD brennen, löschen und auslesen
- ISO-Downloader mit Segmentierung und Fortsetzen (Windows und Linux, automatisch oder manuell)
- Tiny11, Tiny11 Core und Tiny10 als eigener Builder
- Rettungsmedien, Profile, Auftragsprotokolle, mehrere Sticks gleichzeitig
- Kommandozeile mit denselben Funktionen (`bootrix-cli`)

## Bauen

```
dotnet build Bootrix.slnx -c Release
dotnet test tests/Bootrix.Core.Tests -c Release
```

Benötigt wird das .NET 10 SDK. Die Windows-Projekte lassen sich auch unter Linux kompilieren, ausgeführt wird Bootrix nur unter Windows 10/11.

## Lizenz

GPL-3.0-or-later, siehe [LICENSE](LICENSE).
