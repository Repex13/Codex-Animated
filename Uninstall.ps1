[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$RemoveData
)
# Removes the install: stops the helpers, drops the logon entries, retargets or deletes
# shortcuts that point into an install folder, then removes the folders.
$ErrorActionPreference = 'Stop'
$installs = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'),
    (Join-Path $env:LOCALAPPDATA 'Programs\DevinAnimated'))
$names = 'DevinAnimated', 'AntigravityAnimated', 'CodexAnimated', 'ThemeStudio'
$native = @{
    'DevinAnimated.exe'       = Join-Path $env:LOCALAPPDATA 'Programs\Devin\Devin.exe'
    'AntigravityAnimated.exe' = Join-Path $env:LOCALAPPDATA 'Programs\Antigravity IDE\Antigravity IDE.exe'
}
function Test-Inside([string]$path) {
    foreach ($folder in $installs) {
        if ($path -and $path.StartsWith($folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess("$($process.ProcessName) ($($process.Id))", 'stop process')) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { Write-Warning $_.Exception.Message }
    }
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($name in 'DevinAnimated', 'AntigravityAnimated', 'CodexAnimated') {
    if ($null -ne (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess("$runKey\$name", 'remove Run value')) {
            Remove-ItemProperty -Path $runKey -Name $name
        }
    }
}

$shell = New-Object -ComObject WScript.Shell
$roots = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
        $link = $shell.CreateShortcut($file.FullName)
        if (-not (Test-Inside $link.TargetPath)) { continue }
        $replacement = $native[[IO.Path]::GetFileName($link.TargetPath)]
        if ($replacement -and (Test-Path -LiteralPath $replacement)) {
            if ($PSCmdlet.ShouldProcess($file.FullName, "retarget to $replacement")) {
                $link.TargetPath = $replacement
                $link.Arguments = ''
                $link.WorkingDirectory = Split-Path -Parent $replacement
                $link.Save()
            }
        }
        elseif ($PSCmdlet.ShouldProcess($file.FullName, 'delete shortcut')) {
            Remove-Item -LiteralPath $file.FullName
        }
    }
}

foreach ($install in $installs) {
    if ((Test-Path -LiteralPath $install) -and $PSCmdlet.ShouldProcess($install, 'remove install folder')) {
        Remove-Item -LiteralPath $install -Recurse -Force
    }
}

if ($RemoveData) {
    $folders = 'Devin Animated', 'Antigravity Animated', 'Codex Animated', 'Animated Launchers' |
        ForEach-Object { Join-Path $env:LOCALAPPDATA $_ }
    foreach ($folder in $folders) {
        if ((Test-Path -LiteralPath $folder) -and $PSCmdlet.ShouldProcess($folder, 'remove data folder')) {
            Remove-Item -LiteralPath $folder -Recurse -Force
        }
    }
}
