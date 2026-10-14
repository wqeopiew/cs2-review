using CS2Review.Core;
using System.Reflection;
using System.Text;
using System.Text.Json;

static class BuiltInRatingModelTests
{
    public static RatingModel Model()
    {
        var type=typeof(RatingEngine).Assembly.GetType("CS2Review.Core.BuiltInRatingModel");
        if(type is null)throw new Exception("Missing third-round built-in RT model.");
        return (RatingModel)type.GetMethod("Create")!.Invoke(null,null)!;
    }
    static bool IsRoundThree(RatingModel? model)
    {
        var type=typeof(RatingEngine).Assembly.GetType("CS2Review.Core.BuiltInRatingModel")??throw new Exception("Missing third-round built-in RT model.");
        return (bool)type.GetMethod("IsRoundThree")!.Invoke(null,[model])!;
    }
    static void Reject(Action body)
    {
        try{body();}catch(ArgumentException){return;}catch(TargetInvocationException ex) when(ex.InnerException is ArgumentException){return;}
        throw new Exception("altered built-in model accepted");
    }
    static MatchData TwoKillMatch()
    {
        var round=RatingRuleTests.Round();
        round.Participants.RemoveAll(p=>p.PlayerId=="mate");
        round.EconomySnapshots.RemoveAll(p=>p.PlayerId=="mate");
        round.Survivors=["p"];
        round.Events=[RatingRuleTests.Damage("damage",10,"p",10),RatingRuleTests.Kill("first",20),RatingRuleTests.Kill("second",30,"p","e2")];
        var match=RatingRuleTests.Match(round);match.MapName="unknown-map";match.Players.RemoveAll(p=>p.Id=="mate");return match;
    }
    static MatchData MixedMatch(string map)
    {
        var round=RatingRuleTests.Round();
        var opening=RatingRuleTests.Kill("opening",8);opening.FlashAssist=true;opening.AssisterId="mate";
        round.Events=[RatingRuleTests.Damage("assist",5,"mate",40),opening,RatingRuleTests.Bomb("bomb_planted","plant",10),RatingRuleTests.Kill("counter",15,"e2","mate"),RatingRuleTests.Kill("trade",18,"p","e2")];
        round.EconomySnapshots.Single(p=>p.PlayerId=="p").EquipmentValue=6000;
        round.EconomySnapshots.Single(p=>p.PlayerId=="mate").EquipmentValue=500;
        round.EconomySnapshots.Single(p=>p.PlayerId=="e2").EquipmentValue=7000;
        round.Survivors=["p"];
        var match=RatingRuleTests.Match(round);match.MapName=map;return match;
    }
    public static void Run()
    {
        var failures=new List<string>();var count=0;
        void Test(string name,Action body){count++;try{body();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Test("third-round identity detection handles absent and legacy models",()=>{
            RatingRuleTests.Yes(!IsRoundThree(null)&&!IsRoundThree(DefaultRatingModel.Create())&&!IsRoundThree(RatingRuleTests.Model()),"absent or legacy model received third-round identity");
            var result=new RatingEngine().Score(TwoKillMatch(),"p",null);
            RatingRuleTests.Yes(result.Overall is null&&result.RoundSwing is null&&result.UnavailableReasons.Count>0,"missing model selected the new built-in implicitly");
        });
        Test("third-round RT evaluates the frozen linear parameters with independently derived features",()=>{
            var result=new RatingEngine().Score(TwoKillMatch(),"p",Model());
            // Equal equipment gives two kills, 10 damage and 8/9 survival/KAST; two kills give one multi-kill point.
            // The public 1T/2CT table has CT .811, adjusted from the .512 anchor to unknown-map .514.
            foreach(var pair in result.FeatureValues!.Zip(new[]{2d,10d,8d/9,8d/9,1d,.8122240091992942}))RatingRuleTests.Near(pair.Second,pair.First);
            RatingRuleTests.Near(81.22240091992942,result.RoundSwing);
            RatingRuleTests.Near(5.037574837449807,result.Overall);
        });
        Test("third-round shares probabilities six features and axes exactly preserve baseline RS",()=>{
            foreach(var map in new[]{"unknown-map","de_mirage","de_dust2","de_nuke"})
            {
                var match=MixedMatch(map);
                foreach(var player in match.Players)
                {
                    var baseline=new RatingEngine().Score(match,player.Id,DefaultRatingModel.Create());
                    var current=new RatingEngine().Score(match,player.Id,Model());
                    RatingRuleTests.Yes(baseline.Overall is not null&&current.Overall is not null,"complete synthetic facts became unavailable");
                    RatingRuleTests.Yes(baseline.RoundSwing==current.RoundSwing,"RS probability branch changed");
                    RatingRuleTests.Yes(baseline.FeatureValues!.SequenceEqual(current.FeatureValues!),"six feature calculation changed");
                    RatingRuleTests.Yes(baseline.Axes.SequenceEqual(current.Axes),"fixed six axes changed");
                    RatingRuleTests.Yes(JsonSerializer.Serialize(baseline.Credits,DataJson.Options)==JsonSerializer.Serialize(current.Credits,DataJson.Options),"event credit allocation changed");
                }
            }
        });
        Test("third-round models disclose real RT provenance without claiming probability training or CV",()=>{
            var model=Model();RatingModelCodec.Validate(model);
            RatingRuleTests.Yes(IsRoundThree(model)&&!DefaultRatingModel.IsBaseline(model)&&model.SchemaVersion==3&&model.ModelVersion=="rating-rt-calibrated-v1","third-round identity mislabeled");
            RatingRuleTests.Yes(model.CalibrationStatus=="experimental"&&model.Validation is null&&model.ProbabilityCells.Count==0,"invented calibration or probability observations");
            RatingRuleTests.Yes(model.TrainingManifestSha256=="09184017a0b62136c38927c6e2250a45957c75dd89a063e3a55208e99b631ad4"&&model.TrainingGroups.Count==12,"real training provenance lost");
            RatingRuleTests.Yes(model.ModelId!="301bcf632d091517354617f15449a664c12e02bb77231cc04bb020164cfc0356","research ID reused as executable product ID");
            foreach(var value in new[]{"artifacts/calibration-real-3/fitting/fitted/candidate-frozen.json","301bcf632d091517354617f15449a664c12e02bb77231cc04bb020164cfc0356","09ebced004c66e8d5c38b69d3cab471ce263a2ab278b1ce27d03f2ccc1dabc51","120","0.082624","0.108298","1.636221"})RatingRuleTests.Yes(model.ParameterDisclosure.Contains(value,StringComparison.Ordinal),"missing provenance or independent holdout disclosure: "+value);
        });
        Test("third-round model roundtrip executes independently of factory instances",()=>{
            var first=Model();var second=Model();var imported=RatingModelCodec.Load(RatingModelCodec.Serialize(first));
            RatingRuleTests.Yes(first.ModelId==second.ModelId&&first.ModelId==imported.ModelId,"unstable executable identity");
            RatingRuleTests.Near(5.037574837449807,new RatingEngine().Score(TwoKillMatch(),"p",imported).Overall);
            first.Weights[0]=.99;first.TrainingGroups.Clear();first.AxisScales[0]=1;
            RatingRuleTests.Near(5.037574837449807,new RatingEngine().Score(TwoKillMatch(),"p",second).Overall);
            RatingRuleTests.Yes(second.TrainingGroups.Count==12,"shared mutable provenance");
        });
        Test("resealing changed third-round parameters cannot retain the released built-in label",()=>{
            foreach(var mutate in new Action<RatingModel>[] {m=>m.Weights[0]=.5,m=>m.Intercept=.1,m=>m.AxisScales[5]=.01,m=>m.ProbabilityPriorCtWin=.9,m=>{m.KillerShare=.6;m.DamageShare=.2;},m=>m.TrainingManifestSha256=new string('f',64),m=>m.TrainingGroups[0]="unseen-series",m=>m.ParameterDisclosure="official calibrated"})
            {var model=Model();mutate(model);model=RatingModelCodec.Seal(model);Reject(()=>RatingModelCodec.Validate(model));Reject(()=>RatingModelCodec.Serialize(model));}
        });
        Test("third-round built-in cannot invent trained probability cells or CV metadata",()=>{
            var model=Model();model.ProbabilityCells.Add(new(){Map="global",CtAlive=5,TAlive=5,CtEquipmentBand=3,TEquipmentBand=3,Observations=1,CtWins=1});Reject(()=>RatingModelCodec.Seal(model));
            model=Model();model.Validation=new();Reject(()=>RatingModelCodec.Seal(model));
        });
        Test("third-round schema rejects mismatched version labels and calibrated claims",()=>{
            foreach(var mutate in new Action<RatingModel>[] {m=>m.SchemaVersion=2,m=>m.ModelVersion="rating-approx-v1",m=>m.ModelVersion="rating-rt-calibrated-v2",m=>m.CalibrationStatus="calibrated",m=>m.FeatureVersion="strongest-weapon-armor-v1"})
            {var model=Model();mutate(model);Reject(()=>RatingModelCodec.Seal(model));var result=new RatingEngine().Score(TwoKillMatch(),"p",model);RatingRuleTests.Yes(result.Overall is null&&result.RoundSwing is null,"invalid released model label executed");}
        });
        Test("new built-in serialization keeps legacy and original baseline identities executable",()=>{
            foreach(var model in new[]{Model(),DefaultRatingModel.Create(),RatingRuleTests.Model()})
            {var loaded=RatingModelCodec.Load(RatingModelCodec.Serialize(model));RatingRuleTests.Yes(loaded.ModelId==model.ModelId,"model roundtrip changed identity");RatingRuleTests.Yes(new RatingEngine().Score(RatingRuleTests.Match(RatingRuleTests.Round()),"p",loaded).Overall is not null,"compatible model became unavailable");}
            RatingRuleTests.Yes(DefaultRatingModel.Create().ModelId=="ddba1171038f2eb7c4d1cff08dcd34f6b980234e720ad414a750048ac72989d6","original baseline changed");
        });
        Test("third-round model still leaves invalid facts unavailable",()=>{
            foreach(var mutate in new Action<MatchData>[] {m=>m.SourceSha256="bad-sha",m=>m.Rounds[0].EconomySnapshots[0].EquipmentValue=null,m=>m.Rounds[0].EconomySnapshots[0].Health=null,m=>m.Rounds[0].Events[1].ActorState=null})
            {var match=TwoKillMatch();mutate(match);var result=new RatingEngine().Score(match,"p",Model());RatingRuleTests.Yes(result.Overall is null&&result.RoundSwing is null&&result.UnavailableReasons.Count>0,"invalid facts filled in");}
            var resultWithWrongSource=new RatingEngine().Score(TwoKillMatch(),"p",Model(),new(){SourceSha256=new string('f',64)});
            RatingRuleTests.Yes(resultWithWrongSource.Overall is null&&resultWithWrongSource.RoundSwing is null,"unbound supplement accepted");
        });
        Test("third-round scoring explains RT training and unchanged RS without sparse-table warnings",()=>{
            var result=new RatingEngine().Score(TwoKillMatch(),"p",Model());
            RatingRuleTests.Yes(result.Warnings.Any(w=>w.Contains("RT")&&w.Contains("训练")&&w.Contains("RS")&&w.Contains("基线")),"RT/RS model scope undisclosed");
            RatingRuleTests.Yes(!result.Warnings.Any(w=>w.Contains("内置基础估算")||w.Contains("稀疏全局局面")),"third-round model routed to untrained probability fallback");
        });
        Console.WriteLine($"Third-round built-in rating: {count-failures.Count}/{count} passed");if(failures.Count>0)Environment.ExitCode=1;
        if(failures.Count==0){var model=Model();Console.WriteLine("Third-round product model ID: "+model.ModelId);Console.WriteLine("Third-round product JSON SHA-256: "+RatingModelCodec.Sha256(Encoding.UTF8.GetBytes(RatingModelCodec.Serialize(model))));}
    }
}
