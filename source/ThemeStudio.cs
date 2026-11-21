using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// Theme Studio: standalone configurator for the Animated launchers. It edits
// the per-app settings the artwork helper and splash already read from
// %LOCALAPPDATA%\<App> Animated — wallpaper.jpg, avatar.jpg, style.txt,
// opacity.txt, skip-when-open.flag — and keeps a named theme library per app
// under Themes\<name>. "Restart helper" applies changes live by launching
// <App>Animated.exe --artwork; the launcher's takeover handshake swaps the
// running helper so it reloads the new files.
internal static class ThemeStudio
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new StudioForm());
        return 0;
    }
}

internal sealed class AppChoice
{
    internal readonly string Name, Product, HelperExe;
    internal AppChoice(string name, string product, string helperExe)
    {
        Name = name; Product = product; HelperExe = helperExe;
    }
    internal string Root
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product); }
    }
    internal string ThemesRoot { get { return Path.Combine(Root, "Themes"); } }
    public override string ToString() { return Name; }
}

internal sealed class StudioForm : Form
{
    private static readonly AppChoice[] Apps = {
        new AppChoice("Devin", "Devin Animated", "DevinAnimated.exe"),
        new AppChoice("Antigravity", "Antigravity Animated", "AntigravityAnimated.exe"),
        new AppChoice("Codex", "Codex Animated", "CodexAnimated.exe")
    };
    private static readonly string[] Styles = { "natural", "soften", "dim", "aura" };
    private static readonly int[] Percents = { 10, 15, 20, 25 };

