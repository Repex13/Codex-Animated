using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// key=value settings file: UTF-8 without BOM, CRLF, keys case-insensitive,
// unknown keys kept on save.
internal sealed class IniFile
{
    private readonly List<string> order = new List<string>();
    private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // A missing or unreadable file reads as empty.
    internal static IniFile Load(string path)
    {
        try { return LoadStrict(path); }
        catch (IOException) { return new IniFile(); }
        catch (UnauthorizedAccessException) { return new IniFile(); }
    }

    // A missing file is empty; a file that exists but cannot be read throws.
    internal static IniFile LoadStrict(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;
        string text;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var reader = new StreamReader(file, new UTF8Encoding(false), true))
            text = reader.ReadToEnd();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;
            int split = line.IndexOf('=');
            if (split <= 0) continue;
            string key = line.Substring(0, split).Trim();
            if (key.Length == 0) continue;
            ini.Set(key, line.Substring(split + 1).Trim());
        }
        return ini;
    }

    internal string Get(string key)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : null;
    }

    internal void Set(string key, string value)
    {
        if (value == null)
        {
            if (values.Remove(key)) order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            return;
        }
        value = value.Replace("\r", "").Replace("\n", "");
        if (!values.ContainsKey(key)) order.Add(key);
        values[key] = value;
    }

    internal void Save(string path)
    {
        var text = new StringBuilder();
        foreach (string key in order) text.Append(key).Append('=').Append(values[key]).Append("\r\n");
        ImageCodec.SaveAtomic(path, new UTF8Encoding(false).GetBytes(text.ToString()));
    }
}

internal sealed class Look
{
    internal static readonly Color DefaultTint = Color.FromArgb(0x8C, 0x66, 0xD1);
    internal int Opacity = 15, Blur, Dim, Tint, FocusX = 50, FocusY = 50;
    internal Color TintColor = DefaultTint;

    internal byte Alpha { get { return (byte)Math.Round(255 * Opacity / 100.0, MidpointRounding.AwayFromZero); } }

    internal bool SameRender(Look other)
    {
        return Blur == other.Blur && Dim == other.Dim && Tint == other.Tint &&
            TintColor.ToArgb() == other.TintColor.ToArgb();
    }

    internal static Look Read(IniFile ini)
    {
        var look = new Look();
        look.Opacity = Number(ini, "opacity", 15, 4, 40);
        look.Blur = Number(ini, "blur", 0, 0, 100);
        look.Dim = Number(ini, "dim", 0, 0, 80);
        look.Tint = Number(ini, "tint", 0, 0, 100);
        look.FocusX = Number(ini, "focusX", 50, 0, 100);
        look.FocusY = Number(ini, "focusY", 50, 0, 100);
        Color color;
        look.TintColor = SplashText.TryParseColor(ini.Get("tintColor"), out color) ? color : DefaultTint;
        return look;
    }

