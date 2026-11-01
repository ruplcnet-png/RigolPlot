using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Rigol Datalogger")]
[assembly: System.Reflection.AssemblyVersion("1.0.2")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.2")]

namespace RigolPlot
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string path = null;
            if (args.Length > 0 && File.Exists(args[0])) path = args[0];
            Application.Run(new MainForm(path));
        }
    }

    public struct PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    public class VoltBox : NumericUpDown
    {
        bool fixing;

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == '.')
                e.KeyChar = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0];
            base.OnKeyPress(e);
        }

        protected override void ValidateEditText()
        {
            if (!fixing)
            {
                fixing = true;
                try
                {
                    string s = Text;
                    if (!string.IsNullOrEmpty(s) && s.IndexOf('.') >= 0)
                        Text = s.Replace('.', CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]);
                }
                finally { fixing = false; }
            }
            base.ValidateEditText();
        }
    }

    public class MainForm : Form
    {
        PlotControl plot;
        ToolStripStatusLabel lblView, lblMouse, lblRec;

        TextBox ipBox;
        NumericUpDown intervalBox;
        CheckBox[] chkCh;
        ComboBox measBox;
        CheckBox chkTime, chkVolt, autoCheck;
        ComboBox themeBox;
        Button btnStart;
        Button btnOpen;
        Button btnJpg;
        ToolStripMenuItem miOpen, miSaveJpg;
        NumericUpDown thrHiBox, thrLoBox;
        CheckBox chkSound;
        Label lblAlarm;
        double thrHi = 30.0, thrLo = -30.0;
        bool alarmHi, alarmLo;
        double lastValue;
        bool hasValue;
        long lastBeepTick;
        MenuStrip mainMenu;
        StatusStrip statusBar;
        Label lblStatus;

        Thread logThread;
        volatile bool running;
        DateTime startTime;
        string csvPath;
        readonly object fileLock = new object();
        const int ScpiPort = 5555;

        public MainForm(string path)
        {
            Text = "Rigol Datalogger";
            var wa = Screen.PrimaryScreen.WorkingArea;
            Width = (int)(wa.Width * 0.90);
            Height = (int)(wa.Height * 0.92);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                                 wa.Top + Math.Max(0, (wa.Height - Height) / 2));
            WindowState = FormWindowState.Maximized;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }


            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            miOpen = new ToolStripMenuItem("&Open CSV...");
            miOpen.Click += (s, e) => Open();
            miSaveJpg = new ToolStripMenuItem("Save as &JPG...");
            miSaveJpg.Click += (s, e) => ExportJpg();
            var exit = new ToolStripMenuItem("E&xit");
            exit.Click += (s, e) => Close();
            var about = new ToolStripMenuItem("О программе");
            about.Click += (s, e) => ShowAbout();
            file.DropDownItems.AddRange(new ToolStripItem[] { miOpen, miSaveJpg, new ToolStripSeparator(), about, new ToolStripSeparator(), exit });
            menu.Items.Add(file);
            MainMenuStrip = menu;
            mainMenu = menu;

            var panel = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Gainsboro };

            var rowA = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 8, 10, 2), BackColor = Color.Transparent };
            var rowB = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 2, 10, 2), BackColor = Color.Transparent };
            var rowC = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 2, 10, 6), BackColor = Color.Transparent };

            var lblIp = new Label { Text = "IP:", AutoSize = true };
            ipBox = new TextBox { Text = "192.168.89.234", Width = 130 };

            var lblInt = new Label { Text = "Интервал, мс:", AutoSize = true };
            intervalBox = new NumericUpDown { Width = 110, Minimum = 10, Maximum = 60000, Increment = 10, Value = 500 };

            var lblCh = new Label { Text = "Каналы:", AutoSize = true };
            chkCh = new CheckBox[4];
            string[] chN = { "CH1", "CH2", "CH3", "CH4" };
            for (int i = 0; i < 4; i++)
            {
                chkCh[i] = new CheckBox { Text = chN[i], AutoSize = true, Checked = (i == 0) };
                chkCh[i].Margin = new Padding(4, 6, 8, 2);
            }

            var lblMeas = new Label { Text = "Величина:", AutoSize = true };
            measBox = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            measBox.Items.AddRange(new object[] { "VAVG", "VRMS" });
            measBox.SelectedIndex = 0;

            btnStart = new Button { Text = "Пуск", Width = 110, Height = 30 };
            btnStart.Click += (s, e) => ToggleStartStop();

            btnOpen = new Button { Text = "Открыть CSV...", Width = 130, Height = 30 };
            btnOpen.Click += (s, e) => Open();

            btnJpg = new Button { Text = "Экспорт JPG", Width = 130, Height = 30 };
            btnJpg.Click += (s, e) => ExportJpg();

            var lblZ = new Label { Text = "Зум:", AutoSize = true };
            chkTime = new CheckBox { Text = "время", AutoSize = true, Checked = true };
            chkTime.CheckedChanged += (s, e) => { if (plot != null) plot.ZoomX = chkTime.Checked; };
            chkVolt = new CheckBox { Text = "напряжение", AutoSize = true, Checked = false };
            chkVolt.CheckedChanged += (s, e) => { if (plot != null) plot.ZoomY = chkVolt.Checked; };

            autoCheck = new CheckBox { Text = "Автомасштаб", AutoSize = true, Checked = true };
            autoCheck.CheckedChanged += (s, e) => { if (plot != null) plot.SetAutoScale(autoCheck.Checked); };

            var lblTheme = new Label { Text = "Экран:", AutoSize = true };
            themeBox = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
            themeBox.Items.AddRange(new object[] { "Тёмный", "Светлый" });
            themeBox.SelectedIndex = 0;
            themeBox.SelectedIndexChanged += (s, e) => { if (plot != null) ApplyTheme(themeBox.SelectedIndex == 0); };

            lblStatus = new Label { AutoSize = false, Width = Math.Max(600, wa.Width - 700), Height = 22, Text = "Готово." };

            var lblThrHi = new Label { Text = "Порог верх, В:", AutoSize = true };
            thrHiBox = new VoltBox
            {
                Width = 145,
                Minimum = -1000000M, Maximum = 1000000M,
                DecimalPlaces = 3, Increment = 0.5M, Value = 30M
            };
            thrHiBox.ValueChanged += (s, e) => thrHi = (double)thrHiBox.Value;

            var lblThrLo = new Label { Text = "Порог низ, В:", AutoSize = true };
            thrLoBox = new VoltBox
            {
                Width = 145,
                Minimum = -1000000M, Maximum = 1000000M,
                DecimalPlaces = 3, Increment = 0.5M, Value = -30M
            };
            thrLoBox.ValueChanged += (s, e) => thrLo = (double)thrLoBox.Value;

            chkSound = new CheckBox { Text = "Звук при выходе за порог", AutoSize = true, Checked = true };

            lblAlarm = new Label { AutoSize = false, Width = 380, Height = 22, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.Red, Text = "", TextAlign = ContentAlignment.MiddleLeft };

            var mLbl = new Padding(6, 8, 4, 2);
            var mIn = new Padding(0, 4, 16, 2);
            var mChk = new Padding(14, 6, 4, 2);
            foreach (var c in new Control[] { lblIp, lblInt, lblCh, lblMeas, lblZ, lblThrHi, lblThrLo }) c.Margin = mLbl;
            foreach (var c in new Control[] { ipBox, intervalBox, measBox, thrHiBox, thrLoBox }) c.Margin = mIn;
            lblTheme.Margin = new Padding(20, 6, 4, 2);
            themeBox.Margin = new Padding(0, 2, 4, 2);
            btnStart.Margin = new Padding(4, 2, 8, 2);
            btnOpen.Margin = new Padding(0, 2, 8, 2);
            btnJpg.Margin = new Padding(0, 2, 4, 2);
            chkTime.Margin = mChk; chkVolt.Margin = mChk; autoCheck.Margin = mChk; chkSound.Margin = mChk;
            lblStatus.Margin = new Padding(24, 6, 4, 2);
            lblAlarm.Margin = new Padding(18, 6, 4, 2);

            var rowAControls = new List<Control> { lblIp, ipBox, lblInt, intervalBox, lblCh };
            rowAControls.AddRange(chkCh);
            rowAControls.Add(lblMeas); rowAControls.Add(measBox);
            rowAControls.Add(btnStart); rowAControls.Add(btnOpen); rowAControls.Add(btnJpg);
            rowA.Controls.AddRange(rowAControls.ToArray());
            rowB.Controls.AddRange(new Control[] { lblZ, chkTime, chkVolt, autoCheck, lblTheme, themeBox, lblStatus });
            rowC.Controls.AddRange(new Control[] { lblThrHi, thrHiBox, lblThrLo, thrLoBox, chkSound, lblAlarm });

            panel.Controls.Add(rowC);
            panel.Controls.Add(rowB);
            panel.Controls.Add(rowA);

            plot = new PlotControl();
            plot.Dock = DockStyle.Fill;
            plot.ThresholdHi = thrHi;
            plot.ThresholdLo = thrLo;
            thrHiBox.ValueChanged += (s, e) => { plot.ThresholdHi = thrHi; plot.Invalidate(); RefreshAlarm(); };
            thrLoBox.ValueChanged += (s, e) => { plot.ThresholdLo = thrLo; plot.Invalidate(); RefreshAlarm(); };
            chkSound.CheckedChanged += (s, e) => RefreshAlarm();

            var bar = new StatusStrip();
            lblRec = new ToolStripStatusLabel("●") { ForeColor = Color.Gray, ToolTipText = "Запись не идёт" };
            lblView = new ToolStripStatusLabel("нет данных");
            lblMouse = new ToolStripStatusLabel(" ");
            bar.Items.Add(lblRec);
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(lblView);
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(lblMouse);
            statusBar = bar;

            Controls.Add(plot);
            Controls.Add(panel);
            Controls.Add(menu);
            Controls.Add(bar);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.R) plot.ResetView();
                else if (e.KeyCode == Keys.F) { autoCheck.Checked = !autoCheck.Checked; }
                else if (e.KeyCode == Keys.Space)
                { ToggleStartStop(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Home) { plot.GotoStart(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.End) { plot.GotoEnd(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Left) { plot.ScrollBy(-1); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Right) { plot.ScrollBy(1); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.PageUp) { plot.ScrollBy(-3); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.PageDown) { plot.ScrollBy(3); e.Handled = true; }
                else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
                {
                    if (e.Shift) plot.ZoomInY(); else plot.ZoomInX();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
                {
                    if (e.Shift) plot.ZoomOutY(); else plot.ZoomOutX();
                    e.Handled = true;
                }
            };

            plot.ViewChanged += UpdateStatus;
            plot.CursorMoved += (t, v) =>
                lblMouse.Text = string.Format(CultureInfo.InvariantCulture, "курсор: {0:F3} с, {1:F5} В", t, v);

            FormClosing += (s, e) => { StopLog(); SaveSettings(); };

            LoadSettings();
            ApplyTheme(themeBox.SelectedIndex == 0);
            HandleCreated += (s, e) => SetDarkTitleBar(plot.DarkTheme);
            Shown += (s, e) => plot.Focus();

            if (path != null) LoadData(path);
            else { path = FindLatestCsv(); if (path != null) LoadData(path); }
        }

        void ShowAbout()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            MessageBox.Show(this, "Rigol Datalogger\r\nv" + v.ToString(3), "О программе",
                            MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        string FindLatestCsv()
        {
            try { if (Directory.Exists(DataDir)) return Directory.GetFiles(DataDir, "*.csv").OrderByDescending(File.GetLastWriteTime).FirstOrDefault(); }
            catch { }
            return null;
        }

        static string SettingsPath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RigolPlot.ini");
            }
        }

        static string DataDir
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var inv = CultureInfo.InvariantCulture;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int p = line.IndexOf('=');
                    if (p > 0) dict[line.Substring(0, p).Trim()] = line.Substring(p + 1).Trim();
                }

                string s;
                if (dict.TryGetValue("ip", out s) && s.Length > 0) ipBox.Text = s;

                double d;
                if (dict.TryGetValue("interval_ms", out s) && double.TryParse(s, NumberStyles.Float, inv, out d))
                    intervalBox.Value = Math.Max(intervalBox.Minimum, Math.Min(intervalBox.Maximum, (decimal)d));
                if (dict.TryGetValue("thr_hi", out s) && double.TryParse(s, NumberStyles.Float, inv, out d))
                    thrHiBox.Value = Math.Max(thrHiBox.Minimum, Math.Min(thrHiBox.Maximum, (decimal)d));
                if (dict.TryGetValue("thr_lo", out s) && double.TryParse(s, NumberStyles.Float, inv, out d))
                    thrLoBox.Value = Math.Max(thrLoBox.Minimum, Math.Min(thrLoBox.Maximum, (decimal)d));

                if (dict.TryGetValue("meas", out s))
                {
                    int i = measBox.Items.IndexOf(s);
                    if (i >= 0) measBox.SelectedIndex = i;
                }
                if (dict.TryGetValue("theme", out s))
                {
                    int i;
                    if (int.TryParse(s, NumberStyles.Integer, inv, out i) && i >= 0 && i < themeBox.Items.Count)
                        themeBox.SelectedIndex = i;
                }
                if (dict.TryGetValue("sound", out s))
                {
                    int i;
                    if (int.TryParse(s, NumberStyles.Integer, inv, out i) && (i == 0 || i == 1))
                        chkSound.Checked = (i == 1);
                }
                if (dict.TryGetValue("autoscale", out s))
                {
                    int i;
                    if (int.TryParse(s, NumberStyles.Integer, inv, out i) && (i == 0 || i == 1))
                        autoCheck.Checked = (i == 1);
                }
                if (dict.TryGetValue("channels", out s))
                {
                    foreach (var c in chkCh) c.Checked = false;
                    foreach (var part in s.Split(','))
                    {
                        int c;
                        if (int.TryParse(part.Trim(), NumberStyles.Integer, inv, out c) && c >= 1 && c <= 4)
                            chkCh[c - 1].Checked = true;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var sb = new StringBuilder();
                sb.Append("ip=").AppendLine(ipBox.Text.Trim());
                sb.Append("interval_ms=").AppendLine(((int)intervalBox.Value).ToString(inv));
                sb.Append("thr_hi=").AppendLine(thrHi.ToString("R", inv));
                sb.Append("thr_lo=").AppendLine(thrLo.ToString("R", inv));
                sb.Append("meas=").AppendLine((string)measBox.SelectedItem);
                sb.Append("theme=").AppendLine(themeBox.SelectedIndex.ToString(inv));
                sb.Append("sound=").AppendLine(chkSound.Checked ? "1" : "0");
                sb.Append("autoscale=").AppendLine(autoCheck.Checked ? "1" : "0");
                sb.Append("channels=").AppendLine(string.Join(",", Array.ConvertAll(SelectedChannels(), c => c.ToString(inv))));
                File.WriteAllText(SettingsPath, sb.ToString());
            }
            catch { }
        }

        void Open()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "CSV files|*.csv|All files|*.*";
                d.InitialDirectory = DataDir;
                if (d.ShowDialog(this) == DialogResult.OK) { StopLog(); LoadData(d.FileName); }
            }
        }

        void ExportJpg()
        {
            try
            {
                try { Directory.CreateDirectory(DataDir); } catch { }

                using (var d = new SaveFileDialog())
                {
                    d.Filter = "JPEG image|*.jpg;*.jpeg";
                    d.DefaultExt = "jpg";
                    d.InitialDirectory = DataDir;
                    d.FileName = "rigol_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg";
                    if (d.ShowDialog(this) != DialogResult.OK) return;

                    int w = Math.Max(640, plot.Width);
                    int h = Math.Max(480, plot.Height);

                    using (var bmp = new Bitmap(w, h))
                    {
                        plot.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));

                        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        var encParams = new EncoderParameters(1);
                        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        if (codec != null) bmp.Save(d.FileName, codec, encParams);
                        else bmp.Save(d.FileName, ImageFormat.Jpeg);
                    }

                    lblStatus.Text = "JPG сохранён: " + d.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Ошибка экспорта JPG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void CheckThresholds(double v)
        {
            hasValue = true;
            lastValue = v;
            bool over = v > thrHi;
            bool under = v < thrLo;

            bool newHi = over && !alarmHi;
            bool newLo = under && !alarmLo;
            if (newHi) alarmHi = true;
            else if (!over) alarmHi = false;
            if (newLo) alarmLo = true;
            else if (!under) alarmLo = false;

            if (chkSound.Checked)
            {
                if (alarmHi && alarmLo) SafeBeepTriple();
                else if (alarmHi) SafeBeep(1200);
                else if (alarmLo) SafeBeep(600);
            }

            if (alarmHi && alarmLo) lblAlarm.Text = "ТРЕВОГА: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " В (оба порога)";
            else if (alarmHi) lblAlarm.Text = "ВЕРХ: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " В > " + thrHi.ToString("0.###", CultureInfo.InvariantCulture) + " В";
            else if (alarmLo) lblAlarm.Text = "НИЗ: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " В < " + thrLo.ToString("0.###", CultureInfo.InvariantCulture) + " В";
            else lblAlarm.Text = "";
        }

        void RefreshAlarm() { if (hasValue) CheckThresholds(lastValue); }

        void SafeBeep(int freq)
        {
            long now = Environment.TickCount;
            if (now - lastBeepTick < 900) return;
            lastBeepTick = now;
            BeepAsync(freq);
        }

        void SafeBeepTriple()
        {
            long now = Environment.TickCount;
            if (now - lastBeepTick < 900) return;
            lastBeepTick = now;
            BeepTripleAsync();
        }

        static void BeepAsync(int freq)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Console.Beep(freq, 300); } catch { }
            });
        }

        static void BeepTripleAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Console.Beep(1200, 120);
                        Thread.Sleep(90);
                    }
                }
                catch { }
            });
        }

        static bool IsVrmsHeader(string header)
        {
            string low = (header ?? "").Trim().ToLowerInvariant();
            return low.Contains("vrms");
        }

        static int ChannelIndexFromHeader(string header)
        {
            string low = (header ?? "").Trim().ToLowerInvariant();
            int p = low.LastIndexOf("ch", StringComparison.Ordinal);
            if (p >= 0 && p + 2 < low.Length)
            {
                char d = low[p + 2];
                if (d >= '1' && d <= '4') return d - '1';
            }
            if (low.IndexOf("voltage", StringComparison.Ordinal) >= 0) return 0;
            return -1;
        }

        static string SeriesLabel(string header, int index)
        {
            int ci = ChannelIndexFromHeader(header);
            if (ci >= 0) return "CH" + (ci + 1);
            string h = (header ?? "").Trim();
            return h.Length > 0 ? h : "CH" + (index + 1);
        }

        static int SeriesColorIndex(string header, int index)
        {
            int ci = ChannelIndexFromHeader(header);
            return ci >= 0 ? ci : index;
        }

        void LoadData(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) return;
                string delim = ","; if (lines[0].Contains(';')) delim = ";";
                var inv = CultureInfo.InvariantCulture;

                var hdr = lines[0].Split(delim.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                var sList = new List<PlotControl.Series>();
                for (int j = 1; j < hdr.Length; j++)
                {
                    sList.Add(new PlotControl.Series { Name = SeriesLabel(hdr[j], j - 1), Color = PlotControl.ChannelColor(SeriesColorIndex(hdr[j], j - 1), plot.DarkTheme) });
                }
                if (sList.Count == 0) return;

                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split(delim.ToCharArray());
                    if (p.Length < 2) continue;
                    double x;
                    if (!double.TryParse(p[0].Trim(), NumberStyles.Float, inv, out x)) continue;
                    for (int j = 0; j < sList.Count && (j + 1) < p.Length; j++)
                    {
                        double y;
                        if (double.TryParse(p[j + 1].Trim(), NumberStyles.Float, inv, out y) &&
                            !double.IsNaN(y) && Math.Abs(y) < 1e30)
                            sList[j].Pts.Add(new PointD(x, y));
                    }
                }

                var nonEmpty = sList.Where(s => s.Pts.Count >= 2).ToList();
                if (nonEmpty.Count == 0) return;
                plot.ZeroBasedY = hdr.Length > 1 && IsVrmsHeader(hdr[1]);
                plot.SetData(nonEmpty);

                var lastPts = nonEmpty.Last().Pts;
                lastValue = lastPts[lastPts.Count - 1].Y;
                hasValue = true;
                CheckThresholds(lastValue);
                Text = "Rigol Datalogger - " + Path.GetFileName(path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка загрузки", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void ToggleStartStop()
        {
            if (running) StopLog();
            else StartLog();
        }

        int[] SelectedChannels()
        {
            var list = new List<int>();
            for (int i = 0; i < 4; i++)
                if (chkCh[i].Checked) list.Add(i + 1);
            return list.ToArray();
        }

        void StartLog()
        {
            IPAddress addr;
            if (!IPAddress.TryParse(ipBox.Text.Trim(), out addr))
            { MessageBox.Show(this, "Некорректный адрес IP.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            int interval = (int)intervalBox.Value;
            string param = ((string)measBox.SelectedItem).Trim().ToUpperInvariant();

            int[] chans = SelectedChannels();
            if (chans.Length == 0)
            { MessageBox.Show(this, "Выберите хотя бы один канал.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            try { Directory.CreateDirectory(DataDir); }
            catch { }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string chTag = string.Join("_", Array.ConvertAll(chans, c => "ch" + c));
            csvPath = Path.Combine(DataDir, "rigol_" + chTag + "_" + param.ToLowerInvariant() + "_" + stamp + ".csv");

            var hdr = new StringBuilder("time_s");
            foreach (int c in chans) hdr.Append(';').Append("voltage_").Append(param.ToLowerInvariant()).Append("_ch").Append(c);
            try { File.WriteAllText(csvPath, hdr.ToString() + "\r\n"); }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось создать файл: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            plot.ZeroBasedY = (param == "VRMS") && chans.Length > 0;
            plot.ClearData();
            foreach (int c in chans) plot.AddSeries("CH" + c, c - 1);
            plot.SetAutoScale(autoCheck.Checked);
            startTime = DateTime.UtcNow;
            running = true;
            btnStart.Text = "Стоп";
            SetRecordingUi(true);
            SetRecIndicator(true);
            string chTxt = string.Join(", ", Array.ConvertAll(chans, c => "CH" + c));
            lblStatus.Text = "Логирование " + chTxt + " (" + param + ") → " + Path.GetFileName(csvPath) + "   (" + ipBox.Text.Trim() + ":" + interval + " мс)";

            logThread = new Thread(() => LogLoop(ipBox.Text.Trim(), interval, param, chans));
            logThread.IsBackground = true;
            logThread.Start();
        }

        void SetRecordingUi(bool rec)
        {
            ipBox.Enabled = !rec;
            intervalBox.Enabled = !rec;
            foreach (var c in chkCh) c.Enabled = !rec;
            measBox.Enabled = !rec;
            btnOpen.Enabled = !rec;
            btnJpg.Enabled = !rec;
            miOpen.Enabled = !rec;
            miSaveJpg.Enabled = !rec;
        }

        void SetRecIndicator(bool on)
        {
            lblRec.ForeColor = on ? Color.LimeGreen : Color.Gray;
            lblRec.ToolTipText = on ? "Идёт запись" : "Запись не идёт";
        }

        void StopLog()
        {
            if (!running) return;
            running = false;
            btnStart.Text = "Пуск";
            lblStatus.Text = "Остановлено. Файл: " + (csvPath ?? "-");
            SetRecordingUi(false);
            SetRecIndicator(false);
            try { if (logThread != null && logThread.IsAlive) logThread.Join(1500); } catch { }
        }

        void LogLoop(string ip, int interval, string param, int[] chans)
        {
            var inv = CultureInfo.InvariantCulture;
            double gapStart = -1;
            bool everConnected = false;
            AppendLog("Логирование запущено");
            while (running)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        ConnectWithTimeout(client, ip, ScpiPort, 3000);
                        using (var stream = client.GetStream())
                        {
                            if (!everConnected) everConnected = true;
                            if (gapStart >= 0)
                            {
                                double gapEnd = NowSec();
                                if (gapEnd > gapStart + 0.05)
                                {
                                    plot.AddGap(gapStart, gapEnd);
                                    AppendLog(string.Format("Связь восстановлена (обрыв {0:F1} с)", gapEnd - gapStart));
                                }
                                gapStart = -1;
                                SafeIfUi(() => lblStatus.Text = "Связь восстановлена. Логирование продолжается → " + Path.GetFileName(csvPath));
                            }
                            while (running)
                            {
                                double t = NowSec();
                                var sb = new StringBuilder();
                                sb.Append(t.ToString("F3", inv));
                                for (int i = 0; i < chans.Length; i++)
                                {
                                    string cmd = string.Format(":MEAS:ITEM? {0},CHAN{1}", param, chans[i]);
                                    double v = QueryVrms(stream, cmd);
                                    if (double.IsNaN(v) || Math.Abs(v) > 1e30) { sb.Append(';').Append("NaN"); continue; }
                                    sb.Append(';').Append(v.ToString("F8", inv));
                                    SafeAppend(i, t, v);
                                }
                                lock (fileLock) { try { File.AppendAllText(csvPath, sb.ToString() + "\r\n"); } catch { } }
                                Thread.Sleep(Math.Max(5, interval));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!running) break;
                    if (everConnected && gapStart < 0)
                    {
                        gapStart = NowSec();
                        AppendLog("Обрыв связи: " + ex.Message + " [" + ex.GetType().Name + "]");
                    }
                    SafeIfUi(() => lblStatus.Text = "НЕТ СВЯЗИ — переподключение… (" + ex.Message + ")");
                    for (int i = 0; i < 40 && running; i++) Thread.Sleep(100);
                }
            }
            AppendLog("Логирование остановлено");
            SafeIfUi(() => { btnStart.Text = "Пуск"; SetRecordingUi(false); SetRecIndicator(false); });
        }

        void ConnectWithTimeout(TcpClient client, string ip, int port, int timeoutMs)
        {
            var ar = client.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                client.Close();
                throw new TimeoutException("таймаут подключения");
            }
            client.EndConnect(ar);
        }

        double NowSec() { return (DateTime.UtcNow - startTime).TotalSeconds; }

        void AppendLog(string msg)
        {
            try
            {
                string path = Path.Combine(DataDir, "log.txt");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                              + "  " + msg + Environment.NewLine;
                lock (fileLock) { File.AppendAllText(path, line); }
            }
            catch { }
        }

        string QueryString(NetworkStream stream, string cmd)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string r = QueryStringOnce(stream, cmd);
                if (r.Length > 0) return r;
                Thread.Sleep(200);
            }
            throw new IOException("пустой ответ");
        }

        string QueryStringOnce(NetworkStream stream, string cmd)
        {
            byte[] b = Encoding.ASCII.GetBytes(cmd + "\n");
            stream.Write(b, 0, b.Length);
            var sb = new StringBuilder();
            var buf = new byte[65536];
            DateTime last = DateTime.UtcNow;
            while ((DateTime.UtcNow - last).TotalMilliseconds < 1500)
            {
                if (stream.DataAvailable)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n > 0)
                    {
                        last = DateTime.UtcNow;
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.ToString().Contains("\n")) break;
                    }
                }
                else Thread.Sleep(10);
            }
            return sb.ToString().Trim();
        }

        double QueryVrms(NetworkStream stream, string cmd)
        {
            string r = QueryString(stream, cmd);
            double val;
            if (!double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return double.NaN;
            if (val >= 9.8e36) return double.NaN;
            return val;
        }

        void SafeAppend(int idx, double t, double v)
        {
            SafeIfUi(() =>
            {
                plot.AppendData(idx, t, v);
                CheckThresholds(v);
            });
        }
        void SafeStatus(string msg) { SafeIfUi(() => lblStatus.Text = msg); }

        void SafeIfUi(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); }
            catch { }
        }

        void ApplyTheme(bool dark)
        {
            plot.SetTheme(dark);

            Color back = dark ? Color.FromArgb(24, 29, 35) : SystemColors.Control;
            Color fore = dark ? Color.FromArgb(226, 235, 243) : Color.Black;
            Color inputBack = dark ? Color.FromArgb(32, 39, 46) : Color.White;
            Color btnBack = dark ? Color.FromArgb(45, 55, 65) : SystemColors.Control;
            Color btnBorder = dark ? Color.FromArgb(70, 85, 100) : SystemColors.ControlDark;

            BackColor = back;
            ThemeControls(this, dark, back, fore, inputBack, btnBack, btnBorder);
            lblAlarm.ForeColor = dark ? Color.FromArgb(255, 96, 96) : Color.Red;

            if (mainMenu != null)
            {
                mainMenu.BackColor = back;
                mainMenu.ForeColor = fore;
                foreach (ToolStripItem it in mainMenu.Items) ThemeMenuItem(it, fore);
                mainMenu.Renderer = dark
                    ? (ToolStripRenderer)new ToolStripProfessionalRenderer(new DarkColorTable())
                    : new ToolStripProfessionalRenderer();
            }
            if (statusBar != null)
            {
                statusBar.BackColor = back;
                statusBar.ForeColor = fore;
                statusBar.Renderer = dark
                    ? (ToolStripRenderer)new ToolStripProfessionalRenderer(new DarkColorTable())
                    : new ToolStripProfessionalRenderer();
                lblView.ForeColor = fore;
                lblMouse.ForeColor = fore;
            }
            Invalidate(true);
            if (IsHandleCreated) SetDarkTitleBar(dark);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void SetDarkTitleBar(bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
            }
            catch { }
        }

        static void ThemeMenuItem(ToolStripItem it, Color fore)
        {
            it.ForeColor = fore;
            var mi = it as ToolStripMenuItem;
            if (mi != null) foreach (ToolStripItem sub in mi.DropDownItems) ThemeMenuItem(sub, fore);
        }

        static void ThemeControls(Control root, bool dark, Color back, Color fore, Color inputBack, Color btnBack, Color btnBorder)
        {
            foreach (Control c in root.Controls)
            {
                if (c is PlotControl || c is MenuStrip || c is StatusStrip) continue;

                if (c is Button)
                {
                    var b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = btnBack;
                    b.ForeColor = fore;
                    b.FlatAppearance.BorderColor = btnBorder;
                }
                else if (c is TextBox || c is ComboBox || c is NumericUpDown)
                {
                    c.BackColor = inputBack;
                    c.ForeColor = fore;
                }
                else if (c is Label || c is CheckBox)
                {
                    c.BackColor = Color.Transparent;
                    c.ForeColor = fore;
                }
                else if (c is Panel || c is FlowLayoutPanel)
                {
                    c.BackColor = back;
                }

                ThemeControls(c, dark, back, fore, inputBack, btnBack, btnBorder);
            }
        }

        void UpdateStatus()
        {            lblView.Text = string.Format(CultureInfo.InvariantCulture,
                "вид: t [{0:F3}; {1:F3}] с   V [{2:F5}; {3:F5}]   |  колесо=зум по времени, Ctrl+колесо=зум по напряжению, ЛКМ-прямоугольник=зум, СКМ/ПКМ=прокрутка, ПКМ/двойной клик/R=полный вид, F=автомасштаб, Space=пуск/стоп",
                plot.XMin, plot.XMax, plot.YMin, plot.YMax);
        }
    }

    public class DarkColorTable : ProfessionalColorTable
    {
        static Color Bg { get { return Color.FromArgb(24, 29, 35); } }
        static Color Sel { get { return Color.FromArgb(48, 59, 70); } }
        static Color Line { get { return Color.FromArgb(70, 85, 100); } }

        public override Color MenuItemSelected { get { return Sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
        public override Color MenuItemPressedGradientBegin { get { return Sel; } }
        public override Color MenuItemPressedGradientEnd { get { return Sel; } }
        public override Color MenuItemBorder { get { return Line; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(30, 36, 43); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(30, 36, 43); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(30, 36, 43); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(30, 36, 43); } }
        public override Color ToolStripGradientBegin { get { return Bg; } }
        public override Color ToolStripGradientMiddle { get { return Bg; } }
        public override Color ToolStripGradientEnd { get { return Bg; } }
        public override Color StatusStripGradientBegin { get { return Bg; } }
        public override Color StatusStripGradientEnd { get { return Bg; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Color.FromArgb(40, 48, 56); } }
    }

    public class PlotControl : Control
    {
        public class Series
        {
            public string Name;
            public Color Color;
            public readonly List<PointD> Pts = new List<PointD>();
        }

        readonly List<Series> series = new List<Series>();
        static readonly Color[] ChannelDark = new Color[]
        {
            Color.FromArgb(255, 209, 26),
            Color.FromArgb(0, 208, 224),
            Color.FromArgb(232, 59, 208),
            Color.FromArgb(74, 107, 255)
        };
        static readonly Color[] ChannelLight = new Color[]
        {
            Color.FromArgb(196, 154, 0),
            Color.FromArgb(0, 144, 168),
            Color.FromArgb(176, 32, 160),
            Color.FromArgb(30, 63, 191)
        };
        public static Color ChannelColor(int i, bool dark)
        {
            var pal = dark ? ChannelDark : ChannelLight;
            return pal[((i % pal.Length) + pal.Length) % pal.Length];
        }

        public bool DarkTheme = true;
        public Color ChartBack { get { return DarkTheme ? Color.FromArgb(11, 16, 22) : Color.White; } }
        Color GridColor { get { return DarkTheme ? Color.FromArgb(38, 50, 60) : Color.LightGray; } }
        Color AxisColor { get { return DarkTheme ? Color.FromArgb(150, 165, 178) : Color.Black; } }
        Color TextColor { get { return DarkTheme ? Color.FromArgb(214, 226, 234) : Color.Black; } }
        Color ThresholdColor { get { return DarkTheme ? Color.FromArgb(255, 90, 90) : Color.Crimson; } }
        Color ZeroLineColor { get { return DarkTheme ? Color.FromArgb(100, 115, 130) : Color.FromArgb(105, 105, 105); } }

        double dataXMin, dataXMax, dataYMin, dataYMax;
        double fitXMin, fitXMax, fitYMin, fitYMax;
        bool hasData;
        Point startPt, curPt;
        bool dragging;
        Point panPt;
        bool panning;
        bool axisPanX, axisPanY;
        bool rightPanning, rightPanned;
        bool rightWheelZoomed;
        readonly List<PointD> gaps = new List<PointD>();
        public bool ZoomX = true;
        public bool ZoomY = false;
        public bool ZeroBasedY = false;
        bool autoScale = true;
        public double XMin, XMax, YMin, YMax;
        public double ThresholdHi = double.PositiveInfinity;
        public double ThresholdLo = double.NegativeInfinity;

        const float PadL = 82f, PadR = 78f, PadT = 18f, PadB = 56f;
        float PW { get { return Math.Max(10, Width - PadL - PadR); } }
        float PH { get { return Math.Max(10, Height - PadT - PadB); } }

        public event Action ViewChanged;
        public event Action<double, double> CursorMoved;

        public PlotControl()
        {
            DoubleBuffered = true;
            BackColor = ChartBack;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            var menu = new ContextMenuStrip();
            var miInX = new ToolStripMenuItem("Приблизить (время)");
            miInX.Click += (s, e) => ZoomInX();
            var miOutX = new ToolStripMenuItem("Отдалить (время)");
            miOutX.Click += (s, e) => ZoomOutX();
            var miInY = new ToolStripMenuItem("Приблизить (напряжение)");
            miInY.Click += (s, e) => ZoomInY();
            var miOutY = new ToolStripMenuItem("Отдалить (напряжение)");
            miOutY.Click += (s, e) => ZoomOutY();
            var miFull = new ToolStripMenuItem("Полный вид");
            miFull.Click += (s, e) => ResetView();
            menu.Items.Add(miFull);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miInX);
            menu.Items.Add(miOutX);
            menu.Items.Add(miInY);
            menu.Items.Add(miOutY);
            ContextMenuStrip = menu;
        }

        public void SetData(List<Series> src)
        {
            series.Clear();
            series.AddRange(src);
            RecomputeBounds();
            ResetView();
        }

        void RecomputeBounds()
        {
            hasData = false;
            dataXMin = dataXMax = dataYMin = dataYMax = 0;
            foreach (var s in series)
                foreach (var q in s.Pts)
                {
                    if (!hasData) { hasData = true; dataXMin = dataXMax = q.X; dataYMin = dataYMax = q.Y; }
                    else UpdateMinMax(q.X, q.Y);
                }
            if (hasData) ComputeFit();
            else
            {
                fitXMin = fitXMax = fitYMin = fitYMax = 0;
                XMin = XMax = YMin = YMax = 0;
            }
        }

        void UpdateMinMax(double x, double y)
        {
            if (x < dataXMin) dataXMin = x;
            if (x > dataXMax) dataXMax = x;
            if (y < dataYMin) dataYMin = y;
            if (y > dataYMax) dataYMax = y;
        }

        public void ClearData()
        {
            series.Clear();
            lock (gaps) gaps.Clear();
            hasData = false;
            dataXMin = dataXMax = dataYMin = dataYMax = 0;
            fitXMin = fitXMax = fitYMin = fitYMax = 0;
            XMin = XMax = YMin = YMax = 0;
            Invalidate();
        }

        public void AddGap(double t0, double t1)
        {
            if (t1 <= t0) return;
            lock (gaps) gaps.Add(new PointD(t0, t1));
            Invalidate();
        }

        public void AddSeries(string name)
        {
            AddSeries(name, series.Count);
        }

        public void AddSeries(string name, int colorIndex)
        {
            series.Add(new Series { Name = name, Color = ChannelColor(colorIndex, DarkTheme) });
        }

        public void SetTheme(bool dark)
        {
            DarkTheme = dark;
            BackColor = ChartBack;
            for (int i = 0; i < series.Count; i++) series[i].Color = ChannelColor(i, dark);
            Invalidate();
        }

        public void AppendData(int idx, double x, double y)
        {
            if (idx < 0 || idx >= series.Count) return;
            if (double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(y) > 1e30) return;
            var s = series[idx];
            s.Pts.Add(new PointD(x, y));
            if (!hasData) { hasData = true; dataXMin = dataXMax = x; dataYMin = dataYMax = y; }
            else UpdateMinMax(x, y);
            ComputeFit();
            if (autoScale)
            {
                XMin = 0; XMax = dataXMax; YMin = fitYMin; YMax = fitYMax;
            }
            else
            {
                double span = XMax - XMin;
                if (span <= 0)
                {
                    XMin = fitXMin; XMax = fitXMax;
                }
                else if (x > XMax)
                {
                    XMin = x - span;
                    XMax = x;
                }
                if (YMax - YMin <= 0) { YMin = fitYMin; YMax = fitYMax; }
            }
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        void ComputeFit()
        {
            double sx = StepFor(dataXMin, dataXMax);
            fitXMin = Math.Floor((dataXMin * 1.05) / sx) * sx;
            fitXMax = Math.Ceiling((dataXMax * 1.03) / sx) * sx;

            double sy;
            if (ZeroBasedY)
            {
                sy = StepFor(0, dataYMax);
                double limit0 = Math.Ceiling((dataYMax * 1.10) / sy) * sy;
                if (limit0 <= 0) limit0 = sy > 0 ? sy : 1;
                fitYMin = 0;
                fitYMax = limit0;
            }
            else
            {
                double amp = Math.Max(Math.Abs(dataYMin), Math.Abs(dataYMax));
                if (amp <= 0) amp = 1;
                sy = StepFor(-amp, amp);
                double limit = Math.Ceiling((amp * 1.10) / sy) * sy;
                fitYMin = -limit;
                fitYMax = limit;
            }

            if (fitXMax <= fitXMin) fitXMax = fitXMin + (sx > 0 ? sx : 1);
            if (fitYMax <= fitYMin) fitYMax = fitYMin + (sy > 0 ? sy : 1);
        }

        static double StepFor(double mn, double mx)
        {
            double range = mx - mn;
            if (range <= 0) range = Math.Max(Math.Abs(mx), 1);
            double raw = range / 8;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double nrm = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
            return nrm * mag;
        }

        public void SetAutoScale(bool on)
        {
            autoScale = on;
            if (on) ResetView();
            else Invalidate();
        }

        public void ResetView()
        {
            if (!hasData) { Invalidate(); return; }
            autoScale = true;
            XMin = 0; XMax = dataXMax; YMin = fitYMin; YMax = fitYMax;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        float ToPxX(double v) { double d = XMax - XMin; if (d <= 0) return PadL; return PadL + (float)((v - XMin) / d) * PW; }
        float ToPxY(double v) { double d = YMax - YMin; if (d <= 0) return PadT; return PadT + (float)((YMax - v) / d) * PH; }
        double ToValX(float px) { return XMin + ((px - PadL) / PW) * (XMax - XMin); }
        double ToValY(float py) { return YMax - ((py - PadT) / PH) * (YMax - YMin); }

        static bool ClipTest(double p, double q, ref double u1, ref double u2)
        {
            if (p < 0)
            {
                double r = q / p;
                if (r > u2) return false;
                if (r > u1) u1 = r;
            }
            else if (p > 0)
            {
                double r = q / p;
                if (r < u1) return false;
                if (r < u2) u2 = r;
            }
            else if (q < 0) return false;
            return true;
        }

        static bool ClipLine(ref double x0, ref double y0, ref double x1, ref double y1,
                             double xmin, double ymin, double xmax, double ymax)
        {
            double u1 = 0, u2 = 1;
            double dx = x1 - x0, dy = y1 - y0;
            if (!ClipTest(-dx, x0 - xmin, ref u1, ref u2)) return false;
            if (!ClipTest(dx, xmax - x0, ref u1, ref u2)) return false;
            if (!ClipTest(-dy, y0 - ymin, ref u1, ref u2)) return false;
            if (!ClipTest(dy, ymax - y0, ref u1, ref u2)) return false;
            if (u2 < u1) return false;
            double x2 = x0 + u2 * dx, y2 = y0 + u2 * dy;
            x0 += u1 * dx; y0 += u1 * dy;
            x1 = x2; y1 = y2;
            return true;
        }

        Rectangle RectFromPoints(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                 Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle && hasData)
            {
                panning = true;
                panPt = e.Location;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Right && hasData)
            {
                rightPanning = true;
                rightPanned = false;
                panPt = e.Location;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (hasData && (e.X < PadL || e.X > Width - PadR))
                {
                    axisPanY = true;
                    panPt = e.Location;
                    Cursor = Cursors.SizeNS;
                    return;
                }
                if (hasData && e.Y > Height - PadB)
                {
                    axisPanX = true;
                    panPt = e.Location;
                    Cursor = Cursors.SizeWE;
                    return;
                }
                dragging = true; startPt = e.Location; curPt = e.Location; Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (hasData && CursorMoved != null) CursorMoved(ToValX(e.X), ToValY(e.Y));
            if (axisPanY)
            {
                PanByPixels(0, e.Y - panPt.Y, false, true);
                panPt = e.Location;
                return;
            }
            if (axisPanX)
            {
                PanByPixels(e.X - panPt.X, 0, true, false);
                panPt = e.Location;
                return;
            }
            if (rightPanning)
            {
                int dx = e.X - panPt.X, dy = e.Y - panPt.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) rightPanned = true;
                PanByPixels(dx, dy, true, true);
                panPt = e.Location;
                return;
            }
            if (panning)
            {
                PanByPixels(e.X - panPt.X, e.Y - panPt.Y, true, true);
                panPt = e.Location;
                return;
            }
            if (dragging) { curPt = e.Location; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle)
            {
                panning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                rightPanning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (axisPanX || axisPanY)
            {
                axisPanX = axisPanY = false;
                Cursor = Cursors.Default;
                return;
            }
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;

            int x0 = Math.Min(startPt.X, curPt.X), x1 = Math.Max(startPt.X, curPt.X);
            int y0 = Math.Min(startPt.Y, curPt.Y), y1 = Math.Max(startPt.Y, curPt.Y);

            if ((x1 - x0) < 4 && (y1 - y0) < 4) { Invalidate(); return; }

            bool changed = false;

            if (ZoomX && (x1 - x0) >= 4)
            {
                double tx0 = ToValX(x0), tx1 = ToValX(x1);
                if (tx1 > tx0) { XMin = Math.Max(fitXMin, tx0); XMax = Math.Min(fitXMax, tx1); changed = true; }
            }

            if (ZoomY && (y1 - y0) >= 4)
            {
                double ny1 = ToValY(y0), ny0 = ToValY(y1);
                if (ny1 > ny0) { YMin = Math.Max(fitYMin, ny0); YMax = Math.Min(fitYMax, ny1); changed = true; }
            }

            if (!changed) { Invalidate(); return; }
            autoScale = false;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!hasData) return;
            double factor = Math.Pow(1.2, e.Delta / 120.0);
            bool rightDown = (MouseButtons & MouseButtons.Right) != 0;
            if (rightDown) rightWheelZoomed = true;
            bool vertical = (ModifierKeys & Keys.Control) != 0 || rightDown;
            autoScale = false;
            if (vertical)
            {
                ZoomAxis(ref YMin, ref YMax, ToValY(e.Y), factor);
                ClampYTo(6000);
            }
            else ZoomAxis(ref XMin, ref XMax, ToValX(e.X), factor);
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        const int WM_MOUSEWHEEL = 0x020A;
        const int WM_CONTEXTMENU = 0x007B;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                Point pt = PointToClient(Cursor.Position);
                var e = new MouseEventArgs(MouseButtons.None, 0, pt.X, pt.Y, delta);
                OnMouseWheel(e);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_CONTEXTMENU)
            {
                if (rightWheelZoomed || rightPanned)
                {
                    rightWheelZoomed = false;
                    rightPanned = false;
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        void ZoomAxis(ref double mn, ref double mx, double anchor, double factor)
        {
            double span = mx - mn;
            if (span <= 0 || factor <= 0) return;
            double newSpan = span / factor;
            if (newSpan <= 0) return;
            double f = (anchor - mn) / span;
            mn = anchor - f * newSpan;
            mx = mn + newSpan;
        }

        void ClampYTo(double limit)
        {
            double lo = -limit, hi = limit;
            double span = YMax - YMin;
            if (span >= hi - lo) { YMin = lo; YMax = hi; }
            else if (YMin < lo) { YMin = lo; YMax = lo + span; }
            else if (YMax > hi) { YMax = hi; YMin = hi - span; }
        }

        void PanByPixels(int dx, int dy, bool xAxis, bool yAxis)
        {
            if (!hasData) return;
            autoScale = false;
            double spX = XMax - XMin, spY = YMax - YMin;
            if (xAxis)
            {
                double dt = -(dx / PW) * spX;
                XMin += dt; XMax += dt;
            }
            if (yAxis)
            {
                double dv = (dy / PH) * spY;
                YMin += dv; YMax += dv;
            }
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        public void ZoomInX() { ZoomRelative(1.5, true); }
        public void ZoomOutX() { ZoomRelative(1.0 / 1.5, true); }
        public void ZoomInY() { ZoomRelative(1.5, false); }
        public void ZoomOutY() { ZoomRelative(1.0 / 1.5, false); }

        void ZoomRelative(double factor, bool axisX)
        {
            if (!hasData) return;
            autoScale = false;
            double anchor = axisX ? (XMin + XMax) / 2 : (YMin + YMax) / 2;
            if (axisX) ZoomAxis(ref XMin, ref XMax, anchor, factor);
            else ZoomAxis(ref YMin, ref YMax, anchor, factor);
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        public void ScrollBy(double pages)
        {
            if (!hasData) return;
            autoScale = false;
            double span = XMax - XMin;
            double dt = pages * 0.25 * span;
            XMin += dt; XMax += dt;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        public void GotoStart()
        {
            if (!hasData) return;
            autoScale = false;
            double span = Math.Min(XMax - XMin, fitXMax - fitXMin);
            XMin = fitXMin; XMax = fitXMin + span;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        public void GotoEnd()
        {
            if (!hasData) return;
            autoScale = false;
            double span = Math.Min(XMax - XMin, fitXMax - fitXMin);
            XMax = fitXMax; XMin = fitXMax - span;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); ResetView(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            if (!hasData)
            {
                using (var b = new SolidBrush(TextColor))
                    g.DrawString("Нет данных. Нажмите «Пуск» для логирования или File → Open CSV...", Font, b, 30, 30);
                return;
            }

            DrawGrid(g);
            DrawAxes(g);
            DrawThresholds(g);
            DrawGaps(g);

            foreach (var s in series)
            {
                if (s.Pts.Count == 0) continue;
                using (var pen = new Pen(s.Color, 2.2f))
                    DrawSeries(g, pen, s);
            }

            DrawLegend(g);

            if (dragging)
            {
                var r = RectFromPoints(startPt, curPt);
                using (var p = new Pen(Color.Red, 1f))
                using (var b = new SolidBrush(Color.FromArgb(40, Color.Red)))
                { g.FillRectangle(b, r); g.DrawRectangle(p, r); }
            }
        }

        static int LowerBound(List<PointD> l, double x)
        {
            int a = 0, b = l.Count;
            while (a < b)
            {
                int m = (a + b) / 2;
                if (l[m].X < x) a = m + 1; else b = m;
            }
            return a;
        }

        static int UpperBound(List<PointD> l, double x)
        {
            int a = 0, b = l.Count;
            while (a < b)
            {
                int m = (a + b) / 2;
                if (l[m].X <= x) a = m + 1; else b = m;
            }
            return a;
        }

        void DrawSeries(Graphics g, Pen pen, Series s)
        {
            var pts = s.Pts;
            int lo = LowerBound(pts, XMin);
            int hi = UpperBound(pts, XMax);
            if (hi <= lo) return;
            int start = Math.Max(0, lo - 1);
            int count = hi - start;
            int cols = Math.Max(1, (int)PW);
            double span = XMax - XMin;

            if (span > 0 && count > Math.Max(2 * cols, 500))
            {
                var mn = new double[cols];
                var mx = new double[cols];
                var sum = new double[cols];
                var cnt = new int[cols];
                var any = new bool[cols];
                for (int i = start; i < hi; i++)
                {
                    int c = (int)((pts[i].X - XMin) / span * cols);
                    if (c < 0) c = 0; else if (c >= cols) c = cols - 1;
                    double y = pts[i].Y;
                    if (!any[c]) { any[c] = true; mn[c] = mx[c] = y; }
                    else { if (y < mn[c]) mn[c] = y; if (y > mx[c]) mx[c] = y; }
                    sum[c] += y; cnt[c]++;
                }
                double xStep = span / cols;
                for (int c = 0; c < cols; c++)
                {
                    if (!any[c]) continue;
                    float x = (ToPxX(XMin + c * xStep) + ToPxX(XMin + (c + 1) * xStep)) / 2f;
                    g.DrawLine(pen, x, ToPxY(mx[c]), x, ToPxY(mn[c]));
                }
                using (var penAvg = new Pen(pen.Color, pen.Width))
                {
                    PointF prevAvg = PointF.Empty;
                    bool first = true;
                    for (int c = 0; c < cols; c++)
                    {
                        if (!any[c]) continue;
                        float x = (ToPxX(XMin + c * xStep) + ToPxX(XMin + (c + 1) * xStep)) / 2f;
                        float y = ToPxY(sum[c] / cnt[c]);
                        var pc = new PointF(x, y);
                        if (!first) g.DrawLine(penAvg, prevAvg, pc);
                        prevAvg = pc; first = false;
                    }
                }
                return;
            }

            double cX0 = PadL, cY0 = PadT, cX1 = Width - PadR, cY1 = Height - PadB;
            PointD prev = pts[start];
            for (int i = start + 1; i < hi; i++)
            {
                PointD q = pts[i];
                double x0 = ToPxX(prev.X), y0 = ToPxY(prev.Y);
                double x1 = ToPxX(q.X), y1 = ToPxY(q.Y);
                if (ClipLine(ref x0, ref y0, ref x1, ref y1, cX0, cY0, cX1, cY1))
                    g.DrawLine(pen, (float)x0, (float)y0, (float)x1, (float)y1);
                prev = q;
            }
        }

        void DrawGaps(Graphics g)
        {
            PointD[] copy;
            lock (gaps) copy = gaps.ToArray();
            if (copy.Length == 0) return;
            float top = PadT, height = Height - PadB - PadT;
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            foreach (var gp in copy)
            {
                if (gp.Y < XMin || gp.X > XMax) continue;
                double a = Math.Max(gp.X, XMin), b = Math.Min(gp.Y, XMax);
                float x0 = ToPxX(a), x1 = ToPxX(b);
                if (x1 - x0 < 1) continue;
                using (var pen = new Pen(ThresholdColor, 1f))
                using (var fill = new SolidBrush(Color.FromArgb(46, 110, 110, 110)))
                {
                    g.FillRectangle(fill, x0, top, x1 - x0, height);
                    g.DrawRectangle(pen, x0, top, x1 - x0, height);
                }
                if (x1 - x0 > 90)
                {
                    using (var tb = new SolidBrush(TextColor))
                        g.DrawString("Обрыв связи", Font, tb, new RectangleF(x0, top, x1 - x0, height), fmt);
                }
            }
            fmt.Dispose();
        }

        void DrawLegend(Graphics g)
        {
            float y = PadT + 6;
            foreach (var s in series)
            {
                if (s.Pts.Count == 0) continue;
                using (var brush = new SolidBrush(s.Color))
                {
                    g.FillRectangle(brush, PadL + 4, y, 18, 4);
                }
                using (var tb = new SolidBrush(TextColor))
                    g.DrawString(s.Name, Font, tb, PadL + 26, y - 3);
                y += 18;
            }
        }

        void DrawThresholds(Graphics g)
        {
            DrawOneThreshold(g, ThresholdHi, "Верх. порог");
            DrawOneThreshold(g, ThresholdLo, "Ниж. порог");
        }

        void DrawOneThreshold(Graphics g, double val, string label)
        {
            if (double.IsInfinity(val) || double.IsNaN(val)) return;
            if (val < YMin || val > YMax) return;

            float y = ToPxY(val);
            using (var pen = new Pen(ThresholdColor, 1.4f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, PadL, y, Width - PadR, y);
            }
            using (var brush = new SolidBrush(ThresholdColor))
            {
                string txt = label + ": " + val.ToString("0.###", CultureInfo.InvariantCulture) + " В";
                var sz = g.MeasureString(txt, Font);
                float tx = Width - PadR - sz.Width - 4;
                float ty = y - sz.Height - 1;
                if (ty < PadT) ty = y + 2;
                g.DrawString(txt, Font, brush, tx, ty);
            }
        }

        void DrawGrid(Graphics g)
        {
            using (var pen = new Pen(GridColor, 1f))
            {
                double step = XStep();
                for (double v = Math.Ceiling(XMin / step) * step; v <= XMax; v += step)
                { float x = ToPxX(v); g.DrawLine(pen, x, PadT, x, Height - PadB); }
                NiceTicks(YMin, YMax, 12, out step);
                for (double v = Math.Ceiling(YMin / step) * step; v <= YMax; v += step)
                { float y = ToPxY(v); g.DrawLine(pen, PadL, y, Width - PadR, y); }
            }
            if (YMin <= 0 && YMax >= 0)
            {
                float y0 = ToPxY(0);
                using (var pen = new Pen(ZeroLineColor, 1.5f))
                    g.DrawLine(pen, PadL, y0, Width - PadR, y0);
            }
        }

        void DrawAxes(Graphics g)
        {
            using (var pen = new Pen(AxisColor, 1.2f))
            {
                g.DrawLine(pen, PadL, PadT, PadL, Height - PadB);
                g.DrawLine(pen, Width - PadR, PadT, Width - PadR, Height - PadB);
                g.DrawLine(pen, PadL, Height - PadB, Width - PadR, Height - PadB);
            }
            var fmtCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var fmtRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var fmtLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

            using (var brush = new SolidBrush(TextColor))
            {
                double step = XStep();
                for (double v = Math.Ceiling(XMin / step) * step; v <= XMax + 1e-9; v += step)
                {
                    float x = ToPxX(v);
                    g.DrawString(FormatTime(v), Font, brush, x, Height - PadB + 9, fmtCenter);
                }
                NiceTicks(YMin, YMax, 12, out step);
                for (double v = Math.Ceiling(YMin / step) * step; v <= YMax + 1e-9; v += step)
                {
                    float y = ToPxY(v);
                    string txt = v.ToString("0.####", CultureInfo.InvariantCulture) + " V";
                    g.DrawString(txt, Font, brush, PadL - 4, y, fmtRight);
                    g.DrawString(txt, Font, brush, Width - PadR + 4, y, fmtLeft);
                }
            }
        }

        static string FormatTime(double sec)
        {
            if (sec < 60) return sec.ToString("0.###", CultureInfo.InvariantCulture) + " s";
            if (sec < 3600)
            {
                int m = (int)(sec / 60), s = (int)(sec % 60);
                return m.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
            }
            if (sec < 86400)
            {
                int h = (int)(sec / 3600);
                int m = (int)(sec % 3600 / 60);
                int s = (int)(sec % 60);
                return h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture)
                       + ":" + s.ToString("00", CultureInfo.InvariantCulture);
            }
            int d = (int)(sec / 86400);
            int hh = (int)(sec % 86400 / 3600);
            int mm = (int)(sec % 3600 / 60);
            return d.ToString(CultureInfo.InvariantCulture) + " d " + hh.ToString(CultureInfo.InvariantCulture)
                   + ":" + mm.ToString("00", CultureInfo.InvariantCulture);
        }

        double XStep()
        {
            double range = XMax - XMin;
            if (range >= 86400)
            {
                double raw = range / 86400.0 / 10;
                double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
                double norm = raw / mag;
                double nrm = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
                return nrm * mag * 86400;
            }
            double s;
            NiceTicks(XMin, XMax, 10, out s);
            return s;
        }

        static void NiceTicks(double min, double max, int target, out double step)
        {
            double range = max - min;
            if (range <= 0) range = 1;
            double raw = range / Math.Max(1, target);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double nrm = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
            step = nrm * mag;
        }
    }
}
