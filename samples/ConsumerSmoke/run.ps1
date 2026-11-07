$ErrorActionPreference = 'Stop'
[Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

# 1. Run pack script
Write-Host "Running scripts/pack.ps1..."
& (Join-Path $PSScriptRoot "../../scripts/pack.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Error "Pack script failed."
    exit 1
}

# 2. Clear only ChronoAudit packages from local cache (Trap T14: NEVER clear the whole cache)
$userProfile = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
$cachePackages = Join-Path $userProfile ".nuget/packages"
if (Test-Path $cachePackages) {
    Get-ChildItem -Path $cachePackages -Filter "chronoaudit.*" -Directory | ForEach-Object {
        Write-Host "Clearing local NuGet cache: $($_.FullName)"
        Remove-Item -Path $_.FullName -Recurse -Force
    }
}

$smokeProj = Join-Path $PSScriptRoot "Smoke.csproj"

# 3. Run net8.0
Write-Host "Running Smoke test on net8.0..."
$out8 = dotnet run --project $smokeProj -f net8.0 2>&1
$out8 | ForEach-Object { Write-Host $_ }
if ($out8 -notmatch "SMOKE OK net8.0") {
    Write-Error "Smoke test failed on net8.0"
    exit 1
}

# 4. Run net10.0
Write-Host "Running Smoke test on net10.0..."
$out10 = dotnet run --project $smokeProj -f net10.0 2>&1
$out10 | ForEach-Object { Write-Host $_ }
if ($out10 -notmatch "SMOKE OK net10.0") {
    Write-Error "Smoke test failed on net10.0"
    exit 1
}

Write-Host "ALL SMOKE TESTS PASSED (K06 DONE)."
exit 0