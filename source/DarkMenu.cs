using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Flat dark look for the tray menu.
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly Color Surface = Color.FromArgb(0x16, 0x16, 0x1B);
    internal static readonly Color Line = Color.FromArgb(0x2A, 0x2A, 0x33);
    internal static readonly Color Text = Color.FromArgb(0xEF, 0xE9, 0xF4);
    internal static readonly Color Disabled = Color.FromArgb(0x82, 0x7B, 0x90);
    internal static readonly Color Hover = Color.FromArgb(0x26, 0x26, 0x30);

    internal DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    // Applies the dark style to a menu or submenu.
    internal static void Style(ToolStripDropDown dropDown)
    {
        dropDown.Renderer = new DarkMenuRenderer();
        dropDown.BackColor = Surface;
        dropDown.ForeColor = Text;
        var menu = dropDown as ToolStripDropDownMenu;
        if (menu != null)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (var brush = new SolidBrush(Surface)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using (var brush = new SolidBrush(Surface)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using (var pen = new Pen(Line))
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using (var brush = new SolidBrush((e.Item.Selected || e.Item.Pressed) && e.Item.Enabled ? Hover : Surface))
            e.Graphics.FillRectangle(brush, bounds);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using (var brush = new SolidBrush(Surface)) e.Graphics.FillRectangle(brush, 0, 0, e.Item.Width, e.Item.Height);
        using (var pen = new Pen(Line))
            e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item.Enabled ? Text : Disabled;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle box = e.ImageRectangle;
        int cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
        var old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(e.Item.Enabled ? Text : Disabled, 1.6f))
            e.Graphics.DrawLines(pen, new[] { new Point(cx - 4, cy), new Point(cx - 1, cy + 3), new Point(cx + 4, cy - 3) });
        e.Graphics.SmoothingMode = old;
    }
}

internal sealed class DarkColors : ProfessionalColorTable
{
    public DarkColors() { UseSystemColors = false; }
    public override Color ToolStripDropDownBackground { get { return DarkMenuRenderer.Surface; } }
    public override Color ImageMarginGradientBegin { get { return DarkMenuRenderer.Surface; } }
    public override Color ImageMarginGradientMiddle { get { return DarkMenuRenderer.Surface; } }
    public override Color ImageMarginGradientEnd { get { return DarkMenuRenderer.Surface; } }
    public override Color MenuBorder { get { return DarkMenuRenderer.Line; } }
    public override Color MenuItemBorder { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemSelected { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemPressedGradientBegin { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemPressedGradientMiddle { get { return DarkMenuRenderer.Hover; } }
    public override Color MenuItemPressedGradientEnd { get { return DarkMenuRenderer.Hover; } }
    public override Color SeparatorDark { get { return DarkMenuRenderer.Line; } }
    public override Color SeparatorLight { get { return DarkMenuRenderer.Line; } }
    public override Color CheckBackground { get { return DarkMenuRenderer.Surface; } }
    public override Color CheckSelectedBackground { get { return DarkMenuRenderer.Hover; } }
    public override Color CheckPressedBackground { get { return DarkMenuRenderer.Hover; } }
}
