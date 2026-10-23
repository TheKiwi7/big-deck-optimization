# BaseLib is optional (v0.2.3): two sessions with your mod list edited in settings.save, restored byte for byte after.
#   baselib-late:   this mod moved before BaseLib in the load order (the settings menu registers when BaseLib loads).
#   baselib-absent: BaseLib disabled (every patch applies, bdopt works, no settings menu).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/baselib-optional.ps1
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$settings = Join-Path (Get-SteamAccountDir) "settings.save"
$backup = Join-Path $Results "settings.save.backup"
Copy-Item $settings $backup -Force
$before = (Get-FileHash $settings).Hash

function Write-Settings($data) {
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($settings, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
}

try {
    $data = Get-Content $settings -Raw | ConvertFrom-Json
    $mods = @($data.mod_settings.mod_list)
    $ours = @($mods | Where-Object { $_.id -eq "BigDeckOptimization" })
    $data.mod_settings.mod_list = @($ours + @($mods | Where-Object { $_.id -ne "BigDeckOptimization" }))
    Write-Settings $data
    & (Join-Path $PSScriptRoot "run.ps1") -Name baselib-late -TimeoutSec 240

    $data = Get-Content $backup -Raw | ConvertFrom-Json
    $data.mod_settings.mod_list | Where-Object { $_.id -eq "BaseLib" } | ForEach-Object { $_.is_enabled = $false }
    Write-Settings $data
    & (Join-Path $PSScriptRoot "run.ps1") -Name baselib-absent -TimeoutSec 240
}
finally {
    Copy-Item $backup $settings -Force
    Write-Output "settings.save restored: $($before -eq (Get-FileHash $settings).Hash)"
}
