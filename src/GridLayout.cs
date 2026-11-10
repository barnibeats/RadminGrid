using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Раскладка окон подключений по сетке. Координаты - физические пиксели (процесс DPI-aware), поэтому 4K со 150–200% масштабом раскладывается точно.</summary>
    internal static class GridLayout
    {
        public static readonly string[] Presets = { "Авто", "1x2", "2x2", "2x3", "3x3", "3x4", "4x4", "5x5", "6x6", "Своя" };

        public static Screen[] OrderedScreens()
        {
            return Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();
        }

        static void GridFor(AppSettings s, int count, out int rows, out int cols)
        {
            string p = s.GridPreset ?? "Авто";
            if (p == "Своя") { rows = Math.Max(1, s.GridRows); cols = Math.Max(1, s.GridCols); return; }
            var parts = p.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out rows) && int.TryParse(parts[1], out cols)) return;
            // Авто: минимальная почти квадратная сетка под количество окон
            count = Math.Max(1, count);
            cols = (int)Math.Ceiling(Math.Sqrt(count));
            rows = (int)Math.Ceiling(count / (double)cols);
        }

        public static List<Rectangle> BuildCells(AppSettings s, int count)
        {
            var screens = OrderedScreens();
            var targets = new List<Screen>();
            if (s.Monitor < 0) targets.AddRange(screens);
            else targets.Add(screens[Math.Min(s.Monitor, screens.Length - 1)]);

            var cells = new List<Rectangle>();
            int remaining = count;
            for (int i = 0; i < targets.Count; i++)
            {
                // При раскладке на все мониторы окна делятся между экранами поровну.
                int share = s.Monitor < 0 ? (int)Math.Ceiling(remaining / (double)(targets.Count - i)) : count;
                int rows, cols;
                GridFor(s, share, out rows, out cols);
                Rectangle area = s.CoverTaskbar ? targets[i].Bounds : targets[i].WorkingArea;
                int g = Math.Max(0, s.Gap);
                int cw = (area.Width - g * (cols + 1)) / cols;
                int ch = (area.Height - g * (rows + 1)) / rows;
                int made = 0;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        cells.Add(new Rectangle(area.X + g + c * (cw + g), area.Y + g + r * (ch + g), cw, ch));
                        made++;
                    }
                remaining = Math.Max(0, remaining - Math.Min(share, made));
            }
            return cells;
        }

        // ---------------- вкладки ----------------
        public static int CurrentPage;           // 0-based
        public static int PageCount = 1;
        public static int PageSize = int.MaxValue;
        /// <summary>Номер вкладки каждого окна после последней раскладки.</summary>
        public static readonly Dictionary<Session, int> PageOf = new Dictionary<Session, int>();

        /// <summary>Сколько окон помещается на одну вкладку при текущих настройках.</summary>
        public static int CellsPerPage(AppSettings s, int count)
        {
            if (!s.Paging) return int.MaxValue;
            bool auto = (s.GridPreset ?? "Авто") == "Авто";
            int monitors = s.Monitor < 0 ? OrderedScreens().Length : 1;
            if (auto) return Math.Max(1, s.AutoPageMax) * monitors;
            return BuildCells(s, 1).Count;
        }

        /// <summary>
        /// Раскладывает окна текущей вкладки по сетке. Окна других вкладок уносятся за пределы экрана
        /// (не сворачиваются - так переключение мгновенное и номер смены продолжает читаться).
        /// Без вкладок при нехватке ячеек окна идут по кругу (перекрываются).
        /// </summary>
        public static int Arrange(AppSettings s, List<Session> sessions, bool raise)
        {
            PageOf.Clear();
            if (sessions.Count == 0) { PageCount = 1; CurrentPage = 0; return 0; }

            PageSize = Math.Min(sessions.Count, CellsPerPage(s, sessions.Count));
            PageCount = (sessions.Count + PageSize - 1) / PageSize;
            CurrentPage = Math.Max(0, Math.Min(CurrentPage, PageCount - 1));

            // на текущей вкладке может быть меньше окон (последняя) - сетку считаем по полной вкладке
            var cells = BuildCells(s, PageSize);
            if (cells.Count == 0) return 0;
            Rectangle vs = SystemInformation.VirtualScreen;

            for (int i = 0; i < sessions.Count; i++)
            {
                var sess = sessions[i];
                int page = i / PageSize;
                PageOf[sess] = page;
                IntPtr h = sess.Hwnd;
                if (h == ShiftMonitor.Capturing) continue;
                ApplyBorder(sess, s.Borderless);
                if (Native.IsIconic(h) || Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                var cell = cells[(i % PageSize) % cells.Count];
                if (page == CurrentPage) { Place(h, cell); if (raise) Raise(h); }
                else Native.SetWindowPos(h, new IntPtr(1) /*HWND_BOTTOM*/, vs.Right + 100, vs.Top, cell.Width, cell.Height,
                         Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
            return sessions.Count;
        }

        /// <summary>Окно вне всех мониторов (на скрытой вкладке).</summary>
        public static bool IsParked(IntPtr h)
        {
            var r = Native.GetRect(h);
            return !Screen.AllScreens.Any(sc => sc.Bounds.IntersectsWith(r));
        }

        /// <summary>
        /// Поднять окно поверх обычных окон без активации. Windows не даёт фоновому процессу просто
        /// сделать HWND_TOP чужому окну, поэтому окно на мгновение становится «поверх всех» и сразу перестаёт им быть.
        /// </summary>
        static void Raise(IntPtr h)
        {
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            Native.SetWindowPos(h, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, f);
            Native.SetWindowPos(h, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0, f);
        }

        static void Place(IntPtr h, Rectangle cell)
        {
            const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_FRAMECHANGED;
            Native.SetWindowPos(h, IntPtr.Zero, cell.X, cell.Y, cell.Width, cell.Height, flags);
            // Компенсация невидимых границ Windows 10, чтобы видимые рамки стыковались без щелей
            Rectangle wr = Native.GetRect(h), fr = Native.GetFrameRect(h);
            int dl = fr.Left - wr.Left, dt = fr.Top - wr.Top, dr = wr.Right - fr.Right, db = wr.Bottom - fr.Bottom;
            if ((dl | dt | dr | db) != 0 && dl >= 0 && dt >= 0 && dr >= 0 && db >= 0 && dl < 32 && dr < 32 && db < 32 && dt < 32)
                Native.SetWindowPos(h, IntPtr.Zero, cell.X - dl, cell.Y - dt, cell.Width + dl + dr, cell.Height + dt + db, flags);
        }

        public static void ApplyBorder(Session sess, bool borderless)
        {
            IntPtr h = sess.Hwnd;
            if (borderless)
            {
                int st = Native.GetStyle(h);
                if ((st & (Native.WS_CAPTION | Native.WS_THICKFRAME)) == 0) return;
                if (!sess.StyleChanged) { sess.OrigStyle = st; sess.StyleChanged = true; }
                Native.SetStyle(h, st & ~(Native.WS_CAPTION | Native.WS_THICKFRAME));
            }
            else if (sess.StyleChanged)
            {
                Native.SetStyle(h, sess.OrigStyle);
                sess.StyleChanged = false;
            }
        }
    }
}
