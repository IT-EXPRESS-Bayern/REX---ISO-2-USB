using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

// WICHTIG: Timer Fix
using Timer = System.Windows.Forms.Timer;

namespace Rex
{
    public class MainForm : Form
    {
        // UI Komponenten
        private ListBox _logBox;
        private ProgressBar _progress;

        // --- HIER WAR DER FEHLER: JETZT ModernTextBox STATT TextBox ---
        private ModernTextBox _txtIso, _txtDriver;
        // -------------------------------------------------------------

        private ComboBox _cmbDrives;
        private RadioButton _rbWin, _rbBackup;
        private CheckBox _chkWin11, _chkGpt, _chkDriver;
        private Button _btnStart;
        private Label _lblTimer;

        private VisualizerPanel _visualizer;
        private Timer _timerProcess;
        private DateTime _startTime;

        // Theme Farben
        public static Color ColBackground = Color.FromArgb(18, 18, 18);
        public static Color ColPanel = Color.FromArgb(30, 30, 30);
        public static Color ColAccent = Color.FromArgb(0, 122, 204);
        public static Color ColText = Color.FromArgb(220, 220, 220);
        public static Color ColSuccess = Color.FromArgb(40, 167, 69);
        public static Color ColWarning = Color.FromArgb(255, 193, 7);

        public MainForm()
        {
            SetupProfessionalUI();
            RefreshDrives();
            Log("REX System v3.0 [Professional UI] bereit.");
        }

        private void SetupProfessionalUI()
        {
            Text = "REX // ULTIMATE ISO TOOL";
            Size = new Size(1200, 700);
            MinimumSize = new Size(1000, 600);
            BackColor = ColBackground;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            DoubleBuffered = true;

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(10),
                BackColor = ColBackground
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            Controls.Add(mainLayout);

            // --- SPALTE 1: LOG ---
            var groupLog = new ModernGroupBox { Text = " LIVE TERMINAL ", Dock = DockStyle.Fill };
            _logBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 10, 10),
                ForeColor = ColSuccess,
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            groupLog.ContentPanel.Controls.Add(_logBox);
            mainLayout.Controls.Add(groupLog, 0, 0);

