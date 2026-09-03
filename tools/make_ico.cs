using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Regenerate heh.ico with one whole-pixel frame per size the shell asks for,
// scaled from the largest frame the source file carries.
//
// The single 128x128 frame LIMISAW inherited was the blur `UI.md` forbids: the
// shell (and System.Drawing) got one oversized image and resampled it into a
// 16x16 slot. Every frame here is a nearest-neighbour reduction of 128, so no
// smoothing happens at any DPI.
//
// Two format details are load-bearing:
//   * the source frame is PNG-compressed, so it is decoded as a PNG rather than
//     through Icon.ToBitmap(), which flattens the alpha channel;
//   * every frame is written as a classic 32bpp DIB. System.Drawing.Icon
//     misparses a PNG frame when the file holds several frames (it takes the
//     BITMAPINFOHEADER path and reads past the end), and that is exactly the
//     lookup the app does for its window and tray icon. DIBs cost ~99 KB for
//     the whole set, which is the right trade for an icon that always decodes.
//
// Build + run (only needed when the artwork changes):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:make_ico.exe -r:System.dll -r:System.Drawing.dll tools\make_ico.cs
//   make_ico.exe heh.ico heh.new.ico
public static class MakeIco
{
    static Bitmap Master(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        int count = BitConverter.ToUInt16(raw, 4);
        int best = -1, bestSize = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int w = raw[entry] == 0 ? 256 : raw[entry];
            if (w <= bestSize) continue;
            bestSize = w; best = entry;
        }
        int length = BitConverter.ToInt32(raw, best + 8);
        int offset = BitConverter.ToInt32(raw, best + 12);
        var slice = new byte[length];
        Array.Copy(raw, offset, slice, 0, length);
        if (slice.Length > 8 && slice[0] == 0x89 && slice[1] == 0x50)   // PNG magic
            using (var ms = new MemoryStream(slice))
                return new Bitmap(Image.FromStream(ms));
        using (var icon = new Icon(path, bestSize, bestSize)) return icon.ToBitmap();
    }

    static Bitmap Scale(Bitmap master, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(master, new Rectangle(0, 0, size, size),
                new Rectangle(0, 0, master.Width, master.Height), GraphicsUnit.Pixel);
        }
        return bmp;
    }

    static byte[] Dib(Bitmap bmp)    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);          // XOR + AND stacked
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(0);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)                      // DIBs are bottom-up
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
            }
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < w; x++)
                if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(row);
        }
        bw.Flush();
        return ms.ToArray();
    }

    public static int Main(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.WriteLine("usage: make_ico.exe <source.ico> <output.ico>");
            return 2;
        }
        string src = argv[0], dst = argv[1];
        int[] sizes = { 16, 24, 32, 48, 64, 128 };
        Bitmap master = Master(src);
        Console.WriteLine("master " + master.Width + "x" + master.Height + " " + master.PixelFormat);

        var frames = new List<byte[]>();
        var dims = new List<int>();
        foreach (int size in sizes)
            using (Bitmap bmp = Scale(master, size))
            {
                frames.Add(Dib(bmp));
                dims.Add(size);
            }

        using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                w.Write((byte)(dims[i] >= 256 ? 0 : dims[i]));
                w.Write((byte)(dims[i] >= 256 ? 0 : dims[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }
        Console.WriteLine("wrote " + dst + " with " + frames.Count + " frames");
        return 0;
    }
}
