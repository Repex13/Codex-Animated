using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

#if APP_DEVIN
[assembly: AssemblyTitle("Devin Animated")]
[assembly: AssemblyProduct("Devin Animated")]
[assembly: AssemblyDescription("Windows launcher with the startup animation and wallpaper for Devin")]
#elif APP_ANTIGRAVITY
[assembly: AssemblyTitle("Antigravity Animated")]
[assembly: AssemblyProduct("Antigravity Animated")]
[assembly: AssemblyDescription("Windows launcher with the startup animation and wallpaper for Antigravity")]
#elif APP_CODEX
[assembly: AssemblyTitle("Codex Animated")]
[assembly: AssemblyProduct("Codex Animated")]
[assembly: AssemblyDescription("Windows launcher with the startup animation and wallpaper for Codex")]
#endif
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Launcher
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    internal static int Run(string[] args)
    {
        bool preview = args.Length == 1 && args[0] == "--preview";
        bool artwork = args.Length == 1 && args[0] == "--artwork";
        if (args.Length != 0 && !preview && !artwork) return 64;
        string instance = artwork ? "Artwork" : preview ? "Preview" : "Launch";
        bool first;
        using (var mutex = new Mutex(true, @"Local\" + AppTarget.HelperName + "Splash." + instance, out first))
        {
            if (!first && !(artwork && ArtworkUpgrade.TakeOverPreviousHelper(mutex)))
                return preview ? 5 : 0;
            int result = 3;
            string failure = null;
            try
            {
                try
                {
                    if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware();
                }
                catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (artwork)
                {
                    ArtworkOverlay.Run();
                    return 0;
                }
                bool skipSplash = !preview && new WallpaperSettings().SkipWhenOpen &&
                    AppTarget.FindMainWindow() != IntPtr.Zero;
                result = skipSplash ? 0 : Play(out failure);
            }
            catch (Exception error) { failure = error.GetBaseException().Message; Console.Error.WriteLine(error); }
            if (artwork && failure != null)
                MessageBox.Show("The " + AppTarget.Name + " wallpaper helper could not start.\n\n" + failure,
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (preview || artwork) return result;
            if (failure != null)
                MessageBox.Show("The startup animation could not play.\n\n" + failure +
                    "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed and run the full Windows download again. Select OK to continue to " + AppTarget.Name + ".",
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);

            try
            {
                AppTarget.Launch();
            }
            catch (Exception error)
            {
                MessageBox.Show("Windows could not open " + AppTarget.Name + ".\n\n" + error.Message,
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            try
            {
                using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--artwork")
                {
                    UseShellExecute = false
                })) { }
            }
            catch (Exception error)
            {
                MessageBox.Show("Windows could not start the wallpaper helper.\n\n" + error.Message,
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            return 0;
        }
    }

    // Keep animation dependencies out of the normal activation fallback's JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Play(out string failure)
    {
        CoreWebView2Environment.SetLoaderDllFolderPath(PortableStartup.BundleDirectory);
        using (var splash = new Splash())
        {
            Application.Run(splash);
            failure = splash.Failure;
            return splash.Result;
        }
    }
}

internal sealed class LauncherTheme
{
    private readonly string root;

    private LauncherTheme(string root) { this.root = root; }

    internal static LauncherTheme Current
    {
        get { return new LauncherTheme(Path.Combine(PortableStartup.BundleDirectory, "web")); }
    }

    internal static string DefaultWallpaperPath { get { return Current.WallpaperPath; } }
    internal string WallpaperPath { get { return Path.Combine(root, "assets", "artwork.jpg"); } }

    internal Dictionary<string, byte[]> ReadFiles(string settingsDirectory)
    {
        var settings = new WallpaperSettings(WallpaperPath, settingsDirectory);
        byte[] wallpaper = settings.LoadJpeg();
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { "index.html", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Read("index.html")).Replace("{{APP}}", AppTarget.Name.ToUpperInvariant())) },
            { "style.css", Read("style.css") },
            { "animation.js", Read("animation.js") },
            { "assets/artwork.jpg", wallpaper },
            { "assets/avatar.jpg", settings.LoadAvatarJpegOr(wallpaper) },
            { "assets/contours.js", Read("assets/contours.js") }
        };
    }

    private byte[] Read(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(root, name));
        if (data.Length == 0) throw new InvalidDataException("The animation file " + name + " is empty.");
        return data;
    }
}

