using System.Text.RegularExpressions;

namespace ToolsTouch.Core;

public sealed record CycleResolution(int? SourceYear, int? CycleYear, int? EntryYear, string[] Reasons);

public static partial class CycleResolver
{
    public static CycleResolution Resolve(int targetCycleYear, int? sourceYear, string? title)
    {
        if (targetCycleYear is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(targetCycleYear));
        var normalizedSource = sourceYear is >= 1900 and <= 2200 ? sourceYear : null;
        int? entryYear = null;
        if (title is not null && EntryYearPattern().Match(title) is { Success: true } match &&
            int.TryParse(match.Groups["year"].Value, out var parsed))
            entryYear = parsed;
        var reasons = new List<string>();
        if (sourceYear is not null && normalizedSource is null) reasons.Add("SOURCE_YEAR_UNKNOWN");
        int? cycleYear = normalizedSource;
        if (cycleYear is null) reasons.Add("CYCLE_YEAR_UNKNOWN");
        if (entryYear == targetCycleYear) reasons.Add("ENTRY_YEAR_EQUALS_CYCLE_YEAR");
        if (entryYear is not null && entryYear != targetCycleYear) reasons.Add("ENTRY_YEAR_SEPARATE_FROM_CYCLE_YEAR");
        return new(normalizedSource, cycleYear, entryYear, reasons.ToArray());
    }

    [GeneratedRegex(@"(?<!\d)(?<year>19\d{2}|20\d{2}|21\d{2})\s*级")]
    private static partial Regex EntryYearPattern();
}
