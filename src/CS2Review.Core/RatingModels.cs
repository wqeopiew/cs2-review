using System.Text.Json.Serialization;

namespace CS2Review.Core;

public sealed class RatingModel
{
    public int SchemaVersion { get; set; } = 1;
    public string ModelId { get; set; } = "";
    public string ModelVersion { get; set; } = "rating-approx-v1";
    public string TargetDate { get; set; } = "2026-10-01";
    public string CalibrationStatus { get; set; } = "experimental";
    public string TrainingManifestSha256 { get; set; } = "";
    public string FeatureVersion { get; set; } = "freeze-equipment-value-proxy-v1";
    public string ParameterDisclosure { get; set; } = "公开规则重建；装备总价分层、平滑概率、贡献份额和六轴变换均为自身模型参数，不是 HLTV 参数。";
    public int[] EquipmentBandUpperBounds { get; set; } = [1000, 2000, 3500, 5000, 6500];
    public double[] EquipmentStrengths { get; set; } = [1, 1.2, 1.6, 2, 2.6, 3];
    public double ProbabilityPriorWeight { get; set; } = 2;
    public double ProbabilityPriorCtWin { get; set; } = .5;
    public int MinimumMapObservations { get; set; } = 20;
    public List<RatingProbabilityCell> ProbabilityCells { get; set; } = [];
    public double KillerShare { get; set; } = .5;
    public double DamageShare { get; set; } = .3;
    public double FlashShare { get; set; } = .1;
    public double TradeShare { get; set; } = .1;
    public double TradeWindowSeconds { get; set; } = 5;
    public double[] MultiKillPoints { get; set; } = [0, 0, 1, 2, 4, 7];
    public double[] AxisCenters { get; set; } = new double[6];
    public double[] AxisScales { get; set; } = [1, 1, 1, 1, 1, 1];
    public double[] Weights { get; set; } = new double[6];
    public double Intercept { get; set; }
    public List<string> TrainingGroups { get; set; } = [];
    public RatingValidationReport? Validation { get; set; }
}

public sealed class RatingProbabilityCell
{
    public string Map { get; set; } = "global";
    public int CtAlive { get; set; }
    public int TAlive { get; set; }
    public int CtEquipmentBand { get; set; }
    public int TEquipmentBand { get; set; }
    public bool Planted { get; set; }
    public int ClockBand { get; set; }
    public int Observations { get; set; }
    public int CtWins { get; set; }
}

// Optional independently recorded pre-event state. It must refer to the exact demo/event.
// Without it, only explicitly named freeze-equipment proxy models can consume existing schema2.
public sealed class RatingMatchFacts
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceSha256 { get; set; } = "";
    public string Provenance { get; set; } = "";
    public List<RatingStateFrame> Frames { get; set; } = [];
}

public sealed class RatingStateFrame
{
    public int Round { get; set; }
    public string EventId { get; set; } = "";
    public int Tick { get; set; }
    public double Time { get; set; }
    public List<RatingPlayerState> Players { get; set; } = [];
}

public sealed class RatingPlayerState
{
    public string PlayerId { get; set; } = "";
    public string Side { get; set; } = "";
    public bool? Alive { get; set; }
    public int? EquipmentValue { get; set; }
    public int? StrongestWeaponPrice { get; set; }
    public int? ArmorPrice { get; set; }
    public bool? DefuseKit { get; set; }
}

