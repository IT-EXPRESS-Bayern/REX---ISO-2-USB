using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Rex
{
    public class RexEngine
    {
        private readonly Action<string> _log;
        private readonly Action<int> _progress;

        // Wir nutzen dieses Label, um den Stick nach dem Formatieren sicher wiederzufinden
        private const string STICK_LABEL = "REX_BOOT";

        public RexEngine(Action<string> logCallback, Action<int> progressCallback)
        {
            _log = logCallback;
            _progress = progressCallback;
        }

        // Hauptmethode für Windows-Installationsmedien
        public void RunExtractMode(string isoPath, string targetDrive, bool useGpt, bool bypassWin11, string driverPath)
        {
            string letter = targetDrive.Substring(0, 2);
            _log($"[INIT] Starte Prozess für Laufwerk {letter}...");

            int diskNum = HardwareHelper.GetDiskNumber(letter);
            if (diskNum == -1) throw new Exception("Konnte das physische Laufwerk nicht identifizieren.");
            _log($"[INFO] Ziel ist PhysicalDisk{diskNum}");

            using (FileStream isoStream = File.OpenRead(isoPath))
            {
                // Wir müssen erraten, ob es UDF oder ISO9660 ist
                var reader = GetBestReader(isoStream);
                using (reader)
                {
                    _log("[ISO] Analysiere Dateistruktur...");
                    var files = new List<string>();
                    long totalBytes = 0;

                    // Erst alles scannen, um die Gesamtgröße für den Progressbar zu haben
                    ScanRecursive(reader, reader.Root.FullName, files, ref totalBytes);

                    if (files.Count == 0) throw new Exception("Die ISO-Datei scheint leer oder beschädigt zu sein.");
                    _log($"[ISO] {files.Count} Dateien gefunden ({totalBytes / 1024 / 1024} MB).");

                    // Der kritische Teil: Formatieren und neu einbinden
                    string newLetter = FormatAndFindStick(diskNum, useGpt);
                    _log($"[DSK] Partitionierung abgeschlossen. Neuer Pfad: {newLetter}");

                    _log("[COPY] Schreibe Daten...");
                    byte[] buffer = new byte[128 * 1024]; // 128KB Buffer ist ein guter Kompromiss
                    long copiedTotal = 0;

                    foreach (var file in files)
                    {
                        var info = reader.GetFileInfo(file);
                        string relPath = file.TrimStart('\\');
                        string target = Path.Combine(newLetter + "\\", relPath);

                        Directory.CreateDirectory(Path.GetDirectoryName(target));

                        using (var input = info.OpenRead())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                copiedTotal += read;

                                // GUI-Updates drosseln, um Performance zu sparen
                                if (totalBytes > 0 && copiedTotal % (5 * 1024 * 1024) == 0)
                                    _progress((int)((copiedTotal * 100) / totalBytes));
                            }
                        }
                    }
                    _log("[COPY] Übertragung abgeschlossen.");

                    // Optionale Module anwenden
                    if (bypassWin11) ApplyUltimateWin11Hack(newLetter);
                    if (!string.IsNullOrEmpty(driverPath)) InjectDrivers(newLetter, driverPath);

                    // Bootsektor schreiben für Legacy-BIOS Support
                    _log("[BOOT] Installiere Bootloader...");
                    string bootSect = Path.Combine(newLetter + "\\", "boot", "bootsect.exe");
                    if (File.Exists(bootSect)) HardwareHelper.RunProcess(bootSect, $"/nt60 {newLetter}");
                }
            }
        }

        // Erstellt ein 1:1 Image des USB-Sticks
        public void CreateBackup(string driveLetter, string savePath)
        {
            _log($"[BACKUP] Starte Sicherung von {driveLetter}...");
            int diskNum = HardwareHelper.GetDiskNumber(driveLetter);
            string physPath = HardwareHelper.GetPhysicalPathByNumber(diskNum);

            _log($"[BACKUP] Öffne Hardware-Stream für {physPath}...");

            using (var handle = HardwareHelper.CreateFile(physPath, HardwareHelper.GENERIC_READ, 1, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Exception("Konnte Laufwerk nicht öffnen (Zugriff verweigert?).");

                using (var driveStream = new FileStream(handle, FileAccess.Read))
                using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[1024 * 1024]; // 1MB Chunks für Speed
                    long totalLen = driveStream.Length;
                    long readTotal = 0;
                    int read;

                    while ((read = driveStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, read);
                        readTotal += read;

                        int pct = (int)((readTotal * 100) / totalLen);
                        _progress(pct);
                    }
                }
            }
            _log("[BACKUP] Image erfolgreich geschrieben.");
        }

        public void InjectDrivers(string driveLetter, string sourcePath)
        {
            _log($"[DRV] Integriere Treiber aus {Path.GetFileName(sourcePath)}...");

            // Windows Setup sucht automatisch in $WinPEDriver$
            string targetDir = Path.Combine(driveLetter, "$WinPEDriver$");

            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetDir));
            }
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetDir), true);
            }
            _log("[DRV] Treiberintegration abgeschlossen.");
        }

        // Die aggressive 3-Phasen-Formatierung, um Timing-Probleme zu umgehen
        private string FormatAndFindStick(int diskNum, bool useGpt)
        {
            // Phase 1: Bereinigen und Schreibschutz entfernen
            _log($"[FMT] Bereinige Disk {diskNum}...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nattributes disk clear readonly\nonline disk\nclean\nrescan\nexit")}\"");

            _log("[FMT] Warte auf Controller-Reset (3s)...");
            Thread.Sleep(3000);

            // Phase 2: Partitionstabelle anlegen
            _log($"[FMT] Erstelle Partition ({(useGpt ? "GPT" : "MBR")})...");
            string style = useGpt ? "convert gpt" : "convert mbr";
            string active = useGpt ? "" : "active";
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\n{style}\ncreate partition primary\nselect partition 1\n{active}\nexit")}\"");

            Thread.Sleep(1000);

            // Phase 3: Dateisystem formatieren
            _log("[FMT] Formatiere NTFS...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nselect partition 1\nformat fs=ntfs quick label=\"{STICK_LABEL}\"\nassign\nexit")}\"");

            // Phase 4: Wiederfinden anhand des Labels
            _log("[FMT] Warte auf Laufwerksbuchstaben...");
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                foreach (var d in DriveInfo.GetDrives())
                {
                    // Wir suchen explizit nach unserem Label, da sich der Buchstabe ändern kann
                    if (d.DriveType == DriveType.Removable && d.IsReady && d.VolumeLabel.Equals(STICK_LABEL, StringComparison.OrdinalIgnoreCase))
                        return d.Name.Substring(0, 2);
                }
            }
            throw new Exception("Laufwerk wurde formatiert, aber von Windows nicht rechtzeitig eingebunden.");
        }

        // Erstellt die Antwortdatei für vollautomatische Installation
        private void ApplyUltimateWin11Hack(string targetDrive)
        {
            _log("[HACK] Injiziere 'Ultimate Bypass' (User + OOBE)...");

            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
<settings pass=""windowsPE"">
<component name=""Microsoft-Windows-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
<RunSynchronous>
<RunSynchronousCommand wcm:action=""add""><Order>1</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
<RunSynchronousCommand wcm:action=""add""><Order>2</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
<RunSynchronousCommand wcm:action=""add""><Order>3</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
</RunSynchronous>
<UserData><AcceptEula>true</AcceptEula></UserData>
</component>
</settings>
<settings pass=""oobeSystem"">
<component name=""Microsoft-Windows-Shell-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
<OOBE>
<HideEULAPage>true</HideEULAPage>
<HideOnlineAccountScreens>true</HideOnlineAccountScreens>
<HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
<ProtectYourPC>3</ProtectYourPC>
</OOBE>
<UserAccounts>
<LocalAccounts>
<LocalAccount wcm:action=""add"">
<Name>RexUser</Name><Group>Administrators</Group><Password><Value>1234</Value><PlainText>true</PlainText></Password>
</LocalAccount>
</LocalAccounts>
</UserAccounts>
</component>
</settings>
</unattend>";
            File.WriteAllText(Path.Combine(targetDrive + "\\", "autounattend.xml"), xml);
        }

        private string CreateScript(string content) { string f = Path.GetTempFileName(); File.WriteAllText(f, content); return f; }

        // Versucht verschiedene Reader, da ISOs unterschiedlich gemastert sind
        private DiscFileSystem GetBestReader(FileStream s) { try { s.Position = 0; if (UdfReader.Detect(s)) return new UdfReader(s); } catch { } try { s.Position = 0; if (CDReader.Detect(s)) return new CDReader(s, true); } catch { } throw new Exception("Unbekanntes Dateisystem."); }

        private void ScanRecursive(DiscFileSystem r, string path, List<string> list, ref long size) { foreach (var f in r.GetFiles(path)) { list.Add(f); size += r.GetFileInfo(f).Length; } foreach (var d in r.GetDirectories(path)) ScanRecursive(r, d, list, ref size); }

        public void RunDDMode(string isoPath, string driveLetter)
        {
            // DD Modus ist aktuell Platzhalter für künftige Erweiterungen
            _log("[DD] Modus noch in Entwicklung.");
        }
    }
}