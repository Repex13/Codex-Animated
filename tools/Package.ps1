[CmdletBinding()]
param(
    [string]$Version,
    [switch]$NoBuild
)
# Builds (unless -NoBuild) and writes dist\Codex-Animated-v<version>-windows-x64.zip plus its .sha256.
# Entries are in a fixed order and dated 2000-01-01, so the same inputs give the same archive.
$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression

if (-not $Version) {
    $launcher = Get-Content -LiteralPath (Join-Path $package 'source\Launcher.cs') -Raw
    if ($launcher -notmatch 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.\d+"\)') { throw 'Could not read AssemblyVersion from source\Launcher.cs.' }
    $Version = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]
}
if (-not $NoBuild) { & (Join-Path $package 'source\Build.ps1') }

$entries = 'DevinAnimated.exe', 'AntigravityAnimated.exe', 'CodexAnimated.exe', 'ThemeStudio.exe',
    'Install.cmd', 'Install.ps1', 'Uninstall.cmd', 'Uninstall.ps1', 'README.md',
    'notices/LICENSE.txt', 'notices/NOTICE.txt'
foreach ($entry in $entries) {
    if (-not (Test-Path -LiteralPath (Join-Path $package ($entry -replace '/', '\')))) { throw "$entry is missing. Run without -NoBuild." }
}

$dist = Join-Path $package 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$name = "Codex-Animated-v$Version-windows-x64.zip"
$zipPath = Join-Path $dist $name
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
$stamp = New-Object DateTimeOffset 2000, 1, 1, 0, 0, 0, ([TimeSpan]::Zero)
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive $stream, ([IO.Compression.ZipArchiveMode]::Create), $false
    try {
        foreach ($entry in $entries) {
            $item = $archive.CreateEntry($entry, [IO.Compression.CompressionLevel]::Optimal)
            $item.LastWriteTime = $stamp
            $target = $item.Open()
            try {
                $bytes = [IO.File]::ReadAllBytes((Join-Path $package ($entry -replace '/', '\')))
                $target.Write($bytes, 0, $bytes.Length)
            }
            finally { $target.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$hash  $name`n", (New-Object Text.UTF8Encoding($false)))
Write-Output "$zipPath"
Write-Output "$hash  $name"
