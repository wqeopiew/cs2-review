param([string]$ReleaseDirectory='artifacts\release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'environment.ps1')
$taskReleaseRoot = if ([IO.Path]::IsPathRooted($ReleaseDirectory)) { [IO.Path]::GetFullPath($ReleaseDirectory) } else { [IO.Path]::GetFullPath((Join-Path $taskProjectRoot $ReleaseDirectory)) }
if (-not $taskReleaseRoot.StartsWith((Join-Path $taskProjectRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'License output must stay in workspace artifacts.' }
$taskLicenseOutput = Join-Path $taskReleaseRoot 'licenses'
New-Item -ItemType Directory -Path $taskLicenseOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'LICENSE') -Destination (Join-Path $taskLicenseOutput 'CS2Review-MIT.txt') -Force
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'licenses\Apache-2.0.txt') -Destination (Join-Path $taskLicenseOutput 'Apache-2.0.txt') -Force
foreach ($taskProjectLicense in (Get-ChildItem -LiteralPath (Join-Path $taskProjectRoot 'licenses') -File)) {
    Copy-Item -LiteralPath $taskProjectLicense.FullName -Destination (Join-Path $taskLicenseOutput $taskProjectLicense.Name) -Force
}
Copy-Item -LiteralPath (Join-Path $taskProjectRoot '.tools\go\LICENSE') -Destination (Join-Path $taskLicenseOutput 'Go-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $taskProjectRoot '.tools\dotnet\LICENSE.txt') -Destination (Join-Path $taskLicenseOutput 'Dotnet-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $taskProjectRoot '.tools\dotnet\ThirdPartyNotices.txt') -Destination (Join-Path $taskLicenseOutput 'Dotnet-ThirdPartyNotices.txt') -Force
$taskNotice = [Collections.Generic.List[string]]::new()
$taskNotice.Add('# Third-party notices')
$taskNotice.Add('')
$taskNotice.Add('本清单依据本次构建实际使用的 Go 模块和锁定的 NuGet 包生成。完整许可证随发布物保存在 licenses 目录。')
$taskNotice.Add('')
foreach ($taskFramework in ((Get-Content -Raw -LiteralPath (Join-Path $taskReleaseRoot 'CS2Review.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions.includedFrameworks)) {
    $taskRuntimePackage = switch ($taskFramework.name) {
        'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
        'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
        default { throw ('Runtime license mapping missing: ' + $taskFramework.name) }
    }
    $taskRuntimePackageRoot = Join-Path $env:NUGET_PACKAGES ($taskRuntimePackage + '\' + $taskFramework.version)
    $taskRuntimeLicenses = @(Get-ChildItem -LiteralPath $taskRuntimePackageRoot -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    if ($taskRuntimeLicenses.Count -eq 0) { throw ('Runtime license not found: ' + $taskFramework.name) }
    foreach ($taskRuntimeLicense in $taskRuntimeLicenses) {
        Copy-Item -LiteralPath $taskRuntimeLicense.FullName -Destination (Join-Path $taskLicenseOutput ($taskFramework.name + '-' + $taskFramework.version + '-' + $taskRuntimeLicense.Name)) -Force
    }
    $taskNotice.Add('- `' + $taskFramework.name + '` ' + $taskFramework.version + ': exact runtime MIT license and available third-party notices copied from the matching runtime package.')
}
$taskNotice.Add('')
Push-Location (Join-Path $taskProjectRoot 'parser')
try {
    $taskModules = & $taskGo list -deps -f '{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}' .
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate parser dependencies.' }
} finally { Pop-Location }
foreach ($taskModule in ($taskModules | Where-Object { $_ -match '\|' } | Sort-Object -Unique)) {
    $taskParts = $taskModule.Split('|')
    if ($taskParts[0] -eq 'cs2review/parser') { continue }
    $taskSafeName = ($taskParts[0] + '-' + $taskParts[1]) -replace '[^A-Za-z0-9._-]', '_'
    $taskLicenseFiles = Get-ChildItem -LiteralPath $taskParts[2] -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' }
    if ($taskLicenseFiles.Count -eq 0) { throw ('License not found for ' + $taskParts[0]) }
    foreach ($taskLicenseFile in $taskLicenseFiles) {
        Copy-Item -LiteralPath $taskLicenseFile.FullName -Destination (Join-Path $taskLicenseOutput ($taskSafeName + '-' + $taskLicenseFile.Name)) -Force
    }
    $taskNotice.Add('- `' + $taskParts[0] + '` ' + $taskParts[1] + ': ' + (($taskLicenseFiles | ForEach-Object Name) -join ', '))
}
$taskLockPath = Join-Path $taskProjectRoot 'src\CS2Review.Core\packages.lock.json'
if (Test-Path -LiteralPath $taskLockPath) {
    $taskLock = Get-Content -Raw -LiteralPath $taskLockPath | ConvertFrom-Json
    $taskSeenPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskFramework in $taskLock.dependencies.PSObject.Properties) {
        foreach ($taskPackage in $taskFramework.Value.PSObject.Properties) {
            $taskPackageName = $taskPackage.Name
            $taskVersion = $taskPackage.Value.resolved
            if (-not $taskSeenPackages.Add($taskPackageName + '/' + $taskVersion)) { continue }
            $taskPackageDirectory = Join-Path $env:NUGET_PACKAGES ($taskPackageName.ToLowerInvariant() + '\' + $taskVersion)
            $taskNuspec = Get-ChildItem -LiteralPath $taskPackageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
            if (-not $taskNuspec) { throw ('Package metadata missing for ' + $taskPackageName) }
            [xml]$taskMetadata = Get-Content -Raw -LiteralPath $taskNuspec.FullName
            $taskLicenseNode = $taskMetadata.SelectSingleNode("//*[local-name()='license']")
            $taskLicenseText = if ($taskLicenseNode) { $taskLicenseNode.InnerText } else { 'See package metadata' }
            $taskNotice.Add('- `' + $taskPackageName + '` ' + $taskVersion + ': ' + $taskLicenseText)
            $taskPrefix = $taskPackageName + '-' + $taskVersion
            Copy-Item -LiteralPath $taskNuspec.FullName -Destination (Join-Path $taskLicenseOutput ($taskPrefix + '.nuspec')) -Force
            $taskPackageLicenses = Get-ChildItem -LiteralPath $taskPackageDirectory -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|License)' }
            foreach ($taskLicenseFile in $taskPackageLicenses) {
                Copy-Item -LiteralPath $taskLicenseFile.FullName -Destination (Join-Path $taskLicenseOutput ($taskPrefix + '-' + $taskLicenseFile.Name)) -Force
            }
        }
    }
}
$taskNotice.Add('')
$taskNotice.Add('Go 运行库遵循 BSD 许可证。Microsoft .NET 的许可证及归属见附带文件；SQLite 为 public domain，SQLitePCLRaw 的归属见 NuGet 包元数据和许可文件。')
$taskNotice.Add('')
$taskNotice.Add('地图提取工具 Source2Viewer 20.0 遵循 MIT，精确 CLI 依赖清单、原生库及内嵌 .NET 运行时的完整许可通知见 map-tools/runtime-manifest.json、map-tools/cli-dependency-inventory.json 和 map-tools/notices。包内不含 Valve 游戏地图资源；地图由用户本机安装读取。离线校准工具遵循本项目 MIT 许可，运行时与 SQLite 通知同见 licenses。')
[IO.File]::WriteAllLines((Join-Path $taskProjectRoot 'THIRD-PARTY-NOTICES.md'), $taskNotice, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $taskReleaseRoot 'THIRD-PARTY-NOTICES.md') -Force
