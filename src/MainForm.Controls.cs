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
    internal partial class MainForm
    {
        // =====================================================================  элементы оформления

        /// <summary>Панель иконок слева в духе MISTY: логотип, отдельная кнопка сверху, группа в «таблетке», кнопка внизу.</summary>
        class NavRail : PaintedControl
        {
            public enum Slot { Top, Group, Bottom }
            public enum Flag { None, Hud, Log }
            class Item { public string Icon, Tip; public Action Click; public Slot Slot; public Flag Flag; public Rectangle R; }
            readonly List<Item> _items = new List<Item>();
            readonly ToolTip _tip = new ToolTip();
            static readonly Font Logo = new Font(Theme.Display.FontFamily, 20f, Theme.Display.Style);
            static Font _ico;
            int _hover = -1;
            public bool HudOn, LogOn;

            public NavRail() { BackColor = Theme.Rail; Cursor = Cursors.Hand; }

            static Font Ico { get { return _ico ?? (_ico = new Font(Theme.Icons.FontFamily, 13f)); } }

            public void Add(string icon, string tip, Action click, Slot slot, Flag flag = Flag.None)
            {
                _items.Add(new Item { Icon = icon, Tip = tip, Click = click, Slot = slot, Flag = flag });
            }

            void DoLayout()
            {
                int s = 46, cx = (Width - s) / 2;
                var group = _items.Where(x => x.Slot == Slot.Group).ToList();
                int groupH = group.Count * s + 16;
                int gy = Math.Max(150, (Height - groupH) / 2);
                int gi = 0;
                foreach (var it in _items)
                {
                    if (it.Slot == Slot.Top) it.R = new Rectangle(cx, gy - s - 20, s, s);
                    else if (it.Slot == Slot.Bottom) it.R = new Rectangle(cx, Height - s - 26, s, s);
                    else it.R = new Rectangle(cx, gy + 8 + (gi++) * s, s, s);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                DoLayout();
                var g = e.Graphics;
                g.Clear(Theme.Rail);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                TextRenderer.DrawText(g, "R", Logo, new Rectangle(0, 20, Width, 44), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                var group = _items.Where(x => x.Slot == Slot.Group).ToList();
                if (group.Count > 0)
                {
                    var gr = Rectangle.Union(group.First().R, group.Last().R);
                    gr.Inflate(0, 8);
                    using (var path = Theme.Round(gr, gr.Width / 2))
                    using (var b = new SolidBrush(Theme.Surface))
                        g.FillPath(b, path);
                }
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    bool active = (it.Flag == Flag.Hud && HudOn) || (it.Flag == Flag.Log && LogOn) || (it.Slot == Slot.Group && it.Click == null);
                    if (it.Slot != Slot.Group)
                        using (var b = new SolidBrush(i == _hover ? Theme.Hover : Theme.Surface)) g.FillEllipse(b, it.R);
                    else if (i == _hover)
                        using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, Rectangle.Inflate(it.R, -3, -3));
                    TextRenderer.DrawText(g, it.Icon, Ico, it.R, active ? Theme.Accent : (i == _hover ? Theme.Text : Theme.TextDim),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            int Hit(Point p)
            {
                for (int i = 0; i < _items.Count; i++) if (_items[i].R.Contains(p)) return i;
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = Hit(e.Location);
                if (h == _hover) return;
                _hover = h;
                Invalidate();
                _tip.SetToolTip(this, h >= 0 ? _items[h].Tip : "");
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int h = Hit(e.Location);
                if (h >= 0 && _items[h].Click != null) _items[h].Click();
            }
        }

        /// <summary>Баннер: пилюли статистики сверху, крупный заголовок, описание и кнопки-«таблетки».</summary>
        class Hero : Panel
        {
            public Func<IEnumerable<Session>> Sessions;
            public AppSettings Settings;
            public string Selection = "";
            public string LastLog = "";
            static readonly Font PillNum = new Font("Segoe UI Semibold", 10.5f);

            public Hero()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Surface;
            }

            /// <summary>Пилюля «подпись сверху, значение снизу», как «Сейчас играют: 2975».</summary>
            int StatPill(Graphics g, int x, int y, string caption, string icon, string value, Color valueColor)
            {
                int w = Math.Max(TextRenderer.MeasureText(caption, Theme.Small).Width, TextRenderer.MeasureText(value, PillNum).Width + 22) + 36;
                var r = new Rectangle(x, y, w, 50);
                using (var path = Theme.Round(r, 25))
                using (var b = new SolidBrush(Theme.Pill))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, caption, Theme.Small, new Point(x + 18, y + 7), Theme.TextDim, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, icon, Theme.Icons, new Point(x + 18, y + 27), Theme.Ink(valueColor), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, value, PillNum, new Point(x + 38, y + 24), valueColor == Theme.TextDim ? Theme.Text : Theme.Ink(valueColor), TextFormatFlags.NoPadding);
                return w;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = Theme.Round(r, 16))
                {
                    using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                    // приглушённое фиолетовое свечение, как подсветка фона в макете
                    using (var glow = new GraphicsPath())
                    {
                        glow.AddEllipse(Width - 640, -260, 820, 620);
                        using (var pb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(46, Theme.Accent), SurroundColors = new[] { Color.FromArgb(0, Theme.Accent) } })
                        {
                            g.SetClip(path);
                            g.FillPath(pb, glow);
                            g.ResetClip();
                        }
                    }
                }

                var list = Sessions != null ? Sessions().ToList() : new List<Session>();
                int connected = list.Count(StatusGroup.All[0].Match);
                int busy = list.Count(StatusGroup.All[1].Match) + list.Count(StatusGroup.All[2].Match);
                int bad = list.Count(StatusGroup.All[3].Match) + list.Count(StatusGroup.All[4].Match);

                // пилюли статистики
                int x = 30, y = 20;
                x += StatPill(g, x, y, "Сейчас подключено", "", connected + (list.Count > 0 ? " из " + list.Count : ""), connected > 0 ? StatusGroup.All[0].Color : Theme.TextDim) + 10;
                if (busy > 0) x += StatPill(g, x, y, "В процессе", "", busy.ToString(), StatusGroup.All[1].Color) + 10;
                if (bad > 0) x += StatPill(g, x, y, "Проблемы", "", bad.ToString(), StatusGroup.All[4].Color) + 10;
                if (Settings.ShiftCheck)
                {
                    int open = list.Count(StatusGroup.All[5].Match), closed = list.Count(StatusGroup.All[6].Match);
                    x += StatPill(g, x, y, "Смены открыты", Theme.IcoShift, open.ToString(), open > 0 ? StatusGroup.ShiftOpen : Theme.TextDim) + 10;
                    x += StatPill(g, x, y, "Смены закрыты", Theme.IcoShift, closed.ToString(), closed > 0 ? StatusGroup.ShiftClosed : Theme.TextDim) + 10;
                }

                // последнее событие - справа сверху, как «Загрузка…» в макете
                if (LastLog.Length > 0)
                {
                    int w = Math.Min(420, Math.Max(200, Width - x - 60));
                    if (w >= 200)
                    {
                        var lr = new Rectangle(Width - w - 28, y, w, 50);
                        using (var path = Theme.Round(lr, 25))
                        using (var b = new SolidBrush(Theme.Pill))
                            g.FillPath(b, path);
                        TextRenderer.DrawText(g, "Последнее событие", Theme.Small, new Point(lr.X + 20, lr.Y + 7), Theme.TextDim, TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, LastLog, Theme.Small, new Rectangle(lr.X + 20, lr.Y + 26, lr.Width - 36, 18), Theme.Text,
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    }
                }

                // заголовок и описание
                TextRenderer.DrawText(g, "RADMIN GRID", Theme.Display, new Point(28, 80), Theme.Text, TextFormatFlags.NoPadding);
                string desc;
                if (Settings.RadminPath.Length == 0) desc = "Radmin Viewer не указан - нажмите «Настроить».";
                else if (!File.Exists(Settings.RadminPath)) desc = "Radmin Viewer не найден: " + Settings.RadminPath;
                else desc = Selection + "   ·   " + ModeNames[(int)Settings.Mode] + ", " + (Settings.GridPreset == "Авто" ? "сетка авто" : "сетка " + Settings.GridPreset) +
                            (Settings.ShiftCheck ? ", смены каждые " + Settings.ShiftIntervalMin + " мин" : "");
                TextRenderer.DrawText(g, desc, Theme.Small, new Rectangle(32, 136, Width - 64, 18),
                    File.Exists(Settings.RadminPath) ? Theme.TextDim : Theme.Warn, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }
}
