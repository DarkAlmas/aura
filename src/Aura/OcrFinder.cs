using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Aura;

/// v0.2.2: opt-in OCR fallback (Windows.Media.Ocr, built into Windows 10/11, offline).
/// Runs ONCE per user-started search, only when UIA found nothing and the user enabled
/// «OCR если UIA не нашёл». Captures only the target window's rect into memory; the bitmap is
/// disposed right after recognition and never written to disk. No timers, no background scanning.
public static class OcrFinder
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    public static bool Available
    {
        get { try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; } catch { return false; } }
    }

    static OcrEngine? CreateEngine()
    {
        try
        {
            var e = OcrEngine.TryCreateFromUserProfileLanguages();
            if (e != null) return e;
            foreach (var l in OcrEngine.AvailableRecognizerLanguages) { e = OcrEngine.TryCreateFromLanguage(l); if (e != null) return e; }
        }
        catch { }
        return null;
    }

    static string Norm(string s) => string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// Tiny word boxes (< 24×16) are padded around their centre to at least 40×24, clamped to the window.
    static System.Windows.Rect Pad(System.Windows.Rect b, RECT wr)
    {
        if (b.Width >= 24 && b.Height >= 16) return b;
        double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, nw = Math.Max(b.Width, 40), nh = Math.Max(b.Height, 24);
        double x = Math.Max(wr.L, cx - nw / 2), y = Math.Max(wr.T, cy - nh / 2);
        return new System.Windows.Rect(x, y, Math.Min(nw, wr.R - x), Math.Min(nh, wr.B - y));
    }

    /// Best exact (case/space-insensitive) match for a learned OCR name among words/lines from Find(hwnd, "").
    public static UiaFinder.Target? Best(List<UiaFinder.Target> all, string name)
    {
        var n = Norm(name);
        return all.FirstOrDefault(t => Norm(t.Name) == n);
    }

    /// Returns null if the engine is missing (no OCR language pack). `exclude` = overlay rect (physical px) to skip.
    public static async Task<List<UiaFinder.Target>?> Find(IntPtr hwnd, string q, System.Windows.Rect exclude)
    {
        var engine = CreateEngine();
        if (engine == null) return null;
        if (!GetWindowRect(hwnd, out var wr)) return new();
        int w = Math.Min(wr.R - wr.L, 8000), h = Math.Min(wr.B - wr.T, 8000);
        if (w < 8 || h < 8) return new();

        OcrResult result; double S = 1;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(wr.L, wr.T, 0, 0, new Size(w, h));
            // v0.4.1: upscale 2× (memory only) so small text is recognised; clamp to OCR engine max dimension.
            S = Math.Min(2.0, (double)OcrEngine.MaxImageDimension / Math.Max(w, h)); if (S < 1) S = 1;
            int sw = (int)(w * S), shh = (int)(h * S);
            using var big = new Bitmap(sw, shh, PixelFormat.Format32bppPArgb);
            using (var g2 = Graphics.FromImage(big)) { g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear; g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g2.DrawImage(bmp, 0, 0, sw, shh); }
            var data = big.LockBits(new Rectangle(0, 0, sw, shh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var bytes = new byte[data.Stride * shh];
            try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); } finally { big.UnlockBits(data); }
            using var sb = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, sw, shh, BitmapAlphaMode.Premultiplied);
            Array.Clear(bytes);
            result = await engine.RecognizeAsync(sb);
        }

        q = (q ?? "").Trim();
        var exact = new List<UiaFinder.Target>(); var part = new List<UiaFinder.Target>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
            foreach (var wd in line.Words)
            {
                var r = wd.BoundingRect;
                x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y); x2 = Math.Max(x2, r.X + r.Width); y2 = Math.Max(y2, r.Y + r.Height);
            }
            var text = line.Text.Trim();
            if (text.Length < 2) continue;
            var b = Pad(new System.Windows.Rect(wr.L + x1 / S, wr.T + y1 / S, (x2 - x1) / S, (y2 - y1) / S), wr);
            if (b.Width < 4 || b.Height < 4) continue;
            if (!exclude.IsEmpty && exclude.Contains(new System.Windows.Point(b.X + b.Width / 2, b.Y + b.Height / 2))) continue;
            var t = new UiaFinder.Target(text, "", "OCR", b, null);
            if (q.Length == 0)
            {
                part.Add(t);
                if (line.Words.Count > 1)
                    foreach (var wd in line.Words) { var r = wd.BoundingRect; part.Add(new UiaFinder.Target(wd.Text.Trim(), "", "OCR", Pad(new System.Windows.Rect(wr.L + r.X / S, wr.T + r.Y / S, r.Width / S, r.Height / S), wr), null)); }
            }
            else if (Norm(text) == Norm(q)) exact.Add(t);
            else if (text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) part.Add(t);
            else
            {   // single word match inside a longer line → use the word box
                foreach (var wd in line.Words)
                {
                    var wt = wd.Text.Trim();
                    if (!wt.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
                    var r = wd.BoundingRect;
                    var wb = Pad(new System.Windows.Rect(wr.L + r.X / S, wr.T + r.Y / S, r.Width / S, r.Height / S), wr);
                    var wtg = new UiaFinder.Target(wt, "", "OCR", wb, null);
                    if (string.Equals(wt, q, StringComparison.CurrentCultureIgnoreCase)) exact.Add(wtg); else part.Add(wtg);
                    break;
                }
            }
        }
        exact.AddRange(part);
        if (q.Length > 0 && exact.Count > 50) exact.RemoveRange(50, exact.Count - 50);
        return exact;
    }
}
