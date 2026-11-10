using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>
    /// Периодически читает номер смены из окон подключений (снимок окна + OCR).
    /// Окна обрабатываются по одному в фоновом потоке. В подключение ничего не отправляется.
    /// </summary>
    internal class ShiftMonitor : IDisposable
    {
        /// <summary>Окно, которое сейчас снимается: раскладка по сетке его не трогает.</summary>
        public static volatile IntPtr Capturing = IntPtr.Zero;

        readonly AppSettings _s;
        readonly ConnectionManager _cm;
        readonly Control _ui;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        bool _busy, _warnedNoOcr;

        public event Action<string> Log;

        public ShiftMonitor(AppSettings s, ConnectionManager cm, Control ui)
        {
            _s = s; _cm = cm; _ui = ui;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
        }

        public void Dispose() { _timer.Stop(); }

        public bool Busy { get { return _busy; } }

        /// <summary>Проверить все открытые окна сейчас, не дожидаясь интервала.</summary>
        public void CheckNow()
        {
            if (!ShiftOcr.Available) { DoLog("Проверка смен недоступна: в Windows нет OCR для русского/английского языка."); return; }
            foreach (var s in _cm.ConnectedSessions()) s.NextShiftCheck = DateTime.MinValue;
            if (_busy) { DoLog("Проверка смен уже идёт - окна будут проверены сразу после неё."); return; }
            Tick(true);
        }

        void DoLog(string m) { if (Log != null) Log(m); }

        void Tick(bool force = false)
        {
            if (_busy || (!_s.ShiftCheck && !force)) return;
            if (!ShiftOcr.Available)
            {
                if (!_warnedNoOcr) { _warnedNoOcr = true; DoLog("Проверка смен отключена: OCR Windows недоступен (нужен языковой пакет ru или en)."); }
                return;
            }
            var now = DateTime.Now;
            var due = _cm.ConnectedSessions().Where(x => now >= x.NextShiftCheck).ToList();
            if (due.Count == 0) return;

            _busy = true;
            var jobs = due.Select(x => new KeyValuePair<Session, IntPtr>(x, x.Hwnd)).ToList();
            bool enlarge = _s.ShiftEnlarge;
            IntPtr fg = Native.GetForegroundWindow();
            var t = new Thread(delegate()
            {
                try
                {
                    foreach (var job in jobs)
                    {
                        IntPtr h = job.Value;
                        ShiftReading r;
                        try
                        {
                            if (!Native.IsWindow(h)) continue;
                            Bitmap shot;
                            // Окно, с которым пользователь сейчас работает, не двигаем - снимаем как есть
                            bool mayEnlarge = enlarge && h != fg && h != Native.GetForegroundWindow();
                            Capturing = h;
                            try
                            {
                                var wa = Screen.FromHandle(h).WorkingArea;
                                shot = ShiftOcr.Capture(h, mayEnlarge, 1000, new Size(1300, 1000), wa);
                            }
                            finally { Capturing = IntPtr.Zero; }
                            if (shot == null) r = new ShiftReading { Error = "окно свёрнуто - снимок невозможен" };
                            else using (shot) r = ShiftOcr.Read(shot, job.Key.ShiftNo);
                        }
                        catch (Exception ex) { r = new ShiftReading { Error = ex.Message }; }
                        var sess = job.Key;
                        var res = r;
                        Post(delegate { Apply(sess, res); });
                        Thread.Sleep(150);
                    }
                }
                finally { Post(delegate { _busy = false; _cm.NotifyChanged(); }); }
            }) { IsBackground = true, Name = "ShiftOcr" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void Post(Action a)
        {
            try { if (!_ui.IsDisposed) _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        static string Describe(int? n)
        {
            if (n == null) return "неизвестно";
            return n == 0 ? "закрыта" : "открыта №" + n;
        }

        void Apply(Session s, ShiftReading r)
        {
            s.ShiftCheckedAt = DateTime.Now;
            s.NextShiftCheck = DateTime.Now.AddMinutes(Math.Max(1, _s.ShiftIntervalMin));
            if (r.Number != null)
            {
                int? old = s.ShiftNo;
                s.ShiftNo = r.Number;
                s.ShiftRaw = r.Raw;
                s.ShiftError = null;
                if (old != r.Number)
                    DoLog("[" + s.Name + "] Смена: " + (old == null ? "" : Describe(old) + " → ") + Describe(r.Number) + "   (OCR: " + r.Raw + ")");
            }
            else
            {
                bool first = s.ShiftError == null;
                s.ShiftError = r.Error ?? "номер смены не найден - строка статуса не видна в окне";
                s.ShiftRaw = r.Raw;
                if (first) DoLog("[" + s.Name + "] Смена не прочитана: " + s.ShiftError + (r.Raw.Length > 0 ? "   (OCR: " + r.Raw + ")" : ""));
            }
            _cm.NotifyChanged();
        }
    }
}
