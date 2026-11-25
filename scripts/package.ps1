<#
.SYNOPSIS
    Builds the Windows x64 release: artifacts\AgenticWorkflow-<version>-win-x64.zip

.DESCRIPTION
    Publishes aw and the evidence hook self-contained (the user needs no .NET), assembles the release folder from known locations, audits it, and writes a reproducible ZIP.
    The version comes from Directory.Build.props, the one place it is kept.

        .\scripts\package.ps1

    Needs the .NET SDK (to build) and network access the first time (to restore the win-x64 runtime pack). The result needs neither.

.PARAMETER OutputDirectory
    Where the ZIP (and the release folder it was made from) go. Default: artifacts under the repository.

.PARAMETER PublishDirectory
    Use an already published folder instead of publishing. For tests; a release is always built by publishing.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'PackageLib.ps1')

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$version = Get-AwProductVersion -RepositoryRoot $repo
$name = "AgenticWorkflow-$version-win-x64"
$work = Join-Path $OutputDirectory 'work'
$bundle = Join-Path $work $name
$zip = Join-Path $OutputDirectory "$name.zip"

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $work 'publish'
    if (Test-Path -LiteralPath $PublishDirectory) { Remove-Item -LiteralPath $PublishDirectory -Recurse -Force }

    # One folder holds both executables and one copy of the runtime. -p:ContinuousIntegrationBuild makes the build deterministic and keeps the repository's path out of the binaries.
    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $PublishDirectory, '-p:ContinuousIntegrationBuild=true', '-p:DebugType=none', '-p:DebugSymbols=false', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false')

    foreach ($project in 'src\AgenticWorkflow.Cli', 'src\AgenticWorkflow.ClaudeEvidenceHook') {
        Write-Host "Publishing $project ..."
        & dotnet publish (Join-Path $repo $project) @common --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project." }
    }
}

Write-Host 'Assembling the release...'
New-AwBundle -PublishDirectory $PublishDirectory -RepositoryRoot $repo -Destination $bundle -Version $version

Write-Host 'Auditing the release...'
$problems = @(Test-AwBundle -Root $bundle -Version $version -RepositoryRoot $repo -RunSmokeTest)
if ($problems.Count -gt 0) { throw ("The release failed its audit:`n  - " + ($problems -join "`n  - ")) }

New-AwZip -Root $bundle -Path $zip -TopFolder $name
$hash = Get-AwFileHash -Path $zip
$size = [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "Built $zip" -ForegroundColor Green
Write-Host "  version  $version"
Write-Host "  size     $size MB"
Write-Host "  sha256   $hash"