public sealed class RatingResult
{
    public string PlayerId { get; set; } = "";
    public string Side { get; set; } = "all";
    public string? ModelId { get; set; }
    public string TargetDate { get; set; } = "2026-10-01";
    public string CalibrationStatus { get; set; } = "unavailable";
    public double? Overall { get; set; }
    public double? RoundSwing { get; set; }
    public int PlayedRounds { get; set; }
    public PlayerStats Raw { get; set; } = new();
    public double? ScoringKastPercent { get; set; }
    public Dictionary<string, double?> Axes { get; set; } = [];
    public List<string> UnavailableReasons { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<RatingEventCredit> Credits { get; set; } = [];
    [JsonIgnore] public double[]? FeatureValues { get; set; }
}

public sealed class RatingEventCredit
{
    public int Round { get; set; }
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Time { get; set; }
    public bool Eligible { get; set; }
    public bool NegativeKillerCorrection { get; set; }
    public string Reason { get; set; } = "";
    public List<string> EvidenceIds { get; set; } = [];
    // Probability units; result.RoundSwing multiplies the per-played-round mean by 100.
    public Dictionary<string, double> PlayerCredits { get; set; } = [];
}

public sealed class RatingReferenceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ReferenceId { get; set; } = "";
    public string TargetDate { get; set; } = "2026-10-01";
    public string Provenance { get; set; } = "";
    public List<RatingReferenceMatch> Matches { get; set; } = [];
}

public sealed class RatingReferenceMatch
{
    public string GroupId { get; set; } = "";
    public string CanonicalMatchPath { get; set; } = "";
    public string CanonicalSha256 { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string MapName { get; set; } = "";
    public string? FactsPath { get; set; }
    public string? FactsSha256 { get; set; }
    public RatingOfficialSnapshot Snapshot { get; set; } = new();
}

public sealed class RatingOfficialSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string TargetDate { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public int MapStatsId { get; set; }
    public List<RatingOfficialPlayer> Players { get; set; } = [];
}

public sealed class RatingOfficialPlayer
{
    public string PlayerId { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string Side { get; set; } = "all";
    public int PlayedRounds { get; set; }
    public double Rating { get; set; }
    public double? Swing { get; set; }
    public int? Kills { get; set; }
    public int? Deaths { get; set; }
    public double? Adr { get; set; }
    public double? KastPercent { get; set; }
}

public sealed class RatingTrainingSample
{
    public RatingReferenceMatch Reference { get; set; } = new();
    public MatchData Match { get; set; } = new();
    public RatingMatchFacts? Facts { get; set; }
    public string CanonicalFileSha256 { get; set; } = "";
    public string? FactsFileSha256 { get; set; }
}

public sealed class RatingTrainingOptions
{
    public int Iterations { get; set; } = 2000;
    public double LearningRate { get; set; } = .01;
    public double Ridge { get; set; } = .1;
    public int MinimumMapObservations { get; set; } = 20;
    public double ProbabilityPriorWeight { get; set; } = 2;
    public string FeatureVersion { get; set; } = "freeze-equipment-value-proxy-v1";
    public double KillerShare { get; set; } = .5;
    public double DamageShare { get; set; } = .3;
    public double FlashShare { get; set; } = .1;
    public double TradeShare { get; set; } = .1;
}

public sealed class RatingValidationReport
{
    public string Method { get; set; } = "leave-one-whole-match-out";
    public string Status { get; set; } = "exploratory";
    public List<RatingValidationFold> Folds { get; set; } = [];
    public RatingErrorMetrics Rating { get; set; } = new();
    public RatingErrorMetrics Swing { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public int ProbabilityCount { get; set; }
    public double? ProbabilityBrier { get; set; }
    public double? ProbabilityLogLoss { get; set; }
}

public sealed class RatingValidationFold
{
    public string HeldOutGroup { get; set; } = "";
    public List<string> TrainingGroups { get; set; } = [];
    public string ModelId { get; set; } = "";
    public RatingErrorMetrics Rating { get; set; } = new();
    public RatingErrorMetrics Swing { get; set; } = new();
    public int UnavailableRows { get; set; }
    public RatingModel? FrozenModel { get; set; }
    public int ProbabilityCount { get; set; }
    public double? ProbabilityBrier { get; set; }
    public double? ProbabilityLogLoss { get; set; }
}

public sealed class RatingErrorMetrics
{
    public int Count { get; set; }
    public double? Mae { get; set; }
    public double? Rmse { get; set; }
    public double? Bias { get; set; }
    public double? Spearman { get; set; }
    public double? P50 { get; set; }
    public double? P90 { get; set; }
    public double? P95 { get; set; }
    public double? Within05 { get; set; }
    public double? Within10 { get; set; }
    public double? Within20 { get; set; }
}
