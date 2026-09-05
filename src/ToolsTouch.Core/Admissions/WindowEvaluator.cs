namespace ToolsTouch.Core;

public enum ActualWindowState { NotStarted, Open, Closed, Cancelled, Unknown, Conflict }
public enum ActualWindowBasis { CurrentOfficial, CurrentAggregator, UserVerified, NoCurrentEvidence }
public enum EstimatedWindowPhase { NotAvailable, Upcoming, WithinEstimatedRange, EstimatePassed, Unprojectable }

public sealed record RegistrationRange(string? StartRaw, string? EndRaw, DatePrecision Precision);

public sealed record CurrentWindow(
    int CycleYear,
    ActualWindowBasis Basis,
    RegistrationRange Range,
    string? ExplicitStatus = null,
    DateTimeOffset? StatusObservedAt = null,
    DateTimeOffset? StatusValidUntil = null,
    string[]? EvidenceIds = null,
    bool HasConflict = false);

public sealed record HistoricalWindow(
    int CycleYear,
    RegistrationRange Range,
    string[]? EvidenceIds = null);

public sealed record WindowInput(int TargetCycleYear, string TimeZoneId, CurrentWindow? Current, HistoricalWindow? Historical);

public sealed record WindowAssessment(
    int TargetCycleYear,
    ActualWindowState ActualState,
    ActualWindowBasis ActualBasis,
    EstimatedWindowPhase EstimatedPhase,
    DateTimeOffset? Start,
    DateTimeOffset? EndExclusive,
    DateTimeOffset? ProjectedStart,
    DateTimeOffset? ProjectedEndExclusive,
    string[] Reasons,
    string[] EvidenceIds,
    DateTimeOffset AsOf,
    string RuleVersion,
    int? SourceYear = null,
    RegistrationRange? OriginalRange = null,
    RegistrationRange? ProjectedRange = null);

public interface IWindowEvaluator
{
    WindowAssessment Evaluate(WindowInput input, DateTimeOffset asOf);
}

public sealed class WindowEvaluator : IWindowEvaluator
{
    public const string RuleVersion = "window-v1";
    private static readonly TimeSpan StatusDefaultLifetime = TimeSpan.FromHours(24);

