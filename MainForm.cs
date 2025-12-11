using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rex
{
    public class MainForm : Form
    {
        // UI Komponenten
        private ListBox _logBox;
        private ProgressBar _progress;
        private TextBox _txtIso, _txtDriver;
        private ComboBox _cmbDrives;
        private RadioButton _rbWin, _rbBackup;
        private CheckBox _chkWin11, _chkGpt, _chkDriver;
        private Button _btnStart;

        // Modernes Farbschema (Dark Mode)
        private Color _bgDark = Color.FromArgb(20, 20, 20);
        private Color _bgPanel = Color.FromArgb(35, 35, 35);
        private Color _accent = Color.FromArgb(0, 120, 215); // Windows Blau
        private Color _text = Color.Gainsboro;
        private Color _green = Color.LimeGreen;

        public MainForm()
        {
            SetupModernUI();
            RefreshDrives();
            Log("REX System bereit. Warte auf Benutzereingabe...");
        }

        private void SetupModernUI()
        {
            Text = "REX // ULTIMATE ISO TOOL";
            Size = new Size(900, 550);
            BackColor = _bgDark;
            ForeColor = _text;

            // Layout teilen: Links Log, Rechts Einstellungen
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = _bgDark, SplitterWidth = 5 };
            Controls.Add(split);

            // --- LINKER BEREICH: LOG ---
            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var lblLog = new Label { Text = "LIVE DEBUG LOG", Dock = DockStyle.Top, Font = new Font("Consolas", 10, FontStyle.Bold), ForeColor = Color.Gray, Height = 30 };
            _logBox = new ListBox { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = _green, Font = new Font("Consolas", 9), BorderStyle = BorderStyle.None };
            logPanel.Controls.Add(_logBox);
            logPanel.Controls.Add(lblLog);
            split.Panel1.Controls.Add(logPanel);

            // --- RECHTER BEREICH: STEUERUNG ---
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), AutoScroll = true };

            // Sektion 1: Quelle
            rightPanel.Controls.Add(CreateHeader("1. QUELLE (ISO / IMAGE)", 0));
            var pnlIso = CreatePanel(30);
            _txtIso = CreateBox();
            var btnIso = CreateBtn("...", 350, 0, 40, (s, e) => {
                using var o = new OpenFileDialog { Filter = "Images|*.iso;*.img" };
                if (o.ShowDialog() == DialogResult.OK) _txtIso.Text = o.FileName;
            });
            pnlIso.Controls.AddRange(new Control[] { _txtIso, btnIso });
            rightPanel.Controls.Add(pnlIso);

            // Sektion 2: Ziel
            rightPanel.Controls.Add(CreateHeader("2. ZIEL (USB STICK)", 80));
            var pnlDrv = CreatePanel(110);
            _cmbDrives = new ComboBox { Location = new Point(0, 0), Size = new Size(340, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = _bgPanel, ForeColor = _text, FlatStyle = FlatStyle.Flat };
            var btnRef = CreateBtn("🔄", 350, 0, 40, (s, e) => RefreshDrives());
            pnlDrv.Controls.AddRange(new Control[] { _cmbDrives, btnRef });
            rightPanel.Controls.Add(pnlDrv);

            // Sektion 3: Arbeitsmodus
            rightPanel.Controls.Add(CreateHeader("3. MODUS", 160));
            var pnlMode = CreatePanel(190, 60);
            _rbWin = CreateRadio("Windows / UEFI Installation", 0, 0, true);
            _rbBackup = CreateRadio("Backup erstellen (Stick -> .img)", 0, 25, false);
            pnlMode.Controls.AddRange(new Control[] { _rbWin, _rbBackup });
            rightPanel.Controls.Add(pnlMode);

            // Sektion 4: Erweiterte Features
            rightPanel.Controls.Add(CreateHeader("4. PRO FEATURES", 260));
            var pnlOpt = CreatePanel(290, 100);
            _chkGpt = CreateCheck("GPT Partitionsschema (empfohlen)", 0, 0, true);
            _chkWin11 = CreateCheck("Ultimate Bypass (Win11/TPM/Account/OOBE)", 0, 25, true);
            _chkDriver = CreateCheck("Treiber Injektion ($WinPEDriver$)", 0, 50, false);

            _txtDriver = CreateBox(); _txtDriver.Location = new Point(25, 75); _txtDriver.Width = 325; _txtDriver.Visible = false;

            // Logik für Treiber-Auswahl-Dialog
            _chkDriver.CheckedChanged += (s, e) => {
                _txtDriver.Visible = _chkDriver.Checked;
                if (_chkDriver.Checked)
                {
                    using var f = new FolderBrowserDialog();
                    if (f.ShowDialog() == DialogResult.OK) _txtDriver.Text = f.SelectedPath; else _chkDriver.Checked = false;
                }
            };

            pnlOpt.Controls.AddRange(new Control[] { _chkGpt, _chkWin11, _chkDriver, _txtDriver });
            rightPanel.Controls.Add(pnlOpt);

            // Start & Progress
            _progress = new ProgressBar { Location = new Point(20, 410), Size = new Size(400, 10), Style = ProgressBarStyle.Continuous };
            _btnStart = new Button { Text = "PROZESS STARTEN", Location = new Point(20, 430), Size = new Size(400, 50), BackColor = _accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold), Cursor = Cursors.Hand };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += StartProcess;

            rightPanel.Controls.Add(_progress);
            rightPanel.Controls.Add(_btnStart);
            split.Panel2.Controls.Add(rightPanel);
        }

        private async void StartProcess(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_txtIso.Text) || _cmbDrives.SelectedItem == null)
            {
                Log("[ERROR] Bitte erst ISO und Ziel-Laufwerk auswählen.");
                return;
            }

            string drive = _cmbDrives.SelectedItem.ToString().Substring(0, 2);
            string iso = _txtIso.Text;

            if (_rbBackup.Checked)
            {
                // Backup-Pfad abfragen
                using var sfd = new SaveFileDialog { Filter = "Disk Image|*.img", FileName = "rex_backup.img" };
                if (sfd.ShowDialog() != DialogResult.OK) return;

                RunTask(eng => eng.CreateBackup(drive, sfd.FileName));
            }
            else
            {
                // Sicherheitsabfrage vor Formatierung
                if (MessageBox.Show($"WARNUNG: Laufwerk {drive} wird unwiderruflich gelöscht!", "Datenverlust", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                RunTask(eng => eng.RunExtractMode(iso, drive, _chkGpt.Checked, _chkWin11.Checked, _chkDriver.Checked ? _txtDriver.Text : null));
            }
        }

        // Kapselt den Async-Task und UI-Updates
        private async void RunTask(Action<RexEngine> action)
        {
            _btnStart.Enabled = false; _btnStart.BackColor = Color.Gray;

            // Engine Instanz mit UI-Callbacks
            var engine = new RexEngine(Log, p => Invoke(new Action(() => _progress.Value = p)));

            try
            {
                await Task.Run(() => action(engine));
                Log("[SUCCESS] Vorgang erfolgreich beendet!");
                MessageBox.Show("Operation erfolgreich!", "REX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
                MessageBox.Show(ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnStart.Enabled = true; _btnStart.BackColor = _accent; RefreshDrives();
            }
        }

        private void Log(string msg)
        {
            Invoke(new Action(() => {
                _logBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                _logBox.TopIndex = _logBox.Items.Count - 1; // Auto-Scroll
            }));
        }

        private void RefreshDrives()
        {
            _cmbDrives.Items.Clear();
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Removable && d.IsReady)
                    _cmbDrives.Items.Add($"{d.Name} ({d.TotalSize / 1024 / 1024 / 1024} GB) - {d.VolumeLabel}");
            if (_cmbDrives.Items.Count > 0) _cmbDrives.SelectedIndex = 0;
        }

        // Helper-Methoden für sauberen UI-Code
        private Label CreateHeader(string t, int y) => new Label { Text = t, Location = new Point(20, y), AutoSize = true, ForeColor = _accent, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        private Panel CreatePanel(int y, int h = 40) => new Panel { Location = new Point(20, y + 20), Size = new Size(400, h) };
        private TextBox CreateBox() => new TextBox { Location = new Point(0, 0), Size = new Size(340, 25), BackColor = _bgPanel, ForeColor = _text, BorderStyle = BorderStyle.FixedSingle };
        private Button CreateBtn(string t, int x, int y, int w, EventHandler c) { var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(w, 25), BackColor = _bgPanel, ForeColor = _text, FlatStyle = FlatStyle.Flat }; b.FlatAppearance.BorderSize = 0; b.Click += c; return b; }
        private RadioButton CreateRadio(string t, int x, int y, bool c) => new RadioButton { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = _text, Checked = c };
        private CheckBox CreateCheck(string t, int x, int y, bool c) => new CheckBox { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = _text, Checked = c };
    }
}