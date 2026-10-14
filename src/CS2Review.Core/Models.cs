using System.Text.Json;

namespace CS2Review.Core;

public sealed class MatchData
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string MapName { get; set; } = "";
    public string ParserVersion { get; set; } = "";
    public string Platform { get; set; } = "未知";
    public int? FinalTScore { get; set; }
    public int? FinalCTScore { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Warnings { get; set; } = [];
    public List<PlayerData> Players { get; set; } = [];
    public List<RoundData> Rounds { get; set; } = [];
    public List<PositionSample> Positions { get; set; } = [];
}

public sealed class PlayerData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class RoundData
{
    public int Number { get; set; }
    public bool IsValid { get; set; }
    public string Winner { get; set; } = "";
    public double StartedAt { get; set; }
    public double EndedAt { get; set; }
    public double? StatisticsEndedAt { get; set; }
    public List<Participation> Participants { get; set; } = [];
    public List<string> Survivors { get; set; } = [];
    public List<GameEvent> Events { get; set; } = [];
    public double? FreezeEndedAt { get; set; }
    public double? BombTimerSeconds { get; set; }
    public List<PlayerState> EconomySnapshots { get; set; } = [];
}

public sealed class Participation
{
    public string PlayerId { get; set; } = "";
    public string Side { get; set; } = "";
}

public sealed class GameEvent
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Tick { get; set; }
    public double Time { get; set; }
    public string? ActorId { get; set; }
    public string? TargetId { get; set; }
    public string? AssisterId { get; set; }
    public string? ActorSide { get; set; }
    public string? TargetSide { get; set; }
    public int Damage { get; set; }
    public int? TargetHealthAfter { get; set; }
    public bool? AfterRoundEnd { get; set; }
    public string Weapon { get; set; } = "";
    public bool Headshot { get; set; }
    public bool FlashAssist { get; set; }
    public double Duration { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public PlayerState? ActorState { get; set; }
    public PlayerState? TargetState { get; set; }
    public string? BombSite { get; set; }
    public bool? HasKit { get; set; }
    public int? EntityId { get; set; }
    public List<FirePoint>? FirePoints { get; set; }
}

public sealed class PlayerState
{
    public string PlayerId { get; set; } = "";
    public string Side { get; set; } = "";
    public int Tick { get; set; }
    public double Time { get; set; }
    public int? Money { get; set; }
    public int? EquipmentValue { get; set; }
    public int? Health { get; set; }
    public int? Armor { get; set; }
    public bool? Helmet { get; set; }
    public bool? DefuseKit { get; set; }
    public string? ActiveWeapon { get; set; }
}

public sealed class FirePoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool IsBurning { get; set; }
}

public sealed class PositionSample
{
    public int Round { get; set; }
    public int Tick { get; set; }
    public double Time { get; set; }
    public string PlayerId { get; set; } = "";
    public string Side { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Yaw { get; set; }
    public int Health { get; set; }
}

public static class DataJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
