function Copy-TaskMapRuntime([string]$Destination) {
    $taskManifestDirectory = Join-Path $taskProjectRoot 'tools\cs2-map-exporter'
    $taskManifest = Get-Content -Raw -LiteralPath (Join-Path $taskManifestDirectory 'runtime-manifest.json') | ConvertFrom-Json
    if ($taskManifest.schemaVersion -ne 1 -or $taskManifest.auditComplete -ne $true -or $taskManifest.containsValveMapAssets -ne $false) {
        throw 'Map runtime manifest needs a completed dependency notice audit.'
    }
    $taskRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot $taskManifest.workspaceToolDirectory))
    $taskDestinationRoot = [IO.Path]::GetFullPath($Destination)
    if (-not $taskDestinationRoot.StartsWith($taskProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Map runtime destination must stay in this workspace.' }
    New-Item -ItemType Directory -Path $taskDestinationRoot -Force | Out-Null
    foreach ($taskEntry in $taskManifest.runtimeFiles) {
        if ([IO.Path]::IsPathRooted($taskEntry.path) -or $taskEntry.path -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Map runtime entry escapes its root.' }
        $taskPublicFile = [IO.Path]::GetFullPath((Join-Path $taskManifestDirectory $taskEntry.path))
        $taskRuntimeFile = [IO.Path]::GetFullPath((Join-Path $taskRuntimeRoot $taskEntry.path))
        $taskSource = if (Test-Path -LiteralPath $taskPublicFile -PathType Leaf) { $taskPublicFile } else { $taskRuntimeFile }
        if ((Get-Item -LiteralPath $taskSource).Length -ne $taskEntry.bytes -or (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskEntry.sha256) { throw ('Map runtime checksum mismatch: ' + $taskEntry.path) }
        $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskDestinationRoot $taskEntry.path))
        if (-not $taskTarget.StartsWith($taskDestinationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Map runtime target escapes its root.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
        if ($taskSource -ne $taskTarget) { Copy-Item -LiteralPath $taskSource -Destination $taskTarget -Force }
    }
    Copy-Item -LiteralPath (Join-Path $taskManifestDirectory 'runtime-manifest.json') -Destination (Join-Path $taskDestinationRoot 'runtime-manifest.json') -Force
    Copy-Item -LiteralPath (Join-Path $taskManifestDirectory 'cli-dependency-inventory.json') -Destination (Join-Path $taskDestinationRoot 'cli-dependency-inventory.json') -Force
    foreach ($taskDocument in @('README.md','LICENSE-AUDIT.md')) { Copy-Item -LiteralPath (Join-Path $taskManifestDirectory $taskDocument) -Destination (Join-Path $taskDestinationRoot $taskDocument) -Force }
}
