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
        // =====================================================================  подключение

        void ConnectChecked()
        {
            var list = _s.Entries.Where(x => x.Checked).ToList();
            if (list.Count == 0) { Error("Поставьте галочки на папках или узлах в дереве - к ним и будет подключение."); return; }
            var folders = list.Select(x => x.Folder).Distinct().ToList();
            AppendLog("Отмечено: " + list.Count + " узл. в " + folders.Count + " папк.: " + string.Join(", ", folders.Take(5)) + (folders.Count > 5 ? "…" : ""));
            ConnectEntries(list);
        }

        void ConnectQuick()
        {
            var tokens = _tbQuick.Text.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (tokens.Count == 0) { ConnectChecked(); return; }
            var result = new List<PbEntry>();
            foreach (var tok in tokens)
            {
                string t = tok.Replace('\\', '/').Trim('/');
                var byFolder = _s.Entries.Where(x => x.Folder.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                                                     x.Folder.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                var byName = _s.Entries.Where(x => x.Name.Equals(tok, StringComparison.OrdinalIgnoreCase) ||
                                                   x.Host.Equals(tok, StringComparison.OrdinalIgnoreCase) ||
                                                   x.Address(_s.DefaultPort).Equals(tok, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byFolder.Count > 0) { result.AddRange(byFolder); ExpandFolder(t); }
                if (byName.Count > 0) result.AddRange(byName);
                if (byFolder.Count > 0 || byName.Count > 0) continue;

                string h; int p;
                if (RadminController.TryParseHost(tok, true, out h, out p) || IsSimpleHostName(tok))
                {
                    if (h == null) h = tok;
                    result.Add(new PbEntry { Folder = "", Name = tok, Host = h, Port = p, Source = "quick" });
                }
                else AppendLog("«" + tok + "» не найден в списке узлов.");
            }
            result = result.GroupBy(x => x.Key).Select(g => g.First()).ToList();
            if (result.Count == 0) { Error("Ни один узел не найден."); return; }
            RequestSave();
            ConnectEntries(result);
        }

        static bool IsSimpleHostName(string s)
        {
            return s.Length > 0 && s.Length < 64 && s.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == ':');
        }

        void ExpandFolder(string path)
        {
            foreach (TreeNode n in AllNodes(_tree.Nodes))
                if (n.Tag is string && ((string)n.Tag).Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    n.EnsureVisible();
                    n.Expand();
                }
        }

        static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        void ConnectEntries(List<PbEntry> list)
        {
            try { _rc.ValidateExe(); } catch (Exception ex) { Error(ex.Message + "\n\nУкажите путь в настройках."); return; }

            int noHost = list.Count(x => x.Host.Length == 0);
            if (_s.Method == ConnectMethod.CommandLine && noHost > 0)
                AppendLog(noHost + " узл. без адреса будут открыты через телефонную книгу Radmin.");
            if (_s.AutoLogin && _s.GetPassword().Length == 0)
                AppendLog("Автоввод включён, но пароль не задан - окна входа нужно будет заполнить вручную.");
            if (_s.Method == ConnectMethod.CommandLine && !_s.AutoLogin)
                AppendLog("Подсказка: при подключении через командную строку Radmin запросит пароль - включите автоввод в настройках.");

            AppendLog("Подключение: " + list.Count + " узл., режим «" + ModeNames[(int)_s.Mode] + "».");
            _cm.Connect(list);
        }

        void ArrangeNow(bool manual, bool raise = false)
        {
            var sessions = _cm.ConnectedSessions();
            if (sessions.Count == 0)
            {
                if (manual) AppendLog("Нет открытых окон подключений для раскладки.");
                RefreshConnections();
                return;
            }
            try
            {
                int pagesBefore = GridLayout.PageCount;
                int n = GridLayout.Arrange(_s, sessions, manual || raise);
                var cells = GridLayout.BuildCells(_s, Math.Min(n, GridLayout.PageSize));
                if (GridLayout.PageCount > 1)
                {
                    if (manual || pagesBefore != GridLayout.PageCount)
                        AppendLog("Разложено окон: " + n + " на " + GridLayout.PageCount + " вкладк. по " + GridLayout.PageSize +
                                  ", открыта вкладка " + (GridLayout.CurrentPage + 1) + ". Переключение: вкладки над списком, Ctrl+Alt+← / →, Ctrl+Alt+1…9.");
                }
                else if (manual)
                    AppendLog("Разложено окон: " + n + " (ячеек: " + cells.Count + (n > cells.Count ? ", лишние окна перекрываются - включите вкладки" : "") + ").");
            }
            catch (Exception ex) { AppendLog("Ошибка раскладки: " + ex.Message); }
            _hud.RefreshLayout();
            RefreshConnections();
        }

        // ---------------- вкладки ----------------

        void SetPage(int page, bool fromTab = false)
        {
            if (GridLayout.PageCount <= 1) return;
            int p = ((page % GridLayout.PageCount) + GridLayout.PageCount) % GridLayout.PageCount;   // по кругу
            if (!fromTab && _pages.Selected >= 0) _pages.Selected = p;   // список следует за переключением с клавиатуры
            if (p == GridLayout.CurrentPage && !fromTab) return;
            GridLayout.CurrentPage = p;
            ArrangeNow(false, true);
        }

        /// <summary>Показать окно подключения - с переключением на его вкладку.</summary>
        void FocusSession(Session s)
        {
            int page;
            if (GridLayout.PageOf.TryGetValue(s, out page) && page != GridLayout.CurrentPage)
            {
                GridLayout.CurrentPage = page;
                if (_pages.Selected >= 0) _pages.Selected = page;
                ArrangeNow(false, true);
            }
            _cm.Focus(s);
        }

        // Глобальные сочетания: Ctrl+Alt+← / → - соседняя вкладка, Ctrl+Alt+1…9 - вкладка по номеру
        const int HotPrev = 0x5201, HotNext = 0x5202, HotNum = 0x5210;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const uint mods = 0x0001 | 0x0002 | 0x4000;   // ALT | CONTROL | NOREPEAT
            var failed = new List<string>();
            if (!Native.RegisterHotKey(Handle, HotPrev, mods, 0x25)) failed.Add("Ctrl+Alt+←");
            if (!Native.RegisterHotKey(Handle, HotNext, mods, 0x27)) failed.Add("Ctrl+Alt+→");
            for (int i = 1; i <= 9; i++)
                if (!Native.RegisterHotKey(Handle, HotNum + i, mods, (uint)(0x30 + i))) failed.Add("Ctrl+Alt+" + i);
            if (failed.Count > 0)
                BeginInvoke(new Action(() => AppendLog("Сочетания заняты другой программой: " + string.Join(", ", failed) + " - используйте вкладки над списком подключений.")));
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HotPrev);
            Native.UnregisterHotKey(Handle, HotNext);
            for (int i = 1; i <= 9; i++) Native.UnregisterHotKey(Handle, HotNum + i);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 /*WM_HOTKEY*/)
            {
                int id = m.WParam.ToInt32();
                if (id == HotPrev) SetPage(GridLayout.CurrentPage - 1);
                else if (id == HotNext) SetPage(GridLayout.CurrentPage + 1);
                else if (id > HotNum && id <= HotNum + 9) SetPage(id - HotNum - 1);
                return;
            }
            base.WndProc(ref m);
        }

        void ToggleHud()
        {
            if (_hud.Visible) _hud.Hide(); else _hud.Show();
            _s.HudVisible = _hud.Visible;
            _rail.HudOn = _hud.Visible;
            _rail.Invalidate();
            RequestSave();
        }
    }
}
