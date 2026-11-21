using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("Theme Studio")]
[assembly: AssemblyProduct("Theme Studio")]
[assembly: AssemblyDescription("Theme library and look editor for the Animated launchers")]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

internal static class ThemeStudioProgram
{
    private static readonly string[] bundleFiles = {
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
        "notices/LICENSE.txt", "notices/NOTICE.txt"
    };
    internal static string BundleDirectory { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        bool evalMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANIMATED_STUDIO_EVAL"));
        bool first;
        using (var mutex = new Mutex(true, @"Local\AnimatedLaunchers.ThemeStudio", out first))
        {
            if (!first)
            {
                if (!evalMode)
                {
                    uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                    foreach (var other in System.Diagnostics.Process.GetProcessesByName("ThemeStudio"))
                        using (other)
                        {
                            if ((uint)other.Id == self) continue;
                            IntPtr existing = other.MainWindowHandle;
                            if (existing == IntPtr.Zero) continue;
                            if (IsIconic(existing)) ShowWindow(existing, 9);
                            SetForegroundWindow(existing);
                            break;
                        }
                }
                return 0;
            }
            try
            {
                BundleDirectory = EmbeddedBundle.Prepare(typeof(ThemeStudioProgram).Assembly, ThemeLibrary.SharedRoot,
                    "ThemeStudio.Bundle.", bundleFiles);
                EmbeddedBundle.ResolveWebViewFrom(BundleDirectory);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                    ex is System.Security.SecurityException)) throw;
                MessageBox.Show("Windows could not prepare the Theme Studio files.\n\n" + ex.Message,
                    "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return Run(args);
        }
    }

    // Keep WebView2 references out of Main until the assembly resolver is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new StudioForm(args)) Application.Run(form);
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed class StudioApp
{
    internal readonly string Id, Name, Product, Helper;
    internal StudioApp(string id, string name) { Id = id; Name = name; Product = name + " Animated"; Helper = name + "Animated"; }
    internal string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product); } }
}

internal sealed class StudioForm : Form
{
    private const string Origin = "https://studio.invalid/";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly StudioApp[] Apps = { new StudioApp("devin", "Devin"), new StudioApp("antigravity", "Antigravity"), new StudioApp("codex", "Codex"), new StudioApp("claude", "Claude") };
    private static readonly string[] Commands = {
        "state", "saveTheme", "applyTheme", "createTheme", "renameTheme", "deleteTheme", "replaceWallpaper",
        "setAvatar", "clearAvatar", "setApp", "setAutostart", "startHelper", "playSplash" };

    private readonly WebView2 browser;
    private readonly string[] args;
    private readonly bool evalMode;
    private readonly string devFolder;
    private readonly string evalFile;
    private readonly System.Windows.Forms.Timer evalTimer = new System.Windows.Forms.Timer();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    private readonly Dictionary<string, byte[]> scaledCache = new Dictionary<string, byte[]>();
    private readonly Dictionary<string, string[]> accentCache = new Dictionary<string, string[]>();
    private readonly Dictionary<string, bool> brokenCache = new Dictionary<string, bool>();
    private string previewKey;
    private Bitmap previewSource;
    private string evalStamp = "";
    private bool closingSaved;

