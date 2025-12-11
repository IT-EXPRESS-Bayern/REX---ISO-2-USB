using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private Label _lblTimer;

        // Visualizer Panel
        private VisualizerPanel _visualizer;

        // Timer
        private Timer _timerProcess;
        private DateTime _startTime;

        // Farben
        private Color _bgDark = Color.FromArgb(18, 18, 18);
        private Color _bgPanel = Color.FromArgb(30, 30, 30);
        private Color _accent = Color.FromArgb(0, 120, 215);
        private Color _text = Color.Gainsboro;
        private Color _green = Color.LimeGreen;

        public MainForm()
        {
            SetupResponsiveUI();
            RefreshDrives();
            Log("REX System v2.0 initialisiert.");
        }

        private void SetupResponsiveUI()
        {
            Text = "REX // ULTIMATE ISO TOOL";
            Size = new Size(1100, 650);
            MinimumSize = new Size(900, 500);
            BackColor = _bgDark;
            ForeColor = _text;
            DoubleBuffered = true; // Verhindert Flackern

            // --- HAUPT LAYOUT (Tabelle) ---
            // 3 Spalten: Log (30%) | Settings (40%) | Visualizer (30%)
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = _bgDark,
                Padding = new Padding(5)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            Controls.Add(layout);

            // --- SPALTE 1: LOG ---
            var pnlLog = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };
            var lblLogHeader = new Label { Text = "SYSTEM LOG", Dock = DockStyle.Top, Font = new Font("Consolas", 10, FontStyle.Bold), ForeColor = Color.Gray, Height = 25 };
            _logBox = new ListBox { Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = _green, Font = new Font("Consolas", 8), BorderStyle = BorderStyle.None, IntegralHeight = false };
            pnlLog.Controls.Add(_logBox);
            pnlLog.Controls.Add(lblLogHeader);
            layout.Controls.Add(pnlLog, 0, 0);

            // --- SPALTE 2: SETTINGS (Scrollbar) ---
            var pnlSettings = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };

            // Elemente für Settings
            pnlSettings.Controls.Add(CreateHeader("1. QUELLE", 0));
            var pnlIso = CreatePanel(25);
            _txtIso = CreateBox(); _txtIso.Width = 250; _txtIso.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var btnIso = CreateBtn("...", 260, 0, 40, (s, e) => SelectIso());
            btnIso.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pnlIso.Controls.AddRange(new Control[] { _txtIso, btnIso });
            pnlSettings.Controls.Add(pnlIso);

            pnlSettings.Controls.Add(CreateHeader("2. ZIEL", 80));
            var pnlDrv = CreatePanel(105);
            _cmbDrives = new ComboBox { Location = new Point(0, 0), Height = 25, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = _bgPanel, ForeColor = _text, FlatStyle = FlatStyle.Flat };
            _cmbDrives.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var btnRef = CreateBtn("🔄", 260, 0, 40, (s, e) => RefreshDrives());
            btnRef.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pnlDrv.Controls.AddRange(new Control[] { _cmbDrives, btnRef });
            pnlSettings.Controls.Add(pnlDrv);

            pnlSettings.Controls.Add(CreateHeader("3. MODUS & FEATURES", 160));
            var pnlOpt = CreatePanel(185, 200); // Höheres Panel
            _rbWin = CreateRadio("Windows / UEFI Setup", 0, 0, true);
            _rbBackup = CreateRadio("Backup (.img)", 0, 25, false);

            var div = new Label { Text = "_________________________", Location = new Point(0, 45), AutoSize = true, ForeColor = Color.Gray };

            _chkGpt = CreateCheck("GPT Schema (Empfohlen)", 0, 70, true);
            _chkWin11 = CreateCheck("Ultimate Win11 Bypass", 0, 95, true);
            _chkDriver = CreateCheck("Treiber Injektion", 0, 120, false);

            _txtDriver = CreateBox(); _txtDriver.Location = new Point(20, 145); _txtDriver.Width = 200; _txtDriver.Visible = false;
            _chkDriver.CheckedChanged += (s, e) => SelectDriver();

            pnlOpt.Controls.AddRange(new Control[] { _rbWin, _rbBackup, div, _chkGpt, _chkWin11, _chkDriver, _txtDriver });
            pnlSettings.Controls.Add(pnlOpt);

            // Start Button & Progress
            _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 10, Style = ProgressBarStyle.Continuous };
            _btnStart = new Button { Text = "STARTEN", Dock = DockStyle.Bottom, Height = 50, BackColor = _accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold), Cursor = Cursors.Hand };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += StartProcess;

            // Reihenfolge umdrehen wegen Dock Bottom
            pnlSettings.Controls.Add(_btnStart);
            pnlSettings.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10 }); // Spacer
            pnlSettings.Controls.Add(_progress);

            layout.Controls.Add(pnlSettings, 1, 0);

            // --- SPALTE 3: VISUALIZER ---
            var pnlVis = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            // Timer Oben Rechts
            _lblTimer = new Label { Text = "00:00:00", Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", 14, FontStyle.Bold), ForeColor = _green, Height = 30 };

            // Custom Control
            _visualizer = new VisualizerPanel { Dock = DockStyle.Fill };

            pnlVis.Controls.Add(_visualizer);
            pnlVis.Controls.Add(_lblTimer);
            layout.Controls.Add(pnlVis, 2, 0);

            // --- TIMER LOGIC ---
            _timerProcess = new Timer { Interval = 1000 };
            _timerProcess.Tick += (s, e) => {
                var span = DateTime.Now - _startTime;
                _lblTimer.Text = span.ToString(@"hh\:mm\:ss");
            };
        }

        private void SelectIso()
        {
            using var o = new OpenFileDialog { Filter = "Images|*.iso;*.img" };
            if (o.ShowDialog() == DialogResult.OK)
            {
                _txtIso.Text = o.FileName;
                try
                {
                    long size = new FileInfo(o.FileName).Length;
                    _visualizer.SetIsoInfo(Path.GetFileName(o.FileName), $"{size / 1024 / 1024} MB");
                }
                catch { }
            }
        }

        private void SelectDriver()
        {
            _txtDriver.Visible = _chkDriver.Checked;
            if (_chkDriver.Checked)
            {
                using var f = new FolderBrowserDialog();
                if (f.ShowDialog() == DialogResult.OK) _txtDriver.Text = f.SelectedPath; else _chkDriver.Checked = false;
            }
        }

        private async void StartProcess(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_txtIso.Text) || _cmbDrives.SelectedItem == null) { Log("[ERR] Daten fehlen."); return; }
            string driveStr = _cmbDrives.SelectedItem.ToString();
            string drive = driveStr.Substring(0, 2);
            string iso = _txtIso.Text;

            // Visualizer Reset
            _visualizer.ResetStatus();
            _visualizer.SetUsbInfo(drive, driveStr.Split('(')[1].Split(')')[0]); // Hacky size parse

            if (_rbBackup.Checked)
            {
                using var sfd = new SaveFileDialog { Filter = "Disk Image|*.img", FileName = "rex_backup.img" };
                if (sfd.ShowDialog() != DialogResult.OK) return;
                RunTask(eng => eng.CreateBackup(drive, sfd.FileName));
            }
            else
            {
                if (MessageBox.Show($"WARNUNG: {drive} wird gelöscht!", "ACHTUNG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                RunTask(eng => eng.RunExtractMode(iso, drive, _chkGpt.Checked, _chkWin11.Checked, _chkDriver.Checked ? _txtDriver.Text : null));
            }
        }

        private async void RunTask(Action<RexEngine> action)
        {
            _btnStart.Enabled = false; _btnStart.BackColor = Color.Gray;
            _visualizer.IsAnimating = true;
            _startTime = DateTime.Now; _timerProcess.Start();

            var engine = new RexEngine(Log, p => Invoke(new Action(() => _progress.Value = p)));

            try
            {
                await Task.Run(() => action(engine));
                Log("[SUCCESS] Fertig!");
                _visualizer.SetSuccess();
                MessageBox.Show("Vorgang erfolgreich!", "REX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"[CRITICAL] {ex.Message}");
                _visualizer.IsAnimating = false;
                MessageBox.Show(ex.Message, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _timerProcess.Stop();
                _btnStart.Enabled = true; _btnStart.BackColor = _accent; RefreshDrives();
                _visualizer.IsAnimating = false; // Stop animation but keep checkmark if success
            }
        }

        private void Log(string msg)
        {
            if (!IsHandleCreated) return;
            Invoke(new Action(() =>
            {
                _logBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                _logBox.TopIndex = _logBox.Items.Count - 1;
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

        // --- UI HELPERS ---
        private Label CreateHeader(string t, int y) => new Label { Text = t, Location = new Point(0, y), AutoSize = true, ForeColor = _accent, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        private Panel CreatePanel(int y, int h = 40) => new Panel { Location = new Point(0, y + 20), Size = new Size(300, h), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        private TextBox CreateBox() => new TextBox { Location = new Point(0, 0), Height = 25, BackColor = _bgPanel, ForeColor = _text, BorderStyle = BorderStyle.FixedSingle };
        private Button CreateBtn(string t, int x, int y, int w, EventHandler c) { var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(w, 25), BackColor = _bgPanel, ForeColor = _text, FlatStyle = FlatStyle.Flat }; b.FlatAppearance.BorderSize = 0; b.Click += c; return b; }
        private RadioButton CreateRadio(string t, int x, int y, bool c) => new RadioButton { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = _text, Checked = c };
        private CheckBox CreateCheck(string t, int x, int y, bool c) => new CheckBox { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = _text, Checked = c };
    }

    // --- CUSTOM CONTROL: VISUALIZER (Rechtes Fenster) ---
    public class VisualizerPanel : Control
    {
        private Timer _animTimer;
        private int _arrowOffset = 0;
        private string _isoName = "Keine ISO";
        private string _isoSize = "-";
        private string _usbName = "-";
        private string _usbSize = "-";
        private bool _isSuccess = false;

        public bool IsAnimating
        {
            get => _animTimer.Enabled;
            set { if (value) { _isSuccess = false; _animTimer.Start(); } else _animTimer.Stop(); Invalidate(); }
        }

        public VisualizerPanel()
        {
            DoubleBuffered = true;
            _animTimer = new Timer { Interval = 50 };
            _animTimer.Tick += (s, e) => {
                _arrowOffset = (_arrowOffset + 2) % 20; // Animation Speed
                Invalidate();
            };
        }

        public void SetIsoInfo(string name, string size) { _isoName = name; _isoSize = size; Invalidate(); }
        public void SetUsbInfo(string name, string size) { _usbName = name; _usbSize = size; Invalidate(); }
        public void ResetStatus() { _isSuccess = false; Invalidate(); }
        public void SetSuccess() { _isSuccess = true; IsAnimating = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cx = Width / 2;
            int cy = Height / 2;

            // Farben
            using var brushIso = new SolidBrush(Color.FromArgb(50, 50, 50));
            using var penAccent = new Pen(Color.DodgerBlue, 2);
            using var penArrow = new Pen(Color.LimeGreen, 4) { EndCap = LineCap.ArrowAnchor };
            using var textBrush = new SolidBrush(Color.Gainsboro);
            using var fontBig = new Font("Segoe UI", 12, FontStyle.Bold);
            using var fontSmall = new Font("Consolas", 9);

            // 1. ISO CIRCLE (Oben)
            int isoY = 50;
            g.FillEllipse(brushIso, cx - 40, isoY, 80, 80);
            g.DrawEllipse(penAccent, cx - 40, isoY, 80, 80);
            g.DrawString("ISO", fontBig, textBrush, cx - 15, isoY + 30);

            // Text Oben
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_isoName, fontSmall, textBrush, cx, isoY - 20, sf);
            g.DrawString(_isoSize, fontSmall, textBrush, cx, isoY + 85, sf);

            // 2. USB RECT (Unten)
            int usbY = Height - 130;
            g.FillRectangle(brushIso, cx - 30, usbY, 60, 100);
            g.DrawRectangle(penAccent, cx - 30, usbY, 60, 100);
            g.DrawString("USB", fontBig, textBrush, cx - 18, usbY + 40);

            // Text Unten
            g.DrawString(_usbName, fontSmall, textBrush, cx, usbY + 105, sf);
            g.DrawString(_usbSize, fontSmall, textBrush, cx, usbY + 120, sf);

            // 3. PFEIL / STATUS (Mitte)
            int arrowStart = isoY + 95;
            int arrowEnd = usbY - 15;

            if (_isSuccess)
            {
                // Grüner Haken
                using var penCheck = new Pen(Color.LimeGreen, 6);
                g.DrawLine(penCheck, cx - 20, cy, cx - 5, cy + 15);
                g.DrawLine(penCheck, cx - 5, cy + 15, cx + 25, cy - 15);
            }
            else
            {
                // Pfeil Linie
                using var penGray = new Pen(Color.Gray, 2);
                g.DrawLine(penGray, cx, arrowStart, cx, arrowEnd);

                if (IsAnimating)
                {
                    // Animierter Drop
                    int y = arrowStart + (int)((arrowEnd - arrowStart) * (_arrowOffset / 20.0f));
                    // Zeichne mehrere Pfeilspitzen für Flow-Effekt
                    g.DrawLine(penArrow, cx, y - 10, cx, y);
                }
            }
        }
    }
}