using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Resolves the target desktop application. Devin and Antigravity are regular
// per-user Electron installs; Claude is a Squirrel-style per-user install with a
// versioned app-<version> folder; Codex is a Microsoft Store package activated
// through its AUMID. Build.ps1 compiles one helper per target:
//   /define:APP_DEVIN /out:DevinAnimated.exe
//   /define:APP_ANTIGRAVITY /out:AntigravityAnimated.exe
//   /define:APP_CODEX /out:CodexAnimated.exe
//   /define:APP_CLAUDE /out:ClaudeAnimated.exe
internal static class AppTarget
{
#if APP_DEVIN
    internal const string Name = "Devin";
    internal const string Product = "Devin Animated";
    internal const string HelperName = "DevinAnimated";
    private const string ExecutableName = "Devin.exe";
    private const string InstallFolder = "Devin";
    private const string Scheme = "devin";
#elif APP_ANTIGRAVITY
    internal const string Name = "Antigravity";
    internal const string Product = "Antigravity Animated";
    internal const string HelperName = "AntigravityAnimated";
    private const string ExecutableName = "Antigravity IDE.exe";
    private const string InstallFolder = "Antigravity IDE";
    private const string Scheme = "antigravity";
#elif APP_CLAUDE
    internal const string Name = "Claude";
    internal const string Product = "Claude Animated";
    internal const string HelperName = "ClaudeAnimated";
    private const string ExecutableName = "claude.exe";
    private const string InstallFolder = "AnthropicClaude";
    private const string Scheme = "claude";
#elif APP_CODEX
    internal const string Name = "Codex";
    internal const string Product = "Codex Animated";
    internal const string HelperName = "CodexAnimated";
    private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string Aumid = PackageFamily + "!App";
    private static readonly string[] StoreImageNames = { "ChatGPT.exe", "Codex.exe" };
#else
#error Define one of APP_DEVIN, APP_ANTIGRAVITY, APP_CLAUDE, APP_CODEX.
#endif
    private static volatile string[] directories;

    internal static string SettingsDirectory
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
        }
    }

    internal static string[] Directories
    {
        get
        {
            var list = directories;
            if (list == null) directories = list = Probe();
            return list;
        }
    }

    // Opens the target application. Starting it again lets it bring an existing
    // session forward.
    internal static void Launch()
    {
#if APP_CODEX
        ActivateCodex();
#else
        string executable = ExecutablePath();
        if (executable == null)
            throw new FileNotFoundException("The " + Name + " desktop app is not installed for this Windows account.");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
#endif
    }

    // The first existing target executable among the candidate roots, or null.
    // Codex is a Store package and is activated through its AUMID instead.
    internal static string ExecutablePath()
    {
#if APP_CODEX
        return null;
#elif APP_CLAUDE
        // The stub at the root starts the newest version; fall back to the highest app-<version> folder.
        foreach (string directory in Directories)
        {
            string stub = Path.Combine(directory, ExecutableName);
            if (File.Exists(stub)) return stub;
        }
        string best = null;
        Version bestVersion = null;
        string bestText = null;
        foreach (string directory in Directories)
        {
            string[] folders;
            try { folders = Directory.GetDirectories(directory, "app-*"); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string folder in folders)
            {
                string exe = Path.Combine(folder, ExecutableName);
                if (!File.Exists(exe)) continue;
                string text = Path.GetFileName(folder).Substring(4);
                Version version;
                bool parsed = Version.TryParse(text, out version);
                bool better;
                if (best == null) better = true;
                else if (parsed && bestVersion != null) better = version > bestVersion;
                else if (parsed != (bestVersion != null)) better = parsed;
                else better = string.CompareOrdinal(text, bestText) > 0;
                if (better) { best = exe; bestVersion = parsed ? version : null; bestText = text; }
            }
        }
        return best;
#else
        foreach (string directory in Directories)
        {
            string path = Path.Combine(directory, ExecutableName);
            if (File.Exists(path)) return path;
        }
        return null;
#endif
    }

    // Whether a process image path belongs to the target desktop app itself.
    internal static bool Owns(string imagePath)
    {
#if APP_CODEX
        string file = Path.GetFileName(imagePath);
        foreach (string allowed in StoreImageNames)
            if (string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase))
                return imagePath.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
        return false;
