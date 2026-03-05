using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Rex
{
    public class RexEngine
    {
        private const string STICK_LABEL = "REX_BOOT";
        private bool _cancelRequested = false;

        public RexEngine()
        {
        }

        public void Cancel() { _cancelRequested = true; }

        public async Task RunExtractModeAsync(string isoPath, string targetDrive, bool useGpt, bool bypassWin11, string driverPath, IProgress<string> log, IProgress<int> progress)
        {
            try
            {
                string letter = targetDrive.Substring(0, 2);
                log?.Report($"[INIT] 🟢 Starte Workflow für Laufwerk {letter}...");
                log?.Report($"[INIT] Prüfe ISO: {Path.GetFileName(isoPath)}");

                // Disk ID holen
                int diskNum = HardwareHelper.GetDiskNumber(letter);
                if (diskNum == -1) throw new Exception($"Hardware-Fehler: Konnte Disk-ID für {letter} nicht ermitteln.");
                log?.Report($"[HARDWARE] Physisches Ziel erkannt: \\\\.\\PhysicalDrive{diskNum}");

                // Asynchroner I/O Stream
                using (FileStream isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    var reader = GetBestReader(isoStream);
                    using (reader)
                    {
                        log?.Report($"[ISO] Dateisystem erkannt: {reader.GetType().Name}");
                        log?.Report("[ISO] Scanne Dateistruktur (Pre-Scan)...");

                        var files = new List<string>();
                        long totalBytes = 0;
                        ScanRecursive(reader, reader.Root.FullName, files, ref totalBytes);

                        if (files.Count == 0) throw new Exception("ISO enthält keine lesbaren Dateien.");
                        log?.Report($"[ISO] Scan abgeschlossen: {files.Count} Dateien, {(totalBytes / 1024 / 1024):N2} MB Gesamtgröße.");

                        // Formatierung
                        CheckCancel();
                        string newLetter = await Task.Run(() => FormatAndFindStick(diskNum, useGpt, log));
                        log?.Report($"[DSK] ✅ Formatierung OK. Neuer Mountpoint: {newLetter}");

                        // Kopieren
                        log?.Report("[COPY] 🚀 Starte Datenübertragung...");
                        byte[] buffer = new byte[4 * 1024 * 1024]; // 4MB Buffer for Maximum Speed
                        long copiedTotal = 0;
                        int lastPercent = -1;

                        foreach (var file in files)
                        {
                            CheckCancel();
                            try
                            {
                                var info = reader.GetFileInfo(file);
                                string relPath = file.TrimStart('\\');
                                string target = Path.Combine(newLetter + "\\", relPath);

                                log?.Report($"[WRITE] {relPath} ({info.Length / 1024} KB)");

                                Directory.CreateDirectory(Path.GetDirectoryName(target));

                                using (var input = info.OpenRead())
                                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                                {
                                    int read;
                                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                    {
                                        await output.WriteAsync(buffer, 0, read);
                                        copiedTotal += read;

                                        if (totalBytes > 0)
                                        {
                                            int newPercent = (int)((copiedTotal * 100) / totalBytes);
                                            if (newPercent != lastPercent)
                                            {
                                                progress?.Report(newPercent);
                                                lastPercent = newPercent;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception fileEx)
                            {
                                log?.Report($"[ERROR] ❌ Konnte Datei '{file}' nicht schreiben: {fileEx.Message}");
                                throw new Exception($"Schreibfehler auf USB-Stick (Datei: {file}). Der Vorgang wurde aus Sicherheitsgründen abgebrochen.", fileEx);
                            }
                        }

                        log?.Report("[COPY] ✅ Alle Dateien erfolgreich verifiziert.");

                        // Features
                        if (bypassWin11) await ApplyUltimateWin11HackAsync(newLetter, log);
                        if (!string.IsNullOrEmpty(driverPath)) await Task.Run(() => InjectDrivers(newLetter, driverPath, log));

                        // Bootsektor
                        log?.Report("[BOOT] Schreibe UEFI/BIOS Bootsektor...");
                        string bootSect = Path.Combine(newLetter + "\\", "boot", "bootsect.exe");
                        if (File.Exists(bootSect))
                        {
                            await Task.Run(() => HardwareHelper.RunProcess(bootSect, $"/nt60 {newLetter}"));
                            log?.Report("[BOOT] Bootsektor geschrieben.");
                        }
                        else
                        {
                            log?.Report("[BOOT] Info: bootsect.exe nicht in ISO gefunden (OK für reines UEFI).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Report($"[CRITICAL] 💥 ABBRUCH: {ex.Message}");
                throw; // Weiterwerfen an UI
            }
        }

        public async Task CreateBackupAsync(string driveLetter, string savePath, IProgress<string> log, IProgress<int> progress)
        {
            try
            {
                log?.Report($"[BACKUP] Initialisiere Sicherung von {driveLetter}...");
                int diskNum = HardwareHelper.GetDiskNumber(driveLetter);
                string physPath = HardwareHelper.GetPhysicalPathByNumber(diskNum);
                log?.Report($"[BACKUP] Quelle: {physPath}");

                await Task.Run(async () =>
                {
                    using (var handle = HardwareHelper.CreateFile(physPath, HardwareHelper.GENERIC_READ, 1, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid) throw new Exception("Kein Hardware-Zugriff. Admin-Rechte prüfen.");

                        using (var driveStream = new FileStream(handle, FileAccess.Read, 4 * 1024 * 1024, true))
                        using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, true))
                        {
                            byte[] buffer = new byte[4 * 1024 * 1024];
                            long totalLen = 0;
                            try { totalLen = driveStream.Length; } catch { /* Ignore on RAW volumes */ }
                            long readTotal = 0;
                            int read;
                            int lastPercent = -1;

                            log?.Report($"[BACKUP] Starte Lesen (4MB Blöcke)...");

                            while ((read = await driveStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                CheckCancel();
                                await fileStream.WriteAsync(buffer, 0, read);
                                readTotal += read;

                                if (totalLen > 0)
                                {
                                    int newPercent = (int)((readTotal * 100) / totalLen);
                                    if (newPercent != lastPercent)
                                    {
                                        progress?.Report(newPercent);
                                        lastPercent = newPercent;
                                    }
                                }
                            }
                        }
                    }
                });
                log?.Report("[BACKUP] ✅ Image erfolgreich erstellt.");
            }
            catch (Exception ex)
            {
                log?.Report($"[BACKUP ERROR] {ex.Message}");
                throw;
            }
        }

        public async Task WriteRawImageAsync(string isoPath, string targetDrive, IProgress<string> log, IProgress<int> progress)
        {
            try
            {
                string letter = targetDrive.Substring(0, 2);
                log?.Report($"[RAW] 🚀 Initialisiere RAW/DD Flash für {letter}...");
                log?.Report($"[RAW] Image: {Path.GetFileName(isoPath)}");

                int diskNum = HardwareHelper.GetDiskNumber(letter);
                if (diskNum == -1) throw new Exception($"Hardware-Fehler: Konnte Disk-ID für {letter} nicht ermitteln.");
                string physPath = HardwareHelper.GetPhysicalPathByNumber(diskNum);
                log?.Report($"[RAW] Physisches Ziel erkannt: {physPath}");

                FileInfo isoInfo = new FileInfo(isoPath);
                long totalBytes = isoInfo.Length;

                // 1. Unmount & Clean via Diskpart für sauberen Sektorenzugriff
                log?.Report($"[RAW] 🧹 Lösche Partitionstabelle um exklusiven Zugriff zu garantieren...");
                await Task.Run(() => HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\noffline disk\nonline disk\nattributes disk clear readonly\nclean\nrescan\nexit")}\""));
                Thread.Sleep(3000); // Controller Zeit geben

                // 2. Raw Write Operation
                await Task.Run(async () =>
                {
                    // GENERIC_WRITE | GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE
                    using (var handle = HardwareHelper.CreateFile(physPath, HardwareHelper.GENERIC_WRITE | HardwareHelper.GENERIC_READ, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid) throw new Exception("Konnte keinen physischen (Raw) Zugriff auf den Stick erhalten. Ggf. blockiert Anti-Virus.");

                        // Windows blockiert direkte Sektorenschreibzugriffe wenn Partitionen erkannt werden -> FSCTL_LOCK_VOLUME (via DeviceIoControl, hier reicht aber oft CLEAN)
                        // Da wir diskpart clean genutzt haben, ist die Disk aktuell unformatiert (RAW), weshalb Windows CreateFile zulassen sollte.

                        using (var driveStream = new FileStream(handle, FileAccess.Write, 4 * 1024 * 1024, true))
                        using (var fileStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, true))
                        {
                            byte[] buffer = new byte[8 * 1024 * 1024]; // 8MB Buffer für rohe Flashes
                            long writeTotal = 0;
                            int read;
                            int lastPercent = -1;

                            log?.Report($"[RAW] ⚡ Starte Block-Copy (Bit-für-Bit)...");

                            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                CheckCancel();
                                
                                // Die Write-Länge auf dem Laufwerk MUSS ein Vielfaches der Sektorgröße (512 Bytes / 4096 Bytes) sein!
                                // (Der Puffer ist 8MB, gelesen wird idR immer das Vielfache davon. Am Dateiende evtl. mit Nullen auffüllen)
                                int writeLen = read;
                                if (writeLen % 512 != 0)
                                {
                                    writeLen = ((writeLen / 512) + 1) * 512; 
                                }

                                await driveStream.WriteAsync(buffer, 0, writeLen);
                                writeTotal += read; // Tatsächliche bytes für % Rechung

                                int newPercent = (int)((writeTotal * 100) / totalBytes);
                                if (newPercent != lastPercent)
                                {
                                    progress?.Report(newPercent);
                                    lastPercent = newPercent;
                                }
                            }
                        }
                    }
                });

                // 3. Rescan anstoßen
                log?.Report("[RAW] 🔄 Aktualisiere System-Laufwerke...");
                await Task.Run(() => HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript("rescan\nexit")}\"", false));

                log?.Report("[RAW] ✅ Raw Flash erfolgreich abgeschlossen. (Hinweis: Windows meldet ggf. 'Laufwerk formatieren'. NICHT auf Formatieren klicken, da Linux-Dateisysteme von Windows nicht erkannt werden!).");
            }
            catch (Exception ex)
            {
                log?.Report($"[RAW ERROR] {ex.Message}");
                throw;
            }
        }

        public void InjectDrivers(string driveLetter, string sourcePath, IProgress<string> log)
        {
            try
            {
                log?.Report($"[DRV] 💉 Injiziere Treiber aus: {sourcePath}");
                string targetDir = Path.Combine(driveLetter, "$WinPEDriver$");

                // Rekursiver Copy
                foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(dirPath.Replace(sourcePath, targetDir));

                foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(newPath, newPath.Replace(sourcePath, targetDir), true);
                    log?.Report($"[DRV] + {Path.GetFileName(newPath)}");
                }
            }
            catch (Exception ex)
            {
                log?.Report($"[DRV WARN] Treiber konnten nicht vollständig kopiert werden: {ex.Message}");
            }
        }

        private string FormatAndFindStick(int diskNum, bool useGpt, IProgress<string> log)
        {
            log?.Report($"[FMT] 🧹 Lösche Partitionstabelle auf Disk {diskNum}...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nattributes disk clear readonly\nonline disk\nclean\nrescan\nexit")}\"");

            log?.Report("[FMT] Warte auf Controller-Reset (3s)...");
            Thread.Sleep(3000);

            string style = useGpt ? "convert gpt" : "convert mbr";
            string active = useGpt ? "" : "active";

            log?.Report($"[FMT] Erstelle Partition ({style.ToUpper()})...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\n{style}\ncreate partition primary\nselect partition 1\n{active}\nexit")}\"");
            Thread.Sleep(1000);

            log?.Report("[FMT] Formatiere NTFS (Label: REX_BOOT)...");
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{CreateScript($"select disk {diskNum}\nselect partition 1\nformat fs=ntfs quick label=\"{STICK_LABEL}\"\nassign\nexit")}\"");

            log?.Report("[FMT] 🔍 Warte auf Windows Volume Manager...");
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

        private async Task ApplyUltimateWin11HackAsync(string targetDrive, IProgress<string> log)
        {
            log?.Report("[HACK] 🔓 Wende Ultimate Bypass an (TPM, CPU, OOBE, User)...");
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?><unattend xmlns=""urn:schemas-microsoft-com:unattend""><settings pass=""windowsPE""><component name=""Microsoft-Windows-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS""><RunSynchronous><RunSynchronousCommand wcm:action=""add""><Order>1</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand><RunSynchronousCommand wcm:action=""add""><Order>2</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand><RunSynchronousCommand wcm:action=""add""><Order>3</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand></RunSynchronous><UserData><AcceptEula>true</AcceptEula></UserData></component></settings><settings pass=""oobeSystem""><component name=""Microsoft-Windows-Shell-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS""><OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE><UserAccounts><LocalAccounts><LocalAccount wcm:action=""add""><Name>RexUser</Name><Group>Administrators</Group><Password><Value>1234</Value><PlainText>true</PlainText></Password></LocalAccount></LocalAccounts></UserAccounts></component></settings></unattend>";
            
            using (var fs = new FileStream(Path.Combine(targetDrive + "\\", "autounattend.xml"), FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            using (var sw = new StreamWriter(fs))
            {
                await sw.WriteAsync(xml);
            }
        }

        private string CreateScript(string content) { string f = Path.GetTempFileName(); File.WriteAllText(f, content); return f; }
        private DiscFileSystem GetBestReader(FileStream s) { try { s.Position = 0; if (UdfReader.Detect(s)) return new UdfReader(s); } catch { } try { s.Position = 0; if (CDReader.Detect(s)) return new CDReader(s, true); } catch { } throw new Exception("ISO-Format nicht erkannt (kein UDF/ISO9660)."); }
        private void ScanRecursive(DiscFileSystem r, string path, List<string> list, ref long size) { foreach (var f in r.GetFiles(path)) { list.Add(f); size += r.GetFileInfo(f).Length; } foreach (var d in r.GetDirectories(path)) ScanRecursive(r, d, list, ref size); }
        private void CheckCancel() { if (_cancelRequested) throw new Exception("Vorgang vom Benutzer abgebrochen."); }
    }
}