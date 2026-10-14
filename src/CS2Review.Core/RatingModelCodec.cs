using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2Review.Core;

public static class RatingModelCodec
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented=true, PropertyNameCaseInsensitive=false, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, MaxDepth=64 };
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static RatingModel Seal(RatingModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateParameters(model);
        var clone=JsonSerializer.Deserialize<RatingModel>(JsonSerializer.Serialize(model,Json),Json)!;
        clone.ModelId="";clone.ProbabilityCells=clone.ProbabilityCells.OrderBy(RatingRoundEvaluator.Key,StringComparer.Ordinal).ToList();clone.TrainingGroups=clone.TrainingGroups.Distinct().Order(StringComparer.Ordinal).ToList();
        ValidateParameters(clone);clone.ModelId=Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(clone,Json)));return clone;
    }
    public static void Validate(RatingModel model)
    {
        ValidateParameters(model);
        if(!IsSha(model.ModelId)||Seal(model).ModelId!=model.ModelId)throw new ArgumentException("模型 SHA-256 与冻结参数不一致。");
        if(DefaultRatingModel.IsBaseline(model)&&model.ModelId!=DefaultRatingModel.Create().ModelId)
            throw new ArgumentException("内置基础模型参数与发布版本不一致；自训模型应使用参考模型格式。");
        if(BuiltInRatingModel.IsRoundThree(model)&&model.ModelId!=BuiltInRatingModel.Create().ModelId)
            throw new ArgumentException("第三轮内置 RT 模型参数与发布版本不一致；自训模型应使用参考模型格式。");
    }
    public static string Serialize(RatingModel model) { Validate(model); return JsonSerializer.Serialize(model,Json); }
    public static RatingModel Load(string json)
    {
        try
        {
            if(Encoding.UTF8.GetByteCount(json)>16*1024*1024)throw new ArgumentException("模型文件过大。");
            RejectDuplicateProperties(json);
            var model=JsonSerializer.Deserialize<RatingModel>(json,Json)??throw new ArgumentException("模型为空。");
            Validate(model);return model;
        }
        catch(JsonException ex){throw new ArgumentException("模型 JSON 无效或含未知字段。",ex);}
    }
    public static async Task<RatingModel> LoadFileAsync(string path,CancellationToken ct=default)
    { if(new FileInfo(path).Length>16*1024*1024)throw new ArgumentException("模型文件过大。");return Load(await File.ReadAllTextAsync(path,ct)); }
    public static Task SaveFileAsync(string path,RatingModel model,CancellationToken ct=default) => File.WriteAllTextAsync(path,Serialize(model),new UTF8Encoding(false),ct);
    internal static bool IsSha(string? value) => value is not null&&value.Length==64&&value.All(Uri.IsHexDigit);
    internal static void RejectDuplicateProperties(string json)
    {
        using var doc=JsonDocument.Parse(json,new(){MaxDepth=64});
        void Walk(JsonElement e)
        {
            if(e.ValueKind==JsonValueKind.Object){var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject()){if(!seen.Add(p.Name))throw new ArgumentException("JSON 含重复字段。");Walk(p.Value);}}
            else if(e.ValueKind==JsonValueKind.Array)foreach(var x in e.EnumerateArray())Walk(x);
        }
        Walk(doc.RootElement);
    }
    internal static void ValidateParameters(RatingModel m)
    {
        void Require([DoesNotReturnIf(false)] bool condition,string reason){if(!condition)throw new ArgumentException(reason);}
        Require(m is not null,"模型为空。");
        var baseline=DefaultRatingModel.IsBaseline(m);
        var roundThree=BuiltInRatingModel.IsRoundThree(m);
        Require(m.SchemaVersion==1&&m.ModelVersion=="rating-approx-v1"||baseline||roundThree,"模型版本不支持。");
        Require(DateOnly.TryParseExact(m.TargetDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)&&m.TargetDate=="2026-10-01","评分目标日期不匹配。");
        Require(m.CalibrationStatus is "experimental" or "exploratory","当前模块只接受实验/探索模型，不能据标记宣称真实校准。");
        if(baseline)
            Require(m.CalibrationStatus=="experimental"&&m.TrainingManifestSha256==""&&m.ProbabilityCells is {Count:0}&&m.TrainingGroups is {Count:0}&&m.Validation is null&&m.FeatureVersion=="freeze-equipment-value-proxy-v1","内置基础模型不能声明训练、观测、验证或精确装备事实。");
        else if(roundThree)
            Require(m.CalibrationStatus=="experimental"&&IsSha(m.TrainingManifestSha256)&&m.ProbabilityCells is {Count:0}&&m.TrainingGroups is {Count:12}&&m.Validation is null&&m.FeatureVersion=="freeze-equipment-value-proxy-v1","第三轮内置模型仅训练 RT 权重，不能声明训练胜率格、交叉验证或精确装备事实。");
        else Require(IsSha(m.TrainingManifestSha256),"训练材料清单 SHA 缺失。");
        Require(m.FeatureVersion is "freeze-equipment-value-proxy-v1" or "strongest-weapon-armor-v1","特征版本不支持。");
        Require(!string.IsNullOrWhiteSpace(m.ParameterDisclosure),"模型未公开自身参数选择。");
        Require(m.EquipmentBandUpperBounds is {Length:5}&&m.EquipmentBandUpperBounds.All(v=>v>0&&v<=100000)&&m.EquipmentBandUpperBounds.Zip(m.EquipmentBandUpperBounds.Skip(1),(a,b)=>a<b).All(v=>v),"装备分层无效。");
        Require(m.EquipmentStrengths is {Length:6}&&m.EquipmentStrengths.All(v=>double.IsFinite(v)&&v>0&&v<=100),"装备强度无效。");
        Require(double.IsFinite(m.ProbabilityPriorWeight)&&m.ProbabilityPriorWeight>=0&&m.ProbabilityPriorWeight<=100000&&double.IsFinite(m.ProbabilityPriorCtWin)&&m.ProbabilityPriorCtWin>=0&&m.ProbabilityPriorCtWin<=1,"概率先验无效。");
        Require(m.MinimumMapObservations>0&&m.MinimumMapObservations<=10000000,"地图最低观测数无效。");
        Require(m.ProbabilityCells is not null&&m.ProbabilityCells.Count<=100000,"概率表缺失或过大。");
        foreach(var c in m.ProbabilityCells)
        {
            Require(c is not null&&!string.IsNullOrWhiteSpace(c.Map)&&c.Map.Length<100&&c.CtAlive is >=0 and <=5&&c.TAlive is >=0 and <=5&&c.CtEquipmentBand is >=0 and <=5&&c.TEquipmentBand is >=0 and <=5&&c.ClockBand is >=0 and <=3&&(!c.Planted?c.ClockBand==0:c.ClockBand>0),"概率格特征无效。");
            Require(c.Observations>0&&c.Observations<=10000000&&c.CtWins>=0&&c.CtWins<=c.Observations,"概率格计数无效。");
        }
        Require(m.ProbabilityCells.Select(RatingRoundEvaluator.Key).Distinct(StringComparer.Ordinal).Count()==m.ProbabilityCells.Count,"概率格重复。");
        var shares=new[]{m.KillerShare,m.DamageShare,m.FlashShare,m.TradeShare};
        Require(shares.All(v=>double.IsFinite(v)&&v>=0&&v<=1)&&Math.Abs(shares.Sum()-1)<1e-9,"贡献份额必须非负且和为一。");
        Require(double.IsFinite(m.TradeWindowSeconds)&&m.TradeWindowSeconds==5,"当前补枪口径固定为五秒。");
        Require(m.MultiKillPoints is {Length:6}&&m.MultiKillPoints.All(v=>double.IsFinite(v)&&v>=0&&v<=100),"多杀点数无效。");
        Require(m.AxisCenters is {Length:6}&&m.AxisCenters.All(double.IsFinite)&&m.AxisScales is {Length:6}&&m.AxisScales.All(v=>double.IsFinite(v)&&v>0),"六轴标准化无效。");
        Require(m.Weights is {Length:6}&&m.Weights.All(v=>double.IsFinite(v)&&v>=0)&&m.Weights.Sum()>0&&m.Weights.Sum()<=100&&double.IsFinite(m.Intercept),"综合权重/偏置无效。");
        Require(m.TrainingGroups is not null&&m.TrainingGroups.All(g=>!string.IsNullOrWhiteSpace(g)),"训练组无效。");
    }
}
