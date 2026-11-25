# Functions shared by install.ps1 and uninstall.ps1. Dot-sourced; defines functions only, and does nothing by itself.
#
# Rules this file keeps:
#   * Everything is per-user. Nothing needs, asks for or uses Administrator rights.
#   * Paths are always passed as values (never interpolated into a command string), so spaces and unusual characters survive.
#   * Only AgenticWorkflow's own files are touched: its application folder, its cockpit folder inside Herdr's mods folder, its cockpit settings folder, one entry of the user's PATH.
#     The run history (the state folder) is never touched unless -PurgeState is given to the uninstaller.
#   * Works in Windows PowerShell 5.1 and PowerShell 7.

Set-StrictMode -Version Latest

$script:AwModName = 'agenticworkflow'
$script:AwModTitle = 'AgenticWorkflow'
$script:AwUserEnvironmentKey = 'HKCU:\Environment'

# ---- locations ------------------------------------------------------------------------------------------------------------------------

function Resolve-AwLocations {
    [CmdletBinding()]
    param([string]$InstallDir, [string]$HerdrHome, [string]$StateRoot)

    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')

    if (-not $InstallDir) { $InstallDir = Join-Path (Join-Path $localAppData 'Programs') 'AgenticWorkflow' }

    if (-not $HerdrHome) {
        if ($env:HERDR_APP_HOME -and [IO.Path]::IsPathRooted($env:HERDR_APP_HOME)) { $HerdrHome = $env:HERDR_APP_HOME }
        else { $HerdrHome = Join-Path (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.config') 'herdr-app' }
    }

    if (-not $StateRoot) {
        if ($env:AGENTICWORKFLOW_HOME -and [IO.Path]::IsPathRooted($env:AGENTICWORKFLOW_HOME)) { $StateRoot = $env:AGENTICWORKFLOW_HOME }
        else { $StateRoot = Join-Path $localAppData 'AgenticWorkflow' }
    }

    $install = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\', '/')
    $herdr = [IO.Path]::GetFullPath($HerdrHome).TrimEnd('\', '/')

    [pscustomobject]@{
        InstallDir  = $install
        BinDir      = Join-Path $install 'bin'
        HerdrHome   = $herdr
        ModDir      = Join-Path (Join-Path $herdr 'mods') $script:AwModName
        SettingsDir = Join-Path $herdr $script:AwModName
        StateRoot   = [IO.Path]::GetFullPath($StateRoot).TrimEnd('\', '/')
    }
}

# ---- the package ----------------------------------------------------------------------------------------------------------------------

function Get-AwFileHash {
    param([Parameter(Mandatory)][string]$Path)

    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-AwRelativePath {
    param([string]$Root, [string]$FullName)
    $FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
}

# Checks the extracted release against its own manifest: every listed file is present and unchanged, nothing unlisted is there, and the pieces an installation needs exist.
# Returns the manifest and the list of problems (empty when the package is whole).
function Test-AwPackage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageRoot)

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $manifestPath = Join-Path $root 'manifest.json'

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{ Manifest = $null; Problems = @('manifest.json is missing: this is not an extracted AgenticWorkflow release.') }
    }

    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { return [pscustomobject]@{ Manifest = $null; Problems = @("manifest.json cannot be read: $($_.Exception.Message)") } }

    if ($manifest.product -ne 'AgenticWorkflow' -or -not $manifest.version -or -not $manifest.files) {
        return [pscustomobject]@{ Manifest = $null; Problems = @('manifest.json does not describe an AgenticWorkflow release.') }
    }

    $listed = @{}

    foreach ($entry in $manifest.files) {
        $relative = [string]$entry.path

        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') {
            $problems.Add("the manifest lists an unsafe path '$relative'.")
            continue
        }

        $listed[$relative.ToLowerInvariant()] = $true
        $full = Join-Path $root ($relative.Replace('/', '\'))

        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { $problems.Add("missing: $relative"); continue }
        if ((Get-Item -LiteralPath $full -Force).Length -ne [long]$entry.size) { $problems.Add("changed (size): $relative"); continue }
        if ((Get-AwFileHash -Path $full) -ne ([string]$entry.sha256).ToLowerInvariant()) { $problems.Add("changed (content): $relative") }
    }

    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = Get-AwRelativePath -Root $root -FullName $file.FullName

        if ($relative -ne 'manifest.json' -and -not $listed.ContainsKey($relative.ToLowerInvariant())) { $problems.Add("unexpected file: $relative") }
    }

    foreach ($required in 'bin/aw.exe', 'bin/AgenticWorkflow.ClaudeEvidenceHook.exe', 'bin/aw-package.json', 'cockpit/main.js', 'cockpit/mod.json', 'cockpit/lib/version.js', 'install.ps1', 'uninstall.ps1', 'setup/AgenticWorkflow.Setup.ps1') {
        if (-not $listed.ContainsKey($required.ToLowerInvariant())) { $problems.Add("the release does not contain $required") }
    }

    [pscustomobject]@{ Manifest = $manifest; Problems = $problems.ToArray() }
}

# Whether this machine can run the release (Windows, 64-bit).
function Test-AwPlatform {
    param([string]$Platform = 'win-x64')

    if ($Platform -ne 'win-x64') { return "this release is for '$Platform', not win-x64." }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return 'this release runs on Windows only.' }
    if (-not [Environment]::Is64BitOperatingSystem) { return 'this release needs 64-bit Windows.' }
    $null
}

# -1 / 0 / 1. Numeric parts are compared as numbers; a pre-release (0.4.0-rc1) sorts before its release.
function Compare-AwVersion {
    param([string]$Left, [string]$Right)

    function Split-Version([string]$v) {
        $core, $pre = $v.Split('-', 2)
        [pscustomobject]@{ Core = [version]$core; Pre = $pre }
    }

    $a = Split-Version $Left
    $b = Split-Version $Right

    if ($a.Core -lt $b.Core) { return -1 }
    if ($a.Core -gt $b.Core) { return 1 }
    if ($a.Pre -eq $b.Pre) { return 0 }
    if (-not $a.Pre) { return 1 }
    if (-not $b.Pre) { return -1 }

    [Math]::Sign([string]::CompareOrdinal($a.Pre, $b.Pre))
}

# The record the installer leaves in the application folder when an install completed (null when nothing is installed).
function Get-AwInstallRecord {
    param([Parameter(Mandatory)][string]$InstallDir)

    $path = Join-Path $InstallDir 'install.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }

    try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $null }
}

