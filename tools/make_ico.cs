using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Regenerate an .ico with one frame per size the shell asks for, reduced from
// the largest image the source carries. The source may be a PNG or an .ico:
//
//   make_ico.exe assets\branding\LIMISAW1.png LIMISAW.ico
//   make_ico.exe heh.ico heh.new.ico
//
// Every frame here is a nearest-neighbour reduction with compositing and
// smoothing off, so nothing is ever interpolated: the artwork is intentional
// pixel/block art and must not be "improved" with bicubic blending. 16/32/64
// (and 128/256 when the master is large enough) are exact integer ratios;
// 24 and 48 are not, but they are still point-sampled rather than blended.
//
// Frames are PNG-compressed (the Vista+ icon format), which keeps the file
// small instead of shipping ~99 KB of raw DIBs. That choice is only safe
// because the app loads its icon through `LoadImage` (see Assets.cs):
// System.Drawing.Icon misparses a PNG frame when it picks a size out of a
// multi-frame file — it takes the BITMAPINFOHEADER path and reads past the
// end. If the loader ever goes back to `new Icon(path, w, h)`, this must go
// back to DIBs.
//
// An .ico source is decoded from its largest frame's own bytes (PNG frame if
// it carries one, otherwise through Icon.ToBitmap), exactly as before.
//
// Build + run (only needed when the artwork changes):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo ^
//     -out:make_ico.exe -r:System.dll -r:System.Drawing.dll tools\make_ico.cs
//   make_ico.exe assets\branding\LIMISAW1.png LIMISAW.ico
public static class MakeIco
{
    static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    static Bitmap Master(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        bool png = raw.Length > 8 && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47;
        if (png)
            using (var ms = new MemoryStream(raw))
                return new Bitmap(Image.FromStream(ms));
        // Otherwise treat it as an icon directory and take its largest frame.
        if (raw.Length < 6) throw new ArgumentException("not a PNG or .ico file: " + path);
        int count = BitConverter.ToUInt16(raw, 4);
        int best = -1, bestSize = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int w = raw[entry] == 0 ? 256 : raw[entry];
            if (w <= bestSize) continue;
            bestSize = w; best = entry;
        }
        if (best < 0) throw new ArgumentException("no icon frames in " + path);
        int length = BitConverter.ToInt32(raw, best + 8);
        int offset = BitConverter.ToInt32(raw, best + 12);
        var slice = new byte[length];
        Array.Copy(raw, offset, slice, 0, length);
        if (slice.Length > 8 && slice[0] == 0x89 && slice[1] == 0x50)   // PNG magic
            using (var ms = new MemoryStream(slice))
                return new Bitmap(Image.FromStream(ms));
        using (var icon = new Icon(path, bestSize, bestSize)) return icon.ToBitmap();
    }

    // Point sampling only. DrawImage with NearestNeighbor + SourceCopy is the
    // nearest-neighbour resample; no filter, no blend, no antialiasing.
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
            Console.WriteLine("usage: make_ico.exe <source.png|source.ico> <output.ico>");
            return 2;
        }
        string src = argv[0], dst = argv[1];
        Bitmap master = Master(src);
        Console.WriteLine("master " + master.Width + "x" + master.Height + " " + master.PixelFormat);

        var frames = new List<byte[]>();
        var dims = new List<int>();
        foreach (int size in Sizes)
        {
            if (size > master.Width && size > master.Height)
            {
                // Upscaling beyond the master would invent pixels the artwork
                // never had; the shell can scale the largest frame itself.
                if (dims.Count > 0) break;
                continue;
            }
            using (Bitmap bmp = Scale(master, size))
            {
                frames.Add(Png(bmp));
                dims.Add(size);
            }
        }
        if (dims.Count == 0) throw new InvalidOperationException("source smaller than the smallest frame and no frame produced");

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
        Console.WriteLine("wrote " + dst + " with " + frames.Count + " frames (" + string.Join("/", dims.ToArray()) + ")");
        return 0;
    }
}
