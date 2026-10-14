# 补充许可来源

发布脚本从实际锁定的 Go/NuGet 包复制归属元数据和完整许可。以下补件用于包内没有附带独立许可文本的组件；源码与发布包均保留这些原文。

- `Microsoft.Data.Sqlite-10.0.0-LICENSE.txt`：适用于 Microsoft.Data.Sqlite 与 Microsoft.Data.Sqlite.Core 10.0.0。来自两包元数据指向的 dotnet/dotnet 提交 `b0f34d51fccc69fd334253924abd8d6853fad7aa` 中的 [EF Core MIT 原文](https://github.com/dotnet/dotnet/blob/b0f34d51fccc69fd334253924abd8d6853fad7aa/src/efcore/LICENSE.txt)。SHA-256：`ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b`。发布包同时保留两包 nuspec 中的 Microsoft 版权归属。
- `SQLitePCLRaw-3.0.5-NOTICE.txt`：保留 SQLitePCLRaw 的 SourceGear 归属及 SQLite、MS Open Tech 通知；完整 Apache 2.0 原文见 `Apache-2.0.txt`。NOTICE SHA-256：`5a1ab2f670f86dd2f8d42e40a8459cc278ca6d5225a7e2a5fa3ea0184396a6d9`。来源为包元数据中的 [ed046114 提交](https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/NOTICE.TXT) 与 [96043b8c 提交](https://github.com/ericsink/SQLitePCL.raw/blob/96043b8cff323f21919df86a431c136655d81b4a/NOTICE.TXT)；两份 LICENSE 与 NOTICE 已逐字节核对相同。config 包的源码提交元数据与其版本不一致，这里的核对仅覆盖许可文本，不证明整个源码版本。

自包含 .NETCore 与 WindowsDesktop 运行库的精确版本取自发布程序的 `includedFrameworks`，其 MIT 许可及可用的第三方通知由构建脚本从对应 runtime NuGet 包复制。另附 SDK 许可文件供归属参考。

地图工具的精确依赖与完整通知见 `tools/cs2-map-exporter` 及发布包的 `map-tools/notices`。
