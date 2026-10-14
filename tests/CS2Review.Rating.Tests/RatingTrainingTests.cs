using CS2Review.Core;
using System.Text.Json;
using System.Text;

static class RatingTrainingTests
{
    public static (RatingReferenceManifest Manifest,List<RatingTrainingSample> Samples) Data()
    {
        var manifest=new RatingReferenceManifest{ReferenceId="synthetic-training-test",Provenance="SYNTHETIC; not an accuracy benchmark"};var samples=new List<RatingTrainingSample>();
        for(var i=0;i<3;i++)
        {
            var round=RatingRuleTests.Round();round.Events=[RatingRuleTests.Kill("k",20)];if(i==1)round.Winner="CT";
            var match=RatingRuleTests.Match(round);match.SourceSha256=new string((char)('a'+i),64);
            var hash=RatingModelCodec.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(match,DataJson.Options)));
            var reference=new RatingReferenceMatch{GroupId="series-"+i,CanonicalMatchPath="match-"+i+".json",CanonicalSha256=hash,SourceSha256=match.SourceSha256,MapName=match.MapName,Snapshot=new(){CapturedAtUtc=DateTimeOffset.Parse("2026-10-12T00:00:00Z"),TargetDate="2026-10-01",SourceUrl="https://synthetic.invalid/mapstats/"+i,MapStatsId=i+1,Players=[new(){PlayerId="p",SteamId="10001",PlayedRounds=1,Rating=1.2+i*.05,Swing=5, Kills=1,Deaths=0,Adr=0,KastPercent=100},new(){PlayerId="mate",SteamId="10002",PlayedRounds=1,Rating=.8+i*.05,Swing=-5,Kills=0,Deaths=0,Adr=0,KastPercent=100}]} };
            manifest.Matches.Add(reference);samples.Add(new(){Reference=reference,Match=match,CanonicalFileSha256=hash});
        }
        return(manifest,samples);
    }
    public static void Run()
    {
        var failures=0;
        void Test(string name,Action body){try{body();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        void Reject(Action body){try{body();}catch(ArgumentException){return;}throw new Exception("invalid reference accepted");}
        Test("real MatchData training produces repeatable frozen experimental model",()=>{var d=Data();var trainer=new RatingTrainer();var m=trainer.Train(d.Manifest,d.Samples,new(){Iterations=50});var again=trainer.Train(d.Manifest,d.Samples,new(){Iterations=50});RatingRuleTests.Yes(m.ModelId==again.ModelId,"not deterministic");RatingRuleTests.Yes(m.CalibrationStatus=="experimental"&&m.ProbabilityCells.Count>0,"fake calibrated/empty probability");RatingModelCodec.Validate(m);RatingRuleTests.Yes(new RatingEngine().Score(d.Samples[0].Match,"p",m).Overall is not null,"trained model not executable");});
        Test("same round same state not weighted by shot frequency",()=>{var d=Data();for(var i=0;i<100;i++)d.Samples[0].Match.Rounds[0].Events.Insert(0,new(){Id="shot"+i,Kind="shot",Tick=i,Time=1+i*.01,ActorId="p"});var m=new RatingTrainer().Train(d.Manifest,d.Samples,new(){Iterations=5});var cell=m.ProbabilityCells.Single(c=>c.Map=="global"&&c.CtAlive==2&&c.TAlive==2&&!c.Planted);RatingRuleTests.Near(3,cell.Observations);});
        Test("each CV fold excludes whole series and rebuilds its probability and normalization",()=>{var d=Data();var report=new RatingTrainer().ValidateLeaveOneMatchOut(d.Manifest,d.Samples,new(){Iterations=20});RatingRuleTests.Near(3,report.Folds.Count);RatingRuleTests.Near(6,report.Rating.Count);RatingRuleTests.Near(6,report.Swing.Count);RatingRuleTests.Yes(report.Folds.All(f=>!f.TrainingGroups.Contains(f.HeldOutGroup)&&f.TrainingGroups.Count==2),"group leakage");RatingRuleTests.Yes(report.Folds.Select(f=>f.ModelId).Distinct().Count()==3,"reused all-data model");RatingRuleTests.Yes(report.Status=="exploratory"&&report.Rating.Mae is >=0,"fake formal calibration");});
        Test("snapshot target capture side participation and raw stats validated",()=>{foreach(var change in new Action<RatingTrainingSample>[] {s=>s.Reference.Snapshot.CapturedAtUtc=DateTimeOffset.Parse("2026-09-30T23:59:59Z"),s=>s.Reference.Snapshot.TargetDate="2025-10-29",s=>s.Reference.Snapshot.Players[0].Side="spectator",s=>s.Reference.Snapshot.Players[0].PlayedRounds=2,s=>s.Reference.Snapshot.Players[0].Kills=99,s=>s.Reference.Snapshot.Players[0].Rating=double.NaN}){var d=Data();change(d.Samples[0]);Reject(()=>RatingReferenceLoader.Validate(d.Manifest,d.Samples));}});
        Test("demo SHA distinct from actual canonical file SHA",()=>{var d=Data();d.Samples[0].CanonicalFileSha256=new string('f',64);Reject(()=>RatingReferenceLoader.Validate(d.Manifest,d.Samples));d=Data();d.Samples[0].Match.SourceSha256=new string('e',64);Reject(()=>RatingReferenceLoader.Validate(d.Manifest,d.Samples));});
        Test("same source cannot cross groups",()=>{var d=Data();d.Samples[1].Reference.SourceSha256=d.Samples[0].Match.SourceSha256;d.Samples[1].Match.SourceSha256=d.Samples[0].Match.SourceSha256;Reject(()=>RatingReferenceLoader.Validate(d.Manifest,d.Samples));});
        Test("manifest loader executes file hash and relative path binding",()=>{var d=Data();var dir=Path.Combine("artifacts","rating-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);foreach(var s in d.Samples)File.WriteAllText(Path.Combine(dir,s.Reference.CanonicalMatchPath),JsonSerializer.Serialize(s.Match,DataJson.Options),new UTF8Encoding(false));var path=Path.Combine(dir,"manifest.json");File.WriteAllText(path,JsonSerializer.Serialize(d.Manifest,DataJson.Options),new UTF8Encoding(false));var loaded=RatingReferenceLoader.LoadAsync(path).GetAwaiter().GetResult();RatingRuleTests.Near(3,loaded.Samples.Count);File.AppendAllText(Path.Combine(dir,d.Samples[0].Reference.CanonicalMatchPath)," ");Reject(()=>RatingReferenceLoader.LoadAsync(path).GetAwaiter().GetResult());});
        Test("cancellation aborts score training and validation",()=>{var d=Data();using var stop=new CancellationTokenSource();stop.Cancel();var count=0;foreach(var action in new Action[]{()=>new RatingEngine().Score(d.Samples[0].Match,"p",RatingRuleTests.Model(),ct:stop.Token),()=>new RatingTrainer().Train(d.Manifest,d.Samples,ct:stop.Token),()=>new RatingTrainer().ValidateLeaveOneMatchOut(d.Manifest,d.Samples,ct:stop.Token)})try{action();}catch(OperationCanceledException){count++;}RatingRuleTests.Near(3,count);});
        Test("CV freezes each train-only probability table and computes held-out probability metrics",()=>{var d=Data();var report=new RatingTrainer().ValidateLeaveOneMatchOut(d.Manifest,d.Samples,new(){Iterations=10});foreach(var fold in report.Folds){RatingRuleTests.Yes(fold.FrozenModel is not null,"fold parameters unavailable");var cell=fold.FrozenModel!.ProbabilityCells.Single(c=>c.Map=="global"&&c.CtAlive==2&&c.TAlive==2&&!c.Planted);RatingRuleTests.Near(2,cell.Observations);RatingRuleTests.Yes(fold.ProbabilityCount>0&&fold.ProbabilityBrier is >=0&&fold.ProbabilityLogLoss is >=0,"missing held-out probability evaluation");RatingModelCodec.Validate(fold.FrozenModel);}RatingRuleTests.Yes(report.ProbabilityCount==6,"event frequency bias in validation");});
        if(failures>0)Environment.ExitCode=1;
    }
}
