using CS2Review.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

var failures = 0;
void Test(string name, Action body) { try { body(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, DataJson.Options);
int? Int(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : value.GetProperty(field).GetInt32();
bool? Bool(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : value.GetProperty(field).GetBoolean();
RoundData Round(bool advantage = false) {
 var r = new RoundData { Number=1,IsValid=true,Winner="T",StartedAt=100,EndedAt=200,FreezeEndedAt=105,BombTimerSeconds=40,
  Participants=[new(){PlayerId="p",Side="T"},new(){PlayerId="mate",Side="T"},new(){PlayerId="e",Side="CT"}] };
 r.Participants.Add(new(){PlayerId=advantage?"mate2":"e2",Side=advantage?"T":"CT"});
 r.EconomySnapshots=r.Participants.Select(p=>new PlayerState{PlayerId=p.PlayerId,Side=p.Side,Time=105,Tick=5,Health=100,EquipmentValue=0}).ToList();return r;
}
GameEvent Event(RoundData r,string id,string kind,double time,int tick,string? actor,string? target=null,int? actorHp=100,int? targetHp=0) {
 PlayerState? State(string? player,int? hp)=>player is null?null:new(){PlayerId=player,Side=r.Participants.Single(p=>p.PlayerId==player).Side,Time=time,Tick=tick,Health=hp};
 return new(){Id=id,Kind=kind,Time=time,Tick=tick,ActorId=actor,TargetId=target,ActorState=State(actor,actorHp),TargetState=State(target,targetHp),Damage=kind=="damage"?20:0};
}
MatchData Match(RoundData r) => new(){Id="canonical",SourceSha256=new string('a',64),SchemaVersion=2,Players=[new(){Id="p",Name="玩家"}],Rounds=[r]};
PlayerAnalysis Analyze(RoundData r)=>new AnalysisEngine().Analyze(Match(r),"p");
AiReport Local(MatchData match,PlayerAnalysis a,CancellationToken ct=default,int? max=null) => new LocalReportService().Generate(match,a,max.HasValue ? new(max.Value):null,ct);
T WithField<T>(T value,string field,JsonNode? observation) { var node=JsonSerializer.SerializeToNode(value,DataJson.Options)!;node[field]=observation;return node.Deserialize<T>(DataJson.Options)!; }

if(args.Length>1 && args[0]=="--validate-real")
{
 foreach(var path in args.Skip(1)) Test("Real snapshot all players "+Path.GetFileName(Path.GetDirectoryName(path)),()=>{
  var m=JsonSerializer.Deserialize<MatchData>(File.ReadAllText(path),DataJson.Options) ?? throw new Exception("empty real snapshot");
  var eventIndex=m.Rounds.Where(r=>r.IsValid && r.Winner is "T" or "CT").SelectMany(r=>r.Events.Select(e=>(Round:r.Number,Event:e))).Where(x=>double.IsFinite(x.Event.Time)).ToDictionary(x=>x.Event.Id);
  var totalFindings=0;var advanced=0;var totalReferences=0;var unknownPoints=0;
  foreach(var player in m.Players){
   var a=new AnalysisEngine().Analyze(m,player.Id);var report=Local(m,a);Eq("raw-v2",a.AnalysisVersion);Eq("rules-v3",a.RulesVersion);Eq("local",report.Source);
   var contexts=m.Rounds.Where(r=>r.IsValid && r.Participants.Any(p=>p.PlayerId==player.Id)).ToDictionary(r=>r.Number,r=>new RoundContextAnalyzer().Analyze(r,player.Id));
   unknownPoints+=contexts.Values.Sum(c=>c.Timeline.Count(p=>p.OwnAlive is null||p.EnemyAlive is null));
   foreach(var f in a.Findings){totalFindings++;if(f.EventIds.Count==0)throw new Exception("finding missing evidence");var anchors=f.EventIds.Select(id=>eventIndex[id]).ToList();if(!anchors.Any(x=>x.Round==f.Round && x.Event.Time==f.Time && x.Event.Tick==f.Tick))throw new Exception("finding anchor mismatch");foreach(var x in anchors)Eq(f.Round,x.Round);
    if(f.Kind is "clutch" or "advantage_loss"){advanced++;var point=contexts[f.Round].Timeline.Single(p=>p.EventId==anchors.First(x=>x.Event.Time==f.Time&&x.Event.Tick==f.Tick).Event.Id);
     if(f.Kind=="clutch"){Eq<int?>(1,point.OwnAlive);if(point.EnemyAlive is not >0)throw new Exception("unknown clutch enemy count");Eq<bool?>(true,point.PlayerAliveAfter);}
     else{if(point.OwnAliveBefore is not int own||point.EnemyAliveBefore is not int enemy||own<=enemy||enemy<=0)throw new Exception("unknown advantage counts");Eq<bool?>(true,point.PlayerAliveBefore);Eq<bool?>(false,point.PlayerAliveAfter);}
    }
   }
   foreach(var reference in report.Strengths.Concat(report.Priorities).SelectMany(i=>i.Evidence)){totalReferences++;var actual=eventIndex[reference.EventId];Eq(actual.Round,reference.Round);Eq(actual.Event.Time,reference.Time);}
   if(report.Priorities.Count>3)throw new Exception("priority budget exceeded");
  }
  Console.WriteLine($"REAL schema={m.SchemaVersion} rounds={m.Rounds.Count(r=>r.IsValid)} players={m.Players.Count} findings={totalFindings} reliableAdvanced={advanced} reportReferences={totalReferences} unknownTimelinePoints={unknownPoints}");
 });
 Environment.ExitCode=failures==0?0:1;return;
}

Test("Legacy metadata defaults are preserved while current analysis is rules-v3",()=>{
 Eq("rules-v1",Json(JsonSerializer.Deserialize<PlayerAnalysis>("{}",DataJson.Options)!).GetProperty("rulesVersion").GetString());
 Eq("rules-v3",Json(Analyze(Round())).GetProperty("rulesVersion").GetString());
 var old=Json(JsonSerializer.Deserialize<AiReport>("{}",DataJson.Options)!);Eq("cloud",old.GetProperty("source").GetString());Eq("rules-v1",old.GetProperty("rulesVersion").GetString());Eq("context-v1",old.GetProperty("contextVersion").GetString());
});
Test("Context has reliable before and after counts at first post-freeze death",()=>{
 var r=Round();r.Events=[Event(r,"death","kill",110,10,"e","mate")];var c=new RoundContextAnalyzer().Analyze(r,"p");Eq("context-v2",c.ContextVersion);var p=Json(c.Timeline.Single());Eq<int?>(2,Int(p,"ownAliveBefore"));Eq<int?>(2,Int(p,"enemyAliveBefore"));Eq<int?>(1,Int(p,"ownAlive"));Eq<bool?>(true,Bool(p,"playerAliveBefore"));Eq<bool?>(true,Bool(p,"playerAliveAfter"));
 Eq(1,Analyze(r).Findings.Count(f=>f.Kind=="clutch"));
});
Test("Same-clock recorded post-outcome kill is excluded from the context timeline",()=>{
 var r=Round();r.StatisticsEndedAt=205;var exit=WithField(Event(r,"exit", "kill",200,100,"e","mate"),"afterRoundEnd",JsonValue.Create(true));r.Events=[exit];
 Eq(0,new RoundContextAnalyzer().Analyze(r,"p").Timeline.Count);Eq(0,Analyze(r).Findings.Count);
});
Test("Context findings reject recorded post-outcome evidence at the outcome clock",()=>{
 var r=Round(true);var exit=WithField(Event(r,"exit", "kill",200,100,"e","p"),"afterRoundEnd",JsonValue.Create(true));r.Events=[exit];
 var timeline=JsonSerializer.Deserialize<RoundContext>("{\"number\":1,\"timeline\":[{\"eventId\":\"exit\",\"kind\":\"kill\",\"time\":200,\"tick\":100,\"ownAliveBefore\":3,\"enemyAliveBefore\":1,\"ownAlive\":2,\"enemyAlive\":1,\"playerAliveBefore\":true,\"playerAliveAfter\":false}]}",DataJson.Options)!;
 Eq(0,new ContextFindingAnalyzer().Analyze(r,"p",timeline).Count);
});
Test("Raw fatal hurt health overrides a stale living entity state before kill callback",()=>{
 var r=Round(true);var fatal=WithField(Event(r,"fatal-hurt","damage",110,10,"e","p",100,100),"targetHealthAfter",JsonValue.Create(0));r.Events=[fatal,Event(r,"late-positive","damage",111,11,"e","p",100,100)];
 var timeline=new RoundContextAnalyzer().Analyze(r,"p").Timeline;Eq<int?>(2,timeline[0].OwnAlive);Eq<bool?>(false,timeline[0].PlayerAliveAfter);Eq<bool?>(false,timeline[1].PlayerAliveAfter);
 var finding=Analyze(r).Findings.Single(f=>f.Kind=="advantage_loss");Eq("fatal-hurt",finding.EventIds.Single());
});
Test("Raw nonfatal hurt health overrides a stale zero entity health",()=>{
 var r=Round(true);var hurt=WithField(Event(r,"nonfatal","damage",110,10,"e","p",100,0),"targetHealthAfter",JsonValue.Create(58));r.Events=[hurt];
 var point=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Single();Eq<int?>(3,point.OwnAlive);Eq<bool?>(true,point.PlayerAliveAfter);Eq(0,Analyze(r).Findings.Count(f=>f.Kind=="advantage_loss"));
});
Test("Self-hurt raw nonfatal health also overrides the same actor's stale zero snapshot",()=>{
 var r=Round(true);r.Events=[WithField(Event(r,"self-hurt","damage",110,10,"p","p",0,0),"targetHealthAfter",JsonValue.Create(58))];
 var point=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Single();Eq<int?>(3,point.OwnAlive);Eq<bool?>(true,point.PlayerAliveAfter);Eq(0,Analyze(r).Findings.Count(f=>f.Kind=="advantage_loss"));
});
Test("Invalid or missing raw hurt health keeps legacy entity-state fallback",()=>{
 foreach(var hp in new int?[]{null,-1,101}){var r=Round(true);r.Events=[WithField(Event(r,"fatal","damage",110,10,"e","p",100,0),"targetHealthAfter",JsonValue.Create(hp))];Eq<bool?>(false,new RoundContextAnalyzer().Analyze(r,"p").Timeline.Single().PlayerAliveAfter);}
});
Test("Missing freeze or incomplete health cannot fabricate advantage or clutch",()=>{
 foreach(var freezeMissing in new[]{true,false}){
 var r=Round(true);if(freezeMissing)r.FreezeEndedAt=null;else r.EconomySnapshots.Single(p=>p.PlayerId=="mate2").Health=null;r.Events=[Event(r,"death","kill",110,10,"e","p")];Eq(0,Analyze(r).Findings.Count(f=>f.Kind=="advantage_loss"));
 var c=Round();if(freezeMissing)c.FreezeEndedAt=null;else c.EconomySnapshots.Single(p=>p.PlayerId=="e2").Health=null;c.Events=[Event(c,"death","kill",110,10,"e","mate")];Eq(0,Analyze(c).Findings.Count(f=>f.Kind=="clutch"));
 }
});
Test("Fatal damage owns advantage transition and later kill cannot restore the dead",()=>{
 var r=Round(true);var hurt=Event(r,"fatal","damage",110,10,"e","p");r.Events=[hurt,Event(r,"kill","kill",110,10,"e","p"),Event(r,"posthumous","damage",111,11,"p","e",100,80)];var c=new RoundContextAnalyzer().Analyze(r,"p");var fatal=Json(c.Timeline[0]);Eq<int?>(3,Int(fatal,"ownAliveBefore"));Eq<int?>(2,Int(fatal,"ownAlive"));Eq<bool?>(true,Bool(fatal,"playerAliveBefore"));Eq<bool?>(false,Bool(fatal,"playerAliveAfter"));
 var later=Json(c.Timeline[1]);Eq<int?>(2,Int(later,"ownAliveBefore"));Eq<bool?>(false,Bool(later,"playerAliveBefore"));Eq<bool?>(false,Bool(Json(c.Timeline[2]),"playerAliveAfter"));var findings=Analyze(r).Findings.Where(f=>f.Kind=="advantage_loss").ToList();Eq(1,findings.Count);Eq("fatal",findings.Single().EventIds.Single());
});
Test("Same-clock future freeze snapshot never fills before or player alive",()=>{
 var r=Round();r.Events=[Event(r,"early","shot",105,4,"p"),Event(r,"ready","shot",105,5,"p")];var c=new RoundContextAnalyzer().Analyze(r,"p");var early=Json(c.Timeline[0]);Eq<int?>(null,Int(early,"ownAliveBefore"));Eq<bool?>(null,Bool(early,"playerAliveBefore"));Eq<bool?>(null,Bool(early,"playerAliveAfter"));Eq<int?>(2,Int(Json(c.Timeline[1]),"ownAliveBefore"));
});
Test("Dead target player never enters another survivor's clutch",()=>{
 var r=Round();r.Events=[Event(r,"p-death","kill",110,10,"e","p")];Eq(0,Analyze(r).Findings.Count(f=>f.Kind=="clutch"));
});
Test("Initial zero health does not count alive and may record a single-person state",()=>{
 var r=Round();r.EconomySnapshots.Single(p=>p.PlayerId=="mate").Health=0;r.Events=[Event(r,"shot","shot",110,10,"p")];var point=Json(new RoundContextAnalyzer().Analyze(r,"p").Timeline.Single());Eq<int?>(1,Int(point,"ownAliveBefore"));Eq(1,Analyze(r).Findings.Count(f=>f.Kind=="clutch"));
});
Test("Local report needs no key and preserves exact evidence with unavailable ratings",()=>{
 var r=Round(true);r.Events=[Event(r,"death","kill",110,10,"e","p")];var m=Match(r);var a=Analyze(r);var report=Local(m,a);var j=Json(report);Eq("local",j.GetProperty("source").GetString());Eq("local-rules-v1",report.Model);Eq("rules-v3",j.GetProperty("rulesVersion").GetString());Eq("context-v2",j.GetProperty("contextVersion").GetString());Eq<double?>(null,a.Summary.Rating);Eq<double?>(null,a.Summary.RoundSwing);
 if(!report.Summary.Contains("未校准"))throw new Exception("missing calibration reason");if(report.Priorities.Count is <1 or >3)throw new Exception("missing or excessive priorities");foreach(var e in report.Strengths.Concat(report.Priorities).SelectMany(i=>i.Evidence)){Eq("death",e.EventId);Eq(1,e.Round);Eq(110d,e.Time);}
 if(report.Priorities.Any(p=>string.IsNullOrWhiteSpace(p.Training)||string.IsNullOrWhiteSpace(p.FollowUpMetric)))throw new Exception("missing training or follow-up");
});
Test("No events or zero valid rounds produce no invented report item",()=>{
 foreach(var valid in new[]{true,false}){var r=Round();r.IsValid=valid;var m=Match(r);var report=Local(m,Analyze(r));Eq(0,report.Strengths.Count);Eq(0,report.Priorities.Count);if(string.IsNullOrWhiteSpace(report.Summary))throw new Exception("empty summary");}
});
Test("Priority limit can reduce suggestions without filler",()=>{
 var r=Round();r.Events=[Event(r,"team","damage",110,10,"p","mate",100,80),Event(r,"first-death","kill",111,11,"e","p")];var m=Match(r);var a=Analyze(r);Eq(0,Local(m,a,max:0).Priorities.Count);Eq(1,Local(m,a,max:1).Priorities.Count);if(Local(m,a).Priorities.Count>3)throw new Exception("too many priorities");
 try{Local(m,a,max:4);throw new Exception("invalid priority limit accepted");}catch(ArgumentOutOfRangeException){}
});
Test("Local report rejects references from a different persisted snapshot",()=>{
 var r=Round();r.Events=[Event(r,"kill","kill",110,10,"p","e")];var a=Analyze(r);a.Findings.Single(f=>f.Kind=="first_kill").EventIds=["not-saved"];try{Local(Match(r),a);throw new Exception("foreign reference accepted");}catch(InvalidDataException){}
});
Test("Local report respects pre-cancellation",()=>{
 var r=Round();using var cancel=new CancellationTokenSource();cancel.Cancel();try{Local(Match(r),Analyze(r),cancel.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
});
Test("Large context and local report stay bounded and can cancel",()=>{
 var r=Round();r.EndedAt=1500;r.Events=Enumerable.Range(0,80000).Select(i=>new GameEvent{Id="shot-"+i,Kind="shot",Time=110+i/64d,Tick=i+10,ActorId="p"}).ToList();var m=Match(r);var analysisClock=System.Diagnostics.Stopwatch.StartNew();var a=Analyze(r);analysisClock.Stop();var clock=System.Diagnostics.Stopwatch.StartNew();var report=Local(m,a);clock.Stop();Eq(0,report.Strengths.Count);Eq(0,report.Priorities.Count);if(report.Summary.Length>3000)throw new Exception("unbounded summary");Console.WriteLine($"BENCH localEvents=80000 analysisElapsedMs={analysisClock.ElapsedMilliseconds} reportElapsedMs={clock.ElapsedMilliseconds}");
 using var cancel=new CancellationTokenSource();cancel.Cancel();try{Local(m,a,cancel.Token);throw new Exception("large report ignored requested cancellation");}catch(OperationCanceledException){}
});
Test("Same-time tick source order controls the reliable clutch transition",()=>{
 foreach(var playerDiesFirst in new[]{true,false}){var r=Round();var own=Event(r,"z-own-death","kill",110,10,"e","mate");var player=Event(r,"a-player-death","kill",110,10,"e","p");r.Events=playerDiesFirst?[player,own]:[own,player];Eq(playerDiesFirst?0:1,Analyze(r).Findings.Count(f=>f.Kind=="clutch"));}
});
Test("Team kills and suicides update reliable counts without changing raw formulas",()=>{
 var r=Round();r.Events=[Event(r,"team-kill","kill",110,10,"p","mate"),Event(r,"suicide","kill",111,11,"p","p",0,0)];var a=Analyze(r);Eq(0,a.Summary.Kills);Eq(1,a.Summary.TeamKills);Eq(1,a.Summary.Suicides);Eq(1,a.Summary.Deaths);Eq("raw-v2",a.AnalysisVersion);Eq(1,a.Findings.Count(f=>f.Kind=="clutch"));var c=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(1,c.Timeline[0].OwnAlive);Eq<int?>(0,c.Timeline[1].OwnAlive);Eq<bool?>(false,c.Timeline[1].PlayerAliveAfter);
});
Test("An individual reliable observation does not repair incomplete team coverage",()=>{
 var r=Round();r.EconomySnapshots.Single(p=>p.PlayerId=="p").Health=null;r.Events=[Event(r,"hurt","damage",110,10,"p","e",100,80)];var p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Single();Eq<bool?>(null,p.PlayerAliveBefore);Eq<bool?>(true,p.PlayerAliveAfter);Eq<int?>(null,p.OwnAliveBefore);Eq<int?>(null,p.OwnAlive);Eq(0,Analyze(r).Findings.Count(f=>f.Kind is "clutch" or "advantage_loss"));
});
Test("Local report metadata and references survive SQLite close and reopen",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"history.db");var r=Round();r.Events=[Event(r,"first","kill",110,10,"p","e")];var m=Match(r);var report=Local(m,Analyze(r));
 using(var store=new HistoryStore(path)){store.InitializeAsync().GetAwaiter().GetResult();store.SaveMatchAsync(m).GetAwaiter().GetResult();store.SaveReportAsync(m.Id,"p",report).GetAwaiter().GetResult();}
 using var reopened=new HistoryStore(path);reopened.InitializeAsync().GetAwaiter().GetResult();var saved=reopened.ListReportsAsync(m.Id,"p").GetAwaiter().GetResult().Single();Eq("local",saved.Source);Eq("rules-v3",saved.RulesVersion);Eq("context-v2",saved.ContextVersion);Eq("first",saved.Strengths.Single().Evidence.Single().EventId);
});
Test("Current engine and context findings respect cancellation",()=>{
 var r=Round();using var cancel=new CancellationTokenSource();cancel.Cancel();try{new AnalysisEngine().Analyze(Match(r),"p",cancel.Token);throw new Exception("engine ignored cancel");}catch(OperationCanceledException){}
 try{new ContextFindingAnalyzer().Analyze(r,"p",new RoundContext(),cancel.Token);throw new Exception("findings ignored cancel");}catch(OperationCanceledException){}
});
Environment.ExitCode=failures==0?0:1;
