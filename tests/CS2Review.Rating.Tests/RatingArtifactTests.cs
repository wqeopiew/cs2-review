using CS2Review.Core;
using System.Text.Json.Nodes;

static class RatingArtifactTests
{
    public static void Run()
    {
        var failures=0;
        void Test(string name,Action body){try{body();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        void Reject(Action body){try{body();}catch(ArgumentException){return;}throw new Exception("invalid model accepted");}
        Test("frozen model survives serialization deterministically",()=>{var m=RatingModelCodec.Seal(RatingRuleTests.Model());var json=RatingModelCodec.Serialize(m);var copy=RatingModelCodec.Load(json);RatingRuleTests.Yes(copy.ModelId==m.ModelId,"hash drift");RatingRuleTests.Yes(RatingModelCodec.Serialize(copy)==json,"serialization drift");});
        Test("changed model parameters invalidate hash",()=>{var m=RatingModelCodec.Seal(RatingRuleTests.Model());var node=JsonNode.Parse(RatingModelCodec.Serialize(m))!;node["intercept"]=99;Reject(()=>RatingModelCodec.Load(node.ToJsonString()));});
        Test("unknown version or fields rejected",()=>{var m=RatingModelCodec.Seal(RatingRuleTests.Model());foreach(var field in new[]{"schemaVersion","unknown"}){var node=JsonNode.Parse(RatingModelCodec.Serialize(m))!;node[field]=9;Reject(()=>RatingModelCodec.Load(node.ToJsonString()));}});
        Test("NaN negative weights invalid shares and cells rejected",()=>{foreach(var mutate in new Action<RatingModel>[] {m=>m.Weights[0]=-1,m=>m.AxisScales[0]=0,m=>m.KillerShare=2,m=>m.ProbabilityCells[0].CtWins=101,m=>m.ProbabilityCells.Add(m.ProbabilityCells[0]),m=>m.Intercept=double.NaN}){var m=RatingRuleTests.Model();mutate(m);Reject(()=>RatingModelCodec.Seal(m));}});
        if(failures>0)Environment.ExitCode=1;
    }
}
