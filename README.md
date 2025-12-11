# 🦖 REX - Ultimate ISO Tool

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)
![Language](https://img.shields.io/badge/language-C%23%20%7C%20.NET-purple.svg)

**REX** ist ein modernes, leistungsstarkes Tool zum Erstellen von bootfähigen USB-Sticks. Es wurde als leichte, aber mächtige Alternative zu Tools wie Rufus entwickelt, mit einem speziellen Fokus auf moderne Windows-Bereitstellung und Systemadministration.



##  Features

* **Windows 11 Bypass:** Installiere Windows 11 auf nicht unterstützter Hardware. Entfernt automatisch TPM 2.0, SecureBoot und RAM-Checks.
* **Ultimate Automation:** Überspringt den OOBE-Prozess (Out-of-Box Experience), erstellt automatisch einen lokalen Admin-Nutzer und deaktiviert den Microsoft-Konto-Zwang.
* **Backup-Funktion:** Erstelle 1:1 Images (`.img`) von deinen USB-Sticks, um Konfigurationen zu sichern.
* **Treiber-Injektion:** Integriere WLAN- oder VMD/RST-Treiber direkt in das Installationsmedium – ideal für moderne Laptops.
* **Modernes UI:** Dunkles "Cyberpunk"-Design mit Live-Debug-Log für volle Transparenz.
* **GPT & MBR:** Volle Unterstützung für moderne UEFI-Systeme (GPT) und Legacy-BIOS (MBR).
* **Smart Format:** Intelligente 3-Phasen-Formatierung, die auch beschädigte oder schreibgeschützte Sticks rettet.

## 🛠️ Technologie

REX ist in **C# (.NET 8/10)** geschrieben und nutzt Low-Level Windows APIs für direkten Hardwarezugriff.

* **DiscUtils:** Zum direkten Lesen und Extrahieren von ISO/UDF-Dateien (kein Mounten nötig).
* **WMI & PowerShell:** Für robuste Hardware-Erkennung.
* **Diskpart Interop:** Für zuverlässige Partitionierung.

## 🚀 Installation & Build

### Voraussetzungen
* Visual Studio 2022
* .NET Desktop Development Workload

### Abhängigkeiten (NuGet)
Das Projekt benötigt folgende Pakete:
```xml
<PackageReference Include="DiscUtils.Core" Version="0.16.13" />
<PackageReference Include="DiscUtils.Iso9660" Version="0.16.13" />
<PackageReference Include="DiscUtils.Udf" Version="0.16.13" />
<PackageReference Include="DiscUtils.Streams" Version="0.16.13" />
<PackageReference Include="System.Management" Version="8.0.0" />
