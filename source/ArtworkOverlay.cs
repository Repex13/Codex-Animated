using System;
using System.Diagnostics;
using System.Drawing;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class ArtworkOverlay : ApplicationContext
{
    private const string Family = "OpenAI.Codex_2p2nqsd0c76g0";
    private const uint ForegroundEvent = 3;
    private const uint MoveSizeStart = 0x000A, MoveSizeEnd = 0x000B;
    private const uint LocationEvent = 0x800B;
    private const int IdleInterval = 350, DragInterval = 16;
    private readonly Bitmap source;
    private PreviewArtworkChild child;
    private ArtworkWindow artwork;
    private bool childUnavailable, refreshing;
    private readonly Timer timer = new Timer();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly Icon trayIcon;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem toggle = new ToolStripMenuItem("Hide artwork");
    private readonly Native.EventCallback onEvent;
    private IntPtr foregroundHook, locationHook, moveSizeHook, target;
    private uint targetPid, targetThread;
    private bool enabled = true, shuttingDown, disposed;
    private bool changedTransitions;
    private int previousTransitions;

    internal static void Run()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "assets", "artwork.jpg");
        using (var source = new Bitmap(path))
        using (var overlay = new ArtworkOverlay(source))
            Application.Run(overlay);
    }

    private ArtworkOverlay(Bitmap source)
    {
        this.source = source;
        toggle.Click += delegate {
            enabled = !enabled;
            toggle.Text = enabled ? "Hide artwork" : "Show artwork";
            SetCodexTransitions(enabled);
            RefreshArtwork();
        };
        menu.Items.Add(toggle);
        menu.Items.Add("Exit artwork", null, delegate { EndSession(); });
        tray.Text = "Codex Animated artwork";
        trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        tray.Icon = trayIcon ?? SystemIcons.Application;
        tray.ContextMenuStrip = menu;
        tray.Visible = true;

        // OUTOFCONTEXT (0) delivers these notifications on our own message-loop thread.
        // Only a foreground notification is global. Location events are later limited to
        // the official application's UI thread and the exact window handle.
        onEvent = WindowChanged;
        foregroundHook = Native.SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, onEvent, 0, 0, 0);
        timer.Interval = IdleInterval;
        timer.Tick += delegate { RefreshArtwork(); };
        timer.Start();
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
        target = window;
        targetPid = pid;
        targetThread = thread;
        SetCodexTransitions(enabled);
        locationHook = Native.SetWinEventHook(LocationEvent, LocationEvent, IntPtr.Zero, onEvent, pid, thread, 0);
        moveSizeHook = Native.SetWinEventHook(MoveSizeStart, MoveSizeEnd, IntPtr.Zero, onEvent, pid, thread, 0);
        return true;
    }

    private void SetCodexTransitions(bool disable)
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
        IntPtr process = Native.OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            uint length = 256;
            var family = new StringBuilder((int)length);
            if (Native.GetPackageFamilyName(process, ref length, family) != 0 ||
                !Family.Equals(family.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
            length = 32768;
            var image = new StringBuilder((int)length);
            return Native.QueryFullProcessImageName(process, 0, image, ref length) &&
                string.Equals(Path.GetFileName(image.ToString()), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseHandle(process); }
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
        if (target == IntPtr.Zero)
        {
            if (!SelectWindow(Native.GetForegroundWindow()))
                Native.EnumWindows(delegate(IntPtr window, IntPtr param) { return !SelectWindow(window); }, IntPtr.Zero);
            if (target == IntPtr.Zero)
            {
                if (elapsed.ElapsedMilliseconds > 45000) EndSession();
                return;
            }
        }
        uint pid;
        uint thread = Native.GetWindowThreadProcessId(target, out pid);
        if (pid != targetPid || thread != targetThread)
        {
            EndSession();
            return;
        }
        if (!enabled)
        {
            if (child != null) child.SetEnabled(false);
            if (artwork != null) artwork.Conceal();
            return;
        }
        if (!childUnavailable && TryChildArtwork()) return;
        // Only one renderer is visible. The independent owned window is used when
        // Windows cannot attach a layered child to this application window.
        if (artwork == null)
        {
            artwork = new ArtworkWindow(source);
            artwork.AttachTo(target);
        }
        if (!Native.IsWindowVisible(target) || Native.IsIconic(target))
        {
            artwork.Conceal();
            return;
        }
        int cloaked;
        if (Native.DwmGetWindowAttribute(target, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)
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

    private bool TryChildArtwork()
    {
        try
        {
            if (child == null)
            {
                child = new PreviewArtworkChild(target, targetPid, source);
                return true;
            }
            child.SetEnabled(true);
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
        uint pid;
        if (Native.GetWindowThreadProcessId(target, out pid) != targetThread || pid != targetPid)
        {
            EndSession();
            return true;
        }
        return false;
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
            SetCodexTransitions(false);
            timer.Dispose();
            if (moveSizeHook != IntPtr.Zero) Native.UnhookWinEvent(moveSizeHook);
            if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            tray.Visible = false;
            tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            menu.Dispose();
            if (child != null) child.Dispose();
            if (artwork != null) artwork.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ArtworkWindow : Form
    {
        private readonly Surface source;
        private Surface current, previousSize;
        private Rectangle placed;
        private bool shown;
        private IntPtr codexOwner;

        internal ArtworkWindow(Bitmap source)
        {
            this.source = new Surface(source.GetHbitmap(), source.Size);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            // UpdateLayeredWindow supplies the bitmap and opacity in one operation.
            // Form.Opacity uses a different Windows layering API and would disable it.
        }

        internal void AttachTo(IntPtr window)
        {
            // Let Windows raise the pair together, before it presents the first frame
            // of the reactivated window. Clicks and keyboard focus still go to Codex.
            codexOwner = window;
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
                if (codexOwner != IntPtr.Zero) p.Parent = codexOwner;
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
            if (codexOwner == targetWindow && Native.GetWindow(handle, Native.Owner) != targetWindow)
                Native.SetOwner(handle, -8, targetWindow);
            bool topmost = Native.IsTopmost(targetWindow);
            const uint keepBounds = Native.NoActivate | Native.NoOwnerZOrder | Native.NoMove | Native.NoSize;
            if (!topmost && Native.IsTopmost(handle))
                Native.SetWindowPos(handle, Native.NotTopmost, 0, 0, 0, 0, keepBounds);

            if (current == null || rect.Size != placed.Size)
            {
                // Keep the previous image on screen while preparing the new crop.
                // Commit both the pixels and the bounds without an intervening WM_PAINT.
                Surface next = previousSize;
                if (next == null || next.Size != rect.Size)
                {
                    try { next = source.ScaleToCover(rect.Size); }
                    catch (Win32Exception) { return; } // Preserve the last committed frame and retry.
                }
                var destination = new Native.Point { X = rect.X, Y = rect.Y };
                var size = new Native.Size { Width = rect.Width, Height = rect.Height };
                var from = new Native.Point();
                var blend = new Native.Blend { ConstantAlpha = 38 };
                if (!Native.UpdateLayeredWindow(handle, IntPtr.Zero, ref destination, ref size,
                    next.DC, ref from, 0, ref blend, 2))
                {
                    if (next != previousSize) next.Dispose();
                    return;
                }
                if (previousSize != null && previousSize != next) previousSize.Dispose();
                previousSize = current;
                current = next;
                placed = rect;
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
                // Use the preceding topmost window as the anchor if another app covers Codex.
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
                if (Native.IsTopmost(Handle) && Native.GetWindow(Handle, Native.Owner) != codexOwner)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (previousSize != null) previousSize.Dispose();
                if (current != null) current.Dispose();
                source.Dispose();
            }
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

        internal Surface ScaleToCover(Size viewport)
        {
            var frame = new Surface(Native.CreateCompatibleBitmap(DC, viewport.Width, viewport.Height), viewport);
            double zoom = Math.Max((double)viewport.Width / Size.Width, (double)viewport.Height / Size.Height);
            int width = Math.Max(1, (int)Math.Round(viewport.Width / zoom));
            int height = Math.Max(1, (int)Math.Round(viewport.Height / zoom));
            Native.SetStretchBltMode(frame.DC, 4); // HALFTONE
            Native.SetBrushOrgEx(frame.DC, 0, 0, IntPtr.Zero);
            if (!Native.StretchBlt(frame.DC, 0, 0, viewport.Width, viewport.Height, DC,
                (Size.Width - width) / 2, (Size.Height - height) / 2, width, height, 0x00CC0020))
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
        [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder family);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    }
}
