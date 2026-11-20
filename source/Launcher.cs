using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("Codex Animated")]
[assembly: AssemblyProduct("Codex Animated")]
[assembly: AssemblyDescription("Windows launcher with an aurora splash and window artwork for Codex")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

internal static class Launcher
{
    // The registered Microsoft Store identity stays the same across Codex updates.
    private const string CodexAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("ole32.dll")]
    private static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object manager, IntPtr reserved);

    internal static int Run(string[] args)
    {
        bool preview = args.Length == 1 && args[0] == "--preview";
        bool artwork = args.Length == 1 && args[0] == "--artwork";
        if (args.Length != 0 && !preview && !artwork) return 64;
        string instance = artwork ? "Artwork" : preview ? "Preview" : "Launch";
        bool first;
        using (var mutex = new Mutex(true, @"Local\CodexAnimatedSplash." + instance, out first))
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
                result = Play(out failure);
            }
            catch (Exception error) { failure = error.GetBaseException().Message; Console.Error.WriteLine(error); }
            if (artwork && failure != null)
                MessageBox.Show("The Codex wallpaper helper could not start.\n\n" + failure,
                    "Codex Animated", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (preview || artwork) return result;
            if (failure != null)
                MessageBox.Show("The startup animation could not play.\n\n" + failure +
                    "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed and run the full Windows download again. Select OK to continue to Codex.",
                    "Codex Animated", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // Activate after the splash closes. This lets Windows bring the existing
            // session forward or create one normally, without changing its window.
            try
            {
                Type managerType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
                var manager = (IApplicationActivationManager)Activator.CreateInstance(managerType);
                try
                {
                    uint processId;
                    CoAllowSetForegroundWindow(manager, IntPtr.Zero);
                    Marshal.ThrowExceptionForHR(manager.ActivateApplication(CodexAppId, null, 0, out processId));
                }
                finally { Marshal.ReleaseComObject(manager); }
            }
            catch (Exception error)
            {
                MessageBox.Show("Windows could not open Codex.\n\n" + error.Message,
                    "Codex Animated", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    "Codex Animated", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    private static readonly string[] CommonFiles = {
        "index.html", "style.css", "animation.js", "assets/artwork.jpg"
    };
    private static readonly string[] PortraitFiles = {
        "assets/avatar.jpg", "assets/contours.js", "assets/trace-wallpaper.js"
    };
    private readonly string root;
    private readonly bool portrait;
    private LauncherTheme(string root, bool portrait) { this.root = root; this.portrait = portrait; }

    internal static LauncherTheme Current
    {
        get
        {
            string packaged = Path.Combine(PortableStartup.BundleDirectory, "web");
            string besideExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Codex Animated", "web");
            if (!String.Equals(besideExe, packaged, StringComparison.OrdinalIgnoreCase) &&
                CompletePortrait(besideExe)) return new LauncherTheme(besideExe, true);
            return Select(packaged, installed);
        }
    }

    internal static string DefaultWallpaperPath { get { return Current.WallpaperPath; } }
    internal string WallpaperPath { get { return Path.Combine(root, "assets", "artwork.jpg"); } }

    internal static LauncherTheme Select(string packaged, string installed)
    {
        // The original Windows installation has a portrait, stored outlines, and a
        // tracer for selected images. Its local resources can accompany this runtime.
        if (CompletePortrait(packaged))
            return new LauncherTheme(packaged, true);
        if (Complete(packaged, CommonFiles) && LocalInstallation(installed) && CompletePortrait(installed))
            return new LauncherTheme(installed, true);
        return new LauncherTheme(packaged, false);
    }

    private static bool CompletePortrait(string folder)
    {
        return Complete(folder, CommonFiles) && Complete(folder, PortraitFiles) && PortraitEntry(folder);
    }

    private static bool PortraitEntry(string folder)
    {
        try
        {
            string entry = File.ReadAllText(Path.Combine(folder, "index.html"));
            return entry.Contains("assets/contours.js") && entry.Contains("assets/avatar.jpg");
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool LocalInstallation(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(Path.Combine(folder, "assets"));
            if (!directory.Exists || new Uri(directory.FullName).IsUnc ||
                new DriveInfo(directory.Root.FullName).DriveType == DriveType.Network) return false;
            // An implicitly discovered installed theme must resolve inside the local folder.
            for (; directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Complete(string folder, string[] names)
    {
        if (!Directory.Exists(folder)) return false;
        try
        {
            foreach (string name in names)
            {
                var file = new FileInfo(Path.Combine(folder, name));
                if (!file.Exists || file.Length == 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal Dictionary<string, byte[]> ReadFiles(string settingsDirectory)
    {
        var settings = new WallpaperSettings(WallpaperPath, settingsDirectory);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in CommonFiles)
            files.Add(name, name == "assets/artwork.jpg" ? settings.LoadJpeg() : Read(name));
        if (portrait)
        {
            bool custom = settings.HasCustomWallpaper;
            files.Add("assets/avatar.jpg", custom ? files["assets/artwork.jpg"] : Read("assets/avatar.jpg"));
            files.Add("assets/contours.js", Read(custom ? "assets/trace-wallpaper.js" : "assets/contours.js"));
        }
        return files;
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
        Text = "Codex Animated";
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
            string settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Animated");
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