    internal StudioForm(string[] args)
    {
        this.args = args;
        evalFile = Environment.GetEnvironmentVariable("ANIMATED_STUDIO_EVAL");
        evalMode = !string.IsNullOrEmpty(evalFile);
        devFolder = Environment.GetEnvironmentVariable("ANIMATED_STUDIO_DEV");
        Text = "Theme Studio";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(0x0F, 0x10, 0x14);
        StartPosition = FormStartPosition.Manual;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
        PlaceWindow();
        browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = false };
        Controls.Add(browser);
        Shown += Start;
        evalTimer.Interval = 150;
        evalTimer.Tick += delegate { PollEval(); };
    }

    protected override bool ShowWithoutActivation { get { return evalMode; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, caption = 0x14100F, text = 0xF4EEEC; // COLORREF is BGR
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        DwmSetWindowAttribute(Handle, 36, ref text, 4);
    }

    private static string StudioIni { get { return Path.Combine(ThemeLibrary.SharedRoot, "studio.ini"); } }

    private static float ScaleAt(Point point)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(point, 2);
            uint x, y;
            if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0) return x / 96f;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1f;
    }

    private void SetSizes(float scale)
    {
        ClientSize = new Size((int)Math.Round(1240 * scale), (int)Math.Round(800 * scale));
        MinimumSize = SizeFromClientSize(new Size((int)Math.Round(1040 * scale), (int)Math.Round(660 * scale)));
    }

    // Client size and minimum size are in CSS pixels, scaled by the DPI of the monitor the window opens on.
    private void PlaceWindow()
    {
        Rectangle saved;
        string value = IniFile.Load(StudioIni).Get("window");
        if (value != null && TryParseRect(value, out saved))
        {
            var center = new Point(saved.Left + saved.Width / 2, saved.Top + saved.Height / 2);
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.Contains(center))
                {
                    SetSizes(ScaleAt(center));
                    Bounds = saved;
                    return;
                }
        }
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        SetSizes(ScaleAt(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)));
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private static bool TryParseRect(string text, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        string[] parts = text.Split(',');
        int[] n = new int[4];
        if (parts.Length != 4) return false;
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[2] < 400 || n[3] < 300) return false;
        rect = new Rectangle(n[0], n[1], n[2], n[3]);
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingSaved)
        {
            closingSaved = true;
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                IniFile ini = IniFile.Load(StudioIni);
                ini.Set("window", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", bounds.X, bounds.Y, bounds.Width, bounds.Height));
                ini.Save(StudioIni);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            evalTimer.Dispose();
            if (previewSource != null) previewSource.Dispose();
        }
        base.Dispose(disposing);
    }

    private async void Start(object sender, EventArgs e)
    {
        try
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(ThemeStudioProgram.BundleDirectory);
            string profile = Path.Combine(ThemeLibrary.SharedRoot, "WebView2");
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-networking --disable-sync" };
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            await browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = !string.IsNullOrEmpty(devFolder);
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = !string.IsNullOrEmpty(devFolder);
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (s, a) => a.Handled = true;
            core.PermissionRequested += (s, a) => a.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (s, a) => a.Cancel = true;
            core.LaunchingExternalUriScheme += (s, a) => a.Cancel = true;
            core.NavigationStarting += (s, a) => a.Cancel = !a.Uri.StartsWith(Origin + "index.html", StringComparison.Ordinal);
            core.FrameNavigationStarting += (s, a) => a.Cancel = !a.Uri.StartsWith(Origin + "splash/", StringComparison.Ordinal);
            core.WebMessageReceived += OnMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, a) => Serve(environment, a);
            string hash = "";
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--open") hash = "#" + args[i + 1].Replace(" ", "%20");
            core.Navigate(Origin + "index.html" + hash);
            if (evalMode) evalTimer.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show("Theme Studio could not start its window.\n\n" + error.GetBaseException().Message +
                "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed.", "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // ---- resources ----

    private static Dictionary<string, string> Query(Uri uri)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string query = uri.Query;
        if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
        foreach (string pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));
            map[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return map;
    }

    private static int Number(Dictionary<string, string> q, string key, int fallback, int min, int max)
    {
        string text;
        int value;
        if (!q.TryGetValue(key, out text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs a)
    {
        string uriText = a.Request.Uri;
        if (!uriText.StartsWith(Origin, StringComparison.Ordinal))
        {
            a.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
            return;
        }
        byte[] body = null;
        string type = "text/plain; charset=utf-8";
        int status = 403;
        try
        {
            if (a.Request.Method == "GET")
            {
                var uri = new Uri(uriText);
                string[] segments = uri.AbsolutePath.TrimStart('/').Split('/');
                for (int i = 0; i < segments.Length; i++) segments[i] = Uri.UnescapeDataString(segments[i]);
                status = 404;
                body = Route(segments, Query(uri), ref type);
                if (body != null) status = 200;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ExternalException || error is OutOfMemoryException || error is UriFormatException)) throw;
            body = null;
            status = 404;
        }
        string headers = "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
        a.Response = environment.CreateWebResourceResponse(body == null ? null : new MemoryStream(body, false), status,
            status == 200 ? "OK" : status == 404 ? "Not Found" : "Forbidden", headers);
    }

    private byte[] Route(string[] s, Dictionary<string, string> q, ref string type)
    {
        if (s.Length == 1 && (s[0] == "index.html" || s[0] == "studio.css" || s[0] == "studio.js"))
        {
            type = s[0].EndsWith(".html") ? "text/html; charset=utf-8" : s[0].EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            return PageFile(s[0]);
        }
        if (s.Length == 2 && s[0] == "img")
        {
            string theme;
            if (!q.TryGetValue("theme", out theme)) return null;
            theme = ThemeLibrary.Exact(theme);
            if (theme == null) return null;
            type = "image/jpeg";
            if (s[1] == "wallpaper") return ScaledWallpaper(theme, Number(q, "w", 160, 16, 2400));
            if (s[1] == "avatar")
            {
                string avatar = Path.Combine(ThemeLibrary.Folder(theme), "avatar.jpg");
                return File.Exists(avatar) ? ImageCodec.ReadBytes(avatar) : null;
            }
            if (s[1] == "look") return LookImage(theme, q);
            return null;
        }
        if (s.Length >= 4 && s[0] == "splash") return SplashFile(s, q, ref type);
        return null;
    }

    private byte[] PageFile(string name)
    {
        if (!string.IsNullOrEmpty(devFolder)) return File.ReadAllBytes(Path.Combine(devFolder, name));
        return Embedded("studio/" + name);
    }

    private static byte[] Embedded(string resource)
    {
        using (Stream stream = typeof(StudioForm).Assembly.GetManifestResourceStream(resource))
        {
            if (stream == null) return null;
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        using (var output = new MemoryStream())
        using (var options = new EncoderParameters(1))
        {
            options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            ImageCodecInfo codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
            bitmap.Save(output, codec, options);
            return output.ToArray();
        }
    }

    private static Bitmap Scale(Bitmap src, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(result))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return result;
    }

    private byte[] ScaledWallpaper(string theme, int width)
    {
        string key = theme + "|" + width + "|" + ThemeLibrary.Stamp(theme);
        byte[] cached;
        if (scaledCache.TryGetValue(key, out cached)) return cached;
        using (Bitmap source = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(ThemeLibrary.Folder(theme), "wallpaper.jpg"))))
        using (Bitmap small = width >= source.Width ? null : Scale(source, width, Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width))))
        {
            cached = EncodeJpeg(small ?? source, 85);
        }
        if (scaledCache.Count > 80) scaledCache.Clear();
        scaledCache[key] = cached;
        return cached;
    }

    private byte[] LookImage(string theme, Dictionary<string, string> q)
    {
        string key = theme + "|" + ThemeLibrary.Stamp(theme);
        if (previewKey != key)
        {
            using (Bitmap source = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(ThemeLibrary.Folder(theme), "wallpaper.jpg"))))
            {
                double zoom = Math.Min(1.0, 960.0 / Math.Max(source.Width, source.Height));
                Bitmap fresh = zoom >= 1.0 ? new Bitmap(source) : Scale(source, Math.Max(1, (int)Math.Round(source.Width * zoom)), Math.Max(1, (int)Math.Round(source.Height * zoom)));
                if (previewSource != null) previewSource.Dispose();
                previewSource = fresh;
                previewKey = key;
            }
        }
        var look = new Look { Blur = Number(q, "blur", 0, 0, 100), Dim = Number(q, "dim", 0, 0, 80), Tint = Number(q, "tint", 0, 0, 100) };
        Color tint;
        string tintText;
        if (q.TryGetValue("tintColor", out tintText) && SplashText.TryParseColor(tintText, out tint)) look.TintColor = tint;
        Bitmap rendered = ArtworkLook.Render(previewSource, look);
        try { return EncodeJpeg(rendered, 88); }
        finally { if (!object.ReferenceEquals(rendered, previewSource)) rendered.Dispose(); }
    }

    private byte[] SplashFile(string[] s, Dictionary<string, string> q, ref string type)
    {
        string theme = ThemeLibrary.Exact(s[1]);
        if (theme == null) return null;
        string folder = ThemeLibrary.Folder(theme);
        string file = string.Join("/", s, 3, s.Length - 3);
        switch (file)
        {
            case "index.html":
            {
                string id;
                q.TryGetValue("app", out id);
                StudioApp app = FindApp(id) ?? Apps[0];
                byte[] wallpaper = ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"));
                Size size;
                using (var memory = new MemoryStream(wallpaper, false))
                using (var image = Image.FromStream(memory, false, false)) size = image.Size;
                SplashContent content = SplashContent.Load(folder, app.Name, size);
                type = "text/html; charset=utf-8";
                return SplashPage.RenderIndex(Embedded("web/index.html"), content, app.Name);
            }
            case "style.css": type = "text/css; charset=utf-8"; return Embedded("web/style.css");
            case "animation.js": type = "text/javascript; charset=utf-8"; return Embedded("web/animation.js");
            case "assets/contours.js": type = "text/javascript; charset=utf-8"; return Embedded("web/assets/contours.js");
            case "assets/artwork.jpg":
                type = "image/jpeg";
                return ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"));
            case "assets/avatar.jpg":
            {
                type = "image/jpeg";
                string avatar = Path.Combine(folder, "avatar.jpg");
                return ImageCodec.ReadBytes(File.Exists(avatar) ? avatar : Path.Combine(folder, "wallpaper.jpg"));
            }
        }
        return null;
    }

    // ---- bridge ----

    private static StudioApp FindApp(string id)
    {
        foreach (StudioApp app in Apps) if (string.Equals(app.Id, id, StringComparison.Ordinal)) return app;
        return null;
    }

    private static IEnumerable<string> AppRoots()
    {
        foreach (StudioApp app in Apps) yield return app.Root;
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs a)
    {
        // The embedded splash frame also posts "complete"; only the studio page itself may command the host.
        if (!a.Source.StartsWith(Origin + "index.html", StringComparison.Ordinal)) return;
        Dictionary<string, object> message = null;
        try { message = json.DeserializeObject(a.WebMessageAsJson) as Dictionary<string, object>; }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        if (message == null) return;
        object cmdValue;
        if (!message.TryGetValue("cmd", out cmdValue) || !(cmdValue is string) || Array.IndexOf(Commands, (string)cmdValue) < 0) return;
        object id;
        message.TryGetValue("id", out id);
        var reply = new Dictionary<string, object>();
        reply["id"] = id;
        try
        {
            reply["data"] = Execute((string)cmdValue, message);
            reply["ok"] = true;
        }
        catch (Exception error)
        {
            reply["ok"] = false;
            reply["error"] = Friendly(error);
        }
        browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(reply));
    }

    private static string Friendly(Exception error)
    {
        if (error is InvalidDataException) return "Couldn't read that image. Use a JPEG, PNG or BMP under 64 megapixels.";
        if (error is ArgumentException || error is IOException || error is FileNotFoundException || error is UnauthorizedAccessException ||
            error is InvalidOperationException || error is Win32Exception || error is ExternalException || error is NotSupportedException)
            return error.Message;
        return "Something went wrong: " + error.Message;
    }

    private static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    private static bool Flag(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    private static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    private static int Int(Dictionary<string, object> d, string key, int fallback)
    {
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (FormatException) { return fallback; } catch (InvalidCastException) { return fallback; }
    }

    private string ExistingTheme(Dictionary<string, object> m)
    {
        string name = ThemeLibrary.Exact(Str(m, "name"));
        if (name == null) throw new FileNotFoundException("That theme no longer exists. Pick another one.");
        return name;
    }

    private object Execute(string cmd, Dictionary<string, object> m)
    {
        switch (cmd)
        {
            case "state": return State();
            case "saveTheme": return SaveTheme(m);
            case "applyTheme":
            {
                string name = ExistingTheme(m);
                string target = Str(m, "app");
                if (target == "all") foreach (StudioApp app in Apps) ThemeLibrary.Apply(name, app.Root);
                else
                {
                    StudioApp app = FindApp(target);
                    if (app == null) throw new ArgumentException("Unknown app.");
                    ThemeLibrary.Apply(name, app.Root);
                }
                return AppList();
            }
            case "createTheme":
            {
                string path = TestPath(m) ?? PickImage("Choose a wallpaper image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                string created = ThemeLibrary.Create(path);
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "renameTheme":
            {
                string name = ExistingTheme(m);
                string renamed = ThemeLibrary.Rename(name, Str(m, "newName"), AppRoots());
                Dictionary<string, object> state = State();
                state["renamed"] = renamed;
                return state;
            }
            case "deleteTheme":
            {
                string name = ExistingTheme(m);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(ThemeLibrary.Folder(name),
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ThemeLibrary.ClearReferences(name, AppRoots());
                return State();
            }
            case "replaceWallpaper":
            {
                string name = ExistingTheme(m);
                string path = TestPath(m) ?? PickImage("Choose a wallpaper image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                byte[] jpeg = ImageCodec.ToJpeg(path);
                using (ImageCodec.DecodeJpeg(jpeg)) { }
                string target = Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { ImageCodec.SaveAtomic(target, jpeg); });
                ReapplyTo(name);
                return State();
            }
            case "setAvatar":
            {
                string name = ExistingTheme(m);
                string path = TestPath(m) ?? PickImage("Choose an avatar image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                byte[] jpeg = ImageCodec.ToJpeg(path);
                using (ImageCodec.DecodeJpeg(jpeg)) { }
                string target = Path.Combine(ThemeLibrary.Folder(name), "avatar.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { ImageCodec.SaveAtomic(target, jpeg); });
                ReapplyTo(name);
                return State();
            }
            case "clearAvatar":
            {
                string name = ExistingTheme(m);
                string target = Path.Combine(ThemeLibrary.Folder(name), "avatar.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { File.Delete(target); });
                ReapplyTo(name);
                return State();
            }
            case "setApp":
            {
                StudioApp app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                AppProfile profile = AppProfile.LoadStrict(app.Root);
                if (m.ContainsKey("splash")) profile.SplashOnStart = Flag(m, "splash");
                if (m.ContainsKey("speed")) profile.Speed = Int(m, "speed", 1) == 2 ? 2 : 1;
                profile.Save();
                return AppList();
            }
            case "setAutostart":
            {
                StudioApp app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (Flag(m, "on"))
                        key.SetValue(app.Helper, "\"" + Path.Combine(StudioFolder(), app.Helper + ".exe") + "\" --artwork");
                    else key.DeleteValue(app.Helper, false);
                }
                return AppList();
            }
            case "startHelper":
            {
                StudioApp app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                string exe = Path.Combine(StudioFolder(), app.Helper + ".exe");
                if (!File.Exists(exe)) throw new FileNotFoundException(app.Helper + ".exe is not next to Theme Studio, so the helper can't be started.");
                using (Process.Start(new ProcessStartInfo(exe, "--artwork") { UseShellExecute = false })) { }
                for (int i = 0; i < 15 && !HelperRunning(app); i++) Thread.Sleep(100);
                return AppList();
            }
            case "playSplash":
            {
                string name = ExistingTheme(m);
                StudioApp app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                string exe = Path.Combine(StudioFolder(), app.Helper + ".exe");
                string arguments = "--preview \"" + ThemeLibrary.Folder(name) + "\" " + Handle64();
                if (evalMode && Flag(m, "dryRun"))
                    return new Dictionary<string, object> { { "exe", exe }, { "args", arguments }, { "hwnd", Handle64() } };
                if (!File.Exists(exe)) throw new FileNotFoundException(app.Helper + ".exe is not next to Theme Studio, so the animation can't be played.");
                using (Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })) { }
                return new Dictionary<string, object>();
            }
        }
        throw new ArgumentException("Unknown command.");
    }

    private string Handle64() { return Handle.ToInt64().ToString(CultureInfo.InvariantCulture); }

    private static string StudioFolder() { return Path.GetDirectoryName(Application.ExecutablePath); }

    private string TestPath(Dictionary<string, object> m)
    {
        // Test-only: the native file dialog cannot be driven without input.
        string path = Str(m, "path");
        return evalMode && !string.IsNullOrEmpty(path) ? path : null;
    }

    private string PickImage(string title)
    {
        using (var dialog = new OpenFileDialog {
            Title = title, RestoreDirectory = true,
            Filter = "Images (*.jpg, *.jpeg, *.png, *.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private void ReapplyTo(string name)
    {
        foreach (string root in AppRoots())
            if (ThemeLibrary.UsesTheme(root, name)) ThemeLibrary.Apply(name, root);
    }

    private object SaveTheme(Dictionary<string, object> m)
    {
        string name = ExistingTheme(m);
        Dictionary<string, object> l = Obj(m, "look") ?? new Dictionary<string, object>();
        Dictionary<string, object> t = Obj(m, "text") ?? new Dictionary<string, object>();
        var ini = new IniFile();
        foreach (string key in new[] { "opacity", "blur", "dim", "tint", "tintColor", "focusX", "focusY" })
            if (Str(l, key) != null) ini.Set(key, Str(l, key));
        foreach (string key in new[] { "title", "subtitle", "accent" })
            if (Str(t, key) != null) ini.Set(key, Str(t, key));
        object logs;
        if (t.TryGetValue("logs", out logs) && logs is System.Collections.IEnumerable && !(logs is string))
        {
            var lines = new List<string>();
            foreach (object line in (System.Collections.IEnumerable)logs) lines.Add(Convert.ToString(line, CultureInfo.InvariantCulture));
            ini.Set("logs", string.Join("|", lines.ToArray()));
        }
        SplashText stored;
        Look clamped = ThemeLibrary.Save(name, Look.Read(ini), SplashText.Read(ini), AppRoots(), out stored);
        return new Dictionary<string, object> { { "look", LookData(clamped) }, { "text", TextData(stored) } };
    }

    private static Dictionary<string, object> LookData(Look look)
    {
        return new Dictionary<string, object> {
            { "opacity", look.Opacity }, { "blur", look.Blur }, { "dim", look.Dim }, { "tint", look.Tint },
            { "tintColor", string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", look.TintColor.R, look.TintColor.G, look.TintColor.B) },
            { "focusX", look.FocusX }, { "focusY", look.FocusY }
        };
    }

    private static Dictionary<string, object> TextData(SplashText text)
    {
        return new Dictionary<string, object> {
            { "title", text.Title }, { "subtitle", text.Subtitle }, { "accent", text.Accent }, { "logs", text.Logs }
        };
    }

    private bool HelperRunning(StudioApp app)
    {
        Mutex running;
        if (!Mutex.TryOpenExisting(@"Local\" + app.Helper + "Splash.Artwork", out running)) return false;
        running.Dispose();
        return true;
    }

    private object AppList()
    {
        var list = new List<object>();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            foreach (StudioApp app in Apps)
            {
                AppProfile profile = AppProfile.Load(app.Root);
                list.Add(new Dictionary<string, object> {
                    { "id", app.Id }, { "name", app.Name }, { "theme", profile.Theme }, { "splash", profile.SplashOnStart },
                    { "speed", profile.Speed }, { "helperExists", File.Exists(Path.Combine(StudioFolder(), app.Helper + ".exe")) },
                    { "helperRunning", HelperRunning(app) }, { "autostart", key != null && key.GetValue(app.Helper) != null }
                });
            }
        return list;
    }

    private Dictionary<string, object> State()
    {
        var themes = new List<object>();
        foreach (string name in ThemeLibrary.Names())
        {
            string folder = ThemeLibrary.Folder(name);
            IniFile ini = IniFile.Load(Path.Combine(folder, "theme.ini"));
            string stamp = name + "|" + ThemeLibrary.Stamp(name);
            string[] accents;
            bool broken;
            if (!accentCache.TryGetValue(stamp, out accents))
            {
                var found = new List<string>();
                broken = false;
                try
                {
                    using (Bitmap bitmap = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"))))
                        foreach (Color c in ArtworkLook.SuggestAccents(bitmap, 4))
                            found.Add(string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
                }
                catch (Exception error)
                {
                    if (!(error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
                        error is ArgumentException || error is ExternalException || error is OutOfMemoryException)) throw;
                    broken = true;
                }
                accents = found.ToArray();
                accentCache[stamp] = accents;
                brokenCache[stamp] = broken;
            }
            brokenCache.TryGetValue(stamp, out broken);
            themes.Add(new Dictionary<string, object> {
                { "name", name }, { "look", LookData(Look.Read(ini)) }, { "text", TextData(SplashText.Read(ini)) },
                { "hasAvatar", File.Exists(Path.Combine(folder, "avatar.jpg")) }, { "stamp", ThemeLibrary.Stamp(name) },
                { "accents", accents }, { "broken", broken }
            });
        }
        Rectangle area = Screen.FromControl(this).WorkingArea;
        return new Dictionary<string, object> {
            { "themes", themes }, { "apps", AppList() },
            { "screen", new Dictionary<string, object> { { "w", area.Width }, { "h", area.Height } } }
        };
    }

    // ---- eval hook (tests only, ANIMATED_STUDIO_EVAL=<file>) ----

    private void PollEval()
    {
        FileInfo info = new FileInfo(evalFile);
        if (!info.Exists) return;
        string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
        if (stamp == evalStamp) return;
        string code;
        try { code = File.ReadAllText(evalFile, Encoding.UTF8); }
        catch (IOException) { return; }
        evalStamp = stamp;
        RunEval(code);
    }

    private async void RunEval(string code)
    {
        string result;
        try
        {
            CoreWebView2 core = browser.CoreWebView2;
            if (code.StartsWith("@capture ", StringComparison.Ordinal))
            {
                string path = code.Substring(9).Trim();
                using (var file = File.Create(path))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                result = json.Serialize(new Dictionary<string, object> { { "ok", true }, { "captured", path } });
            }
            else if (code.StartsWith("@close", StringComparison.Ordinal))
            {
                result = json.Serialize(new Dictionary<string, object> { { "ok", true }, { "closing", true } });
                File.WriteAllText(evalFile + ".out", result, new UTF8Encoding(false));
                BeginInvoke(new Action(Close));
                return;
            }
            else
            {
                string wrapped = "(function(){try{var r=(function(){" + code + "\n})();return JSON.stringify({ok:true,value:r===undefined?null:r});}" +
                    "catch(e){return JSON.stringify({ok:false,error:String((e&&e.stack)||e)});}})()";
                string raw = await core.ExecuteScriptAsync(wrapped);
                result = json.DeserializeObject(raw) as string ?? raw;
            }
        }
        catch (Exception error)
        {
            result = json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", error.Message } });
        }
        File.WriteAllText(evalFile + ".out", result, new UTF8Encoding(false));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
