using CS2Review.Core;

namespace CS2Review.Desktop;

internal static class CanonicalHistory
{
    public static async Task<MatchData> SaveAndLoadAsync(HistoryStore history, MatchData imported, CancellationToken cancellationToken)
    {
        var id = await history.SaveMatchAsync(imported, cancellationToken);
        // After the transaction commits, use the saved snapshot even if a late cancel arrives.
        // A duplicate SHA may retain older event IDs and reports by design.
        var saved = await history.LoadMatchAsync(id, CancellationToken.None)
            ?? throw new InvalidDataException("比赛已经保存，但无法读取保存的比赛快照；请从历史记录重新打开，未使用新解析数据生成报告。");
        if (!string.Equals(saved.SourceSha256, imported.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("保存的比赛来源校验失败，未更新当前复盘。");
        ParserImportService.ValidateMatch(saved);
        return saved;
    }
}
