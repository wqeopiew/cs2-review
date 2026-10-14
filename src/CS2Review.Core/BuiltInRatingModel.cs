namespace CS2Review.Core;

// Only the RT linear combination was fitted. Features, axes and RS use the
// published baseline algorithm; no research probability observations are shipped.
public static class BuiltInRatingModel
{
    public const string Version="rating-rt-calibrated-v1";
    public const string ResearchModelId="301bcf632d091517354617f15449a664c12e02bb77231cc04bb020164cfc0356";
    // SHA-256 of the frozen research candidate JSON, separate from the product ID.
    public const string ResearchParameterSha="09ebced004c66e8d5c38b69d3cab471ce263a2ab278b1ce27d03f2ccc1dabc51";
    public const string ResearchParameterSha256=ResearchParameterSha;
    public const string FrozenCandidatePath="artifacts/calibration-real-3/fitting/fitted/candidate-frozen.json";

    public static bool IsRoundThree(RatingModel? model)=>model is {SchemaVersion:3,ModelVersion:Version};

    // Runtime creation is self-contained and returns independently mutable arrays
    // and lists. The original schema-2 baseline remains available for rollback.
    public static RatingModel Create()=>RatingModelCodec.Seal(new(){
        SchemaVersion=3,ModelVersion=Version,TargetDate="2026-10-01",CalibrationStatus="experimental",
        TrainingManifestSha256="09184017a0b62136c38927c6e2250a45957c75dd89a063e3a55208e99b631ad4",
        TrainingGroups=[
            "hltv-series-2393003","hltv-series-2393006","hltv-series-2393011","hltv-series-2393017",
            "hltv-series-2393081","hltv-series-2393082","hltv-series-2393083","hltv-series-2393234",
            "hltv-series-2393236","hltv-series-2393237","hltv-series-2393239","hltv-series-2393240"
        ],
        FeatureVersion="freeze-equipment-value-proxy-v1",ProbabilityCells=[],Validation=null,
        EquipmentBandUpperBounds=[1000,2000,3500,5000,6500],EquipmentStrengths=[1,1.2,1.6,2,2.6,3],
        ProbabilityPriorWeight=0,ProbabilityPriorCtWin=.514,MinimumMapObservations=20,
        KillerShare=.5,DamageShare=.3,FlashShare=.1,TradeShare=.1,TradeWindowSeconds=5,
        MultiKillPoints=[0,0,1,2,4,7],AxisCenters=[.65,70,.35,.70,.25,0],AxisScales=[.65,70,.35,.70,.25,.08],
        Weights=[.2995397042424282,.22903952593221624,.11225310197505149,.11212713967535527,.03953941799683493,.31328491096175687],
        Intercept=.003561701208648185,
        ParameterDisclosure="第三轮 RT 实验模型：仅六项组合权重与截距以12个系列、120条真实官方全图记录进行非负岭拟合（ridge-prior-0.1）。研究冻结候选路径 "+FrozenCandidatePath+"；研究模型 ID "+ResearchModelId+"；冻结候选文件 SHA-256 "+ResearchParameterSha+"。产品模型 ID 由可执行产品参数独立生成，不复用研究模型 ID。独立新赛事测试为四场、40条记录，RT MAE 0.082624、RMSE 0.108298；RS MAE 1.636221 个百分点且与原版一致。测试指标是研究披露，未填入产品交叉验证报告；不能据此宣称所有比赛均改善或复原 HLTV 私有公式。RS 未达到预设替换门槛，保留原内置基线：公开2025全图全枪人数表的形状，按2026-10-01图的5v5地图胜率调整log-odds，并非完整2026官方胜率表。冻结装备总价代理、强度[1,1.2,1.6,2,2.6,3]、装备比修正、下包赔率倍数[0.03,0.15,0.5]、击杀/伤害/闪光/补枪份额[0.5,0.3,0.1,0.1]与原基线一致，均为本软件假设。六轴固定锚点[0.65,70,0.35,0.70,0.25,0]与尺度[0.65,70,0.35,0.70,0.25,0.08]保留原值，不是官方均值或标准差，不逐场重新中心化。回合末采用既有可追溯正向击杀分配策略，未复刻全部官方残局/拆包/存活分配。产品未携带训练胜率格或伪造交叉验证数据。"
    });
}