    public WindowAssessment Evaluate(WindowInput input, DateTimeOffset asOf)
    {
        if (input.TargetCycleYear is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(input.TargetCycleYear));
        var timeZone = ResolveTimeZone(input.TimeZoneId);
        var evidence = input.Current?.EvidenceIds ?? input.Historical?.EvidenceIds ?? [];
        var reasons = new List<string>();
        var actualBasis = input.Current?.Basis ?? ActualWindowBasis.NoCurrentEvidence;
        if (input.Current is { } current)
        {
            if (current.CycleYear != input.TargetCycleYear) return Assessment(input, asOf, actualBasis, ActualWindowState.Conflict, EstimatedWindowPhase.NotAvailable,
                null, null, null, null, ["YEAR_AMBIGUOUS"], evidence, null, null);
            if (current.HasConflict) return Assessment(input, asOf, actualBasis, ActualWindowState.Conflict, EstimatedWindowPhase.NotAvailable,
                null, null, null, null, ["CONFLICTING_CURRENT_EVIDENCE"], evidence, null, current.Range);
            if (current.ExplicitStatus is "Cancelled") return Assessment(input, asOf, actualBasis, ActualWindowState.Cancelled, EstimatedWindowPhase.NotAvailable,
                null, null, null, null, [], evidence, null, current.Range);
            if (current.ExplicitStatus is not (null or "Unknown" or "Open" or "Closed" or "Cancelled"))
                reasons.Add("INVALID_EXPLICIT_STATUS");
            var parsed = Parse(current.Range, timeZone, reasons);
            if (parsed.Unparseable) return Assessment(input, asOf, actualBasis, ActualWindowState.Unknown, EstimatedWindowPhase.NotAvailable,
                null, null, null, null, reasons.ToArray(), evidence, null, current.Range);
            if (parsed.Invalid) return Assessment(input, asOf, actualBasis, ActualWindowState.Conflict, EstimatedWindowPhase.NotAvailable,
                parsed.Start, parsed.EndExclusive, null, null, reasons.Append("INVALID_DATE_RANGE").Distinct().ToArray(), evidence, null, current.Range);
            var state = DetermineActual(parsed.Start, parsed.EndExclusive, asOf, current, timeZone, reasons);
            return Assessment(input, asOf, actualBasis, state, EstimatedWindowPhase.NotAvailable, parsed.Start, parsed.EndExclusive,
                null, null, reasons.ToArray(), evidence, null, current.Range);
        }

        var actual = ActualWindowState.Unknown;
        if (input.Historical is not { } historical)
            return Assessment(input, asOf, ActualWindowBasis.NoCurrentEvidence, actual, EstimatedWindowPhase.NotAvailable,
                null, null, null, null, [], evidence, null, null);
        var historicalParsed = Parse(historical.Range, timeZone, reasons);
        if (historicalParsed.Unparseable || historicalParsed.Invalid || historicalParsed.Start == null && historicalParsed.EndExclusive == null)
            return Assessment(input, asOf, ActualWindowBasis.NoCurrentEvidence, actual, EstimatedWindowPhase.Unprojectable,
                null, null, null, null, reasons.Append("INVALID_PROJECTED_DATE").Distinct().ToArray(), evidence, historical.CycleYear, historical.Range, historical.Range);
        var projected = Project(historical.Range, historicalParsed, historical.CycleYear, input.TargetCycleYear, timeZone);
        if (projected.Invalid)
            return Assessment(input, asOf, ActualWindowBasis.NoCurrentEvidence, actual, EstimatedWindowPhase.Unprojectable,
                null, null, null, null, ["INVALID_PROJECTED_DATE"], evidence, historical.CycleYear, historical.Range, historical.Range);
        var phase = DetermineEstimate(projected.Start, projected.EndExclusive, asOf);
        return Assessment(input, asOf, ActualWindowBasis.NoCurrentEvidence, actual, phase, null, null, projected.Start, projected.EndExclusive,
            reasons.ToArray(), evidence, historical.CycleYear, historical.Range, historical.Range);
    }

    private static ActualWindowState DetermineActual(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset asOf, CurrentWindow current,
        TimeZoneInfo timeZone, List<string> reasons)
    {
        var now = asOf.ToUniversalTime();
        if (end is { } knownEnd && now >= knownEnd.ToUniversalTime()) return ActualWindowState.Closed;
        if (start is { } knownStart && now < knownStart.ToUniversalTime()) return ActualWindowState.NotStarted;
        if (start is { } openStart && end is { } openEnd && now >= openStart.ToUniversalTime() && now < openEnd.ToUniversalTime()) return ActualWindowState.Open;
        if (current.ExplicitStatus is "Open" or "Closed")
        {
            var validUntil = current.StatusValidUntil ?? current.StatusObservedAt?.Add(StatusDefaultLifetime);
            if (validUntil is null || asOf <= validUntil)
            {
                if (current.ExplicitStatus == "Closed") return ActualWindowState.Closed;
                if (current.ExplicitStatus == "Open" && end is null) return ActualWindowState.Open;
            }
            else reasons.Add("STATUS_EXPIRED");
        }
        return ActualWindowState.Unknown;
    }

    private static EstimatedWindowPhase DetermineEstimate(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset asOf)
    {
        var now = asOf.ToUniversalTime();
        if (start is { } projectedStart && now < projectedStart.ToUniversalTime()) return EstimatedWindowPhase.Upcoming;
        if (start is { } withinStart && end is { } withinEnd && now >= withinStart.ToUniversalTime() && now < withinEnd.ToUniversalTime()) return EstimatedWindowPhase.WithinEstimatedRange;
        if (end is { } passedEnd && now >= passedEnd.ToUniversalTime()) return EstimatedWindowPhase.EstimatePassed;
        return EstimatedWindowPhase.Unprojectable;
    }

