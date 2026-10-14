using CS2Review.Core;

static class RatingBaselineTests
{
    public static RatingModel Model()
    {
        var type=typeof(RatingEngine).Assembly.GetType("CS2Review.Core.DefaultRatingModel");
        if(type is null)throw new Exception("Missing built-in rating model: a new installation cannot calculate Rating/RS.");
        return (RatingModel)type.GetMethod("Create")!.Invoke(null,null)!;
    }
    static MatchData FullMatch()
    {
        var r=RatingRuleTests.Round();
        var ids=new[]{"p","t2","t3","t4","t5","e","ct2","ct3","ct4","ct5"};
        r.Participants=ids.Select(id=>new Participation{PlayerId=id,Side=id is "p"||id.StartsWith('t')?"T":"CT"}).ToList();
        r.EconomySnapshots=r.Participants.Select(p=>RatingRuleTests.State(p.PlayerId,p.Side,0,0)).ToList();
        r.Survivors=ids.Where(id=>id!="e").ToList();r.Events=[RatingRuleTests.Kill("opening",20)];
        var m=RatingRuleTests.Match(r);m.MapName="unknown-map";m.Players=ids.Select(id=>new PlayerData{Id=id,Name=id}).ToList();return m;
    }
    public static void Run()
    {
        var failures=new List<string>();var count=0;
        void Test(string name,Action body){count++;try{body();Console.WriteLine("PASS "+name);}catch(Exception ex){failures.Add(name);Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Test("built-in model is immutable by hash and has no invented training",()=>{var a=Model();var b=Model();RatingModelCodec.Validate(a);RatingRuleTests.Yes(a.ModelId==b.ModelId&&a.SchemaVersion==2&&a.ModelVersion=="rating-baseline-v1","unstable baseline identity");RatingRuleTests.Yes(a.TrainingManifestSha256.Length==0&&a.TrainingGroups.Count==0&&a.ProbabilityCells.Count==0&&a.Validation is null&&a.CalibrationStatus=="experimental","invented training/validation");a.Weights[0]=.99;RatingRuleTests.Yes(b.Weights[0]==.25,"shared mutable default");});
        Test("published six weights use the correct feature order",()=>{var m=Model();foreach(var pair in m.Weights.Zip(new[]{.25,.15,.15,.08,.04,.33}))RatingRuleTests.Near(pair.Second,pair.First);});
        Test("opening kill earns nonzero positive and victim negative swing",()=>{var m=FullMatch();var a=new RatingEngine().Score(m,"p",Model());var v=new RatingEngine().Score(m,"e",Model());RatingRuleTests.Yes(a.Overall is double x&&double.IsFinite(x)&&a.RoundSwing>0,"baseline remained unavailable");var c=RatingRuleTests.Credit(a,"opening");RatingRuleTests.Yes(c.PlayerCredits["p"] is >.20 and <.23&&c.PlayerCredits["e"]<-.20,"public manpower shape was not used");RatingRuleTests.Yes(v.RoundSwing<0,"victim loss hidden");});
        Test("map anchor affects the same recorded opening",()=>{var m=FullMatch();m.MapName="de_mirage";var a=new RatingEngine().Score(m,"p",Model());m.MapName="de_dust2";var b=new RatingEngine().Score(m,"p",Model());RatingRuleTests.Yes(Math.Abs(RatingRuleTests.Credit(a,"opening").PlayerCredits["p"]-RatingRuleTests.Credit(b,"opening").PlayerCredits["p"])>.0001,"map anchor ignored");});
        Test("finishing a five versus one is worth less than an opening",()=>{var m=FullMatch();var opening=RatingRuleTests.Credit(new RatingEngine().Score(m,"p",Model()),"opening").PlayerCredits["p"];foreach(var p in m.Rounds[0].EconomySnapshots.Where(p=>p.Side=="CT"&&p.PlayerId!="e"))p.Health=0;var finish=RatingRuleTests.Credit(new RatingEngine().Score(m,"p",Model()),"opening").PlayerCredits["p"];RatingRuleTests.Yes(finish>=0&&finish<opening/5,"low-impact finish overvalued");});
        Test("missing freeze equipment is not filled to produce a number",()=>{var m=FullMatch();m.Rounds[0].EconomySnapshots[0].EquipmentValue=null;var a=new RatingEngine().Score(m,"p",Model());RatingRuleTests.Yes(a.Overall is null&&a.RoundSwing is null&&a.UnavailableReasons.Count>0,"unknown facts invented");});
        Test("baseline serialization and legacy trained model roundtrip coexist",()=>{var m=Model();RatingRuleTests.Yes(RatingModelCodec.Load(RatingModelCodec.Serialize(m)).ModelId==m.ModelId,"baseline roundtrip");var old=RatingRuleTests.Model();RatingRuleTests.Yes(RatingModelCodec.Load(RatingModelCodec.Serialize(old)).ModelId==old.ModelId&&old.SchemaVersion==1,"legacy model changed");});
        Test("baseline cannot claim a training table or validation",()=>{var a=Model();a.ProbabilityCells.Add(new(){Map="global",CtAlive=5,TAlive=5,CtEquipmentBand=3,TEquipmentBand=3,Observations=1,CtWins=1});try{RatingModelCodec.Seal(a);throw new Exception("invented baseline observations accepted");}catch(ArgumentException){}a=Model();a.Validation=new();try{RatingModelCodec.Seal(a);throw new Exception("baseline validation claim accepted");}catch(ArgumentException){} });
        Test("changed baseline cannot retain a published-parameter label",()=>{foreach(var mutate in new Action<RatingModel>[] {m=>m.Weights[0]=.5,m=>m.AxisScales[5]=.01,m=>m.ProbabilityPriorCtWin=.9,m=>m.ParameterDisclosure="official and calibrated"}){var m=Model();mutate(m);m=RatingModelCodec.Seal(m);try{RatingModelCodec.Validate(m);throw new Exception("modified baseline accepted");}catch(ArgumentException){}}});
        Console.WriteLine($"Rating baseline: {count-failures.Count}/{count} passed");if(failures.Count>0)Environment.ExitCode=1;
    }
}
