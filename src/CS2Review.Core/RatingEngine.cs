namespace CS2Review.Core;

public sealed class RatingEngine
{
    public RatingResult Score(MatchData match, string playerId, RatingModel? model = null, RatingMatchFacts? enrichedFacts = null, string side = "all", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        ct.ThrowIfCancellationRequested();
        if (side is not ("all" or "T" or "CT")) throw new ArgumentException("阵营必须为 all、T 或 CT。", nameof(side));
        var analysis = new AnalysisEngine().Analyze(match, playerId, ct);
        var raw = side == "T" ? analysis.T : side == "CT" ? analysis.CT : analysis.Summary;
        var result = new RatingResult { PlayerId = playerId, Side = side, Raw = raw, PlayedRounds = raw.Rounds, ScoringKastPercent = raw.Rounds > 0 ? raw.ScoringKast : null };
        if (model is null) { result.UnavailableReasons.Add("尚未导入已冻结的评分模型；需要真实参考回合、装备/时钟事实与目标日期的官方评分快照进行校准。"); return result; }
        result.ModelId = model.ModelId; result.TargetDate = model.TargetDate; result.CalibrationStatus = model.CalibrationStatus;
        if(!RatingModelCodec.IsSha(match.SourceSha256)){result.UnavailableReasons.Add("源 demo SHA-256 身份无效。");return result;}
        try { RatingModelCodec.Validate(model); }
        catch (ArgumentException ex) { result.UnavailableReasons.Add("模型无效：" + ex.Message); return result; }
        if (enrichedFacts is not null && (enrichedFacts.SchemaVersion != 1 || enrichedFacts.Frames is null || enrichedFacts.Frames.Any(f=>f is null||string.IsNullOrWhiteSpace(f.EventId)||f.Players is null) || !string.Equals(enrichedFacts.SourceSha256, match.SourceSha256, StringComparison.OrdinalIgnoreCase)))
        { result.UnavailableReasons.Add("补充状态的源 demo SHA 或版本不匹配。"); return result; }
        if (raw.Rounds == 0) { result.UnavailableReasons.Add("没有可靠的实际参与计分回合。"); return result; }
        var totals = new double[6]; var kast = 0;
        foreach (var round in match.Rounds.Where(r => ValidRound(r) && r.Participants.Any(p => p.PlayerId == playerId && (side == "all" || p.Side == side))))
        {
            ct.ThrowIfCancellationRequested();
            var evaluation = RatingRoundEvaluator.Evaluate(match, round, model, enrichedFacts, ct);
            result.Credits.AddRange(evaluation.Credits);
            result.UnavailableReasons.AddRange(evaluation.Reasons);
            result.Warnings.AddRange(evaluation.Warnings);
            if (!evaluation.Features.TryGetValue(playerId, out var values)) { result.UnavailableReasons.Add($"回合 {round.Number} 缺少可靠参与/起始状态。"); continue; }
            for (var i=0;i<6;i++) totals[i] += values[i];
            if(evaluation.ScoringKast.Contains(playerId)) kast++;
        }
        result.UnavailableReasons = result.UnavailableReasons.Distinct().ToList();
        result.Warnings = result.Warnings.Distinct().ToList();
        if(result.UnavailableReasons.Count==0) result.ScoringKastPercent = 100d * kast / raw.Rounds;
        if (result.UnavailableReasons.Count > 0) return result;
        var features = totals.Select(v => v / raw.Rounds).ToArray();
        var names = new[] { "kills", "damage", "survival", "kast", "multiKills", "swing" };
        var axes = features.Select((v,i) => 1 + (v-model.AxisCenters[i])/model.AxisScales[i]).ToArray();
        var overall=Math.Max(0, model.Intercept + axes.Select((v,i) => v*model.Weights[i]).Sum());
        if(features.Any(v=>!double.IsFinite(v))||axes.Any(v=>!double.IsFinite(v))||!double.IsFinite(overall)||!double.IsFinite(features[5]*100))
        {result.UnavailableReasons.Add("模型计算产生非有限数值；需要核对标准化尺度、权重与输入事实。");return result;}
        result.FeatureValues = features; result.RoundSwing = features[5] * 100;
        for(var i=0;i<6;i++) result.Axes[names[i]] = axes[i];
        result.Overall = overall;
        result.Warnings.Add("模型估算："+(model.FeatureVersion=="freeze-equipment-value-proxy-v1"?"冻结装备总价代理（不反映回合内捡枪/丢枪）":"经绑定的交战前最贵武器和护甲事实")+"、贡献比例和标准化参数为自身重建；官方未公开参数不能视为已复刻。");
        return result;
    }
    internal static bool ValidRound(RoundData r) => r.IsValid && r.Winner is "T" or "CT" && double.IsFinite(r.StartedAt) && double.IsFinite(r.EndedAt) && r.EndedAt >= r.StartedAt;
}

