namespace CS2Review.Core;

// The baseline is an untrained reconstruction, not a set of invented observations.
// Changes to its probability/normalization algorithm require a new ModelVersion.
public static class DefaultRatingModel
{
    public const string Version="rating-baseline-v1";
    // HLTV's August 2025 global full-buy table. Rows: T5..1; columns: CT5..1.
    // https://www.hltv.org/news/42485/introducing-rating-30
    private static readonly double[,] TWin={
        {.488,.702,.890,.981,.999},
        {.315,.525,.769,.942,.996},
        {.143,.300,.546,.819,.974},
        {.032,.099,.258,.547,.870},
        {.002,.009,.045,.189,.551}
    };
    // October 2026's published 5v5 buy-round anchors. Cache is omitted because
    // the article and its graphic disagree. Unknown maps use the global anchor.
    // https://www.hltv.org/news/45596/rating-30-receives-small-accuracy-improvements
    private static readonly IReadOnlyDictionary<string,double> MapTWin=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase){
        ["de_anubis"]=.535,["de_dust2"]=.505,["de_inferno"]=.496,["de_vertigo"]=.496,
        ["de_ancient"]=.479,["de_mirage"]=.475,["de_nuke"]=.467,["de_train"]=.455,["de_overpass"]=.442
    };
    public static bool IsBaseline(RatingModel model)=>model.SchemaVersion==2&&model.ModelVersion==Version;
    public static RatingModel Create()=>RatingModelCodec.Seal(new(){
        SchemaVersion=2,ModelVersion=Version,CalibrationStatus="experimental",TrainingManifestSha256="",
        ProbabilityCells=[],TrainingGroups=[],Validation=null,ProbabilityPriorWeight=0,ProbabilityPriorCtWin=.514,
        // These are declared fixed anchors/scales, not measured professional averages.
        AxisCenters=[.65,70,.35,.70,.25,0],AxisScales=[.65,70,.35,.70,.25,.08],
        // Published October 2025 weights, in the engine's six-feature order.
        // https://www.hltv.org/news/43047/rating-30-adjustments-go-live
        Weights=[.25,.15,.15,.08,.04,.33],Intercept=0,
        ParameterDisclosure="内置基础估算，未训练、未校准。组合权重来自 HLTV 2025-10-29：击杀25%、伤害15%、存活15%、KAST8%、多杀4%、Swing33%。胜率使用公开2025全图全枪人数表的形状，并按2026-10-01图的5v5地图胜率调整log-odds；这不是完整2026官方胜率表。冻结装备总价代理与强度[1,1.2,1.6,2,2.6,3]、装备比修正、下包赔率倍数[0.03,0.15,0.5]、击杀/伤害/闪光/补枪份额[0.5,0.3,0.1,0.1]均是本软件假设。六轴固定锚点[0.65,70,0.35,0.70,0.25,0]与尺度[0.65,70,0.35,0.70,0.25,0.08]为自身选择，不是官方均值或标准差，不逐场重新中心化。回合末仍采用本软件的可追溯正向击杀分配策略，尚未复刻全部官方残局/拆包/存活分配。"
    });
    internal static double CtWinProbability(RatingProbabilityCell state,RatingModel model)
    {
        if(state.CtAlive==0)return 0;
        if(state.TAlive==0)return 1;
        var ctBase=1-TWin[5-state.TAlive,5-state.CtAlive];
        var ctAnchor=MapTWin.TryGetValue(state.Map,out var tAnchor)?1-tAnchor:model.ProbabilityPriorCtWin;
        static double LogOdds(double p)=>Math.Log(p)-Math.Log(1-p);
        var logOdds=LogOdds(ctBase)+LogOdds(ctAnchor)-LogOdds(1-.488)
            +Math.Log(model.EquipmentStrengths[state.CtEquipmentBand])-Math.Log(model.EquipmentStrengths[state.TEquipmentBand]);
        if(state.Planted)logOdds+=Math.Log(state.ClockBand switch{1=>.03,2=>.15,_=>.5});
        // Stable for all finite positive equipment strengths accepted by the codec.
        return logOdds>=0?1/(1+Math.Exp(-logOdds)):Math.Exp(logOdds)/(1+Math.Exp(logOdds));
    }
}
