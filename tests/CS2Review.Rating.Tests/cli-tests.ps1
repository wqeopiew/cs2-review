param([string]$ToolDll, [string]$TestDll)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\environment.ps1')
$ratingFixtureDir=Join-Path $taskProjectRoot ('artifacts\rating-cli-tests\'+[guid]::NewGuid().ToString('N'))
& $taskDotnet $TestDll --export-synthetic $ratingFixtureDir
if($LASTEXITCODE -ne 0){throw 'Synthetic fixture export failed.'}
$ratingManifest=Join-Path $ratingFixtureDir 'manifest.json'
$ratingModel=Join-Path $ratingFixtureDir 'model.json'
$ratingCv=Join-Path $ratingFixtureDir 'cv.json'
$ratingScore=Join-Path $ratingFixtureDir 'score.json'
& $taskDotnet $ToolDll validate $ratingManifest
if($LASTEXITCODE -ne 0){throw 'CLI manifest validation failed.'}
& $taskDotnet $ToolDll train $ratingManifest $ratingModel
if($LASTEXITCODE -ne 0){throw 'CLI training failed.'}
& $taskDotnet $ToolDll cross-validate $ratingManifest $ratingCv
if($LASTEXITCODE -ne 0){throw 'CLI whole-match validation failed.'}
& $taskDotnet $ToolDll score $ratingModel (Join-Path $ratingFixtureDir 'match-0.json') p $ratingScore
if($LASTEXITCODE -ne 0){throw 'CLI scoring failed.'}
$ratingOutput=Get-Content -LiteralPath $ratingScore -Raw | ConvertFrom-Json
if($null -eq $ratingOutput.overall -or $null -eq $ratingOutput.roundSwing -or $ratingOutput.calibrationStatus -ne 'experimental'){throw 'CLI score invented calibration or unavailable output.'}
$ratingValidation=Get-Content -LiteralPath $ratingCv -Raw | ConvertFrom-Json
if($ratingValidation.folds.Count -ne 3 -or $ratingValidation.rating.count -ne 6){throw 'CLI fold/row count mismatch.'}
Add-Content -LiteralPath (Join-Path $ratingFixtureDir 'match-0.json') -Value ' '
& $taskDotnet $ToolDll validate $ratingManifest
if($LASTEXITCODE -eq 0){throw 'CLI accepted a changed canonical file.'}
Write-Output 'PASS CLI validate/train/whole-match-CV/score/hash-rejection (SYNTHETIC only)'