#elif APP_CLAUDE
        // claude.exe in an install root or in an app-<version> folder directly under one. Checked on every
        // call, so a folder created by an update while the helper runs is accepted. Other programs that are
        // also called claude.exe (the command-line tool) live elsewhere and never match.
        if (!string.Equals(Path.GetFileName(imagePath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return false;
        string directory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(directory)) return false;
        string parent = null;
        if (Path.GetFileName(directory).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
            parent = Path.GetDirectoryName(directory);
        foreach (string candidate in Directories)
        {
            if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)) return true;
            if (parent != null && string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
#else
        if (!string.Equals(Path.GetFileName(imagePath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return false;
        string directory = Path.GetDirectoryName(imagePath);
        foreach (string candidate in Directories)
            if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
#endif
    }

    // Whether a live process (by id) belongs to the target app.
    internal static bool OwnsProcess(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            uint length = 32768;
            var image = new StringBuilder((int)length);
            if (!QueryFullProcessImageName(process, 0, image, ref length)) return false;
            if (!Owns(image.ToString())) return false;
#if APP_CODEX
            uint familyLength = 0;
            if (GetPackageFamilyName(process, ref familyLength, null) != 122) return false;
            var family = new StringBuilder((int)familyLength);
            if (GetPackageFamilyName(process, ref familyLength, family) != 0) return false;
            return string.Equals(family.ToString(), PackageFamily, StringComparison.OrdinalIgnoreCase);
#else
            return true;
#endif
        }
        finally { CloseHandle(process); }
    }

    // When the process (by id) was created; false when it cannot be queried.
    internal static bool TryGetStartTime(uint pid, out DateTime utc)
    {
        utc = DateTime.MinValue;
        IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            long created, exited, kernel, user;
            if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) return false;
            utc = DateTime.FromFileTimeUtc(created);
            return true;
        }
        finally { CloseHandle(process); }
    }

    // A visible top-level target window, or zero when the app is not running.
    // Used by the launcher to skip the splash when a session already exists.
    internal static IntPtr FindMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr param) {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            var cls = new StringBuilder(128);
            GetClassNameW(window, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            uint pid;
            if (GetWindowThreadProcessId(window, out pid) == 0) return true;
            if (OwnsProcess(pid))
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

#if APP_CODEX
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            IntPtr itemArray, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    private static void ActivateCodex()
    {
        var activator = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        int hr = activator.ActivateApplication(Aumid, null, 0, out pid);
        if (hr != 0) throw new System.ComponentModel.Win32Exception(hr);
    }
#endif

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder output, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder family);

    private static string[] Probe()
    {
        var list = new List<string>();
#if APP_CLAUDE
        Add(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallFolder));
        Add(list, RegisteredCommandDirectory());
#elif !APP_CODEX
        Add(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", InstallFolder));
        foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root)) Add(list, Path.Combine(root, InstallFolder));
        }
        Add(list, RegisteredCommandDirectory());
#endif
        return list.ToArray();
    }

    // The protocol registration records the real install location even when the
    // executable sits outside the usual Programs folders.
    private static string RegisteredCommandDirectory()
    {
#if APP_CODEX
        return null;
#else
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\" + Scheme + @"\shell\open\command"))
            {
                string command = key == null ? null : key.GetValue(null) as string;
                // Shaped like: "C:\…\Devin.exe" --open-url -- "%1"
                if (string.IsNullOrEmpty(command) || command[0] != '"') return null;
                int end = command.IndexOf('"', 1);
                if (end <= 1) return null;
                string folder = Path.GetDirectoryName(command.Substring(1, end - 1));
#if APP_CLAUDE
                if (folder != null && Path.GetFileName(folder).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                    folder = Path.GetDirectoryName(folder);
#endif
                return folder;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)) throw;
            return null;
        }
#endif
    }

    private static void Add(List<string> list, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { path = Path.GetFullPath(path); }
        catch (ArgumentException) { return; }
        catch (NotSupportedException) { return; }
        catch (System.Security.SecurityException) { return; }
        foreach (string existing in list)
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(path);
    }
}
