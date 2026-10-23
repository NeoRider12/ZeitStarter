// ZeitStarter – launches a program (or shortcut) on selected weekdays at a set time, optionally waking the PC.
// Build on Windows without extra tools (see build.bat) or with Mono: mcs -target:winexe ...
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("ZeitStarter")]
[assembly: System.Reflection.AssemblyProduct("ZeitStarter")]
[assembly: System.Reflection.AssemblyDescription("Launches programs on a schedule, even from sleep/hibernate")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace ZeitStarter
{
    // ---------- small on/off toggle switch ----------
    class ToggleSwitch : Control
    {
        bool _checked;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(56, 28);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Height - 2, w = Width - 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(1, 1, h, h, 90, 180);
                path.AddArc(w - h + 1, 1, h, h, 270, 180);
                path.CloseFigure();
                using (var b = new SolidBrush(_checked ? Color.FromArgb(46, 160, 67) : Color.FromArgb(170, 170, 170)))
                    g.FillPath(b, path);
            }
            int d = h - 6;
            int x = _checked ? w - d - 3 : 4;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, x, 4, d, d);
        }
    }

    // ---------- main window ----------
    class MainForm : Form
    {
        readonly string settingsFile;
        readonly string legacySettingsFile;
        string targetPath = "";
        DateTime nextRun = DateTime.MaxValue;

        Panel dropZone;
        PictureBox iconBox;
        Label dropLabel;
        DateTimePicker timePicker;
        ToggleSwitch toggle;
        Label toggleLabel;
        Label statusLabel;
        CheckBox autostartBox;
        CheckBox wakeBox;
        readonly CheckBox[] dayBoxes = new CheckBox[7];
        IntPtr wakeTimer = IntPtr.Zero;
        string wakeError = null;
        DateTime lastLaunch = DateTime.MinValue;
        Process launchedProc;
        string launchedExeName;
        bool targetRunning;
        DateTime lastRunCheck = DateTime.MinValue;
        ComboBox stayAwakeBox;
        // minutes to keep the PC awake after launch; 0 = while the program is running
        static readonly int[] StayAwakeMinutes = { 0, 15, 30, 60, 120, 240, 480 };
        static readonly string[] StayAwakeLabels = { "while program runs", "15 minutes", "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours" };
        bool keepingAwake;

        // ----- Windows API for wake timers -----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimer(IntPtr attr, bool manualReset, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr hTimer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelWaitableTimer(IntPtr hTimer);
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_AWAYMODE_REQUIRED = 0x00000040;
        NotifyIcon tray;
        System.Windows.Forms.Timer ticker;
        bool loading;
        bool reallyExit;

        public MainForm()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZeitStarter");
            Directory.CreateDirectory(dir);
            settingsFile = Path.Combine(dir, "settings.ini");
            legacySettingsFile = Path.Combine(dir, "einstellungen.ini");

            Text = "ZeitStarter";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 398);
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.FromArgb(248, 248, 250);
            Icon = MakeIcon();

            // Drop zone
            dropZone = new Panel
            {
                Location = new Point(16, 16), Size = new Size(348, 110),
                AllowDrop = true, BackColor = Color.White, Cursor = Cursors.Hand
            };
            dropZone.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(150, 150, 160), 1.5f) { DashStyle = DashStyle.Dash })
                    e.Graphics.DrawRectangle(pen, 1, 1, dropZone.Width - 3, dropZone.Height - 3);
            };
            iconBox = new PictureBox { Size = new Size(32, 32), Location = new Point(158, 14), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            dropLabel = new Label
            {
                Location = new Point(8, 50), Size = new Size(332, 54), TextAlign = ContentAlignment.TopCenter,
                ForeColor = Color.FromArgb(90, 90, 100), BackColor = Color.Transparent
            };
            dropZone.Controls.Add(iconBox);
            dropZone.Controls.Add(dropLabel);
            foreach (Control c in new Control[] { dropZone, iconBox, dropLabel })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
                c.Click += (s, e) => BrowseFile();
            }
            Controls.Add(dropZone);

            // Start time
            Controls.Add(new Label { Text = "Start time:", Location = new Point(16, 146), AutoSize = true });
            timePicker = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
                Location = new Point(100, 142), Width = 80, Font = new Font("Segoe UI", 11f)
            };
            timePicker.ValueChanged += (s, e) => { if (!loading) { Reschedule(); Save(); } };
            Controls.Add(timePicker);
            // Weekdays
            string[] dayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            for (int i = 0; i < 7; i++)
            {
                dayBoxes[i] = new CheckBox
                {
                    Text = dayNames[i], Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat, Size = new Size(46, 30), Location = new Point(16 + i * 50, 180),
                    Checked = true, Cursor = Cursors.Hand, BackColor = Color.White
                };
                dayBoxes[i].FlatAppearance.CheckedBackColor = Color.FromArgb(46, 160, 67);
                dayBoxes[i].FlatAppearance.BorderColor = Color.FromArgb(180, 180, 190);
                var box = dayBoxes[i];
                box.CheckedChanged += (s, e) =>
                {
                    box.ForeColor = box.Checked ? Color.White : Color.FromArgb(90, 90, 100);
                    if (!loading) { Reschedule(); Save(); }
                };
                Controls.Add(box);
            }

            // On/off switch
            toggle = new ToggleSwitch { Location = new Point(16, 226) };
            toggle.CheckedChanged += (s, e) => { UpdateToggleLabel(); if (!loading) { Reschedule(); Save(); } };
            Controls.Add(toggle);
            toggleLabel = new Label { Location = new Point(80, 230), AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            toggleLabel.Click += (s, e) => toggle.Checked = !toggle.Checked;
            Controls.Add(toggleLabel);

            // Status
            statusLabel = new Label { Location = new Point(16, 266), Size = new Size(348, 22), ForeColor = Color.FromArgb(60, 60, 70) };
            Controls.Add(statusLabel);

            // Wake option
            wakeBox = new CheckBox { Text = "Wake PC to launch (from sleep / hibernate)", Location = new Point(16, 298), AutoSize = true };
            wakeBox.CheckedChanged += (s, e) => { if (!loading) { ArmWakeTimer(); Save(); } };
            Controls.Add(wakeBox);

            // Stay awake after launch
            Controls.Add(new Label { Text = "Keep PC awake after launch:", Location = new Point(16, 331), AutoSize = true });
            stayAwakeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(200, 327), Width = 164 };
            stayAwakeBox.Items.AddRange(StayAwakeLabels);
            stayAwakeBox.SelectedIndex = 0;
            stayAwakeBox.SelectedIndexChanged += (s, e) => { if (!loading) Save(); };
            Controls.Add(stayAwakeBox);

            // Autostart
            autostartBox = new CheckBox { Text = "Start ZeitStarter with Windows", Location = new Point(16, 362), AutoSize = true };
            autostartBox.CheckedChanged += (s, e) => { if (!loading) SetAutostart(autostartBox.Checked); };
            Controls.Add(autostartBox);

            // Tray
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowFromTray());
            menu.Items.Add("Launch now (test)", null, (s, e) => LaunchTarget(false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { reallyExit = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "ZeitStarter", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowFromTray();

            ticker = new System.Windows.Forms.Timer { Interval = 1000 };
            ticker.Tick += OnTick;

            LoadSettings();
            ticker.Start();
            OnTick(null, null);
        }

        // ----- drag & drop -----
        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) SetTarget(files[0]);
        }

        void BrowseFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Select a program or shortcut",
                Filter = "Programs & shortcuts (*.exe;*.lnk;*.bat;*.cmd;*.url)|*.exe;*.lnk;*.bat;*.cmd;*.url|All files (*.*)|*.*",
                DereferenceLinks = false
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) SetTarget(dlg.FileName);
            }
        }

        void SetTarget(string path)
        {
            targetPath = path;
            UpdateTargetView();
            Save();
        }

        void UpdateTargetView()
        {
            if (string.IsNullOrEmpty(targetPath))
            {
                iconBox.Image = null;
                dropLabel.Text = "\nDrop a program or shortcut here\n(or click to browse)";
                return;
            }
            try { iconBox.Image = Icon.ExtractAssociatedIcon(targetPath).ToBitmap(); } catch { iconBox.Image = null; }
            string name = Path.GetFileNameWithoutExtension(targetPath);
            dropLabel.Text = name + "\n" + targetPath + (File.Exists(targetPath) ? "" : "\n⚠ File not found");
        }

        void UpdateToggleLabel()
        {
            toggleLabel.Text = toggle.Checked ? "ON" : "OFF";
            toggleLabel.ForeColor = toggle.Checked ? Color.FromArgb(46, 140, 60) : Color.Gray;
        }

        // ----- scheduling -----
        void Reschedule()
        {
            if (!toggle.Checked) { nextRun = DateTime.MaxValue; return; }
            DateTime now = DateTime.Now;
            nextRun = DateTime.MaxValue;
            for (int i = 0; i <= 7; i++)
            {
                DateTime t = now.Date.AddDays(i).AddHours(timePicker.Value.Hour).AddMinutes(timePicker.Value.Minute);
                if (t > now && DayOn(t.DayOfWeek)) { nextRun = t; break; }
            }
            ArmWakeTimer();
        }

        bool DayOn(DayOfWeek d) { return dayBoxes[((int)d + 6) % 7].Checked; }

        // Sets a Windows wake timer 2 minutes before launch time (resuming from hibernate takes a moment)
        void ArmWakeTimer()
        {
            wakeError = null;
            if (wakeTimer == IntPtr.Zero)
                wakeTimer = CreateWaitableTimer(IntPtr.Zero, true, "ZeitStarter_WakeTimer");
            if (wakeTimer == IntPtr.Zero) { wakeError = "Could not create wake timer."; return; }

            CancelWaitableTimer(wakeTimer);
            if (wakeBox == null || !wakeBox.Checked || !toggle.Checked || nextRun == DateTime.MaxValue) return;

            DateTime wakeAt = nextRun.AddMinutes(-2);
            if (wakeAt <= DateTime.Now.AddSeconds(5)) return; // too close – PC is awake anyway
            long due = wakeAt.ToUniversalTime().ToFileTimeUtc();
            if (!SetWaitableTimer(wakeTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, true) || Marshal.GetLastWin32Error() == 50)
                wakeError = "This PC does not support wake timers.";
        }

        // Keeps Windows from going back to sleep: shortly before launch (after a timer wake)
        // and after launch – for the chosen duration or while the launched program is running.
        void UpdateKeepAwake(DateTime now)
        {
            bool beforeLaunch = toggle.Checked && wakeBox.Checked &&
                                nextRun != DateTime.MaxValue && nextRun - now < TimeSpan.FromMinutes(3);

            bool afterLaunch = false;
            if (lastLaunch != DateTime.MinValue)
            {
                TimeSpan since = now - lastLaunch;
                int minutes = StayAwakeMinutes[Math.Max(0, stayAwakeBox.SelectedIndex)];
                if (since < TimeSpan.FromMinutes(5)) afterLaunch = true;          // always give it a few minutes to start
                else if (minutes > 0) afterLaunch = since < TimeSpan.FromMinutes(minutes);
                else
                {
                    if (now - lastRunCheck > TimeSpan.FromSeconds(5)) { targetRunning = IsTargetRunning(); lastRunCheck = now; }
                    afterLaunch = targetRunning;
                }
                if (!afterLaunch) lastLaunch = DateTime.MinValue;               // done – stop checking
            }

            bool need = beforeLaunch || afterLaunch;
            if (need == keepingAwake) return;
            keepingAwake = need;
            SetThreadExecutionState(need ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED) : ES_CONTINUOUS);
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            if (toggle.Checked && now >= nextRun)
            {
                // Only launch if we are less than 1 hour late (e.g. after sleep)
                if (now - nextRun < TimeSpan.FromHours(1)) LaunchTarget(true);
                Reschedule();
            }

            if (!toggle.Checked)
                statusLabel.Text = "Off – nothing will be launched.";
            else if (string.IsNullOrEmpty(targetPath))
                statusLabel.Text = "Drop a program in first.";
            else if (nextRun == DateTime.MaxValue)
                statusLabel.Text = "No weekday selected.";
            else
            {
                TimeSpan left = nextRun - now;
                string tag = nextRun.Date == now.Date ? "today" : nextRun.Date == now.Date.AddDays(1) ? "tomorrow"
                           : nextRun.ToString("ddd MMM d", System.Globalization.CultureInfo.InvariantCulture);
                statusLabel.Text = string.Format("Next launch: {0} {1:HH:mm}  (in {2}:{3:00}:{4:00})",
                    tag, nextRun, (int)left.TotalHours, left.Minutes, left.Seconds);
                if (wakeError != null && wakeBox.Checked) statusLabel.Text = "⚠ " + wakeError;
            }
            UpdateKeepAwake(now);
            tray.Text = (toggle.Checked ? "ZeitStarter – ON" : "ZeitStarter – OFF") + (keepingAwake ? " (keeping PC awake)" : "");
        }

        void LaunchTarget(bool scheduled)
        {
            if (string.IsNullOrEmpty(targetPath)) return;
            try
            {
                var psi = new ProcessStartInfo(targetPath) { UseShellExecute = true };
                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                launchedProc = Process.Start(psi);
                launchedExeName = ResolveExeName(targetPath);
                lastLaunch = DateTime.Now;
                targetRunning = true;
                if (scheduled)
                    tray.ShowBalloonTip(3000, "ZeitStarter", Path.GetFileNameWithoutExtension(targetPath) + " was launched.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "ZeitStarter – Error", ex.Message, ToolTipIcon.Error);
            }
        }

        bool IsTargetRunning()
        {
            try { if (launchedProc != null && !launchedProc.HasExited) return true; } catch { }
            if (!string.IsNullOrEmpty(launchedExeName))
            {
                try { return Process.GetProcessesByName(launchedExeName).Length > 0; } catch { }
            }
            return false;
        }

        // Returns the exe name (without extension) behind an .exe or .lnk, so launchers that
        // hand off to a new process are still tracked. Null if it can't be determined.
        static string ResolveExeName(string path)
        {
            try
            {
                string p = path;
                if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    object shell = Activator.CreateInstance(t);
                    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { p });
                    p = (string)lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null);
                }
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(p);
            }
            catch { }
            return null;
        }

        // ----- settings -----
        void LoadSettings()
        {
            loading = true;
            int hh = 8, mm = 0; bool on = false, wake = false; string days = "1111111"; int stay = 0;
            string file = File.Exists(settingsFile) ? settingsFile : legacySettingsFile;
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    // German keys are read too, for settings from older versions
                    if (k == "Path" || k == "Pfad") targetPath = v;
                    else if (k == "Hour" || k == "Stunde") int.TryParse(v, out hh);
                    else if (k == "Minute") int.TryParse(v, out mm);
                    else if (k == "Enabled" || k == "Aktiv") on = v == "1";
                    else if (k == "Wake" || k == "Wecken") wake = v == "1";
                    else if (k == "StayAwake") int.TryParse(v, out stay);
                    else if ((k == "Days" || k == "Tage") && v.Length == 7) days = v;
                }
            }
            timePicker.Value = DateTime.Today.AddHours(Math.Min(Math.Max(hh, 0), 23)).AddMinutes(Math.Min(Math.Max(mm, 0), 59));
            toggle.Checked = on;
            wakeBox.Checked = wake;
            int si = Array.IndexOf(StayAwakeMinutes, stay);
            stayAwakeBox.SelectedIndex = si < 0 ? 0 : si;
            for (int i = 0; i < 7; i++) { dayBoxes[i].Checked = days[i] == '1'; dayBoxes[i].ForeColor = dayBoxes[i].Checked ? Color.White : Color.FromArgb(90, 90, 100); }
            autostartBox.Checked = IsAutostart();
            UpdateToggleLabel();
            UpdateTargetView();
            Reschedule();
            loading = false;
        }

        string DaysString()
        {
            var c = new char[7];
            for (int i = 0; i < 7; i++) c[i] = dayBoxes[i].Checked ? '1' : '0';
            return new string(c);
        }

        void Save()
        {
            try
            {
                File.WriteAllLines(settingsFile, new[]
                {
                    "Path=" + targetPath,
                    "Hour=" + timePicker.Value.Hour,
                    "Minute=" + timePicker.Value.Minute,
                    "Enabled=" + (toggle.Checked ? "1" : "0"),
                    "Wake=" + (wakeBox.Checked ? "1" : "0"),
                    "StayAwake=" + StayAwakeMinutes[Math.Max(0, stayAwakeBox.SelectedIndex)],
                    "Days=" + DaysString()
                });
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool IsAutostart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ZeitStarter") != null; }
            catch { return false; }
        }

        void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("ZeitStarter", "\"" + Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue("ZeitStarter", false);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Autostart"); }
        }

        // ----- window / tray -----
        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The X button only hides to the tray so the schedule keeps running
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(2000, "ZeitStarter is still running", "Right-click the clock icon → Exit.", ToolTipIcon.Info);
                return;
            }
            tray.Visible = false;
            // Wake timer ends with the process; release keep-awake
            SetThreadExecutionState(ES_CONTINUOUS);
            base.OnFormClosing(e);
        }

        public bool StartHidden;
        protected override void SetVisibleCore(bool value)
        {
            if (StartHidden && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(46, 160, 67))) g.FillEllipse(b, 1, 1, 30, 30);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 5, 5, 22, 22);
                using (var p = new Pen(Color.FromArgb(40, 40, 50), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, 16, 16, 16, 9);
                    g.DrawLine(p, 16, 16, 21, 19);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "ZeitStarter_SingleInstance_Mutex", out created))
            {
                if (!created)
                {
                    MessageBox.Show("ZeitStarter is already running (clock icon in the taskbar notification area).", "ZeitStarter");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm();
                form.StartHidden = Array.IndexOf(args, "/tray") >= 0;
                Application.Run(form);
            }
        }
    }
}
