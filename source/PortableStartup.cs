using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class PortableStartup
{
    private static readonly string[] runtimeFiles = {
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
        "web/index.html", "web/style.css", "web/animation.js", "web/assets/artwork.jpg",
        "web/assets/avatar.jpg", "web/assets/contours.js",
        "notices/LICENSE.txt", "notices/NOTICE.txt"
    };
    internal static string BundleDirectory { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppTarget.Product);
            BundleDirectory = EmbeddedBundle.Prepare(typeof(PortableStartup).Assembly, root, AppTarget.HelperName + ".Bundle.", runtimeFiles);
            EmbeddedBundle.ResolveWebViewFrom(BundleDirectory);
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                ex is System.Security.SecurityException)) throw;
            MessageBox.Show("Windows could not prepare the launcher files.\n\n" + ex.Message,
                AppTarget.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return RunLauncher(args);
    }

    // Keep WebView2 references outside this entry point until the resolver is ready.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args) { return Launcher.Run(args); }
}
