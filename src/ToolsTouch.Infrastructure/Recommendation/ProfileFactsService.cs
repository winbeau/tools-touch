using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class ProfileFactsService(LocalDatabase database) : IProfileFactsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public ProfileFactsRevision Confirm(ProfileFactsInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ExtractionVersion);
        Validate(input.Facts);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var lookup = LocalDatabase.Command(connection, """
            SELECT CvPath,CvHash,ExperiencesJson FROM UserProfile WHERE Id=$id
            """, ("$id", input.ProfileId));
        lookup.Transaction = transaction;
        using var reader = lookup.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("PROFILE_NOT_FOUND");
        var cvPath = reader.GetString(0);
        var cvHash = reader.GetString(1);
        var experiences = reader.GetString(2);
        reader.Close();

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var factsJson = JsonSerializer.Serialize(input.Facts, JsonOptions);
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO UserProfile(Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed,CreatedAt,StructuredFactsJson,ExtractionVersion,ConfirmedAt)
            SELECT $id,COALESCE(MAX(Version),0)+1,$path,$hash,$experiences,1,$now,$facts,$extractor,$confirmed
            FROM UserProfile
            """, ("$id", id), ("$path", cvPath), ("$hash", cvHash), ("$experiences", experiences), ("$now", now.ToString("O")),
            ("$facts", factsJson), ("$extractor", input.ExtractionVersion.Trim()), ("$confirmed", now.ToString("O")));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        transaction.Commit();

        using var result = LocalDatabase.Command(connection, """
            SELECT Id,Version,CvPath,CvHash,ExperiencesJson,StructuredFactsJson,ExtractionVersion,ConfirmedAt
            FROM UserProfile WHERE Id=$id
            """, ("$id", id));
        using var resultReader = result.ExecuteReader();
        if (!resultReader.Read()) throw new InvalidOperationException("PROFILE_REVISION_NOT_PUBLISHED");
        return new(resultReader.GetString(0), resultReader.GetInt32(1), resultReader.GetString(2), resultReader.GetString(3),
            resultReader.GetString(4), resultReader.GetString(5), resultReader.GetString(6), true,
            DateTimeOffset.Parse(resultReader.GetString(7)));
    }

    private static void Validate(BackgroundFacts facts)
    {
        if (facts.RankingNumerator is not null || facts.RankingDenominator is not null || !string.IsNullOrWhiteSpace(facts.RankingType))
        {
            if (string.IsNullOrWhiteSpace(facts.RankingType) || facts.RankingNumerator is null || facts.RankingDenominator is null ||
                facts.RankingNumerator < 1 || facts.RankingDenominator < facts.RankingNumerator)
                throw new InvalidOperationException("RANKING_FACT_INVALID");
        }
        if (facts.Gpa is not null || facts.GpaScale is not null)
        {
            if (facts.Gpa is null || facts.GpaScale is null || facts.Gpa < 0 || facts.GpaScale <= 0 || facts.Gpa > facts.GpaScale)
                throw new InvalidOperationException("GPA_FACT_INVALID");
        }
        if (facts.PaperState == PaperFactState.KnownZero && facts.PaperCount != 0)
            throw new InvalidOperationException("PAPER_ZERO_FACT_INVALID");
        if (facts.PaperState == PaperFactState.KnownCount && facts.PaperCount is null or < 1)
            throw new InvalidOperationException("PAPER_COUNT_FACT_INVALID");
        if (facts.PaperState == PaperFactState.Unknown && facts.PaperCount is not null)
            throw new InvalidOperationException("PAPER_UNKNOWN_FACT_INVALID");
    }
}