# ---- the controller -------------------------------------------------------------------------------------------------------------------

function Test-AwControllerLockHeld {
    param([Parameter(Mandatory)][string]$StateRoot)

    $lock = Join-Path (Join-Path $StateRoot 'controller') 'controller.lock'
    if (-not (Test-Path -LiteralPath $lock -PathType Leaf)) { return $false }

    try {
        $stream = [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Dispose()
        return $false
    }
    catch [IO.IOException] { return $true }
    catch { return $false }
}

function Invoke-AwProgram {
    param([string]$Path, [string]$Arguments, [hashtable]$Environment = @{}, [int]$TimeoutMilliseconds = 30000)

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Path
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true

    foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = [string]$Environment[$name] }

    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $null = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ TimedOut = $true; ExitCode = $null; Output = '' }
    }

    $process.WaitForExit()
    [pscustomobject]@{ TimedOut = $false; ExitCode = $process.ExitCode; Output = $output.Result }
}

# Asks the installed aw to stop an idle controller and waits for it to be gone. Never forces: a controller that supervises work is left alone.
#   'none'     no controller was running
#   'stopped'  an idle controller was stopped
#   'busy'     the controller supervises running tasks (ActiveRuns says how many); nothing was stopped
#   'unknown'  it could not be established; the controller lock still says one is running
# A caller proceeds on 'none' and 'stopped' only.
function Stop-AwController {
    [CmdletBinding()]
    param([string]$AwExe, [Parameter(Mandatory)][string]$StateRoot)

    $held = Test-AwControllerLockHeld -StateRoot $StateRoot
    $answer = $null

    if ($AwExe -and (Test-Path -LiteralPath $AwExe -PathType Leaf)) {
        try {
            $run = Invoke-AwProgram -Path $AwExe -Arguments 'ctl stop' -Environment @{ AGENTICWORKFLOW_HOME = $StateRoot }
            if (-not $run.TimedOut -and $run.ExitCode -eq 0) { $answer = $run.Output | ConvertFrom-Json }
        }
        catch { $answer = $null }
    }

    if ($answer -and $answer.kind -eq 'shutdown') {
        $runs = if ($answer.PSObject.Properties.Name -contains 'activeRuns') { $answer.activeRuns } else { $null }

        if (-not $answer.accepted -and $runs) { return [pscustomobject]@{ Outcome = 'busy'; ActiveRuns = [int]$runs } }

        if ($answer.accepted) {
            $deadline = (Get-Date).AddSeconds(30)
            while ((Test-AwControllerLockHeld -StateRoot $StateRoot) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }

            if (Test-AwControllerLockHeld -StateRoot $StateRoot) { return [pscustomobject]@{ Outcome = 'unknown'; ActiveRuns = 0 } }
            return [pscustomobject]@{ Outcome = 'stopped'; ActiveRuns = 0 }
        }

        # Not accepted and no runs: there was no controller to stop.
        if (-not (Test-AwControllerLockHeld -StateRoot $StateRoot)) { return [pscustomobject]@{ Outcome = 'none'; ActiveRuns = 0 } }
    }

    if ($held -or (Test-AwControllerLockHeld -StateRoot $StateRoot)) { return [pscustomobject]@{ Outcome = 'unknown'; ActiveRuns = 0 } }

    [pscustomobject]@{ Outcome = 'none'; ActiveRuns = 0 }
}

