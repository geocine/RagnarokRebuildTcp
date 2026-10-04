<#
.SYNOPSIS
    Ragnarok Rebuild fork helper. Run `rr help` (via rr.cmd in the repo root) for the command list.

.DESCRIPTION
    Wraps everything needed to set up, run, build and maintain this fork: git remotes and hooks,
    the Unity CLI and editor, the client data pack, server build/run, headless Unity import,
    lighting, player builds, disk reporting and cleanup. Every command is idempotent.
    Machine specific paths live in setup/config.local.json (gitignored).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string]$Command = "help",
    [Parameter(Position = 1)] [string]$Target,
    [switch]$Force,
    [switch]$KeepDlls,
    [switch]$Push,
    [switch]$Yes,
    [switch]$NoLibrary,
    [switch]$NoBuild,
    [string]$Name = "latest"
)

$ErrorActionPreference = "Stop"
$SetupDir = $PSScriptRoot
$RepoRoot = Split-Path $SetupDir -Parent
$ClientDir = Join-Path $RepoRoot "RebuildClient"
$ServerRoot = Join-Path $RepoRoot "RoRebuildServer"
$ServerProjectDir = Join-Path $ServerRoot "RoRebuildServer"
$MapsCsv = Join-Path $ServerRoot "GameConfig\ServerData\Db\Maps.csv"
$PackTool = Join-Path $SetupDir "tools\RebuildPack"
$PackToolExe = Join-Path $PackTool "bin\out\RebuildPack.exe"
$ForkFingerprint = Join-Path $SetupDir "pack\fingerprint.tsv"
$ForkOverrides = Join-Path $SetupDir "fork-overrides.txt"
$UnityLogDir = Join-Path $ClientDir "Logs"
$AutomationClass = "Assets.Editor.Automation.RebuildAutomation"

# ---------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "    ok  $msg" -ForegroundColor Green }
function Write-Warn2([string]$msg) { Write-Host "    !!  $msg" -ForegroundColor Yellow }
function Fail([string]$msg) { throw "rr: $msg" }

function Read-Config {
    $cfg = @{}
    foreach ($file in @((Join-Path $SetupDir "config.json"), (Join-Path $SetupDir "config.local.json"))) {
        if (Test-Path $file) {
            $json = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $json.PSObject.Properties) { $cfg[$p.Name] = $p.Value }
        }
    }
    # Everything large lives under workDir unless a key overrides it.
    if ($cfg.workDir) {
        $defaults = @{
            packDir = "pack"; libraryDir = "Library"; customDir = "custom"
            referenceWalk = "reference\walkdata"; buildOutput = "Build\PC"
        }
        foreach ($k in $defaults.Keys) {
            if (-not $cfg[$k] -or ($k -eq "buildOutput" -and $cfg[$k] -eq "RebuildClient/Build/PC")) { $cfg[$k] = Join-Path $cfg.workDir $defaults[$k] }
        }
    }
    return $cfg
}

function Get-PackDataDir {
    $cfg = Read-Config
    if ($cfg.packDir -and (Test-Path -LiteralPath (Join-Path $cfg.packDir "manifest.json"))) { return Join-Path $cfg.packDir "data" }
    return $null
}

function Save-LocalConfig([hashtable]$values) {
    $file = Join-Path $SetupDir "config.local.json"
    $existing = @{}
    if (Test-Path $file) {
        $json = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $json.PSObject.Properties) { $existing[$p.Name] = $p.Value }
    }
    foreach ($k in $values.Keys) { $existing[$k] = $values[$k] }
    Write-TextAtomic $file ($existing | ConvertTo-Json -Depth 5)
}

function Resolve-RepoPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $path))
}

# Native tools print progress on stderr; Windows PowerShell 5.1 turns redirected stderr into
# terminating errors under "Stop", so native calls go through these two helpers.
function Invoke-Native([string]$exe, [string[]]$arguments, [switch]$Quiet, [switch]$AllowFail) {
    $ErrorActionPreference = "Continue"
    if ($Quiet) {
        $out = & $exe @arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        if ($code -ne 0 -and -not $AllowFail) {
            $out | Select-Object -Last 40 | ForEach-Object { Write-Host "    | $_" }
        }
    } else {
        & $exe @arguments | Out-Host
        $code = $LASTEXITCODE
    }
    if ($code -ne 0 -and -not $AllowFail) { Fail "$([System.IO.Path]::GetFileName($exe)) $($arguments -join ' ') failed with exit code $code" }
    return $code
}

function Get-NativeOutput([string]$exe, [string[]]$arguments) {
    $ErrorActionPreference = "Continue"
    $out = & $exe @arguments 2>&1 | ForEach-Object { "$_" }
    $script:NativeExit = $LASTEXITCODE
    return ($out -join "`n")
}

function Invoke-Git([string[]]$arguments, [switch]$AllowFail) {
    $ErrorActionPreference = "Continue"
    $all = & git -C $RepoRoot @arguments 2>&1
    $script:NativeExit = $LASTEXITCODE
    $stdout = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $stderr = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    if ($script:NativeExit -ne 0 -and -not $AllowFail) { Fail "git $($arguments -join ' ') failed:`n$(($stdout + $stderr) -join "`n")" }
    if ($script:NativeExit -ne 0) { return ($stdout + $stderr) }
    return $stdout
}

function Test-Command([string]$name) { return [bool](Get-Command $name -ErrorAction SilentlyContinue) }

function Get-FolderBytes([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($sum) { return [long]$sum } else { return 0 }
}

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return "{0:N2} GB" -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return "{0:N0} MB" -f ($bytes / 1MB) }
    return "{0:N0} KB" -f ($bytes / 1KB)
}

function Get-FreeBytes([string]$path) {
    $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($path))
    try { return (New-Object System.IO.DriveInfo($root)).AvailableFreeSpace } catch { return -1 }
}

function Confirm-Action([string]$question, [switch]$DefaultYes) {
    if ($Yes -or $Force) { return $true }
    if ($DefaultYes) { return (Read-Host "$question [Y/n]") -notmatch '^(n|no)$' }
    $answer = Read-Host "$question [y/N]"
    return $answer -match '^(y|yes)$'
}

# ---------------------------------------------------------------------------------------------
# durability
#
# Any command may be cut off (Ctrl+C, a closed terminal, a crash, a power cut) and simply rerun.
# Files are written to "<name>.rr.tmp", flushed and renamed into place, so nothing ever reads half
# a file (Unity ignores *.tmp). Steps that cannot be atomic record themselves in
# .git\info\rr-state before they start; the next rr command finishes, undoes or reports them.
# ---------------------------------------------------------------------------------------------

$StateDir = Join-Path $RepoRoot ".git\info\rr-state"
$TempSuffix = ".rr.tmp"

function Move-FileOver([string]$from, [string]$to) {
    if (Test-Path -LiteralPath $to -PathType Leaf) { [System.IO.File]::Replace($from, $to, [NullString]::Value) }
    else { [System.IO.File]::Move($from, $to) }
}

function Write-TextAtomic([string]$path, [string]$text) {
    $dir = Split-Path $path
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $temp = $path + $TempSuffix
    $bytes = (New-Object System.Text.UTF8Encoding $false).GetBytes($text)
    $fs = New-Object System.IO.FileStream($temp, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try { $fs.Write($bytes, 0, $bytes.Length); $fs.Flush($true) } finally { $fs.Dispose() }
    Move-FileOver $temp $path
}

function Write-LinesAtomic([string]$path, [string[]]$lines) {
    Write-TextAtomic $path ((@($lines) | ForEach-Object { "$_`n" }) -join "")
}

function Copy-FileAtomic([string]$from, [string]$to) {
    $dir = Split-Path $to
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $temp = $to + $TempSuffix
    [System.IO.File]::Copy($from, $temp, $true)
    $fs = [System.IO.File]::Open($temp, "Open", "ReadWrite", "None")
    try { $fs.Flush($true) } finally { $fs.Dispose() }
    Move-FileOver $temp $to
}

function Get-ProcessStamp([int]$id) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    try { return $p.StartTime.ToUniversalTime().Ticks } catch { return $null }
}

# Process ids are reused, so an owner is identified by its id and start time together.
$MyStamp = Get-ProcessStamp $PID

function Get-StatePath([string]$name) { return Join-Path $StateDir "$name.json" }

function Write-State([string]$name, [hashtable]$value) {
    $value.ownerPid = $PID
    $value.ownerStamp = $MyStamp
    Write-TextAtomic (Get-StatePath $name) ($value | ConvertTo-Json -Depth 5)
}

