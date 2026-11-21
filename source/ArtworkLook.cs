using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// The one look pipeline shared by the helper, the splash and Theme Studio:
// resolution-independent blur, then tint and dim in a single colour matrix.
internal static class ArtworkLook
{
    private static readonly float[] Shade = { 8 / 255f, 20 / 255f, 29 / 255f };

    // src itself when the look changes no pixels (blur too small to matter, tint 0, dim 0);
    // otherwise a new 24bpp bitmap of the same size. The caller disposes it.
    internal static Bitmap Render(Bitmap src, Look look)
    {
        double sigma = Sigma(look.Blur, Math.Max(src.Width, src.Height));
        bool blur = sigma >= 0.3;
        bool color = look.Tint > 0 || look.Dim > 0;
        if (!blur && !color) return src;
        Bitmap blurred = blur ? Blur(src, sigma) : null;
        if (!color) return blurred;
        try { return Colorize(blurred ?? src, look); }
        finally { if (blurred != null) blurred.Dispose(); }
    }

    // Source rectangle in image pixels that covers the viewport, honouring the focal point.
    internal static RectangleF CoverSource(Size image, Size viewport, int focusX, int focusY)
    {
        focusX = Math.Max(0, Math.Min(100, focusX));
        focusY = Math.Max(0, Math.Min(100, focusY));
        float scale = Math.Max((float)viewport.Width / image.Width, (float)viewport.Height / image.Height);
        float w = Math.Min(image.Width, viewport.Width / scale), h = Math.Min(image.Height, viewport.Height / scale);
        float x = (image.Width - w) * focusX / 100f, y = (image.Height - h) * focusY / 100f;
        return new RectangleF(x, y, w, h);
    }