# Stops the controller or throws an explanation. Used by the installer and the uninstaller.
function Assert-AwControllerStopped {
    param([string]$AwExe, [Parameter(Mandatory)][string]$StateRoot, [Parameter(Mandatory)][string]$Action)

    $result = Stop-AwController -AwExe $AwExe -StateRoot $StateRoot

    switch ($result.Outcome) {
        'busy' { throw "AgenticWorkflow is supervising $($result.ActiveRuns) running task(s). Nothing was changed. Finish or abort them (the cockpit, or 'aw abort <run-id>'), then $Action again." }
        'unknown' { throw "A controller is running and could not be stopped safely. Nothing was changed. Close the cockpit's tasks, run 'aw ctl stop', then $Action again." }
        'stopped' { Write-Host 'Stopped the idle AgenticWorkflow controller.' }
    }

    $result.Outcome
}

# ---- the application folder -----------------------------------------------------------------------------------------------------------

function Move-AwDirectory {
    param([string]$From, [string]$To)

    for ($attempt = 1; ; $attempt++) {
        try { [IO.Directory]::Move($From, $To); return }
        catch {
            if ($attempt -ge 5) { throw }
            Start-Sleep -Milliseconds 400
        }
    }
}

function Remove-AwDirectory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $true }

    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; return $true }
    catch { return $false }
}

# Copies the release's bin folder to <InstallDir>\bin so that the folder is either entirely the old version or entirely the new one: the new files are staged next to it, the old
# folder is renamed aside, the new one renamed into place, and the old one deleted. If the rename cannot happen (a program is running from the folder) nothing has changed.
function Install-AwApplication {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)][string]$InstallDir)

    $bin = Join-Path $InstallDir 'bin'
    $token = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $staging = Join-Path $InstallDir "bin.new-$token"
    $aside = Join-Path $InstallDir "bin.old-$token"

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

    # Leftovers of an earlier interrupted install are ours to clear.
    foreach ($leftover in Get-ChildItem -LiteralPath $InstallDir -Directory -Force | Where-Object { $_.Name -like 'bin.new-*' -or $_.Name -like 'bin.old-*' }) {
        $null = Remove-AwDirectory -Path $leftover.FullName
    }

    Copy-Item -LiteralPath (Join-Path $PackageRoot 'bin') -Destination $staging -Recurse -Force

    $hadOld = Test-Path -LiteralPath $bin
    try {
        if ($hadOld) { Move-AwDirectory -From $bin -To $aside }
    }
    catch {
        $null = Remove-AwDirectory -Path $staging
        throw "The existing installation could not be replaced because a program is using it ($($_.Exception.Message)). Close every terminal or tool that is running aw, then try again. Nothing was changed."
    }

    try { Move-AwDirectory -From $staging -To $bin }
    catch {
        if ($hadOld) { try { Move-AwDirectory -From $aside -To $bin } catch { } }
        $null = Remove-AwDirectory -Path $staging
        throw "The new files could not be put in place ($($_.Exception.Message)). The previous installation was restored."
    }

    if ($hadOld -and -not (Remove-AwDirectory -Path $aside)) { Write-Warning "Could not delete the previous version's files ($aside). They are harmless and will be removed by the next install." }

    # The scripts that undo this install live beside bin, so an uninstall never needs the release that was extracted.
    $setup = Join-Path $InstallDir 'setup'
    New-Item -ItemType Directory -Force -Path $setup | Out-Null
    Copy-Item -LiteralPath (Join-Path (Join-Path $PackageRoot 'setup') 'AgenticWorkflow.Setup.ps1') -Destination $setup -Force
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'uninstall.ps1') -Destination $InstallDir -Force
}