            // --- SPALTE 2: MITTE ---
            var centerPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var centerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(0, 0, 10, 0)
            };
            centerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // Sektion 1: Quelle
            var groupIso = new ModernGroupBox { Text = " 1. QUELLE ", Height = 80, Dock = DockStyle.Top };
            var pnlIsoInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            pnlIsoInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 85F));
            pnlIsoInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));

            _txtIso = new ModernTextBox { ReadOnly = true, PlaceholderText = "Keine ISO ausgewählt..." };
            var btnIso = new ModernButton { Text = "...", BackColor = ColPanel };
            btnIso.Click += (s, e) => SelectIso();

            pnlIsoInner.Controls.Add(_txtIso, 0, 0);
            pnlIsoInner.Controls.Add(btnIso, 1, 0);
            groupIso.ContentPanel.Controls.Add(pnlIsoInner);
            centerLayout.Controls.Add(groupIso);

            // Sektion 2: Ziel
            var groupDest = new ModernGroupBox { Text = " 2. ZIEL-LAUFWERK ", Height = 80, Dock = DockStyle.Top };
            var pnlDestInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            pnlDestInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 85F));
            pnlDestInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));

            _cmbDrives = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = ColPanel,
                ForeColor = ColText,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f)
            };
            var btnRef = new ModernButton { Text = "↺", BackColor = ColPanel };
            btnRef.Click += (s, e) => RefreshDrives();

            pnlDestInner.Controls.Add(_cmbDrives, 0, 0);
            pnlDestInner.Controls.Add(btnRef, 1, 0);
            groupDest.ContentPanel.Controls.Add(pnlDestInner);
            centerLayout.Controls.Add(groupDest);

            // Sektion 3: Optionen
            var groupOpt = new ModernGroupBox { Text = " 3. KONFIGURATION ", AutoSize = true, Dock = DockStyle.Top };
            var flowOpt = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

            _rbWin = new RadioButton { Text = "Windows Installation / UEFI Setup", AutoSize = true, Checked = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = ColAccent };
            _rbBackup = new RadioButton { Text = "Backup Modus (USB -> .img)", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = ColWarning };

            flowOpt.Controls.Add(_rbWin);
            flowOpt.Controls.Add(CreateSpacer());

            _chkGpt = new CheckBox { Text = "Partitionsschema: GPT (Empfohlen für UEFI)", AutoSize = true, Checked = true };
            _chkWin11 = new CheckBox { Text = "Ultimate Bypass (Win11 TPM/SecureBoot/User)", AutoSize = true, Checked = true, ForeColor = ColSuccess };
            _chkDriver = new CheckBox { Text = "Treiber-Injektion ($WinPEDriver$)", AutoSize = true };

            _txtDriver = new ModernTextBox { Visible = false, PlaceholderText = "Pfad zum Treiber-Ordner..." };
            _chkDriver.CheckedChanged += (s, e) => SelectDriver();

            flowOpt.Controls.AddRange(new Control[] { _chkGpt, _chkWin11, _chkDriver, _txtDriver, _rbBackup });
            groupOpt.ContentPanel.Controls.Add(flowOpt);
            centerLayout.Controls.Add(groupOpt);

            // Sektion 4: Action
            var pnlAction = new Panel { Height = 100, Dock = DockStyle.Top, Padding = new Padding(0, 20, 0, 0) };
            _btnStart = new ModernButton
            {
                Text = "STARTEN",
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = ColAccent,
                Font = new Font("Segoe UI", 12, FontStyle.Bold)
            };
            _btnStart.Click += StartProcess;

            _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 10, Style = ProgressBarStyle.Continuous };

            pnlAction.Controls.Add(_btnStart);
            pnlAction.Controls.Add(_progress);
            centerLayout.Controls.Add(pnlAction);

            centerPanel.Controls.Add(centerLayout);
            mainLayout.Controls.Add(centerPanel, 1, 0);

            // --- SPALTE 3: VISUALIZER ---
            var groupVis = new ModernGroupBox { Text = " STATUS ", Dock = DockStyle.Fill };
            var pnlVisContent = new Panel { Dock = DockStyle.Fill };

            _lblTimer = new Label { Text = "00:00:00", Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", 16, FontStyle.Bold), ForeColor = ColText, Height = 40 };
            _visualizer = new VisualizerPanel { Dock = DockStyle.Fill };

            pnlVisContent.Controls.Add(_visualizer);
            pnlVisContent.Controls.Add(_lblTimer);
            groupVis.ContentPanel.Controls.Add(pnlVisContent);

            mainLayout.Controls.Add(groupVis, 2, 0);

            _timerProcess = new Timer { Interval = 1000 };
            _timerProcess.Tick += (s, e) => {
                var span = DateTime.Now - _startTime;
                _lblTimer.Text = span.ToString(@"hh\:mm\:ss");
            };
        }

        private Control CreateSpacer() => new Panel { Height = 10, Width = 10 };

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

        private void RefreshDrives()
        {
            _cmbDrives.Items.Clear();
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Removable && d.IsReady)
                    _cmbDrives.Items.Add($"{d.Name} ({d.TotalSize / 1024 / 1024 / 1024} GB) - {d.VolumeLabel}");
            if (_cmbDrives.Items.Count > 0) _cmbDrives.SelectedIndex = 0;
        }

        private async void StartProcess(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_txtIso.Text) || _cmbDrives.SelectedItem == null) { Log("[ERR] Daten fehlen."); return; }
            string driveStr = _cmbDrives.SelectedItem.ToString();
            string drive = driveStr.Substring(0, 2);
            string iso = _txtIso.Text;

            _visualizer.ResetStatus();
            try { _visualizer.SetUsbInfo(drive, driveStr.Split('(')[1].Split(')')[0]); } catch { }

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
                _btnStart.Enabled = true; _btnStart.BackColor = ColAccent; RefreshDrives();
                _visualizer.IsAnimating = false;
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
    }

    // --- CUSTOM CONTROLS ---

    public class ModernGroupBox : Panel
    {
        private Label _header;
        public Panel ContentPanel;

        public ModernGroupBox()
        {
            Padding = new Padding(1);
            BackColor = Color.FromArgb(60, 60, 60);

            var inner = new Panel { Dock = DockStyle.Fill, BackColor = MainForm.ColBackground, Padding = new Padding(10) };

            _header = new Label { Dock = DockStyle.Top, Height = 25, ForeColor = MainForm.ColAccent, Font = new Font("Segoe UI", 9, FontStyle.Bold), BackColor = MainForm.ColBackground };
            ContentPanel = new Panel { Dock = DockStyle.Fill, BackColor = MainForm.ColBackground };

            inner.Controls.Add(ContentPanel);
            inner.Controls.Add(_header);
            Controls.Add(inner);
        }

        public override string Text { get => _header.Text; set => _header.Text = value; }
    }

    public class ModernButton : Button
    {
        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10);
            Height = 35;
        }
    }

    public class ModernTextBox : Panel
    {
        public TextBox InnerBox;
        public ModernTextBox()
        {
            Height = 30;
            BackColor = MainForm.ColPanel;
            Padding = new Padding(5);
            InnerBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = MainForm.ColPanel, ForeColor = MainForm.ColText, Font = new Font("Segoe UI", 10) };
            Controls.Add(InnerBox);
        }
        public override string Text { get => InnerBox.Text; set => InnerBox.Text = value; }
        public bool ReadOnly { get => InnerBox.ReadOnly; set => InnerBox.ReadOnly = value; }
        public string PlaceholderText { get => InnerBox.PlaceholderText; set => InnerBox.PlaceholderText = value; }
    }

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
            _animTimer.Tick += (s, e) => { _arrowOffset = (_arrowOffset + 2) % 20; Invalidate(); };
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
            int topY = 40;
            int bottomY = Height - 100;
            int centerY = (topY + bottomY) / 2;

            using var brushIso = new SolidBrush(Color.FromArgb(50, 50, 50));
            using var penAccent = new Pen(MainForm.ColAccent, 3);
            using var penArrow = new Pen(MainForm.ColSuccess, 5) { EndCap = LineCap.ArrowAnchor };
            using var textBrush = new SolidBrush(MainForm.ColText);
            using var fontBig = new Font("Segoe UI", 12, FontStyle.Bold);
            using var fontSmall = new Font("Consolas", 9);

            // ISO
            g.FillEllipse(brushIso, cx - 40, topY, 80, 80);
            g.DrawEllipse(penAccent, cx - 40, topY, 80, 80);
            g.DrawString("ISO", fontBig, textBrush, cx - 16, topY + 30);

            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_isoName, fontSmall, textBrush, cx, topY - 20, sf);
            g.DrawString(_isoSize, fontSmall, textBrush, cx, topY + 85, sf);

            // USB
            g.FillRectangle(brushIso, cx - 35, bottomY, 70, 90);
            g.DrawRectangle(penAccent, cx - 35, bottomY, 70, 90);
            g.DrawString("USB", fontBig, textBrush, cx - 18, bottomY + 35);

            g.DrawString(_usbName, fontSmall, textBrush, cx, bottomY + 95, sf);
            g.DrawString(_usbSize, fontSmall, textBrush, cx, bottomY + 110, sf);

            // Pfeil
            if (_isSuccess)
            {
                using var penCheck = new Pen(MainForm.ColSuccess, 8);
                g.DrawLine(penCheck, cx - 25, centerY, cx - 10, centerY + 20);
                g.DrawLine(penCheck, cx - 10, centerY + 20, cx + 35, centerY - 25);
            }
            else
            {
                using var penGray = new Pen(Color.Gray, 2);
                g.DrawLine(penGray, cx, topY + 90, cx, bottomY - 10);

                if (IsAnimating)
                {
                    int startAnim = topY + 90;
                    int endAnim = bottomY - 10;
                    int y = startAnim + (int)((endAnim - startAnim) * (_arrowOffset / 20.0f));
                    g.DrawLine(penArrow, cx, y - 15, cx, y);
                }
            }
        }
    }
}