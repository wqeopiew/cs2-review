using CS2Review.Core;
using System.Text;
using System.Text.Json;

if(args.Length==1&&args[0]=="--round-three-only")
{BuiltInRatingModelTests.Run();return;}

if(args.Length==2&&args[0]=="--export-built-in")
{
    var model=BuiltInRatingModelTests.Model();RatingModelCodec.SaveFileAsync(args[1],model).GetAwaiter().GetResult();
    Console.WriteLine("Third-round product model ID: "+model.ModelId);return;
}

if(args.Length==3&&args[0]=="--baseline-score")
{
    var input=JsonSerializer.Deserialize<MatchData>(File.ReadAllText(args[1]),DataJson.Options)!;
    var model=RatingBaselineTests.Model();
    var results=input.Players.SelectMany(p=>new[]{"all","T","CT"}.Select(side=>new RatingEngine().Score(input,p.Id,model,side:side))).ToList();
    File.WriteAllText(args[2],JsonSerializer.Serialize(new{model,results},DataJson.Options));
    if(results.Any(r=>r.PlayedRounds>0&&(r.Overall is null||r.RoundSwing is null)))throw new Exception("A participating player is missing Rating/RS; see private results.");
    Console.WriteLine($"PASS built-in Rating/RS: {results.Count} player/side results");return;
}

if(args.Length==2&&args[0]=="--export-synthetic")
{
    var dir=Path.GetFullPath(args[1]);Directory.CreateDirectory(dir);var dataset=RatingTrainingTests.Data();
    foreach(var sample in dataset.Samples)File.WriteAllText(Path.Combine(dir,sample.Reference.CanonicalMatchPath),JsonSerializer.Serialize(sample.Match,DataJson.Options),new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(dir,"manifest.json"),JsonSerializer.Serialize(dataset.Manifest,DataJson.Options),new UTF8Encoding(false));
    Console.WriteLine(Path.Combine(dir,"manifest.json"));return;
}

// A missing module must fail at runtime before implementation exists.
var type = typeof(AnalysisEngine).Assembly.GetType("CS2Review.Core.RatingEngine");
if (type is null) throw new Exception("FAIL missing-model scoring: RatingEngine is not implemented.");
var engine = Activator.CreateInstance(type)!;
var match = new MatchData { Players = [new() { Id = "p", Name = "Player" }] };
var score = type.GetMethod("Score")!.Invoke(engine, [match, "p", null, null, "all", CancellationToken.None])!;
if (score.GetType().GetProperty("Overall")!.GetValue(score) is not null) throw new Exception("FAIL unavailable model invented a rating");
if (((IEnumerable<string>)score.GetType().GetProperty("UnavailableReasons")!.GetValue(score)!).Any() is false) throw new Exception("FAIL missing model has no reason");
Console.WriteLine("PASS missing model preserves null and reason");
RatingRuleTests.Run();
RatingArtifactTests.Run();
RatingTrainingTests.Run();
RatingBaselineTests.Run();
BuiltInRatingModelTests.Run();
