param(
    [string]$DotNetPath = "",
    [string]$NuGetPackages = "",
    [string]$ArtifactsPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed (exit $LASTEXITCODE): $Command $($Arguments -join ' ')"
    }
}

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
}
if ([string]::IsNullOrWhiteSpace($NuGetPackages)) {
    $NuGetPackages = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES
    } else { Join-Path $repoRoot ".nuget\packages" }
}
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $ArtifactsPath = Join-Path $repoRoot "artifacts\build-$stamp-$PID"
}

New-Item -ItemType Directory -Path $NuGetPackages -Force | Out-Null
New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null
$sdkVersion = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 10) {
    throw ".NET 10 SDK is required. Current version: $sdkVersion"
}

Push-Location $repoRoot
try {
    Invoke-Native "npm" @("run", "typecheck")
    Invoke-Native "npm" @("run", "lint")
    Invoke-Native "npm" @("run", "build")
    Invoke-Native $DotNetPath @("restore", "LanSwitch.slnx", "--locked-mode", "--packages", $NuGetPackages, "--configfile", "NuGet.Config", "--artifacts-path", $ArtifactsPath)
    Invoke-Native $DotNetPath @("test", "LanSwitch.slnx", "--no-restore", "--configuration", "Release", "--artifacts-path", $ArtifactsPath)
    Write-Host "DeskMesh build and tests passed." -ForegroundColor Green
    Write-Host "Isolated build directory: $ArtifactsPath" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