function Read-State([string]$name) {
    $path = Get-StatePath $name
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Clear-State([string]$name) { Remove-Item -LiteralPath (Get-StatePath $name) -Force -ErrorAction SilentlyContinue }

# True while the rr that wrote $state is still running (this one included).
function Test-OwnerAlive($state) {
    return [bool]$state -and (Get-ProcessStamp $state.ownerPid) -eq [long]$state.ownerStamp
}

# The other rr holding the lock, if it is still alive (a killed one leaves a stale file behind).
function Get-LockOwner {
    $lock = Read-State "lock"
    if ($lock -and $lock.ownerPid -ne $PID -and (Test-OwnerAlive $lock)) { return $lock }
    return $null
}

# One state-changing rr at a time; two would race over the same files.
function Enter-RrLock {
    $owner = Get-LockOwner
    if ($owner) { Fail "'rr $($owner.command)' is already running (pid $($owner.ownerPid), since $($owner.since)). Wait for it or stop it first." }
    Write-State "lock" @{ command = $Command; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
    # Two rr started together both find the lock free; only the last writer keeps it.
    Start-Sleep -Milliseconds 200
    $lock = Read-State "lock"
    if ($lock -and $lock.ownerPid -ne $PID) { Fail "'rr $($lock.command)' started at the same time (pid $($lock.ownerPid)); run one at a time." }
    $script:HoldsLock = $true
}

function Exit-RrLock {
    if (-not $script:HoldsLock) { return }
    $lock = Read-State "lock"
    if ($lock -and $lock.ownerPid -eq $PID) { Clear-State "lock" }
    $script:HoldsLock = $false
}

# ---------------------------------------------------------------------------------------------
# Unity CLI / editor
# ---------------------------------------------------------------------------------------------

function Get-UnityVersion {
    $line = Get-Content (Join-Path $ClientDir "ProjectSettings\ProjectVersion.txt") | Where-Object { $_ -like "m_EditorVersion:*" } | Select-Object -First 1
    return ($line -split ":\s*", 2)[1].Trim()
}

# Adds what installers put on PATH since this terminal opened, keeping this session's own entries.
function Update-SessionPath {
    $current = @($env:Path -split ';' | Where-Object { $_ })
    $saved = @(([Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")) -split ';' | Where-Object { $_ })
    $env:Path = (@($current) + @($saved | Where-Object { $current -notcontains $_ })) -join ';'
}

function Get-UnityCli {
    Update-SessionPath
    $cmd = Get-Command unity -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -like "*\WindowsApps\*") {
        # The winget/MSIX build runs editor installers inside the package container, where they fail.
        Write-Warn2 "Unity CLI is the MSIX (winget) build; editor installs fail from its sandbox. Run 'rr install-unity' to replace it."
    }
    if ($cmd) { return $cmd.Source }
    return $null
}

function Install-UnityCli {
    $existing = Get-UnityCli
    if ($existing -and $existing -notlike "*\WindowsApps\*") { Write-Ok "Unity CLI: $existing"; return $existing }

    if ($existing -and (Test-Command winget)) {
        Write-Step "Removing MSIX Unity CLI (winget) in favour of the standalone build"
        & winget uninstall --id Unity.CLI -e --silent | Out-Null
    }

    Write-Step "Installing the Unity CLI (https://unity.com/install.ps1)"
    $script = Join-Path $env:TEMP "unity-cli-install.ps1"
    Invoke-WebRequest "https://unity.com/install.ps1" -UseBasicParsing -OutFile $script
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script
    if ($LASTEXITCODE -ne 0) { Fail "Unity CLI install script failed" }
    Remove-Item $script -Force -ErrorAction SilentlyContinue

    $cli = Get-UnityCli
    if (-not $cli) { Fail "Unity CLI still not found on PATH after install; open a new terminal and retry." }
    Write-Ok "Unity CLI: $cli"
    return $cli
}

function Get-UnityEditorPath {
    $version = Get-UnityVersion
    $cli = Get-UnityCli
    if ($cli) {
        $json = Get-NativeOutput $cli @("editors", "-i", "--format", "json", "--non-interactive")
        if ($script:NativeExit -eq 0 -and $json) {
            try {
                foreach ($e in @(($json | ConvertFrom-Json).data)) {
                    if ($e.version -eq $version -and $e.path) { return $e.path }
                }
            } catch { }
        }
    }
    $fallback = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (Test-Path $fallback) { return $fallback }
    return $null
}

# ProjectSettings pins the Standalone scripting backend; 1 is IL2CPP, which needs the Windows IL2CPP
# module and an MSVC toolchain on top of the editor (Mono player support ships with it).
function Test-ProjectUsesIl2cpp {
    $settings = Get-Content (Join-Path $ClientDir "ProjectSettings\ProjectSettings.asset") -Raw
    return $settings -match 'scriptingBackend:\s*\r?\n\s*Standalone:\s*1'
}

# A GRF written by 'rr pack' carries the whole pack plus Doddler's walk data.
function Test-PackGrf($path) {
    return (Test-Path -LiteralPath $path -PathType Leaf) -and ((Split-Path $path -Leaf) -like "*rebuild-pack*.grf")
}

function Get-PackGrfSource($cfg) {
    return @($cfg.packSources) | Where-Object { $_ -and (Test-PackGrf $_.path) } | Select-Object -First 1
}

function Test-Il2cppSupport($editorPath) {
    if (-not $editorPath) { return $false }
    return Test-Path (Join-Path (Split-Path $editorPath) "Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_nondevelopment_il2cpp")
}

function Test-CppToolchain {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $false }
    return [bool](& $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
}

# The server and tools target net9.0. A newer SDK builds them, but running them takes the .NET 9
# runtimes, which come with the 9 SDK.
function Test-Dotnet9 {
    if (-not (Test-Command dotnet)) { return $false }
    $sdks = Get-NativeOutput "dotnet" @("--list-sdks")
    $runtimes = Get-NativeOutput "dotnet" @("--list-runtimes")
    return ($sdks -match '(?m)^(9|[1-9]\d)\.') -and ($runtimes -match '(?m)^Microsoft\.NETCore\.App 9\.') -and ($runtimes -match '(?m)^Microsoft\.AspNetCore\.App 9\.')
}

function Get-SevenZip {
    $onPath = Get-Command 7z -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}

# ---------------------------------------------------------------------------------------------
# prerequisites
# ---------------------------------------------------------------------------------------------

# Everything rr runs besides PowerShell and the Unity pieces 'rr install-unity' handles. winget
# installs them machine wide, so Windows asks for administrator approval for each.
$Tools = [ordered]@{
    git = @{ Label = "Git"; Package = "Git for Windows"; Id = "Git.Git"; Url = "https://git-scm.com/download/win"; Test = { Test-Command git } }
    dotnet = @{ Label = ".NET 9 SDK"; Package = ".NET 9 SDK"; Id = "Microsoft.DotNet.SDK.9"; Url = "https://dotnet.microsoft.com/download/dotnet/9.0"; Test = { Test-Dotnet9 } }
    sevenzip = @{ Label = "7-Zip"; Package = "7-Zip"; Id = "7zip.7zip"; Url = "https://www.7-zip.org"; Test = { [bool](Get-SevenZip) } }
    buildtools = @{
        Label = "MSVC C++ toolchain"; Package = "Visual Studio 2022 Build Tools, C++ workload"; Size = "~7 GB"
        Id = "Microsoft.VisualStudio.2022.BuildTools"; Url = "https://visualstudio.microsoft.com/visual-cpp-build-tools/"; Test = { Test-CppToolchain }
        # Replaces winget's installer arguments, so it has to ask for waiting and no prompts itself.
        Override = "--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
    }
}

$ToolsFound = @{}
function Test-Tool([string]$name) {
    if (-not $ToolsFound[$name]) { $ToolsFound[$name] = [bool](& $Tools[$name].Test) }
    return $ToolsFound[$name]
}

function Get-ToolInstallCommand([string]$name) {
    $t = $Tools[$name]
    $line = "winget install --id $($t.Id) -e"
    if ($t.Override) { $line += " --override `"$($t.Override)`"" }
    return $line
}

function Format-ToolList([string[]]$names) { return ($names | ForEach-Object { $Tools[$_].Label }) -join ", " }

# Installs with winget and reloads PATH so this terminal can use the tools right away. Returns
# the ones still missing.
function Install-Tools([string[]]$names) {
    if (-not (Test-Command winget)) {
        Write-Warn2 "winget is missing. Install 'App Installer' from the Microsoft Store (ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1) and rerun, or install these yourself:"
        foreach ($n in $names) { Write-Warn2 "  $($Tools[$n].Package): $($Tools[$n].Url)" }
        return $names
    }
    foreach ($n in $names) {
        $t = $Tools[$n]
        Write-Step "Installing $($t.Package) (winget $($t.Id))"
        $wingetArgs = @("install", "--id", $t.Id, "-e", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements")
        if ($t.Override) { $wingetArgs += @("--override", $t.Override) } else { $wingetArgs += "--silent" }
        $code = Invoke-Native "winget" $wingetArgs -AllowFail
        Update-SessionPath
        if (Test-Tool $n) { Write-Ok "$($t.Label) ready" } else { Write-Warn2 "$($t.Label) is still missing (winget exit $code)" }
    }
    return @($names | Where-Object { -not (Test-Tool $_) })
}

# Each command checks its own tools, so a missing one is offered for install instead of failing
# with "not recognized".
function Assert-Tools([string[]]$names, [string]$purpose) {
    $missing = @($names | Where-Object { -not (Test-Tool $_) })
    if ($missing.Count -eq 0) { return }
    # Installed from another window after this one opened.
    Update-SessionPath
    $missing = @($missing | Where-Object { -not (Test-Tool $_) })
    if ($missing.Count -eq 0) { return }
    if (Confirm-Action "$purpose needs $(Format-ToolList $missing). Install with winget now?" -DefaultYes) { $missing = @(Install-Tools $missing) }
    if ($missing.Count -gt 0) {
        Fail "$purpose needs $(Format-ToolList $missing). Install: $(($missing | ForEach-Object { Get-ToolInstallCommand $_ }) -join '; ')"
    }
}

# Same order as Invoke-Reference: walk data already there, then the archive, which needs 7-Zip.
function Test-NeedsSevenZip($cfg) {
    if ($cfg.referenceWalk -and @(Get-ChildItem -LiteralPath $cfg.referenceWalk -Filter *.walk -ErrorAction SilentlyContinue).Count -gt 0) { return $false }
    return [bool]($cfg.releaseArchive -and (Test-Path -LiteralPath $cfg.releaseArchive))
}

# Your commits and the merges 'rr sync' makes need a git identity.
function Confirm-GitIdentity {
    $name = "$(Invoke-Git @("config", "user.name") -AllowFail)".Trim()
    $email = "$(Invoke-Git @("config", "user.email") -AllowFail)".Trim()
    if ($name -and $email) { Write-Ok "git identity: $name <$email>"; return }
    if ($Yes) { Write-Warn2 "git has no user.name/user.email; set them before committing: git config --global user.name `"...`""; return }
    Write-Host "    Git needs a name and email for your commits and the merges 'rr sync' makes (saved in your global git config)."
    if (-not $name) { $name = (Read-Host "    Name (blank to skip)").Trim(); if ($name) { Invoke-Git @("config", "--global", "user.name", $name) | Out-Null } }
    if (-not $email) { $email = (Read-Host "    Email (blank to skip)").Trim(); if ($email) { Invoke-Git @("config", "--global", "user.email", $email) | Out-Null } }
}

# Space the remaining setup steps take, from a full setup of this project: the editor with IL2CPP
# 8.6 GB (plus ~4 GB of installers, deleted afterwards), imported assets 10 GB in the repo, the
# Library ~10 GB and the pack 3 GB. Player builds later add ~20 GB of build cache to the Library.
function Test-SetupSpace($cfg, [string[]]$installing, [bool]$installingEditor) {
    $needs = [ordered]@{}
    $add = {
        param([string]$path, [long]$bytes)
        $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($path))
        $needs[$root] = [long]$needs[$root] + $bytes
    }
    if ($installingEditor) { & $add $env:ProgramFiles 13GB }
    if ($installing -contains "buildtools") { & $add $env:ProgramFiles 7GB }
    if ($installing -contains "dotnet") { & $add $env:ProgramFiles 1GB }
    if (@(Get-ChildItem -LiteralPath (Join-Path $ClientDir "Assets\Scenes\Maps") -Filter *.unity -ErrorAction SilentlyContinue).Count -lt 100) {
        & $add $ClientDir 11GB
        & $add $(if ($cfg.libraryDir) { $cfg.libraryDir } else { $ClientDir }) 10GB
    }
    if ($cfg.packDir -and -not (Test-Path -LiteralPath (Join-Path $cfg.packDir "manifest.json"))) { & $add $cfg.packDir 4GB }
    $roomy = $true
    foreach ($root in $needs.Keys) {
        $free = Get-FreeBytes $root
        if ($free -lt 0) { continue }
        $line = "$root $(Format-Size $free) free, the rest of setup takes about $(Format-Size $needs[$root])"
        if ($free -ge $needs[$root]) { Write-Ok $line } else { Write-Warn2 $line; $roomy = $false }
    }
    return $roomy
}

# One pass over everything setup needs: what is there, what winget installs after one question,
# and whether the drives have room. 'rr setup' runs it first; 'rr prereqs' runs it on its own.
function Invoke-Prereqs {
    $cfg = Read-Config
    Write-Step "Prerequisites"
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19043) { Write-Warn2 "Unity 6 needs Windows 10 21H1 (build 19043) or Windows 11; this is build $build" }

    $uses = [ordered]@{ git = "the fork and upstream syncing"; dotnet = "the server, update-client and the pack tool" }
    if (Test-NeedsSevenZip $cfg) { $uses.sevenzip = "reading Doddler's release archive (releaseArchive)" }
    if (Test-ProjectUsesIl2cpp) { $uses.buildtools = "IL2CPP player builds (rr build-client, rr smoke); playing in the editor works without it" }
    $required = @(); $optional = @()
    foreach ($n in $uses.Keys) {
        if (Test-Tool $n) { Write-Ok $Tools[$n].Label; continue }
        if ($n -eq "buildtools") { $optional += $n; Write-Warn2 "$($Tools[$n].Label) missing (optional), needed for $($uses[$n])" }
        else { $required += $n; Write-Warn2 "$($Tools[$n].Label) missing, needed for $($uses[$n])" }
    }
    $unity = Get-UnityVersion
    $editor = Get-UnityEditorPath
    if ($editor) { Write-Ok "Unity $unity" }
    else { Write-Host "    --  Unity $($unity) comes next with 'rr install-unity': about 9 GB, and a browser sign-in to a free Unity account" }

    if (-not (Test-SetupSpace $cfg ($required + $optional) (-not $editor))) {
        if (-not (Confirm-Action "    Not enough free space for the rest of setup. Continue anyway?")) { Fail "Free up space, or point workDir/libraryDir at a bigger drive (rr init -Force)." }
    }

    $install = @()
    if ($required.Count + $optional.Count -gt 0) {
        Write-Host "    winget installs (Windows asks for administrator approval for each):"
        foreach ($n in $required + $optional) {
            $what = if ($Tools[$n].Size) { "$($Tools[$n].Package) ($($Tools[$n].Size))" } else { $Tools[$n].Package }
            Write-Host ("      {0,-56} {1}" -f $what, $(if ($optional -contains $n) { "optional" } else { "required" }))
        }
        if ($required.Count -gt 0 -and $optional.Count -gt 0) {
            $answer = if ($Yes) { "a" } else { "$(Read-Host '    Install [a]ll, [r]equired only, or [n]othing? [a]')".Trim().ToLowerInvariant() }
            if ($answer -match '^(|a|all|y|yes)$') { $install = $required + $optional } elseif ($answer -match '^r') { $install = $required }
        } elseif (Confirm-Action "    Install now?" -DefaultYes) { $install = $required + $optional }
    }
    if ($install.Count -gt 0) { $null = Install-Tools $install }

    $still = @($required | Where-Object { -not (Test-Tool $_) })
    if ($still.Count -gt 0) {
        Fail "Setup needs $(Format-ToolList $still). Install: $(($still | ForEach-Object { Get-ToolInstallCommand $_ }) -join '; ')"
    }
    Confirm-GitIdentity
    if ($optional.Count -gt 0 -and -not (Test-Tool "buildtools")) { Write-Warn2 "player builds need $($Tools.buildtools.Package) later: 'rr prereqs' or 'rr build-client' offers it again" }
    Write-Ok "prerequisites ready"
}

function Test-UnityProjectOpen {
    $lock = Join-Path $ClientDir "Temp\UnityLockfile"
    if (-not (Test-Path $lock)) { return $false }
    try {
        $fs = [System.IO.File]::Open($lock, "Open", "ReadWrite", "None")
        $fs.Close()
        return $false
    } catch { return $true }
}

# Unity opening a half-restored project or a half-moved Library would reimport it all for nothing.
function Assert-NothingHalfDone {
    if ($Force) { return }
    $restore = Read-State "restore"
    if ($restore -and -not (Test-OwnerAlive $restore)) {
        Fail "Restoring snapshot '$($restore.name)' was interrupted ($($restore.since)). Run 'rr snapshot restore -Name $($restore.name)' to finish it (it resumes), or pass -Force."
    }
    $move = Read-State "library-move"
    if ($move -and -not (Test-OwnerAlive $move)) { Fail "Moving the Unity Library to $($move.to) was interrupted. Run 'rr library-link' to finish it, or pass -Force." }
}

function Assert-UnityReady {
    Assert-NothingHalfDone
    $script:UnityCliPath = Get-UnityCli
    if (-not $script:UnityCliPath) { Fail "Unity CLI not installed. Run 'rr install-unity'." }
    if (-not (Get-UnityEditorPath)) { Fail "Unity $(Get-UnityVersion) not installed. Run 'rr install-unity'." }
    if (Test-UnityProjectOpen) { Fail "RebuildClient is open in a Unity Editor. Close it first; batch mode cannot share the project." }
    if (-not (Test-Path (Join-Path $ClientDir "Assets\StreamingAssets\ClientConfigGenerated\maps.json"))) {
        Write-Warn2 "Generated client config is missing; running update-client first."
        Invoke-UpdateClient
    }
}

# Streams the interesting lines of a Unity log while a batch process runs.
function Watch-UnityProcess([System.Diagnostics.Process]$proc, [string]$logFile, [string]$label) {
    $pattern = '\[Rebuild Automation\]|\[Map Import\]|\[Ragnarok Copy Utility\]|\[Addressables\] Sprites found|Loading ragnarok world resource file|error CS\d+|Scripts have compiler errors|Aborting batchmode|Exception:|Build (succeeded|failed)'
    $position = 0L
    $started = Get-Date
    $lastBeat = Get-Date
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 1500
        if (Test-Path $logFile) {
            try {
                $fs = [System.IO.File]::Open($logFile, "Open", "Read", "ReadWrite")
                if ($fs.Length -lt $position) { $position = 0 }
                $fs.Position = $position
                $reader = New-Object System.IO.StreamReader($fs)
                $text = $reader.ReadToEnd()
                $position = $fs.Position
                $reader.Close()
                foreach ($line in ($text -split "`r?`n")) {
                    if ($line -match $pattern) { Write-Host ("    | {0:hh\:mm\:ss} {1}" -f ((Get-Date) - $started), $line) }
                }
            } catch { }
        }
        if (((Get-Date) - $lastBeat).TotalSeconds -ge 60) {
            $lastBeat = Get-Date
            $elapsed = (Get-Date) - $started
            $size = 0; if (Test-Path $logFile) { $size = (Get-Item $logFile).Length }
            Write-Host ("    . {0} running {1:hh\:mm\:ss} (log {2})" -f $label, $elapsed, (Format-Size $size)) -ForegroundColor DarkGray
        }
    }
    $proc.WaitForExit()
    return $proc.ExitCode
}

function ConvertTo-ArgumentString([string[]]$arguments) {
    return ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

# ---------------------------------------------------------------------------------------------
# generated files
#
# The importers rewrite files upstream tracks (addressable groups, effect textures, atlases,
# lighting settings). They are local build state: committing them would conflict with every
# upstream regeneration. Files a Unity run changes are recorded and hidden with skip-worktree;
# `rr sync` sets them aside around merges and `rr generated` lists, reveals or discards them.
# ---------------------------------------------------------------------------------------------

function Get-GeneratedListPath { return Join-Path $RepoRoot ".git\info\rr-generated" }

function Get-GeneratedFiles {
    $file = Get-GeneratedListPath
    if (-not (Test-Path $file)) { return @() }
    return @(Get-Content $file -Encoding UTF8 | Where-Object { $_ })
}

function Get-ModifiedTrackedFiles {
    return @(Invoke-Git @("-c", "core.quotepath=off", "diff", "--name-only") | Where-Object { $_ })
}

function Protect-GeneratedFiles([string[]]$before) {
    $after = Get-ModifiedTrackedFiles
    # Unity only writes inside the project; files edited elsewhere during a long run stay visible.
    $new = @($after | Where-Object { $before -notcontains $_ -and $_ -like 'RebuildClient/*' -and $_ -notmatch '\.cs$' })
    if ($new.Count -eq 0) { return }
    $all = @(@(Get-GeneratedFiles) + $new | Sort-Object -Unique)
    Write-LinesAtomic (Get-GeneratedListPath) $all
    foreach ($chunk in (Split-Chunks $new 50)) { Invoke-Git (@("update-index", "--skip-worktree", "--") + $chunk) -AllowFail | Out-Null }
    Write-Ok "$($new.Count) tracked file(s) regenerated by Unity hidden from git status ('rr generated' to review)"
}

# Recorded before Unity starts, so a run that never returns can still have its regenerated files
# hidden and its half-written files removed by the next rr command.
function Start-UnityRun([string]$method) {
    $before = @(Get-ModifiedTrackedFiles)
    Write-State "unity-run" @{ command = $Command; method = $method; startedTicks = [DateTime]::UtcNow.Ticks; since = (Get-Date).ToString("yyyy-MM-dd HH:mm"); before = $before }
    return $before
}

function Complete-UnityRun([string[]]$before, [int]$code) {
    Protect-GeneratedFiles $before
    # A crash inside Unity can cut a copy off just like a kill.
    $run = Read-State "unity-run"
    if ($code -ne 0 -and $run) { Remove-PartialFiles ([long]$run.startedTicks) $run.method }
    Clear-State "unity-run"
}

$GeneratedExtensions = @(".asset", ".prefab", ".unity", ".mat", ".meta", ".controller", ".anim", ".spriteatlasv2", ".lighting", ".png", ".exr", ".bytes", ".walk", ".fbx", ".psd", ".json")
$TextAssetExtensions = @(".asset", ".prefab", ".unity", ".mat", ".meta", ".controller", ".anim", ".spriteatlasv2", ".lighting")

# Raw client files keep their source's timestamp when copied, so one stamped after the interrupted
# run started was cut off mid-copy: Windows sizes the file first, leaving a full-size file ending in
# zeros that the importers, which skip existing files, would keep forever. Unity saves assets as
# text, so a generated asset ending in NUL (or a PNG without its end chunk) was cut off as well.
function Remove-PartialFiles([long]$sinceTicks, [string]$method) {
    $since = New-Object DateTime($sinceTicks, [DateTimeKind]::Utc)
    $copies = 0
    $damaged = @()
    $tail = New-Object byte[] 8
    foreach ($rel in $ImportedFolders) {
        $root = Join-Path $RepoRoot $rel
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($f in (New-Object System.IO.DirectoryInfo $root).EnumerateFiles("*", [System.IO.SearchOption]::AllDirectories)) {
            # Import records are only ever replaced whole (temp file renamed over), never torn.
            if ($f.LastWriteTimeUtc -lt $since -or $f.FullName.StartsWith($ImportRecords + "\")) { continue }
            $ext = $f.Extension.ToLowerInvariant()
            if ($f.Name.EndsWith($TempSuffix) -or $GeneratedExtensions -notcontains $ext) { $f.Delete(); $copies++; continue }
            if ($TextAssetExtensions -notcontains $ext -and $ext -ne ".png") { continue }
            $bad = $f.Length -eq 0
            if (-not $bad) {
                $n = [int][Math]::Min(8, $f.Length)
                $fs = $f.OpenRead()
                try { $first = $fs.ReadByte(); $fs.Position = $f.Length - $n; [void]$fs.Read($tail, 0, $n) } finally { $fs.Dispose() }
                # Some assets stay binary whatever the project's serialization mode (LightingData.asset);
                # those start with a zero byte and may end with one.
                $bad = if ($ext -eq ".png") { [BitConverter]::ToString($tail, 0, $n) -ne "49-45-4E-44-AE-42-60-82" } else { $first -ne 0 -and $tail[$n - 1] -eq 0 }
            }
            if ($bad) { $f.Delete(); $damaged += $f.FullName.Substring($RepoRoot.Length + 1) }
        }
    }
    if ($copies -gt 0) { Write-Ok "removed $copies file(s) it was still copying; the rerun copies them again" }
    if ($damaged.Count -eq 0) { return }
    Write-Warn2 "removed $($damaged.Count) generated file(s) it left damaged:"
    $damaged | Select-Object -First 10 | ForEach-Object { Write-Host "    | $_" }
    # Map assets are only regenerated with their scene, so a damaged one sends its map back to import.
    # A bake puts its map back from its own backup, and minimaps are simply made again.
    if ($method -ne "ImportProfile") { return }
    $mapsJson = Join-Path $ClientDir "Assets\StreamingAssets\ClientConfigGenerated\maps.json"
    if (-not (Test-Path $mapsJson)) { return }
    $codes = @((Get-Content $mapsJson -Raw | ConvertFrom-Json).Items | ForEach-Object { $_.Code } | Sort-Object Length -Descending)
    $maps = @($damaged | Where-Object { $_ -match '^RebuildClient\\Assets\\(Maps|Scenes\\Maps)\\' } | ForEach-Object {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($_)
        $folder = Split-Path (Split-Path $_) -Leaf
        $codes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) -or $folder -eq $_ } | Select-Object -First 1
    } | Sort-Object -Unique)
    foreach ($code in $maps) { Remove-Item -LiteralPath (Join-Path $ClientDir "Assets\Scenes\Maps\$code.unity") -Force -ErrorAction SilentlyContinue }
    if ($maps.Count -gt 0) { Write-Ok "the rerun imports $($maps.Count) affected map(s) again: $($maps -join ', ')" }
}

function Split-Chunks([string[]]$items, [int]$size) {
    $chunks = @()
    for ($i = 0; $i -lt $items.Count; $i += $size) { $chunks += , @($items[$i..([Math]::Min($i + $size, $items.Count) - 1)]) }
    return $chunks
}

$GeneratedBackup = Join-Path $RepoRoot ".git\info\rr-generated-backup"
$BackupComplete = ".rr-complete"

# The backup is the only copy of the regenerated files while a merge runs. The marker is written
# once every file is in it, before the working copies are replaced; without it the working copies
# are still the real ones.
function Set-GeneratedAside {
    $files = @(Get-GeneratedFiles)
    if ($files.Count -eq 0) { return $null }
    Resume-GeneratedAside
    # Created first so a cut-off run is always found and its files hidden again.
    New-Item -ItemType Directory -Force $GeneratedBackup | Out-Null
    foreach ($chunk in (Split-Chunks $files 50)) { Invoke-Git (@("update-index", "--no-skip-worktree", "--") + $chunk) -AllowFail | Out-Null }
    foreach ($f in $files) {
        $src = Join-Path $RepoRoot $f
        if (Test-Path -LiteralPath $src) { Copy-FileAtomic $src (Join-Path $GeneratedBackup $f) }
    }
    Write-TextAtomic (Join-Path $GeneratedBackup $BackupComplete) ""
    foreach ($chunk in (Split-Chunks $files 50)) { Invoke-Git (@("checkout", "--") + $chunk) -AllowFail | Out-Null }
    return $GeneratedBackup
}

function Restore-GeneratedAside([string]$backup) {
    if (-not $backup) { return }
    $files = @(Get-GeneratedFiles)
    if (Test-Path -LiteralPath (Join-Path $backup $BackupComplete)) {
        foreach ($f in $files) {
            $src = Join-Path $backup $f
            if (Test-Path -LiteralPath $src) { Copy-FileAtomic $src (Join-Path $RepoRoot $f) }
        }
    }
    foreach ($chunk in (Split-Chunks $files 50)) { Invoke-Git (@("update-index", "--skip-worktree", "--") + $chunk) -AllowFail | Out-Null }
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
}

# An 'rr sync' that was cut off leaves the committed versions in place of the regenerated files.
function Resume-GeneratedAside {
    if (-not (Test-Path -LiteralPath $GeneratedBackup)) { return }
    Restore-GeneratedAside $GeneratedBackup
    Write-Ok "put back the regenerated files an interrupted 'rr sync' had set aside"
}

function Invoke-Generated {
    $files = @(Get-GeneratedFiles)
    $action = if ($Target) { $Target.ToLowerInvariant() } else { "list" }
    switch ($action) {
        "list" {
            Write-Step "$($files.Count) generated file(s) hidden from git status"
            $files | ForEach-Object { Write-Host "    $_" }
        }
        "hide" {
            # For regenerations done from the Unity menus instead of rr: hide every modified non-script tracked file.
            Protect-GeneratedFiles @()
        }
        "reveal" {
            foreach ($chunk in (Split-Chunks $files 50)) { Invoke-Git (@("update-index", "--no-skip-worktree", "--") + $chunk) -AllowFail | Out-Null }
            Remove-Item (Get-GeneratedListPath) -Force -ErrorAction SilentlyContinue
            Write-Ok "revealed $($files.Count) file(s); they show in git status again"
        }
        "discard" {
            if (-not (Confirm-Action "Restore the committed versions of $($files.Count) generated file(s)? Re-run 'rr import'/'rr addressables' afterwards.")) { return }
            foreach ($chunk in (Split-Chunks $files 50)) {
                Invoke-Git (@("update-index", "--no-skip-worktree", "--") + $chunk) -AllowFail | Out-Null
                Invoke-Git (@("checkout", "--") + $chunk) -AllowFail | Out-Null
            }
            Remove-Item (Get-GeneratedListPath) -Force -ErrorAction SilentlyContinue
            Write-Ok "restored committed versions"
        }
        default { Fail "Unknown action '$action' (list | hide | reveal | discard)" }
    }
}

function Assert-ImportSettled {
    if ((Get-UnityJournal "import").phase -eq "maps") { Fail "An import stopped among the maps, so the last map it saved may be incomplete. Run 'rr import' first (it carries on)." }
}

# Files an unfinished import deleted to make again are still missing, so a build, bake, minimap run
# or snapshot made now lacks them.
function Assert-ImportFinished {
    if ((Test-Path -LiteralPath (Get-RecordPath "outputs.tsv")) -and -not (Read-ImportComplete)) {
        Fail "The last 'rr import' did not finish, so files it is making again are missing. Run 'rr import' first (it carries on)."
    }
}

function Invoke-UnityMethod([string]$method, [string[]]$extra, [switch]$Async) {
    Assert-UnityReady
    if (@("BakeLighting", "MakeMinimaps") -contains $method) { Assert-ImportSettled; Assert-ImportFinished }
    New-Item -ItemType Directory -Force $UnityLogDir | Out-Null
    $logFile = Join-Path $UnityLogDir ("rr-{0}.log" -f $method.ToLowerInvariant())
    if (Test-Path $logFile) { Remove-Item $logFile -Force }

    if ($Async) {
        # `unity run` always adds -quit, which would end the editor before async bakes finish.
        $exe = Get-UnityEditorPath
        $arguments = @("-batchmode", "-projectPath", $ClientDir, "-logFile", $logFile, "-executeMethod", "$AutomationClass.$method") + $extra
    } else {
        $exe = $script:UnityCliPath
        $arguments = @("run", $ClientDir, "--non-interactive", "--no-banner", "--no-tail", "--log-file", $logFile, "--", "-executeMethod", "$AutomationClass.$method") + $extra
    }

    Write-Step "Unity: $method (log: $logFile)"
    $beforeRun = Start-UnityRun $method
    $proc = Start-Process -FilePath $exe -ArgumentList (ConvertTo-ArgumentString $arguments) -NoNewWindow -PassThru
    $null = $proc.Handle
    $code = Watch-UnityProcess $proc $logFile $method
    Complete-UnityRun $beforeRun $code
    if ($code -ne 0) {
        Write-Warn2 "Unity exited with code $code. Last errors from the log:"
        if (Test-Path $logFile) {
            Get-Content $logFile | Select-String -Pattern "error|exception|failed" | Select-Object -Last 15 | ForEach-Object { Write-Host "    | $($_.Line)" }
        }
        Fail "Unity $method failed (exit $code). Full log: $logFile"
    }
    Write-Ok "$method finished"
}

# ---------------------------------------------------------------------------------------------
# commands
# ---------------------------------------------------------------------------------------------

function Show-Help {
    @"
Ragnarok Rebuild fork helper  (run from the repo root as: .\rr <command> [target] [options])

 First time  (bare machine: setup\bootstrap.ps1 installs Git, clones the fork and runs setup)
   init                 Create setup\config.local.json: work folder, client GRFs (or just a
                        rebuild-pack.grf), BGM folder, release archive
   prereqs              Check Git, .NET 9, 7-Zip (when needed), the C++ build tools
                        (optional, player builds) and free space; winget installs what's missing
                        after one question (-Yes: no questions)
   setup                Everything needed to play in the editor: init, prereqs, git-setup,
                        install-unity, library-link, server-build, update-client, pack, then import
                        (into a fresh project, the latest snapshot is restored first when it
                        matches the pack or records what it was imported from)
   use-grf <file>       Setup from a rebuild-pack.grf alone: it becomes the only pack source (no
                        clients, BGM or release archive), then setup imports everything from it,
                        or restores a snapshot of that pack if the work folder has one
   use-bundle <file>    Setup from a baked bundle (RagnarokRebuild-baked-<date>.7z): extracts its
                        GRF and snapshot into the work folder, then setup restores the snapshot
                        instead of importing, lighting and minimaps included (-Name: the name
                        the snapshot gets, default latest)
   doctor               Check prerequisites, git wiring, data, import state and disk space, and
                        list anything a stopped command left half done (rerunning it carries on)

 Git / fork
   git-setup            origin = the fork (forkUrl), upstream = Doddler (push disabled), work branch,
                        hooks that block commits/pushes to master/main
   sync                 Fetch upstream, fast-forward master locally and on the fork, merge
                        upstream/master into the current work branch (-Push to push it)
   generated [action]   Tracked files Unity regenerated during rr runs, hidden from git status.
                        action: list (default) | hide | reveal | discard

 Tools
   install-unity        Standalone Unity CLI + the project's editor version + Personal license,
                        then clears the CLI download cache

 Data (the Rebuild pack)
   reference            Extract Doddler's release walk data (releaseArchive) for map verification
   pack                 Build our own pack from packSources (+ Custom folder): only the files Rebuild
                        references, every map checked against the release walk data, gaps filled
                        from other clients or setup\pack\aliases.json. Writes data\, walkdata\,
                        rebuild-pack.grf, manifest.json, deps.tsv (the pack files each imported
                        asset is made from) and report.html into packDir. The GRF also
                        carries the reference walk data, so it can be the only source elsewhere.
                        Compares the result with setup\pack\fingerprint.tsv (the fork's pack) and
                        names the client the fork took each differing file from.
   fingerprint          Make this machine's pack the one others compare with: copies packDir's
                        fingerprint.tsv (names, hashes, sources; no game data) into setup\pack\

 Server
   server-build         dotnet build RoRebuildServer.sln
   update-client        Rebuild shared DLLs and regenerate client config (updateclient.bat).
                        Committed DLLs are restored unless shared code changed (-KeepDlls)
   server [profile]     Run the server (launch profile: RoRebuildServer | Minimal)

 Client (headless Unity)
   import [profile]     Copy BGM and import client data (minimum | medium | full). Remakes only
                        what pack files changed since the last import (deps.tsv) and removes
                        what the pack no longer makes; with the pack and project as last imported
                        it returns without starting Unity (-Force runs the importer anyway)
   addressables         Update addressable groups after adding sprites or maps
   bake [maps]          Bake lighting for all maps or a comma list, skipping maps whose lighting
                        was baked from their current scene (hours; GPU; -Force rebakes)
   minimaps [maps]      Render minimaps (after lighting), skipping maps whose minimap was made
                        from their current scene and lighting (-Force remakes)
   report               Print import counts
   build-client [dir]   Windows player build (Local addressables profile); skipped when the
                        project hasn't changed since the last build (-Force builds anyway)
   play                 Launch the built client
   smoke                End-to-end check: server + built client create an account and a character,
                        enter the world and save a screenshot (workDir\smoke)
   showcase [dir]       Captioned screenshots of every file the pack adds, stands in or fixes, on one
                        page with what to look at in each (workDir\showcase\<time>\index.html).
                        Given an earlier run's folder, only rewrites its page
   release [dir]        Shareable release (workDir\release): the player build, a self-contained
                        server with its data and walk data, Play.cmd, readme and version.txt,
                        checked with its own server and client, then 7z'd. -NoBuild reuses the
                        current player build instead of building it
   editor               Open the project in the Unity Editor

 Housekeeping
   disk                 Sizes of everything this setup created and free space per drive
   library-link [dir]   Move RebuildClient\Library (Unity cache) to another drive via a junction
                        (also moves an existing junction). Prefer an internal SSD over USB.
   perf [undo]          Defender exclusions for the Unity project, Library and pack (admin)
   snapshot [action]    list | save | restore | delete the imported client state (+ Library unless
                        -NoLibrary) under workDir\snapshots\<-Name, default latest>; restore turns a
                        multi-hour import into a copy, and the next import brings in only what
                        the pack changed since the snapshot was saved
   clean [scope]        Remove regenerable files. scope: temp (default) | build | data | imported |
                        library | all. 'imported' removes only gitignored importer output
"@ | Write-Host
}

function Read-WorkDir {
    $best = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' -and $_.Free -gt 20GB -and $_.DisplayRoot -notlike '\\*' } |
        Sort-Object Free -Descending | Select-Object -First 1
    $suggest = if ($best) { Join-Path $best.Root "RagnarokRebuildData" } else { Join-Path (Split-Path $RepoRoot -Parent) "RagnarokRebuildData" }
    Write-Host "    The work folder holds the pack and the Unity cache (~15 GB). Player builds add ~25 GB and each snapshot ~15 GB."
    $answer = (Read-Host "Work folder, outside the repo [$suggest]").Trim('"', ' ')
    if ([string]::IsNullOrWhiteSpace($answer)) { return $suggest }
    return $answer
}

function Invoke-Init {
    $cfg = Read-Config
    if ($cfg.workDir -and @($cfg.packSources).Count -gt 0 -and -not $Force) { Write-Ok "config.local.json already complete (use -Force to redo)"; return }
    $values = @{ workDir = Read-WorkDir }

    Write-Host "    Client data sources, highest priority first. The pack takes each file from the first client that has it"
    Write-Host "    (maps are chosen by matching Doddler's release instead). Use kRO clients with Korean file names."
    Write-Host "    A rebuild-pack.grf from an earlier 'rr pack' is enough on its own."
    $sources = @()
    while ($true) {
        $path = (Read-Host "Path to a data.grf, or a loose data folder (blank to finish)").Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($path)) { break }
        if (-not (Test-Path -LiteralPath $path)) { Write-Warn2 "not found: $path"; continue }
        if (Test-PackGrf $path) {
            $sources += @{ name = "Rebuild pack"; path = $path }
            Write-Ok "Rebuild pack GRF: it already holds every file, the music and Doddler's walk data"
            break
        }
        $name = Read-Host "Short name for it [client $($sources.Count + 1)]"
        if ([string]::IsNullOrWhiteSpace($name)) { $name = "client $($sources.Count + 1)" }
        $entry = @{ name = $name; path = $path }
        if (Test-Path -LiteralPath $path -PathType Container) { $entry.type = "folder" }
        $sources += $entry
    }
    if ($sources.Count -eq 0) { Fail "At least one data.grf is needed." }
    if (Test-PackGrf $sources[-1].path) {
        $values.packSources = $sources
        Save-LocalConfig $values
        Write-Ok "Saved setup\config.local.json"
        return
    }

    $guess = Join-Path (Split-Path $sources[0].path -Parent) "BGM"
    $prompt = "Folder with BGM mp3 files (blank to skip)"
    if (Test-Path $guess) { $prompt += " [$guess]" }
    $bgm = Read-Host $prompt
    if ([string]::IsNullOrWhiteSpace($bgm) -and (Test-Path $guess)) { $bgm = $guess }
    if ($bgm) { $sources += @{ name = "BGM"; path = $bgm; type = "folder"; prefix = "bgm" } }
    $values.packSources = $sources

    $release = Read-Host "Doddler's release archive (RagnarokRebuild_*_Client+Server*.7z) for map verification (blank to skip)"
    if ($release) { $values.releaseArchive = $release }

    Save-LocalConfig $values
    Write-Ok "Saved setup\config.local.json"
}

# A rebuild-pack.grf holds every file, the music and Doddler's walk data, so it replaces the client list.
function Set-PackGrfSource([string]$grf, [string]$workDir) {
    $cfg = Read-Config
    $values = @{}
    if (-not $cfg.workDir) { $values.workDir = $workDir }
    $packDir = if ($cfg.packDir) { $cfg.packDir } else { Join-Path $workDir "pack" }
    if ($grf -eq [System.IO.Path]::GetFullPath((Join-Path $packDir "rebuild-pack.grf"))) {
        Fail "$grf is the GRF 'rr pack' writes, so packing would overwrite its own source. Copy it out of $packDir (into $workDir, say) and give the copy."
    }
    $current = @($cfg.packSources | Where-Object { $_ })
    if (-not ($current.Count -eq 1 -and [System.IO.Path]::GetFullPath("$($current[0].path)") -eq $grf)) {
        if ($current.Count -gt 0) {
            Write-Host "    packSources now: $(($current | ForEach-Object { $_.name }) -join ', ')"
            if (-not (Confirm-Action "Replace them with $grf?" -DefaultYes)) { Fail "packSources left as they were." }
        }
        $values.packSources = @(@{ name = "Rebuild pack"; path = $grf })
    }
    if ($values.Count -gt 0) { Save-LocalConfig $values; Write-Ok "saved setup\config.local.json; pack source: $grf" }
    else { Write-Ok "pack source: $grf" }
}

function Invoke-UseGrf {
    if (-not $Target) { Fail "Give the GRF: rr use-grf <path to rebuild-pack.grf>" }
    $grf = [System.IO.Path]::GetFullPath($Target.Trim('"', ' '))
    if (-not (Test-Path -LiteralPath $grf -PathType Leaf)) { Fail "Not found: $grf" }
    if (-not (Test-PackGrf $grf)) { Fail "$(Split-Path $grf -Leaf) isn't a pack GRF ('rr pack' names it rebuild-pack.grf). A client's data.grf goes in through 'rr init -Force'." }
    $cfg = Read-Config
    Write-Step "Setting up from $grf"
    Set-PackGrfSource $grf $(if ($cfg.workDir) { $cfg.workDir } else { Read-WorkDir })
    Invoke-Setup
}

# The baked bundle is a work folder in an archive: rebuild-pack.grf at its root and a snapshot saved
# from that same pack under snapshots\, so setup rebuilds the pack and restores instead of importing.
function Invoke-UseBundle {
    if (-not $Target) { Fail "Give the bundle: rr use-bundle <path to RagnarokRebuild-baked-<date>.7z>" }
    $archive = [System.IO.Path]::GetFullPath($Target.Trim('"', ' '))
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { Fail "Not found: $archive" }
    $file = Get-Item -LiteralPath $archive
    Assert-Tools @("sevenzip") "Extracting $($file.Name)"
    $sevenZip = Get-SevenZip

    $listing = @((Get-NativeOutput $sevenZip @("l", "-ba", $archive, "rebuild-pack.grf", "snapshots\*\snapshot.json", "-r-")) -split "`n")
    if ($script:NativeExit -ne 0) { Fail "7-Zip can't read $archive" }
    $bundleSnap = $listing | ForEach-Object { if ($_ -match '\ssnapshots\\([^\\]+)\\snapshot\.json$') { $Matches[1] } } | Select-Object -First 1
    if (-not ($listing -match '\srebuild-pack\.grf$') -or -not $bundleSnap) {
        Fail "$($file.Name) isn't a baked bundle: one has rebuild-pack.grf and snapshots\<name>\snapshot.json at its root."
    }

    $cfg = Read-Config
    $workDir = [System.IO.Path]::GetFullPath($(if ($cfg.workDir) { $cfg.workDir } else { Read-WorkDir }))
    # Saved before the long extraction, so a rerun after an interruption finds its half-done copy.
    if (-not $cfg.workDir) { Save-LocalConfig @{ workDir = $workDir } }
    $grf = Join-Path $workDir "rebuild-pack.grf"
    $snapDir = Join-Path $workDir "snapshots\$Name"
    $markerFile = Join-Path $workDir "bundle.json"
    $stamp = "$($file.Name) $($file.Length) $($file.LastWriteTimeUtc.ToString('o'))"
    $marker = $null
    if (Test-Path -LiteralPath $markerFile) { try { $marker = Get-Content -LiteralPath $markerFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { } }

    if ($marker -and $marker.archive -eq $stamp -and $marker.snapshot -eq $Name -and (Test-Path -LiteralPath $grf) -and (Test-Path -LiteralPath (Join-Path $snapDir "snapshot.json"))) {
        Write-Ok "$($file.Name) is already extracted into $workDir"
    } else {
        # Extracted into a staging folder first and marked complete there, so a cut-off extraction is
        # redone and one that finished is only moved into place (a rename, same drive).
        $staging = Join-Path $workDir "bundle$TempSuffix"
        if (-not (Test-Path -LiteralPath (Join-Path $staging $SwapMarker))) {
            if (Test-Path -LiteralPath $staging) { Remove-PathSafe $staging "the extraction an earlier run left unfinished" }
            $summary = (Get-NativeOutput $sevenZip @("l", $archive)) -split "`n" | Select-Object -Last 1
            $need = if ($summary -match '\s(\d+)\s+\d+\s+\d+ files') { [long]$Matches[1] } else { 0 }
            $free = Get-FreeBytes $workDir
            if ($free -ge 0 -and $free -lt $need) { Fail "Extracting $($file.Name) takes $(Format-Size $need) on $([System.IO.Path]::GetPathRoot($workDir)), which has $(Format-Size $free) free." }
            Write-Step "Extracting $($file.Name) into $workDir ($(Format-Size $need); 13 minutes on the machine that made it)"
            New-Item -ItemType Directory -Force $staging | Out-Null
            $log = Join-Path $env:TEMP "rr-use-bundle-7z.log"
            $argLine = "x `"$archive`" `"-o$staging`" -aoa -y -bso0 -bsp0 `"-x!README-master-*.txt`""
            $p = Start-Process -FilePath $sevenZip -ArgumentList $argLine -NoNewWindow -PassThru -RedirectStandardError $log
            $null = $p.Handle
            $startFree = Get-FreeBytes $workDir
            try {
                while (-not $p.WaitForExit(60000)) {
                    Write-Host "    $(Format-Size ([Math]::Max([long]0, $startFree - (Get-FreeBytes $workDir)))) of $(Format-Size $need)"
                }
                $p.WaitForExit()
            } finally {
                if (-not $p.HasExited) { $p.Kill() }
            }
            if ($p.ExitCode -ne 0) {
                Get-Content -LiteralPath $log -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    | $_" }
                Fail "7-Zip stopped extracting $($file.Name) (exit $($p.ExitCode)). Rerun 'rr use-bundle' to try again."
            }
            Write-TextAtomic (Join-Path $staging $SwapMarker) ""
        }
        $stagedGrf = Join-Path $staging "rebuild-pack.grf"
        if (Test-Path -LiteralPath $stagedGrf) { Move-FileOver $stagedGrf $grf }
        $stagedSnap = Join-Path $staging "snapshots\$bundleSnap"
        if (Test-Path -LiteralPath $stagedSnap) {
            if (Test-Path -LiteralPath $snapDir) {
                if (-not (Confirm-Action "Replace snapshot '$Name' in $(Split-Path $snapDir) with the bundle's?" -DefaultYes)) { Fail "Kept snapshot '$Name'. Pass -Name <another name> to put the bundle's beside it." }
                Remove-PathSafe $snapDir "snapshot $Name"
            }
            New-Item -ItemType Directory -Force (Split-Path $snapDir) | Out-Null
            [System.IO.Directory]::Move($stagedSnap, $snapDir)
        }
        if (-not (Test-Path -LiteralPath $grf) -or -not (Test-Path -LiteralPath (Join-Path $snapDir "snapshot.json"))) {
            Fail "The extracted bundle is missing from $workDir. Delete $staging and rerun 'rr use-bundle'."
        }
        Remove-Item -LiteralPath $staging -Recurse -Force
        Write-TextAtomic $markerFile (@{ archive = $stamp; snapshot = $Name; extracted = (Get-Date).ToString("yyyy-MM-dd HH:mm") } | ConvertTo-Json)
        Write-Ok "rebuild-pack.grf and snapshot '$Name' are in $workDir"
    }

    Set-PackGrfSource $grf $workDir
    $nameArg = if ($Name -ne "latest") { " -Name $Name" } else { "" }
    if (Test-Path -LiteralPath (Get-RecordPath "ledger.tsv")) {
        if (Confirm-Action "This clone already has imported client data. Replace it with the bundle's snapshot?" -DefaultYes) { $script:ReplaceImport = $true }
        else { Write-Ok "keeping the current import; 'rr snapshot restore$nameArg' brings in the bundle's later" }
    }
    Invoke-Setup
    $snapBytes = [long](Get-Content -LiteralPath (Join-Path $snapDir "snapshot.json") -Raw -Encoding UTF8 | ConvertFrom-Json).bytes
    Write-Host "    $($file.Name) can be deleted now. 'rr snapshot delete$nameArg' frees the snapshot ($(Format-Size $snapBytes)) if you won't reinstall from it."
    Write-Host "    Keep $grf`: every 'rr pack' reads it."
}

function Invoke-GitSetup {
    $cfg = Read-Config
    Write-Step "Git remotes"
    $remotes = @(Invoke-Git @("remote"))
    $originUrl = if ($remotes -contains "origin") { (Invoke-Git @("remote", "get-url", "origin")) } else { "" }
    $upstreamPattern = "Doddler/RagnarokRebuildTcp"

    if (-not ($remotes -contains "upstream")) {
        if ($originUrl -like "*$upstreamPattern*") {
            Invoke-Git @("remote", "rename", "origin", "upstream") | Out-Null
            $remotes = @(Invoke-Git @("remote"))
            Write-Ok "renamed origin -> upstream"
        } else {
            Invoke-Git @("remote", "add", "upstream", $cfg.upstreamUrl) | Out-Null
            Write-Ok "added upstream $($cfg.upstreamUrl)"
        }
    }
    Invoke-Git @("remote", "set-url", "--push", "upstream", "DISABLED--do-not-push-to-upstream") | Out-Null
    Write-Ok "upstream push disabled"

    if (-not ($remotes -contains "origin")) {
        if (-not $cfg.forkUrl) { Fail "No origin remote and no forkUrl in setup\config.json." }
        Invoke-Git @("remote", "add", "origin", $cfg.forkUrl) | Out-Null
        Write-Ok "origin = $($cfg.forkUrl)"
    }

    Invoke-Git @("fetch", "--all", "--prune") | Out-Null
    Invoke-Git @("branch", "--set-upstream-to=upstream/master", "master") -AllowFail | Out-Null

    # Upstream stores every text file with LF and Unity rewrites assets with LF, so "input" keeps
    # Unity saves from showing up as phantom modifications (autocrlf=true does).
    Invoke-Git @("config", "core.autocrlf", "input") | Out-Null
    # Upstream tracks this .meta but ignores its .bin, so Unity deletes the orphan on every import.
    $orphanMeta = "RebuildClient/Assets/AddressableAssetsData/Windows/addressables_content_state.bin.meta"
    $listed = @(Get-GeneratedFiles)
    if ($listed -notcontains $orphanMeta) { Write-LinesAtomic (Get-GeneratedListPath) (@($listed) + $orphanMeta) }
    Invoke-Git @("update-index", "--skip-worktree", $orphanMeta) -AllowFail | Out-Null
    Write-Ok "core.autocrlf=input; Unity's orphaned addressables .meta hidden (rr generated)"

    $branch = $cfg.workBranch
    $hasLocal = [bool]"$(Invoke-Git @("branch", "--list", $branch))".Trim()
    if (-not $hasLocal) {
        $remoteBranch = "$(Invoke-Git @("branch", "-r", "--list", "origin/$branch"))".Trim()
        if ($remoteBranch) {
            Invoke-Git @("branch", "--track", $branch, "origin/$branch") | Out-Null
        } else {
            Invoke-Git @("branch", $branch, "upstream/master") | Out-Null
            Invoke-Git @("push", "-u", "origin", $branch) | Out-Null
        }
        Write-Ok "work branch '$branch' created"
    } else { Write-Ok "work branch '$branch' exists" }

    Write-Step "Git hooks"
    $hooksDir = "$(Invoke-Git @("rev-parse", "--git-path", "hooks"))".Trim()
    if (-not [System.IO.Path]::IsPathRooted($hooksDir)) { $hooksDir = Join-Path $RepoRoot $hooksDir }
    New-Item -ItemType Directory -Force $hooksDir | Out-Null
    foreach ($hook in Get-ChildItem (Join-Path $SetupDir "git-hooks") -File) {
        # Hooks run under Git's sh, so they are written with LF endings regardless of checkout settings.
        $content = (Get-Content $hook.FullName -Raw) -replace "`r`n", "`n"
        Write-TextAtomic (Join-Path $hooksDir $hook.Name) $content
        Write-Ok "installed $($hook.Name)"
    }

    $current = "$(Invoke-Git @("branch", "--show-current"))".Trim()
    if ($current -eq "master" -or $current -eq "main") { Write-Warn2 "You are on '$current'. Switch to your work branch: git switch $branch" }
}

function Invoke-Sync {
    $mergeHead = "$(Invoke-Git @("rev-parse", "--git-path", "MERGE_HEAD"))".Trim()
    if (-not [System.IO.Path]::IsPathRooted($mergeHead)) { $mergeHead = Join-Path $RepoRoot $mergeHead }
    if (Test-Path -LiteralPath $mergeHead) {
        Fail "A merge is still in progress (conflicts, or an interrupted sync). Resolve and 'git commit' it, or 'git merge --abort', then rerun 'rr sync'."
    }
    $backup = Set-GeneratedAside
    try { Invoke-SyncCore }
    finally {
        if ($backup) {
            Restore-GeneratedAside $backup
            Write-Ok "generated files restored and hidden again"
        }
    }
}

function Get-ForkOverrides {
    if (-not (Test-Path -LiteralPath $ForkOverrides)) { return @() }
    return @(Get-Content -LiteralPath $ForkOverrides -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith("#") })
}

function Test-ForkOverride([string]$path, [string[]]$overrides) {
    foreach ($o in $overrides) { if ($path -eq $o -or ($o.EndsWith("/") -and $path.StartsWith($o))) { return $true } }
    return $false
}

# Puts each path back the way it was in $commit: that version, or deleted.
function Restore-ForkPaths([string]$commit, [string[]]$paths) {
    foreach ($p in $paths) {
        Invoke-Git @("cat-file", "-e", "${commit}:$p") -AllowFail | Out-Null
        if ($script:NativeExit -eq 0) { Invoke-Git @("checkout", $commit, "--", $p) | Out-Null }
        else { Invoke-Git @("rm", "-q", "-f", "--ignore-unmatch", "--", $p) | Out-Null }
    }
}

function Invoke-SyncCore {
    $cfg = Read-Config
    $dirty = "$(Invoke-Git @("status", "--porcelain", "--untracked-files=no"))".Trim()
    if ($dirty) { Fail "Working tree has uncommitted changes. Commit or stash them first." }

    Write-Step "Fetching upstream and origin"
    Invoke-Git @("fetch", "upstream", "--prune") | Out-Null
    Invoke-Git @("fetch", "origin", "--prune") | Out-Null

    $current = "$(Invoke-Git @("branch", "--show-current"))".Trim()
    foreach ($mirror in @("master", "main")) {
        $up = "$(Invoke-Git @("rev-parse", "--verify", "-q", "refs/remotes/upstream/$mirror") -AllowFail)".Trim()
        if (-not $up -or $up -match "fatal") { continue }

        $local = "$(Invoke-Git @("rev-parse", "--verify", "-q", "refs/heads/$mirror") -AllowFail)".Trim()
        if ($local -and $local -notmatch "fatal") {
            Invoke-Git @("merge-base", "--is-ancestor", $local, $up) -AllowFail | Out-Null
            if ($script:NativeExit -ne 0) { Fail "Local $mirror has commits that are not in upstream/$mirror. Move them to a work branch, then reset $mirror." }
            if ($current -eq $mirror) { Invoke-Git @("merge", "--ff-only", "upstream/$mirror") | Out-Null }
            else { Invoke-Git @("update-ref", "refs/heads/$mirror", $up, $local) | Out-Null }
        }
        Invoke-Git @("push", "origin", "refs/remotes/upstream/${mirror}:refs/heads/$mirror") | Out-Null
        Write-Ok "$mirror = upstream/$mirror ($($up.Substring(0, 8))) locally and on origin"
    }

    $work = if ($current -and $current -ne "master" -and $current -ne "main") { $current } else { $cfg.workBranch }
    if ($current -ne $work) { Invoke-Git @("switch", $work) | Out-Null }

    $behind = "$(Invoke-Git @("rev-list", "--count", "HEAD..upstream/master"))".Trim()
    if ($behind -eq "0") { Write-Ok "$work already contains upstream/master" }
    else {
        Write-Step "Merging $behind upstream commit(s) into $work"
        $preMerge = "$(Invoke-Git @("rev-parse", "HEAD"))".Trim()
        $overrides = @(Get-ForkOverrides)
        $out = Invoke-Git @("merge", "--no-edit", "upstream/master") -AllowFail
        if ($script:NativeExit -ne 0) {
            $merged = $false
            $unmerged = @(Invoke-Git @("-c", "core.quotepath=off", "diff", "--name-only", "--diff-filter=U") -AllowFail | Where-Object { $_ })
            $ours = @($unmerged | Where-Object { Test-ForkOverride $_ $overrides })
            if ($ours.Count -gt 0) {
                Restore-ForkPaths $preMerge $ours
                Write-Ok "kept the fork's version of $($ours.Count) file(s) from setup\fork-overrides.txt: $($ours -join ', ')"
                if ($ours.Count -eq $unmerged.Count) {
                    $out = Invoke-Git @("commit", "--no-edit") -AllowFail
                    $merged = $script:NativeExit -eq 0
                }
            }
            if (-not $merged) {
                $out | ForEach-Object { Write-Host "    | $_" }
                Fail "Merge conflicts. Resolve them, 'git commit', then rerun 'rr update-client'."
            }
        }
        # A clean merge can bring listed paths back too, e.g. a new file in a folder the fork removed.
        if ($overrides.Count -gt 0) {
            $back = @(Invoke-Git (@("-c", "core.quotepath=off", "diff", "--name-only", $preMerge, "HEAD", "--") + $overrides) -AllowFail | Where-Object { $_ })
            if ($back.Count -gt 0) {
                Restore-ForkPaths $preMerge $back
                Invoke-Git @("commit", "-q", "-m", "chore(sync): keep the fork's version of $($back.Count) upstream path(s)", "-m", ($back -join "`n")) | Out-Null
                Write-Ok "kept the fork's version of $($back.Count) file(s) from setup\fork-overrides.txt: $($back -join ', ')"
            }
        }
        Write-Ok "merged upstream/master into $work"
        $generated = @(Get-GeneratedFiles)
        if ($generated.Count -gt 0) {
            $touched = @(Invoke-Git (@("-c", "core.quotepath=off", "diff", "--name-only", $preMerge, "HEAD", "--") + $generated) -AllowFail | Where-Object { $_ })
            if ($touched.Count -gt 0) {
                Write-Warn2 "Upstream changed $($touched.Count) file(s) your imports regenerate; your local versions are kept."
                Write-Warn2 "Re-run 'rr import' / 'rr addressables', or 'rr generated discard' to take upstream's copies."
            }
        }
        Write-Warn2 "Server data or shared code may have changed: run 'rr update-client' (and 'rr import' for new maps/sprites)."
    }

    if ($Push) { Invoke-Git @("push", "origin", $work) | Out-Null; Write-Ok "pushed $work" }
    else { Write-Host "    Push when ready: git push origin $work" }
}

function Invoke-InstallUnity {
    $cli = Install-UnityCli
    $version = Get-UnityVersion

    # Batch mode only needs a license; the sign-in is for activating one. It comes before the long
    # download below so that runs unattended.
    $licensePattern = "Unity (Personal|Pro|Plus|Enterprise)"
    $licensed = (Get-NativeOutput $cli @("license", "list", "--non-interactive")) -match $licensePattern
    if (-not $licensed) {
        $status = Get-NativeOutput $cli @("auth", "status", "--non-interactive")
        if ($status -match "not signed in") {
            Write-Step "Signing in to Unity (browser; a free Unity account is enough)"
            Invoke-Native $cli @("auth", "login") | Out-Null
        } else { Write-Ok ($status.Trim()) }
    }

    $il2cpp = Test-ProjectUsesIl2cpp
    # An install that was cut off can leave files that look installed, so it is repaired rather
    # than trusted; --resume reuses what was already downloaded.
    $pending = Read-State "install"
    $editorPath = Get-UnityEditorPath
    if ($editorPath -and $pending.editor -ne $version) { Write-Ok "Unity $version installed: $editorPath" }
    else {
        $modules = if ($il2cpp) { @("-m", "windows-il2cpp") } else { @() }
        $repair = if ($editorPath) { @("--force") } else { @() }
        Write-Step "$(if ($editorPath) { 'Repairing the interrupted install of' } else { 'Installing' }) Unity $version$(if ($il2cpp) { ' with Windows Build Support (IL2CPP)' })"
        Write-State "install" @{ editor = $version; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
        Invoke-Native $cli (@("install", $version) + $modules + $repair + @("--resume", "--accept-eula", "--yes", "--non-interactive")) | Out-Null
        Clear-State "install"
        Write-Ok "Unity $version installed"
    }

    if ($il2cpp) {
        $pending = Read-State "install"
        $present = Test-Il2cppSupport (Get-UnityEditorPath)
        if ($present -and $pending.module -ne "windows-il2cpp") { Write-Ok "Windows IL2CPP build support installed (the project's player backend)" }
        else {
            Write-Step "$(if ($present) { 'Repairing the interrupted install of' } else { 'Installing' }) Windows Build Support (IL2CPP); the project builds its player with IL2CPP"
            Write-State "install" @{ module = "windows-il2cpp"; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
            $repair = if ($present) { @("--reinstall") } else { @() }
            Invoke-Native $cli (@("install-modules", "-e", $version, "-m", "windows-il2cpp", "--accept-eula", "-y") + $repair) | Out-Null
            Clear-State "install"
            Write-Ok "Windows IL2CPP build support installed"
        }
        if (Test-CppToolchain) { Write-Ok "MSVC C++ toolchain found" } else { Write-Warn2 "player builds also need the MSVC C++ toolchain ('rr prereqs' installs it)" }
    }

    if (-not $licensed -and (Get-NativeOutput $cli @("license", "list", "--non-interactive")) -notmatch $licensePattern) {
        Write-Step "Activating Unity Personal"
        Invoke-Native $cli @("license", "activate", "--personal", "--accept-eula", "--non-interactive") | Out-Null
    }
    Write-Ok "license present"

    $null = Get-NativeOutput $cli @("cache", "clean", "--non-interactive")
    Write-Ok "Unity CLI download cache cleared"
}

function Build-PackTool {
    Assert-Tools @("dotnet") "The pack tool"
    Write-Step "Building RebuildPack"
    Invoke-Native "dotnet" @("build", $PackTool, "-c", "Release", "-v", "quiet", "-nologo", "-o", (Join-Path $PackTool "bin\out")) -Quiet | Out-Null
}

# Doddler's release ships the server walk data he plays with (including hand edits). The pack
# verifies every map against it and gives the server those exact files.
function Invoke-Reference {
    $cfg = Read-Config
    if (-not $cfg.referenceWalk) { Fail "referenceWalk (or workDir) is not set in config.local.json." }
    # Extracted beside the folder and swapped in once complete, so a cut-off extraction never
    # passes for the reference data.
    Complete-FolderSwap $cfg.referenceWalk
    $staging = "$($cfg.referenceWalk.TrimEnd('\')).new"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $existing = @(Get-ChildItem -LiteralPath $cfg.referenceWalk -Filter *.walk -ErrorAction SilentlyContinue).Count
    if ($existing -gt 0 -and -not $Force) { Write-Ok "reference walk data present ($existing maps) in $($cfg.referenceWalk)"; return }
    if (-not $cfg.releaseArchive -or -not (Test-Path -LiteralPath $cfg.releaseArchive)) {
        $packGrf = Get-PackGrfSource $cfg
        if ($packGrf) { Write-Ok "reference walk data comes from $($packGrf.path)"; return }
        Write-Warn2 "releaseArchive not set or missing; maps are packed without upstream verification."
        return
    }
    Assert-Tools @("sevenzip") "Reading $($cfg.releaseArchive)"
    $sevenZip = Get-SevenZip
    Write-Step "Extracting reference walk data from $($cfg.releaseArchive)"
    New-Item -ItemType Directory -Force $staging | Out-Null
    Invoke-Native $sevenZip @("e", $cfg.releaseArchive, "-o$staging", "*\Server\walkdata\*.walk", "-r", "-y", "-bso0", "-bsp0") -Quiet | Out-Null
    $count = @(Get-ChildItem -LiteralPath $staging -Filter *.walk).Count
    if ($count -eq 0) { Fail "$($cfg.releaseArchive) has no Server\walkdata\*.walk files." }
    Write-TextAtomic (Join-Path $staging $SwapMarker) ""
    Complete-FolderSwap $cfg.referenceWalk
    Write-Ok "$count reference walk files"
}

function Invoke-Pack {
    $cfg = Read-Config
    if (-not $cfg.packDir) { Fail "packDir (or workDir) is not set in config.local.json." }
    if (-not $cfg.packSources -or @($cfg.packSources).Count -eq 0) { Fail "No packSources in config.local.json. Run 'rr init', or see setup\config.local.example.json." }
    if (-not (Test-Path (Join-Path $ClientDir "Assets\StreamingAssets\ClientConfigGenerated\maps.json"))) { Invoke-UpdateClient }
    Invoke-Reference
    Build-PackTool

    $sources = @()
    if ($cfg.customDir) {
        New-Item -ItemType Directory -Force $cfg.customDir | Out-Null
        $sources += @{ name = "Custom"; path = $cfg.customDir; type = "folder" }
    }
    $sources += @($cfg.packSources)
    $packConfig = @{
        repo = $RepoRoot
        referenceWalk = $cfg.referenceWalk
        aliases = (Join-Path $SetupDir "pack\aliases.json")
        fingerprint = $ForkFingerprint
        sources = $sources
    }
    New-Item -ItemType Directory -Force $cfg.packDir | Out-Null
    $configFile = Join-Path $cfg.packDir "pack.json"
    Write-TextAtomic $configFile ($packConfig | ConvertTo-Json -Depth 5)

    Write-Step "Building the Rebuild pack in $($cfg.packDir)"
    $code = Invoke-Native $PackToolExe @("build", "--config", $configFile, "--out", $cfg.packDir, "--grf") -AllowFail
    if ($code -ne 0 -and $code -ne 3) { Fail "RebuildPack failed ($code)" }
    $report = Join-Path $cfg.packDir "report.html"
    if ($code -eq 3) { Write-Warn2 "some required references are unresolved; see $report" } else { Write-Ok "every required reference resolved" }
    Write-Ok "report: $report"
}

function Get-FingerprintSignature([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    $line = Get-Content -LiteralPath $path -TotalCount 12 -Encoding UTF8 | Where-Object { $_ -like "signature`t*" } | Select-Object -First 1
    if ($line) { return $line.Split("`t")[1] } else { return $null }
}

function Invoke-Fingerprint {
    $cfg = Read-Config
    $built = if ($cfg.packDir) { Join-Path $cfg.packDir "fingerprint.tsv" } else { "" }
    if (-not $built -or -not (Test-Path -LiteralPath $built)) { Fail "No pack fingerprint yet. Run 'rr pack' first." }
    if (Test-Path -LiteralPath $ForkFingerprint) {
        $body = { param($p) (Get-Content -LiteralPath $p -Encoding UTF8 | Where-Object { $_ -notlike "generated`t*" }) -join "`n" }
        if ((& $body $built) -ceq (& $body $ForkFingerprint)) {
            Write-Ok "setup\pack\fingerprint.tsv already describes this pack"
            return
        }
    }
    Copy-FileAtomic $built $ForkFingerprint
    $files = @(Get-Content -LiteralPath $ForkFingerprint -Encoding UTF8 | Where-Object { $_ -like "file`t*" }).Count
    Write-Ok ("wrote setup\pack\fingerprint.tsv ({0:N0} files, no game data); commit it so other machines compare their packs with this one" -f $files)
}

function Invoke-ServerBuild {
    Assert-Tools @("dotnet") "The server"
    Write-Step "Building server solution"
    Invoke-Native "dotnet" @("build", (Join-Path $ServerRoot "RoRebuildServer.sln"), "-c", "Debug", "-v", "quiet", "-nologo") -Quiet | Out-Null
    Write-Ok "server built"
}

function Invoke-UpdateClient {
    Assert-Tools @("dotnet") "update-client"
    Write-Step "update-client: shared DLLs + generated client config"
    foreach ($proj in @("GameConfig", "RebuildSharedData", "DataToClientUtility")) {
        Invoke-Native "dotnet" @("build", (Join-Path $ServerRoot $proj), "-c", "Release", "-v", "quiet", "-nologo", "--property", "WarningLevel=0") -Quiet | Out-Null
    }

    $dataDir = Join-Path $ClientDir "Assets\Data"
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Copy-Item (Join-Path $ServerRoot "GameConfig\bin\Release\netstandard2.0\GameConfig.dll") (Join-Path $dataDir "GameConfig.dll") -Force
    Copy-Item (Join-Path $ServerRoot "RebuildSharedData\bin\Release\netstandard2.1\RebuildSharedData.dll") (Join-Path $dataDir "RebuildSharedData.dll") -Force

    $utilDir = Join-Path $ServerRoot "DataToClientUtility\bin\Release\net9.0"
    Push-Location $utilDir
    try { Invoke-Native (Join-Path $utilDir "DataToClientUtility.exe") @() -Quiet | Out-Null } finally { Pop-Location }

    # The DLLs are committed upstream and rebuilt bytes never match exactly; keep the committed copies
    # unless the shared source was changed locally, so `git status` stays clean.
    $sharedChanged = "$(Invoke-Git @("status", "--porcelain", "--", "RoRebuildServer/GameConfig", "RoRebuildServer/RebuildSharedData", ":!RoRebuildServer/GameConfig/ServerData"))".Trim()
    if (-not $sharedChanged -and -not $KeepDlls) {
        Invoke-Git @("checkout", "--", "RebuildClient/Assets/Data/GameConfig.dll", "RebuildClient/Assets/Data/RebuildSharedData.dll") | Out-Null
        Write-Ok "shared code unchanged; kept committed DLLs"
    } else {
        Write-Ok "shared code changed locally; rebuilt DLLs copied into the client (commit them with your change)"
    }
    Write-Ok "client config regenerated"
}

function Invoke-Server {
    $cfg = Read-Config
    Assert-Tools @("dotnet") "The server"
    $launch = if ($Target) { $Target } else { $cfg.serverLaunchProfile }
    $packWalk = if ($cfg.packDir) { Join-Path $cfg.packDir "walkdata" } else { $null }
    if ($packWalk -and (Test-Path $packWalk)) {
        # Every map, including server-only ones the client never imports, with Doddler's walk edits.
        $env:ServerDataConfig__WalkPathData = $packWalk
        Write-Ok "walk data: $packWalk ($(@(Get-ChildItem $packWalk -Filter *.walk).Count) maps, from the pack)"
    } else {
        $walk = Join-Path $ClientDir "Assets\Maps\exportdata"
        if (-not (Test-Path $walk)) { Write-Warn2 "No walk data yet. Run 'rr pack' (or import maps) first." }
    }
    Write-Step "Starting server (launch profile '$launch') on http://localhost:5000  (Ctrl+C to stop)"
    Push-Location $ServerProjectDir
    try { & dotnet run --launch-profile $launch } finally { Pop-Location; Remove-Item Env:\ServerDataConfig__WalkPathData -ErrorAction SilentlyContinue }
}

function Copy-Bgm([string]$from) {
    if (-not $from -or -not (Test-Path -LiteralPath $from)) { Write-Warn2 "no BGM folder found; skipping music."; return }
    $music = Join-Path $ClientDir "Assets\Music"
    New-Item -ItemType Directory -Force $music | Out-Null
    # No /XO: a copy cut off by a kill is stamped with the time it died, so it looks newer than its
    # source and /XO would keep it forever. Robocopy recopies any file whose size or time differs.
    & robocopy $from $music *.mp3 *.ogg *.wav *.flac /COPY:DAT /NJH /NJS /NFL /NDL /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "robocopy BGM failed ($LASTEXITCODE)" }
    Write-Ok "BGM in Assets\Music ($(@(Get-ChildItem $music -File | Where-Object Extension -ne '.meta').Count) files)"
}

function Get-ImportDataDir {
    $pack = Get-PackDataDir
    if ($pack) { return $pack }
    Fail "No client data yet. Run 'rr pack'."
}

# Deletes the converted sprite data of every ACT whose sound events name one of $soundNames (lower
# case file names); RebuildAutomation converts sprites without data again during the import.
function Reset-SpriteDataForSounds($soundNames) {
    $spriteRoot = Join-Path $ClientDir "Assets\Sprites"
    $dataRoot = Join-Path $spriteRoot "Imported"
    if (-not (Test-Path -LiteralPath $dataRoot)) { return }
    # ACT event names are CP949; PowerShell 7 (.NET Core) needs the code page provider for it.
    try { [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance) } catch { }
    $cp949 = [Text.Encoding]::GetEncoding(949)
    # Suffix matching also catches names read with junk before them; only affordable for a few sounds.
    $suffixes = if ($soundNames.Count -le 50) { @($soundNames) } else { @() }
    $reset = 0
    foreach ($act in Get-ChildItem -LiteralPath $spriteRoot -Recurse -File -Filter *.act) {
        if ($act.FullName.StartsWith($dataRoot + "\")) { continue }
        $text = $cp949.GetString([IO.File]::ReadAllBytes($act.FullName))
        $hit = $false
        foreach ($m in [regex]::Matches($text, '[^\x00-\x1f\\/]+\.wav', 'IgnoreCase')) {
            $name = $m.Value.ToLowerInvariant()
            if ($soundNames.Contains($name) -or ($suffixes | Where-Object { $name.EndsWith($_) })) { $hit = $true; break }
        }
        if (-not $hit) { continue }
        $rel = $act.DirectoryName.Substring($spriteRoot.Length + 1)
        $asset = Join-Path (Join-Path $dataRoot $rel) ($act.BaseName + ".asset")
        if (Test-Path -LiteralPath $asset) {
            Remove-Item -LiteralPath $asset, "$asset.meta" -Force -ErrorAction SilentlyContinue
            $reset++
        }
    }
    if ($reset -gt 0) { Write-Ok "queued $reset sprite(s) for reconversion (their sound events changed)" }
}

# ---------------------------------------------------------------------------------------------
# import records
#
# The importers skip every file that already exists, so a pack file that changed after its import
# would never reach the project. These records say what the project was made from. They sit beside
# the map scenes, so snapshots carry them and 'rr clean imported' removes them (Unity skips folders
# whose name starts with a dot):
#   ledger.tsv     every pack file's SHA-1 and size as of the last import
#   outputs.tsv    a stamp per importer output listed in the pack's deps.tsv: a SHA-1 over the
#                  paths and SHA-1s of the pack files it is made from
#   complete.json  written once an import finishes: pack signature, profile and file counts
#   baked.tsv, minimaps.tsv  written by RebuildAutomation: the scene stamp each bake and minimap
#                  was made from
# 'rr import' deletes the outputs whose stamp changed and the project's copies of pack files that
# changed, so Unity makes exactly those again, and leaves Unity closed when nothing changed.
# ---------------------------------------------------------------------------------------------

$ImportRecords = Join-Path $ClientDir "Assets\Scenes\Maps\.rr-import"

function Get-RecordPath([string]$name) { return Join-Path $ImportRecords $name }

function Read-Tsv([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $rows = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $i = $line.IndexOf("`t")
        if ($i -gt 0) { $rows[$line.Substring(0, $i)] = $line.Substring($i + 1) }
    }
    return $rows
}

function Write-Tsv([string]$path, [hashtable]$rows) {
    $keys = [string[]]@($rows.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $sb = New-Object System.Text.StringBuilder
    foreach ($k in $keys) { [void]$sb.Append($k).Append("`t").Append($rows[$k]).Append("`n") }
    Write-TextAtomic $path $sb.ToString()
}

function Get-Sha1Hex([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant() } finally { $sha.Dispose() }
}

# The signature of the pack's data files (what a project is imported from). It leads manifest.json,
# so this reads only the start of it; packs made before it was added have it in the GRF's .sig.
function Get-PackSignature {
    $cfg = Read-Config
    if (-not $cfg.packDir) { return "" }
    $manifest = Join-Path $cfg.packDir "manifest.json"
    if (Test-Path -LiteralPath $manifest) {
        $fs = [System.IO.File]::OpenRead($manifest)
        try { $buf = New-Object byte[] 512; $n = $fs.Read($buf, 0, $buf.Length) } finally { $fs.Dispose() }
        if ([System.Text.Encoding]::UTF8.GetString($buf, 0, $n) -match '"signature":"([0-9A-F]{40})"') { return $Matches[1] }
    }
    $sig = Join-Path $cfg.packDir "rebuild-pack.grf.sig"
    if (Test-Path -LiteralPath $sig) { return (Get-Content -LiteralPath $sig -Raw).Trim() }
    return ""
}

# The pack as the next import sees it: each file's SHA-1 and size, and each output's stamp.
function Get-PackState([string]$packDir) {
    $m = [System.IO.File]::ReadAllText((Join-Path $packDir "manifest.json")) | ConvertFrom-Json
    $files = @{}
    foreach ($e in $m.entries) { $files[$e.Path] = "$($e.Sha1)`t$($e.Size)" }
    $depsFile = Join-Path $packDir "deps.tsv"
    if (-not (Test-Path -LiteralPath $depsFile)) { return @{ Files = $files; Stamps = $null } }
    # deps.tsv is sorted, so each output's lines are consecutive.
    $stamps = @{}
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $sb = New-Object System.Text.StringBuilder
    $current = $null
    foreach ($line in @([System.IO.File]::ReadAllLines($depsFile)) + "") {
        $i = $line.IndexOf("`t")
        $out = if ($i -gt 0) { $line.Substring(0, $i) } else { $null }
        if ($out -cne $current) {
            if ($current) { $stamps[$current] = Get-Sha1Hex $utf8.GetBytes($sb.ToString()) }
            $current = $out
            [void]$sb.Clear()
        }
        if (-not $out) { continue }
        $from = $line.Substring($i + 1)
        $hash = $files[$from]
        [void]$sb.Append($from).Append("`t").Append($(if ($hash) { $hash.Split("`t")[0] } else { "missing" })).Append("`n")
    }
    return @{ Files = $files; Stamps = $stamps }
}

# Files per imported folder, without what bakes and minimaps add, so deleting imported files to have
# them made again sends the next import to Unity.
function Get-ImportCensus {
    $census = [ordered]@{}
    foreach ($rel in $ImportedFolders) {
        $root = Join-Path $RepoRoot $rel
        $n = 0
        if (Test-Path -LiteralPath $root) {
            $top = $rel -eq "RebuildClient/Assets/Scenes/Maps"
            $option = if ($top) { [System.IO.SearchOption]::TopDirectoryOnly } else { [System.IO.SearchOption]::AllDirectories }
            foreach ($f in [System.IO.Directory]::EnumerateFiles($root, "*", $option)) {
                if ($f.EndsWith(".meta")) { continue }
                $sub = $f.Substring($root.Length + 1)
                if ($sub.StartsWith(".") -or ($rel -eq "RebuildClient/Assets/Maps" -and $sub -match '^(minimap|lighting)\\')) { continue }
                $n++
            }
        }
        $census[$rel] = $n
    }
    return $census
}

function Read-ImportComplete {
    $file = Get-RecordPath "complete.json"
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { return Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

# Lit only while the scene points at lighting data in its own folder (RebuildAutomation.HasLightmaps):
# a scene imported again starts with none, and the lightmaps left in the folder are no longer its own.
function Test-HasLightmaps([string]$code) {
    $dir = Join-Path $ClientDir "Assets\Scenes\Maps\$code"
    $scene = "$dir.unity"
    if (-not ([System.IO.File]::Exists($scene) -and [System.IO.Directory]::Exists($dir) -and
            [System.IO.Directory]::EnumerateFiles($dir, "*.exr").GetEnumerator().MoveNext())) { return $false }
    $guid = $null
    $lines = [System.IO.File]::ReadLines($scene).GetEnumerator()
    try {
        while ($lines.MoveNext()) {
            if ($lines.Current -match '^\s*m_LightingDataAsset:.*guid: ([0-9a-f]{32})') { $guid = $Matches[1]; break }
            if ($lines.Current -match '^\s*m_LightingDataAsset:') { break }
        }
    } finally { $lines.Dispose() }
    if (-not $guid) { return $false }
    foreach ($meta in [System.IO.Directory]::EnumerateFiles($dir, "*.meta")) {
        if ([System.IO.File]::ReadAllText($meta).Contains("guid: $guid")) { return $true }
    }
    return $false
}

# A project imported before these records existed is taken as made from the current pack, its
# lighting and minimaps as made from its scenes.
function Initialize-ImportRecords($pack) {
    $baked = Read-Tsv (Get-RecordPath "baked.tsv"); if ($null -eq $baked) { $baked = @{} }
    $minimaps = Read-Tsv (Get-RecordPath "minimaps.tsv"); if ($null -eq $minimaps) { $minimaps = @{} }
    foreach ($o in @($pack.Stamps.Keys | Where-Object { $_ -like "Assets/Scenes/Maps/*.unity" })) {
        $code = [System.IO.Path]::GetFileNameWithoutExtension($o)
        if (-not (Test-Path -LiteralPath (Join-Path $ClientDir $o))) { continue }
        $lit = Test-HasLightmaps $code
        if ($lit -and -not $baked.ContainsKey($code)) { $baked[$code] = "$($pack.Stamps[$o])@adopted" }
        if ((Test-Path -LiteralPath (Join-Path $ClientDir "Assets\Maps\minimap\$code.png")) -and -not $minimaps.ContainsKey($code)) {
            $minimaps[$code] = if ($lit) { $baked[$code] } else { "$($pack.Stamps[$o])@unlit" }
        }
    }
    Write-Tsv (Get-RecordPath "baked.tsv") $baked
    Write-Tsv (Get-RecordPath "minimaps.tsv") $minimaps
    Write-Tsv (Get-RecordPath "ledger.tsv") $pack.Files
    Write-Tsv (Get-RecordPath "outputs.tsv") $pack.Stamps
}

# The sprite data RebuildAutomation converts from a sprite file (none for files it doesn't use).
function Get-SpriteDataAssets([string]$file) {
    $spriteRoot = Join-Path $ClientDir "Assets\Sprites"
    $dir = Split-Path $file
    $name = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $names = @()
    switch ([System.IO.Path]::GetExtension($file).ToLowerInvariant()) {
        ".pal" {
            # <folder>\Palette\<sprite>_<0-9>.pal and <sprite>_<0-9>_1.pal belong to <folder>\<sprite>.act;
            # "a_1_1" can be either, so both go.
            if ((Split-Path $dir -Leaf) -ne "Palette") { return }
            $dir = Split-Path $dir
            if ($name -match '^(.+)_\d_1$') { $names += $Matches[1] }
            if ($name -match '^(.+)_\d$') { $names += $Matches[1] }
        }
        { $_ -in ".act", ".spr" } { $names = @($name) }
    }
    if ($dir.Length -le $spriteRoot.Length) { return }
    foreach ($n in $names) { Join-Path (Join-Path $spriteRoot "Imported") (Join-Path $dir.Substring($spriteRoot.Length + 1) "$n.asset") }
}

# The importer's sprite and palette renames (RagnarokCopyFromRealClient's UpdateSpriteName, and the
# hair palettes losing their prefix), escaped so Windows PowerShell reads this file the same.
$CopyRenames = @(@("\uBA38\uB9AC", ""), @("\uC131\uC9C1\uC790_", "Acolyte_"), @("\uAD81\uC218_", "Archer_"),
    @("\uB9C8\uBC95\uC0AC_", "Mage_"), @("\uC0C1\uC778_", "Merchant_"), @("\uCD08\uBCF4\uC790_", "Novice_"),
    @("\uAC80\uC0AC_", "Swordsman_"), @("\uB3C4\uB451_", "Thief_"), @("\uC5EC_", "F_"), @("\uB0A8_", "M_")) |
    ForEach-Object { , @([regex]::Unescape($_[0]), $_[1]) }

# A sprite or palette file's name as the importer copies it, so a pack file and its copy compare equal.
function Get-CopyName([string]$name) {
    foreach ($r in $CopyRenames) { $name = $name.Replace($r[0], $r[1]) }
    return $name.ToLowerInvariant()
}

# Deletes what the next import has to make again: importer outputs whose stamp changed, and the
# project's copies of sprite and palette files that changed (byte-for-byte copies, found by the
# old file's SHA-1 since the importer renames some and copies some under alias names) with the
# sprite data converted from them. A match that is also, by name and content, the copy of a pack
# file that didn't change (marin.act is poring.act's bytes) stays.
# Assets go without their .meta, so the files made again keep their GUIDs. Outputs the pack no
# longer makes go with theirs, except map scenes: those come with lightmaps and map data the records
# don't list, so they are only named. The records are only written after the deletions, so a run
# cut off partway deletes the rest next time.
function Update-ImportRecords([string]$packDir) {
    $pack = Get-PackState $packDir
    if (-not $pack.Stamps) { Write-Warn2 "the pack has no deps.tsv (made by an older RebuildPack); run 'rr pack' so imports can tell what changed"; return }
    Remove-Item -LiteralPath (Get-RecordPath "complete.json") -Force -ErrorAction SilentlyContinue
    $ledger = Read-Tsv (Get-RecordPath "ledger.tsv")
    $made = Read-Tsv (Get-RecordPath "outputs.tsv")
    if ($null -eq $ledger -or $null -eq $made) {
        $imported = $false
        foreach ($o in $pack.Stamps.Keys) { if ([System.IO.File]::Exists((Join-Path $ClientDir $o))) { $imported = $true; break } }
        if ($imported) {
            Write-Warn2 "no import records yet: taking the project as imported from the current pack ($(Get-PackSignature)). If it was imported from an older pack, delete the files that changed (or 'rr clean imported') and run 'rr import' again."
            Initialize-ImportRecords $pack
            return
        }
        $ledger = @{}; $made = @{}
    }

    $stale = @(foreach ($o in $pack.Stamps.Keys) {
        if ($made[$o] -ne $pack.Stamps[$o] -and [System.IO.File]::Exists((Join-Path $ClientDir $o))) { $o }
    })
    $dropped = @(foreach ($o in $made.Keys) {
        if (-not $pack.Stamps.ContainsKey($o) -and [System.IO.File]::Exists((Join-Path $ClientDir $o))) { $o }
    })
    $droppedMaps = @($dropped | Where-Object { $_ -like "Assets/Scenes/Maps/*.unity" } | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) })
    $dropped = @($dropped | Where-Object { $_ -notlike "Assets/Scenes/Maps/*.unity" })
    $changed = @(foreach ($p in $ledger.Keys) {
        if (($p -like "sprite/*" -or $p -like "palette/*") -and $pack.Files[$p] -ne $ledger[$p]) { $ledger[$p] }
    })
    $copies = @()
    if ($changed.Count -gt 0) {
        $old = New-Object 'System.Collections.Generic.HashSet[string]'
        $sizes = New-Object 'System.Collections.Generic.HashSet[long]'
        foreach ($c in $changed) { $parts = $c.Split("`t"); $null = $old.Add("$($parts[0])`t$($parts[1])"); $null = $sizes.Add([long]$parts[1]) }
        $current = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($p in $pack.Files.Keys) {
            if (($p -like "sprite/*" -or $p -like "palette/*") -and $ledger[$p] -eq $pack.Files[$p] -and $old.Contains($pack.Files[$p])) {
                $null = $current.Add("$(Get-CopyName ($p.Split('/')[-1]))`t$($pack.Files[$p])")
            }
        }
        $spriteRoot = Join-Path $ClientDir "Assets\Sprites"
        $dataRoot = Join-Path $spriteRoot "Imported"
        foreach ($f in (New-Object System.IO.DirectoryInfo $spriteRoot).EnumerateFiles("*", [System.IO.SearchOption]::AllDirectories)) {
            if (-not $sizes.Contains($f.Length) -or $f.Extension -eq ".meta" -or $f.FullName.StartsWith($dataRoot + "\")) { continue }
            $content = "$(Get-Sha1Hex ([System.IO.File]::ReadAllBytes($f.FullName)))`t$($f.Length)"
            if ($old.Contains($content) -and -not $current.Contains("$(Get-CopyName $f.Name)`t$content")) { $copies += $f.FullName }
        }
    }

    $maps = @($stale | Where-Object { $_ -like "Assets/Scenes/Maps/*" } | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) })
    $models = @($stale | Where-Object { $_ -like "Assets/Models/Prefabs/*" }).Count
    foreach ($o in $stale) { Remove-Item -LiteralPath (Join-Path $ClientDir $o) -Force }
    $assets = Join-Path $ClientDir "Assets"
    foreach ($o in $dropped) {
        $path = Join-Path $ClientDir $o
        Remove-Item -LiteralPath $path, "$path.meta" -Force -ErrorAction SilentlyContinue
        for ($dir = Split-Path $path; $dir.Length -gt $assets.Length -and [System.IO.Directory]::Exists($dir) -and
                -not [System.IO.Directory]::EnumerateFileSystemEntries($dir).GetEnumerator().MoveNext(); $dir = Split-Path $dir) {
            Remove-Item -LiteralPath $dir, "$dir.meta" -Force -ErrorAction SilentlyContinue
        }
    }
    $spriteData = 0
    foreach ($f in $copies) {
        Remove-Item -LiteralPath $f -Force
        foreach ($asset in @(Get-SpriteDataAssets $f)) {
            if (Test-Path -LiteralPath $asset) { Remove-Item -LiteralPath $asset -Force; $spriteData++ }
        }
    }
    $outputs = $pack.Stamps.Clone()
    foreach ($code in $droppedMaps) { $outputs["Assets/Scenes/Maps/$code.unity"] = $made["Assets/Scenes/Maps/$code.unity"] }
    Write-Tsv (Get-RecordPath "ledger.tsv") $pack.Files
    Write-Tsv (Get-RecordPath "outputs.tsv") $outputs

    if ($dropped.Count -gt 0) { Write-Ok "removed $($dropped.Count) converted file(s) the pack no longer makes" }
    if ($droppedMaps.Count -gt 0) {
        Write-Warn2 "map(s) no longer in the pack: $($droppedMaps -join ', '). Their scenes stay in Assets\Scenes\Maps (and in builds) until deleted with their folders, Assets\Maps data and minimaps."
    }
    if ($stale.Count + $copies.Count -eq 0) {
        if ($dropped.Count + $droppedMaps.Count -eq 0) { Write-Ok "nothing the project was imported from has changed" }
        return
    }
    $what = @()
    if ($maps.Count -gt 0) { $what += "$($maps.Count) map(s) ($(($maps | Select-Object -First 8) -join ', ')$(if ($maps.Count -gt 8) { ', ...' }))" }
    if ($models -gt 0) { $what += "$models model(s)" }
    $others = $stale.Count - $maps.Count - $models
    if ($others -gt 0) { $what += "$others other converted file(s)" }
    if ($copies.Count -gt 0) { $what += "$($copies.Count) sprite file(s) ($spriteData converted sprite(s))" }
    Write-Ok "made from pack files that changed, so imported again: $($what -join ', ')"
}

# Once the import has finished: records it, and names the maps imported again since their bake.
function Complete-ImportRecords([string]$signature, [string]$importProfile) {
    $made = Read-Tsv (Get-RecordPath "outputs.tsv")
    if ($null -eq $made) { return }
    Write-TextAtomic (Get-RecordPath "complete.json") ([ordered]@{
        pack = $signature; profile = $importProfile; finished = (Get-Date).ToString("yyyy-MM-dd HH:mm"); census = Get-ImportCensus
    } | ConvertTo-Json -Depth 3)
    $baked = Read-Tsv (Get-RecordPath "baked.tsv")
    if (-not $baked) { return }
    $relight = @(foreach ($code in $baked.Keys) {
        $stamp = $made["Assets/Scenes/Maps/$code.unity"]
        if ($stamp -and (-not $baked[$code].StartsWith("$stamp@") -or -not (Test-HasLightmaps $code))) { $code }
    })
    if ($relight.Count -gt 0) { Write-Ok "imported again since their bake: $(($relight | Sort-Object) -join ', '); 'rr bake' and then 'rr minimaps' redo just those" }
}

function Invoke-Import {
    $cfg = Read-Config
    $importProfile = if ($Target) { $Target } else { $cfg.importProfile }
    $data = Get-ImportDataDir
    $signature = Get-PackSignature
    $done = Read-ImportComplete
    if (-not $Force -and $done -and $signature -and $done.pack -eq $signature -and $done.profile -eq $importProfile -and
        -not (Get-UnityJournal "import") -and -not (Read-State "sound-resets") -and
        ((Get-ImportCensus) | ConvertTo-Json -Compress) -eq ($done.census | ConvertTo-Json -Compress)) {
        Write-Ok "the project is up to date with the pack ($signature, profile '$importProfile', imported $($done.finished)); 'rr import -Force' runs the importer anyway"
        return
    }
    Assert-UnityReady
    $free = Get-FreeBytes $ClientDir
    if ($importProfile -eq "full" -and $free -lt 15GB) { Write-Warn2 "Only $(Format-Size $free) free on the project drive; a full import can use 10+ GB." }
    Copy-Bgm (Join-Path $data "bgm")

    # The importer skips files that already exist, so sounds the pack added or changed (e.g. ADPCM
    # decoded to PCM) are copied here; the .meta stays, keeping the asset GUID. Sprites bind their
    # sound events when converted, so sprites naming those sounds are queued for reconversion.
    $packWav = Join-Path $data "wav"
    $sounds = Join-Path $ClientDir "Assets\Sounds"
    if ((Test-Path -LiteralPath $packWav) -and (Test-Path -LiteralPath $sounds)) {
        # The names are recorded before any sound is replaced and cleared once their sprites are
        # queued, so an interrupted run cannot leave a new sound behind a sprite bound to the old one.
        $changed = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($n in @((Read-State "sound-resets").names)) { if ($n) { $null = $changed.Add($n) } }
        $copies = @()
        foreach ($f in Get-ChildItem -LiteralPath $packWav -Recurse -File) {
            $dest = Join-Path $sounds $f.FullName.Substring($packWav.Length + 1)
            $existing = Get-Item -LiteralPath $dest -ErrorAction SilentlyContinue
            if ($existing -and $existing.Length -eq $f.Length -and ($existing.LastWriteTimeUtc -eq $f.LastWriteTimeUtc -or
                [System.Linq.Enumerable]::SequenceEqual([byte[]][System.IO.File]::ReadAllBytes($dest), [byte[]][System.IO.File]::ReadAllBytes($f.FullName)))) { continue }
            $copies += , @($f.FullName, $dest)
            $null = $changed.Add($f.Name.ToLowerInvariant())
        }
        if ($changed.Count -gt 0) {
            Write-State "sound-resets" @{ names = @($changed) }
            foreach ($c in $copies) { Copy-FileAtomic $c[0] $c[1] }
            if ($copies.Count -gt 0) { Write-Ok "copied $($copies.Count) new or changed sound(s) from the pack" }
            Reset-SpriteDataForSounds $changed
            Clear-State "sound-resets"
        }
    }
    Update-ImportRecords $cfg.packDir
    Write-Step "Importing profile '$importProfile' from $data"
    Invoke-UnityMethod "ImportProfile" @("-rrDataPath", $data, "-rrProfile", $importProfile)
    Complete-ImportRecords $signature $importProfile
}

$SwapMarker = ".rr-complete"

# Replaces $dir with the finished "$dir.new" (marked complete). Every step can be cut off: rerunning
# this picks up where it stopped, so $dir is always either the old folder or the new one.
function Complete-FolderSwap([string]$dir) {
    $dir = $dir.TrimEnd('\')
    $new = "$dir.new"
    $old = "$dir.old"
    if (Test-Path -LiteralPath (Join-Path $new $SwapMarker)) {
        if (Test-Path -LiteralPath $dir) {
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
            Rename-Item -LiteralPath $dir -NewName (Split-Path $old -Leaf)
        }
        Rename-Item -LiteralPath $new -NewName (Split-Path $dir -Leaf)
    }
    Remove-Item -LiteralPath (Join-Path $dir $SwapMarker) -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}

function Get-BuildDirs([string]$path) {
    $out = (Resolve-RepoPath $path).TrimEnd('\')
    return @{ Out = $out; Symbols = "$out-symbols" }
}

function Complete-BuildSwap($dirs) {
    Complete-FolderSwap $dirs.Symbols
    Complete-FolderSwap $dirs.Out
}

$BuildStampName = ".rr-build-stamp"
# What a player build rewrites itself, every time.
$BuildRewrites = @("ProjectSettings/*", "Assets/AddressableAssetsData/*", "Assets/Settings/*", "Assets/UniversalRenderPipelineGlobalSettings.asset")

# What a player is built from: the Unity version and the path, size and time of every project file
# Unity reads (it skips names starting with a dot or ending in ~). Taken after a build, since the
# build itself rewrites addressable groups and settings, so the next one can tell nothing changed
# since. Changed lists the other files written since $since (ticks, UTC): a build they changed
# during may not have them.
function Get-BuildStamp([long]$since = [long]::MaxValue) {
    $changed = New-Object System.Collections.Generic.List[string]
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("unity $(Get-UnityVersion)`n")
    $stack = New-Object System.Collections.Generic.Stack[object]
    foreach ($top in "ProjectSettings", "Packages", "Assets") {
        $dir = Join-Path $ClientDir $top
        if (Test-Path -LiteralPath $dir) { $stack.Push(@((New-Object System.IO.DirectoryInfo $dir), $top)) }
    }
    while ($stack.Count -gt 0) {
        $dir, $rel = $stack.Pop()
        $entries = $dir.GetFileSystemInfos()
        if ($entries.Length -eq 0) { continue }
        [Array]::Sort([string[]]$entries.Name, $entries, [StringComparer]::Ordinal)
        foreach ($e in $entries) {
            if ($e.Name.StartsWith(".") -or $e.Name.EndsWith("~")) { continue }
            if ($e -is [System.IO.DirectoryInfo]) { $stack.Push(@($e, "$rel/$($e.Name)")); continue }
            [void]$sb.Append($rel).Append("/").Append($e.Name).Append("`t").Append($e.Length).Append("`t").Append($e.LastWriteTimeUtc.Ticks).Append("`n")
            if ($e.LastWriteTimeUtc.Ticks -ge $since) {
                $path = "$rel/$($e.Name)"
                if (-not ($BuildRewrites | Where-Object { $path -like $_ })) { $changed.Add($path) }
            }
        }
    }
    return @{ Stamp = Get-Sha1Hex ((New-Object System.Text.UTF8Encoding $false).GetBytes($sb.ToString())); Changed = $changed }
}

function Invoke-Build {
    $cfg = Read-Config
    $dirs = Get-BuildDirs $(if ($Target) { $Target } else { $cfg.buildOutput })
    $out = $dirs.Out
    Assert-UnityReady
    Assert-ImportSettled
    Assert-ImportFinished
    if ((Test-ProjectUsesIl2cpp) -and -not (Test-Il2cppSupport (Get-UnityEditorPath))) {
        Fail "The player uses IL2CPP, but Windows Build Support (IL2CPP) is not installed. Run 'rr install-unity'."
    }
    if (Test-ProjectUsesIl2cpp) { Assert-Tools @("buildtools") "The IL2CPP player build" }
    Complete-BuildSwap $dirs
    $stampFile = Join-Path $out $BuildStampName
    if (-not $Force -and (Test-Path -LiteralPath (Join-Path $out "RebuildClient.exe")) -and (Test-Path -LiteralPath $stampFile) -and
        (Get-Content -LiteralPath $stampFile -Raw).Trim() -eq (Get-BuildStamp).Stamp) {
        Write-Ok "the player in $out is up to date: no project file changed since it was built ('rr build-client -Force' builds anyway)"
        return
    }
    # The player is built beside the current one and swapped in once complete, so 'rr play' and
    # 'rr smoke' never start half a build.
    $staging = "$out.new"
    $symbolsStaging = "$($dirs.Symbols).new"
    foreach ($d in @($staging, $symbolsStaging)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    New-Item -ItemType Directory -Force $UnityLogDir | Out-Null
    $logFile = Join-Path $UnityLogDir "rr-buildwindows.log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force }
    $arguments = @("build", $ClientDir, "--target", "StandaloneWindows64", "--execute-method", "$AutomationClass.BuildWindows",
        "-o", $staging, "--log-file", $logFile, "--no-tail", "--non-interactive", "--no-banner")
    Write-Step "Unity: Windows player -> $out (log: $logFile)"
    $started = [DateTime]::UtcNow.Ticks
    $beforeRun = Start-UnityRun "BuildWindows"
    $proc = Start-Process -FilePath $script:UnityCliPath -ArgumentList (ConvertTo-ArgumentString $arguments) -NoNewWindow -PassThru
    $null = $proc.Handle
    $code = Watch-UnityProcess $proc $logFile "build"
    Complete-UnityRun $beforeRun $code
    if ($code -ne 0) { Fail "Build failed (exit $code). Log: $logFile" }

    # IL2CPP and Burst leave ~1 GB of debug output inside the player folder that must not ship; it
    # moves next to the build so the folder can be zipped as is and crashes can still be symbolized.
    $debugDirs = @(Get-ChildItem -LiteralPath $staging -Directory | Where-Object { $_.Name -match '_(ButDontShipItWithYourGame|DoNotShip)$' })
    if ($debugDirs.Count -gt 0) {
        New-Item -ItemType Directory -Force $symbolsStaging | Out-Null
        foreach ($d in $debugDirs) { Move-Item -LiteralPath $d.FullName -Destination $symbolsStaging }
        Write-TextAtomic (Join-Path $symbolsStaging $SwapMarker) ""
    }
    $stamp = Get-BuildStamp $started
    if ($stamp.Changed.Count -eq 0) { Write-TextAtomic (Join-Path $staging $BuildStampName) $stamp.Stamp }
    else { Write-Warn2 "$($stamp.Changed.Count) project file(s) changed while the player was building ($($stamp.Changed[0])$(if ($stamp.Changed.Count -gt 1) { ', ...' })); the next 'rr build-client' builds again" }
    Write-TextAtomic (Join-Path $staging $SwapMarker) ""
    Complete-BuildSwap $dirs
    if ($debugDirs.Count -gt 0) { Write-Ok "debug symbols moved out of the build: $($dirs.Symbols) ($(Format-Size (Get-FolderBytes $dirs.Symbols)))" }
    Write-Ok "built $(Join-Path $out 'RebuildClient.exe') ($(Format-Size (Get-FolderBytes $out)))"
}

function Invoke-Play {
    $cfg = Read-Config
    # Play takes no lock (it is fine while a bake runs), so it leaves a running build's swap alone.
    if (-not (Get-LockOwner)) { Complete-BuildSwap (Get-BuildDirs $cfg.buildOutput) }
    $exe = Join-Path (Resolve-RepoPath $cfg.buildOutput) "RebuildClient.exe"
    if (-not (Test-Path $exe)) { Fail "No build at $exe. Run 'rr build-client'." }
    Start-Process $exe
    Write-Ok "started $exe (start 'rr server' first; the login window's Server tab defaults to ws://127.0.0.1:5000/ws)"
}

function Test-Port([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.ConnectAsync("127.0.0.1", $port).Wait(500) -and $client.Connected } catch { $false } finally { $client.Dispose() }
}

# End-to-end check of a player build against a real server: account, character, map, screenshot.
# A smoke test that was cut off leaves its server on port 5000, and the next one would test against
# that server instead of starting a fresh one.
function Stop-OrphanServer {
    $s = Read-State "smoke-server"
    if (-not $s -or (Test-OwnerAlive $s)) { return }
    if ((Get-ProcessStamp $s.pid) -eq [long]$s.stamp) {
        & taskkill /PID $s.pid /T /F 2>&1 | Out-Null
        Write-Ok "stopped the server an interrupted 'rr smoke' left running (pid $($s.pid))"
    }
    Clear-State "smoke-server"
}

function Invoke-Smoke {
    $cfg = Read-Config
    if (-not (Get-LockOwner)) { Complete-BuildSwap (Get-BuildDirs $cfg.buildOutput) }
    $exe = Join-Path (Resolve-RepoPath $cfg.buildOutput) "RebuildClient.exe"
    if (-not (Test-Path $exe)) { Fail "No build at $exe. Run 'rr build-client'." }
    $server = $null
    if (Test-Port 5000) { Write-Ok "using the server already listening on port 5000" }
    else {
        Assert-Tools @("dotnet") "The server"
        $packWalk = if ($cfg.packDir) { Join-Path $cfg.packDir "walkdata" } else { $null }
        $server = @{ File = "dotnet"; Arguments = "run --launch-profile $($cfg.serverLaunchProfile)"; Dir = $ServerProjectDir
            WalkData = $(if ($packWalk -and (Test-Path $packWalk)) { $packWalk }) }
    }
    Invoke-SmokeTest $exe $server "smoke"
}

# Runs the client's -rrSmokeTest against port 5000. $server (File, Arguments, Dir, WalkData, and Env
# for settings of that run only) is started first and stopped afterwards; without it the test uses the
# server already listening. -Strict fails on error lines in the player log instead of listing them.
function Invoke-SmokeTest([string]$exe, [hashtable]$server, [string]$prefix, [switch]$Strict,
    [string]$ClientArgs, [int]$Minutes = 15, [int[]]$Screen = @(1280, 720)) {
    $cfg = Read-Config
    $outDir = if ($cfg.workDir) { Join-Path $cfg.workDir "smoke" } else { Join-Path $UnityLogDir "smoke" }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $shot = Join-Path $outDir "$prefix-$stamp.png"
    $playerLog = Join-Path $outDir "player-$stamp.log"
    $serverLog = Join-Path $outDir "server-$stamp.log"

    $proc = $null
    try {
        if ($server) {
            Write-Step "Starting the server for the smoke test (log: $serverLog)"
            if ($server.WalkData) { $env:ServerDataConfig__WalkPathData = $server.WalkData }
            if ($server.Env) { foreach ($k in $server.Env.Keys) { Set-Item "Env:\$k" $server.Env[$k] } }
            $start = @{ FilePath = $server.File; WorkingDirectory = $server.Dir; RedirectStandardOutput = $serverLog; RedirectStandardError = "$serverLog.err"; WindowStyle = "Hidden"; PassThru = $true }
            if ($server.Arguments) { $start.ArgumentList = $server.Arguments }
            try { $proc = Start-Process @start }
            finally {
                Remove-Item Env:\ServerDataConfig__WalkPathData -ErrorAction SilentlyContinue
                if ($server.Env) { foreach ($k in $server.Env.Keys) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue } }
            }
            Write-State "smoke-server" @{ pid = $proc.Id; stamp = (Get-ProcessStamp $proc.Id) }
            $deadline = (Get-Date).AddMinutes(4)
            while (-not (Test-Port 5000)) {
                if ($proc.HasExited -or (Get-Date) -gt $deadline) { Fail "server did not start; see $serverLog" }
                Start-Sleep 2
            }
            Write-Ok "server listening on port 5000"
        }

        Write-Step "Running the client smoke test (log: $playerLog)"
        $clientArgs = "-rrSmokeTest -rrSmokeShot `"$shot`" -logFile `"$playerLog`" -screen-fullscreen 0 -screen-width $($Screen[0]) -screen-height $($Screen[1]) $ClientArgs"
        $client = Start-Process -FilePath $exe -ArgumentList $clientArgs -PassThru
        $null = $client.Handle
        if (-not $client.WaitForExit($Minutes * 60 * 1000)) { $client.Kill(); Fail "client did not finish within $Minutes minutes" }
        $lines = @(Get-Content $playerLog -ErrorAction SilentlyContinue)
        $lines | Where-Object { $_ -match '\[SmokeTest\]' } | ForEach-Object { Write-Host "    | $_" }
        # Only errors up to the verdict count: logging out makes websocket-sharp log a Fatal when the
        # server drops the connection, as it does for every player who disconnects.
        $pass = [Array]::FindIndex([string[]]$lines, [Predicate[string]]{ param($l) $l -match '\[SmokeTest\] PASS' })
        $checked = if ($pass -ge 0) { $lines[0..$pass] } else { $lines }
        $errors = @($checked | Where-Object { $_ -match 'Exception|Could not load|Failed to load|InvalidKeyException' })
        if ($errors.Count -gt 0) {
            Write-Warn2 "$($errors.Count) error line(s) in the player log, first few:"
            $errors | Select-Object -First 8 | ForEach-Object { Write-Host "    | $_" }
        }
        if ($client.ExitCode -ne 0) { Fail "smoke test failed (exit $($client.ExitCode)); logs in $outDir" }
        if ($Strict -and $errors.Count -gt 0) { Fail "smoke test passed but logged errors; see $playerLog" }
        if (Test-Path -LiteralPath $shot) { Write-Ok "smoke test passed; screenshot: $shot" } else { Write-Ok "smoke test passed" }
    }
    finally {
        Remove-Item Env:\ServerDataConfig__WalkPathData -ErrorAction SilentlyContinue
        if ($proc) { & taskkill /PID $proc.Id /T /F 2>&1 | Out-Null; Clear-State "smoke-server" }
    }
}

# Screenshots of everything the pack adds, stands in or fixes (setup\showcase.json), to check by eye.
# The server it starts allows /adminify with a passcode made up for this run only; the player build
# runs the plan, and the shots go on a page with what the client measured and where each file came from.
function Invoke-Showcase {
    $cfg = Read-Config
    $plan = Join-Path $SetupDir "showcase.json"
    if ($Target) {
        $dir = Resolve-RepoPath $Target
        if (-not (Test-Path -LiteralPath (Join-Path $dir "shots.jsonl"))) { Fail "$Target has no shots.jsonl; pass a folder an earlier 'rr showcase' wrote" }
        Write-Ok "showcase: $(Write-ShowcasePage $plan $dir $cfg)"
        return
    }
    if (-not (Get-LockOwner)) { Complete-BuildSwap (Get-BuildDirs $cfg.buildOutput) }
    $exe = Join-Path (Resolve-RepoPath $cfg.buildOutput) "RebuildClient.exe"
    if (-not (Test-Path $exe)) { Fail "No build at $exe. Run 'rr build-client'." }
    if (Test-Port 5000) { Fail "port 5000 is in use. The showcase starts its own server with admin commands on; stop the running one first." }
    Assert-Tools @("dotnet") "The server"
    $root = if ($cfg.workDir) { Join-Path $cfg.workDir "showcase" } else { Join-Path $UnityLogDir "showcase" }
    $outDir = Join-Path $root (Get-Date -Format "yyyyMMdd-HHmmss")
    $pass = [guid]::NewGuid().ToString("N")
    $packWalk = if ($cfg.packDir) { Join-Path $cfg.packDir "walkdata" } else { $null }
    $server = @{ File = "dotnet"; Arguments = "run --launch-profile $($cfg.serverLaunchProfile)"; Dir = $ServerProjectDir
        WalkData = $(if ($packWalk -and (Test-Path $packWalk)) { $packWalk })
        Env = @{ ServerOperationConfig__AllowAdminifyCommand = "true"; ServerOperationConfig__AdminifyPasscode = $pass } }
    Invoke-SmokeTest $exe $server "showcase" -ClientArgs "-rrShowcase `"$plan`" -rrShowcaseOut `"$outDir`" -rrShowcasePass $pass" -Minutes 30 -Screen 1600, 900
    if (-not (Test-Path -LiteralPath (Join-Path $outDir "shots.jsonl"))) { Fail "the player build predates the showcase; run 'rr build-client', then 'rr showcase' again" }
    $page = Write-ShowcasePage $plan $outDir $cfg
    Write-Ok "showcase: $page"
}

function Get-WalkSize([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $stream = [IO.File]::OpenRead($file)
    try { $reader = New-Object IO.BinaryReader($stream); return "{0}x{1}" -f $reader.ReadInt32(), $reader.ReadInt32() }
    finally { $stream.Dispose() }
}

function Get-WavFormat([string]$file) {
    $b = [IO.File]::ReadAllBytes($file)
    $i = 12
    while ($i + 24 -le $b.Length) {
        $len = [BitConverter]::ToInt32($b, $i + 4)
        if ([Text.Encoding]::ASCII.GetString($b, $i, 4) -eq "fmt ") {
            $tag = [BitConverter]::ToUInt16($b, $i + 8)
            $name = switch ($tag) { 1 { "PCM" } 2 { "MS ADPCM" } 17 { "IMA ADPCM" } default { "format $tag" } }
            return "$name, $([BitConverter]::ToUInt16($b, $i + 22))-bit, $([BitConverter]::ToInt32($b, $i + 12)) Hz"
        }
        if ($len -lt 0) { break }
        $i += 8 + $len + ($len % 2)
    }
    return "no fmt chunk"
}

function Write-ShowcasePage([string]$planFile, [string]$outDir, $cfg) {
    $h = { param($s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
    $plan = Get-Content -LiteralPath $planFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $results = @(Get-Content -LiteralPath (Join-Path $outDir "shots.jsonl") -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $planShots = @{}
    foreach ($s in $plan.shots) { $planShots[$s.id] = $s }
    # Only the entries the page names: Windows PowerShell's ConvertFrom-Json can't take the whole manifest.
    $manifest = @{}
    $manifestFile = if ($cfg.packDir) { Join-Path $cfg.packDir "manifest.json" } else { $null }
    if ($manifestFile -and (Test-Path -LiteralPath $manifestFile)) {
        $text = [IO.File]::ReadAllText($manifestFile, [Text.Encoding]::UTF8)
        foreach ($p in @($plan.shots | ForEach-Object { $_.files }) + @($plan.sounds | ForEach-Object { $_.file })) {
            if (-not $p -or $manifest.ContainsKey($p)) { continue }
            $m = [regex]::Match($text, '\{"Path":"' + [regex]::Escape($p) + '"[^{}]*\}')
            if ($m.Success) { $manifest[$p] = $m.Value | ConvertFrom-Json }
        }
    }
    $packWalk = if ($cfg.packDir) { Join-Path $cfg.packDir "walkdata" } else { $null }
    $refWalk = $cfg.referenceWalk
    $fileUri = { param($p) ([uri](Resolve-RepoPath $p)).AbsoluteUri }

    $provenance = {
        param($paths)
        $rows = foreach ($p in @($paths | Where-Object { $_ })) {
            $e = $manifest[$p]
            if (-not $e) { "<li><code>$(& $h $p)</code>: <span class='bad'>not in the pack</span></li>"; continue }
            $from = if ($e.SourcePath -and $e.SourcePath -ne $e.Path) { "$($e.Source), as <code>$(& $h $e.SourcePath)</code>" } else { $e.Source }
            $note = if ($e.Note) { ". $(& $h $e.Note)" } else { "" }
            "<li><code>$(& $h $p)</code>: from $from$note</li>"
        }
        if ($rows) { "<ul class='files'>$($rows -join '')</ul>" } else { "" }
    }
    $walkCheck = {
        param($map)
        if (-not $map -or -not $packWalk) { return "" }
        $server = Join-Path $packWalk "$map.walk"
        if (-not (Test-Path -LiteralPath $server)) { return "server walk file: <span class='bad'>missing</span>" }
        $text = "server walk file $(Get-WalkSize $server)"
        $release = if ($refWalk) { Join-Path $refWalk "$map.walk" } else { $null }
        if ($release -and (Test-Path -LiteralPath $release)) {
            $same = (Get-FileHash -LiteralPath $server).Hash -eq (Get-FileHash -LiteralPath $release).Hash
            $text += if ($same) { ", <span class='ok'>identical to Doddler's release</span>" } else { ", <span class='bad'>differs from Doddler's release</span>" }
        }
        return $text
    }

    $flagged = @($results | Where-Object { $_.error }).Count
    $body = New-Object System.Collections.Generic.List[string]
    $groups = [ordered]@{}
    foreach ($r in $results) { if (-not $groups.Contains($r.group)) { $groups[$r.group] = New-Object System.Collections.Generic.List[object] }; $groups[$r.group].Add($r) }
    $links = (@($groups.Keys) + @("Sounds", "Server only", "Not shown")) | ForEach-Object { "<a href='#$(& $h ($_ -replace '\W', '-'))'>$(& $h $_)</a>" }
    $body.Add("<nav>$($links -join ' ')</nav>")
    foreach ($g in $groups.Keys) {
        $body.Add("<h2 id='$(& $h ($g -replace '\W', '-'))'>$(& $h $g)</h2><div class='grid'>")
        foreach ($r in $groups[$g]) {
            $p = $planShots[$r.id]
            $facts = @()
            if ($r.subject) { $facts += "subject: $(& $h $r.subject)" }
            if ($r.map) { $facts += "map <code>$(& $h $r.map)</code>" + $(if ($r.size) { " $($r.size), $($r.water) water cells" } else { "" }) }
            if ($r.audio) { $facts += "playing: $(& $h $r.audio)" }
            if (@($p.files) -match '\.gat$') { $w = & $walkCheck $r.map; if ($w) { $facts += $w } }
            $check = if ($r.error) { "<p class='bad'>Check: $(& $h $r.error)</p>" } elseif ($p.expect) { "<p class='ok'>Check passed: found &ldquo;$(& $h $p.expect)&rdquo;</p>" } else { "" }
            $body.Add(@"
<figure><a href='$(& $h $r.file)'><img src='$(& $h $r.file)' loading='lazy'></a>
<figcaption><b>$(& $h $r.file.Substring(0, 2)). $(& $h $r.title)</b><p>$(& $h $r.look)</p>
<p class='facts'>Measured in game: $($facts -join ' &middot; ')</p>$check$(& $provenance $p.files)</figcaption></figure>
"@)
        }
        $body.Add("</div>")
    }

    $body.Add("<h2 id='Sounds'>Sounds</h2><p>A still can't show these; the players below are the project's own copies, the ones the game plays.</p><table><tr><th>Sound</th><th>Listen</th><th>Checked</th></tr>")
    foreach ($s in $plan.sounds) {
        $file = Resolve-RepoPath $s.project
        $checks = @()
        if (-not (Test-Path -LiteralPath $file)) { $checks += "<span class='bad'>not in the project</span>" }
        else {
            if ($file -match '\.wav$') { $checks += "format: $(Get-WavFormat $file)" }
            if ($s.same) {
                $other = Resolve-RepoPath $s.same
                $same = (Test-Path -LiteralPath $other) -and (Get-FileHash -LiteralPath $file).Hash -eq (Get-FileHash -LiteralPath $other).Hash
                $checks += if ($same) { "<span class='ok'>same bytes as <code>$(& $h (Split-Path $other -Leaf))</code></span>" } else { "<span class='bad'>differs from <code>$(& $h (Split-Path $other -Leaf))</code></span>" }
            }
        }
        $body.Add("<tr><td><b>$(& $h $s.title)</b><br>$(& $h $s.note)$(& $provenance @($s.file))</td><td><audio controls preload='none' src='$(& $fileUri $s.project)'></audio></td><td>$($checks -join '<br>')</td></tr>")
    }
    $body.Add("</table>")

    $body.Add("<h2 id='Server-only'>Server only</h2><p>These maps have no client scene, so there's nothing to see in game. The server's walk file is what matters.</p><table><tr><th>Map</th><th>Note</th><th>Checked</th></tr>")
    foreach ($m in $plan.serverOnly) { $body.Add("<tr><td><code>$(& $h $m.map)</code></td><td>$(& $h $m.note)</td><td>$(& $walkCheck $m.map)</td></tr>") }
    $body.Add("</table>")

    $report = if ($cfg.packDir) { Join-Path $cfg.packDir "report.html" } else { $null }
    $reportLink = if ($report -and (Test-Path -LiteralPath $report)) { " The pack report lists them: <a href='$(([uri]$report).AbsoluteUri)'>report.html</a>." } else { "" }
    $body.Add("<h2 id='Not-shown'>Not shown</h2><p>Fixes with nothing to see in game.$reportLink</p><ul>$((@($plan.notShown) | ForEach-Object { "<li>$(& $h $_)</li>" }) -join '')</ul>")

    $summary = "$($results.Count) shots" + $(if ($flagged) { ", <span class='bad'>$flagged flagged</span>" } else { ", <span class='ok'>all checks passed</span>" })
    $html = @"
<!doctype html><html><head><meta charset='utf-8'><title>Rebuild showcase $(Split-Path $outDir -Leaf)</title><style>
body{font:15px/1.5 system-ui,sans-serif;margin:0 auto;max-width:1500px;padding:0 24px 60px;background:#111;color:#ddd}
h1{margin:24px 0 4px}h2{margin-top:40px;border-bottom:1px solid #333;padding-bottom:4px}
nav{position:sticky;top:0;background:#111;padding:8px 0;border-bottom:1px solid #333;z-index:1}nav a{margin-right:14px}
a{color:#8bf}code{background:#222;padding:1px 4px;border-radius:3px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(680px,1fr));gap:24px}
figure{margin:0;background:#1a1a1a;border:1px solid #2a2a2a;border-radius:6px;overflow:hidden}
figure img{width:100%;display:block}figcaption{padding:10px 14px}figcaption p{margin:6px 0}
.facts{color:#aaa;font-size:13px}.files{font-size:13px;color:#aaa;margin:6px 0;padding-left:18px}
.ok{color:#7d7}.bad{color:#f77;font-weight:600}
table{border-collapse:collapse;width:100%}td,th{border:1px solid #333;padding:8px;vertical-align:top;text-align:left}
</style></head><body>
<h1>Ragnarok Rebuild showcase</h1>
<p>$(Get-Date -Format "yyyy-MM-dd HH:mm") &middot; $summary &middot; Each screenshot has its caption at the bottom right and a yellow box around what to look at. Click a shot for full size.</p>
$($body -join "`n")
</body></html>
"@
    $page = Join-Path $outDir "index.html"
    Write-TextAtomic $page $html
    return $page
}

# Sets one value in a settings file that may carry // comments, keeping the comments.
function Set-JsonSetting([string]$json, [string]$key, [string]$value) {
    $pattern = '("' + [regex]::Escape($key) + '"\s*:\s*)("(?:[^"\\]|\\.)*"|true|false|-?[\d.]+)'
    $found = [regex]::Matches($json, $pattern).Count
    if ($found -ne 1) { Fail "appsettings.json has $found '$key' settings; expected one" }
    return [regex]::Replace($json, $pattern, '${1}' + $value.Replace('$', '$$'))
}

function Get-ReleasePlayCmd {
    return @(
        '@echo off'
        'rem Starts the server in its own window unless one is already running, waits until it accepts'
        'rem connections, then starts the client.'
        'setlocal'
        'call :listening && goto client'
        'echo Starting the server. When you are done, stop it with Ctrl+C in its window so it saves everything.'
        'start "Ragnarok Rebuild server" /d "%~dp0Server" "%~dp0Server\RoRebuildServer.exe"'
        'set tries=0'
        ':wait'
        'ping -n 3 127.0.0.1 >nul'
        'call :listening && goto client'
        'set /a tries+=1'
        'if %tries% lss 90 goto wait'
        'echo The server did not start within 3 minutes. Its window and Server\Logs say why.'
        'pause'
        'exit /b 1'
        ':client'
        'start "" /d "%~dp0Client" "%~dp0Client\RebuildClient.exe"'
        'exit /b 0'
        ':listening'
        'powershell -NoProfile -Command "$c = New-Object Net.Sockets.TcpClient; try { if ($c.ConnectAsync(''127.0.0.1'', 5000).Wait(500) -and $c.Connected) { exit 0 } } catch { }; exit 1"'
        'exit /b %errorlevel%'
    )
}

function Get-ReleaseReadme([hashtable]$v) {
    $text = @'
Ragnarok Rebuild
================

Doddler's Ragnarok Rebuild, client and server, ready to play on one Windows PC or over a LAN.
Ragnarok Rebuild is Doddler's project: https://github.com/Doddler/RagnarokRebuildTcp
This release was made on {date} by 'rr release' from {fork}.

Nothing needs installing: the server carries its own .NET runtime. You need 64-bit Windows 10 or
11 and a graphics card that supports DirectX 11.

Playing
-------
1. Run Play.cmd. It starts the server in its own window, waits until it is ready (a few seconds),
   then starts the client.
2. On the login screen, make an account on the second tab, then log in with it. The third tab
   holds the server address, ws://127.0.0.1:5000/ws (this PC).
3. When you are done, close the client, then stop the server with Ctrl+C in its window so it
   saves everything before it exits.

You can also start Server\RoRebuildServer.exe and Client\RebuildClient.exe yourself.

Admin commands
--------------
Type "/adminify {passcode}" in chat to make your character an admin until you log out. The
passcode is AdminifyPasscode in Server\appsettings.json, picked at random for each release;
AllowAdminifyCommand turns the command off. Doddler's list of admin commands:

  Shift+R: resurrect in place
  Warp to map: /warp MapName
  Summon monsters: /summon Monster Name ###
  Create Item: /item Item_Name
  Refine Gear (replace slot with head/armor/weapon/shield/garment/footgear): /refine slot #
  Hide from view: /hide
  Skill reset: /skillreset
  Stat reset: /statreset
  Change Job (# is value between 0-6): /change job #
  Change Hairstyle (first value is hair type, second is hair color): /change hair ## ##
  Level up: /level ##
  Job up: /joblevel ##
  Change speed (150 is default, lower is faster): /speed ##
  God mode: /godmode
  Kill monsters on screen: /kill
  Kill all monsters on map: /killall
  Find target on current map (use full monster code to search for monster): /find Name
  Reload server data: /reloadscript

Server
------
- Settings are in Server\appsettings.json, with comments. Game data (monsters, items, skills,
  scripts) is in Server\ServerData.
- Characters are saved in Server\RoCharacterDatabase.db, an SQLite file made on the first start.
  Delete it to reset the server.
- Logs are in Server\Logs.
- Doddler's custom monsters replace some spawns when "DoddlerCustomMonsters" is added to
  FeatureFlags. They need his custom sprites, which this client has only if it was built with
  them.

Playing over a LAN
------------------
1. In Server\appsettings.json, change "urls" to "http://0.0.0.0:5000" and change the adminify
   passcode.
2. Start the server and let Windows Firewall allow it on private networks when it asks.
3. Other players copy the Client folder and run RebuildClient.exe. On the login screen's third
   tab they enter ws://<this PC's IP address>:5000/ws.

Contents
--------
Client\       the game client
Server\       the server, with .NET {dotnet} and walk data for {walk} maps
Play.cmd      starts both
version.txt   what this release was built from
'@
    foreach ($k in $v.Keys) { $text = $text.Replace("{$k}", "$($v[$k])") }
    return $text
}

# A release in the layout of Doddler's (Client, Server, readme), made from this checkout: the
# player build, a self-contained server with the pack's walk data, and Play.cmd. It is checked
# with its own server and client before it is archived.
function Invoke-Release {
    $cfg = Read-Config
    if (-not $cfg.workDir) { Fail "workDir is not set in config.local.json." }
    $walk = if ($cfg.packDir) { Join-Path $cfg.packDir "walkdata" } else { "" }
    if (-not $walk -or -not (Test-Path (Join-Path $walk "*.walk"))) { Fail "No server walk data in the pack. Run 'rr pack' first." }
    if (Test-Port 5000) { Fail "A server is listening on port 5000. Stop it first: the release is checked with its own server." }
    Assert-Tools @("dotnet") "The release's server"

    $head = "$(Invoke-Git @("rev-parse", "HEAD") | Select-Object -First 1)".Trim()
    $dirty = @(Invoke-Git @("status", "--porcelain", "--untracked-files=no")).Count -gt 0
    $branch = "$(Invoke-Git @("rev-parse", "--abbrev-ref", "HEAD") | Select-Object -First 1)".Trim()
    $origin = "$(Invoke-Git @("remote", "get-url", "origin") -AllowFail | Select-Object -First 1)".Trim()
    $fork = if ($origin -match 'github\.com[:/](.+?)(\.git)?$') { "https://github.com/$($Matches[1])" } else { "this fork" }
    $name = "RagnarokRebuild-$(Get-Date -Format 'yyyy-MM-dd')-$($head.Substring(0, 8))"
    $root = if ($Target) { Resolve-RepoPath $Target } else { Join-Path $cfg.workDir "release" }
    $dest = Join-Path $root $name
    $stage = "$dest.staging"
    # A stopped release starts over; its half-made folder or archive can carry an earlier date.
    # 7-Zip writes its own "<archive>.tmp" beside the temp archive it was given.
    Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "RagnarokRebuild-*.staging" -or $_.Name -like "RagnarokRebuild-*$TempSuffix" -or $_.Name -like "RagnarokRebuild-*$TempSuffix.tmp" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    if (-not $NoBuild) { $script:Target = $null; Invoke-Build }
    Complete-BuildSwap (Get-BuildDirs $cfg.buildOutput)
    $build = Resolve-RepoPath $cfg.buildOutput
    if (-not (Test-Path (Join-Path $build "RebuildClient.exe"))) { Fail "No player build at $build. Run 'rr release' without -NoBuild." }
    $provenance = Get-Content (Join-Path $build "unity-build.provenance.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json

    Write-Step "Client: $build -> $stage"
    Invoke-Robocopy $build (Join-Path $stage "Client")
    Remove-Item (Join-Path $stage "Client\unity-build.provenance.json"), (Join-Path $stage "Client\$SwapMarker"), (Join-Path $stage "Client\$BuildStampName") -Force -ErrorAction SilentlyContinue

    Write-Step "Server: self-contained publish for win-x64"
    $server = Join-Path $stage "Server"
    Invoke-Native "dotnet" @("publish", (Join-Path $ServerProjectDir "RoRebuildServer.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-o", $server, "-nologo", "-v", "quiet", "-p:WarningLevel=0", "-p:SatelliteResourceLanguages=en") -Quiet | Out-Null
    # Development and Minimal are for working on the server; Production adds an https endpoint on
    # port 443, which needs a certificate this PC may not have.
    foreach ($f in "appsettings.Development.json", "appsettings.Minimal.json", "appsettings.Production.json", "web.config") {
        Remove-Item (Join-Path $server $f) -Force -ErrorAction SilentlyContinue
    }
    Invoke-Robocopy (Join-Path $ServerRoot "GameConfig\ServerData") (Join-Path $server "ServerData") -Mirror
    Invoke-Robocopy $walk (Join-Path $server "walkdata") -Mirror
    $passcode = -join ((48..57) + (97..122) | Get-Random -Count 12 | ForEach-Object { [char]$_ })
    $settingsPath = Join-Path $server "appsettings.json"
    $settings = Get-Content $settingsPath -Raw -Encoding UTF8
    $settings = Set-JsonSetting $settings "DataPath" '"./ServerData/"'
    $settings = Set-JsonSetting $settings "WalkPathData" '"./walkdata/"'
    $settings = Set-JsonSetting $settings "AllowAdminifyCommand" 'true'
    $settings = Set-JsonSetting $settings "AdminifyPasscode" "`"$passcode`""
    Write-TextAtomic $settingsPath $settings
    $runtime = (Get-Content (Join-Path $server "RoRebuildServer.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
        Where-Object { $_.name -eq "Microsoft.NETCore.App" } | Select-Object -First 1 -ExpandProperty version
    $walkCount = @(Get-ChildItem (Join-Path $server "walkdata") -Filter "*.walk" -File).Count

    $packSig = Get-PackSignature
    $client = if ($provenance) {
        $at = ([datetime]$provenance.endedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        "built $at with Unity $($provenance.editor.version) from $($provenance.source.revision.Substring(0, 8))$(if ($provenance.source.dirty) { ' with uncommitted changes' })"
    } else { "built with Unity $(Get-UnityVersion) (no build record)" }
    $crlf = "`r`n"
    Write-TextAtomic (Join-Path $stage "Play.cmd") (((Get-ReleasePlayCmd) -join $crlf) + $crlf)
    Write-TextAtomic (Join-Path $stage "readme.txt") ((Get-ReleaseReadme @{ date = (Get-Date -Format "yyyy-MM-dd"); fork = $fork; passcode = $passcode; dotnet = $runtime; walk = $walkCount }) -replace "`r?`n", $crlf)
    Write-TextAtomic (Join-Path $stage "version.txt") ((@(
        "release   $name"
        "made      $(Get-Date -Format 'yyyy-MM-dd HH:mm') from $fork, branch $branch, commit $($head.Substring(0, 8))$(if ($dirty) { ' with uncommitted changes' })"
        "client    $client"
        "server    .NET $runtime, self-contained, walk data for $walkCount maps"
        "pack      $(if ($packSig) { $packSig } else { 'unknown' }) (rebuild-pack.grf signature)"
    ) -join $crlf) + $crlf)

    Write-Step "Checking the release with its own server and client"
    $before = @{}
    Get-ChildItem -LiteralPath $stage -Recurse -Force | ForEach-Object { $before[$_.FullName] = $true }
    Invoke-SmokeTest (Join-Path $stage "Client\RebuildClient.exe") @{ File = (Join-Path $server "RoRebuildServer.exe"); Dir = $server } "release" -Strict
    # The check leaves a database, keys and logs behind; only the compiled script cache ships, so
    # the first start doesn't compile the scripts.
    $cache = Join-Path $server "Cache"
    Get-ChildItem -LiteralPath $stage -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not $before.ContainsKey($_.FullName) -and $_.FullName -ne $cache -and -not $_.FullName.StartsWith("$cache\", [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Rename-Item -LiteralPath $stage -NewName $name
    Write-Ok "release folder: $dest ($(Format-Size (Get-FolderBytes $dest)))"

    $sevenZip = Get-SevenZip
    $archive = if ($sevenZip) { "$dest.7z" } else { "$dest.zip" }
    $temp = $archive + $TempSuffix
    Write-Step "Archiving $archive"
    if ($sevenZip) {
        Push-Location $root
        try { Invoke-Native $sevenZip @("a", "-t7z", "-mx=5", "-mmt=on", $temp, $name) -Quiet | Out-Null } finally { Pop-Location }
    } else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($dest, $temp, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    }
    Move-FileOver $temp $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TextAtomic "$archive.sha256" "$hash  $(Split-Path $archive -Leaf)`n"
    Write-Ok "archive: $archive ($(Format-Size (Get-Item -LiteralPath $archive).Length)), SHA-256 $hash"
}

function Invoke-Editor {
    $cli = Get-UnityCli
    if (-not $cli) { Fail "Unity CLI not installed. Run 'rr install-unity'." }
    Assert-NothingHalfDone
    Write-Step "Opening RebuildClient in Unity $(Get-UnityVersion)"
    & $cli open $ClientDir
}

function Invoke-Doctor {
    $cfg = Read-Config
    Write-Step "Git"
    $remotes = "$(Invoke-Git @("remote", "-v"))"
    if ($remotes -match "upstream\s+\S*Doddler/RagnarokRebuildTcp") { Write-Ok "upstream -> Doddler" } else { Write-Warn2 "upstream remote missing (rr git-setup)" }
    if ($remotes -match "origin\s+(\S+)") { Write-Ok "origin -> $($Matches[1])" } else { Write-Warn2 "origin remote missing (rr git-setup)" }
    $branch = "$(Invoke-Git @("branch", "--show-current"))".Trim()
    if ($branch -eq "master" -or $branch -eq "main") { Write-Warn2 "on '$branch'; work on '$($cfg.workBranch)'" } else { Write-Ok "branch $branch" }
    $hooksDir = "$(Invoke-Git @("rev-parse", "--git-path", "hooks"))".Trim()
    if (-not [System.IO.Path]::IsPathRooted($hooksDir)) { $hooksDir = Join-Path $RepoRoot $hooksDir }
    if (Test-Path (Join-Path $hooksDir "pre-push")) { Write-Ok "master/main guard hooks installed" } else { Write-Warn2 "hooks not installed (rr git-setup)" }
    $behind = "$(Invoke-Git @("rev-list", "--count", "HEAD..upstream/master") -AllowFail)".Trim()
    if ($behind -match '^\d+$' -and [int]$behind -gt 0) { Write-Warn2 "$behind upstream commit(s) not merged (rr sync)" } elseif ($behind -eq "0") { Write-Ok "up to date with upstream/master (as of last fetch)" }

    Write-Step "Tools"
    $toolUses = [ordered]@{ git = ""; dotnet = "" }
    if (Test-NeedsSevenZip $cfg) { $toolUses.sevenzip = "; rr reference needs it for releaseArchive" }
    foreach ($n in $toolUses.Keys) {
        if (-not (Test-Tool $n)) { Write-Warn2 "$($Tools[$n].Label) missing$($toolUses[$n]) (rr prereqs installs it)" }
        elseif ($n -eq "dotnet") {
            $runtime = [regex]::Matches((Get-NativeOutput "dotnet" @("--list-runtimes")), '(?m)^Microsoft\.AspNetCore\.App (9\.\S+)') | Select-Object -Last 1
            Write-Ok ".NET SDK $((Get-NativeOutput "dotnet" @("--version")).Trim()), .NET 9 runtime $($runtime.Groups[1].Value)"
        }
        else { Write-Ok $Tools[$n].Label }
    }
    $cli = Get-UnityCli
    if ($cli) { Write-Ok "Unity CLI: $cli" } else { Write-Warn2 "Unity CLI missing (rr install-unity)" }
    $editor = Get-UnityEditorPath
    if ($editor) { Write-Ok "Unity $(Get-UnityVersion): $editor" } else { Write-Warn2 "Unity $(Get-UnityVersion) missing (rr install-unity)" }
    if ($editor -and (Test-ProjectUsesIl2cpp)) {
        if (Test-Il2cppSupport $editor) { Write-Ok "Windows IL2CPP build support (player backend)" } else { Write-Warn2 "Windows IL2CPP build support missing; rr build-client needs it (rr install-unity)" }
        if (Test-Tool "buildtools") { Write-Ok "MSVC C++ toolchain for IL2CPP" } else { Write-Warn2 "MSVC C++ toolchain missing; rr build-client needs it (rr prereqs installs it)" }
    }
    if ($cli) {
        $lic = Get-NativeOutput $cli @("license", "list", "--non-interactive")
        if ($lic -match "Unity (Personal|Pro|Plus|Enterprise)") { Write-Ok "Unity license present" } else { Write-Warn2 "no Unity license (rr install-unity)" }
    }

    Write-Step "Data"
    foreach ($s in @($cfg.packSources)) {
        if (-not $s) { continue }
        if (Test-Path -LiteralPath $s.path) { Write-Ok "source $($s.name): $($s.path)" } else { Write-Warn2 "source $($s.name) missing: $($s.path)" }
    }
    $refCount = if ($cfg.referenceWalk) { @(Get-ChildItem -LiteralPath $cfg.referenceWalk -Filter *.walk -ErrorAction SilentlyContinue).Count } else { 0 }
    $packGrf = Get-PackGrfSource $cfg
    if ($refCount -gt 0) { Write-Ok "reference walk data: $refCount maps" }
    elseif ($packGrf) { Write-Ok "reference walk data: inside $($packGrf.path)" }
    else { Write-Warn2 "no reference walk data (rr reference)" }
    $manifest = if ($cfg.packDir) { Join-Path $cfg.packDir "manifest.json" } else { "" }
    if ($manifest -and (Test-Path -LiteralPath $manifest)) {
        $m = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Ok ("pack: {0:N0} files, {1} in {2} (built {3})" -f $m.files, (Format-Size $m.bytes), $cfg.packDir, ([datetime]$m.generatedUtc).ToLocalTime())
        $mine = Get-FingerprintSignature (Join-Path $cfg.packDir "fingerprint.tsv")
        $fork = Get-FingerprintSignature $ForkFingerprint
        if (-not $fork) { Write-Host "        no fork fingerprint to compare with (rr fingerprint writes setup\pack\fingerprint.tsv)" }
        elseif (-not $mine) { Write-Host "        pack predates fingerprints; the next rr pack compares it with the fork's pack" }
        elseif ($mine -eq $fork) { Write-Ok "pack files are identical to the fork's pack" }
        else { Write-Warn2 "pack files differ from the fork's pack; report.html lists which, and the client the fork took each from" }
    } else { Write-Warn2 "no pack yet (rr pack)" }

    Write-Step "Client"
    $mapsJson = Join-Path $ClientDir "Assets\StreamingAssets\ClientConfigGenerated\maps.json"
    if (Test-Path $mapsJson) {
        $codes = (Get-Content $mapsJson -Raw | ConvertFrom-Json).Items | ForEach-Object { $_.Code }
        $imported = @($codes | Where-Object { Test-Path (Join-Path $ClientDir "Assets\Scenes\Maps\$_.unity") }).Count
        $lit = @($codes | Where-Object { Test-Path (Join-Path $ClientDir "Assets\Scenes\Maps\$_\*.exr") }).Count
        Write-Ok "generated config present; maps imported $imported/$($codes.Count), lit $lit/$($codes.Count)"
    } else { Write-Warn2 "generated client config missing (rr update-client)" }
    $music = @(Get-ChildItem (Join-Path $ClientDir "Assets\Music") -File -ErrorAction SilentlyContinue | Where-Object Extension -ne ".meta").Count
    if ($music -gt 0) { Write-Ok "music files: $music" } else { Write-Warn2 "no music in Assets\Music (rr import copies it)" }
    $exe = Join-Path (Resolve-RepoPath $cfg.buildOutput) "RebuildClient.exe"
    if (Test-Path $exe) { Write-Ok "player build: $exe" } else { Write-Host "    --  no player build yet (rr build-client)" }

    Write-Step "Disk"
    $drives = @($RepoRoot, $cfg.workDir, $cfg.packDir, $cfg.libraryDir, "C:\") | Where-Object { $_ } | ForEach-Object { [System.IO.Path]::GetPathRoot($_) } | Sort-Object -Unique
    foreach ($d in $drives) {
        $free = Get-FreeBytes $d
        $line = "{0} free {1}" -f $d, (Format-Size $free)
        if ($free -ge 0 -and $free -lt 15GB) { Write-Warn2 $line } else { Write-Ok $line }
    }

    Write-Step "Interrupted work"
    $owner = Get-LockOwner
    if ($owner) { Write-Warn2 "'rr $($owner.command)' is running now (pid $($owner.ownerPid), since $($owner.since))" }
    $notes = @(Get-InterruptedNotes)
    $notes | ForEach-Object { Write-Warn2 $_ }
    if ($notes.Count -eq 0) { Write-Ok "nothing left half done" }
}

function Invoke-Disk {
    $cfg = Read-Config
    $rows = @(
        @{ Name = "Unity Library (cache)"; Path = Join-Path $ClientDir "Library" },
        @{ Name = "Imported maps"; Path = Join-Path $ClientDir "Assets\Maps" },
        @{ Name = "Map scenes + lightmaps"; Path = Join-Path $ClientDir "Assets\Scenes\Maps" },
        @{ Name = "Imported sprites"; Path = Join-Path $ClientDir "Assets\Sprites" },
        @{ Name = "Imported models"; Path = Join-Path $ClientDir "Assets\Models" },
        @{ Name = "Imported sounds"; Path = Join-Path $ClientDir "Assets\Sounds" },
        @{ Name = "Music"; Path = Join-Path $ClientDir "Assets\Music" },
        @{ Name = "Effects"; Path = Join-Path $ClientDir "Assets\Effects" },
        @{ Name = "Player build"; Path = Resolve-RepoPath $cfg.buildOutput },
        @{ Name = "Player debug symbols"; Path = "$((Resolve-RepoPath $cfg.buildOutput).TrimEnd('\'))-symbols" },
        @{ Name = "Releases"; Path = $(if ($cfg.workDir) { Join-Path $cfg.workDir "release" }) },
        @{ Name = "Unity Temp/Logs"; Path = Join-Path $ClientDir "Temp" },
        @{ Name = "Server bin/obj"; Path = $ServerRoot; Filter = "bin|obj" },
        @{ Name = "Rebuild pack"; Path = $cfg.packDir },
        @{ Name = "Reference walk data"; Path = $cfg.referenceWalk },
        @{ Name = "Custom files"; Path = $cfg.customDir },
        @{ Name = "git objects"; Path = Join-Path $RepoRoot ".git" }
    )
    Write-Step "Disk usage"
    $total = 0L
    foreach ($r in $rows) {
        if (-not $r.Path) { continue }
        if ($r.Filter) {
            $bytes = 0L
            Get-ChildItem $r.Path -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "^($($r.Filter))$" } | ForEach-Object { $bytes += Get-FolderBytes $_.FullName }
        } else { $bytes = Get-FolderBytes $r.Path }
        $total += $bytes
        Write-Host ("    {0,-26} {1,10}   {2}" -f $r.Name, (Format-Size $bytes), $r.Path)
    }
    Write-Host ("    {0,-26} {1,10}" -f "total", (Format-Size $total))
    $cli = Get-UnityCli
    if ($cli) { Write-Host "    Unity CLI download cache: $((Get-NativeOutput $cli @("cache", "info", "--non-interactive")).Trim() -replace '\s+', ' ')" }
    Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 -and $_.Root -match '^[A-Z]:\\$' } | ForEach-Object {
        Write-Host ("    drive {0} free {1}" -f $_.Root, (Format-Size $_.Free))
    }
}

# Moves RebuildClient\Library (Unity's import cache, the largest folder after a full import) to
# another drive and leaves a directory junction behind, so the repo can stay on a small drive.
function Invoke-LibraryLink {
    $cfg = Read-Config
    # A move cut off halfway leaves the Library split across two folders; it can only be finished
    # towards the destination it started with (robocopy /MOVE resumes where it stopped).
    $pending = Read-State "library-move"
    $dest = if ($Target) { $Target } elseif ($pending) { $pending.to } else { $cfg.libraryDir }
    if (-not $dest) { Fail "Pass a destination (rr library-link D:\RagnarokRebuildData\Library) or set libraryDir in config.local.json." }
    $dest = [System.IO.Path]::GetFullPath($dest)
    if ($pending -and $pending.to -ne $dest) { Fail "The interrupted move to $($pending.to) is not finished; run 'rr library-link' without a folder to finish it first." }
    if (Test-UnityProjectOpen) { Fail "Close Unity before moving the Library." }

    $lib = Join-Path $ClientDir "Library"
    $item = Get-Item -LiteralPath $lib -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dest | Out-Null
    if ($item -and $item.LinkType -eq "Junction") {
        $current = @($item.Target)[0]
        if ($current -eq $dest) { Clear-State "library-move"; Write-Ok "Library already linked to $dest"; return }
        Write-State "library-move" @{ to = $dest; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
        Write-Step "Moving Library from $current to $dest"
        & robocopy $current $dest /E /MOVE /COPY:DAT /DCOPY:DAT /MT:16 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy failed ($LASTEXITCODE); Library left at $current." }
        [System.IO.Directory]::Delete($lib)
        $item = $null
    }

    if ($item) {
        Write-State "library-move" @{ to = $dest; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
        Write-Step "Moving Library ($(Format-Size (Get-FolderBytes $lib))) to $dest"
        & robocopy $lib $dest /E /MOVE /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy failed ($LASTEXITCODE); rerun 'rr library-link' to finish the move." }
        if (Test-Path -LiteralPath $lib) { Remove-Item -LiteralPath $lib -Recurse -Force }
    }
    New-Item -ItemType Junction -Path $lib -Target $dest | Out-Null
    Save-LocalConfig @{ libraryDir = $dest }
    Clear-State "library-move"
    Write-Ok "RebuildClient\Library -> $dest (junction)"
}

# Everything the Unity import produces, so a restore replaces hours of importing with a copy.
$ImportedFolders = @("Sprites", "Sounds", "Maps", "Models", "Scenes/Maps", "Effects", "Textures/Import", "Music") | ForEach-Object { "RebuildClient/Assets/$_" }

# Build output and caches in the Library (up to ~25 GB after player builds); a snapshot only needs
# the import state. Excluded directories are neither copied nor purged by a mirror.
$LibraryBuildCaches = @("BuildCache", "Bee", "com.unity.addressables", "BurstCache", "PlayerDataCache", "ShaderCache")

# RebuildAutomation's records of interrupted imports, bakes and minimaps: state of this machine
# only, never copied into or out of a snapshot.
$LibraryJournal = "rr-journal"

function Get-LibraryTarget {
    $lib = Join-Path $ClientDir "Library"
    $item = Get-Item -LiteralPath $lib -Force -ErrorAction SilentlyContinue
    if ($item -and $item.LinkType -eq "Junction") { return @($item.Target)[0] }
    return $lib
}

function Invoke-Robocopy([string]$from, [string]$to, [switch]$Mirror, [string[]]$ExcludeDirs = @()) {
    $mode = if ($Mirror) { "/MIR" } else { "/E" }
    # Bare names, so they also protect the destination's copies from the mirror's purge.
    $exclude = if ($ExcludeDirs.Count -gt 0) { @("/XD") + $ExcludeDirs } else { @() }
    & robocopy $from $to $mode /COPY:DAT /DCOPY:DAT /MT:16 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP @exclude | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "robocopy $from -> $to failed ($LASTEXITCODE)" }
}

function Invoke-Snapshot {
    $cfg = Read-Config
    if (-not $cfg.workDir) { Fail "workDir is not set in config.local.json." }
    $root = Join-Path $cfg.workDir "snapshots"
    $dir = Join-Path $root $Name
    $action = if ($Target) { $Target.ToLowerInvariant() } else { "list" }
    if ($action -ne "list" -and (Test-UnityProjectOpen)) { Fail "Close Unity first." }

    switch ($action) {
        "list" {
            Write-Step "Snapshots in $root"
            foreach ($s in Get-ChildItem $root -Directory -ErrorAction SilentlyContinue) {
                $infoFile = Join-Path $s.FullName "snapshot.json"
                if (-not (Test-Path -LiteralPath $infoFile)) { Write-Host ("    {0,-12} incomplete: the save was interrupted (rerun 'rr snapshot save -Name {0}')" -f $s.Name); continue }
                $info = Get-Content $infoFile -Raw -Encoding UTF8 | ConvertFrom-Json
                Write-Host ("    {0,-12} {1}  commit {2}  {3}{4}" -f $s.Name, $info.created, $info.commit, (Format-Size $info.bytes), $(if ($info.library) { " (with Library)" } else { "" }))
            }
        }
        "save" {
            # The journals that repair these stay behind in Library, so a snapshot would keep the damage.
            $bake = Get-UnityJournal "bake"
            if ($bake -and $bake.current) { Fail "The lighting bake stopped while baking $($bake.current). Run 'rr bake' (or 'rr report') first, which puts that map back." }
            if ((Get-UnityJournal "import").phase -eq "maps") { Fail "An import stopped among the maps. Run 'rr import' first." }
            Assert-ImportFinished
            Write-Step "Saving client snapshot '$Name' to $dir"
            New-Item -ItemType Directory -Force $dir | Out-Null
            # snapshot.json goes first and comes back last, so a save cut off halfway is never taken
            # for a snapshot; rerunning it resumes, since robocopy skips what is already copied.
            Remove-Item -LiteralPath (Join-Path $dir "snapshot.json") -Force -ErrorAction SilentlyContinue
            foreach ($f in $ImportedFolders) {
                $src = Join-Path $RepoRoot $f
                if (Test-Path $src) { Invoke-Robocopy $src (Join-Path $dir "repo\$f") -Mirror }
            }
            $generated = @(Get-GeneratedFiles)
            foreach ($g in $generated) {
                $src = Join-Path $RepoRoot $g
                if (Test-Path -LiteralPath $src) { Copy-FileAtomic $src (Join-Path $dir "repo\$g") }
            }
            Write-LinesAtomic (Join-Path $dir "generated.txt") $generated
            if (-not $NoLibrary) { Invoke-Robocopy (Join-Path $ClientDir "Library") (Join-Path $dir "Library") -Mirror -ExcludeDirs ($LibraryBuildCaches + $LibraryJournal) }
            elseif (Test-Path (Join-Path $dir "Library")) { Remove-Item (Join-Path $dir "Library") -Recurse -Force }

            $packSig = Get-PackSignature
            $info = [ordered]@{
                created = (Get-Date).ToString("yyyy-MM-dd HH:mm"); commit = "$(Invoke-Git @("rev-parse", "--short", "HEAD"))".Trim()
                unity = Get-UnityVersion; pack = $packSig; library = -not $NoLibrary; bytes = Get-FolderBytes $dir
            }
            Write-TextAtomic (Join-Path $dir "snapshot.json") ($info | ConvertTo-Json)
            Write-Ok "snapshot '$Name' saved ($(Format-Size $info.bytes))"
        }
        "restore" {
            if (-not (Test-Path (Join-Path $dir "snapshot.json"))) {
                if (Test-Path -LiteralPath $dir) { Fail "Snapshot '$Name' is incomplete (its save was interrupted); rerun 'rr snapshot save -Name $Name' first." }
                Fail "No snapshot '$Name' in $root (rr snapshot list)."
            }
            $info = Get-Content (Join-Path $dir "snapshot.json") -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($info.unity -ne (Get-UnityVersion)) { Write-Warn2 "snapshot was made with Unity $($info.unity); the project uses $(Get-UnityVersion). Unity will reimport." }
            Write-Step "Restoring client snapshot '$Name' ($($info.created), commit $($info.commit))"
            # Until this finishes the project is part old, part snapshot: rr keeps Unity away from it
            # and a rerun resumes the copy.
            Write-State "restore" @{ name = $Name; since = (Get-Date).ToString("yyyy-MM-dd HH:mm") }
            # The project's import records describe the files this replaces; the snapshot brings its own.
            Remove-Item -LiteralPath $ImportRecords -Recurse -Force -ErrorAction SilentlyContinue
            $packSig = Get-PackSignature
            if (-not (Test-Path -LiteralPath (Join-Path $dir "repo\RebuildClient\Assets\Scenes\Maps\.rr-import\ledger.tsv")) -and $info.pack -ne $packSig) {
                Write-Warn2 "this snapshot predates import records and was made from another pack ($($info.pack)); the next 'rr import' takes it as made from the current one ($packSig), so files that differ between the two packs stay as the snapshot has them."
            }
            foreach ($f in $ImportedFolders) {
                $src = Join-Path $dir "repo\$f"
                if (Test-Path $src) { Invoke-Robocopy $src (Join-Path $RepoRoot $f) }
            }
            $generated = @(Get-Content (Join-Path $dir "generated.txt") -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ })
            foreach ($g in $generated) {
                $src = Join-Path $dir "repo\$g"
                if (Test-Path -LiteralPath $src) { Copy-FileAtomic $src (Join-Path $RepoRoot $g) }
            }
            if ($generated.Count -gt 0) {
                Write-LinesAtomic (Get-GeneratedListPath) (@(@(Get-GeneratedFiles) + $generated | Sort-Object -Unique))
                foreach ($chunk in (Split-Chunks $generated 50)) { Invoke-Git (@("update-index", "--skip-worktree", "--") + $chunk) -AllowFail | Out-Null }
            }
            $libTarget = Get-LibraryTarget
            if ($info.library -and (Test-Path (Join-Path $dir "Library"))) {
                Invoke-Robocopy (Join-Path $dir "Library") $libTarget -Mirror -ExcludeDirs ($LibraryBuildCaches + $LibraryJournal)
            }
            # Unity-side records of interrupted bakes describe the state this restore replaced.
            Remove-Item -LiteralPath (Join-Path $libTarget $LibraryJournal) -Recurse -Force -ErrorAction SilentlyContinue
            Clear-State "restore"
            Write-Ok "restored; open the project with 'rr editor' (or verify with 'rr report')"
        }
        "delete" {
            if (Confirm-Action "Delete snapshot '$Name' ($dir)?") { Remove-PathSafe $dir "snapshot $Name" }
        }
        default { Fail "Unknown snapshot action '$action' (list | save | restore | delete)" }
    }
}

# Windows Defender scans every file Unity writes during an import; Unity recommends excluding the
# project and its cache. Needs an elevated shell. 'rr perf undo' removes the exclusions again.
function Invoke-Perf {
    $cfg = Read-Config
    $paths = @($ClientDir, $cfg.libraryDir, $cfg.packDir) | Where-Object { $_ } | ForEach-Object { [System.IO.Path]::GetFullPath($_) } | Sort-Object -Unique
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not (Get-Command Add-MpPreference -ErrorAction SilentlyContinue)) { Write-Warn2 "Windows Defender cmdlets not available; nothing to do."; return }
    if (-not $admin) { Fail "Run from an elevated terminal (Defender exclusions need admin)." }
    $existing = @((Get-MpPreference).ExclusionPath)
    if ($Target -eq "undo") {
        foreach ($p in $paths) { if ($existing -contains $p) { Remove-MpPreference -ExclusionPath $p; Write-Ok "removed Defender exclusion $p" } }
        return
    }
    foreach ($p in $paths) {
        if ($existing -contains $p) { Write-Ok "already excluded: $p" }
        else { Add-MpPreference -ExclusionPath $p; Write-Ok "Defender exclusion added: $p" }
    }
}

function Remove-PathSafe([string]$path, [string]$label) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return }
    $bytes = Get-FolderBytes $path
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "removed $label ($(Format-Size $bytes))"
}

function Invoke-Clean {
    $cfg = Read-Config
    $scope = if ($Target) { $Target.ToLowerInvariant() } else { "temp" }
    if ($scope -notin @("temp", "build", "data", "imported", "library", "all")) { Fail "Unknown clean scope '$scope' (temp | build | data | imported | library | all)" }

    if ($scope -eq "imported") {
        # Only files git ignores are removed, so upstream's tracked assets in the same folders stay.
        if (Test-UnityProjectOpen) { Fail "Close Unity before removing imported client data." }
        $folders = @("Sprites", "Sounds", "Maps", "Models", "Scenes/Maps", "Effects", "Textures/Import") | ForEach-Object { "RebuildClient/Assets/$_" }
        if (-not (Confirm-Action "Remove everything 'rr import' created in $($folders -join ', ')? (rr import recreates it)")) { return }
        $before = ($folders | ForEach-Object { Get-FolderBytes (Join-Path $RepoRoot $_) } | Measure-Object -Sum).Sum
        Invoke-Git (@("clean", "-fdXq", "--") + $folders) | Out-Null
        $after = ($folders | ForEach-Object { Get-FolderBytes (Join-Path $RepoRoot $_) } | Measure-Object -Sum).Sum
        Write-Ok "removed imported client data ($(Format-Size ($before - $after))); tracked files untouched"
        return
    }

    Write-Step "Cleaning ($scope)"
    if (-not (Test-UnityProjectOpen)) {
        Remove-PathSafe (Join-Path $ClientDir "Temp") "Unity Temp"
    } else { Write-Warn2 "Unity is open; leaving RebuildClient\Temp alone" }
    Get-ChildItem (Join-Path $ClientDir "Logs") -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item -Force
    foreach ($proj in Get-ChildItem $ServerRoot -Directory) {
        foreach ($sub in @("obj")) { Remove-PathSafe (Join-Path $proj.FullName $sub) "$($proj.Name)\$sub" }
    }
    Remove-PathSafe (Join-Path $PackTool "obj") "RebuildPack\obj"
    $cli = Get-UnityCli
    if ($cli) { $null = Get-NativeOutput $cli @("cache", "clean", "--non-interactive"); Write-Ok "Unity CLI download cache cleared" }
    if (Test-Command dotnet) { $null = Get-NativeOutput "dotnet" @("build-server", "shutdown") }

    if ($scope -in @("build", "all")) {
        $dirs = Get-BuildDirs $cfg.buildOutput
        foreach ($suffix in @("", ".new", ".old")) {
            Remove-PathSafe "$($dirs.Out)$suffix" "player build$suffix"
            Remove-PathSafe "$($dirs.Symbols)$suffix" "player debug symbols$suffix"
        }
        if (Test-UnityProjectOpen) { Write-Warn2 "Unity is open; build caches in the Library were left alone." }
        else {
            foreach ($c in $LibraryBuildCaches) { Remove-PathSafe (Join-Path $ClientDir "Library\$c") "Library\$c (the next build rebuilds it, slower)" }
        }
    }
    if ($scope -in @("data", "all")) {
        $grf = if ($cfg.packDir) { Join-Path $cfg.packDir "rebuild-pack.grf" } else { "" }
        if ($grf -and (Test-Path -LiteralPath $grf) -and (Confirm-Action "Delete $grf? (portable copy of the pack; rr pack rebuilds it)")) { Remove-PathSafe $grf "pack GRF" }
    }
    if ($scope -in @("library", "all")) {
        if (Test-UnityProjectOpen) { Fail "Close Unity before deleting the Library." }
        if (Confirm-Action "Delete RebuildClient\Library? Unity will reimport everything on next open (can take hours after a full import).") {
            $lib = Join-Path $ClientDir "Library"
            $item = Get-Item -LiteralPath $lib -Force -ErrorAction SilentlyContinue
            if ($item -and $item.LinkType -eq "Junction") {
                # Empty the junction target but keep the link so the cache stays on the other drive.
                $target = @($item.Target)[0]
                $bytes = Get-FolderBytes $target
                Get-ChildItem -LiteralPath $target -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                Write-Ok "emptied Unity Library at $target ($(Format-Size $bytes)); junction kept"
            } else {
                Remove-PathSafe $lib "Unity Library"
            }
        }
    }
}

function Invoke-Setup {
    $cfg = Read-Config
    if (-not $cfg.workDir) { Invoke-Init }
    Invoke-Prereqs
    Invoke-GitSetup
    Invoke-InstallUnity
    $cfg = Read-Config
    $pendingMove = Read-State "library-move"
    if ($pendingMove -or ($cfg.libraryDir -and -not (Get-Item -LiteralPath (Join-Path $ClientDir "Library") -Force -ErrorAction SilentlyContinue).LinkType)) {
        $script:Target = if ($pendingMove) { $null } else { $cfg.libraryDir }
        Invoke-LibraryLink
    }
    Invoke-ServerBuild
    Invoke-UpdateClient
    Invoke-Pack

    # A project with import records brings itself up to date. Otherwise a snapshot skips the
    # multi-hour import: one made from this exact pack as is, one with import records followed by
    # an import of just what changed since.
    $snapDir = if ($cfg.workDir) { Join-Path $cfg.workDir "snapshots\$Name" } else { "" }
    $packSig = Get-PackSignature
    $snap = if ($snapDir -and (Test-Path (Join-Path $snapDir "snapshot.json"))) { Get-Content (Join-Path $snapDir "snapshot.json") -Raw | ConvertFrom-Json } else { $null }
    $snapRecords = $snap -and (Test-Path -LiteralPath (Join-Path $snapDir "repo\RebuildClient\Assets\Scenes\Maps\.rr-import\ledger.tsv"))
    $imported = (Test-Path -LiteralPath (Get-RecordPath "ledger.tsv")) -and -not $script:ReplaceImport
    $restore = $snap -and -not $Force -and -not $imported -and ($snapRecords -or ($packSig -and $snap.pack -eq $packSig))
    if ($snap -and -not $Force -and -not $imported -and -not $restore) {
        Write-Warn2 "snapshot '$Name' was saved from another pack ($($snap.pack); this one is $packSig), so setup imports instead of restoring it"
    }
    if ($restore) {
        $script:Target = "restore"
        Invoke-Snapshot
    }
    if (-not $restore -or $snapRecords) {
        $script:Target = $null
        Invoke-Import
    }
    Write-Step "Setup complete"
    Write-Host "    Next: 'rr server' in one terminal, then 'rr editor' and press Play (or 'rr build-client' + 'rr play')."
    Write-Host "    Check a build end to end with 'rr smoke'; save the imported state with 'rr snapshot save'."
    if ($restore) { Write-Host "    'rr bake' only bakes the maps the snapshot left unlit." }
    else { Write-Host "    Optional: 'rr bake' (lighting, overnight) then 'rr minimaps'." }
}

# RebuildAutomation's own records (Library\rr-journal) of work a killed Unity left unfinished.
function Get-UnityJournal([string]$name) {
    $file = Join-Path (Join-Path (Get-LibraryTarget) $LibraryJournal) "$name.json"
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { return Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

function Get-UnityJournalNotes {
    $notes = @()
    if (Test-UnityProjectOpen) { return $notes }
    $import = Get-UnityJournal "import"
    if ($import) {
        $notes += if ($import.phase -eq "maps") { "a Unity import stopped among the maps; 'rr import' carries on (the last map it saved is imported again)" }
                  else { "a Unity import stopped after the maps; 'rr import' finishes the sprite data and addressables" }
    }
    $bake = Get-UnityJournal "bake"
    if ($bake) {
        $left = @($bake.remaining).Count
        $notes += if ($bake.current) { "the lighting bake stopped while baking $($bake.current), with $left more to go; 'rr bake' puts $($bake.current) back as it was before, then carries on" }
                  else { "the lighting bake stopped with $left map(s) to go; 'rr bake' carries on" }
    }
    $minimaps = Get-UnityJournal "minimaps"
    if ($minimaps) { $notes += "minimaps stopped with $(@($minimaps.remaining).Count) map(s) to go; 'rr minimaps' carries on" }
    return $notes
}

# Runs before every command while no other rr is running: finishes or undoes what an interrupted
# rr left behind, and says what still needs a rerun.
function Resume-InterruptedWork {
    $indexLock = Join-Path $RepoRoot ".git\index.lock"
    $lockItem = Get-Item -LiteralPath $indexLock -Force -ErrorAction SilentlyContinue
    if ($lockItem -and $lockItem.LastWriteTime -lt (Get-Date).AddMinutes(-1) -and -not (Get-Process git -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $indexLock -Force
        Write-Ok "removed the .git\index.lock an interrupted git command left behind"
    }
    if (-not (Test-Path -LiteralPath $StateDir) -and -not (Test-Path -LiteralPath $GeneratedBackup)) { return }

    $run = Read-State "unity-run"
    if ($run -and -not (Test-OwnerAlive $run)) {
        if (Test-UnityProjectOpen) { Write-Warn2 "Unity is still running $($run.method) for an 'rr $($run.command)' that was stopped; let it finish or close it." }
        else {
            Write-Warn2 "'rr $($run.command)' was interrupted during Unity $($run.method) (started $($run.since)); cleaning up after it"
            Protect-GeneratedFiles @($run.before)
            Remove-PartialFiles ([long]$run.startedTicks) $run.method
            Clear-State "unity-run"
            Write-Warn2 "rerun 'rr $($run.command)' to resume it"
        }
    }
    Resume-GeneratedAside
    Stop-OrphanServer
    # Doctor lists these in its own section.
    if ($Command -ne "doctor") { Get-InterruptedNotes | ForEach-Object { Write-Warn2 $_ } }
}

# Work that an interrupted rr left for a rerun to finish.
function Get-InterruptedNotes {
    $notes = @()
    $restore = Read-State "restore"
    if ($restore -and -not (Test-OwnerAlive $restore)) { $notes += "restoring snapshot '$($restore.name)' was interrupted ($($restore.since)); 'rr snapshot restore -Name $($restore.name)' finishes it" }
    $move = Read-State "library-move"
    if ($move -and -not (Test-OwnerAlive $move)) { $notes += "moving the Unity Library to $($move.to) was interrupted; 'rr library-link' finishes it" }
    $install = Read-State "install"
    if ($install -and -not (Test-OwnerAlive $install)) {
        $what = if ($install.editor) { "Unity $($install.editor)" } else { "the Unity $($install.module) module" }
        $notes += "installing $what was interrupted ($($install.since)); 'rr install-unity' repairs it"
    }
    $resets = Read-State "sound-resets"
    if ($resets -and -not (Test-OwnerAlive $resets)) { $notes += "sprites that play $(@($resets.names).Count) replaced sound(s) still need converting again; the next 'rr import' does it" }
    $notes += @(Get-UnityJournalNotes)
    if (Get-LockOwner) { return $notes }
    if ((Test-Path -LiteralPath (Get-RecordPath "outputs.tsv")) -and -not (Read-ImportComplete) -and -not (Get-UnityJournal "import")) {
        $notes += "the last 'rr import' did not finish; rerunning it makes the files it deleted to bring in pack changes"
    }
    $cfg = Read-Config
    if ($cfg.packDir -and (Test-Path -LiteralPath (Join-Path $cfg.packDir "manifest.partial.jsonl"))) { $notes += "'rr pack' stopped partway; rerunning it carries on from the files it already wrote" }
    if ($cfg.workDir) {
        foreach ($s in Get-ChildItem (Join-Path $cfg.workDir "snapshots") -Directory -ErrorAction SilentlyContinue) {
            if (-not (Test-Path -LiteralPath (Join-Path $s.FullName "snapshot.json"))) { $notes += "snapshot '$($s.Name)' is incomplete (its save was interrupted); 'rr snapshot save -Name $($s.Name)' finishes it" }
        }
    }
    return $notes
}

# ---------------------------------------------------------------------------------------------

# Commands that change nothing another rr could be working on; they run alongside a long bake.
$LockFreeCommands = @("help", "prereqs", "doctor", "disk", "server", "play", "smoke", "showcase", "editor")

try {
    $cmd = $Command.ToLowerInvariant()
    Update-SessionPath
    if ($cmd -ne "help") {
        $owner = Get-LockOwner
        if (-not $owner) {
            Enter-RrLock
            Resume-InterruptedWork
            if ($LockFreeCommands -contains $cmd) { Exit-RrLock }
        } elseif ($LockFreeCommands -notcontains $cmd) {
            Fail "'rr $($owner.command)' is already running (pid $($owner.ownerPid), since $($owner.since)). Wait for it or stop it first."
        }
    }
    switch ($cmd) {
        "help" { Show-Help }
        "init" { Invoke-Init }
        "setup" { Invoke-Setup }
        "use-grf" { Invoke-UseGrf }
        "use-bundle" { Invoke-UseBundle }
        "prereqs" { Invoke-Prereqs }
        "doctor" { Invoke-Doctor }
        "git-setup" { Invoke-GitSetup }
        "sync" { Invoke-Sync }
        "install-unity" { Invoke-InstallUnity }
        "reference" { Invoke-Reference }
        "pack" { Invoke-Pack }
        "fingerprint" { Invoke-Fingerprint }
        "server-build" { Invoke-ServerBuild }
        "update-client" { Invoke-UpdateClient }
        "server" { Invoke-Server }
        "import" { Invoke-Import }
        "addressables" { Invoke-UnityMethod "UpdateAddressables" @() }
        "bake" {
            $extra = @(); if ($Target) { $extra += @("-rrMaps", $Target) }; if ($Force) { $extra += "-rrForce" }
            Invoke-UnityMethod "BakeLighting" $extra -Async
        }
        "minimaps" {
            $extra = @(); if ($Target) { $extra += @("-rrMaps", $Target) }; if ($Force) { $extra += "-rrForce" }
            Invoke-UnityMethod "MakeMinimaps" $extra -Async
        }
        "report" { Invoke-UnityMethod "Report" @() }
        "generated" { Invoke-Generated }
        "build-client" { Invoke-Build }
        "play" { Invoke-Play }
        "smoke" { Invoke-Smoke }
        "showcase" { Invoke-Showcase }
        "release" { Invoke-Release }
        "editor" { Invoke-Editor }
        "disk" { Invoke-Disk }
        "library-link" { Invoke-LibraryLink }
        "snapshot" { Invoke-Snapshot }
        "perf" { Invoke-Perf }
        "clean" { Invoke-Clean }
        default { Show-Help; Fail "Unknown command '$Command'" }
    }
    $exitCode = 0
} catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    $exitCode = 1
} finally {
    Exit-RrLock
}
exit $exitCode
