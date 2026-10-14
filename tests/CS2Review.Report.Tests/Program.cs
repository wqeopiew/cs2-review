using System.Net;
using System.Text;
using System.Text.Json;
using CS2Review.Core;

var match = new MatchData { Id="canonical-local-id", SourceSha256=new string('a',64), Players=[new(){Id="76561198000000001",Name="private-name"},new(){Id="76561198000000002",Name="enemy-private"}], Rounds=[new(){Number=1,IsValid=true,Winner="T",StartedAt=0,EndedAt=30,Participants=[new(){PlayerId="76561198000000001",Side="T"},new(){PlayerId="76561198000000002",Side="CT"}],Events=[new(){Id="76561198000000001-event",Kind="kill",Time=10,Tick=640,ActorId="76561198000000001",TargetId="76561198000000002"}]}] };
var analysis = new AnalysisEngine().Analyze(match,match.Players[0].Id);
var options = new ReportOptions {Endpoint="https://unit.test/v1/chat/completions", Model="test-model",ApiKey="synthetic-key",MaxInputCharacters=12000};
var handler = new InspectHandler(); var service = new ReportService(new HttpClient(handler));
var phaseErrors=new List<string>();
var phaseMatch=new MatchData{SchemaVersion=2,Id="phase-evidence-test",SourceSha256=new string('f',64),MapName="phase-test",ParserVersion="phase-fixture",Players=match.Players, Rounds=[new(){Number=1,IsValid=true,Winner="T",StartedAt=0,EndedAt=30,StatisticsEndedAt=35,Participants=match.Rounds[0].Participants,Events=[new(){Id="competitive-shot",Kind="shot",Time=10,Tick=640,ActorId=match.Players[0].Id},new(){Id="recorded-exit",Kind="kill",Time=30,Tick=1920,ActorId=match.Players[0].Id,TargetId=match.Players[1].Id,AfterRoundEnd=true}]}]};
var phasePrepared=await service.PrepareAsync(phaseMatch,new AnalysisEngine().Analyze(phaseMatch,phaseMatch.Players[0].Id),options);
using(var doc=JsonDocument.Parse(phasePrepared.Preview))if(doc.RootElement.GetProperty("evidence").GetArrayLength()!=1)phaseErrors.Add("AI preview includes same-clock exit evidence");
using(var phaseHistory=new HistoryStore(Path.Combine("artifacts","report-phase-tests",Guid.NewGuid().ToString("N"),"history.db")))
{
 await phaseHistory.InitializeAsync();var phaseId=await phaseHistory.SaveMatchAsync(phaseMatch);var canonical=(await phaseHistory.LoadMatchAsync(phaseId))!;
 var invalidExitReport=new LocalReportService().Generate(canonical,new AnalysisEngine().Analyze(canonical,canonical.Players[0].Id));
 invalidExitReport.Strengths=[new(){Observation="Exit evidence must be rejected",Evidence=[new(){EventId="recorded-exit",Round=1,Time=30}]}];
 try{await phaseHistory.SaveReportAsync(phaseId,canonical.Players[0].Id,invalidExitReport);phaseErrors.Add("History accepts same-clock exit evidence");}catch(InvalidDataException){}
}
if(phaseErrors.Count>0)throw new Exception(string.Join("; ",phaseErrors));
Console.WriteLine("PASS same-clock recorded exit events excluded from AI preview and report history evidence");
var prepared = await service.PrepareAsync(match,analysis,options);
Assert(handler.Calls==0,"preparation made network request");
Assert(!prepared.Preview.Contains("765611") && !prepared.Preview.Contains("private") && !prepared.Preview.Contains("synthetic-key"),"preview exposed identity/key");
Assert(prepared.PayloadSha256.Length==64 && prepared.CharacterCount==prepared.Preview.Length,"frozen metadata");
var frozen = prepared.Preview; match.Rounds[0].Events[0].Time=20; analysis.Summary.Kills=99;
var report = await service.GeneratePreparedAsync(prepared,options);
Assert(handler.LastUserContent==frozen,"sent payload differs from preview");
Assert(report.Strengths.Single().Evidence.Single().EventId=="76561198000000001-event" && report.Strengths[0].Evidence[0].Time==10,"alias not restored to frozen canonical event");
Assert(report.Strengths[0].Metrics["kills"]==1,"frozen metrics mutated");
var changed = new ReportOptions {Endpoint=options.Endpoint,Model="different",ApiKey=options.ApiKey,MaxInputCharacters=12000};
await Reject(()=>service.GeneratePreparedAsync(prepared,changed)); Assert(handler.Calls==1,"changed target sent");
using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await service.PrepareAsync(match,analysis,options,ct:cancelled.Token);throw new Exception("accepted cancellation");}catch(OperationCanceledException){}}
foreach(var (status,expected) in new[]{(HttpStatusCode.Unauthorized,ConnectionProbeStatus.AuthenticationFailed),(HttpStatusCode.NotFound,ConnectionProbeStatus.AddressOrModelUnavailable),(HttpStatusCode.TooManyRequests,ConnectionProbeStatus.RateLimited),(HttpStatusCode.InternalServerError,ConnectionProbeStatus.ServiceFailed)}){
 var probeHandler=new ProbeHandler(status);var result=await new ProviderConnectionService(new HttpClient(probeHandler)).TestAsync(options);
 Assert(result.Status==expected,"probe status");Assert(!probeHandler.Body.Contains("765611")&&!probeHandler.Body.Contains("private-name"),"probe used match");Assert(!JsonSerializer.Serialize(result).Contains("synthetic-key"),"probe exposed key");
}
var okHandler=new ProbeHandler(HttpStatusCode.OK);var ok=await new ProviderConnectionService(new HttpClient(okHandler)).TestAsync(options);Assert(ok.Status==ConnectionProbeStatus.Success&&ok.StructuredOutputSupported,"structured probe");
using(var defaultBody=JsonDocument.Parse(okHandler.Body))Assert(!defaultBody.RootElement.TryGetProperty("enable_thinking",out _),"provider-neutral request added Qwen parameter");
analysis=new AnalysisEngine().Analyze(match,match.Players[0].Id);
analysis.Summary.Rating=1.23;analysis.Summary.RatingModelId=new string('c',64);analysis.Summary.RoundSwing=2.34;
var qwenOptions=new ReportOptions{Endpoint=options.Endpoint,Model="qwen-plus",ApiKey=options.ApiKey,MaxInputCharacters=12000,EnableThinking=false};
var preparation=service.PrepareAsync(match,analysis,qwenOptions);
analysis.Summary.Rating=9.99;analysis.Summary.RatingModelId=new string('d',64);qwenOptions.Model="changed-during-preparation";
var frozenPreparation=await preparation;
using(var frozenBody=JsonDocument.Parse(frozenPreparation.Preview))Assert(frozenBody.RootElement.GetProperty("metrics").GetProperty("rating").GetDouble()==1.23&&frozenPreparation.Model=="qwen-plus"&&frozenPreparation.ScoringVersion==new string('c',64),"preparation yielded before freezing analysis/configuration");
qwenOptions.Model="qwen-plus";await service.GeneratePreparedAsync(frozenPreparation,qwenOptions);Assert(handler.LastEnableThinking==false,"Qwen report did not disable thinking");
await new ProviderConnectionService(new HttpClient(okHandler)).TestAsync(qwenOptions);using(var qwenBody=JsonDocument.Parse(okHandler.Body))Assert(qwenBody.RootElement.GetProperty("enable_thinking").GetBoolean()==false,"Qwen probe did not disable thinking");
qwenOptions.EnableThinking=true;await Reject(()=>service.GeneratePreparedAsync(frozenPreparation,qwenOptions));
var repairOptions=new ReportOptions{Endpoint=options.Endpoint,Model="qwen-plus",ApiKey=options.ApiKey,MaxInputCharacters=12000,EnableThinking=false};
var repairHandler=new InspectHandler{CorruptFirstEvidence=true,AfterFirstRequest=()=>{repairOptions.Model="mutated-mid-request";repairOptions.EnableThinking=true;}};
var repaired=await new ReportService(new HttpClient(repairHandler)).GeneratePreparedAsync(frozenPreparation,repairOptions);
Assert(repairHandler.RequestedModels.SequenceEqual(new[]{"qwen-plus","qwen-plus"})&&repairHandler.LastEnableThinking==false&&repaired.Model=="qwen-plus","repair used mutated send configuration instead of frozen target");
var oneStep=await service.GenerateAsync(match,analysis,options);Assert(!handler.LastUserContent.Contains("765611")&&!handler.LastUserContent.Contains("private-name"),"one-step default exposed identities");
var other=JsonSerializer.Deserialize<MatchData>(JsonSerializer.Serialize(match,DataJson.Options),DataJson.Options)!;other.Id="other-match";other.SourceSha256=new string('b',64);other.Rounds[0].Events[0].ActorId=match.Players[1].Id;other.Rounds[0].Events[0].TargetId=match.Players[0].Id;
await RejectData(()=>service.PrepareAsync(other,analysis,options));try{new LocalReportService().Generate(other,analysis);throw new Exception("accepted cross-match analysis");}catch(InvalidDataException){}
analysis.Summary.Kills=99;await RejectData(()=>service.PrepareAsync(match,analysis,options));
other.Id=match.Id;
using(var history=new HistoryStore(Path.Combine("artifacts","report-binding-tests",Guid.NewGuid().ToString("N"),"history.db")))
{
 await history.InitializeAsync();var first=await history.SaveMatchAsync(match);var second=await history.SaveMatchAsync(other);Assert(first!=second,"ID collision did not create canonical version");
 var canonical=(await history.LoadMatchAsync(second))!;var correct=new LocalReportService().Generate(canonical,new AnalysisEngine().Analyze(canonical,match.Players[0].Id));await history.SaveReportAsync(second,match.Players[0].Id,correct);
 await RejectData(()=>history.SaveReportAsync(second,match.Players[1].Id,correct));
 var wrong=new LocalReportService().Generate(match,new AnalysisEngine().Analyze(match,match.Players[0].Id));await RejectData(()=>history.SaveReportAsync(second,match.Players[0].Id,wrong));
}
var sub=JsonSerializer.Deserialize<MatchData>(JsonSerializer.Serialize(match,DataJson.Options),DataJson.Options)!;
sub.Rounds.Add(new(){Number=2,IsValid=true,Winner="CT",StartedAt=40,EndedAt=60,Participants=[new(){PlayerId=match.Players[1].Id,Side="CT"}],Events=[new(){Id="unrelated-round",Kind="shot",ActorId=match.Players[1].Id,Time=50,Tick=3200}]});
var subPrepared=await service.PrepareAsync(sub,new AnalysisEngine().Analyze(sub,sub.Players[0].Id),options);using(var doc=JsonDocument.Parse(subPrepared.Preview))Assert(doc.RootElement.GetProperty("evidence").EnumerateArray().All(e=>e.GetProperty("round").GetInt32()==1),"substitute nonparticipation round sent");
var deadRound=new RoundData{Number=1,IsValid=true,Winner="T",StartedAt=0,EndedAt=30,FreezeEndedAt=1,Participants=[new(){PlayerId="p",Side="T"},new(){PlayerId="e",Side="CT"}],EconomySnapshots=[new(){PlayerId="p",Side="T",Time=1,Tick=64,Health=0},new(){PlayerId="e",Side="CT",Time=1,Tick=64,Health=100}],Events=[new(){Id="posthumous",Kind="damage",ActorId="p",TargetId="e",Time=2,Tick=128,ActorState=new(){PlayerId="p",Side="T",Time=2,Tick=128,Health=100},TargetState=new(){PlayerId="e",Side="CT",Time=2,Tick=128,Health=90}}]};
var deadContext=new RoundContextAnalyzer().Analyze(deadRound,"p");Assert(deadContext.Timeline.Single().PlayerAliveAfter==false&&deadContext.Timeline.Single().OwnAlive==0,"initial zero health resurrected");
await Reject(()=>new ProviderConnectionService(new HttpClient(okHandler)).TestAsync(new(){Endpoint="https://unit.test/v1/chat/completions#secret",Model="m",ApiKey="s"}));
Console.WriteLine("PASS frozen anonymous preview, no preparation request, canonical evidence mapping, immutable metrics, changed-target rejection, cancellation, synthetic probe status/privacy/HTTPS validation");
static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
static async Task Reject(Func<Task> call){try{await call();throw new Exception("invalid configuration accepted");}catch(ArgumentException){}}
static async Task RejectData(Func<Task> call){try{await call();throw new Exception("cross-snapshot or mutated analysis accepted");}catch(InvalidDataException){}}
sealed class InspectHandler:HttpMessageHandler{
 public int Calls;public string LastUserContent="";public bool? LastEnableThinking;
 public Action? AfterFirstRequest;public bool CorruptFirstEvidence;public List<string> RequestedModels=[];
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
  Calls++;using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));LastUserContent=body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
  LastEnableThinking=body.RootElement.TryGetProperty("enable_thinking",out var thinking)?thinking.GetBoolean():null;
  RequestedModels.Add(body.RootElement.GetProperty("model").GetString()!);
  var repairNote=LastUserContent.IndexOf("\n上次报告未通过",StringComparison.Ordinal);
  using var payload=JsonDocument.Parse(repairNote<0?LastUserContent:LastUserContent[..repairNote]);var e=payload.RootElement.GetProperty("evidence")[0];var metrics=payload.RootElement.GetProperty("metrics");
  var result=new AiReport{Summary="依据事件复核；评分尚未校准。",Strengths=[new(){Observation="记录到有效击杀。",Inference="不能据此推断稳定枪法。",Evidence=[new(){EventId=e.GetProperty("eventId").GetString()!,Round=e.GetProperty("round").GetInt32(),Time=e.GetProperty("time").GetDouble()}],Metrics=new(){["kills"]=metrics.GetProperty("kills").GetDouble()}}]};
  if(Calls==1){if(CorruptFirstEvidence)result.Strengths[0].Evidence[0].EventId="invented-event";AfterFirstRequest?.Invoke();}
  return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=JsonSerializer.Serialize(result,DataJson.Options)}}}}),Encoding.UTF8,"application/json")};
 }
}
sealed class ProbeHandler(HttpStatusCode status):HttpMessageHandler{
 public string Body="";
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Body=await request.Content!.ReadAsStringAsync(ct);return new(status){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content="{\"ok\":true}"}}}}))};}
}
