using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Поиск Radmin Viewer: рядом с программой, в Program Files, у запущенного процесса.</summary>
    internal static class RadminLocator
    {
        public static string Detect(out string how)
        {
            how = null;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var roots = new List<string> { baseDir };
            try { roots.AddRange(Directory.GetDirectories(baseDir)); } catch { }
            string parent = Path.GetDirectoryName(baseDir.TrimEnd('\\'));
            if (parent != null) roots.Add(parent);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Radmin Viewer 3"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Radmin Viewer 3"));
            foreach (var d in roots)
                foreach (var n in new[] { "rview3.exe", "Radmin.exe" })
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) { how = "найден: " + p; return p; }
                }
            foreach (var n in new[] { "rview3", "Radmin" })
                foreach (var pr in Process.GetProcessesByName(n))
                {
                    string p = Native.GetProcessPath(pr.Id);
                    if (p != null && File.Exists(p)) { how = "взят у запущенного Radmin Viewer: " + p; return p; }
                }
            return null;
        }
    }

    /// <summary>Окно настроек: разделы слева, содержимое справа. Изменения применяются по «Сохранить».</summary>
    internal class SettingsForm : Form
    {
        readonly AppSettings _s;
        readonly Action _checkShifts;
        readonly List<string> _sections = new List<string>();
        readonly List<Control> _pages = new List<Control>();
        readonly NavList _nav;
        readonly Panel _content;

        // Radmin
        TextBox _tbExe, _tbPb; Toggle _tgPb;
        // Подключение
        Choice _cmbMode, _cmbMethod; NumericUpDown _numPort, _numTimeout, _numDelay, _numRetries, _numRetryDelay;
        Toggle _tgPrecheck, _tgStretch, _tgCloseErr, _tgAutoRe; TextBox _tbExtra;
        // Авторизация
        Toggle _tgAutoLogin, _tgEncrypt; TextBox _tbLogin, _tbPass;
        // Сетка
        Choice _cmbGrid, _cmbMonitor, _cmbSort; NumericUpDown _numRows, _numCols, _numGap, _numAutoPage;
        Toggle _tgBorderless, _tgTaskbar, _tgAutoArrange, _tgAdopt, _tgPaging;
        // Смены
        Toggle _tgShift, _tgShiftEnlarge; NumericUpDown _numShiftMin;
        // HUD
        Choice _cmbTheme;
        Toggle _tgHud; Choice _cmbOpacity; Toggle _tgHudResetSize;
        // Обновления
        Toggle _tgUpdCheck, _tgUpdPre;

        /// <summary>Пользователь нажал «Установить» в разделе обновлений - MainForm продолжит установку.</summary>
        public ReleaseInfo InstallRelease { get; private set; }

        public SettingsForm(AppSettings s, Action checkShifts, int startSection = 0)
        {
            _s = s; _checkShifts = checkShifts;
            Theme.StyleForm(this);
            Text = "Настройки - Radmin Grid";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 640);
            MinimumSize = new Size(760, 520);
            ShowInTaskbar = false;
            MinimizeBox = false;

            _nav = new NavList { Dock = DockStyle.Left, Width = 210 };
            _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 16, 8), BackColor = Theme.Bg };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 12) };
            var btnSave = new RButton("Сохранить", Theme.IcoPlay.Length > 0 ? "" : null, RButton.Kind.Primary);
            var btnCancel = new RButton("Отмена");
            btnSave.Dock = DockStyle.Right; btnCancel.Dock = DockStyle.Right;
            var gap = new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.Bg };
            bottom.Controls.Add(btnSave); bottom.Controls.Add(gap); bottom.Controls.Add(btnCancel);
            btnSave.Click += delegate { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            BuildRadmin(); BuildConnection(); BuildAuth(); BuildGrid(); BuildShifts(); BuildLook(); BuildHud(); BuildUpdates();
            _nav.Items = _sections;
            _nav.SelectedChanged += ShowPage;

            Controls.Add(_content);
            Controls.Add(bottom);
            Controls.Add(_nav);
            LoadValues();
            _nav.Selected = Math.Max(0, Math.Min(startSection, _sections.Count - 1));
            ShowPage(_nav.Selected);
            KeyPreview = true;
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        void ShowPage(int i)
        {
            _content.SuspendLayout();
            _content.Controls.Clear();
            _content.Controls.Add(_pages[i]);
            _content.ResumeLayout();
        }

        // ---------------- построение разделов ----------------

        TableLayoutPanel Page(string title, string icon, string subtitle)
        {
            _sections.Add(icon + "\t" + title);
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var h = new Label { Text = title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            t.Controls.Add(h, 0, t.RowCount++); t.SetColumnSpan(h, 2);
            if (subtitle != null)
            {
                var sub = new Label { Text = subtitle, Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 14) };
                t.Controls.Add(sub, 0, t.RowCount++); t.SetColumnSpan(sub, 2);
            }
            Theme.DarkScroll(t);
            _pages.Add(t);
            return t;
        }

        static void Row(TableLayoutPanel t, string label, Control c, string hint = null)
        {
            int r = t.RowCount++;
            var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 10, 12, 4) };
            t.Controls.Add(l, 0, r);
            if (hint != null)
            {
                var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
                box.Controls.Add(c);
                box.Controls.Add(new Label { Text = hint, Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(4, 0, 0, 6) });
                t.Controls.Add(box, 1, r);
            }
            else t.Controls.Add(c, 1, r);
        }

        static void Wide(TableLayoutPanel t, Control c, string hint = null)
        {
            int r = t.RowCount++;
            if (hint == null) { t.Controls.Add(c, 0, r); t.SetColumnSpan(c, 2); return; }
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
            box.Controls.Add(c);
            box.Controls.Add(new Label { Text = hint, Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(55, 0, 0, 8) });
            t.Controls.Add(box, 0, r); t.SetColumnSpan(box, 2);
        }

        static void Divider(TableLayoutPanel t, string title)
        {
            var l = new Label { Text = title, Font = Theme.Section, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 16, 0, 4) };
            int r = t.RowCount++;
            t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 2);
        }

        static Choice Combo(params string[] items) { return new Choice(items); }

        static NumericUpDown Num(int min, int max, int inc = 1) { return new NumericUpDown { Minimum = min, Maximum = max, Increment = inc }; }

        static Control F(Control c, int w = 260) { if (c is Choice) { c.Width = w; return c; } return Theme.Field(c, w); }

        static FlowLayoutPanel Line(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
            f.Controls.AddRange(cs);
            return f;
        }

        void BuildRadmin()
        {
            var t = Page("Radmin Viewer", "", "Путь к Radmin Viewer 3 (портативному rview3.exe или установленному Radmin.exe). Прав администратора не нужно.");
            _tbExe = new TextBox();
            var browse = new RButton("Обзор…", Theme.IcoFolder);
            browse.Click += delegate
            {
                using (var d = new OpenFileDialog { Filter = "Radmin Viewer|rview3.exe;Radmin.exe;*.exe|Все файлы|*.*" })
                {
                    if (File.Exists(_tbExe.Text)) d.InitialDirectory = Path.GetDirectoryName(_tbExe.Text);
                    if (d.ShowDialog(this) == DialogResult.OK) _tbExe.Text = d.FileName;
                }
            };
            var find = new RButton("Найти", Theme.IcoSearch);
            find.Click += delegate
            {
                string how;
                string p = RadminLocator.Detect(out how);
                if (p != null) _tbExe.Text = p;
                else MessageBox.Show(this, "Radmin Viewer не найден. Укажите путь кнопкой «Обзор…».", Text);
            };
            Row(t, "Radmin Viewer", Line(F(_tbExe, 380), browse, find));

            _tbPb = new TextBox();
            var browsePb = new RButton("Обзор…", Theme.IcoFolder);
            browsePb.Click += delegate
            {
                using (var d = new OpenFileDialog { Filter = "Телефонная книга Radmin|*.rpb|Все файлы|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) _tbPb.Text = d.FileName;
            };
            Row(t, "Телефонная книга (.rpb)", Line(F(_tbPb, 380), browsePb), "Необязательно. Нужна для импорта и ключа /pb.");
            _tgPb = new Toggle("Передавать Radmin ключ /pb с этой книгой");
            Wide(t, _tgPb);
        }

        void BuildConnection()
        {
            var t = Page("Подключение", "", "Как запускать подключения и как реагировать на ошибки.");
            _cmbMode = Combo("Полное управление", "Только просмотр", "Передача файлов", "Telnet");
            _cmbMethod = Combo("Командная строка (/connect)", "Через телефонную книгу (UI)");
            Row(t, "Режим", F(_cmbMode));
            Row(t, "Способ", F(_cmbMethod), "Командная строка - надёжнее. Телефонная книга - использует пароли, сохранённые в Radmin.");
            _numPort = Num(1, 65535); Row(t, "Порт по умолчанию", F(_numPort, 120));
            _numTimeout = Num(5, 600); Row(t, "Таймаут подключения, с", F(_numTimeout, 120), "Рекомендуется 20–30 с.");
            _numDelay = Num(0, 10000, 100); Row(t, "Пауза между запусками, мс", F(_numDelay, 120));
            _tbExtra = new TextBox(); Row(t, "Доп. аргументы Radmin", F(_tbExtra, 300));
            Divider(t, "Поведение");
            _tgPrecheck = new Toggle("Проверять доступность порта перед запуском"); Wide(t, _tgPrecheck);
            _tgStretch = new Toggle("Масштабировать изображение под окно (/stretch)"); Wide(t, _tgStretch);
            _tgCloseErr = new Toggle("Закрывать окна ошибок Radmin автоматически"); Wide(t, _tgCloseErr);
            _tgAutoRe = new Toggle("Автоматически переподключать при обрыве"); Wide(t, _tgAutoRe);
            _numRetries = Num(1, 100); Row(t, "Попыток", F(_numRetries, 120));
            _numRetryDelay = Num(1, 3600); Row(t, "Пауза между попытками, с", F(_numRetryDelay, 120));
            _tgAutoRe.CheckedChanged += delegate { _numRetries.Enabled = _numRetryDelay.Enabled = _tgAutoRe.Checked; };
        }

        void BuildAuth()
        {
            var t = Page("Авторизация", "", "Программа сама заполняет окно входа Radmin для запускаемых ею подключений.");
            _tgAutoLogin = new Toggle("Автоматически вводить логин и пароль"); Wide(t, _tgAutoLogin);
            _tbLogin = new TextBox(); Row(t, "Логин", F(_tbLogin, 260));
            _tbPass = new TextBox { UseSystemPasswordChar = true };
            var eye = new RButton(null, Theme.IcoEye, RButton.Kind.Ghost);
            eye.Click += delegate { _tbPass.UseSystemPasswordChar = !_tbPass.UseSystemPasswordChar; eye.Toggled = !_tbPass.UseSystemPasswordChar; eye.Invalidate(); };
            Row(t, "Пароль", Line(F(_tbPass, 260), eye));
            _tgEncrypt = new Toggle("Шифровать пароль (DPAPI)");
            Wide(t, _tgEncrypt, "Пароль расшифруется только под этим пользователем Windows на этом ПК. Без шифрования он хранится в файле настроек почти открытым текстом.");
        }

        void BuildGrid()
        {
            var t = Page("Сетка и вкладки", "", "Раскладка окон подключений по экрану. Если окна не помещаются - они делятся на вкладки.");
            _cmbGrid = Combo(GridLayout.Presets);
            _numRows = Num(1, 12); _numCols = Num(1, 12);
            _cmbGrid.SelectedIndexChanged += delegate { _numRows.Enabled = _numCols.Enabled = _cmbGrid.Text == "Своя"; };
            Row(t, "Раскладка", F(_cmbGrid, 160));
            Row(t, "Строк × столбцов", Line(F(_numRows, 90), new Label { Text = "×", AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(4, 10, 4, 0) }, F(_numCols, 90)), "Для раскладки «Своя».");
            _cmbMonitor = new Choice();
            var scr = GridLayout.OrderedScreens();
            for (int i = 0; i < scr.Length; i++)
                _cmbMonitor.Items.Add("Монитор " + (i + 1) + ": " + scr[i].Bounds.Width + "×" + scr[i].Bounds.Height + (scr[i].Primary ? " (основной)" : ""));
            _cmbMonitor.Items.Add("Все мониторы");
            Row(t, "Монитор", F(_cmbMonitor, 300));
            _cmbSort = Combo("Порядок открытия", "По имени", "По папке и имени", "По адресу");
            Row(t, "Сортировка окон", F(_cmbSort));
            _numGap = Num(0, 50); Row(t, "Отступ между окнами, px", F(_numGap, 120));
            Divider(t, "Поведение");
            _tgPaging = new Toggle("Не помещаются в сетку - делить на вкладки"); Wide(t, _tgPaging);
            _numAutoPage = Num(4, 64); Row(t, "«Авто»: окон на вкладке", F(_numAutoPage, 120), "На один монитор.");
            _tgAutoArrange = new Toggle("Расставлять автоматически при подключении"); Wide(t, _tgAutoArrange);
            _tgAdopt = new Toggle("Подхватывать окна, открытые вручную в Radmin"); Wide(t, _tgAdopt);
            _tgBorderless = new Toggle("Убирать рамки и заголовки окон"); Wide(t, _tgBorderless);
            _tgTaskbar = new Toggle("Занимать и область панели задач"); Wide(t, _tgTaskbar);
        }

        void BuildShifts()
        {
            var t = Page("Смены", Theme.IcoShift, "Номер смены читается из строки «Зміна №…» в окне подключения: число - смена открыта, 0 - закрыта. " +
                                           "Только снимок окна на этом ПК - на удалённый компьютер не уходит ни щелчков, ни клавиш.");
            _tgShift = new Toggle("Отслеживать номер смены"); Wide(t, _tgShift);
            _numShiftMin = Num(1, 240); Row(t, "Проверять каждые, мин", F(_numShiftMin, 120));
            _tgShiftEnlarge = new Toggle("Ненадолго увеличивать мелкие окна для чтения");
            Wide(t, _tgShiftEnlarge, "Окно на ~1 с увеличивается под всеми остальными окнами (этого не видно) и возвращается на место. Нужно для мелких окон в сетке.");
            var now = new RButton("Проверить смены сейчас", Theme.IcoRefresh);
            now.Click += delegate { if (_checkShifts != null) _checkShifts(); };
            Wide(t, now);
            if (!ShiftOcr.Available)
                Wide(t, Theme.NewLabel("OCR Windows недоступен: нужен языковой пакет «Русский» или «English» с распознаванием текста.", true));
        }

        void BuildLook()
        {
            var t = Page("Оформление", "", "Тема окна программы. Применяется сразу после «Сохранить», подключения не прерываются.");
            _cmbTheme = Combo("Тёмная", "Светлая", "Как в Windows");
            Row(t, "Тема", F(_cmbTheme, 220), "«Как в Windows» - по параметру «Режим приложений» в настройках Windows.");

            var open = new RButton("Открыть папку программы", Theme.IcoFolder);
            open.Click += delegate { try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(AppSettings.FilePath) + "\""); } catch { } };
            Wide(t, open);
            var lnk = new RButton(Shortcut.Exists ? "Обновить ярлык на рабочем столе" : "Создать ярлык на рабочем столе", Theme.IcoAdd);
            lnk.Click += delegate
            {
                try
                {
                    string path = Shortcut.CreateOnDesktop();
                    lnk.Text = "Обновить ярлык на рабочем столе";
                    MessageBox.Show(this, "Ярлык создан:\n" + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "Не удалось создать ярлык: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            Wide(t, lnk);
        }

        static readonly string[] ThemeKeys = { "dark", "light", "system" };

        void BuildHud()
        {
            var t = Page("HUD", "", "Компактная панель статусов поверх всех окон.");
            _tgHud = new Toggle("Показывать HUD"); Wide(t, _tgHud);
            _cmbOpacity = Combo("100%", "90%", "75%", "60%");
            Row(t, "Непрозрачность", F(_cmbOpacity, 120));
            _tgHudResetSize = new Toggle("Сбросить размер и положение HUD"); Wide(t, _tgHudResetSize);
        }

        void BuildUpdates()
        {
            var t = Page("Обновления", Theme.IcoSync, "Перед установкой проверяются контрольная сумма SHA-256, имя и версия файла.");
            Row(t, "Установлена версия", Theme.NewLabel("v" + Updater.Format(Updater.Current), true));
            _tgUpdCheck = new Toggle("Проверять обновления при запуске"); Wide(t, _tgUpdCheck);
            _tgUpdPre = new Toggle("Предлагать предварительные версии"); Wide(t, _tgUpdPre);

            var status = Theme.NewLabel("", true);
            status.MaximumSize = new Size(560, 0);
            var check = new RButton("Проверить сейчас", Theme.IcoRefresh);
            var install = new RButton("Установить", null, RButton.Kind.Primary) { Visible = false };
            ReleaseInfo found = null;
            check.Click += delegate
            {
                check.Enabled = false; install.Visible = false;
                status.ForeColor = Theme.TextDim; status.Text = "Проверка…";
                bool pre = _tgUpdPre.Checked;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    ReleaseInfo r = null; string err = null;
                    try { r = Updater.FindLatest(pre); }
                    catch (UpdateException ex) { err = ex.Message; }
                    catch (Exception ex) { err = "Не удалось проверить обновления: " + ex.Message; }
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        check.Enabled = true;
                        found = r;
                        if (err != null) { status.ForeColor = Theme.Danger; status.Text = err; }
                        else if (Updater.IsNewer(r))
                        {
                            status.ForeColor = Theme.Text;
                            status.Text = "Доступна версия " + r.Title + ".";
                            install.Text = "Установить v" + Updater.Format(r.Version);
                            install.Visible = true;
                        }
                        else status.Text = r != null ? "Установлена последняя версия."
                                                     : "Обновлений пока нет.";
                    }));
                });
            };
            install.Click += delegate
            {
                if (found == null || !Commit()) return;
                InstallRelease = found;
                DialogResult = DialogResult.OK;
                Close();
            };
            Wide(t, Line(check, install));
            Wide(t, status);
        }

        // ---------------- значения ----------------

        static decimal Clamp(NumericUpDown n, int v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        void LoadValues()
        {
            _tbExe.Text = _s.RadminPath; _tbPb.Text = _s.PhonebookPath; _tgPb.Checked = _s.PassPhonebookSwitch;
            _cmbMode.SelectedIndex = (int)_s.Mode; _cmbMethod.SelectedIndex = (int)_s.Method;
            _numPort.Value = Clamp(_numPort, _s.DefaultPort); _numTimeout.Value = Clamp(_numTimeout, _s.ConnectTimeoutSec);
            _numDelay.Value = Clamp(_numDelay, _s.LaunchDelayMs); _tbExtra.Text = _s.ExtraArgs;
            _tgPrecheck.Checked = _s.TcpPrecheck; _tgStretch.Checked = _s.Stretch; _tgCloseErr.Checked = _s.CloseErrorDialogs;
            _tgAutoRe.Checked = _s.AutoReconnect; _numRetries.Value = Clamp(_numRetries, _s.MaxReconnects);
            _numRetryDelay.Value = Clamp(_numRetryDelay, _s.ReconnectDelaySec);
            _numRetries.Enabled = _numRetryDelay.Enabled = _s.AutoReconnect;
            _tgAutoLogin.Checked = _s.AutoLogin; _tbLogin.Text = _s.Login; _tbPass.Text = _s.GetPassword(); _tgEncrypt.Checked = _s.EncryptPassword;
            int gi = Array.IndexOf(GridLayout.Presets, _s.GridPreset); _cmbGrid.SelectedIndex = gi >= 0 ? gi : 0;
            _numRows.Value = Clamp(_numRows, _s.GridRows); _numCols.Value = Clamp(_numCols, _s.GridCols);
            _numRows.Enabled = _numCols.Enabled = _cmbGrid.Text == "Своя";
            int screens = GridLayout.OrderedScreens().Length;
            _cmbMonitor.SelectedIndex = _s.Monitor < 0 ? screens : Math.Min(_s.Monitor, screens - 1);
            _cmbSort.SelectedIndex = Math.Max(0, _cmbSort.Items.IndexOf(_s.GridSort));
            _numGap.Value = Clamp(_numGap, _s.Gap);
            _tgPaging.Checked = _s.Paging; _numAutoPage.Value = Clamp(_numAutoPage, _s.AutoPageMax);
            _tgAutoArrange.Checked = _s.AutoArrange; _tgAdopt.Checked = _s.AdoptManual;
            _tgBorderless.Checked = _s.Borderless; _tgTaskbar.Checked = _s.CoverTaskbar;
            _tgShift.Checked = _s.ShiftCheck; _numShiftMin.Value = Clamp(_numShiftMin, _s.ShiftIntervalMin); _tgShiftEnlarge.Checked = _s.ShiftEnlarge;
            _cmbTheme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, _s.UiTheme));
            _tgHud.Checked = _s.HudVisible;
            _cmbOpacity.SelectedIndex = Math.Max(0, _cmbOpacity.Items.IndexOf(_s.HudOpacity + "%"));
            _tgUpdCheck.Checked = _s.UpdateCheck; _tgUpdPre.Checked = _s.UpdatePrerelease;
        }

        bool Commit()
        {
            string exe = _tbExe.Text.Trim().Trim('"');
            if (exe.Length > 0 && !File.Exists(exe) &&
                MessageBox.Show(this, "Файл не найден:\n" + exe + "\n\nВсё равно сохранить?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;
            _s.RadminPath = exe; _s.PhonebookPath = _tbPb.Text.Trim().Trim('"'); _s.PassPhonebookSwitch = _tgPb.Checked;
            _s.Mode = (ConnectMode)Math.Max(0, _cmbMode.SelectedIndex); _s.Method = (ConnectMethod)Math.Max(0, _cmbMethod.SelectedIndex);
            _s.DefaultPort = (int)_numPort.Value; _s.ConnectTimeoutSec = (int)_numTimeout.Value; _s.LaunchDelayMs = (int)_numDelay.Value;
            _s.ExtraArgs = _tbExtra.Text; _s.TcpPrecheck = _tgPrecheck.Checked; _s.Stretch = _tgStretch.Checked;
            _s.CloseErrorDialogs = _tgCloseErr.Checked; _s.AutoReconnect = _tgAutoRe.Checked;
            _s.MaxReconnects = (int)_numRetries.Value; _s.ReconnectDelaySec = (int)_numRetryDelay.Value;
            _s.AutoLogin = _tgAutoLogin.Checked; _s.Login = _tbLogin.Text.Trim(); _s.EncryptPassword = _tgEncrypt.Checked; _s.SetPassword(_tbPass.Text);
            _s.GridPreset = _cmbGrid.Text; _s.GridRows = (int)_numRows.Value; _s.GridCols = (int)_numCols.Value;
            int screens = GridLayout.OrderedScreens().Length;
            _s.Monitor = _cmbMonitor.SelectedIndex >= screens ? -1 : Math.Max(0, _cmbMonitor.SelectedIndex);
            _s.GridSort = _cmbSort.Text; _s.Gap = (int)_numGap.Value;
            _s.Paging = _tgPaging.Checked; _s.AutoPageMax = (int)_numAutoPage.Value;
            _s.AutoArrange = _tgAutoArrange.Checked; _s.AdoptManual = _tgAdopt.Checked;
            _s.Borderless = _tgBorderless.Checked; _s.CoverTaskbar = _tgTaskbar.Checked;
            _s.ShiftCheck = _tgShift.Checked; _s.ShiftIntervalMin = (int)_numShiftMin.Value; _s.ShiftEnlarge = _tgShiftEnlarge.Checked;
            _s.UiTheme = ThemeKeys[Math.Max(0, _cmbTheme.SelectedIndex)];
            _s.HudVisible = _tgHud.Checked;
            int op; if (int.TryParse(_cmbOpacity.Text.TrimEnd('%'), out op)) _s.HudOpacity = op;
            if (_tgHudResetSize.Checked) { _s.HudWidth = 0; _s.HudHeight = 0; _s.HudX = -1; _s.HudY = -1; }
            _s.UpdateCheck = _tgUpdCheck.Checked; _s.UpdatePrerelease = _tgUpdPre.Checked;
            return true;
        }

        /// <summary>Список разделов слева.</summary>
        class NavList : PaintedControl
        {
            public List<string> Items = new List<string>();
            public int Selected;
            int _hover = -1;
            public event Action<int> SelectedChanged;
            int RowH { get { return Theme.Ui.Height + 18; } }

            public NavList() { BackColor = Theme.Surface; Cursor = Cursors.Hand; Padding = new Padding(8, 16, 8, 8); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Surface);
                for (int i = 0; i < Items.Count; i++)
                {
                    var parts = Items[i].Split('\t');
                    var r = new Rectangle(8, 16 + i * (RowH + 4), Width - 16, RowH);
                    if (i == Selected || i == _hover) Pill(g, new Rectangle(r.X, r.Y, r.Width, r.Height), i == Selected ? Theme.Hover : Theme.Surface2, Color.Empty);
                    if (i == Selected) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X + 2, r.Y + 8, 3, r.Height - 16);
                    TextRenderer.DrawText(g, parts[0], Theme.Icons, new Rectangle(r.X + 14, r.Y, 24, r.Height), Theme.Text, TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, parts.Length > 1 ? parts[1] : "", i == Selected ? Theme.UiBold : Theme.Ui,
                        new Rectangle(r.X + 44, r.Y, r.Width - 48, r.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            int Hit(Point p)
            {
                int i = (p.Y - 16) / (RowH + 4);
                return p.Y >= 16 && i >= 0 && i < Items.Count ? i : -1;
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int h = Hit(e.Location);
                if (h < 0 || h == Selected) return;
                Selected = h;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(h);
            }
        }
    }
}
