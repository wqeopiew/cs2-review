using CS2Review.Core;

namespace CS2Review.Desktop;

internal static class MatchHeaderFormatter
{
    public static string Format(MatchData match)
    {
        var score = match.FinalTScore is >= 0 && match.FinalCTScore is >= 0 ? $"终局 T {match.FinalTScore} : CT {match.FinalCTScore}" : "比分未知";
        return $"{match.MapName}  ·  {score}  ·  {match.Rounds.Count(r => r.IsValid)} 回合  ·  {match.Platform}";
    }
}
