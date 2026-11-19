using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The controller verifies the official Codex package, process and top-level HWND before attaching.
internal sealed class PreviewArtworkChild : NativeWindow, IDisposable
{
    private readonly IntPtr parent;
    private readonly uint parentPid;
    private readonly uint parentThread;
    private readonly IntPtr dpiContext;
    private readonly Bitmap image;
    private Size presented;
    private bool enabled = true;

    internal PreviewArtworkChild(IntPtr parent, uint expectedPid, Bitmap image)
    {
        this.parent = parent;
        this.image = image;
        uint actualPid;
        parentThread = GetWindowThreadProcessId(parent, out actualPid);
        if (parentThread == 0 || actualPid != expectedPid || GetAncestor(parent, 2) != parent)
            throw new ArgumentException("The verified source window is unavailable.");
        parentPid = expectedPid;
        dpiContext = GetWindowDpiAwarenessContext(parent);
        IntPtr previous = SetThreadDpiAwarenessContext(dpiContext);
        if (previous == IntPtr.Zero) throw new Win32Exception();
        try
        {
            // Create as a child from the outset. No SetParent call or changes to the host.
            CreateHandle(new CreateParams {
                Parent = parent, Caption = "Codex Animated artwork preview", Style = 0x40000000,
                ExStyle = 0x80000 | 0x20 | 0x08000000 | 0x4, // LAYERED, TRANSPARENT, NOACTIVATE, NOPARENTNOTIFY
                X = 0, Y = 0, Width = 1, Height = 1
            });
            if (!AreDpiAwarenessContextsEqual(dpiContext, GetWindowDpiAwarenessContext(Handle)))
                throw new InvalidOperationException("The child DPI context did not match.");
            if ((GetWindowLongPtrW(Handle, -20).ToInt64() & 0x80000) == 0)
                throw new NotSupportedException("The helper manifest does not enable layered child windows.");
            if (!RefreshSurface()) throw new InvalidOperationException("The source window closed during attachment.");
        }
        catch { Dispose(); throw; }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    internal void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (Handle == IntPtr.Zero) return;
        if (!value) ShowWindow(Handle, 0);
        else if (!presented.IsEmpty)
        {
            // Reuse the retained pixels even when artwork is re-enabled from the
            // tray while the host is minimized. Preserve focus, position and Z-order.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0040 | 0x0010 | 0x0004 | 0x0002 | 0x0001);
        }
    }

    internal bool RefreshSurface()
    {
        uint pid;
        if (Handle == IntPtr.Zero || GetWindowThreadProcessId(parent, out pid) != parentThread || pid != parentPid)
            return false;
        // Preserve the source's last submitted image for taskbar previews while minimized.
        if (!enabled || IsIconic(parent) || !IsWindowVisible(parent)) return true;
        IntPtr previous = SetThreadDpiAwarenessContext(dpiContext);
        if (previous == IntPtr.Zero) throw new Win32Exception();
        try { return Present(); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private bool Present()
    {
        Rect rect;
        if (!GetClientRect(parent, out rect) || rect.Right <= 0 || rect.Bottom <= 0)
            return true;
        Size size = new Size(rect.Right, rect.Bottom);
        if (size != presented)
        {
            using (var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb))
            {
                var scale = Math.Max((float)size.Width / image.Width, (float)size.Height / image.Height);
                float w = size.Width / scale, h = size.Height / scale;
                using (var g = Graphics.FromImage(frame))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(image, new Rectangle(0, 0, size.Width, size.Height),
                        new RectangleF((image.Width - w) / 2, (image.Height - h) / 2, w, h), GraphicsUnit.Pixel);
                }
                IntPtr bitmap = frame.GetHbitmap(), dc = CreateCompatibleDC(IntPtr.Zero);
                IntPtr old = IntPtr.Zero;
                try
                {
                    if (dc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception();
                    old = SelectObject(dc, bitmap);
                    if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new Win32Exception();
                    var from = new NativePoint();
                    var nativeSize = new NativeSize { W = size.Width, H = size.Height };
                    var blend = new Blend { Alpha = 38 };
                    // Null destination preserves the original child-relative (0,0) across parent moves.
                    if (!UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref nativeSize, dc,
                        ref from, 0, ref blend, 2)) throw new Win32Exception();
                }
                finally
                {
                    if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(dc, old);
                    if (dc != IntPtr.Zero) DeleteDC(dc);
                    if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                }
            }
            presented = size;
        }
        if (!IsWindowVisible(Handle) || GetWindow(parent, 5) != Handle)
        {
            // HWND_TOP here acts only within this parent's siblings.
            if (!SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0040 | 0x0010 | 0x0001))
                throw new Win32Exception();
        }
        return true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }

    public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int W,H; }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint which);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint kind);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr window,int index);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window, IntPtr insert, int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destDC, IntPtr dest, ref NativeSize size, IntPtr sourceDC, ref NativePoint source, uint key, ref Blend blend, uint flags);
}
