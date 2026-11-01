using System;
using System.Collections.Generic;
using System.Drawing;
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

    /// Returns null if the engine is missing (no OCR language pack). `exclude` = overlay rect (physical px) to skip.
    public static async Task<List<UiaFinder.Target>?> Find(IntPtr hwnd, string q, System.Windows.Rect exclude)
    {
        var engine = CreateEngine();
        if (engine == null) return null;
        if (!GetWindowRect(hwnd, out var wr)) return new();
        int w = Math.Min(wr.R - wr.L, 8000), h = Math.Min(wr.B - wr.T, 8000);
        if (w < 8 || h < 8) return new();

        OcrResult result;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(wr.L, wr.T, 0, 0, new Size(w, h));
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var bytes = new byte[data.Stride * h];
            try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); } finally { bmp.UnlockBits(data); }
            using var sb = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
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
            var b = new System.Windows.Rect(wr.L + x1, wr.T + y1, x2 - x1, y2 - y1);
            if (b.Width < 4 || b.Height < 4) continue;
            if (!exclude.IsEmpty && exclude.Contains(new System.Windows.Point(b.X + b.Width / 2, b.Y + b.Height / 2))) continue;
            var t = new UiaFinder.Target(text, "", "OCR", b, null);
            if (q.Length == 0) part.Add(t);
            else if (string.Equals(text, q, StringComparison.CurrentCultureIgnoreCase)) exact.Add(t);
            else if (text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) part.Add(t);
            else
            {   // single word match inside a longer line → use the word box
                foreach (var wd in line.Words)
                    if (wd.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    {
                        var r = wd.BoundingRect;
                        part.Add(new UiaFinder.Target(wd.Text, "", "OCR", new System.Windows.Rect(wr.L + r.X, wr.T + r.Y, r.Width, r.Height), null));
                        break;
                    }
            }
        }
        exact.AddRange(part);
        if (exact.Count > 50) exact.RemoveRange(50, exact.Count - 50);
        return exact;
    }
}
