using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class ArtworkUpgrade
{
    // SHA-256 of previously shipped launchers and the previous local picker build.
    private static bool PreviousBuild(string path)
    {
        using (var file = File.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            return hash == "4B65AFB848880C7E79E99497D0925096AB3D58EECE575019BB2986DDC4969983" ||
                hash == "78CC9FB2C66E5BC07CF5FF19FEA1EC5A246D654A8AAE8B076E9CA72FFD1409F1" ||
                hash == "C9E8E7BBE9AAC7B7A04402A898966E2402133EBD1019B35478187E31E9E59AC9" ||
                hash == "727B588143FF147834ADB78392B24B4918FE4FC40C556853666D73D695B8133A";
        }
    }

    internal static bool TakeOverPreviousHelper(Mutex artworkMutex)
    {
        if (Acquire(artworkMutex, 0)) return true;
        bool requested = false;
        using (var current = Process.GetCurrentProcess())
        {
            foreach (var process in Process.GetProcessesByName("CodexAnimated"))
            using (process)
            {
                try
                {
                    if (requested || process.Id == current.Id || process.SessionId != current.SessionId) continue;
                    if (process.HasExited || !PreviousBuild(process.MainModule.FileName) ||
                        !HasArtworkArgument(process.Id)) continue;
                    uint thread = FormsThread(process);
                    if (thread != 0 && !process.HasExited)
                        requested = PostThreadMessageW(thread, 0x0012, UIntPtr.Zero, IntPtr.Zero); // WM_QUIT
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return Acquire(artworkMutex, requested ? 5000 : 0);
    }

    private static bool Acquire(Mutex mutex, int milliseconds)
    {
        try { return mutex.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; }
    }

    // Limit the command-line query to one already verified process. Only --artwork owns the tray.
    private static bool HasArtworkArgument(int processId)
    {
        try
        {
            using (var process = new ManagementObject("Win32_Process.Handle='" + processId + "'"))
            {
                string command = process["CommandLine"] as string;
                if (String.IsNullOrEmpty(command)) return false;
                int count;
                IntPtr argv = CommandLineToArgvW(command, out count);
                if (argv == IntPtr.Zero) return false;
                try
                {
                    return count == 2 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, IntPtr.Size)) == "--artwork";
                }
                finally { LocalFree(argv); }
            }
        }
        catch (ManagementException) { return false; }
        catch (COMException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static uint FormsThread(Process process)
    {
        foreach (ProcessThread candidate in process.Threads)
        using (candidate)
        {
            uint thread = 0;
            EnumThreadWindows((uint)candidate.Id, delegate(IntPtr window, IntPtr unused) {
                var name = new StringBuilder(128);
                GetClassNameW(window, name, name.Capacity);
                uint pid;
                uint id = GetWindowThreadProcessId(window, out pid);
                if (pid == process.Id && name.ToString().StartsWith("WindowsForms10.", StringComparison.Ordinal))
                    thread = id;
                return thread == 0;
            }, IntPtr.Zero);
            if (thread != 0) return thread;
        }
        return 0;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