    internal void Write(IniFile ini)
    {
        ini.Set("opacity", Opacity.ToString(CultureInfo.InvariantCulture));
        ini.Set("blur", Blur.ToString(CultureInfo.InvariantCulture));
        ini.Set("dim", Dim.ToString(CultureInfo.InvariantCulture));
        ini.Set("tint", Tint.ToString(CultureInfo.InvariantCulture));
        ini.Set("tintColor", string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", TintColor.R, TintColor.G, TintColor.B));
        ini.Set("focusX", FocusX.ToString(CultureInfo.InvariantCulture));
        ini.Set("focusY", FocusY.ToString(CultureInfo.InvariantCulture));
    }

    internal static int Number(IniFile ini, string key, int fallback, int min, int max)
    {
        int value;
        if (!int.TryParse(ini.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
        return Math.Max(min, Math.Min(max, value));
    }
}

internal sealed class SplashText
{
    internal string Title = "", Subtitle = "", Accent = "";
    internal string[] Logs = new string[0];

    internal static SplashText Read(IniFile ini)
    {
        var text = new SplashText();
        text.Title = Limit(ini.Get("title"), 40);
        text.Subtitle = Limit(ini.Get("subtitle"), 60);
        Color color;
        text.Accent = TryParseColor(ini.Get("accent"), out color)
            ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B) : "";
        var logs = new List<string>();
        string joined = ini.Get("logs");
        if (!string.IsNullOrEmpty(joined))
            foreach (string part in joined.Split('|'))
            {
                string line = Limit(part, 80);
                if (line.Length > 0 && logs.Count < 4) logs.Add(line);
            }
        text.Logs = logs.ToArray();
        return text;
    }

    internal void Write(IniFile ini)
    {
        ini.Set("title", Limit(Title, 40));
        ini.Set("subtitle", Limit(Subtitle, 60));
        ini.Set("accent", Accent ?? "");
        var logs = new List<string>();
        if (Logs != null)
            foreach (string log in Logs)
            {
                string line = Limit((log ?? "").Replace("|", " "), 80);
                if (line.Length > 0 && logs.Count < 4) logs.Add(line);
            }
        ini.Set("logs", string.Join("|", logs.ToArray()));
    }

    // Strict #RRGGBB.
    internal static bool TryParseColor(string text, out Color color)
    {
        color = Color.Empty;
        if (text == null) return false;
        text = text.Trim();
        if (text.Length != 7 || text[0] != '#') return false;
        int rgb;
        for (int i = 1; i < 7; i++)
            if (Uri.IsHexDigit(text[i]) == false) return false;
        if (!int.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb))
            return false;
        color = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    private static string Limit(string value, int max)
    {
        value = (value ?? "").Trim();
        return value.Length > max ? value.Substring(0, max) : value;
    }
}

// One app's settings.ini.
internal sealed class AppProfile
{
    internal const string FileName = "settings.ini";
    internal readonly string Root;
    internal string Theme = "";
    internal Look Look = new Look();
    internal SplashText Text = new SplashText();
    internal bool SplashOnStart = true;
    internal int Speed = 1;

    private AppProfile(string root) { Root = root; }

    internal static AppProfile Load(string root) { return Parse(root, IniFile.Load(Path.Combine(root, FileName))); }

    // Throws when the file exists but cannot be read, so a live reload can keep its last state.
    internal static AppProfile LoadStrict(string root) { return Parse(root, IniFile.LoadStrict(Path.Combine(root, FileName))); }

    private static AppProfile Parse(string root, IniFile ini)
    {
        var profile = new AppProfile(root);
        profile.Theme = (ini.Get("theme") ?? "").Trim();
        profile.Look = Look.Read(ini);
        profile.Text = SplashText.Read(ini);
        profile.SplashOnStart = !string.Equals((ini.Get("splash") ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        profile.Speed = (ini.Get("speed") ?? "").Trim() == "2" ? 2 : 1;
        return profile;
    }

    internal void Save()
    {
        ThemeLibrary.WithLock(Root, delegate {
            string path = Path.Combine(Root, FileName);
            IniFile ini = IniFile.LoadStrict(path);
            ini.Set("theme", Theme ?? "");
            Look.Write(ini);
            Text.Write(ini);
            ini.Set("splash", SplashOnStart ? "on" : "off");
            ini.Set("speed", Speed == 2 ? "2" : "1");
            ini.Save(path);
        });
    }
}

internal static class ThemeLibrary
{
    internal static string RootOverride = null;

    internal static string SharedRoot
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Animated Launchers");
        }
    }

    internal static string Root { get { return RootOverride ?? Path.Combine(SharedRoot, "Themes"); } }

    internal static string[] Names()
    {
        var names = new List<string>();
        try
        {
            foreach (string directory in Directory.GetDirectories(Root))
                if (File.Exists(Path.Combine(directory, "wallpaper.jpg"))) names.Add(Path.GetFileName(directory));
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    internal static string Folder(string name) { return Path.Combine(Root, name); }

    // Copies the theme's images into the app folder, then replaces the app's look
    // and splash text with the theme's. Images first, settings.ini last.
    internal static void Apply(string name, string appRoot)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The theme name is not valid.");
        string folder = Folder(name);
        string wallpaper = Path.Combine(folder, "wallpaper.jpg");
        if (!File.Exists(wallpaper)) throw new FileNotFoundException("The theme \"" + name + "\" has no wallpaper.", wallpaper);
        WithLock(appRoot, delegate {
            ImageCodec.SaveAtomic(Path.Combine(appRoot, "wallpaper.jpg"), ImageCodec.ReadBytes(wallpaper));
            string avatar = Path.Combine(folder, "avatar.jpg");
            string appAvatar = Path.Combine(appRoot, "avatar.jpg");
            if (File.Exists(avatar)) ImageCodec.SaveAtomic(appAvatar, ImageCodec.ReadBytes(avatar));
            else File.Delete(appAvatar);
            IniFile theme = IniFile.Load(Path.Combine(folder, "theme.ini"));
            AppProfile profile = AppProfile.Load(appRoot);
            profile.Look = Look.Read(theme);
            profile.Text = SplashText.Read(theme);
            profile.Theme = name;
            profile.Save();
        });
    }

    private static readonly string[] ReservedNames = {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // The trimmed name, or an ArgumentException saying what to change.
    internal static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name.");
        if (name.Length > 60) throw new ArgumentException("Use 60 characters or fewer.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A name can't contain \\ / : * ? \" < > |.");
        if (name.EndsWith(".", StringComparison.Ordinal)) throw new ArgumentException("A name can't end with a dot.");
        string stem = name.Split('.')[0].TrimEnd();
        if (Array.IndexOf(ReservedNames, stem.ToUpperInvariant()) >= 0)
            throw new ArgumentException("\"" + stem + "\" is reserved by Windows. Use another name.");
        return name;
    }

    // The library folder name that equals the given text exactly, or null.
    internal static string Exact(string name)
    {
        if (name == null) return null;
        foreach (string existing in Names()) if (string.Equals(existing, name, StringComparison.Ordinal)) return existing;
        return null;
    }

    internal static bool UsesTheme(string root, string name)
    {
        return string.Equals(AppProfile.Load(root).Theme, name, StringComparison.OrdinalIgnoreCase);
    }

    // Writes theme.ini (clamped, accent made legible) and copies look and text into every app on this theme.
    internal static Look Save(string name, Look look, SplashText text, IEnumerable<string> appRoots, out SplashText stored)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        string path = Path.Combine(Folder(exact), "theme.ini");
        Look clamped = null;
        SplashText normalized = null;
        WithLock(Folder(exact), delegate {
            IniFile ini = IniFile.LoadStrict(path);
            look.Write(ini);
            clamped = Look.Read(ini);
            clamped.Write(ini);
            text.Write(ini);
            normalized = SplashText.Read(ini);
            Color accent;
            if (SplashText.TryParseColor(normalized.Accent, out accent))
            {
                Color legible = ArtworkLook.Legible(accent);
                normalized.Accent = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", legible.R, legible.G, legible.B);
            }
            normalized.Write(ini);
            ini.Save(path);
        });
        foreach (string root in appRoots)
        {
            if (!UsesTheme(root, exact)) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            profile.Look = clamped;
            profile.Text = normalized;
            profile.Save();
        }
        stored = normalized;
        return clamped;
    }

    // Renames the folder (a case-only change goes through a temporary name) and fixes theme= in apps.
    internal static string Rename(string name, string newName, IEnumerable<string> appRoots)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        newName = ValidateName(newName);
        if (string.Equals(exact, newName, StringComparison.Ordinal)) return exact;
        bool caseOnly = string.Equals(exact, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Directory.Exists(Folder(newName)))
            throw new IOException("A theme called \"" + newName + "\" already exists.");
        if (caseOnly)
        {
            string temporary = Folder(exact) + ".rename-" + Guid.NewGuid().ToString("N");
            Directory.Move(Folder(exact), temporary);
            Directory.Move(temporary, Folder(newName));
        }
        else Directory.Move(Folder(exact), Folder(newName));
        foreach (string root in appRoots)
        {
            if (!UsesTheme(root, exact)) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            profile.Theme = newName;
            profile.Save();
        }
        return newName;
    }

    // Imports a picture as a new theme named after the file; returns the folder name.
    internal static string Create(string imagePath)
    {
        byte[] jpeg = ImageCodec.ToJpeg(imagePath);
        Color suggestion = Color.Empty;
        using (Bitmap decoded = ImageCodec.DecodeJpeg(jpeg))
        {
            Color[] accents = ArtworkLook.SuggestAccents(decoded, 1);
            if (accents.Length > 0) suggestion = accents[0];
        }
        string baseName = CleanName(Path.GetFileNameWithoutExtension(imagePath));
        string name = baseName;
        for (int n = 2; Directory.Exists(Folder(name)); n++)
        {
            string suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            name = (baseName.Length + suffix.Length > 60 ? baseName.Substring(0, 60 - suffix.Length).TrimEnd() : baseName) + suffix;
        }
        string folder = Folder(name);
        try
        {
            Directory.CreateDirectory(folder);
            ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), jpeg);
            var ini = new IniFile();
            new Look().Write(ini);
            var text = new SplashText();
            string title = name.ToUpperInvariant();
            text.Title = title.Length > 40 ? title.Substring(0, 40) : title;
            Color accent = suggestion.IsEmpty ? Color.FromArgb(0xD9, 0x80, 0xA7) : ArtworkLook.Legible(suggestion);
            text.Accent = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", accent.R, accent.G, accent.B);
            text.Write(ini);
            ini.Save(Path.Combine(folder, "theme.ini"));
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return name;
    }

