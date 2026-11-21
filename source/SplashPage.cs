using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// The text and accent shown by the splash page, and the token replacement that
// puts them into index.html.
internal sealed class SplashContent
{
    internal const string DefaultAccent = "#D980A7";
    // The text the original animation shipped with; used when an app has no theme and no text of its own.
    internal const string UpstreamTitle = "\u98DE\u884C\u96EA\u7ED2";
    internal const string UpstreamSubtitle = "\u5E7D\u7075\u6765\u5230\u2026\u4F60\u8EAB\u8FB9\uFF5E";
    internal const string UpstreamLabel = "RESONANCE / AEMEATH";
    internal static readonly string[] UpstreamLogs = {
        "MAGI SYSTEM \u00B7 CASPER / MELCHIOR / BALTHASAR", "loading renderer surface ........ ok",
        "entry plug depth 21.6 ........... ok", "LCL pressure nominal" };

    internal string Title = "", Subtitle = "", Accent = DefaultAccent, Theme = "", Label = "", Caption = "";
    internal string[] Logs = { "", "", "", "" };
    internal int Rate = 1;

    // A folder with theme.ini is a library theme; otherwise it is an app folder
    // and settings.ini supplies the text, theme name and speed.
    internal static SplashContent Load(string folder, string appName, Size artwork)
    {
        string themeFile = Path.Combine(folder, "theme.ini");
        bool isTheme = File.Exists(themeFile);
        IniFile ini = IniFile.Load(isTheme ? themeFile : Path.Combine(folder, AppProfile.FileName));
        SplashText text = SplashText.Read(ini);
        var content = new SplashContent();
        string upper = appName.ToUpperInvariant();
        bool original = !isTheme && (ini.Get("theme") ?? "").Trim().Length == 0 &&
            text.Title.Length == 0 && text.Subtitle.Length == 0 && text.Logs.Length == 0;
        content.Title = original ? UpstreamTitle : text.Title.Length > 0 ? text.Title : upper;
        content.Subtitle = original ? UpstreamSubtitle : text.Subtitle;
        content.Accent = text.Accent.Length > 0 ? text.Accent : DefaultAccent;
        content.Theme = isTheme ? Path.GetFileName(folder.TrimEnd('\\', '/')) : (ini.Get("theme") ?? "").Trim();
        content.Rate = !isTheme && (ini.Get("speed") ?? "").Trim() == "2" ? 2 : 1;
        content.Caption = upper + (original ? " INITIALIZE / TYPE-0" : " INITIALIZE");
        content.Label = original ? UpstreamLabel : content.Theme.Length > 0 ? content.Theme : appName;
        string[] lines = original ? UpstreamLogs : text.Logs;
        if (lines.Length == 0)
            lines = new[] {
                appName + " launcher ready",
                content.Theme.Length > 0 ? "theme: " + content.Theme : "custom artwork",
                "artwork " + artwork.Width.ToString(CultureInfo.InvariantCulture) + " \u00D7 " +
                    artwork.Height.ToString(CultureInfo.InvariantCulture),
                "opening " + appName
            };
        for (int i = 0; i < 4; i++) content.Logs[i] = i < lines.Length ? "> " + lines[i] : "";
        return content;
    }
}

internal static class SplashPage
{
    internal static byte[] RenderIndex(byte[] template, SplashContent content, string appName)
    {
        string accent = content.Accent;
        Color color;
        if (!SplashText.TryParseColor(accent, out color)) accent = SplashContent.DefaultAccent;
        var values = new System.Collections.Generic.Dictionary<string, string>
        {
            { "APP", appName.ToUpperInvariant() }, { "TITLE", content.Title }, { "SUBTITLE", content.Subtitle },
            { "THEME", content.Theme }, { "CAPTION", content.Caption }, { "LABEL", content.Label }, { "ACCENT", accent }, { "RATE", content.Rate == 2 ? "2" : "1" },
            { "LOG1", content.Logs[0] }, { "LOG2", content.Logs[1] }, { "LOG3", content.Logs[2] }, { "LOG4", content.Logs[3] }
        };
        // One pass, so text that looks like a token is never substituted again.
        string html = Regex.Replace(Encoding.UTF8.GetString(template), @"\{\{([A-Z0-9]+)\}\}", delegate(Match match) {
            string value;
            return values.TryGetValue(match.Groups[1].Value, out value) ? WebUtility.HtmlEncode(value ?? "") : match.Value;
        });
        return Encoding.UTF8.GetBytes(html);
    }
}
