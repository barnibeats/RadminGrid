using System;
using System.IO;
using System.Reflection;

namespace RadminGrid
{
    /// <summary>Ярлык программы на рабочем столе текущего пользователя (прав администратора не нужно).</summary>
    internal static class Shortcut
    {
        public const string FileName = "Radmin Grid.lnk";

        public static string DesktopPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileName); }
        }

        public static bool Exists { get { return File.Exists(DesktopPath); } }

        /// <summary>Создаёт или обновляет ярлык на запущенный exe. Возвращает путь к ярлыку.</summary>
        public static string CreateOnDesktop()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string lnk = DesktopPath;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host недоступен.");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type t = link.GetType();
                Set(t, link, "TargetPath", exe);
                Set(t, link, "WorkingDirectory", Path.GetDirectoryName(exe));
                Set(t, link, "IconLocation", exe + ",0");
                Set(t, link, "Description", "Radmin Grid");
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            return lnk;
        }

        static void Set(Type t, object o, string name, string value)
        {
            t.InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { value });
        }
    }
}
