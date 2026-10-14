param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null

function Install-LocalArchive([string]$Url, [string]$ArchiveName, [string]$Hash, [string]$Algorithm, [string]$Destination) {
    $taskArchive = Join-Path $taskTools $ArchiveName
    if (-not (Test-Path -LiteralPath $taskArchive)) {
        Invoke-WebRequest -Uri $Url -OutFile $taskArchive
    }
    if ((Get-FileHash -LiteralPath $taskArchive -Algorithm $Algorithm).Hash.ToLowerInvariant() -ne $Hash) {
        throw "$ArchiveName checksum mismatch; delete this archive and retry."
    }
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $Destination -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $taskTools 'dotnet\dotnet.exe'))) {
    Install-LocalArchive 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' 'dotnet.zip' '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430' 'SHA512' (Join-Path $taskTools 'dotnet')
}
if (-not (Test-Path -LiteralPath (Join-Path $taskTools 'go\bin\go.exe'))) {
    Install-LocalArchive 'https://go.dev/dl/go1.27.2.windows-amd64.zip' 'go.zip' '1314008898bd40df77af4b014f777f08873dbdfbcd3d92308728ee03304fe04f' 'SHA256' $taskTools
}
. (Join-Path $PSScriptRoot 'environment.ps1')
. (Join-Path $PSScriptRoot 'map-runtime.ps1')
$taskMapManifest = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'tools\cs2-map-exporter\runtime-manifest.json') | ConvertFrom-Json
$taskMapRuntime = Join-Path $taskRoot $taskMapManifest.workspaceToolDirectory
if (-not (Test-Path -LiteralPath (Join-Path $taskMapRuntime 'Source2Viewer-CLI.exe'))) {
    Install-LocalArchive $taskMapManifest.source 'source2viewer-20.0.zip' $taskMapManifest.archiveSha256 'SHA256' $taskMapRuntime
}
Copy-TaskMapRuntime $taskMapRuntime
& (Join-Path $taskTools 'dotnet\dotnet.exe') --version
& (Join-Path $taskTools 'go\bin\go.exe') version
