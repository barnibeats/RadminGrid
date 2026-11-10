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
        // =====================================================================  Radmin

        /// <summary>Запуск главного окна с обработкой уже запущенного экземпляра.</summary>
        bool LaunchRadmin()
        {
            try { _rc.ValidateExe(); }
            catch (Exception ex)
            {
                Error(ex.Message + "\n\nУкажите путь в настройках.");
                OpenSettings(0);
                return false;
            }

            IntPtr main = _rc.FindMainWindow();
            if (main != IntPtr.Zero)
            {
                int pid = Native.GetPid(main);
                string path = Native.GetProcessPath(pid) ?? "?";
                bool elevated = Native.IsBlockedByUipi(pid);
                bool samePath = string.Equals(path, _s.RadminPath, StringComparison.OrdinalIgnoreCase);
                string msg = "Radmin Viewer уже запущен (PID " + pid + ").\n" + path +
                             (samePath ? "" : "\n\nЭто другой экземпляр, не из указанной папки.") +
                             (elevated ? "\n\nВНИМАНИЕ: он запущен с правами администратора - управлять им без прав администратора нельзя. Выберите «Нет», чтобы перезапустить." : "") +
                             "\n\nДа - использовать запущенный\nНет - закрыть и запустить заново\nОтмена - ничего не делать";
                var r = MessageBox.Show(this, msg, "Radmin Viewer уже запущен", MessageBoxButtons.YesNoCancel,
                    elevated ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.Yes)
                {
                    if (elevated) { Error("Запущенный Radmin Viewer работает с правами администратора - автоматизация невозможна."); return false; }
                    Native.ShowWindow(main, Native.SW_RESTORE);
                    Native.SetForegroundWindow(main);
                    AppendLog("Используется запущенный Radmin Viewer (PID " + pid + ").");
                    return true;
                }
                var live = _cm.Sessions.Count(x => x.IsLive);
                if (live > 0 && MessageBox.Show(this, "Перезапуск закроет и открытые подключения (" + live + "). Продолжить?", Text,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return false;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    _rc.CloseAll(4000);
                    AppendLog("Запущенный Radmin Viewer закрыт.");
                }
                catch (Exception ex) { Error(ex.Message + "\n\nЗакройте Radmin Viewer вручную."); return false; }
                finally { Cursor = Cursors.Default; }
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _rc.StartMain();
                main = _rc.WaitMainWindow(15000);
                if (main == IntPtr.Zero) { Error("Radmin Viewer запущен, но окно телефонной книги не появилось за 15 с."); return false; }
                AppendLog("Radmin Viewer запущен (PID " + Native.GetPid(main) + ").");
                return true;
            }
            catch (Exception ex) { Error("Не удалось запустить Radmin Viewer: " + ex.Message); return false; }
            finally { Cursor = Cursors.Default; }
        }

        // =====================================================================  телефонная книга

        void ScanFromRadmin()
        {
            try { _rc.ValidateExe(); } catch (Exception ex) { Error(ex.Message + "\n\nУкажите путь в настройках."); return; }
            if (_rc.FindMainWindow() == IntPtr.Zero && !LaunchRadmin()) return;

            AppendLog("Чтение телефонной книги из окна Radmin Viewer…");
            UseWaitCursor = true;
            Enabled = false;
            var t = new Thread(delegate()
            {
                List<PbEntry> res = null; Exception err = null;
                try
                {
                    IntPtr main = _rc.EnsureMainWindow(m => AppendLog(m));
                    res = _rc.ScanPhonebook(main, m => AppendLog(m));
                }
                catch (Exception ex) { err = ex; }
                BeginInvoke(new Action(delegate
                {
                    Enabled = true; UseWaitCursor = false;
                    if (err != null) { Error("Не удалось прочитать телефонную книгу: " + err.Message); return; }
                    MergeEntries(res, "radmin");
                    AppendLog("Прочитано узлов: " + res.Count + ", папок: " + res.Select(x => x.Folder).Distinct().Count());
                }));
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void ImportRpb()
        {
            using (var d = new OpenFileDialog { Filter = "Телефонная книга Radmin|*.rpb|Все файлы|*.*" })
            {
                if (File.Exists(_s.PhonebookPath)) d.FileName = _s.PhonebookPath;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var res = PhonebookImport.ImportRpbHeuristic(d.FileName);
                    if (res.Count == 0) { Error("В файле не найдено ни одного адреса. Используйте «Из Radmin»."); return; }
                    if (MessageBox.Show(this, "Найдено адресов: " + res.Count + "\n\n" +
                            string.Join("\n", res.Take(15).Select(x => x.Name + " - " + x.Host)) + (res.Count > 15 ? "\n…" : "") +
                            "\n\nФормат .rpb закрытый, разбор эвристический: проверьте список. Импортировать?",
                            "Импорт .rpb", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                    MergeEntries(res, "rpb");
                    AppendLog("Импортировано из .rpb: " + res.Count);
                }
                catch (Exception ex) { Error("Ошибка чтения .rpb: " + ex.Message); }
            }
        }

        void ImportCsv()
        {
            using (var d = new OpenFileDialog { Filter = "CSV|*.csv;*.txt|Все файлы|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var res = PhonebookImport.ImportCsv(d.FileName);
                    MergeEntries(res, "csv");
                    AppendLog("Импортировано из CSV: " + res.Count);
                }
                catch (Exception ex) { Error("Ошибка чтения CSV: " + ex.Message); }
            }
        }

        void ExportCsv()
        {
            using (var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "radmin-hosts.csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { PhonebookImport.ExportCsv(d.FileName, _s.Entries); AppendLog("Экспортировано: " + d.FileName); }
                catch (Exception ex) { Error(ex.Message); }
            }
        }

        /// <summary>Заменяет узлы из того же источника, сохраняя отметки.</summary>
        void MergeEntries(List<PbEntry> incoming, string source)
        {
            var checkedKeys = new HashSet<string>(_s.Entries.Where(x => x.Checked).Select(x => x.Key));
            _s.Entries.RemoveAll(x => x.Source == source);
            var existing = new HashSet<string>(_s.Entries.Select(x => x.Key));
            foreach (var e in incoming)
            {
                e.Source = source;
                e.Checked = checkedKeys.Contains(e.Key);
                if (existing.Add(e.Key)) _s.Entries.Add(e);
            }
            RebuildTree();
            RequestSave();
        }

        static bool MatchesSearch(PbEntry e, string q)
        {
            return q.Length == 0 ||
                   e.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   e.Host.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   e.Folder.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        void RebuildTree()
        {
            string q = _tbSearch.Text.Trim();
            _suppressCheck = true;
            _tree.BeginUpdate();
            var expanded = new HashSet<string>();
            CollectExpanded(_tree.Nodes, expanded);
            _tree.Nodes.Clear();
            var folderIndex = new Dictionary<string, TreeNode>();
            var shown = _s.Entries.Where(x => MatchesSearch(x, q))
                                  .OrderBy(x => x.Folder, StringComparer.CurrentCultureIgnoreCase)
                                  .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var e in shown)
            {
                var nodes = _tree.Nodes;
                string path = "";
                foreach (var part in e.Folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    path = path.Length == 0 ? part : path + "/" + part;
                    TreeNode f;
                    if (!folderIndex.TryGetValue(path, out f))
                    {
                        f = new TreeNode(part) { Tag = path, NodeFont = Theme.UiBold };
                        nodes.Add(f);
                        folderIndex[path] = f;
                        if (q.Length > 0 || expanded.Contains(path)) f.Expand();
                    }
                    nodes = f.Nodes;
                }
                string text = e.Name + (e.Host.Length > 0 && e.Host != e.Name ? "   " + e.Address(_s.DefaultPort) : e.Host.Length == 0 ? "   адрес неизвестен" : "");
                var n2 = new TreeNode(text) { Tag = e, Checked = e.Checked, ToolTipText = "Источник: " + e.Source + (e.Folder.Length > 0 ? "\nПапка: " + e.Folder : "") };
                if (e.Host.Length == 0) n2.ForeColor = Theme.TextDim;
                nodes.Add(n2);
            }
            UpdateFolderChecks(_tree.Nodes);
            _tree.EndUpdate();
            _suppressCheck = false;
            _lblBookCount.Text = (q.Length > 0 ? "Найдено " + shown.Count + " из " + _s.Entries.Count : _s.Entries.Count + " узлов") + "  ·  галочка на папке - подключить все её узлы";
            UpdateGoButton();
        }

        static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> set)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is string && n.IsExpanded) set.Add((string)n.Tag);
                CollectExpanded(n.Nodes, set);
            }
        }

        static bool UpdateFolderChecks(TreeNodeCollection nodes)
        {
            bool all = nodes.Count > 0;
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is string) n.Checked = UpdateFolderChecks(n.Nodes);
                all &= n.Checked;
            }
            return all;
        }

        void TreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressCheck) return;
            _suppressCheck = true;
            SetCheckedRecursive(e.Node, e.Node.Checked);
            _suppressCheck = false;
            UpdateGoButton();
            RequestSave();
        }

        void UpdateGoButton()
        {
            var sel = _s.Entries.Where(x => x.Checked).ToList();
            _btnGo.Text = sel.Count > 0 ? "ПОДКЛЮЧИТЬ  " + sel.Count : "ПОДКЛЮЧИТЬ";
            _hero.PerformLayout();
            _btnGo.Enabled = sel.Count > 0;
            _hero.Selection = sel.Count == 0 ? "Отметьте папки или узлы в телефонной книге"
                : "Выбрано " + sel.Count + " узл. в " + sel.Select(x => x.Folder).Distinct().Count() + " папк.";
            _hero.Invalidate();
        }

        static void SetCheckedRecursive(TreeNode n, bool v)
        {
            n.Checked = v;
            var en = n.Tag as PbEntry;
            if (en != null) en.Checked = v;
            foreach (TreeNode c in n.Nodes) SetCheckedRecursive(c, v);
        }

        void CheckAll(bool v)
        {
            _suppressCheck = true;
            foreach (TreeNode n in _tree.Nodes) SetCheckedRecursive(n, v);   // при поиске - только найденные
            _suppressCheck = false;
            UpdateGoButton();
            RequestSave();
        }

        void AddEntry()
        {
            var e = new PbEntry { Source = "manual" };
            var sel = _tree.SelectedNode;
            if (sel != null) e.Folder = sel.Tag is string ? (string)sel.Tag : ((PbEntry)sel.Tag).Folder;
            using (var d = new EntryDialog(e))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                d.ApplyTo(e);
                _s.Entries.Add(e);
                RebuildTree();
                RequestSave();
            }
        }

        void EditEntry()
        {
            var e = _tree.SelectedNode == null ? null : _tree.SelectedNode.Tag as PbEntry;
            if (e == null) return;
            using (var d = new EntryDialog(e))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                d.ApplyTo(e);
                RebuildTree();
                RequestSave();
            }
        }

        void DeleteSelected()
        {
            var n = _tree.SelectedNode;
            if (n == null) return;
            if (n.Tag is PbEntry) _s.Entries.Remove((PbEntry)n.Tag);
            else
            {
                string path = (string)n.Tag;
                int cnt = _s.Entries.Count(x => x.Folder == path || x.Folder.StartsWith(path + "/"));
                if (MessageBox.Show(this, "Удалить папку «" + path + "» из списка (" + cnt + " узлов)?\nТелефонная книга Radmin не изменится.", Text,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _s.Entries.RemoveAll(x => x.Folder == path || x.Folder.StartsWith(path + "/"));
            }
            RebuildTree();
            RequestSave();
        }
    }
}
