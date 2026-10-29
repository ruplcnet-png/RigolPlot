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

namespace RigolPlot
{
    static class Program
    {
        // точность системного таймера 1 мс: иначе Sleep квантуется ~15.6 мс
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint TimeEndPeriod(uint ms);

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string path = null;
            if (args.Length > 0 && File.Exists(args[0])) path = args[0];

            TimeBeginPeriod(1);
            try { Application.Run(new MainForm(path)); }
            finally { TimeEndPeriod(1); }
        }
    }

    public struct PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    // Поле ввода напряжения: принимает и точку, и запятую как десятичный разделитель
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
        ToolStripStatusLabel lblView, lblMouse;

        // control panel
        TextBox ipBox;
        NumericUpDown intervalBox;
        CheckBox[] chkCh;
        ComboBox measBox;
        CheckBox chkTime, chkVolt, autoCheck;
        ComboBox themeBox;
        Button btnStart;
        Button btnOpen;
        Button btnJpg;
        NumericUpDown thrHiBox, thrLoBox;
        Label lblThrHi, lblThrLo;
        CheckBox chkSound;
        Label lblAlarm;
        string unitSymbol = "В";   // текущая единица канала по данным прибора
        double thrHi = 30.0, thrLo = -30.0;
        bool alarmHi, alarmLo;
        double lastValue;
        bool hasValue;
        long lastBeepTick;
        MenuStrip mainMenu;
        StatusStrip statusBar;
        ToolStripStatusLabel lblWarn;
        volatile bool scaleWarnShown;   // предупреждение о развёртке показываем один раз на сеанс
        Label lblStatus;

        // logging state
        Thread logThread;
        volatile bool running;
        DateTime startTime;
        string csvPath;
        readonly object fileLock = new object();
        const int ScpiPort = 5555;
        const string DefaultIp = "192.168.89.234";
        // всё (ini, CSV, JPG) — рядом с RigolPlot.exe; папки принудительно не создаём
        static readonly string AppDir = Application.StartupPath;
        static readonly string SettingsPath = Path.Combine(AppDir, "RigolPlot.ini");

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

            // menu
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            var open = new ToolStripMenuItem("&Open CSV...");
            open.Click += (s, e) => Open();
            var saveJpg = new ToolStripMenuItem("Save as &JPG...");
            saveJpg.Click += (s, e) => ExportJpg();
            var exit = new ToolStripMenuItem("E&xit");
            exit.Click += (s, e) => Close();
            file.DropDownItems.AddRange(new ToolStripItem[] { open, saveJpg, new ToolStripSeparator(), exit });
            menu.Items.Add(file);
            MainMenuStrip = menu;
            mainMenu = menu;

            // контрольная панель: три авто-раскладываемые строки
            var panel = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Gainsboro };

            var rowA = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 8, 10, 2), BackColor = Color.Transparent };
            var rowB = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 2, 10, 2), BackColor = Color.Transparent };
            var rowC = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(10, 2, 10, 6), BackColor = Color.Transparent };

            var lblIp = new Label { Text = "IP:", AutoSize = true };
            ipBox = new TextBox { Text = DefaultIp, Width = 130 };
            string lastIp = LoadLastIp();
            if (lastIp != null) ipBox.Text = lastIp;
            ipBox.Validated += (s, e) => SaveLastIp();

            var lblInt = new Label { Text = "Интервал, мс:", AutoSize = true };
            intervalBox = new NumericUpDown { Width = 110, Minimum = 10, Maximum = 60000, Increment = 10, Value = 100 };

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
            measBox.Items.AddRange(new object[] { "VRMS", "VAVG" });
            measBox.SelectedIndex = 1; // VAVG по умолчанию

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

            lblStatus = new Label { AutoSize = false, Width = 460, Height = 22, Text = "Готово." };

            lblThrHi = new Label { Text = "Порог верх, " + unitSymbol + ":", AutoSize = true };
            thrHiBox = new VoltBox
            {
                Width = 145,
                Minimum = -1000000M, Maximum = 1000000M,
                DecimalPlaces = 3, Increment = 0.5M, Value = 30M
            };
            thrHiBox.ValueChanged += (s, e) => thrHi = (double)thrHiBox.Value;

            lblThrLo = new Label { Text = "Порог низ, " + unitSymbol + ":", AutoSize = true };
            thrLoBox = new VoltBox
            {
                Width = 145,
                Minimum = -1000000M, Maximum = 1000000M,
                DecimalPlaces = 3, Increment = 0.5M, Value = -30M
            };
            thrLoBox.ValueChanged += (s, e) => thrLo = (double)thrLoBox.Value;

            chkSound = new CheckBox { Text = "Звук при выходе за порог", AutoSize = true, Checked = true };

            lblAlarm = new Label { AutoSize = false, Width = 380, Height = 22, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.Red, Text = "", TextAlign = ContentAlignment.MiddleLeft };

            // отступы, чтобы элементы не прилипали друг к другу
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

            // док-порядок: последним добавляем верхнюю строку
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
            lblView = new ToolStripStatusLabel("no data");
            lblWarn = new ToolStripStatusLabel("") { ForeColor = Color.OrangeRed, Visible = false };
            lblMouse = new ToolStripStatusLabel(" ");
            bar.Items.Add(lblView);
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(lblWarn);
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(lblMouse);
            statusBar = bar;

            // порядок добавления определяет док-раскладку:
            // меню — у самого верха, под ним панель, график занимает остаток
            Controls.Add(plot);
            Controls.Add(panel);
            Controls.Add(menu);
            Controls.Add(bar);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.R) plot.ResetView();
                else if (e.KeyCode == Keys.F) { autoCheck.Checked = !autoCheck.Checked; }
            };

            plot.ViewChanged += UpdateStatus;
            plot.CursorMoved += (t, v) =>
                lblMouse.Text = string.Format(CultureInfo.InvariantCulture, "курсор: {0:F3} с, {1:F5} {2}", t, v, plot.YUnit);

            FormClosing += (s, e) => { SaveLastIp(); StopLog(); };

            ApplyTheme(true);
            HandleCreated += (s, e) => SetDarkTitleBar(plot.DarkTheme);

            if (path != null) LoadData(path);
            else { path = FindLatestCsv(); if (path != null) LoadData(path); }
        }

        string FindLatestCsv()
        {
            try { return Directory.GetFiles(AppDir, "*.csv").OrderByDescending(File.GetLastWriteTime).FirstOrDefault(); }
            catch { }
            return null;
        }

        // --- последний IP осциллографа хранится в RigolPlot.ini рядом с exe ---
        static string LoadLastIp()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (string raw in File.ReadAllLines(SettingsPath))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!line.Substring(0, eq).Trim().Equals("ip", StringComparison.OrdinalIgnoreCase)) continue;

                    string v = line.Substring(eq + 1).Trim();
                    IPAddress a;
                    if (IPAddress.TryParse(v, out a)) return v;
                }
            }
            catch { }
            return null;
        }

        void SaveLastIp()
        {
            string v = (ipBox.Text ?? "").Trim();
            IPAddress a;
            if (!IPAddress.TryParse(v, out a)) return;   // неверный адрес не запоминаем
            try
            {
                File.WriteAllText(SettingsPath, "ip=" + v + "\r\n");
            }
            catch { }
        }

        void Open()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "CSV files|*.csv|All files|*.*";
                d.InitialDirectory = AppDir;
                if (d.ShowDialog(this) == DialogResult.OK) { StopLog(); LoadData(d.FileName); }
            }
        }

        // --- экспорт графика в JPG ---
        void ExportJpg()
        {
            try
            {
                using (var d = new SaveFileDialog())
                {
                    d.Filter = "JPEG image|*.jpg;*.jpeg";
                    d.DefaultExt = "jpg";
                    d.InitialDirectory = AppDir;   // как «Открыть CSV…» — папка exe
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

        // --- звуковой сигнал при выходе за порог ---
        void CheckThresholds(double v)
        {
            hasValue = true;
            lastValue = v;
            bool over = v > thrHi;
            bool under = v < thrLo;

            if (over && !alarmHi)
            {
                alarmHi = true;
                if (chkSound.Checked) SafeBeep(1200);
            }
            else if (!over && alarmHi) alarmHi = false;

            if (under && !alarmLo)
            {
                alarmLo = true;
                if (chkSound.Checked) SafeBeep(600);
            }
            else if (!under && alarmLo) alarmLo = false;

            if (alarmHi && alarmLo) lblAlarm.Text = "ТРЕВОГА: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " " + unitSymbol + " (оба порога)";
            else if (alarmHi) lblAlarm.Text = "ВЕРХ: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " " + unitSymbol + " > " + thrHi.ToString("0.###", CultureInfo.InvariantCulture) + " " + unitSymbol;
            else if (alarmLo) lblAlarm.Text = "НИЗ: " + v.ToString("0.###", CultureInfo.InvariantCulture) + " " + unitSymbol + " < " + thrLo.ToString("0.###", CultureInfo.InvariantCulture) + " " + unitSymbol;
            else lblAlarm.Text = "";
        }

        void RefreshAlarm() { if (hasValue) CheckThresholds(lastValue); }

        void SafeBeep(int freq)
        {
            long now = Environment.TickCount;
            if (now - lastBeepTick < 900) return; // не пищать чаще, чем раз в секунду
            lastBeepTick = now;
            BeepAsync(freq);
        }

        static void BeepAsync(int freq)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Console.Beep(freq, 300); } catch { }
            });
        }

        // номер канала из заголовка voltage_vavg_ch3 -> 2 (0-based); -1 если не найден
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

        // имя серии для легенды: voltage_vavg_ch2 -> CH2; старый одно-канальный заголовок -> CH1
        static string SeriesLabel(string header, int index)
        {
            int ci = ChannelIndexFromHeader(header);
            if (ci >= 0) return "CH" + (ci + 1);
            string h = (header ?? "").Trim();
            return h.Length > 0 ? h : "CH" + (index + 1);
        }

        // цвет серии привязан к номеру канала, а не к порядку колонок
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
                    int ci = SeriesColorIndex(hdr[j], j - 1);
                    sList.Add(new PlotControl.Series
                    {
                        Name = SeriesLabel(hdr[j], j - 1),
                        ChannelIndex = ci,
                        Color = PlotControl.ChannelColor(ci, plot.DarkTheme)
                    });
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
                plot.SetData(nonEmpty);
                plot.SetTimeOrigin(null);   // время начала записи в CSV не хранится — показываем системные дату и время

                // в CSV единицы не хранятся — для загруженного файла показываем вольты
                unitSymbol = "В";
                plot.SetUnits(null, unitSymbol);
                lblThrHi.Text = "Порог верх, " + unitSymbol + ":";
                lblThrLo.Text = "Порог низ, " + unitSymbol + ":";

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
            SaveLastIp();   // запоминаем рабочий адрес для следующего запуска

            int interval = (int)intervalBox.Value;
            string param = ((string)measBox.SelectedItem).Trim().ToUpperInvariant();

            int[] chans = SelectedChannels();
            if (chans.Length == 0)
            { MessageBox.Show(this, "Выберите хотя бы один канал.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string chTag = string.Join("_", Array.ConvertAll(chans, c => "ch" + c));
            csvPath = Path.Combine(AppDir, "rigol_" + chTag + "_" + param.ToLowerInvariant() + "_" + stamp + ".csv");

            var hdr = new StringBuilder("time_s");
            foreach (int c in chans) hdr.Append(';').Append("voltage_").Append(param.ToLowerInvariant()).Append("_ch").Append(c);
            try { File.WriteAllText(csvPath, hdr.ToString() + "\r\n"); }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось создать файл: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            plot.ClearData();
            foreach (int c in chans) plot.AddSeries("CH" + c, c - 1);
            plot.SetAutoScale(autoCheck.Checked);
            startTime = DateTime.UtcNow;
            plot.SetTimeOrigin(DateTime.Now);   // реальное время начала записи (t = 0)
            running = true;
            btnStart.Text = "Стоп";
            scaleWarnShown = false;
            if (lblWarn != null) { lblWarn.Text = ""; lblWarn.Visible = false; }
            string chTxt = string.Join(", ", Array.ConvertAll(chans, c => "CH" + c));
            lblStatus.Text = "Логирование " + chTxt + " (" + param + ") → " + Path.GetFileName(csvPath) + "   (" + ipBox.Text.Trim() + ":" + interval + " мс)";

            logThread = new Thread(() => LogLoop(ipBox.Text.Trim(), interval, param, chans));
            logThread.IsBackground = true;
            logThread.Start();
        }

        void StopLog()
        {
            if (!running) return;
            running = false;
            btnStart.Text = "Пуск";
            lblStatus.Text = "Остановлено. Файл: " + (csvPath ?? "-");
            try { if (logThread != null && logThread.IsAlive) logThread.Join(1500); } catch { }
        }

        void LogLoop(string ip, int interval, string param, int[] chans)
        {
            var inv = CultureInfo.InvariantCulture;
            int failures = 0;

            while (running)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        ConnectWithTimeout(client, ip, ScpiPort);
                        using (var stream = client.GetStream())
                        {
                            if (failures > 0) SafeStatus("Связь восстановлена: " + ip + " — запись продолжается");
                            failures = 0;
                            CheckTimebase(stream);   // предупреждение, если развёртка > 10 мс
                            SampleLoop(stream, interval, param, chans, inv);
                        }
                    }
                    break; // штатное завершение по кнопке «Стоп»
                }
                catch (Exception ex)
                {
                    if (!running) break;

                    failures++;
                    if (failures >= 10)
                    {
                        SafeStatus("Нет связи с " + ip + ": " + ex.Message + ". Логирование остановлено.");
                        running = false;
                        break;
                    }

                    SafeStatus("Сбой связи: " + ex.Message + " — переподключение " + failures + "/10");
                    WaitWhileRunning(1000);
                }
            }

            SafeIfUi(() =>
            {
                btnStart.Text = "Пуск";
                if (csvPath != null) lblStatus.Text = "Остановлено. Файл: " + csvPath;
            });
        }

        // подключение с таймаутом, чтобы недоступный прибор не «вешал» поток
        static void ConnectWithTimeout(TcpClient client, string ip, int port)
        {
            IAsyncResult ar = client.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(3000))
                throw new TimeoutException("таймаут подключения к " + ip + ":" + port);
            client.EndConnect(ar);
        }

        void WaitWhileRunning(int ms)
        {
            int spent = 0;
            while (running && spent < ms) { Thread.Sleep(100); spent += 100; }
        }

        // развёртка прибора (:TIMebase:SCAL?, с/дел). Если больше 10 мс — предупреждение:
        // строка состояния держит пометку, окно показываем один раз на сеанс логирования.
        void CheckTimebase(NetworkStream stream)
        {
            double scal = double.NaN;
            try { scal = double.Parse(QueryString(stream, ":TIMebase:SCAL?"), CultureInfo.InvariantCulture); }
            catch { }
            if (double.IsNaN(scal) || scal <= 0.01) return;

            string msTxt = (scal * 1000).ToString("0.###", CultureInfo.InvariantCulture);
            string msg = "Развёртка осциллографа " + msTxt + " мс больше 10 мс.";
            SafeIfUi(() =>
            {
                if (lblWarn != null) { lblWarn.Text = "Развёртка " + msTxt + " мс > 10 мс"; lblWarn.Visible = true; }
                if (!scaleWarnShown)
                {
                    scaleWarnShown = true;
                    MessageBox.Show(this, msg, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        // один сеанс связи: опрос по расписанию с шагом interval, без накопления drift
        void SampleLoop(NetworkStream stream, int interval, string param, int[] chans, CultureInfo inv)
        {
            DateTime next = DateTime.UtcNow;
            DateTime nextUnits = DateTime.UtcNow;
            int misses = 0;

            while (running)
            {
                // единицы каналов и развёртку перечитываем раз в 5 с — на случай смены на приборе
                if (DateTime.UtcNow >= nextUnits)
                {
                    RefreshUnits(stream, chans);
                    CheckTimebase(stream);
                    nextUnits = DateTime.UtcNow.AddSeconds(5);
                }

                double t = (DateTime.UtcNow - startTime).TotalSeconds;
                var sb = new StringBuilder();
                sb.Append(t.ToString("F3", inv));
                bool noAnswer = false;

                for (int i = 0; i < chans.Length; i++)
                {
                    string cmd = string.Format(CultureInfo.InvariantCulture, ":MEAS:ITEM? {0},CHAN{1}", param, chans[i]);
                    double v;
                    try { v = QueryVrms(stream, cmd); }
                    catch (FormatException) { v = double.NaN; }              // мусор в ответе — просто пропускаем точку
                    catch (Exception) { v = double.NaN; noAnswer = true; }   // таймаут/обрыв — повод переподключиться

                    if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e30)
                        sb.Append(";NaN");
                    else
                    {
                        sb.Append(';').Append(v.ToString("F8", inv));
                        int idx = i;
                        double tv = t, vv = v;
                        SafeAppend(idx, tv, vv);
                    }
                }

                AppendCsv(sb.ToString());

                // одиночный пропуск ответа не рвёт запись, три подряд — переподключаемся
                if (noAnswer)
                {
                    if (++misses >= 3) throw new IOException("прибор не отвечает");
                }
                else misses = 0;

                // держим расписание: следующий отсчёт — через interval от предыдущего
                next = next.AddMilliseconds(interval);
                int wait = (int)(next - DateTime.UtcNow).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
                else if (wait < -interval) next = DateTime.UtcNow; // сильно отстали — ресинхронизация
            }
        }

        void AppendCsv(string line)
        {
            lock (fileLock)
            {
                try { File.AppendAllText(csvPath, line + "\r\n"); }
                catch (Exception ex) { SafeStatus("Ошибка записи CSV: " + ex.Message); }
            }
        }

        string QueryString(NetworkStream stream, string cmd)
        {
            byte[] b = Encoding.ASCII.GetBytes(cmd + "\n");
            stream.Write(b, 0, b.Length);
            var sb = new StringBuilder();
            var buf = new byte[65536];
            DateTime last = DateTime.UtcNow;
            while ((DateTime.UtcNow - last).TotalMilliseconds < 300)
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
            string r = sb.ToString().Trim();
            if (r.Length == 0) throw new IOException("пустой ответ");
            return r;
        }

        double QueryVrms(NetworkStream stream, string cmd)
        {
            double val = double.Parse(QueryString(stream, cmd), CultureInfo.InvariantCulture);
            if (val >= 9.8e36) return double.NaN; // OVERRANGE / измерение недоступно
            return val;
        }

        // --- единицы каналов из прибора: :CHAN<n>:UNIT? -> VOLT / AMP / WATT / UNKN ---
        static string UnitSymbol(string raw)
        {
            string u = (raw ?? "").Trim().ToUpperInvariant();
            if (u.StartsWith("VOLT")) return "В";
            if (u.StartsWith("AMP")) return "А";
            if (u.StartsWith("WATT")) return "Вт";
            if (u.StartsWith("UNKN") || u == "U") return "ед.";
            return "В";
        }

        void RefreshUnits(NetworkStream stream, int[] chans)
        {
            var sym = new string[chans.Length];
            for (int i = 0; i < chans.Length; i++)
            {
                string raw = null;
                try { raw = QueryString(stream, ":CHAN" + chans[i] + ":UNIT?"); }
                catch { }
                sym[i] = UnitSymbol(raw);
            }
            int[] ch = chans;
            string[] s = sym;
            SafeIfUi(() => ApplyUnits(ch, s));
        }

        void ApplyUnits(int[] chans, string[] sym)
        {
            if (sym == null || sym.Length == 0) return;
            unitSymbol = sym[0];
            lblThrHi.Text = "Порог верх, " + unitSymbol + ":";
            lblThrLo.Text = "Порог низ, " + unitSymbol + ":";
            plot.SetUnits(sym, unitSymbol);
            RefreshAlarm();
        }

        void SafeAppend(int idx, double t, double v)
        {
            SafeIfUi(() =>
            {
                plot.AppendData(idx, t, v);
                CheckThresholds(v);
                UpdateStatus();   // строка состояния показывает текущий вид и при живой записи
            });
        }
        void SafeStatus(string msg) { SafeIfUi(() => lblStatus.Text = msg); }

        void SafeIfUi(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); }
            catch { }
        }

        // --- палитра экрана ---
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
                    DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int)); // старые сборки Windows 10
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
                "вид: t [{0:F3}; {1:F3}] с   V [{2:F5}; {3:F5}]   |  прямоугольник=зум по отмеченным осям, R/прав.кн.=полный вид, F=автомасштаб",
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
            public int ChannelIndex;   // номер канала (0..3) — палитра не зависит от порядка колонок
            public string Units = "В"; // единица канала по данным прибора (В/А/Вт)
            public readonly List<PointD> Pts = new List<PointD>();
        }

        readonly List<Series> series = new List<Series>();
        // цвета каналов как у Rigol DHO900: CH1 жёлтый, CH2 голубой, CH3 пурпурный, CH4 синий
        static readonly Color[] ChannelDark = new Color[]
        {
            Color.FromArgb(255, 209, 26),   // CH1 - жёлтый
            Color.FromArgb(0, 208, 224),    // CH2 - голубой
            Color.FromArgb(232, 59, 208),   // CH3 - пурпурный
            Color.FromArgb(74, 107, 255)    // CH4 - синий
        };
        static readonly Color[] ChannelLight = new Color[]
        {
            Color.FromArgb(196, 154, 0),    // CH1 - тёмно-жёлтый
            Color.FromArgb(0, 144, 168),    // CH2 - бирюзовый
            Color.FromArgb(176, 32, 160),   // CH3 - пурпурный
            Color.FromArgb(30, 63, 191)     // CH4 - тёмно-синий
        };
        public static Color ChannelColor(int i, bool dark)
        {
            var pal = dark ? ChannelDark : ChannelLight;
            return pal[((i % pal.Length) + pal.Length) % pal.Length];
        }

        public bool DarkTheme = true;
        public string YUnit = "В";
        public Color ChartBack { get { return DarkTheme ? Color.FromArgb(11, 16, 22) : Color.White; } }
        Color GridColor { get { return DarkTheme ? Color.FromArgb(38, 50, 60) : Color.LightGray; } }
        Color AxisColor { get { return DarkTheme ? Color.FromArgb(150, 165, 178) : Color.Black; } }
        Color TextColor { get { return DarkTheme ? Color.FromArgb(214, 226, 234) : Color.Black; } }
        Color ThresholdColor { get { return DarkTheme ? Color.FromArgb(255, 90, 90) : Color.Crimson; } }

        double dataXMin, dataXMax, dataYMin, dataYMax;
        double fitXMin, fitXMax, fitYMin, fitYMax;
        bool hasData;
        Point startPt, curPt;
        bool dragging;
        public bool ZoomX = true;
        public bool ZoomY = false;
        bool autoScale = true;
        public double XMin, XMax, YMin, YMax;
        public double ThresholdHi = double.PositiveInfinity;
        public double ThresholdLo = double.NegativeInfinity;

        // реальное время, соответствующее t = 0 (null — привязка неизвестна, напр. загруженный CSV)
        public DateTime? TimeOrigin;
        public void SetTimeOrigin(DateTime? origin) { TimeOrigin = origin; Invalidate(); }

        const float PadL = 92f, PadR = 18f, PadT = 18f, PadB = 64f;
        float PW { get { return Math.Max(10, Width - PadL - PadR); } }
        float PH { get { return Math.Max(10, Height - PadT - PadB); } }

        public event Action ViewChanged;
        public event Action<double, double> CursorMoved;

        public PlotControl()
        {
            DoubleBuffered = true;
            BackColor = ChartBack;
            ResizeRedraw = true;

            // живые системные дата и время в правом верхнем углу диаграммы
            var clk = new System.Windows.Forms.Timer { Interval = 1000 };
            clk.Tick += (s, e) => Invalidate();
            clk.Start();
            Disposed += (s, e) => clk.Dispose();
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
            hasData = false;
            dataXMin = dataXMax = dataYMin = dataYMax = 0;
            fitXMin = fitXMax = fitYMin = fitYMax = 0;
            XMin = XMax = YMin = YMax = 0;
            Invalidate();
        }

        public void AddSeries(string name)
        {
            AddSeries(name, series.Count);
        }

        public void AddSeries(string name, int colorIndex)
        {
            series.Add(new Series { Name = name, Color = ChannelColor(colorIndex, DarkTheme), ChannelIndex = colorIndex });
        }

        public void SetTheme(bool dark)
        {
            DarkTheme = dark;
            BackColor = ChartBack;
            for (int i = 0; i < series.Count; i++) series[i].Color = ChannelColor(series[i].ChannelIndex, dark);
            Invalidate();
        }

        // единицы: символ для каждой серии + основной (для оси и порогов)
        public void SetUnits(string[] perSeries, string primary)
        {
            YUnit = string.IsNullOrEmpty(primary) ? "В" : primary;
            for (int i = 0; i < series.Count; i++)
                series[i].Units = (perSeries != null && i < perSeries.Length && !string.IsNullOrEmpty(perSeries[i]))
                    ? perSeries[i] : YUnit;
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
                XMin = fitXMin; XMax = fitXMax; YMin = fitYMin; YMax = fitYMax;
            }
            Invalidate();
        }

        void ComputeFit()
        {
            double sx = StepFor(dataXMin, dataXMax);
            fitXMin = Math.Floor((dataXMin * 1.05) / sx) * sx;
            fitXMax = Math.Ceiling((dataXMax * 1.03) / sx) * sx;

            double sy = StepFor(dataYMin, dataYMax);
            double yRange = dataYMax - dataYMin;
            if (yRange <= 0) yRange = Math.Max(Math.Abs(dataYMax), 1);
            fitYMin = Math.Floor((dataYMin - yRange * 0.05) / sy) * sy;
            fitYMax = Math.Ceiling((dataYMax + yRange * 0.20) / sy) * sy;

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
            XMin = fitXMin; XMax = fitXMax; YMin = fitYMin; YMax = fitYMax;
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        float ToPxX(double v) { return PadL + (float)((v - XMin) / (XMax - XMin)) * PW; }
        float ToPxY(double v) { return PadT + (float)((YMax - v) / (YMax - YMin)) * PH; }
        double ToValX(float px) { return XMin + ((px - PadL) / PW) * (XMax - XMin); }
        double ToValY(float py) { return YMax - ((py - PadT) / PH) * (YMax - YMin); }

        Rectangle RectFromPoints(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                 Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { ResetView(); return; }
            if (e.Button == MouseButtons.Left) { dragging = true; startPt = e.Location; curPt = e.Location; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (hasData && CursorMoved != null) CursorMoved(ToValX(e.X), ToValY(e.Y));
            if (dragging) { curPt = e.Location; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;

            int x0 = Math.Min(startPt.X, curPt.X), x1 = Math.Max(startPt.X, curPt.X);
            int y0 = Math.Min(startPt.Y, curPt.Y), y1 = Math.Max(startPt.Y, curPt.Y);

            if ((x1 - x0) < 4 && (y1 - y0) < 4) { Invalidate(); return; }

            bool changed = false;

            // зум по времени (X), если отмечено «время»
            if (ZoomX && (x1 - x0) >= 4)
            {
                double tx0 = ToValX(x0), tx1 = ToValX(x1);
                if (tx1 > tx0) { XMin = Math.Max(fitXMin, tx0); XMax = Math.Min(fitXMax, tx1); changed = true; }
            }

            // зум по напряжению (Y), если отмечено «напряжение»
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

        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); ResetView(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            DrawClock(g);

            if (!hasData)
            {
                using (var b = new SolidBrush(TextColor))
                    g.DrawString("Нет данных. Нажмите «Пуск» для логирования или File → Open CSV...", Font, b, 30, 30);
                return;
            }

            DrawGrid(g);
            DrawAxes(g);
            DrawThresholds(g);

            foreach (var s in series)
            {
                if (s.Pts.Count == 0) continue;
                using (var pen = new Pen(s.Color, 2.2f))
                {
                    bool first = true; PointF prev = PointF.Empty;
                    foreach (var q in s.Pts)
                    {
                        if (q.X < XMin || q.X > XMax || q.Y < YMin || q.Y > YMax) { first = true; continue; }
                        float px = ToPxX(q.X), py = ToPxY(q.Y);
                        var pc = new PointF(px, py);
                        if (!first) g.DrawLine(pen, prev, pc);
                        prev = pc; first = false;
                    }
                }
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

        // текущие системные дата и время — справа вверху внутри области графика
        void DrawClock(Graphics g)
        {
            using (var brush = new SolidBrush(TextColor))
            {
                string txt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
                var sz = g.MeasureString(txt, Font);
                float x = Width - PadR - sz.Width - 2;
                g.DrawString(txt, Font, brush, x, PadT + 4);
            }
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
                string nm = s.Name;
                if (!string.IsNullOrEmpty(s.Units) && s.Units != YUnit) nm += " (" + s.Units + ")";
                using (var tb = new SolidBrush(TextColor))
                    g.DrawString(nm, Font, tb, PadL + 26, y - 3);
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
                string txt = label + ": " + val.ToString("0.###", CultureInfo.InvariantCulture) + " " + YUnit;
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
                double step; NiceTicks(XMin, XMax, 10, out step);
                for (double v = Math.Ceiling(XMin / step) * step; v <= XMax; v += step)
                { float x = ToPxX(v); g.DrawLine(pen, x, PadT, x, Height - PadB); }
                NiceTicks(YMin, YMax, 12, out step);
                for (double v = Math.Ceiling(YMin / step) * step; v <= YMax; v += step)
                { float y = ToPxY(v); g.DrawLine(pen, PadL, y, Width - PadR, y); }
            }
        }

        void DrawAxes(Graphics g)
        {
            using (var pen = new Pen(AxisColor, 1.2f))
            {
                g.DrawLine(pen, PadL, PadT, PadL, Height - PadB);
                g.DrawLine(pen, PadL, Height - PadB, Width - PadR, Height - PadB);
            }
            var fmtCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var fmtRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            using (var brush = new SolidBrush(TextColor))
            {
                double step; NiceTicks(XMin, XMax, 10, out step);
                for (double v = Math.Ceiling(XMin / step) * step; v <= XMax + 1e-9; v += step)
                { float x = ToPxX(v); g.DrawString(v.ToString("0.###", CultureInfo.InvariantCulture), Font, brush, x, Height - PadB + 10, fmtCenter); }
                NiceTicks(YMin, YMax, 12, out step);
                for (double v = Math.Ceiling(YMin / step) * step; v <= YMax + 1e-9; v += step)
                { float y = ToPxY(v); g.DrawString(v.ToString("0.####", CultureInfo.InvariantCulture), Font, brush, PadL - 6, y, fmtRight); }
            }
            using (var brush = new SolidBrush(TextColor))
            {
                g.DrawString("время (с)", Font, brush, (PadL + Width - PadR) / 2f, Height - PadB + 24, new StringFormat { Alignment = StringAlignment.Center });
                // системные дата и время: для видимого окна, если известно начало записи; иначе — текущие
                g.DrawString(TimeCaption(), Font, brush, (PadL + Width - PadR) / 2f, Height - PadB + 42, new StringFormat { Alignment = StringAlignment.Center });
                var saved = g.Save();
                g.TranslateTransform(14, (PadT + Height - PadB) / 2f);
                g.RotateTransform(-90);
                g.DrawString("значение (" + YUnit + ")", Font, brush, 0, 0, new StringFormat { Alignment = StringAlignment.Center });
                g.Restore(saved);
            }
        }

        // подпись под осью времени: системные дата и время видимого окна
        string TimeCaption()
        {
            var inv = CultureInfo.InvariantCulture;
            if (!TimeOrigin.HasValue || !hasData)
                return DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", inv);

            DateTime t0 = TimeOrigin.Value.AddSeconds(XMin);
            DateTime t1 = TimeOrigin.Value.AddSeconds(XMax);
            if (t0.Date == t1.Date)
                return t0.ToString("dd.MM.yyyy HH:mm:ss", inv) + " \u2014 " + t1.ToString("HH:mm:ss", inv);
            return t0.ToString("dd.MM.yyyy HH:mm:ss", inv) + " \u2014 " + t1.ToString("dd.MM.yyyy HH:mm:ss", inv);
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
