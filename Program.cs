using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

namespace Rex
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!IsAdministrator())
            {
                // Startet sich selbst als Admin neu
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? Application.ExecutablePath;
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); } catch { }
                return;
            }

            Application.Run(new MainForm());
        }

        static bool IsAdministrator()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}