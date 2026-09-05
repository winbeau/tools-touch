using System.Text.Json;
using System.Text.RegularExpressions;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class PreferenceService(LocalDatabase database) : IPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<PreferenceConstraint> Parse(string? promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return [];
        var text = promptText.Trim();
        var constraints = new List<PreferenceConstraint>();
        AddIf(text, constraints, "research_direction", "大模型", PreferenceConstraintStrength.Soft,
            "大模型", "大模型|LLM|large language model|World Model|world model");
        AddIf(text, constraints, "degree", "masters-only", PreferenceConstraintStrength.Hard,
            "不接受直博", "不接受直博|不考虑直博|不要直博");
        AddIf(text, constraints, "degree", "masters-preferred", PreferenceConstraintStrength.Soft,
            "希望硕士", "希望硕士|偏好硕士");
        AddIf(text, constraints, "internship", "no-internship", PreferenceConstraintStrength.Hard,
            "不接受实习", "不接受实习|不希望实习");
        AddIf(text, constraints, "internship", "internship-compatible", PreferenceConstraintStrength.Soft,
            "允许实习", "允许实习|接受实习|希望实习");
        foreach (var city in new[] { "北京", "上海", "杭州", "深圳", "广州" })
            if (text.Contains(city, StringComparison.Ordinal) && Regex.IsMatch(text, $"偏好[^。；，,]*{Regex.Escape(city)}|{Regex.Escape(city)}[^。；，,]*(偏好|希望)"))
                constraints.Add(new("region", city, PreferenceConstraintStrength.Soft, city));
        if (text.Contains("硕士或博士", StringComparison.Ordinal) || text.Contains("硕博都可以", StringComparison.Ordinal))
            constraints.Add(new("degree", "masters-or-doctorate", PreferenceConstraintStrength.Ambiguous, "硕士或博士"));
        return constraints
            .GroupBy(item => item.Key + "\0" + item.Value, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Strength).First())
            .ToArray();
    }

    public PreferenceRevisionRecord Save(string? promptText, IReadOnlyList<PreferenceConstraint> constraints, bool userConfirmed)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        var normalized = constraints.Select(item => item with
        {
            Key = Required(item.Key, "PREFERENCE_KEY_REQUIRED"),
            Value = Required(item.Value, "PREFERENCE_VALUE_REQUIRED"),
            SourceText = Required(item.SourceText, "PREFERENCE_SOURCE_REQUIRED")
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO PreferenceRevision(Id,Version,PromptText,ParsedConstraintsJson,UserConfirmed,CreatedAt)
            SELECT $id,COALESCE(MAX(Version),0)+1,$prompt,$constraints,$confirmed,$now FROM PreferenceRevision
            """, ("$id", id), ("$prompt", string.IsNullOrWhiteSpace(promptText) ? null : promptText.Trim()),
            ("$constraints", JsonSerializer.Serialize(normalized, JsonOptions)), ("$confirmed", userConfirmed ? 1 : 0), ("$now", now.ToString("O")));
        command.ExecuteNonQuery();
        var version = Convert.ToInt32(Scalar(connection, "SELECT Version FROM PreferenceRevision WHERE Id=$id", ("$id", id)));
        return new(id, version, string.IsNullOrWhiteSpace(promptText) ? null : promptText.Trim(), normalized, userConfirmed, now);
    }

    private static void AddIf(string text, ICollection<PreferenceConstraint> target, string key, string value,
        PreferenceConstraintStrength strength, string sourceText, string pattern)
    {
        if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            target.Add(new(key, value, strength, sourceText));
    }

    private static string Required(string value, string error) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(error) : value.Trim();
    private static object? Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = LocalDatabase.Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }
}
