using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CS2Review.Core;
using Microsoft.Win32;

namespace CS2Review.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly Brush BackgroundBrush = Brush("#0B1424"), PanelBrush = Brush("#142237"), Muted = Brush("#92A7BF"), Ink = Brush("#EAF1F8"), Accent = Brush("#52D8C1");
    private readonly string _directory;
    private readonly SettingsService _settingsService;
    private readonly HistoryStore _history;
    private readonly StackPanel _page = new() { Margin = new Thickness(30, 24, 30, 32) };
    private readonly TextBlock _status = Text("就绪 · 数据留在本机", 12, Muted);
    private readonly TextBlock _source = Text("尚未导入比赛", 14, Ink);
    private readonly ProgressBar _progress = new() { Height = 3, Minimum = 0, Maximum = 1, Foreground = Accent, Background = PanelBrush };
    private readonly Button _import, _cancel, _exportJson, _exportMarkdown;
    private readonly ComboBox _players = new() { MinWidth = 300, MaxWidth = 480, DisplayMemberPath = "Label", Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _search = new() { Width = 180, ToolTip = "按昵称或 SteamID 搜索", Margin = new Thickness(0, 0, 0, 0) };
    private readonly ScrollViewer _contentScroll;
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly AnalysisEngine _engine = new();
    private MatchData? _match;
    private PlayerAnalysis? _analysis;
    private AiReport? _report;
    private Button? _generateButton;
    private CancellationTokenSource? _operation;
    private string _section = "总览";
    private int? _roundNumber;
    private bool _updatingPlayers;
    private bool _disposed;
    private AppSettings _settings;

    private sealed record PlayerChoice(string Id, string Label);
    public MainWindow(string directory, bool smokeMode = false)
    {
        _directory = directory;
        _settingsService = new(directory, !smokeMode);
        try { _settings = _settingsService.Load(); } catch { _settings = new(); }
        _history = new HistoryStore(System.IO.Path.Combine(directory, "history.db"));
        Title = "CS2 复盘 · CS2Review"; Width = 1600; Height = 960; MinWidth = 900; MinHeight = 600;
        Background = BackgroundBrush; Foreground = Ink; FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyStyles();
        var root = new Grid { Background = BackgroundBrush };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new DockPanel { Background = Brush("#101C2D"), LastChildFill = true };
        var brand = new StackPanel { Margin = new Thickness(24, 30, 20, 34) };
        brand.Children.Add(Text("CS2 REVIEW", 22, Ink, FontWeights.SemiBold)); brand.Children.Add(Text("让每个回合都有收获", 12, Muted));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var sidebarBottom = new StackPanel { Margin = new Thickness(22, 18, 20, 24) };
        sidebarBottom.Children.Add(Text("LOCAL FIRST", 10, Accent)); sidebarBottom.Children.Add(Text("原生桌面 · 本地证据\n统计 raw-v2 · 规则 rules-v3", 11, Muted));
        DockPanel.SetDock(sidebarBottom, Dock.Bottom); sidebar.Children.Add(sidebarBottom);
        var nav = new StackPanel();
        foreach (var (title, label) in new[] { ("总览", "◈  比赛总览"), ("回合复盘", "≡  回合复盘"), ("历史", "◷  历史记录"), ("AI 设置", "⚙  AI 与设置") })
        {
            var button = Button(label, (_, _) => { _section = title; RenderPage(); }, false); button.Margin = new Thickness(12, 4, 12, 4); button.HorizontalContentAlignment = HorizontalAlignment.Left;
            _navigation[title] = button; nav.Children.Add(button);
        }
        sidebar.Children.Add(nav); root.Children.Add(sidebar);
        var workspace = new Grid(); Grid.SetColumn(workspace, 1);
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(30, 22, 30, 16), LastChildFill = true };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _import = Button("＋ 导入 .dem", async (_, _) => await ImportAsync(), true);
        _cancel = Button("取消", (_, _) => _operation?.Cancel(), false); _cancel.IsEnabled = false;
        _exportJson = Button("导出 JSON", (_, _) => Export(false), false); _exportJson.IsEnabled = false;
        _exportMarkdown = Button("导出报告", (_, _) => Export(true), false); _exportMarkdown.IsEnabled = false;
        buttons.Children.Add(_exportJson); buttons.Children.Add(_exportMarkdown); buttons.Children.Add(_cancel); buttons.Children.Add(_import);
        DockPanel.SetDock(buttons, Dock.Right); toolbar.Children.Add(buttons);
        var titlePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        titlePanel.Children.Add(Text("比赛复盘", 12, Muted)); titlePanel.Children.Add(_source); toolbar.Children.Add(titlePanel); workspace.Children.Add(toolbar);
        var selector = new WrapPanel { Margin = new Thickness(30, 0, 30, 0), VerticalAlignment = VerticalAlignment.Center };
        var playerLabel = Text("复盘玩家", 12, Muted); playerLabel.Margin = new Thickness(0, 8, 14, 0); selector.Children.Add(playerLabel); selector.Children.Add(_search); selector.Children.Add(_players);
        var searchLabel = Text("搜索昵称 / SteamID", 11, Muted); searchLabel.Margin = new Thickness(12, 8, 0, 0); selector.Children.Add(searchLabel);
        _search.TextChanged += (_, _) => UpdatePlayerChoices();
        _players.SelectionChanged += (_, _) => { if (!_updatingPlayers && _players.SelectedItem is PlayerChoice choice) SelectPlayer(choice.Id); };
        Grid.SetRow(selector, 1); workspace.Children.Add(selector);
        var scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _contentScroll = scroll;
        Grid.SetRow(scroll, 2); workspace.Children.Add(scroll);
        var footer = new StackPanel(); footer.Children.Add(_progress); _status.Margin = new Thickness(30, 10, 30, 12); footer.Children.Add(_status);
        Grid.SetRow(footer, 3); workspace.Children.Add(footer); root.Children.Add(workspace); Content = root;
        Closing += (_, _) => _operation?.Cancel();
        RenderPage();
        _pendingModelRestore=RestoreScoringModelAsync();
    }
    private void ApplyStyles()
    {
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button))
        {
            VisualTree = ButtonTemplate()
        }));
        Resources.Add(typeof(Button), buttonStyle);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45d)); buttonStyle.Triggers.Add(disabled);
        var comboStyle = new Style(typeof(ComboBox)); comboStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black)); comboStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13d)); comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8))); Resources.Add(typeof(ComboBox), comboStyle);
        var textStyle = new Style(typeof(TextBox)); textStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#20334C"))); textStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink)); textStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#344B66"))); textStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7))); Resources.Add(typeof(TextBox), textStyle);
    }
    private static FrameworkElementFactory ButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetValue(Border.PaddingProperty, new Thickness(15, 10, 15, 10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); return border;
    }
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Text(string value, double size = 14, Brush? color = null, FontWeight? weight = null) => new() { Text = value, FontSize = size, Foreground = color ?? Ink, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private static Button Button(string label, RoutedEventHandler click, bool primary)
    {
        var button = new Button { Content = label, Background = primary ? Accent : Brush("#21334B"), Foreground = primary ? BackgroundBrush : Ink, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0) };
        button.Click += click; return button;
    }
    private static Border Panel(UIElement child, Thickness? margin = null) => new() { Child = child, Background = PanelBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(22), Margin = margin ?? new Thickness(0, 0, 0, 16) };
    private void Heading(string title, string subtitle)
    {
        _page.Children.Add(Text(title, 30, Ink, FontWeights.SemiBold)); _page.Children.Add(Text(subtitle, 13, Muted));
        _page.Children.Add(new Border { Height = 14 });
    }
    private void RenderPage()
    {
        _page.Children.Clear();
        foreach (var item in _navigation) item.Value.Background = item.Key == _section ? Brush("#25463F") : Brushes.Transparent;
        if (_section != "历史") _comparisonCancellation?.Cancel();
        switch (_section) { case "历史": _historyRendering = RenderHistoryAsync(); break; case "AI 设置": RenderSettings(); break; case "回合复盘": RenderRounds(); break; default: RenderOverview(); break; }
    }
    internal void RenderSmokePage(string section)
    {
        _section = section switch { "rounds" => "回合复盘", "history" => "历史", "settings" => "AI 设置", _ => "总览" }; RenderPage();
    }
    internal Task PendingHistoryRender => _historyRendering ?? Task.CompletedTask;
    internal void SelectSmokeEvent(string eventId)
    {
        var round = _match?.Rounds.FirstOrDefault(r => r.Events.Any(e => e.Id == eventId));
        if (round is null) throw new InvalidDataException("Smoke event ID not present in loaded match.");
        _roundNumber = round.Number; _selectedEventId = eventId; _section = "回合复盘"; RenderPage();
    }
    internal void ScrollSmoke(double offset) { _contentScroll.ScrollToVerticalOffset(offset); _contentScroll.UpdateLayout(); }
    private void RenderEmpty()
    {
        var content = new StackPanel { Margin = new Thickness(10, 55, 10, 55), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 700 };
        content.Children.Add(Text("从一场比赛开始", 27, Ink, FontWeights.SemiBold));
        content.Children.Add(Text("导入本机的 CS2 .dem 文件，选择玩家，即可查看真实统计、第三轮 RT 估算、关键事件和逐回合轨迹。", 15, Muted));
        content.Children.Add(new Border { Height = 22 }); var button = Button("选择 demo 文件", async (_, _) => await ImportAsync(), true); button.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(button);
        content.Children.Add(Text("支持中文和空格路径。原始 demo 保留在本机。\n无需配置 AI，即可使用本地统计、内置估算与复盘。", 12, Muted)); _page.Children.Add(Panel(content));
        var limits = new StackPanel(); limits.Children.Add(Text("证据先于结论", 18, Ink, FontWeights.SemiBold));
        limits.Children.Add(Text("Rating 默认使用第三轮训练参数，Round Swing 沿用原算法；仍为小样本验证的实验估算，缺少必要事实时显示不可用。轨迹仅为坐标示意，缺少地图几何时不推断可见性、掩体或预瞄。", 13, Muted)); _page.Children.Add(Panel(limits));
    }
    private void RenderOverview()
    {
        Heading("本场总览", "有效计分回合 · 原始对敌生命值伤害 · 玩家实际参与回合作为分母");
        if (_match is null || _analysis is null) { RenderEmpty(); return; }
        var stats = _analysis.Summary;
        var hero = new Grid(); hero.ColumnDefinitions.Add(new ColumnDefinition()); hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel(); identity.Children.Add(Text(_analysis.PlayerName, 23, Ink, FontWeights.SemiBold)); identity.Children.Add(Text($"SteamID  {_analysis.PlayerId}  ·  {stats.Rounds} 个有效参与回合", 12, Muted));
        identity.Children.Add(Text($"击杀 / 死亡 / 助攻   {stats.Kills} / {stats.Deaths} / {stats.Assists}       首杀 / 首死   {stats.FirstKills} / {stats.FirstDeaths}", 16));
        identity.Children.Add(Text($"存活 {stats.SurvivalPercent:0.0}%    补枪 {stats.TradeKills}    被补枪 {stats.TradedDeaths}    闪光助攻 {stats.FlashAssists}", 12, Muted)); hero.Children.Add(identity);
        var rating = new StackPanel { Width = 300, Margin = new Thickness(20, 0, 0, 0) }; rating.Children.Add(Text("综合估算 · "+stats.RatingState, 12, Muted)); rating.Children.Add(Text(stats.Rating?.ToString("0.00") ?? "—", 37, Accent, FontWeights.SemiBold)); rating.Children.Add(Text(stats.RatingUnavailableReason, 11, Muted)); Grid.SetColumn(rating, 1); hero.Children.Add(rating); _page.Children.Add(Panel(hero));
        var cards = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(-6, 0, -6, 14) };
        foreach (var (title, value, detail) in new[] {
            ("KPR", stats.Kpr.ToString("0.00"), "每回合对敌击杀"), ("DPR", stats.Dpr.ToString("0.00"), "每回合死亡 · 事件口径"), ("ADR", stats.Adr.ToString("0.0"), "每回合对敌生命值伤害"),
            ("KAST", $"{stats.Kast:0.0}%", "击杀 / 助攻 / 存活 / 被补枪并集"), ("MULTI-KILL", $"{stats.MultiKillPercent:0.0}%", $"2K {stats.MultiKill2} · 3K {stats.MultiKill3} · 4K {stats.MultiKill4} · 5K {stats.MultiKill5}"), ("ROUND SWING · 估算", stats.RoundSwing?.ToString("+0.0;-0.0;0.0") ?? "—", stats.RoundSwing.HasValue?"平均每回合估算胜率贡献 · 百分点":"计算中或必要事实不足 · 详情见评分说明") })
        {
            var card = new StackPanel(); card.Children.Add(Text(title, 11, Muted, FontWeights.SemiBold)); card.Children.Add(Text(value, 32, Accent, FontWeights.SemiBold)); card.Children.Add(Text(detail, 11, Muted)); cards.Children.Add(Panel(card, new Thickness(6, 0, 6, 12)));
        }
        _page.Children.Add(cards);
        RenderRatingDetails();
        var split = new UniformGrid { Columns = 2 };
        foreach (var (side, s) in new[] { ("T · 进攻方", _analysis.T), ("CT · 防守方", _analysis.CT) })
        {
            var panel = new StackPanel(); panel.Children.Add(Text(side, 18, Ink, FontWeights.SemiBold));
            panel.Children.Add(Text(s.Rounds > 0 ? $"{s.Rounds} 回合    K/D/A  {s.Kills}/{s.Deaths}/{s.Assists}\nADR {s.Adr:0.0}    KAST {s.Kast:0.0}%    存活 {s.SurvivalPercent:0.0}%" : "该阵营没有有效参与回合", 14, Muted));
            panel.Children.Add(Text($"综合估算 {s.Rating?.ToString("0.00")??"不可用"} · 胜率贡献 {s.RoundSwing?.ToString("+0.00;-0.00;0.00")??"不可用"} 个百分点\n{s.RatingState}",12,Accent));
            split.Children.Add(Panel(panel, new Thickness(0, 0, 12, 16)));
        }
        _page.Children.Add(split);
        _page.Children.Add(Text($"队伤 {stats.TeamDamage} · 队杀 {stats.TeamKills} · 自杀 {stats.Suicides} · 道具伤害 {stats.UtilityDamage}    |    评分用 KAST（40 伤害门槛）{stats.ScoringKast:0.0}%", 12, Muted));
        foreach (var warning in _match.Warnings) _page.Children.Add(Text("数据提示：" + warning, 12, Brush("#E3BF72")));
        _page.Children.Add(Text("关键发现", 20, Ink, FontWeights.SemiBold));
        if (_analysis.Findings.Count == 0) _page.Children.Add(Text("当前事件规则没有生成可靠发现。仍可在回合复盘查看事实。", 13, Muted));
        foreach (var finding in _analysis.Findings.Take(6)) _page.Children.Add(FindingPanel(finding));
        RenderReport();
    }
    private Border FindingPanel(Finding finding)
    {
        var panel = new StackPanel(); panel.Children.Add(Text($"R{finding.Round}  ·  {finding.Title}", 16, Ink, FontWeights.SemiBold)); panel.Children.Add(Text(finding.Description, 13, Muted));
        panel.Children.Add(Text($"累计 {finding.Time:0.00}s · Tick {finding.Tick} · 可信度 {finding.Confidence}\n{finding.ConfidenceReason}", 11, Muted));
        var evidence = Button("打开回合证据 →", (_, _) => { _roundNumber = finding.Round; _selectedEventId = finding.EventIds.FirstOrDefault(); _section = "回合复盘"; RenderPage(); }, false); evidence.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(evidence); return Panel(panel);
    }
    private void RenderRounds()
    {
        Heading("回合复盘", "事件保留累计秒数、回合内秒数与 tick；地图与轨迹可辅助复核位置。");
        if (_match is null || _analysis is null) { RenderEmpty(); return; }
        var chooser = new ComboBox { DisplayMemberPath = "Label", MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var choices = _analysis.Rounds.Select(r => new RoundChoice(r.Number, $"R{r.Number:00}  ·  {r.Side}  ·  {r.Kills}K {r.Deaths}D  ·  伤害 {r.Damage}  ·  {(r.Survived ? "存活" : "未存活")}")).ToList();
        chooser.ItemsSource = choices; chooser.SelectedItem = choices.FirstOrDefault(c => c.Number == _roundNumber) ?? choices.FirstOrDefault();
        chooser.SelectionChanged += (_, _) => { if (chooser.SelectedItem is RoundChoice choice) { _roundNumber = choice.Number; _selectedEventId = null; RenderPage(); } };
        _page.Children.Add(chooser); _page.Children.Add(new Border { Height = 18 });
        if (chooser.SelectedItem is not RoundChoice selected) { _page.Children.Add(Text("此玩家没有有效参与回合。", 14, Muted)); return; }
        _roundNumber = selected.Number;
        var round = _match.Rounds.Single(r => r.Number == selected.Number); var analysisRound = _analysis.Rounds.Single(r => r.Number == selected.Number);
        _page.Children.Add(Text($"胜方 {round.Winner} · {round.EndedAt - round.StartedAt:0.0}s · KAST {(analysisRound.Kast ? "有贡献" : "无贡献")} · 解析版本 {_match.SchemaVersion} / {(_match.SchemaVersion >= 2 ? "上下文 context-v2" : "旧版上下文信息有限")}", 13, Muted));
        var context = new RoundContextAnalyzer().Analyze(round, _analysis.PlayerId);
        RenderEconomyContext(round, context);
        RenderEventContext(round, context);
        var traceContent = new StackPanel();
        var canvas = new Canvas { Height = 310, Background = Brush("#0C192B"), ClipToBounds = true, Margin = new Thickness(0, 12, 0, 8) };
        RenderMapTrace(traceContent,canvas,round.Number);_page.Children.Add(Panel(traceContent));
        foreach (var finding in analysisRound.Findings) _page.Children.Add(FindingPanel(finding));
    }
    private sealed record RoundChoice(int Number, string Label);
    private string PlayerName(string? id) => id is null ? "—" : _match?.Players.FirstOrDefault(p => p.Id == id)?.Name ?? id;
    private static string EventName(string kind) => kind switch { "kill" => "击杀", "damage" => "伤害", "flash" => "闪光", "shot" => "射击", "bomb_planted" => "下包完成", "bomb_defused" => "拆包完成", "bomb_exploded" => "爆炸", "bomb_plant_begin" => "开始下包", "bomb_plant_aborted" => "中断下包", "bomb_defuse_start" => "开始拆包", "bomb_defuse_aborted" => "中断拆包", "inferno_start" => "火焰生成", "inferno_expired" => "火焰消失", _ => kind };
    private void DrawTrace(Canvas canvas, int round)
    {
        canvas.Children.Clear(); if (_match is null || _analysis is null || canvas.ActualWidth < 1) return;
        var points = _match.Positions.Where(p => p.Round == round && p.PlayerId == _analysis.PlayerId).OrderBy(p => p.Time).ThenBy(p=>p.Tick).ToList(); if(DrawMapTrace(canvas,points))return;if (points.Count == 0) return;
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X); var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var scale = Math.Min((canvas.ActualWidth - 70) / Math.Max(100, maxX - minX), (canvas.ActualHeight - 60) / Math.Max(100, maxY - minY));
        Point Coordinate(PositionSample p) => new(35 + (p.X - minX) * scale, canvas.ActualHeight - 30 - (p.Y - minY) * scale);
        for (var x = 20; x < canvas.ActualWidth; x += 50) canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = canvas.ActualHeight, Stroke = Brush("#1B2F47"), StrokeThickness = 1 });
        for (var y = 10; y < canvas.ActualHeight; y += 50) canvas.Children.Add(new Line { X1 = 0, X2 = canvas.ActualWidth, Y1 = y, Y2 = y, Stroke = Brush("#1B2F47"), StrokeThickness = 1 });
        var line = new Polyline { Stroke = Accent, StrokeThickness = 2, Points = new PointCollection(points.Select(Coordinate)) }; canvas.Children.Add(line);
        foreach (var p in points.Where((_, i) => i % Math.Max(1, points.Count / 24) == 0))
        {
            var point = Coordinate(p); var angle = p.Yaw * Math.PI / 180;
            var arrow = new Line { X1 = point.X, Y1 = point.Y, X2 = point.X + 13 * Math.Cos(angle), Y2 = point.Y - 13 * Math.Sin(angle), Stroke = Brush("#D8F6EF"), StrokeThickness = 2, ToolTip = $"{p.Time:0.00}s · Tick {p.Tick}\nX {p.X:0.0} Y {p.Y:0.0} Z {p.Z:0.0}\n朝向 {p.Yaw:0.0}° · HP {p.Health}" }; canvas.Children.Add(arrow);
        }
        var first = Coordinate(points[0]); var marker = new Ellipse { Width = 10, Height = 10, Fill = Accent, ToolTip = "轨迹起点" }; Canvas.SetLeft(marker, first.X - 5); Canvas.SetTop(marker, first.Y - 5); canvas.Children.Add(marker);
    }
    private async Task RenderHistoryAsync()
    {
        Heading("历史记录与比较", "每个来源默认显示最新版本；旧快照与原报告保留。同一来源的不同版本不会被算作多场比赛。");
        var container = new StackPanel(); _page.Children.Add(container);
        try
        {
            await _history.InitializeAsync(CancellationToken.None); var matches = await _history.ListMatchesAsync(CancellationToken.None);
            if (_section != "历史") return;
            await RenderHistoryComparisonControlsAsync(container, matches);
            if (_section != "历史" || !_page.Children.Contains(container)) return;
            var search = new TextBox { ToolTip = "输入地图、日期或文件名", Margin = new Thickness(0, 0, 0, 16) }; container.Children.Add(search);
            var allVersions = new CheckBox { Content = "显示所有解析版本（可打开旧快照）", Foreground = Muted, Margin = new Thickness(0, 0, 0, 14) }; container.Children.Add(allVersions);
            var list = new StackPanel(); container.Children.Add(list);
            void Populate()
            {
                list.Children.Clear(); var versions = matches.OrderByDescending(m => m.ImportedAt).ThenBy(m => m.Id, StringComparer.Ordinal);
                var visible = allVersions.IsChecked == true ? versions.AsEnumerable() : versions.DistinctBy(m => m.SourceSha256, StringComparer.OrdinalIgnoreCase);
                var filtered = visible.Where(m => $"{m.MapName} {m.ImportedAt:yyyy-MM-dd} {System.IO.Path.GetFileName(m.SourcePath)} {m.ParserVersion}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count == 0) list.Children.Add(Text("没有匹配的历史比赛。导入完整 demo 后会自动保存。", 14, Muted));
                foreach (var m in filtered)
                {
                    var row = new StackPanel(); row.Children.Add(Text($"{m.MapName}  ·  导入时间 {m.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm}", 18, Ink, FontWeights.SemiBold)); row.Children.Add(Text(System.IO.Path.GetFileName(m.SourcePath), 12, Muted));
                    row.Children.Add(Text($"SHA-256 {m.SourceSha256[..Math.Min(16, m.SourceSha256.Length)]}… · 协议 {m.SchemaVersion} / {m.ParserVersion}", 11, Muted));
                    var open = Button("打开比赛 →", async (_, _) => await OpenHistoryAsync(m.Id), false); open.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(open); list.Children.Add(Panel(row));
                }
            }
            search.TextChanged += (_, _) => Populate(); allVersions.Checked += (_, _) => Populate(); allVersions.Unchecked += (_, _) => Populate(); Populate();
        }
        catch (Exception) { container.Children.Add(Text("历史读取失败，请检查本地数据目录是否可访问。", 14, Brush("#E3BF72"))); }
    }
    private async Task OpenHistoryAsync(string id)
    {
        try
        {
            var match = await _history.LoadMatchAsync(id, CancellationToken.None); if (match is null) throw new InvalidDataException("历史比赛不存在。");
            ParserImportService.ValidateMatch(match); LoadMatch(match); _section = "总览"; RenderPage(); await RestoreReportAsync();
            _status.Text = "已恢复完整本地比赛";
        }
        catch (Exception e) { _status.Text = "历史恢复失败：" + e.Message; }
    }
    private void RenderSettings()
    {
        Heading("AI 与设置", "本地分析始终可用；配置 AI 后可根据已经验证的事件证据生成文字建议。");
        var form = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        var providers = new ComboBox { ItemsSource = new[] { "DeepSeek", "Qwen", "OpenAI 兼容" }, SelectedItem = _settings.Provider, Margin = new Thickness(0, 0, 0, 14) };
        if (providers.SelectedItem is null) providers.SelectedIndex = 2;
        var endpoint = new TextBox { Text = _settings.BaseUrl, Margin = new Thickness(0, 0, 0, 14), MinWidth = 540 };
        var model = new TextBox { Text = _settings.Model, Margin = new Thickness(0, 0, 0, 14) };
        var key = new PasswordBox { Padding = new Thickness(9), Background = Brush("#20334C"), Foreground = Ink, Margin = new Thickness(0, 0, 0, 14) };
        var timeout = new TextBox { Text = _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 14) };
        var budget = new TextBox { Text = _settings.InputBudget.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 14) };
        providers.SelectionChanged += (_, _) =>
        {
            if ((string?)providers.SelectedItem == "DeepSeek") { endpoint.Text = "https://api.deepseek.com/v1"; model.Text = "deepseek-chat"; }
            if ((string?)providers.SelectedItem == "Qwen") { endpoint.Text = "https://dashscope.aliyuncs.com/compatible-mode/v1"; model.Text = "qwen-plus"; }
        };
        foreach (var (label, control) in new (string, UIElement)[] { ("提供商", providers), ("API 地址（HTTPS，基础地址或完整 chat/completions）", endpoint), ("模型名称", model), ("API 密钥（留空保留已保存密钥）", key), ("请求超时（10–300 秒）", timeout), ("输入字符预算（4,000–100,000）", budget) }) { form.Children.Add(Text(label, 12, Muted)); form.Children.Add(control); }
        var notice = Text(_settingsService.HasKeyFor(_settings.BaseUrl) ? "当前服务的密钥已保存在 Windows 凭据管理器。" : "当前服务尚未保存密钥，本地复盘可正常使用。", 12, Accent); form.Children.Add(notice);
        var actions = new WrapPanel();
        actions.Children.Add(Button("保存设置", (_, _) =>
        {
            try
            {
                if (!int.TryParse(timeout.Text, out var seconds) || seconds is < 10 or > 300 || !int.TryParse(budget.Text, out var chars) || chars is < 4000 or > 100000) throw new InvalidOperationException("超时或输入预算超出有效范围。");
                var settings = new AppSettings { Provider = providers.SelectedItem as string ?? "OpenAI 兼容", BaseUrl = endpoint.Text.Trim(), Model = model.Text.Trim(), TimeoutSeconds = seconds, InputBudget = chars, ScoringModelId=_settings.ScoringModelId };
                _settingsService.Save(settings, string.IsNullOrWhiteSpace(key.Password) ? null : key.Password); _settings = settings; key.Clear(); notice.Text = "设置已保存，密钥由 Windows 凭据管理器保护。"; _status.Text = "AI 设置已保存";
            }
            catch (Exception e) { notice.Text = "未保存：" + e.Message; }
        }, true));
        actions.Children.Add(Button("移除密钥", (_, _) => { _settingsService.DeleteKey(_settings.BaseUrl); key.Clear(); notice.Text = "已移除当前服务密钥，本地分析继续可用。"; }, false));
        actions.Children.Add(Button("测试已保存连接", async (_, _) => await TestProviderAsync(), false)); form.Children.Add(actions);
        form.Children.Add(Text("测试连接仅发送固定测试文字和模型名称，可能产生少量调用费用。请先保存设置；更换服务地址需要为该服务保存密钥。", 12, Muted));
        form.Children.Add(Text("Qwen 预设为北京兼容地址；地域或业务空间不同，请使用百炼控制台提供的 API 地址。报告与连接测试使用非思考模式的 JSON 输出。",12,Muted));
        form.Children.Add(Text("请求发送：选定玩家的统计与必要回合事件证据。原始 demo、API 密钥和其他本地文件不会加入分析内容。仅支持 OpenAI 兼容文本接口；Claude/Gemini 专用协议尚未适配。", 12, Muted));
        _page.Children.Add(Panel(form));
        var local = new StackPanel(); local.Children.Add(Text("本地环境", 18, Ink, FontWeights.SemiBold)); local.Children.Add(Text($"数据目录：{_directory}\n地图资源：{(_mapResource is null?"当前比赛尚未读取":"已读取本机图像与碰撞资源；Demo 版本匹配未知")}\n统计 raw-v2 · 规则 rules-v3 · 上下文 context-v2", 13, Muted)); _page.Children.Add(Panel(local));
        RenderRatingDetails();
    }
    private void RenderReport()
        => RenderCoachingReport();
    public void LoadMatch(MatchData match)
    {
        _match = match; _mapResource=null; _report = null; _analysis = null; _storedReports=[]; _roundNumber = null; _selectedEventId = null;
        _source.Text = MatchHeaderFormatter.Format(match);
        _search.Text = ""; UpdatePlayerChoices();
        var candidate = match.Players.First();
        SelectPlayer(candidate.Id); _exportJson.IsEnabled = true; _exportMarkdown.IsEnabled = true; _=RestoreMapAsync();
    }
    private void UpdatePlayerChoices()
    {
        if (_match is null) return;
        _updatingPlayers = true;
        var selected = _analysis?.PlayerId;
        var choices = _match.Players.Where(p => p.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).Select(p => new PlayerChoice(p.Id, $"{p.Name}  ·  {p.Id}  ·  {_match.Rounds.Count(r => r.IsValid && r.Participants.Any(x => x.PlayerId == p.Id))} 回合")).ToList();
        _players.ItemsSource = choices; _players.SelectedItem = choices.FirstOrDefault(p => p.Id == selected); _updatingPlayers = false;
    }
    private void SelectPlayer(string id)
    {
        if (_match is null) return;
        _reportExplicitlySelected=false;_analysis = _engine.Analyze(_match, id); _report = new LocalReportService().Generate(_match,_analysis); _storedReports=[]; _roundNumber = null; _selectedEventId = null;
        _updatingPlayers = true; _players.SelectedItem = (_players.ItemsSource as IEnumerable<PlayerChoice>)?.FirstOrDefault(p => p.Id == id); _updatingPlayers = false;
        _=ScheduleScoringAsync();RenderPage();_ = RestoreReportAsync();
    }
    private async Task RestoreReportAsync()
    {
        if (_match is null || _analysis is null) return;
        var matchId = _match.Id; var playerId = _analysis.PlayerId;
        try
        {
            await _history.InitializeAsync(CancellationToken.None); var reports = await _history.ListReportsAsync(matchId, playerId, CancellationToken.None);
            if (_match?.Id == matchId && _analysis?.PlayerId == playerId) { _storedReports=reports.OrderByDescending(r=>r.CreatedAt).ToList(); ApplyDefaultReport(); if (_section == "总览") RenderPage(); }
        }
        catch { /* Missing history never prevents local analysis. */ }
    }
    private async Task ImportAsync()
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog { Filter = "CS2 Demo (*.dem)|*.dem", Title = "选择本机 CS2 demo", CheckFileExists = true }; if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; SetBusy(true); _progress.Value = 0;
        try
        {
            var parser = System.IO.Path.Combine(AppContext.BaseDirectory, "cs2-parser.exe");
            var result = await new ParserImportService().ImportAsync(parser, dialog.FileName, System.IO.Path.Combine(_directory, "cache"), new Progress<ImportProgress>(p => { if (_operation == cancellation && !cancellation.IsCancellationRequested) { _progress.Value = p.Fraction; _status.Text = p.Message; } }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); await _history.InitializeAsync(cancellation.Token);
            var saved = await CanonicalHistory.SaveAndLoadAsync(_history, result, cancellation.Token);
            LoadMatch(saved); _section = "总览"; RenderPage(); _progress.Value = 1; _status.Text = "解析完成 · 完整比赛已保存，重复文件自动打开既有比赛";
        }
        catch (OperationCanceledException) { _status.Text = "导入已取消；未发布不完整比赛。"; _progress.Value = 0; }
        catch (Exception e) { _status.Text = "导入失败：" + e.Message; _progress.Value = 0; }
        finally { CompleteOperation(); }
    }
    private void SetBusy(bool busy)
    {
        _import.IsEnabled = !busy; _cancel.IsEnabled = busy; _players.IsEnabled = !busy; _search.IsEnabled = !busy;
        if (_generateButton is not null) { _generateButton.IsEnabled = !busy; _generateButton.Content = busy ? "正在处理…" : "生成证据报告"; }
    }
    private void CompleteOperation() { _operation = null; SetBusy(false); RenderPage(); }
    internal void AssertOperationStateRecovery()
    {
        foreach (var outcome in new[] { "success", "failure", "cancel" })
        {
            LoadMatch(ImportSelfTests.Fixture());
            using var cancellation = new CancellationTokenSource(); _operation = cancellation; SetBusy(true);
            if (_generateButton is null || _generateButton.IsEnabled) throw new InvalidOperationException("WPF operation did not enter busy state.");
            if (outcome == "success") LoadMatch(ImportSelfTests.Fixture());
            if (outcome == "cancel") cancellation.Cancel();
            CompleteOperation();
            if (_generateButton is null || !_generateButton.IsEnabled || !Equals(_generateButton.Content, "生成证据报告") || !_import.IsEnabled || _cancel.IsEnabled)
                throw new InvalidOperationException("WPF operation state did not recover after " + outcome);
        }
    }
    private async Task GenerateReportAsync()
        => await GenerateCloudReportAsync();
    private void Export(bool markdown)
    {
        if (_match is null || _analysis is null) return;
        var dialog = new SaveFileDialog { Filter = markdown ? "Markdown 报告 (*.md)|*.md" : "JSON 报告 (*.json)|*.json", FileName = $"CS2Review-{_match.MapName}-{DateTime.Now:yyyyMMdd-HHmm}" + (markdown ? ".md" : ".json") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Export reports, never configuration, credentials, or raw demo content.
            var contexts = _match.Rounds.Where(r => r.IsValid && r.Participants.Any(p => p.PlayerId == _analysis.PlayerId)).Select(r => new RoundContextAnalyzer().Analyze(r, _analysis.PlayerId)).ToList();
            var text = markdown ? MarkdownReport() : JsonSerializer.Serialize(new { schemaVersion = _match.SchemaVersion, contextVersion = "context-v2", mapName = _match.MapName, finalTScore = _match.FinalTScore, finalCTScore = _match.FinalCTScore, sourceSha256 = _match.SourceSha256, parserVersion = _match.ParserVersion, warnings = _match.Warnings, analysis = _analysis, ratingResults=_ratingResults, mapResource=_mapResource is null?null:new{_mapResource.MapName,_mapResource.BuildId,_mapResource.PhysSha256,_mapResource.GeometrySha256,_mapResource.MatchStatus}, roundContexts = contexts, aiReport = _report }, new JsonSerializerOptions(DataJson.Options) { WriteIndented = true });
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false)); _status.Text = "本地报告已导出：" + System.IO.Path.GetFileName(dialog.FileName);
        }
        catch (Exception e) { _status.Text = "导出失败：" + e.Message; }
    }
    private string MarkdownReport()
    {
        var a = _analysis!; var s = a.Summary;
        var output = new StringBuilder($"# CS2 复盘报告\n\n地图：{_match!.MapName}\n玩家：{a.PlayerName}（{a.PlayerId}）\n版本：{a.AnalysisVersion}\n来源 SHA-256：{_match.SourceSha256}\n\n有效参与回合：{s.Rounds}\nK/D/A：{s.Kills}/{s.Deaths}/{s.Assists}\nKPR：{s.Kpr:0.00}，DPR：{s.Dpr:0.00}，ADR：{s.Adr:0.0}，KAST：{s.Kast:0.0}%\nMulti-kill：{s.MultiKillPercent:0.0}%（2K {s.MultiKill2} / 3K {s.MultiKill3} / 4K {s.MultiKill4} / 5K {s.MultiKill5}）\n队伤：{s.TeamDamage}，队杀：{s.TeamKills}，自杀：{s.Suicides}\n\n评分状态：{s.RatingState}；Rating {s.Rating?.ToString("0.00")??"不可用"}，Round Swing {s.RoundSwing?.ToString("+0.00;-0.00;0.00")??"不可用"}。{s.RatingUnavailableReason}\n地图参考：{(_mapResource is null?"尚未读取本机地图":"已读取本机资源；Demo 地图版本未知")}，不支持真实可见性结论。\n");
        foreach (var finding in a.Findings) output.Append($"\n## R{finding.Round} · {finding.Title}\n\n{finding.Description}\n\n累计 {finding.Time:0.00}s / Tick {finding.Tick}\n证据：{string.Join(", ", finding.EventIds)}\n可信度：{finding.Confidence}；{finding.ConfidenceReason}\n");
        output.Append($"\n分析规则：{a.RulesVersion}；评分模型：{s.RatingModelId??"不可用"}；目标日期：{s.TargetRatingVersion}\n");
        if (_report is not null)
        {
            output.Append($"\n## {(_report.Source=="local"?"本地证据复盘":"AI 增强复盘")}（{_report.Model}）\n\n报告规则：{_report.RulesVersion}；回合状态：{_report.ContextVersion}；生成时评分模型：{_report.ScoringVersion}\n\n{_report.Summary}\n");
            foreach (var item in _report.Strengths.Concat(_report.Priorities)) output.Append($"\n### {item.Observation}\n\n解释：{item.Inference}\n替代打法：{item.AlternativeAction}\n训练：{item.Training}\n复查指标：{item.FollowUpMetric}\n证据：{string.Join(", ", item.Evidence.Select(e => $"R{e.Round} {e.Time:0.00}s {e.EventId}"))}\n");
        }
        return output.ToString();
    }
    public void DisposeServices()
    {
        if (_disposed) return; _disposed = true; _operation?.Cancel(); _scoreCancellation?.Cancel(); _scoreCancellation?.Dispose(); _comparisonCancellation?.Cancel(); _comparisonCancellation?.Dispose(); _history.Dispose();
    }
}
