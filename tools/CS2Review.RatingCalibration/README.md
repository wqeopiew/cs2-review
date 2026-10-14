# 离线评分模型与校准工具

此工具读取实际规范比赛 `MatchData`、同地图的官方评分快照及哈希清单，训练并冻结一个可导入软件的实验模型。它不从聚合 Rating JSON 虚构逐回合胜率，也不随附一个已证明 HLTV 精度的模型。没有模型或必要事实不足时，评分和 Round Swing 为 `null`，并给出具体原因；原始统计仍可查看。

桌面程序默认内置第三轮 RT 实验模型，RS 沿用原算法；完整 Demo 无需导入模型或配置 AI 即可显示数值。本工具用于建立独立参考模型；`score` 命令仍要求显式模型路径。可使用发布包 `rating-models/round3.json`，模型范围见 `docs/rating-round3.md`，原版概率假设见 `docs/rating-baseline.md`。

## 运行

发布包的工具入口是 `calibration/CS2Review.RatingCalibration.exe`，为 Windows x64 自包含程序，用户无需安装 SDK 或 .NET runtime。在源码仓库根目录加载 `scripts/environment.ps1` 后，开发者也可用 `$taskDotnet run --project tools/CS2Review.RatingCalibration -- <命令>`。

```text
validate manifest.json
train manifest.json model.json [options.json]
cross-validate manifest.json validation.json [options.json]
inspect model.json
score model.json canonical-match.json playerId score.json [all|T|CT] [facts.json]
```

`validate` 核对实际文件、两种 SHA 和快照对应关系。`train` 生成冻结模型；`cross-validate` 每次留出一个完整比赛组，并在该折内重新训练概率表、标准化和综合权重。每折的完整冻结模型也保存到报告，便于重现。`score` 读取实际比赛计算贡献详情。退出码：成功为 0，输入/文件错误为 2，评分不可用为 3，取消为 130。Ctrl+C 可取消读取、分析、训练和验证。

## 参考清单

JSON 字段使用以下大小写。未知字段和重复字段会拒绝。路径相对于 manifest 所在目录，也可为绝对路径。

```json
{
  "schemaVersion": 1,
  "referenceId": "verified-reference-set",
  "targetDate": "2026-10-01",
  "provenance": "采集者、公开来源、许可或使用依据、映射核对方式；synthetic 必须明确标识",
  "matches": [{
    "groupId": "whole-series-identity",
    "canonicalMatchPath": "matches/map.json",
    "canonicalSha256": "规范JSON文件实际字节的64位十六进制SHA256",
    "sourceSha256": "原demo实际字节的64位十六进制SHA256",
    "mapName": "de_mirage",
    "factsPath": null,
    "factsSha256": null,
    "snapshot": {
      "capturedAtUtc": "2026-10-12T00:00:00Z",
      "targetDate": "2026-10-01",
      "sourceUrl": "https://synthetic.invalid/mapstats/1",
      "mapStatsId": 1,
      "players": [{
        "playerId": "规范比赛中原样保留的玩家ID",
        "steamId": "10001",
        "side": "all",
        "playedRounds": 23,
        "rating": 1.62,
        "swing": 5.04,
        "kills": null,
        "deaths": null,
        "adr": null,
        "kastPercent": null
      }]
    }
  }]
}
```

这是字段格式示例，须替换为已经核对的真实比赛与哈希，不能作为训练数据。`playerId` 和 `steamId` 均为字符串；若规范 ID 使用别名，应在 provenance 中记录与 SteamID 的可审计映射。`side` 只能为 `all`、`T` 或 `CT`，`playedRounds` 必须等于该玩家该阵营实际可靠参与回合。提供的 K/D、ADR、KAST 会与事实核对；这里的评分 KAST 助攻阈值是 40 伤害。缺少官方 Swing 时用 `null`，验证报告的 Swing 样本数为 0、误差为 `null`。

捕获日期必须在目标更新日期以后，比赛举行日期不能代替捕获日期。官方历史分数会重算，旧社区 oracle 不能自动成为十月真值。`sourceSha256` 验证规范数据的 demo 身份；`canonicalSha256` 独立验证规范文件字节，两者不可混用。同一 demo 不可重复进入清单或跨组；同一 BO3/BO5 的所有地图使用同一个 `groupId`，以免留出时泄漏。

## 装备和事实契约

默认 `featureVersion=freeze-equipment-value-proxy-v1`：概率分层、经济击杀/伤害、生存和 KAST 权重统一使用冻结结束时的装备总价代理，不使用死亡回调当前装备价格推断交战前价格。这一代理不反映回合内捡枪或丢枪。起始 HP、装备或冻结时间缺失会使评分不可用。事件快照的玩家 ID、阵营、tick 和时间须与事件相符。

`strongest-weapon-armor-v1` 要求额外补充事实文件，包含完整、独立记录的交战前最贵持有武器与护甲价格，而非当前手持武器。格式为 `RatingMatchFacts`：

