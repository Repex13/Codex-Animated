using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

internal static class LookTests
{
    private static int failures, total;

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--apply")
        {
            ThemeLibrary.Apply(args[1], args[2]);
            Console.WriteLine("applied \"" + args[1] + "\" to " + args[2]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--sheets") return Sheets();
        if (args.Length == 4 && args[0] == "--render-splash") return RenderSplash(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--codepoints")
        {
            string value = IniFile.Load(args[1]).Get(args[2]) ?? "";
            var points = new List<string>();
            foreach (char c in value) points.Add("U+" + ((int)c).ToString("X4"));
            Console.WriteLine(args[2] + " = " + value + "  " + string.Join(" ", points.ToArray()));
            return 0;
        }

        IniRoundTrip();
        LookClamps();
        SplashTextCases();
        ProfileRoundTrip();
        RenderIdentity();
        TintCase();
        DimCase();
        BlurResolutionIndependent();
        BlurKeepsBorder();
        BlurCurve();
        CoverSourceCases();
        AccentCases();
        ApplyCase();
        SplashPageCase();
        LibraryOperations();
        NameValidation();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL " + total + " CHECKS PASSED" : failures + " of " + total + " CHECKS FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        total++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
    }

    private static string Temp(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "LookTests-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void IniRoundTrip()
    {
        string folder = Temp("ini");
        string path = Path.Combine(folder, "a.ini");
        var ini = new IniFile();
        ini.Set("Unknown", "kept");
        ini.Set("title", "\u8072\u306E\u5F62");
        ini.Set("note", "one\r\ntwo");
        ini.Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        Check("ini: no BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), null);
        string text = new UTF8Encoding(false).GetString(bytes);
        Check("ini: CRLF lines, line break in value stays on one line",
            text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length == 3, null);
        IniFile back = IniFile.Load(path);
        Check("ini: unknown key kept and key lookup case-insensitive", back.Get("UNKNOWN") == "kept", null);
        Check("ini: Japanese value survives", back.Get("title") == "\u8072\u306E\u5F62", null);
        Check("ini: line break stripped", back.Get("note") == "onetwo", back.Get("note"));
        Check("ini: missing key is null", back.Get("nope") == null, null);
        File.WriteAllText(path, "# c\r\n; c\r\n\r\n  key = a=b \r\nkey=last\r\nbad line\r\n", new UTF8Encoding(false));
        back = IniFile.Load(path);
        Check("ini: comments skipped, split at first =, last duplicate wins", back.Get("key") == "last", back.Get("key"));
        File.WriteAllText(path, "k = a=b \r\n", new UTF8Encoding(false));
        Check("ini: value keeps later =", IniFile.Load(path).Get("k") == "a=b", null);
        Check("ini: missing file reads empty", IniFile.Load(Path.Combine(folder, "none.ini")).Get("x") == null, null);
    }

    private static void LookClamps()
    {
        var ini = new IniFile();
        ini.Set("opacity", "300");
        ini.Set("blur", "-5");
        ini.Set("dim", "abc");
        ini.Set("tintColor", "red");
        Look look = Look.Read(ini);
        Check("look: opacity=300 clamps to 40", look.Opacity == 40, look.Opacity.ToString());
        Check("look: blur=-5 clamps to 0", look.Blur == 0, look.Blur.ToString());
        Check("look: dim=abc falls back to 0", look.Dim == 0, look.Dim.ToString());
        Check("look: tintColor=red falls back to #8C66D1", look.TintColor.ToArgb() == Color.FromArgb(0x8C, 0x66, 0xD1).ToArgb(), null);
        Check("look: defaults of an empty file", Look.Read(new IniFile()).Opacity == 15 && Look.Read(new IniFile()).FocusX == 50, null);
        Check("look: alpha for opacity 15 is 38", new Look().Alpha == 38, new Look().Alpha.ToString());
        var round = new Look { Opacity = 22, Blur = 7, Dim = 9, Tint = 61, FocusX = 3, FocusY = 97, TintColor = Color.FromArgb(1, 2, 3) };
        var file = new IniFile();
        round.Write(file);
        Look again = Look.Read(file);
        Check("look: write/read round trip", again.Opacity == 22 && again.Blur == 7 && again.Dim == 9 && again.Tint == 61 &&
            again.FocusX == 3 && again.FocusY == 97 && again.TintColor.ToArgb() == round.TintColor.ToArgb() && again.SameRender(round), null);
    }

    private static void SplashTextCases()
    {
        var ini = new IniFile();
        ini.Set("logs", "a|b|c|d|e|f");
        ini.Set("accent", "#b49cff");
        Check("text: more than 4 log lines keeps 4", SplashText.Read(ini).Logs.Length == 4, null);
        Check("text: accent normalized to upper case", SplashText.Read(ini).Accent == "#B49CFF", SplashText.Read(ini).Accent);
        ini.Set("accent", "javascript:1");
        Check("text: invalid accent reads as empty", SplashText.Read(ini).Accent == "", null);
        ini.Set("title", new string('x', 60));
        Check("text: title limited to 40", SplashText.Read(ini).Title.Length == 40, null);
        var written = new IniFile();
        new SplashText { Title = "T", Subtitle = "S", Accent = "#112233", Logs = new[] { "one", "two" } }.Write(written);
        Check("text: logs joined with pipe", written.Get("logs") == "one|two", written.Get("logs"));
    }

    private static void ProfileRoundTrip()
    {
        string root = Temp("profile");
        string path = Path.Combine(root, "settings.ini");
        var seed = new IniFile();
        seed.Set("futureKey", "42");
        seed.Save(path);
        AppProfile profile = AppProfile.Load(root);
        profile.SplashOnStart = false;
        profile.Speed = 2;
        profile.Theme = "T";
        profile.Look.Blur = 33;
        profile.Save();
        AppProfile back = AppProfile.Load(root);
        Check("profile: splash=off and speed=2 kept", !back.SplashOnStart && back.Speed == 2, null);
        Check("profile: theme and look kept", back.Theme == "T" && back.Look.Blur == 33, null);
        Check("profile: unknown key kept", IniFile.Load(path).Get("futureKey") == "42", null);
        Check("profile: file documents every known key",
            IniFile.Load(path).Get("focusX") == "50" && IniFile.Load(path).Get("logs") == "" && IniFile.Load(path).Get("splash") == "off", null);
        Check("profile: missing file gives defaults", AppProfile.Load(Temp("empty")).SplashOnStart && AppProfile.Load(Temp("empty2")).Speed == 1, null);
    }

    private static Bitmap Solid(int w, int h, Color color)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color);
        return bitmap;
    }

    private static void RenderIdentity()
    {
        using (var image = Solid(40, 30, Color.Orange))
        {
            Bitmap result = ArtworkLook.Render(image, new Look());
            Check("render: blur 0, tint 0, dim 0 returns the same object", object.ReferenceEquals(result, image), null);
            Bitmap tiny = ArtworkLook.Render(image, new Look { Blur = 1 });
            Check("render: negligible blur returns the same object", object.ReferenceEquals(tiny, image), null);
        }
    }

    private static Bitmap RandomImage(int w, int h, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        return bitmap;
    }

    private static void TintCase()
    {
        using (var image = RandomImage(48, 48, 7))
        {
            var look = new Look { Tint = 100, TintColor = Color.FromArgb(0x8C, 0x66, 0xD1) };
            using (Bitmap result = ArtworkLook.Render(image, look))
            {
                int worst = 0;
                for (int y = 0; y < 48; y++)
                    for (int x = 0; x < 48; x++)
                    {
                        Color s = image.GetPixel(x, y), o = result.GetPixel(x, y);
                        double luma = 0.299 * s.R + 0.587 * s.G + 0.114 * s.B;
                        worst = Math.Max(worst, Math.Abs(o.R - (int)Math.Round(luma * 140 / 255)));
                        worst = Math.Max(worst, Math.Abs(o.G - (int)Math.Round(luma * 102 / 255)));
                        worst = Math.Max(worst, Math.Abs(o.B - (int)Math.Round(luma * 209 / 255)));
                    }
                Check("tint: tint=100 equals luma * tint colour within 2 levels", worst <= 2, "worst " + worst);
            }
        }
    }

    private static double MeanLuma(Bitmap bitmap)
    {
        double sum = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color c = bitmap.GetPixel(x, y);
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            }
        return sum / (bitmap.Width * bitmap.Height);
    }

    private static void DimCase()
    {
        using (var image = RandomImage(32, 32, 11))
        {
            double previous = MeanLuma(image);
            bool decreasing = true;
            string trail = previous.ToString("0.0");
            foreach (int dim in new[] { 20, 40, 80 })
                using (Bitmap result = ArtworkLook.Render(image, new Look { Dim = dim }))
                {
                    double now = MeanLuma(result);
                    trail += " > " + now.ToString("0.0");
                    if (!(now < previous)) decreasing = false;
                    previous = now;
                }
            Check("dim: mean luminance strictly decreases across 0, 20, 40, 80", decreasing, trail);
        }
        using (var white = Solid(8, 8, Color.White))
        using (Bitmap result = ArtworkLook.Render(white, new Look { Dim = 80 }))
        {
            Color c = result.GetPixel(4, 4);
            Check("dim: dim=80 on white is (57, 67, 74) within 2",
                Math.Abs(c.R - 57) <= 2 && Math.Abs(c.G - 67) <= 2 && Math.Abs(c.B - 74) <= 2, c.R + "," + c.G + "," + c.B);
        }
    }

    private static Bitmap Scaled(Bitmap src, int w, int h)
    {
        var result = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(result))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return result;
    }