function Write-AwInstallRecord {
    param([Parameter(Mandatory)]$Locations, [Parameter(Mandatory)]$Manifest, [bool]$PathChanged, [bool]$CockpitInstalled)

    $record = [ordered]@{
        product          = 'AgenticWorkflow'
        version          = [string]$Manifest.version
        platform         = [string]$Manifest.platform
        installedAt      = [DateTime]::UtcNow.ToString('o')
        binDirectory     = $Locations.BinDir
        herdrHome        = if ($CockpitInstalled) { $Locations.HerdrHome } else { $null }
        pathEntry        = $PathChanged
    }

    [IO.File]::WriteAllText((Join-Path $Locations.InstallDir 'install.json'), (($record | ConvertTo-Json) + "`n"), (New-Object Text.UTF8Encoding($false)))
}

# ---- the cockpit ----------------------------------------------------------------------------------------------------------------------

function Test-AwReparsePoint {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    [bool]($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))
}

# True when the folder is the AgenticWorkflow cockpit (so it is ours to replace or remove): a link (a development install) or a real folder whose mod.json is titled AgenticWorkflow.
function Test-AwCockpitOwned {
    param([string]$ModDir)

    if (-not (Test-Path -LiteralPath $ModDir)) { return $false }
    if (Test-AwReparsePoint -Path $ModDir) { return $true }

    $manifest = Join-Path $ModDir 'mod.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return $false }

    try { (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).title -eq $script:AwModTitle } catch { $false }
}

function Remove-AwCockpitFolder {
    param([string]$ModDir)

    if (Test-AwReparsePoint -Path $ModDir) { [IO.Directory]::Delete($ModDir); return }   # a link is removed without touching what it points at
    Remove-Item -LiteralPath $ModDir -Recurse -Force
}

# Makes <HerdrHome>\mods\agenticworkflow an exact copy of the release's cockpit folder (files the new version no longer has are removed), and points the cockpit at the installed aw.
# Refuses to replace a folder of that name that is not AgenticWorkflow's.
function Install-AwCockpit {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)]$Locations, [Parameter(Mandatory)][string]$Version)

    $source = Join-Path $PackageRoot 'cockpit'
    $target = $Locations.ModDir

    if ((Test-Path -LiteralPath $target) -and -not (Test-AwCockpitOwned -ModDir $target)) {
        throw "'$target' exists and is not the AgenticWorkflow cockpit, so it was left alone. Move it away, or install with -NoCockpit."
    }

    # Herdr lists a mod's command-palette entry only for a real folder, never for a link.
    if ((Test-Path -LiteralPath $target) -and (Test-AwReparsePoint -Path $target)) { Remove-AwCockpitFolder -ModDir $target }

    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $wanted = @{}
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
        $relative = Get-AwRelativePath -Root ([IO.Path]::GetFullPath($source).TrimEnd('\')) -FullName $file.FullName
        $wanted[$relative.ToLowerInvariant()] = $true
        $destination = Join-Path $target ($relative.Replace('/', '\'))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $target -Recurse -File -Force)) {
        $relative = Get-AwRelativePath -Root $target -FullName $file.FullName
        if (-not $wanted.ContainsKey($relative.ToLowerInvariant())) { Remove-Item -LiteralPath $file.FullName -Force }
    }

    foreach ($directory in @(Get-ChildItem -LiteralPath $target -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)) {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force)) { Remove-Item -LiteralPath $directory.FullName -Force }
    }

    # The settings file names the installed aw by absolute path. It holds nothing else: no secret, no per-user data.
    New-Item -ItemType Directory -Force -Path $Locations.SettingsDir | Out-Null
    $settings = [ordered]@{ aw = (Join-Path $Locations.BinDir 'aw.exe').Replace('\', '/'); version = $Version }
    [IO.File]::WriteAllText((Join-Path $Locations.SettingsDir 'config.json'), (($settings | ConvertTo-Json) + "`n"), (New-Object Text.UTF8Encoding($false)))
}

function Uninstall-AwCockpit {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Locations)

    $removed = @()

    if (Test-Path -LiteralPath $Locations.ModDir) {
        if (Test-AwCockpitOwned -ModDir $Locations.ModDir) { Remove-AwCockpitFolder -ModDir $Locations.ModDir; $removed += $Locations.ModDir }
        else { Write-Warning "'$($Locations.ModDir)' is not the AgenticWorkflow cockpit; it was left alone." }
    }

    if (Test-Path -LiteralPath $Locations.SettingsDir) {
        Remove-Item -LiteralPath $Locations.SettingsDir -Recurse -Force
        $removed += $Locations.SettingsDir
    }

    $removed
}

# ---- PATH -----------------------------------------------------------------------------------------------------------------------------

function Open-AwEnvironmentKey {
    param([string]$EnvironmentKey)
    $sub = $EnvironmentKey -replace '^HKCU:\\?', ''
    [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($sub)
}

function Get-AwPathEntries {
    param([string]$EnvironmentKey = $script:AwUserEnvironmentKey)

    $key = Open-AwEnvironmentKey -EnvironmentKey $EnvironmentKey
    try {
        $value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        @($value.Split(';') | Where-Object { $_.Trim() })
    }
    finally { $key.Dispose() }
}

function Set-AwPathEntries {
    param([string[]]$Entries, [string]$EnvironmentKey = $script:AwUserEnvironmentKey)

    $key = Open-AwEnvironmentKey -EnvironmentKey $EnvironmentKey
    try {
        # Keep the value's kind: a PATH that uses %VARIABLES% must stay expandable.
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        try { if ($key.GetValueNames() -contains 'Path') { $kind = $key.GetValueKind('Path') } } catch { }
        $key.SetValue('Path', ($Entries -join ';'), $kind)
    }
    finally { $key.Dispose() }

    if ($EnvironmentKey -eq $script:AwUserEnvironmentKey) { Send-AwEnvironmentChange }
}

# Tells running programs (Explorer, so new terminals) that the user's environment changed.
function Send-AwEnvironmentChange {
    try {
        if (-not ('AwSetup.NativeMethods' -as [type])) {
            Add-Type -Namespace AwSetup -Name NativeMethods -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)] public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);'
        }
        $result = [UIntPtr]::Zero
        $null = [AwSetup.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
    }
    catch { Write-Verbose "The environment change could not be broadcast: $($_.Exception.Message)" }
}

