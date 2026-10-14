$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$referenceRoot = Join-Path (Split-Path -Parent $repoRoot) 'TouristMod-AutoTour'
$localDotnet = Join-Path $referenceRoot '.dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$sharedPackages = Join-Path $referenceRoot '.dotnet-home\.nuget\packages'
if (Test-Path -LiteralPath $sharedPackages) {
    $env:NUGET_PACKAGES = $sharedPackages
}

$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 10) {
    throw "Dalamud.NET.Sdk/15.0.0 requires .NET 10; found '$sdkVersion'."
}

Push-Location $repoRoot
try {
    & $dotnet restore '.\MutaMaxxing.csproj' --ignore-failed-sources -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }

    & $dotnet build '.\MutaMaxxing.csproj' --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    $buildOutput = Join-Path $repoRoot 'bin\Debug'
    $pluginOutput = Join-Path $referenceRoot 'local-plugin'
    New-Item -ItemType Directory -Force -Path $pluginOutput | Out-Null
    $pluginOutput = (Resolve-Path -LiteralPath $pluginOutput).Path

    $artifacts = @(Get-ChildItem -LiteralPath $buildOutput -File |
        Where-Object { $_.Name -like 'MutaMaxxing.*' -and $_.Extension -in '.dll', '.json' })
    if (-not ($artifacts | Where-Object { $_.Name -eq 'MutaMaxxing.dll' })) {
        throw "MutaMaxxing.dll was not found in $buildOutput."
    }

    # The staging folder is shared with TouristMod and AnoMech; remove only
    # MutaMaxxing-owned files so rebuilding this plugin preserves the others.
    Get-ChildItem -LiteralPath $pluginOutput -File |
        Where-Object { $_.Name -like 'MutaMaxxing.*' } |
        Remove-Item -Force

    foreach ($artifact in $artifacts) {
        Copy-Item -LiteralPath $artifact.FullName -Destination $pluginOutput -Force
    }

    $sourceDll = Join-Path $buildOutput 'MutaMaxxing.dll'
    $destinationDll = Join-Path $pluginOutput 'MutaMaxxing.dll'
    $sourceHash = (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destinationDll -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "MutaMaxxing.dll copy verification failed: source hash $sourceHash != destination hash $destinationHash."
    }

    Write-Host "Dalamud Dev Plugin Locations folder: $pluginOutput"
    Write-Host "Plugin DLL: $destinationDll"
    Write-Host "MutaMaxxing.dll SHA256: $destinationHash"
}
finally {
    Pop-Location
}
