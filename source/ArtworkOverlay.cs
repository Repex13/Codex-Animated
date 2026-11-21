using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class ArtworkOverlay : ApplicationContext
{
    private const uint ForegroundEvent = 3;
    private const uint MoveSizeStart = 0x000A, MoveSizeEnd = 0x000B;
    private const uint LocationEvent = 0x800B;
    private const int IdleInterval = 350, DragInterval = 16, WaitingInterval = 1500;
    private readonly WallpaperSettings settings;
    private Bitmap raw, source; // source aliases raw unless a style pass produced a copy.
    private byte opacity;
    private PreviewArtworkChild child;
    private ArtworkWindow artwork;
    private bool childUnavailable, refreshing;
    private readonly Timer timer = new Timer();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem toggle = new ToolStripMenuItem("Hide artwork");
    private readonly ToolStripMenuItem restore = new ToolStripMenuItem("Restore default wallpaper");
    private readonly ToolStripMenuItem restoreAvatar = new ToolStripMenuItem("Restore default avatar");
    private readonly Native.EventCallback onEvent;
    private IntPtr foregroundHook, locationHook, moveSizeHook, target;
    private uint targetPid, targetThread;
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
        raw = settings.LoadBitmap();
        source = ApplyStyle(raw);
        opacity = settings.Opacity;
        toggle.Click += delegate {
            enabled = !enabled;
            toggle.Text = enabled ? "Hide artwork" : "Show artwork";
            SetDevinTransitions(enabled);
            RefreshArtwork();
        };
        menu.Items.Add("Choose wallpaper\u2026", null, delegate { ChooseWallpaper(); });
        restore.Enabled = settings.HasCustomWallpaper;
        restore.Click += delegate { TryChangeWallpaper(null); };
        menu.Items.Add(restore);
        menu.Items.Add("Choose avatar\u2026", null, delegate { ChooseAvatar(); });
        restoreAvatar.Enabled = settings.HasCustomAvatar;
        restoreAvatar.Click += delegate { TryResetAvatar(); };
        menu.Items.Add(restoreAvatar);
        menu.Items.Add(new ToolStripSeparator());
        var styleMenu = new ToolStripMenuItem("Artwork style");
        AddStyleItem(styleMenu, "Natural", "natural");
        AddStyleItem(styleMenu, "Soften for readability", "soften");
        AddStyleItem(styleMenu, "Dim for readability", "dim");
        AddStyleItem(styleMenu, "Nen aura tint", "aura");
        menu.Items.Add(styleMenu);
        var opacityMenu = new ToolStripMenuItem("Artwork opacity");
        foreach (int percent in new[] { 10, 15, 20, 25 })
        {
            byte alpha = (byte)Math.Round(255 * percent / 100.0);
            var item = new ToolStripMenuItem(percent + "%") { Checked = opacity == alpha };
            item.Click += delegate {
                opacity = alpha;
                settings.SaveOpacity(alpha);
                foreach (ToolStripItem sibling in opacityMenu.DropDownItems)
                    ((ToolStripMenuItem)sibling).Checked = sibling == item;
                if (child != null) child.SetAlpha(alpha);
                if (artwork != null) artwork.SetAlpha(alpha);
                RefreshArtwork();
            };
            opacityMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(opacityMenu);
        var skip = new ToolStripMenuItem("Skip splash when " + AppTarget.Name + " is open")
        {
            Checked = settings.SkipWhenOpen,
            CheckOnClick = true
        };
        skip.CheckedChanged += delegate { settings.SkipWhenOpen = skip.Checked; };
        menu.Items.Add(skip);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add("Exit artwork", null, delegate { EndSession(); });
        tray.Text = AppTarget.Product + " artwork";
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (ArgumentException) { tray.Icon = SystemIcons.Application; }
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

    private void ChooseWallpaper()
    {
        using (var dialog = new OpenFileDialog {
            Title = "Choose a " + AppTarget.Name + " wallpaper",
            Filter = "Wallpaper images (*.jpg, *.jpeg, *.png, *.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog() == DialogResult.OK && !shuttingDown)
                TryChangeWallpaper(dialog.FileName);
        }
    }

    private void ChooseAvatar()
    {
        using (var dialog = new OpenFileDialog {
            Title = "Choose a splash avatar",
            Filter = "Avatar images (*.jpg, *.jpeg, *.png, *.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog() != DialogResult.OK || shuttingDown) return;
            try
            {
                settings.ImportAvatar(dialog.FileName);
                restoreAvatar.Enabled = true;
                MessageBox.Show("The avatar will appear in the radar on the next animated start.",
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                    error is ExternalException || error is NotSupportedException ||
                    error is System.Security.SecurityException)) throw;
                MessageBox.Show("Could not change the avatar.\n\n" + error.Message,
                    AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void TryResetAvatar()
    {
        try
        {
            settings.ResetAvatar();
            restoreAvatar.Enabled = false;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            MessageBox.Show("Could not restore the default avatar.\n\n" + error.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddStyleItem(ToolStripMenuItem menu, string label, string value)
    {
        var item = new ToolStripMenuItem(label) { Checked = settings.Style == value };
        item.Click += delegate {
            settings.SaveStyle(value);
            foreach (ToolStripItem sibling in menu.DropDownItems)
                ((ToolStripMenuItem)sibling).Checked = sibling == item;
            Restyle();
        };
        menu.DropDownItems.Add(item);
    }

    private void Restyle()
    {
        Bitmap old = source;
        source = ApplyStyle(raw);
        if (child != null) child.SetImage(source);
        if (artwork != null) artwork.SetImage(source);
        if (old != null && old != raw) old.Dispose();
        RefreshArtwork();
    }

    // A styled copy of the wallpaper, or the same bitmap when style is natural.
    private Bitmap ApplyStyle(Bitmap src)
    {
        var bounds = new Rectangle(0, 0, src.Width, src.Height);
        if (settings.Style == "aura")
        {
            using (var gray = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
            {
                // Light blur under the tint so dense linework sits back.
                using (var g = Graphics.FromImage(gray))
                using (var attrs = new ImageAttributes())
                {
                    attrs.SetColorMatrix(new ColorMatrix(new float[][] {
                        new float[] {0.299f, 0.299f, 0.299f, 0, 0},
                        new float[] {0.587f, 0.587f, 0.587f, 0, 0},
                        new float[] {0.114f, 0.114f, 0.114f, 0, 0},
                        new float[] {0, 0, 0, 1, 0},
                        new float[] {0, 0, 0, 0, 1}}));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (var soft = BlurBitmap(src, 5))
                        g.DrawImage(soft, bounds, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                }
                var styled = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(styled))
                using (var attrs = new ImageAttributes())
                {
                    // Nen aura: grayscale luminance mapped onto a violet ramp.
                    attrs.SetColorMatrix(new ColorMatrix(new float[][] {
                        new float[] {0.55f, 0, 0, 0, 0},
                        new float[] {0, 0.40f, 0, 0, 0},
                        new float[] {0, 0, 0.82f, 0, 0},
                        new float[] {0, 0, 0, 1, 0},
                        new float[] {0, 0, 0, 0, 1}}));
                    g.DrawImage(gray, bounds, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                }
                // Original colors dominant; the duotone lands as a violet wash.
                var composite = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (var g = Graphics.FromImage(composite))
                    {
                        g.DrawImage(src, bounds, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                        using (var attrs = new ImageAttributes())
                        {
                            var wash = new ColorMatrix();
                            wash.Matrix33 = 0.35f;
                            attrs.SetColorMatrix(wash);
                            g.DrawImage(styled, bounds, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                        }
                    }
                    styled.Dispose();
                    return composite;
                }
                catch { composite.Dispose(); throw; }
            }
        }
        if (settings.Style == "dim") return BlurShade(src, 14, 132);
        if (settings.Style == "soften")
        {
            int tinyW = Math.Max(1, src.Width / 10), tinyH = Math.Max(1, src.Height / 10);
            using (var tiny = new Bitmap(tinyW, tinyH, PixelFormat.Format24bppRgb))
            {
                using (var tg = Graphics.FromImage(tiny))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    tg.DrawImage(src, 0, 0, tinyW, tinyH);
                }
                var styled = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(styled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(tiny, bounds);
                    using (var shade = new SolidBrush(Color.FromArgb(78, 8, 20, 29)))
                        g.FillRectangle(shade, bounds);
                }
                return styled;
            }
        }
        return src;
    }

    // Heavy blur + shade used by the dim style: artwork becomes a soft dark
    // texture so UI text stays readable over it.
    private static Bitmap BlurShade(Bitmap src, int downscale, int shadeAlpha)
    {
        var styled = BlurBitmap(src, downscale);
        try
        {
            using (var g = Graphics.FromImage(styled))
            using (var shade = new SolidBrush(Color.FromArgb(shadeAlpha, 8, 20, 29)))
                g.FillRectangle(shade, new Rectangle(0, 0, src.Width, src.Height));
            return styled;
        }
        catch { styled.Dispose(); throw; }
    }

    // Downscale-then-upscale bicubic blur at the original size.
    private static Bitmap BlurBitmap(Bitmap src, int downscale)
    {
        int tinyW = Math.Max(1, src.Width / downscale), tinyH = Math.Max(1, src.Height / downscale);
        using (var tiny = new Bitmap(tinyW, tinyH, PixelFormat.Format24bppRgb))
        {
            using (var tg = Graphics.FromImage(tiny))
            {
                tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                tg.DrawImage(src, 0, 0, tinyW, tinyH);
            }
            var blurred = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(blurred))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tiny, new Rectangle(0, 0, src.Width, src.Height));
            }
            return blurred;
        }
    }

    private void TryChangeWallpaper(string fileName)
    {
        try { ChangeWallpaper(fileName); }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is ExternalException || error is NotSupportedException ||
                error is System.Security.SecurityException)) throw;
            MessageBox.Show("Could not change the wallpaper.\n\n" + error.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ChangeWallpaper(string fileName)
    {
        // The store returns an unlocked image only after it has saved the choice.
        Bitmap nextRaw = fileName == null ? settings.Reset() : settings.Import(fileName);
        Bitmap oldRaw = raw, oldStyled = source;
        raw = nextRaw;
        source = ApplyStyle(nextRaw);
        if (child != null) child.SetImage(source);
        if (artwork != null) artwork.SetImage(source);
        if (oldStyled != null && oldStyled != oldRaw) oldStyled.Dispose();
        oldRaw.Dispose();
        restore.Enabled = settings.HasCustomWallpaper;
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
        SetDevinTransitions(enabled);
        locationHook = Native.SetWinEventHook(LocationEvent, LocationEvent, IntPtr.Zero, onEvent, pid, thread, 0);
        moveSizeHook = Native.SetWinEventHook(MoveSizeStart, MoveSizeEnd, IntPtr.Zero, onEvent, pid, thread, 0);
        return true;
    }

    private void SetDevinTransitions(bool disable)
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
        if (!childUnavailable && (enabled || child != null) && TryChildArtwork()) return;
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
            artwork.SetAlpha(opacity);
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
                child = new PreviewArtworkChild(target, targetPid, source, opacity);
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
        SetDevinTransitions(false);
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
        private IntPtr devinOwner;

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

        // Re-composites the same pixels with a new global opacity on the next Place.
        internal void SetAlpha(byte value)
        {
            alpha = value;
            forceComposite = true;
        }

        internal void AttachTo(IntPtr window)
        {
            // Let Windows raise the pair together, before it presents the first frame
            // of the reactivated window. Clicks and keyboard focus still go to Devin.
            devinOwner = window;
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
                if (devinOwner != IntPtr.Zero) p.Parent = devinOwner;
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
            if (devinOwner == targetWindow && Native.GetWindow(handle, Native.Owner) != targetWindow)
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
                        next = source.ScaleToCover(rect.Size);
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
                if (previousSize != null && previousSize != next) previousSize.Dispose();
                previousSize = current;
                current = next;
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
                // Use the preceding topmost window as the anchor if another app covers Devin.
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
                if (Native.IsTopmost(Handle) && Native.GetWindow(Handle, Native.Owner) != devinOwner)
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    }
}
