using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Релиз на GitHub, в котором есть RadminGrid.exe.</summary>
    internal class ReleaseInfo
    {
        public Version Version;
        public string Tag = "", Name = "", Notes = "", PageUrl = "";
        public bool Prerelease;
        public DateTime Published;
        public string ExeUrl, ExeApiUrl, ExeDigest;   // digest - "sha256:…", GitHub считает его сам
        public long ExeSize;
        public string SumsUrl, SumsApiUrl;            // SHA256SUMS.txt, если приложен

        public string Title { get { return "v" + Updater.Format(Version) + (Prerelease ? " (предварительная)" : ""); } }
    }

    internal class UpdateException : Exception
    {
        public UpdateException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Обновление через GitHub Releases: поиск новой версии, загрузка, проверка SHA-256, имени и версии сборки,
    /// замена exe. Работающий exe нельзя перезаписать, но можно переименовать - он уходит в RadminGrid.exe.old,
    /// новый встаёт на его место, при следующем запуске .old удаляется. Прав администратора не нужно.
    /// </summary>
    internal static class Updater
    {
        public const string Repo = "barnibeats/RadminGrid";
        public const string AssetName = "RadminGrid.exe";
        public const string SumsName = "SHA256SUMS.txt";
        const string Api = "https://api.github.com/repos/" + Repo;

        public static readonly Version Current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        public static string Format(Version v) { return v == null ? "?" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        /// <summary>«v0.2.0», «0.2», «v0.2.0-beta» → 0.2.0; null, если тег не похож на версию.</summary>
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string s = tag.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (dash >= 0) s = s.Substring(0, dash);
            Version v;
            return Version.TryParse(s.Contains('.') ? s : s + ".0", out v) ? Normalize(v) : null;
        }

        static string ExePath { get { return Application.ExecutablePath; } }

        // =====================================================================  поиск

        /// <summary>Самый новый релиз с RadminGrid.exe (черновики пропускаются, предварительные - по настройке).</summary>
        public static ReleaseInfo FindLatest(bool prerelease)
        {
            string json = Encoding.UTF8.GetString(Get(Api + "/releases?per_page=30", "application/vnd.github+json"));
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var list = ser.DeserializeObject(json) as object[];
            if (list == null) throw new UpdateException("Сервер обновлений вернул неожиданный ответ.");

            ReleaseInfo best = null;
            foreach (var o in list.OfType<Dictionary<string, object>>())
            {
                if (Bool(o, "draft")) continue;
                bool pre = Bool(o, "prerelease");
                if (pre && !prerelease) continue;
                var v = ParseTag(Str(o, "tag_name"));
                if (v == null || (best != null && v <= best.Version)) continue;

                var r = new ReleaseInfo
                {
                    Version = v, Tag = Str(o, "tag_name"), Name = Str(o, "name"), Notes = Str(o, "body"),
                    PageUrl = Str(o, "html_url"), Prerelease = pre
                };
                DateTime.TryParse(Str(o, "published_at"), out r.Published);
                foreach (var a in ((o.ContainsKey("assets") ? o["assets"] as object[] : null) ?? new object[0]).OfType<Dictionary<string, object>>())
                {
                    string name = Str(a, "name");
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        r.ExeUrl = Str(a, "browser_download_url"); r.ExeApiUrl = Str(a, "url"); r.ExeDigest = Str(a, "digest");
                        long.TryParse(Convert.ToString(a.ContainsKey("size") ? a["size"] : null), out r.ExeSize);
                    }
                    else if (name.Equals(SumsName, StringComparison.OrdinalIgnoreCase))
                    {
                        r.SumsUrl = Str(a, "browser_download_url"); r.SumsApiUrl = Str(a, "url");
                    }
                }
                if (r.ExeUrl != null) best = r;
            }
            return best;
        }

        public static bool IsNewer(ReleaseInfo r) { return r != null && r.Version > Current; }

        // =====================================================================  загрузка и проверка

        /// <summary>
        /// Скачивает RadminGrid.exe рядом с программой (RadminGrid.exe.new) и проверяет его.
        /// Возвращает путь к проверенному файлу. progress(получено, всего); cancelled() - прервать загрузку.
        /// </summary>
        public static string Download(ReleaseInfo r, Action<long, long> progress, Func<bool> cancelled)
        {
            string dir = Path.GetDirectoryName(ExePath);
            if (!CanWrite(dir))
                throw new UpdateException("Папка программы доступна только для чтения:\n" + dir +
                                          "\n\nЗапустите программу из папки, куда можно записывать файлы.");

            string expected = ExpectedHash(r);
            string part = ExePath + ".download", ready = ExePath + ".new";
            TryDelete(part);
            try
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.Create(part))
                {
                    var req = Request(r.ExeUrl, "application/octet-stream");
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var net = resp.GetResponseStream())
                    {
                        long total = resp.ContentLength > 0 ? resp.ContentLength : r.ExeSize, got = 0;
                        var buf = new byte[81920];
                        int n;
                        while ((n = net.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException();
                            fs.Write(buf, 0, n);
                            sha.TransformBlock(buf, 0, n, null, 0);
                            got += n;
                            if (progress != null) progress(got, total);
                        }
                        if (r.ExeSize > 0 && got != r.ExeSize)
                            throw new UpdateException("Файл скачан не полностью: " + got + " из " + r.ExeSize + " байт.");
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    actual = Hex(sha.Hash);
                }
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("Контрольная сумма не совпала - файл повреждён или подменён.\nОжидалась: " + expected + "\nПолучена: " + actual);

                AssemblyName an;
                try { an = AssemblyName.GetAssemblyName(part); }
                catch { throw new UpdateException("Скачанный файл не является программой .NET."); }
                if (!string.Equals(an.Name, "RadminGrid", StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("Скачанный файл - не Radmin Grid (" + an.Name + ").");
                if (Normalize(an.Version) != r.Version)
                    throw new UpdateException("Версия файла (" + Format(an.Version) + ") не совпадает с версией релиза (" + Format(r.Version) + ").");

                TryDelete(ready);
                File.Move(part, ready);
                return ready;
            }
            finally { TryDelete(part); }
        }

        /// <summary>SHA-256 из поля digest ассета (GitHub считает его сам), иначе из SHA256SUMS.txt.</summary>
        static string ExpectedHash(ReleaseInfo r)
        {
            if (!string.IsNullOrEmpty(r.ExeDigest) && r.ExeDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return r.ExeDigest.Substring(7).Trim();
            if (r.SumsUrl != null)
            {
                string text = Encoding.UTF8.GetString(Get(r.SumsUrl, "application/octet-stream"));
                foreach (var line in text.Split('\n'))
                {
                    // формат sha256sum: "<hex>  RadminGrid.exe" (или "*RadminGrid.exe")
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1].TrimStart('*').Equals(AssetName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                        return parts[0];
                }
            }
            throw new UpdateException("В релизе " + r.Tag + " нет контрольной суммы для " + AssetName + " - установка без проверки отключена.");
        }

        // =====================================================================  установка

        /// <summary>
        /// Ставит проверенный файл на место работающего exe и запускает новую версию.
        /// Вызывается при закрытии программы. При ошибке возвращает прежний exe на место.
        /// </summary>
        public static void InstallAndRestart(string ready)
        {
            string exe = ExePath, old = exe + ".old";
            if (!File.Exists(ready)) throw new UpdateException("Файл обновления не найден: " + ready);
            TryDelete(old);
            if (File.Exists(old)) throw new UpdateException("Не удалось удалить " + old + ". Закройте программу и удалите его вручную.");
            File.Move(exe, old);
            try { File.Move(ready, exe); }
            catch
            {
                try { File.Move(old, exe); } catch { }
                throw;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, "/updated:" + Process.GetCurrentProcess().Id)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                });
            }
            catch (Exception ex)
            {
                throw new UpdateException("Новая версия установлена, но не запустилась: " + ex.Message + "\nЗапустите " + exe + " вручную.");
            }
        }

        /// <summary>
        /// При запуске: если нас запустил установщик (/updated:PID) - дождаться выхода прежней версии,
        /// затем убрать остатки прошлого обновления. true - программа только что обновилась.
        /// </summary>
        public static bool AfterStart(string[] args)
        {
            bool updated = false;
            foreach (var a in args)
            {
                if (!a.StartsWith("/updated", StringComparison.OrdinalIgnoreCase)) continue;
                updated = true;
                int pid;
                if (int.TryParse(a.Substring(a.IndexOf(':') + 1), out pid))
                {
                    try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(20000); }
                    catch { }   // уже завершился
                }
            }
            string exe = ExePath;
            TryDelete(exe + ".old");
            TryDelete(exe + ".new");
            TryDelete(exe + ".download");
            return updated;
        }

        // =====================================================================  HTTP

        static HttpWebRequest Request(string url, string accept)
        {
            // .NET Framework по умолчанию может не включать TLS 1.2, а GitHub принимает только его и новее
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "RadminGrid/" + Format(Current);
            req.Accept = accept;
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            if (req.Proxy != null) req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            return req;
        }

        static byte[] Get(string url, string accept)
        {
            try
            {
                using (var resp = (HttpWebResponse)Request(url, accept).GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr == null) throw new UpdateException("Нет связи с сервером обновлений: " + ex.Message);
                int code = (int)hr.StatusCode;
                if (code == 403 || code == 429) throw new UpdateException("Сервер обновлений временно ограничил запросы (" + code + "). Попробуйте позже.");
                throw new UpdateException("Сервер обновлений недоступен (код " + code + "). Попробуйте позже.");
            }
        }

        // =====================================================================  мелочи

        static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        static bool Bool(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static string Hex(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2"))); }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".rg_write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