internal sealed class RatingRoundEvaluation
{
    public Dictionary<string,double[]> Features { get; } = [];
    public HashSet<string> ScoringKast { get; } = [];
    public List<RatingEventCredit> Credits { get; } = [];
    public List<string> Reasons { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<RatingProbabilityCell> ObservedStates { get; } = [];
    public Dictionary<string,(double Expected,double Predicted)> ProbabilityPredictions { get; } = [];
}

internal static class RatingRoundEvaluator
{
    private sealed record FatalTransition(RatingProbabilityCell Before,RatingProbabilityCell After,string? ActorId,string EventId,double Time,int Tick,int Order,bool Defusing,bool SwingOpen,bool CtHasKit,bool CtKitUnknown);
    internal static int Band(int value, RatingModel m) { var i=0; while(i<m.EquipmentBandUpperBounds.Length && value>m.EquipmentBandUpperBounds[i]) i++; return i; }
    internal static string Key(RatingProbabilityCell c) => $"{c.Map}|{c.CtAlive}|{c.TAlive}|{c.CtEquipmentBand}|{c.TEquipmentBand}|{c.Planted}|{c.ClockBand}";
    internal static RatingRoundEvaluation Evaluate(MatchData match, RoundData round, RatingModel model, RatingMatchFacts? facts, CancellationToken ct = default)
    {
        var result=new RatingRoundEvaluation();
        ct.ThrowIfCancellationRequested();
        void Missing(string detail) => result.Reasons.Add($"回合 {round.Number}：{detail}");
        var grouped=round.Participants.GroupBy(p=>p.PlayerId).ToList();
        if(grouped.Any(g=>g.Count()!=1 || string.IsNullOrWhiteSpace(g.Key) || g.First().Side is not ("T" or "CT"))) { Missing("参与身份或阵营不唯一。"); return result; }
        var sides=grouped.ToDictionary(g=>g.Key,g=>g.Single().Side);
        if(!sides.Values.Contains("T") || !sides.Values.Contains("CT") || sides.Values.Count(s=>s=="T")>5 || sides.Values.Count(s=>s=="CT")>5) { Missing("双方参与人数缺失或超出模型范围。"); return result; }
        var freeze=round.FreezeEndedAt;
        if(freeze is null || !double.IsFinite(freeze.Value) || freeze<round.StartedAt || freeze>round.EndedAt) { Missing("缺少可靠冻结结束时间。"); return result; }
        var snapshots=round.EconomySnapshots.Where(s=>sides.TryGetValue(s.PlayerId,out var side)&&s.Side==side&&double.IsFinite(s.Time)&&Math.Abs(s.Time-freeze.Value)<.0001).GroupBy(s=>s.PlayerId).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single());
        if(sides.Keys.Any(p=>!snapshots.TryGetValue(p,out var s)||s.Health is null or <0 or >1000||s.EquipmentValue is null or <0 or >100000)) { Missing("起始存活健康或装备事实不完整。"); return result; }
        var freezeTick=snapshots.Values.Max(s=>s.Tick);
        var alive=sides.Keys.Where(p=>snapshots[p].Health>0).ToHashSet();
        var health=sides.Keys.ToDictionary(p=>p,p=>snapshots[p].Health!.Value);
        var kits=sides.Keys.ToDictionary(p=>p,p=>snapshots[p].DefuseKit);
        var equip=sides.Keys.ToDictionary(p=>p,p=>snapshots[p].EquipmentValue!.Value);
        bool ValidFrame(RatingStateFrame f,int tick,double time) => f.Tick==tick&&double.IsFinite(f.Time)&&Math.Abs(f.Time-time)<.0001&&f.Players is not null&&f.Players.Count==sides.Count&&f.Players.All(p=>p is not null&&!string.IsNullOrWhiteSpace(p.PlayerId)&&sides.TryGetValue(p.PlayerId,out var side)&&side==p.Side&&p.Alive is not null)&&f.Players.GroupBy(p=>p.PlayerId).All(g=>g.Count()==1);
        int? StrongestPrice(RatingPlayerState p) => p.StrongestWeaponPrice is >=0 and <=100000&&p.ArmorPrice is >=0 and <=100000&&p.StrongestWeaponPrice+p.ArmorPrice<=100000?p.StrongestWeaponPrice+p.ArmorPrice:null;
        if(model.FeatureVersion=="strongest-weapon-armor-v1")
        {
            var freezeFrames=facts?.Frames.Where(f=>f.Round==round.Number&&f.EventId==$"round-{round.Number}-freeze").ToList();
            var f=freezeFrames is {Count:1}?freezeFrames[0]:null;
            if(f is null||!ValidFrame(f,freezeTick,freeze.Value)||f.Players.Any(p=>p.Alive!=alive.Contains(p.PlayerId)||StrongestPrice(p) is null))
            {Missing("缺少绑定冻结时刻的最贵持有武器与护甲价格事实。");return result;}
            foreach(var p in f.Players)equip[p.PlayerId]=StrongestPrice(p)!.Value;
        }
        var kills=sides.Keys.ToDictionary(p=>p,_=>0);
        var damageByVictim=new Dictionary<string,Dictionary<string,(int Damage,List<string> Ids)>>();
        var killed=new HashSet<string>(); var assists=new HashSet<string>(); var traded=new HashSet<string>();
        var knownDead=sides.Keys.Where(p=>health[p]==0).ToHashSet();
        var pendingFatal=new Dictionary<string,FatalTransition>();
        var previousKills=new List<GameEvent>();
        foreach(var p in sides.Keys)result.Features[p]=new double[6];
        var ctBand=Band((int)sides.Keys.Where(p=>sides[p]=="CT").Average(p=>equip[p]),model);
        var tBand=Band((int)sides.Keys.Where(p=>sides[p]=="T").Average(p=>equip[p]),model);
        var cells=model.ProbabilityCells.ToDictionary(Key);
        var framesByEvent=facts?.Frames.Where(f=>f.Round==round.Number).GroupBy(f=>f.EventId).ToDictionary(g=>g.Key,g=>g.ToList());
        // Choose once using only freeze facts. Future plants must never alter an earlier kill's table.
        // Our conservative coverage requirement includes every possible phase, even if no plant occurs.
        bool MapCovered()
        {
            for(var ct=0;ct<=sides.Values.Count(s=>s=="CT");ct++)for(var t=0;t<=sides.Values.Count(s=>s=="T");t++)
            foreach(var planted in new[]{false,true})
            foreach(var clock in planted?new[]{1,2,3}:new[]{0})
                if(!cells.TryGetValue(Key(new(){Map=match.MapName,CtAlive=ct,TAlive=t,CtEquipmentBand=ctBand,TEquipmentBand=tBand,Planted=planted,ClockBand=clock}),out var c)||c.Observations<model.MinimumMapObservations)return false;
            return true;
        }
        var baseline=DefaultRatingModel.IsBaseline(model);
        var roundThree=BuiltInRatingModel.IsRoundThree(model);
        var usesBaselineProbability=baseline||roundThree;
        var probabilityMap=usesBaselineProbability?match.MapName:MapCovered()?match.MapName:"global";
        result.Warnings.Add(roundThree?"第三轮 RT 已以12个系列、120条真实官方记录训练；RS 保留原内置基线，公开人数表与5v5地图锚点、经济和下包假设未变。模型仍为实验性估算。":baseline?"内置基础估算：公开旧全局人数表与新5v5地图锚点；经济、下包和标准化采用本软件假设，未校准。":$"回合 {round.Number} 固定使用 {probabilityMap} 概率；装备条件为"+(model.FeatureVersion=="freeze-equipment-value-proxy-v1"?"冻结结束总价代理。":"绑定冻结时刻最贵武器与护甲价格。"));
        double? plantedAt=null;string? defuser=null;var ended=false;var terminalRecorded=false;
        RatingProbabilityCell State(double time) => new(){Map=probabilityMap,CtAlive=alive.Count(p=>sides[p]=="CT"),TAlive=alive.Count(p=>sides[p]=="T"),CtEquipmentBand=ctBand,TEquipmentBand=tBand,Planted=plantedAt.HasValue,ClockBand=plantedAt.HasValue&&round.BombTimerSeconds is double timer?Math.Max(0,plantedAt.Value+timer-time) is var remaining&&remaining<5?1:remaining<10?2:3:0};
        double Probability(RatingProbabilityCell c)
        {
            if(usesBaselineProbability)return DefaultRatingModel.CtWinProbability(c,model);
            if(c.CtAlive==0&&c.TAlive>0&&!c.Planted)return 0;
            if(c.TAlive==0&&c.CtAlive>0&&!c.Planted)return 1;
            if(cells.TryGetValue(Key(c),out var cell))return (cell.CtWins+model.ProbabilityPriorWeight*model.ProbabilityPriorCtWin)/(cell.Observations+model.ProbabilityPriorWeight);
            result.Warnings.Add("稀疏全局局面使用冻结的先验概率，尚无该格观测。");
            return model.ProbabilityPriorCtWin;
        }
        void Observe(RatingProbabilityCell c)
        {
            result.ObservedStates.Add(new(){Map=match.MapName,CtAlive=c.CtAlive,TAlive=c.TAlive,CtEquipmentBand=c.CtEquipmentBand,TEquipmentBand=c.TEquipmentBand,Planted=c.Planted,ClockBand=c.ClockBand,Observations=1,CtWins=round.Winner=="CT"?1:0});
            result.ProbabilityPredictions[Key(c)]=(round.Winner=="CT"?1:0,Probability(c));
        }
        bool Reliable(GameEvent e,string id,PlayerState? s) => s is not null&&s.PlayerId==id&&s.Side==sides[id]&&s.Tick==e.Tick&&double.IsFinite(s.Time)&&Math.Abs(s.Time-e.Time)<.0001;
        RatingStateFrame? Frame(GameEvent e)
        {
            if(facts is null)return null;
            var frames=framesByEvent?.GetValueOrDefault(e.Id);
            if(frames is null||frames.Count==0)return null;
            var f=frames.Count==1?frames[0]:null;
            if(f is null||!ValidFrame(f,e.Tick,e.Time)){Missing("补充事件状态身份/时间/阵营不可靠。");return null;}
            return f;
        }
        int? EventEquipment(GameEvent e,string id,PlayerState? state,RatingStateFrame? frame)
        {
            if(frame is not null)
            {
                var p=frame.Players.Single(p=>p.PlayerId==id);
                if(model.FeatureVersion=="strongest-weapon-armor-v1")return StrongestPrice(p);
                return equip[id];
            }
            if(model.FeatureVersion=="strongest-weapon-armor-v1")return null;
            return Reliable(e,id,state)?equip[id]:null;
        }
        var eligibleKills=new List<RatingEventCredit>();
        RatingProbabilityCell last=State(freeze.Value);Observe(last);
        var settlementState=last;
        var settlementTime=freeze.Value;var settlementTick=freezeTick;var settlementOrder=0;var eventOrder=0;
        var seenIds=new HashSet<string>();
        foreach(var e in round.Events.Select((e,i)=>{ct.ThrowIfCancellationRequested();return(Event:e,Index:i);}).Where(x=>x.Event.AfterRoundEnd!=true&&double.IsFinite(x.Event.Time)&&x.Event.Time>=freeze.Value&&x.Event.Time<=round.EndedAt&&(x.Event.Time>freeze.Value||x.Event.Tick>=freezeTick)).OrderBy(x=>x.Event.Time).ThenBy(x=>x.Event.Tick).ThenBy(x=>x.Index).Select(x=>x.Event))
        {
            ct.ThrowIfCancellationRequested();
            eventOrder++;
            if(terminalRecorded)continue;
            if(string.IsNullOrWhiteSpace(e.Id)||!seenIds.Add(e.Id)){Missing("事件 ID 缺失或重复。");continue;}
            if(e.ActorId is not null&&sides.TryGetValue(e.ActorId,out var actorSide)&&e.ActorSide is not null&&e.ActorSide!=actorSide||e.TargetId is not null&&sides.TryGetValue(e.TargetId,out var targetSide)&&e.TargetSide is not null&&e.TargetSide!=targetSide)
            {Missing("事件阵营与当回合参与身份不一致。");continue;}
            var enemy=e.ActorId is not null&&e.TargetId is not null&&sides.ContainsKey(e.ActorId)&&sides.ContainsKey(e.TargetId)&&sides[e.ActorId]!=sides[e.TargetId];
            var frame=Frame(e);
            if(e.ActorId is not null&&sides.ContainsKey(e.ActorId)&&Reliable(e,e.ActorId,e.ActorState))kits[e.ActorId]=e.HasKit??e.ActorState!.DefuseKit;
            if(e.TargetId is not null&&sides.ContainsKey(e.TargetId)&&Reliable(e,e.TargetId,e.TargetState))kits[e.TargetId]=e.TargetState!.DefuseKit;
            if(frame is not null)
            {
                foreach(var p in frame.Players)
                {
                    if(p.Alive==true)
                    {if(knownDead.Contains(p.PlayerId))Missing("补充事实试图复活已确认死亡的参与者。");else alive.Add(p.PlayerId);}
                    else {alive.Remove(p.PlayerId);knownDead.Add(p.PlayerId);health[p.PlayerId]=0;}
                    kits[p.PlayerId]=p.DefuseKit;
                }
                if(defuser is not null&&!alive.Contains(defuser))defuser=null;
            }
            var before=State(e.Time);Observe(before);
            if(e.Kind=="damage"&&e.TargetId is not null&&sides.ContainsKey(e.TargetId)&&alive.Contains(e.TargetId))
            {
                // All damage consumes HP; only enemy damage earns scoring/assist contribution.
                var defusingBefore=defuser is not null;
                var ctKits=alive.Where(p=>sides[p]=="CT").Select(p=>kits[p]).ToList();
                var actual=Math.Clamp(e.Damage,0,health[e.TargetId]);
                var observedHealth=e.TargetHealthAfter is int hp&&hp is >=0 and <=100;
                health[e.TargetId]=observedHealth?e.TargetHealthAfter!.Value:health[e.TargetId]-actual;
                if(!observedHealth&&Reliable(e,e.TargetId,e.TargetState)&&e.TargetState!.Health==0&&health[e.TargetId]>0)
                {Missing("伤害量与可靠致命HP状态不一致。");health[e.TargetId]=0;}
                if(defuser==e.TargetId&&(health[e.TargetId]==0||(!observedHealth&&Reliable(e,e.TargetId,e.TargetState)&&e.TargetState!.Health==0)))defuser=null;
                if(health[e.TargetId]==0)
                {alive.Remove(e.TargetId);knownDead.Add(e.TargetId);pendingFatal[e.TargetId]=new(before,State(e.Time),e.ActorId,e.Id,e.Time,e.Tick,eventOrder,defusingBefore,!ended,ctKits.Any(k=>k==true),ctKits.Any(k=>k is null));}
                if(enemy)
                {
                    if(!damageByVictim.TryGetValue(e.TargetId,out var contributors))damageByVictim[e.TargetId]=contributors=[];
                    var old=contributors.GetValueOrDefault(e.ActorId!);var ids=old.Ids??[];
                    ids.Add(e.Id);contributors[e.ActorId!]=(old.Damage+actual,ids);
                    var a=EventEquipment(e,e.ActorId!,e.ActorState,frame);var t=EventEquipment(e,e.TargetId,e.TargetState,frame);
                    if(a is null||t is null)Missing("伤害事件缺少可靠装备快照。");
                    else result.Features[e.ActorId!][1]+=actual*2*(1-model.EquipmentStrengths[Band(a.Value,model)]/(model.EquipmentStrengths[Band(a.Value,model)]+model.EquipmentStrengths[Band(t.Value,model)]));
                }
            }
            if(e.Kind=="bomb_planted"&&e.ActorId is not null&&sides.GetValueOrDefault(e.ActorId)=="T"&&alive.Contains(e.ActorId)&&health[e.ActorId]>0)
            {
                plantedAt=e.Time;defuser=null;
                if(round.BombTimerSeconds is not double timer||!double.IsFinite(timer)||timer<=0||timer>600)Missing("下包时钟未知。");
            }
            if(e.Kind=="bomb_defuse_start"&&e.ActorId is not null&&sides.GetValueOrDefault(e.ActorId)=="CT"&&alive.Contains(e.ActorId)&&health[e.ActorId]>0&&Reliable(e,e.ActorId,e.ActorState)&&plantedAt.HasValue)defuser=e.ActorId;
            if(e.Kind=="bomb_defuse_aborted"&&(e.ActorId is null||defuser==e.ActorId))defuser=null;
            if(e.Kind is "bomb_defused" or "bomb_exploded") { ended=true;terminalRecorded=true; }
            if(e.Kind=="kill"&&e.TargetId is not null&&sides.ContainsKey(e.TargetId)&&!killed.Contains(e.TargetId))
            {
                var fatal=pendingFatal.GetValueOrDefault(e.TargetId);
                var matchingFatal=fatal is not null&&fatal.ActorId==e.ActorId&&e.Time>=fatal.Time&&e.Tick>=fatal.Tick;
                var wasAlive=alive.Contains(e.TargetId)||matchingFatal;
                if(matchingFatal)before=fatal!.Before;
                else if(fatal is not null&&enemy)Missing("击杀回调归因与已记录致命伤害不一致。");
                pendingFatal.Remove(e.TargetId);killed.Add(e.TargetId);knownDead.Add(e.TargetId);alive.Remove(e.TargetId);health[e.TargetId]=0;
                if(enemy&&wasAlive)
                {
                    kills[e.ActorId!]++;var a=EventEquipment(e,e.ActorId!,e.ActorState,frame);var t=EventEquipment(e,e.TargetId,e.TargetState,frame);
                    if(a is null||t is null)Missing("击杀事件缺少可靠装备快照。");
                    else result.Features[e.ActorId!][0]+=2*(1-model.EquipmentStrengths[Band(a.Value,model)]/(model.EquipmentStrengths[Band(a.Value,model)]+model.EquipmentStrengths[Band(t.Value,model)]));
                    var revenge=previousKills.LastOrDefault(k=>k.ActorId==e.TargetId&&sides[k.TargetId!]==sides[e.ActorId!]&&e.Time-k.Time>=0&&e.Time-k.Time<=model.TradeWindowSeconds&&!traded.Contains(k.TargetId!));
                    if(revenge is not null)traded.Add(revenge.TargetId!);
                    var contributing=damageByVictim.GetValueOrDefault(e.TargetId);
                    foreach(var p in contributing??[]){if(p.Key!=e.ActorId&&sides[p.Key]==sides[e.ActorId!]&&p.Value.Damage>=40)assists.Add(p.Key);}
                    var flash=e.FlashAssist&&e.AssisterId is not null&&e.AssisterId!=e.ActorId&&sides.GetValueOrDefault(e.AssisterId)==sides[e.ActorId!]?e.AssisterId:null;
                    if(flash is not null)assists.Add(flash);
                    var effectiveTime=matchingFatal?fatal!.Time:e.Time;
                    var credit=new RatingEventCredit{Round=round.Number,EventId=e.Id,Kind="kill",Time=effectiveTime,Eligible=matchingFatal?fatal!.SwingOpen:!ended,EvidenceIds=[e.Id]};
                    if(matchingFatal)credit.EvidenceIds.Add(fatal!.EventId);
                    if(plantedAt.HasValue&&credit.Eligible)
                    {
                        if(round.BombTimerSeconds is not double timer||!double.IsFinite(timer)||timer<=0||timer>600){Missing("下包时钟未知。");credit.Eligible=false;credit.Reason="炸弹时钟未知";}
                        else
                        {
                            var remaining=Math.Max(0,plantedAt.Value+timer-effectiveTime);
                            var ctBefore=alive.Where(p=>sides[p]=="CT").Append(e.TargetId).Where(p=>sides[p]=="CT").Distinct().ToList();
                            var defusingAtKill=matchingFatal?fatal!.Defusing:defuser is not null;
                            if(!defusingAtKill&&remaining<10)
                            {
                                if(matchingFatal?fatal!.CtHasKit:ctBefore.Any(p=>kits[p]==true)){if(remaining<5)credit.Eligible=false;}
                                else if(matchingFatal?fatal!.CtKitUnknown:ctBefore.Any(p=>kits[p] is null)){Missing("CT 拆包钳状态未知。");credit.Eligible=false;credit.Reason="拆包钳状态未知";}
                                else credit.Eligible=false;
                            }
                            if(!credit.Eligible&&credit.Reason.Length==0){credit.Reason="炸弹已不可阻止爆炸";ended=true;}
                        }
                    }
                    var after=matchingFatal?fatal!.After:State(e.Time);
                    if(credit.Eligible)
                    {
                        var delta=Probability(after)-Probability(before);if(sides[e.ActorId!]=="T")delta=-delta;
                        credit.NegativeKillerCorrection=delta<0;
                        credit.PlayerCredits[e.TargetId]=-Math.Abs(delta);
                        var positive=Math.Max(0,delta);
                        var shares=new Dictionary<string,double>{{e.ActorId!,model.KillerShare}};
                        void AddShare(string id,double share){shares[id]=shares.GetValueOrDefault(id)+share;}
                        var damageTotal=contributing?.Where(p=>sides[p.Key]==sides[e.ActorId!]).Sum(p=>p.Value.Damage)??0;
                        if(damageTotal>0){foreach(var p in contributing!.Where(p=>sides[p.Key]==sides[e.ActorId!])){AddShare(p.Key,model.DamageShare*p.Value.Damage/damageTotal);credit.EvidenceIds.AddRange(p.Value.Ids);}}
                        else AddShare(e.ActorId!,model.DamageShare);
                        AddShare(flash??e.ActorId!,model.FlashShare);
                        AddShare(revenge?.TargetId??e.ActorId!,model.TradeShare);
                        if(revenge is not null)credit.EvidenceIds.Add(revenge.Id);
                        foreach(var s in shares)credit.PlayerCredits[s.Key]=credit.PlayerCredits.GetValueOrDefault(s.Key)+positive*s.Value;
                        eligibleKills.Add(credit);
                        var effectiveTick=matchingFatal?fatal!.Tick:e.Tick;
                        var effectiveOrder=matchingFatal?fatal!.Order:eventOrder;
                        if(effectiveTime>settlementTime||effectiveTime==settlementTime&&(effectiveTick>settlementTick||effectiveTick==settlementTick&&effectiveOrder>=settlementOrder))
                        {settlementState=after;settlementTime=effectiveTime;settlementTick=effectiveTick;settlementOrder=effectiveOrder;}
                    }
                    else if(credit.Reason.Length==0)credit.Reason="回合已结束";
                    credit.EvidenceIds=credit.EvidenceIds.Distinct().ToList();result.Credits.Add(credit);previousKills.Add(e);
                    last=after;
                }
            }
            else last=State(e.Time);
            if(defuser is not null&&!alive.Contains(defuser))defuser=null;
            Observe(last);
        }
        var residualCandidates=eligibleKills.Where(c=>!plantedAt.HasValue||c.Time>=plantedAt.Value).Where(c=>c.PlayerCredits.Any(p=>sides.GetValueOrDefault(p.Key)==round.Winner&&p.Value>0)).ToList();
        if(residualCandidates.Count>0)
        {
            // The unpublished end split is our explicit policy: freeze the last eligible kill's
            // after-state and split the excess among positive winning kill contributions.
            // A terminal callback prevents future events; it does not erase earlier earned excess.
            var winProbability=round.Winner=="CT"?Probability(settlementState):1-Probability(settlementState);
            var residual=Math.Clamp(1-winProbability,0,1);
            var weights=new Dictionary<string,double>();
            foreach(var candidate in residualCandidates)foreach(var p in candidate.PlayerCredits.Where(p=>sides.GetValueOrDefault(p.Key)==round.Winner&&p.Value>0))weights[p.Key]=weights.GetValueOrDefault(p.Key)+p.Value;
            var c=new RatingEventCredit{Round=round.Number,EventId=$"round-{round.Number}-residual",Kind="residual",Time=round.EndedAt,Eligible=true,EvidenceIds=residualCandidates.SelectMany(c=>c.EvidenceIds).Distinct().ToList()};
            foreach(var p in weights)c.PlayerCredits[p.Key]=residual*p.Value/weights.Values.Sum();
            var losers=alive.Where(p=>sides[p]!=round.Winner).ToList();if(losers.Count==0)losers=sides.Keys.Where(p=>sides[p]!=round.Winner).ToList();
            foreach(var p in losers)c.PlayerCredits[p]=-residual/losers.Count;
            result.Credits.Add(c);
        }
        foreach(var p in sides.Keys)
        {
            var survived=round.Survivors.Contains(p)&&!knownDead.Contains(p);
            var scoringKast=kills[p]>0||assists.Contains(p)||traded.Contains(p)||survived;
            if(scoringKast)result.ScoringKast.Add(p);
            var playerStrength=model.EquipmentStrengths[Band(equip[p],model)];
            var economicWeight=2*playerStrength/(playerStrength+model.EquipmentStrengths[3]);
            result.Features[p][2]=survived?economicWeight:0;
            result.Features[p][3]=scoringKast?economicWeight:0;
            result.Features[p][4]=model.MultiKillPoints[Math.Min(5,kills[p])];
            result.Features[p][5]=result.Credits.Sum(c=>c.PlayerCredits.GetValueOrDefault(p));
        }
        if(model.FeatureVersion=="strongest-weapon-armor-v1"&&facts is null)Missing("缺少最贵持有武器与护甲价格补充事实；不能从手持武器推断。");
        return result;
    }
}
