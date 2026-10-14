using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CS2Review.Core;

public static class RatingReferenceLoader
{
    public static async Task<(RatingReferenceManifest Manifest,List<RatingTrainingSample> Samples)> LoadAsync(string manifestPath,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        var full=Path.GetFullPath(manifestPath);var directory=Path.GetDirectoryName(full)!;
        var json=await ReadBounded(full,16*1024*1024,ct);RatingModelCodec.RejectDuplicateProperties(Encoding.UTF8.GetString(json));
        RatingReferenceManifest manifest;
        try{manifest=JsonSerializer.Deserialize<RatingReferenceManifest>(json,RatingModelCodec.Json)??throw new ArgumentException("参考清单为空。");}
        catch(JsonException ex){throw new ArgumentException("参考清单 JSON 无效或含未知字段。",ex);}
        if(manifest.Matches is null)throw new ArgumentException("参考比赛列表为空。");
        var samples=new List<RatingTrainingSample>();
        foreach(var reference in manifest.Matches)
        {
            ct.ThrowIfCancellationRequested();
            if(reference is null||string.IsNullOrWhiteSpace(reference.CanonicalMatchPath))throw new ArgumentException("参考比赛文件路径缺失。");
            var bytes=await ReadBounded(Path.GetFullPath(reference.CanonicalMatchPath,directory),256*1024*1024,ct);
            var hash=RatingModelCodec.Sha256(bytes);
            if(!string.Equals(hash,reference.CanonicalSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("规范比赛文件 SHA-256 不匹配。");
            RatingModelCodec.RejectDuplicateProperties(Encoding.UTF8.GetString(bytes));
            MatchData match;
            try{match=JsonSerializer.Deserialize<MatchData>(bytes,RatingModelCodec.Json)??throw new ArgumentException("比赛为空。");}
            catch(JsonException ex){throw new ArgumentException("规范比赛 JSON 无效或含未知字段。",ex);}
            var sample=new RatingTrainingSample{Reference=reference,Match=match,CanonicalFileSha256=hash};
            if(reference.FactsPath is not null)
            {
                var data=await ReadBounded(Path.GetFullPath(reference.FactsPath,directory),256*1024*1024,ct);
                RatingModelCodec.RejectDuplicateProperties(Encoding.UTF8.GetString(data));
                sample.FactsFileSha256=RatingModelCodec.Sha256(data);
                if(!string.Equals(sample.FactsFileSha256,reference.FactsSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("补充事实文件 SHA-256 不匹配。");
                try{sample.Facts=JsonSerializer.Deserialize<RatingMatchFacts>(data,RatingModelCodec.Json)??throw new ArgumentException("补充事实为空。");}
                catch(JsonException ex){throw new ArgumentException("补充事实 JSON 无效。",ex);}
            }
            samples.Add(sample);
        }
        Validate(manifest,samples,ct);return(manifest,samples);
    }
    private static async Task<byte[]> ReadBounded(string path,int maximum,CancellationToken ct)
    {
        if(new FileInfo(path).Length>maximum)throw new ArgumentException("参考文件超出大小限制。");
        var bytes=await File.ReadAllBytesAsync(path,ct);if(bytes.Length>maximum)throw new ArgumentException("参考文件超出大小限制。");return bytes;
    }
    public static void Validate(RatingReferenceManifest manifest,IReadOnlyList<RatingTrainingSample> samples,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(manifest is null||manifest.SchemaVersion!=1||string.IsNullOrWhiteSpace(manifest.ReferenceId)||string.IsNullOrWhiteSpace(manifest.Provenance)||manifest.TargetDate!="2026-10-01"||manifest.Matches is null||manifest.Matches.Count==0||manifest.Matches.Count!=samples.Count)throw new ArgumentException("参考清单版本/目标/来源/样本数无效。");
        if(samples.Any(s=>s is null||s.Reference is null||s.Match is null)||manifest.Matches.Any(r=>r is null))throw new ArgumentException("参考样本为空。");
        if(manifest.Matches.GroupBy(r=>r.SourceSha256,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new ArgumentException("同一源 demo 不能重复或分配到不同整场组。");
        var references=manifest.Matches.ToDictionary(r=>r.SourceSha256,StringComparer.OrdinalIgnoreCase);
        foreach(var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            var r=sample.Reference;var m=sample.Match;
            if(!RatingModelCodec.IsSha(r.SourceSha256)||!RatingModelCodec.IsSha(r.CanonicalSha256)||!string.Equals(m.SourceSha256,r.SourceSha256,StringComparison.OrdinalIgnoreCase)||!string.Equals(sample.CanonicalFileSha256,r.CanonicalSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("demo SHA 与规范文件 SHA 必须分别匹配。");
            if(!references.TryGetValue(r.SourceSha256,out var original)||JsonSerializer.Serialize(original,RatingModelCodec.Json)!=JsonSerializer.Serialize(r,RatingModelCodec.Json))throw new ArgumentException("样本与参考清单不一致。");
            if(string.IsNullOrWhiteSpace(r.GroupId)||string.IsNullOrWhiteSpace(r.CanonicalMatchPath)||r.MapName!=m.MapName)throw new ArgumentException("整场分组/路径/地图不匹配。");
            if(m.Players is null||m.Rounds is null||m.Players.Any(p=>p is null)||m.Rounds.Any(round=>round is null||round.Participants is null||round.Participants.Any(p=>p is null)||round.Events is null||round.Events.Any(e=>e is null)||round.EconomySnapshots is null||round.EconomySnapshots.Any(p=>p is null)||round.Survivors is null)||m.Players.GroupBy(p=>p.Id).Any(g=>g.Count()!=1))throw new ArgumentException("规范比赛参与身份或必要集合无效。");
            if(sample.Facts is not null&&(sample.Facts.SchemaVersion!=1||sample.Facts.Frames is null||sample.Facts.Frames.Any(f=>f is null||f.Players is null||f.Players.Any(p=>p is null))||!string.Equals(sample.Facts.SourceSha256,m.SourceSha256,StringComparison.OrdinalIgnoreCase)||!RatingModelCodec.IsSha(r.FactsSha256)||!string.Equals(r.FactsSha256,sample.FactsFileSha256,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("补充事实身份或文件 SHA 不匹配。");
            if((r.FactsPath is null)!=(sample.Facts is null))throw new ArgumentException("补充事实声明与实际材料不一致。");
            var snapshot=r.Snapshot;
            var target=DateOnly.ParseExact(manifest.TargetDate,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(snapshot is null||snapshot.TargetDate!=manifest.TargetDate||DateOnly.FromDateTime(snapshot.CapturedAtUtc.UtcDateTime)<target||snapshot.CapturedAtUtc> DateTimeOffset.UtcNow.AddMinutes(5)||snapshot.MapStatsId<=0||!Uri.TryCreate(snapshot.SourceUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||snapshot.Players is null||snapshot.Players.Count==0)throw new ArgumentException("官方快照日期/目标/来源无效；比赛日期不能代替评分捕获日期。");
            if(snapshot.Players.Any(p=>p is null)||snapshot.Players.GroupBy(p=>(p.PlayerId,p.Side)).Any(g=>g.Count()!=1))throw new ArgumentException("官方玩家/阵营行重复或为空。");
            foreach(var p in snapshot.Players)
            {
                ct.ThrowIfCancellationRequested();
                if(p is null||p.Side is not ("all" or "T" or "CT")||!m.Players.Any(x=>x.Id==p.PlayerId)||string.IsNullOrWhiteSpace(p.SteamId)||!p.SteamId.All(c=>c is >= '0' and <= '9')||!ulong.TryParse(p.SteamId,out var steam)||steam==0||!double.IsFinite(p.Rating)||p.Rating<0||p.Rating>10||p.Swing is double swing&&(!double.IsFinite(swing)||Math.Abs(swing)>100))throw new ArgumentException("官方玩家映射/阵营/评分无效。");
                var a=new AnalysisEngine().Analyze(m,p.PlayerId,ct);var raw=p.Side=="T"?a.T:p.Side=="CT"?a.CT:a.Summary;
                if(p.PlayedRounds<=0||p.PlayedRounds!=raw.Rounds)throw new ArgumentException("官方参与回合数与事实不一致。");
                if(p.Kills is int kills&&kills!=raw.Kills||p.Deaths is int deaths&&deaths!=raw.Deaths||p.Adr is double adr&&(!double.IsFinite(adr)||Math.Abs(adr-raw.Adr)>.11)||p.KastPercent is double kast&&(!double.IsFinite(kast)||Math.Abs(kast-raw.ScoringKast)>.11))throw new ArgumentException("参考原始统计与事实不一致，必须先核对 parser/40伤害评分 KAST 口径。");
            }
        }
    }
}

public sealed class RatingTrainer
{
    public RatingModel Train(RatingReferenceManifest manifest,IReadOnlyList<RatingTrainingSample> samples,RatingTrainingOptions? options=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();RatingReferenceLoader.Validate(manifest,samples,ct);var opts=options??new();ValidateOptions(opts);
        var model=new RatingModel{TrainingManifestSha256=RatingModelCodec.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest,RatingModelCodec.Json))),CalibrationStatus="experimental",FeatureVersion=opts.FeatureVersion,ProbabilityPriorWeight=opts.ProbabilityPriorWeight,MinimumMapObservations=opts.MinimumMapObservations,KillerShare=opts.KillerShare,DamageShare=opts.DamageShare,FlashShare=opts.FlashShare,TradeShare=opts.TradeShare,TrainingGroups=samples.Select(s=>s.Reference.GroupId).Distinct().Order(StringComparer.Ordinal).ToList(),Weights=Enumerable.Repeat(1d/6,6).ToArray()};
        var collectingModel=RatingModelCodec.Seal(model);var counts=new Dictionary<string,RatingProbabilityCell>();
        foreach(var sample in samples.OrderBy(s=>s.Reference.SourceSha256,StringComparer.Ordinal))
        foreach(var round in sample.Match.Rounds.Where(RatingEngine.ValidRound))
        {
            ct.ThrowIfCancellationRequested();
            var evaluated=RatingRoundEvaluator.Evaluate(sample.Match,round,collectingModel,sample.Facts,ct);
            if(evaluated.Reasons.Count>0)continue;
            // One observed feature cell per round, regardless of how many shots/damage/events exposed it.
            foreach(var state in evaluated.ObservedStates.DistinctBy(RatingRoundEvaluator.Key))
            foreach(var map in new[]{state.Map,"global"}.Distinct(StringComparer.Ordinal))
            {
                var cell=new RatingProbabilityCell{Map=map,CtAlive=state.CtAlive,TAlive=state.TAlive,CtEquipmentBand=state.CtEquipmentBand,TEquipmentBand=state.TEquipmentBand,Planted=state.Planted,ClockBand=state.ClockBand};
                var key=RatingRoundEvaluator.Key(cell);
                if(!counts.TryGetValue(key,out var count))counts[key]=count=cell;
                count.Observations++;count.CtWins+=state.CtWins;
            }
        }
        if(counts.Count==0)throw new ArgumentException("没有包含可靠起始健康/装备/时钟的实际参考回合，不能训练概率。");
        model.ProbabilityCells=counts.Values.ToList();model=RatingModelCodec.Seal(model);
        var rows=new List<(double[] Features,double Expected)>();
        foreach(var sample in samples.OrderBy(s=>s.Reference.SourceSha256,StringComparer.Ordinal))
        foreach(var label in sample.Reference.Snapshot.Players.OrderBy(p=>p.PlayerId,StringComparer.Ordinal).ThenBy(p=>p.Side,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();var score=new RatingEngine().Score(sample.Match,label.PlayerId,model,sample.Facts,label.Side,ct);
            if(score.FeatureValues is null)throw new ArgumentException("参考评分行缺少必要状态："+string.Join("；",score.UnavailableReasons));
            rows.Add((score.FeatureValues,label.Rating));
        }
        if(rows.Count<2)throw new ArgumentException("综合拟合至少需要两条有效参考玩家行。");
        for(var axis=0;axis<6;axis++)
        {
            model.AxisCenters[axis]=rows.Average(r=>r.Features[axis]);
            var variance=rows.Average(r=>Math.Pow(r.Features[axis]-model.AxisCenters[axis],2));
            model.AxisScales[axis]=Math.Sqrt(variance);if(model.AxisScales[axis]<1e-9)model.AxisScales[axis]=1;
        }
        var x=rows.Select(r=>r.Features.Select((v,i)=>1+(v-model.AxisCenters[i])/model.AxisScales[i]).ToArray()).ToArray();
        var initial=Enumerable.Repeat(1d/6,6).ToArray();model.Weights=initial.ToArray();model.Intercept=rows.Average(r=>r.Expected)-1;
        for(var iteration=0;iteration<opts.Iterations;iteration++)
        {
            ct.ThrowIfCancellationRequested();var gradient=new double[6];var biasGradient=0d;
            for(var row=0;row<rows.Count;row++)
            {
                var prediction=model.Intercept+x[row].Select((v,i)=>v*model.Weights[i]).Sum();var error=prediction-rows[row].Expected;
                biasGradient+=2*error/rows.Count;
                for(var axis=0;axis<6;axis++)gradient[axis]+=2*error*x[row][axis]/rows.Count;
            }
            model.Intercept-=opts.LearningRate*biasGradient;
            for(var axis=0;axis<6;axis++)model.Weights[axis]=Math.Max(0,model.Weights[axis]-opts.LearningRate*(gradient[axis]+2*opts.Ridge*(model.Weights[axis]-initial[axis])));
            if(!double.IsFinite(model.Intercept)||model.Weights.Any(v=>!double.IsFinite(v)))throw new ArgumentException("拟合发散；需要降低学习率或核对参考数据。");
        }
        if(model.Weights.Sum()<=1e-12)throw new ArgumentException("拟合产生退化零权重，不能冻结评分模型。");
        model.ParameterDisclosure=$"自身实验模型；"+(opts.FeatureVersion=="freeze-equipment-value-proxy-v1"?"冻结结束总装备价格代理（概率、经济击杀/伤害、生存/KAST统一；不含回合内拾取/丢弃）":"绑定冻结和交战前时刻的最贵持有武器加护甲事实")+$"；每回合同状态去重；Beta先验；地图/全局仅按起始条件和全phase覆盖整回合固定；伤害按实际HP份额；缺少flash/trade份额回归killer；回合末按最后eligible kill after-state概率分配给符合条件击杀正贡献者、败方存活玩家等分负值；炸弹终局只停止以后事件不抹去已赚贡献；负killer修正保留victim绝对代价；六轴训练集z变换；非负ridge梯度拟合（iterations={opts.Iterations},lr={opts.LearningRate.ToString("R",CultureInfo.InvariantCulture)},ridge={opts.Ridge.ToString("R",CultureInfo.InvariantCulture)}）。来源状态/官方分配差异均未证明HLTV精度。";
        return RatingModelCodec.Seal(model);
    }
    public RatingValidationReport ValidateLeaveOneMatchOut(RatingReferenceManifest manifest,IReadOnlyList<RatingTrainingSample> samples,RatingTrainingOptions? options=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();RatingReferenceLoader.Validate(manifest,samples,ct);
        var groups=samples.Select(s=>s.Reference.GroupId).Distinct().Order(StringComparer.Ordinal).ToList();
        if(groups.Count<2)throw new ArgumentException("整场留出至少需要两个独立比赛组。");
        var report=new RatingValidationReport();var ratingRows=new List<(double Expected,double Predicted)>();var swingRows=new List<(double Expected,double Predicted)>();var probabilityRows=new List<(double Expected,double Predicted)>();
        foreach(var group in groups)
        {
            ct.ThrowIfCancellationRequested();var train=samples.Where(s=>s.Reference.GroupId!=group).ToList();var held=samples.Where(s=>s.Reference.GroupId==group).ToList();
            var subset=new RatingReferenceManifest{ReferenceId=manifest.ReferenceId+"-without-"+group,TargetDate=manifest.TargetDate,Provenance=manifest.Provenance,Matches=train.Select(s=>s.Reference).ToList()};
            // Train is called inside the fold: probabilities, normalization, and coefficients all exclude held-out data.
            var model=Train(subset,train,options,ct);var fold=new RatingValidationFold{HeldOutGroup=group,TrainingGroups=model.TrainingGroups,ModelId=model.ModelId,FrozenModel=model};
            var probability=new List<(double Expected,double Predicted)>();
            foreach(var sample in held)foreach(var round in sample.Match.Rounds.Where(RatingEngine.ValidRound))
            {
                ct.ThrowIfCancellationRequested();var evaluated=RatingRoundEvaluator.Evaluate(sample.Match,round,model,sample.Facts,ct);
                if(evaluated.Reasons.Count==0)probability.AddRange(evaluated.ProbabilityPredictions.Values);
            }
            var rating=new List<(double Expected,double Predicted)>();var swing=new List<(double Expected,double Predicted)>();
            foreach(var sample in held)foreach(var label in sample.Reference.Snapshot.Players)
            {
                ct.ThrowIfCancellationRequested();var score=new RatingEngine().Score(sample.Match,label.PlayerId,model,sample.Facts,label.Side,ct);
                if(score.Overall is not double overall){fold.UnavailableRows++;continue;}
                rating.Add((label.Rating,overall));
                if(label.Swing is double expected&&score.RoundSwing is double actual)swing.Add((expected,actual));
            }
            fold.Rating=Measure(rating);fold.Swing=Measure(swing);var pm=ProbabilityMetrics(probability);fold.ProbabilityCount=pm.Count;fold.ProbabilityBrier=pm.Brier;fold.ProbabilityLogLoss=pm.LogLoss;report.Folds.Add(fold);ratingRows.AddRange(rating);swingRows.AddRange(swing);probabilityRows.AddRange(probability);
        }
        report.Rating=Measure(ratingRows);report.Swing=Measure(swingRows);
        var allProbability=ProbabilityMetrics(probabilityRows);report.ProbabilityCount=allProbability.Count;report.ProbabilityBrier=allProbability.Brier;report.ProbabilityLogLoss=allProbability.LogLoss;
        report.Warnings.Add($"只有 {groups.Count} 个独立比赛组；探索性验证不能证明稳定泛化、官方等价或个人平台误差。");
        report.Warnings.Add("Rating误差带以评分单位计；Swing误差以百分点计；无官方Swing标签时Count=0且误差为null。");
        return report;
    }
    private static void ValidateOptions(RatingTrainingOptions o)
    {
        if(o.Iterations<1||o.Iterations>100000||!double.IsFinite(o.LearningRate)||o.LearningRate<=0||o.LearningRate>.1||!double.IsFinite(o.Ridge)||o.Ridge<0||o.Ridge>100||o.MinimumMapObservations<=0||!double.IsFinite(o.ProbabilityPriorWeight)||o.ProbabilityPriorWeight<=0)throw new ArgumentException("训练参数无效。");
    }
    private static (int Count,double? Brier,double? LogLoss) ProbabilityMetrics(List<(double Expected,double Predicted)> rows)
    {
        if(rows.Count==0)return(0,null,null);
        return(rows.Count,rows.Average(r=>Math.Pow(r.Predicted-r.Expected,2)),rows.Average(r=>{var p=Math.Clamp(r.Predicted,1e-12,1-1e-12);return-(r.Expected*Math.Log(p)+(1-r.Expected)*Math.Log(1-p));}));
    }
    private static RatingErrorMetrics Measure(List<(double Expected,double Predicted)> rows)
    {
        var m=new RatingErrorMetrics{Count=rows.Count};if(rows.Count==0)return m;
        var error=rows.Select(r=>r.Predicted-r.Expected).ToArray();var abs=error.Select(Math.Abs).Order().ToArray();
        double Percentile(double q){var p=(abs.Length-1)*q;var lo=(int)Math.Floor(p);var hi=(int)Math.Ceiling(p);return abs[lo]+(abs[hi]-abs[lo])*(p-lo);}
        m.Mae=abs.Average();m.Rmse=Math.Sqrt(error.Average(v=>v*v));m.Bias=error.Average();m.P50=Percentile(.5);m.P90=Percentile(.9);m.P95=Percentile(.95);
        m.Within05=(double)abs.Count(v=>v<=.05)/abs.Length;m.Within10=(double)abs.Count(v=>v<=.10)/abs.Length;m.Within20=(double)abs.Count(v=>v<=.20)/abs.Length;
        double[] Ranks(double[] values){var ranked=new double[values.Length];var sorted=values.Select((v,i)=>(Value:v,Index:i)).OrderBy(x=>x.Value).ToArray();for(var i=0;i<sorted.Length;){var end=i+1;while(end<sorted.Length&&sorted[end].Value==sorted[i].Value)end++;for(var j=i;j<end;j++)ranked[sorted[j].Index]=(i+end-1)/2d;i=end;}return ranked;}
        var a=Ranks(rows.Select(r=>r.Expected).ToArray());var b=Ranks(rows.Select(r=>r.Predicted).ToArray());var am=a.Average();var bm=b.Average();var denominator=Math.Sqrt(a.Sum(v=>Math.Pow(v-am,2))*b.Sum(v=>Math.Pow(v-bm,2)));
        m.Spearman=denominator>0?a.Select((v,i)=>(v-am)*(b[i]-bm)).Sum()/denominator:null;
        return m;
    }
}
