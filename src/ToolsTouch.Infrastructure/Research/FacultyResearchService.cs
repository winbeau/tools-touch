using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Research;

public sealed class FacultyResearchService(
    LocalDatabase database,
    ArtifactStore artifacts,
    SourceRepository sources) : IFacultyResearchService
{
    private const string HomepageSource = "official-faculty-homepage";
    private const string PaperSource = "paper-metadata";
    private const string EvaluationSource = "public-evaluation";
    private static readonly Regex EmailPattern = new(@"(?:mailto:)?([A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public HomepageEvidenceSummary ImportHomepageEvidence(HomepageEvidenceInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProfessorId);
        var url = PublicWeb.ValidateUri(input.Url).AbsoluteUri;
        if (input.Text.Length > 2_000_000) throw new InvalidOperationException("HOMEPAGE_TOO_LARGE");
        EnsureProfessor(input.ProfessorId);
        var bytes = new UTF8Encoding(false, true).GetBytes(input.Text);
        var snapshot = SaveSnapshot(HomepageSource, input.ProfessorId + "|" + url, input.Url, input.Title, bytes, input.FetchedAt, "faculty-homepage-v1");
        var extracted = ExtractHomepageClaims(input.Text);
        var claimCount = 0;
        foreach (var claim in extracted.Claims)
        {
            var id = StableId("claim", snapshot.Id + "|" + claim.Type + "|" + claim.ValueJson);
            if (!ClaimExists(id))
            {
                sources.AddClaim(new EvidenceClaim(id, snapshot.Id, "Professor", input.ProfessorId, claim.Type,
                    claim.ValueJson, claim.QuotedText, claim.LocatorJson, "OfficialFact", "High"));
                claimCount++;
            }
        }
        if (extracted.Email is not null) SetEmail(input.ProfessorId, extracted.Email);
        return new HomepageEvidenceSummary(input.ProfessorId, snapshot.Id, claimCount, extracted.Email, Revision());
    }

    public PaperAttributionResult ImportPaper(Paper paper, string professorId)
    {
        ArgumentNullException.ThrowIfNull(paper);
        ArgumentException.ThrowIfNullOrWhiteSpace(professorId);
        EnsureProfessor(professorId);
        var sourceUrl = PublicWeb.ValidateUri(paper.SourceUrl).AbsoluteUri;
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            external_id = paper.ExternalId,
            title = paper.Title,
            authors = paper.Authors,
            paper.Year,
            paper.Abstract,
            source_url = sourceUrl
        });
        var snapshot = SaveSnapshot(PaperSource, paper.ExternalId, sourceUrl, paper.Title, metadata, DateTimeOffset.UtcNow, "paper-metadata-v1");
        var paperId = SavePaperMetadata(paper with { SourceUrl = sourceUrl });
        var professorName = ProfessorName(professorId);
        var matchingAuthors = paper.Authors.Where(author => SamePerson(author, professorName)).ToArray();
        var matches = matchingAuthors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var claimId = StableId("claim", snapshot.Id + "|" + paperId + "|author-attribution|" + professorId);
        if (matchingAuthors.Length != 1)
        {
            var resolutionId = StableId("paper-resolution", paperId + "|" + professorId);
            var changed = InsertPaperResolution(resolutionId, paperId, professorId, matches, matches.Length == 0 ?
                "PAPER_AUTHOR_NOT_UNIQUELY_MATCHED" : "PAPER_AUTHOR_NAME_MATCH_AMBIGUOUS");
            if (changed) IncrementRevision();
            return new PaperAttributionResult(paperId, PaperAttributionState.NeedsVerification, null, matches, resolutionId, null, Revision());
        }

        var claimInserted = InsertClaimIfMissing(new EvidenceClaim(claimId, snapshot.Id, "Paper", paperId, "author_attribution",
            JsonSerializer.Serialize(new { professor_id = professorId, author = matches[0], method = "exact_normalized_name" }),
            matches[0], JsonSerializer.Serialize(new { source_url = sourceUrl }), "OfficialFact", "Medium"));
        var linkInserted = InsertPaperLink(professorId, paperId);
        var resolutionInserted = InsertResolvedPaperResolution(paperId, professorId, matches, claimId);
        if (claimInserted || linkInserted || resolutionInserted) IncrementRevision();
        return new PaperAttributionResult(paperId, PaperAttributionState.Attributed, professorId, matches, null, claimId, Revision());
    }

    public PaperReadResult RecordPaperRead(PaperReadInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.PaperId);
        if (input.ReadScope is not ("Abstract" or "Pages" or "FullText" or "Unreadable"))
            throw new InvalidOperationException("PAPER_READ_SCOPE_INVALID");
        if (input.StartPage is < 0 || input.EndPage is < 0 || input.StartPage is not null && input.EndPage is not null && input.EndPage < input.StartPage)
            throw new InvalidOperationException("PAPER_PAGE_RANGE_INVALID");
        EnsurePaper(input.PaperId);
        var bytes = input.TextContent?.ToArray();
        if (bytes is { Length: > 2_000_000 }) throw new InvalidOperationException("PAPER_TEXT_TOO_LARGE");
        var contentHash = input.ContentHash ?? (bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var artifact = bytes is null ? null : artifacts.Stage("paper-reading", bytes, "text/plain; charset=utf-8", ".txt");
        var id = StableId("read", input.PaperId + "|" + (contentHash ?? "") + "|" + (input.StartPage?.ToString() ?? "") + "|" +
            (input.EndPage?.ToString() ?? "") + "|" + input.ReadScope + "|" + input.ExtractorVersion);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (artifact is not null) ArtifactStore.Insert(connection, transaction, artifact);
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO PaperReadSegment(Id,PaperId,ContentHash,StartPage,EndPage,ReadScope,ExtractorVersion,TextArtifactId,CreatedAt)
            VALUES($id,$paper,$hash,$start,$end,$scope,$extractor,$artifact,$now)
            """, ("$id", id), ("$paper", input.PaperId), ("$hash", contentHash), ("$start", input.StartPage), ("$end", input.EndPage),
            ("$scope", input.ReadScope), ("$extractor", input.ExtractorVersion), ("$artifact", artifact?.Id), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        insert.Transaction = transaction;
        var inserted = insert.ExecuteNonQuery() == 1;
        if (inserted)
        {
            using var revision = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
            revision.Transaction = transaction;
            revision.ExecuteNonQuery();
        }
        transaction.Commit();
        return new PaperReadResult(id, input.PaperId, input.ReadScope, input.StartPage, input.EndPage, artifact?.Id, Revision());
    }

    public ProfessorEvaluationResult ImportEvaluation(ProfessorEvaluationInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProfessorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Summary);
        if (input.VerificationState is not ("Unverified" or "Conflicting" or "UserVerified"))
            throw new InvalidOperationException("EVALUATION_VERIFICATION_STATE_INVALID");
        using var topics = JsonDocument.Parse(input.TopicsJson);
        if (topics.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new InvalidOperationException("EVALUATION_TOPICS_JSON_INVALID");
        EnsureProfessor(input.ProfessorId);
        var url = PublicWeb.ValidateUri(input.SourceUrl).AbsoluteUri;
        var bytes = new UTF8Encoding(false, true).GetBytes(input.RawText);
        var snapshot = SaveSnapshot(EvaluationSource, input.ProfessorId + "|" + url + "|" + Convert.ToHexString(SHA256.HashData(bytes)),
            url, input.SourceType, bytes, input.FetchedAt, "evaluation-v1");
        var claimValue = JsonSerializer.Serialize(new
        {
            source_type = input.SourceType,
            summary = input.Summary,
            topics = JsonDocument.Parse(input.TopicsJson).RootElement,
            posted_at = input.PostedAt
        });
        var claimId = StableId("claim", snapshot.Id + "|evaluation|" + input.Summary);
        var claimInserted = InsertClaimIfMissing(new EvidenceClaim(claimId, snapshot.Id, "Professor", input.ProfessorId,
            "evaluation_statement", claimValue, input.RawText, JsonSerializer.Serialize(new { url, source_type = input.SourceType }),
            "ThirdPartyReport", input.VerificationState == "UserVerified" ? "Medium" : "Unverified"));
        var evaluationId = StableId("evaluation", claimId);
        var evaluationInserted = InsertEvaluation(evaluationId, input, claimId);
        if (claimInserted || evaluationInserted) IncrementRevision();
        return new ProfessorEvaluationResult(input.ProfessorId, evaluationId, snapshot.Id, claimId, Revision());
    }

    private SourceSnapshot SaveSnapshot(string sourceKey, string externalId, string url, string title, byte[] content,
        DateTimeOffset fetchedAt, string parseVersion)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var previous = LatestSnapshot(sourceKey, externalId, hash);
        if (previous is not null) return previous;
        var snapshotId = StableId("snapshot", sourceKey + "|" + externalId + "|" + hash + "|" + fetchedAt.ToUniversalTime().ToString("O"));
        return sources.SaveSnapshot(new SourceSnapshot(snapshotId, sourceKey, url, url, externalId, hash, fetchedAt, null, null,
            "https", parseVersion, title: title), content, sourceKey, "text/plain; charset=utf-8", ".txt");
    }

    private SourceSnapshot? LatestSnapshot(string sourceKey, string externalId, string hash)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ParseVersion,ArtifactId,Title
            FROM SourceSnapshot WHERE SourceKey=$source AND ExternalRecordId=$external AND ContentHash=$hash ORDER BY FetchedAt DESC,Id DESC LIMIT 1
            """, ("$source", sourceKey), ("$external", externalId), ("$hash", hash));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceSnapshot(reader.GetString(0), reader.GetString(1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4),
            reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), NullableDate(reader, 7), NullableInt(reader, 8), reader.GetString(9),
            reader.GetString(10), Nullable(reader, 11), Nullable(reader, 12));
    }

    private string SavePaperMetadata(Paper paper)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Paper(Id,ExternalId,Title,AuthorsJson,Year,Abstract,SourceUrl,LocalPath,ParseStatus)
            VALUES($id,$external,$title,$authors,$year,$abstract,$source,NULL,$status)
            ON CONFLICT(ExternalId) DO UPDATE SET Title=excluded.Title,AuthorsJson=excluded.AuthorsJson,Year=excluded.Year,
              Abstract=COALESCE(excluded.Abstract,Paper.Abstract),SourceUrl=excluded.SourceUrl,ParseStatus=excluded.ParseStatus
            RETURNING Id
            """, ("$id", paper.Id), ("$external", paper.ExternalId), ("$title", paper.Title),
            ("$authors", JsonSerializer.Serialize(paper.Authors)), ("$year", paper.Year), ("$abstract", paper.Abstract),
            ("$source", paper.SourceUrl), ("$status", paper.ParseStatus));
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("PAPER_SAVE_FAILED");
    }

    private static bool SamePerson(string author, string professor)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Normalize(author) == Normalize(professor);
    }

    private void EnsureProfessor(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM Professor WHERE Id=$id", ("$id", id));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new KeyNotFoundException("PROFESSOR_NOT_FOUND");
    }

    private string ProfessorName(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Name FROM Professor WHERE Id=$id", ("$id", id));
        return command.ExecuteScalar() as string ?? throw new KeyNotFoundException("PROFESSOR_NOT_FOUND");
    }

    private void EnsurePaper(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM Paper WHERE Id=$id", ("$id", id));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new KeyNotFoundException("PAPER_NOT_FOUND");
    }

    private bool ClaimExists(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM EvidenceClaim WHERE Id=$id", ("$id", id));
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private bool InsertClaimIfMissing(EvidenceClaim claim)
    {
        if (ClaimExists(claim.Id)) return false;
        sources.AddClaim(claim);
        return true;
    }

    private bool InsertPaperLink(string professorId, string paperId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "INSERT OR IGNORE INTO ProfessorPaper(ProfessorId,PaperId) VALUES($prof,$paper)",
            ("$prof", professorId), ("$paper", paperId));
        return command.ExecuteNonQuery() == 1;
    }

    private bool InsertPaperResolution(string id, string paperId, string professorId, IReadOnlyList<string> matches, string reason)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO PaperAttributionResolution(Id,PaperId,ProfessorId,AuthorMatchesJson,State,Reason,CreatedAt)
            VALUES($id,$paper,$prof,$matches,'Pending',$reason,$now)
            """, ("$id", id), ("$paper", paperId), ("$prof", professorId), ("$matches", JsonSerializer.Serialize(matches)),
            ("$reason", reason), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        return command.ExecuteNonQuery() == 1;
    }

    private bool InsertResolvedPaperResolution(string paperId, string professorId, IReadOnlyList<string> matches, string claimId)
    {
        var id = StableId("paper-resolution", paperId + "|" + professorId);
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO PaperAttributionResolution(Id,PaperId,ProfessorId,AuthorMatchesJson,State,Reason,CreatedAt,ResolvedAt)
            VALUES($id,$paper,$prof,$matches,'Resolved','UNIQUE_AUTHOR_MATCH',$now,$now)
            ON CONFLICT(Id) DO UPDATE SET State='Resolved',Reason='UNIQUE_AUTHOR_MATCH',ResolvedAt=excluded.ResolvedAt
            WHERE PaperAttributionResolution.State<>'Resolved'
            """, ("$id", id), ("$paper", paperId), ("$prof", professorId), ("$matches", JsonSerializer.Serialize(matches)),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        var affected = command.ExecuteNonQuery();
        return affected == 1;
    }

    private bool InsertEvaluation(string id, ProfessorEvaluationInput input, string claimId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO ProfessorEvaluation(Id,ProfessorId,ClaimId,SourceType,PostedAt,Summary,TopicsJson,VerificationState,CreatedAt)
            VALUES($id,$prof,$claim,$type,$posted,$summary,$topics,$state,$now)
            """, ("$id", id), ("$prof", input.ProfessorId), ("$claim", claimId), ("$type", input.SourceType),
            ("$posted", input.PostedAt?.ToString("O")), ("$summary", input.Summary), ("$topics", input.TopicsJson),
            ("$state", input.VerificationState), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        return command.ExecuteNonQuery() == 1;
    }

    private void SetEmail(string professorId, string email)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, "UPDATE Professor SET Email=$email,UpdatedAt=$now WHERE Id=$id AND (Email IS NULL OR Email<>$email)",
            ("$email", email), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", professorId));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() == 1)
        {
            using var revision = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
            revision.Transaction = transaction;
            revision.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void IncrementRevision()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        command.ExecuteNonQuery();
    }

    private long Revision() => new OrganizationRepository(database).Get().DataRevision;

    private static HomepageClaims ExtractHomepageClaims(string text)
    {
        var lines = text.Split('\n').Select((value, index) => (Value: Collapse(value), Index: index)).Where(item => item.Value.Length > 0).ToArray();
        var claims = new List<ExtractedClaim>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Value;
            if (ContainsAny(line, "research interests", "research focus", "研究方向", "研究兴趣"))
            {
                var value = AfterColon(line);
                if (value.Length == 0 && i + 1 < lines.Length) value = lines[i + 1].Value;
                if (value.Length > 0) claims.Add(new("research_direction", JsonSerializer.Serialize(new { text = value }), line,
                    JsonSerializer.Serialize(new { line = lines[i].Index, field = "research" })));
            }
            if (ContainsAny(line, "招生", "招收", "recruit", "admission") && ContainsYear(line))
                claims.Add(new("recruitment_statement", JsonSerializer.Serialize(new { text = line }), line,
                    JsonSerializer.Serialize(new { line = lines[i].Index, field = "recruitment" })));
        }
        var email = EmailPattern.Match(text) is { Success: true } match ? NormalizeEmail(match.Groups[1].Value) : null;
        return new HomepageClaims(claims, email);
    }

    private static string? NormalizeEmail(string value) => MailAddress.TryCreate(value, out var address) && address.Address == value &&
        !value.Contains('\r') && !value.Contains('\n') ? address.Address : null;
    private static bool ContainsAny(string value, params string[] patterns) => patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsYear(string value) => value.Any(char.IsDigit) && value.Contains("20", StringComparison.Ordinal);
    private static string AfterColon(string value) => value.Split(['：', ':'], 2).Skip(1).FirstOrDefault()?.Trim() ?? "";
    private static string Collapse(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string StableId(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));

    private sealed record ExtractedClaim(string Type, string ValueJson, string QuotedText, string LocatorJson);
    private sealed record HomepageClaims(IReadOnlyList<ExtractedClaim> Claims, string? Email);
}
