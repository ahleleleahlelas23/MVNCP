using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mvncp
{
    // Console self-test: connects to the server, grabs a frame and saves it as PNG.
    internal static class TestProgram
    {
        private static int Main(string[] args)
        {
            string host = args.Length > 0 ? args[0] : "127.0.0.1";
            int port = args.Length > 1 ? int.Parse(args[1]) : 5900;
            string password = args.Length > 2 ? args[2] : "";
            string outPath = args.Length > 3 ? args[3] : "frame.png";

            RfbClient.Trace = delegate(string m)
            {
                Console.WriteLine("  " + m);
                Console.Out.Flush();
            };

            try
            {
                using (RfbClient c = new RfbClient(host, port, password))
                {
                    Console.WriteLine("handshake OK: " + c.Width + "x" + c.Height + " name='" + c.ServerName + "'");

                    using (Bitmap bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppRgb))
                    {
                        c.RequestUpdate(false);
                        List<RectInfo> rects = c.ReadUpdate();
                        Console.WriteLine("full update: rects=" + rects.Count);

                        for (int i = 0; i < rects.Count; i++) PixelCopy.Blit(bmp, rects[i]);

                        bmp.Save(outPath, ImageFormat.Png);

                        long sum = 0;
                        long nonzero = 0;
                        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            byte[] row = new byte[bmp.Width * 4];
                            for (int y = 0; y < bmp.Height; y++)
                            {
                                Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, row.Length);
                                for (int i = 0; i < row.Length; i++)
                                {
                                    sum += row[i];
                                    if (row[i] != 0) nonzero++;
                                }
                            }
                        }
                        finally
                        {
                            bmp.UnlockBits(bd);
                        }

                        Console.WriteLine("pixels: checksum=" + sum + " nonzero=" + nonzero);
                        if (nonzero == 0) throw new Exception("frame is completely black");
                    }

                    c.RequestUpdate(true);
                    List<RectInfo> inc = c.ReadUpdate();
                    Console.WriteLine("incremental update: rects=" + inc.Count);
                }

                Console.WriteLine("PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: " + ex);
                return 1;
            }
        }
    }
}
