<#
.SYNOPSIS
    Removes AgenticWorkflow for the current user: the application, the PATH entry and the Herdr cockpit. Your run history is kept unless you ask otherwise.

.DESCRIPTION
        .\uninstall.ps1                      (from the extracted release, or from %LOCALAPPDATA%\Programs\AgenticWorkflow)

    It refuses, changing nothing, while a controller is supervising running tasks. It removes only AgenticWorkflow's own files; it never touches other Herdr mods or settings. Running it
    again when nothing is installed is harmless.

.PARAMETER PurgeState
    ALSO DELETES the run history and tasks (the state folder). This cannot be undone. You are asked to confirm unless -Force is given.

.PARAMETER Force
    Do not ask for confirmation of -PurgeState.

.PARAMETER InstallDir
    Default: %LOCALAPPDATA%\Programs\AgenticWorkflow.

.PARAMETER HerdrHome
    Default: where the installation recorded the cockpit, else $env:HERDR_APP_HOME, else ~\.config\herdr-app.

.PARAMETER StateRoot
    Default: $env:AGENTICWORKFLOW_HOME, else %LOCALAPPDATA%\AgenticWorkflow.

.PARAMETER UserEnvironmentKey
    The registry key whose Path value is edited (default HKCU:\Environment). Only the automated tests change it.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$HerdrHome,
    [string]$StateRoot,
    [switch]$PurgeState,
    [switch]$Force,
    [string]$UserEnvironmentKey = 'HKCU:\Environment'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Join-Path $PSScriptRoot 'setup') 'AgenticWorkflow.Setup.ps1')

# Deleting the folder this script runs from needs the shell to stand somewhere else.
Push-Location ([IO.Path]::GetTempPath())

try {
    $locations = Resolve-AwLocations -InstallDir $InstallDir -HerdrHome $HerdrHome -StateRoot $StateRoot
    $record = Get-AwInstallRecord -InstallDir $locations.InstallDir

    if (-not $HerdrHome -and $record -and $record.PSObject.Properties.Name -contains 'herdrHome' -and $record.herdrHome) {
        $locations = Resolve-AwLocations -InstallDir $InstallDir -HerdrHome ([string]$record.herdrHome) -StateRoot $StateRoot
    }

    $stateExists = Test-Path -LiteralPath $locations.StateRoot
    if ($PurgeState -and $stateExists -and -not (Test-AwStateRoot -StateRoot $locations.StateRoot)) {
        throw "'$($locations.StateRoot)' does not look like an AgenticWorkflow state folder (no 'runs' or 'controller' inside), so it will not be deleted."
    }

    if ($PurgeState -and $stateExists -and -not $Force) {
        Write-Host "This will PERMANENTLY DELETE all AgenticWorkflow run history and tasks in $($locations.StateRoot)." -ForegroundColor Yellow
        if ((Read-Host 'Type DELETE to continue') -ne 'DELETE') { throw 'Cancelled. Nothing was changed.' }
    }

    $null = Assert-AwControllerStopped -AwExe (Join-Path $locations.BinDir 'aw.exe') -StateRoot $locations.StateRoot -Action 'run uninstall.ps1'

    $removed = @()

    if (Remove-AwFromUserPath -Directory $locations.BinDir -EnvironmentKey $UserEnvironmentKey) { $removed += 'the PATH entry' }

    foreach ($path in @(Uninstall-AwCockpit -Locations $locations)) { $removed += $path }

    if (Test-Path -LiteralPath $locations.BinDir) {
        if (-not (Remove-AwDirectory -Path $locations.BinDir)) { throw "The application folder could not be removed because a program is using it. Close every terminal or tool that is running aw, then run uninstall.ps1 again." }
        $removed += $locations.BinDir
    }

    foreach ($leftover in @(Get-ChildItem -LiteralPath $locations.InstallDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'bin.new-*' -or $_.Name -like 'bin.old-*' })) {
        $null = Remove-AwDirectory -Path $leftover.FullName
    }

    foreach ($name in 'install.json', 'uninstall.ps1') {
        $file = Join-Path $locations.InstallDir $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force; $removed += $file }
    }

    $setup = Join-Path $locations.InstallDir 'setup'
    if (Test-Path -LiteralPath $setup) { $null = Remove-AwDirectory -Path $setup }

    if ((Test-Path -LiteralPath $locations.InstallDir) -and -not (Get-ChildItem -LiteralPath $locations.InstallDir -Force)) { Remove-Item -LiteralPath $locations.InstallDir -Force }

    if ($PurgeState -and $stateExists) {
        Remove-Item -LiteralPath $locations.StateRoot -Recurse -Force
        $removed += $locations.StateRoot
    }

    Write-Host ''
    if ($removed.Count -eq 0) { Write-Host 'AgenticWorkflow is not installed; nothing to remove.' }
    else {
        Write-Host 'Removed:' -ForegroundColor Green
        foreach ($item in $removed) { Write-Host "  $item" }
    }

    if (-not $PurgeState -and (Test-Path -LiteralPath $locations.StateRoot)) { Write-Host "Kept your run history: $($locations.StateRoot)  (delete that folder, or run uninstall.ps1 -PurgeState, to remove it too)" }
    Write-Host 'Terminals that are already open still have the old PATH until they are reopened.'
    Write-Host ''
    exit 0
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
