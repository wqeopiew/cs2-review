using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CS2Review.Core;

public sealed class PreparedReport
{
    public string Preview { get; }
    public int CharacterCount => Preview.Length;
    public string PayloadSha256 { get; }
    public string MatchId { get; }
    public string PlayerId { get; }
    public string SourceSha256 { get; }
    public string AnalysisVersion { get; }
    public string AnalysisSnapshotSha256 { get; }
    public string RulesVersion { get; }
    public string ScoringVersion { get; }
    public string Endpoint { get; }
    public string Model { get; }
    public bool Anonymized { get; }
    public TimeSpan NetworkTimeout { get; }
    public int MaxOutputTokens { get; }
    public bool? EnableThinking { get; }
    public int EventCount => Allowed.Count;
    internal int InputBudget { get; }
    internal Dictionary<string, double> Metrics { get; }
    internal Dictionary<string, (int Round, double Time)> Allowed { get; }
    internal Dictionary<string, string> CanonicalIds { get; }
    internal PreparedReport(string input, MatchData match, PlayerAnalysis analysis, ReportOptions options,
        Dictionary<string, double> metrics, Dictionary<string, (int Round, double Time)> allowed,
        Dictionary<string,string> canonicalIds, bool anonymized)
    {
        Preview=input; PayloadSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        MatchId=match.Id; PlayerId=analysis.PlayerId; SourceSha256=match.SourceSha256; AnalysisVersion=analysis.AnalysisVersion;AnalysisSnapshotSha256=analysis.AnalysisSnapshotSha256;
        RulesVersion=analysis.RulesVersion; Endpoint=options.Endpoint; Model=options.Model; InputBudget=options.MaxInputCharacters; Anonymized=anonymized;
        ScoringVersion=analysis.Summary.RatingModelId??"unavailable";
        NetworkTimeout=options.Timeout;MaxOutputTokens=options.MaxOutputTokens;EnableThinking=options.EnableThinking;
        Metrics=new(metrics); Allowed=new(allowed); CanonicalIds=new(canonicalIds);
    }
}

public sealed partial class ReportService
{
    public Task<PreparedReport> PrepareAsync(MatchData match, PlayerAnalysis analysis, ReportOptions options,
        bool anonymizeIdentities=true, CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(match);ArgumentNullException.ThrowIfNull(analysis); ValidateOptions(options,false);
        // Freeze UI-owned analysis and configuration before background work can yield.
        // The key is only needed at send time and never enters this snapshot.
        var frozenAnalysis=JsonSerializer.Deserialize<PlayerAnalysis>(JsonSerializer.Serialize(analysis,DataJson.Options),DataJson.Options)!;
        var frozenOptions=new ReportOptions{Endpoint=options.Endpoint,Model=options.Model,Timeout=options.Timeout,MaxInputCharacters=options.MaxInputCharacters,MaxOutputTokens=options.MaxOutputTokens,EnableThinking=options.EnableThinking};
        ct.ThrowIfCancellationRequested();
        return Task.Run(()=>
        {
            ct.ThrowIfCancellationRequested();
            AnalysisBinding.Validate(match,frozenAnalysis,ct);
            var metrics=Metrics(frozenAnalysis.Summary);
            var (input,allowed)=BuildEvidence(match,frozenAnalysis,metrics,frozenOptions.MaxInputCharacters,ct);
            var canonical=new Dictionary<string,string>();
            if(anonymizeIdentities)
            {
                var identities=new Dictionary<string,string>(StringComparer.Ordinal);
                string Identity(string id) { if(!identities.TryGetValue(id,out var alias)) identities[id]=alias=$"player-{identities.Count+1:000}"; return alias; }
                Identity(frozenAnalysis.PlayerId);
                foreach(var player in match.Players) Identity(player.Id);
                var aliases=allowed.Keys.Select((id,i)=>(id,alias:$"evidence-{i+1:0000}")).ToDictionary(x=>x.id,x=>x.alias);
                var node=JsonNode.Parse(input)!;
                void Visit(JsonNode? value)
                {
                    ct.ThrowIfCancellationRequested();
                    if(value is JsonObject obj)
                        foreach(var property in obj.ToList())
                        {
                            if(property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                            {
                                if(property.Key is "playerId" or "actorId" or "targetId" or "assisterId" or "activeDefuserId") obj[property.Key]=Identity(text);
                                else if(property.Key is "playerName") obj[property.Key]="复盘玩家";
                                else if(property.Key=="eventId" && aliases.TryGetValue(text,out var alias)) obj[property.Key]=alias;
                            }
                            else Visit(property.Value);
                        }
                    else if(value is JsonArray array) foreach(var item in array) Visit(item);
                }
                Visit(node); input=node.ToJsonString(DataJson.Options);
                foreach(var pair in aliases) canonical[pair.Value]=pair.Key;
                allowed=allowed.ToDictionary(p=>aliases[p.Key],p=>p.Value);
            }
            else foreach(var id in allowed.Keys) canonical[id]=id;
            ct.ThrowIfCancellationRequested();
            AnalysisBinding.Validate(match,frozenAnalysis,ct);
            if(input.Length>frozenOptions.MaxInputCharacters-500) throw new ReportValidationException("匿名证据超出输入预算，请增加预算。");
            return new PreparedReport(input,match,frozenAnalysis,frozenOptions,metrics,allowed,canonical,anonymizeIdentities);
        },ct);
    }
    internal static void ValidateOptions(ReportOptions options,bool requireKey)
    {
        if(!Uri.TryCreate(options.Endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0
            || string.IsNullOrWhiteSpace(options.Model) || requireKey && string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("请配置无内嵌凭据的 HTTPS chat/completions 地址、模型和 API 密钥。");
        if(options.Timeout<=TimeSpan.Zero || options.Timeout>TimeSpan.FromMinutes(10) || options.MaxInputCharacters is <2000 or >100000 || options.MaxOutputTokens is <128 or >16000)
            throw new ArgumentException("报告预算或超时配置超出允许范围。");
    }
}
