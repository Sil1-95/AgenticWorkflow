# Functions that turn a published build into the release bundle, audit it, and zip it. Dot-sourced by package.ps1 (and by the packaging tests, which feed it a build they made themselves).
#
#   New-AwBundle   publish folder + repository files  ->  the release folder (bin, cockpit, docs, installer scripts, manifests)
#   Test-AwBundle  the release folder                  ->  a list of problems (empty = clean)
#   New-AwZip      the release folder                  ->  a reproducible ZIP (same input, same bytes)
#
# Everything in the bundle comes from a fixed list of known places. Nothing is "whatever happens to be in a folder": a stray file in the repository or the publish output can never end up
# in a release because it is not on the list, and the audit then checks the result anyway.

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '..\installer\setup\AgenticWorkflow.Setup.ps1')

$script:Platform = 'win-x64'

# The product version, from its one source (Directory.Build.props).
function Get-AwProductVersion {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $props = [xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw)
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Directory.Build.props has no valid <Version> ('$version')." }
    $version
}

function ConvertTo-AwJsonString {
    param([string]$Text)
    '"' + $Text.Replace('\', '\\').Replace('"', '\"') + '"'
}

# A manifest written by hand so its bytes do not depend on the PowerShell version: entries sorted, no timestamps, LF line endings.
function Write-AwManifest {
    param([string]$Path, [string]$Version, [object[]]$Files)

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('{')
    $lines.Add('  "product": "AgenticWorkflow",')
    $lines.Add("  `"version`": $(ConvertTo-AwJsonString $Version),")
    $lines.Add("  `"platform`": $(ConvertTo-AwJsonString $script:Platform),")
    $lines.Add('  "files": [')

    for ($i = 0; $i -lt $Files.Count; $i++) {
        $comma = if ($i -lt $Files.Count - 1) { ',' } else { '' }
        $lines.Add("    { `"path`": $(ConvertTo-AwJsonString $Files[$i].Path), `"size`": $($Files[$i].Size), `"sha256`": $(ConvertTo-AwJsonString $Files[$i].Sha256) }$comma")
    }

    $lines.Add('  ]')
    $lines.Add('}')
    [IO.File]::WriteAllText($Path, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
}

function Get-AwBundleFiles {
    param([Parameter(Mandatory)][string]$Root, [string[]]$Exclude = @())

    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    # Ordinal order, so the manifest and the archive do not depend on the machine's culture.
    $sorted = New-Object 'System.Collections.Generic.SortedDictionary[string,object]' ([StringComparer]::Ordinal)

    foreach ($file in Get-ChildItem -LiteralPath $full -Recurse -File -Force) {
        $relative = $file.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/')
        if ($Exclude -notcontains $relative) { $sorted[$relative] = [pscustomobject]@{ Path = $relative; Size = $file.Length; Sha256 = (Get-AwFileHash -Path $file.FullName); Full = $file.FullName } }
    }

    @($sorted.Values)
}

# What the cockpit consists of in a release. The development driver, the installer for source checkouts, the tests and the editor files are deliberately not on this list.
$script:CockpitFiles = @('main.js', 'ui.js', 'mod.json', 'lib')

function New-AwBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Version
    )

    $repo = [IO.Path]::GetFullPath($RepositoryRoot)

    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    # The application: exactly what was published (never a Debug build or a bin folder of the repository).
    Copy-Item -LiteralPath $PublishDirectory -Destination (Join-Path $Destination 'bin') -Recurse -Force
    $bin = Join-Path $Destination 'bin'
    foreach ($name in 'aw.exe', 'AgenticWorkflow.ClaudeEvidenceHook.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $name))) { throw "The publish folder has no $name." }
    }
    Get-ChildItem -LiteralPath $bin -Recurse -File -Force | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

    # The cockpit.
    $cockpitSource = Join-Path $repo 'integrations\herdr\agenticworkflow'
    $cockpit = Join-Path $Destination 'cockpit'
    New-Item -ItemType Directory -Force -Path $cockpit | Out-Null
    foreach ($name in $script:CockpitFiles) { Copy-Item -LiteralPath (Join-Path $cockpitSource $name) -Destination $cockpit -Recurse -Force }

    $stamp = Join-Path $cockpit 'lib\version.js'
    $text = [IO.File]::ReadAllText($stamp)
    $stamped = $text -replace 'COCKPIT_VERSION = null;', "COCKPIT_VERSION = `"$Version`";"
    if ($stamped -eq $text) { throw 'lib/version.js has no unstamped COCKPIT_VERSION line to stamp.' }
    [IO.File]::WriteAllText($stamp, $stamped, (New-Object Text.UTF8Encoding($false)))

    # The installer scripts, licence, documentation and example.
    $installer = Join-Path $repo 'installer'
    Copy-Item -LiteralPath (Join-Path $installer 'install.ps1') -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $installer 'uninstall.ps1') -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $installer 'README.txt') -Destination $Destination
    New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'setup') | Out-Null
    Copy-Item -LiteralPath (Join-Path $installer 'setup\AgenticWorkflow.Setup.ps1') -Destination (Join-Path $Destination 'setup')
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $Destination
    New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'docs'), (Join-Path $Destination 'examples') | Out-Null
    Copy-Item -Path (Join-Path $repo 'docs\*.md') -Destination (Join-Path $Destination 'docs')
    Copy-Item -LiteralPath (Join-Path $repo 'examples\agenticworkflow.json') -Destination (Join-Path $Destination 'examples')

    # The manifests: the one inside bin (read by `aw doctor`), then the one for the whole release (read by install.ps1).
    Write-AwManifest -Path (Join-Path $bin 'aw-package.json') -Version $Version -Files (Get-AwBundleFiles -Root $bin -Exclude @('aw-package.json'))
    Write-AwManifest -Path (Join-Path $Destination 'manifest.json') -Version $Version -Files (Get-AwBundleFiles -Root $Destination -Exclude @('manifest.json'))
}

# ---- audit ----------------------------------------------------------------------------------------------------------------------------

if (-not ('AwPackaging.ByteSearch' -as [type])) {
    Add-Type -Namespace AwPackaging -Name ByteSearch -MemberDefinition @'
public static bool Contains(byte[] haystack, byte[] needle)
{
    if (needle.Length == 0 || haystack.Length < needle.Length) return false;
    int last = haystack.Length - needle.Length;
    byte first = needle[0];
    for (int i = 0; i <= last; i++)
    {
        if (haystack[i] != first) continue;
        int j = 1;
        while (j < needle.Length && haystack[i + j] == needle[j]) j++;
        if (j == needle.Length) return true;
    }
    return false;
}
'@
}

# Names that must never be in a release.
$script:ForbiddenPatterns = @(
    '(^|/)\.git(/|$)', '(^|/)\.gitignore$', '(^|/)\.gitattributes$', '(^|/)\.vscode/', '(^|/)node_modules/', '(^|/)test(s)?/', '(^|/)obj/', '(^|/)artifacts/', '(^|/)TestResults/',
    '\.pdb$', '\.log$', '\.db$', '\.db-(wal|shm)$', '\.sqlite3?$', '\.(png|jpe?g|gif|bmp)$', '\.pem$', '\.pfx$', '\.key$', '(^|/)\.env', '\.tmp$',
    '(^|/)driver\.js$', '(^|/)install\.mjs$', '(^|/)package\.json$', '(^|/)jsconfig\.json$', '\.d\.ts$',
    '\.Tests\.', '(^|/)xunit', '(^|/)Microsoft\.NET\.Test', '(^|/)testhost', '(^|/)Microsoft\.TestPlatform', '(^|/)coverlet',
    '(^|/)controller/', '(^|/)runs/'
)

function Test-AwBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [string]$RepositoryRoot,
        [switch]$RunSmokeTest
    )

    $problems = New-Object 'System.Collections.Generic.List[string]'
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $files = @(Get-ChildItem -LiteralPath $full -Recurse -File -Force)

    foreach ($required in 'install.ps1', 'uninstall.ps1', 'LICENSE', 'README.txt', 'manifest.json', 'setup/AgenticWorkflow.Setup.ps1', 'bin/aw.exe', 'bin/AgenticWorkflow.ClaudeEvidenceHook.exe', 'bin/aw-package.json',
                           'cockpit/main.js', 'cockpit/ui.js', 'cockpit/mod.json', 'cockpit/lib/version.js', 'docs/getting-started.md', 'examples/agenticworkflow.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $full $required.Replace('/', '\')) -PathType Leaf)) { $problems.Add("missing: $required") }
    }

    # The Pi extension is embedded in the runtime assembly; a bundle without that assembly has no Pi support.
    $runtime = Join-Path $full 'bin\AgenticWorkflow.Runtime.Herdr.dll'
    if (-not (Test-Path -LiteralPath $runtime)) { $problems.Add('missing: bin/AgenticWorkflow.Runtime.Herdr.dll (the Pi extension is embedded in it)') }
    elseif (-not [AwPackaging.ByteSearch]::Contains([IO.File]::ReadAllBytes($runtime), [Text.Encoding]::UTF8.GetBytes('aw-pi-extension.mjs'))) { $problems.Add('AgenticWorkflow.Runtime.Herdr.dll does not contain the Pi extension') }

    # Native and runtime files the application cannot start without (self-contained: the runtime itself is part of the bundle).
    foreach ($native in 'bin/e_sqlite3.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $full $native.Replace('/', '\')))) { $problems.Add("missing: $native") }
    }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/')
        foreach ($pattern in $script:ForbiddenPatterns) {
            if ($relative -match $pattern) { $problems.Add("must not be in a release: $relative"); break }
        }
    }

    # The historical documents live outside the repository and never ship.
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $full 'docs') -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -match '^(M\d|V\d)') { $problems.Add("historical document in docs: $($file.Name)") }
    }

    # No developer machine details: the repository's location, the user's profile, the user's name.
    $needles = New-Object 'System.Collections.Generic.List[string]'
    if ($RepositoryRoot) {
        $repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
        $needles.Add($repo); $needles.Add($repo.Replace('\', '/'))
    }
    $needles.Add([Environment]::GetFolderPath('UserProfile'))
    $needles.Add([Environment]::GetFolderPath('UserProfile').Replace('\', '/'))
    if ($env:USERNAME -and $env:USERNAME.Length -ge 4) { $needles.Add($env:USERNAME) }

    $encoded = @()
    foreach ($needle in ($needles | Select-Object -Unique)) {
        $encoded += , @($needle, [Text.Encoding]::UTF8.GetBytes($needle))
        $encoded += , @($needle, [Text.Encoding]::Unicode.GetBytes($needle))
    }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/')
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        foreach ($pair in $encoded) {
            if ([AwPackaging.ByteSearch]::Contains($bytes, $pair[1])) { $problems.Add("contains a developer machine detail ('$($pair[0])'): $relative"); break }
        }
    }

    # One version everywhere.
    try {
        $outer = Get-Content -LiteralPath (Join-Path $full 'manifest.json') -Raw | ConvertFrom-Json
        $bundled = Get-Content -LiteralPath (Join-Path $full 'bin\aw-package.json') -Raw | ConvertFrom-Json
        if ($outer.version -ne $Version) { $problems.Add("manifest.json says $($outer.version), expected $Version") }
        if ($bundled.version -ne $Version) { $problems.Add("bin/aw-package.json says $($bundled.version), expected $Version") }
        if ($outer.platform -ne $script:Platform) { $problems.Add("manifest.json says platform $($outer.platform), expected $script:Platform") }

        $stampLine = Select-String -LiteralPath (Join-Path $full 'cockpit\lib\version.js') -Pattern 'COCKPIT_VERSION = (.*);' | Select-Object -First 1
        if (-not $stampLine -or $stampLine.Matches[0].Groups[1].Value -ne "`"$Version`"") { $problems.Add("cockpit/lib/version.js is not stamped with $Version") }

        # Every manifest entry matches the file, so the bundle describes itself truthfully.
        foreach ($entry in $outer.files) {
            $path = Join-Path $full ([string]$entry.path).Replace('/', '\')
            if (-not (Test-Path -LiteralPath $path)) { $problems.Add("manifest lists a missing file: $($entry.path)") }
            elseif ((Get-AwFileHash -Path $path) -ne $entry.sha256) { $problems.Add("manifest hash does not match: $($entry.path)") }
        }
    }
    catch { $problems.Add("the manifests cannot be read: $($_.Exception.Message)") }

    $palette = Join-Path $full 'cockpit\mod.json'
    if (Test-Path -LiteralPath $palette) {
        $mod = Get-Content -LiteralPath $palette -Raw | ConvertFrom-Json
        if (-not ($mod.PSObject.Properties.Name -contains 'palette') -or -not $mod.palette) { $problems.Add('cockpit/mod.json declares no command-palette entry') }
    }

    if ($RunSmokeTest) {
        $shown = & (Join-Path $full 'bin\aw.exe') --version
        if ($LASTEXITCODE -ne 0 -or $shown -ne "aw $Version") { $problems.Add("bin/aw.exe --version printed '$shown', expected 'aw $Version'") }
    }

    $problems.ToArray()
}

# ---- the archive ----------------------------------------------------------------------------------------------------------------------

# A ZIP that is the same bytes for the same input: entries in ordinal order, forward slashes, a fixed timestamp, one top-level folder named for the release.
function New-AwZip {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$TopFolder)

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($Path))) | Out-Null

    $stamp = New-Object DateTimeOffset (2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
    $archive = New-Object IO.Compression.ZipArchive ($stream, [IO.Compression.ZipArchiveMode]::Create, $false)

    try {
        foreach ($file in (Get-AwBundleFiles -Root $full)) {
            $entry = $archive.CreateEntry("$TopFolder/$($file.Path)", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $target = $entry.Open()
            try { $source = [IO.File]::OpenRead($file.Full); try { $source.CopyTo($target) } finally { $source.Dispose() } }
            finally { $target.Dispose() }
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}
