using CS2Review.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

if(args.Length==3 && args[0]=="--analyze")
{
 var data=JsonSerializer.Deserialize<MatchData>(await File.ReadAllTextAsync(args[1]),DataJson.Options)!;
 Console.WriteLine(JsonSerializer.Serialize(new AnalysisEngine().Analyze(data,args[2]),DataJson.Options));
 return;
}

var failures = new List<string>();
void Test(string name, Action body) { try { body(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
RoundData Round(int n) => new() { Number = n, IsValid = true, Winner = "T", StartedAt = n * 100, EndedAt = n * 100 + 90, Participants = [new() { PlayerId = "p", Side = "T" },new() {PlayerId="mate",Side="T"},new() {PlayerId="e",Side="CT"},new() {PlayerId="e2",Side="CT"},new() {PlayerId="e3",Side="CT"}] };
GameEvent Kill(string id, double time, string actor, string target, string? assist=null) => new() { Id=id,Kind="kill",Time=time,ActorId=actor,TargetId=target,AssisterId=assist };
GameEvent Damage(string id,double time,string target,int damage) => new() { Id=id,Kind="damage",Time=time,ActorId="p",TargetId=target,Damage=damage,Weapon="ak47" };
MatchData Match(params RoundData[] rounds) => new() { Id="match1",SourceSha256=new string('a',64),Players=[new() {Id="p",Name="Player"}],Rounds=rounds.ToList() };
PlayerAnalysis Analyze(params RoundData[] rounds) => new AnalysisEngine().Analyze(Match(rounds),"p");
T WithField<T>(T value,string field,JsonNode? observation) { var node=JsonSerializer.SerializeToNode(value,DataJson.Options)!;node[field]=observation;return node.Deserialize<T>(DataJson.Options)!; }

Test("Recorded statistics settlement includes exit combat but no later cleanup",()=>{
 var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(195d));r.Survivors=["p"];
 r.Events=[Kill("exit-kill",191,"p","e"),Damage("exit-damage",192,"e2",30),Kill("exit-death",194,"e2","p"),Kill("later-cleanup",195.001,"p","e3"),Damage("before-start",99,"e3",100)];
 var a=Analyze(r);Eq(1,a.Summary.Kills);Eq(1,a.Summary.Deaths);Eq(30,a.Summary.Damage);Eq(0d,a.Summary.SurvivalPercent);Eq(100d,a.Summary.Kast);
 Eq(0,a.Summary.FirstKills);Eq(0,a.Summary.FirstDeaths);Eq(0,a.Summary.TradeKills);Eq(0,a.Findings.Count);
 Eq(0,new RoundContextAnalyzer().Analyze(r,"p").Timeline.Count);
});
Test("Exit revenge does not turn into competitive opening or trade evidence",()=>{
 var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(195d));r.Events=[Kill("competitive-death",189,"e","mate"),Kill("exit-revenge",191,"p","e")];
 var a=Analyze(r);Eq(1,a.Summary.Kills);Eq(0,a.Summary.FirstKills);Eq(0,a.Summary.TradeKills);Eq(0,a.Findings.Count(f=>f.Kind is "first_kill" or "trade_kill"));
});
Test("Absent or reversed settlement preserves the legacy cleanup cutoff",()=>{
 foreach(var end in new double?[]{null,189d}){var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(end));r.Events=[Kill("cleanup",191,"p","e"),Damage("cleanup-hit",192,"e2",80)];var a=Analyze(r);Eq(0,a.Summary.Kills);Eq(0,a.Summary.Damage);}
});
Test("Same-tick raw damage uses the observed integer health for the following fatal hit",()=>{
 var first=WithField(Damage("z-first",110,"e",41),"targetHealthAfter",JsonValue.Create(58));first.Tick=42;
 var fatal=WithField(Damage("a-fatal",110,"e",100),"targetHealthAfter",JsonValue.Create(0));fatal.Tick=42;
 var death=Kill("death",110,"p","e");death.Tick=42;var r=Round(1);r.Events=[first,fatal,death];Eq(99,Analyze(r).Summary.Damage);
});
Test("Other-player observed health limits selected player's fatal credit",()=>{
 var first=WithField(Damage("mate-hit",110,"e",41),"targetHealthAfter",JsonValue.Create(58));first.ActorId="mate";
 var fatal=WithField(Damage("p-fatal",111,"e",59),"targetHealthAfter",JsonValue.Create(0));var r=Round(1);r.Events=[first,fatal];Eq(58,Analyze(r).Summary.Damage);
});
Test("World and team hurt observations also settle the target health ledger",()=>{
 var world=WithField(Damage("world-hit",110,"e",15),"targetHealthAfter",JsonValue.Create(84));world.ActorId=null;
 var friendly=WithField(Damage("team-hit",111,"e",4),"targetHealthAfter",JsonValue.Create(79));friendly.ActorId="e2";
 var fatal=WithField(Damage("p-fatal",112,"e",100),"targetHealthAfter",JsonValue.Create(0));var r=Round(1);r.Events=[world,friendly,fatal];Eq(79,Analyze(r).Summary.Damage);
});
Test("Invalid observed health falls back without restoring confirmed deaths",()=>{
 foreach(var hp in new[]{-1,101}){var first=WithField(Damage("hit",110,"e",40),"targetHealthAfter",JsonValue.Create(hp));var r=Round(1);r.Events=[first,Damage("fatal",111,"e",80)];Eq(100,Analyze(r).Summary.Damage);}
 var dead=Round(1);dead.Events=[Damage("hit",110,"e",40),Kill("dead",111,"mate","e"),WithField(Damage("posthumous",112,"e",100),"targetHealthAfter",JsonValue.Create(100))];Eq(40,Analyze(dead).Summary.Damage);
});
Test("Observed zero health prevents late positive hurt callbacks restoring a dead target",()=>{
 var fatal=WithField(Damage("fatal-hurt",110,"e",50),"targetHealthAfter",JsonValue.Create(0));
 var late=WithField(Damage("late-positive",110,"e",10),"targetHealthAfter",JsonValue.Create(100));var r=Round(1);r.Events=[fatal,late,Damage("duplicate",110,"e",100),Kill("later-kill-callback",110,"p","e")];Eq(50,Analyze(r).Summary.Damage);
});
Test("Local report uses current raw analysis while excluding exit combat findings",()=>{
 var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(195d));var hit=Damage("exit-nade",191,"e",40);hit.Weapon="hegrenade";r.Events=[hit,Kill("exit-kill",192,"p","e")];
 var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");var report=new LocalReportService().Generate(m,a);Eq("raw-v2",report.AnalysisVersion);Eq("rules-v3",report.RulesVersion);Eq(40,a.Summary.Damage);Eq(0,report.Strengths.Count);Eq(0,report.Priorities.Count);
});
Test("Same-clock recorded post-outcome kill counts raw but cannot become an opening",()=>{
 var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(195d));var exit=WithField(Kill("exit-at-outcome-clock",190,"p","e"),"afterRoundEnd",JsonValue.Create(true));exit.Tick=100;
 r.Events=[exit];var a=Analyze(r);Eq(1,a.Summary.Kills);Eq(0,a.Summary.FirstKills);Eq(0,a.Findings.Count);
});
Test("Same-clock post-outcome utility and team hurt cannot generate competitive findings",()=>{
 var r=WithField(Round(1),"statisticsEndedAt",JsonValue.Create(195d));var utility=Damage("exit-utility",190,"e",30);utility.Weapon="hegrenade";
 r.Events=[WithField(utility,"afterRoundEnd",JsonValue.Create(true)),WithField(Damage("exit-team",190,"mate",15),"afterRoundEnd",JsonValue.Create(true))];
 var a=Analyze(r);Eq(30,a.Summary.UtilityDamage);Eq(15,a.Summary.TeamDamage);Eq(0,a.Findings.Count);
});

