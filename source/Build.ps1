$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $package
try {
    $assets = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll',
        'web/index.html', 'web/style.css', 'web/animation.js', 'web/assets/artwork.jpg', 'web/assets/avatar.jpg',
        'web/assets/contours.js', 'notices/LICENSE.txt', 'notices/NOTICE.txt')
    $resources = foreach ($asset in $assets) {
        '/resource:{0},bundle/{0}' -f $asset
    }
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:CodexAnimated.exe /win32icon:source\launcher.ico /win32manifest:source\app.manifest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll @resources .\source\PortableStartup.cs .\source\ArtworkUpgrade.cs .\source\Launcher.cs .\source\ArtworkOverlay.cs .\source\PreviewArtworkChild.cs .\source\WallpaperSettings.cs
    if ($LASTEXITCODE -ne 0) { throw "The C# build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
