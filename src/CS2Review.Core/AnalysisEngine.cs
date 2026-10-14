namespace CS2Review.Core;

public sealed class AnalysisEngine
{
    public PlayerAnalysis Analyze(MatchData match, string playerId) => Analyze(match, playerId, CancellationToken.None);

    public PlayerAnalysis Analyze(MatchData match, string playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(match);
        if (!match.Players.Any(p => p.Id == playerId)) throw new ArgumentException("比赛中不存在此玩家。", nameof(playerId));
        var result = new PlayerAnalysis { PlayerId = playerId, PlayerName = match.Players.First(p => p.Id == playerId).Name, AnalysisVersion = "raw-v2", RulesVersion = "rules-v3" };
        var contributions = new List<(string Side, PlayerStats Stats, bool Kast, bool Scoring, bool Survived)>();
        foreach (var round in match.Rounds.Where(r => r.IsValid && r.Winner is "T" or "CT" && r.EndedAt >= r.StartedAt))
        {
            ct.ThrowIfCancellationRequested();
            var participation = round.Participants.FirstOrDefault(p => p.PlayerId == playerId && p.Side is "T" or "CT");
            if (participation is null) continue;
            var side = round.Participants.GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.First().Side);
            bool Enemy(GameEvent e) => e.ActorId != null && e.TargetId != null && e.ActorId != e.TargetId && side.TryGetValue(e.ActorId, out var a) && side.TryGetValue(e.TargetId, out var t) && a is "T" or "CT" && t is "T" or "CT" && a != t;
            bool Team(GameEvent e) => e.ActorId != e.TargetId && e.ActorId != null && e.TargetId != null && side.TryGetValue(e.ActorId, out var a) && side.TryGetValue(e.TargetId, out var t) && a == t;
            var statisticsEnd = round.StatisticsEndedAt is double observedEnd && double.IsFinite(observedEnd) && observedEnd >= round.EndedAt ? observedEnd : round.EndedAt;
            var events = round.Events.Select((e, index) => { ct.ThrowIfCancellationRequested(); return (Event: e, SourceIndex: index); })
                .Where(x => double.IsFinite(x.Event.Time) && x.Event.Time >= round.StartedAt && x.Event.Time <= statisticsEnd)
                .OrderBy(x => x.Event.Time).ThenBy(x => x.Event.Tick).ThenBy(x => x.SourceIndex).Select(x => x.Event).ToList();
            var kills = events.Where(e => e.Kind == "kill" && Enemy(e)).ToList();
            var competitiveKills = kills.Where(e => e.Time <= round.EndedAt && e.AfterRoundEnd != true).ToList();
            var allDeaths = events.Where(e => e.Kind == "kill").DistinctBy(e => e.TargetId).ToList();
            var stats = new PlayerStats { Rounds = 1 };
            var ra = new RoundAnalysis { Number = round.Number, Side = participation.Side };
            void Finding(string kind, string title, string description, GameEvent e, params GameEvent[] extra)
            {
                if (e.Time > round.EndedAt || e.AfterRoundEnd == true || extra.Any(x => x.Time > round.EndedAt || x.AfterRoundEnd == true)) return;
                var f = new Finding { Id = $"{playerId}-r{round.Number}-{kind}-{e.Id}", Kind = kind, Title = title, Description = description, Round = round.Number, Time = e.Time, Tick = e.Tick, EventIds = new[] { e }.Concat(extra).Select(x => x.Id).Where(x => x.Length > 0).Distinct().ToList() };
                ra.Findings.Add(f); result.Findings.Add(f);
            }
            stats.Kills = kills.Count(e => e.ActorId == playerId);
            stats.Deaths = allDeaths.Count(e => e.TargetId == playerId);
            stats.TeamKills = allDeaths.Count(e => e.ActorId == playerId && Team(e));
            stats.Suicides = allDeaths.Count(e => e.ActorId == playerId && e.TargetId == playerId);
            stats.Assists = kills.Count(e => e.AssisterId == playerId && e.ActorId != playerId && side.GetValueOrDefault(playerId) == side.GetValueOrDefault(e.ActorId ?? ""));
            stats.FlashAssists = kills.Count(e => e.FlashAssist && e.AssisterId == playerId && e.ActorId != playerId && side.GetValueOrDefault(playerId) == side.GetValueOrDefault(e.ActorId ?? ""));
            foreach (var e in allDeaths.Where(e => e.ActorId == playerId))
            {
                if (Team(e)) Finding("team_kill", "记录到队杀", "此击杀目标为同阵营队友；建议复核当时的信息与协同。", e);
                if (e.TargetId == playerId) Finding("suicide", "记录到自杀", "事件记录击杀者与目标均为本人；具体原因需复核原回合。", e);
            }
            var health = side.Keys.ToDictionary(p => p, _ => 100);
            var damageTo = new Dictionary<string, int>();
            var scoringAssist = false;
            var dead = new HashSet<string>();
            foreach (var e in events)
            {
                ct.ThrowIfCancellationRequested();
                if (e.Kind == "damage" && e.TargetId != null && !dead.Contains(e.TargetId))
                {
                    var actual = Math.Clamp(e.Damage, 0, health.GetValueOrDefault(e.TargetId, 100));
                    health[e.TargetId] = e.TargetHealthAfter is int observedHealth && observedHealth is >= 0 and <= 100
                        ? observedHealth : Math.Max(0, health.GetValueOrDefault(e.TargetId, 100) - actual);
                    if (e.TargetHealthAfter == 0) dead.Add(e.TargetId);
                    if (e.ActorId == playerId && Enemy(e))
                    {
                        stats.Damage += actual; damageTo[e.TargetId] = damageTo.GetValueOrDefault(e.TargetId) + actual;
                        if (IsUtility(e.Weapon)) { stats.UtilityDamage += actual; if (actual > 0) Finding("utility_damage", "道具造成对敌伤害", $"此事件造成 {actual} 点实际生命值伤害。", e); }
                    }
                    else if (e.ActorId == playerId && Team(e)) { stats.TeamDamage += actual; if (actual > 0) Finding("team_damage", "记录到队伤", $"此事件记录 {actual} 点友军生命值伤害；不计入对敌ADR。", e); }
                }
                if (e.Kind == "kill" && e.TargetId != null)
                {
                    if (Enemy(e) && e.ActorId != playerId && side.GetValueOrDefault(e.ActorId ?? "") == participation.Side && damageTo.GetValueOrDefault(e.TargetId) >= 40) scoringAssist = true;
                    dead.Add(e.TargetId);
                }
                if (e.Kind == "flash" && e.ActorId == playerId && Enemy(e) && e.Duration > 0)
                    Finding("flash", "闪光影响敌人", $"记录到致盲时长 {e.Duration:0.##} 秒；是否创造安全击杀机会需进一步上下文。", e);
            }
            var usedDeaths = new HashSet<string>();
            var traded = false;
            for (var killIndex = 0; killIndex < competitiveKills.Count; killIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var revenge = competitiveKills[killIndex];
                var victim = competitiveKills.Take(killIndex).Where(d => revenge.Time - d.Time <= 5 && d.ActorId == revenge.TargetId && side.GetValueOrDefault(d.TargetId ?? "") == side.GetValueOrDefault(revenge.ActorId ?? "") && !usedDeaths.Contains(d.Id)).LastOrDefault();
                if (victim is null || victim == revenge) continue;
                usedDeaths.Add(victim.Id);
                if (victim.TargetId == playerId) { traded = true; stats.TradedDeaths++; Finding("traded_death", "死亡在补枪窗口内被补回", $"队友于 {revenge.Time - victim.Time:0.###} 秒后击杀原击杀者。", revenge, victim); }
                if (revenge.ActorId == playerId) { stats.TradeKills++; Finding("trade_kill", "完成补枪", $"于队友死亡后 {revenge.Time - victim.Time:0.###} 秒击杀原击杀者。", revenge, victim); }
            }
            if (competitiveKills.FirstOrDefault() is { } opening)
            {
                if (opening.ActorId == playerId) { stats.FirstKills++; Finding("first_kill", "取得首杀", "本回合首个有效对敌击杀。", opening); }
                if (opening.TargetId == playerId) { stats.FirstDeaths++; Finding("first_death", "本回合首死", "本回合首个有效对敌死亡；是否有可补枪队友或暴露问题需要空间证据。", opening); }
            }
            var context = new RoundContextAnalyzer().Analyze(round, playerId, ct);
            var contextFindings = new ContextFindingAnalyzer().Analyze(round, playerId, context, ct);
            ra.Findings.AddRange(contextFindings); result.Findings.AddRange(contextFindings);
            ra.Survived = round.Survivors.Contains(playerId) && stats.Deaths == 0;
            ra.Traded = traded;
            ra.Kast = stats.Kills > 0 || stats.Assists > 0 || ra.Survived || traded;
            ra.ScoringKast = stats.Kills > 0 || scoringAssist || stats.FlashAssists > 0 || ra.Survived || traded;
            ra.Kills = stats.Kills; ra.Deaths = stats.Deaths; ra.Damage = stats.Damage;
            if (stats.Kills == 2) stats.MultiKill2 = 1;
            if (stats.Kills == 3) stats.MultiKill3 = 1;
            if (stats.Kills == 4) stats.MultiKill4 = 1;
            if (stats.Kills >= 5) stats.MultiKill5 = 1;
            result.Rounds.Add(ra); contributions.Add((participation.Side, stats, ra.Kast, ra.ScoringKast, ra.Survived));
        }
        PlayerStats Sum(IEnumerable<(string Side, PlayerStats Stats, bool Kast, bool Scoring, bool Survived)> rows)
        {
            var list = rows.ToList(); var s = new PlayerStats { Rounds = list.Count };
            foreach (var x in list.Select(x => x.Stats))
            {
                s.Kills += x.Kills; s.Deaths += x.Deaths; s.Assists += x.Assists; s.Damage += x.Damage;
                s.TeamDamage += x.TeamDamage; s.TeamKills += x.TeamKills; s.Suicides += x.Suicides;
                s.FirstKills += x.FirstKills; s.FirstDeaths += x.FirstDeaths; s.TradeKills += x.TradeKills; s.TradedDeaths += x.TradedDeaths;
                s.UtilityDamage += x.UtilityDamage; s.FlashAssists += x.FlashAssists;
                s.MultiKill2 += x.MultiKill2; s.MultiKill3 += x.MultiKill3; s.MultiKill4 += x.MultiKill4; s.MultiKill5 += x.MultiKill5;
            }
            if (s.Rounds > 0)
            {
                s.Kpr = (double)s.Kills / s.Rounds; s.Dpr = (double)s.Deaths / s.Rounds; s.Adr = (double)s.Damage / s.Rounds;
                s.Kast = 100d * list.Count(x => x.Kast) / s.Rounds; s.ScoringKast = 100d * list.Count(x => x.Scoring) / s.Rounds;
                s.SurvivalPercent = 100d * list.Count(x => x.Survived) / s.Rounds;
                s.MultiKillPercent = 100d * (s.MultiKill2 + s.MultiKill3 + s.MultiKill4 + s.MultiKill5) / s.Rounds;
            }
            return s;
        }
        result.Summary = Sum(contributions); result.T = Sum(contributions.Where(x => x.Side == "T")); result.CT = Sum(contributions.Where(x => x.Side == "CT"));
        AnalysisBinding.Bind(match,result,ct);
        return result;
    }

    private static bool IsUtility(string weapon) => weapon.Replace(" ", "").ToLowerInvariant() is "hegrenade" or "inferno" or "molotov" or "molotovgrenade" or "incgrenade" or "incendiarygrenade";
}