    private static double MeanDifference(Bitmap a, Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        return sum / (3.0 * a.Width * a.Height);
    }

    private static void BlurResolutionIndependent()
    {
        var random = new Random(5);
        using (var big = new Bitmap(2000, 1250, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.FromArgb(40, 60, 90));
                for (int i = 0; i < 700; i++)
                    using (var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))))
                        g.FillRectangle(brush, random.Next(2000), random.Next(1250), 6 + random.Next(70), 6 + random.Next(70));
                using (var pen = new Pen(Color.White, 1.5f))
                    for (int i = 0; i < 400; i++)
                        g.DrawLine(pen, random.Next(2000), random.Next(1250), random.Next(2000), random.Next(1250));
            }
            using (Bitmap small = Scaled(big, 800, 500))
            {
                var look = new Look { Blur = 40 };
                using (Bitmap blurBig = ArtworkLook.Render(big, look))
                using (Bitmap blurSmall = ArtworkLook.Render(small, look))
                using (Bitmap a = Scaled(blurBig, 400, 250))
                using (Bitmap b = Scaled(blurSmall, 400, 250))
                using (Bitmap plain = Scaled(big, 400, 250))
                {
                    double between = MeanDifference(a, b), versusPlain = MeanDifference(a, plain);
                    Console.WriteLine("     measured: 2000px vs 800px blur difference " + between.ToString("0.00") +
                        ", blurred vs unblurred " + versusPlain.ToString("0.00"));
                    Check("blur: same look at 2000x1250 and 800x500 within 6 per channel", between < 6, between.ToString("0.00"));
                    Check("blur: blur=40 differs from the unblurred image by more than 6", versusPlain > 6, versusPlain.ToString("0.00"));
                }
            }
        }
    }

    private static Bitmap Detailed(int w, int h, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(40, 60, 90));
            for (int i = 0; i < 350; i++)
                using (var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))))
                    g.FillRectangle(brush, random.Next(w), random.Next(h), 4 + random.Next(w / 25), 4 + random.Next(w / 25));
            using (var pen = new Pen(Color.White, 1f))
                for (int i = 0; i < 250; i++)
                    g.DrawLine(pen, random.Next(w), random.Next(h), random.Next(w), random.Next(h));
        }
        return bitmap;
    }

    private static void BlurCurve()
    {
        using (Bitmap image = Detailed(960, 600, 9))
        {
            double previous = 0;
            bool growing = true;
            string trail = "";
            double first = 0;
            foreach (int blur in new[] { 6, 12, 20, 35, 60 })
                using (Bitmap result = ArtworkLook.Render(image, new Look { Blur = blur }))
                {
                    double difference = MeanDifference(result, image);
                    if (blur == 6) first = difference;
                    if (!(difference > previous)) growing = false;
                    previous = difference;
                    trail += blur + ":" + difference.ToString("0.00") + " ";
                }
            Console.WriteLine("     measured: mean difference by blur " + trail);
            Check("blur curve: blur=6 already differs from the unblurred image by more than 0.3", first > 0.3, first.ToString("0.00"));
            Check("blur curve: difference grows strictly across 6, 12, 20, 35, 60", growing, trail);
        }
    }

    private static void BlurKeepsBorder()
    {
        using (var white = Solid(300, 200, Color.White))
        using (Bitmap result = ArtworkLook.Render(white, new Look { Blur = 60 }))
        {
            int lowest = 255;
            foreach (Point p in new[] { new Point(0, 0), new Point(299, 0), new Point(0, 199), new Point(299, 199) })
            {
                Color c = result.GetPixel(p.X, p.Y);
                lowest = Math.Min(lowest, Math.Min(c.R, Math.Min(c.G, c.B)));
            }
            Check("blur: border of a white image stays at least 250", lowest >= 250, "lowest " + lowest);
        }
    }

    private static void CoverSourceCases()
    {
        bool ok = true;
        string detail = "";
        foreach (Size image in new[] { new Size(2000, 1000), new Size(1000, 2000) })
        {
            var viewport = new Size(800, 600);
            RectangleF mid = ArtworkLook.CoverSource(image, viewport, 50, 50);
            RectangleF lo = ArtworkLook.CoverSource(image, viewport, 0, 0);
            RectangleF hi = ArtworkLook.CoverSource(image, viewport, 100, 100);
            float overflowX = image.Width - mid.Width, overflowY = image.Height - mid.Height;
            ok &= Math.Abs(lo.X) < 0.01f && Math.Abs(lo.Y) < 0.01f;
            ok &= Math.Abs(hi.X - overflowX) < 0.01f && Math.Abs(hi.Y - overflowY) < 0.01f;
            ok &= Math.Abs(mid.X - overflowX / 2) < 0.01f && Math.Abs(mid.Y - overflowY / 2) < 0.01f;
            foreach (RectangleF r in new[] { lo, mid, hi })
                ok &= r.Left >= -0.01f && r.Top >= -0.01f && r.Right <= image.Width + 0.01f && r.Bottom <= image.Height + 0.01f;
            detail += image + " ";
        }
        Check("cover: focus 0/50/100 on a wide and a tall image, never outside the image", ok, detail);
    }

    private static double HueOf(Color c) { return c.GetHue(); }

    private static void AccentCases()
    {
        using (var red = Solid(120, 80, Color.FromArgb(220, 30, 30)))
        {
            Color[] accents = ArtworkLook.SuggestAccents(red, 3);
            double hue = accents.Length == 0 ? -1 : HueOf(accents[0]);
            Check("accents: mostly red image gives red first", accents.Length > 0 && (hue <= 15 || hue >= 345), "hue " + hue.ToString("0"));
            bool legible = true;
            foreach (Color c in accents) legible &= c.GetBrightness() >= 0.619 && c.GetBrightness() <= 0.801;
            Check("accents: lightness within [0.62, 0.80]", legible, null);
        }
        using (var gray = Solid(120, 80, Color.FromArgb(120, 120, 120)))
            Check("accents: grayscale image gives none", ArtworkLook.SuggestAccents(gray, 3).Length == 0, null);
        using (var split = new Bitmap(120, 80, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(split))
            {
                g.FillRectangle(Brushes.Crimson, 0, 0, 60, 80);
                g.FillRectangle(Brushes.RoyalBlue, 60, 0, 60, 80);
            }
            Color[] accents = ArtworkLook.SuggestAccents(split, 3);
            bool apart = accents.Length >= 2;
            if (apart)
            {
                double d = Math.Abs(HueOf(accents[0]) - HueOf(accents[1]));
                apart = Math.Min(d, 360 - d) > 60;
            }
            bool legible = true;
            foreach (Color c in accents) legible &= c.GetBrightness() >= 0.619 && c.GetBrightness() <= 0.801;
            Check("accents: half red, half blue gives two more than 60 degrees apart", apart && legible, accents.Length + " accents");
        }
    }

    private static void WriteJpeg(string path, Color color)
    {
        using (var image = Solid(20, 20, color)) image.Save(path, ImageFormat.Jpeg);
    }

    private static void ApplyCase()
    {
        string work = Temp("apply");
        string library = Path.Combine(work, "Themes");
        string app = Path.Combine(work, "App");
        Directory.CreateDirectory(app);
        ThemeLibrary.RootOverride = library;
        try
        {
            string withAvatar = Path.Combine(library, "With Avatar");
            string without = Path.Combine(library, "Without");
            Directory.CreateDirectory(withAvatar);
            Directory.CreateDirectory(without);
            WriteJpeg(Path.Combine(withAvatar, "wallpaper.jpg"), Color.Red);
            WriteJpeg(Path.Combine(withAvatar, "avatar.jpg"), Color.Green);
            WriteJpeg(Path.Combine(without, "wallpaper.jpg"), Color.Blue);
            var themeIni = new IniFile();
            themeIni.Set("opacity", "22");
            themeIni.Set("blur", "30");
            themeIni.Set("title", "WITH AVATAR");
            themeIni.Set("accent", "#112233");
            themeIni.Set("logs", "x|y");
            themeIni.Set("themeOnlyKey", "ignored");
            themeIni.Save(Path.Combine(withAvatar, "theme.ini"));

            var seed = new IniFile();
            seed.Set("splash", "off");
            seed.Set("speed", "2");
            seed.Set("keepMe", "yes");
            seed.Set("opacity", "5");
            seed.Save(Path.Combine(app, "settings.ini"));
            WriteJpeg(Path.Combine(app, "avatar.jpg"), Color.Yellow);

            Check("library: names list folders with a wallpaper", string.Join("|", ThemeLibrary.Names()) == "With Avatar|Without", string.Join("|", ThemeLibrary.Names()));
            ThemeLibrary.Apply("With Avatar", app);
            Color wall;
            using (var b = ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(app, "wallpaper.jpg")))) wall = b.GetPixel(5, 5);
            Check("apply: wallpaper copied", wall.R > 200 && wall.G < 60, wall.ToString());
            Color avatar;
            using (var b = ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(app, "avatar.jpg")))) avatar = b.GetPixel(5, 5);
            Check("apply: avatar copied", avatar.G > 100 && avatar.R < 60, avatar.ToString());
            AppProfile profile = AppProfile.Load(app);
            Check("apply: look and text replaced, theme set",
                profile.Look.Opacity == 22 && profile.Look.Blur == 30 && profile.Text.Title == "WITH AVATAR" &&
                profile.Text.Accent == "#112233" && profile.Text.Logs.Length == 2 && profile.Theme == "With Avatar", null);
            Check("apply: splash, speed and unknown keys kept", !profile.SplashOnStart && profile.Speed == 2 &&
                IniFile.Load(Path.Combine(app, "settings.ini")).Get("keepMe") == "yes", null);
            Check("apply: theme-only unknown keys are not copied", IniFile.Load(Path.Combine(app, "settings.ini")).Get("themeOnlyKey") == null, null);
            ThemeLibrary.Apply("Without", app);
            Check("apply: avatar removed when the theme has none", !File.Exists(Path.Combine(app, "avatar.jpg")), null);
            profile = AppProfile.Load(app);
            Check("apply: theme without theme.ini resets look and text to defaults",
                profile.Look.Opacity == 15 && profile.Look.Blur == 0 && profile.Text.Title == "" && profile.Theme == "Without", null);
            bool rejected = false;
            try { ThemeLibrary.Apply("..\\x", app); } catch (ArgumentException) { rejected = true; }
            Check("apply: names with path separators are rejected", rejected, null);
            bool missing = false;
            try { ThemeLibrary.Apply("Nope", app); } catch (FileNotFoundException) { missing = true; }
            Check("apply: unknown theme throws FileNotFoundException", missing, null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    private static void SplashPageCase()
    {
        string folder = Temp("splash");
        var ini = new IniFile();
        ini.Set("title", "<b>\"x\"&");
        ini.Set("accent", "javascript:1");
        ini.Set("logs", "first|second");
        ini.Set("speed", "2");
        ini.Save(Path.Combine(folder, "settings.ini"));
        SplashContent content = SplashContent.Load(folder, "Devin", new Size(800, 600));
        byte[] template = Encoding.UTF8.GetBytes(
            "<main data-rate=\"{{RATE}}\" data-theme=\"{{THEME}}\" style=\"--accent:{{ACCENT}}\">{{APP}}|{{TITLE}}|{{SUBTITLE}}|" +
            "<div>{{LOG1}}</div><div>{{LOG2}}</div><div>{{LOG3}}</div><div>{{LOG4}}</div></main>");
        string html = Encoding.UTF8.GetString(SplashPage.RenderIndex(template, content, "Devin"));
        Check("splash: title is HTML-encoded", html.Contains("&lt;b&gt;&quot;x&quot;&amp;") && !html.Contains("<b>"), html);
        Check("splash: invalid accent becomes #D980A7", html.Contains("--accent:#D980A7"), null);
        Check("splash: two log lines give two filled and two empty tokens",
            html.Contains("<div>&gt; first</div><div>&gt; second</div><div></div><div></div>"), html);
        Check("splash: no token remains", !html.Contains("{{"), null);
        Check("splash: speed=2 from settings.ini", html.Contains("data-rate=\"2\""), null);
        SplashContent plain = SplashContent.Load(Temp("splash-empty"), "Devin", new Size(888, 500));
        Check("splash: defaults use app name and artwork size",
            plain.Title == "DEVIN" && plain.Logs[1] == "> custom artwork" && plain.Logs[2] == "> artwork 888 \u00D7 500" && plain.Logs[3] == "> opening Devin", plain.Logs[2]);
    }

    private static int RenderSplash(string folder, string app, string output)
    {
        string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(Path.Combine(output, "assets"));
        File.Copy(Path.Combine(web, "style.css"), Path.Combine(output, "style.css"), true);
        File.Copy(Path.Combine(web, "animation.js"), Path.Combine(output, "animation.js"), true);
        File.Copy(Path.Combine(web, "assets", "contours.js"), Path.Combine(output, "assets", "contours.js"), true);
        string wallpaper = Path.Combine(folder, "wallpaper.jpg");
        if (!File.Exists(wallpaper)) wallpaper = Path.Combine(web, "assets", "artwork.jpg");
        string avatar = Path.Combine(folder, "avatar.jpg");
        if (!File.Exists(avatar)) avatar = wallpaper;
        File.Copy(wallpaper, Path.Combine(output, "assets", "artwork.jpg"), true);
        File.Copy(avatar, Path.Combine(output, "assets", "avatar.jpg"), true);
        Size size;
        using (Bitmap decoded = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(wallpaper))) size = decoded.Size;
        SplashContent content = SplashContent.Load(folder, app, size);
        File.WriteAllBytes(Path.Combine(output, "index.html"),
            SplashPage.RenderIndex(File.ReadAllBytes(Path.Combine(web, "index.html")), content, app));
        Console.WriteLine("rendered " + folder + " -> " + output);
        return 0;
    }

    private static void LibraryOperations()
    {
        string work = Temp("ops");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            string source = Path.Combine(work, "My Wall.png");
            using (var image = Solid(60, 40, Color.FromArgb(200, 40, 40))) image.Save(source, ImageFormat.Png);
            string first = ThemeLibrary.Create(source);
            string second = ThemeLibrary.Create(source);
            Check("create: two creates from the same file name give X and X 2", first == "My Wall" && second == "My Wall 2", first + " | " + second);
            IniFile created = IniFile.Load(Path.Combine(ThemeLibrary.Folder(first), "theme.ini"));
            Check("create: defaults (opacity 15, title upper case, red-ish accent)",
                created.Get("opacity") == "15" && created.Get("blur") == "0" && created.Get("title") == "MY WALL" && (created.Get("accent") ?? "").StartsWith("#"), created.Get("accent"));
            Check("create: wallpaper.jpg is a decodable JPEG", ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder(first), "wallpaper.jpg"))).Width == 60, null);
            bool bad = false;
            string junk = Path.Combine(work, "junk.png");
            File.WriteAllText(junk, "not an image");
            try { ThemeLibrary.Create(junk); } catch (InvalidDataException) { bad = true; }
            Check("create: an unreadable image is refused and leaves no folder", bad && ThemeLibrary.Names().Length == 2, null);

            string onTheme = Path.Combine(work, "AppOn"), other = Path.Combine(work, "AppOther");
            Directory.CreateDirectory(onTheme);
            Directory.CreateDirectory(other);
            WriteJpeg(Path.Combine(onTheme, "wallpaper.jpg"), Color.Blue);
            WriteJpeg(Path.Combine(other, "wallpaper.jpg"), Color.Blue);
            string imageBefore = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(onTheme, "wallpaper.jpg")));
            AppProfile a = AppProfile.Load(onTheme); a.Theme = first; a.SplashOnStart = false; a.Save();
            AppProfile b = AppProfile.Load(other); b.Theme = second; b.Save();
            string otherBefore = File.ReadAllText(Path.Combine(other, "settings.ini"));
            var look = new Look { Opacity = 33, Blur = 21, Tint = 40 };
            var text = new SplashText { Title = "Edited", Accent = "#101010", Logs = new[] { "a", "b" } };
            SplashText stored;
            ThemeLibrary.Save(first, look, text, new[] { onTheme, other }, out stored);
            AppProfile reread = AppProfile.Load(onTheme);
            Check("save: theme.ini written and the app on that theme follows", IniFile.Load(Path.Combine(ThemeLibrary.Folder(first), "theme.ini")).Get("blur") == "21" &&
                reread.Look.Opacity == 33 && reread.Look.Blur == 21 && reread.Text.Title == "Edited" && !reread.SplashOnStart, null);
            Check("save: an app on another theme is not touched", File.ReadAllText(Path.Combine(other, "settings.ini")) == otherBefore, null);
            Check("save: the app's images are not touched", Convert.ToBase64String(File.ReadAllBytes(Path.Combine(onTheme, "wallpaper.jpg"))) == imageBefore, null);
            Color shown; SplashText.TryParseColor(stored.Accent, out shown);
            Check("save: a dark accent is stored in legible form", ArtworkLook.Legible(shown).ToArgb() == shown.ToArgb() && shown.GetBrightness() >= 0.619, stored.Accent);

            string renamed = ThemeLibrary.Rename(first, "Fresh Name", new[] { onTheme, other });
            Check("rename: folder moved and theme= follows only the app on it",
                renamed == "Fresh Name" && Directory.Exists(ThemeLibrary.Folder("Fresh Name")) && !Directory.Exists(ThemeLibrary.Folder(first)) &&
                AppProfile.Load(onTheme).Theme == "Fresh Name" && AppProfile.Load(other).Theme == second, null);
            string caseOnly = ThemeLibrary.Rename("Fresh Name", "fresh name", new[] { onTheme });
            bool exactCase = false;
            foreach (string n in ThemeLibrary.Names()) if (n == "fresh name") exactCase = true;
            Check("rename: a letter-case-only change works", caseOnly == "fresh name" && exactCase && AppProfile.Load(onTheme).Theme == "fresh name", null);
            bool clash = false;
            try { ThemeLibrary.Rename("fresh name", second, new string[0]); } catch (IOException) { clash = true; }
            Check("rename: refuses an existing name", clash, null);

            string settingsBefore = File.ReadAllText(Path.Combine(onTheme, "settings.ini"));
            ThemeLibrary.ClearReferences("fresh name", new[] { onTheme, other });
            string settingsAfter = File.ReadAllText(Path.Combine(onTheme, "settings.ini"));
            Check("clear references: theme= emptied, nothing else changed",
                AppProfile.Load(onTheme).Theme == "" && settingsAfter == settingsBefore.Replace("theme=fresh name", "theme=") && AppProfile.Load(other).Theme == second, null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    private static void NameValidation()
    {
        string[] bad = { "", "   ", new string('x', 61), "a/b", "a:b", "what?", "CON", "nul", "Com1", "con.txt", "ends.", "tab	here" };
        bool allRejected = true;
        string leaked = "";
        foreach (string name in bad)
        {
            try { ThemeLibrary.ValidateName(name); allRejected = false; leaked += "[" + name + "]"; }
            catch (ArgumentException) { }
        }
        Check("names: empty, too long, invalid characters, device names and trailing dot are rejected", allRejected, leaked);
        Check("names: trimmed and ordinary names pass", ThemeLibrary.ValidateName("  Hunter x Hunter ") == "Hunter x Hunter" && ThemeLibrary.ValidateName(new string('y', 60)).Length == 60, null);
    }

    private static int Sheets()
    {
        string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
        foreach (string name in ThemeLibrary.Names())
        {
            string folder = ThemeLibrary.Folder(name);
            Look look = Look.Read(IniFile.Load(Path.Combine(folder, "theme.ini")));
            using (Bitmap wallpaper = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"))))
            using (Bitmap styled = ArtworkLook.Render(wallpaper, look))
            using (var sheet = new Bitmap(1280, 400, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x1E, 0x1E, 0x1E));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                RectangleF crop = ArtworkLook.CoverSource(styled.Size, new Size(640, 400), look.FocusX, look.FocusY);
                g.DrawImage(styled, new Rectangle(0, 0, 640, 400), crop, GraphicsUnit.Pixel);
                using (var attrs = new ImageAttributes())
                {
                    var matrix = new ColorMatrix();
                    matrix.Matrix33 = look.Alpha / 255f;
                    attrs.SetColorMatrix(matrix);
                    g.DrawImage(styled, new Rectangle(640, 0, 640, 400), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attrs);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(0xD0, 0xD0, 0xD0)))
                {
                    string[] lines = {
                        name + "  (opacity " + look.Opacity + "%, blur " + look.Blur + ", dim " + look.Dim + ", tint " + look.Tint + ")",
                        "PS C:\\project> git status",
                        "On branch main. Your branch is up to date with origin/main.",
                        "  modified:   source/ArtworkLook.cs",
                        "  modified:   source/ThemeSettings.cs",
                        "The quick brown fox jumps over the lazy dog 0123456789"
                    };
                    for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], font, brush, 660, 24 + i * 22);
                }
                string file = Path.Combine(output, "sheet-" + name + ".png");
                sheet.Save(file, ImageFormat.Png);
                Console.WriteLine(file);
            }
        }
        return 0;
    }
}
