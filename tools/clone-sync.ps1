# Brings a branch of another clone of this repo into the main tree's working copy, builds it, optionally deploys it to the test
# copies. Never commits.
#
#   clone-sync.ps1 -Clone <path> -Branch <name> [-Base <commit>] [-Force] [-NoBuild] [-Deploy] [-Main <path>]
#
# 1. The branch's diff (Base..Branch, binary-safe; Base defaults to the merge base with the main tree's HEAD) is applied to the main
#    tree's working copy with `git apply --3way` (then unstaged: the changes stay in the working tree, nothing is committed).
#    Refused when the main tree has uncommitted changes to tracked files (a battery or the user may be using them), unless -Force.
# 2. Debug and Release are built with -p:DeployToGame=false (bin\Debug, bin\Release of the main tree).
# 3. -Deploy: Debug is built once more per test copy with that copy's mods\BigDeckOptimization as the deploy folder. Only the two
#    known copies below are ever deployed to (never the Steam install); a running game there locks the DLL ("Deploy skipped").
# 4. The DLL hashes of the builds and of the copies are printed (a battery's checks compare them).
# -Main: the tree to apply to (default: this script's repo; a scratch clone for a test). Exit code 1 on a refusal or a failure.
param([Parameter(Mandatory)][string]$Clone, [Parameter(Mandatory)][string]$Branch, [string]$Base = "", [switch]$Force, [switch]$NoBuild, [switch]$Deploy, [string]$Main = "")
$ErrorActionPreference = "Stop"
$main = if ($Main) { (Resolve-Path $Main).Path } else { Split-Path -Parent $PSScriptRoot }
$copies = @("C:\Games\StS2-public-0.111.1", "C:\Games\StS2-beta-0.113.1")
$project = Join-Path $main "src\BigDeckOptimization\BigDeckOptimization.csproj"
if (Test-Path "C:\Program Files\dotnet") { $env:PATH = "C:\Program Files\dotnet;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Invoke-Git([string]$dir, [string[]]$gitArgs) {
    $ErrorActionPreference = "Continue"  # git's progress notes go to stderr: not errors
    $out = & git -C $dir @gitArgs 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') failed in ${dir}: $($out -join ' | ')" }
    $out
}
function Fail([string]$why) { Write-Output "CLONE-SYNC REFUSED: $why"; exit 1 }

try {
    $Clone = (Resolve-Path $Clone).Path
    # The base: the point the branch left the main tree's history.
    if (-not $Base) {
        Invoke-Git $Clone @("fetch", "-q", $main, "HEAD") | Out-Null
        $Base = (Invoke-Git $Clone @("merge-base", $Branch, "FETCH_HEAD") | Select-Object -First 1).Trim()
    }
    $files = @(Invoke-Git $Clone @("diff", "--name-only", "$Base..$Branch") | Where-Object { $_ })
    if (-not $files.Count) { Fail "$Branch has no change since $Base" }
    $dirty = @(Invoke-Git $main @("status", "--porcelain", "--untracked-files=no") | Where-Object { $_ })
    if ($dirty.Count -and -not $Force) {
        Fail ("the main tree has $($dirty.Count) uncommitted change(s) to tracked files (-Force to go on):`n  " + (($dirty | Select-Object -First 8) -join "`n  "))
    }
    $patch = Join-Path ([IO.Path]::GetTempPath()) "clone-sync-$([guid]::NewGuid().ToString('N').Substring(0, 8)).patch"
    # git writes the patch bytes as they are: a redirection through PowerShell 5.1 would re-encode them.
    & cmd /c "git -C `"$Clone`" diff --binary $Base..$Branch > `"$patch`""
    if ($LASTEXITCODE -ne 0) { throw "git diff failed" }
    try {
        $applied = Invoke-Git $main @("apply", "--3way", "--whitespace=nowarn", $patch)
    } finally { Remove-Item $patch -ErrorAction SilentlyContinue }
    $conflicts = @(Invoke-Git $main @("diff", "--name-only", "--diff-filter=U") | Where-Object { $_ })
    if ($conflicts.Count) { Fail "conflicts in: $($conflicts -join ', ') (resolve them in the main tree, then build by hand)" }
    Invoke-Git $main @("reset", "-q") | Out-Null   # --3way stages what it applied: back to plain working-tree changes
    Write-Output "APPLIED $($files.Count) file(s) of $Branch ($Base..$Branch) to $main"
} catch { Write-Output "CLONE-SYNC FAILED: $($_.Exception.Message)"; exit 1 }

if ($NoBuild) { Write-Output "CLONE-SYNC DONE (not built)"; exit 0 }
function Build([string]$config, [string[]]$extra) {
    $out = & dotnet build $project -c $config @extra 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $errors = @($out | Where-Object { $_ -match ": error " } | Select-Object -Unique)
    if ($code -ne 0) { Write-Output "BUILD FAILED $config (exit $code)"; $errors | Select-Object -First 8 | ForEach-Object { Write-Output "  $_" }; exit 1 }
    Write-Output "BUILD ok $config"
    $out | Where-Object { $_ -match "Deploy skipped" } | Select-Object -First 1 | ForEach-Object { Write-Output "  WARN $_" }
}
Build "Debug" @("-p:DeployToGame=false")
Build "Release" @("-p:DeployToGame=false")
if ($Deploy) {
    foreach ($copy in $copies) {
        if ($copy -match "Steam" -or -not (Test-Path $copy)) { Write-Output "DEPLOY skipped: $copy"; continue }
        Build "Debug" @("-p:ModDeployDir=$copy\mods\BigDeckOptimization")
    }
}
foreach ($place in @(@{ Label = "Debug"; Dir = (Join-Path $main "src\BigDeckOptimization\bin\Debug") }, @{ Label = "Release"; Dir = (Join-Path $main "src\BigDeckOptimization\bin\Release") }) +
    @($copies | ForEach-Object { @{ Label = (Split-Path $_ -Leaf); Dir = (Join-Path $_ "mods\BigDeckOptimization") } })) {
    foreach ($dll in "BigDeckOptimization.dll", "BigDeckOptimization.Menu.dll") {
        $path = Join-Path $place.Dir $dll
        if (Test-Path $path) { Write-Output ("HASH {0,-24} {1,-30} {2}" -f $place.Label, $dll, (Get-FileHash $path).Hash.Substring(0, 12)) }
    }
}
Write-Output "CLONE-SYNC DONE"
