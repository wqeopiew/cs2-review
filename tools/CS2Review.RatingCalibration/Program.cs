using CS2Review.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
var jsonOptions=new JsonSerializerOptions(DataJson.Options){WriteIndented=true,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
void Help()=>Console.WriteLine("评分离线工具（实验模型）\n  validate manifest.json\n  train manifest.json model.json [options.json]\n  cross-validate manifest.json report.json [options.json]\n  inspect model.json\n  score model.json canonical-match.json playerId score.json [all|T|CT] [facts.json]\n必须提供实际MatchData/文件哈希/同地图官方快照；synthetic材料只能验证流程，不能证明评分精度。");
async Task<T> Read<T>(string path)
{
    stop.Token.ThrowIfCancellationRequested();if(new FileInfo(path).Length>256L*1024*1024)throw new ArgumentException("输入文件过大。");
    var json=await File.ReadAllTextAsync(path,stop.Token);
    using var document=JsonDocument.Parse(json);
    void Check(JsonElement node){if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in node.EnumerateObject()){if(!names.Add(property.Name))throw new ArgumentException("输入含重复JSON字段。");Check(property.Value);}}else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())Check(child);}
    Check(document.RootElement);return JsonSerializer.Deserialize<T>(json,jsonOptions)??throw new ArgumentException("输入为空。");
}
async Task Write<T>(string path,T value)
{
    var full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full,JsonSerializer.Serialize(value,jsonOptions),new UTF8Encoding(false),stop.Token);
}
async Task<RatingTrainingOptions?> Options(int index)=>args.Length>index?await Read<RatingTrainingOptions>(args[index]):null;
try
{
    if(args.Length==0||args[0] is "--help" or "-h"){Help();return;}
    var trainer=new RatingTrainer();
    switch(args[0])
    {
        case "validate" when args.Length==2:
        {
            var input=await RatingReferenceLoader.LoadAsync(args[1],stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(new{input.Manifest.ReferenceId,Matches=input.Samples.Count,WholeMatchGroups=input.Samples.Select(s=>s.Reference.GroupId).Distinct().Count(),Status="validated-input-only"},jsonOptions));break;
        }
        case "train" when args.Length is 3 or 4:
        {
            var input=await RatingReferenceLoader.LoadAsync(args[1],stop.Token);
            var model=trainer.Train(input.Manifest,input.Samples,await Options(3),stop.Token);
            var full=Path.GetFullPath(args[2]);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await RatingModelCodec.SaveFileAsync(full,model,stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(new{model.ModelId,model.TargetDate,model.CalibrationStatus,ProbabilityCells=model.ProbabilityCells.Count,model.TrainingGroups},jsonOptions));break;
        }
        case "cross-validate" when args.Length is 3 or 4:
        {
            var input=await RatingReferenceLoader.LoadAsync(args[1],stop.Token);
            var report=trainer.ValidateLeaveOneMatchOut(input.Manifest,input.Samples,await Options(3),stop.Token);
            await Write(args[2],report);Console.WriteLine(JsonSerializer.Serialize(new{report.Method,report.Status,Folds=report.Folds.Count,report.Rating,report.Swing,report.ProbabilityCount,report.ProbabilityBrier,report.ProbabilityLogLoss,report.Warnings},jsonOptions));break;
        }
        case "inspect" when args.Length==2:
        {
            var model=await RatingModelCodec.LoadFileAsync(args[1],stop.Token);Console.WriteLine(RatingModelCodec.Serialize(model));break;
        }
        case "score" when args.Length is >=5 and <=7:
        {
            var model=await RatingModelCodec.LoadFileAsync(args[1],stop.Token);var match=await Read<MatchData>(args[2]);
            var facts=args.Length==7?await Read<RatingMatchFacts>(args[6]):null;
            var result=new RatingEngine().Score(match,args[3],model,facts,args.Length>=6?args[5]:"all",stop.Token);
            await Write(args[4],result);Console.WriteLine(JsonSerializer.Serialize(new{result.Overall,result.RoundSwing,result.CalibrationStatus,result.UnavailableReasons},jsonOptions));if(result.Overall is null)Environment.ExitCode=3;break;
        }
        default:throw new ArgumentException("命令或参数数量无效；运行 --help 查看格式。");
    }
}
catch(OperationCanceledException){Console.Error.WriteLine("已取消；没有宣称模型已校准。");Environment.ExitCode=130;}
catch(Exception ex)when(ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
{Console.Error.WriteLine(ex.Message);Environment.ExitCode=2;}
