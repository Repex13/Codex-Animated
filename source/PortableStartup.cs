using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

internal static class PortableStartup
{
    private static readonly string[] runtimeFiles = {
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
        "web/index.html", "web/style.css", "web/animation.js", "web/assets/artwork.jpg"
    };
    private static readonly string[] notices = { "notices/LICENSE.txt", "notices/NOTICE.txt" };
    internal static string BundleDirectory { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string besideExe = AppDomain.CurrentDomain.BaseDirectory;
            if (Array.TrueForAll(runtimeFiles, name => File.Exists(Path.Combine(besideExe, name))))
                BundleDirectory = besideExe;
            else
            {
                BundleDirectory = PrepareEmbeddedBundle();
                AppDomain.CurrentDomain.AssemblyResolve += ResolveWebViewAssembly;
            }
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                ex is System.Security.SecurityException)) throw;
            MessageBox.Show("Windows could not prepare the launcher files.\n\n" + ex.Message,
                "Codex Animated", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return RunLauncher(args);
    }

    // Keep WebView2 references outside this entry point until the resolver is ready.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args) { return Launcher.Run(args); }

    private static Assembly ResolveWebViewAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms")
            return null;
        return Assembly.LoadFrom(Path.Combine(BundleDirectory, name + ".dll"));
    }

    private static string PrepareEmbeddedBundle()
    {
        var assembly = typeof(PortableStartup).Assembly;
        string build = assembly.ManifestModule.ModuleVersionId.ToString("N");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codex Animated", "Runtime", build);
        using (var mutex = new Mutex(false, @"Local\CodexAnimated.Bundle." + build))
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("The launcher files are busy. Try again.");
            }
            catch (AbandonedMutexException) { }
            try
            {
                foreach (string name in runtimeFiles) PrepareFile(assembly, folder, name);
                foreach (string name in notices) PrepareFile(assembly, folder, name);
            }
            finally { mutex.ReleaseMutex(); }
        }
        return folder;
    }

    private static void PrepareFile(Assembly assembly, string folder, string name)
    {
        string destination = Path.Combine(folder, name);
        using (var embedded = assembly.GetManifestResourceStream("bundle/" + name))
        using (var hash = SHA256.Create())
        {
            if (embedded == null) throw new InvalidDataException("Missing embedded file " + name + ".");
            if (File.Exists(destination))
            {
                using (var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (existing.Length == embedded.Length &&
                        Convert.ToBase64String(hash.ComputeHash(existing)) == Convert.ToBase64String(hash.ComputeHash(embedded))) return;
                }
                embedded.Position = 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    embedded.CopyTo(target);
                if (File.Exists(destination)) File.Replace(staged, destination, null);
                else File.Move(staged, destination);
            }
            finally { File.Delete(staged); }
        }
    }
}
