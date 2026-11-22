using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mvncp
{
    // ------------------------------------------------------------------
    //  Network helpers
    // ------------------------------------------------------------------
    public static class Net
    {
        public static void WriteAscii(NetworkStream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        public static byte[] ReadExact(NetworkStream s, int count)
        {
            byte[] buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException("Connection closed");
                got += n;
            }
            return buf;
        }

        public static int ReadByteOrThrow(NetworkStream s)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException("Connection closed");
            return b;
        }

        public static void WriteU16(Stream s, int v)
        {
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)(v & 0xFF));
        }

        public static void WriteS32(Stream s, int v)
        {
            WriteU16(s, (v >> 16) & 0xFFFF);
            WriteU16(s, v & 0xFFFF);
        }

        public static void WriteU32(Stream s, uint v)
        {
            WriteU16(s, (int)(v >> 16));
            WriteU16(s, (int)(v & 0xFFFF));
        }

        public static int ReadU16(NetworkStream s)
        {
            byte[] b = ReadExact(s, 2);
            return (b[0] << 8) | b[1];
        }

        public static int ReadS32(NetworkStream s)
        {
            byte[] b = ReadExact(s, 4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        public static uint ReadU32(NetworkStream s)
        {
            return (uint)ReadS32(s);
        }

        public static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    // ------------------------------------------------------------------
    //  VNC authentication (DES, RFB 3.8)
    // ------------------------------------------------------------------
    public static class VncAuth
    {
        public static byte[] Response(string password, byte[] challenge)
        {
            byte[] key = new byte[8];
            byte[] pw = Encoding.ASCII.GetBytes(password == null ? "" : password);
            for (int i = 0; i < 8; i++)
                key[i] = i < pw.Length ? Reverse(pw[i]) : (byte)0;

            using (DES des = DES.Create())
            {
                des.Mode = CipherMode.ECB;
                des.Padding = PaddingMode.None;
                des.Key = key;
                using (ICryptoTransform enc = des.CreateEncryptor())
                    return enc.TransformFinalBlock(challenge, 0, challenge.Length);
            }
        }

        private static byte Reverse(byte b)
        {
            int v = 0;
            for (int i = 0; i < 8; i++)
                if ((b & (1 << i)) != 0) v |= 1 << (7 - i);
            return (byte)v;
        }
    }

    public static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
    }

    public class RectInfo
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public byte[] Data; // BGRA, tightly packed rows
    }

    public class AuthRequiredException : Exception
    {
        public AuthRequiredException(string message) : base(message) { }
    }

    // ------------------------------------------------------------------
    //  Keysyms
    // ------------------------------------------------------------------
    public static class Ks
    {
        public const uint BackSpace = 0xFF08;
        public const uint Tab = 0xFF09;
        public const uint Return = 0xFF0D;
        public const uint Escape = 0xFF1B;
        public const uint Insert = 0xFF63;
        public const uint Delete = 0xFFFF;
        public const uint Home = 0xFF50;
        public const uint End = 0xFF57;
        public const uint PageUp = 0xFF55;
        public const uint PageDown = 0xFF56;
        public const uint Left = 0xFF51;
        public const uint Up = 0xFF52;
        public const uint Right = 0xFF53;
        public const uint Down = 0xFF54;
        public const uint ShiftL = 0xFFE1;
        public const uint CtrlL = 0xFFE3;
        public const uint AltL = 0xFFE9;
        public const uint SuperL = 0xFFEB;
        public const uint CapsLock = 0xFFE5;
        public const uint NumLock = 0xFF7F;
        public const uint ScrollLock = 0xFF14;
        public const uint Print = 0xFF61;
        public const uint Pause = 0xFF13;
        public const uint F1 = 0xFFBE;
        public const uint Space = 0x20;
    }

    // ------------------------------------------------------------------
    //  RFB 3.8 client (used by the viewer and the self-test)
    // ------------------------------------------------------------------
    public class RfbClient : IDisposable
    {
        private TcpClient tcp;
        private NetworkStream stream;
        private readonly object writeLock = new object();
        private bool disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public string ServerName { get; private set; }

        public static Action<string> Trace;

        private static void T(string message)
        {
            Action<string> t = Trace;
            if (t != null) t(message);
        }

        public RfbClient(string host, int port, string password)
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Connect(host, port);
            stream = tcp.GetStream();
            stream.ReadTimeout = 8000;
            stream.WriteTimeout = 8000;
            Handshake(password);
        }

        private void Handshake(string password)
        {
            byte[] ver = Net.ReadExact(stream, 12);
            string vs = Encoding.ASCII.GetString(ver);
            T("server version: " + vs.Trim());
            if (!vs.StartsWith("RFB ")) throw new IOException("Not a VNC server: " + vs.Trim());

            Net.WriteAscii(stream, "RFB 003.008\n");
            T("version sent");

            int typeCount = Net.ReadByteOrThrow(stream);
            T("security types: " + typeCount);
            if (typeCount == 0)
                throw new IOException("Server refused: " + ReadReason());

            byte[] types = Net.ReadExact(stream, typeCount);
            bool hasVnc = Array.IndexOf(types, (byte)2) >= 0;
            bool hasNone = Array.IndexOf(types, (byte)1) >= 0;

            int chosen;
            if (hasVnc) chosen = 2;
            else if (hasNone) chosen = 1;
            else throw new IOException("Unsupported security type");

            stream.WriteByte((byte)chosen);
            T("chosen security type: " + chosen);

            if (chosen == 2)
            {
                if (string.IsNullOrEmpty(password))
                    throw new AuthRequiredException("Password required");
                byte[] challenge = Net.ReadExact(stream, 16);
                T("challenge received");
                byte[] response = VncAuth.Response(password, challenge);
                stream.Write(response, 0, response.Length);
                T("response sent");
            }

            uint result = Net.ReadU32(stream);
            T("auth result: " + result);
            if (result != 0)
            {
                string reason = "";
                try { reason = ReadReason(); } catch { }
                throw new IOException("Authentication rejected" + (reason.Length > 0 ? ": " + reason : ""));
            }

            stream.WriteByte(1); // shared session

            byte[] init = Net.ReadExact(stream, 24);
            Width = (init[0] << 8) | init[1];
            Height = (init[2] << 8) | init[3];
            int bpp = init[4];
            int nameLen = (init[20] << 24) | (init[21] << 16) | (init[22] << 8) | init[23];
            ServerName = nameLen > 0 ? Encoding.ASCII.GetString(Net.ReadExact(stream, nameLen)) : "";

            if (bpp != 32)
                throw new IOException("Only 32-bit server pixel format is supported (got " + bpp + ")");

            SendSetEncodings();
        }

        private string ReadReason()
        {
            int len = (int)Net.ReadU32(stream);
            if (len <= 0 || len > 65536) return "";
            return Encoding.ASCII.GetString(Net.ReadExact(stream, len));
        }

        private void SendSetEncodings()
        {
            lock (writeLock)
            {
                stream.WriteByte(2);
                stream.WriteByte(0);
                Net.WriteU16(stream, 1);
                Net.WriteS32(stream, 0); // Raw
            }
        }

        public void RequestUpdate(bool incremental)
        {
            lock (writeLock)
            {
                stream.WriteByte(3);
                stream.WriteByte((byte)(incremental ? 1 : 0));
                Net.WriteU16(stream, 0);
                Net.WriteU16(stream, 0);
                Net.WriteU16(stream, Width);
                Net.WriteU16(stream, Height);
            }
        }

        public void SendPointer(int mask, int x, int y)
        {
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x >= Width) x = Width - 1;
            if (y >= Height) y = Height - 1;
            lock (writeLock)
            {
                stream.WriteByte(5);
                stream.WriteByte((byte)mask);
                Net.WriteU16(stream, x);
                Net.WriteU16(stream, y);
            }
        }

        public void SendKey(bool down, uint keysym)
        {
            lock (writeLock)
            {
                stream.WriteByte(4);
                stream.WriteByte((byte)(down ? 1 : 0));
                stream.WriteByte(0);
                stream.WriteByte(0);
                Net.WriteU32(stream, keysym);
            }
        }

        // Returns the rectangles of one FramebufferUpdate.
        public List<RectInfo> ReadUpdate()
        {
            while (true)
            {
                int type = Net.ReadByteOrThrow(stream);

                if (type == 0)
                {
                    Net.ReadExact(stream, 1);
                    int count = Net.ReadU16(stream);
                    List<RectInfo> rects = new List<RectInfo>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int x = Net.ReadU16(stream);
                        int y = Net.ReadU16(stream);
                        int w = Net.ReadU16(stream);
                        int h = Net.ReadU16(stream);
                        int enc = Net.ReadS32(stream);
                        if (enc != 0)
                            throw new IOException("Unknown encoding: " + enc);
                        RectInfo r = new RectInfo();
                        r.X = x; r.Y = y; r.Width = w; r.Height = h;
                        r.Data = Net.ReadExact(stream, w * h * 4);
                        rects.Add(r);
                    }
                    return rects;
                }

                if (type == 1) // SetColourMapEntries
                {
                    Net.ReadExact(stream, 1);
                    Net.ReadU16(stream);
                    int n = Net.ReadU16(stream);
                    Net.ReadExact(stream, n * 6);
                }
                else if (type == 2) // Bell
                {
                }
                else if (type == 3) // ServerCutText
                {
                    Net.ReadExact(stream, 3);
                    int len = Net.ReadS32(stream);
                    if (len > 0 && len < 16 * 1024 * 1024) Net.ReadExact(stream, len);
                }
                else
                {
                    throw new IOException("Unknown message type: " + type);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (stream != null) stream.Close(); } catch { }
            try { if (tcp != null) tcp.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    //  Blit a BGRA rectangle into a Bitmap
    // ------------------------------------------------------------------
    public static class PixelCopy
    {
        public static void Blit(Bitmap target, RectInfo rect)
        {
            if (target == null || rect == null) return;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            int x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > target.Width) w = target.Width - x;
            if (y + h > target.Height) h = target.Height - y;
            if (w <= 0 || h <= 0) return;

            BitmapData bd = target.LockBits(new Rectangle(0, 0, target.Width, target.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int srcStride = rect.Width * 4;
                for (int row = 0; row < h; row++)
                {
                    IntPtr dst = IntPtr.Add(bd.Scan0, (y + row) * bd.Stride + x * 4);
                    Marshal.Copy(rect.Data, row * srcStride, dst, w * 4);
                }
            }
            finally
            {
                target.UnlockBits(bd);
            }
        }
    }
}
