using System.Linq;
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
    // v0.9.7: id must be plain [0-9a-zA-Z] — a hand-edited buttons.json can never point outside templates\
    static string PathOf(string id) => Path.Combine(Dir, (id.Length is > 0 and <= 40 && id.All(char.IsAsciiLetterOrDigit) ? id : "invalid") + ".png");

    public static bool Save(string id, System.Windows.Rect r)
    {
        try
        {
            int w = (int)Math.Min(120, Math.Max(8, r.Width)), h = (int)Math.Min(60, Math.Max(8, r.Height));
            int x = (int)(r.X + r.Width / 2 - w / 2.0), y = (int)(r.Y + r.Height / 2 - h / 2.0);
            Directory.CreateDirectory(Dir);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) if (!Native.SafeCapture(g, x, y, w, h)) return false;
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
    public static System.Windows.Rect? Match(string id, IntPtr hwnd, System.Windows.Rect? hint = null)
    {
        try
        {
            if (hint is System.Windows.Rect hr && !hr.IsEmpty && File.Exists(PathOf(id)))
            {   // v0.5.1 fast path: small capture around the remembered spot (±24 px) first.
                using var tb0 = new Bitmap(PathOf(id));
                using var t0 = new Bitmap(tb0.Width, tb0.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(t0)) g.DrawImage(tb0, 0, 0, tb0.Width, tb0.Height);
                int cx = (int)(hr.X + hr.Width / 2), cy = (int)(hr.Y + hr.Height / 2), M = 24;
                int L = cx - t0.Width / 2 - M, T = cy - t0.Height / 2 - M, w = t0.Width + 2 * M, h = t0.Height + 2 * M;
                using var sh = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(sh)) Native.SafeCapture(g, L, T, w, h);
                var iF0 = Gray(sh, out int iw0, out _, 1); var tF0 = Gray(t0, out int tw0, out int th0, 1);
                long b0 = long.MaxValue; int x0 = 0, y0 = 0;
                for (int y = 0; y <= h - th0; y++) for (int x = 0; x <= w - tw0; x++) { var v = Sad(iF0, iw0, tF0, tw0, th0, x, y, b0); if (v < b0) { b0 = v; x0 = x; y0 = y; } }
                if (b0 != long.MaxValue && b0 / (double)(tw0 * th0) <= 18) return new System.Windows.Rect(L + x0, T + y0, tw0, th0);
            }
            if (!File.Exists(PathOf(id)) || !GetWindowRect(hwnd, out var wr)) return null;
            int W = Math.Min(wr.R - wr.L, 4000), H = Math.Min(wr.B - wr.T, 3000);
            using var tb = new Bitmap(PathOf(id));
            using var tpl = new Bitmap(tb.Width, tb.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(tpl)) g.DrawImage(tb, 0, 0, tb.Width, tb.Height);
            if (W < tpl.Width || H < tpl.Height) return null;
            using var shot = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot)) Native.SafeCapture(g, wr.L, wr.T, W, H);
            // coarse pass at 1/2
            const int K = 4; var ic = Gray(shot, out int icw, out int ich, K); var tc = Gray(tpl, out int tcw, out int tch, K);
            long best = long.MaxValue; int bx = 0, by = 0;
            for (int y = 0; y + tch <= ich; y++)
                for (int x = 0; x + tcw <= icw; x++)
                { var s = Sad(ic, icw, tc, tcw, tch, x, y, best); if (s < best) { best = s; bx = x; by = y; } }
            // refine at full res ±3 px
            var iF = Gray(shot, out int iw, out int ih, 1); var tF = Gray(tpl, out int tw, out int th, 1);
            long fb = long.MaxValue; int fx = bx * K, fy = by * K;
            for (int y = Math.Max(0, by * K - K - 1); y <= Math.Min(ih - th, by * K + K + 1); y++)
                for (int x = Math.Max(0, bx * K - K - 1); x <= Math.Min(iw - tw, bx * K + K + 1); x++)
                { var s = Sad(iF, iw, tF, tw, th, x, y, fb); if (s < fb) { fb = s; fx = x; fy = y; } }
            if (fb == long.MaxValue || fb / (double)(tw * th) > 18) return null;
            return new System.Windows.Rect(wr.L + fx, wr.T + fy, tw, th);
        }
        catch { return null; }
    }
}
