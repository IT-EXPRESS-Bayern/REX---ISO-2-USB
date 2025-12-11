using System.ComponentModel;
using System.Diagnostics;
using System.Management; // Benötigt NuGet: System.Management
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

// --- FIXES FÜR NAMENSKONFLIKTE (.NET 10) ---
// Verhindert Fehler zwischen System.Label und Windows.Forms.Label etc.
using Application = System.Windows.Forms.Application;
using Font = System.Drawing.Font;
using Label = System.Windows.Forms.Label;
// -------------------------------------------

namespace Rex;

// --- HAUPTPROGRAMM ---
static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --- AUTOMATISCHER ADMIN-NEUSTART ---
        if (!IsAdministrator())
        {
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName == null) exeName = Application.ExecutablePath;

            var startInfo = new ProcessStartInfo(exeName)
            {
                UseShellExecute = true,
                Verb = "runas" // Fordert Admin-Rechte an
            };

            try
            {
                Process.Start(startInfo);
            }
            catch
            {
                MessageBox.Show("Rex benötigt Administrator-Rechte für Hardware-Zugriff.", "Abbruch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return; // Aktuelle Instanz beenden
        }
        // ------------------------------------

        Application.Run(new RexForm());
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

// --- DIE GUI (FORM) ---
public class RexForm : Form
{
    // Controls
    private readonly TextBox _txtIso;
    private readonly ComboBox _cmbDrives;
    private readonly RadioButton _rbWindows;
    private readonly RadioButton _rbDD;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblStatus;
    private readonly Button _btnStart;

    public RexForm()
    {
        Text = "Rex - ISO Tool (Admin Mode)";
        Size = new Size(520, 460);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // --- UI AUFBAU ---

        // 1. ISO Auswahl
        var grpIso = new GroupBox { Text = "1. ISO Image wählen", Location = new Point(10, 10), Size = new Size(480, 60) };
        _txtIso = new TextBox { Location = new Point(10, 25), Size = new Size(370, 23), ReadOnly = true };
        var btnBrowse = new Button { Text = "Suchen", Location = new Point(390, 23), Size = new Size(80, 25) };

        btnBrowse.Click += (_, _) => {
            using var ofd = new OpenFileDialog { Filter = "ISO Files|*.iso|All Files|*.*" };
            if (ofd.ShowDialog() == DialogResult.OK) _txtIso.Text = ofd.FileName;
        };

        grpIso.Controls.AddRange(new Control[] { _txtIso, btnBrowse });

        // 2. Drive Auswahl
        var grpDrive = new GroupBox { Text = "2. USB-Stick wählen", Location = new Point(10, 80), Size = new Size(480, 60) };
        _cmbDrives = new ComboBox { Location = new Point(10, 25), Size = new Size(370, 23), DropDownStyle = ComboBoxStyle.DropDownList };
        var btnRefresh = new Button { Text = "Refresh", Location = new Point(390, 23), Size = new Size(80, 25) };

        btnRefresh.Click += (_, _) => RefreshDrives();

        grpDrive.Controls.AddRange(new Control[] { _cmbDrives, btnRefresh });

        // 3. Modus
        var grpMode = new GroupBox { Text = "3. Modus", Location = new Point(10, 150), Size = new Size(480, 80) };
        _rbWindows = new RadioButton { Text = "Windows Installer (Format NTFS + File Copy)", Location = new Point(20, 25), Size = new Size(400, 20), Checked = true };
        _rbDD = new RadioButton { Text = "DD Image Mode (Raw Write für Linux/Hybrid)", Location = new Point(20, 50), Size = new Size(400, 20) };

        grpMode.Controls.AddRange(new Control[] { _rbWindows, _rbDD });

        // Status & Start
        _lblStatus = new Label { Text = "Bereit.", Location = new Point(15, 240), Size = new Size(480, 20) };
        _progressBar = new ProgressBar { Location = new Point(15, 265), Size = new Size(475, 30) };
        _btnStart = new Button { Text = "BRENNEN STARTEN", Location = new Point(15, 310), Size = new Size(475, 50), Font = new Font("Segoe UI", 12, FontStyle.Bold), BackColor = Color.LightSteelBlue };

        _btnStart.Click += BtnStart_Click;

        Controls.AddRange(new Control[] { grpIso, grpDrive, grpMode, _lblStatus, _progressBar, _btnStart });

        RefreshDrives();
    }

    private void RefreshDrives()
    {
        _cmbDrives.Items.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    long sizeGb = drive.TotalSize / 1024 / 1024 / 1024;
                    _cmbDrives.Items.Add($"{drive.Name} ({sizeGb} GB) - {drive.VolumeLabel}");
                }
            }
        }
        catch { }

        if (_cmbDrives.Items.Count > 0) _cmbDrives.SelectedIndex = 0;
        else _cmbDrives.Items.Add("Keine USB-Laufwerke gefunden");
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_txtIso.Text) || _cmbDrives.SelectedItem is null || _cmbDrives.SelectedItem.ToString()!.Contains("Keine"))
        {
            MessageBox.Show("Bitte ISO und Laufwerk wählen.");
            return;
        }

        string driveSelection = _cmbDrives.SelectedItem.ToString()!;
        string driveLetter = driveSelection.Substring(0, 2); // Nimmt "E:"
        string isoPath = _txtIso.Text;

        if (MessageBox.Show($"ACHTUNG: Alle Daten auf {driveLetter} werden unwiderruflich gelöscht!\n\nSicher?", "Rex Sicherheitswarnung", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _btnStart.Enabled = false;

        try
        {
            if (_rbDD.Checked)
            {
                await Task.Run(() => RunDDMode(isoPath, driveLetter));
            }
            else
            {
                await Task.Run(() => RunWindowsMode(isoPath, driveLetter));
            }

            MessageBox.Show("Vorgang erfolgreich abgeschlossen!", "Rex Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ein Fehler ist aufgetreten:\n{ex.Message}", "Rex Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateStatus("Bereit.");
            UpdateProgress(0);
            _btnStart.Enabled = true;
            RefreshDrives();
        }
    }

    // --- LOGIK: Windows Mode (MIT TIMING FIX) ---
    private void RunWindowsMode(string isoPath, string targetDriveLetter)
    {
        UpdateStatus("Bereinige Mount-Status...");
        // Cleanup vorab, falls ISO noch gemountet war
        RunProcess("powershell.exe", $"Dismount-DiskImage -ImagePath \"{isoPath}\"", ignoreExitCode: true);
        Thread.Sleep(500);

        // 1. Formatieren
        UpdateStatus("Formatiere Laufwerk (NTFS)...");
        string scriptFile = Path.GetTempFileName();
        string driveChar = targetDriveLetter.Substring(0, 1);
        string script = $@"
select volume {driveChar}
format fs=ntfs quick label=""REX_WIN""
active
assign letter={driveChar}
exit";
        File.WriteAllText(scriptFile, script);
        RunProcess("diskpart.exe", $"/s \"{scriptFile}\"");
        File.Delete(scriptFile);

        // 2. Mounten
        UpdateStatus("Mounte ISO Image...");
        RunProcess("powershell.exe", $"Mount-DiskImage -ImagePath \"{isoPath}\"");

        // WICHTIG: Warten bis Windows den Buchstaben vergibt!
        UpdateStatus("Warte auf Windows...");
        Thread.Sleep(3000); // 3 Sekunden Wartezeit

        // Gemountetes Laufwerk suchen
        string? isoDrive = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            // Wir suchen ein CDROM Laufwerk, das Dateien enthält
            if (d.DriveType == DriveType.CDRom && d.IsReady)
            {
                try
                {
                    if (d.RootDirectory.GetFiles().Length > 0) isoDrive = d.Name;
                }
                catch { }
            }
        }

        if (isoDrive is null) throw new Exception("ISO wurde gemountet, aber Windows hat keinen Laufwerksbuchstaben vergeben (Timeout).");

        // 3. Kopieren
        UpdateStatus($"Kopiere von {isoDrive}...");
        RunProcess("robocopy.exe", $"\"{isoDrive.TrimEnd('\\')}\" \"{targetDriveLetter}\" /E /J /NFL /NDL", ignoreExitCode: true);

        // 4. Bootsektor
        UpdateStatus("Schreibe Bootsektor...");
        string bootsectPath = Path.Combine(isoDrive, "boot", "bootsect.exe");
        if (File.Exists(bootsectPath))
        {
            RunProcess(bootsectPath, $"/nt60 {targetDriveLetter.Substring(0, 2)}");
        }

        // 5. Cleanup
        UpdateStatus("Räume auf...");
        RunProcess("powershell.exe", $"Dismount-DiskImage -ImagePath \"{isoPath}\"");
    }

    // --- LOGIK: DD Mode (Raw Write) ---
    private void RunDDMode(string isoPath, string driveLetter)
    {
        UpdateStatus("Ermittle Hardware-ID...");
        string? physicalPath = GetPhysicalPath(driveLetter); // z.B. \\.\PhysicalDrive2

        if (physicalPath is null) throw new Exception("Konnte physisches Laufwerk nicht finden.");

        UpdateStatus("Sperre Laufwerk...");
        RunProcess("powershell.exe", $"Dismount-DiskImage -DevicePath {physicalPath} -ErrorAction SilentlyContinue", ignoreExitCode: true);

        UpdateStatus("Schreibe Raw Image...");
        using var fsIso = new FileStream(isoPath, FileMode.Open, FileAccess.Read);

        // Native Zugriff
        using var driveHandle = CreateFile(physicalPath, 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (driveHandle.IsInvalid) throw new Exception("Zugriff verweigert (Laufwerk in Nutzung).");

        using var fsDrive = new FileStream(driveHandle, FileAccess.Write);

        byte[] buffer = new byte[1024 * 1024]; // 1 MB
        long totalBytes = fsIso.Length;
        long totalRead = 0;
        int read;

        while ((read = fsIso.Read(buffer, 0, buffer.Length)) > 0)
        {
            fsDrive.Write(buffer, 0, read);
            totalRead += read;
            UpdateProgress((int)((totalRead * 100) / totalBytes));
        }

        fsDrive.Flush();
    }

    // --- HELPER ---

    private void UpdateStatus(string text) => Invoke(() => _lblStatus.Text = text);

    private void UpdateProgress(int value) => Invoke(() => _progressBar.Value = value);

    private void RunProcess(string filename, string args, bool ignoreExitCode = false)
    {
        using var p = new Process();
        p.StartInfo.FileName = filename;
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.Start();
        p.WaitForExit();

        if (!ignoreExitCode && p.ExitCode != 0) Debug.WriteLine($"Error: {filename} ExitCode {p.ExitCode}");
    }

    private string? GetPhysicalPath(string driveLetter)
    {
        try
        {
            string cleanLetter = driveLetter.Replace("\\", "");
            var searcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{cleanLetter}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject partition in searcher.Get())
            {
                var searcher2 = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (ManagementObject drive in searcher2.Get())
                {
                    return drive["DeviceID"]?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}