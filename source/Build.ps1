$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $package
try {
    $assets = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll',
        'web/index.html', 'web/style.css', 'web/animation.js', 'web/assets/artwork.jpg', 'notices/LICENSE.txt', 'notices/NOTICE.txt')
    # A portrait-theme installation can keep the standalone public web assets here.
    $portableWeb = Join-Path $PSScriptRoot 'portable-web'
    $resources = foreach ($asset in $assets) {
        $diskFile = $asset
        if ($asset.StartsWith('web/') -and (Test-Path -LiteralPath $portableWeb)) {
            $diskFile = Join-Path $portableWeb $asset.Substring(4)
        }
        '/resource:{0},bundle/{1}' -f $diskFile, $asset
    }
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:CodexAnimated.exe /win32icon:source\launcher.ico /win32manifest:source\app.manifest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll @resources .\source\PortableStartup.cs .\source\ArtworkUpgrade.cs .\source\Launcher.cs .\source\ArtworkOverlay.cs .\source\PreviewArtworkChild.cs .\source\WallpaperSettings.cs
    if ($LASTEXITCODE -ne 0) { throw "The C# build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
