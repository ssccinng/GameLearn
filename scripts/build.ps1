param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not (Test-Path 'src/GameLearn/Assets/ecdict.sqlite')) {
        throw 'Missing offline dictionary. Run python scripts/prepare_dictionary.py first.'
    }
    dotnet restore GameLearn.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    python scripts/collect_notices.py
    if ($LASTEXITCODE -ne 0) { throw 'License collection failed.' }
    dotnet run --project tests/GameLearn.Tests -c Release -- artifacts/test-results.json
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $contained = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish src/GameLearn/GameLearn.csproj -c Release -r win-x64 --self-contained $contained -o publish/GameLearn
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    # Runtime pack licenses only appear after RID-specific restore/publish.
    python scripts/collect_notices.py
    if ($LASTEXITCODE -ne 0) { throw 'Runtime license collection failed.' }
    Copy-Item src/GameLearn/Assets/licenses/* -Destination publish/GameLearn/Assets/licenses -Force
    Copy-Item README.md,TEST_REPORT.md -Destination publish/GameLearn
    Write-Host 'Ready: publish/GameLearn/GameLearn.exe'
} finally { Pop-Location }
