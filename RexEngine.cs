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
        private readonly Action<string> _statusCallback;
        private readonly Action<int> _progressCallback;

        // Label für die Wiedererkennung
        private const string STICK_LABEL = "REX_BOOT";

        public RexEngine(Action<string> statusCallback, Action<int> progressCallback)
        {
            _statusCallback = statusCallback;
            _progressCallback = progressCallback;
        }

        public void RunExtractMode(string isoPath, string targetDrive, bool useGpt, bool bypassWin11)
        {
            string initialLetter = targetDrive.Substring(0, 2);

            // 1. DISK ID SICHERN
            _statusCallback("Ermittle Disk-ID...");
            int diskNum = HardwareHelper.GetDiskNumber(initialLetter);

            if (diskNum == -1) throw new Exception("Konnte Disk-Nummer nicht ermitteln. Bitte Stick neu einstecken.");

            _statusCallback($"Ziel erkannt: Disk {diskNum}");

            using (FileStream isoStream = File.OpenRead(isoPath))
            {
                DiscFileSystem reader = GetBestReader(isoStream);
                using (reader)
                {
                    _statusCallback("Scanne ISO...");
                    var files = new List<string>();
                    long totalBytes = 0;
                    ScanRecursive(reader, reader.Root.FullName, files, ref totalBytes);

                    if (files.Count == 0) throw new Exception("ISO leer.");

                    // 2. FORMATIEREN (Der aggressive Fix!)
                    string newDriveLetter = FormatAndFindStick(diskNum, useGpt);

                    _statusCallback($"Bereit auf: {newDriveLetter}");

                    // 3. KOPIEREN
                    byte[] buffer = new byte[64 * 1024];
                    long copiedTotal = 0;

                    foreach (var file in files)
                    {
                        var info = reader.GetFileInfo(file);
                        string relativePath = file.TrimStart('\\');
                        string targetPath = Path.Combine(newDriveLetter + "\\", relativePath);

                        string targetDir = Path.GetDirectoryName(targetPath);
                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        using (var input = info.OpenRead())
                        using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                copiedTotal += read;
                                if (totalBytes > 0 && copiedTotal % (1024 * 1024) == 0)
                                    _progressCallback((int)((copiedTotal * 100) / totalBytes));
                            }
                        }
                    }

                    if (bypassWin11)
                    {
                        _statusCallback("Win11 Bypass...");
                        ApplyWin11Hack(newDriveLetter);
                    }

                    _statusCallback("Bootsektor...");
                    string bootSect = Path.Combine(newDriveLetter + "\\", "boot", "bootsect.exe");
                    if (File.Exists(bootSect))
                        HardwareHelper.RunProcess(bootSect, $"/nt60 {newDriveLetter}");
                }
            }
        }

        public void RunDDMode(string isoPath, string driveLetter)
        {
            _statusCallback("Suche Disk...");
            int diskNum = HardwareHelper.GetDiskNumber(driveLetter);
            if (diskNum == -1) throw new Exception("Disk Nummer nicht gefunden.");

            string physPath = $@"\\.\PhysicalDrive{diskNum}";

            _statusCallback("Dismount...");
            HardwareHelper.RunProcess("powershell.exe", $"-Command \"Dismount-DiskImage -DevicePath {physPath} -ErrorAction SilentlyContinue\"");

            _statusCallback("Schreibe Image...");
            using (var iso = File.OpenRead(isoPath))
            using (var handle = HardwareHelper.CreateFile(physPath, 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Exception("Zugriff verweigert.");
                using (var drive = new FileStream(handle, FileAccess.Write))
                {
                    byte[] buf = new byte[1024 * 1024];
                    long total = iso.Length;
                    long written = 0;
                    int read;
                    while ((read = iso.Read(buf, 0, buf.Length)) > 0)
                    {
                        drive.Write(buf, 0, read);
                        written += read;
                        _progressCallback((int)((written * 100) / total));
                    }
                    drive.Flush();
                }
            }
        }

        // --- DER FIX: 3-PHASEN FORMATIERUNG ---
        private string FormatAndFindStick(int diskNum, bool useGpt)
        {
            // PHASE 1: UNLOCK & CLEAN
            _statusCallback($"Lösche Disk {diskNum} (Clean)...");

            // 'attributes disk clear readonly' entfernt Schreibschutz
            // 'online disk' weckt den Stick auf
            string cleanScript = $@"
select disk {diskNum}
attributes disk clear readonly
online disk
clean
rescan
exit";
            RunDiskpart(cleanScript);

            // Zwangspause für den Controller
            _statusCallback("Controller Reset (3s)...");
            Thread.Sleep(3000);

            // PHASE 2: PARTITIONIEREN
            _statusCallback($"Erstelle Partition ({(useGpt ? "GPT" : "MBR")})...");

            string style = useGpt ? "convert gpt" : "convert mbr";
            string active = useGpt ? "" : "active";

            string partitionScript = $@"
select disk {diskNum}
{style}
create partition primary
select partition 1
{active}
exit";
            RunDiskpart(partitionScript);

            Thread.Sleep(1000); // Kurze Pause

            // PHASE 3: FORMATIEREN
            _statusCallback("Formatiere (NTFS)...");

            string formatScript = $@"
select disk {diskNum}
select partition 1
format fs=ntfs quick label=""{STICK_LABEL}""
assign
exit";
            RunDiskpart(formatScript);

            // PHASE 4: SUCHE NACH LABEL
            _statusCallback("Warte auf Laufwerk...");

            // Wir geben Windows 30 Sekunden Zeit (Manche Sticks sind langsam)
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);

                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType == DriveType.Removable && d.IsReady)
                    {
                        try
                        {
                            // Wir identifizieren den Stick über das Label "REX_BOOT"
                            if (d.VolumeLabel.Equals(STICK_LABEL, StringComparison.OrdinalIgnoreCase))
                            {
                                return d.Name.Substring(0, 2); // z.B. "F:"
                            }
                        }
                        catch { }
                    }
                }
            }

            throw new Exception($"Formatierung abgeschlossen, aber Laufwerk '{STICK_LABEL}' wurde nicht gefunden. Bitte Stick neu anstecken.");
        }

        private void RunDiskpart(string script)
        {
            string f = Path.GetTempFileName();
            File.WriteAllText(f, script);
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{f}\"");
            File.Delete(f);
        }

        private void ApplyWin11Hack(string targetDrive)
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
<settings pass=""windowsPE"">
<component name=""Microsoft-Windows-Setup"" processorArchitecture=""amd64"" publicKeyToken=""31bf3856ad364e35"" language=""neutral"" versionScope=""nonSxS"">
<RunSynchronous>
<RunSynchronousCommand wcm:action=""add""><Order>1</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
<RunSynchronousCommand wcm:action=""add""><Order>2</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
<RunSynchronousCommand wcm:action=""add""><Order>3</Order><Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>
</RunSynchronous>
</component>
</settings>
</unattend>";
            File.WriteAllText(Path.Combine(targetDrive + "\\", "autounattend.xml"), xml);
        }

        private DiscFileSystem GetBestReader(FileStream s)
        {
            try { s.Position = 0; if (UdfReader.Detect(s)) return new UdfReader(s); } catch { }
            try { s.Position = 0; if (CDReader.Detect(s)) return new CDReader(s, true); } catch { }
            throw new Exception("ISO Format unbekannt.");
        }

        private void ScanRecursive(DiscFileSystem r, string path, List<string> list, ref long size)
        {
            foreach (var f in r.GetFiles(path)) { list.Add(f); size += r.GetFileInfo(f).Length; }
            foreach (var d in r.GetDirectories(path)) ScanRecursive(r, d, list, ref size);
        }
    }
}