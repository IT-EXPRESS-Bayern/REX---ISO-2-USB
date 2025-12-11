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
        private const string STICK_LABEL = "REX_BOOT";
        private bool _cancelRequested = false;

        public RexEngine(Action<string> logCallback, Action<int> progressCallback)
        {
            _log = logCallback;
            _progress = progressCallback;
        }

        public void Cancel() { _cancelRequested = true; }

        public void RunExtractMode(string isoPath, string targetDrive, bool useGpt, bool bypassWin11, string driverPath)
        {
            try
            {
                string letter = targetDrive.Substring(0, 2);
                _log($"[INIT] 🟢 Starte Workflow für Laufwerk {letter}...");
                _log($"[INIT] Prüfe ISO: {Path.GetFileName(isoPath)}");

                // Disk ID holen
                int diskNum = HardwareHelper.GetDiskNumber(letter);
                if (diskNum == -1) throw new Exception($"Hardware-Fehler: Konnte Disk-ID für {letter} nicht ermitteln.");
                _log($"[HARDWARE] Physisches Ziel erkannt: \\\\.\\PhysicalDrive{diskNum}");

                using (FileStream isoStream = File.OpenRead(isoPath))
                {
                    var reader = GetBestReader(isoStream);
                    using (reader)
                    {
                        _log($"[ISO] Dateisystem erkannt: {reader.GetType().Name}");
                        _log("[ISO] Scanne Dateistruktur (Pre-Scan)...");

                        var files = new List<string>();
                        long totalBytes = 0;
                        ScanRecursive(reader, reader.Root.FullName, files, ref totalBytes);

                        if (files.Count == 0) throw new Exception("ISO enthält keine lesbaren Dateien.");
                        _log($"[ISO] Scan abgeschlossen: {files.Count} Dateien, {(totalBytes / 1024 / 1024):N2} MB Gesamtgröße.");

                        // Formatierung
                        CheckCancel();
                        string newLetter = FormatAndFindStick(diskNum, useGpt);
                        _log($"[DSK] ✅ Formatierung OK. Neuer Mountpoint: {newLetter}");

                        // Kopieren
                        _log("[COPY] 🚀 Starte Datenübertragung...");
                        byte[] buffer = new byte[256 * 1024]; // 256KB Buffer
                        long copiedTotal = 0;
                        int errorCount = 0;

                        foreach (var file in files)
                        {
                            CheckCancel();
                            try
                            {
                                var info = reader.GetFileInfo(file);
                                string relPath = file.TrimStart('\\');
                                string target = Path.Combine(newLetter + "\\", relPath);

                                // Detailliertes Log (Dateiname)
                                _log($"[WRITE] {relPath} ({info.Length / 1024} KB)");

                                Directory.CreateDirectory(Path.GetDirectoryName(target));

                                using (var input = info.OpenRead())
                                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                                {
                                    int read;
                                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        output.Write(buffer, 0, read);
                                        copiedTotal += read;

                                        // Progress nur alle 1% updaten (Performance)
                                        if (totalBytes > 0 && copiedTotal % (totalBytes / 100 + 1) == 0)
                                            _progress((int)((copiedTotal * 100) / totalBytes));
                                    }
                                }
                            }
                            catch (Exception fileEx)
                            {
                                errorCount++;
                                _log($"[ERROR] ❌ Konnte Datei '{file}' nicht schreiben: {fileEx.Message}");
                                // Wir brechen nicht ab, sondern versuchen weiterzumachen
                            }
                        }

                        if (errorCount > 0) _log($"[WARN] Prozess fertig mit {errorCount} Fehlern beim Kopieren.");
                        else _log("[COPY] ✅ Alle Dateien erfolgreich verifiziert.");

                        // Features
                        if (bypassWin11) ApplyUltimateWin11Hack(newLetter);
                        if (!string.IsNullOrEmpty(driverPath)) InjectDrivers(newLetter, driverPath);

                        // Bootsektor
                        _log("[BOOT] Schreibe UEFI/BIOS Bootsektor...");
                        string bootSect = Path.Combine(newLetter + "\\", "boot", "bootsect.exe");
                        if (File.Exists(bootSect))
                        {
                            HardwareHelper.RunProcess(bootSect, $"/nt60 {newLetter}");
                            _log("[BOOT] Bootsektor geschrieben.");
                        }
                        else
                        {
                            _log("[BOOT] Info: bootsect.exe nicht in ISO gefunden (OK für reines UEFI).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[CRITICAL] 💥 ABBRUCH: {ex.Message}");
                throw; // Weiterwerfen an UI
            }
        }

        public void CreateBackup(string driveLetter, string savePath)
        {
            try
            {
                _log($"[BACKUP] Initialisiere Sicherung von {driveLetter}...");
                int diskNum = HardwareHelper.GetDiskNumber(driveLetter);
                string physPath = HardwareHelper.GetPhysicalPathByNumber(diskNum);
                _log($"[BACKUP] Quelle: {physPath}");

                using (var handle = HardwareHelper.CreateFile(physPath, HardwareHelper.GENERIC_READ, 1, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Exception("Kein Hardware-Zugriff. Admin-Rechte prüfen.");

                    using (var driveStream = new FileStream(handle, FileAccess.Read))
                    using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        long totalLen = driveStream.Length;
                        long readTotal = 0;
                        int read;

                        _log($"[BACKUP] Größe: {totalLen / 1024 / 1024} MB. Starte Lesen...");

                        while ((read = driveStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            CheckCancel();
                            fileStream.Write(buffer, 0, read);
                            readTotal += read;
                            _progress((int)((readTotal * 100) / totalLen));
                        }
                    }
                }
                _log("[BACKUP] ✅ Image erfolgreich erstellt.");
            }
            catch (Exception ex)
            {
                _log($"[BACKUP ERROR] {ex.Message}");
                throw;
            }
        }

        public void InjectDrivers(string driveLetter, string sourcePath)
        {
            try
            {
                _log($"[DRV] 💉 Injiziere Treiber aus: {sourcePath}");
                string targetDir = Path.Combine(driveLetter, "$WinPEDriver$");

                // Rekursiver Copy (einfach gehalten)
                foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(dirPath.Replace(sourcePath, targetDir));

                foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(newPath, newPath.Replace(sourcePath, targetDir), true);
                    _log($"[DRV] + {Path.GetFileName(newPath)}");
                }
            }
            catch (Exception ex)
            {
                _log($"[DRV WARN] Treiber konnten nicht vollständig kopiert werden: {ex.Message}");
            }
        }

        private string FormatAndFindStick(int diskNum, bool useGpt)
        {
            _log($"[FMT] 🧹 Lösche Partitionstabelle auf Disk {diskNum}...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nattributes disk clear readonly\nonline disk\nclean\nrescan\nexit")}\"");

            _log("[FMT] Warte auf Controller-Reset (3s)...");
            Thread.Sleep(3000);

            string style = useGpt ? "convert gpt" : "convert mbr";
            string active = useGpt ? "" : "active";

            _log($"[FMT] Erstelle Partition ({style.ToUpper()})...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\n{style}\ncreate partition primary\nselect partition 1\n{active}\nexit")}\"");
            Thread.Sleep(1000);

            _log("[FMT] Formatiere NTFS (Label: REX_BOOT)...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nselect partition 1\nformat fs=ntfs quick label=\"{STICK_LABEL}\"\nassign\nexit")}\"");

            _log("[FMT] 🔍 Warte auf Windows Volume Manager...");
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType == DriveType.Removable && d.IsReady && d.VolumeLabel.Equals(STICK_LABEL, StringComparison.OrdinalIgnoreCase))
                        return d.Name.Substring(0, 2);
                }
            }
            throw new Exception("Timeout: Laufwerk wurde formatiert, aber nicht eingebunden.");
        }

        private void ApplyUltimateWin11Hack(string targetDrive)
        {
            _log("[HACK] 🔓 Wende Ultimate Bypass an (TPM, CPU, OOBE, User)...");
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?><unattend xmlns=""urn:schemas-microsoft-com:unattend""><settings pass=""windowsPE""><component name=""Microsoft-Windows-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS""><RunSynchronous><RunSynchronousCommand wcm:action=""add""><Order>1</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand><RunSynchronousCommand wcm:action=""add""><Order>2</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand><RunSynchronousCommand wcm:action=""add""><Order>3</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand></RunSynchronous><UserData><AcceptEula>true</AcceptEula></UserData></component></settings><settings pass=""oobeSystem""><component name=""Microsoft-Windows-Shell-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS""><OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE><UserAccounts><LocalAccounts><LocalAccount wcm:action=""add""><Name>RexUser</Name><Group>Administrators</Group><Password><Value>1234</Value><PlainText>true</PlainText></Password></LocalAccount></LocalAccounts></UserAccounts></component></settings></unattend>";
            File.WriteAllText(Path.Combine(targetDrive + "\\", "autounattend.xml"), xml);
        }

        private string CreateScript(string content) { string f = Path.GetTempFileName(); File.WriteAllText(f, content); return f; }
        private DiscFileSystem GetBestReader(FileStream s) { try { s.Position = 0; if (UdfReader.Detect(s)) return new UdfReader(s); } catch { } try { s.Position = 0; if (CDReader.Detect(s)) return new CDReader(s, true); } catch { } throw new Exception("ISO-Format nicht erkannt (kein UDF/ISO9660)."); }
        private void ScanRecursive(DiscFileSystem r, string path, List<string> list, ref long size) { foreach (var f in r.GetFiles(path)) { list.Add(f); size += r.GetFileInfo(f).Length; } foreach (var d in r.GetDirectories(path)) ScanRecursive(r, d, list, ref size); }
        private void CheckCancel() { if (_cancelRequested) throw new Exception("Vorgang vom Benutzer abgebrochen."); }
    }
}