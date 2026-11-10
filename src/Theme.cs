using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Тёмная тема: палитра, шрифты, иконки Segoe MDL2 и оформление стандартных контролов.</summary>
    internal static class Theme
    {
        // Палитра в духе лаунчера MISTY: нейтральный почти чёрный фон, приглушённые карточки, фиолетовый акцент
        // Палитра задаётся Apply(): тёмная (в духе MISTY) или светлая. Цвета читаются при построении окон.
        public static Color Bg, Rail, Surface, Surface2, Hover, Border, Text, TextDim, Accent, AccentHover, AccentDark, OnAccent, Danger;
        /// <summary>Фон строки списка подключений и «пилюль» баннера.</summary>
        public static Color Row, Pill, Warn;
        public static bool IsDark = true;

        static Theme() { Apply("dark"); }

        /// <summary>"dark", "light" или "system" (как в Windows: параметр «Режим приложений» текущего пользователя).</summary>
        public static void Apply(string mode)
        {
            IsDark = mode == "light" ? false : mode == "system" ? !WindowsUsesLightTheme() : true;
            if (IsDark)
            {
                Bg = Color.FromArgb(19, 19, 23); Rail = Color.FromArgb(15, 15, 18);
                Surface = Color.FromArgb(27, 27, 32); Surface2 = Color.FromArgb(35, 35, 41);
                Hover = Color.FromArgb(45, 44, 53); Border = Color.FromArgb(44, 43, 52);
                Text = Color.FromArgb(238, 238, 242); TextDim = Color.FromArgb(128, 128, 142);
                Accent = Color.FromArgb(125, 95, 230); AccentHover = Color.FromArgb(146, 120, 242); AccentDark = Color.FromArgb(60, 45, 120);
                Row = Color.FromArgb(31, 31, 37); Pill = Color.FromArgb(34, 34, 40);
                Danger = Color.FromArgb(229, 83, 75); Warn = Color.FromArgb(255, 170, 120);
            }
            else
            {
                Bg = Color.FromArgb(242, 242, 247); Rail = Color.FromArgb(232, 232, 239);
                Surface = Color.White; Surface2 = Color.FromArgb(238, 238, 244);
                Hover = Color.FromArgb(226, 225, 236); Border = Color.FromArgb(220, 219, 230);
                Text = Color.FromArgb(26, 26, 34); TextDim = Color.FromArgb(108, 108, 124);
                Accent = Color.FromArgb(110, 80, 220); AccentHover = Color.FromArgb(128, 100, 234); AccentDark = Color.FromArgb(214, 205, 250);
                Row = Color.FromArgb(247, 247, 251); Pill = Color.FromArgb(240, 239, 247);
                Danger = Color.FromArgb(205, 55, 50); Warn = Color.FromArgb(200, 100, 30);
            }
            OnAccent = Color.White;
        }

        /// <summary>Цвет статуса для текста: в светлой теме затемняется, чтобы читался на белом.</summary>
        public static Color Ink(Color c)
        {
            if (IsDark) return c;
            return Color.FromArgb((int)(c.R * 0.72), (int)(c.G * 0.72), (int)(c.B * 0.72));
        }

        static bool WindowsUsesLightTheme()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        public static readonly Font Ui = new Font("Segoe UI", 9.5f);
        public static readonly Font UiBold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 13f);
        public static readonly Font Section = new Font("Segoe UI Semibold", 11f);
        /// <summary>Мелкие заглавные подписи кнопок и заголовков карточек.</summary>
        public static readonly Font Caps = new Font("Segoe UI Semibold", 8.5f);
        /// <summary>Крупный жирный заголовок баннера (как «MAGIC»).</summary>
        public static readonly Font Display = CreateDisplay();

        static Font CreateDisplay()
        {
            var f = new Font("Segoe UI Black", 30f);
            return f.Name == "Segoe UI Black" ? f : new Font("Segoe UI", 30f, FontStyle.Bold);
        }
        public static readonly Font Mono = new Font("Consolas", 9f);

        static Font _icons;
        /// <summary>Шрифт иконок Windows 10 (Segoe MDL2 Assets); если его нет - обычный шрифт.</summary>
        public static Font Icons
        {
            get
            {
                if (_icons == null)
                {
                    var f = new Font("Segoe MDL2 Assets", 10f);
                    _icons = f.Name == "Segoe MDL2 Assets" ? f : new Font("Segoe UI Symbol", 10f);
                }
                return _icons;
            }
        }

        // Иконки Segoe MDL2 Assets
        public const string IcoPlay = "", IcoSettings = "", IcoRefresh = "", IcoGrid = "",
            IcoStop = "", IcoAdd = "", IcoEdit = "", IcoDelete = "", IcoSearch = "",
            IcoSync = "", IcoImport = "", IcoCheckAll = "", IcoUncheck = "", IcoHud = "",
            IcoLaunch = "", IcoFolder = "", IcoLog = "", IcoChevronUp = "", IcoChevronDown = "",
            IcoShift = "", IcoInfo = "", IcoKey = "", IcoPlug = "", IcoEye = "";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        /// <summary>Заголовок окна под тему (Windows 10 20H1+; на старых сборках - атрибут 19).</summary>
        public static void DarkTitleBar(Form f)
        {
            f.HandleCreated += delegate { ApplyTitleBar(f); };
        }

        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int on = IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }

        /// <summary>Тёмные полосы прокрутки и значки у стандартных контролов (TreeView, TextBox).</summary>
        public static void DarkScroll(Control c)
        {
            if (c.IsHandleCreated) Apply(c);
            else c.HandleCreated += delegate { Apply(c); };
        }

        static void Apply(Control c)
        {
            try { SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        public static void StyleForm(System.Windows.Forms.Form f)
        {
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.Font = Ui;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            if (AppIcon != null) f.Icon = AppIcon;
            DarkTitleBar(f);
        }

        static Icon _appIcon;
        static bool _appIconLoaded;

        /// <summary>Значок программы из exe (src\app.ico); null, если достать не удалось.</summary>
        public static Icon AppIcon
        {
            get
            {
                if (!_appIconLoaded)
                {
                    _appIconLoaded = true;
                    try { _appIcon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                    catch { }
                }
                return _appIcon;
            }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------- поля ввода ----------------

        /// <summary>Поле ввода в «карточке» со скруглённой рамкой: TextBox/NumericUpDown/ComboBox внутри панели.</summary>
        public static Control Field(Control inner, int width)
        {
            var host = new FieldHost { Width = width, Height = inner.PreferredSize.Height + 10, Margin = new Padding(3, 3, 3, 3) };
            inner.BackColor = Surface2;
            inner.ForeColor = Text;
            inner.Font = Ui;
            var tb = inner as TextBox;
            if (tb != null) tb.BorderStyle = BorderStyle.None;
            var nud = inner as NumericUpDown;
            if (nud != null) nud.BorderStyle = BorderStyle.None;
            var cb = inner as ComboBox;
            if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            inner.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, cb != null ? 2 : 5, 10, 2);
            host.Controls.Add(inner);
            host.Inner = inner;
            inner.GotFocus += delegate { host.Focused2 = true; host.Invalidate(); };
            inner.LostFocus += delegate { host.Focused2 = false; host.Invalidate(); };
            return host;
        }

        class FieldHost : Panel
        {
            public Control Inner;
            public bool Focused2;
            public FieldHost() { DoubleBuffered = true; BackColor = Surface2; }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent != null ? Parent.BackColor : Bg);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = Round(r, Math.Min(Height / 2, 12)))
                using (var b = new SolidBrush(Surface2))
                using (var p = new Pen(Focused2 ? Accent : Surface2))
                {
                    e.Graphics.FillPath(b, path);
                    e.Graphics.DrawPath(p, path);
                }
            }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Inner != null) Inner.Focus(); }
        }

        public static TextBox NewTextBox(string cue = null)
        {
            var t = new TextBox();
            if (cue != null) SetCue(t, cue);
            return t;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        /// <summary>Серый текст-подсказка в пустом поле.</summary>
        public static void SetCue(TextBox t, string cue)
        {
            t.HandleCreated += delegate { SendMessage(t.Handle, 0x1501 /*EM_SETCUEBANNER*/, new IntPtr(1), cue); };
        }

        public static Label NewLabel(string text, bool dim = false)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = dim ? TextDim : Text, Font = dim ? Small : Ui, Margin = new Padding(3, 8, 3, 3), BackColor = Color.Transparent };
        }
    }

    /// <summary>Кнопка со скруглёнными углами, иконкой Segoe MDL2 и вариантами оформления.</summary>
    internal class RButton : Control
    {
        public enum Kind { Primary, Secondary, Ghost, Danger }

        public Kind Style = Kind.Secondary;
        public string Icon;
        bool _hover, _down;
        public bool Toggled;       // для кнопок-переключателей (например, HUD)

        public RButton(string text, string icon = null, Kind kind = Kind.Secondary)
        {
            Text = text; Icon = icon; Style = kind;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Style == Kind.Primary ? Theme.UiBold : Theme.Ui;
            if (kind == Kind.Primary) ForeColor = Theme.OnAccent;
            Cursor = Cursors.Hand;
            Margin = new Padding(3);
            Height = Theme.Ui.Height + 14;
            AutoSizeWidth();
        }

        public Font IconFont = Theme.Icons;

        public void PerformClick() { OnClick(EventArgs.Empty); }

        public void AutoSizeWidth()
        {
            bool hasText = !string.IsNullOrEmpty(Text);
            if (!hasText) { Width = Height; return; }
            int w = 30 + TextRenderer.MeasureText(Text, Font).Width;
            if (!string.IsNullOrEmpty(Icon)) w += Height - 12 + 4;
            Width = w;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AutoSizeWidth(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg, fg = Theme.Text, border = Color.Empty;
            switch (Style)
            {
                case Kind.Primary:
                    bg = _down ? Theme.Accent : _hover ? Theme.AccentHover : Theme.Accent;
                    fg = Theme.OnAccent;
                    break;
                case Kind.Danger:
                    bg = _hover ? Color.FromArgb(80, Theme.Danger) : Theme.Surface2;
                    fg = _hover ? Color.White : (Theme.IsDark ? Color.FromArgb(255, 150, 140) : Theme.Danger);
                    break;
                case Kind.Ghost:
                    bg = Toggled ? Color.FromArgb(60, Theme.Accent) : _down ? Theme.Surface2 : _hover ? Theme.Hover : Color.Transparent;
                    break;
                default:
                    bg = Toggled ? Color.FromArgb(70, Theme.Accent) : _down ? Theme.Surface2 : _hover ? Theme.Hover : Theme.Surface2;
                    break;
            }
            if (!Enabled) { fg = Theme.TextDim; if (Style == Kind.Primary) bg = Theme.Surface2; }
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Height / 2))
            {
                if (bg != Color.Transparent) using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                if (border != Color.Empty) using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            int x = 10;
            bool hasText = !string.IsNullOrEmpty(Text);
            if (!string.IsNullOrEmpty(Icon))
            {
                var isz = TextRenderer.MeasureText(Icon, IconFont);
                if (hasText)
                {
                    // иконка в полупрозрачном кружке слева
                    int d = Height - 12;
                    var c = new Rectangle(6, 6, d, d);
                    using (var b = new SolidBrush(Style == Kind.Primary ? Color.FromArgb(50, Color.White) : Color.FromArgb(Theme.IsDark ? 30 : 22, Theme.IsDark ? Color.White : Theme.Text))) g.FillEllipse(b, c);
                    TextRenderer.DrawText(g, Icon, IconFont, c, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x = c.Right + 8;
                }
                else
                    TextRenderer.DrawText(g, Icon, IconFont, new Rectangle(0, 0, Width, Height), fg,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (hasText)
            {
                if (string.IsNullOrEmpty(Icon)) x = 15;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - 10, Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>Тёмный выпадающий список-«таблетка» вместо ComboBox (стандартный в тёмной теме рисуется белым).</summary>
    internal class Choice : RButton
    {
        public readonly List<string> Items = new List<string>();
        int _index = -1;
        public event EventHandler SelectedIndexChanged;
        /// <summary>Только выбор пользователя (не программная установка).</summary>
        public event EventHandler SelectionChangeCommitted;

        public Choice(params string[] items) : base("", null)
        {
            Items.AddRange(items);
            Width = 200;
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (value == _index) return;
                _index = value;
                base.Text = value >= 0 && value < Items.Count ? Items[value] : "";
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public override string Text { get { return base.Text; } set { int i = Items.IndexOf(value); if (i >= 0) SelectedIndex = i; } }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); }   // ширину задаёт владелец

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var m = MainForm.DarkMenu();
            for (int i = 0; i < Items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(Items[i]) { Checked = i == _index };
                it.Click += delegate
                {
                    SelectedIndex = k;
                    if (SelectionChangeCommitted != null) SelectionChangeCommitted(this, EventArgs.Empty);
                };
                m.Items.Add(it);
            }
            m.ShowImageMargin = true;
            m.MinimumSize = new Size(Width, 0);
            m.Show(this, new Point(0, Height + 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, Theme.IcoChevronDown, new Font(Theme.Icons.FontFamily, 8f), new Rectangle(Width - 28, 0, 20, Height),
                Enabled ? Theme.TextDim : Theme.Border, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Переключатель в стиле Windows 11 вместо флажка.</summary>
    internal class Toggle : CheckBox
    {
        public Toggle(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AutoSize = false;
            Font = Theme.Ui;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            Height = Theme.Ui.Height + 12;
            Width = TextRenderer.MeasureText(text, Theme.Ui).Width + 60;
            Margin = new Padding(3, 4, 3, 4);
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = 20, w = 40, y = (Height - h) / 2;
            var track = new Rectangle(1, y, w, h);
            Color on = Enabled ? Theme.Accent : Theme.Border;
            using (var path = Theme.Round(track, h / 2))
            {
                if (Checked) using (var b = new SolidBrush(on)) g.FillPath(b, path);
                else using (var p = new Pen(Theme.TextDim, 1.5f)) g.DrawPath(p, path);
            }
            int k = h - 8;
            int kx = Checked ? track.Right - k - 4 : track.X + 4;
            using (var b = new SolidBrush(Checked ? Color.White : Theme.TextDim)) g.FillEllipse(b, kx, y + 4, k, k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(w + 12, 0, Width - w - 12, Height), Enabled ? ForeColor : Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Панель-карточка со скруглёнными углами.</summary>
    internal class Card : Panel
    {
        public Card()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Padding = new Padding(12);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 14))
            using (var b = new SolidBrush(Theme.Surface))
                e.Graphics.FillPath(b, path);
        }
    }
}
