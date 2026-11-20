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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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

    [STAThread]
    private static int Main(string[] args)
    {
        bool preview = args.Length == 1 && args[0] == "--preview";
        bool artwork = args.Length == 1 && args[0] == "--artwork";
        if (args.Length != 0 && !preview && !artwork) return 64;
        string instance = artwork ? "Artwork" : preview ? "Preview" : "Launch";
        bool first;
        using (var mutex = new Mutex(true, @"Local\CodexAnimatedSplash." + instance, out first))
        {
            if (!first) return preview ? 5 : 0;
            int result = 3;
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
                result = Play();
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            if (preview || artwork) return result;

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
            catch (Exception error) { Console.Error.WriteLine(error.Message); }
            return 0;
        }
    }

    // Keep animation dependencies out of the normal activation fallback's JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Play()
    {
        using (var splash = new Splash())
        {
            Application.Run(splash);
            return splash.Result;
        }
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
        {"assets/artwork.jpg", "image/jpeg"}
    };

    private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
    private readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
    private readonly WebView2 browser;
    private bool finished;
    internal int Result { get; private set; }

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
        watchdog.Tick += delegate { Finish(2); };
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
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
            foreach (string name in ContentTypes.Keys)
                files.Add(Origin + name, name == "assets/artwork.jpg"
                    ? new WallpaperSettings().LoadJpeg()
                    : File.ReadAllBytes(Path.Combine(root, name)));
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Codex Animated", "WebView2");
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
                Console.Error.WriteLine("Splash navigation failed: " + e.WebErrorStatus);
                Finish(3);
            };
            core.ProcessFailed += (s, e) =>
            {
                Console.Error.WriteLine("WebView2 process failed: " + e.ProcessFailedKind);
                Finish(3);
            };
            core.WebMessageReceived += OnMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, e) =>
            {
                string uri = e.Request.Uri;
                byte[] bytes;
                if (e.Request.Method != "GET" || !files.TryGetValue(uri, out bytes))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                    return;
                }
                string headers = "Content-Type: " + ContentTypes[uri.Substring(Origin.Length)] +
                    "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK", headers);
            };
            stage = "opening the animation page";
            core.Navigate(Page);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Failed while " + stage + ": " + error);
            Finish(3);
        }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Page) return;
        // WebMessageAsJson also safely ignores non-string messages.
        if (args.WebMessageAsJson == "\"complete\"") Finish(0);
        if (args.WebMessageAsJson == "\"assetError\"")
        {
            Console.Error.WriteLine("The animation page reported an asset decode or canvas failure.");
            Finish(3);
        }
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
