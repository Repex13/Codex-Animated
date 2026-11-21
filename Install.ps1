[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoStart
)
# Installs the executables lying next to this script (building them first when the source is here),
# then restarts the wallpaper helper of each app that is installed.
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$destination = Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'
$oldFolder = Join-Path $env:LOCALAPPDATA 'Programs\DevinAnimated'
$helpers = @(
    @{ App = 'Devin';       Exe = 'DevinAnimated.exe';       Product = 'Devin Animated' },
    @{ App = 'Antigravity'; Exe = 'AntigravityAnimated.exe'; Product = 'Antigravity Animated' },
    @{ App = 'Codex';       Exe = 'CodexAnimated.exe';       Product = 'Codex Animated' },
    @{ App = 'Claude';      Exe = 'ClaudeAnimated.exe';      Product = 'Claude Animated' }
)

# Whether the desktop app is on this machine. ANIMATED_INSTALL_ASSUME (a comma-separated list) overrides this for tests.
function Test-AppInstalled([string]$app) {
    if ($env:ANIMATED_INSTALL_ASSUME) {
        return @($env:ANIMATED_INSTALL_ASSUME -split ',' | ForEach-Object { $_.Trim() }) -contains $app
    }
    if ($app -eq 'Codex') {
        if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages\OpenAI.Codex_2p2nqsd0c76g0')) { return $true }
        try { return $null -ne (Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Select-Object -First 1) } catch { return $false }
    }
    if ($app -eq 'Claude') {
        $claudeRoot = Join-Path $env:LOCALAPPDATA 'AnthropicClaude'
        if (Test-Path -LiteralPath (Join-Path $claudeRoot 'claude.exe')) { return $true }
        if (@(Get-ChildItem -Path (Join-Path $claudeRoot 'app-*\claude.exe') -ErrorAction SilentlyContinue).Count -gt 0) { return $true }
        try {
            $command = (Get-ItemProperty -Path 'HKCU:\Software\Classes\claude\shell\open\command' -ErrorAction Stop).'(default)'
            if ($command -match '^\s*"([^"]+)"' -and (Test-Path -LiteralPath $Matches[1])) { return $true }
        } catch { }
        return $false
    }
    $folder = if ($app -eq 'Devin') { 'Devin' } else { 'Antigravity IDE' }
    $exe = "$folder.exe"
    $roots = @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($root in $roots) { if (Test-Path -LiteralPath (Join-Path $root "$folder\$exe")) { return $true } }
    try {
        $command = (Get-ItemProperty -Path ('HKCU:\Software\Classes\' + $app.ToLowerInvariant() + '\shell\open\command') -ErrorAction Stop).'(default)'
        if ($command -match '^\s*"([^"]+)"' -and (Test-Path -LiteralPath $Matches[1])) { return $true }
    } catch { }
    return $false
}
$exes = @($helpers | ForEach-Object { $_.Exe }) + 'ThemeStudio.exe'
$names = $exes | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }

# 1. Build, when this is a source checkout
$build = Join-Path $package 'source\Build.ps1'
if (-not $NoBuild -and (Test-Path -LiteralPath $build)) { & $build }
foreach ($exe in $exes) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $exe))) {
        throw "$exe is not next to Install.ps1. Extract the whole release zip first, or run this from a source checkout without -NoBuild."
    }
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
    if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $hash) { throw "Copy of $exe does not match the original." }
    Unblock-File -LiteralPath $to
    $summary += [pscustomobject]@{ File = $exe; Sha256 = $hash.Substring(0, 12); Installed = ''; Run = ''; Pid = '' }
}

# 4. Move shortcuts and remove the old install folder (earlier versions installed to Programs\DevinAnimated)
$shell = New-Object -ComObject WScript.Shell
$shortcutRoots = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
function Test-Inside([string]$path, [string]$folder) {
    return $path -and $path.StartsWith($folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if (Test-Path -LiteralPath $oldFolder) {
    foreach ($root in $shortcutRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
            $link = $shell.CreateShortcut($file.FullName)
            if (-not (Test-Inside $link.TargetPath $oldFolder)) { continue }
            $before = $link.TargetPath
            $link.TargetPath = Join-Path $destination (Split-Path -Leaf $link.TargetPath)
            if ((Test-Inside $link.WorkingDirectory $oldFolder) -or ($link.WorkingDirectory.TrimEnd('\') -ieq $oldFolder)) { $link.WorkingDirectory = $destination }
            $icon = $link.IconLocation
            if ($icon) {
                $iconPath = ($icon -split ',')[0]
                if (Test-Inside $iconPath $oldFolder) { $link.IconLocation = (Join-Path $destination (Split-Path -Leaf $iconPath)) + ',' + (($icon -split ',')[1]) }
            }
            $link.Save()
            Write-Output ("shortcut {0}: {1} -> {2}" -f $file.FullName, $before, $link.TargetPath)
        }
    }
    foreach ($exe in $exes) { Remove-Item -LiteralPath (Join-Path $oldFolder $exe) -Force -ErrorAction SilentlyContinue }
    if (@(Get-ChildItem -LiteralPath $oldFolder -Force).Count -eq 0) {
        Remove-Item -LiteralPath $oldFolder
        Write-Output "removed the old install folder $oldFolder"
    }
    else { Write-Output "left $oldFolder in place: it still contains other files" }
}

# 5. Regenerable bundle caches
foreach ($helper in $helpers) {
    $runtime = Join-Path $env:LOCALAPPDATA "$($helper.Product)\Runtime"
    if (Test-Path -LiteralPath $runtime) { Get-ChildItem -LiteralPath $runtime -Force | Remove-Item -Recurse -Force }
}

# 6. Logon entries
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$active = @{}
foreach ($helper in $helpers) {
    $name = [IO.Path]::GetFileNameWithoutExtension($helper.Exe)
    $installed = Test-AppInstalled $helper.App
    $hasValue = $null -ne (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue)
    $row = $summary | Where-Object { $_.File -eq $helper.Exe }
    $row.Installed = if ($installed) { 'yes' } else { 'no' }
    if ($installed -or $hasValue) {
        $value = '"{0}" --artwork' -f (Join-Path $destination $helper.Exe)
        Set-ItemProperty -Path $runKey -Name $name -Value $value
        $row.Run = $value
        $active[$helper.Exe] = $true
    }
    else { Write-Output "skipped $($helper.App): not installed. Turn its helper on later in Theme Studio, Apps and startup." }
}

# 7. Start Menu shortcut for Theme Studio
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
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

# 8. Restore library themes that are missing (only when a themes folder is next to this script)
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

# 9. Start the helpers
if (-not $NoStart) {
    foreach ($helper in $helpers) {
        if (-not $active[$helper.Exe]) { continue }
        $process = Start-Process -FilePath (Join-Path $destination $helper.Exe) -ArgumentList '--artwork' -PassThru
        ($summary | Where-Object { $_.File -eq $helper.Exe }).Pid = $process.Id
    }
}

# 10. Summary
$summary | Format-List
