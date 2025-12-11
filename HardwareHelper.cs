using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rex
{
    public static class HardwareHelper
    {
        // Konstanten für den nativen Windows-Kernel-Zugriff
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;

        // Führt Kommandozeilen-Tools (Diskpart, etc.) unsichtbar im Hintergrund aus
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

        // Versucht, die physische Disk-Nummer (z.B. 2 für Disk 2) anhand des Laufwerksbuchstabens zu finden.
        // Nutzt PowerShell, da WMI bei USB-Sticks manchmal unzuverlässig ist.
        public static int GetDiskNumber(string driveLetter)
        {
            string letterOnly = driveLetter.Substring(0, 1);
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "powershell.exe";
                    // Wir pipen Partition -> Disk -> Number, um die ID sauber zu bekommen
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

            return -1; // Fehlerfall
        }

        public static string GetPhysicalPath(string driveLetter)
        {
            int num = GetDiskNumber(driveLetter);
            if (num != -1) return $@"\\.\PhysicalDrive{num}";
            return null;
        }

        public static string GetPhysicalPathByNumber(int diskNum)
        {
            return $@"\\.\PhysicalDrive{diskNum}";
        }

        // Import für CreateFile (Kernel32), um direkten Sektorenzugriff zu erhalten (nötig für Backup/DD)
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}