    private static string CleanName(string raw)
    {
        var chars = new StringBuilder();
        foreach (char c in raw ?? "")
            chars.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c);
        string name = string.Join(" ", chars.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (name.Length > 60) name = name.Substring(0, 60).TrimEnd('.', ' ');
        try { return ValidateName(name); }
        catch (ArgumentException) { return "New theme"; }
    }

    // Empties theme= in every app that used the theme; their own copies keep working.
    internal static void ClearReferences(string name, IEnumerable<string> appRoots)
    {
        foreach (string root in appRoots)
        {
            if (!UsesTheme(root, name)) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            profile.Theme = "";
            profile.Save();
        }
    }

    // Wallpaper and avatar write times and sizes combined; changes whenever either image does.
    internal static string Stamp(string name)
    {
        long value = 17;
        foreach (string file in new[] { "wallpaper.jpg", "avatar.jpg" })
        {
            var info = new FileInfo(Path.Combine(Folder(name), file));
            value = value * 31 + (info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0);
        }
        return (value & long.MaxValue).ToString("x", CultureInfo.InvariantCulture);
    }

    // Serializes everything that writes into one settings or theme folder, in every process.
    internal static void WithLock(string folder, Action action)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\').ToUpperInvariant();
        string id;
        using (var sha = SHA1.Create())
            id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full))).Replace("-", "").Substring(0, 16);
        using (var mutex = new Mutex(false, @"Local\AnimatedLaunchers.Files." + id))
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new IOException("Another settings update is still in progress. Try again.");
            }
            catch (AbandonedMutexException) { } // The previous process exited while it held the lock.
            try { action(); } finally { mutex.ReleaseMutex(); }
        }
    }
}
