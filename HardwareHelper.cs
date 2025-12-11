using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rex
{
    public static class HardwareHelper
    {
        // Führt externe Programme aus (Diskpart, Robocopy, PowerShell)
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

        // Ermittelt den physischen Pfad (\\.\PhysicalDriveX) aus einem Buchstaben (E:)
        public static string GetPhysicalPath(string driveLetter)
        {
            try
            {
                string clean = driveLetter.Replace("\\", "");
                // WMI Magie um von E: -> Partition -> Disk zu kommen
                var searcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{clean}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                foreach (ManagementObject part in searcher.Get())
                {
                    var searcher2 = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                    foreach (ManagementObject drive in searcher2.Get()) return drive["DeviceID"]?.ToString();
                }
            }
            catch { }
            return null;
        }

        // Native Windows API für direkten Schreibzugriff (Bypass Filesystem)
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