namespace CS2Review.Core;

public sealed class ContextFindingAnalyzer
{
    public IReadOnlyList<Finding> Analyze(RoundData round, string playerId, RoundContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(round); ArgumentNullException.ThrowIfNull(context);
        var side = round.Participants.FirstOrDefault(p => p.PlayerId == playerId && p.Side is "T" or "CT")?.Side;
        var result = new List<Finding>();
        if (side is null || !round.IsValid || round.Winner is not ("T" or "CT") || context.Number != round.Number) return result;
        var events = new Dictionary<string, GameEvent>(); var ambiguous = new HashSet<string>();
        foreach (var e in round.Events)
        {
            ct.ThrowIfCancellationRequested();
            if (e.AfterRoundEnd == true || string.IsNullOrWhiteSpace(e.Id) || !double.IsFinite(e.Time) || e.Time < round.StartedAt || e.Time > round.EndedAt) continue;
            if (!events.TryAdd(e.Id, e)) ambiguous.Add(e.Id);
        }
        var advantageRecorded = false; var clutchRecorded = false;
        void Add(string kind, string title, string description, GameEvent e) => result.Add(new Finding
        {
            Id = $"{playerId}-r{round.Number}-{kind}-{e.Id}", Kind = kind, Title = title, Description = description,
            Round = round.Number, Time = e.Time, Tick = e.Tick, EventIds = [e.Id],
            ConfidenceReason = "可靠冻结健康与在先事件支持人数候选；没有地图视线、玩家视角和语音，不据此认定战术错误。"
        });
        foreach (var p in context.Timeline)
        {
            ct.ThrowIfCancellationRequested();
            if (ambiguous.Contains(p.EventId) || !events.TryGetValue(p.EventId, out var e) || p.Tick != e.Tick || p.Time != e.Time) continue;
            if (!advantageRecorded && e.Kind is "damage" or "kill" && e.TargetId == playerId && p.PlayerAliveBefore == true && p.PlayerAliveAfter == false
                && p.OwnAliveBefore is int own && p.EnemyAliveBefore is int enemy && own > enemy && enemy > 0)
            {
                advantageRecorded = true;
                Add("advantage_loss", "人数优势中发生死亡", $"可靠事件前本方 {own} 人、对方 {enemy} 人，随后目标玩家死亡。仅为复核候选，不能认定交战不必要。", e);
            }
            if (!clutchRecorded && p.PlayerAliveAfter == true && p.OwnAlive == 1 && p.EnemyAlive is > 0)
            {
                clutchRecorded = true;
                Add("clutch", p.OwnAliveBefore > 1 ? "进入单人残局" : "记录到单人残局", $"可靠事件后本方仅目标玩家存活，对方 {p.EnemyAlive} 人；回合最终{(round.Winner == side ? "获胜" : "失利")}。回合结果不等于目标玩家完成残局；经济和炸弹时钟仅按实际记录复核。", e);
            }
        }
        return result;
    }
}
