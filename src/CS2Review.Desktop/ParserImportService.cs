using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2Review.Core;

namespace CS2Review.Desktop;

public sealed record ImportProgress(double Fraction, string Message);

public sealed class ParserImportService
{
    private const int MaxLineChars = 128 * 1024 * 1024;
    private const long MaxProtocolChars = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new(DataJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public Task<MatchData> ImportAsync(string parserPath, string demoPath, string cacheDirectory,
        IProgress<ImportProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(parserPath, ["--demo", demoPath], demoPath, cacheDirectory, progress, cancellationToken), cancellationToken);

    internal async Task<MatchData> RunAsync(string parserPath, string[] arguments, string demoPath, string cacheDirectory,
        IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(demoPath)) throw new FileNotFoundException("找不到所选 demo 文件。", demoPath);
        if (!File.Exists(parserPath)) throw new FileNotFoundException("缺少本地解析器 cs2-parser.exe，请使用完整发布包。", parserPath);
        Directory.CreateDirectory(cacheDirectory);
        var pending = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".pending.ndjson");
        var info = new ProcessStartInfo(parserPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        MatchData? match = null;
        var complete = false;
        int? lifecycleVersion = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("无法启动解析器。");
            using var registration = cancellationToken.Register(() => Kill(process));
            var stderrTask = DrainErrorAsync(process.StandardError, cancellationToken);
            try
            {
                await using var cache = new StreamWriter(pending, false, new UTF8Encoding(false));
                await foreach (var line in ReadBoundedLinesAsync(process.StandardOutput, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("解析器输出空白协议行。");
                    await cache.WriteLineAsync(line.AsMemory(), cancellationToken);
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version is not (1 or 2))
                        throw new InvalidDataException("解析器协议版本不受支持。");
                    lifecycleVersion ??= version;
                    if (lifecycleVersion != version) throw new InvalidDataException("解析器生命周期消息混用了不同协议版本。");
                    var type = root.GetProperty("type").GetString();
                    RequireFields(root, type switch { "progress" => ["schemaVersion", "type", "progress", "message"], "match" => ["schemaVersion", "type", "match"], "complete" => ["schemaVersion", "type"], "error" => ["schemaVersion", "type", "message"], _ => ["schemaVersion", "type"] });
                    if (complete) throw new InvalidDataException("完成标志后仍收到数据，结果不完整。");
                    switch (type)
                    {
                        case "progress":
                            if (match is not null) throw new InvalidDataException("比赛结果后收到进度事件。");
                            var fraction = root.GetProperty("progress").GetDouble();
                            if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1) throw new InvalidDataException("解析进度无效。");
                            var message = root.GetProperty("message").GetString() ?? "解析中";
                            if (message.Length > 2048) throw new InvalidDataException("解析进度消息过长。");
                            progress?.Report(new(fraction, message));
                            break;
                        case "match":
                            if (match is not null) throw new InvalidDataException("解析器输出多个比赛结果。");
                            var data = root.GetProperty("match");
                            RequireMatchFields(data, version);
                            match = data.Deserialize<MatchData>(StrictJson) ?? throw new InvalidDataException("比赛数据为空。");
                            if (match.SchemaVersion != version) throw new InvalidDataException("比赛数据与协议版本不一致。");
                            ValidateMatch(match);
                            if (match.SourceSha256 is null || match.SourceSha256.Length != 64 || !match.SourceSha256.All(Uri.IsHexDigit) || !string.Equals(match.Id, match.SourceSha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("解析器来源 SHA-256 或比赛身份无效。");
                            break;
                        case "complete":
                            if (match is null) throw new InvalidDataException("未收到比赛数据便收到完成标志。");
                            complete = true;
                            break;
                        case "error": throw new InvalidDataException("解析失败：" + root.GetProperty("message").GetString());
                        default: throw new InvalidDataException("解析器输出未知消息类型。");
                    }
                }
                await process.WaitForExitAsync(cancellationToken);
                await stderrTask;
                if (process.ExitCode != 0) throw new InvalidDataException($"解析器异常结束（退出码 {process.ExitCode}）。文件可能截断或版本不受支持。");
                if (!complete || match is null) throw new InvalidDataException("解析结果缺少完整结束标志，未保存历史。");
                await using var source = File.OpenRead(demoPath);
                var actualSha = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
                if (!string.Equals(match.SourceSha256, actualSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("demo 文件在导入期间发生变化，解析事实与当前来源不一致；未保存缓存或历史。请等待下载完成后重试。");
                match.SourcePath = Path.GetFullPath(demoPath);
                match.ImportedAt = DateTimeOffset.UtcNow;
                await cache.FlushAsync(cancellationToken);
            }
            catch
            {
                Kill(process);
                try { await process.WaitForExitAsync(CancellationToken.None); await stderrTask; } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            var parserIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.ParserVersion))).ToLowerInvariant();
            var final = Path.Combine(cacheDirectory, $"{match.SourceSha256.ToLowerInvariant()}-s{match.SchemaVersion}-{parserIdentity}.ndjson");
            File.Move(pending, final, true);
            return match;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    private static async Task DrainErrorAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { }
    }
    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new char[16384];
        var line = new StringBuilder();
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            total += read;
            if (total > MaxProtocolChars) throw new InvalidDataException("解析结果超过缓存体积上限。");
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Append(buffer, start, i - start);
                if (line.Length > MaxLineChars) throw new InvalidDataException("单条解析消息过大。");
                yield return line.ToString().TrimEnd('\r');
                line.Clear(); start = i + 1;
            }
            line.Append(buffer, start, read - start);
            if (line.Length > MaxLineChars) throw new InvalidDataException("单条解析消息过大。");
        }
        if (line.Length > 0) throw new InvalidDataException("协议最后一行被截断。");
    }
    private static void RequireMatchFields(JsonElement data, int version)
    {
        RequireFields(data, ["schemaVersion", "id", "sourcePath", "sourceSha256", "mapName", "parserVersion", "platform", "importedAt", "warnings", "players", "rounds", "positions"], ["finalTScore", "finalCTScore"]);
        foreach (var player in data.GetProperty("players").EnumerateArray()) RequireFields(player, ["id", "name"]);
        foreach (var position in data.GetProperty("positions").EnumerateArray()) RequireFields(position, ["round", "tick", "time", "playerId", "side", "x", "y", "z", "yaw", "health"]);
        foreach (var round in data.GetProperty("rounds").EnumerateArray())
        {
            var roundFields = new[] { "number", "isValid", "winner", "startedAt", "endedAt", "participants", "survivors", "events" };
            RequireFields(round, version == 2 ? [.. roundFields, "freezeEndedAt", "bombTimerSeconds", "economySnapshots"] : roundFields, version == 2 ? ["statisticsEndedAt"] : null);
            if (version == 2) foreach (var state in round.GetProperty("economySnapshots").EnumerateArray()) RequireStateFields(state);
            foreach (var participant in round.GetProperty("participants").EnumerateArray()) RequireFields(participant, ["playerId", "side"]);
            foreach (var e in round.GetProperty("events").EnumerateArray())
            {
                var eventFields = new[] { "id", "kind", "tick", "time", "actorId", "targetId", "assisterId", "actorSide", "targetSide", "damage", "weapon", "headshot", "flashAssist", "duration", "x", "y", "z" };
                RequireFields(e, version == 2 ? [.. eventFields, "actorState", "targetState", "bombSite", "hasKit", "entityId", "firePoints"] : eventFields, version == 2 ? ["targetHealthAfter", "afterRoundEnd"] : null);
                if (version != 2) continue;
                foreach (var field in new[] { "actorState", "targetState" }) if (e.GetProperty(field).ValueKind != JsonValueKind.Null) RequireStateFields(e.GetProperty(field));
                if (e.GetProperty("firePoints").ValueKind != JsonValueKind.Null)
                    foreach (var point in e.GetProperty("firePoints").EnumerateArray()) RequireFields(point, ["x", "y", "z", "isBurning"]);
            }
        }
    }
    private static void RequireStateFields(JsonElement state) => RequireFields(state, ["playerId", "side", "tick", "time", "money", "equipmentValue", "health", "armor", "helmet", "defuseKit", "activeWeapon"]);
    private static void RequireFields(JsonElement data, string[] properties, string[]? optional = null)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("协议数据必须为对象。");
        foreach (var property in data.EnumerateObject())
        {
            if (!unique.Add(property.Name)) throw new InvalidDataException("协议字段重复：" + property.Name);
            if (!properties.Contains(property.Name, StringComparer.Ordinal) && optional?.Contains(property.Name, StringComparer.Ordinal) != true) throw new InvalidDataException("协议包含未知字段：" + property.Name);
        }
        foreach (var property in properties) if (!data.TryGetProperty(property, out _)) throw new InvalidDataException($"协议缺少 {property}。");
    }
    public static void ValidateMatch(MatchData match)
    {
        if (match.SchemaVersion is not (1 or 2) || string.IsNullOrWhiteSpace(match.MapName) || string.IsNullOrWhiteSpace(match.ParserVersion) || match.Players is null || match.Rounds is null || match.Positions is null || match.Warnings is null || match.Players.Count == 0 || match.Rounds.Count == 0)
            throw new InvalidDataException("比赛数据缺少版本、地图、玩家或回合。");
        if (match.Positions.Count > 500_000 || match.Rounds.Sum(r => (long)r.Events.Count) > 2_000_000) throw new InvalidDataException("比赛采样或事件数量超过安全上限。");
        var ids = match.Players.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != match.Players.Count || ids.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("玩家身份无效或重复。");
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var roundNumbers = new HashSet<int>();
        foreach (var round in match.Rounds)
        {
            if (!roundNumbers.Add(round.Number) || !double.IsFinite(round.StartedAt) || !double.IsFinite(round.EndedAt) || round.EndedAt < round.StartedAt) throw new InvalidDataException("回合时钟或编号无效。");
            if (round.StatisticsEndedAt is double statisticsEnd && (match.SchemaVersion != 2 || !double.IsFinite(statisticsEnd) || statisticsEnd < round.EndedAt
                || match.Rounds.Any(next => next.Number > round.Number && next.StartedAt < statisticsEnd))) throw new InvalidDataException("原始统计结算时钟无效或跨越下一回合。");
            if (round.IsValid && (round.Winner is not ("T" or "CT") || round.Participants.Count == 0)) throw new InvalidDataException("有效回合缺少可靠终结或参与玩家。");
            if (round.Participants.Any(p => !ids.Contains(p.PlayerId) || p.Side is not ("T" or "CT")) || round.Participants.Select(p => p.PlayerId).Distinct().Count() != round.Participants.Count || round.Survivors.Any(p => !round.Participants.Any(x => x.PlayerId == p))) throw new InvalidDataException("回合参与关系无效。");
            foreach (var e in round.Events)
            {
                var legacyKind = e.Kind is "kill" or "damage" or "flash" or "bomb_planted" or "bomb_defused" or "bomb_exploded" or "shot";
                var contextKind = e.Kind is "bomb_plant_begin" or "bomb_plant_aborted" or "bomb_defuse_start" or "bomb_defuse_aborted" or "inferno_start" or "inferno_expired";
                if (string.IsNullOrWhiteSpace(e.Id) || !eventIds.Add(e.Id) || !double.IsFinite(e.Time) || e.Tick < 0 || e.Damage < 0 || !(legacyKind || match.SchemaVersion == 2 && contextKind)) throw new InvalidDataException("事件证据无效或重复。");
                if (new[] { e.ActorId, e.TargetId, e.AssisterId }.Any(id => id is not null && !ids.Contains(id))) throw new InvalidDataException("事件引用未知玩家。");
                if (e.ActorSide is not (null or "T" or "CT") || e.TargetSide is not (null or "T" or "CT") || new[] { e.X, e.Y, e.Z }.Any(v => v.HasValue && !double.IsFinite(v.Value))) throw new InvalidDataException("事件阵营或坐标无效。");
                if (match.SchemaVersion == 2)
                {
                    if (e.Time < round.StartedAt || e.Time > (round.StatisticsEndedAt ?? round.EndedAt)) throw new InvalidDataException("事件超出已记录的回合统计窗口。");
                    if (e.AfterRoundEnd == true && (round.StatisticsEndedAt is null || e.Time < round.EndedAt || e.Kind is not ("kill" or "damage" or "flash") || e.TargetId is null)) throw new InvalidDataException("结算阶段事件缺少可靠边界或战斗身份。");
                    if (e.TargetHealthAfter is int health && (health is < 0 or > 100 || e.Kind != "damage" || e.TargetId is null)) throw new InvalidDataException("受伤事件的剩余生命值或目标身份无效。");
                    if (e.BombSite is not (null or "A" or "B") || e.EntityId is < 0) throw new InvalidDataException("炸弹区域或实体身份无效。");
                    ValidateState(e.ActorState, e.ActorId, e.ActorSide, e.Time, e.Tick, round);
                    ValidateState(e.TargetState, e.TargetId, e.TargetSide, e.Time, e.Tick, round);
                    if (e.FirePoints is not null && (e.FirePoints.Count > 128 || e.FirePoints.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)))) throw new InvalidDataException("Inferno 火点无效或超过 128 点上限。");
                }
            }
            if (match.SchemaVersion == 2)
            {
                if (round.FreezeEndedAt is double freeze && (!double.IsFinite(freeze) || freeze < round.StartedAt || freeze > round.EndedAt) || round.BombTimerSeconds is double timer && (!double.IsFinite(timer) || timer <= 0 || timer > 600) || round.EconomySnapshots is null) throw new InvalidDataException("回合冻结时钟、炸弹规则或经济结构无效。");
                if (round.EconomySnapshots.Any(s => s is null) || round.EconomySnapshots.Count > round.Participants.Count || round.EconomySnapshots.Select(s => s.PlayerId).Distinct().Count() != round.EconomySnapshots.Count) throw new InvalidDataException("回合经济快照为空、重复或过多。");
                foreach (var state in round.EconomySnapshots)
                {
                    if (round.FreezeEndedAt is not double at) throw new InvalidDataException("经济快照缺少可靠冻结结束时钟。");
                    ValidateState(state, state.PlayerId, null, at, null, round);
                }
            }
        }
        if (!match.Rounds.Any(r => r.IsValid)) throw new InvalidDataException("未识别到可靠结束的正式回合。");
        if (match.Positions.Any(p => !ids.Contains(p.PlayerId) || !roundNumbers.Contains(p.Round) || p.Side is not ("T" or "CT") || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) || !double.IsFinite(p.Yaw) || !double.IsFinite(p.Time))) throw new InvalidDataException("轨迹数据无效。");
    }
    private static void ValidateState(PlayerState? state, string? playerId, string? side, double time, int? tick, RoundData round)
    {
        if (state is null) return;
        if (playerId is null || state.PlayerId != playerId || state.Side is not ("T" or "CT") || side is not null && state.Side != side || !round.Participants.Any(p => p.PlayerId == state.PlayerId && p.Side == state.Side)
            || state.Tick < 0 || tick.HasValue && state.Tick != tick.Value || !double.IsFinite(state.Time) || Math.Abs(state.Time - time) > 0.0001 || state.Money is < 0 or > 1_000_000 || state.EquipmentValue is < 0 or > 1_000_000 || state.Health is < 0 or > 1000 || state.Armor is < 0 or > 1000)
            throw new InvalidDataException("玩家状态的身份、观测时钟或数值无效。");
    }
}
