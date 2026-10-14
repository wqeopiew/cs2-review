$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRelease = Join-Path $taskRoot 'artifacts\release-round3\CS2Review.exe'
if (-not (Test-Path -LiteralPath $taskRelease)) { $taskRelease = Join-Path $taskRoot 'artifacts\release\CS2Review.exe' }
if (Test-Path -LiteralPath $taskRelease) {
    Start-Process -FilePath $taskRelease -WindowStyle Hidden
} else {
    . (Join-Path $PSScriptRoot 'environment.ps1')
    $taskApp = Join-Path $taskRoot 'artifacts\dev\CS2Review.dll'
    if (-not (Test-Path -LiteralPath $taskApp)) { throw 'Run scripts/build.ps1 first.' }
    Start-Process -FilePath $taskDotnet -ArgumentList @(('"'+$taskApp+'"')) -WindowStyle Hidden
}