function Format-AwPathEntry {
    param([string]$Entry)
    [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Entry.Trim().Trim('"'))).TrimEnd('\', '/')
}

function Test-AwSamePathEntry {
    param([string]$Left, [string]$Right)
    try { [string]::Equals((Format-AwPathEntry $Left), (Format-AwPathEntry $Right), [StringComparison]::OrdinalIgnoreCase) } catch { $false }
}

# Adds one directory to the user's PATH. Idempotent: it changes nothing if the directory is already there. Returns whether it changed anything.
function Add-AwToUserPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory, [string]$EnvironmentKey = $script:AwUserEnvironmentKey)

    $entries = @(Get-AwPathEntries -EnvironmentKey $EnvironmentKey)
    if ($entries | Where-Object { Test-AwSamePathEntry -Left $_ -Right $Directory }) { return $false }

    Set-AwPathEntries -Entries ($entries + $Directory) -EnvironmentKey $EnvironmentKey
    $true
}

function Remove-AwFromUserPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory, [string]$EnvironmentKey = $script:AwUserEnvironmentKey)

    $entries = @(Get-AwPathEntries -EnvironmentKey $EnvironmentKey)
    $kept = @($entries | Where-Object { -not (Test-AwSamePathEntry -Left $_ -Right $Directory) })

    if ($kept.Count -eq $entries.Count) { return $false }

    Set-AwPathEntries -Entries $kept -EnvironmentKey $EnvironmentKey
    $true
}

# ---- the state folder (only the uninstaller's explicit -PurgeState reaches this) ------------------------------------------------------

# A state folder is recognisable by what AgenticWorkflow puts in it; anything else is refused, so a mistaken path can never delete someone's files.
function Test-AwStateRoot {
    param([string]$StateRoot)

    if (-not (Test-Path -LiteralPath $StateRoot -PathType Container)) { return $false }
    if ((Get-ChildItem -LiteralPath $StateRoot -Force | Measure-Object).Count -eq 0) { return $true }

    $names = @(Get-ChildItem -LiteralPath $StateRoot -Force | ForEach-Object { $_.Name })
    [bool](($names -contains 'runs') -or ($names -contains 'controller'))
}
