using System.Globalization;

namespace CS2Review.Core;

public sealed record LocalReportOptions(int MaxPriorities = 3);

/// <summary>Deterministic local review. No endpoint, credential, or network dependency.</summary>
public sealed class LocalReportService
{
    public AiReport Generate(MatchData match, PlayerAnalysis analysis, LocalReportOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(match); ArgumentNullException.ThrowIfNull(analysis);
        AnalysisBinding.Validate(match,analysis,ct);
        options ??= new();
        if (options.MaxPriorities is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(options));
        if (!match.Players.Any(p => p.Id == analysis.PlayerId) || analysis.RulesVersion != "rules-v3") throw new ArgumentException("请对已保存比赛的目标玩家运行当前规则分析后生成本地报告。");
        var events = new Dictionary<string, (int Round, GameEvent Event)>();
        foreach (var round in match.Rounds)
        {
            ct.ThrowIfCancellationRequested();
            if (!round.IsValid || round.Winner is not ("T" or "CT") || !round.Participants.Any(p => p.PlayerId == analysis.PlayerId && p.Side is "T" or "CT")) continue;
            foreach (var e in round.Events)
            {
                ct.ThrowIfCancellationRequested();
                if (e.AfterRoundEnd == true || string.IsNullOrWhiteSpace(e.Id) || !double.IsFinite(e.Time) || e.Time < round.StartedAt || e.Time > round.EndedAt) continue;
                if (!events.TryAdd(e.Id, (round.Number, e))) throw new InvalidDataException("事件ID不唯一，无法建立本地报告引用。");
            }
        }
        var groups = new Dictionary<string, Finding>();
        foreach (var f in analysis.Findings)
        {
            ct.ThrowIfCancellationRequested();
            if (f.EventIds.Count == 0 || !double.IsFinite(f.Time)) throw new InvalidDataException("规则发现没有有效事件证据。");
            foreach (var id in f.EventIds)
            {
                ct.ThrowIfCancellationRequested();
                if (!events.TryGetValue(id, out var e) || e.Round != f.Round) throw new InvalidDataException("规则引用不属于当前比赛快照。");
            }
            if (!f.EventIds.Any(id => events[id].Event.Time == f.Time && events[id].Event.Tick == f.Tick)) throw new InvalidDataException("规则引用时间与当前快照不匹配。");
            groups.TryAdd(f.Kind, f);
        }
        var s = analysis.Summary;
        var report = new AiReport
        {
            PlayerId=analysis.PlayerId,MatchId=match.Id,SourceSha256=match.SourceSha256,AnalysisSnapshotSha256=analysis.AnalysisSnapshotSha256,ScoringVersion=s.RatingModelId??"unavailable",
            Source = "local", Model = "local-rules-v1", AnalysisVersion = analysis.AnalysisVersion, RulesVersion = analysis.RulesVersion, ContextVersion = "context-v2",
            Summary = string.Create(CultureInfo.InvariantCulture, $"本场有效参与 {s.Rounds} 回合，击杀/死亡/助攻 {s.Kills}/{s.Deaths}/{s.Assists}，KPR {s.Kpr:0.00}、DPR {s.Dpr:0.00}、ADR {s.Adr:0.0}、KAST {s.Kast:0.0}%。这是本场记录，不据此认定稳定习惯。评分状态：{s.RatingState}；Rating {(s.Rating?.ToString("0.00",CultureInfo.InvariantCulture)??"不可用")}，Round Swing {(s.RoundSwing?.ToString("+0.00;-0.00;0.00",CultureInfo.InvariantCulture)??"不可用")}。{s.RatingUnavailableReason}。精确地图版本、玩家视角与语音未验证，只提供证据支持的复核候选。")
        };
        ReportItem Item(Finding f, string inference, string alternative, string training, string followUp, string? metric = null, double value = 0)
        {
            var refs = new List<EvidenceReference>();
            foreach (var id in f.EventIds.Distinct().Take(20)) { ct.ThrowIfCancellationRequested(); var e = events[id]; refs.Add(new() { EventId = id, Round = e.Round, Time = e.Event.Time }); }
            return new() { Observation = f.Description, Inference = inference, AlternativeAction = alternative, Training = training, FollowUpMetric = followUp, Evidence = refs, Metrics = metric is null ? [] : new() { [metric] = value } };
        }
        foreach (var kind in new[] { "first_kill", "trade_kill", "utility_damage", "flash" })
        {
            ct.ThrowIfCancellationRequested();
            if (!groups.TryGetValue(kind, out var f)) continue;
            var item = kind switch
            {
                "first_kill" => Item(f, "此回合取得首杀；不能仅凭结果认定对枪优势稳定或站位安全。", "保留有信息与队友协同支持的交战条件，再检查后续目标处理。", "复看引用回合，记录出手前已知信息和首杀后行动。", "后续比赛首杀数/有效参与回合，并人工复核交战条件。", "firstKills", s.FirstKills),
                "trade_kill" => Item(f, "记录支持补枪时序；距离、真实视线和语音未验证。", "有可靠信息与支援条件时保持可补枪节奏。", "复看引用的死亡与击杀顺序，练习确认队友信息后协同行动。", "后续补枪数和被补枪死亡数，分别按有效回合/本人死亡复查。", "tradeKills", s.TradeKills),
                "utility_damage" => Item(f, "道具造成伤害，不等于封锁视线或创造安全路线。", "根据实际信息选择投掷时机，并确认队友位置。", "对照记录复查投掷目标、友军位置与后续协同；未知视线不下结论。", "后续对敌道具伤害及队伤，按有效参与回合观察。", "utilityDamage", s.UtilityDamage),
                _ => Item(f, "有致盲事件记录；是否产生安全击杀机会需要视角和协同证据。", "在队友可配合且信息足够时安排闪光后的行动。", "复看闪光与队友行动顺序，明确投掷前的协同约定。", "后续闪光助攻与致盲事件，结合原回合人工复核。", "flashAssists", s.FlashAssists)
            };
            report.Strengths.Add(item);
        }
        var plans = new (string Kind, string Inference, string Alternative, string Training, string FollowUp, string? Metric, double Value)[]
        {
            ("team_kill", "队杀是记录事实，具体信息与操作原因仍需复查。", "交战前确认目标身份与友军位置。", "逐次复查友军受影响的时机，明确身份确认和协同流程。", "后续队杀次数及其具体回合。", "teamKills", s.TeamKills),
            ("team_damage", "友军受到伤害；不据此判断玩家意图。", "投掷或开火前根据当时信息确认友军位置。", "复看引用事件，记录可改进的身份/投掷确认步骤。", "后续队伤/有效参与回合，同时人工复核触发条件。", "teamDamage", s.TeamDamage),
            ("suicide", "记录到自杀；具体原因需原回合证据。", "有信息时先确认环境和操作风险。", "复查引用回合，记录已知信息及可避免的操作条件。", "后续自杀次数与原因复核记录。", "suicides", s.Suicides),
            ("advantage_loss", "可靠人数支持优势中死亡候选；不能认定当时交战不必要。", "信息和目标允许时先评估保留人数与队友协同的条件。", "复看该时间点，分别记录人数、目标状态与玩家当时可知信息。", "后续可靠优势死亡候选，未知人数不进入比较。", null, 0),
            ("first_death", "这是首死回合复核候选；单场结果不等于急躁或长期习惯。", "按当时信息评估交战与队友支援条件。", "逐一复查首死回合，记录出手前信息与队友行动；不由坐标臆测视线。", "后续首死数/有效参与回合及被补枪死亡数/本人死亡。", "firstDeaths", s.FirstDeaths),
            ("clutch", "可靠状态记录单人残局；回合结果不证明玩家完成残局。", "仅在目标时钟和信息可靠时评估争取目标或保留装备的条件。", "按事件顺序复核人数、炸弹状态、实际时钟与可知信息。", "后续可靠残局记录，分别复查目标结果与本人存活。", null, 0)
        };
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            if (report.Priorities.Count == options.MaxPriorities) break;
            if (groups.TryGetValue(plan.Kind, out var f)) report.Priorities.Add(Item(f, plan.Inference, plan.Alternative, plan.Training, plan.FollowUp, plan.Metric, plan.Value));
        }
        ct.ThrowIfCancellationRequested();
        return report;
    }
}