internal sealed class Splash : Form
{
    private const string Origin = "https://animation.invalid/";
    private const string Page = Origin + "index.html";
    private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        {"index.html", "text/html; charset=utf-8"},
        {"style.css", "text/css; charset=utf-8"},
        {"animation.js", "text/javascript; charset=utf-8"},
        {"assets/artwork.jpg", "image/jpeg"},
        {"assets/avatar.jpg", "image/jpeg"},
        {"assets/contours.js", "text/javascript; charset=utf-8"}
    };

    private readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
    private readonly WebView2 browser;
    private bool finished;
    internal int Result { get; private set; }
    internal string Failure { get; private set; }

    internal Splash()
    {
        Result = 4;
        Text = AppTarget.Product;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(8, 20, 29);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale;
        using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
        int width = (int)Math.Min(1000 * scale, Math.Min(workArea.Width * 0.86, workArea.Height * 0.84 * 1.5));
        int height = width * 2 / 3;
        Bounds = new Rectangle(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
        browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = false };
        Controls.Add(browser);
        KeyDown += SkipOnEscape;
        browser.KeyDown += SkipOnEscape;
        watchdog.Interval = 25000;
        watchdog.Tick += delegate { Fail(2, "The animation did not finish loading or playing within 25 seconds."); };
        Shown += Start;
    }

    private void SkipOnEscape(object sender, KeyEventArgs args)
    {
        if (args.KeyCode != Keys.Escape) return;
        args.SuppressKeyPress = true;
        Finish(4);
    }

    private async void Start(object sender, EventArgs args)
    {
        watchdog.Start();
        string stage = "loading animation assets";
        try
        {
            string settingsDirectory = AppTarget.SettingsDirectory;
            var files = LauncherTheme.Current.ReadFiles(settingsDirectory);
            string profile = Path.Combine(settingsDirectory, "WebView2");
            var options = new CoreWebView2EnvironmentOptions();
            options.AdditionalBrowserArguments = "--disable-background-networking --disable-sync";
            stage = "creating the WebView2 environment";
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            if (finished || IsDisposed) return;
            stage = "initializing the WebView2 control";
            await browser.EnsureCoreWebView2Async(environment);
            if (finished || IsDisposed) return;
            stage = "configuring the local renderer";
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (s, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (s, e) => e.Cancel = true;
            core.NavigationStarting += (s, e) => e.Cancel = e.Uri != Page;
            core.FrameNavigationStarting += (s, e) => e.Cancel = true;
            core.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess) return;
                Fail(3, "WebView2 could not load the animation page (" + e.WebErrorStatus + ").");
            };
            core.ProcessFailed += (s, e) =>
            {
                Fail(3, "The WebView2 renderer closed unexpectedly (" + e.ProcessFailedKind + ").");
            };
            core.WebMessageReceived += OnMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, e) =>
            {
                string uri = e.Request.Uri;
                string name = uri.StartsWith(Origin, StringComparison.Ordinal) ? uri.Substring(Origin.Length) : "";
                byte[] bytes;
                if (e.Request.Method != "GET" || !files.TryGetValue(name, out bytes))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                    return;
                }
                string headers = "Content-Type: " + ContentTypes[name] +
                    "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK", headers);
            };
            stage = "opening the animation page";
            core.Navigate(Page);
        }
        catch (Exception error)
        {
            Fail(3, "An error occurred while " + stage + ". " + error.GetBaseException().Message);
        }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Page) return;
        // WebMessageAsJson also safely ignores non-string messages.
        if (args.WebMessageAsJson == "\"complete\"") Finish(0);
        if (args.WebMessageAsJson == "\"assetError\"")
            Fail(3, "The animation page could not decode an image or initialize its drawing surface.");
    }

    private void Fail(int result, string message)
    {
        if (finished || IsDisposed) return;
        Failure = message;
        Console.Error.WriteLine(message);
        Finish(result);
    }

    private void Finish(int result)
    {
        if (finished || IsDisposed) return;
        finished = true;
        Result = result;
        watchdog.Stop();
        // Let the WebView2 callback return before disposing its controller.
        BeginInvoke(new Action(Close));
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        finished = true;
        watchdog.Stop();
        base.OnFormClosing(args);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) watchdog.Dispose();
        base.Dispose(disposing);
    }
}
