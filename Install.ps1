[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoStart
)
# Builds (unless -NoBuild) and deploys the four exes, then restarts the three wallpaper helpers.
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$destination = Join-Path $env:LOCALAPPDATA 'Programs\DevinAnimated'
$helpers = @(
    @{ Exe = 'DevinAnimated.exe';       Product = 'Devin Animated' },
    @{ Exe = 'AntigravityAnimated.exe'; Product = 'Antigravity Animated' },
    @{ Exe = 'CodexAnimated.exe';       Product = 'Codex Animated' }
)
$exes = @($helpers | ForEach-Object { $_.Exe }) + 'ThemeStudio.exe'
$names = $exes | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }

# 1. Build
if (-not $NoBuild) {
    & (Join-Path $package 'source\Build.ps1')
}
foreach ($exe in $exes) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $exe))) { throw "$exe is missing. Run without -NoBuild." }
}

# 2. Stop the helpers and Theme Studio
foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    if ($process.ProcessName -eq 'ThemeStudio') {
        if ($process.CloseMainWindow()) { [void]$process.WaitForExit(3000) }
    }
}
foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
}
$deadline = (Get-Date).AddSeconds(15)
while (@(Get-Process -Name $names -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -gt $deadline) { throw 'The helpers did not exit.' }
    Start-Sleep -Milliseconds 200
}

# 3. Copy and verify
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$summary = @()
foreach ($exe in $exes) {
    $from = Join-Path $package $exe
    $to = Join-Path $destination $exe
    Copy-Item -LiteralPath $from -Destination $to -Force
    $hash = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $hash) { throw "Copy of $exe does not match the build." }
    $summary += [pscustomobject]@{ File = $exe; Sha256 = $hash.Substring(0, 12); Run = ''; Pid = '' }
}

# 4. Regenerable bundle caches
foreach ($helper in $helpers) {
    $runtime = Join-Path $env:LOCALAPPDATA "$($helper.Product)\Runtime"
    if (Test-Path -LiteralPath $runtime) { Remove-Item -LiteralPath (Join-Path $runtime '*') -Recurse -Force }
}

# 5. Logon entries
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($helper in $helpers) {
    $value = '"{0}" --artwork' -f (Join-Path $destination $helper.Exe)
    Set-ItemProperty -Path $runKey -Name ([IO.Path]::GetFileNameWithoutExtension($helper.Exe)) -Value $value
    ($summary | Where-Object { $_.File -eq $helper.Exe }).Run = $value
}

# 6. Start Menu shortcut for Theme Studio
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$studio = Join-Path $destination 'ThemeStudio.exe'
$hasShortcut = $false
foreach ($link in Get-ChildItem -LiteralPath $programs -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
    if ($shell.CreateShortcut($link.FullName).TargetPath -eq $studio) { $hasShortcut = $true; break }
}
if (-not $hasShortcut) {
    $shortcut = $shell.CreateShortcut((Join-Path $programs 'Theme Studio.lnk'))
    $shortcut.TargetPath = $studio
    $shortcut.WorkingDirectory = $destination
    $shortcut.Save()
}

# 7. Restore library themes that are missing
$library = Join-Path $env:LOCALAPPDATA 'Animated Launchers\Themes'
$themeSource = Join-Path $package 'themes'
if (Test-Path -LiteralPath $themeSource) {
    foreach ($theme in Get-ChildItem -LiteralPath $themeSource -Directory) {
        if (-not ((Test-Path -LiteralPath (Join-Path $theme.FullName 'wallpaper.jpg')) -and
                  (Test-Path -LiteralPath (Join-Path $theme.FullName 'theme.ini')))) { continue }
        $target = Join-Path $library $theme.Name
        if (Test-Path -LiteralPath $target) { continue }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($file in 'wallpaper.jpg', 'avatar.jpg', 'theme.ini') {
            $from = Join-Path $theme.FullName $file
            if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $target $file) }
        }
        Write-Output "restored theme $($theme.Name)"
    }
}

# 8. Start the helpers
if (-not $NoStart) {
    foreach ($helper in $helpers) {
        $process = Start-Process -FilePath (Join-Path $destination $helper.Exe) -ArgumentList '--artwork' -PassThru
        ($summary | Where-Object { $_.File -eq $helper.Exe }).Pid = $process.Id
    }
}

# 9. Summary
$summary | Format-List
