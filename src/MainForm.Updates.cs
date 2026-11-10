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
        // =====================================================================  обновления

        /// <summary>Проверка обновлений в фоне. manual - по кнопке: сообщать и об отсутствии обновлений.</summary>
        void CheckUpdates(bool manual)
        {
            bool pre = _s.UpdatePrerelease;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo r = null; string err = null;
                try { r = Updater.FindLatest(pre); }
                catch (UpdateException ex) { err = ex.Message; }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke(new Action(delegate { OnUpdateChecked(r, err, manual); })); }
                catch (InvalidOperationException) { }   // окно уже закрыто
            });
        }

        void OnUpdateChecked(ReleaseInfo r, string err, bool manual)
        {
            if (err != null)
            {
                AppendLog("Проверка обновлений: " + err.Replace("\n", " "));
                if (manual) MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Updater.IsNewer(r))
            {
                if (manual) MessageBox.Show(this, "Установлена последняя версия v" + Updater.Format(Updater.Current) + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AppendLog("Доступна новая версия " + r.Title + " (установлена v" + Updater.Format(Updater.Current) + ").");
            if (!manual && (r.Tag == _s.UpdateSkip || _autoStarted || _pendingUpdate != null)) return;
            OfferUpdate(r, !manual);
        }

        void OfferUpdate(ReleaseInfo r, bool allowSkip)
        {
            if (_pendingUpdate != null) { AppendLog("Обновление уже загружено и будет установлено при закрытии программы."); return; }
            using (var d = new UpdateDialog(r, allowSkip))
            {
                var res = d.ShowDialog(this);
                if (res == DialogResult.Ignore)
                {
                    _s.UpdateSkip = r.Tag; RequestSave();
                    AppendLog("Версия " + r.Title + " пропущена. Её можно установить из настроек.");
                    return;
                }
                if (res != DialogResult.OK || d.ReadyPath == null) return;
                _pendingUpdate = d.ReadyPath;
            }
            AppendLog("Обновление " + r.Title + " скачано и проверено (SHA-256). Перезапуск…");
            Close();
            if (!IsDisposed && Visible)
                AppendLog("Закрытие отменено - обновление установится при следующем закрытии программы.");
        }
    }
}
