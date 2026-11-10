using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    public enum SessStatus
    {
        Queued, Checking, Launching, Connecting, WaitingAuth, LoggingIn, AuthFailed, Connected,
        Unreachable, Failed, Dropped, Closed
    }

    internal class Session
    {
        public PbEntry Entry;
        public int Order;
        public int Attempt;
        public SessStatus Status = SessStatus.Queued;
        public string Message = "";
        public DateTime Since = DateTime.Now;
        public DateTime LaunchedAt = DateTime.MinValue;
        public DateTime ConnectedAt = DateTime.MinValue;
        public ConnectMethod Method;
        public ConnectMode Mode;
        public Process Proc;
        public int Pid;
        public IntPtr Hwnd;
        public int AuthSubmits;
        public DateTime AuthSubmittedAt;
        public bool UserClosed;
        public bool WasConnected;
        public int Reconnects;
        public DateTime? ReconnectAt;
        public HashSet<IntPtr> HandledDialogs = new HashSet<IntPtr>();
        public int OrigStyle;
        public bool StyleChanged;
        public bool External;      // окно открыто вручную в Radmin и подхвачено
        public bool HandedOff;     // Radmin.exe передал /connect уже запущенному экземпляру и завершился

        // Номер смены из окна (OCR): null - ещё не прочитан, 0 - смена закрыта
        public int? ShiftNo;
        public string ShiftRaw = "";
        public string ShiftError;
        public DateTime ShiftCheckedAt = DateTime.MinValue;
        public DateTime NextShiftCheck = DateTime.MinValue;

        public string Name { get { return Entry.Display; } }

        /// <summary>Процесс/окно запущены и отслеживаются.</summary>
        public bool IsLive
        {
            get
            {
                return Status == SessStatus.Launching || Status == SessStatus.Connecting || Status == SessStatus.WaitingAuth ||
                       Status == SessStatus.LoggingIn || Status == SessStatus.AuthFailed || Status == SessStatus.Connected;
            }
        }

        public bool IsBusy { get { return IsLive || Status == SessStatus.Queued || Status == SessStatus.Checking; } }

        public static string StatusText(SessStatus st)
        {
            switch (st)
            {
                case SessStatus.Queued: return "В очереди";
                case SessStatus.Checking: return "Проверка порта";
                case SessStatus.Launching: return "Запуск";
                case SessStatus.Connecting: return "Подключение";
                case SessStatus.WaitingAuth: return "Ждёт пароль";
                case SessStatus.LoggingIn: return "Вход…";
                case SessStatus.AuthFailed: return "Ошибка входа";
                case SessStatus.Connected: return "Подключено";
                case SessStatus.Unreachable: return "Недоступен";
                case SessStatus.Failed: return "Не открылось";
                case SessStatus.Dropped: return "Отвалилось";
                case SessStatus.Closed: return "Закрыто";
            }
            return st.ToString();
        }
    }

    /// <summary>Очередь запуска подключений и отслеживание их состояния по окнам Radmin.</summary>
    internal class ConnectionManager : IDisposable
    {
        class WinInfo
        {
            public IntPtr H;
            public int Pid;
            public string Cls, Title;
            public bool IsDialog;
            public System.Drawing.Rectangle Rect;
        }

        readonly AppSettings _s;
        readonly RadminController _rc;
        readonly Control _ui;
        readonly System.Windows.Forms.Timer _timer;
        readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        readonly Dictionary<IntPtr, DateTime> _firstSeen = new Dictionary<IntPtr, DateTime>();
        int _order;
        bool _changed;

        public readonly List<Session> Sessions = new List<Session>();
        public event Action Changed;
        public event Action<string> Log;
        public event Action ArrangeRequested;

        public ConnectionManager(AppSettings s, RadminController rc, Control ui)
        {
            _s = s; _rc = rc; _ui = ui;
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RadminWorker" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            _timer = new System.Windows.Forms.Timer { Interval = 700 };
            _timer.Tick += delegate { Poll(); };
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _queue.CompleteAdding();
        }

        void WorkerLoop()
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception ex) { Post(() => DoLog("Ошибка фоновой задачи: " + ex.Message)); }
            }
        }

        void Post(Action a)
        {
            try { if (!_ui.IsDisposed) _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void DoLog(string msg) { if (Log != null) Log(msg); }

        // ------------------------------------------------------------------ команды

        public void Connect(IEnumerable<PbEntry> entries)
        {
            foreach (var e in entries)
            {
                var s = Sessions.FirstOrDefault(x => x.Entry.Key == e.Key);
                if (s != null && s.IsBusy)
                {
                    DoLog("[" + s.Name + "] уже подключается/подключено - пропуск.");
                    continue;
                }
                if (s == null)
                {
                    s = new Session { Entry = e, Order = _order++ };
                    Sessions.Add(s);
                }
                s.Reconnects = 0;
                StartAttempt(s);
            }
            _changed = true;
            FireChanged();
        }

        public void Reconnect(Session s)
        {
            if (s.External)
            {
                DoLog("[" + s.Name + "] открыто вручную в Radmin - адрес неизвестен, переподключите его в Radmin.");
                return;
            }
            if (s.IsLive) CloseWindows(s);
            s.Reconnects = 0;
            StartAttempt(s);
            FireChanged();
        }

        public void ReconnectFailed()
        {
            foreach (var s in Sessions.Where(x => !x.External).Where(x => x.Status == SessStatus.Dropped || x.Status == SessStatus.Failed ||
                                                  x.Status == SessStatus.Unreachable || x.Status == SessStatus.AuthFailed).ToList())
                Reconnect(s);
        }

        public void Disconnect(Session s)
        {
            s.UserClosed = true;
            s.ReconnectAt = null;
            if (s.Status == SessStatus.Connected && s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd))
            {
                CloseWindows(s);   // статус «Закрыто» выставит Poll, когда окно исчезнет
                return;
            }
            CloseWindows(s);
            s.Attempt++;           // отменяет запуск из очереди/проверки
            Set(s, SessStatus.Closed, "закрыто пользователем");
        }

        public void DisconnectAll()
        {
            foreach (var s in Sessions.ToList()) if (s.IsBusy || s.ReconnectAt != null) Disconnect(s);
        }

        public void Remove(Session s)
        {
            if (s.IsBusy) Disconnect(s);
            Sessions.Remove(s);
            FireChanged();
        }

        public void ClearFinished()
        {
            Sessions.RemoveAll(x => !x.IsBusy && x.ReconnectAt == null);
            FireChanged();
        }

        public void Focus(Session s)
        {
            if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) return;
            if (Native.IsIconic(s.Hwnd)) Native.ShowWindow(s.Hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(s.Hwnd);
        }

        public List<Session> ConnectedSessions()
        {
            var list = Sessions.Where(x => x.Status == SessStatus.Connected && x.Hwnd != IntPtr.Zero && Native.IsWindow(x.Hwnd)).ToList();
            Comparison<string> nat = (a, b) => Native.StrCmpLogicalW(a ?? "", b ?? "");
            switch (_s.GridSort)
            {
                case "По имени":
                    list.Sort((a, b) => nat(a.Name, b.Name));
                    break;
                case "По папке и имени":
                    list.Sort((a, b) => { int r = nat(a.Entry.Folder, b.Entry.Folder); return r != 0 ? r : nat(a.Name, b.Name); });
                    break;
                case "По адресу":
                    list.Sort((a, b) => { int r = nat(a.Entry.Host, b.Entry.Host); return r != 0 ? r : nat(a.Name, b.Name); });
                    break;
                default:
                    list.Sort((a, b) => a.Order.CompareTo(b.Order));
                    break;
            }
            return list;
        }

        void CloseWindows(Session s)
        {
            if (s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd))
                Native.PostMessage(s.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (s.Proc != null)
            {
                try
                {
                    if (!s.Proc.HasExited && !s.Proc.CloseMainWindow())
                        s.Proc.Kill();
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ запуск

        void StartAttempt(Session s)
        {
            int attempt = ++s.Attempt;
            s.Proc = null; s.Pid = 0; s.Hwnd = IntPtr.Zero;
            s.AuthSubmits = 0; s.UserClosed = false; s.ReconnectAt = null; s.HandedOff = false;
            s.HandledDialogs.Clear();
            s.LaunchedAt = DateTime.MinValue;
            s.Mode = _s.Mode;
            s.Method = s.Entry.Host.Length == 0 ? ConnectMethod.Phonebook : _s.Method;

            if (_s.TcpPrecheck && s.Entry.Host.Length > 0)
            {
                Set(s, SessStatus.Checking, "");
                string host = s.Entry.Host;
                int port = s.Entry.Port > 0 ? s.Entry.Port : _s.DefaultPort;
                int timeout = _s.PrecheckTimeoutMs;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string err;
                    bool ok = TcpCheck(host, port, timeout, out err);
                    Post(delegate
                    {
                        if (s.Attempt != attempt) return;
                        if (ok) Enqueue(s, attempt);
                        else Set(s, SessStatus.Unreachable, host + ":" + port + " - " + err);
                    });
                });
            }
            else Enqueue(s, attempt);
        }

        void Enqueue(Session s, int attempt)
        {
            Set(s, SessStatus.Queued, "");
            _queue.Add(delegate { RunLaunch(s, attempt); });
        }

        void RunLaunch(Session s, int attempt)
        {
            if (s.Attempt != attempt) return;
            Post(delegate { if (s.Attempt == attempt) Set(s, SessStatus.Launching, ""); });
            try
            {
                if (s.Method == ConnectMethod.CommandLine)
                {
                    var p = _rc.StartConnection(s.Entry, s.Mode);
                    Post(delegate
                    {
                        if (s.Attempt != attempt) { try { p.Kill(); } catch { } return; }
                        s.Proc = p; s.Pid = p.Id; s.LaunchedAt = DateTime.Now;
                        Set(s, SessStatus.Connecting, "PID " + p.Id);
                    });
                }
                else
                {
                    IntPtr main = _rc.EnsureMainWindow(m => Post(() => DoLog(m)));
                    var before = new HashSet<IntPtr>(RadminWindows());
                    Post(delegate
                    {
                        if (s.Attempt != attempt) return;
                        s.LaunchedAt = DateTime.Now;
                        Set(s, SessStatus.Connecting, "через телефонную книгу");
                    });
                    _rc.ConnectViaPhonebook(main, s.Entry, s.Mode,
                        () => RadminWindows().Any(h => !before.Contains(h)),
                        m => Post(() => DoLog("[" + s.Name + "] " + m)));
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Post(delegate { if (s.Attempt == attempt) Set(s, SessStatus.Failed, msg); });
            }
            Thread.Sleep(Math.Max(0, _s.LaunchDelayMs));
        }

        List<IntPtr> RadminWindows()
        {
            var pids = _rc.GetPids();
            return Native.TopLevelWindows().Where(h => Native.IsWindowVisible(h) && pids.Contains(Native.GetPid(h))).ToList();
        }

        static bool TcpCheck(string host, int port, int timeoutMs, out string err)
        {
            err = null;
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { err = "нет ответа (порт закрыт или узел выключен)"; return false; }
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch (SocketException ex) { err = ex.Message; return false; }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        // ------------------------------------------------------------------ состояние

        void Set(Session s, SessStatus st, string msg)
        {
            bool statusChanged = s.Status != st;
            if (!statusChanged && s.Message == msg) return;
            s.Status = st;
            s.Message = msg ?? "";
            if (statusChanged)
            {
                s.Since = DateTime.Now;
                DoLog("[" + s.Name + "] " + Session.StatusText(st) + (string.IsNullOrEmpty(msg) ? "" : ": " + msg));
                if (st == SessStatus.Connected)
                {
                    s.WasConnected = true;
                    s.ConnectedAt = DateTime.Now;
                    s.NextShiftCheck = DateTime.Now.AddSeconds(5);   // дать окну отрисоваться
                    if (ArrangeRequested != null) ArrangeRequested();
                }
                if (st == SessStatus.Dropped || st == SessStatus.Failed || st == SessStatus.Unreachable)
                    ScheduleReconnect(s);
                if (s.WasConnected && (st == SessStatus.Dropped || st == SessStatus.Closed) && ArrangeRequested != null)
                    ArrangeRequested();
            }
            _changed = true;
        }

        void ScheduleReconnect(Session s)
        {
            if (!_s.AutoReconnect || s.UserClosed || s.External || s.Reconnects >= _s.MaxReconnects) return;
            s.Reconnects++;
            s.ReconnectAt = DateTime.Now.AddSeconds(Math.Max(1, _s.ReconnectDelaySec));
            s.Message += " · повтор " + s.Reconnects + "/" + _s.MaxReconnects + " через " + _s.ReconnectDelaySec + " с";
        }

        public void NotifyChanged() { FireChanged(); }

        void FireChanged()
        {
            _changed = false;
            if (Changed != null) Changed();
        }

        void Poll()
        {
            try { PollCore(); }
            catch (Exception ex) { DoLog("Ошибка мониторинга: " + ex.Message); }
            if (_changed) FireChanged();
        }

        void PollCore()
        {
            var now = DateTime.Now;
            foreach (var s in Sessions.Where(x => x.ReconnectAt != null && x.ReconnectAt <= now).ToList())
            {
                int left = s.Reconnects;
                StartAttempt(s);
                s.Reconnects = left;
            }

            var live = Sessions.Where(x => x.IsLive).OrderBy(x => x.LaunchedAt).ToList();
            if (live.Count == 0 && !_s.AdoptManual) return;

            // Снимок окон всех процессов Radmin Viewer
            var pids = _rc.GetPids();
            foreach (var s in live) if (s.Pid > 0) pids.Add(s.Pid);
            var wins = new List<WinInfo>();
            foreach (var h in Native.TopLevelWindows())
            {
                if (!Native.IsWindowVisible(h)) continue;
                int pid = Native.GetPid(h);
                if (!pids.Contains(pid)) continue;
                string cls = Native.GetClass(h);
                wins.Add(new WinInfo { H = h, Pid = pid, Cls = cls, Title = Native.GetText(h), IsDialog = cls == "#32770", Rect = Native.GetRect(h) });
                if (!_firstSeen.ContainsKey(h)) _firstSeen[h] = now;
            }
            foreach (var dead in _firstSeen.Keys.Where(k => !Native.IsWindow(k)).ToList()) _firstSeen.Remove(dead);

            var mainWnds = new HashSet<IntPtr>(wins.Where(w => !w.IsDialog && _rc.IsMainWindow(w.H)).Select(w => w.H));
            var cliPids = new HashSet<int>(Sessions.Where(x => x.Pid > 0).Select(x => x.Pid));
            var claimed = new HashSet<IntPtr>(Sessions.Where(x => x.Hwnd != IntPtr.Zero).Select(x => x.Hwnd));
            foreach (var x in Sessions) claimed.UnionWith(x.HandledDialogs);

            foreach (var s in live)
            {
                var mine = wins.Where(w => !mainWnds.Contains(w.H) && Belongs(s, w, live, cliPids, claimed)).ToList();
                CheckSession(s, mine, claimed);
            }

            if (_s.AdoptManual) AdoptManualWindows(wins, mainWnds, cliPids, claimed, live, now);
        }

        readonly HashSet<IntPtr> _orphanDialogs = new HashSet<IntPtr>();
        readonly HashSet<IntPtr> _otherDialogs = new HashSet<IntPtr>();
        DateTime _lastOrphanMsgAt = DateTime.MinValue;
        string _lastOrphanMsg = "";

        /// <summary>
        /// Окна подключений, открытые вручную в Radmin Viewer (двойной щелчок по узлу), тоже попадают в HUD и сетку.
        /// Сообщения Radmin без владельца запоминаются, чтобы отличить обрыв связи от обычного закрытия окна.
        /// </summary>
        void AdoptManualWindows(List<WinInfo> wins, HashSet<IntPtr> mainWnds, HashSet<int> cliPids, HashSet<IntPtr> claimed, List<Session> live, DateTime now)
        {
            foreach (var w in wins.Where(x => x.IsDialog && !claimed.Contains(x.H) && !cliPids.Contains(x.Pid)))
            {
                // диалог разбирается один раз: дочерние окна не перечисляются каждые 0,7 с
                if (_otherDialogs.Contains(w.H)) continue;
                if (!_orphanDialogs.Contains(w.H))
                {
                    var di = RadminController.InspectDialog(w.H);
                    if (!di.IsMessage) { _otherDialogs.Add(w.H); continue; }
                    _orphanDialogs.Add(w.H);
                    _lastOrphanMsg = di.Text.Length > 0 ? di.Text : di.Title;
                }
                _lastOrphanMsgAt = now;
            }
            _orphanDialogs.RemoveWhere(h => !Native.IsWindow(h));
            _otherDialogs.RemoveWhere(h => !Native.IsWindow(h));

            // Пока окно ждёт своя сессия «телефонной книги» - не забираем чужое
            if (live.Any(x => x.Pid == 0 && x.Hwnd == IntPtr.Zero && !x.External)) return;

            foreach (var w in wins)
            {
                if (w.IsDialog || mainWnds.Contains(w.H) || claimed.Contains(w.H) || cliPids.Contains(w.Pid)) continue;
                if (w.Rect.Width < 200 || w.Rect.Height < 150) continue;
                DateTime seen;
                if (!_firstSeen.TryGetValue(w.H, out seen) || (now - seen).TotalSeconds < 1.5) continue;

                string title = (w.Title ?? "").Trim();
                var s = new Session
                {
                    Entry = new PbEntry { Name = title.Length > 0 ? title : "Окно Radmin", Source = "radmin-manual" },
                    Order = _order++,
                    Hwnd = w.H,
                    External = true,
                    Method = ConnectMethod.Phonebook,
                    LaunchedAt = now,
                    Status = SessStatus.Connecting
                };
                Sessions.Add(s);
                claimed.Add(w.H);
                Set(s, SessStatus.Connected, "открыто вручную в Radmin");
            }
        }

        public bool RecentDropMessage(out string text)
        {
            text = _lastOrphanMsg;
            return (DateTime.Now - _lastOrphanMsgAt).TotalSeconds < 5;
        }

        /// <summary>Окно принадлежит сессии: по PID (командная строка) либо по заголовку/порядку появления (телефонная книга).</summary>
        bool Belongs(Session s, WinInfo w, List<Session> live, HashSet<int> cliPids, HashSet<IntPtr> claimed)
        {
            if (s.Hwnd == w.H || s.HandledDialogs.Contains(w.H)) return true;
            if (claimed.Contains(w.H)) return false;
            if (s.Pid > 0) return w.Pid == s.Pid;
            if (cliPids.Contains(w.Pid)) return false;
            if (TitleMatches(s, w.Title)) return true;
            // Заголовок не подсказывает - отдаём самой ранней ожидающей сессии «телефонной книги», если окно появилось после её запуска.
            if (live.Any(o => o != s && TitleMatches(o, w.Title))) return false;
            DateTime seen;
            if (s.LaunchedAt == DateTime.MinValue || !_firstSeen.TryGetValue(w.H, out seen) || seen < s.LaunchedAt.AddSeconds(-1)) return false;
            var first = live.FirstOrDefault(o => o.Pid == 0 && o.Hwnd == IntPtr.Zero && o.LaunchedAt != DateTime.MinValue);
            return first == s;
        }

        static bool TitleMatches(Session s, string title)
        {
            if (string.IsNullOrEmpty(title) || s.External) return false;
            return ContainsWord(title, s.Entry.Name) || ContainsWord(title, s.Entry.Host);
        }

        /// <summary>Вхождение по границе: «10.0.0.1» не совпадает с «10.0.0.13», «Магазин 1» - с «Магазин 12».</summary>
        static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            int i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = i + word.Length;
                bool leftOk = i == 0 || !IsWordChar(text[i - 1]);
                bool rightOk = end >= text.Length || !IsWordChar(text[end]);
                if (leftOk && rightOk) return true;
                i++;
            }
            return false;
        }

        static bool IsWordChar(char c) { return char.IsLetterOrDigit(c) || c == '.' || c == '_'; }

        void CheckSession(Session s, List<WinInfo> mine, HashSet<IntPtr> claimed)
        {
            bool authOpen = false;

            foreach (var w in mine.Where(x => x.IsDialog))
            {
                var di = RadminController.InspectDialog(w.H);
                if (di.IsAuth)
                {
                    authOpen = true;
                    HandleAuth(s, di);
                    claimed.Add(w.H);
                }
                else if (di.IsMessage && !s.HandledDialogs.Contains(w.H))
                {
                    s.HandledDialogs.Add(w.H);
                    claimed.Add(w.H);
                    string text = di.Text.Length > 0 ? di.Text : di.Title;
                    if (s.Status == SessStatus.Connected) Set(s, SessStatus.Dropped, text);
                    else if (s.AuthSubmits > 0 && s.Hwnd == IntPtr.Zero) Set(s, SessStatus.AuthFailed, text);
                    else Set(s, SessStatus.Failed, text);
                    if (_s.CloseErrorDialogs) RadminController.CloseDialog(di);
                    if (s.Status != SessStatus.AuthFailed) return;
                }
            }

            // Окно подключения
            if (s.Hwnd != IntPtr.Zero)
            {
                if (!Native.IsWindow(s.Hwnd) || !Native.IsWindowVisible(s.Hwnd))
                {
                    s.Hwnd = IntPtr.Zero;
                    string dropText;
                    if (s.External && !s.UserClosed)
                    {
                        if (RecentDropMessage(out dropText)) Set(s, SessStatus.Dropped, dropText);
                        else Set(s, SessStatus.Closed, "окно закрыто в Radmin");
                    }
                    else if (s.UserClosed) Set(s, SessStatus.Closed, "закрыто пользователем");
                    else if (s.Status == SessStatus.Connected) Set(s, SessStatus.Dropped, "окно подключения закрылось / связь потеряна");
                    else Set(s, SessStatus.Failed, "окно подключения закрылось");
                    return;
                }
            }
            else
            {
                var views = mine.Where(w => !w.IsDialog && w.Rect.Width >= 200 && w.Rect.Height >= 150).ToList();
                var view = views.FirstOrDefault(w => s.Pid > 0 || TitleMatches(s, w.Title)) ?? views.FirstOrDefault();
                if (view != null) { s.Hwnd = view.H; claimed.Add(view.H); }
            }

            if (s.Hwnd != IntPtr.Zero && !authOpen && s.Status != SessStatus.Connected)
            {
                Set(s, SessStatus.Connected, s.Pid > 0 ? "PID " + s.Pid : "");
                return;
            }

            // Процесс, запущенный из командной строки, завершился
            if (s.Proc != null)
            {
                bool exited;
                try { exited = s.Proc.HasExited; } catch { exited = false; }
                if (exited)
                {
                    if (!s.UserClosed && s.Hwnd == IntPtr.Zero && !s.HandedOff && s.Status != SessStatus.Connected &&
                        (DateTime.Now - s.LaunchedAt).TotalSeconds < 6 && _rc.GetPids().Count > 0)
                    {
                        // Однокопийный Radmin: команда ушла в уже открытый Radmin Viewer - ждём окно у него
                        s.HandedOff = true;
                        s.Proc = null;
                        s.Pid = 0;
                        s.LaunchedAt = DateTime.Now;
                        Set(s, SessStatus.Connecting, "передано запущенному Radmin Viewer");
                        return;
                    }
                    if (s.UserClosed) Set(s, SessStatus.Closed, "закрыто пользователем");
                    else if (s.Status == SessStatus.Connected) Set(s, SessStatus.Dropped, "процесс Radmin завершился");
                    else Set(s, SessStatus.Failed, "Radmin Viewer закрылся, не открыв подключение");
                    return;
                }
            }

            // Таймаут (не действует, пока ждём ручного ввода пароля)
            if ((s.Status == SessStatus.Connecting || s.Status == SessStatus.LoggingIn || s.Status == SessStatus.Launching) &&
                s.LaunchedAt != DateTime.MinValue &&
                (DateTime.Now - s.LaunchedAt).TotalSeconds > Math.Max(5, _s.ConnectTimeoutSec))
            {
                Set(s, SessStatus.Failed, "таймаут " + _s.ConnectTimeoutSec + " с");
                if (s.Proc != null) { try { s.Proc.Kill(); } catch { } }
            }
        }

        void HandleAuth(Session s, DialogInfo di)
        {
            string pwd = _s.AutoLogin ? _s.GetPassword() : "";
            if (pwd.Length == 0)
            {
                if (s.Status != SessStatus.AuthFailed) Set(s, SessStatus.WaitingAuth, "введите пароль в окне Radmin");
                return;
            }
            if (s.HandledDialogs.Contains(di.Hwnd))
            {
                if (s.Status == SessStatus.LoggingIn && (DateTime.Now - s.AuthSubmittedAt).TotalSeconds > 6)
                    Set(s, SessStatus.AuthFailed, "окно входа не закрылось - проверьте логин/пароль");
                return;
            }
            s.HandledDialogs.Add(di.Hwnd);
            if (s.AuthSubmits >= 1)
            {
                Set(s, SessStatus.AuthFailed, "повторный запрос пароля - неверный логин/пароль, введите вручную");
                return;
            }
            try
            {
                RadminController.FillCredentials(di, _s.Login, pwd);
                s.AuthSubmits++;
                s.AuthSubmittedAt = DateTime.Now;
                Set(s, SessStatus.LoggingIn, "логин/пароль введены автоматически");
            }
            catch (Exception ex)
            {
                Set(s, SessStatus.WaitingAuth, "автоввод не удался: " + ex.Message);
            }
        }
    }
}
