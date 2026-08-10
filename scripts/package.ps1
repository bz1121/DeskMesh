param(
    [string]$DotNetPath = "",
    [string]$NuGetPackages = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildPropertiesPath = Join-Path $repoRoot "Directory.Build.props"
$protocolContractsPath = Join-Path $repoRoot "src\LanSwitch.Core\Protocol\ProtocolContracts.cs"

function Get-ProductVersion {
    if (-not (Test-Path -LiteralPath $buildPropertiesPath -PathType Leaf)) {
        throw "Version source not found: $buildPropertiesPath"
    }

    [xml]$buildProperties = Get-Content -LiteralPath $buildPropertiesPath -Raw
    $versionNode = $buildProperties.SelectSingleNode("/Project/PropertyGroup/Version")
    $version = if ($null -eq $versionNode) { "" } else { $versionNode.InnerText.Trim() }
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Directory.Build.props must contain a concrete semantic Version value. Found: '$version'"
    }

    return $version
}

function Get-ProtocolConstant([string]$Name) {
    if (-not (Test-Path -LiteralPath $protocolContractsPath -PathType Leaf)) { return $null }
    $source = Get-Content -LiteralPath $protocolContractsPath -Raw
    $pattern = "public\s+const\s+int\s+$([Regex]::Escape($Name))\s*=\s*(\d+)\s*;"
    $match = [Regex]::Match($source, $pattern)
    if (-not $match.Success) { return $null }
    return [int]$match.Groups[1].Value
}

function Get-GitValue([string[]]$Arguments) {
    $value = & git -C $repoRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($value -join "`n").Trim()
}

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
}
if ([string]::IsNullOrWhiteSpace($NuGetPackages)) {
    $NuGetPackages = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $env:NUGET_PACKAGES
    } else { Join-Path $repoRoot ".nuget\packages" }
}

$productVersion = Get-ProductVersion
$gitCommit = Get-GitValue @("rev-parse", "--verify", "HEAD")
if ([string]::IsNullOrWhiteSpace($gitCommit)) { $gitCommit = "unknown" }
$gitStatus = Get-GitValue @("status", "--porcelain=v1", "--untracked-files=normal")
$protocolVersion = Get-ProtocolConstant "CurrentVersion"
$minimumCompatibleProtocolVersion = Get-ProtocolConstant "MinimumSupportedVersion"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$artifactRoot = Join-Path $repoRoot "artifacts"
$validationPath = Join-Path $artifactRoot "validation-$stamp-$PID"
$publishBuildPath = Join-Path $artifactRoot "publish-build-$stamp-$PID"
$publishDirectory = Join-Path $artifactRoot "DeskMesh-$productVersion-win-x64-$stamp"
$zipPath = "$publishDirectory.zip"
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& (Join-Path $PSScriptRoot "build.ps1") -DotNetPath $DotNetPath -NuGetPackages $NuGetPackages -ArtifactsPath $validationPath
if ($LASTEXITCODE -ne 0) { throw "Build validation failed." }

