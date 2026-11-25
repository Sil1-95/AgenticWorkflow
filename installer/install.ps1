<#
.SYNOPSIS
    Installs (or updates) AgenticWorkflow for the current user. No Administrator rights, no .NET SDK or runtime, no Node.js and no source checkout are needed.

.DESCRIPTION
    Run it from the extracted release folder:

        .\install.ps1

    It checks the release against its manifest, stops an idle AgenticWorkflow controller (and refuses, changing nothing, if one is supervising running tasks), installs the application,
    installs the Herdr cockpit as a real copy configured to run that application, and adds the application folder to your user PATH. Run history and tasks are never touched, so
    running it over an existing installation is an update.

    If Windows refuses to run the script ("running scripts is disabled", or a security warning about a downloaded file), either unblock the files once:
        Get-ChildItem -Recurse . | Unblock-File
    or run it with a one-time policy override:
        powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1

.PARAMETER InstallDir
    Where the application is installed. Default: %LOCALAPPDATA%\Programs\AgenticWorkflow.

.PARAMETER HerdrHome
    Herdr's app home, where the cockpit goes (<HerdrHome>\mods\agenticworkflow). Default: $env:HERDR_APP_HOME, else ~\.config\herdr-app.

.PARAMETER StateRoot
    Where AgenticWorkflow keeps run history (the installer never writes there; it only asks the controller that serves it to stop). Default: $env:AGENTICWORKFLOW_HOME, else %LOCALAPPDATA%\AgenticWorkflow.

.PARAMETER NoPath
    Do not add the application folder to your user PATH.

.PARAMETER NoCockpit
    Do not install the Herdr cockpit.

.PARAMETER AllowDowngrade
    Install an older release over a newer one. The older program may not be able to read state a newer one wrote; by default this is refused.

.PARAMETER UserEnvironmentKey
    The registry key whose Path value is edited (default HKCU:\Environment). Only the automated tests change it.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$HerdrHome,
    [string]$StateRoot,
    [switch]$NoPath,
    [switch]$NoCockpit,
    [switch]$AllowDowngrade,
    [string]$UserEnvironmentKey = 'HKCU:\Environment'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Join-Path $PSScriptRoot 'setup') 'AgenticWorkflow.Setup.ps1')

try {
    $packageRoot = $PSScriptRoot

    Write-Host 'Checking the release...'
    $package = Test-AwPackage -PackageRoot $packageRoot
    if ($package.Problems.Count -gt 0) {
        throw ("The release is damaged or incomplete; nothing was installed:`n  - " + ($package.Problems -join "`n  - ") + "`nExtract the ZIP again (or download it again).")
    }

    $manifest = $package.Manifest
    $version = [string]$manifest.version

    $platformProblem = Test-AwPlatform -Platform ([string]$manifest.platform)
    if ($platformProblem) { throw "This release cannot be installed here: $platformProblem" }

    $locations = Resolve-AwLocations -InstallDir $InstallDir -HerdrHome $HerdrHome -StateRoot $StateRoot
    $installedAw = Join-Path $locations.BinDir 'aw.exe'

    $previous = Get-AwInstallRecord -InstallDir $locations.InstallDir
    if ($previous -and $previous.version) {
        $order = Compare-AwVersion -Left $version -Right ([string]$previous.version)
        if ($order -lt 0 -and -not $AllowDowngrade) {
            throw "AgenticWorkflow $($previous.version) is installed and this release is the older $version. Installing it could leave you with a program that cannot read the history a newer one wrote, so nothing was changed. Use -AllowDowngrade if you are sure."
        }
    }

    # Nothing is replaced while a controller could be supervising work.
    $null = Assert-AwControllerStopped -AwExe $installedAw -StateRoot $locations.StateRoot -Action 'run install.ps1'

    Write-Host "Installing AgenticWorkflow $version..."
    Install-AwApplication -PackageRoot $packageRoot -InstallDir $locations.InstallDir

    $shown = & $installedAw --version
    if ($LASTEXITCODE -ne 0 -or $shown -ne "aw $version") { throw "The installed program did not report the expected version (expected 'aw $version', got '$shown'). The installation may be damaged." }

    $cockpitInstalled = $false
    if (-not $NoCockpit) {
        Install-AwCockpit -PackageRoot $packageRoot -Locations $locations -Version $version
        $cockpitInstalled = $true
    }

    $pathChanged = $false
    if (-not $NoPath) { $pathChanged = Add-AwToUserPath -Directory $locations.BinDir -EnvironmentKey $UserEnvironmentKey }

    Write-AwInstallRecord -Locations $locations -Manifest $manifest -PathChanged $pathChanged -CockpitInstalled $cockpitInstalled

    Write-Host ''
    $verb = if ($previous) { 'Updated' } else { 'Installed' }
    Write-Host "$verb AgenticWorkflow $version." -ForegroundColor Green
    Write-Host "  Application   $($locations.BinDir)"
    Write-Host "  Run history   $($locations.StateRoot)  (kept separate; never touched by install or update)"
    if ($cockpitInstalled) { Write-Host "  Herdr cockpit $($locations.ModDir)  (a copy, using $installedAw)" } else { Write-Host '  Herdr cockpit not installed (-NoCockpit)' }
    if ($NoPath) { Write-Host '  PATH          not changed (-NoPath)' }
    elseif ($pathChanged) { Write-Host "  PATH          added $($locations.BinDir) to your user PATH" }
    else { Write-Host '  PATH          already contains the application folder' }

    Write-Host ''
    Write-Host 'Next:'
    if (-not $NoPath) { Write-Host '  1. Open a NEW terminal (an already-open one keeps its old PATH), then run:  aw doctor' }
    else { Write-Host "  1. Run:  `"$installedAw`" doctor" }
    if ($cockpitInstalled) { Write-Host '  2. In Herdr press Ctrl+Shift+P and run "Open AgenticWorkflow".' }
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
