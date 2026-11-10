using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>
    /// «Доступна новая версия»: что нового, загрузка с проверкой. OK - файл скачан и проверен (ReadyPath),
    /// Ignore - пользователь пропустил эту версию.
    /// </summary>
    internal class UpdateDialog : Form
    {
        readonly ReleaseInfo _r;
        readonly Label _status;
        readonly Bar _bar;
        readonly RButton _install, _later, _skip;
        volatile bool _cancel;
        bool _busy;

        public string ReadyPath { get; private set; }

        public UpdateDialog(ReleaseInfo r, bool allowSkip)
        {
            _r = r;
            Theme.StyleForm(this);
            Text = "Обновление - Radmin Grid";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 440);

            var t = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 16), BackColor = Theme.Bg };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            t.Controls.Add(new Label { Text = "Доступна версия " + r.Title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
            t.Controls.Add(new Label
            {
                Text = "Сейчас установлена v" + Updater.Format(Updater.Current) +
                       (r.Published > DateTime.MinValue ? ".  Выпущена " + r.Published.ToLocalTime().ToString("dd.MM.yyyy") + "." : "."),
                Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, Margin = new Padding(0, 0, 0, 12)
            });

            var notes = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Ui, Dock = DockStyle.Fill, TabStop = false,
                Text = (r.Notes.Trim().Length > 0 ? r.Notes.Trim() : "Описание изменений не приложено.").Replace("\r\n", "\n").Replace("\n", "\r\n")
            };
            var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 4, 8), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 12) };
            notesHost.Controls.Add(notes);
            t.Controls.Add(notesHost);

            _bar = new Bar { Height = 6, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6), Visible = false };
            t.Controls.Add(_bar);
            _status = new Label
            {
                Text = "Программа скачает новую версию, проверит её контрольную сумму и перезапустится. Подключения можно оставить открытыми.",
                Font = Theme.Small, ForeColor = Theme.TextDim, AutoSize = true, MaximumSize = new Size(510, 0), Margin = new Padding(0, 0, 0, 10)
            };
            t.Controls.Add(_status);

            _install = new RButton("Установить и перезапустить", null, RButton.Kind.Primary);
            _install.AutoSizeWidth();
            _later = new RButton("Позже");
            _skip = new RButton("Пропустить версию", null, RButton.Kind.Ghost) { Visible = allowSkip };
            _skip.AutoSizeWidth();

            var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            fl.Controls.Add(_install); fl.Controls.Add(_later); fl.Controls.Add(_skip);
            t.Controls.Add(fl);
            Controls.Add(t);

            _install.Click += delegate { StartDownload(); };
            _later.Click += delegate
            {
                if (_busy) { _cancel = true; return; }
                DialogResult = DialogResult.Cancel; Close();
            };
            _skip.Click += delegate { DialogResult = DialogResult.Ignore; Close(); };
            FormClosing += (o, e) => { if (_busy) { _cancel = true; e.Cancel = true; } };
            KeyPreview = true;
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) _later.PerformClick(); };
        }

        void StartDownload()
        {
            if (_busy) return;
            _busy = true; _cancel = false;
            _install.Enabled = false; _skip.Enabled = false;
            _later.Text = "Отмена";
            _bar.Value = 0; _bar.Visible = true;
            SetStatus("Загрузка…", false);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string ready = null, error = null;
                try
                {
                    ready = Updater.Download(_r, (got, total) => BeginInvoke(new Action(delegate
                    {
                        _bar.Value = total > 0 ? (float)got / total : 0;
                        SetStatus("Загрузка… " + (got / 1024) + " КБ" + (total > 0 ? " из " + (total / 1024) + " КБ" : ""), false);
                    })), () => _cancel);
                }
                catch (OperationCanceledException) { error = "Загрузка отменена."; }
                catch (UpdateException ex) { error = ex.Message; }
                catch (Exception ex) { error = "Не удалось скачать обновление: " + ex.Message; }

                BeginInvoke(new Action(delegate
                {
                    _busy = false;
                    if (ready != null)
                    {
                        ReadyPath = ready;
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                    _bar.Visible = false;
                    SetStatus(error, true);
                    _install.Text = "Повторить"; _install.Enabled = true; _skip.Enabled = true;
                    _later.Text = "Позже";
                }));
            });
        }

        void SetStatus(string text, bool error)
        {
            _status.Text = text;
            _status.ForeColor = error ? Theme.Danger : Theme.TextDim;
        }

        /// <summary>Тонкая полоса прогресса в цветах темы.</summary>
        class Bar : Control
        {
            float _v;
            public float Value { get { return _v; } set { _v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

            public Bar()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var p = Theme.Round(r, Height / 2)) using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, p);
                int w = (int)(r.Width * _v);
                if (w < Height) return;
                using (var p = Theme.Round(new Rectangle(0, 0, w, r.Height), Height / 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            }
        }
    }
}
