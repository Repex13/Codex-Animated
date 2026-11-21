$ErrorActionPreference = 'Stop'
# Mirrors the shared theme library into the repo's themes\ folder so Install.ps1 can restore it.
$package = Split-Path -Parent $PSScriptRoot
$library = Join-Path $env:LOCALAPPDATA 'Animated Launchers\Themes'
if (-not (Test-Path -LiteralPath $library)) { throw "No theme library at $library." }
foreach ($folder in Get-ChildItem -LiteralPath $library -Directory) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder.FullName 'wallpaper.jpg'))) { continue }
    $target = Join-Path $package "themes\$($folder.Name)"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in 'wallpaper.jpg', 'avatar.jpg', 'theme.ini') {
        $from = Join-Path $folder.FullName $file
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $target $file) -Force }
        else { Remove-Item -LiteralPath (Join-Path $target $file) -ErrorAction SilentlyContinue }
    }
    Write-Output "exported $($folder.Name)"
}
