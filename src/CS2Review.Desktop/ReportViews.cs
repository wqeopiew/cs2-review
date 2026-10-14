using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CS2Review.Core;

namespace CS2Review.Desktop;

public sealed partial class MainWindow
{
    private List<AiReport> _storedReports=[];
    private bool _reportExplicitlySelected;
    private void ApplyDefaultReport()
    {
        if(_reportExplicitlySelected||_match is null||_analysis is null)return;
        _report=_storedReports.FirstOrDefault(r=>r.AnalysisVersion==_analysis.AnalysisVersion&&r.RulesVersion==_analysis.RulesVersion&&r.ScoringVersion==ScoringVersion)??new LocalReportService().Generate(_match,_analysis);
    }
    private sealed record ReportChoice(AiReport Report,string Label);
    private void RenderCoachingReport()
    {
        var panel=new StackPanel();panel.Children.Add(Text("证据复盘与训练建议",20,Ink,FontWeights.SemiBold));
        panel.Children.Add(Text("本地建议根据实际事件生成，无需密钥。AI 增强报告会先展示化名证据，再由你确认发送。",12,Muted));
        var actions=new WrapPanel();
        var save=Button("保存本地复盘",async(_,_)=>await SaveLocalReportAsync(),false);save.IsEnabled=_operation is null;actions.Children.Add(save);
        var generate=Button(_operation is null?"生成证据报告":"正在处理…",async(_,_)=>await GenerateReportAsync(),true);generate.IsEnabled=_operation is null;actions.Children.Add(generate);_generateButton=generate;panel.Children.Add(actions);
        if(!_settingsService.HasKeyFor(_settings.BaseUrl))panel.Children.Add(Text("AI 服务尚未配置密钥；下方本地复盘可直接阅读和导出。",12,Muted));
        var reports=new List<AiReport>();if(_report is not null)reports.Add(_report);reports.AddRange(_storedReports.Where(r=>reports.All(x=>x.Id!=r.Id)));
        if(reports.Count>1)
        {
            var choices=reports.Select(r=>new ReportChoice(r,$"{(r.Source=="local"?"本地":"AI")} · {r.CreatedAt.ToLocalTime():MM-dd HH:mm} · {r.RulesVersion}")).ToList();
            var chooser=new ComboBox{ItemsSource=choices,DisplayMemberPath="Label",SelectedItem=choices.FirstOrDefault(c=>c.Report.Id==_report?.Id),MaxWidth=500,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,10,0,10)};
            chooser.SelectionChanged+=(_,_)=>{if(chooser.SelectedItem is ReportChoice choice){_reportExplicitlySelected=true;_report=choice.Report;RenderPage();}};
            panel.Children.Add(Text("已保存报告（包含旧版）",12,Muted));panel.Children.Add(chooser);
        }
        if(_report is not null)
        {
            panel.Children.Add(Text($"{(_report.Source=="local"?"本地规则建议":"AI 增强建议")} · {_report.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {_report.RulesVersion}",11,Accent));
            if(_analysis is not null && (_report.RulesVersion!=_analysis.RulesVersion || _report.AnalysisVersion!=_analysis.AnalysisVersion || _report.ScoringVersion!=ScoringVersion))panel.Children.Add(Text("正在查看先前版本的报告。其文字和引用保留生成时的规则及评分模型；页面统计使用当前分析。",12,Muted));
            panel.Children.Add(Text(_report.Summary,14));
            foreach(var (title,items) in new[]{("表现优点",_report.Strengths),("优先复核",_report.Priorities)})
            {
                panel.Children.Add(Text(title,16,Ink,FontWeights.SemiBold));
                if(items.Count==0)panel.Children.Add(Text("记录不足以形成有依据的建议。",12,Muted));
                foreach(var item in items)
                {
                    panel.Children.Add(Text($"观测：{item.Observation}\n可能解释：{item.Inference}\n替代行动：{item.AlternativeAction}\n训练建议：{item.Training}\n下次观察：{item.FollowUpMetric}",13,Muted));
                    foreach(var evidence in item.Evidence)
                    {
                        var link=Button($"R{evidence.Round} · {evidence.Time:0.00}s · {evidence.EventId}",(_,_)=>{_roundNumber=evidence.Round;_selectedEventId=evidence.EventId;_section="回合复盘";RenderPage();},false);link.HorizontalAlignment=HorizontalAlignment.Left;panel.Children.Add(link);
                    }
                }
            }
        }
        _page.Children.Add(Panel(panel));
    }
    private async Task SaveLocalReportAsync()
    {
        if(_match is null||_analysis is null||_operation is not null)return;
        var match=_match;var analysis=JsonSerializer.Deserialize<PlayerAnalysis>(JsonSerializer.Serialize(_analysis,DataJson.Options),DataJson.Options)!;
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);
        try
        {
            var report=await Task.Run(()=>new LocalReportService().Generate(match,analysis,ct:cancellation.Token),cancellation.Token);
            await _history.InitializeAsync(cancellation.Token);
            await _history.SaveReportAsync(match.Id,analysis.PlayerId,report,cancellation.Token);
            if(_match?.Id==match.Id&&_analysis?.PlayerId==analysis.PlayerId){_reportExplicitlySelected=true;_report=report;_storedReports.Insert(0,report);}
            _status.Text="本地复盘已保存，可以关闭后继续查看。";
        }
        catch(OperationCanceledException){_status.Text="本地报告保存已取消。";}
        catch(Exception){_status.Text="本地报告未能保存；当前分析仍可查看和导出。";}
        finally{CompleteOperation();}
    }
    private ReportOptions ReportConfiguration(string? key=null)
    {
        var url=_settings.BaseUrl.TrimEnd('/');if(!url.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase))url+="/chat/completions";
        return new(){Endpoint=url,Model=_settings.Model,ApiKey=key??"",Timeout=TimeSpan.FromSeconds(_settings.TimeoutSeconds),MaxInputCharacters=_settings.InputBudget,EnableThinking=_settings.Provider=="Qwen"?false:null};
    }
    private async Task GenerateCloudReportAsync()
    {
        if(_match is null||_analysis is null||_operation is not null)return;
        if(!_settingsService.HasKeyFor(_settings.BaseUrl)){_section="AI 设置";RenderPage();_status.Text="请在软件中为当前服务保存密钥。";return;}
        var match=_match;var analysis=_analysis;var baseUrl=_settings.BaseUrl;var options=ReportConfiguration();
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);RenderPage();
        try
        {
            _status.Text="正在准备化名证据预览；尚未发送。";
            var service=new ReportService();var prepared=await service.PrepareAsync(match,analysis,options,ct:cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(!ConfirmEvidence(prepared)) {_status.Text="已取消预览，没有发送模型请求。";return;}
            cancellation.Token.ThrowIfCancellationRequested();
            if(_match?.Id!=match.Id||_analysis?.PlayerId!=analysis.PlayerId||_settings.BaseUrl!=baseUrl||_settings.Model!=options.Model||_settings.InputBudget!=options.MaxInputCharacters)throw new InvalidOperationException("配置已变化，请重新生成预览。");
            var key=_settingsService.ReadKey(baseUrl);if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("当前服务没有可用密钥。");options.ApiKey=key;
            _status.Text="正在生成并校验 AI 报告…";
            var report=await service.GeneratePreparedAsync(prepared,options,cancellation.Token);
            await _history.InitializeAsync(cancellation.Token);await _history.SaveReportAsync(match.Id,analysis.PlayerId,report,cancellation.Token);
            if(_match?.Id==match.Id&&_analysis?.PlayerId==analysis.PlayerId){_reportExplicitlySelected=true;_report=report;_storedReports.Insert(0,report);}
            _status.Text="AI 报告引用与结构化数值已校验，报告已保存。";
        }
        catch(OperationCanceledException){_status.Text="AI 操作已取消，本地复盘保留。";}
        catch(Exception){_status.Text="AI 报告未能生成或通过校验，请检查服务设置；本地复盘保留。";}
        finally{CompleteOperation();}
    }
    private bool ConfirmEvidence(PreparedReport prepared)=>BuildEvidenceDialog(prepared).ShowDialog()==true;
    private Window BuildEvidenceDialog(PreparedReport prepared)
    {
        var dialog=new Window{Title="确认发送复盘证据",Width=780,Height=650,MinWidth=600,MinHeight=450,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=BackgroundBrush,Foreground=Ink,FontFamily=FontFamily};
        if(IsVisible)dialog.Owner=this;
        var grid=new Grid{Margin=new Thickness(24)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        using var document=JsonDocument.Parse(prepared.Preview);
        var rounds=document.RootElement.GetProperty("evidence").EnumerateArray().Select(e=>e.GetProperty("round").GetInt32()).Distinct().OrderBy(x=>x).ToList();
        var heading=new StackPanel();heading.Children.Add(Text("将发送以下化名证据",20,Ink,FontWeights.SemiBold));
        heading.Children.Add(Text($"服务：{new Uri(prepared.Endpoint).Host}\n模型：{prepared.Model}\n选中 {prepared.EventCount} 条事件 · 回合 {string.Join("、",rounds)} · {prepared.CharacterCount:N0} 个字符\n回复上限 {prepared.MaxOutputTokens:N0} tokens · 请求超时 {prepared.NetworkTimeout.TotalSeconds:0} 秒",13,Accent));
        heading.Children.Add(Text("内容包括玩家统计、回合与时间、化名参与者、记录到的健康和装备、炸弹状态及少量火点坐标。昵称与 SteamID 已替换为化名；原始 demo、完整轨迹、本地路径和密钥不加入证据。",13,Muted));
        heading.Children.Add(Text("发送后可能产生模型费用；校验失败最多追加一次修复请求。",12,Muted));grid.Children.Add(heading);
        var preview=new TextBox{Text=prepared.Preview,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,FontFamily=new FontFamily("Consolas"),FontSize=12,Background=PanelBrush,Foreground=Ink};
        var expander=new Expander{Header="查看完整发送内容",Content=preview,Margin=new Thickness(0,12,0,12),IsExpanded=true,Foreground=Ink};Grid.SetRow(expander,1);grid.Children.Add(expander);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};var close=Button("取消",(_,_)=>dialog.DialogResult=false,false);close.IsCancel=true;actions.Children.Add(close);
        actions.Children.Add(Button("发送并生成",(_,_)=>dialog.DialogResult=true,true));Grid.SetRow(actions,2);grid.Children.Add(actions);dialog.Content=grid;
        return dialog;
    }
    internal async Task AssertCoachingViewsAsync()
    {
        var fixture=ImportSelfTests.Fixture();fixture.Rounds[0].Events=[new(){Id="preview-ui-evidence",Kind="shot",ActorId="1",ActorSide="T",Time=2,Tick=128}];LoadMatch(fixture);
        if(_report?.Source!="local"||_report.PlayerId!=_analysis?.PlayerId)throw new InvalidOperationException("Local coaching was not automatically bound to selected player.");
        var current=_report;var old=new AiReport{RulesVersion="rules-v1",ScoringVersion="previous-model",Summary="Synthetic preserved report"};
        _storedReports=[old,current];_report=old;_reportExplicitlySelected=true;ApplyDefaultReport();
        if(_report!=old)throw new InvalidOperationException("Restoration overwrote explicit old report selection.");
        _reportExplicitlySelected=false;ApplyDefaultReport();if(_report!=current)throw new InvalidOperationException("Default report did not prefer current rules/scoring version.");
        var prepared=await new ReportService().PrepareAsync(fixture,_analysis!,ReportConfiguration());
        var dialog=BuildEvidenceDialog(prepared);var visual=(FrameworkElement)dialog.Content;
        visual.Measure(new Size(600,450));visual.Arrange(new Rect(0,0,600,450));visual.UpdateLayout();
        IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject
        {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T typed)yield return typed;foreach(var nested in Descendants<T>(child))yield return nested;}}
        var preview=Descendants<TextBox>(visual).Single(t=>t.IsReadOnly);
        if(preview.Text!=prepared.Preview||preview.ActualHeight<=0||preview.ActualHeight>450)throw new InvalidOperationException("Evidence preview text/layout mismatch.");
        if(!Descendants<Button>(visual).Any(b=>Equals(b.Content,"发送并生成"))||!Descendants<Button>(visual).Any(b=>b.IsCancel))throw new InvalidOperationException("Evidence confirmation actions missing.");
        _section="AI 设置";RenderPage();
        if(_settings.Provider!="Qwen"||ReportConfiguration().EnableThinking!=false)throw new InvalidOperationException("Qwen default/nonthinking JSON configuration missing.");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_directory,"coaching-ui-results.txt"),"PASS hidden WPF automatic local coaching, preservation of selected old report, current-version default, exact frozen preview at 600x450, confirm/cancel actions, Qwen nonthinking defaults. No provider request or real credential access.");
    }
    private async Task TestProviderAsync()
    {
        if(_operation is not null)return;
        var key=_settingsService.ReadKey(_settings.BaseUrl);if(string.IsNullOrWhiteSpace(key)){_status.Text="请先为当前服务保存密钥。";return;}
        var options=ReportConfiguration(key);
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);
        try
        {
            _status.Text="正在发送固定连接测试文字，不包含比赛数据…";
            var result=await new ProviderConnectionService().TestAsync(options,cancellation.Token);
            var message=result.Status switch{ConnectionProbeStatus.Success=>"连接与最小 JSON 输出测试通过",ConnectionProbeStatus.AuthenticationFailed=>"鉴权失败，请检查密钥",ConnectionProbeStatus.PermissionDenied=>"服务拒绝访问，请检查权限",ConnectionProbeStatus.AddressOrModelUnavailable=>"地址或模型不可用",ConnectionProbeStatus.RateLimited=>"服务限流或额度不足",ConnectionProbeStatus.UnsupportedRequest=>"服务未接受结构化测试参数",ConnectionProbeStatus.TimedOut=>"连接测试超时",ConnectionProbeStatus.NetworkFailed=>"网络连接失败",ConnectionProbeStatus.InvalidResponse=>"回复不符合所需 JSON 结构",_=>"服务暂时不可用"};
            _status.Text=$"{message} · {result.DurationSeconds:0.0} 秒。本次测试不代表复盘内容质量。";
        }
        catch(OperationCanceledException){_status.Text="连接测试已取消。";}
        catch(Exception){_status.Text="无法测试，请检查已保存的 HTTPS 地址与模型。";}
        finally{CompleteOperation();}
    }
}
