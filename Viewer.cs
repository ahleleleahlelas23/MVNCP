using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Mvncp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string initial = "";
            bool minimized = false;
            bool addressGiven = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("/min", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--min", StringComparison.OrdinalIgnoreCase))
                {
                    minimized = true;
                }
                else if (initial.Length == 0)
                {
                    initial = a;
                    addressGiven = true;
                }
            }
            if (initial.Length == 0) initial = Settings.LoadLast();

            MainForm form = new MainForm(initial, addressGiven);
            if (minimized) form.WindowState = FormWindowState.Minimized;
            Application.Run(form);
        }
    }

    internal static class Settings
    {
        private static string PathFile
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mvncp-viewer.ini"); }
        }

        public static string LoadLast()
        {
            try
            {
                if (!File.Exists(PathFile)) return "";
                string[] lines = File.ReadAllLines(PathFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("last=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(5).Trim();
                }
            }
            catch { }
            return "";
        }

        public static string LoadPassword()
        {
            try
            {
                if (!File.Exists(PathFile)) return "";
                string[] lines = File.ReadAllLines(PathFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(9).Trim();
                }
            }
            catch { }
            return "";
        }

        public static void Save(string last, string password)
        {
            try
            {
                File.WriteAllText(PathFile,
                    "last=" + last + Environment.NewLine +
                    "password=" + password + Environment.NewLine);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    internal class MainForm : Form
    {
        private readonly TextBox addr;
        private readonly Button connectBtn;
        private readonly Button disconnectBtn;
        private readonly Label status;
        private readonly Panel viewport;
        private readonly ScreenView view;

        private RfbClient client;
        private Thread reader;
        private volatile bool running;
        private string lastPassword = Settings.LoadPassword();
        private readonly bool autoConnect;

        public MainForm(string initial, bool autoConnect)
        {
            this.autoConnect = autoConnect;
            Text = "MVNCP Viewer";
            Width = 1100;
            Height = 700;
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterScreen;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 36;

            addr = new TextBox();
            addr.Left = 6;
            addr.Top = 7;
            addr.Width = 240;
            addr.Text = initial;

            connectBtn = new Button();
            connectBtn.Left = 252;
            connectBtn.Top = 5;
            connectBtn.Width = 110;
            connectBtn.Height = 25;
            connectBtn.Text = "Connect";

            disconnectBtn = new Button();
            disconnectBtn.Left = 368;
            disconnectBtn.Top = 5;
            disconnectBtn.Width = 100;
            disconnectBtn.Height = 25;
            disconnectBtn.Text = "Disconnect";
            disconnectBtn.Enabled = false;

            status = new Label();
            status.Left = 478;
            status.Top = 10;
            status.Width = 400;
            status.Text = "Address: 192.168.1.10 or 192.168.1.10:5900";

            top.Controls.Add(addr);
            top.Controls.Add(connectBtn);
            top.Controls.Add(disconnectBtn);
            top.Controls.Add(status);

            viewport = new Panel();
            viewport.Dock = DockStyle.Fill;
            viewport.AutoScroll = false;
            viewport.BackColor = Color.Black;

            view = new ScreenView();
            view.Dock = DockStyle.Fill;
            viewport.Controls.Add(view);

            Controls.Add(viewport);
            Controls.Add(top);

            connectBtn.Click += delegate { Connect(); };
            disconnectBtn.Click += delegate { Disconnect(); SetStatus("Disconnected"); };
            addr.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Connect(); }
            };

            FormClosing += delegate { Disconnect(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (autoConnect && addr.Text.Trim().Length > 0) Connect();
        }

        private void Connect()
        {
            string host;
            int port;
            if (!ParseAddress(addr.Text, out host, out port))
            {
                SetStatus("Address must be host or host:port");
                return;
            }

            Disconnect();
            Settings.Save(addr.Text.Trim(), lastPassword);
            SetStatus("Connecting to " + host + ":" + port + "...");

            string hostCopy = host;
            int portCopy = port;
            string pw = lastPassword;

            Thread t = new Thread(delegate() { DoConnect(hostCopy, portCopy, pw); });
            t.IsBackground = true;
            t.Start();
        }

        private void DoConnect(string host, int port, string password)
        {
            try
            {
                RfbClient c = null;
                bool needPassword = false;

                try
                {
                    c = new RfbClient(host, port, password);
                }
                catch (AuthRequiredException)
                {
                    needPassword = true;
                }
                catch (IOException ex)
                {
                    if (password.Length > 0 && ex.Message.IndexOf("Authentication", StringComparison.Ordinal) >= 0)
                        needPassword = true;
                    else
                        throw;
                }

                if (needPassword)
                {
                    string entered = AskPassword();
                    if (entered == null)
                    {
                        SetStatus("Password entry cancelled");
                        return;
                    }
                    lastPassword = entered;
                    c = new RfbClient(host, port, entered);
                }

                RfbClient captured = c;
                Invoke(new Action(delegate() { Attach(captured, host, port); }));
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
            }
        }

        private string AskPassword()
        {
            string result = null;
            Invoke(new Action(delegate()
            {
                using (PasswordForm f = new PasswordForm())
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        result = f.Password;
                        if (f.Remember) Settings.Save(addr.Text.Trim(), result);
                    }
                }
            }));
            return result;
        }

        private void Attach(RfbClient c, string host, int port)
        {
            client = c;
            view.Frame = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppRgb);
            view.Client = c;
            view.Focus();

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int wantW = c.Width + 24;
            int wantH = c.Height + 64;
            if (wantW <= wa.Width && wantH <= wa.Height) ClientSize = new Size(wantW, wantH);

            Text = "MVNCP - " + host + ":" + port +
                   (c.ServerName.Length > 0 ? "  [" + c.ServerName + "]" : "");
            SetStatus("Connected: " + c.Width + "x" + c.Height);

            connectBtn.Enabled = false;
            disconnectBtn.Enabled = true;

            running = true;
            reader = new Thread(ReaderLoop);
            reader.IsBackground = true;
            reader.Start();
        }

        private void ReaderLoop()
        {
            try
            {
                client.RequestUpdate(false);
                while (running)
                {
                    List<RectInfo> rects = client.ReadUpdate();
                    if (!running) break;
                    Apply(rects);
                    client.RequestUpdate(true);
                }
            }
            catch (Exception ex)
            {
                if (running) SetStatus("Connection lost: " + ex.Message);
            }
        }

        private void Apply(List<RectInfo> rects)
        {
            if (rects.Count == 0) return;
            try
            {
                Invoke(new Action(delegate()
                {
                    if (!running || view.Frame == null) return;
                    for (int i = 0; i < rects.Count; i++) PixelCopy.Blit(view.Frame, rects[i]);
                    view.Invalidate();
                }));
            }
            catch (InvalidOperationException)
            {
                // the form is closing
            }
        }

        private void Disconnect()
        {
            running = false;

            RfbClient c = client;
            client = null;
            if (c != null)
            {
                try { c.Dispose(); } catch { }
            }

            view.Client = null;
            if (view.Frame != null)
            {
                view.Frame.Dispose();
                view.Frame = null;
            }
            view.Invalidate();

            connectBtn.Enabled = true;
            disconnectBtn.Enabled = false;
        }

        private void SetStatus(string text)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(delegate() { SetStatus(text); }));
                else status.Text = text;
            }
            catch { }
        }

        private static bool ParseAddress(string text, out string host, out int port)
        {
            host = "";
            port = 5900;
            if (text == null) return false;

            string s = text.Trim();
            if (s.Length == 0) return false;
            if (s.StartsWith("vnc://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(6);
            if (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);

            int colon = s.LastIndexOf(':');
            if (colon > 0 && colon < s.Length - 1)
            {
                int p;
                if (int.TryParse(s.Substring(colon + 1), out p) && p > 0 && p < 65536)
                {
                    host = s.Substring(0, colon);
                    port = p;
                    return host.Length > 0;
                }
            }

            host = s;
            return true;
        }
    }

    // ------------------------------------------------------------------
    internal class ScreenView : Control
    {
        public Bitmap Frame;
        public RfbClient Client;

        private int mask;
        private readonly HashSet<uint> sentDown = new HashSet<uint>();

        public ScreenView()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            TabStop = true;
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (Frame == null) return;

            Rectangle dest = GetDestRect();
            if (dest.Width <= 0 || dest.Height <= 0) return;

            bool exact = dest.Width == Frame.Width && dest.Height == Frame.Height;
            e.Graphics.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(Frame, dest);
        }

        private Rectangle GetDestRect()
        {
            if (Frame == null || Width <= 0 || Height <= 0) return Rectangle.Empty;

            double scale = Math.Min((double)Width / Frame.Width, (double)Height / Frame.Height);
            int w = Math.Max(1, (int)Math.Round(Frame.Width * scale));
            int h = Math.Max(1, (int)Math.Round(Frame.Height * scale));
            return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }

        private Point ToRemote(Point p)
        {
            if (Frame == null) return Point.Empty;

            Rectangle dest = GetDestRect();
            if (dest.Width <= 0 || dest.Height <= 0) return Point.Empty;

            double sx = (double)Frame.Width / dest.Width;
            double sy = (double)Frame.Height / dest.Height;

            int x = (int)Math.Round((p.X - dest.X) * sx);
            int y = (int)Math.Round((p.Y - dest.Y) * sy);

            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > Frame.Width - 1) x = Frame.Width - 1;
            if (y > Frame.Height - 1) y = Frame.Height - 1;

            return new Point(x, y);
        }

        private static int ButtonBit(MouseButtons b)
        {
            if (b == MouseButtons.Left) return 1;
            if (b == MouseButtons.Middle) return 2;
            if (b == MouseButtons.Right) return 4;
            return 0;
        }

        private void SendPointer(MouseEventArgs e)
        {
            if (Client == null) return;
            Point p = ToRemote(e.Location);
            Client.SendPointer(mask, p.X, p.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            SendPointer(e);
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            mask |= ButtonBit(e.Button);
            SendPointer(e);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            mask &= ~ButtonBit(e.Button);
            SendPointer(e);
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Client != null)
            {
                Point p = ToRemote(e.Location);
                int bit = e.Delta > 0 ? 8 : 16;
                Client.SendPointer(mask | bit, p.X, p.Y);
                Client.SendPointer(mask, p.X, p.Y);
            }
            base.OnMouseWheel(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mask = 0;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            HandleKey(e, true);
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            HandleKey(e, false);
            base.OnKeyUp(e);
        }

        private void HandleKey(KeyEventArgs e, bool down)
        {
            if (Client == null) return;

            uint ks = KeyToKeysym(e.KeyCode);
            if (ks == 0) return;

            bool plain = IsPlainLetterOrDigit(e.KeyCode);

            if (down)
            {
                if (plain && !e.Control && !e.Alt) return; // OnKeyPress will send it

                Client.SendKey(true, ks);
                sentDown.Add(ks);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else
            {
                if (sentDown.Remove(ks))
                {
                    Client.SendKey(false, ks);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
        }

        private static bool IsPlainLetterOrDigit(Keys k)
        {
            return (k >= Keys.A && k <= Keys.Z) || (k >= Keys.D0 && k <= Keys.D9);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (Client != null && e.KeyChar >= 0x20 && e.KeyChar != 0x7F && !char.IsControl(e.KeyChar))
            {
                uint ks = (uint)e.KeyChar;
                Client.SendKey(true, ks);
                Client.SendKey(false, ks);
                e.Handled = true;
            }
            base.OnKeyPress(e);
        }

        private static uint KeyToKeysym(Keys k)
        {
            switch (k)
            {
                case Keys.Back: return Ks.BackSpace;
                case Keys.Tab: return Ks.Tab;
                case Keys.Enter: return Ks.Return;
                case Keys.Escape: return Ks.Escape;
                case Keys.Space: return Ks.Space;
                case Keys.Insert: return Ks.Insert;
                case Keys.Delete: return Ks.Delete;
                case Keys.Home: return Ks.Home;
                case Keys.End: return Ks.End;
                case Keys.PageUp: return Ks.PageUp;
                case Keys.PageDown: return Ks.PageDown;
                case Keys.Left: return Ks.Left;
                case Keys.Up: return Ks.Up;
                case Keys.Right: return Ks.Right;
                case Keys.Down: return Ks.Down;

                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey: return Ks.ShiftL;

                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey: return Ks.CtrlL;

                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu: return Ks.AltL;

                case Keys.LWin:
                case Keys.RWin: return Ks.SuperL;

                case Keys.CapsLock: return Ks.CapsLock;
                case Keys.NumLock: return Ks.NumLock;
                case Keys.Scroll: return Ks.ScrollLock;
                case Keys.PrintScreen: return Ks.Print;
                case Keys.Pause: return Ks.Pause;

                case Keys.F1: return Ks.F1;
                case Keys.F2: return Ks.F1 + 1;
                case Keys.F3: return Ks.F1 + 2;
                case Keys.F4: return Ks.F1 + 3;
                case Keys.F5: return Ks.F1 + 4;
                case Keys.F6: return Ks.F1 + 5;
                case Keys.F7: return Ks.F1 + 6;
                case Keys.F8: return Ks.F1 + 7;
                case Keys.F9: return Ks.F1 + 8;
                case Keys.F10: return Ks.F1 + 9;
                case Keys.F11: return Ks.F1 + 10;
                case Keys.F12: return Ks.F1 + 11;
            }

            if (k >= Keys.D0 && k <= Keys.D9) return (uint)('0' + (k - Keys.D0));
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return (uint)('0' + (k - Keys.NumPad0));
            if (k >= Keys.A && k <= Keys.Z) return (uint)('a' + (k - Keys.A));

            return 0;
        }
    }

    // ------------------------------------------------------------------
    internal class PasswordForm : Form
    {
        private readonly TextBox box;
        private readonly CheckBox remember;

        public string Password
        {
            get { return box.Text; }
        }

        public bool Remember
        {
            get { return remember.Checked; }
        }

        public PasswordForm()
        {
            Text = "VNC Password";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(280, 126);

            Label label = new Label();
            label.Text = "Password:";
            label.Left = 10;
            label.Top = 14;
            label.Width = 60;

            box = new TextBox();
            box.Left = 72;
            box.Top = 12;
            box.Width = 196;
            box.UseSystemPasswordChar = true;

            remember = new CheckBox();
            remember.Text = "Remember password";
            remember.Left = 72;
            remember.Top = 42;
            remember.Width = 180;
            remember.Checked = true;

            Button ok = new Button();
            ok.Text = "OK";
            ok.Left = 112;
            ok.Top = 78;
            ok.Width = 76;
            ok.DialogResult = DialogResult.OK;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Left = 194;
            cancel.Top = 78;
            cancel.Width = 76;
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(label);
            Controls.Add(box);
            Controls.Add(remember);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
