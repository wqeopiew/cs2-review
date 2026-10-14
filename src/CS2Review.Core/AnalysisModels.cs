namespace CS2Review.Core;

public sealed class PlayerAnalysis
{
    public string MatchId { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string AnalysisSnapshotSha256 { get; set; } = "";
    public string AnalysisFactsSha256 { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string AnalysisVersion { get; set; } = "raw-v1";
    public string RulesVersion { get; set; } = "rules-v1";
    public PlayerStats Summary { get; set; } = new();
    public PlayerStats T { get; set; } = new();
    public PlayerStats CT { get; set; } = new();
    public List<RoundAnalysis> Rounds { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
}

public sealed class PlayerStats
{
    public int Rounds { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Damage { get; set; }
    public double Kpr { get; set; }
    public double Dpr { get; set; }
    public double Adr { get; set; }
    public double Kast { get; set; }
    public double ScoringKast { get; set; }
    public double SurvivalPercent { get; set; }
    public int MultiKill2 { get; set; }
    public int MultiKill3 { get; set; }
    public int MultiKill4 { get; set; }
    public int MultiKill5 { get; set; }
    public double MultiKillPercent { get; set; }
    public int FirstKills { get; set; }
    public int FirstDeaths { get; set; }
    public int TradeKills { get; set; }
    public int TradedDeaths { get; set; }
    public int TeamDamage { get; set; }
    public int TeamKills { get; set; }
    public int Suicides { get; set; }
    public int UtilityDamage { get; set; }
    public int FlashAssists { get; set; }
    public double? Rating { get; set; }
    public double? RoundSwing { get; set; }
    public string? RatingModelId { get; set; }
    public Dictionary<string,double?> RatingAxes { get; set; } = [];
    public string RatingState { get; set; } = "实验 / 未校准";
    public string TargetRatingVersion { get; set; } = "2026-10-01";
    public string RatingUnavailableReason { get; set; } = "缺少已校准的胜率模型及独立参考样本；装备仅在记录字段可用时辅助分析，Rating 与 Round Swing 暂不可用。";
}

public sealed class RoundAnalysis
{
    public int Number { get; set; }
    public string Side { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Damage { get; set; }
    public bool Kast { get; set; }
    public bool ScoringKast { get; set; }
    public bool Survived { get; set; }
    public bool Traded { get; set; }
    public List<Finding> Findings { get; set; } = [];
}

public sealed class Finding
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Round { get; set; }
    public double Time { get; set; }
    public int Tick { get; set; }
    public List<string> EventIds { get; set; } = [];
    public string Confidence { get; set; } = "中";
    public string ConfidenceReason { get; set; } = "事件序列支持此候选；装备与生命值仅在有记录时辅助，缺少地图几何、语音及玩家视角上下文。";
}

public sealed class HistoryMatch
{
    public int SchemaVersion { get; set; }
    public string ParserVersion { get; set; } = "";
    public int PlayerCount { get; set; }
    public int RoundCount { get; set; }
    public string Id { get; set; } = "";
    public string MapName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
}

public sealed class ReportOptions
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxInputCharacters { get; set; } = 24000;
    public int MaxOutputTokens { get; set; } = 2200;
    public bool? EnableThinking { get; set; }
}

public sealed class AiReport
{
    public string PlayerId { get; set; } = "";
    public string MatchId { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string AnalysisSnapshotSha256 { get; set; } = "";
    public string ScoringVersion { get; set; } = "unavailable";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AnalysisVersion { get; set; } = "";
    public string Source { get; set; } = "cloud";
    public string RulesVersion { get; set; } = "rules-v1";
    public string ContextVersion { get; set; } = "context-v1";
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Summary { get; set; } = "";
    public List<ReportItem> Strengths { get; set; } = [];
    public List<ReportItem> Priorities { get; set; } = [];
}

public sealed class ReportItem
{
    public string Observation { get; set; } = "";
    public string Inference { get; set; } = "";
    public string AlternativeAction { get; set; } = "";
    public string Training { get; set; } = "";
    public string FollowUpMetric { get; set; } = "";
    public List<EvidenceReference> Evidence { get; set; } = [];
    public Dictionary<string, double> Metrics { get; set; } = [];
}

public sealed class EvidenceReference
{
    public string EventId { get; set; } = "";
    public int Round { get; set; }
    public double Time { get; set; }
}

public sealed class RoundContext
{
    public string ContextVersion { get; set; } = "context-v2";
    public int Number { get; set; }
    public PlayerState? PlayerStartState { get; set; }
    public int? OwnEquipmentValue { get; set; }
    public int? EnemyEquipmentValue { get; set; }
    public string EquipmentCoverage { get; set; } = "未知：没有可靠冻结结束快照。";
    public List<RoundTimelinePoint> Timeline { get; set; } = [];
}

public sealed class RoundTimelinePoint
{
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Tick { get; set; }
    public double Time { get; set; }
    public double RoundSeconds { get; set; }
    public int? OwnAliveBefore { get; set; }
    public int? EnemyAliveBefore { get; set; }
    public bool? PlayerAliveBefore { get; set; }
    public bool? PlayerAliveAfter { get; set; }
    public int? OwnAlive { get; set; }
    public int? EnemyAlive { get; set; }
    public string BombState { get; set; } = "unknown";
    public double? BombRemainingSeconds { get; set; }
    public double? DefuseRemainingSeconds { get; set; }
    public string? ActiveDefuserId { get; set; }
    public bool? DefuseCanFinish { get; set; }
}

public sealed class HistoryComparison
{
    public string PlayerId { get; set; } = "";
    public List<HistoryPerformance> Matches { get; set; } = [];
    public PlayerStats Aggregate { get; set; } = new();
    public PlayerStats Recent { get; set; } = new();
    public PlayerStats Previous { get; set; } = new();
    public bool HasComparison { get; set; }
    public string Note { get; set; } = "";
}

public sealed class HistoryPerformance
{
    public string MatchId { get; set; } = "";
    public string MapName { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
    public string AnalysisVersion { get; set; } = "";
    public PlayerStats Stats { get; set; } = new();
}