Test("KAST is a union, side rates use participation", () => {
 var a=Round(1); a.Events=[Kill("k",110,"p","e")]; a.Survivors=["p"];
 var b=Round(2); b.Events=[Kill("d",210,"e","p")];
 var c=Round(3); c.Participants.RemoveAll(x=>x.PlayerId=="p");
 var s=Analyze(a,b,c).Summary; Eq(2,s.Rounds); Eq(50d,s.Kast); Eq(.5,s.Kpr); Eq(.5,s.Dpr); Eq(50d,s.SurvivalPercent);
});
Test("5 second trade boundary inclusive, later excluded",()=>{
 var a=Round(1);a.Events=[Kill("d",110,"e","p"),Kill("t",115,"mate","e")];
 var b=Round(2);b.Events=[Kill("d2",210,"e","p"),Kill("t2",215.001,"mate","e")];
 var s=Analyze(a,b).Summary;Eq(1,s.TradedDeaths);Eq(50d,s.Kast);
});
Test("Multi kill buckets are exclusive",()=>{
 var a=Round(1);a.Events=[Kill("k1",110,"p","e"),Kill("k2",111,"p","e2"),Kill("k3",112,"p","e3")];
 var s=Analyze(a).Summary;Eq(0,s.MultiKill2);Eq(1,s.MultiKill3);Eq(100d,s.MultiKillPercent);
});
Test("Enemy health damage excludes team damage, invalid and cleanup",()=>{
 var a=Round(1);a.Events=[Damage("over",110,"e",140),Damage("team",112,"mate",30),Damage("cleanup",191,"e2",90)];
 var b=Round(2);b.IsValid=false;b.Events=[Damage("warmup",210,"e",100)];
 var s=Analyze(a,b).Summary;Eq(100,s.Damage);Eq(30,s.TeamDamage);Eq(100d,s.Adr);
});
Test("Teamkill and suicide separated from enemy kills",()=>{
 var a=Round(1);a.Events=[Kill("tk",110,"p","mate"),Kill("suicide",120,"p","p")];
 var s=Analyze(a).Summary;Eq(0,s.Kills);Eq(1,s.Deaths);Eq(1,s.TeamKills);Eq(1,s.Suicides);Eq(0d,s.Kast);
});
Test("40 damage scoring assist independent from demo assist",()=>{
 var a=Round(1);a.Events=[Damage("hit",110,"e",40),Kill("a",120,"mate","e")];
 var b=Round(2);b.Events=[Kill("b",220,"mate","e","p")];
 var s=Analyze(a,b).Summary;Eq(50d,s.Kast);Eq(50d,s.ScoringKast);Eq(1,s.Assists);
});
Test("Disconnected missing survivor is not survival, unavailable rating",()=>{
 var s=Analyze(Round(1)).Summary;Eq(0d,s.SurvivalPercent);Eq(0d,s.Kast);Eq<double?>(null,s.Rating);Eq<double?>(null,s.RoundSwing);
 if(string.IsNullOrWhiteSpace(s.RatingUnavailableReason))throw new Exception("missing reason");
});
Test("Damage consumed by other players is not counted twice",()=>{
 var r=Round(1);var first=Damage("mate-hit",110,"e",70);first.ActorId="mate";r.Events=[first,Damage("p-hit",111,"e",80)];
 Eq(30,Analyze(r).Summary.Damage);
});
Test("T CT aggregate counts, not mean of side ratios",()=>{
 var t=Round(1);t.Events=[Kill("t-k",110,"p","e")];
 var ct=Round(2);ct.Participants.ForEach(x=>x.Side=x.Side=="T"?"CT":"T");
 var ct2=Round(3);ct2.Participants.ForEach(x=>x.Side=x.Side=="T"?"CT":"T");
 var a=Analyze(t,ct,ct2);Eq(1,a.T.Rounds);Eq(2,a.CT.Rounds);Eq(1d,a.T.Kpr);Eq(0d,a.CT.Kpr);Eq(1d/3,a.Summary.Kpr);
});
Test("Findings preserve exact event provenance and no spatial certainty",()=>{
 var r=Round(1);r.Events=[Kill("mate-death",110,"e","mate"),Damage("nade",112,"e",30),Kill("trade",114,"p","e")];r.Events[1].Weapon="hegrenade";
 var a=Analyze(r);Eq(30,a.Summary.UtilityDamage);Eq(1,a.Summary.TradeKills);
 if(a.Findings.Any(f=>f.Kind=="clutch")||!a.Findings.Any(f=>f.Kind=="utility_damage"))throw new Exception("unknown initial health must not produce a clutch candidate");
 var f=a.Findings.Single(f=>f.Kind=="trade_kill");Eq(114d,f.Time);Eq(1,f.Round);Eq(2,f.EventIds.Count);
 if(a.Findings.Any(f=>f.Kind is "line_of_sight" or "crosshair_error"))throw new Exception("spatial certainty invented");
});
Test("Utility damage recognizes parser equipment display names",()=>{
 foreach(var weapon in new[]{"HE Grenade","Incendiary Grenade","Molotov"}){
 var r=Round(1);var hit=Damage("utility",110,"e",30);hit.Weapon=weapon;r.Events=[hit];Eq(30,Analyze(r).Summary.UtilityDamage);
 }
});
Test("SQLite reopen and SHA dedup preserve original match and versioned reports",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"history.db");
 string id;
 using(var store=new HistoryStore(path)) {
 store.InitializeAsync().GetAwaiter().GetResult();id=store.SaveMatchAsync(Match(Round(1))).GetAwaiter().GetResult();
 var duplicate=Match(Round(2));duplicate.Id="another-id";Eq(id,store.SaveMatchAsync(duplicate).GetAwaiter().GetResult());
 store.SaveReportAsync(id,"p",new AiReport {AnalysisVersion="v1",Summary="first"}).GetAwaiter().GetResult();
 store.SaveReportAsync(id,"p",new AiReport {AnalysisVersion="v2",Summary="second"}).GetAwaiter().GetResult();
 }
 using(var store=new HistoryStore(path)) {
 store.InitializeAsync().GetAwaiter().GetResult();Eq(1,store.ListMatchesAsync().GetAwaiter().GetResult().Count);
 Eq(1,store.LoadMatchAsync(id).GetAwaiter().GetResult()!.Rounds[0].Number);
 Eq(2,store.ListReportsAsync(id,"p").GetAwaiter().GetResult().Count);
 }
});
Test("AI rejects invented evidence after one repair attempt",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 var response=new AiReport {Summary="摘要",Strengths=[new() {Observation="首杀",Evidence=[new() {EventId="invented",Round=1,Time=110}]}]};
 var handler=new ReportHandler(response);var service=new ReportService(new HttpClient(handler));
 try {service.GenerateFixtureAsync(m,a,new ReportOptions{Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="not-a-real-key"}).GetAwaiter().GetResult();throw new Exception("invented reference accepted");} catch(ReportValidationException) { Eq(2,handler.Calls); }
});
Test("AI rejects invented metrics and times",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 foreach(var item in new[] {new ReportItem {Observation="首杀",Evidence=[new(){EventId="actual",Round=1,Time=111}]},new ReportItem {Observation="首杀",Evidence=[new(){EventId="actual",Round=1,Time=110}],Metrics=new(){["kills"]=99}},new ReportItem {Observation="完成99次击杀",Evidence=[new(){EventId="actual",Round=1,Time=110}]}}) {
 var handler=new ReportHandler(new AiReport {Summary="摘要",Strengths=[item]});
 try {new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new ReportOptions{Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();throw new Exception("false fact accepted");}catch(ReportValidationException){Eq(2,handler.Calls);}
 }
});
Test("AI valid response has local version and excluded raw demo",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);m.SourcePath="PRIVATE_DEMO_PATH";m.Positions=[new(){PlayerId="p",X=12345}];var a=new AnalysisEngine().Analyze(m,"p");
 var handler=new ReportHandler(new AiReport {Summary="本场有可复核首杀。",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId="actual",Round=1,Time=110}],Metrics=new(){["kills"]=1}}]});
 var report=new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new ReportOptions{Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();Eq("raw-v2",report.AnalysisVersion);Eq(1,handler.Calls);
 if(handler.LastBody.Contains("PRIVATE_DEMO_PATH")||handler.LastBody.Contains("12345")||handler.LastBody.Contains("secret"))throw new Exception("private payload leaked");
});
Test("AI training numeric targets allowed and references may use non-finding events",()=>{
 var r=Round(1);r.Events=[Damage("hit",110,"e",20)];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 var handler=new ReportHandler(new AiReport{Summary="存在可复核伤害事件。",Strengths=[new(){Observation="造成有效伤害",Inference="需进一步复核上下文",Training="每天练习10分钟",Evidence=[new(){EventId="hit",Round=1,Time=110}],Metrics=new(){["damage"]=20}}]});
 var report=new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new ReportOptions{Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();Eq("每天练习10分钟",report.Strengths[0].Training);
});
Test("AI timeout and cancellation preserve local statistics",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 var opts=new ReportOptions{Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret",Timeout=TimeSpan.FromMilliseconds(20)};
 try{new ReportService(new HttpClient(new WaitingHandler())).GenerateFixtureAsync(m,a,opts).GetAwaiter().GetResult();throw new Exception("timeout ignored");}catch(TimeoutException){}
 using var cancelled=new CancellationTokenSource();cancelled.Cancel();
 try{new ReportService(new HttpClient(new WaitingHandler())).GenerateFixtureAsync(m,a,opts,cancelled.Token).GetAwaiter().GetResult();throw new Exception("cancellation ignored");}catch(OperationCanceledException){}
 Eq(1,a.Summary.Kills);
});
Test("AI unknown metrics, null lists, excessive priorities rejected",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 ReportItem Item()=>new(){Observation="首杀",Evidence=[new(){EventId="actual",Round=1,Time=110}]};
 var unknown=Item();unknown.Metrics["rating"]=1.5;
 foreach(var invalid in new[]{new AiReport{Summary="摘要",Strengths=[unknown]},new AiReport{Summary="摘要",Strengths=null!},new AiReport{Summary="摘要",Priorities=[Item(),Item(),Item(),Item()]}}){
 var handler=new ReportHandler(invalid);
 try{new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();throw new Exception("invalid contract accepted");}catch(ReportValidationException){Eq(2,handler.Calls);}
 }
});
Test("History cancelled save has no partially visible match",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
 using var store=new HistoryStore(Path.Combine(dir,"history.db"));store.InitializeAsync().GetAwaiter().GetResult();using var cancelled=new CancellationTokenSource();cancelled.Cancel();
 try{store.SaveMatchAsync(Match(Round(1)),cancelled.Token).GetAwaiter().GetResult();throw new Exception("cancelled save accepted");}catch(OperationCanceledException){}
 Eq(0,store.ListMatchesAsync().GetAwaiter().GetResult().Count);
});
Test("AI rejects Chinese percentages, quantity classifiers and Unicode numerals",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];r.Survivors=["p"];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 foreach(var text in new[]{"KAST为百分之九十九","本场九十九名敌人被击杀","完成Ⅸ次击杀","击杀９９位敌人","完成⑨次击杀","本场有两名敌人被击杀"}){
 var handler=new ReportHandler(new(){Summary="摘要",Strengths=[new(){Observation=text,Evidence=[new(){EventId="actual",Round=1,Time=110}]}]});
 try{new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();throw new Exception("numeric fact accepted: "+text);}catch(ReportValidationException){Eq(2,handler.Calls);}
 }
});
Test("Trade cannot match future death at equal time, tick or source order",()=>{
 foreach(var sameTick in new[]{false,true}){
 var r=Round(1);var revenge=Kill("z-earlier",110,"mate","e");revenge.Tick=10;var death=Kill("a-later",110,"e","p");death.Tick=sameTick?10:11;r.Events=[revenge,death];
 var s=Analyze(r).Summary;Eq(0,s.TradedDeaths);Eq(0d,s.Kast);
 r.Events=[death,revenge];death.Tick=10;revenge.Tick=10;s=Analyze(r).Summary;Eq(1,s.TradedDeaths);Eq(100d,s.Kast);
 }
});
Test("SHA dedup reports must reference persisted match snapshot and player",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"history.db");using var store=new HistoryStore(path);store.InitializeAsync().GetAwaiter().GetResult();
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var id=store.SaveMatchAsync(Match(r)).GetAwaiter().GetResult();
 var reparse=Round(1);reparse.Events=[Kill("reparse-event",110,"p","e")];Eq(id,store.SaveMatchAsync(Match(reparse)).GetAwaiter().GetResult());
 AiReport Report(string eventId,int round=1,double time=110)=>new(){AnalysisVersion="raw-v2",Summary="摘要",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId=eventId,Round=round,Time=time}]}]};
 foreach(var report in new[]{Report("reparse-event"),Report("actual",2),Report("actual",1,111)}){
 try{store.SaveReportAsync(id,"p",report).GetAwaiter().GetResult();throw new Exception("snapshot mismatch accepted");}catch(InvalidDataException){}
 }
 try{store.SaveReportAsync(id,"missing-player",Report("actual")).GetAwaiter().GetResult();throw new Exception("unknown player accepted");}catch(InvalidDataException){}
 try{store.SaveReportAsync("missing-match","p",Report("actual")).GetAwaiter().GetResult();throw new Exception("unknown match accepted");}catch(InvalidDataException){}
 Eq(0,store.ListReportsAsync(id,"p").GetAwaiter().GetResult().Count);
 store.SaveReportAsync(id,"p",Report("actual")).GetAwaiter().GetResult();Eq(1,store.ListReportsAsync(id,"p").GetAwaiter().GetResult().Count);
 store.Dispose();using var reopened=new HistoryStore(path);reopened.InitializeAsync().GetAwaiter().GetResult();var savedMatch=reopened.LoadMatchAsync(id).GetAwaiter().GetResult()!;
 var savedRef=reopened.ListReportsAsync(id,"p").GetAwaiter().GetResult().Single().Strengths.Single().Evidence.Single();
 Eq("actual",savedRef.EventId);Eq(1,savedRef.Round);Eq(110d,savedRef.Time);Eq("actual",savedMatch.Rounds.Single().Events.Single().Id);
});
Test("AI malformed null event ID uses repair once then fails clearly",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 foreach(var eventId in new string?[]{null,"", " "}){
 var handler=new ReportHandler(new(){Summary="摘要",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId=eventId!,Round=1,Time=110}]}]});
 try{new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();throw new Exception("malformed citation accepted");}catch(ReportValidationException){Eq(2,handler.Calls);}
 }
});
Test("AI single Chinese numeric assertions cannot bypass facts validation",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 foreach(var text in new[]{"本场击杀数为九。","本场死亡数为八。","本场击杀数：九。","本场死亡数:八。","击杀数九","死亡总数八","KAST为九","ADR：八","本场数量是九。","本场总数等于八。","九","九。","数量：九","击杀总计为九。"}){
 foreach(var field in new[]{"summary","observation","inference"}){
 var item=new ReportItem{Observation="首杀",Evidence=[new(){EventId="actual",Round=1,Time=110}]};var response=new AiReport{Summary="摘要",Strengths=[item]};
 if(field=="summary")response.Summary=text;else if(field=="observation")item.Observation=text;else item.Inference=text;
 var handler=new ReportHandler(response);
 try{new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();throw new Exception("single numeric fact accepted: "+text+" in "+field);}catch(ReportValidationException){Eq(2,handler.Calls);}
 }
 }
});
Test("AI ordinary qualitative Chinese and suggestion numbers remain usable",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 var handler=new ReportHandler(new(){Summary="需要进一步复核。",Strengths=[new(){Observation="队友步调一致，应统一行动。",Inference="一般交战上下文仍不足。",Training="建议每天练习十分钟，重复9次。",Evidence=[new(){EventId="actual",Round=1,Time=110}],Metrics=new(){["kills"]=1,["deaths"]=0}}]});
 var report=new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();Eq(1,handler.Calls);Eq("需要进一步复核。",report.Summary);Eq(1d,report.Strengths.Single().Metrics["kills"]);
});
Test("AI malformed first citation can repair successfully once",()=>{
 var r=Round(1);r.Events=[Kill("actual",110,"p","e")];var m=Match(r);var a=new AnalysisEngine().Analyze(m,"p");
 AiReport Response(string? id)=>new(){Summary="摘要",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId=id!,Round=1,Time=110}]}]};
 var handler=new RepairHandler(Response(null),Response("actual"));
 var report=new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret"}).GetAwaiter().GetResult();Eq(2,handler.Calls);Eq("actual",report.Strengths.Single().Evidence.Single().EventId);
});
RoundData ContextRound(int n=1) {
 var r=Round(n);r.FreezeEndedAt=n*100+5;r.BombTimerSeconds=40;
 r.EconomySnapshots=r.Participants.Select(p=>new PlayerState{PlayerId=p.PlayerId,Side=p.Side,Time=n*100+5,Tick=5,EquipmentValue=p.PlayerId=="p"?0:100,Health=100,DefuseKit=false}).ToList();return r;
}
GameEvent Bomb(string id,string kind,double time,bool? kit=null,string? actor="p")=>new(){Id=id,Kind=kind,Time=time,ActorId=actor,HasKit=kit};
Test("Round context equipment requires every player but preserves recorded zero",()=>{
 var r=ContextRound();r.Events=[Damage("hit",110,"e",10)];var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(100,a.OwnEquipmentValue);Eq<int?>(300,a.EnemyEquipmentValue);Eq<int?>(0,a.PlayerStartState!.EquipmentValue);
 r.EconomySnapshots.Single(x=>x.PlayerId=="e3").EquipmentValue=null;a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.EnemyEquipmentValue);Eq<int?>(100,a.OwnEquipmentValue);
 r.EconomySnapshots.RemoveAll(x=>x.PlayerId=="mate");a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.OwnEquipmentValue);
});
Test("Round context alive counts require initial health and preserve zero",()=>{
 var r=ContextRound();r.EconomySnapshots.Single(x=>x.PlayerId=="mate").Health=0;r.Events=[Damage("hit",110,"e",10),Kill("death",111,"e","p")];
 r.Events[0].ActorState=new(){PlayerId="p",Side="T",Time=110,Health=100};r.Events[0].TargetState=new(){PlayerId="e",Side="CT",Time=110,Health=90};r.Events[1].ActorState=new(){PlayerId="e",Side="CT",Time=111,Health=90};r.Events[1].TargetState=new(){PlayerId="p",Side="T",Time=111,Health=0};
 var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(1,a.Timeline[0].OwnAlive);Eq<int?>(3,a.Timeline[0].EnemyAlive);Eq<int?>(0,a.Timeline[1].OwnAlive);
 r.EconomySnapshots.Single(x=>x.PlayerId=="e2").Health=null;a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.Timeline[0].EnemyAlive);
 var old=Round(1);old.Events=[Damage("old",110,"e",10)];a=new RoundContextAnalyzer().Analyze(old,"p");Eq<int?>(null,a.OwnEquipmentValue);Eq<int?>(null,a.Timeline[0].OwnAlive);Eq("unknown",a.Timeline[0].BombState);
});
Test("Round context observes state after events without subtracting damage twice",()=>{
 var r=ContextRound();var hit=Damage("hit",110,"e",100);hit.TargetState=new(){PlayerId="e",Side="CT",Time=110,Health=0};r.Events=[hit];var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(2,a.Timeline.Single().EnemyAlive);
 hit.TargetState.Health=null;a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.Timeline.Single().EnemyAlive);
});
Test("Recorded bomb timer and kit compare inclusive five and ten second boundaries",()=>{
 foreach(var fixture in new[]{(Kit:true,Start:145d,Can:true),(Kit:true,Start:145.001,Can:false),(Kit:false,Start:140d,Can:true),(Kit:false,Start:140.001,Can:false)}){
 var r=ContextRound();r.Events=[Bomb("plant","bomb_planted",110),Bomb("begin","bomb_defuse_start",fixture.Start,fixture.Kit)];var p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Last();Eq<bool?>(fixture.Can,p.DefuseCanFinish);Eq<double?>(fixture.Kit?5:10,p.DefuseRemainingSeconds);Eq<string?>("p",p.ActiveDefuserId);
 }
});
Test("Defuse abort and restart reset timer without borrowing future events",()=>{
 var r=ContextRound();r.BombTimerSeconds=50;r.Events=[Bomb("restart","bomb_defuse_start",150,true),Bomb("plant","bomb_planted",110),Bomb("abort","bomb_defuse_aborted",147),Bomb("begin","bomb_defuse_start",145,false),Damage("later",151,"e",1),Bomb("done","bomb_defused",155),Damage("earlier",109,"e",1)];
 var a=new RoundContextAnalyzer().Analyze(r,"p");var p=a.Timeline.Single(x=>x.EventId=="earlier");Eq("unplanted",p.BombState);Eq<double?>(null,p.BombRemainingSeconds);
 p=a.Timeline.Single(x=>x.EventId=="abort");Eq("planted",p.BombState);Eq<string?>(null,p.ActiveDefuserId);Eq<double?>(null,p.DefuseRemainingSeconds);
 p=a.Timeline.Single(x=>x.EventId=="later");Eq("defusing",p.BombState);Eq<double?>(9,p.BombRemainingSeconds);Eq<double?>(4,p.DefuseRemainingSeconds);Eq<bool?>(true,p.DefuseCanFinish);Eq(51d,p.RoundSeconds);
 p=a.Timeline.Last();Eq("defused",p.BombState);Eq<double?>(null,p.BombRemainingSeconds);
});
Test("Missing timer or kit leaves finish feasibility unknown",()=>{
 var r=ContextRound();r.BombTimerSeconds=null;r.Events=[Bomb("plant","bomb_planted",110),Bomb("begin","bomb_defuse_start",145,true)];var p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Last();Eq<double?>(null,p.BombRemainingSeconds);Eq<bool?>(null,p.DefuseCanFinish);
 r.BombTimerSeconds=40;r.Events[1].HasKit=null;p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Last();Eq<double?>(null,p.DefuseRemainingSeconds);Eq<bool?>(null,p.DefuseCanFinish);
});
Test("History accepts schema two, reopens nullable context, rejects unknown",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"history.db");
 var match=Match(ContextRound());match.SchemaVersion=2;using(var s=new HistoryStore(path)){s.InitializeAsync().GetAwaiter().GetResult();s.SaveMatchAsync(match).GetAwaiter().GetResult();}
 using var reopened=new HistoryStore(path);reopened.InitializeAsync().GetAwaiter().GetResult();var saved=reopened.LoadMatchAsync(match.Id).GetAwaiter().GetResult()!;Eq(2,saved.SchemaVersion);Eq<int?>(0,saved.Rounds.Single().EconomySnapshots.Single(x=>x.PlayerId=="p").EquipmentValue);
 match.SchemaVersion=3;try{reopened.SaveMatchAsync(match).GetAwaiter().GetResult();throw new Exception("unknown version accepted");}catch(ArgumentException){}
});
Test("History comparison filters player and map, weights by actual rounds",()=>{
 var dir=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);using var s=new HistoryStore(Path.Combine(dir,"history.db"));s.InitializeAsync().GetAwaiter().GetResult();
 for(var i=0;i<8;i++){
 var rounds=new List<RoundData>();for(var j=1;j<=(i==0?10:1);j++){var r=Round(j);if(i==0){r.Events=[Kill($"k-{j}",j*100+10,"p","e")];r.Survivors=["p"];}rounds.Add(r);}
 var m=Match(rounds.ToArray());m.Id="history-"+i;m.SourceSha256=i.ToString("x64");m.MapName=i==7?"de_other":"de_test";m.ImportedAt=DateTimeOffset.Parse("2026-10-12T00:00:00Z").AddDays(-i);
 if(i==6){m.Players=[new(){Id="same-name-other-id",Name="Player"}];m.Rounds.ForEach(r=>r.Participants.RemoveAll(p=>p.PlayerId=="p"));}
 s.SaveMatchAsync(m).GetAwaiter().GetResult();
 }
 var a=new HistoryComparisonService(s).CompareAsync("p","de_test").GetAwaiter().GetResult();Eq(6,a.Matches.Count);Eq(15,a.Aggregate.Rounds);Eq(10d/15,a.Aggregate.Kpr);Eq(100d*10/15,a.Aggregate.Kast);Eq(false,a.HasComparison);Eq(14,a.Recent.Rounds);Eq(1,a.Previous.Rounds);
 var b=new HistoryComparisonService(s).CompareAsync("p",null).GetAwaiter().GetResult();Eq(7,b.Matches.Count);Eq(true,b.HasComparison);Eq(2,b.Previous.Rounds);
 var empty=new HistoryComparisonService(s).CompareAsync("missing",null).GetAwaiter().GetResult();Eq(0,empty.Matches.Count);Eq(0,empty.Aggregate.Rounds);Eq(false,empty.HasComparison);
});
void CreateLegacy(string path, MatchData match,AiReport report){
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,ForeignKeys=true}.ToString());db.Open();using var cmd=db.CreateCommand();
 cmd.CommandText="CREATE TABLE matches(id TEXT PRIMARY KEY,sha256 TEXT NOT NULL UNIQUE,map TEXT NOT NULL,source TEXT NOT NULL,imported TEXT NOT NULL,player_count INTEGER NOT NULL,round_count INTEGER NOT NULL,json TEXT NOT NULL); CREATE TABLE reports(id TEXT PRIMARY KEY,match_id TEXT NOT NULL REFERENCES matches(id),player_id TEXT NOT NULL,analysis_version TEXT NOT NULL,created TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX report_lookup ON reports(match_id,player_id,created); PRAGMA user_version=1;";cmd.ExecuteNonQuery();
 cmd.CommandText="INSERT INTO matches VALUES($id,$sha,$map,'',$date,1,1,$json)";cmd.Parameters.AddWithValue("$id",match.Id);cmd.Parameters.AddWithValue("$sha",match.SourceSha256);cmd.Parameters.AddWithValue("$map",match.MapName);cmd.Parameters.AddWithValue("$date",match.ImportedAt.ToString("O"));cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(match,DataJson.Options));cmd.ExecuteNonQuery();cmd.Parameters.Clear();
 cmd.CommandText="INSERT INTO reports VALUES($id,$match,'p',$version,$date,$json)";cmd.Parameters.AddWithValue("$id",report.Id);cmd.Parameters.AddWithValue("$match",match.Id);cmd.Parameters.AddWithValue("$version",report.AnalysisVersion);cmd.Parameters.AddWithValue("$date",report.CreatedAt.ToString("O"));cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(report,DataJson.Options));cmd.ExecuteNonQuery();
}
Test("Legacy migration keeps immutable schema parser versions and report bindings",()=>{
 var path=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"),"history.db");var oldRound=Round(1);oldRound.Events=[Kill("old-event",110,"p","e")];var old=Match(oldRound);old.ParserVersion="parser-v1";old.MapName="de_test";old.ImportedAt=DateTimeOffset.UtcNow.AddDays(-1);
 AiReport Report(string eventId)=>new(){AnalysisVersion="raw-v1",Summary="摘要",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId=eventId,Round=1,Time=110}]}]};var oldReport=Report("old-event");CreateLegacy(path,old,oldReport);
 string newId;
 using(var s=new HistoryStore(path)){
 s.InitializeAsync().GetAwaiter().GetResult();Eq(old.Id,s.LoadMatchAsync(old.Id).GetAwaiter().GetResult()!.Id);Eq(oldReport.Id,s.ListReportsAsync(old.Id,"p").GetAwaiter().GetResult().Single().Id);
 var next=Match(ContextRound());next.SchemaVersion=2;next.ParserVersion="parser-v2";next.MapName="de_test";next.Rounds.Single().Events=[Kill("new-event",110,"p","e")];newId=s.SaveMatchAsync(next).GetAwaiter().GetResult();if(newId==old.Id)throw new Exception("new version overwrote legacy ID");
 s.SaveReportAsync(newId,"p",Report("new-event")).GetAwaiter().GetResult();next.Id="other-incoming-id";next.Rounds.Single().Events=[Kill("mutated",110,"p","e")];Eq(newId,s.SaveMatchAsync(next).GetAwaiter().GetResult());
 Eq(2,s.ListMatchesAsync().GetAwaiter().GetResult().Count);Eq("old-event",s.LoadMatchAsync(old.Id).GetAwaiter().GetResult()!.Rounds.Single().Events.Single().Id);Eq("new-event",s.LoadMatchAsync(newId).GetAwaiter().GetResult()!.Rounds.Single().Events.Single().Id);
 var comparison=new HistoryComparisonService(s).CompareAsync("p","de_test").GetAwaiter().GetResult();Eq(1,comparison.Matches.Count);Eq(newId,comparison.Matches.Single().MatchId);
 }
 using var reopened=new HistoryStore(path);reopened.InitializeAsync().GetAwaiter().GetResult();Eq(2,reopened.LoadMatchAsync(newId).GetAwaiter().GetResult()!.SchemaVersion);Eq("old-event",reopened.ListReportsAsync(old.Id,"p").GetAwaiter().GetResult().Single().Strengths.Single().Evidence.Single().EventId);Eq("new-event",reopened.ListReportsAsync(newId,"p").GetAwaiter().GetResult().Single().Strengths.Single().Evidence.Single().EventId);
 var metadata=reopened.ListMatchesAsync().GetAwaiter().GetResult().Single(x=>x.Id==newId);Eq(2,metadata.SchemaVersion);Eq("parser-v2",metadata.ParserVersion);
});
Test("Cancelled migration keeps legacy table and report intact",()=>{
 var path=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"),"history.db");var match=Match(Round(1));var report=new AiReport{AnalysisVersion="old",Summary="old"};CreateLegacy(path,match,report);
 using var s=new HistoryStore(path);using var cancel=new CancellationTokenSource();cancel.Cancel();try{s.InitializeAsync(cancel.Token).GetAwaiter().GetResult();throw new Exception("cancel ignored");}catch(OperationCanceledException){}
 using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";Eq(1L,(long)cmd.ExecuteScalar()!);cmd.CommandText="SELECT COUNT(*) FROM reports";Eq(1L,(long)cmd.ExecuteScalar()!);}
 s.InitializeAsync().GetAwaiter().GetResult();Eq(report.Id,s.ListReportsAsync(match.Id,"p").GetAwaiter().GetResult().Single().Id);
});
Test("AI sends only selected event contexts inside existing budget",()=>{
 var r=ContextRound();r.Events=[Bomb("plant","bomb_planted",110),Bomb("begin","bomb_defuse_start",145,true),Kill("kill",146,"p","e")];r.Events.Last().ActorState=new(){PlayerId="p",Side="T",Time=146,Health=65,EquipmentValue=2800,DefuseKit=true};var m=Match(r);m.SchemaVersion=2;m.SourcePath="PRIVATE_PATH";m.Positions=[new(){X=12345}];var a=new AnalysisEngine().Analyze(m,"p");
 var handler=new ReportHandler(new(){Summary="摘要",Strengths=[new(){Observation="首杀",Evidence=[new(){EventId="kill",Round=1,Time=146}]}]});
 new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret",MaxInputCharacters=12000}).GetAwaiter().GetResult();
 using var envelope=JsonDocument.Parse(handler.LastBody);var input=envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;if(input.Length>11500)throw new Exception("input exceeded reserved budget");using var packet=JsonDocument.Parse(input);var selected=packet.RootElement.GetProperty("evidence").EnumerateArray().Single(x=>x.GetProperty("eventId").GetString()=="kill");
 var context=selected.GetProperty("context");Eq("context-v2",context.GetProperty("contextVersion").GetString());Eq(100,context.GetProperty("ownEquipmentValue").GetInt32());Eq(4d,context.GetProperty("timelinePoint").GetProperty("bombRemainingSeconds").GetDouble());Eq(4d,context.GetProperty("timelinePoint").GetProperty("defuseRemainingSeconds").GetDouble());Eq(65,selected.GetProperty("actorState").GetProperty("health").GetInt32());
 if(input.Contains("PRIVATE_PATH")||input.Contains("12345")||input.Contains("secret")||context.TryGetProperty("timeline",out _))throw new Exception("private or unrelated trajectory/timeline leaked");
});
Test("History selection reaches target player despite forty newer other-player demos",()=>{
 var path=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"),"history.db");using var s=new HistoryStore(path);s.InitializeAsync().GetAwaiter().GetResult();
 for(var i=0;i<42;i++){var m=Match(Round(1));m.Id="match-"+i;m.SourceSha256=(1000+i).ToString("x64");m.ImportedAt=DateTimeOffset.UtcNow.AddDays(-i);if(i<40){m.Players=[new(){Id="other",Name="Player"}];m.Rounds.Single().Participants.RemoveAll(p=>p.PlayerId=="p");}s.SaveMatchAsync(m).GetAwaiter().GetResult();}
 var a=new HistoryComparisonService(s).CompareAsync("p",null).GetAwaiter().GetResult();Eq(2,a.Matches.Count);Eq(2,a.Aggregate.Rounds);Eq(false,a.HasComparison);
});
Test("Bomb planting abort and missing observation never invent clock or alive",()=>{
 var r=ContextRound();r.Events=[Bomb("start","bomb_plant_begin",108),Bomb("abort","bomb_plant_aborted",109),Damage("pre-freeze",103,"e",10),Bomb("unknown-clock","bomb_defuse_start",112,null)];var a=new RoundContextAnalyzer().Analyze(r,"p");
 var pre=a.Timeline.Single(p=>p.EventId=="pre-freeze");Eq<int?>(null,pre.OwnAlive);Eq("unknown",pre.BombState);
 var abort=a.Timeline.Single(p=>p.EventId=="abort");Eq("unplanted",abort.BombState);Eq<double?>(null,abort.BombRemainingSeconds);
 var unknown=a.Timeline.Last();Eq<double?>(null,unknown.BombRemainingSeconds);Eq<bool?>(null,unknown.DefuseCanFinish);Eq<int?>(null,unknown.OwnAlive);
});
Test("Migration failure rolls back renamed tables and original reports",()=>{
 var path=Path.Combine("artifacts","tests",Guid.NewGuid().ToString("N"),"history.db");var m=Match(Round(1));var report=new AiReport{AnalysisVersion="old",Summary="old"};CreateLegacy(path,m,report);
 using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE matches SET json='damaged-json'";cmd.ExecuteNonQuery();}
 using var s=new HistoryStore(path);try{s.InitializeAsync().GetAwaiter().GetResult();throw new Exception("damaged migration silently accepted");}catch(SqliteException){}
 using var check=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());check.Open();using var c=check.CreateCommand();c.CommandText="PRAGMA user_version";Eq(1L,(long)c.ExecuteScalar()!);c.CommandText="SELECT COUNT(*) FROM reports";Eq(1L,(long)c.ExecuteScalar()!);c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='legacy_matches'";Eq(0L,(long)c.ExecuteScalar()!);
});
Test("Late defuse remains impossible when both displayed countdowns reach zero",()=>{
 var r=ContextRound();r.Events=[Bomb("plant","bomb_planted",110),Bomb("late-start","bomb_defuse_start",146,true),Bomb("still-pending","bomb_defuse_start",146,true),Damage("late-observation",152,"e",1)];
 var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<bool?>(false,a.Timeline.Single(p=>p.EventId=="late-start").DefuseCanFinish);Eq<double?>(0,a.Timeline.Last().BombRemainingSeconds);Eq<double?>(0,a.Timeline.Last().DefuseRemainingSeconds);Eq<bool?>(false,a.Timeline.Last().DefuseCanFinish);
});
Test("Same-clock earlier tick cannot observe future freeze health snapshot",()=>{
 var r=ContextRound();r.Events=[new(){Id="before-freeze",Kind="shot",Time=105,Tick=4,ActorId="p"},new(){Id="at-freeze",Kind="shot",Time=105,Tick=5,ActorId="p"}];var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.Timeline[0].OwnAlive);Eq<int?>(2,a.Timeline[1].OwnAlive);
});
Test("Future same-clock callback state cannot update current health or kit",()=>{
 var r=ContextRound();var hit=Damage("hit",110,"e",10);hit.Tick=10;hit.ActorState=new(){PlayerId="p",Side="T",Time=110,Tick=11,Health=0};hit.TargetState=new(){PlayerId="e",Side="CT",Time=110,Tick=10,Health=90};
 var begin=Bomb("begin","bomb_defuse_start",145,null);begin.Tick=20;begin.ActorState=new(){PlayerId="p",Side="T",Time=145,Tick=21,Health=100,DefuseKit=true};r.Events=[hit,Bomb("plant","bomb_planted",110),begin];var a=new RoundContextAnalyzer().Analyze(r,"p");Eq<int?>(null,a.Timeline.Single(p=>p.EventId=="hit").OwnAlive);Eq<bool?>(null,a.Timeline.Last().DefuseCanFinish);
});
Test("Fatal observed damage stops defuse before later kill callback",()=>{
 var r=ContextRound();r.EconomySnapshots.Single(s=>s.PlayerId=="mate").Health=0;
 var fatal=new GameEvent{Id="fatal",Kind="damage",Time=146,Tick=20,ActorId="e",TargetId="p",TargetState=new(){PlayerId="p",Side="T",Time=146,Tick=20,Health=0}};
 r.Events=[Bomb("plant","bomb_planted",110),Bomb("begin","bomb_defuse_start",145,true),fatal,new(){Id="between",Kind="shot",Time=146.1,Tick=21},Kill("death",147,"e","p")];
 var timeline=new RoundContextAnalyzer().Analyze(r,"p").Timeline;
 Eq<int?>(0,timeline.Single(p=>p.EventId=="fatal").OwnAlive);
 foreach(var id in new[]{"fatal","between","death"}){var p=timeline.Single(p=>p.EventId==id);Eq("planted",p.BombState);Eq<string?>(null,p.ActiveDefuserId);Eq<double?>(null,p.DefuseRemainingSeconds);Eq<bool?>(null,p.DefuseCanFinish);}
});
Test("Known dead actor cannot start or retain a defuse",()=>{
 foreach(var priorStart in new[]{false,true}){
 var r=ContextRound();var deadStart=Bomb("dead-start","bomb_defuse_start",146,true);deadStart.ActorState=new(){PlayerId="p",Side="T",Time=146,Health=0};
 r.Events=[Bomb("plant","bomb_planted",110)];if(priorStart)r.Events.Add(Bomb("begin","bomb_defuse_start",145,true));r.Events.Add(deadStart);
 var p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Last();Eq("planted",p.BombState);Eq<string?>(null,p.ActiveDefuserId);Eq<double?>(null,p.DefuseRemainingSeconds);Eq<bool?>(null,p.DefuseCanFinish);
 }
});
Test("Kit fallback requires matching participant identity side clock and tick",()=>{
 foreach(var fixture in new[]{(Id:"p",Side:"CT",Time:145d,Tick:20),(Id:"p",Side:"",Time:145d,Tick:20),(Id:"outsider",Side:"T",Time:145d,Tick:20),(Id:"p",Side:"T",Time:145.001,Tick:20),(Id:"p",Side:"T",Time:145d,Tick:21)}){
 var r=ContextRound();var start=Bomb("start","bomb_defuse_start",145,null,fixture.Id);start.Tick=20;start.ActorState=new(){PlayerId=fixture.Id,Side=fixture.Side,Time=fixture.Time,Tick=fixture.Tick,Health=100,DefuseKit=true};r.Events=[Bomb("plant","bomb_planted",110),start];
 var p=new RoundContextAnalyzer().Analyze(r,"p").Timeline.Last();Eq<double?>(null,p.DefuseRemainingSeconds);Eq<bool?>(null,p.DefuseCanFinish);
 }
 foreach(var kit in new[]{true,false}){var valid=ContextRound();var begin=Bomb("begin","bomb_defuse_start",145,null);begin.Tick=20;begin.ActorState=new(){PlayerId="p",Side="T",Time=145,Tick=20,Health=100,DefuseKit=kit};valid.Events=[Bomb("plant","bomb_planted",110),begin];var point=new RoundContextAnalyzer().Analyze(valid,"p").Timeline.Last();Eq<double?>(kit?5:10,point.DefuseRemainingSeconds);Eq<bool?>(kit,point.DefuseCanFinish);}
});
Test("Large round report keeps selected evidence inside budget",()=>{
 const int count=80000;var r=ContextRound();r.EndedAt=110+count/64d+10;
 r.Events=Enumerable.Range(0,count).Select(i=>new GameEvent{Id="shot-"+i,Kind="shot",ActorId="p",ActorSide="T",Weapon="ak47",Time=110+i/64d,Tick=7040+i}).ToList();
 var m=Match(r);m.SchemaVersion=2;var a=new AnalysisEngine().Analyze(m,"p");
 var handler=new ReportHandler(new(){Summary="摘要",Strengths=[new(){Observation="射击记录可供复盘",Evidence=[new(){EventId="shot-0",Round=1,Time=110}]}]});
 var watch=System.Diagnostics.Stopwatch.StartNew();new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,a,new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret",MaxInputCharacters=12000}).GetAwaiter().GetResult();watch.Stop();
 using var envelope=JsonDocument.Parse(handler.LastBody);var input=envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;using var packet=JsonDocument.Parse(input);var evidence=packet.RootElement.GetProperty("evidence").EnumerateArray().ToList();
 if(input.Length>11500||evidence.Count==0||evidence.Count>=count)throw new Exception("invalid large-round evidence budget");
 foreach(var selected in evidence){Eq(selected.GetProperty("eventId").GetString(),selected.GetProperty("context").GetProperty("timelinePoint").GetProperty("eventId").GetString());Eq(selected.GetProperty("time").GetDouble(),selected.GetProperty("context").GetProperty("timelinePoint").GetProperty("time").GetDouble());}
 Console.WriteLine($"BENCH events={count} elapsedMs={watch.ElapsedMilliseconds} selected={evidence.Count} inputCharacters={input.Length}");
});
Test("Round context cancellation remains cancellation during large event ordering",()=>{
 var r=ContextRound();r.Events=Enumerable.Range(0,80000).Select(i=>new GameEvent{Id="cancel-"+i,Kind="shot",Time=110+(80000-i)/2000d,Tick=i}).ToList();
 using var cancel=new CancellationTokenSource();cancel.CancelAfter(TimeSpan.FromMilliseconds(1));
 try{new RoundContextAnalyzer().Analyze(r,"p",cancel.Token);throw new Exception("large context ignored cancellation");}catch(OperationCanceledException) when(cancel.IsCancellationRequested){}
});
Test("AI skips oversized context while keeping a later smaller event",()=>{
 var r=ContextRound();r.Events=[new(){Id="oversized",Kind="shot",Time=110,ActorId="p",Weapon=new string('x',14000)},new(){Id="small",Kind="shot",Time=111,ActorId="p",Weapon="ak47"}];
 var m=Match(r);var handler=new ReportHandler(new(){Summary="摘要",Strengths=[new(){Observation="射击记录可供复盘",Evidence=[new(){EventId="small",Round=1,Time=111}]}]});
 new ReportService(new HttpClient(handler)).GenerateFixtureAsync(m,new AnalysisEngine().Analyze(m,"p"),new(){Endpoint="https://unit.test/v1/chat/completions",Model="test",ApiKey="secret",MaxInputCharacters=4000}).GetAwaiter().GetResult();
 using var envelope=JsonDocument.Parse(handler.LastBody);using var input=JsonDocument.Parse(envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);Eq("small",input.RootElement.GetProperty("evidence").EnumerateArray().Single().GetProperty("eventId").GetString());
});
Test("Confirmed death stays zero through posthumous positive or missing health callbacks",()=>{
 foreach(var hp in new int?[]{100,null}){
 var r=ContextRound();r.EconomySnapshots.Single(s=>s.PlayerId=="mate").Health=0;
 var posthumous=Damage("posthumous",148,"e",20);posthumous.Tick=30;posthumous.ActorState=new(){PlayerId="p",Side="T",Time=148,Tick=30,Health=hp};posthumous.TargetState=new(){PlayerId="e",Side="CT",Time=148,Tick=30,Health=80};
 r.Events=[Bomb("plant","bomb_planted",110),Bomb("begin","bomb_defuse_start",145,true),Kill("death",146,"e","p"),posthumous,Bomb("dead-restart","bomb_defuse_start",149,true)];
 var timeline=new RoundContextAnalyzer().Analyze(r,"p").Timeline;foreach(var id in new[]{"death","posthumous","dead-restart"}){var point=timeline.Single(p=>p.EventId==id);Eq<int?>(0,point.OwnAlive);Eq("planted",point.BombState);Eq<string?>(null,point.ActiveDefuserId);Eq<bool?>(null,point.DefuseCanFinish);}
 }
});
Test("Unknown actor defuse abort stops while mismatched known actor does not",()=>{
 foreach(var actor in new string?[]{null,"p"}){
 var r=ContextRound();var start=Bomb("start","bomb_defuse_start",145,true);start.ActorId=null;var abort=Bomb("abort","bomb_defuse_aborted",146);abort.ActorId=actor;r.Events=[Bomb("plant","bomb_planted",110),start,abort];
 var timeline=new RoundContextAnalyzer().Analyze(r,"p").Timeline;Eq("defusing",timeline[1].BombState);Eq<string?>(null,timeline[1].ActiveDefuserId);var end=timeline.Last();Eq("planted",end.BombState);Eq<string?>(null,end.ActiveDefuserId);Eq<double?>(null,end.DefuseRemainingSeconds);Eq<bool?>(null,end.DefuseCanFinish);
 }
 var known=ContextRound();known.Events=[Bomb("plant","bomb_planted",110),Bomb("start","bomb_defuse_start",145,true),Bomb("other-abort","bomb_defuse_aborted",146,null,"mate")];var point=new RoundContextAnalyzer().Analyze(known,"p").Timeline.Last();Eq("defusing",point.BombState);Eq<string?>("p",point.ActiveDefuserId);Eq<double?>(4,point.DefuseRemainingSeconds);Eq<bool?>(true,point.DefuseCanFinish);
});
if(failures.Count>0) Environment.ExitCode=1;

sealed class ReportHandler(AiReport report) : HttpMessageHandler
{
 public int Calls;public string LastBody="";
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
 Calls++;LastBody=await request.Content!.ReadAsStringAsync(ct);
 var envelope=new {choices=new[]{new {message=new{content=JsonSerializer.Serialize(report,DataJson.Options)}}}};
 return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(envelope),Encoding.UTF8,"application/json")};
 }
}

sealed class WaitingHandler : HttpMessageHandler
{
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);}
}

sealed class RepairHandler(AiReport malformed,AiReport repaired) : HttpMessageHandler
{
 public int Calls;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
 var response=Calls++==0?malformed:repaired;
 var envelope=new{choices=new[]{new{message=new{content=JsonSerializer.Serialize(response,DataJson.Options)}}}};
 return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(envelope),Encoding.UTF8,"application/json")});
 }
}

// Legacy fixed-ID fixtures deliberately opt out. Default pseudonymization is covered by Report.Tests.
static class ReportFixtureExtensions
{
 public static Task<AiReport> GenerateFixtureAsync(this ReportService service,MatchData match,PlayerAnalysis analysis,ReportOptions options,CancellationToken ct=default)
  => service.GenerateAsync(match,analysis,options,ct,anonymizeIdentities:false);
}
