using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Regenerate heh.ico with one frame per size the shell asks for, reduced from
// the largest frame the source file carries.
//
// The single 128x128 frame LIMISAW inherited was the blur `UI.md` forbids: the
// shell got one oversized image and resampled it into a 16x16 slot. Every frame
// here is a nearest-neighbour reduction with compositing and smoothing off, so
// nothing is ever interpolated. 16/32/64/128 are exact integer ratios of the
// master; 24 and 48 are not, but they are still point-sampled rather than
// blended, and the shell asks for them.
//
// Frames are PNG-compressed (the Vista+ icon format), which is what keeps the
// file at ~4 KB instead of ~99 KB of raw DIBs. That choice is only safe because
// the app loads its icon through `LoadImage` (see Assets.cs): `System.Drawing.Icon`
// misparses a PNG frame when it picks a size out of a multi-frame file — it takes
// the BITMAPINFOHEADER path and reads past the end. If the loader ever goes back
// to `new Icon(path, w, h)`, this must go back to DIBs.
//
// The source frame is itself PNG-compressed, so it is decoded as a PNG rather
// than through `Icon.ToBitmap()`, which flattens the alpha channel.
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

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
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
                frames.Add(Png(bmp));
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
