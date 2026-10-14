param([switch]$Publish, [switch]$SmokeTest, [string]$ReleaseDirectory='artifacts\release-round3')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'environment.ps1')
. (Join-Path $PSScriptRoot 'map-runtime.ps1')
$taskReleaseRoot = if ([IO.Path]::IsPathRooted($ReleaseDirectory)) { [IO.Path]::GetFullPath($ReleaseDirectory) } else { [IO.Path]::GetFullPath((Join-Path $taskProjectRoot $ReleaseDirectory)) }
$taskArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot 'artifacts'))
if (-not $taskReleaseRoot.StartsWith($taskArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release directory must be a child of workspace artifacts.' }
Push-Location $taskProjectRoot
try {
    New-Item -ItemType Directory -Path 'artifacts\parser','artifacts\dev','artifacts\tests' -Force | Out-Null
    Push-Location 'parser'
    try {
        & $taskGo test -count=1 ./...
        if ($LASTEXITCODE -ne 0) { throw 'Parser tests failed.' }
        & $taskGo vet ./...
        if ($LASTEXITCODE -ne 0) { throw 'Parser vet failed.' }
        & $taskGo build -trimpath -ldflags '-s -w' -o '..\artifacts\parser\cs2-parser.exe' .
        if ($LASTEXITCODE -ne 0) { throw 'Parser build failed.' }
    } finally { Pop-Location }
    foreach ($taskSuite in @('Core','Context','Map','Rating','Report')) {
        $taskTestProject = 'tests\CS2Review.' + $taskSuite + '.Tests\CS2Review.' + $taskSuite + '.Tests.csproj'
        & $taskDotnet build $taskTestProject -c Release "-p:RestoreConfigFile=$taskNugetConfig"
        if ($LASTEXITCODE -ne 0) { throw ($taskSuite + ' test build failed.') }
        & $taskDotnet run --project $taskTestProject -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw ($taskSuite + ' tests failed.') }
    }
    & $taskDotnet build 'tools\CS2Review.RatingCalibration\CS2Review.RatingCalibration.csproj' -c Release "-p:RestoreConfigFile=$taskNugetConfig"
    if ($LASTEXITCODE -ne 0) { throw 'Calibration tool build failed.' }
    & (Join-Path $taskProjectRoot 'tests\CS2Review.Rating.Tests\cli-tests.ps1') -ToolDll (Join-Path $taskProjectRoot 'tools\CS2Review.RatingCalibration\bin\Release\net10.0\CS2Review.RatingCalibration.dll') -TestDll (Join-Path $taskProjectRoot 'tests\CS2Review.Rating.Tests\bin\Release\net10.0\CS2Review.Rating.Tests.dll')
    & $taskDotnet build 'src\CS2Review.Desktop\CS2Review.Desktop.csproj' -c Release -o 'artifacts\dev' "-p:RestoreConfigFile=$taskNugetConfig"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    Copy-Item -LiteralPath 'artifacts\parser\cs2-parser.exe' -Destination 'artifacts\dev\cs2-parser.exe' -Force
    Copy-TaskMapRuntime (Join-Path $taskProjectRoot 'artifacts\dev\map-tools')
    if ($SmokeTest) {
        $taskImportProcess = Start-Process -FilePath $taskDotnet -ArgumentList @('artifacts\dev\CS2Review.dll','--self-test-import','artifacts\tests\import') -WindowStyle Hidden -Wait -PassThru
        if ($taskImportProcess.ExitCode -ne 0) { throw 'Importer tests failed.' }
        $taskUiProcess = Start-Process -FilePath $taskDotnet -ArgumentList @('artifacts\dev\CS2Review.dll','--self-test-ui','artifacts\tests\ui') -WindowStyle Hidden -Wait -PassThru
        if ($taskUiProcess.ExitCode -ne 0) { throw 'WPF interaction tests failed.' }
        $taskSmokeProcess = Start-Process -FilePath $taskDotnet -ArgumentList @('artifacts\dev\CS2Review.dll','--smoke-test','artifacts\tests\window.png','--smoke-test-data-directory','artifacts\tests\smoke') -WindowStyle Hidden -Wait -PassThru
        if ($taskSmokeProcess.ExitCode -ne 0) { throw 'WPF smoke test failed.' }
    }
    if ($Publish) {
        & $taskDotnet build 'src\CS2Review.Core\CS2Review.Core.csproj' -c Release -t:Rebuild --no-restore -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Release Core rebuild failed.' }
        & $taskDotnet publish 'src\CS2Review.Desktop\CS2Review.Desktop.csproj' -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $taskReleaseRoot "-p:RestoreConfigFile=$taskNugetConfig"
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath 'artifacts\parser\cs2-parser.exe' -Destination (Join-Path $taskReleaseRoot 'cs2-parser.exe') -Force
        Copy-TaskMapRuntime (Join-Path $taskReleaseRoot 'map-tools')
        & $taskDotnet publish 'tools\CS2Review.RatingCalibration\CS2Review.RatingCalibration.csproj' -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:PublishTrimmed=false -o (Join-Path $taskReleaseRoot 'calibration') "-p:RestoreConfigFile=$taskNugetConfig"
        if ($LASTEXITCODE -ne 0) { throw 'Rating calibration publish failed.' }
        Copy-Item -LiteralPath 'tools\CS2Review.RatingCalibration\README.md' -Destination (Join-Path $taskReleaseRoot 'calibration\README.md') -Force
        & (Join-Path $PSScriptRoot 'licenses.ps1') -ReleaseDirectory $taskReleaseRoot
        $taskPublishedReadme = (Get-Content -Raw -LiteralPath 'README.md').Replace('(tools/CS2Review.RatingCalibration/README.md)','(calibration/README.md)')
        [IO.File]::WriteAllText((Join-Path $taskReleaseRoot 'README.md'), $taskPublishedReadme, [Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath 'docs' -Destination $taskReleaseRoot -Recurse -Force
        New-Item -ItemType Directory -Path (Join-Path $taskReleaseRoot 'rating-models') -Force | Out-Null
        & $taskDotnet 'tests\CS2Review.Rating.Tests\bin\Release\net10.0\CS2Review.Rating.Tests.dll' --export-built-in (Join-Path $taskReleaseRoot 'rating-models\round3.json')
        if ($LASTEXITCODE -ne 0) { throw 'Built-in rating model export failed.' }
        Compress-Archive -Path (Join-Path $taskReleaseRoot '*') -DestinationPath 'artifacts\CS2Review-win-x64.zip' -Force
        $taskReleaseDigest = (Get-FileHash -LiteralPath 'artifacts\CS2Review-win-x64.zip' -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath 'artifacts\CS2Review-win-x64.zip.sha256' -Value ($taskReleaseDigest + '  CS2Review-win-x64.zip') -Encoding ascii
    }
} finally { Pop-Location }
