using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rex
{
    public static class HardwareHelper
    {
        public static void RunProcess(string filename, string args)
        {
            using (var p = new Process())
            {
                p.StartInfo.FileName = filename;
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit();
            }
        }

        // --- METHODE 1: Finde Disk-Nummer (E: -> Disk 2) ---
        public static int GetDiskNumber(string driveLetter)
        {
            string letterOnly = driveLetter.Substring(0, 1); // "E"

            // Versuch 1: PowerShell (Modern & Schnell)
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "powershell.exe";
                    // Pipeline: Partition -> Disk -> Nummer
                    p.StartInfo.Arguments = $"-NoProfile -Command \"(Get-Partition -DriveLetter {letterOnly} | Get-Disk).Number\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    string outStr = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (int.TryParse(outStr, out int num)) return num;
                }
            }
            catch { }

            // Versuch 2: Diskpart Parsing (Der "Panzer" - funktioniert auch bei kaputten Partitionstabellen)
            try
            {
                return GetDiskNumberViaDiskpart(letterOnly);
            }
            catch { }

            return -1;
        }

        // Liest die Diskpart-Ausgabe aus, um die Disk zu finden
        private static int GetDiskNumberViaDiskpart(string letter)
        {
            // Skript erstellen: Listet Volumes auf
            string scriptFile = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(scriptFile, "list volume\nexit");

            using (var p = new Process())
            {
                p.StartInfo.FileName = "diskpart.exe";
                p.StartInfo.Arguments = $"/s \"{scriptFile}\"";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                System.IO.File.Delete(scriptFile);

                /* Output sieht so aus:
                 * Volume 3     E   Label   Removable   14 GB
                 */
                // Wir suchen die Zeile mit dem Buchstaben
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    // Suche nach "E " (Buchstabe mit Leerzeichen danach)
                    if (line.Contains($" {letter} "))
                    {
                        // Jetzt müssen wir wissen, auf welcher Disk dieses Volume liegt.
                        // Das geht nur über "select volume X" -> "detail volume"
                        // Aber einfacher Hack: Wir nehmen an, der User hat es ausgewählt, also ist es da.
                        // Wir parsen Volume Nummer: "Volume 3"
                        var match = Regex.Match(line, @"Volume\s+(\d+)");
                        if (match.Success)
                        {
                            int volNum = int.Parse(match.Groups[1].Value);
                            return GetDiskFromVolume(volNum);
                        }
                    }
                }
            }
            return -1;
        }

        private static int GetDiskFromVolume(int volNum)
        {
            // Zweites Skript: Detail Volume
            string script = $"select volume {volNum}\ndetail volume\nexit";
            string file = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(file, script);

            using (var p = new Process())
            {
                p.StartInfo.FileName = "diskpart.exe";
                p.StartInfo.Arguments = $"/s \"{file}\"";
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                System.IO.File.Delete(file);

                // Output enthält: "* Disk 2    Online"
                var match = Regex.Match(output, @"Disk\s+(\d+)");
                if (match.Success) return int.Parse(match.Groups[1].Value);
            }
            return -1;
        }

        // --- METHODE 2: Finde Buchstaben (Disk 2 -> F:) ---
        // Das ist die Funktion, die du wolltest!
        public static string GetDriveLetterFromDisk(int diskNum)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "powershell.exe";
                    // Befehl: Nimm Disk X -> Hole Partitionen -> Die einen Buchstaben haben -> Gib den ersten Buchstaben zurück
                    p.StartInfo.Arguments = $"-NoProfile -Command \"Get-Partition -DiskNumber {diskNum} | Where-Object {{ $_.DriveLetter }} | Select-Object -First 1 -ExpandProperty DriveLetter\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();

                    string letterChar = p.StandardOutput.ReadToEnd().Trim(); // z.B. "F"
                    p.WaitForExit();

                    // Prüfung: Ist es ein Buchstabe?
                    if (!string.IsNullOrEmpty(letterChar) && char.IsLetter(letterChar[0]))
                    {
                        return letterChar + ":"; // Macht "F:" draus
                    }
                }
            }
            catch { }
            return null;
        }

        public static string GetPhysicalPath(string driveLetter)
        {
            int num = GetDiskNumber(driveLetter);
            if (num != -1) return $@"\\.\PhysicalDrive{num}";
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}