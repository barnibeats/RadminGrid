using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace RadminGrid
{
    /// <summary>Сведения о диалоге Radmin (авторизация, ошибка, прогресс).</summary>
    internal class DialogInfo
    {
        public IntPtr Hwnd;
        public string Title = "";
        public string Text = "";
        public IntPtr UserEdit, PasswordEdit, OkButton;
        public bool HasProgress;
        public bool IsAuth { get { return PasswordEdit != IntPtr.Zero; } }
        public bool IsMessage { get { return !IsAuth && !HasProgress && OkButton != IntPtr.Zero; } }
    }

    /// <summary>Запуск Radmin Viewer, подключения из командной строки и автоматизация окна телефонной книги.</summary>
    internal class RadminController
    {
        readonly AppSettings _s;
        readonly Dictionary<IntPtr, bool> _mainCache = new Dictionary<IntPtr, bool>();

        public RadminController(AppSettings s) { _s = s; }

        public string ExePath { get { return _s.RadminPath; } }

        public string ProcessName
        {
            get { return string.IsNullOrEmpty(ExePath) ? "rview3" : Path.GetFileNameWithoutExtension(ExePath); }
        }

        public void ValidateExe()
        {
            if (string.IsNullOrEmpty(ExePath))
                throw new ApplicationException("Не указан путь к Radmin Viewer (rview3.exe).");
            if (!File.Exists(ExePath))
                throw new ApplicationException("Файл не найден: " + ExePath);
        }

        public Process[] GetProcesses()
        {
            try { return Process.GetProcessesByName(ProcessName); }
            catch { return new Process[0]; }
        }

        public HashSet<int> GetPids()
        {
            var set = new HashSet<int>();
            foreach (var p in GetProcesses()) { set.Add(p.Id); p.Dispose(); }
            return set;
        }

        // ---------------- главное окно (телефонная книга) ----------------

        /// <summary>Главное окно - видимое окно Radmin с деревом папок (SysTreeView32).</summary>
        public bool IsMainWindow(IntPtr h)
        {
            bool v;
            if (_mainCache.TryGetValue(h, out v)) return v;
            v = Native.ChildWindows(h).Any(c => Native.GetClass(c).IndexOf("SysTreeView32", StringComparison.OrdinalIgnoreCase) >= 0);
            if (_mainCache.Count > 500) _mainCache.Clear();
            _mainCache[h] = v;
            return v;
        }

        public IntPtr FindMainWindow()
        {
            var pids = GetPids();
            if (pids.Count == 0) return IntPtr.Zero;
            foreach (var h in Native.TopLevelWindows())
                if (Native.IsWindowVisible(h) && pids.Contains(Native.GetPid(h)) && IsMainWindow(h))
                    return h;
            return IntPtr.Zero;
        }

        public Process StartMain()
        {
            ValidateExe();
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath)
            };
            if (_s.PassPhonebookSwitch && !string.IsNullOrEmpty(_s.PhonebookPath))
                psi.Arguments = "/pb:\"" + _s.PhonebookPath + "\"";
            return Process.Start(psi);
        }

        public IntPtr WaitMainWindow(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                _mainCache.Clear();
                IntPtr h = FindMainWindow();
                if (h != IntPtr.Zero) return h;
                Thread.Sleep(250);
            }
            return IntPtr.Zero;
        }

        /// <summary>Возвращает главное окно, при необходимости запуская Radmin Viewer.</summary>
        public IntPtr EnsureMainWindow(Action<string> log)
        {
            IntPtr h = FindMainWindow();
            if (h == IntPtr.Zero)
            {
                log("Запускаю Radmin Viewer: " + ExePath);
                StartMain();
                h = WaitMainWindow(15000);
                if (h == IntPtr.Zero)
                    throw new ApplicationException("Окно телефонной книги Radmin Viewer не появилось за 15 с.");
            }
            int pid = Native.GetPid(h);
            if (Native.IsBlockedByUipi(pid))
                throw new ApplicationException("Radmin Viewer (PID " + pid + ") запущен с правами администратора - " +
                    "Windows блокирует управление им из обычного процесса. Перезапустите Radmin Viewer без прав администратора.");
            return h;
        }

        public void CloseAll(int waitMs)
        {
            foreach (var p in GetProcesses())
            {
                try { p.CloseMainWindow(); } catch { }
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs && GetPids().Count > 0) Thread.Sleep(200);
            foreach (var p in GetProcesses())
            {
                try { p.Kill(); }
                catch (Exception ex) { throw new ApplicationException("Не удалось закрыть Radmin Viewer (PID " + p.Id + "): " + ex.Message); }
            }
        }

        // ---------------- подключение через командную строку ----------------

        public string BuildArgs(PbEntry e, ConnectMode mode)
        {
            var sb = new StringBuilder();
            sb.Append("/connect:").Append(e.Address(_s.DefaultPort));
            switch (mode)
            {
                case ConnectMode.ViewOnly: sb.Append(" /noinput"); break;
                case ConnectMode.FileTransfer: sb.Append(" /file"); break;
                case ConnectMode.Telnet: sb.Append(" /telnet"); break;
            }
            if (_s.Stretch && (mode == ConnectMode.FullControl || mode == ConnectMode.ViewOnly)) sb.Append(" /stretch");
            if (_s.PassPhonebookSwitch && !string.IsNullOrEmpty(_s.PhonebookPath)) sb.Append(" /pb:\"").Append(_s.PhonebookPath).Append('"');
            if (!string.IsNullOrEmpty(_s.ExtraArgs)) sb.Append(' ').Append(_s.ExtraArgs.Trim());
            return sb.ToString();
        }

        public Process StartConnection(PbEntry e, ConnectMode mode)
        {
            ValidateExe();
            var psi = new ProcessStartInfo(ExePath, BuildArgs(e, mode))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath)
            };
            return Process.Start(psi);
        }

        // ---------------- UI Automation: чтение и управление телефонной книгой ----------------

        static readonly Condition TreeItemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem);
        // SysListView32 в режиме «Список/Значки» - List/ListItem, в режиме «Таблица» - DataGrid/DataItem
        static readonly Condition ListItemCond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
        static readonly Condition ListCond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));

        static AutomationElement FindFirst(AutomationElement root, ControlType t)
        {
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, t));
        }

        static void Expand(AutomationElement e)
        {
            object p;
            if (!e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p)) return;
            var ec = (ExpandCollapsePattern)p;
            try
            {
                if (ec.Current.ExpandCollapseState != ExpandCollapseState.LeafNode &&
                    ec.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                {
                    ec.Expand();
                    Thread.Sleep(120);
                }
            }
            catch (InvalidOperationException) { }
        }

        static void Select(AutomationElement e)
        {
            object p;
            if (e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out p))
                try { ((ScrollItemPattern)p).ScrollIntoView(); } catch { }
            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p))
                ((SelectionItemPattern)p).Select();
        }

        static readonly Regex IpRx = new Regex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})(?::(\d{1,5}))?\s*$");
        static readonly Regex HostRx = new Regex(@"^\s*([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)(?::(\d{1,5}))?\s*$");

        /// <summary>Разбирает "host" или "host:port". allowNames=false - принимать только IPv4.</summary>
        public static bool TryParseHost(string s, bool allowNames, out string host, out int port)
        {
            host = null; port = 0;
            if (string.IsNullOrEmpty(s)) return false;
            var m = IpRx.Match(s);
            if (!m.Success && allowNames) m = HostRx.Match(s);
            if (!m.Success) return false;
            host = m.Groups[1].Value;
            if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out port);
            return true;
        }

        /// <summary>Считывает структуру папок и подключений из открытого окна Radmin Viewer.</summary>
        public List<PbEntry> ScanPhonebook(IntPtr main, Action<string> log)
        {
            var root = AutomationElement.FromHandle(main);
            var tree = FindFirst(root, ControlType.Tree);
            if (tree == null) throw new ApplicationException("В окне Radmin Viewer не найдено дерево папок.");
            var list = root.FindFirst(TreeScope.Descendants, ListCond);

            var result = new List<PbEntry>();
            foreach (AutomationElement item in tree.FindAll(TreeScope.Children, TreeItemCond))
                ScanTreeItem(item, "", list, result, log, 0, IsRootItem(item));

            int noHost = result.Count(e => e.Host.Length == 0);
            if (noHost > 0)
                log("У " + noHost + " узлов не удалось прочитать адрес - включите в Radmin вид «Таблица» (Details) " +
                    "с колонкой адреса, либо подключайтесь к ним способом «Через телефонную книгу».");
            return result;
        }

        /// <summary>Корневой элемент «\» в Radmin - сама телефонная книга, в путь папок не входит.</summary>
        static bool IsRootItem(AutomationElement e)
        {
            string n = (e.Current.Name ?? "").Trim();
            return n == "\\" || n == "/" || n.Length == 0;
        }

        void ScanTreeItem(AutomationElement item, string parent, AutomationElement list, List<PbEntry> res, Action<string> log, int depth, bool isRoot)
        {
            if (depth > 32) return;
            string name = item.Current.Name ?? "";
            string path = isRoot ? "" : parent.Length == 0 ? name : parent + "/" + name;
            Expand(item);
            var kids = item.FindAll(TreeScope.Children, TreeItemCond).Cast<AutomationElement>().ToList();
            var kidNames = new HashSet<string>(kids.Select(k => k.Current.Name ?? ""), StringComparer.OrdinalIgnoreCase);

            if (list != null)
            {
                try
                {
                    if (depth <= 2) log("Чтение папки: " + (path.Length == 0 ? "\\" : path));
                    Select(item);
                    Thread.Sleep(300);
                    foreach (var e in ReadList(list, path))
                        if (!kidNames.Contains(e.Name)) res.Add(e);
                }
                catch (Exception ex) { log("Папка «" + path + "»: " + ex.Message); }
            }
            else if (kids.Count == 0 && depth > 0)
            {
                // В дереве нет отдельного списка - листья дерева считаем подключениями.
                string h; int p;
                var e = new PbEntry { Folder = parent, Name = name, Source = "radmin" };
                if (TryParseHost(name, false, out h, out p)) { e.Host = h; e.Port = p; }
                res.Add(e);
                return;
            }

            foreach (var k in kids) ScanTreeItem(k, path, list, res, log, depth + 1, false);
        }

        IEnumerable<PbEntry> ReadList(AutomationElement list, string folder)
        {
            foreach (AutomationElement it in list.FindAll(TreeScope.Children, ListItemCond))
            {
                string name = (it.Current.Name ?? "").Trim();
                if (name.Length == 0 || name == "..") continue;
                var e = new PbEntry { Folder = folder, Name = name, Source = "radmin" };
                string h; int p;
                if (TryParseHost(name, false, out h, out p)) { e.Host = h; e.Port = p; }
                else
                {
                    // В режиме «Таблица» колонки (Компьютер | Адрес | Порт) - дочерние элементы строки.
                    foreach (AutomationElement cell in it.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        string t = (cell.Current.Name ?? "").Trim();
                        if (t == name || t.Length == 0) continue;
                        int port;
                        if (e.Host.Length == 0 && TryParseHost(t, true, out h, out p)) { e.Host = h; if (p > 0) e.Port = p; }
                        else if (e.Host.Length > 0 && e.Port == 0 && int.TryParse(t, out port) && port > 0 && port < 65536) e.Port = port;
                    }
                }
                yield return e;
            }
        }

        static readonly Dictionary<ConnectMode, string[]> ModeNames = new Dictionary<ConnectMode, string[]>
        {
            // Radmin Viewer 3 (RU): «Управление мышью и клавиатурой удаленного компьютера», «Просмотр экрана удаленного компьютера»
            { ConnectMode.FullControl, new[] { "Full Control", "Полное управление", "Управление мышью", "mouse and keyboard", "mouse & keyboard", "Управление" } },
            { ConnectMode.ViewOnly, new[] { "View Only", "Только просмотр", "Просмотр экрана", "View remote", "Просмотр", "View" } },
            { ConnectMode.FileTransfer, new[] { "File Transfer", "Передача файлов", "Обмен файлами", "файл" } },
            { ConnectMode.Telnet, new[] { "Telnet" } },
        };

        /// <summary>
        /// Раскрывает папки по пути, выделяет узел и запускает подключение в выбранном режиме
        /// (кнопка режима на панели инструментов, иначе Enter). Используются сохранённые в Radmin учётные данные.
        /// </summary>
        public void ConnectViaPhonebook(IntPtr main, PbEntry entry, ConnectMode mode, Func<bool> newWindowAppeared, Action<string> log)
        {
            var root = AutomationElement.FromHandle(main);
            var tree = FindFirst(root, ControlType.Tree);
            if (tree == null) throw new ApplicationException("В окне Radmin Viewer не найдено дерево папок.");
            var list = root.FindFirst(TreeScope.Descendants, ListCond);

            AutomationElement current = tree;
            var tops = tree.FindAll(TreeScope.Children, TreeItemCond);
            if (tops.Count == 1 && IsRootItem(tops[0])) current = tops[0];
            string[] parts = entry.Folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (current != tree) Expand(current);
                var next = current.FindFirst(TreeScope.Children, new AndCondition(TreeItemCond,
                    new PropertyCondition(AutomationElement.NameProperty, part, PropertyConditionFlags.IgnoreCase)));
                if (next == null) throw new ApplicationException("Папка «" + part + "» не найдена в телефонной книге Radmin.");
                current = next;
            }
            if (current != tree) { Expand(current); Select(current); Thread.Sleep(300); }

            AutomationElement target = null;
            var nameCond = new PropertyCondition(AutomationElement.NameProperty, entry.Name, PropertyConditionFlags.IgnoreCase);
            if (list != null) target = list.FindFirst(TreeScope.Children, new AndCondition(ListItemCond, nameCond));
            if (target == null && current != tree) target = current.FindFirst(TreeScope.Children, new AndCondition(TreeItemCond, nameCond));
            if (target == null) throw new ApplicationException("Узел «" + entry.Name + "» не найден в папке «" + entry.Folder + "».");
            Select(target);
            Thread.Sleep(150);

            var res = InvokeModeButton(root, mode);
            if (res == ModeResult.NotFound) log("Кнопка режима " + mode + " не найдена - подключаю текущим режимом Radmin.");
            // Кнопка-команда может сама начать подключение - даём ей время
            if (res == ModeResult.Invoked && WaitFor(newWindowAppeared, 2500)) return;

            // Кнопка лишь выбрала режим (переключатель) - открываем выделенный узел клавишей Enter
            IntPtr lh = IntPtr.Zero;
            var holder = list != null && target.Current.ControlType != ControlType.TreeItem ? list : tree;
            try { lh = new IntPtr(holder.Current.NativeWindowHandle); } catch { }
            if (lh == IntPtr.Zero) return;
            Native.PostEnter(lh);
            if (WaitFor(newWindowAppeared, 2500)) return;

            // Enter не сработал - двойной щелчок по элементу
            try
            {
                var r = target.Current.BoundingRectangle;
                var pt = new Native.POINT { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) };
                Native.ScreenToClient(lh, ref pt);
                Native.PostMessage(lh, 0x0203 /*WM_LBUTTONDBLCLK*/, new IntPtr(1), new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF)));
                log("Enter не открыл подключение - отправлен двойной щелчок.");
            }
            catch (Exception ex) { log("Не удалось отправить двойной щелчок: " + ex.Message); }
        }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(150);
            }
            return false;
        }

        enum ModeResult { NotFound, Invoked, Toggled }

        ModeResult InvokeModeButton(AutomationElement root, ConnectMode mode)
        {
            var buttons = root.FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)))
                .Cast<AutomationElement>().ToList();
            foreach (var wanted in ModeNames[mode])
            {
                var b = buttons.FirstOrDefault(x => string.Equals((x.Current.Name ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                     ?? buttons.FirstOrDefault(x => (x.Current.Name ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
                if (b == null) continue;
                object p;
                if (b.TryGetCurrentPattern(TogglePattern.Pattern, out p))
                {
                    // Переключатель режима: нажимаем только если он ещё не включён (иначе снимем выбор)
                    var tp = (TogglePattern)p;
                    if (tp.Current.ToggleState != ToggleState.On) tp.Toggle();
                    return ModeResult.Toggled;
                }
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return ModeResult.Invoked; }
                if (b.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return ModeResult.Toggled; }
            }
            return ModeResult.NotFound;
        }

        // ---------------- диалоги Radmin ----------------

        public static DialogInfo InspectDialog(IntPtr dlg)
        {
            var di = new DialogInfo { Hwnd = dlg, Title = Native.GetText(dlg) };
            var texts = new List<string>();
            var edits = new List<IntPtr>();
            foreach (var c in Native.ChildWindows(dlg))
            {
                if (!Native.IsWindowVisible(c)) continue;
                string cls = Native.GetClass(c);
                if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.EndsWith("Edit", StringComparison.OrdinalIgnoreCase))
                {
                    if ((Native.GetStyle(c) & Native.WS_DISABLED) != 0) continue;
                    if ((Native.GetStyle(c) & Native.ES_PASSWORD) != 0) { if (di.PasswordEdit == IntPtr.Zero) di.PasswordEdit = c; }
                    else edits.Add(c);
                }
                else if (cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                {
                    string t = Native.GetText(c).Replace("&", "").Trim();
                    if (Native.GetDlgCtrlID(c) == Native.IDOK || t.Equals("OK", StringComparison.OrdinalIgnoreCase) || t.Equals("ОК", StringComparison.OrdinalIgnoreCase))
                        di.OkButton = c;
                }
                else if (cls.StartsWith("msctls_progress", StringComparison.OrdinalIgnoreCase))
                    di.HasProgress = true;
                else if (cls.Equals("Static", StringComparison.OrdinalIgnoreCase))
                {
                    string t = Native.GetText(c).Trim();
                    if (t.Length > 0) texts.Add(t);
                }
            }
            if (di.PasswordEdit != IntPtr.Zero)
            {
                // Поле логина - ближайшее обычное поле над полем пароля.
                var pr = Native.GetRect(di.PasswordEdit);
                di.UserEdit = edits.Where(e => Native.GetRect(e).Top < pr.Top)
                                   .OrderByDescending(e => Native.GetRect(e).Top).FirstOrDefault();
            }
            di.Text = string.Join(" ", texts);
            return di;
        }

        public static void FillCredentials(DialogInfo di, string login, string password)
        {
            if (di.UserEdit != IntPtr.Zero && !string.IsNullOrEmpty(login)) Native.SetText(di.UserEdit, login);
            Native.SetText(di.PasswordEdit, password);
            Thread.Sleep(50);
            if (di.OkButton != IntPtr.Zero) Native.ClickButton(di.Hwnd, di.OkButton);
            else Native.PostEnter(di.PasswordEdit);
        }

        public static void CloseDialog(DialogInfo di)
        {
            if (di.OkButton != IntPtr.Zero) Native.ClickButton(di.Hwnd, di.OkButton);
            else Native.PostMessage(di.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
