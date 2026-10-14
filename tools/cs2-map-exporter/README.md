# CS2 map resource adapter

`CS2Review.Core.MapResourceService` reads an existing CS2 install through the pinned official Source2Viewer 20.0 Windows x64 CLI. It exports radar PNG, overview KV, the original physics resource and PHYS block, and a lossless welded AWMH1 static collision mesh. Source game files remain read-only. No map assets are distributed with this project.

The Desktop app calls:

~~~csharp
var maps = new MapResourceService(Path.Combine(AppContext.BaseDirectory, "map-tools", "Source2Viewer-CLI.exe"));
var installCandidates = MapInstallLocator.FindInstalls();
var resource = maps.Find("de_mirage", cacheDirectory);
resource ??= await maps.ImportFromGameAsync("de_mirage", selectedInstallRoot, cacheDirectory, cancellationToken);
var pixel = resource.Overview.Project(worldX, worldY);
~~~

`cacheDirectory` is outside the game installation. Supported map names are `de_mirage` and `de_dust2`; they are identifiers, never paths. Cache paths, checksums, PNG dimensions, AWMH counts/indices and linked directories are checked. Import publishes a manifest after successful extraction and validation. A cancelled or failed worker leaves no staged import or new manifest.

Coordinates remain Hammer units, Z-up. `SpatialMesh.ReadAwmh` builds a BVH; `TraceStaticSegment(start,end)` tests only the imported static collision mesh. The first policy preserves every recorded collision material, without decimation. It includes nonvisual clips and glass, and lacks smoke, moving doors and player eye state. `SpatialMath.IsWithinFov` is a separate mathematical cone test. Neither operation proves actual player visibility.

`MapResource.MatchStatus` is always `unknown` until a future independently verified demo version mapping exists. `MapResourceProvenance.UserAsserted(resource, sourceDemoSha256)` produces a serializable sidecar bound to exact demo SHA, geometry SHA and asset directory, only after explicit user assertion. It stays `user-asserted`; `AllowsVerifiedVisibility` is always false. Map name/build coincidence never upgrades this evidence.

The CLI is self-contained: users need neither the .NET SDK nor Python/Go. `runtime-manifest.json` pins its archive, executable, required native DLLs and notices. Publishers copy only its `runtimeFiles` into `map-tools` and verify each hash, preferring the same-path public sidecars from this directory. Never copy the download archive, game assets, `artifacts/maps`, private demos or user cache. The exact Windows release audit is complete: .NET 10.0.11, 16 runtime packages, both native DLLs and embedded/generated-code notices are recorded in `notices/COMPONENTS.json` and [LICENSE-AUDIT.md](LICENSE-AUDIT.md). Preserve all notices, including upstream aggregate third-party texts and source-availability notices.

Unit checks use the independent `tests/CS2Review.Map.Tests` console project. Set `CS2_MAP_TEST_CACHE` to an already imported private cache for both-map geometry/projection/integrity regressions. Its optional `--import`, `--cancel`, `--failure` arguments accept explicit install/tool/output paths; personal paths are never hardcoded in source.
