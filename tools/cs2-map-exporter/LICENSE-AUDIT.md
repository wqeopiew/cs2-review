# Source2Viewer 20.0 Windows CLI distribution audit

This inventory applies to the unmodified [official Windows x64 archive](https://github.com/ValveResourceFormat/ValveResourceFormat/releases/download/20.0/cli-windows-x64.zip), SHA256 `d32ab327b8bbb42a2528866afb03bb582bdb779d0005488da32b90292afd3ff5`. It does not cover other tool releases or user map resources. Only the 37 hash-pinned `runtimeFiles` in `runtime-manifest.json` are published. Notice texts live in this directory's `notices/` and can be copied without the downloaded archive or Valve content.

The executable contains a .NET bundle format 6.0 with 192 members. Its actual runtime config identifies **Microsoft.NETCore.App 10.0.11**; the actual embedded `.deps.json` identifies the following **16 runtime packages**. Each exact NuGet signed package was downloaded from its official flat-container URL; its package-content SHA512 matches the release deps value, and NuGet's signed package content-integrity validation passed. Signed ZIP archive hashes differ from package-content hashes and are recorded separately. Online signing-certificate chain/revocation validation was not performed. `cli-dependency-inventory.json` preserves the actual release graph and `notices/COMPONENTS.json` records exact versions, commits, dependencies, integrity evidence and individual notices.

| Published package | License / notice treatment |
|---|---|
| Blake3 3.0.2 | BSD-2-Clause, exact repository notice; additional upstream CC0 retained |
| K4os.Compression.LZ4 1.3.8 | MIT, official 1.3.8 tag notice (package has legacy license URL) |
| KeyValues2 0.8.0 | MIT, exact package COPYING (not GPL) |
| SharpGLTF.Core 1.0.6 | MIT, exact package repository commit |
| SharpGLTF.Runtime 1.0.6 | MIT, exact package repository commit |
| SharpGLTF.Toolkit 1.0.6 | MIT, exact package repository commit |
| SkiaSharp 4.151.1 | Exact package MIT notice |
| SkiaSharp.NativeAssets.Win32 4.151.1 | Exact package MIT and full third-party notices; original source chain and MPL 1.1 text retained |
| System.IO.Hashing 10.0.10 | MIT and exact package third-party notices |
| TinyBCSharp 0.1.2 | MIT, exact repository commit |
| TinyEXR.NET 1.1.0 | MIT, exact repository commit; TinyEXR/OpenEXR BSD3 and OpenJPH BSD2 attribution also retained for ported code |
| ValveKeyValue 0.70.0.499 | MIT, exact repository commit |
| ValvePak 5.0.2.177 | MIT, exact repository commit |
| Vortice.SPIRV 1.0.5 | MIT, exact repository commit |
| Vortice.SpirvCross 1.5.4 | MIT wrapper; Apache-2.0/MIT SPIRV-Cross and MIT SPIRV header notices retained |
| ZstdSharp.Port 0.8.8 | MIT port and underlying Zstandard 1.5.7 BSD-3-Clause notice |

The direct package declarations are in the exact [CLI project](https://github.com/ValveResourceFormat/ValveResourceFormat/blob/20.0/CLI/CLI.csproj) and [VRF project](https://github.com/ValveResourceFormat/ValveResourceFormat/blob/20.0/ValveResourceFormat/ValveResourceFormat.csproj); versions are not inherited from a root Directory.Packages.props. SharpGLTF, ValvePak, Skia native and Vortice transitive dependencies are resolved from the release deps graph, rather than from whatever versions NuGet resolves today.

The native `libSkiaSharp.dll` and `spirv-cross.dll` were compared against their exact signed NuGet package members: their bytes and SHA256 match. The official executable needs those two external DLLs and normal Windows system libraries; no SDK, separate managed dependency files, GUI assets or installed .NET runtime are needed. Skia's exact package commit `279f93f4ffa7f9fe4e9c0bc298bedc3c9e439764` pins its Skia submodule `bdd0c3a8eaba1afa7148f02bba3a07f94e682847`. Its Windows build targets SkiaSharp → skia/pathops/skottie. The complete native publisher notice is a broad source-tree aggregate: it contains historical GIF/MPL and developer libmicrohttpd/LGPL notices, while this source version uses Wuffs for GIF and has no libmicrohttpd target or DEPS entry. Those original texts remain intact; `Skia-SOURCE-NOTICE.txt` states source availability and the MPL alternative for any covered material. The exact package's third-party file is preserved rather than replaced by a generic Skia license. SPIRV-Cross's native compiler source revision is not published in the Vortice package metadata: identity is established by exact package DLL/hash, without inventing an SDK revision. Upstream Apache/MIT notices and copyright attributions are preserved.

Additional notices cover the bundled .NET 10.0.11 runtime and its third-party components, the embedded Roboto font (font name-table copyright **2011 Google Inc.**, Apache-2.0), and ConsoleAppFramework 5.7.13 (MIT) generated code. ConsoleAppFramework is a compile-time source generator; its runtime assembly is absent from the executable/deps, but its notice is included conservatively. TinyEXR and Blake3 are managed implementations; their separate native/test libraries are absent. Supplementary codec notices preserve upstream attribution without claiming that an unrelated upstream build was used to compile this executable.

The [GUI project](https://github.com/ValveResourceFormat/ValveResourceFormat/blob/20.0/GUI/GUI.csproj), Renderer, FCTB, NAudio, NLayer, OpenTK, Svg.Skia and CsWin32 are absent from this CLI release. Linux Skia native assets, TinyEXR's netstandard-only SharpZipLib, and Blake3.Native are also absent. GUI dependency licenses therefore do not apply to this whitelist. Complete upstream aggregate notices may mention additional components; their presence in a notice is not proof that their code is linked.

Publishers must retain the supplied original license and notice files, source availability statements and checksums, distribute upstream binaries unchanged, and avoid attributing any project warranty to upstream authors. `auditComplete` means the exact release dependency inventory, package/native integrity and redistributed notices were checked; it is not a signing-chain or vulnerability-audit result. The bundle and package audit work files are ignored local artifacts and are excluded from release output. No Valve map assets or personal demo data are part of this tool distribution.
