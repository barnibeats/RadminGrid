using System;
using System.Drawing;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Добавление/редактирование узла вручную.</summary>
    internal class EntryDialog : Form
    {
        readonly TextBox _folder = new TextBox(), _name = new TextBox(), _host = new TextBox(), _port = new TextBox();

        public EntryDialog(PbEntry e)
        {
            Theme.StyleForm(this);
            Text = e == null || (e.Name.Length == 0 && e.Host.Length == 0) ? "Новый узел" : "Изменить узел";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(22, 18, 22, 16), Dock = DockStyle.Fill, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = Text.ToUpperInvariant(), Font = Theme.Caps, ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            t.Controls.Add(title, 0, t.RowCount++); t.SetColumnSpan(title, 2);
            Add(t, "Папка (через /)", _folder);
            Add(t, "Имя", _name);
            Add(t, "Адрес (IP или имя)", _host);
            Add(t, "Порт (пусто - по умолчанию)", _port);

            var ok = new RButton("СОХРАНИТЬ", null, RButton.Kind.Primary) { Font = Theme.Caps };
            ok.AutoSizeWidth();
            var cancel = new RButton("Отмена");
            var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 14, 0, 0) };
            fl.Controls.Add(ok); fl.Controls.Add(cancel);
            t.Controls.Add(fl, 0, t.RowCount++);
            t.SetColumnSpan(fl, 2);
            Controls.Add(t);

            if (e != null)
            {
                _folder.Text = e.Folder; _name.Text = e.Name; _host.Text = e.Host; _port.Text = e.Port > 0 ? e.Port.ToString() : "";
            }
            ok.Click += delegate
            {
                int port;
                if (_name.Text.Trim().Length == 0 && _host.Text.Trim().Length == 0)
                    MessageBox.Show(this, "Укажите имя или адрес.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (_port.Text.Trim().Length > 0 && (!int.TryParse(_port.Text.Trim(), out port) || port < 1 || port > 65535))
                    MessageBox.Show(this, "Порт - число от 1 до 65535.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else { DialogResult = DialogResult.OK; Close(); }
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            KeyPreview = true;
            KeyDown += (o, ev) =>
            {
                if (ev.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; ok.PerformClick(); }
            };
        }

        static void Add(TableLayoutPanel t, string label, TextBox c)
        {
            int r = t.RowCount++;
            t.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.TextDim, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 16, 3) }, 0, r);
            t.Controls.Add(Theme.Field(c, 280), 1, r);
        }
        public void ApplyTo(PbEntry e)
        {
            e.Folder = _folder.Text.Trim().Replace('\\', '/').Trim('/');
            e.Name = _name.Text.Trim();
            e.Host = _host.Text.Trim();
            int pv; e.Port = int.TryParse(_port.Text.Trim(), out pv) ? pv : 0;
            int colon = e.Host.LastIndexOf(':');
            int p;
            if (colon > 0 && int.TryParse(e.Host.Substring(colon + 1), out p))
            {
                e.Host = e.Host.Substring(0, colon);
                if (e.Port == 0) e.Port = p;
            }
            if (e.Name.Length == 0) e.Name = e.Host;
            if (string.IsNullOrEmpty(e.Source)) e.Source = "manual";
        }
    }
}
