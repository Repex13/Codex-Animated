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
    $sources = '.\source\PortableStartup.cs', '.\source\ArtworkUpgrade.cs', '.\source\Launcher.cs',
        '.\source\ArtworkOverlay.cs', '.\source\PreviewArtworkChild.cs', '.\source\WallpaperSettings.cs',
        '.\source\AppTarget.cs'
    $manifestTemplate = Get-Content 'source\app.manifest' -Raw
    $targets = @(
        @{ Define = 'APP_DEVIN';       Out = 'DevinAnimated.exe';       Identity = 'DevinAnimated';       Icon = 'source\launcher.ico' },
        @{ Define = 'APP_ANTIGRAVITY'; Out = 'AntigravityAnimated.exe'; Identity = 'AntigravityAnimated'; Icon = 'source\launcher.ico' },
        @{ Define = 'APP_CODEX';       Out = 'CodexAnimated.exe';       Identity = 'CodexAnimated';       Icon = 'source\codex.ico' }
    )
    foreach ($target in $targets) {
        $manifest = Join-Path $env:TEMP ($target.Identity + '.manifest')
        Set-Content -LiteralPath $manifest -Value ($manifestTemplate -replace 'name="DevinAnimated"', ('name="' + $target.Identity + '"'))
        & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:$($target.Out) /define:$($target.Define) /win32icon:$($target.Icon) /win32manifest:$manifest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll @resources $sources
        if ($LASTEXITCODE -ne 0) { throw "The $($target.Out) build failed with exit code $LASTEXITCODE." }
        Remove-Item -LiteralPath $manifest -ErrorAction SilentlyContinue
    }
    $studioManifest = Join-Path $env:TEMP 'ThemeStudio.manifest'
    Set-Content -LiteralPath $studioManifest -Value ($manifestTemplate -replace 'name="DevinAnimated"', 'name="ThemeStudio"')
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:ThemeStudio.exe /win32icon:source\launcher.ico /win32manifest:$studioManifest /r:System.Windows.Forms.dll /r:System.Drawing.dll .\source\ThemeStudio.cs
    if ($LASTEXITCODE -ne 0) { throw "The ThemeStudio.exe build failed with exit code $LASTEXITCODE." }
    Remove-Item -LiteralPath $studioManifest -ErrorAction SilentlyContinue
} finally {
    Pop-Location
}
