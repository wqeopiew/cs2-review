namespace CS2Review.Core;

public sealed class HistoryComparisonService(HistoryStore history)
{
    public async Task<HistoryComparison> CompareAsync(string playerId, string? mapName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("需要玩家身份 ID。", nameof(playerId));
        var result = new HistoryComparison { PlayerId = playerId };
        var contributions = new List<(PlayerStats Stats, int Kast, int Scoring, int Survival)>();
        var entries = (await history.ListMatchesAsync(ct).ConfigureAwait(false)).OrderByDescending(m => m.ImportedAt).ThenBy(m => m.Id, StringComparer.Ordinal)
            .DistinctBy(m => m.SourceSha256, StringComparer.OrdinalIgnoreCase).Where(m => mapName is null || string.Equals(m.MapName, mapName, StringComparison.OrdinalIgnoreCase));
        // Collect forty valid target-player matches, rather than letting other
        // players' newer demos displace this player's history.
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var match = await history.LoadMatchAsync(entry.Id, ct).ConfigureAwait(false);
            if (match is null || !match.Players.Any(p => p.Id == playerId)) continue;
            var analysis = await Task.Run(() => new AnalysisEngine().Analyze(match, playerId), ct).ConfigureAwait(false);
            if (analysis.Summary.Rounds == 0) continue;
            result.Matches.Add(new() { MatchId = entry.Id, MapName = match.MapName, ImportedAt = match.ImportedAt, AnalysisVersion = analysis.AnalysisVersion, Stats = analysis.Summary });
            contributions.Add((analysis.Summary, analysis.Rounds.Count(r => r.Kast), analysis.Rounds.Count(r => r.ScoringKast), analysis.Rounds.Count(r => r.Survived)));
            if (result.Matches.Count == 40) break;
        }
        result.Aggregate = Aggregate(contributions);
        var recent = result.Matches.Take(5).ToList(); var previous = result.Matches.Skip(5).Take(5).ToList();
        result.Recent = Aggregate(contributions.Take(5)); result.Previous = Aggregate(contributions.Skip(5).Take(5));
        result.HasComparison = recent.Count >= 2 && previous.Count >= 2;
        result.Note = result.HasComparison ? $"最近 {recent.Count} 场与此前 {previous.Count} 场；比率按实际参与回合加权。本场群组差异不证明稳定习惯，评分仍未校准。" : $"当前 {result.Matches.Count} 场有效参与比赛；最近五场与前五场双方各至少两场才比较。样本不足以判断稳定习惯。";
        return result;
    }

    private static PlayerStats Aggregate(IEnumerable<(PlayerStats Stats, int Kast, int Scoring, int Survival)> values)
    {
        var list = values.ToList(); var total = new PlayerStats();
        foreach (var s in list.Select(x => x.Stats))
        {
            total.Rounds += s.Rounds; total.Kills += s.Kills; total.Deaths += s.Deaths; total.Assists += s.Assists; total.Damage += s.Damage;
            total.MultiKill2 += s.MultiKill2; total.MultiKill3 += s.MultiKill3; total.MultiKill4 += s.MultiKill4; total.MultiKill5 += s.MultiKill5;
            total.FirstKills += s.FirstKills; total.FirstDeaths += s.FirstDeaths; total.TradeKills += s.TradeKills; total.TradedDeaths += s.TradedDeaths;
            total.TeamDamage += s.TeamDamage; total.TeamKills += s.TeamKills; total.Suicides += s.Suicides; total.UtilityDamage += s.UtilityDamage; total.FlashAssists += s.FlashAssists;
        }
        if (total.Rounds > 0)
        {
            total.Kpr = (double)total.Kills / total.Rounds; total.Dpr = (double)total.Deaths / total.Rounds; total.Adr = (double)total.Damage / total.Rounds;
            total.Kast = 100d * list.Sum(s => s.Kast) / total.Rounds; total.ScoringKast = 100d * list.Sum(s => s.Scoring) / total.Rounds;
            total.SurvivalPercent = 100d * list.Sum(s => s.Survival) / total.Rounds;
            total.MultiKillPercent = 100d * (total.MultiKill2 + total.MultiKill3 + total.MultiKill4 + total.MultiKill5) / total.Rounds;
        }
        return total;
    }
}