Push-Location $repoRoot
try {
    & $DotNetPath restore "src\LanSwitch.Agent\LanSwitch.Agent.csproj" `
        --locked-mode --runtime win-x64 --packages $NuGetPackages --configfile "NuGet.Config" --artifacts-path $publishBuildPath `
        -p:NuGetLockFilePath=packages.win-x64.lock.json
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
    & $DotNetPath publish "src\LanSwitch.Agent\LanSwitch.Agent.csproj" `
        --no-restore --configuration Release --runtime win-x64 --self-contained true `
        --output $publishDirectory --artifacts-path $publishBuildPath -p:SkipWebBuild=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
    foreach ($readme in @("README.md", "README.zh-CN.md")) {
        Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDirectory $readme)
    }
    Copy-Item -LiteralPath "LICENSE" -Destination (Join-Path $publishDirectory "LICENSE")
    Copy-Item -LiteralPath "THIRD_PARTY_NOTICES.md" -Destination (Join-Path $publishDirectory "THIRD_PARTY_NOTICES.md")
    foreach ($document in @("CHANGELOG.md", "CODE_OF_CONDUCT.md", "CONTRIBUTING.md", "PRIVACY.md", "SECURITY.md", "SUPPORT.md")) {
        Copy-Item -LiteralPath $document -Destination (Join-Path $publishDirectory $document)
    }
    $packageDocsDirectory = Join-Path $publishDirectory "docs"
    New-Item -ItemType Directory -Path $packageDocsDirectory -Force | Out-Null
    foreach ($document in @("ARCHITECTURE.md", "QUICKSTART.zh-CN.md", "RELEASING.md", "SECURITY-MODEL.md")) {
        Copy-Item -LiteralPath (Join-Path "docs" $document) -Destination (Join-Path $packageDocsDirectory $document)
    }

    $packageLicensesDirectory = Join-Path $publishDirectory "licenses"
    New-Item -ItemType Directory -Path $packageLicensesDirectory -Force | Out-Null
    Copy-Item -LiteralPath "licenses\NAUDIO-LICENSE.txt" -Destination (Join-Path $packageLicensesDirectory "NAUDIO-LICENSE.txt")
    Copy-Item -LiteralPath "licenses\PHOSPHOR-ICONS-LICENSE.txt" -Destination (Join-Path $packageLicensesDirectory "PHOSPHOR-ICONS-LICENSE.txt")
    Copy-Item -LiteralPath "node_modules\react\LICENSE" -Destination (Join-Path $packageLicensesDirectory "REACT-REACT-DOM-SCHEDULER-LICENSE.txt")

    $depsPath = Join-Path $publishBuildPath "bin\LanSwitch.Agent\release_win-x64\DeskMesh.deps.json"
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "Build dependency manifest missing: $depsPath"
    }
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $runtimePacks = @(
        @{ Library = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64"; Package = "microsoft.netcore.app.runtime.win-x64"; Prefix = "DOTNET-RUNTIME" },
        @{ Library = "runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64"; Package = "microsoft.aspnetcore.app.runtime.win-x64"; Prefix = "ASPNETCORE-RUNTIME" },
        @{ Library = "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64"; Package = "microsoft.windowsdesktop.app.runtime.win-x64"; Prefix = "WINDOWS-DESKTOP-RUNTIME" }
    )
    $libraryNames = @($deps.libraries.PSObject.Properties.Name)
    foreach ($runtimePack in $runtimePacks) {
        $libraryName = $libraryNames | Where-Object { $_.StartsWith("$($runtimePack.Library)/", [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($libraryName)) { throw "Runtime package missing from DeskMesh.deps.json: $($runtimePack.Library)" }
        $runtimeVersion = $libraryName.Substring($libraryName.IndexOf('/') + 1)
        $runtimePackageDirectory = Join-Path $NuGetPackages "$($runtimePack.Package)\$runtimeVersion"
        $runtimeLicense = Get-ChildItem -LiteralPath $runtimePackageDirectory -File | Where-Object { $_.Name -match '^LICENSE(?:\.TXT)?$' } | Select-Object -First 1
        if ($null -eq $runtimeLicense) { throw "Runtime license missing: $runtimePackageDirectory" }
        Copy-Item -LiteralPath $runtimeLicense.FullName -Destination (Join-Path $packageLicensesDirectory "$($runtimePack.Prefix)-LICENSE.txt")
        $runtimeNotices = Get-ChildItem -LiteralPath $runtimePackageDirectory -File | Where-Object { $_.Name -match '^THIRD-PARTY-NOTICES(?:\.TXT)?$' } | Select-Object -First 1
        if ($null -ne $runtimeNotices) {
            Copy-Item -LiteralPath $runtimeNotices.FullName -Destination (Join-Path $packageLicensesDirectory "$($runtimePack.Prefix)-THIRD-PARTY-NOTICES.txt")
        }
    }

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
        $_.Extension -ieq ".pdb" -or
        $_.Extension -ieq ".dll" -or
        $_.Name -ieq "appsettings.Development.json" -or
        $_.Name -ieq "DeskMesh.deps.json" -or
        $_.Name -ieq "DeskMesh.runtimeconfig.json" -or
        $_.Name -ieq "packages.lock.json" -or
        $_.Name -ieq "packages.win-x64.lock.json" -or
        $_.Name -ieq "web.config" -or
        ($_.Extension -ieq ".exe" -and $_.Name -ine "DeskMesh.exe")
    })
    if ($forbiddenFiles.Count -gt 0) {
        $relativePaths = $forbiddenFiles | ForEach-Object { [IO.Path]::GetRelativePath($publishDirectory, $_.FullName) }
        throw "Publish output contains files forbidden from the public package: $($relativePaths -join ', ')"
    }

    foreach ($requiredPath in @("DeskMesh.exe", "appsettings.json", "wwwroot\index.html")) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredPath) -PathType Leaf)) {
            throw "Required portable package file missing: $requiredPath"
        }
    }

    $manifest = [ordered]@{
        format = 1
        product = "DeskMesh"
        version = $productVersion
        gitCommit = $gitCommit
        gitDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
        architecture = "win-x64"
        createdAt = [DateTimeOffset]::UtcNow.ToString("O")
    }
    if ($null -ne $protocolVersion) {
        $manifest["protocolVersion"] = $protocolVersion
    }
    if ($null -ne $minimumCompatibleProtocolVersion) {
        $manifest["minimumCompatibleProtocolVersion"] = $minimumCompatibleProtocolVersion
    }
    $packageManifest = $manifest | ConvertTo-Json
    Set-Content -LiteralPath (Join-Path $publishDirectory "deskmesh-package.json") -Value $packageManifest -Encoding UTF8
    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Host "Package: $zipPath" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
}
finally {
    Pop-Location
}
