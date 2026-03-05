using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rex
{
    public static class HardwareHelper
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint OPEN_EXISTING = 3;

        /// <summary>
        /// Führt ein CLI Tool im Hintergrund aus.
        /// </summary>
        public static string RunProcess(string filename, string args, bool throwOnError = true)
        {
            using (var p = new Process())
            {
                p.StartInfo.FileName = filename;
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.Start();

                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (throwOnError && p.ExitCode != 0)
                {
                    throw new Exception($"Prozess '{filename}' schlug mit Code {p.ExitCode} fehl.\n{error}");
                }
                
                return output;
            }
        }

        /// <summary>
        /// Versucht, die physische Disk-Nummer anhand des Laufwerksbuchstabens zu finden.
        /// Nutzt PowerShell für zuverlässigste Erkennung bei USB Medien.
        /// </summary>
        public static int GetDiskNumber(string driveLetter)
        {
            string letterOnly = driveLetter.Substring(0, 1);
            try
            {
                // PowerShell Command: Liest saubere Integer-Werte der Disk-ID (-NoProfile -NonInteractive beschleunigt den Aufruf signifikant)
                string args = $"-NoProfile -NonInteractive -Command \"(Get-Partition -DriveLetter {letterOnly} | Get-Disk).Number\"";
                string output = RunProcess("powershell.exe", args, false).Trim();

                if (int.TryParse(output, out int num)) 
                {
                    return num;
                }
            }
            catch (Exception)
            {
                // Ignoriere Exceptions hier, falle auf -1 zurück
            }
            return -1;
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName, 
            uint dwDesiredAccess, 
            uint dwShareMode, 
            IntPtr lpSecurityAttributes, 
            uint dwCreationDisposition, 
            uint dwFlagsAndAttributes, 
            IntPtr hTemplateFile);
    }
}