using System;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    internal static class Program
    {
        /// <summary>Программа запущена установщиком обновления.</summary>
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            Native.EnableDpiAwareness();
            // после обновления: дождаться выхода прежней версии (она держит мьютекс) и удалить RadminGrid.exe.old
            JustUpdated = Updater.AfterStart(args);

            // Один экземпляр на пользователя и папку программы (Local\ - без прав администратора)
            string id = "Local\\RadminGrid_" + AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant().GetHashCode().ToString("X8");
            bool created;
            using (var mutex = new Mutex(true, id, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Radmin Grid уже запущен из этой папки.", "Radmin Grid", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ShowFatal(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ShowFatal(e.ExceptionObject as Exception); };
                Application.Run(new MainForm());
            }
        }

        static void ShowFatal(Exception ex)
        {
            MessageBox.Show("Непредвиденная ошибка:\n\n" + (ex != null ? ex.ToString() : "неизвестно"), "Radmin Grid",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
