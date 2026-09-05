using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsTouch.Core;

static class WindowTests
{
    public static Task RunAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine("docs", "design", "fixtures", "window-cases.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var fixture = document.RootElement;
        Check(fixture.GetProperty("rule_version").GetString() == WindowEvaluator.RuleVersion, "window fixture rule version changed");

        foreach (var testCase in fixture.GetProperty("cases").EnumerateArray())
        {
            var input = ReadInput(testCase);
            var expected = testCase.GetProperty("expected");
            var asOf = testCase.GetProperty("as_of").GetDateTimeOffset();
            var result = new WindowEvaluator().Evaluate(input, asOf);
            var caseId = testCase.GetProperty("case_id").GetString()!;

            Check(result.TargetCycleYear == input.TargetCycleYear, caseId + ": target cycle year");
            Check(result.ActualState.ToString() == expected.GetProperty("actual_state").GetString(), caseId + ": actual state");
            Check(result.ActualBasis.ToString() == expected.GetProperty("actual_basis").GetString(), caseId + ": actual basis");
            Check(result.EstimatedPhase.ToString() == expected.GetProperty("estimated_phase").GetString(), caseId + ": estimated phase");
            Check(result.RuleVersion == WindowEvaluator.RuleVersion, caseId + ": rule version");
            Check(result.AsOf == asOf, caseId + ": as-of is preserved");

            AssertDate(result.ProjectedStart, expected, "projected_start", caseId);
            AssertDate(result.ProjectedEndExclusive, expected, "projected_end_exclusive", caseId);
            AssertDate(result.EndExclusive, expected, "end_exclusive", caseId);
            if (expected.TryGetProperty("required_reason", out var reason))
                Check(result.Reasons.Contains(reason.GetString()!), caseId + ": required reason");
        }

        var cycle = CycleResolver.Resolve(2026, 0, "2027 级夏令营");
        Check(cycle.SourceYear is null && cycle.CycleYear is null && cycle.EntryYear == 2027,
            "cycle resolver must keep unknown source year separate from entry year");
        Check(cycle.Reasons.Contains("SOURCE_YEAR_UNKNOWN") && cycle.Reasons.Contains("CYCLE_YEAR_UNKNOWN") &&
            cycle.Reasons.Contains("ENTRY_YEAR_SEPARATE_FROM_CYCLE_YEAR"),
            "cycle resolver must explain year distinctions");
        var stable = CycleResolver.Resolve(2026, 2025, "2027级");
        Check(stable.SourceYear == 2025 && stable.CycleYear == 2025 && stable.EntryYear == 2027,
            "cycle resolver must preserve source, cycle and entry years");

        Console.WriteLine("PASS: cycle resolver and 20 fixture-driven admission window cases");
        return Task.CompletedTask;
    }

    private static WindowInput ReadInput(JsonElement testCase)
    {
        var currentElement = testCase.GetProperty("current");
        var historicalElement = testCase.GetProperty("historical");
        return new WindowInput(
            testCase.GetProperty("target_cycle_year").GetInt32(),
            testCase.GetProperty("time_zone").GetString()!,
            currentElement.ValueKind == JsonValueKind.Null ? null : ReadCurrent(currentElement),
            historicalElement.ValueKind == JsonValueKind.Null ? null : ReadHistorical(historicalElement));
    }

    private static CurrentWindow ReadCurrent(JsonElement value) => new(
        value.GetProperty("cycle_year").GetInt32(),
        Enum.Parse<ActualWindowBasis>(value.GetProperty("basis").GetString()!, ignoreCase: false),
        ReadRange(value),
        value.TryGetProperty("explicit_status", out var status) ? status.GetString() : null,
        ReadOptionalDate(value, "status_observed_at"),
        ReadOptionalDate(value, "status_valid_until"),
        null,
        value.TryGetProperty("has_conflict", out var conflict) && conflict.GetBoolean());

    private static HistoricalWindow ReadHistorical(JsonElement value) => new(
        value.GetProperty("cycle_year").GetInt32(), ReadRange(value));

    private static RegistrationRange ReadRange(JsonElement value) => new(
        ReadOptionalString(value, "start"),
        ReadOptionalString(value, "end"),
        Enum.Parse<DatePrecision>(value.GetProperty("precision").GetString()!, ignoreCase: false));

    private static DateTimeOffset? ReadOptionalDate(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetDateTimeOffset()
            : null;

    private static string? ReadOptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static void AssertDate(DateTimeOffset? actual, JsonElement expected, string name, string caseId)
    {
        if (!expected.TryGetProperty(name, out var property)) return;
        var expectedValue = property.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : property.GetDateTimeOffset();
        Check(actual == expectedValue, caseId + ": " + name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
