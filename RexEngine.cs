using System;
using System.Collections.Generic;
using System.IO;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Rex
{
    public class RexEngine
    {
        // Callbacks, um der GUI Bericht zu erstatten
        private readonly Action<string> _statusCallback;
        private readonly Action<int> _progressCallback;

        public RexEngine(Action<string> statusCallback, Action<int> progressCallback)
        {
            _statusCallback = statusCallback;
            _progressCallback = progressCallback;
        }

        public void RunExtractMode(string isoPath, string targetDrive, bool useGpt, bool bypassWin11)
        {
            _statusCallback("Analysiere ISO...");

            using (FileStream isoStream = File.OpenRead(isoPath))
            {
                DiscFileSystem reader = GetBestReader(isoStream);
                using (reader)
                {
                    // 1. Scan
                    _statusCallback("Scanne Dateien...");
                    var files = new List<string>();
                    long totalBytes = 0;
                    ScanRecursive(reader, reader.Root.FullName, files, ref totalBytes);

                    if (files.Count == 0) throw new Exception("ISO leer oder unbekanntes Format.");

                    // 2. Formatieren (MBR oder GPT)
                    _statusCallback($"Formatiere ({(useGpt ? "GPT" : "MBR")})...");
                    FormatStick(targetDrive, useGpt);

                    // 3. Kopieren
                    _statusCallback("Kopiere Dateien...");
                    byte[] buffer = new byte[64 * 1024];
                    long copiedTotal = 0;

                    foreach (var file in files)
                    {
                        var info = reader.GetFileInfo(file);
                        string targetPath = Path.Combine(targetDrive, file.TrimStart('\\'));

                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        _statusCallback($"Kopiere: {Path.GetFileName(file)}");

                        using (var input = info.OpenRead())
                        using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                        {
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                copiedTotal += read;
                                if (copiedTotal % (1024 * 1024) == 0)
                                    _progressCallback((int)((copiedTotal * 100) / totalBytes));
                            }
                        }
                    }

                    // 4. Feature: Windows 11 Bypass
                    if (bypassWin11)
                    {
                        _statusCallback("Wende Windows 11 Bypass an...");
                        ApplyWin11Hack(targetDrive);
                    }

                    // 5. Bootsektor (Nur bei MBR/Legacy nötig, schadet bei GPT aber meist nicht)
                    _statusCallback("Schreibe Bootsektor...");
                    string bootSect = Path.Combine(targetDrive, "boot", "bootsect.exe");
                    if (File.Exists(bootSect))
                        HardwareHelper.RunProcess(bootSect, $"/nt60 {targetDrive.Substring(0, 2)}");
                }
            }
        }

        public void RunDDMode(string isoPath, string driveLetter)
        {
            _statusCallback("Suche Hardware...");
            string physPath = HardwareHelper.GetPhysicalPath(driveLetter);
            if (physPath == null) throw new Exception("Physischer Pfad nicht gefunden.");

            _statusCallback("Dismount...");
            HardwareHelper.RunProcess("powershell.exe", $"-Command \"Dismount-DiskImage -DevicePath {physPath} -ErrorAction SilentlyContinue\"");

            _statusCallback("Raw Write...");
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

        private void FormatStick(string driveLetter, bool useGpt)
        {
            string d = driveLetter.Substring(0, 1);
            // Unterschiedliche Befehle für GPT vs MBR
            string styleCmd = useGpt ? "convert gpt" : "convert mbr";
            // GPT Partitionen werden nicht als "active" markiert (das macht das UEFI Bios selbst)
            string activeCmd = useGpt ? "" : "active";

            string script = $@"
select volume {d}
clean
{styleCmd}
create partition primary
format fs=ntfs quick label=""REX_BOOT""
{activeCmd}
assign letter={d}
exit";

            string file = Path.GetTempFileName();
            File.WriteAllText(file, script);
            HardwareHelper.RunProcess("diskpart.exe", $"/s \"{file}\"");
            File.Delete(file);
        }

        private void ApplyWin11Hack(string targetDrive)
        {
            // Erstellt die autounattend.xml um TPM/CPU Checks zu umgehen
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
            File.WriteAllText(Path.Combine(targetDrive, "autounattend.xml"), xml);
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