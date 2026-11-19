$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $package
try {
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:CodexAnimated.exe /win32icon:source\launcher.ico /win32manifest:source\app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll .\source\Launcher.cs .\source\ArtworkOverlay.cs .\source\PreviewArtworkChild.cs
    if ($LASTEXITCODE -ne 0) { throw "The C# build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
