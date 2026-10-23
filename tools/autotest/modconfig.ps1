# ModConfig integration check: enables ModConfig for two sessions (modconfig-1: change a setting, modconfig-2: it is
# applied after a restart), then restores settings.save byte for byte and removes the test ModConfig file.
#   powershell -File tools/autotest/modconfig.ps1
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$settings = Join-Path (Get-SteamAccountDir) "settings.save"
$backup = Join-Path $Results "settings.save.backup"
$ourConfig = Join-Path $UserData "ModConfig\BigDeckOptimization.json"
$hadConfig = Test-Path $ourConfig
Copy-Item $settings $backup -Force
$before = (Get-FileHash $settings).Hash
try {
    $data = Get-Content $settings -Raw | ConvertFrom-Json
    $modConfig = $data.mod_settings.mod_list | Where-Object { $_.id -eq "ModConfig" }
    if (-not $modConfig) { throw "ModConfig is not installed" }
    $modConfig.is_enabled = $true
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($settings, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
    & (Join-Path $PSScriptRoot "run.ps1") -Name modconfig-1 -TimeoutSec 300
    if (Test-Path $ourConfig) { Write-Output "--- saved after modconfig-1:"; Get-Content $ourConfig }
    & (Join-Path $PSScriptRoot "run.ps1") -Name modconfig-2 -TimeoutSec 300
    if (Test-Path $ourConfig) { Write-Output "--- saved after modconfig-2:"; Get-Content $ourConfig }
}
finally {
    Copy-Item $backup $settings -Force
    Write-Output "settings.save restored: $($before -eq (Get-FileHash $settings).Hash)"
    if (-not $hadConfig -and (Test-Path $ourConfig)) { Remove-Item $ourConfig; Write-Output "removed the test ModConfig file" }
}