    private static ParsedRange Parse(RegistrationRange range, TimeZoneInfo timeZone, List<string> reasons)
    {
        if (range.Precision is DatePrecision.Unknown or DatePrecision.Approximate)
        {
            if (range.StartRaw != null || range.EndRaw != null) reasons.Add("DATE_PRECISION_UNCERTAIN");
            return new(null, null, false, false);
        }
        try
        {
            DateTimeOffset? start;
            DateTimeOffset? end;
            if (range.Precision == DatePrecision.Date)
            {
                start = range.StartRaw == null ? null : LocalDate(DateOnly.ParseExact(range.StartRaw, "yyyy-MM-dd"), timeZone);
                end = range.EndRaw == null ? null : LocalDate(DateOnly.ParseExact(range.EndRaw, "yyyy-MM-dd").AddDays(1), timeZone);
            }
            else
            {
                start = range.StartRaw == null ? null : DateTimeOffset.Parse(range.StartRaw);
                end = range.EndRaw == null ? null : DateTimeOffset.Parse(range.EndRaw);
            }
            return new(start, end, start is { } left && end is { } right && left >= right, false);
        }
        catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
        {
            reasons.Add("DATE_UNPARSEABLE");
            return new(null, null, false, true);
        }
    }

    private static ParsedRange Project(RegistrationRange raw, ParsedRange range, int sourceYear, int targetYear, TimeZoneInfo timeZone)
    {
        if (sourceYear is < 1900 or > 2200) return new(null, null, true, false);
        try
        {
            var yearDelta = targetYear - sourceYear;
            if (raw.Precision == DatePrecision.Date)
            {
                DateOnly? startDate = raw.StartRaw is null ? null : ShiftDate(DateOnly.ParseExact(raw.StartRaw, "yyyy-MM-dd"), yearDelta);
                DateOnly? endDate = raw.EndRaw is null ? null : ShiftDate(DateOnly.ParseExact(raw.EndRaw, "yyyy-MM-dd"), yearDelta);
                return new(startDate is null ? null : LocalDate(startDate.Value, timeZone),
                    endDate is null ? null : LocalDate(endDate.Value.AddDays(1), timeZone), false, false);
            }

            return new(ShiftInstant(range.Start, yearDelta), ShiftInstant(range.EndExclusive, yearDelta), false, false);
        }
        catch (ArgumentOutOfRangeException) { return new(null, null, true, false); }
        catch (FormatException) { return new(null, null, true, true); }
    }

    private static DateOnly ShiftDate(DateOnly date, int yearDelta) =>
        new(date.Year + yearDelta, date.Month, date.Day);

    private static DateTimeOffset? ShiftInstant(DateTimeOffset? value, int yearDelta) => value is null
        ? null
        : new DateTimeOffset(value.Value.Year + yearDelta, value.Value.Month, value.Value.Day,
            value.Value.Hour, value.Value.Minute, value.Value.Second, value.Value.Millisecond, value.Value.Offset)
                .AddTicks(value.Value.Ticks % TimeSpan.TicksPerMillisecond);

    private static DateTimeOffset LocalDate(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("TIME_ZONE_REQUIRED", nameof(id));
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) when (id == "Asia/Shanghai") { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) when (id == "America/Los_Angeles") { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
    }

    private static WindowAssessment Assessment(WindowInput input, DateTimeOffset asOf, ActualWindowBasis basis, ActualWindowState state,
        EstimatedWindowPhase phase, DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset? projectedStart, DateTimeOffset? projectedEnd,
        string[] reasons, string[] evidence, int? sourceYear, RegistrationRange? original, RegistrationRange? projected = null) =>
        new(input.TargetCycleYear, state, basis, phase, start, end, projectedStart, projectedEnd, reasons, evidence, asOf, RuleVersion, sourceYear, original, projected);

    private sealed record ParsedRange(DateTimeOffset? Start, DateTimeOffset? EndExclusive, bool Invalid, bool Unparseable);
}
