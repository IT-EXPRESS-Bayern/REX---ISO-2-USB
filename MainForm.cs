using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rex
{
    public class MainForm : Form
    {
        // Controls
        private TextBox _txtIso;
        private ComboBox _cmbDrives;
        private RadioButton _rbStandard, _rbDD;
        private RadioButton _rbMBR, _rbGPT; // NEU: Partitionstabelle
        private CheckBox _chkWin11;         // NEU: Win11 Hack
        private ProgressBar _progress;
        private Label _lblStatus;
        private Button _btnStart;

        // Farben für Dark Mode
        private Color _darkBg = Color.FromArgb(32, 32, 32);
        private Color _lightText = Color.WhiteSmoke;
        private Color _accent = Color.DodgerBlue;
        private Color _controlBg = Color.FromArgb(50, 50, 50);

        public MainForm()
        {
            SetupUI();
            RefreshDrives();
        }

        private void SetupUI()
        {
            // Fenster Setup
            Text = "Rex - Professional ISO Tool";
            Size = new Size(600, 550);
            BackColor = _darkBg;
            ForeColor = _lightText;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // 1. ISO Bereich
            var grpIso = CreateGroup("1. Image Auswahl", 10);
            _txtIso = CreateTextBox(10, 25, 450);
            var btnBrowse = CreateButton("...", 470, 23, 80);
            btnBrowse.Click += (s, e) => {
                using var ofd = new OpenFileDialog { Filter = "ISO|*.iso;*.img" };
                if (ofd.ShowDialog() == DialogResult.OK) _txtIso.Text = ofd.FileName;
            };
            grpIso.Controls.Add(_txtIso);
            grpIso.Controls.Add(btnBrowse);
            Controls.Add(grpIso);

            // 2. Laufwerk
            var grpDrive = CreateGroup("2. Ziel-Laufwerk", 80);
            _cmbDrives = new ComboBox { Location = new Point(10, 25), Size = new Size(450, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = _controlBg, ForeColor = _lightText, FlatStyle = FlatStyle.Flat };
            var btnRef = CreateButton("🔄", 470, 23, 80);
            btnRef.Click += (s, e) => RefreshDrives();
            grpDrive.Controls.Add(_cmbDrives);
            grpDrive.Controls.Add(btnRef);
            Controls.Add(grpDrive);

            // 3. Modus & Optionen (Hier sind die neuen Features!)
            var grpOpt = CreateGroup("3. Optionen & Partitionsschema", 150);
            grpOpt.Height = 130;

            _rbStandard = CreateRadio("Standard Installation (Windows / UEFI)", 20, 25, true);
            _rbDD = CreateRadio("DD Raw Image (Linux / Raspberry Pi)", 20, 50, false);

            // Trennlinie simulieren
            var line = new Panel { Location = new Point(10, 80), Size = new Size(540, 1), BackColor = Color.Gray };

            // Sub-Optionen für Standard Modus
            var lblPart = new Label { Text = "Schema:", Location = new Point(20, 90), AutoSize = true, ForeColor = Color.Gray };
            _rbGPT = CreateRadio("GPT (UEFI - Modern)", 80, 88, true);
            _rbMBR = CreateRadio("MBR (BIOS - Alt)", 250, 88, false);

            _chkWin11 = new CheckBox { Text = "Windows 11 Limits entfernen (TPM/CPU Bypass)", Location = new Point(20, 110), AutoSize = true, ForeColor = Color.Gold };

            // Logik: Optionen nur aktiv wenn nicht DD Mode
            EventHandler modeChanged = (s, e) => {
                bool std = _rbStandard.Checked;
                _rbGPT.Enabled = _rbMBR.Enabled = _chkWin11.Enabled = std;
            };
            _rbStandard.CheckedChanged += modeChanged;

            grpOpt.Controls.AddRange(new Control[] { _rbStandard, _rbDD, line, lblPart, _rbGPT, _rbMBR, _chkWin11 });
            Controls.Add(grpOpt);

            // 4. Status
            _lblStatus = new Label { Text = "Bereit.", Location = new Point(15, 300), Size = new Size(550, 20), ForeColor = Color.Gray };
            _progress = new ProgressBar { Location = new Point(15, 325), Size = new Size(550, 30) }; // Farbe geht nur via Custom Paint schwer, lassen wir Standard

            _btnStart = new Button { Text = "STARTEN", Location = new Point(15, 370), Size = new Size(550, 50), BackColor = _accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += BtnStart_Click;

            Controls.AddRange(new Control[] { _lblStatus, _progress, _btnStart });
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_txtIso.Text) || _cmbDrives.SelectedIndex < 0) return;

            string iso = _txtIso.Text;
            string drive = _cmbDrives.SelectedItem.ToString().Substring(0, 2);

            if (MessageBox.Show($"Laufwerk {drive} wird komplett gelöscht!\nFortfahren?", "WARNUNG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            // UI Sperren
            _btnStart.Enabled = false;

            // Engine initialisieren und Callbacks verbinden
            var engine = new RexEngine(
                status => Invoke(new Action(() => _lblStatus.Text = status)),
                pct => Invoke(new Action(() => _progress.Value = pct))
            );

            try
            {
                await Task.Run(() => {
                    if (_rbDD.Checked)
                    {
                        engine.RunDDMode(iso, drive);
                    }
                    else
                    {
                        // Hier übergeben wir die neuen Optionen (GPT?, Win11?)
                        engine.RunExtractMode(iso, drive, _rbGPT.Checked, _chkWin11.Checked);
                    }
                });
                MessageBox.Show("Erfolgreich!", "Rex", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _lblStatus.Text = "Bereit.";
                _progress.Value = 0;
                _btnStart.Enabled = true;
                RefreshDrives();
            }
        }

        private void RefreshDrives()
        {
            _cmbDrives.Items.Clear();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Removable && d.IsReady)
                {
                    long gb = d.TotalSize / 1024 / 1024 / 1024;
                    _cmbDrives.Items.Add($"{d.Name} ({gb} GB) - {d.VolumeLabel}");
                }
            }
            if (_cmbDrives.Items.Count > 0) _cmbDrives.SelectedIndex = 0;
        }

        // --- UI Helper für Dark Mode ---
        private GroupBox CreateGroup(string text, int y)
        {
            return new GroupBox { Text = text, Location = new Point(10, y), Size = new Size(560, 60), ForeColor = _lightText };
        }
        private TextBox CreateTextBox(int x, int y, int w)
        {
            return new TextBox { Location = new Point(x, y), Size = new Size(w, 23), BackColor = _controlBg, ForeColor = _lightText, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
        }
        private Button CreateButton(string text, int x, int y, int w)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 25), BackColor = _controlBg, ForeColor = _lightText, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = Color.Gray;
            return b;
        }
        private RadioButton CreateRadio(string text, int x, int y, bool check)
        {
            return new RadioButton { Text = text, Location = new Point(x, y), AutoSize = true, Checked = check, ForeColor = _lightText };
        }
    }
}