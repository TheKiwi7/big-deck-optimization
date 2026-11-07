# Daily check of the game, BaseLib and Limitless versions (check_versions.py, published builds included): a Windows
# notification when one differs from what the mod was verified with. Runs as the current user, no admin rights.
#   powershell -ExecutionPolicy Bypass -File tools/version_task.ps1 -Install [-At 12:00]   registers the scheduled task
#   powershell -ExecutionPolicy Bypass -File tools/version_task.ps1 -Remove
#   powershell -ExecutionPolicy Bypass -File tools/version_task.ps1 [-TestNotification]    one check now
# Last result: %LOCALAPPDATA%\BigDeckOptimization\version-check.log
param([switch]$Install, [switch]$Remove, [string]$At = "12:00", [switch]$TestNotification, [string]$Python = "")
$TaskName = "BigDeckOptimization version check"
$Root = Split-Path -Parent $PSScriptRoot

function Show-Notification([string]$title, [string]$text) {
    [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
    $xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
    $escape = { param($s) [Security.SecurityElement]::Escape($s) }
    $xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$(& $escape $title)</text><text>$(& $escape $text)</text></binding></visual></toast>")
    # Windows PowerShell's own application id: no registration needed.
    $app = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
}

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Output "removed: $TaskName"
    exit 0
}
if ($Install) {
    $py = if ($Python) { $Python } else { (Get-Command python).Source }
    # -WindowStyle Hidden alone still flashes a console before PowerShell hides it; a headless conhost never shows one
    # (Python and the toast notification work the same).
    $action = New-ScheduledTaskAction -Execute "conhost.exe" -WorkingDirectory $Root -Argument "--headless powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Python `"$py`""
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Write-Output "registered: $TaskName, every day at $At (missed: at the next chance)"
    exit 0
}
$py = if ($Python) { $Python } else { "python" }
$dir = Join-Path $env:LOCALAPPDATA "BigDeckOptimization"
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Location $Root
$output = & $py -I (Join-Path $Root "tools\check_versions.py") 2>&1 | ForEach-Object { $_.ToString() }
$code = $LASTEXITCODE
$found = @($output | Where-Object { $_ -match "CHANGED|PUBLISHED UPDATE NOT VERIFIED" })
(@("$(Get-Date -Format 'yyyy-MM-dd HH:mm') exit $code") + $output) | Set-Content (Join-Path $dir "version-check.log") -Encoding utf8
if ($TestNotification) { Show-Notification "Big Deck Optimization" "Test: this is how a version change is announced. Current result: $(if ($found.Count) { "$($found.Count) change(s)" } else { "nothing changed" })." }
elseif ($found.Count) { Show-Notification "Big Deck Optimization: version change" (($found | ForEach-Object { ($_ -replace '\s+', ' ').Trim() }) -join "; ") }
Write-Output ("exit {0}, {1} change(s)" -f $code, $found.Count)
$found | ForEach-Object { Write-Output $_ }