```json
{
  "schemaVersion": 1,
  "sourceSha256": "与规范比赛相同的demoSHA",
  "provenance": "独立采集的pre-event事实来源与方法",
  "frames": [{
    "round": 1,
    "eventId": "round-1-freeze",
    "tick": 100,
    "time": 15.625,
    "players": [{
      "playerId": "原样玩家ID",
      "side": "CT",
      "alive": true,
      "equipmentValue": null,
      "strongestWeaponPrice": 2900,
      "armorPrice": 1000,
      "defuseKit": true
    }]
  }]
}
```

每个 frame 必须含当回合所有参与者且身份唯一。`round-{number}-freeze` 是保留的冻结 frame ID，时间须等于 FreezeEndedAt、tick 等于该时刻规范 EconomySnapshots 的最大 tick，存活与冻结 HP 一致；它为概率和生存条件提供最贵武器加护甲价格。每个击杀或伤害所需 frame 的 `eventId` 则与规范事件 ID 相同，tick/time 完全绑定，表示**事件前**状态。没有可靠价格时保留 `null`，不要猜测。补充文件同时由独立 `factsSha256` 绑定，不能用于另一份 demo。

## 模型和验证口径

本工具训练产生的模型为 `schemaVersion=1`、`modelVersion=rating-approx-v1`、`targetDate=2026-10-01`。另支持原版内置 schema2 与第三轮 RT 内置 schema3 的产品文件；内置标签只接受其固定发布参数。冻结内容 SHA-256 为 `modelId`；`trainingManifestSha256` 记录规范化清单身份。导入会验证参数、哈希、未知/重复字段。参数调整后必须重新冻结，旧 SHA 不能继续使用。

当前状态仅为 `experimental` 或 `exploratory`，训练不会自动宣称已校准。模型保存自身装备分层、先验、贡献份额、多杀点、六轴均值/尺度/非负权重、训练组和参数披露。默认经济强度与贡献分配是本项目选择，非 HLTV 未公开参数。

胜率表从实际可靠回合状态和最终回合结果统计，每回合相同状态只计一次，避免多次 shot/damage 加权。地图/全局表在冻结时刻按所有可能阶段的覆盖条件选定，整回合固定，不读取未来是否下包来改变早期 Swing。没有该格观测时采用模型已冻结的先验，并显示稀疏局面提示。

击杀 Swing 按事件前后概率变化分配给击杀、实际敌方 HP 伤害、闪光和五秒补枪的参与者；队伤/环境伤害先消耗 HP，却不获得敌方伤害贡献。负击杀增益修正为零，victim 代价保留。实际炸弹规则时长、下包时间、可靠 kit 和正在拆包事实决定严格不足 10/5 秒的截断。abort 或致命伤害终止正在拆包例外；未知时钟/kit 降级为不可用。

回合末剩余贡献的比例未公开，本项目明确选择：使用最后一次符合条件击杀后的冻结概率，在胜方符合条件击杀的正贡献之间按比例分配，败方存活者等分负值（无人存活时由当回合败方参与者分担）。下包回合候选只含下包后击杀。真实爆炸/拆包终局停止后续评分事件，但不抹去先前已产生的合法剩余贡献。此选择以及队员生存/闪光/补枪权重都不能视为官方等价。

验证报告给出 Rating 和 Swing 的 Count、MAE、RMSE、bias、Spearman、P50/P90/P95 和误差带，并给出每回合去重局面的 Brier/log-loss。Swing 单位是百分点/实际参与回合。只有三个完整比赛组的 CV 是探索性流程验证，不能证明泛化；需补充独立地图/赛事/极端局面、真实同源 demo 和最新官方标签。synthetic 测试只证明文件契约、训练执行、拆分与重现。

## API

`RatingEngine.Score(match, playerId, model = null, enrichedFacts = null, side = "all", ct = default)` 返回可空评分、原始统计、缺失原因和逐事件贡献。`RatingModelCodec.Load(json)` 读取 JSON 内容；`LoadFileAsync(path, ct)`、`SaveFileAsync(path, model, ct)` 读写文件。`RatingReferenceLoader.LoadAsync(manifestPath, ct)` 核对实际文件哈希。`RatingTrainer.Train(manifest, samples, options = null, ct = default)` 和 `ValidateLeaveOneMatchOut(...)` 可直接使用 Loader 返回的样本。

训练研究的原始录像、官方标签与内部验收记录不随源码或用户包分发。公开规则来源是 [HLTV 基础介绍](https://www.hltv.org/news/42485/introducing-rating-30)、[2025 年调整](https://www.hltv.org/news/43047/rating-30-adjustments-go-live)和 [2026-10-01 更新](https://www.hltv.org/news/45596/rating-30-receives-small-accuracy-improvements)。本工具自行实现，研究 oracle 和未确认再分发许可的职业 demo 不打进发布包。
