using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>
    /// Полупрозрачная панель поверх всех окон со статусами подключений.
    /// Прокрутка колесом/полосой, изменение размера за края, фильтр по щелчку на счётчике, сворачивание до шапки.
    /// </summary>
    internal class HudForm : Form
    {
        class Group
        {
            public string Title;
            public Color Color;
            public Func<Session, bool> Match;
            public Rectangle Bounds;
            public bool Shift;       // счётчик смен - показывается, только если включено отслеживание
        }

        readonly ConnectionManager _cm;
        readonly AppSettings _s;
        readonly Action _arrange;
        readonly Timer _tick = new Timer { Interval = 1000 };
        readonly ToolTip _tip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };
        readonly ContextMenuStrip _rowMenu = MainForm.DarkMenu();
        readonly ContextMenuStrip _headMenu = MainForm.DarkMenu();
        readonly ToolStripMenuItem _miAutoHeight;
        readonly Font _bold, _small;
        readonly Group[] _groups;
        int _rowH, _headH, _sbW, _grip;
        int _scroll;                 // первая видимая строка
        Point _dragFrom;
        bool _dragging, _sbDragging;
        int _sbDragOffset;
        int _hoverRow = -1;
        string _lastTip;
        Session _menuTarget;
        Rectangle _btnCollapse, _btnArrange, _btnRetry, _btnHide, _sbTrack, _sbThumb;

        static readonly Color Bg = Color.FromArgb(22, 24, 29);
        static readonly Color HeadBg = Color.FromArgb(34, 37, 44);
        static readonly Color Fg = Color.FromArgb(230, 232, 236);
        static readonly Color Dim = Color.FromArgb(140, 146, 158);

        public event Action HiddenByUser;
        public event Action CheckShiftsRequested;
        public event Action<Session> FocusRequested;


        static readonly Color ShiftOpenColor = Color.FromArgb(26, 188, 156);
        static readonly Color ShiftClosedColor = Color.FromArgb(155, 89, 182);

        public HudForm(ConnectionManager cm, AppSettings s, Action arrange)
        {
            _cm = cm; _s = s; _arrange = arrange;
            Text = "Radmin Grid - HUD";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Bg;
            Font = new Font("Segoe UI", 9f);
            _bold = new Font(Font, FontStyle.Bold);
            _small = new Font("Segoe UI", 8f);
            Opacity = Math.Max(30, Math.Min(100, s.HudOpacity)) / 100.0;
            _rowH = (int)(Font.Height * 1.45);
            _headH = (int)(Font.Height * 1.9);
            _sbW = Math.Max(8, Font.Height * 2 / 3);
            _grip = Math.Max(5, Font.Height / 3);
            MinimumSize = new Size(Font.Height * 14, _headH + 2);

            _groups = new[]
            {
                new Group { Title = "Подключено", Color = StatusColor(SessStatus.Connected), Match = x => x.Status == SessStatus.Connected },
                new Group { Title = "В процессе", Color = StatusColor(SessStatus.Connecting),
                            Match = x => x.IsBusy && x.Status != SessStatus.Connected && x.Status != SessStatus.WaitingAuth && x.Status != SessStatus.AuthFailed },
                new Group { Title = "Ждут пароль", Color = StatusColor(SessStatus.WaitingAuth), Match = x => x.Status == SessStatus.WaitingAuth },
                new Group { Title = "Отвалились", Color = StatusColor(SessStatus.Dropped), Match = x => x.Status == SessStatus.Dropped },
                new Group { Title = "Не открылись", Color = StatusColor(SessStatus.Failed),
                            Match = x => x.Status == SessStatus.Failed || x.Status == SessStatus.Unreachable || x.Status == SessStatus.AuthFailed },
                new Group { Title = "Смена открыта", Color = ShiftOpenColor, Shift = true,
                            Match = x => x.Status == SessStatus.Connected && x.ShiftNo > 0 },
                new Group { Title = "Смена закрыта", Color = ShiftClosedColor, Shift = true,
                            Match = x => x.Status == SessStatus.Connected && x.ShiftNo == 0 },
            };
            if (_s.HudFilter >= _groups.Length) _s.HudFilter = -1;

            _rowMenu.Items.Add("Показать окно", null, delegate { if (_menuTarget != null) ShowSession(_menuTarget); });
            _rowMenu.Items.Add("Переподключить", null, delegate { if (_menuTarget != null) _cm.Reconnect(_menuTarget); });
            _rowMenu.Items.Add("Отключить", null, delegate { if (_menuTarget != null) _cm.Disconnect(_menuTarget); });
            _rowMenu.Items.Add("Убрать из списка", null, delegate { if (_menuTarget != null) _cm.Remove(_menuTarget); });

            _headMenu.Items.Add("Расставить по сетке", null, delegate { _arrange(); });
            _headMenu.Items.Add("Проверить смены сейчас", null, delegate { if (CheckShiftsRequested != null) CheckShiftsRequested(); });
            _headMenu.Items.Add("Переподключить отвалившиеся/неоткрывшиеся", null, delegate { _cm.ReconnectFailed(); });
            _headMenu.Items.Add("Очистить завершённые", null, delegate { _cm.ClearFinished(); });
            _headMenu.Items.Add("Закрыть все подключения", null, delegate { _cm.DisconnectAll(); });
            _headMenu.Items.Add(new ToolStripSeparator());
            _headMenu.Items.Add("Показать все (сбросить фильтр)", null, delegate { SetFilter(-1); });
            _miAutoHeight = new ToolStripMenuItem("Высота по содержимому", null, delegate { _s.HudHeight = 0; UpdateSize(); Invalidate(); });
            _headMenu.Items.Add(_miAutoHeight);
            foreach (int op in new[] { 100, 90, 75, 60 })
            {
                int v = op;
                _headMenu.Items.Add("Прозрачность " + (100 - v) + "%", null, delegate { _s.HudOpacity = v; Opacity = v / 100.0; });
            }
            _headMenu.Items.Add(new ToolStripSeparator());
            _headMenu.Items.Add("Скрыть HUD", null, delegate { HideByUser(); });
            _headMenu.Opening += delegate { _miAutoHeight.Checked = _s.HudHeight <= 0; };

            _cm.Changed += OnChanged;
            _tick.Tick += delegate { if (_cm.Sessions.Count > 0 && !_s.HudCollapsed) Invalidate(); };
            _tick.Start();

            Width = _s.HudWidth > 0 ? _s.HudWidth : Font.Height * 26;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = (s.HudX >= 0 && s.HudY >= 0 && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(s.HudX + 20, s.HudY + 10)))
                ? new Point(s.HudX, s.HudY)
                : new Point(wa.Right - Width - 16, wa.Top + 16);
            UpdateSize();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW - нет в Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE - не отнимает фокус у окон Radmin
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _cm.Changed -= OnChanged; _tick.Dispose(); _tip.Dispose(); }
            base.Dispose(disposing);
        }

        // Изменение размера за края окна без рамки
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 /*WM_NCHITTEST*/ || m.Result.ToInt32() != 1 /*HTCLIENT*/) return;
            int lp = m.LParam.ToInt32();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            bool l = p.X < _grip, r = p.X >= Width - _grip, t = p.Y < _grip / 2 + 1, b = p.Y >= Height - _grip;
            bool corner = p.X >= Width - _grip * 3 && p.Y >= Height - _grip * 3;
            if (_s.HudCollapsed) { t = b = corner = false; }
            int hit = 0;
            if (corner) hit = 17;              // HTBOTTOMRIGHT
            else if (b && l) hit = 16;
            else if (t && l) hit = 13;
            else if (t && r) hit = 14;
            else if (l) hit = 10;
            else if (r) hit = 11;
            else if (t) hit = 12;
            else if (b) hit = 15;
            if (hit != 0) m.Result = new IntPtr(hit);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            _s.HudWidth = Width;
            if (!_s.HudCollapsed) _s.HudHeight = Height;
            _s.HudX = Left; _s.HudY = Top;
            ClampScroll();
            Invalidate();
        }

        void HideByUser()
        {
            Hide();
            if (HiddenByUser != null) HiddenByUser();
        }

        void OnChanged()
        {
            UpdateSize();
            ClampScroll();
            Invalidate();
        }

        void SetFilter(int g)
        {
            _s.HudFilter = g;
            _scroll = 0;
            if (_s.HudCollapsed) _s.HudCollapsed = false;
            UpdateSize();
            Invalidate();
        }

        List<Session> Rows()
        {
            IEnumerable<Session> q = _cm.Sessions;
            if (_s.HudFilter >= 0) q = q.Where(_groups[_s.HudFilter].Match);
            // Сначала проблемные, затем подключающиеся, затем подключённые
            return q.OrderBy(x => Rank(x.Status)).ThenBy(x => x.Order).ToList();
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

        public static Color StatusColor(SessStatus st)
        {
            switch (st)
            {
                case SessStatus.Connected: return Color.FromArgb(46, 204, 113);
                case SessStatus.WaitingAuth:
                case SessStatus.LoggingIn: return Color.FromArgb(52, 152, 219);
                case SessStatus.Dropped: return Color.FromArgb(230, 126, 34);
                case SessStatus.Failed:
                case SessStatus.Unreachable:
                case SessStatus.AuthFailed: return Color.FromArgb(231, 76, 60);
                case SessStatus.Closed: return Color.FromArgb(120, 125, 135);
                default: return Color.FromArgb(241, 196, 15);
            }
        }

        Rectangle ListArea { get { return new Rectangle(0, _headH + 2, Width, Math.Max(0, Height - _headH - 4)); } }

        /// <summary>Пересчитать высоту (например, появилась или исчезла полоса вкладок).</summary>
        /// <summary>Применить непрозрачность и (после сброса в настройках) размер и положение.</summary>
        public void ApplySettings()
        {
            Opacity = Math.Max(30, Math.Min(100, _s.HudOpacity)) / 100.0;
            if (_s.HudWidth <= 0) Width = Font.Height * 26;
            if (_s.HudX < 0 || _s.HudY < 0)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 16, wa.Top + 16);
            }
            RefreshLayout();
        }

        public void RefreshLayout()
        {
            UpdateSize();
            ClampScroll();
            Invalidate();
        }

        void ShowSession(Session s)
        {
            if (FocusRequested != null) FocusRequested(s); else _cm.Focus(s);
        }

        int VisibleRows { get { return Math.Max(1, ListArea.Height / _rowH); } }

        void UpdateSize()
        {
            if (_s.HudCollapsed) { Height = _headH + 1; return; }
            if (_s.HudHeight > 0) { Height = Math.Max(_headH + _rowH * 2, _s.HudHeight); return; }
            int rows = Math.Max(1, Rows().Count);
            int maxH = (int)(Screen.FromControl(this).WorkingArea.Height * 0.6);
            Height = Math.Min(_headH + rows * _rowH + 6, maxH);
        }

        void ClampScroll()
        {
            int max = Math.Max(0, Rows().Count - VisibleRows);
            _scroll = Math.Max(0, Math.Min(_scroll, max));
        }

        static string Elapsed(DateTime since)
        {
            var t = DateTime.Now - since;
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // ---------- шапка: счётчики-фильтры и кнопки ----------
            using (var hb = new SolidBrush(HeadBg)) g.FillRectangle(hb, 0, 0, Width, _headH);
            int pad = Font.Height / 2;
            int bw = _headH;
            _btnHide = new Rectangle(Width - bw, 0, bw, _headH);
            _btnRetry = new Rectangle(Width - 2 * bw, 0, bw, _headH);
            _btnArrange = new Rectangle(Width - 3 * bw, 0, bw, _headH);
            _btnCollapse = new Rectangle(Width - 4 * bw, 0, bw, _headH);

            int x = pad;
            var hy = (_headH - Font.Height) / 2;
            int d = Font.Height / 2;
            // Заголовок не должен вытеснять счётчики: активный фильтр виден по подсветке счётчика
            int countersW = 0;
            for (int i = 0; i < _groups.Length; i++)
            {
                if (_groups[i].Shift && !_s.ShiftCheck) continue;
                countersW += d + 3 + TextRenderer.MeasureText(_cm.Sessions.Count(_groups[i].Match).ToString(), _bold).Width + 4 + pad / 2;
            }
            int room = _btnCollapse.Left - pad - countersW;
            string title = "RADMIN GRID";
            if (TextRenderer.MeasureText(title, _bold).Width + pad > room) title = "RG";
            if (TextRenderer.MeasureText(title, _bold).Width + pad > room) title = "";
            if (title.Length > 0)
            {
                TextRenderer.DrawText(g, title, _bold, new Point(x, hy), Fg, TextFormatFlags.NoPadding);
                x += TextRenderer.MeasureText(title, _bold).Width + pad;
            }

            for (int i = 0; i < _groups.Length; i++)
            {
                var gr = _groups[i];
                if (gr.Shift && !_s.ShiftCheck) { gr.Bounds = Rectangle.Empty; continue; }
                int n = _cm.Sessions.Count(gr.Match);
                string ns = n.ToString();
                int w = d + 3 + TextRenderer.MeasureText(ns, _bold).Width + 4;
                gr.Bounds = new Rectangle(x - 2, 3, w + 2, _headH - 6);
                if (gr.Bounds.Right > _btnCollapse.Left) { gr.Bounds = Rectangle.Empty; continue; }
                if (_s.HudFilter == i)
                    using (var sel = new SolidBrush(Color.FromArgb(70, gr.Color))) g.FillRectangle(sel, gr.Bounds);
                using (var b = new SolidBrush(n > 0 ? gr.Color : Color.FromArgb(80, gr.Color)))
                {
                    if (gr.Shift) g.FillRectangle(b, x, (_headH - d) / 2, d, d);   // квадрат - счётчики смен
                    else g.FillEllipse(b, x, (_headH - d) / 2, d, d);
                }
                TextRenderer.DrawText(g, ns, _bold, new Point(x + d + 3, hy), n > 0 ? Fg : Dim, TextFormatFlags.NoPadding);
                x += w + pad / 2;
            }

            DrawHeadButton(g, _btnCollapse, _s.HudCollapsed ? "+" : "–");
            DrawHeadButton(g, _btnArrange, "⊞");
            DrawHeadButton(g, _btnRetry, "↻");
            DrawHeadButton(g, _btnHide, "✕");

            if (!_s.HudCollapsed) PaintRows(g, pad);

            using (var p = new Pen(Color.FromArgb(60, 64, 74))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        void PaintRows(Graphics g, int pad)
        {
            var rows = Rows();
            var area = ListArea;
            bool needSb = rows.Count > VisibleRows;
            int rightEdge = Width - (needSb ? _sbW + 2 : 0);

            if (rows.Count == 0)
                TextRenderer.DrawText(g, _s.HudFilter >= 0 ? "Нет подключений в этой группе" : "Нет подключений", Font,
                    new Rectangle(0, area.Y, Width, _rowH), Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            g.SetClip(area);
            int y = area.Y;
            for (int i = _scroll; i < rows.Count && y < area.Bottom; i++, y += _rowH)
            {
                var s = rows[i];
                if (i == _hoverRow) using (var hb = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(hb, 0, y, rightEdge, _rowH);
                var col = StatusColor(s.Status);
                using (var b = new SolidBrush(col)) g.FillRectangle(b, 0, y + 2, 3, _rowH - 4);
                int d = (int)(Font.Height * 0.6);
                using (var b = new SolidBrush(col)) g.FillEllipse(b, pad, y + (_rowH - d) / 2, d, d);

                string right = Session.StatusText(s.Status) + "  " + Elapsed(s.Since);
                if (s.ReconnectAt != null)
                    right = "повтор через " + Math.Max(0, (int)(s.ReconnectAt.Value - DateTime.Now).TotalSeconds) + " с";
                int rw = TextRenderer.MeasureText(right, _small).Width;
                TextRenderer.DrawText(g, right, _small, new Rectangle(rightEdge - rw - pad, y, rw + 2, _rowH), col,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                // Смена: «№3292», «закрыта», «?» - не прочитана
                string shift = null; Color shiftCol = Dim;
                if (_s.ShiftCheck && s.Status == SessStatus.Connected)
                {
                    if (s.ShiftNo == 0) { shift = "закрыта"; shiftCol = ShiftClosedColor; }
                    else if (s.ShiftNo > 0) { shift = "№" + s.ShiftNo; shiftCol = ShiftOpenColor; }
                    else shift = s.ShiftError != null ? "см. ?" : "см. …";
                    if (s.ShiftError != null && s.ShiftNo != null) shift += "?";   // последнее чтение не удалось
                }
                if (shift != null)
                {
                    int sw = TextRenderer.MeasureText(shift, _small).Width;
                    rw += sw + pad;
                    TextRenderer.DrawText(g, shift, _small, new Rectangle(rightEdge - rw - pad, y, sw + 2, _rowH), shiftCol,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                int nx = pad + d + pad;
                TextRenderer.DrawText(g, s.Name, Font, new Rectangle(nx, y, Math.Max(0, rightEdge - nx - rw - pad * 2), _rowH), Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            g.ResetClip();

            // полоса прокрутки
            _sbTrack = _sbThumb = Rectangle.Empty;
            if (needSb)
            {
                _sbTrack = new Rectangle(Width - _sbW - 2, area.Y + 2, _sbW, area.Height - _grip * 3);
                int th = Math.Max(_rowH, _sbTrack.Height * VisibleRows / rows.Count);
                int maxScroll = rows.Count - VisibleRows;
                int ty = _sbTrack.Y + (maxScroll > 0 ? (_sbTrack.Height - th) * _scroll / maxScroll : 0);
                _sbThumb = new Rectangle(_sbTrack.X, ty, _sbW, th);
                using (var tb = new SolidBrush(Color.FromArgb(34, 37, 44))) g.FillRectangle(tb, _sbTrack);
                using (var th2 = new SolidBrush(_sbDragging ? Color.FromArgb(150, 156, 168) : Color.FromArgb(95, 100, 112))) g.FillRectangle(th2, _sbThumb);
                string info = (_scroll + 1) + "–" + Math.Min(rows.Count, _scroll + VisibleRows) + " из " + rows.Count +
                              (_s.HudFilter >= 0 ? " (" + _groups[_s.HudFilter].Title.ToLowerInvariant() + ")" : "");
                int iw = TextRenderer.MeasureText(info, _small).Width;
                TextRenderer.DrawText(g, info, _small, new Point(Width - iw - _sbW - 8, Height - _small.Height - 2), Dim);
            }

            // уголок изменения размера
            using (var p = new Pen(Dim))
                for (int k = 1; k <= 3; k++)
                    g.DrawLine(p, Width - 3 - k * 3, Height - 3, Width - 3, Height - 3 - k * 3);
        }

        void DrawHeadButton(Graphics g, Rectangle r, string glyph)
        {
            TextRenderer.DrawText(g, glyph, Font, r, Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int RowAt(Point p)
        {
            var area = ListArea;
            if (_s.HudCollapsed || !area.Contains(p) || (_sbTrack != Rectangle.Empty && p.X >= _sbTrack.X - 1)) return -1;
            int i = _scroll + (p.Y - area.Y) / _rowH;
            return i < Rows().Count ? i : -1;
        }

        int GroupAt(Point p)
        {
            for (int i = 0; i < _groups.Length; i++) if (_groups[i].Bounds.Contains(p)) return i;
            return -1;
        }

        bool OnHeadButton(Point p)
        {
            return _btnCollapse.Contains(p) || _btnArrange.Contains(p) || _btnRetry.Contains(p) || _btnHide.Contains(p) || GroupAt(p) >= 0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll -= Math.Sign(e.Delta) * 3;
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_sbThumb.Contains(e.Location))
            {
                _sbDragging = true;
                _sbDragOffset = e.Y - _sbThumb.Y;
                Capture = true;
                return;
            }
            if (_sbTrack.Contains(e.Location))
            {
                _scroll += e.Y < _sbThumb.Y ? -VisibleRows : VisibleRows;   // постраничная прокрутка
                ClampScroll();
                Invalidate();
                return;
            }
            if (e.Y < _headH && !OnHeadButton(e.Location))
            {
                _dragging = true;
                _dragFrom = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                Location = new Point(Location.X + e.X - _dragFrom.X, Location.Y + e.Y - _dragFrom.Y);
                return;
            }
            if (_sbDragging)
            {
                int total = Rows().Count, max = Math.Max(0, total - VisibleRows);
                int free = Math.Max(1, _sbTrack.Height - _sbThumb.Height);
                _scroll = (int)Math.Round((e.Y - _sbDragOffset - _sbTrack.Y) * (double)max / free);
                ClampScroll();
                Invalidate();
                return;
            }
            int row = RowAt(e.Location);
            if (row != _hoverRow) { _hoverRow = row; Invalidate(); }

            string tip = "";
            var rows = Rows();
            int gi = GroupAt(e.Location);
            if (row >= 0 && row < rows.Count)
            {
                var s = rows[row];
                tip = s.Name + (s.Entry.Host.Length > 0 ? "  (" + s.Entry.Address(_s.DefaultPort) + ")" : "") +
                      (s.Entry.Folder.Length > 0 ? "\nПапка: " + s.Entry.Folder : "") +
                      (GridLayout.PageCount > 1 && GridLayout.PageOf.ContainsKey(s) ? "\nВкладка: " + (GridLayout.PageOf[s] + 1) : "") +
                      "\n" + Session.StatusText(s.Status) + (s.Message.Length > 0 ? ": " + s.Message : "") +
                      (s.Reconnects > 0 ? "\nПереподключений: " + s.Reconnects : "") +
                      (_s.ShiftCheck && s.Status == SessStatus.Connected
                          ? "\nСмена: " + (s.ShiftNo == null ? "ещё не прочитана" : s.ShiftNo == 0 ? "закрыта (0)" : "открыта №" + s.ShiftNo) +
                            (s.ShiftCheckedAt != DateTime.MinValue ? ", проверено в " + s.ShiftCheckedAt.ToString("HH:mm:ss") : "") +
                            (s.ShiftError != null ? "\nПоследнее чтение: " + s.ShiftError : "") +
                            (s.ShiftRaw.Length > 0 ? "\nOCR: " + s.ShiftRaw : "")
                          : "") +
                      "\n\nДвойной щелчок - показать окно, правый - меню";
            }
            else if (gi >= 0) tip = _groups[gi].Title + (_s.HudFilter == gi ? " - щелчок: показать все" : " - щелчок: показать только их");
            else if (_btnCollapse.Contains(e.Location)) tip = _s.HudCollapsed ? "Развернуть список" : "Свернуть до шапки";
            else if (_btnArrange.Contains(e.Location)) tip = "Расставить окна по сетке";
            else if (_btnRetry.Contains(e.Location)) tip = "Переподключить отвалившиеся и неоткрывшиеся";
            else if (_btnHide.Contains(e.Location)) tip = "Скрыть HUD";
            else if (e.Y < _headH) tip = "Перетащите за шапку. Размер - за край или правый нижний угол. Правый щелчок - меню.";
            if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(this, tip); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverRow = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_sbDragging) { _sbDragging = false; Capture = false; Invalidate(); return; }
            if (_dragging)
            {
                _dragging = false;
                _s.HudX = Location.X; _s.HudY = Location.Y;
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                int gi = GroupAt(e.Location);
                if (gi >= 0) SetFilter(_s.HudFilter == gi ? -1 : gi);
                else if (_btnCollapse.Contains(e.Location)) { _s.HudCollapsed = !_s.HudCollapsed; UpdateSize(); Invalidate(); }
                else if (_btnArrange.Contains(e.Location)) _arrange();
                else if (_btnRetry.Contains(e.Location)) _cm.ReconnectFailed();
                else if (_btnHide.Contains(e.Location)) HideByUser();
            }
            else if (e.Button == MouseButtons.Right)
            {
                int row = RowAt(e.Location);
                var rows = Rows();
                if (row >= 0 && row < rows.Count) { _menuTarget = rows[row]; _rowMenu.Show(this, e.Location); }
                else _headMenu.Show(this, e.Location);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Y < _headH && !OnHeadButton(e.Location)) { _s.HudCollapsed = !_s.HudCollapsed; UpdateSize(); Invalidate(); return; }
            int row = RowAt(e.Location);
            var rows = Rows();
            if (row >= 0 && row < rows.Count) ShowSession(rows[row]);
        }
    }
}