    // Up to `count` distinct accent candidates sampled from the image, already legible.
    // Empty for near-monochrome images.
    internal static Color[] SuggestAccents(Bitmap src, int count)
    {
        const int bins = 24;
        int longSide = Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)Math.Round(src.Width * Math.Min(1.0, 96.0 / longSide)));
        int h = Math.Max(1, (int)Math.Round(src.Height * Math.Min(1.0, 96.0 / longSide)));
        var weight = new double[bins];
        var hueSum = new double[bins];
        var satSum = new double[bins];
        var hits = new int[bins];
        int qualifying = 0;
        using (var small = Resample(src, w, h))
        {
            byte[] pixels;
            int stride;
            Read(small, out pixels, out stride);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 3;
                    double r = pixels[i + 2] / 255.0, g = pixels[i + 1] / 255.0, b = pixels[i] / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double v = max, s = max <= 0 ? 0 : (max - min) / max;
                    if (s < 0.25 || v < 0.25) continue;
                    double hue = Hue(r, g, b, max, min);
                    int bin = Math.Min(bins - 1, (int)(hue / 15));
                    double wgt = s * v;
                    weight[bin] += wgt;
                    hueSum[bin] += hue * wgt;
                    satSum[bin] += s;
                    hits[bin]++;
                    qualifying++;
                }
            if (qualifying < 0.04 * w * h) return new Color[0];
        }
        var order = new List<int>();
        for (int i = 0; i < bins; i++) if (hits[i] > 0) order.Add(i);
        order.Sort((a, b) => weight[b].CompareTo(weight[a]));
        var taken = new List<double>();
        var result = new List<Color>();
        double top = order.Count > 0 ? weight[order[0]] : 0;
        foreach (int bin in order)
        {
            if (result.Count >= count) break;
            if (weight[bin] < 0.12 * top) continue;
            double hue = hueSum[bin] / weight[bin];
            bool near = false;
            foreach (double other in taken)
            {
                double d = Math.Abs(hue - other);
                if (Math.Min(d, 360 - d) < 30) { near = true; break; }
            }
            if (near) continue;
            taken.Add(hue);
            double sat = Math.Max(0.55, Math.Min(0.85, satSum[bin] / hits[bin]));
            result.Add(Legible(FromHsl(hue, sat, 0.72)));
        }
        return result.ToArray();
    }

    // Any colour made usable as an accent on a dark surface.
    internal static Color Legible(Color color)
    {
        double h, s, l;
        ToHsl(color, out h, out s, out l);
        l = Math.Max(0.62, Math.Min(0.80, l));
        s = Math.Min(s, 0.90);
        return FromHsl(h, s, l);
    }

    private static double Sigma(int blur, int longSide)
    {
        double unit = Math.Max(0, Math.Min(100, blur)) / 100.0;
        return 0.025 * Math.Pow(unit, 1.5) * longSide;
    }

    private static Bitmap Blur(Bitmap src, double sigma)
    {
        int w = src.Width, h = src.Height;
        double scale = Math.Min(1.0, 4.0 / sigma);
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        Bitmap small = scale >= 1.0 ? Copy(src) : Resample(src, sw, sh);
        try
        {
            byte[] data;
            int stride;
            Read(small, out data, out stride);
            var temp = new byte[data.Length];
            double working = sigma * scale;
            if (working < 2.0)
            {
                // Box passes cannot approximate a Gaussian this narrow.
                float[] kernel = GaussianKernel(working);
                GaussHorizontal(data, temp, sw, sh, stride, kernel);
                GaussVertical(temp, data, sw, sh, stride, kernel);
            }
            else
            {
                foreach (int size in BoxSizes(working, 3))
                {
                    int radius = (size - 1) / 2;
                    if (radius < 1) continue;
                    BoxHorizontal(data, temp, sw, sh, stride, radius);
                    BoxVertical(temp, data, sw, sh, stride, radius);
                }
            }
            Write(small, data, stride);
            if (scale >= 1.0) { Bitmap done = small; small = null; return done; }
            return Resample(small, w, h);
        }
        finally { if (small != null) small.Dispose(); }
    }

    // Box widths whose repeated blur approximates a Gaussian of the given sigma.
    private static int[] BoxSizes(double sigma, int passes)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        int upper = lower + 2;
        double m = (12 * sigma * sigma - passes * lower * lower - 4 * passes * lower - 3 * passes) / (-4.0 * lower - 4);
        int switchAt = (int)Math.Round(m);
        var sizes = new int[passes];
        for (int i = 0; i < passes; i++) sizes[i] = i < switchAt ? lower : upper;
        return sizes;
    }

    private static float[] GaussianKernel(double sigma)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new float[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++) sum += Math.Exp(-(i * i) / (2 * sigma * sigma));
        for (int i = -radius; i <= radius; i++) kernel[i + radius] = (float)(Math.Exp(-(i * i) / (2 * sigma * sigma)) / sum);
        return kernel;
    }

    private static void GaussHorizontal(byte[] src, byte[] dst, int w, int h, int stride, float[] kernel)
    {
        int r = kernel.Length / 2;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0;
                    for (int k = -r; k <= r; k++) sum += kernel[k + r] * src[row + Math.Max(0, Math.Min(w - 1, x + k)) * 3 + c];
                    dst[row + x * 3 + c] = (byte)Math.Min(255, (int)(sum + 0.5f));
                }
        }
    }

    private static void GaussVertical(byte[] src, byte[] dst, int w, int h, int stride, float[] kernel)
    {
        int r = kernel.Length / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0;
                    for (int k = -r; k <= r; k++) sum += kernel[k + r] * src[Math.Max(0, Math.Min(h - 1, y + k)) * stride + x * 3 + c];
                    dst[y * stride + x * 3 + c] = (byte)Math.Min(255, (int)(sum + 0.5f));
                }
    }

    private static void BoxHorizontal(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[row + Math.Max(0, Math.Min(w - 1, k)) * 3 + c];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 3 + c] = (byte)((sum + span / 2) / span);
                    sum += src[row + Math.Min(w - 1, x + r + 1) * 3 + c] - src[row + Math.Max(0, x - r) * 3 + c];
                }
            }
        }
    }

    private static void BoxVertical(byte[] src, byte[] dst, int w, int h, int stride, int r)
    {
        int span = 2 * r + 1;
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                int col = x * 3 + c, sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Math.Max(0, Math.Min(h - 1, k)) * stride + col];
                for (int y = 0; y < h; y++)
                {
                    dst[y * stride + col] = (byte)((sum + span / 2) / span);
                    sum += src[Math.Min(h - 1, y + r + 1) * stride + col] - src[Math.Max(0, y - r) * stride + col];
                }
            }
    }

    // Edge clamping and half-pixel offsets keep GDI+ from darkening the borders.
    private static Bitmap Resample(Bitmap src, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using (var g = Graphics.FromImage(result))
            using (var attrs = new ImageAttributes())
            {
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attrs);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static Bitmap Copy(Bitmap src)
    {
        var result = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (var g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static Bitmap Colorize(Bitmap src, Look look)
    {
        double t = look.Tint / 100.0, d = look.Dim / 100.0;
        double[] tint = { look.TintColor.R / 255.0, look.TintColor.G / 255.0, look.TintColor.B / 255.0 };
        double[] luma = { 0.299, 0.587, 0.114 };
        var matrix = new float[5][];
        for (int i = 0; i < 5; i++) matrix[i] = new float[5];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                double duotone = luma[i] * tint[j];
                double mixed = (1 - t) * (i == j ? 1 : 0) + t * duotone;
                matrix[i][j] = (float)((1 - d) * mixed);
            }
            matrix[4][i] = (float)(d * Shade[i]);
        }
        matrix[3][3] = 1;
        matrix[4][4] = 1;
        var result = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        try
        {
            using (var g = Graphics.FromImage(result))
            using (var attrs = new ImageAttributes())
            {
                attrs.SetColorMatrix(new ColorMatrix(matrix));
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attrs);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void Read(Bitmap bitmap, out byte[] data, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData bits = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            stride = Math.Abs(bits.Stride);
            data = new byte[stride * bitmap.Height];
            Marshal.Copy(bits.Scan0, data, 0, data.Length);
        }
        finally { bitmap.UnlockBits(bits); }
    }

    private static void Write(Bitmap bitmap, byte[] data, int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData bits = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try { Marshal.Copy(data, 0, bits.Scan0, data.Length); }
        finally { bitmap.UnlockBits(bits); }
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        double delta = max - min;
        if (delta <= 0) return 0;
        double hue;
        if (max == r) hue = ((g - b) / delta) % 6;
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    private static void ToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        h = Hue(r, g, b, max, min);
        double delta = max - min;
        s = delta <= 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = (((h % 360) + 360) % 360) / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1) { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else { r = c; b = x; }
        double m = l - c / 2;
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