    private readonly ComboBox target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly PictureBox wallpaperPreview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(16, 26, 36) };
    private readonly PictureBox avatarPreview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(16, 26, 36) };
    private readonly ComboBox styleBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox opacityBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly CheckBox skipBox = new CheckBox { AutoSize = true, ForeColor = Color.FromArgb(214, 226, 233) };
    private readonly ListBox themes = new ListBox { IntegralHeight = false };
    private readonly PictureBox themePreview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(16, 26, 36) };
    private readonly TextBox themeName = new TextBox();
    private readonly Label status = new Label { AutoSize = true, ForeColor = Color.FromArgb(150, 170, 185) };
    private readonly Label wallpaperState = new Label { AutoSize = true };
    private readonly Label avatarState = new Label { AutoSize = true };
    private bool loading;

    private AppChoice Current { get { return (AppChoice)target.SelectedItem; } }
    private string WallpaperPath { get { return Path.Combine(Current.Root, "wallpaper.jpg"); } }
    private string AvatarPath { get { return Path.Combine(Current.Root, "avatar.jpg"); } }
    private string StylePath { get { return Path.Combine(Current.Root, "style.txt"); } }
    private string OpacityPath { get { return Path.Combine(Current.Root, "opacity.txt"); } }
    private string SkipPath { get { return Path.Combine(Current.Root, "skip-when-open.flag"); } }

    internal StudioForm()
    {
        Text = "Theme Studio";
        ClientSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 24, 33);
        ForeColor = Color.FromArgb(214, 226, 233);
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(660, 600);

        var targetLabel = LabelAt("Target app", 16, 16);
        target.Location = new Point(90, 12);
        foreach (var app in Apps) target.Items.Add(app);
        target.SelectedIndex = 0;
        target.SelectedIndexChanged += delegate { LoadAll(); };

        int top = 52;
        wallpaperPreview.SetBounds(16, top, 420, 210);
        wallpaperPreview.BorderStyle = BorderStyle.FixedSingle;
        var pickWallpaper = ButtonAt("Choose wallpaper…", 16, top + 218, delegate { ChooseImage(WallpaperPath); });
        var resetWallpaper = ButtonAt("Restore bundled", 164, top + 218, delegate {
            TryDelete(WallpaperPath); LoadAll(); Note("Wallpaper reset to the bundled artwork — restart the helper to apply.");
        });
        wallpaperState.Location = new Point(16, top + 248);
        wallpaperState.ForeColor = ForeColor;

        avatarPreview.SetBounds(468, top, 96, 96);
        avatarPreview.BorderStyle = BorderStyle.FixedSingle;
        LabelAt("Splash avatar", 468, top + 104).AutoSize = true;
        var pickAvatar = ButtonAt("Choose…", 468, top + 128, delegate { ChooseImage(AvatarPath); });
        pickAvatar.Width = 96;
        var resetAvatar = ButtonAt("Clear", 468, top + 160, delegate {
            TryDelete(AvatarPath); LoadAll(); Note("Avatar cleared — the wallpaper is used in the radar instead.");
        });
        resetAvatar.Width = 96;
        avatarState.Location = new Point(468, top + 192);
        avatarState.ForeColor = ForeColor;
        avatarState.MaximumSize = new Size(160, 0);

        LabelAt("Artwork style", 468, top + 200);
        styleBox.Location = new Point(468, top + 220);
        foreach (string style in Styles) styleBox.Items.Add(style);
        styleBox.SelectedIndexChanged += delegate {
            if (loading) return;
            WriteText(StylePath, (string)styleBox.SelectedItem);
            Note("Style saved — restart the helper to apply.");
        };
        LabelAt("Opacity", 16, top + 274);
        opacityBox.Location = new Point(16, top + 294);
        foreach (int percent in Percents) opacityBox.Items.Add(percent + "%");
        opacityBox.SelectedIndexChanged += delegate {
            if (loading) return;
            byte alpha = (byte)Math.Round(255 * Percents[opacityBox.SelectedIndex] / 100.0);
            WriteText(OpacityPath, alpha.ToString());
            Note("Opacity saved — restart the helper to apply.");
        };
        skipBox.Text = "Skip the splash when the app is already open";
        skipBox.Location = new Point(120, top + 296);
        skipBox.CheckedChanged += delegate {
            if (loading) return;
            SetFlag(SkipPath, skipBox.Checked);
            Note("Splash skip preference saved.");
        };
        var restart = ButtonAt("Restart artwork helper", 16, top + 330, delegate { RestartHelper(); });
        restart.Width = 200;
        restart.BackColor = Color.FromArgb(34, 70, 92);
        restart.FlatStyle = FlatStyle.Flat;
        restart.ForeColor = Color.FromArgb(230, 240, 246);

        int themesTop = top + 366;
        LabelAt("Themes (per target app)", 16, themesTop);
        themes.SetBounds(16, themesTop + 20, 200, 100);
        themes.SelectedIndexChanged += delegate { ShowThemePreview(); };
        themePreview.SetBounds(228, themesTop + 20, 280, 100);
        themePreview.BorderStyle = BorderStyle.FixedSingle;
        themeName.SetBounds(520, themesTop + 20, 106, 23);
        var saveTheme = ButtonAt("Save current", 520, themesTop + 46, delegate { SaveTheme(); });
        saveTheme.Width = 106;
        var applyTheme = ButtonAt("Apply", 520, themesTop + 76, delegate { ApplyTheme(); });
        applyTheme.Width = 106;
        var deleteTheme = ButtonAt("Delete", 520, themesTop + 106, delegate { DeleteTheme(); });
        deleteTheme.Width = 106;

        status.Location = new Point(16, ClientSize.Height - 26);
        status.MaximumSize = new Size(ClientSize.Width - 32, 0);

        Controls.AddRange(new Control[] {
            targetLabel, target, wallpaperPreview, pickWallpaper, resetWallpaper, wallpaperState,
            avatarPreview, pickAvatar, resetAvatar, avatarState, styleBox, opacityBox, skipBox, restart,
            themes, themePreview, themeName, saveTheme, applyTheme, deleteTheme, status
        });
        LoadAll();
    }

    private Label LabelAt(string text, int x, int y)
    {
        var label = new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = ForeColor };
        Controls.Add(label);
        return label;
    }

    private Button ButtonAt(string text, int x, int y, EventHandler action)
    {
        var button = new Button { Text = text, Location = new Point(x, y), Width = 140, Height = 26 };
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 110);
        button.BackColor = Color.FromArgb(22, 44, 58);
        button.ForeColor = ForeColor;
        button.Click += action;
        return button;
    }

    private void Note(string message) { status.Text = message; }

    private void LoadAll()
    {
        loading = true;
        try
        {
            LoadPreview(wallpaperPreview, WallpaperPath);
            wallpaperState.Text = File.Exists(WallpaperPath) ? "Custom wallpaper" : "Bundled artwork";
            LoadPreview(avatarPreview, AvatarPath);
            avatarState.Text = File.Exists(AvatarPath) ? "Custom" : "Wallpaper fallback";
            string style = "natural";
            try { if (File.Exists(StylePath)) style = File.ReadAllText(StylePath).Trim(); } catch (IOException) { }
            int styleIndex = Array.IndexOf(Styles, style);
            styleBox.SelectedIndex = styleIndex < 0 ? 0 : styleIndex;
            byte alpha = 38;
            try
            {
                if (File.Exists(OpacityPath)) byte.TryParse(File.ReadAllText(OpacityPath).Trim(), out alpha);
            }
            catch (IOException) { }
            int best = 1;
            for (int i = 0; i < Percents.Length; i++)
                if (Math.Abs(Percents[i] * 255 / 100 - alpha) < Math.Abs(Percents[best] * 255 / 100 - alpha)) best = i;
            opacityBox.SelectedIndex = best;
            skipBox.Checked = File.Exists(SkipPath);
            RefreshThemes();
            Note(Current.Root);
        }
        finally { loading = false; }
    }

    private static void LoadPreview(PictureBox box, string path)
    {
        Image previous = box.Image;
        box.Image = null;
        if (previous != null) previous.Dispose();
        if (path == null || !File.Exists(path)) return;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var image = Image.FromStream(stream))
                box.Image = new Bitmap(image);
        }
        catch (Exception) { }
    }

    private void ChooseImage(string destination)
    {
        using (var dialog = new OpenFileDialog {
            Title = "Choose an image",
            Filter = "Images (*.jpg, *.jpeg, *.png, *.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;
            try
            {
                Directory.CreateDirectory(Current.Root);
                File.WriteAllBytes(destination, Images.ToJpeg(dialog.FileName));
                LoadAll();
                Note("Saved " + Path.GetFileName(destination) + " — restart the helper to apply.");
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is ArgumentException || error is NotSupportedException)) throw;
                MessageBox.Show("Could not import that image.\n\n" + error.Message,
                    "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void RestartHelper()
    {
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Current.HelperExe);
        if (!File.Exists(exe))
        {
            MessageBox.Show(Current.HelperExe + " was not found next to ThemeStudio.exe.",
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--artwork") { UseShellExecute = false });
            Note("Helper restarted — the new artwork is now live in " + Current.Name + ".");
        }
        catch (Exception error)
        {
            if (!(error is System.ComponentModel.Win32Exception || error is InvalidOperationException ||
                error is FileNotFoundException)) throw;
            MessageBox.Show("Could not restart the helper.\n\n" + error.Message,
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshThemes()
    {
        string selected = themes.SelectedItem as string;
        themes.Items.Clear();
        if (Directory.Exists(Current.ThemesRoot))
            foreach (string directory in Directory.GetDirectories(Current.ThemesRoot))
                themes.Items.Add(Path.GetFileName(directory));
        if (selected != null && themes.Items.Contains(selected)) themes.SelectedItem = selected;
        ShowThemePreview();
    }

    private string SelectedThemeDir
    {
        get
        {
            string name = themes.SelectedItem as string;
            return name == null ? null : Path.Combine(Current.ThemesRoot, name);
        }
    }

    private void ShowThemePreview()
    {
        string dir = SelectedThemeDir;
        LoadPreview(themePreview, dir == null ? null : Path.Combine(dir, "wallpaper.jpg"));
    }

    private void SaveTheme()
    {
        string name = themeName.Text.Trim();
        if (name.Length == 0) { Note("Type a theme name first."); return; }
        foreach (char bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '-');
        string dir = Path.Combine(Current.ThemesRoot, name);
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(WallpaperPath)) File.Copy(WallpaperPath, Path.Combine(dir, "wallpaper.jpg"), true);
            else File.Delete(Path.Combine(dir, "wallpaper.jpg"));
            if (File.Exists(AvatarPath)) File.Copy(AvatarPath, Path.Combine(dir, "avatar.jpg"), true);
            else File.Delete(Path.Combine(dir, "avatar.jpg"));
            WriteText(Path.Combine(dir, "theme.txt"),
                "style=" + styleBox.SelectedItem + "\r\n" +
                "opacity=" + (byte)Math.Round(255 * Percents[opacityBox.SelectedIndex] / 100.0) + "\r\n" +
                "skip=" + (skipBox.Checked ? 1 : 0) + "\r\n");
            themeName.Clear();
            RefreshThemes();
            themes.SelectedItem = name;
            Note("Theme \"" + name + "\" saved for " + Current.Name + ".");
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            MessageBox.Show("Could not save the theme.\n\n" + error.Message,
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyTheme()
    {
        string dir = SelectedThemeDir;
        if (dir == null) { Note("Select a theme to apply."); return; }
        try
        {
            string wallpaper = Path.Combine(dir, "wallpaper.jpg");
            if (File.Exists(wallpaper))
            {
                Directory.CreateDirectory(Current.Root);
                File.Copy(wallpaper, WallpaperPath, true);
            }
            string avatar = Path.Combine(dir, "avatar.jpg");
            if (File.Exists(avatar)) File.Copy(avatar, AvatarPath, true);
            else TryDelete(AvatarPath);
            string meta = Path.Combine(dir, "theme.txt");
            string style = "natural"; byte opacity = 38; bool? skip = null;
            if (File.Exists(meta))
                foreach (string line in File.ReadAllLines(meta))
                {
                    int at = line.IndexOf('=');
                    if (at <= 0) continue;
                    string key = line.Substring(0, at).Trim(), value = line.Substring(at + 1).Trim();
                    if (key == "style" && Array.IndexOf(Styles, value) >= 0) style = value;
                    else if (key == "opacity") byte.TryParse(value, out opacity);
                    else if (key == "skip") skip = value == "1";
                }
            WriteText(StylePath, style);
            WriteText(OpacityPath, opacity.ToString());
            if (skip.HasValue) SetFlag(SkipPath, skip.Value);
            LoadAll();
            Note("Theme applied — restarting the helper.");
            RestartHelper();
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            MessageBox.Show("Could not apply the theme.\n\n" + error.Message,
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteTheme()
    {
        string dir = SelectedThemeDir;
        if (dir == null) { Note("Select a theme to delete."); return; }
        if (MessageBox.Show("Delete the theme \"" + Path.GetFileName(dir) + "\" for " + Current.Name + "?",
            "Theme Studio", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { Directory.Delete(dir, true); RefreshThemes(); Note("Theme deleted."); }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            MessageBox.Show("Could not delete the theme.\n\n" + error.Message,
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteText(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SetFlag(string path, bool on)
    {
        if (on) WriteText(path, "");
        else TryDelete(path);
    }
}

// Minimal image normalization: decode any GDI+ image and re-encode as JPEG
// quality 94, matching the launcher's own wallpaper import path.
internal static class Images
{
    internal static byte[] ToJpeg(string path)
    {
        using (var image = Image.FromFile(path))
        {
            if ((long)image.Width * image.Height > 64L * 1024 * 1024)
                throw new InvalidDataException("Choose an image under 64 megapixels.");
            using (var output = new MemoryStream())
            using (var options = new EncoderParameters(1))
            {
                options.Param[0] = new EncoderParameter(Encoder.Quality, 94L);
                var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                image.Save(output, codec, options);
                return output.ToArray();
            }
        }
    }
}
