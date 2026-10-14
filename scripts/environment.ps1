$taskProjectRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskProjectRoot '.tools\dotnet\dotnet.exe'
$taskGo = Join-Path $taskProjectRoot '.tools\go\bin\go.exe'
$env:DOTNET_CLI_HOME = Join-Path $taskProjectRoot '.tools\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $taskProjectRoot '.tools\nuget'
$taskNugetConfig = Join-Path $taskProjectRoot 'NuGet.Config'
$env:GOPATH = Join-Path $taskProjectRoot '.tools\go-path'
$env:GOCACHE = Join-Path $taskProjectRoot '.tools\go-cache'
$env:GOMODCACHE = Join-Path $taskProjectRoot '.tools\go-path\pkg\mod'
$env:GOTOOLCHAIN = 'local'
if (-not (Test-Path -LiteralPath $taskDotnet) -or -not (Test-Path -LiteralPath $taskGo)) {
    throw 'Run scripts/bootstrap.ps1 first.'
}
