using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Группы статусов - счётчики-фильтры на главном экране.</summary>
    internal class StatusGroup
    {
        public string Title;
        public Color Color;
        public Func<Session, bool> Match;
        public bool Shift;

        public static readonly Color ShiftOpen = Color.FromArgb(26, 188, 156);
        public static readonly Color ShiftClosed = Color.FromArgb(155, 89, 182);

        public static readonly StatusGroup[] All =
        {
            new StatusGroup { Title = "Подключено", Color = HudForm.StatusColor(SessStatus.Connected), Match = x => x.Status == SessStatus.Connected },
            new StatusGroup { Title = "В процессе", Color = HudForm.StatusColor(SessStatus.Connecting),
                              Match = x => x.IsBusy && x.Status != SessStatus.Connected && x.Status != SessStatus.WaitingAuth && x.Status != SessStatus.AuthFailed },
            new StatusGroup { Title = "Ждут пароль", Color = HudForm.StatusColor(SessStatus.WaitingAuth), Match = x => x.Status == SessStatus.WaitingAuth },
            new StatusGroup { Title = "Отвалились", Color = HudForm.StatusColor(SessStatus.Dropped), Match = x => x.Status == SessStatus.Dropped },
            new StatusGroup { Title = "Не открылись", Color = HudForm.StatusColor(SessStatus.Failed),
                              Match = x => x.Status == SessStatus.Failed || x.Status == SessStatus.Unreachable || x.Status == SessStatus.AuthFailed },
            new StatusGroup { Title = "Смена открыта", Color = ShiftOpen, Shift = true, Match = x => x.Status == SessStatus.Connected && x.ShiftNo > 0 },
            new StatusGroup { Title = "Смена закрыта", Color = ShiftClosed, Shift = true, Match = x => x.Status == SessStatus.Connected && x.ShiftNo == 0 },
        };
    }

    /// <summary>Базовый контрол с двойной буферизацией и фоном родителя.</summary>
    internal abstract class PaintedControl : Control
    {
        protected PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Ui;
            ForeColor = Theme.Text;
        }

        protected void Pill(Graphics g, Rectangle r, Color fill, Color border)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, r.Height / 2))
            {
                if (fill != Color.Empty) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty) using (var p = new Pen(border)) g.DrawPath(p, path);
            }
        }
    }

    /// <summary>Вкладки окон: «Все» + 1, 2, 3… Фиолетовая точка - на вкладке есть закрытая смена.</summary>
    internal class PageStrip : PaintedControl
    {
        readonly List<Rectangle> _rects = new List<Rectangle>();
        int _hover = -2;
        public int Selected = -1;               // -1 - «Все»
        public Func<IEnumerable<Session>> Sessions;
        public event Action<int> TabClicked;
        readonly ToolTip _tip = new ToolTip();

        public PageStrip() { Height = Theme.Ui.Height + 16; Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            _rects.Clear();
            int x = 0, h = Height - 4;
            var counts = new int[Math.Max(1, GridLayout.PageCount)];
            var closed = new bool[counts.Length];
            foreach (var kv in GridLayout.PageOf)
            {
                if (kv.Value >= counts.Length) continue;
                counts[kv.Value]++;
                if (kv.Key.ShiftNo == 0 && kv.Key.Status == SessStatus.Connected) closed[kv.Value] = true;
            }
            int total = Sessions != null ? Sessions().Count() : 0;
            for (int i = -1; i < GridLayout.PageCount; i++)
            {
                if (i >= 0 && GridLayout.PageCount <= 1) break;
                string label = i < 0 ? "Все  " + total : "Вкладка " + (i + 1) + "  " + counts[i];
                int w = TextRenderer.MeasureText(label, Theme.UiBold).Width + 22;
                var r = new Rectangle(x, 2, w, h);
                _rects.Add(r);
                bool sel = Selected == i;
                bool live = i == GridLayout.CurrentPage && i >= 0;
                Pill(g, r, sel ? Theme.Accent : _hover == i ? Theme.Hover : Theme.Surface2, Color.Empty);
                TextRenderer.DrawText(g, label, sel ? Theme.UiBold : Theme.Ui, r, sel ? Theme.OnAccent : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (i >= 0 && closed[i]) using (var b = new SolidBrush(StatusGroup.ShiftClosed)) g.FillEllipse(b, r.Right - 10, r.Y + 3, 7, 7);
                if (live && !sel) using (var p = new Pen(Theme.Accent, 2)) g.DrawLine(p, r.X + 12, r.Bottom - 1, r.Right - 12, r.Bottom - 1);
                x += w + 6;
            }
        }

        int Hit(Point p)
        {
            for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(p)) return i - 1;
            return -2;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h == _hover) return;
            _hover = h;
            Invalidate();
            _tip.SetToolTip(this, h == -1 ? "Все подключения" :
                h >= 0 ? "Показать окна вкладки " + (h + 1) + " на экране (Ctrl+Alt+" + (h + 1) + ")" : "");
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -2; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location);
            if (h >= -1 && e.Button == MouseButtons.Left && TabClicked != null) TabClicked(h);
        }
    }

    /// <summary>Счётчики статусов; щелчок - фильтр списка.</summary>
    internal class ChipBar : PaintedControl
    {
        readonly List<KeyValuePair<Rectangle, int>> _rects = new List<KeyValuePair<Rectangle, int>>();
        public int Active = -1;
        public bool ShowShift = true;
        public Func<IEnumerable<Session>> Sessions;
        public event Action<int> ChipClicked;

        public ChipBar() { Height = Theme.Small.Height + 14; Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            _rects.Clear();
            var list = Sessions != null ? Sessions().ToList() : new List<Session>();
            int x = 0, h = Height - 4;
            for (int i = 0; i < StatusGroup.All.Length; i++)
            {
                var gr = StatusGroup.All[i];
                if (gr.Shift && !ShowShift) continue;
                int n = list.Count(gr.Match);
                string label = gr.Title + "  " + n;
                int w = TextRenderer.MeasureText(label, Theme.Small).Width + 26;
                var r = new Rectangle(x, 2, w, h);
                _rects.Add(new KeyValuePair<Rectangle, int>(r, i));
                bool act = Active == i;
                Pill(g, r, act ? Color.FromArgb(70, gr.Color) : Color.Empty, act ? gr.Color : Theme.Border);
                using (var b = new SolidBrush(n > 0 ? gr.Color : Color.FromArgb(90, gr.Color)))
                {
                    if (gr.Shift) g.FillRectangle(b, r.X + 9, r.Y + (h - 7) / 2, 7, 7);
                    else g.FillEllipse(b, r.X + 9, r.Y + (h - 8) / 2, 8, 8);
                }
                TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(r.X + 20, r.Y, w - 22, h), n > 0 ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += w + 6;
                if (x > Width) break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            foreach (var kv in _rects)
                if (kv.Key.Contains(e.Location))
                {
                    Active = Active == kv.Value ? -1 : kv.Value;
                    Invalidate();
                    if (ChipClicked != null) ChipClicked(Active);
                    return;
                }
        }
    }

    /// <summary>Список подключений: две строки на подключение, прокрутка колесом и полосой.</summary>
    internal class SessionList : PaintedControl
    {
        List<Session> _items = new List<Session>();
        int _scroll, _hover = -1, _selected = -1;
        int _rowH;
        Rectangle _track, _thumb;
        bool _drag;
        int _dragOff;
        readonly ToolTip _tip = new ToolTip { InitialDelay = 400 };
        string _lastTip;

        public string EmptyText = "Нет подключений";
        public bool ShowShift = true;
        public int DefaultPort = 4899;
        public ContextMenuStrip RowMenu;
        public Session MenuSession;
        public event Action<Session> Activated;

        public SessionList()
        {
            _rowH = Theme.Ui.Height + Theme.Small.Height + 18;
        }

        public void SetItems(List<Session> items)
        {
            _items = items;
            Clamp();
            Invalidate();
        }

        int VisibleRows { get { return Math.Max(1, Height / _rowH); } }

        void Clamp() { _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, _items.Count - VisibleRows))); }

        static string Elapsed(DateTime since)
        {
            var t = DateTime.Now - since;
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Theme.Ui, ClientRectangle, Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            bool sb = _items.Count > VisibleRows;
            int right = Width - (sb ? 14 : 2);
            int y = 0;
            for (int i = _scroll; i < _items.Count && y < Height; i++, y += _rowH)
            {
                var s = _items[i];
                var row = new Rectangle(0, y + 2, right, _rowH - 4);
                var col = HudForm.StatusColor(s.Status);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(row, 8))
                using (var b = new SolidBrush(i == _selected ? Theme.Hover : i == _hover ? Theme.Surface2 : Theme.Row))
                    g.FillPath(b, path);
                using (var b = new SolidBrush(col)) g.FillRectangle(b, row.X + 1, row.Y + 8, 3, row.Height - 16);

                int x = row.X + 14;
                int l1 = row.Y + 6, l2 = row.Y + 6 + Theme.Ui.Height + 2;

                // справа: статус и время
                string st = Session.StatusText(s.Status) + "   " + Elapsed(s.Since);
                if (s.ReconnectAt != null) st = "повтор через " + Math.Max(0, (int)(s.ReconnectAt.Value - DateTime.Now).TotalSeconds) + " с";
                int sw = TextRenderer.MeasureText(st, Theme.Small).Width;
                TextRenderer.DrawText(g, st, Theme.Small, new Point(row.Right - sw - 12, l1 + 2), Theme.Ink(col), TextFormatFlags.NoPadding);

                // смена - «таблетка»
                int shiftW = 0;
                if (ShowShift && s.Status == SessStatus.Connected)
                {
                    string sh; Color sc;
                    if (s.ShiftNo == 0) { sh = "смена закрыта"; sc = StatusGroup.ShiftClosed; }
                    else if (s.ShiftNo > 0) { sh = "смена №" + s.ShiftNo; sc = StatusGroup.ShiftOpen; }
                    else { sh = s.ShiftError != null ? "смена ?" : "смена …"; sc = Theme.TextDim; }
                    if (s.ShiftError != null && s.ShiftNo != null) sh += " ?";
                    shiftW = TextRenderer.MeasureText(sh, Theme.Small).Width + 16;
                    var pr = new Rectangle(row.Right - sw - 24 - shiftW, l1, shiftW, Theme.Small.Height + 6);
                    Pill(g, pr, Color.FromArgb(55, sc), Color.Empty);
                    TextRenderer.DrawText(g, sh, Theme.Small, pr, Theme.Ink(sc), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    shiftW += 12;
                }

                TextRenderer.DrawText(g, s.Name, Theme.UiBold, new Rectangle(x, l1, Math.Max(0, row.Right - sw - 24 - shiftW - x), Theme.Ui.Height + 2),
                    Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                // вторая строка: папка · адрес · сообщение | вкладка
                var parts = new List<string>();
                if (s.Entry.Folder.Length > 0) parts.Add(s.Entry.Folder);
                if (s.Entry.Host.Length > 0) parts.Add(s.Entry.Address(DefaultPort));
                if (s.Message.Length > 0 && s.Status != SessStatus.Connected) parts.Add(s.Message);
                if (s.External) parts.Add("открыто вручную");
                int page;
                string pg = GridLayout.PageCount > 1 && GridLayout.PageOf.TryGetValue(s, out page) ? "вкладка " + (page + 1) : "";
                int pw = pg.Length > 0 ? TextRenderer.MeasureText(pg, Theme.Small).Width : 0;
                if (pw > 0) TextRenderer.DrawText(g, pg, Theme.Small, new Point(row.Right - pw - 12, l2), Theme.TextDim, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, string.Join("  ·  ", parts), Theme.Small, new Rectangle(x, l2, Math.Max(0, row.Right - pw - 24 - x), Theme.Small.Height + 2),
                    Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            _track = _thumb = Rectangle.Empty;
            if (sb)
            {
                _track = new Rectangle(Width - 10, 2, 8, Height - 4);
                int th = Math.Max(_rowH / 2, _track.Height * VisibleRows / _items.Count);
                int max = _items.Count - VisibleRows;
                _thumb = new Rectangle(_track.X, _track.Y + (max > 0 ? (_track.Height - th) * _scroll / max : 0), 8, th);
                Pill(g, _thumb, _drag ? Theme.TextDim : Theme.Border, Color.Empty);
            }
        }

        int RowAt(Point p)
        {
            if (_track != Rectangle.Empty && p.X >= _track.X - 2) return -1;
            int i = _scroll + p.Y / _rowH;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll -= Math.Sign(e.Delta) * 2;
            Clamp();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (_thumb.Contains(e.Location)) { _drag = true; _dragOff = e.Y - _thumb.Y; Capture = true; return; }
            if (_track.Contains(e.Location)) { _scroll += e.Y < _thumb.Y ? -VisibleRows : VisibleRows; Clamp(); Invalidate(); return; }
            _selected = RowAt(e.Location);
            Invalidate();
            if (e.Button == MouseButtons.Right && _selected >= 0 && RowMenu != null)
            {
                MenuSession = _items[_selected];
                RowMenu.Show(this, e.Location);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag)
            {
                int max = Math.Max(0, _items.Count - VisibleRows), free = Math.Max(1, _track.Height - _thumb.Height);
                _scroll = (int)Math.Round((e.Y - _dragOff - _track.Y) * (double)max / free);
                Clamp(); Invalidate();
                return;
            }
            int h = RowAt(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            string tip = "";
            if (h >= 0)
            {
                var s = _items[h];
                tip = s.Name + (s.Message.Length > 0 ? "\n" + Session.StatusText(s.Status) + ": " + s.Message : "") +
                      (s.ShiftRaw.Length > 0 ? "\nOCR: " + s.ShiftRaw : "") +
                      (s.ShiftCheckedAt != DateTime.MinValue ? "\nСмена проверена в " + s.ShiftCheckedAt.ToString("HH:mm:ss") : "") +
                      (s.ShiftError != null ? "\n" + s.ShiftError : "") +
                      "\nДвойной щелчок - показать окно";
            }
            if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(this, tip); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag) { _drag = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = RowAt(e.Location);
            if (i >= 0 && Activated != null) Activated(_items[i]);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Clamp(); }
    }
}
