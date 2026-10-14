using System.Windows;
using System.Windows.Controls;
using CS2Review.Core;
using Microsoft.Win32;

namespace CS2Review.Desktop;

public sealed partial class MainWindow
{
    private RatingModel _ratingModel=BuiltInRatingModel.Create();
    private List<RatingResult> _ratingResults=[];
    private CancellationTokenSource? _scoreCancellation;
    private Task _pendingScoringRender=Task.CompletedTask;
    private Task _pendingModelRestore=Task.CompletedTask;
    private string? _scoringRestoreNotice;
    private string ModelDirectory=>System.IO.Path.Combine(_directory,"models");
    private string ScoringVersion=>_analysis?.Summary.RatingModelId??"unavailable";
    private async Task RestoreScoringModelAsync()
    {
        var id=_settings.ScoringModelId;if(id is null)return;
        bool CurrentSelection()=>!_disposed&&_settings.ScoringModelId==id;
        try
        {
            if(id.Length!=64||!id.All(Uri.IsHexDigit))throw new ArgumentException("评分模型标识无效。");
            var model=await RatingModelCodec.LoadFileAsync(System.IO.Path.Combine(ModelDirectory,id+".json"));
            if(!CurrentSelection())return;
            if(!string.Equals(model.ModelId,id,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("评分模型文件与已保存标识不匹配。");
            _ratingModel=model;_scoringRestoreNotice=null;await ScheduleScoringAsync();
        }
        catch
        {
            if(!CurrentSelection())return;
            _ratingModel=BuiltInRatingModel.Create();
            _scoringRestoreNotice="已保存的评分模型未能载入，当前已回退第三轮 RT 内置模型；原导入选择保留，可重新导入或恢复内置估算。";
            await ScheduleScoringAsync();if(CurrentSelection())_status.Text=_scoringRestoreNotice;
        }
    }
    private void SaveScoringSelection(string? id)
    {
        var settings=new AppSettings{Provider=_settings.Provider,BaseUrl=_settings.BaseUrl,Model=_settings.Model,TimeoutSeconds=_settings.TimeoutSeconds,InputBudget=_settings.InputBudget,ScoringModelId=id};
        _settingsService.Save(settings,null);_settings=settings;
    }
    private async Task RestoreDefaultRatingAsync(bool useOriginalBaseline=false)
    {
        if(_operation is not null)return;
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);RenderPage();
        var selectionSaved=false;
        try
        {
            var model=useOriginalBaseline?DefaultRatingModel.Create():BuiltInRatingModel.Create();
            if(useOriginalBaseline)
            {
                Directory.CreateDirectory(ModelDirectory);
                await RatingModelCodec.SaveFileAsync(System.IO.Path.Combine(ModelDirectory,model.ModelId+".json"),model,cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            SaveScoringSelection(useOriginalBaseline?model.ModelId:null);_ratingModel=model;_scoringRestoreNotice=null;
            selectionSaved=true;
            await ScheduleScoringAsync(cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();
            _status.Text=useOriginalBaseline?"已切换原版基础估算；未训练、未校准，无需配置 AI。":"已恢复第三轮 RT 内置模型；RS 沿用原算法，无需配置 AI。";
        }
        catch(OperationCanceledException){_status.Text=selectionSaved?"计算已取消；评分选择已保存，原始统计保留。":"切换已取消；原评分选择和原始统计保留。";}
        catch(Exception){_status.Text="恢复内置估算时遇到问题，请检查本地设置目录；原始统计保留。";}
        finally{CompleteOperation();}
    }
    private async Task ImportRatingModelAsync()
    {
        if(_operation is not null)return;
        var file=new OpenFileDialog{Title="选择已冻结的实验评分模型",Filter="评分模型 (*.json)|*.json",CheckFileExists=true};if(file.ShowDialog(this)!=true)return;
        using var cancellation=new CancellationTokenSource();_operation=cancellation;SetBusy(true);RenderPage();
        try
        {
            var model=await RatingModelCodec.LoadFileAsync(file.FileName,cancellation.Token);
            Directory.CreateDirectory(ModelDirectory);await RatingModelCodec.SaveFileAsync(System.IO.Path.Combine(ModelDirectory,model.ModelId+".json"),model,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();SaveScoringSelection(model.ModelId);_ratingModel=model;_scoringRestoreNotice=null;
            await ScheduleScoringAsync(cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();_status.Text="实验评分模型已导入。估算采用模型自定参数，不代表官方评分或已验证精度。";
        }
        catch(OperationCanceledException){_status.Text="评分计算已取消；已保存的模型和原始统计保留。";}
        catch(Exception){_status.Text="评分模型未能导入，请检查格式、完整性与模型版本；原始统计保留。";}
        finally{CompleteOperation();}
    }
    private Task ScheduleScoringAsync(CancellationToken ct=default)=>_pendingScoringRender=RecomputeScoringAsync(ct);
    internal async Task AwaitSmokeScoringAsync()
    {
        await _pendingModelRestore;await _pendingScoringRender;
    }
    private async Task RecomputeScoringAsync(CancellationToken ct=default)
    {
        _scoreCancellation?.Cancel();
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);var token=cancellation.Token;_scoreCancellation=cancellation;
        var match=_match;var analysis=_analysis;var model=_ratingModel;_ratingResults=[];
        try
        {
            if(match is null||analysis is null)return;
            foreach(var stats in new[]{analysis.Summary,analysis.T,analysis.CT})
            {
                stats.Rating=null;stats.RoundSwing=null;stats.RatingModelId=model.ModelId;stats.RatingAxes=[];stats.RatingState="正在计算估算";
                stats.RatingUnavailableReason="正在根据当前评分模型计算；尚未校准，不代表官方评分。";
            }
            ApplyDefaultReport();
            var results=await Task.Run(()=>new[]{"all","T","CT"}.Select(side=>new RatingEngine().Score(match,analysis.PlayerId,model,side:side,ct:token)).ToList(),token);
            token.ThrowIfCancellationRequested();if(_disposed||_match?.Id!=match.Id||_analysis!=analysis||_ratingModel!=model)return;
            void Apply(PlayerStats stats,RatingResult result)
            {
                stats.Rating=result.Overall;stats.RoundSwing=result.RoundSwing;stats.RatingModelId=model.ModelId;stats.RatingAxes=new(result.Axes);
                var baseline=DefaultRatingModel.IsBaseline(model);var roundThree=BuiltInRatingModel.IsRoundThree(model);
                stats.RatingState=result.Overall is null?(baseline||roundThree?"不可用 / 内置估算条件不足":"不可用 / 导入模型条件不足"):(roundThree?"第三轮 RT / RS 原算法 · 实验估算":baseline?"原版基础估算 / 未校准":"导入模型估算 / 未校准");stats.TargetRatingVersion=result.TargetDate;
                stats.RatingUnavailableReason=result.UnavailableReasons.Count>0?string.Join("；",result.UnavailableReasons):(roundThree?"RT 使用第三轮训练参数，RS 沿用原算法；仅在小样本新赛事验证，不代表官方评分。":baseline?"公开权重与部分胜率数据 + 本软件假设；尚未校准，不代表官方 Rating 3.0。":"采用导入模型的概率、贡献分配与标准化参数；未验证官方评分精度。");
            }
            _ratingResults=results;Apply(analysis.Summary,results[0]);Apply(analysis.T,results[1]);Apply(analysis.CT,results[2]);
            ApplyDefaultReport();
            if(_section=="总览"||_section=="AI 设置")RenderPage();
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){}
        catch(OperationCanceledException){throw;}
        catch(Exception){if(_analysis==analysis&&!_disposed)_status.Text="当前比赛无法按此模型完成估算，原始统计保留。";}
        finally{if(ReferenceEquals(_scoreCancellation,cancellation))_scoreCancellation=null;}
        ct.ThrowIfCancellationRequested();
    }
    internal async Task RestoreSmokeRatingAsync(string path)
    {
        await _pendingModelRestore;_ratingModel=await RatingModelCodec.LoadFileAsync(path);_scoringRestoreNotice=null;await ScheduleScoringAsync();RenderPage();
    }
    internal async Task AssertDefaultRatingAsync()
    {
        if(_settings.ScoringModelId is not null||_ratingModel is not {SchemaVersion:3,ModelVersion:"rating-rt-calibrated-v1"})
            throw new InvalidOperationException("A fresh installation must select the built-in estimate without importing a model.");
        var fixture=ImportSelfTests.Fixture();fixture.SchemaVersion=2;fixture.Id=fixture.SourceSha256=new string('d',64);fixture.ParserVersion="rating-ui-self-test";
        fixture.Players=[new(){Id="1",Name="合成评分自检玩家"},new(){Id="2",Name="合成对手"}];
        PlayerState State(string id,string side,double time,int health=100)=>new(){PlayerId=id,Side=side,Tick=(int)(time*64),Time=time,Health=health,EquipmentValue=5000,DefuseKit=false};
        RoundData Round(int number,string ownSide,double start)
        {
            var enemySide=ownSide=="T"?"CT":"T";var freeze=start+1;var hit=start+4;var kill=start+5;
            return new(){Number=number,IsValid=true,Winner=ownSide,StartedAt=start,EndedAt=start+30,FreezeEndedAt=freeze,BombTimerSeconds=40,
                Participants=[new(){PlayerId="1",Side=ownSide},new(){PlayerId="2",Side=enemySide}],
                EconomySnapshots=[State("1",ownSide,freeze),State("2",enemySide,freeze)],Survivors=["1"],
                Events=[new(){Id=$"rating-hit-{number}",Kind="damage",Time=hit,Tick=(int)(hit*64),ActorId="1",ActorSide=ownSide,TargetId="2",TargetSide=enemySide,Damage=100,ActorState=State("1",ownSide,hit),TargetState=State("2",enemySide,hit,0)},
                    new(){Id=$"rating-kill-{number}",Kind="kill",Time=kill,Tick=(int)(kill*64),ActorId="1",ActorSide=ownSide,TargetId="2",TargetSide=enemySide,ActorState=State("1",ownSide,kill),TargetState=State("2",enemySide,kill,0)}]};
        }
        fixture.Rounds=[Round(1,"T",0),Round(2,"CT",40)];ParserImportService.ValidateMatch(fixture);
        LoadMatch(fixture);await RecomputeScoringAsync();
        var baselineId=_ratingModel!.ModelId;
        if(_analysis!.Summary.Rating is not double overall||!double.IsFinite(overall)||_analysis.Summary.RoundSwing is not double swing||!double.IsFinite(swing)
            ||_analysis.T.Rating is null||_analysis.T.RoundSwing is null||_analysis.CT.Rating is null||_analysis.CT.RoundSwing is null||_analysis.T.Rounds!=1||_analysis.CT.Rounds!=1)
            throw new InvalidOperationException("Complete facts must produce a default estimate for overall, T and CT without an AI key.");
        var prepared=await new ReportService().PrepareAsync(fixture,_analysis,ReportConfiguration());
        if(prepared.ScoringVersion!=baselineId||_report?.ScoringVersion!=baselineId)
            throw new InvalidOperationException("Default local and prepared reports must bind to the built-in model SHA.");
        _section="总览";RenderPage();
        IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject
        {for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T typed)yield return typed;foreach(var nested in Descendants<T>(child))yield return nested;}}
        if(!Descendants<TextBlock>(_page).Any(t=>t.Text.Contains("第三轮 RT",StringComparison.Ordinal))
            ||!Descendants<Button>(_page).Any(b=>Equals(b.Content,"恢复内置估算"))
            ||!Descendants<Button>(_page).Any(b=>Equals(b.Content,"使用原版基础估算")))
            throw new InvalidOperationException("The overview must identify the uncalibrated estimate and offer the built-in restore action.");
        fixture.Rounds[0].EconomySnapshots[1].Health=null;LoadMatch(fixture);await RecomputeScoringAsync();
        if(_analysis!.Summary.Rating is not null||_analysis.Summary.RoundSwing is not null||_analysis.Summary.RatingModelId!=baselineId)
            throw new InvalidOperationException("Unknown facts must keep Rating and Swing null while retaining the selected model identity.");
        fixture.Rounds[0].EconomySnapshots[1].Health=100;
        RatingModel Imported(string feature)=>RatingModelCodec.Seal(new(){TrainingManifestSha256=RatingModelCodec.Sha256(System.Text.Encoding.UTF8.GetBytes("SYNTHETIC WPF model-switch test only")),FeatureVersion=feature,Weights=[1d/6,1d/6,1d/6,1d/6,1d/6,1d/6],ParameterDisclosure="合成界面自检模型；不作为真实训练或精度验证。"});
        var imported=Imported("freeze-equipment-value-proxy-v1");Directory.CreateDirectory(ModelDirectory);
        await RatingModelCodec.SaveFileAsync(System.IO.Path.Combine(ModelDirectory,imported.ModelId+".json"),imported);
        _settings.ScoringModelId=imported.ModelId;_settingsService.Save(_settings,null);LoadMatch(fixture);await RestoreScoringModelAsync();
        if(_ratingModel!.ModelId!=imported.ModelId||_analysis!.Summary.RatingModelId!=imported.ModelId||_analysis.Summary.Rating is null||_settingsService.Load().ScoringModelId!=imported.ModelId)
            throw new InvalidOperationException("A valid persisted imported model must override the default and retain its saved selection.");
        var strong=Imported("strongest-weapon-armor-v1");await RatingModelCodec.SaveFileAsync(System.IO.Path.Combine(ModelDirectory,strong.ModelId+".json"),strong);
        _settings.ScoringModelId=strong.ModelId;_settingsService.Save(_settings,null);await RestoreScoringModelAsync();
        if(_ratingModel.ModelId!=strong.ModelId||_analysis.Summary.Rating is not null||_analysis.Summary.RoundSwing is not null||_analysis.Summary.RatingModelId!=strong.ModelId)
            throw new InvalidOperationException("An imported model missing its required facts must remain unavailable without switching to the default.");
        var missingId=new string('f',64);_settings.ScoringModelId=missingId;_settingsService.Save(_settings,null);await RestoreScoringModelAsync();
        if(_ratingModel.ModelId!=baselineId||_settings.ScoringModelId!=missingId||!_status.Text.Contains("回退",StringComparison.Ordinal))
            throw new InvalidOperationException("A failed saved-model load must disclose the default fallback and preserve the failed saved selection.");
        _settings.ScoringModelId=imported.ModelId;_settingsService.Save(_settings,null);
        var restoring=RestoreScoringModelAsync();await RestoreDefaultRatingAsync();await restoring;
        if(_ratingModel.ModelId!=baselineId||_settings.ScoringModelId is not null||_settingsService.Load().ScoringModelId is not null||_analysis.Summary.RatingModelId!=baselineId)
            throw new InvalidOperationException("Restoring the default must persist the cleared selection and defeat an older model restore in flight.");
        await RestoreDefaultRatingAsync(useOriginalBaseline:true);
        var originalId=DefaultRatingModel.Create().ModelId;
        if(_ratingModel.ModelId!=originalId||_settingsService.Load().ScoringModelId!=originalId||_analysis.Summary.RatingModelId!=originalId)
            throw new InvalidOperationException("Original-baseline rollback must persist the exact original model and recompute the report.");
        await RestoreScoringModelAsync();
        if(_ratingModel.ModelId!=originalId||_analysis.Summary.RoundSwing!=swing)
            throw new InvalidOperationException("A persisted original-baseline rollback must restore without changing RS.");
        await RestoreDefaultRatingAsync();
        if(_ratingModel.ModelId!=baselineId||_settingsService.Load().ScoringModelId is not null||_analysis.Summary.RatingModelId!=baselineId)
            throw new InvalidOperationException("Switching back from the original baseline must restore the third-round default and its report identity.");
        var noRounds=ImportSelfTests.Fixture();noRounds.SourceSha256=new string('e',64);noRounds.Rounds=[];LoadMatch(noRounds);await RecomputeScoringAsync();
        if(_analysis!.Summary.Rating is not null||_analysis.Summary.RoundSwing is not null)
            throw new InvalidOperationException("No reliable participating rounds must keep the default score unavailable.");
        LoadMatch(fixture);await RecomputeScoringAsync();
        await File.WriteAllTextAsync(System.IO.Path.Combine(_directory,"rating-ui-results.txt"),"PASS hidden WPF: fresh-install built-in estimate without AI credentials; overall/T/CT estimates; report model SHA binding; visible uncalibrated label and restore action; unknown facts and empty rounds remain null; persisted imported-model restore; imported missing-facts model remains selected; failed restore explicitly falls back; default restore clears persisted selection and wins over older asynchronous restore. Synthetic UI fixtures, not evidence of rating accuracy.");
    }
    private void RenderRatingDetails()
    {
        var model=_ratingModel;var baseline=DefaultRatingModel.IsBaseline(model);var roundThree=BuiltInRatingModel.IsRoundThree(model);
        var panel=new StackPanel();panel.Children.Add(Text(roundThree?"第三轮 RT · RS 原算法（实验）":baseline?"原版基础估算 / 未校准":"导入模型估算 / 未校准",18,Ink,FontWeights.SemiBold));
        if(_scoringRestoreNotice is not null)panel.Children.Add(Text(_scoringRestoreNotice,12,Brush("#E3BF72")));
        if(roundThree)
        {
            panel.Children.Add(Text("RT 使用第三轮训练参数，RS 沿用原算法。无需配置 AI 或导入模型。",13,Muted));
            panel.Children.Add(Text("4 场新赛事、40 条记录：RT 平均误差 0.083；比第二轮低 6.1%。四场中两场改善、两场退步，仍为实验估算。",12,Muted));
        }
        else if(baseline)
        {
            panel.Children.Add(Text("公开权重与部分胜率数据 + 本软件假设；尚未校准，不代表官方 Rating 3.0。",13,Muted));
            panel.Children.Add(Text($"组合权重：击杀 {model.Weights[0]:0%} · 伤害 {model.Weights[1]:0%} · 存活 {model.Weights[2]:0%} · KAST {model.Weights[3]:0%} · 多杀 {model.Weights[4]:0%} · Swing {model.Weights[5]:0%}。",12,Muted));
            panel.Children.Add(Text("胜率参考采用 2025 全局全装备人数表与 2026 地图 5v5 起点；装备、下包和标准化为本软件假设，未取得完整官方概率表。",12,Muted));
        }
        else panel.Children.Add(Text("使用已导入模型的冻结参数；本场必要事实不足时显示不可用，不自动替换评分模型。",13,Muted));
        panel.Children.Add(new Expander{Header="查看模型版本、参数与来源说明",Content=Text($"模型：{model.ModelVersion}\n规则参考日期：{model.TargetDate}\n模型 SHA-256：{model.ModelId}\n{model.ParameterDisclosure}",12,Muted),Foreground=Ink,Margin=new Thickness(0,8,0,8)});
        if(model.Validation is { } validation)
        {
            var m=validation.Rating;panel.Children.Add(Text(m.Count>0?$"模型附带的整场分组验证：{m.Count} 条对照 · MAE {m.Mae:0.000} · RMSE {m.Rmse:0.000} · 偏差 {m.Bias:+0.000;-0.000;0.000}。为导入模型的参考集结果，未验证其对本场的精度。":"模型没有可用的留出评分对照。",12,Muted));
        }
        var result=_ratingResults.FirstOrDefault(r=>r.Side=="all");
        if(result is not null)
        {
            var axes=new WrapPanel();foreach(var axis in result.Axes)
            {
                var title=axis.Key switch{"kills"=>"击杀","damage"=>"伤害","survival"=>"存活","kast"=>"KAST","multiKills"=>"多杀","swing"=>"胜率贡献",_=>axis.Key};
                axes.Children.Add(Text($"{title}：{(axis.Value?.ToString("0.00")??"—")}    ",13,Accent));
            }
            panel.Children.Add(axes);
            var reasons=result.UnavailableReasons.Distinct().ToList();foreach(var reason in reasons.Take(3))panel.Children.Add(Text(reason,12,Muted));
            if(reasons.Count>3)
            {
                var missing=new StackPanel();foreach(var reason in reasons)missing.Children.Add(Text(reason,12,Muted));
                panel.Children.Add(new Expander{Header=$"查看全部不可用原因（{reasons.Count} 条）",Content=missing,Foreground=Ink});
            }
            var warnings=result.Warnings.Distinct().ToList();
            if(warnings.Count>0)
            {
                var limits=new StackPanel();foreach(var warning in warnings)limits.Children.Add(Text(warning,12,Muted));
                panel.Children.Add(new Expander{Header=$"查看估算限制与回合说明（{warnings.Count} 条）",Content=limits,Foreground=Ink});
            }
            var details=new StackPanel();
            foreach(var credit in result.Credits.Where(c=>c.PlayerCredits.ContainsKey(_analysis!.PlayerId)).Take(40))
            {
                var contribution=credit.PlayerCredits[_analysis!.PlayerId]*100;
                var link=Button($"R{credit.Round} · {credit.Time:0.00}s · 估算贡献 {contribution:+0.00;-0.00;0.00} 个百分点",(_,_)=>{_roundNumber=credit.Round;_selectedEventId=credit.EventId;_section="回合复盘";RenderPage();},false);link.HorizontalAlignment=HorizontalAlignment.Left;details.Children.Add(link);details.Children.Add(Text(credit.Reason,11,Muted));
            }
            if(details.Children.Count>0)panel.Children.Add(new Expander{Header="查看事件贡献依据（前 40 条；完整记录可导出）",Content=details,Foreground=Ink});
        }
        var actions=new WrapPanel();
        var button=Button("导入评分模型",async(_,_)=>await ImportRatingModelAsync(),false);button.IsEnabled=_operation is null;actions.Children.Add(button);
        var restore=Button("恢复内置估算",async(_,_)=>await RestoreDefaultRatingAsync(),false);restore.IsEnabled=_operation is null;actions.Children.Add(restore);
        var original=Button("使用原版基础估算",async(_,_)=>await RestoreDefaultRatingAsync(useOriginalBaseline:true),false);original.IsEnabled=_operation is null;actions.Children.Add(original);
        panel.Children.Add(actions);_page.Children.Add(Panel(panel));
    }
}
