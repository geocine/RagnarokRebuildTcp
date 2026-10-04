<#
.SYNOPSIS
    Sets up Ragnarok Rebuild on a Windows machine that has nothing installed yet.

.DESCRIPTION
    Installs Git with winget if it is missing, clones the fork on its work branch and runs
    'rr setup', which installs everything else (.NET, Unity, optionally the C++ build tools) and
    asks for your client data. If it is interrupted, run it again: it reuses the clone and setup
    carries on where it stopped.

    In a PowerShell window:
        irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1 | iex
    With options:
        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1))) -Dir D:\RagnarokRebuildTcp -Yes
    Or from a downloaded copy:
        powershell -ExecutionPolicy Bypass -File bootstrap.ps1 -Dir D:\RagnarokRebuildTcp
    From the fork's shared files instead of your own clients:
        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1))) -Bundle D:\Downloads\RagnarokRebuild-baked-2026-10-02.7z
        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/geocine/RagnarokRebuildTcp/dev/setup/bootstrap.ps1))) -Grf D:\Downloads\rebuild-pack.grf
#>
param(
    # Folder to clone into; asked for when not given.
    [string]$Dir,
    [string]$Repo = "https://github.com/geocine/RagnarokRebuildTcp.git",
    [string]$Branch = "dev",
    # A rebuild-pack.grf to set up from ('rr use-grf') instead of your own client GRFs.
    [string]$Grf,
    # A baked bundle, RagnarokRebuild-baked-<date>.7z, to set up from ('rr use-bundle').
    [string]$Bundle,
    # Install without asking. rr still asks for the work folder (and, without -Grf or -Bundle, the client data).
    [switch]$Yes
)

# Everything runs inside a function: under 'irm | iex' this script shares the caller's session,
# so it must not change their preferences or close their window with exit.
function Invoke-RagnarokBootstrap([string]$Dir, [string]$Repo, [string]$Branch, [string]$Grf, [string]$Bundle, [bool]$Yes) {
    $ErrorActionPreference = "Stop"
    function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
    function Test-Command([string]$name) { return [bool](Get-Command $name -ErrorAction SilentlyContinue) }

    if ($env:OS -ne "Windows_NT") { throw "Ragnarok Rebuild's setup needs Windows 10 21H1 or later." }
    if ($Grf -and $Bundle) { throw "Give -Grf or -Bundle, not both: the bundle already holds the GRF." }
    foreach ($file in @($Grf, $Bundle) | Where-Object { $_ }) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Not found: $file" }
    }
    # rr runs from the clone, so relative paths are resolved against this window's folder first.
    if ($Grf) { $Grf = (Resolve-Path -LiteralPath $Grf).ProviderPath }
    if ($Bundle) { $Bundle = (Resolve-Path -LiteralPath $Bundle).ProviderPath }

    if (-not (Test-Command git)) {
        if (-not (Test-Command winget)) {
            throw "Git is missing, and so is winget to install it. Install 'App Installer' from the Microsoft Store (ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1) or Git from https://git-scm.com/download/win, then run this again."
        }
        Write-Step "Installing Git (winget Git.Git; Windows asks for administrator approval)"
        & winget install --id Git.Git -e --source winget --silent --accept-package-agreements --accept-source-agreements
        $code = $LASTEXITCODE
        $env:Path = "$env:Path;" + [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not (Test-Command git)) { throw "Git did not install (winget exit $code). Install it from https://git-scm.com/download/win and run this again." }
    }

    if (-not $Dir) {
        $best = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.DriveType -eq "Fixed" } |
            Sort-Object AvailableFreeSpace -Descending | Select-Object -First 1
        $suggest = Join-Path $(if ($best) { $best.RootDirectory.FullName } else { $env:USERPROFILE }) "RagnarokRebuildTcp"
        $answer = if ($Yes) { "" } else { Read-Host "Clone into (the repo grows to about 11 GB once the client is imported) [$suggest]" }
        $Dir = if ([string]::IsNullOrWhiteSpace($answer)) { $suggest } else { $answer.Trim('"', ' ') }
    }
    $Dir = [System.IO.Path]::GetFullPath($Dir)
    if ($Dir -match '\\OneDrive') { Write-Host "    !!  $Dir is inside OneDrive; syncing a Unity project that size is slow and can lock files. A folder outside OneDrive is better." -ForegroundColor Yellow }

    # A clone killed before its first checkout leaves only .git behind, which is safe to redo.
    $gitDir = Join-Path $Dir ".git"
    if ((Test-Path -LiteralPath $gitDir) -and -not (Test-Path -LiteralPath (Join-Path $Dir "rr.cmd"))) {
        $others = @(Get-ChildItem -LiteralPath $Dir -Force | Where-Object { $_.Name -ne ".git" })
        if ($others.Count -gt 0) { throw "$Dir holds an incomplete clone with other files in it. Move them out or delete the folder, then run this again." }
        Write-Step "Removing the clone an earlier run left unfinished"
        Remove-Item -LiteralPath $Dir -Recurse -Force
    }
    if (Test-Path -LiteralPath $gitDir) {
        Write-Step "Using the existing clone in $Dir"
    } else {
        if ((Test-Path -LiteralPath $Dir) -and @(Get-ChildItem -LiteralPath $Dir -Force).Count -gt 0) { throw "$Dir already exists and is not empty; choose another folder with -Dir." }
        Write-Step "Cloning $Repo ($Branch) into $Dir"
        & git clone --branch $Branch -c core.autocrlf=input -c core.longpaths=true $Repo $Dir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)." }
    }

    $rr = Join-Path $Dir "rr.cmd"
    if ($Bundle) {
        $rrArgs = @("use-bundle", $Bundle)
        Write-Step "Running rr use-bundle: it asks for a work folder, extracts the bundle into it, then installs the rest and restores the baked client"
    } elseif ($Grf) {
        $rrArgs = @("use-grf", $Grf)
        Write-Step "Running rr use-grf: it asks for a work folder, installs the rest and imports the client from the GRF"
    } else {
        $rrArgs = @("setup")
        Write-Step "Running rr setup: it installs the rest, then asks for a work folder and your client data"
    }
    if ($Yes) { $rrArgs += "-Yes" }
    & $rr @rrArgs
    if ($LASTEXITCODE -ne 0) {
        $code = $LASTEXITCODE
        $again = "& '$rr' $(($rrArgs | ForEach-Object { if ($_ -match '\s') { "'$_'" } else { $_ } }) -join ' ')"
        throw "rr $($rrArgs[0]) stopped (exit $code). Fix what it reported, then run $again; it carries on where it stopped."
    }
    Write-Step "Ready. Use $rr from now on ('rr help' lists the commands)."
}

try {
    Invoke-RagnarokBootstrap $Dir $Repo $Branch $Grf $Bundle $Yes.IsPresent
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($PSCommandPath) { exit 1 }
}
