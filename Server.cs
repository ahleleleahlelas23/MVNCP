using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Mvncp
{
    internal static class Program
    {
        private static Config cfg;
        private static ScreenGrabber grabber;
        private static readonly object logLock = new object();
        private static readonly Random rnd = new Random();

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Native.SetProcessDPIAware();
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                cfg = Config.Load(Path.Combine(dir, "mvncp-server.ini"));
                grabber = new ScreenGrabber();

                Log("starting: screen " + grabber.Width + "x" + grabber.Height +
                    ", port " + cfg.Port + ", password " + (cfg.Password.Length > 0 ? "set" : "NOT SET"));

                TcpListener listener = new TcpListener(cfg.Bind, cfg.Port);
                listener.Start();

                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Thread t = new Thread(delegate() { Serve(client); });
                    t.IsBackground = true;
                    t.Start();
                }
            }
            catch (Exception ex)
            {
                Log("fatal error: " + ex);
            }
        }

        private static void Serve(TcpClient client)
        {
            string peer = "?";
            try
            {
                peer = client.Client.RemoteEndPoint.ToString();
                client.NoDelay = true;
                using (client)
                using (NetworkStream s = client.GetStream())
                {
                    Log("connection from " + peer);

                    Net.WriteAscii(s, "RFB 003.008\n");
                    Net.ReadExact(s, 12); // client version

                    if (cfg.Password.Length > 0)
                    {
                        s.WriteByte(1);
                        s.WriteByte(2); // VNC Authentication
                        Log("auth: security types sent");
                        int sel = Net.ReadByteOrThrow(s);
                        Log("auth: selection=" + sel);
                        if (sel != 2)
                        {
                            Net.WriteU32(s, 1);
                            return;
                        }

                        byte[] challenge = new byte[16];
                        lock (rnd) rnd.NextBytes(challenge);
                        s.Write(challenge, 0, challenge.Length);
                        Log("auth: challenge sent");

                        byte[] response = Net.ReadExact(s, 16);
                        Log("auth: response received");
                        byte[] expected = VncAuth.Response(cfg.Password, challenge);
                        if (!Net.FixedEquals(response, expected))
                        {
                            Net.WriteU32(s, 1);
                            WriteReason(s, "Wrong password");
                            Log("wrong password from " + peer);
                            return;
                        }
                        Net.WriteU32(s, 0);
                    }
                    else
                    {
                        s.WriteByte(1);
                        s.WriteByte(1); // None
                        Net.ReadByteOrThrow(s);
                        Net.WriteU32(s, 0);
                    }

                    Net.ReadByteOrThrow(s); // ClientInit
                    WriteServerInit(s);

                    Log("client authenticated: " + peer);

                    while (true)
                    {
                        int type = Net.ReadByteOrThrow(s);
                        switch (type)
                        {
                            case 0: // SetPixelFormat
                                Net.ReadExact(s, 19);
                                break;

                            case 2: // SetEncodings
                                Net.ReadExact(s, 1);
                                {
                                    int count = Net.ReadU16(s);
                                    Net.ReadExact(s, count * 4);
                                }
                                break;

                            case 3: // FramebufferUpdateRequest
                                {
                                    int incremental = Net.ReadByteOrThrow(s);
                                    Net.ReadU16(s); Net.ReadU16(s);
                                    Net.ReadU16(s); Net.ReadU16(s);
                                    SendUpdate(s, incremental != 0);
                                }
                                break;

                            case 4: // KeyEvent
                                {
                                    int down = Net.ReadByteOrThrow(s);
                                    Net.ReadExact(s, 2);
                                    uint keysym = Net.ReadU32(s);
                                    Input.Key(down != 0, keysym);
                                }
                                break;

                            case 5: // PointerEvent
                                {
                                    int mask = Net.ReadByteOrThrow(s);
                                    int x = Net.ReadU16(s);
                                    int y = Net.ReadU16(s);
                                    Input.Pointer(mask, x, y);
                                }
                                break;

                            case 6: // ClientCutText
                                Net.ReadExact(s, 3);
                                {
                                    int len = Net.ReadS32(s);
                                    if (len > 0 && len < 16 * 1024 * 1024) Net.ReadExact(s, len);
                                }
                                break;

                            default:
                                Log("client " + peer + " closed the session (type " + type + ")");
                                return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("session " + peer + " ended: " + ex.Message);
            }
        }

        private static void WriteServerInit(NetworkStream s)
        {
            MemoryStream ms = new MemoryStream();
            Net.WriteU16(ms, grabber.Width);
            Net.WriteU16(ms, grabber.Height);

            // 32 bpp, depth 24, little-endian, true colour, BGRX
            ms.WriteByte(32);
            ms.WriteByte(24);
            ms.WriteByte(0);
            ms.WriteByte(1);
            Net.WriteU16(ms, 255);
            Net.WriteU16(ms, 255);
            Net.WriteU16(ms, 255);
            ms.WriteByte(16); // red shift
            ms.WriteByte(8);  // green shift
            ms.WriteByte(0);  // blue shift
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(0);

            byte[] name = Encoding.ASCII.GetBytes(cfg.Name);
            Net.WriteU32(ms, (uint)name.Length);
            ms.Write(name, 0, name.Length);

            byte[] buf = ms.ToArray();
            s.Write(buf, 0, buf.Length);
        }

        private static void SendUpdate(NetworkStream s, bool incremental)
        {
            List<RectInfo> rects;
            lock (grabber)
            {
                grabber.Capture();
                rects = grabber.GetChanges(!incremental);
            }

            if (incremental && rects.Count == 0) Thread.Sleep(10);

            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0); // FramebufferUpdate
            ms.WriteByte(0); // padding
            Net.WriteU16(ms, rects.Count);
            for (int i = 0; i < rects.Count; i++)
            {
                RectInfo r = rects[i];
                Net.WriteU16(ms, r.X);
                Net.WriteU16(ms, r.Y);
                Net.WriteU16(ms, r.Width);
                Net.WriteU16(ms, r.Height);
                Net.WriteS32(ms, 0); // Raw
                ms.Write(r.Data, 0, r.Data.Length);
            }

            byte[] buf = ms.ToArray();
            s.Write(buf, 0, buf.Length);
        }

        private static void WriteReason(NetworkStream s, string reason)
        {
            byte[] b = Encoding.ASCII.GetBytes(reason);
            Net.WriteU32(s, (uint)b.Length);
            s.Write(b, 0, b.Length);
        }

        public static void Log(string message)
        {
            try
            {
                lock (logLock)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mvncp-server.log");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    internal class Config
    {
        public int Port = 5900;
        public string Password = "";
        public string Name = "mvncp";
        public IPAddress Bind = IPAddress.Any;

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                if (key == "port")
                {
                    int p;
                    if (int.TryParse(val, out p) && p > 0 && p < 65536) c.Port = p;
                }
                else if (key == "password") c.Password = val;
                else if (key == "name") c.Name = val;
                else if (key == "bind")
                {
                    IPAddress a;
                    if (val.Equals("any", StringComparison.OrdinalIgnoreCase)) c.Bind = IPAddress.Any;
                    else if (IPAddress.TryParse(val, out a)) c.Bind = a;
                }
            }
            return c;
        }
    }

    // ------------------------------------------------------------------
    internal class ScreenGrabber : IDisposable
    {
        private const int Tile = 128;

        public readonly int Width;
        public readonly int Height;

        private readonly Bitmap bmp;
        private readonly Graphics g;
        private readonly byte[] cur;
        private readonly byte[] prev;

        public ScreenGrabber()
        {
            Width = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            Height = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (Width < 1) Width = 1024;
            if (Height < 1) Height = 768;

            bmp = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            g = Graphics.FromImage(bmp);
            cur = new byte[Width * Height * 4];
            prev = new byte[Width * Height * 4];
        }

        public void Capture()
        {
            g.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height), CopyPixelOperation.SourceCopy);
        }

        public List<RectInfo> GetChanges(bool full)
        {
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, Width, Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                int rowBytes = Width * 4;
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), cur, y * rowBytes, rowBytes);
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            List<RectInfo> list = new List<RectInfo>();

            if (full)
            {
                RectInfo all = new RectInfo();
                all.X = 0; all.Y = 0; all.Width = Width; all.Height = Height;
                all.Data = (byte[])cur.Clone();
                list.Add(all);
            }
            else
            {
                for (int ty = 0; ty < Height; ty += Tile)
                {
                    for (int tx = 0; tx < Width; tx += Tile)
                    {
                        int tw = Math.Min(Tile, Width - tx);
                        int th = Math.Min(Tile, Height - ty);
                        if (TileEquals(tx, ty, tw, th)) continue;

                        byte[] data = new byte[tw * th * 4];
                        for (int row = 0; row < th; row++)
                            Buffer.BlockCopy(cur, ((ty + row) * Width + tx) * 4, data, row * tw * 4, tw * 4);

                        RectInfo r = new RectInfo();
                        r.X = tx; r.Y = ty; r.Width = tw; r.Height = th;
                        r.Data = data;
                        list.Add(r);
                    }
                }
            }

            Buffer.BlockCopy(cur, 0, prev, 0, cur.Length);
            return list;
        }

        private bool TileEquals(int x, int y, int w, int h)
        {
            int bytes = w * 4;
            for (int row = 0; row < h; row++)
            {
                int off = ((y + row) * Width + x) * 4;
                if (!BytesEqual(cur, off, prev, off, bytes)) return false;
            }
            return true;
        }

        private static bool BytesEqual(byte[] a, int ao, byte[] b, int bo, int len)
        {
            int i = 0;
            for (; i + 8 <= len; i += 8)
                if (BitConverter.ToInt64(a, ao + i) != BitConverter.ToInt64(b, bo + i)) return false;
            for (; i < len; i++)
                if (a[ao + i] != b[bo + i]) return false;
            return true;
        }

        public void Dispose()
        {
            try { g.Dispose(); } catch { }
            try { bmp.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    internal static class Input
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char ch);

        private const uint KEYUP = 0x0002;
        private const uint LEFT_DOWN = 0x0002, LEFT_UP = 0x0004;
        private const uint RIGHT_DOWN = 0x0008, RIGHT_UP = 0x0010;
        private const uint MIDDLE_DOWN = 0x0020, MIDDLE_UP = 0x0040;
        private const uint WHEEL = 0x0800;
        private const byte VK_SHIFT = 0x10;

        private static readonly object sync = new object();
        private static int prevMask;
        private static readonly Dictionary<uint, KeyState> pressed = new Dictionary<uint, KeyState>();

        private class KeyState
        {
            public byte Vk;
            public bool Shift;
        }

        public static void Pointer(int mask, int x, int y)
        {
            lock (sync)
            {
                SetCursorPos(x, y);

                if ((mask & 1) != 0 && (prevMask & 1) == 0) mouse_event(LEFT_DOWN, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 1) == 0 && (prevMask & 1) != 0) mouse_event(LEFT_UP, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 2) != 0 && (prevMask & 2) == 0) mouse_event(MIDDLE_DOWN, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 2) == 0 && (prevMask & 2) != 0) mouse_event(MIDDLE_UP, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 4) != 0 && (prevMask & 4) == 0) mouse_event(RIGHT_DOWN, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 4) == 0 && (prevMask & 4) != 0) mouse_event(RIGHT_UP, 0, 0, 0, UIntPtr.Zero);
                if ((mask & 8) != 0 && (prevMask & 8) == 0) mouse_event(WHEEL, 0, 0, 120, UIntPtr.Zero);
                if ((mask & 16) != 0 && (prevMask & 16) == 0) mouse_event(WHEEL, 0, 0, unchecked((uint)-120), UIntPtr.Zero);

                prevMask = mask;
            }
        }

        public static void Key(bool down, uint keysym)
        {
            lock (sync)
            {
                KeyState st;

                if (!down)
                {
                    if (pressed.TryGetValue(keysym, out st))
                    {
                        keybd_event(st.Vk, 0, KEYUP, UIntPtr.Zero);
                        if (st.Shift) keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);
                        pressed.Remove(keysym);
                    }
                    return;
                }

                byte vk;
                bool shift;
                if (!Map(keysym, out vk, out shift)) return;

                if (pressed.ContainsKey(keysym))
                {
                    keybd_event(vk, 0, 0, UIntPtr.Zero); // auto-repeat
                    return;
                }

                if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, 0, UIntPtr.Zero);

                KeyState ns = new KeyState();
                ns.Vk = vk;
                ns.Shift = shift;
                pressed[keysym] = ns;
            }
        }

        private static bool Map(uint k, out byte vk, out bool shift)
        {
            vk = 0;
            shift = false;

            switch (k)
            {
                case Ks.BackSpace: vk = 0x08; return true;
                case Ks.Tab: vk = 0x09; return true;
                case Ks.Return: vk = 0x0D; return true;
                case Ks.Escape: vk = 0x1B; return true;
                case Ks.Space: vk = 0x20; return true;
                case Ks.Insert: vk = 0x2D; return true;
                case Ks.Delete: vk = 0x2E; return true;
                case Ks.Home: vk = 0x24; return true;
                case Ks.End: vk = 0x23; return true;
                case Ks.PageUp: vk = 0x21; return true;
                case Ks.PageDown: vk = 0x22; return true;
                case Ks.Left: vk = 0x25; return true;
                case Ks.Up: vk = 0x26; return true;
                case Ks.Right: vk = 0x27; return true;
                case Ks.Down: vk = 0x28; return true;
                case Ks.ShiftL: vk = 0x10; return true;
                case Ks.CtrlL: vk = 0x11; return true;
                case Ks.AltL: vk = 0x12; return true;
                case Ks.SuperL: vk = 0x5B; return true;
                case Ks.CapsLock: vk = 0x14; return true;
                case Ks.NumLock: vk = 0x90; return true;
                case Ks.ScrollLock: vk = 0x91; return true;
                case Ks.Print: vk = 0x2C; return true;
                case Ks.Pause: vk = 0x13; return true;
            }

            if (k >= Ks.F1 && k <= Ks.F1 + 11)
            {
                vk = (byte)(0x70 + (k - Ks.F1));
                return true;
            }

            if (k >= 0x20 && k <= 0xFFFF)
            {
                char c = (char)k;
                if (char.IsControl(c)) return false;
                short r = VkKeyScan(c);
                if (r == -1) return false;
                vk = (byte)(r & 0xFF);
                shift = (r & 0x100) != 0;
                return true;
            }

            return false;
        }
    }
}
