using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

// Extracts named embedded files ("bundle/<name>") into <root>\Runtime\<build id> and lets the
// WebView2 managed assemblies load from there.
internal static class EmbeddedBundle
{
    internal static string Prepare(Assembly assembly, string root, string mutexPrefix, IEnumerable<string> names)
    {
        string build = assembly.ManifestModule.ModuleVersionId.ToString("N");
        string folder = Path.Combine(root, "Runtime", build);
        using (var mutex = new Mutex(false, @"Local\" + mutexPrefix + build))
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("The launcher files are busy. Try again.");
            }
            catch (AbandonedMutexException) { }
            try
            {
                foreach (string name in names) PrepareFile(assembly, folder, name);
            }
            finally { mutex.ReleaseMutex(); }
        }
        return folder;
    }

    internal static void ResolveWebViewFrom(string folder)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args) {
            string name = new AssemblyName(args.Name).Name;
            if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms")
                return null;
            return Assembly.LoadFrom(Path.Combine(folder, name + ".dll"));
        };
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
