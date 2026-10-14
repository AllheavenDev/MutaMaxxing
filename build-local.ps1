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

    Write-Host "Dalamud Dev Plugin Locations folder: $(Join-Path $repoRoot 'bin\Debug')"
}
finally {
    Pop-Location
}
