using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace RadminGrid
{
    /// <summary>Результат чтения номера смены.</summary>
    internal class ShiftReading
    {
        public int? Number;          // null - не прочитано; 0 - смена закрыта
        public string Raw = "";      // распознанная строка, для подсказки и журнала
        public string Error;
    }

    /// <summary>
    /// Чтение номера смены из окна Radmin: локальный снимок окна (PrintWindow) + встроенный OCR Windows 10.
    /// На удалённый компьютер ничего не отправляется - ни щелчков, ни клавиш.
    /// </summary>
    internal static class ShiftOcr
    {
        static OcrEngine _engine;
        static bool _engineTried;

        public static bool Available
        {
            get
            {
                if (!_engineTried)
                {
                    _engineTried = true;
                    try
                    {
                        _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ru")) ??
                                  OcrEngine.TryCreateFromUserProfileLanguages();
                    }
                    catch { _engine = null; }
                }
                return _engine != null;
            }
        }

        // Значок «№» OCR читает как «NQ», «N?», «Nº», а иногда как «N2»/«N9» - лишняя цифра перед номером.
        // Группы: sign - «№» или «N», junk - нецифровой мусор после N, num - цифры.
        const string NumberSign = @"(?<sign>№|N)\s*(?<junk>[^\s\d(]*)(?<num>\d{1,8})";
        const string ShiftWord = @"(?:[МмMm][iІіIl1][НнHh][аАaA]|[Сс]мена)";
        // «NQ3292 (Оператор 6)», «N23292 (OnepaTop 6)» - номер перед «(Оп…»
        static readonly Regex BeforeOperator = new Regex(NumberSign + @"(?=\s*\(\s*[ОоOo0][пnПN])", RegexOptions.Compiled);
        // «Зміна №3292», «3MiHa NQ3292»
        static readonly Regex AfterShift = new Regex(ShiftWord + @"\s*" + NumberSign, RegexOptions.Compiled);
        // «Смена 3292» без значка
        static readonly Regex AfterShiftPlain = new Regex(ShiftWord + @"\s*[:.]?\s*(?<num>\d{1,8})", RegexOptions.Compiled);
        static readonly Regex ClosedWord = new Regex(ShiftWord + @"\s+(?:закрит|закрыт)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ShiftReading Parse(string text)
        {
            var r = new ShiftReading { Raw = text ?? "" };
            if (string.IsNullOrEmpty(text)) return r;
            if (ClosedWord.IsMatch(text)) { r.Number = 0; return r; }
            var m = BeforeOperator.Match(text);
            if (!m.Success) m = AfterShift.Match(text);
            if (!m.Success) m = AfterShiftPlain.Match(text);
            if (!m.Success) return r;
            string num = m.Groups["num"].Value;
            // «N23292»: после N сразу цифра - это остаток значка «№», отбрасываем её
            if (m.Groups["sign"].Success && m.Groups["sign"].Value == "N" && m.Groups["junk"].Value.Length == 0 && num.Length >= 2)
                num = num.Substring(1);
            int n;
            if (int.TryParse(num, out n)) r.Number = n;
            return r;
        }

        /// <summary>Распознаёт верхнюю часть снимка (там строка статуса), при неудаче - весь снимок.</summary>
        public static ShiftReading Read(Bitmap shot, int? expected = null)
        {
            if (!Available) return new ShiftReading { Error = "OCR Windows недоступен (нет языкового пакета ru/en)" };
            // Верхние 35% окна (там строка статуса) распознаются дважды - при увеличении ×2 и ×3.
            // Номер принимается, если прочтения совпали; иначе решает третье прочтение всего снимка.
            int bandH = Math.Min(shot.Height, Math.Max(40, shot.Height * 35 / 100));
            var part = new Rectangle(0, 0, shot.Width, bandH);
            string t2, t3;
            using (var b2 = Scale(shot, part, 2)) t2 = Recognize(b2);
            var r2 = Parse(t2);
            // Номер не изменился с прошлой проверки - второе прочтение не нужно (втрое меньше работы OCR)
            if (expected != null && r2.Number == expected) { r2.Raw = ShiftLine(t2); return r2; }
            using (var b3 = Scale(shot, part, 3)) t3 = Recognize(b3);
            var r3 = Parse(t3);
            if (r2.Number != null && r2.Number == r3.Number) { r2.Raw = ShiftLine(t2); return r2; }

            string full = Recognize(shot);
            var rf = Parse(full);
            foreach (var cand in new[] { r2, r3 })
                if (cand.Number != null && cand.Number == rf.Number) { rf.Raw = ShiftLine(full); return rf; }

            // Прочтения разошлись или номера нет - не выдумываем значение
            var res = new ShiftReading { Raw = Trim(ShiftLine(t2.Length > 0 ? t2 : full)) };
            if (r2.Number != null || r3.Number != null || rf.Number != null)
                res.Error = "прочтения не совпали: " + r2.Number + " / " + r3.Number + " / " + rf.Number;
            return res;
        }

        static string ShiftLine(string text)
        {
            foreach (var line in text.Split('\n'))
                if (BeforeOperator.IsMatch(line) || AfterShift.IsMatch(line) || ClosedWord.IsMatch(line)) return Trim(line);
            return Trim(text);
        }

        static string Trim(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        static Bitmap Scale(Bitmap src, Rectangle part, int k)
        {
            var dst = new Bitmap(part.Width * k, part.Height * k, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), part, GraphicsUnit.Pixel);
            }
            return dst;
        }

        static string Recognize(Bitmap bmp)
        {
            // OCR Windows ограничен по размеру изображения
            if (bmp.Width > OcrEngine.MaxImageDimension || bmp.Height > OcrEngine.MaxImageDimension)
            {
                double k = Math.Min(OcrEngine.MaxImageDimension / (double)bmp.Width, OcrEngine.MaxImageDimension / (double)bmp.Height);
                using (var small = new Bitmap(bmp, (int)(bmp.Width * k), (int)(bmp.Height * k)))
                    return Recognize(small);
            }
            // Пиксели BGRA напрямую в SoftwareBitmap - без кодирования в PNG
            byte[] px;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                px = new byte[bmp.Width * bmp.Height * 4];
                for (int y = 0; y < bmp.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), px, y * bmp.Width * 4, bmp.Width * 4);
            }
            finally { bmp.UnlockBits(data); }

            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(px);
            var buffer = writer.DetachBuffer();
            using (var sb = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied))
            {
                var res = Wait(_engine.RecognizeAsync(sb));
                var lines = new System.Text.StringBuilder();
                foreach (var line in res.Lines) lines.Append(line.Text).Append('\n');
                return lines.ToString();
            }
        }

        /// <summary>Ожидание асинхронной операции WinRT без System.Runtime.WindowsRuntime (штатный csc не видит единый Windows.winmd).</summary>
        static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var info = (Windows.Foundation.IAsyncInfo)op;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (info.Status == Windows.Foundation.AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > 15000) { info.Cancel(); throw new TimeoutException("OCR не ответил за 15 с"); }
                Thread.Sleep(10);
            }
            if (info.Status != Windows.Foundation.AsyncStatus.Completed)
                throw new InvalidOperationException("OCR: " + info.Status + (info.ErrorCode != null ? " " + info.ErrorCode.Message : ""));
            return op.GetResults();
        }

        // ------------------------------------------------------------------ снимок окна

        /// <summary>
        /// Снимок окна. Если окно меньше minWidth - на время съёмки окно увеличивается ПОД всеми окнами
        /// (его не видно), затем возвращается на место и на прежнее место в стопке окон.
        /// Меняется только локальное окно; удалённый компьютер не затрагивается.
        /// </summary>
        public static Bitmap Capture(IntPtr h, bool enlarge, int minWidth, Size bigSize, Rectangle workArea)
        {
            if (!Native.IsWindow(h) || Native.IsIconic(h)) return null;
            Rectangle r = Native.GetRect(h);
            // Окно на скрытой вкладке (за пределами экрана) не перерисовывается - его всегда выносим под остальные окна
            bool parked = GridLayout.IsParked(h);
            if (!parked && (!enlarge || r.Width >= minWidth)) return Snap(h, r.Size);
            if (parked && !enlarge) bigSize = r.Size;

            IntPtr prev = Native.GetWindow(h, 3 /*GW_HWNDPREV*/);
            const uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            try
            {
                Native.SetWindowPos(h, new IntPtr(1) /*HWND_BOTTOM*/, workArea.X, workArea.Y,
                    Math.Min(bigSize.Width, workArea.Width), Math.Min(bigSize.Height, workArea.Height), flags);
                Thread.Sleep(900);   // даём Radmin перерисовать содержимое в новом размере
                return Snap(h, Native.GetRect(h).Size);
            }
            finally
            {
                Native.SetWindowPos(h, prev, r.X, r.Y, r.Width, r.Height, flags);
            }
        }

        static Bitmap Snap(IntPtr h, Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return null;
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { Native.PrintWindow(h, hdc, 2 /*PW_RENDERFULLCONTENT*/); }
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp;
        }
    }
}
