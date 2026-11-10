// Эмулятор Radmin Viewer для проверки Radmin Grid без реальных серверов.
//   без аргументов           - окно «телефонной книги» (TreeView + ListView + кнопки режимов)
//   /connect:host:port ...   - окно входа (#32770, логин admin / пароль test123), затем окно подключения
//   host 10.0.0.99           - «не удаётся подключиться» (MessageBox)
//   host, оканчивающийся .13 - подключение обрывается через 8 с
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class FakeRadmin
{
    delegate IntPtr DlgProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr DialogBoxIndirectParam(IntPtr inst, IntPtr tmpl, IntPtr parent, DlgProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool EndDialog(IntPtr h, IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetDlgItemText(IntPtr h, int id, StringBuilder sb, int max);

    static string _user = "", _pass = "";
    static DlgProc _proc = Proc;

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        var conn = args.FirstOrDefault(a => a.StartsWith("/connect:", StringComparison.OrdinalIgnoreCase));
        if (conn == null) { Application.Run(new Phonebook()); return; }

        // Режим «однокопийного» Radmin: при наличии single.txt команда передаётся открытой книге, процесс сразу выходит
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        if (File.Exists(Path.Combine(dir, "single.txt")) && MainAlive(dir))
        {
            File.AppendAllText(Path.Combine(dir, "cmd.txt"), conn.Substring(9) + Environment.NewLine);
            return;
        }
        string addr = conn.Substring(9);
        string host = addr.Split(':')[0];
        Thread.Sleep(700);
        if (host == "10.0.0.99")
        {
            MessageBox.Show("Не удается подключиться к серверу " + addr, "Radmin Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (args.Contains("/noauth")) { _user = "admin"; _pass = "test123"; } else if (ShowAuth(host) != 1) return;
        if (_user != "admin" || _pass != "test123")
        {
            MessageBox.Show("Доступ запрещён: неверное имя пользователя или пароль", "Radmin Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var f = new Form { Text = host + " - Radmin Viewer" + (args.Contains("/noinput") ? " (просмотр)" : ""), Width = 1024, Height = 768, BackColor = Color.FromArgb(30, 60, 90) };
        f.Controls.Add(new Label { Text = string.Join(" ", args), ForeColor = Color.White, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 16), TextAlign = ContentAlignment.MiddleCenter });
        if (host.EndsWith(".13"))
        {
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += delegate { Environment.Exit(0); };
            t.Start();
        }
        Application.Run(f);
    }

    static bool MainAlive(string dir)
    {
        try { return !Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(dir, "main.pid")))).HasExited; }
        catch { return false; }
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x0110) return new IntPtr(1);                       // WM_INITDIALOG
        if (msg == 0x0111)                                              // WM_COMMAND
        {
            int id = w.ToInt32() & 0xFFFF;
            if (id == 1)
            {
                var sb = new StringBuilder(256);
                GetDlgItemText(h, 100, sb, 256); _user = sb.ToString();
                sb.Clear();
                GetDlgItemText(h, 101, sb, 256); _pass = sb.ToString();
                EndDialog(h, new IntPtr(1)); return new IntPtr(1);
            }
            if (id == 2) { EndDialog(h, new IntPtr(2)); return new IntPtr(1); }
        }
        return IntPtr.Zero;
    }

    static int ShowAuth(string host)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        const uint WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_BORDER = 0x00800000, WS_TABSTOP = 0x00010000;
        bw.Write(0x80000000u | 0x00C00000u | 0x00080000u | 0x40u | 0x80u); // POPUP|CAPTION|SYSMENU|SETFONT|MODALFRAME
        bw.Write(0u);
        bw.Write((short)6);
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)200); bw.Write((short)90);
        bw.Write((short)0); bw.Write((short)0);
        Str(bw, "Radmin security: " + host);
        bw.Write((short)9); Str(bw, "Segoe UI");
        Item(bw, WS_CHILD | WS_VISIBLE, 8, 10, 60, 10, -1, 0x82, "Имя пользователя:");
        Item(bw, WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | 0x80, 75, 8, 115, 12, 100, 0x81, "");
        Item(bw, WS_CHILD | WS_VISIBLE, 8, 30, 60, 10, -1, 0x82, "Пароль:");
        Item(bw, WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | 0x80 | 0x20, 75, 28, 115, 12, 101, 0x81, "");
        Item(bw, WS_CHILD | WS_VISIBLE | WS_TABSTOP | 1, 75, 60, 50, 14, 1, 0x80, "OK");
        Item(bw, WS_CHILD | WS_VISIBLE | WS_TABSTOP, 140, 60, 50, 14, 2, 0x80, "Отмена");
        byte[] data = ms.ToArray();
        IntPtr mem = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, mem, data.Length);
        try { return DialogBoxIndirectParam(Marshal.GetHINSTANCE(typeof(FakeRadmin).Module), mem, IntPtr.Zero, _proc, IntPtr.Zero).ToInt32(); }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static void Str(BinaryWriter bw, string s) { bw.Write(Encoding.Unicode.GetBytes(s + "\0")); }

    static void Item(BinaryWriter bw, uint style, short x, short y, short cx, short cy, int id, ushort atom, string text)
    {
        while (bw.BaseStream.Position % 4 != 0) bw.Write((byte)0);
        bw.Write(style); bw.Write(0u);
        bw.Write(x); bw.Write(y); bw.Write(cx); bw.Write(cy);
        bw.Write((ushort)id);
        bw.Write((ushort)0xFFFF); bw.Write(atom);
        Str(bw, text);
        bw.Write((short)0);
    }

    class Phonebook : Form
    {
        readonly TreeView _tree = new TreeView { Dock = DockStyle.Left, Width = 220, HideSelection = false };
        readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        readonly string[][] _data =
        {
            new[] { "\\/Все/Офис", "Бухгалтерия-1", "10.0.0.11", "4890" },
            new[] { "\\/Все/Офис", "Бухгалтерия-2", "10.0.0.12", "4891" },
            new[] { "\\/Все/Офис/Склад", "Склад-ПК", "10.0.0.13", "4899" },
            new[] { "\\/Все/Филиал", "Касса", "10.0.0.21", "4899" },
            new[] { "\\/Все/Филиал", "Сломанный", "10.0.0.99", "4899" },
        };

        public Phonebook()
        {
            Text = "Radmin Viewer";
            Width = 800; Height = 500;
            _tree.PathSeparator = "/"; _list.Columns.Add("Компьютер", 180); _list.Columns.Add("Адрес", 150); _list.Columns.Add("Порт", 60);
            foreach (var d in _data)
            {
                var nodes = _tree.Nodes; string path = "";
                foreach (var p in d[0].Split('/'))
                {
                    path = path.Length == 0 ? p : path + "/" + p;
                    var n = nodes.Cast<TreeNode>().FirstOrDefault(x => x.Text == p) ?? nodes.Add(path, p);
                    nodes = n.Nodes;
                }
            }
            _tree.AfterSelect += delegate
            {
                _list.Items.Clear();
                string path = _tree.SelectedNode.FullPath;
                foreach (var d in _data.Where(x => x[0] == path))
                    _list.Items.Add(new ListViewItem(new[] { d[1], d[2], d[3] }));
            };
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Connect(""); };
            var ts = new ToolStrip();
            ts.Items.Add("Полное управление", null, delegate { Connect(""); });
            ts.Items.Add("Только просмотр", null, delegate { Connect(" /noinput"); });
            Controls.Add(_list); Controls.Add(new Splitter { Dock = DockStyle.Left }); Controls.Add(_tree); Controls.Add(ts);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "main.pid"), Process.GetCurrentProcess().Id.ToString());
            var poll = new System.Windows.Forms.Timer { Interval = 300 };
            poll.Tick += delegate
            {
                string f = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "cmd.txt");
                if (!File.Exists(f)) return;
                var lines = File.ReadAllLines(f); File.Delete(f);
                foreach (var a in lines.Where(x => x.Trim().Length > 0))
                    new Form { Text = a.Split(':')[0] + " - Управление", Width = 1024, Height = 768, BackColor = Color.FromArgb(60, 30, 90) }.Show();
            };
            poll.Start();
        }

        void Connect(string extra)
        {
            if (_list.SelectedItems.Count == 0) return;
            Process.Start(Application.ExecutablePath, "/connect:" + _list.SelectedItems[0].SubItems[1].Text + ":" + _list.SelectedItems[0].SubItems[2].Text + extra);
        }
    }
}
