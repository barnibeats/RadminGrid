using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RadminGrid
{
    /// <summary>Импорт/экспорт списка узлов из файлов.</summary>
    internal static class PhonebookImport
    {
        /// <summary>Типичные места телефонной книги Radmin Viewer 3.</summary>
        public static List<string> FindRpbCandidates(string radminExe)
        {
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(radminExe)) dirs.Add(Path.GetDirectoryName(radminExe));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Radmin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radmin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Radmin"));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            var res = new List<string>();
            foreach (var d in dirs)
            {
                try
                {
                    if (Directory.Exists(d))
                        res.AddRange(Directory.GetFiles(d, "*.rpb", SearchOption.TopDirectoryOnly));
                }
                catch { }
            }
            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// ЭКСПЕРИМЕНТАЛЬНО. Формат .rpb закрыт, поэтому файл сканируется на строки (UTF-16 и ASCII):
        /// каждой строке-адресу сопоставляется предыдущая строка-имя. Папки не восстанавливаются.
        /// Надёжный способ - «Считать из Radmin» (чтение открытого окна).
        /// </summary>
        public static List<PbEntry> ImportRpbHeuristic(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var strings = new List<KeyValuePair<int, string>>();
            ExtractUtf16(data, strings);
            ExtractAscii(data, strings);
            strings.Sort((a, b) => a.Key.CompareTo(b.Key));

            var res = new List<PbEntry>();
            var seen = new HashSet<string>();
            string lastName = null;
            string folder = "Импорт " + Path.GetFileNameWithoutExtension(path);
            foreach (var kv in strings)
            {
                string h; int p;
                if (RadminController.TryParseHost(kv.Value, true, out h, out p))
                {
                    string name = lastName ?? h;
                    if (seen.Add(name + "|" + h))
                        res.Add(new PbEntry { Folder = folder, Name = name, Host = h, Port = p, Source = "rpb" });
                    lastName = null;
                }
                else if (kv.Value.Length >= 2 && kv.Value.Length <= 64)
                    lastName = kv.Value.Trim();
            }
            return res;
        }

        static void ExtractUtf16(byte[] d, List<KeyValuePair<int, string>> outList)
        {
            for (int align = 0; align < 2; align++)
            {
                var sb = new StringBuilder();
                int start = -1;
                for (int i = align; i + 1 < d.Length; i += 2)
                {
                    char c = (char)(d[i] | (d[i + 1] << 8));
                    if (!char.IsControl(c) && (c < 0xD800 || c > 0xDFFF) && c != 0xFFFF && c != 0xFEFF && (c < 0x0250 || (c >= 0x0400 && c <= 0x04FF)))
                    {
                        if (start < 0) start = i;
                        sb.Append(c);
                    }
                    else
                    {
                        if (sb.Length >= 2) outList.Add(new KeyValuePair<int, string>(start, sb.ToString()));
                        sb.Clear(); start = -1;
                    }
                }
            }
        }

        static void ExtractAscii(byte[] d, List<KeyValuePair<int, string>> outList)
        {
            var sb = new StringBuilder();
            int start = -1;
            for (int i = 0; i < d.Length; i++)
            {
                byte b = d[i];
                if (b >= 0x20 && b < 0x7F)
                {
                    if (start < 0) start = i;
                    sb.Append((char)b);
                }
                else
                {
                    if (sb.Length >= 7) outList.Add(new KeyValuePair<int, string>(start, sb.ToString()));
                    sb.Clear(); start = -1;
                }
            }
        }

        // CSV: Папка;Имя;Адрес;Порт (UTF-8, разделитель ";" или ",")
        public static List<PbEntry> ImportCsv(string path)
        {
            var res = new List<PbEntry>();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                char sep = line.Contains(';') ? ';' : ',';
                var f = line.Split(sep).Select(x => x.Trim().Trim('"')).ToArray();
                if (f.Length < 3) continue;
                if (f[0].Equals("Folder", StringComparison.OrdinalIgnoreCase) || f[0].Equals("Папка", StringComparison.OrdinalIgnoreCase)) continue;
                int port = 0;
                if (f.Length > 3) int.TryParse(f[3], out port);
                res.Add(new PbEntry { Folder = f[0].Replace('\\', '/'), Name = f[1], Host = f[2], Port = port, Source = "csv" });
            }
            return res;
        }

        public static void ExportCsv(string path, IEnumerable<PbEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Папка;Имя;Адрес;Порт");
            foreach (var e in entries)
                sb.AppendLine(e.Folder + ";" + e.Name + ";" + e.Host + ";" + (e.Port > 0 ? e.Port.ToString() : ""));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
