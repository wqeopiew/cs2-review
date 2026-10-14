using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CS2Review.Core;

namespace CS2Review.Desktop;

public sealed partial class MainWindow
{
    private string? _selectedEventId;
    private DataGrid? _eventTable;
    private TextBlock? _eventContextText;
    private CancellationTokenSource? _comparisonCancellation;
    private HistoryComparison? _lastComparison;
    private Task? _historyRendering;

    private static string Known(int? value) => value?.ToString() ?? "未知";
    private static string Known(double? value) => value.HasValue ? $"{value:0.0}s" : "未知";
    private static string Known(bool? value) => value.HasValue ? value.Value ? "有" : "无" : "未知";
    private static DataGrid FactTable() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, MaxHeight = 230, MinHeight = 40,
        Background = PanelBrush, Foreground = Brushes.Black, RowBackground = Brush("#EFF3F8"), AlternatingRowBackground = Brush("#E4EBF3"),
        GridLinesVisibility = DataGridGridLinesVisibility.None, HeadersVisibility = DataGridHeadersVisibility.Column,
        CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
        EnableRowVirtualization = true, EnableColumnVirtualization = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private static void Columns(DataGrid grid, params (string Label, string Binding, double Width)[] columns)
    {
        foreach (var c in columns) grid.Columns.Add(new DataGridTextColumn { Header = c.Label, Binding = new System.Windows.Data.Binding(c.Binding), Width = c.Width });
    }
    private void RenderEconomyContext(RoundData round, RoundContext context)
    {
        var content = new StackPanel(); content.Children.Add(Text("开局经济与装备", 18, Ink, FontWeights.SemiBold));
        content.Children.Add(Text($"本方装备总值 {Known(context.OwnEquipmentValue)}  ·  对方装备总值 {Known(context.EnemyEquipmentValue)}  ·  冻结结束 {Known(round.FreezeEndedAt)}", 14, Accent));
        content.Children.Add(Text(context.EquipmentCoverage, 12, Muted));
        var grid = FactTable(); grid.MaxHeight = 185;
        grid.ItemsSource = round.Participants.Select(p =>
        {
            var state = round.EconomySnapshots.FirstOrDefault(s => s.PlayerId == p.PlayerId && round.FreezeEndedAt is double freeze && Math.Abs(s.Time - freeze) < 0.0001);
            return new { Player = PlayerName(p.PlayerId), SteamId = p.PlayerId, p.Side, Money = Known(state?.Money), Equipment = Known(state?.EquipmentValue), HP = Known(state?.Health), Armor = Known(state?.Armor), Helmet = Known(state?.Helmet), Kit = Known(state?.DefuseKit), Weapon = state?.ActiveWeapon is null ? "未知" : state.ActiveWeapon.Length == 0 ? "无活动武器" : state.ActiveWeapon };
        }).ToList();
        Columns(grid, ("玩家", "Player", 140), ("阵营", "Side", 50), ("现金", "Money", 70), ("装备总值", "Equipment", 90), ("生命", "HP", 65), ("护甲", "Armor", 65), ("头盔", "Helmet", 65), ("拆包钳", "Kit", 75), ("活动武器", "Weapon", 110), ("SteamID", "SteamId", 170));
        content.Children.Add(grid); _page.Children.Add(Panel(content));
    }
    private sealed record EventRow(GameEvent Event, RoundTimelinePoint? Point, string Clock, string Kind, string Actor, string Target, string Alive, string Fire, string Detail)
    {
        public string Id => Event.Id;
        public int Tick => Event.Tick;
    }
    private void RenderEventContext(RoundData round, RoundContext context)
    {
        var content = new StackPanel(); content.Children.Add(Text("事件上下文与证据", 18, Ink, FontWeights.SemiBold));
        content.Children.Add(Text("默认包含当前玩家事件及全局炸弹 / Inferno 事件。点击一行查看该时点；状态是回调时观测，不代表受伤或击杀前。", 12, Muted));
        var timelineById = context.Timeline.ToDictionary(p => p.EventId, StringComparer.Ordinal);
        var rows = round.Events.Where(e => e.Time >= round.StartedAt && e.Time <= round.EndedAt &&
            (e.ActorId == _analysis!.PlayerId || e.TargetId == _analysis.PlayerId || e.AssisterId == _analysis.PlayerId || e.Kind.StartsWith("bomb_", StringComparison.Ordinal) || e.Kind.StartsWith("inferno_", StringComparison.Ordinal) || e.Id == _selectedEventId))
            .OrderBy(e => e.Time).ThenBy(e => e.Tick).Select(e =>
            {
                var point = timelineById.GetValueOrDefault(e.Id);
                return new EventRow(e, point, $"+{e.Time - round.StartedAt:0.00}s / {e.Time:0.00}s", EventName(e.Kind), PlayerName(e.ActorId), PlayerName(e.TargetId), $"{Known(point?.OwnAlive)} : {Known(point?.EnemyAlive)}", e.FirePoints is null ? "未知" : $"{e.FirePoints.Count} / {e.FirePoints.Count(p => p.IsBurning)}", $"{e.Weapon} {(e.Kind == "damage" ? e.Damage + " HP" : "")} {(e.Headshot ? "爆头" : "")}");
            }).ToList();
        _eventContextText = Text("选择事件以查看上下文。", 13, Accent); content.Children.Add(_eventContextText);
        var states = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) }; content.Children.Add(states);
        var grid = FactTable(); _eventTable = grid; grid.ItemsSource = rows;
        Columns(grid, ("回合内 / 累计", "Clock", 160), ("Tick", "Tick", 65), ("事件", "Kind", 90), ("本方:对方人数", "Alive", 115), ("火点/燃烧点", "Fire", 100), ("发起者", "Actor", 130), ("目标", "Target", 130), ("细节", "Detail", 130), ("证据 ID", "Id", 180));
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not EventRow row) return;
            _selectedEventId = row.Id; _eventContextText.Text = DescribeEvent(row.Event, row.Point, round);
            states.Text = $"发起者状态：{DescribeState(ReliableEventState(row.Event, row.Event.ActorId, row.Event.ActorState, round))}\n目标状态：{DescribeState(ReliableEventState(row.Event, row.Event.TargetId, row.Event.TargetState, round))}";
        };
        var selected = rows.FirstOrDefault(r => r.Id == _selectedEventId) ?? rows.FirstOrDefault(r => r.Event.Kind.StartsWith("bomb_", StringComparison.Ordinal) || r.Event.Kind.StartsWith("inferno_", StringComparison.Ordinal)) ?? rows.FirstOrDefault();
        if (selected is not null) { grid.SelectedItem = selected; grid.ScrollIntoView(selected); }
        else content.Children.Add(Text("此回合没有当前玩家或全局上下文事件。旧协议不会补造新增事件。", 12, Muted));
        content.Children.Add(grid); _page.Children.Add(Panel(content));
    }
    private static string DescribeState(PlayerState? state) => state is null ? "未知（未记录）" : $"{state.Side} · HP {Known(state.Health)} · 护甲 {Known(state.Armor)} · 现金 {Known(state.Money)} · 装备 {Known(state.EquipmentValue)} · 头盔 {Known(state.Helmet)} · 拆包钳 {Known(state.DefuseKit)} · 武器 {state.ActiveWeapon ?? "未知"} · 观测 {state.Time:0.00}s / Tick {state.Tick}";
    private static PlayerState? ReliableEventState(GameEvent e, string? id, PlayerState? state, RoundData round) =>
        id is not null && state is not null && state.PlayerId == id && state.Side is "T" or "CT"
        && round.Participants.Any(p => p.PlayerId == id && p.Side == state.Side) && state.Tick == e.Tick
        && double.IsFinite(state.Time) && double.IsFinite(e.Time) && Math.Abs(state.Time - e.Time) <= 0.0001 ? state : null;
    private static bool? RecordedKit(GameEvent? e, RoundData round) => e is null ? null : e.HasKit ?? ReliableEventState(e, e.ActorId, e.ActorState, round)?.DefuseKit;
    private static string DescribeEvent(GameEvent e, RoundTimelinePoint? point, RoundData round)
    {
        var bomb = point?.BombState switch { "unplanted" => "未下包", "planting" => "下包中", "planted" => "已下包", "defusing" => "拆包中", "defused" => "已拆除", "exploded" => "已爆炸", _ => "未知" };
        var finish = point?.DefuseCanFinish is bool can ? can ? "时钟允许" : "时钟不足" : "未知";
        var fire = e.FirePoints is null ? "未知（未记录火点）" : $"{e.FirePoints.Count} 个已记录火点 / {e.FirePoints.Count(p => p.IsBurning)} 个燃烧点";
        var prior = round.Events.Select((item, index) => (Event: item, Index: index)).OrderBy(p => p.Event.Time).ThenBy(p => p.Event.Tick).ThenBy(p => p.Index).TakeWhile(p => p.Event.Id != e.Id).Select(p => p.Event).Append(e).ToList();
        var start = point?.BombState == "defusing" ? prior.LastOrDefault(p => p.Kind == "bomb_defuse_start" && p.ActorId == point.ActiveDefuserId) : null;
        var kit = RecordedKit(start, round);
        var progress = point?.DefuseRemainingSeconds is double remaining && kit is bool hasKit ? $"{100 * Math.Clamp(1 - remaining / (hasKit ? 5 : 10), 0, 1):0.0}%" : "未知";
        return $"{EventName(e.Kind)} · +{e.Time - round.StartedAt:0.00}s / 累计 {e.Time:0.00}s · Tick {e.Tick} · 证据 {e.Id}\n本方 / 对方人数 {Known(point?.OwnAlive)} / {Known(point?.EnemyAlive)} · 炸弹 {bomb} · 区域 {e.BombSite ?? "未知"}\n炸弹倒计时 {Known(point?.BombRemainingSeconds)} · 拆包剩余 {Known(point?.DefuseRemainingSeconds)} · 进度 {progress} · 能否完成 {finish}（仅比较已记录时钟）\n拆包钳 {Known(kit ?? RecordedKit(e, round))} · 本回合记录总时长 {Known(round.BombTimerSeconds)} · Inferno：{fire}";
    }
    private async Task RenderHistoryComparisonControlsAsync(StackPanel container, List<HistoryMatch> matches)
    {
        var content = new StackPanel(); container.Children.Add(Panel(content));
        content.Children.Add(Text("玩家多场比较", 20, Ink, FontWeights.SemiBold));
        var loading = Text("正在读取本地玩家列表…", 12, Muted); content.Children.Add(loading);
        var players = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in matches.OrderByDescending(m => m.ImportedAt).ThenBy(m => m.Id, StringComparer.Ordinal).DistinctBy(m => m.SourceSha256, StringComparer.OrdinalIgnoreCase))
        {
            if (_section != "历史" || !_page.Children.Contains(container)) return;
            var match = await _history.LoadMatchAsync(entry.Id, CancellationToken.None);
            if (match is not null) foreach (var player in match.Players) players.TryAdd(player.Id, player.Name.Trim());
        }
        if (_section != "历史" || !_page.Children.Contains(container)) return;
        loading.Text = "按 SteamID 与地图筛选；比率按有效参与回合加权，每个来源仅计最新版本。";
        var controls = new WrapPanel(); var playerPicker = new ComboBox { DisplayMemberPath = "Label", MinWidth = 330, Margin = new Thickness(0, 0, 12, 12) };
        var choices = players.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Select(p => new PlayerChoice(p.Key, $"{p.Value} · {p.Key}")).ToList(); playerPicker.ItemsSource = choices;
        playerPicker.SelectedItem = choices.FirstOrDefault(p => p.Id == _analysis?.PlayerId) ?? choices.FirstOrDefault();
        var mapPicker = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 0, 0, 12), ItemsSource = new[] { "所有地图" }.Concat(matches.Select(m => m.MapName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m)).ToList(), SelectedIndex = 0 };
        controls.Children.Add(playerPicker); controls.Children.Add(mapPicker); content.Children.Add(controls);
        var comparisonPanel = new StackPanel(); content.Children.Add(comparisonPanel);
        async Task RefreshAsync()
        {
            _comparisonCancellation?.Cancel(); _comparisonCancellation = new CancellationTokenSource(); var token = _comparisonCancellation.Token;
            if (playerPicker.SelectedItem is not PlayerChoice choice) { comparisonPanel.Children.Clear(); comparisonPanel.Children.Add(Text("导入比赛后可选择历史玩家进行比较。", 13, Muted)); return; }
            comparisonPanel.Children.Clear(); comparisonPanel.Children.Add(Text("正在按有效参与回合汇总…", 12, Muted));
            try
            {
                var map = mapPicker.SelectedItem as string;
                var comparison = await new HistoryComparisonService(_history).CompareAsync(choice.Id, map == "所有地图" ? null : map, token);
                if (token.IsCancellationRequested || _section != "历史" || !_page.Children.Contains(container)) return;
                _lastComparison = comparison; RenderComparison(comparisonPanel, comparison);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!token.IsCancellationRequested) { comparisonPanel.Children.Clear(); comparisonPanel.Children.Add(Text("比较读取失败；原比赛和报告仍保留。", 13, Muted)); } }
        }
        playerPicker.SelectionChanged += async (_, _) => await RefreshAsync(); mapPicker.SelectionChanged += async (_, _) => await RefreshAsync();
        await RefreshAsync();
    }
    private void RenderComparison(StackPanel panel, HistoryComparison comparison)
    {
        panel.Children.Clear(); panel.Children.Add(Text(comparison.Note, 13, Brush("#E3BF72")));
        var aggregate = comparison.Aggregate;
        panel.Children.Add(Text($"{comparison.Matches.Count} 场 / {aggregate.Rounds} 有效参与回合 · K/D/A {aggregate.Kills}/{aggregate.Deaths}/{aggregate.Assists}\nADR {aggregate.Adr:0.0} · KAST {aggregate.Kast:0.0}% · KPR {aggregate.Kpr:0.00} · DPR {aggregate.Dpr:0.00} · 存活 {aggregate.SurvivalPercent:0.0}%", 14, Accent));
        if (comparison.HasComparison)
        {
            var recent = comparison.Recent; var earlier = comparison.Previous;
            panel.Children.Add(Text($"最近 / 前期\nADR {recent.Adr:0.0} / {earlier.Adr:0.0} · KAST {recent.Kast:0.0}% / {earlier.Kast:0.0}%\nKPR {recent.Kpr:0.00} / {earlier.Kpr:0.00} · DPR {recent.Dpr:0.00} / {earlier.Dpr:0.00} · 存活 {recent.SurvivalPercent:0.0}% / {earlier.SurvivalPercent:0.0}%", 13, Muted));
        }
        else panel.Children.Add(Text("近期 / 前期对照暂不可用；需要双方各至少两场。少量比赛不能证明稳定习惯。", 12, Muted));
        var grid = FactTable(); grid.MaxHeight = 205;
        grid.ItemsSource = comparison.Matches.Select(m => new { m.MatchId, m.MapName, Date = m.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), m.Stats.Rounds, Kda = $"{m.Stats.Kills}/{m.Stats.Deaths}/{m.Stats.Assists}", Adr = m.Stats.Adr.ToString("0.0"), Kast = $"{m.Stats.Kast:0.0}%", Kpr = m.Stats.Kpr.ToString("0.00"), m.AnalysisVersion }).ToList();
        Columns(grid, ("导入时间", "Date", 145), ("地图", "MapName", 120), ("参与回合", "Rounds", 90), ("K/D/A", "Kda", 100), ("ADR", "Adr", 85), ("KAST", "Kast", 85), ("KPR", "Kpr", 75), ("分析版本", "AnalysisVersion", 100)); panel.Children.Add(grid);
        var open = Button("打开所选比赛 →", async (_, _) =>
        {
            var id = grid.SelectedItem?.GetType().GetProperty("MatchId")?.GetValue(grid.SelectedItem) as string; if (id is not null) await OpenHistoryAsync(id);
        }, false); open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
    }
    internal async Task AssertContextAndHistoryViewsAsync()
    {
        var fixture = ImportSelfTests.Fixture(); fixture.SchemaVersion = 2; fixture.Id = fixture.SourceSha256 = new string('a', 64); fixture.ParserVersion = "context-ui-self-test";
        fixture.Players = [new() { Id = "1", Name = "合成自检玩家" }, new() { Id = "2", Name = "合成对手" }];
        var round = fixture.Rounds[0]; round.Participants = [new() { PlayerId = "1", Side = "CT" }, new() { PlayerId = "2", Side = "T" }]; round.FreezeEndedAt = 1; round.BombTimerSeconds = 40;
        PlayerState State(string id, string side, double time, int tick) => new() { PlayerId = id, Side = side, Time = time, Tick = tick, Health = 100, Money = id == "1" ? 0 : null, EquipmentValue = id == "1" ? 0 : null, Armor = id == "1" ? 0 : null, Helmet = id == "1" ? false : null, DefuseKit = id == "1" ? false : null };
        round.EconomySnapshots = [State("1", "CT", 1, 64), State("2", "T", 1, 64)];
        round.Events = [new() { Id = "ui-planted", Kind = "bomb_planted", Time = 2, Tick = 128, ActorId = "2", ActorSide = "T", ActorState = State("2", "T", 2, 128), BombSite = "A" },
            new() { Id = "ui-defuse", Kind = "bomb_defuse_start", Time = 5, Tick = 320, ActorId = "1", ActorSide = "CT", ActorState = State("1", "CT", 5, 320), HasKit = false },
            new() { Id = "ui-fire", Kind = "inferno_start", Time = 6, Tick = 384, EntityId = 7, FirePoints = [] }];
        ParserImportService.ValidateMatch(fixture);
        LoadMatch(fixture); _section = "回合复盘"; _roundNumber = 1; _selectedEventId = "ui-defuse"; RenderPage();
        if (_eventTable?.SelectedItem is not EventRow selected || selected.Id != "ui-defuse" || _eventContextText is null || !_eventContextText.Text.Contains("拆包剩余 10.0s", StringComparison.Ordinal) || !_eventContextText.Text.Contains("拆包钳 无", StringComparison.Ordinal))
            throw new InvalidOperationException("Hidden WPF evidence selection/kit/defuse timing failed.");
        var rows = (IEnumerable<EventRow>)_eventTable.ItemsSource;
        _eventTable.SelectedItem = rows.Single(r => r.Id == "ui-fire");
        if (!_eventContextText.Text.Contains("0 个已记录火点 / 0 个燃烧点", StringComparison.Ordinal)) throw new InvalidOperationException("Recorded zero fire points must differ from unknown.");
        _eventTable.SelectedItem = rows.Single(r => r.Id == "ui-planted");
        if (!_eventContextText.Text.Contains("未知（未记录火点）", StringComparison.Ordinal)) throw new InvalidOperationException("Missing fire points must remain unknown.");
        if (Known(0) != "0" || Known((int?)null) != "未知" || Known(false) != "无" || Known((bool?)null) != "未知") throw new InvalidOperationException("Nullable state display lost zero/false semantics.");
        var defuse = round.Events.Single(e => e.Id == "ui-defuse");
        foreach (var invalid in new[] { "wrong-side", "wrong-tick", "wrong-time", "wrong-id" })
        {
            defuse.HasKit = null; defuse.ActorState = State("1", "CT", 5, 320); defuse.ActorState.DefuseKit = true;
            switch (invalid) { case "wrong-side": defuse.ActorState.Side = "T"; break; case "wrong-tick": defuse.ActorState.Tick++; break; case "wrong-time": defuse.ActorState.Time++; break; case "wrong-id": defuse.ActorState.PlayerId = "2"; break; }
            _selectedEventId = defuse.Id; RenderPage();
            if (_eventContextText is null || !_eventContextText.Text.Contains("拆包钳 未知", StringComparison.Ordinal) || !_eventContextText.Text.Contains("进度 未知", StringComparison.Ordinal)) throw new InvalidOperationException("Unreliable observed kit must remain unknown: " + invalid);
        }
        defuse.HasKit = false; defuse.ActorState = State("1", "CT", 5, 320);
        await _history.InitializeAsync(CancellationToken.None);
        var oldVersion = System.Text.Json.JsonSerializer.Deserialize<MatchData>(System.Text.Json.JsonSerializer.Serialize(fixture, DataJson.Options), DataJson.Options)!;
        oldVersion.SchemaVersion = 1; oldVersion.ParserVersion = "old-ui-self-test"; oldVersion.ImportedAt = DateTimeOffset.UtcNow.AddDays(-2);
        oldVersion.Rounds[0].EconomySnapshots.Clear(); oldVersion.Rounds[0].FreezeEndedAt = null; oldVersion.Rounds[0].BombTimerSeconds = null; oldVersion.Rounds[0].Events.Clear();
        var oldId = await _history.SaveMatchAsync(oldVersion, CancellationToken.None);
        fixture.ImportedAt = DateTimeOffset.UtcNow.AddDays(-1); var newId = await _history.SaveMatchAsync(fixture, CancellationToken.None);
        if (oldId == newId || await _history.LoadMatchAsync(oldId, CancellationToken.None) is null) throw new InvalidOperationException("History version snapshots must remain independent and readable.");
        _section = "历史"; RenderPage(); await PendingHistoryRender;
        if (_lastComparison is null || _lastComparison.PlayerId != "1" || _lastComparison.Matches.Count != 1 || _lastComparison.HasComparison || !_lastComparison.Note.Contains("样本", StringComparison.Ordinal)) throw new InvalidOperationException("Hidden history comparison must deduplicate source versions and disclose small samples.");
        var mapComparison = await new HistoryComparisonService(_history).CompareAsync("1", "not-the-map", CancellationToken.None);
        if (mapComparison.Matches.Count != 0) throw new InvalidOperationException("History comparison map filter failed.");
        var largeRound = new RoundData { Number = 1, IsValid = true, Winner = "CT", StartedAt = 0, EndedAt = 120, FreezeEndedAt = 1, BombTimerSeconds = 40, Participants = round.Participants, EconomySnapshots = round.EconomySnapshots, Survivors = ["1", "2"] };
        largeRound.Events = Enumerable.Range(0, 80_000).Select(i => new GameEvent { Id = "large-ui-" + i, Kind = "shot", ActorId = "1", ActorSide = "CT", Time = 2 + i * 0.001, Tick = 128 + i }).ToList();
        fixture.Rounds = [largeRound]; ParserImportService.ValidateMatch(fixture);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LoadMatch(fixture); _section = "回合复盘"; _roundNumber = 1; _selectedEventId = largeRound.Events[^1].Id; RenderPage();
        var visual = (FrameworkElement)Content; visual.Measure(new Size(1066, 640)); visual.Arrange(new Rect(0, 0, 1066, 640)); visual.UpdateLayout();
        var largeRows = ((IEnumerable<EventRow>)_eventTable!.ItemsSource).ToList();
        if (largeRows.Count != 80_000 || largeRows.Any(r => r.Point is null || r.Id != r.Point.EventId || r.Event.Tick != r.Point.Tick || r.Event.Time != r.Point.Time) || _eventTable.SelectedItem is not EventRow chosen || chosen.Id != largeRound.Events[^1].Id)
            throw new InvalidOperationException("Large hidden WPF event mapping or exact evidence selection failed.");
        _eventTable.SelectedItem = largeRows[0];
        if (_eventContextText is null || !_eventContextText.Text.Contains("证据 large-ui-0", StringComparison.Ordinal)) throw new InvalidOperationException("Large hidden WPF selection response failed.");
        stopwatch.Stop(); Console.WriteLine($"PASS hidden WPF 80000-event mapping/layout/selection: {stopwatch.ElapsedMilliseconds} ms (observation, no time threshold)");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_directory, "ui-results.txt"), $"PASS hidden native WPF: success/failure/cancel button recovery; evidence selects exact event; observed zero vs unknown; no-kit ten-second defuse; unreliable-side/id/time/tick kit stays unknown; global Inferno; version history keeps old ID, defaults latest; player/map comparison; small-sample warning; 80000-event same-ID mapping/layout/selection, observed {stopwatch.ElapsedMilliseconds} ms without a time threshold. Synthetic context test, not real parser evidence.");
    }
}
