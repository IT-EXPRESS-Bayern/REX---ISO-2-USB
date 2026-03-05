<div align="center">
  <h1>🚀 REX - ISO 2 USB Ultimate Tool</h1>
  <p><b>Der extrem schnelle, moderne und asynchrone ISO-Flasher für Windows & Linux Image-Dateien.</b></p>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?style=for-the-badge&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/Support-Windows_|_Linux-green?style=for-the-badge" alt="Support" />
</div>

<br />

REX ISO 2 USB ist ein leistungsstarkes, komplett asynchrones Tool, das in C# / .NET 8 entwickelt wurde, um Boot-Sticks so schnell und sicher wie möglich zu erstellen. 

Egal ob du eine klassische **Windows-Installation** vorbereitest (inkl. TPM/SecureBoot-Bypass für Windows 11) oder eine **Hybride Linux-ISO** (wie Ubuntu, Proxmox, Debian) bitgenau auf einen Stick brennen möchtest – REX erledigt das blitzschnell und ohne eingefrorene Benutzeroberflächen.

---

## ✨ Features

- ⚡ **Extreme Performance:** Nutzt einen massiven **4-MB bis 8-MB Daten-Puffer** und reine `async/await`-I/O-Operationen für maximale Schreibgeschwindigkeit auf schnelle USB-3.0- und NVMe-Sticks.
- 🐧 **Linux & Hybrid ISO Support (NEU):** Der neue "Raw Flash"-Modus schreibt Hybrid-Images bitgenau direkt auf Datenträger-Ebene (`\\.\PhysicalDriveX`), perfekt für Linux-Distributionen oder Rettungssysteme.
- 🔓 **Ultimate Windows 11 Bypass:** Integriert automatisch Checks für TPM 2.0, CPU, RAM und SecureBoot aus Windows 11 Setups heraus. Ein Standard-Admin-User (`RexUser` / `1234`) wird gleich mit angelegt.
- 💉 **Treiber-Injektion:** Lade einen Ordner voller Treiber aus und REX packt diese automatisch in den `$WinPEDriver$`-Ordner deines Windows-Setups.
- 📦 **Standalone-Programm:** REX wird als reine Single-File `REX.exe` (ca. 76 MB) ausgeliefert. **Kein `.NET` Setup erforderlich.** Alles ist inkludiert!
- 💾 **Backup-Modus:** Sichert einen kompletten USB-Stick als `.img` Datei direkt zurück auf deine Festplatte.
- 🛡️ **Absolute Stabilität:** Fängt Fehler bei Formatierungen strikt ab und bricht beschädigte Vorgänge kontrolliert ab, um korrupte Boot-Sektoren zu vermeiden.

## 📥 Download & Benutzung

Lade einfach die neueste `REX.exe` aus den Releases herunter. 

1. **REX.exe als Administrator starten.** (Für den direkten Kernel-Zugriff auf die USB-Sektoren zwingend notwendig).
2. Oben die `.iso` Datei (Windows oder Linux) auswählen.
3. Das Ziel-Laufwerk auswählen (Vorsicht: Alle Daten darauf werden gelöscht!).
4. Den passenden Modus wählen:
   - **Windows Installation:** Kopiert die Dateien auf eine formatierte NTFS-Partition (Nur für kompatible ISOs).
   - **Linux / Raw Image Flash:** Führt einen `DD`-Schreibvorgang durch. **WICHTIG für alle Linux / Rescue ISOs.**
5. Auf **STARTEN** klicken und zurücklehnen!

## 🛠️ Für Entwickler bauen

Wenn du REX selbst kompilieren oder anpassen möchtest:

```bash
# Repository klonen
git clone https://github.com/IT-EXPRESS-Bayern/REX---ISO-2-USB.git
cd REX---ISO-2-USB

# Als Single-File Executable kompilieren (Standalone)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Die fertige `.exe` befindet sich dann in `bin\Release\net8.0-windows\win-x64\publish`.

## 📜 Lizenz
Dieses Projekt steht unter der mitgelieferten Open-Source Lizenz. Details findest du in der `LICENSE` Datei.
