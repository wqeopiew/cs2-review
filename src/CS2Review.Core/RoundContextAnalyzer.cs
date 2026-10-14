namespace CS2Review.Core;

public sealed class RoundContextAnalyzer
{
    public RoundContext Analyze(RoundData round, string playerId) => Analyze(round, playerId, CancellationToken.None);

    public RoundContext Analyze(RoundData round, string playerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(round);
        var context = new RoundContext { Number = round.Number };
        var player = round.Participants.FirstOrDefault(p => p.PlayerId == playerId && p.Side is "T" or "CT");
        if (player is null || !round.IsValid || round.Winner is not ("T" or "CT") || !double.IsFinite(round.StartedAt) || !double.IsFinite(round.EndedAt) || round.EndedAt < round.StartedAt)
            return context;
        var participants = round.Participants.Where(p => p.Side is "T" or "CT").GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.First().Side);
        var freeze = round.FreezeEndedAt is double f && double.IsFinite(f) && f >= round.StartedAt && f <= round.EndedAt ? (double?)f : null;
        var snapshots = round.EconomySnapshots.Where(s => freeze.HasValue && double.IsFinite(s.Time) && Math.Abs(s.Time - freeze.Value) <= 0.0001 && participants.TryGetValue(s.PlayerId, out var side) && s.Side == side)
            .GroupBy(s => s.PlayerId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var freezeTick = snapshots.Count > 0 ? snapshots.Values.Max(s => s.Tick) : (int?)null;
        context.PlayerStartState = snapshots.GetValueOrDefault(playerId);
        var own = participants.Where(p => p.Value == player.Side).Select(p => p.Key).ToList();
        var enemy = participants.Where(p => p.Value != player.Side).Select(p => p.Key).ToList();
        int? Equipment(List<string> team)
        {
            if (team.Count == 0 || team.Any(id => !snapshots.TryGetValue(id, out var s) || s.EquipmentValue is null or < 0)) return null;
            var sum = team.Sum(id => (long)snapshots[id].EquipmentValue!.Value);
            return sum <= int.MaxValue ? (int)sum : null;
        }
        context.OwnEquipmentValue = Equipment(own); context.EnemyEquipmentValue = Equipment(enemy);
        var ownCovered = own.Count(id => snapshots.TryGetValue(id, out var s) && s.EquipmentValue is >= 0);
        var enemyCovered = enemy.Count(id => snapshots.TryGetValue(id, out var s) && s.EquipmentValue is >= 0);
        context.EquipmentCoverage = freeze is null ? "未知：没有可靠冻结结束快照；旧协议历史不补推装备。" : $"冻结结束记录：本方 {ownCovered}/{own.Count}、对方 {enemyCovered}/{enemy.Count} 装备有值；缺任一玩家则全队总值未知。事件状态为回调时观测，非交战前快照；人数仅按完整起始健康与后续已记录事件更新。";
        static bool HealthKnown(PlayerState s) => s.Health is >= 0 and <= 1000;
        var ownInitial = own.Count > 0 && own.All(id => snapshots.TryGetValue(id, out var s) && HealthKnown(s));
        var enemyInitial = enemy.Count > 0 && enemy.All(id => snapshots.TryGetValue(id, out var s) && HealthKnown(s));
        var health = snapshots.ToDictionary(p => p.Key, p => HealthKnown(p.Value) ? p.Value.Health : null);
        var initialized = false;
        var dead = new HashSet<string>();
        var confirmedDead = new HashSet<string>();
        var bombState = "unknown";
        double? plantedAt = null, defuseAt = null, defuseDuration = null;
        string? defuser = null;
        var timer = round.BombTimerSeconds is double t && double.IsFinite(t) && t > 0 && t <= 600 ? (double?)t : null;
        var timeComparer = Comparer<double>.Create((a, b) => { ct.ThrowIfCancellationRequested(); return a.CompareTo(b); });
        var tickComparer = Comparer<int>.Create((a, b) => { ct.ThrowIfCancellationRequested(); return a.CompareTo(b); });
        var ordered = round.Events.Select((e, i) => { ct.ThrowIfCancellationRequested(); return (Event: e, Index: i); }).Where(x => x.Event.AfterRoundEnd != true && double.IsFinite(x.Event.Time) && x.Event.Time >= round.StartedAt && x.Event.Time <= round.EndedAt)
            .OrderBy(x => x.Event.Time, timeComparer).ThenBy(x => x.Event.Tick, tickComparer).ThenBy(x => x.Index, tickComparer);
        (GameEvent Event, int Index)[] sorted;
        try { sorted = ordered.ToArray(); }
        // LINQ sorting wraps comparer cancellation in InvalidOperationException.
        catch (InvalidOperationException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        foreach (var (e, _) in sorted)
        {
            ct.ThrowIfCancellationRequested();
            if (!initialized && freeze.HasValue && (e.Time > freeze.Value || e.Time == freeze.Value && freezeTick.HasValue && e.Tick >= freezeTick.Value))
            { initialized = true; foreach (var s in snapshots.Values.Where(s => s.Health == 0)) { dead.Add(s.PlayerId); confirmedDead.Add(s.PlayerId); } foreach (var id in confirmedDead.Where(participants.ContainsKey)) health[id] = 0; if (bombState == "unknown") bombState = "unplanted"; }
            int? Alive(List<string> team, bool initial) => initialized && initial && team.All(id => health.TryGetValue(id, out var hp) && hp.HasValue) ? team.Count(id => health[id] > 0) : null;
            bool? PlayerAlive() => initialized && health.TryGetValue(playerId, out var hp) && hp.HasValue ? hp.Value > 0 : null;
            var ownBefore = Alive(own, ownInitial); var enemyBefore = Alive(enemy, enemyInitial); var playerBefore = PlayerAlive();
            bool ReliableState(string? id, PlayerState? state) => id is not null && participants.TryGetValue(id, out var side)
                && state is not null && state.PlayerId == id && state.Side == side && state.Tick == e.Tick
                && double.IsFinite(state.Time) && Math.Abs(state.Time - e.Time) <= 0.0001;
            int? hurtTargetHealth = e.Kind == "damage" && e.TargetId is not null && participants.ContainsKey(e.TargetId)
                && e.TargetHealthAfter is int hp && hp is >= 0 and <= 100 ? hp : null;
            void Observe(string? id, PlayerState? state)
            {
                if (!initialized || id is null || !participants.ContainsKey(id)) return;
                // Death callbacks can precede later damage from this player's projectile.
                health[id] = confirmedDead.Contains(id) ? 0 : id == e.TargetId && hurtTargetHealth.HasValue ? hurtTargetHealth
                    : ReliableState(id, state) && HealthKnown(state!) ? state!.Health : null;
            }
            void ConfirmDeath(string id) { dead.Add(id); confirmedDead.Add(id); if (initialized && participants.ContainsKey(id)) health[id] = 0; }
            if (ReliableState(e.ActorId, e.ActorState) && e.ActorState!.Health == 0 && !(e.ActorId == e.TargetId && hurtTargetHealth.HasValue)) ConfirmDeath(e.ActorId!);
            if (hurtTargetHealth == 0 || !hurtTargetHealth.HasValue && ReliableState(e.TargetId, e.TargetState) && e.TargetState!.Health == 0) ConfirmDeath(e.TargetId!);
            if (e.Kind == "kill" && e.TargetId is not null) ConfirmDeath(e.TargetId);
            void StopDefuse()
            { defuser = null; defuseAt = defuseDuration = null; if (bombState == "defusing") bombState = "planted"; }
            if (e.Kind is "damage" or "kill" || e.Kind.StartsWith("bomb_", StringComparison.Ordinal))
            {
                Observe(e.ActorId, e.ActorState);
                if (e.TargetId is not null) Observe(e.TargetId, e.TargetState);
            }
            if (initialized && e.Kind == "kill" && e.TargetId is not null && participants.ContainsKey(e.TargetId)) health[e.TargetId] = 0;
            if (defuser is not null && dead.Contains(defuser)) StopDefuse();
            if (bombState is not ("defused" or "exploded"))
            {
                switch (e.Kind)
                {
                    case "bomb_plant_begin" when plantedAt is null: bombState = "planting"; break;
                    case "bomb_plant_aborted" when bombState == "planting": bombState = initialized ? "unplanted" : "unknown"; break;
                    case "bomb_planted": bombState = "planted"; plantedAt = e.Time; defuser = null; defuseAt = defuseDuration = null; break;
                    case "bomb_defuse_start":
                        if (e.ActorId is not null && dead.Contains(e.ActorId)) { StopDefuse(); break; }
                        bombState = "defusing"; defuser = string.IsNullOrWhiteSpace(e.ActorId) ? null : e.ActorId;
                        defuseAt = defuser is null ? null : e.Time;
                        var kit = e.HasKit ?? (ReliableState(e.ActorId, e.ActorState) ? e.ActorState!.DefuseKit : null);
                        defuseDuration = kit.HasValue && defuser is not null ? kit.Value ? 5d : 10d : null;
                        break;
                    case "bomb_defuse_aborted" when bombState == "defusing" && (defuser is null || e.ActorId is null || e.ActorId == defuser):
                        bombState = "planted"; defuser = null; defuseAt = defuseDuration = null; break;
                    case "bomb_defused": bombState = "defused"; defuser = null; defuseAt = defuseDuration = null; break;
                    case "bomb_exploded": bombState = "exploded"; defuser = null; defuseAt = defuseDuration = null; break;
                }
            }
            var remaining = plantedAt.HasValue && timer.HasValue && bombState is "planted" or "defusing" ? (double?)Math.Max(0, plantedAt.Value + timer.Value - e.Time) : bombState == "exploded" ? 0d : null;
            var defuseRemaining = defuseAt.HasValue && defuseDuration.HasValue ? (double?)Math.Max(0, defuseAt.Value + defuseDuration.Value - e.Time) : null;
            context.Timeline.Add(new() { EventId = e.Id, Kind = e.Kind, Tick = e.Tick, Time = e.Time, RoundSeconds = e.Time - round.StartedAt, OwnAliveBefore = ownBefore, EnemyAliveBefore = enemyBefore, PlayerAliveBefore = playerBefore, PlayerAliveAfter = PlayerAlive(), OwnAlive = Alive(own, ownInitial), EnemyAlive = Alive(enemy, enemyInitial), BombState = bombState, BombRemainingSeconds = remaining, DefuseRemainingSeconds = defuseRemaining, ActiveDefuserId = defuser, DefuseCanFinish = remaining.HasValue && defuseRemaining.HasValue ? defuseAt!.Value + defuseDuration!.Value <= plantedAt!.Value + timer!.Value : null });
        }
        return context;
    }
}
