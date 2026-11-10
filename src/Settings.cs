using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace RadminGrid
{
    public enum ConnectMode { FullControl, ViewOnly, FileTransfer, Telnet }

    public enum ConnectMethod { CommandLine, Phonebook }

    /// <summary>Узел телефонной книги. Folder - путь папок через "/".</summary>
    public class PbEntry
    {
        public string Folder = "";
        public string Name = "";
        public string Host = "";
        public int Port;
        public bool Checked;
        public string Source = "";

        [XmlIgnore]
        public string Key { get { return (Folder + "|" + Name + "|" + Host).ToLowerInvariant(); } }

        [XmlIgnore]
        public string Display { get { return Name.Length > 0 ? Name : Host; } }

        public string Address(int defaultPort)
        {
            return Host + ":" + (Port > 0 ? Port : defaultPort);
        }
    }

    public class AppSettings
    {
        public string RadminPath = "";
        public string PhonebookPath = "";
        public bool PassPhonebookSwitch;

        public ConnectMode Mode = ConnectMode.FullControl;
        public ConnectMethod Method = ConnectMethod.CommandLine;
        public int DefaultPort = 4899;
        public bool Stretch = true;
        public string ExtraArgs = "";
        public int ConnectTimeoutSec = 30;
        public bool TcpPrecheck = true;
        public int PrecheckTimeoutMs = 1500;
        public int LaunchDelayMs = 600;
        public bool CloseErrorDialogs = true;
        public bool AutoReconnect;
        public int ReconnectDelaySec = 10;
        public int MaxReconnects = 3;

        public bool AutoLogin = true;
        public string Login = "";
        public string PasswordData = "";
        public bool EncryptPassword = true;

        public string GridPreset = "Авто";
        public int GridRows = 3;
        public int GridCols = 3;
        public int Monitor;              // -1 = все мониторы
        public int Gap;
        public bool Borderless;
        public bool CoverTaskbar;
        public bool AutoArrange = true;
        public bool AdoptManual = true;          // подхватывать окна, открытые вручную в Radmin
        public bool Paging = true;               // не помещаются в сетку - делить на вкладки
        public int AutoPageMax = 25;             // «Авто»: окон на вкладке на один монитор
        public string GridSort = "Порядок открытия";

        public bool ShiftCheck = true;           // читать номер смены из окон (OCR)
        public int ShiftIntervalMin = 5;
        public bool ShiftEnlarge = true;         // на время снимка увеличивать мелкие окна (под остальными окнами)

        public string UiTheme = "dark";       // dark | light | system
        public bool HudVisible = true;
        public int HudX = -1;
        public int HudY = -1;
        public int HudOpacity = 90;
        public int HudWidth;                     // 0 = по умолчанию
        public int HudHeight;                    // 0 = высота по содержимому
        public int HudFilter = -1;               // -1 = все, иначе номер группы счётчика
        public bool HudCollapsed;

        public bool UpdateCheck = true;          // проверять обновления при запуске
        public bool UpdatePrerelease;            // предлагать и предварительные версии
        public string UpdateSkip = "";           // тег версии, которую пользователь пропустил

        public int WinX = -1, WinY = -1, WinW = 1150, WinH = 780;
        public string QuickTargets = "";

        public List<PbEntry> Entries = new List<PbEntry>();

        [XmlIgnore] public static string FilePath;
        [XmlIgnore] public static string LoadWarning;

        // ---- пароль: DPAPI (CurrentUser) либо base64, если пользователь выключил шифрование ----
        public string GetPassword()
        {
            if (string.IsNullOrEmpty(PasswordData)) return "";
            try
            {
                if (PasswordData.StartsWith("dpapi:"))
                {
                    byte[] enc = Convert.FromBase64String(PasswordData.Substring(6));
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser));
                }
                if (PasswordData.StartsWith("plain:"))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(PasswordData.Substring(6)));
            }
            catch
            {
                LoadWarning = "Пароль был зашифрован под другим пользователем/ПК - введите его заново.";
            }
            return "";
        }

        public void SetPassword(string pwd)
        {
            if (string.IsNullOrEmpty(pwd)) { PasswordData = ""; return; }
            byte[] raw = Encoding.UTF8.GetBytes(pwd);
            PasswordData = EncryptPassword
                ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser))
                : "plain:" + Convert.ToBase64String(raw);
        }

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RadminGrid.v1");

        // ---- хранение: файл рядом с exe; если папка только для чтения - %APPDATA%\RadminGrid ----
        public static AppSettings Load()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(exeDir, "RadminGrid.settings.xml");
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadminGrid", "RadminGrid.settings.xml");

            FilePath = CanWrite(exeDir) ? local : roaming;
            string source = File.Exists(local) ? local : (File.Exists(roaming) ? roaming : null);
            if (source == null) return new AppSettings();
            try
            {
                using (var fs = File.OpenRead(source))
                {
                    var s = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs);
                    if (s.Entries == null) s.Entries = new List<PbEntry>();
                    return s;
                }
            }
            catch (Exception ex)
            {
                LoadWarning = "Не удалось прочитать настройки (" + ex.Message + "), используются значения по умолчанию.";
                try { File.Copy(source, source + ".bad", true); } catch { }
                return new AppSettings();
            }
        }

        public void Save()
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
                new XmlSerializer(typeof(AppSettings)).Serialize(fs, this);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

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
