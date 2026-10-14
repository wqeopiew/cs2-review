using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CS2Review.Core;

public static class AnalysisBinding
{
    // Spatial samples are not consumed by raw statistics/context rules or the text evidence package.
    // This binds all facts they do consume, independent of import time and local source path.
    public static string Snapshot(MatchData match,CancellationToken ct=default)=>Hash(new{match.Id,match.SourceSha256,match.SchemaVersion,match.ParserVersion,match.MapName,match.Players,match.Rounds},ct);
    public static string AnalysisFacts(PlayerAnalysis analysis,CancellationToken ct=default)
    {
        JsonObject Raw(PlayerStats stats)
        {
            var node=JsonSerializer.SerializeToNode(stats,DataJson.Options)!.AsObject();
            foreach(var key in node.Select(p=>p.Key).Where(k=>k.StartsWith("rating",StringComparison.Ordinal)||k is "roundSwing" or "targetRatingVersion").ToList())node.Remove(key);
            return node;
        }
        return Hash(new{analysis.PlayerId,analysis.PlayerName,analysis.AnalysisVersion,analysis.RulesVersion,summary=Raw(analysis.Summary),t=Raw(analysis.T),ct=Raw(analysis.CT),analysis.Rounds,analysis.Findings},ct);
    }
    public static void Bind(MatchData match,PlayerAnalysis analysis,CancellationToken ct=default)
    {
        analysis.MatchId=match.Id;analysis.SourceSha256=match.SourceSha256;analysis.AnalysisSnapshotSha256=Snapshot(match,ct);analysis.AnalysisFactsSha256=AnalysisFacts(analysis,ct);
    }
    public static void Validate(MatchData match,PlayerAnalysis analysis,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(analysis.MatchId!=match.Id || analysis.SourceSha256!=match.SourceSha256 || analysis.AnalysisSnapshotSha256!=Snapshot(match,ct) || analysis.AnalysisFactsSha256!=AnalysisFacts(analysis,ct))
            throw new InvalidDataException("比赛与分析内容不属于同一快照，请重新分析当前比赛。");
    }
    private static string Hash<T>(T value,CancellationToken ct)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var sink=new HashSink(hash,ct);JsonSerializer.Serialize(sink,value,DataJson.Options);ct.ThrowIfCancellationRequested();return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private sealed class HashSink(IncrementalHash hash,CancellationToken ct):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>ct.ThrowIfCancellationRequested();public override void Write(byte[] buffer,int offset,int count)=>Write(buffer.AsSpan(offset,count));public override void Write(ReadOnlySpan<byte> buffer){ct.ThrowIfCancellationRequested();hash.AppendData(buffer);}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
}
