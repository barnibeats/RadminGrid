using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    internal partial class MainForm : Form
    {
        readonly AppSettings _s;
        readonly RadminController _rc;
        readonly ConnectionManager _cm;
        readonly ShiftMonitor _shifts;
        readonly HudForm _hud;

        NavRail _rail;
        SplitContainer _split;
        Hero _hero;
        RButton _btnGo;
        // телефонная книга
        TreeView _tree;
        TextBox _tbSearch, _tbQuick;
        Label _lblBookCount;
        // подключения
        PageStrip _pages;
        ChipBar _chips;
        SessionList _list;
        Choice _cmbGridQuick;
        Label _lblConnCount;
        // журнал
        TextBox _log;
        Panel _logPanel;

        bool _suppressCheck;
        bool _autoStarted;              // запущены с /autoconnect - не прерывать окном обновления
        string _pendingUpdate;          // скачанный и проверенный exe, ставится при закрытии
        readonly System.Windows.Forms.Timer _arrangeDebounce = new System.Windows.Forms.Timer { Interval = 800 };
        readonly System.Windows.Forms.Timer _saveDebounce = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly System.Windows.Forms.Timer _searchDebounce = new System.Windows.Forms.Timer { Interval = 250 };
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 1000 };

        static readonly string[] ModeNames = { "Полное управление", "Только просмотр", "Передача файлов", "Telnet" };

        public MainForm()
        {
            _s = AppSettings.Load();
            Theme.Apply(_s.UiTheme);
            _rc = new RadminController(_s);

            Theme.StyleForm(this);
            Text = "Radmin Grid";
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(1060, 680);

            _cm = new ConnectionManager(_s, _rc, this);
            _shifts = new ShiftMonitor(_s, _cm, this);
            _hud = new HudForm(_cm, _s, delegate { ArrangeNow(true); });

            BuildUi();
            LoadWindowBounds();
            RebuildTree();

            _cm.Log += AppendLog;
            _cm.Changed += RefreshConnections;
            _cm.ArrangeRequested += delegate { if (_s.AutoArrange) { _arrangeDebounce.Stop(); _arrangeDebounce.Start(); } };
            _arrangeDebounce.Tick += delegate { _arrangeDebounce.Stop(); ArrangeNow(false); };
            _saveDebounce.Tick += delegate { SaveNow(); };
            _searchDebounce.Tick += delegate { _searchDebounce.Stop(); RebuildTree(); };
            _tick.Tick += delegate { if (_cm.Sessions.Count > 0) { _list.Invalidate(); _hero.Invalidate(); } };
            _tick.Start();
            _shifts.Log += AppendLog;
            _hud.CheckShiftsRequested += delegate { _shifts.CheckNow(); };
            _hud.FocusRequested += delegate(Session s) { FocusSession(s); };
            _hud.HiddenByUser += delegate { _s.HudVisible = false; _rail.HudOn = false; _rail.Invalidate(); RequestSave(); };

            AppendLog("Настройки: " + AppSettings.FilePath);
            if (AppSettings.LoadWarning != null) AppendLog("ВНИМАНИЕ: " + AppSettings.LoadWarning);
            if (_s.RadminPath.Length == 0)
            {
                string how;
                string p = RadminLocator.Detect(out how);
                if (p != null)
                {
                    _s.RadminPath = p;
                    AppendLog("Radmin Viewer " + how);
                    if (_s.PhonebookPath.Length == 0)
                    {
                        var c = PhonebookImport.FindRpbCandidates(p);
                        if (c.Count > 0) { _s.PhonebookPath = c[0]; AppendLog("Найдена телефонная книга: " + c[0]); }
                    }
                    RequestSave();
                }
                else AppendLog("Radmin Viewer не найден - укажите путь в настройках.");
            }
            RefreshConnections();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_s.HudVisible) _hud.Show();
            AppendLog("---- Radmin Grid v" + Updater.Format(Updater.Current) + " запущен ----");
            if (Program.JustUpdated) AppendLog("Обновление установлено: теперь v" + Updater.Format(Updater.Current) + ".");
            if (Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("/autoconnect", StringComparison.OrdinalIgnoreCase)))
            {
                _autoStarted = true;
                AppendLog("Ключ /autoconnect: подключаю отмеченные узлы.");
                ConnectChecked();
            }
            if (_s.UpdateCheck) CheckUpdates(false);
        }

        // =====================================================================  интерфейс

        void BuildUi()
        {
            // ---------- левая панель иконок ----------
            _rail = new NavRail { Dock = DockStyle.Left, Width = 84, HudOn = _s.HudVisible };
            _rail.Add(Theme.IcoLaunch, "Запустить Radmin Viewer", delegate { LaunchRadmin(); }, NavRail.Slot.Top);
            _rail.Add("", "Главная", null, NavRail.Slot.Group);
            _rail.Add(Theme.IcoHud, "HUD поверх окон", delegate { ToggleHud(); }, NavRail.Slot.Group, NavRail.Flag.Hud);
            _rail.Add(Theme.IcoLog, "Журнал", delegate { ToggleLog(); }, NavRail.Slot.Group, NavRail.Flag.Log);
            _rail.Add(Theme.IcoSettings, "Настройки", delegate { OpenSettings(0); }, NavRail.Slot.Group);
            _rail.Add(Theme.IcoShift, "Проверить смены сейчас", delegate { _shifts.CheckNow(); }, NavRail.Slot.Bottom);

            // ---------- журнал ----------
            _logPanel = new Panel { Dock = DockStyle.Bottom, Height = 160, BackColor = Theme.Bg, Padding = new Padding(28, 0, 28, 20), Visible = false };
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = Theme.Mono };
            _log.BackColor = Theme.Surface; _log.ForeColor = Theme.TextDim;
            Theme.DarkScroll(_log);
            var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 8, 12) };
            logCard.Controls.Add(_log);
            _logPanel.Controls.Add(logCard);

            // ---------- баннер ----------
            _hero = new Hero { Dock = DockStyle.Top, Height = 226, Sessions = () => _cm.Sessions, Settings = _s };
            _btnGo = new RButton("ПОДКЛЮЧИТЬ", Theme.IcoPlay, RButton.Kind.Primary) { Font = Theme.Caps, Height = 42 };
            _btnGo.Click += delegate { ConnectChecked(); };
            var btnTune = new RButton("НАСТРОИТЬ", Theme.IcoSettings) { Font = Theme.Caps, Height = 42 };
            btnTune.AutoSizeWidth();
            btnTune.Click += delegate { OpenSettings(1); };
            var btnArr = new RButton("РАССТАВИТЬ", Theme.IcoGrid) { Font = Theme.Caps, Height = 42 };
            btnArr.AutoSizeWidth();
            btnArr.Click += delegate { ArrangeNow(true); };
            _hero.Controls.Add(_btnGo); _hero.Controls.Add(btnTune); _hero.Controls.Add(btnArr);
            _hero.Layout += delegate
            {
                int y = _hero.Height - 42 - 20;
                _btnGo.Location = new Point(34, y);
                btnTune.Location = new Point(_btnGo.Right + 10, y);
                btnArr.Location = new Point(btnTune.Right + 10, y);
            };
            var heroWrap = new Panel { Dock = DockStyle.Top, Height = 226 + 18, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 18) };
            heroWrap.Controls.Add(_hero);

            // ---------- карточки ----------
            var split = _split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 18, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.BackColor = Theme.Bg; split.Panel2.BackColor = Theme.Bg;
            split.Panel1.Controls.Add(BuildBookCard());
            split.Panel2.Controls.Add(BuildConnectionsCard());
            Load += delegate { try { split.SplitterDistance = Math.Max(360, Math.Min(480, Width / 3)); } catch { } };

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(28, 24, 28, 20) };
            content.Controls.Add(split);
            content.Controls.Add(heroWrap);

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            right.Controls.Add(content);
            right.Controls.Add(_logPanel);

            Controls.Add(right);
            Controls.Add(_rail);
        }

        /// <summary>Заголовок карточки как в макете: мелкие заглавные + приглушённая подпись.</summary>
        static Panel CardHead(string title, string subtitle, out Label sub)
        {
            var head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Surface };
            head.Controls.Add(new Label { Text = title.ToUpperInvariant(), Font = Theme.Caps, ForeColor = Theme.Text, AutoSize = true, Location = new Point(0, 2), BackColor = Theme.Surface });
            sub = new Label { Text = subtitle, Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(0, 21), BackColor = Theme.Surface };
            head.Controls.Add(sub);
            return head;
        }
        Control BuildBookCard()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 18) };

            var head = CardHead("Телефонная книга", "", out _lblBookCount);

            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0), Padding = new Padding(0, 2, 0, 6) };
            var scan = new RButton("Из Radmin", Theme.IcoSync, RButton.Kind.Ghost);
            scan.Click += delegate { ScanFromRadmin(); };
            new ToolTip().SetToolTip(scan, "Прочитать папки и узлы из открытого окна Radmin Viewer");
            var imp = new RButton("Импорт", Theme.IcoImport, RButton.Kind.Ghost);
            var impMenu = DarkMenu();
            impMenu.Items.Add("Импорт .rpb (экспериментально)…", null, delegate { ImportRpb(); });
            impMenu.Items.Add("Импорт CSV (Папка;Имя;Адрес;Порт)…", null, delegate { ImportCsv(); });
            impMenu.Items.Add("Экспорт CSV…", null, delegate { ExportCsv(); });
            imp.Click += delegate { impMenu.Show(imp, new Point(0, imp.Height)); };
            tools.Controls.Add(scan); tools.Controls.Add(imp);
            tools.Controls.Add(IconBtn(Theme.IcoAdd, "Добавить узел", delegate { AddEntry(); }));
            tools.Controls.Add(IconBtn(Theme.IcoEdit, "Изменить узел", delegate { EditEntry(); }));
            tools.Controls.Add(IconBtn(Theme.IcoDelete, "Удалить узел или папку", delegate { DeleteSelected(); }));
            tools.Controls.Add(IconBtn(Theme.IcoCheckAll, "Отметить всё (при поиске - найденное)", delegate { CheckAll(true); }));
            tools.Controls.Add(IconBtn(Theme.IcoUncheck, "Снять отметки", delegate { CheckAll(false); }));

            _tbSearch = Theme.NewTextBox("Поиск: имя, IP или папка");
            _tbSearch.TextChanged += delegate { _searchDebounce.Stop(); _searchDebounce.Start(); };
            var search = Theme.Field(_tbSearch, 200);
            search.Dock = DockStyle.Top;

            _tree = new TreeView
            {
                Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true, BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Ui, ShowLines = false, ItemHeight = Theme.Ui.Height + 10
            };
            Theme.DarkScroll(_tree);
            _tree.AfterCheck += TreeAfterCheck;
            _tree.NodeMouseDoubleClick += delegate(object o, TreeNodeMouseClickEventArgs e)
            {
                var en = e.Node.Tag as PbEntry;
                if (en != null) ConnectEntries(new List<PbEntry> { en });
            };
            var treeWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 8) };
            treeWrap.Controls.Add(_tree);

            // быстрый ввод
            _tbQuick = Theme.NewTextBox("Имена, IP или папки через запятую - Enter");
            _tbQuick.Text = _s.QuickTargets;
            _tbQuick.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ConnectQuick(); } };
            var quick = Theme.Field(_tbQuick, 200);
            quick.Dock = DockStyle.Fill;
            var quickBtn = new RButton(null, Theme.IcoPlay) { Dock = DockStyle.Right };
            new ToolTip().SetToolTip(quickBtn, "Подключить введённые");
            quickBtn.Click += delegate { ConnectQuick(); };
            var quickRow = new Panel { Dock = DockStyle.Bottom, Height = quick.Height + 2, BackColor = Theme.Surface };
            quickRow.Controls.Add(quick); quickRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Theme.Surface }); quickRow.Controls.Add(quickBtn);

            card.Controls.Add(treeWrap);
            card.Controls.Add(quickRow);
            card.Controls.Add(search);
            card.Controls.Add(tools);
            card.Controls.Add(head);
            return card;
        }

        Control BuildConnectionsCard()
        {
            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 18) };

            var head = CardHead("Подключения", "Двойной щелчок - показать окно, правый - меню", out _lblConnCount);
            var acts = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            var closeAll = new RButton("Закрыть все", Theme.IcoStop, RButton.Kind.Danger);
            closeAll.Click += delegate { _cm.DisconnectAll(); };
            var retry = new RButton("Повторить", Theme.IcoRefresh);
            new ToolTip().SetToolTip(retry, "Переподключить отвалившиеся и неоткрывшиеся");
            retry.Click += delegate { _cm.ReconnectFailed(); };
            _cmbGridQuick = new Choice(GridLayout.Presets) { Width = 96 };
            int gi = Array.IndexOf(GridLayout.Presets, _s.GridPreset);
            _cmbGridQuick.SelectedIndex = gi >= 0 ? gi : 0;
            _cmbGridQuick.SelectionChangeCommitted += delegate
            {
                _s.GridPreset = _cmbGridQuick.Text;
                GridLayout.CurrentPage = 0;
                RequestSave();
                ArrangeNow(true);
            };
            var gridField = _cmbGridQuick;
            var gridLbl = new Label { Text = "СЕТКА", ForeColor = Theme.TextDim, Font = Theme.Caps, AutoSize = true, Margin = new Padding(8, 12, 0, 0) };
            acts.Controls.Add(closeAll); acts.Controls.Add(retry); acts.Controls.Add(gridField); acts.Controls.Add(gridLbl);
            head.Controls.Add(acts);

            _pages = new PageStrip { Dock = DockStyle.Top, Sessions = () => _cm.Sessions, BackColor = Theme.Surface };
            _pages.TabClicked += OnPageTab;
            var gap1 = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Surface };
            _chips = new ChipBar { Dock = DockStyle.Top, Sessions = () => _cm.Sessions, BackColor = Theme.Surface, ShowShift = _s.ShiftCheck };
            _chips.ChipClicked += delegate { RefreshList(); };
            var gap2 = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Surface };

            _list = new SessionList
            {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ShowShift = _s.ShiftCheck, DefaultPort = _s.DefaultPort,
                EmptyText = "Подключений пока нет.\nОтметьте папки или узлы слева и нажмите «Подключить»."
            };
            var menu = DarkMenu();
            menu.Items.Add("Показать окно", null, delegate { if (_list.MenuSession != null) FocusSession(_list.MenuSession); });
            menu.Items.Add("Переподключить", null, delegate { if (_list.MenuSession != null) _cm.Reconnect(_list.MenuSession); });
            menu.Items.Add("Отключить", null, delegate { if (_list.MenuSession != null) _cm.Disconnect(_list.MenuSession); });
            menu.Items.Add("Убрать из списка", null, delegate { if (_list.MenuSession != null) _cm.Remove(_list.MenuSession); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Проверить смены сейчас", null, delegate { _shifts.CheckNow(); });
            menu.Items.Add("Очистить завершённые", null, delegate { _cm.ClearFinished(); });
            _list.RowMenu = menu;
            _list.Activated += FocusSession;

            card.Controls.Add(_list);
            card.Controls.Add(gap2);
            card.Controls.Add(_chips);
            card.Controls.Add(gap1);
            card.Controls.Add(_pages);
            card.Controls.Add(head);
            return card;
        }

        static RButton IconBtn(string icon, string tip, EventHandler click)
        {
            var b = new RButton(null, icon, RButton.Kind.Ghost);
            b.Click += click;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        internal static ContextMenuStrip DarkMenu()
        {
            return new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ForeColor = Theme.Text, BackColor = Theme.Surface, Font = Theme.Ui, ShowImageMargin = false };
        }

        void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
            _rail.LogOn = _logPanel.Visible;
            _rail.Invalidate();
        }

        // =====================================================================  список подключений

        void RefreshConnections()
        {
            _lblConnCount.Text = _cm.Sessions.Count == 0 ? "Двойной щелчок - показать окно, правый - меню" :
                _cm.Sessions.Count(x => x.Status == SessStatus.Connected) + " из " + _cm.Sessions.Count + " подключено  ·  двойной щелчок - показать окно";
            if (_pages.Selected >= GridLayout.PageCount) _pages.Selected = -1;
            _pages.Invalidate();
            _chips.Invalidate();
            _hero.Invalidate();
            RefreshList();
        }

        void RefreshList()
        {
            IEnumerable<Session> q = _cm.Sessions;
            if (_pages.Selected >= 0)
                q = q.Where(x => { int p; return GridLayout.PageOf.TryGetValue(x, out p) && p == _pages.Selected; });
            if (_chips.Active >= 0) q = q.Where(StatusGroup.All[_chips.Active].Match);
            _list.SetItems(q.OrderBy(x => Rank(x.Status)).ThenBy(x => x.Order).ToList());
        }

        static int Rank(SessStatus st)
        {
            switch (st)
            {
                case SessStatus.Dropped: case SessStatus.Failed: case SessStatus.Unreachable: case SessStatus.AuthFailed: return 0;
                case SessStatus.WaitingAuth: return 1;
                case SessStatus.Connected: return 3;
                case SessStatus.Closed: return 4;
                default: return 2;
            }
        }

        void OnPageTab(int tab)
        {
            _pages.Selected = tab;
            if (tab >= 0) SetPage(tab, true);
            RefreshConnections();
        }

        // =====================================================================  настройки

        void OpenSettings(int section)
        {
            string themeBefore = _s.UiTheme;
            ReleaseInfo install;
            using (var f = new SettingsForm(_s, delegate { _shifts.CheckNow(); }, section))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                install = f.InstallRelease;
            }
            SaveNow();
            if (_s.UiTheme != themeBefore) ReapplyTheme();
            _hud.ApplySettings();
            if (_s.HudVisible && !_hud.Visible) _hud.Show();
            if (!_s.HudVisible && _hud.Visible) _hud.Hide();
            _rail.HudOn = _hud.Visible; _rail.Invalidate();
            int gi = Array.IndexOf(GridLayout.Presets, _s.GridPreset);
            _cmbGridQuick.SelectedIndex = gi >= 0 ? gi : 0;
            _chips.ShowShift = _list.ShowShift = _s.ShiftCheck;
            _list.DefaultPort = _s.DefaultPort;
            RebuildTree();
            ArrangeNow(false);
            _cm.NotifyChanged();
            AppendLog("Настройки сохранены.");
            if (install != null) OfferUpdate(install, false);
        }

        /// <summary>Сменить тему на лету: окно перестраивается на новой палитре, подключения не трогаются.</summary>
        void ReapplyTheme()
        {
            Theme.Apply(_s.UiTheme);
            string logText = _log.Text;
            bool logVisible = _logPanel.Visible;
            int split = _split.SplitterDistance;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Theme.ApplyTitleBar(this);
            // Windows 10 перерисовывает цвет заголовка только при изменении окна - толкаем рамку на 1 px
            if (WindowState == FormWindowState.Normal) { Width += 1; Width -= 1; }
            else Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            BuildUi();
            ResumeLayout(true);
            _log.Text = logText;
            _logPanel.Visible = logVisible;
            _rail.LogOn = logVisible;
            try { _split.SplitterDistance = split; } catch { }
            RebuildTree();
            RefreshConnections();
            AppendLog("Тема: " + (_s.UiTheme == "light" ? "светлая" : _s.UiTheme == "system" ? "как в Windows" : "тёмная") + ".");
        }

        void RequestSave()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        void SaveNow()
        {
            _saveDebounce.Stop();
            _s.QuickTargets = _tbQuick.Text;
            _s.HudVisible = _hud.Visible;
            if (_hud.Visible) { _s.HudX = _hud.Left; _s.HudY = _hud.Top; }
            if (WindowState == FormWindowState.Normal) { _s.WinX = Left; _s.WinY = Top; _s.WinW = Width; _s.WinH = Height; }
            try { _s.Save(); }
            catch (Exception ex) { AppendLog("Не удалось сохранить настройки: " + ex.Message); }
        }

        void LoadWindowBounds()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var b = new Rectangle(_s.WinX, _s.WinY, Math.Max(1060, _s.WinW), Math.Max(680, _s.WinH));
            if (_s.WinX < 0 || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(b)))
                b = new Rectangle(wa.X + (wa.Width - b.Width) / 2, wa.Y + (wa.Height - b.Height) / 2, Math.Min(b.Width, wa.Width), Math.Min(b.Height, wa.Height));
            Bounds = b;
        }

        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); RequestSave(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            int live = _cm.Sessions.Count(x => x.IsLive);
            if (live > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "Открыто подключений: " + live + ".\nЗакрыть их вместе с приложением?",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes) _cm.DisconnectAll();
            }
            SaveNow();
            // окна, которые остаются открытыми: вернуть рамки и вытащить со скрытых вкладок
            var vs = SystemInformation.VirtualScreen;
            foreach (var s in _cm.Sessions.Where(x => x.Hwnd != IntPtr.Zero && Native.IsWindow(x.Hwnd)))
            {
                if (s.StyleChanged) GridLayout.ApplyBorder(s, false);
                if (GridLayout.IsParked(s.Hwnd))
                {
                    var r = Native.GetRect(s.Hwnd);
                    Native.SetWindowPos(s.Hwnd, IntPtr.Zero, vs.X + 40, vs.Y + 40, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
            }
            _shifts.Dispose();
            _cm.Dispose();
            if (_pendingUpdate != null && e.CloseReason != CloseReason.WindowsShutDown)
            {
                try
                {
                    Updater.InstallAndRestart(_pendingUpdate);
                    AppendLog("Новая версия установлена, запускаю её.");
                }
                catch (Exception ex)
                {
                    AppendLog("ОШИБКА установки обновления: " + ex.Message);
                    MessageBox.Show(this, "Не удалось установить обновление:\n\n" + ex.Message + "\n\nПрежняя версия осталась на месте.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            base.OnFormClosing(e);
        }

        void AppendLog(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), msg); return; }
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            if (_log.TextLength > 200000) _log.Text = _log.Text.Substring(_log.TextLength - 100000);
            _log.AppendText(line);
            if (_hero != null) { _hero.LastLog = msg; _hero.Invalidate(); }
            try
            {
                string lf = Path.ChangeExtension(AppSettings.FilePath, ".log");
                if (File.Exists(lf) && new FileInfo(lf).Length > 2000000) File.Delete(lf);
                File.AppendAllText(lf, DateTime.Now.ToString("yyyy-MM-dd ") + line);
            }
            catch { }
        }

        void Error(string msg)
        {
            AppendLog("ОШИБКА: " + msg.Replace("\n", " "));
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
