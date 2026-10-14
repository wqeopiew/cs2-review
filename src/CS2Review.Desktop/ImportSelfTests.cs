using System.Text.Json;
using System.Security.Cryptography;
using CS2Review.Core;

namespace CS2Review.Desktop;

internal static class ImportSelfTests
{
    internal static MatchData Fixture() => new()
    {
        Id = "synthetic-import-test", MapName = "synthetic-test-map", ParserVersion = "self-test",
        Players = [new() { Id = "1", Name = "合成测试玩家" }],
        Rounds = [new() { Number = 1, IsValid = true, Winner = "T", StartedAt = 0, EndedAt = 30, Participants = [new() { PlayerId = "1", Side = "T" }], Survivors = ["1"] }]
    };
    public static int WriteFixture(string scenario, string? inputPath)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (inputPath is null || !File.Exists(inputPath)) { Console.WriteLine("{\"schemaVersion\":1,\"type\":\"error\",\"message\":\"ArgumentList path round-trip failed\"}"); return 1; }
        if (scenario == "cancel") { Thread.Sleep(30000); return 0; }
        if (scenario == "error") { Console.WriteLine("{\"schemaVersion\":1,\"type\":\"error\",\"message\":\"synthetic truncated demo\"}"); return 1; }
        var version = scenario.StartsWith("v2", StringComparison.Ordinal) ? 2 : 1;
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = version, type = "progress", progress = 0.5, message = "合成测试" }, DataJson.Options));
        Console.Error.Write(new string('x', 100000));
        if (scenario == "schema") { Console.WriteLine("{\"schemaVersion\":99,\"type\":\"complete\"}"); return 0; }
        var fixture = Fixture(); fixture.SchemaVersion = version;
        fixture.SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))).ToLowerInvariant(); fixture.Id = fixture.SourceSha256;
        if (scenario == "bad-sha") fixture.SourceSha256 = "not-a-digest";
        if (scenario == "bad-id") fixture.Id = "unrelated-match";
        if (version == 2) { fixture.Rounds[0].Participants[0].Side = "CT"; fixture.Rounds[0].Events.Add(new() { Id = "test-event", Kind = "bomb_defuse_start", Tick = 64, Time = 1, ActorId = "1", ActorSide = "CT", HasKit = false }); }
        var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { schemaVersion = version, type = "match", match = fixture }, DataJson.Options))!;
        if (version == 1)
            foreach (var r in data["match"]!["rounds"]!.AsArray())
            {
                foreach (var name in new[] { "freezeEndedAt", "bombTimerSeconds", "economySnapshots", "statisticsEndedAt" }) r!.AsObject().Remove(name);
                foreach (var e in r!["events"]!.AsArray()) foreach (var name in new[] { "actorState", "targetState", "bombSite", "hasKit", "entityId", "firePoints", "targetHealthAfter", "afterRoundEnd" }) e!.AsObject().Remove(name);
            }
        if (scenario == "v2-old")
            foreach (var r in data["match"]!["rounds"]!.AsArray())
            {
                r!.AsObject().Remove("statisticsEndedAt");
                foreach (var e in r["events"]!.AsArray()) { e!.AsObject().Remove("targetHealthAfter"); e.AsObject().Remove("afterRoundEnd"); }
            }
        if (scenario.StartsWith("v2-statistics", StringComparison.Ordinal) || scenario.StartsWith("v2-health", StringComparison.Ordinal) || scenario.StartsWith("v2-phase", StringComparison.Ordinal))
        {
            var round = data["match"]!["rounds"]![0]!;
            var e = round["events"]![0]!;
            round["statisticsEndedAt"] = 35;
            e["kind"] = "damage"; e["time"] = 31; e["actorId"] = null; e["actorSide"] = null;
            e["targetId"] = "1"; e["targetSide"] = "CT"; e["damage"] = 100; e["targetHealthAfter"] = 0;
            if (scenario == "v2-statistics-before") round["statisticsEndedAt"] = 29;
            if (scenario == "v2-statistics-after") e["time"] = 36;
            if (scenario == "v2-statistics-unknown") round.AsObject().Remove("statisticsEndedAt");
            if (scenario == "v2-health-over") e["targetHealthAfter"] = 101;
            if (scenario == "v2-health-negative") e["targetHealthAfter"] = -1;
            if (scenario == "v2-health-shot") e["kind"] = "shot";
            if (scenario == "v2-health-no-target") e["targetId"] = null;
            if (scenario.StartsWith("v2-phase", StringComparison.Ordinal)) { e["afterRoundEnd"]=true; e["time"]=30; }
            if (scenario == "v2-phase-before") e["time"]=29;
            if (scenario == "v2-phase-unknown") round.AsObject().Remove("statisticsEndedAt");
            if (scenario == "v2-phase-shot") { e["kind"]="shot"; e["targetHealthAfter"]=null; }
        }
        if (scenario == "source-changed") File.WriteAllText(inputPath, "Changed after parser facts were collected.");
        if (scenario == "missing") data["match"]!["rounds"]![0]!.AsObject().Remove("survivors");
        if (scenario == "v2-missing") data["match"]!["rounds"]![0]!.AsObject().Remove("bombTimerSeconds");
        if (scenario == "v2-event-missing") data["match"]!["rounds"]![0]!["events"]![0]!.AsObject().Remove("actorState");
        if (scenario == "v2-invalid-site") data["match"]!["rounds"]![0]!["events"]![0]!["bombSite"] = "C";
        if (scenario == "v2-unknown") data["match"]!["rounds"]![0]!["events"]![0]!["fictionalContext"] = 1;
        if (scenario == "v2-null-economy") data["match"]!["rounds"]![0]!["economySnapshots"] = new System.Text.Json.Nodes.JsonArray { null };
        if (scenario is "v2-money-over" or "v2-equipment-over" or "v2-armor-over")
        {
            var state = new PlayerState { PlayerId = "1", Side = "CT", Time = 1, Tick = 64, Money = scenario == "v2-money-over" ? 1_000_001 : 0, EquipmentValue = scenario == "v2-equipment-over" ? 1_000_001 : 0, Armor = scenario == "v2-armor-over" ? 1001 : 0 };
            data["match"]!["rounds"]![0]!["events"]![0]!["actorState"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(state, DataJson.Options));
        }
        if (scenario == "v2-fire-over") data["match"]!["rounds"]![0]!["events"]![0]!["firePoints"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 129).Select(_ => new FirePoint()), DataJson.Options));
        Console.WriteLine(data.ToJsonString());
        if (scenario != "incomplete") Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = scenario == "v2-mixed" ? 1 : version, type = "complete" }, DataJson.Options));
        if (scenario == "duplicate-complete") Console.WriteLine("{\"schemaVersion\":1,\"type\":\"complete\"}");
        return scenario == "exit" ? 2 : 0;
    }
    public static async Task RunAsync(string directory)
    {
        var switchedSides = Fixture();
        switchedSides.FinalTScore = 5; switchedSides.FinalCTScore = 13;
        // Winner is a round-side label; it must never replace the final game-state score.
        if (!MatchHeaderFormatter.Format(switchedSides).Contains("终局 T 5 : CT 13", StringComparison.Ordinal)) throw new InvalidOperationException("Final scoreboard formatter failed.");
        switchedSides.FinalTScore = null; switchedSides.FinalCTScore = null;
        if (!MatchHeaderFormatter.Format(switchedSides).Contains("比分未知", StringComparison.Ordinal)) throw new InvalidOperationException("Unknown scoreboard formatter must not infer score from round-side winners.");
        switchedSides.FinalTScore = 5;
        if (!MatchHeaderFormatter.Format(switchedSides).Contains("比分未知", StringComparison.Ordinal)) throw new InvalidOperationException("Partial scoreboard must remain unknown.");
        Directory.CreateDirectory(directory);
        var demo = Path.Combine(directory, "中文 空格 测试.dem");
        await File.WriteAllTextAsync(demo, "Synthetic test input; not a real demo.");
        var executable = Environment.ProcessPath!;
        var hostArguments = System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? new[] { typeof(App).Assembly.Location } : Array.Empty<string>();
        var failures = new List<string>();
        var scenarios = new[] { "success", "v2-success", "v2-old", "v2-statistics-success", "v2-statistics-before", "v2-statistics-after", "v2-statistics-unknown", "v2-health-over", "v2-health-negative", "v2-health-shot", "v2-health-no-target", "v2-phase-success", "v2-phase-before", "v2-phase-unknown", "v2-phase-shot", "source-changed", "bad-sha", "bad-id", "incomplete", "error", "schema", "missing", "v2-missing", "v2-event-missing", "v2-invalid-site", "v2-unknown", "v2-null-economy", "v2-money-over", "v2-equipment-over", "v2-armor-over", "v2-fire-over", "v2-mixed", "duplicate-complete", "exit", "cancel" };
        bool Successful(string scenario) => scenario is "success" or "v2-success" or "v2-old" or "v2-statistics-success" or "v2-phase-success";
        foreach (var scenario in scenarios)
        {
            await File.WriteAllTextAsync(demo, "Synthetic test input; not a real demo.");
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancel") cancellation.CancelAfter(500);
            var passed = false;
            try
            {
                var result = await new ParserImportService().RunAsync(executable, [.. hostArguments, "--parser-fixture", scenario, Path.GetFullPath(demo)], demo, Path.Combine(directory, scenario), null, cancellation.Token);
                passed = Successful(scenario) && result.SourcePath == Path.GetFullPath(demo) && result.SourceSha256.Length == 64;
            }
            catch (OperationCanceledException) { passed = scenario == "cancel"; }
            catch (Exception e) when (e is InvalidDataException or JsonException) { passed = !Successful(scenario) && scenario != "cancel"; }
            if (!passed) { failures.Add(scenario); Console.WriteLine("FAIL synthetic parser " + scenario); continue; }
            if (Directory.EnumerateFiles(Path.Combine(directory, scenario), "*.pending.ndjson").Any()) throw new InvalidOperationException("Pending protocol was retained");
            if (!Successful(scenario) && Directory.EnumerateFiles(Path.Combine(directory, scenario)).Any()) throw new InvalidOperationException("Incomplete import was published");
            Console.WriteLine("PASS synthetic parser " + scenario);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Import self-tests failed: " + string.Join(", ", failures));
        await File.WriteAllTextAsync(Path.Combine(directory, "results.txt"), $"PASS {scenarios.Length} cases: schema1/old-schema2/current-schema2 success; reliable statistics window and event-health contracts; changed source rejection, digest/id identity, lifecycle version consistency, strict nullable context fields, invalid/unknown context, null economy entry, money/equipment/armor/fire-point caps, complete and exit validation, cancellation; Unicode/space ArgumentList; stderr drain; no partial cache. PASS final/unknown/partial scoreboard.");
    }
    public static async Task RunRealDemoAsync(string parser, string demo, string directory)
    {
        Directory.CreateDirectory(directory);
        var match = await new ParserImportService().ImportAsync(parser, demo, Path.Combine(directory, "cache"), null, CancellationToken.None);
        using var history = new HistoryStore(Path.Combine(directory, "history.db"));
        await history.InitializeAsync(CancellationToken.None);
        var restored = await CanonicalHistory.SaveAndLoadAsync(history, match, CancellationToken.None);
        match.Id = restored.Id;
        var duplicate = await CanonicalHistory.SaveAndLoadAsync(history, match, CancellationToken.None);
        if (duplicate.Id != restored.Id) throw new InvalidOperationException("Duplicate import was not deduplicated.");
        var divergent = JsonSerializer.Deserialize<MatchData>(JsonSerializer.Serialize(restored, DataJson.Options), DataJson.Options)!;
        var originalEventIds = restored.Rounds.SelectMany(r => r.Events).Select(e => e.Id).ToList();
        if (originalEventIds.Count == 0) throw new InvalidOperationException("Real-demo canonical-snapshot test requires event evidence.");
        foreach (var e in divergent.Rounds.SelectMany(r => r.Events)) e.Id = "new-parser-" + e.Id;
        divergent.Id = Guid.NewGuid().ToString("N");
        var canonical = await CanonicalHistory.SaveAndLoadAsync(history, divergent, CancellationToken.None);
        if (canonical.Id != restored.Id || !canonical.Rounds.SelectMany(r => r.Events).Select(e => e.Id).SequenceEqual(originalEventIds))
            throw new InvalidOperationException("Repeated SHA import must use original saved event IDs, not new parser evidence.");
        restored = canonical;
        ParserImportService.ValidateMatch(restored);
        if (restored.SourceSha256 != match.SourceSha256 || restored.Rounds.Count != match.Rounds.Count) throw new InvalidOperationException("History data changed.");
        var player = restored.Players.First();
        var analysis = new AnalysisEngine().Analyze(restored, player.Id);
        var originalIds = originalEventIds.ToHashSet(StringComparer.Ordinal);
        if (analysis.Findings.SelectMany(f => f.EventIds).Any(id => !originalIds.Contains(id))) throw new InvalidOperationException("Analysis must reference the canonical saved snapshot.");
        if (analysis.Summary.Rating is not null || analysis.Summary.RoundSwing is not null) throw new InvalidOperationException("Uncalibrated score must remain unavailable.");
        await File.WriteAllTextAsync(Path.Combine(directory, "match.json"), JsonSerializer.Serialize(restored, DataJson.Options));
        await File.WriteAllTextAsync(Path.Combine(directory, "analysis.json"), JsonSerializer.Serialize(analysis, DataJson.Options));
        await File.WriteAllTextAsync(Path.Combine(directory, "results.txt"), $"PASS real parser import; SHA256 dedup; history restore; changed-event-ID duplicate uses original canonical snapshot; analysis references saved event IDs; local analysis without AI. Map={match.MapName}; selectedSteamId={player.Id}; playerRounds={analysis.Summary.Rounds}; validMatchRounds={match.Rounds.Count(r => r.IsValid)}. Not evidence of rating accuracy or platform-wide compatibility.");
    }
}
