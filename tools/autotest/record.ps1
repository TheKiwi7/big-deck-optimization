# Screen recording of a scenario (real time, so lag shows as it is): runs run.ps1 -Show -Sound (the game fullscreen and
# on top on screen $Screen, with its sound), starts ffmpeg's desktop duplication capture of that screen and
# tools/audiocapture (the game process's audio only) at the scenario's "marker recording start" line, stops both at
# "marker recording stop", then muxes them. Needs ffmpeg (winget install Gyan.FFmpeg). -NoAudio: picture only, muted.
#   powershell -File tools/autotest/record.ps1 -Name video-cascade-15k [-Screen 1] [-Out <file.mp4>]
# $Screen is Godot's screen index and ddagrab's DXGI output index (the same order on a single GPU: check the log line
# "window fullscreen on screen N at (x, y)" against the recording).
param([Parameter(Mandatory)][string]$Name, [int]$Screen = 1, [int]$Fps = 60, [string]$Out = "", [int]$TimeoutSec = 1800, [switch]$NoAudio)
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpeg) { throw "ffmpeg not found (winget install Gyan.FFmpeg)" }
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath("MyVideos")) "$Name.mp4" }
$log = Join-Path $UserData "logs\godot.log"
$video = if ($NoAudio) { $Out } else { [IO.Path]::ChangeExtension($Out, ".video.mp4") }
$audio = [IO.Path]::ChangeExtension($Out, ".wav")
$audioTool = $null
if (-not $NoAudio) {
    $env:PATH = "C:\Program Files\dotnet;$env:PATH"
    $audioProject = Join-Path (Split-Path -Parent $PSScriptRoot) "audiocapture"
    dotnet build $audioProject -c Release -nologo -v q | Out-Null
    $audioTool = Get-ChildItem (Join-Path $audioProject "bin\Release") -Recurse -Filter audiocapture.exe | Select-Object -First 1 -ExpandProperty FullName
    if (-not $audioTool) { throw "tools/audiocapture did not build" }
}

$start = Get-Date
$run = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList "-ExecutionPolicy", "Bypass", "-File",
    "`"$(Join-Path $PSScriptRoot 'run.ps1')`"", "-Name", $Name, "-Show", "-Screen", $Screen, "-TimeoutSec", $TimeoutSec, $(if ($NoAudio) { "" } else { "-Sound" })

function Wait-LogLine([string]$text) {
    while (-not $run.HasExited) {
        # Only this session's log: the previous one holds the same markers.
        if ((Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $start) {
            $content = Get-Content $log -Raw -ErrorAction SilentlyContinue
            if ($content -and $content.Contains($text)) { return $true }
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Start-Piped([string]$exe, [string]$arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $exe
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($info)
}

$recorder = $null
$listener = $null
try {
    if (-not (Wait-LogLine "MARKER recording start")) { throw "the session ended before 'marker recording start'" }
    if ($audioTool) {
        $game = Get-Process -Name SlayTheSpire2 | Select-Object -First 1
        $listener = Start-Piped $audioTool "$($game.Id) `"$audio`""
    }
    $recorder = Start-Piped $ffmpeg "-hide_banner -loglevel error -y -f lavfi -i ddagrab=output_idx=${Screen}:framerate=$Fps -c:v h264_nvenc -preset p5 -cq 20 `"$video`""
    Write-Output "recording screen $Screen$(if ($audioTool) { ' and the game sound' }) to $Out"
    Wait-LogLine "MARKER recording stop" | Out-Null
}
finally {
    if ($recorder -and -not $recorder.HasExited) {
        $recorder.StandardInput.Write("q")  # ffmpeg finishes the file cleanly
        if (-not $recorder.WaitForExit(30000)) { $recorder.Kill() }
    }
    if ($listener -and -not $listener.HasExited) {
        $listener.StandardInput.WriteLine("q")
        if (-not $listener.WaitForExit(30000)) { $listener.Kill() }
    }
    $run.WaitForExit()
}
if (-not (Test-Path $video)) { throw "no recording" }
if (-not $NoAudio) {
    if (-not (Test-Path $audio)) { throw "no audio recorded" }
    & $ffmpeg -hide_banner -loglevel error -y -i $video -i $audio -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest $Out
    if (-not (Test-Path $Out)) { throw "muxing failed (kept $video and $audio)" }
    Remove-Item $video, $audio -Force
    # A silent track means the game was muted (volume setting, mute in background): say so instead of a success.
    $level = & $ffmpeg -hide_banner -i $Out -vn -af volumedetect -f null - 2>&1 | Select-String "max_volume: (-?[\d.]+) dB"
    $peak = if ($level) { [double]$level.Matches[0].Groups[1].Value } else { -999 }
    if ($peak -lt -60) { Write-Warning ("the game sound is silent in the recording (peak {0} dB)" -f $peak) } else { Write-Output ("game sound peak {0} dB" -f $peak) }
}

# It must have filmed the game, not whatever else is on that screen: the game's own screenshot taken 2 s after
# "recording start" (scenario line "screenshot video-check") against the recording's frame at 2 s, both shrunk to
# 32x18 pixels (no need to look at the picture). A mismatch deletes the recording.
$check = Join-Path $Results "video-check.png"
if (Test-Path $check) {
    $tmp = Join-Path $env:TEMP "bdrecord-check"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    & $ffmpeg -hide_banner -loglevel error -y -i $check -vf scale=32:18 -f rawvideo -pix_fmt rgb24 "$tmp\game.rgb"
    & $ffmpeg -hide_banner -loglevel error -y -ss 2 -i $Out -frames:v 1 -vf scale=32:18 -f rawvideo -pix_fmt rgb24 "$tmp\video.rgb"
    $a = [IO.File]::ReadAllBytes("$tmp\game.rgb"); $b = [IO.File]::ReadAllBytes("$tmp\video.rgb")
    $sum = 0; for ($i = 0; $i -lt [Math]::Min($a.Length, $b.Length); $i++) { $sum += [Math]::Abs([int]$a[$i] - [int]$b[$i]) }
    $diff = $sum / [Math]::Max(1, [Math]::Min($a.Length, $b.Length))
    Remove-Item $tmp -Recurse -Force
    Remove-Item $check -Force
    if ($a.Length -ne $b.Length -or $diff -gt 30) {
        Remove-Item $Out -Force
        throw ("the recording doesn't show the game (mean pixel difference {0:F0}/255): deleted" -f $diff)
    }
    Write-Output ("recording shows the game (mean pixel difference {0:F0}/255)" -f $diff)
}
Write-Output ("saved {0} ({1:F1} MB)" -f $Out, ((Get-Item $Out).Length / 1MB))
