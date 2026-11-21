using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed class ArtworkOverlay : ApplicationContext
{
    private const uint ForegroundEvent = 3;
    private const uint MoveSizeStart = 0x000A, MoveSizeEnd = 0x000B;
    private const uint LocationEvent = 0x800B;
    private const int IdleInterval = 350, DragInterval = 16, WaitingInterval = 1500;
    private const int ReloadDelay = 150, ReloadRetryDelay = 500;
    private static readonly TimeSpan ColdStartAge = TimeSpan.FromSeconds(20);
    private readonly WallpaperSettings settings;
    private readonly string settingsDirectory = AppTarget.SettingsDirectory;
    private readonly bool forceFallback = Environment.GetEnvironmentVariable("ANIMATED_FALLBACK") == "1";
    private AppProfile profile;
    private Bitmap raw, source; // source aliases raw unless the look produced a copy.
    private string wallpaperStamp;
    private PreviewArtworkChild child;
    private ArtworkWindow artwork;
    private bool childUnavailable, refreshing;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer();
    private readonly Control marshal = new Control();
    private FileSystemWatcher watcher;
    private bool reloadRetried;
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem toggle = new ToolStripMenuItem("Hide artwork");
    private readonly ToolStripMenuItem themes = new ToolStripMenuItem("Themes");
    private readonly ToolStripMenuItem splashItem = new ToolStripMenuItem("Play animation when " + AppTarget.Name + " starts");
    private readonly Native.EventCallback onEvent;
    private IntPtr foregroundHook, locationHook, moveSizeHook, target;
    private uint targetPid, targetThread, decidedPid;
    private bool enabled = true, shuttingDown, disposed;
    private bool changedTransitions;
    private int previousTransitions;

    internal static void Run()
    {
        using (var overlay = new ArtworkOverlay(new WallpaperSettings()))
            Application.Run(overlay);
    }

    private ArtworkOverlay(WallpaperSettings settings)
    {
        this.settings = settings;
        profile = AppProfile.Load(settingsDirectory);
        raw = settings.LoadBitmap();
        source = ArtworkLook.Render(raw, profile.Look);
        wallpaperStamp = WallpaperStamp();
        toggle.Click += delegate {
            enabled = !enabled;
            toggle.Text = enabled ? "Hide artwork" : "Show artwork";
            SetTargetTransitions(enabled);
            RefreshArtwork();
        };
        var studio = new ToolStripMenuItem("Theme Studio\u2026");
        studio.Font = new Font(menu.Font, FontStyle.Bold);
        studio.Click += delegate { OpenThemeStudio(); };
        menu.Items.Add(studio);
        themes.DropDownItems.Add(new ToolStripMenuItem("No themes yet. Add one in Theme Studio.") { Enabled = false });
        themes.DropDownOpening += delegate { FillThemes(); };
        menu.Items.Add(themes);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggle);
        splashItem.Checked = profile.SplashOnStart;
        splashItem.CheckOnClick = true;
        splashItem.Click += delegate { SaveSplashChoice(splashItem.Checked); };
        menu.Items.Add(splashItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { EndSession(); });
        DarkMenuRenderer.Style(menu);
        DarkMenuRenderer.Style(themes.DropDown);
        tray.Text = AppTarget.Product + " artwork";
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (ArgumentException) { tray.Icon = SystemIcons.Application; }
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { OpenThemeStudio(); };
        tray.Visible = true;

        // OUTOFCONTEXT (0) delivers these notifications on our own message-loop thread.
        // Only a foreground notification is global. Location events are later limited to
        // the official application's UI thread and the exact window handle.
        onEvent = WindowChanged;
        foregroundHook = Native.SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, onEvent, 0, 0, 0);
        timer.Interval = IdleInterval;
        timer.Tick += delegate { RefreshArtwork(); };
        timer.Start();
        StartWatching();
    }

    private void OpenThemeStudio()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ThemeStudio.exe");
        if (!File.Exists(path))
        {
            MessageBox.Show("Theme Studio was not found.\n\nExpected: " + path,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })) { } }
        catch (Win32Exception error)
        {
            MessageBox.Show("Theme Studio could not start.\n\n" + error.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void FillThemes()
    {
        themes.DropDownItems.Clear();
        string[] names = ThemeLibrary.Names();
        if (names.Length == 0)
        {
            themes.DropDownItems.Add(new ToolStripMenuItem("No themes yet. Add one in Theme Studio.") { Enabled = false });
            return;
        }
        foreach (string name in names)
        {
            string chosen = name;
            var item = new ToolStripMenuItem(name)
            {
                Checked = string.Equals(name, profile.Theme, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += delegate { TryApplyTheme(chosen); };
            themes.DropDownItems.Add(item);
        }
    }

    private void TryApplyTheme(string name)
    {
        try { ThemeLibrary.Apply(name, settingsDirectory); }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is ExternalException || error is NotSupportedException ||
                error is System.Security.SecurityException)) throw;
            MessageBox.Show("Could not apply the theme.\n\n" + error.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveSplashChoice(bool value)
    {
        try
        {
            AppProfile fresh = AppProfile.LoadStrict(settingsDirectory);
            fresh.SplashOnStart = value;
            fresh.Save();
            profile.SplashOnStart = value;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            splashItem.Checked = profile.SplashOnStart;
            MessageBox.Show("Could not save the setting.\n\n" + error.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string WallpaperStamp()
    {
        var info = new FileInfo(Path.Combine(settingsDirectory, "wallpaper.jpg"));
        return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "none";
    }

    private void StartWatching()
    {
        IntPtr unused = marshal.Handle; // Creating the handle lets the watcher post events to this thread.
        reloadTimer.Interval = ReloadDelay;
        reloadTimer.Tick += delegate { Reload(); };
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            watcher = new FileSystemWatcher(settingsDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                SynchronizingObject = marshal
            };
            FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) { SettingsChanged(e.Name); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += delegate(object sender, RenamedEventArgs e) { SettingsChanged(e.Name); SettingsChanged(e.OldName); };
            watcher.Error += delegate { ScheduleReload(ReloadDelay); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is ArgumentException || error is UnauthorizedAccessException ||
                error is PlatformNotSupportedException)) throw;
            Debug.WriteLine("Settings watcher unavailable: " + error.Message);
        }
    }

    private void SettingsChanged(string name)
    {
        if (name == null) return;
        if (string.Equals(name, "settings.ini", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "wallpaper.jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "avatar.jpg", StringComparison.OrdinalIgnoreCase))
            ScheduleReload(ReloadDelay);
    }

    private void ScheduleReload(int delay)
    {
        if (shuttingDown) return;
        reloadTimer.Stop();
        reloadTimer.Interval = delay;
        reloadTimer.Start();
    }

    private void Reload()
    {
        reloadTimer.Stop();
        if (shuttingDown) return;
        try
        {
            ApplyProfile();
            reloadRetried = false;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ExternalException || error is OutOfMemoryException ||
                error is NotSupportedException)) throw;
            // Keep what is on screen; the file may only be busy for a moment.
            if (!reloadRetried)
            {
                reloadRetried = true;
                ScheduleReload(ReloadRetryDelay);
            }
            else reloadRetried = false;
        }
    }

    private void ApplyProfile()
    {
        AppProfile next = AppProfile.LoadStrict(settingsDirectory);
        string stamp = WallpaperStamp();
        bool rawChanged = stamp != wallpaperStamp;
        bool relook = rawChanged || !next.Look.SameRender(profile.Look);
        Bitmap nextRaw = rawChanged ? settings.LoadBitmap() : null;
        Bitmap nextSource = null;
        if (relook)
        {
            try { nextSource = ArtworkLook.Render(nextRaw ?? raw, next.Look); }
            catch { if (nextRaw != null) nextRaw.Dispose(); throw; }
        }
        Bitmap oldRaw = raw, oldSource = source;
        if (rawChanged) { raw = nextRaw; wallpaperStamp = stamp; }
        if (relook)
        {
            source = nextSource;
            if (child != null) child.SetImage(source);
            if (artwork != null) artwork.SetImage(source);
        }
        if (next.Look.Alpha != profile.Look.Alpha)
        {
            if (child != null) child.SetAlpha(next.Look.Alpha);
            if (artwork != null) artwork.SetAlpha(next.Look.Alpha);
        }
        if (next.Look.FocusX != profile.Look.FocusX || next.Look.FocusY != profile.Look.FocusY)
        {
            if (child != null) child.SetFocus(next.Look.FocusX, next.Look.FocusY);
            if (artwork != null) artwork.SetFocus(next.Look.FocusX, next.Look.FocusY);
        }
        profile = next;
        splashItem.Checked = profile.SplashOnStart;
        if (relook && oldSource != oldRaw) oldSource.Dispose();
        if (rawChanged) oldRaw.Dispose();
        RefreshArtwork();
    }

    private void WindowChanged(IntPtr hook, uint kind, IntPtr window, int objectId, int childId,
        uint threadId, uint time)
    {
        if (shuttingDown) return;
        if (kind != ForegroundEvent)
        {
            if (target == IntPtr.Zero || window != target) return;
            if (kind == MoveSizeStart) timer.Interval = DragInterval;
            else if (kind == MoveSizeEnd) timer.Interval = IdleInterval;
            else if (childId != 0 || (objectId != 0 && objectId != -4)) return;
        }
        RefreshArtwork();
    }

    private bool SelectWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window) ||
            Native.GetWindow(window, Native.Owner) != IntPtr.Zero ||
            (Native.Styles(window, Native.ExStyle) & Native.ToolWindow) != 0)
            return false;
        var cls = new StringBuilder(128);
        Native.GetClassName(window, cls, cls.Capacity);
        if (cls.ToString() != "Chrome_WidgetWin_1") return false;
        if (IsCloaked(window)) return false;
        // Electron can remove the frame styles when the main window is already fullscreen.
        if ((Native.Styles(window, Native.Style) & (0x40000 | 0x20000 | 0x10000)) == 0)
        {
            Native.Rect bounds;
            if (!Native.GetWindowRect(window, out bounds) ||
                !Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
                    .Contains(Screen.FromHandle(window).Bounds)) return false;
        }
        uint pid;
        uint thread = Native.GetWindowThreadProcessId(window, out pid);
        if (thread == 0 || !OfficialProcess(pid)) return false;
        ReleaseTarget();
        target = window;
        targetPid = pid;
        targetThread = thread;
        timer.Interval = IdleInterval;
        SetTargetTransitions(enabled);
        locationHook = Native.SetWinEventHook(LocationEvent, LocationEvent, IntPtr.Zero, onEvent, pid, thread, 0);
        moveSizeHook = Native.SetWinEventHook(MoveSizeStart, MoveSizeEnd, IntPtr.Zero, onEvent, pid, thread, 0);
        return true;
    }

    private void SetTargetTransitions(bool disable)
    {
        if (target == IntPtr.Zero || disable == changedTransitions) return;
        uint pid;
        if (Native.GetWindowThreadProcessId(target, out pid) != targetThread || pid != targetPid) return;
        if (disable)
        {
            // Some Windows versions expose this per-window policy only to the setter.
            if (Native.DwmGetWindowAttribute(target, Native.DisableTransitions, out previousTransitions, sizeof(int)) != 0)
                previousTransitions = 0; // Release to Windows' normal animation policy on exit.
            int on = 1;
            changedTransitions = Native.DwmSetWindowAttribute(target, Native.DisableTransitions, ref on, sizeof(int)) == 0;
        }
        else
        {
            if (Native.DwmSetWindowAttribute(target, Native.DisableTransitions, ref previousTransitions, sizeof(int)) == 0)
                changedTransitions = false;
        }
    }

    private static bool OfficialProcess(uint pid)
    {
        return AppTarget.OwnsProcess(pid);
    }

    private void RefreshArtwork()
    {
        // Cross-thread window creation or destruction can dispatch a WinEvent before
        // the original callback returns. Publish one renderer at a time.
        if (shuttingDown || refreshing) return;
        refreshing = true;
        try { UpdateArtwork(); }
        finally { refreshing = false; }
    }

    private void UpdateArtwork()
    {
        PresentArtwork();
        if (target != IntPtr.Zero) DecideColdStart();
    }

    // One decision per process id, so a window the app replaces during startup or a
    // second window of the same process never replays the animation.
    private void DecideColdStart()
    {
        if (targetPid == decidedPid) return;
        DateTime started;
        if (!AppTarget.TryGetStartTime(targetPid, out started) || DateTime.UtcNow - started > ColdStartAge)
        {
            decidedPid = targetPid;
            return;
        }
        Mutex playing;
        if (Mutex.TryOpenExisting(@"Local\" + AppTarget.HelperName + "Splash.Play", out playing))
        {
            // The launcher or an earlier --splash is already playing for this start.
            playing.Dispose();
            decidedPid = targetPid;
            return;
        }
        if (Native.GetForegroundWindow() != target) return;
        decidedPid = targetPid;
        if (!profile.SplashOnStart) return;
        try
        {
            using (Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                "--splash " + target.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture))
            { UseShellExecute = false })) { }
        }
        catch (Win32Exception) { }
    }

    private void PresentArtwork()
    {
        if (target != IntPtr.Zero && !TargetStillExists()) ReleaseTarget();
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground != target) SelectWindow(foreground);

        // Electron can hide a startup window or replace it while another app has focus.
        // Keep a minimized window's preview until an actual replacement is visible.
        if (target == IntPtr.Zero || !Native.IsWindowVisible(target) || Native.IsIconic(target) || IsCloaked(target))
        {
            Native.EnumWindows(delegate(IntPtr window, IntPtr param) {
                return window == target || Native.IsIconic(window) || !SelectWindow(window);
            }, IntPtr.Zero);
            if (target == IntPtr.Zero)
            {
                timer.Interval = WaitingInterval;
                return;
            }
        }
        if (!forceFallback && !childUnavailable && (enabled || child != null) && TryChildArtwork()) return;
        if (!enabled)
        {
            if (artwork != null) artwork.Conceal();
            return;
        }
        // Only one renderer is visible. The independent owned window is used when
        // Windows cannot attach a layered child to this application window.
        if (artwork == null)
        {
            artwork = new ArtworkWindow(source);
            artwork.SetAlpha(profile.Look.Alpha);
            artwork.SetFocus(profile.Look.FocusX, profile.Look.FocusY);
            artwork.AttachTo(target);
        }
        if (!Native.IsWindowVisible(target) || Native.IsIconic(target))
        {
            artwork.Conceal();
            return;
        }
        if (IsCloaked(target))
        {
            artwork.Conceal();
            return;
        }
        Native.Rect rect;
        var point = new Native.Point();
        if (!Native.GetClientRect(target, out rect) || !Native.ClientToScreen(target, ref point) ||
            rect.Right <= 0 || rect.Bottom <= 0)
            return; // A transitioning HWND can briefly report no usable dimensions.
        artwork.Place(target, new Rectangle(point.X, point.Y, rect.Right, rect.Bottom));
    }

    private static bool IsCloaked(IntPtr window)
    {
        int value;
        return Native.DwmGetWindowAttribute(window, 14, out value, sizeof(int)) == 0 && value != 0;
    }

    private bool TargetStillExists()
    {
        uint pid;
        return Native.GetWindowThreadProcessId(target, out pid) == targetThread && pid == targetPid;
    }

    private bool TryChildArtwork()
    {
        try
        {
            if (child == null)
            {
                child = new PreviewArtworkChild(target, targetPid, source, profile.Look.Alpha,
                    profile.Look.FocusX, profile.Look.FocusY);
                return true;
            }
            child.SetEnabled(enabled);
            if (child.RefreshSurface()) return true;
        }
        catch (Exception error)
        {
            if (!(error is ExternalException || error is ArgumentException ||
                error is InvalidOperationException || error is NotSupportedException ||
                error is EntryPointNotFoundException)) throw;
            Debug.WriteLine("Artwork child unavailable: " + error.Message);
        }
        if (child != null) child.Dispose();
        child = null;
        childUnavailable = true;
        if (!TargetStillExists())
        {
            ReleaseTarget();
            return true;
        }
        return false;
    }

    private void ReleaseTarget()
    {
        SetTargetTransitions(false);
        if (moveSizeHook != IntPtr.Zero) Native.UnhookWinEvent(moveSizeHook);
        if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
        moveSizeHook = locationHook = IntPtr.Zero;
        if (child != null) { child.Dispose(); child = null; }
        if (artwork != null) { artwork.Dispose(); artwork = null; }
        target = IntPtr.Zero;
        targetPid = targetThread = 0;
        changedTransitions = childUnavailable = false;
        timer.Interval = WaitingInterval;
    }

    private void EndSession()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        if (child != null) child.SetEnabled(false);
        if (artwork != null) artwork.Conceal();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            shuttingDown = true;
            ReleaseTarget();
            timer.Dispose();
            reloadTimer.Dispose();
            if (watcher != null) watcher.Dispose();
            marshal.Dispose();
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            if (source != raw) source.Dispose();
            raw.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ArtworkWindow : Form
    {
        private Bitmap image;
        private Surface source, current, previousSize;
        private Rectangle placed;
        private bool shown, forceComposite;
        private byte alpha = 38;
        private int focusX = 50, focusY = 50;
        private IntPtr targetOwner;

        internal ArtworkWindow(Bitmap source)
        {
            image = source;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            // UpdateLayeredWindow supplies the bitmap and opacity in one operation.
            // Form.Opacity uses a different Windows layering API and would disable it.
        }

        internal void SetImage(Bitmap replacement)
        {
            image = replacement;
            // Windows retains the last submitted pixels until the next layered update.
            ReleaseSurfaces();
        }

        internal void SetFocus(int x, int y)
        {
            if (focusX == x && focusY == y) return;
            focusX = x;
            focusY = y;
            // Keep showing the last submitted pixels until the next Place recomposes.
            if (previousSize != null) previousSize.Dispose();
            if (current != null) current.Dispose();
            previousSize = current = null;
        }

        // Re-composites the same pixels with a new global opacity on the next Place.
        internal void SetAlpha(byte value)
        {
            alpha = value;
            forceComposite = true;
        }

        internal void AttachTo(IntPtr window)
        {
            // Let Windows raise the pair together, before it presents the first frame
            // of the reactivated window. Clicks and keyboard focus still go to the app.
            targetOwner = window;
            if (IsHandleCreated && Native.GetWindow(Handle, Native.Owner) != window)
                Native.SetOwner(Handle, -8, window); // GWLP_HWNDPARENT on our own top-level window.
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // NOACTIVATE, TRANSPARENT, LAYERED, TOOLWINDOW.
                p.ExStyle |= 0x08000000 | 0x20 | 0x80000 | Native.ToolWindow;
                p.ExStyle &= ~0x40000; // APPWINDOW
                if (targetOwner != IntPtr.Zero) p.Parent = targetOwner;
                return p;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            shown = false;
            placed = Rectangle.Empty;
            // Keep the helper's own show/hide immediate as well.
            int disabled = 1;
            Native.DwmSetWindowAttribute(Handle, Native.DisableTransitions, ref disabled, sizeof(int));
        }

        internal void Place(IntPtr targetWindow, Rectangle rect)
        {
            if (IsDisposed) return;
            IntPtr handle = Handle;
            // Framework handle creation can assign a hidden taskbar owner of its own.
            if (targetOwner == targetWindow && Native.GetWindow(handle, Native.Owner) != targetWindow)
                Native.SetOwner(handle, -8, targetWindow);
            bool topmost = Native.IsTopmost(targetWindow);
            const uint keepBounds = Native.NoActivate | Native.NoOwnerZOrder | Native.NoMove | Native.NoSize;
            if (!topmost && Native.IsTopmost(handle))
                Native.SetWindowPos(handle, Native.NotTopmost, 0, 0, 0, 0, keepBounds);

            if (forceComposite || current == null || rect.Size != placed.Size)
            {
                // Keep the previous image on screen while preparing the new crop.
                // Commit both the pixels and the bounds without an intervening WM_PAINT.
                Surface next = (forceComposite && current != null && current.Size == rect.Size)
                    ? current : previousSize;
                if (next == null || next.Size != rect.Size)
                {
                    try
                    {
                        if (source == null) source = new Surface(image.GetHbitmap(), image.Size);
                        next = source.ScaleToCover(rect.Size, focusX, focusY);
                    }
                    catch (Win32Exception) { return; } // Preserve the last committed frame and retry.
                }
                var destination = new Native.Point { X = rect.X, Y = rect.Y };
                var size = new Native.Size { Width = rect.Width, Height = rect.Height };
                var from = new Native.Point();
                var blend = new Native.Blend { ConstantAlpha = alpha };
                if (!Native.UpdateLayeredWindow(handle, IntPtr.Zero, ref destination, ref size,
                    next.DC, ref from, 0, ref blend, 2))
                {
                    if (next != previousSize && next != current) next.Dispose();
                    return;
                }
                if (next != current)
                {
                    if (previousSize != null && previousSize != next) previousSize.Dispose();
                    previousSize = current;
                    current = next;
                }
                placed = rect;
                forceComposite = false;
            }
            IntPtr previous = Native.GetWindow(targetWindow, Native.Previous);
            bool inPlace = previous == handle && Native.IsTopmost(handle) == topmost;
            // Windows may have hidden or restored an owned popup with its owner.
            bool visible = shown && Native.IsWindowVisible(handle);
            if (visible && placed == rect && inPlace) return;
            uint flags = Native.NoActivate | Native.NoOwnerZOrder | Native.NoSize;
            if (!visible) flags |= Native.Show;
            if (rect.Location == placed.Location) flags |= Native.NoMove;
            if (inPlace) flags |= Native.NoZOrder;
            else if (topmost)
            {
                // Use the preceding topmost window as the anchor if another app covers the target.
                if (previous == IntPtr.Zero || previous == handle || !Native.IsTopmost(previous))
                    previous = Native.TopmostPosition;
            }
            else if (previous != IntPtr.Zero && Native.IsTopmost(previous))
                previous = IntPtr.Zero; // Top of the ordinary band.
            if (!Native.SetWindowPos(handle, previous, rect.X, rect.Y, 0, 0, flags)) return;
            shown = true;
            placed = rect;
        }

        internal void Conceal()
        {
            if (IsHandleCreated && Native.IsWindowVisible(Handle))
            {
                Native.ShowWindow(Handle, 0);
                if (Native.IsTopmost(Handle) && Native.GetWindow(Handle, Native.Owner) != targetOwner)
                    Native.SetWindowPos(Handle, Native.NotTopmost, 0, 0, 0, 0,
                        Native.NoMove | Native.NoSize | Native.NoActivate | Native.NoOwnerZOrder);
            }
            shown = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }

        private void ReleaseSurfaces()
        {
            if (previousSize != null) previousSize.Dispose();
            if (current != null) current.Dispose();
            if (source != null) source.Dispose();
            source = current = previousSize = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseSurfaces();
            base.Dispose(disposing);
        }
    }

    // Opaque JPEG pixels in a reusable GDI bitmap. Global opacity is applied at composition.
    private sealed class Surface : IDisposable
    {
        internal readonly Size Size;
        internal IntPtr DC { get; private set; }
        private IntPtr bitmap, original;

        internal Surface(IntPtr bitmap, Size size)
        {
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            this.bitmap = bitmap;
            Size = size;
            DC = Native.CreateCompatibleDC(IntPtr.Zero);
            if (DC != IntPtr.Zero) original = Native.SelectObject(DC, bitmap);
            if (original == IntPtr.Zero || original == new IntPtr(-1))
            {
                var error = new Win32Exception();
                Dispose();
                throw error;
            }
        }

        internal Surface ScaleToCover(Size viewport, int focusX, int focusY)
        {
            var frame = new Surface(Native.CreateCompatibleBitmap(DC, viewport.Width, viewport.Height), viewport);
            RectangleF crop = ArtworkLook.CoverSource(Size, viewport, focusX, focusY);
            int width = Math.Max(1, Math.Min(Size.Width, (int)Math.Round(crop.Width)));
            int height = Math.Max(1, Math.Min(Size.Height, (int)Math.Round(crop.Height)));
            int left = Math.Max(0, Math.Min(Size.Width - width, (int)Math.Round(crop.X)));
            int top = Math.Max(0, Math.Min(Size.Height - height, (int)Math.Round(crop.Y)));
            Native.SetStretchBltMode(frame.DC, 4); // HALFTONE
            Native.SetBrushOrgEx(frame.DC, 0, 0, IntPtr.Zero);
            if (!Native.StretchBlt(frame.DC, 0, 0, viewport.Width, viewport.Height, DC,
                left, top, width, height, 0x00CC0020))
            {
                var error = new Win32Exception();
                frame.Dispose();
                throw error;
            }
            return frame;
        }

        public void Dispose()
        {
            if (original != IntPtr.Zero && original != new IntPtr(-1)) Native.SelectObject(DC, original);
            if (DC != IntPtr.Zero) Native.DeleteDC(DC);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            DC = bitmap = original = IntPtr.Zero;
        }
    }

    private static class Native
    {
        internal const int DisableTransitions = 3; // DWMWA_TRANSITIONS_FORCEDISABLED
        internal const int Style = -16, ExStyle = -20, ToolWindow = 0x80, Topmost = 8;
        internal const uint Previous = 3, Owner = 4;
        internal const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10, Show = 0x40, NoOwnerZOrder = 0x200;
        internal static readonly IntPtr TopmostPosition = new IntPtr(-1), NotTopmost = new IntPtr(-2);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack=1)] internal struct Blend { internal byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        internal delegate bool EnumCallback(IntPtr window, IntPtr data);
        internal delegate void EventCallback(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        internal static long Styles(IntPtr window, int which) { return GetWindowLongPtr(window, which).ToInt64(); }
        internal static bool IsTopmost(IntPtr window) { return (Styles(window, ExStyle) & Topmost) != 0; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError=true)] internal static extern IntPtr SetOwner(IntPtr window, int index, IntPtr owner);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder output, int length);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr previous, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr library, EventCallback callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr display, ref Point destination, ref Size size, IntPtr source, ref Point from, uint key, ref Blend blend, uint flags);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] internal static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    }
}
