using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Aura;

/// v0.5.0 «запомнить картинку» (opt-in per remember). A tiny crop (≤120×60) of the control is saved to
/// %AppData%\Aura\templates\{Id}.png — the ONLY image Aura ever writes, and only when the user ticks the box.
/// Matching: one in-memory capture of the foreground window, grayscale SAD (coarse 2× → refine). Never saved.
public static class Templates
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    public static string Dir => Path.Combine(Settings.Dir, "templates");
    static string PathOf(string id) => Path.Combine(Dir, id + ".png");

    public static bool Save(string id, System.Windows.Rect r)
    {
        try
        {
            int w = (int)Math.Min(120, Math.Max(8, r.Width)), h = (int)Math.Min(60, Math.Max(8, r.Height));
            int x = (int)(r.X + r.Width / 2 - w / 2.0), y = (int)(r.Y + r.Height / 2 - h / 2.0);
            Directory.CreateDirectory(Dir);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            bmp.Save(PathOf(id), ImageFormat.Png);
            return true;
        }
        catch { return false; }
    }
    public static void Delete(string id) { try { File.Delete(PathOf(id)); } catch { } }
    public static bool Exists(string id) => File.Exists(PathOf(id));

    static byte[] Gray(Bitmap b, out int w, out int h, int step)
    {
        w = b.Width / step; h = b.Height / step;
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var raw = new byte[d.Stride * b.Height];
        try { Marshal.Copy(d.Scan0, raw, 0, raw.Length); } finally { b.UnlockBits(d); }
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * step) * d.Stride + (x * step) * 4;
                g[y * w + x] = (byte)((raw[i] * 29 + raw[i + 1] * 150 + raw[i + 2] * 77) >> 8);
            }
        Array.Clear(raw);
        return g;
    }

    static long Sad(byte[] img, int iw, byte[] t, int tw, int th, int ox, int oy, long cut)
    {
        long s = 0;
        for (int y = 0; y < th; y++)
        {
            int io = (oy + y) * iw + ox, to = y * tw;
            for (int x = 0; x < tw; x++) s += Math.Abs(img[io + x] - t[to + x]);
            if (s >= cut) return s;
        }
        return s;
    }

    /// Returns screen rect of the best match in the window, or null if mean abs diff > 18 / no template.
    public static System.Windows.Rect? Match(string id, IntPtr hwnd)
    {
        try
        {
            if (!File.Exists(PathOf(id)) || !GetWindowRect(hwnd, out var wr)) return null;
            int W = Math.Min(wr.R - wr.L, 4000), H = Math.Min(wr.B - wr.T, 3000);
            using var tb = new Bitmap(PathOf(id));
            using var tpl = new Bitmap(tb.Width, tb.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(tpl)) g.DrawImage(tb, 0, 0, tb.Width, tb.Height);
            if (W < tpl.Width || H < tpl.Height) return null;
            using var shot = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(wr.L, wr.T, 0, 0, new Size(W, H));
            // coarse pass at 1/2
            var ic = Gray(shot, out int icw, out int ich, 2); var tc = Gray(tpl, out int tcw, out int tch, 2);
            long best = long.MaxValue; int bx = 0, by = 0;
            for (int y = 0; y + tch <= ich; y++)
                for (int x = 0; x + tcw <= icw; x++)
                { var s = Sad(ic, icw, tc, tcw, tch, x, y, best); if (s < best) { best = s; bx = x; by = y; } }
            // refine at full res ±3 px
            var iF = Gray(shot, out int iw, out int ih, 1); var tF = Gray(tpl, out int tw, out int th, 1);
            long fb = long.MaxValue; int fx = bx * 2, fy = by * 2;
            for (int y = Math.Max(0, by * 2 - 3); y <= Math.Min(ih - th, by * 2 + 3); y++)
                for (int x = Math.Max(0, bx * 2 - 3); x <= Math.Min(iw - tw, bx * 2 + 3); x++)
                { var s = Sad(iF, iw, tF, tw, th, x, y, fb); if (s < fb) { fb = s; fx = x; fy = y; } }
            if (fb == long.MaxValue || fb / (double)(tw * th) > 18) return null;
            return new System.Windows.Rect(wr.L + fx, wr.T + fy, tw, th);
        }
        catch { return null; }
    }
}
