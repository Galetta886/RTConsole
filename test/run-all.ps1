$ErrorActionPreference = 'Stop'
$tests = 'smoke.ps1','smoke_profiles.ps1','smoke_world.ps1','security.ps1'

foreach ($test in $tests) {
  Write-Host "`n=== $test ==="
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $test)
  if ($LASTEXITCODE -ne 0) { throw "$test failed with exit code $LASTEXITCODE" }
}

Write-Host "`nAll isolated tests passed."
