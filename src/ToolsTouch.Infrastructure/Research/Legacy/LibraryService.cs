using System.Security.Cryptography;
using System.Text.Json;
using UglyToad.PdfPig;

namespace ToolsTouch.Core;

public sealed record PaperPage(int Page, string Text);
public sealed record PaperReading(string PaperId, string Coverage, string SourceUrl, PaperPage[] Pages, string? Note);

public sealed class LibraryService(LocalDatabase database, string dataDirectory, PublicWeb web)
{
    public Paper SavePaper(Paper paper, string? professorId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Paper(Id,ExternalId,Title,AuthorsJson,Year,Abstract,SourceUrl,LocalPath,ParseStatus)
            VALUES($id,$external,$title,$authors,$year,$abstract,$source,NULL,$status)
            ON CONFLICT(ExternalId) DO UPDATE SET Title=excluded.Title,AuthorsJson=excluded.AuthorsJson,
            Year=excluded.Year,Abstract=COALESCE(excluded.Abstract,Paper.Abstract) RETURNING Id
            """, ("$id", paper.Id), ("$external", paper.ExternalId), ("$title", paper.Title),
            ("$authors", ResearchStore.Serialize(paper.Authors)), ("$year", paper.Year), ("$abstract", paper.Abstract),
            ("$source", paper.SourceUrl), ("$status", paper.ParseStatus));
        command.Transaction = transaction;
        var id = (string)command.ExecuteScalar()!;
        if (professorId != null)
        {
            using var link = LocalDatabase.Command(connection, "INSERT OR IGNORE INTO ProfessorPaper VALUES($prof,$paper)", ("$prof", professorId), ("$paper", id));
            link.Transaction = transaction;
            link.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetPaper(id);
    }

    public IReadOnlyList<Paper> ListPapers(string? professorId = null)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT p.Id,p.ExternalId,p.Title,p.AuthorsJson,p.Year,p.Abstract,p.SourceUrl,p.LocalPath,p.ParseStatus
            FROM Paper p WHERE $prof IS NULL OR EXISTS(SELECT 1 FROM ProfessorPaper pp WHERE pp.PaperId=p.Id AND pp.ProfessorId=$prof)
            ORDER BY p.Year DESC,p.Title
            """, ("$prof", professorId));
        using var reader = command.ExecuteReader();
        var result = new List<Paper>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3))!, reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
        return result;
    }
    public Paper GetPaper(string id) => ListPapers().FirstOrDefault(paper => paper.Id == id) ?? throw new KeyNotFoundException("PAPER_NOT_FOUND");

    public async Task AttachPaperAsync(string id, string pdfUrl, CancellationToken cancellationToken)
    {
        _ = GetPaper(id);
        var bytes = await web.DownloadAsync(pdfUrl, 20_000_000, cancellationToken);
        if (!bytes.Bytes.AsSpan().StartsWith("%PDF-"u8)) throw new InvalidOperationException("PDF_REQUIRED");
        using (var pdf = PdfDocument.Open(bytes.Bytes)) { if (pdf.NumberOfPages == 0) throw new InvalidOperationException("EMPTY_PDF"); }
        var path = ManagedPath("papers", id + ".pdf");
        await File.WriteAllBytesAsync(path, bytes.Bytes, cancellationToken);
        using var connection = database.Open();
        using var update = LocalDatabase.Command(connection,
            "UPDATE Paper SET LocalPath=$path,ContentHash=$hash,ParseStatus='FullText',SourceUrl=$source WHERE Id=$id",
            ("$path", path), ("$hash", Convert.ToHexString(SHA256.HashData(bytes.Bytes))), ("$source", bytes.Url.AbsoluteUri), ("$id", id));
        update.ExecuteNonQuery();
    }

    public PaperReading ReadPaper(string id, int startPage, int maxPages)
    {
        if (startPage < 1 || maxPages is < 1 or > 10) throw new InvalidOperationException("INVALID_PAGE_RANGE");
        var paper = GetPaper(id);
        if (paper.LocalPath == null)
            return new(id, "AbstractOnly", paper.SourceUrl, paper.Abstract == null ? [] : [new(0, paper.Abstract)], "Full text unavailable; page 0 denotes abstract, not a PDF page.");
        EnsureManaged(paper.LocalPath, "papers");
        using var pdf = PdfDocument.Open(paper.LocalPath);
        if (startPage > pdf.NumberOfPages) throw new InvalidOperationException("PAGE_OUT_OF_RANGE");
        var pages = Enumerable.Range(startPage, Math.Min(maxPages, pdf.NumberOfPages - startPage + 1))
            .Select(page => new PaperPage(page, pdf.GetPage(page).Text)).ToArray();
        return new(id, "FullTextPages", paper.SourceUrl, pages,
            pages.All(page => string.IsNullOrWhiteSpace(page.Text)) ? "Text extraction empty; scanned PDFs require OCR, not implemented." : null);
    }

    public async Task<PaperReading> ReadPaperAsync(string id, int startPage, int maxPages, CancellationToken cancellationToken)
    {
        var paper = GetPaper(id);
        if (paper.LocalPath == null)
        {
            try
            {
                if (new Uri(paper.SourceUrl).AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || paper.SourceUrl.StartsWith("https://arxiv.org/pdf/", StringComparison.Ordinal))
                    await AttachPaperAsync(id, paper.SourceUrl, cancellationToken);
                else
                {
                    var page = await web.FetchAsync(paper.SourceUrl, cancellationToken);
                    SaveSource(page);
                    var sourceUri = new Uri(paper.SourceUrl);
                    var pdfUrl = sourceUri.Host == "arxiv.org" && sourceUri.AbsolutePath.StartsWith("/abs/", StringComparison.Ordinal)
                        ? "https://arxiv.org/pdf/" + sourceUri.AbsolutePath[5..]
                        : page.Links.FirstOrDefault(link => new Uri(link).AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://arxiv.org/pdf/", StringComparison.Ordinal));
                    if (pdfUrl != null) await AttachPaperAsync(id, pdfUrl, cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException)
            {
                return ReadPaper(id, startPage, maxPages) with { Note = "Full text could not be retrieved. Use only the available abstract; do not infer paper details." };
            }
        }
        return ReadPaper(id, startPage, maxPages);
    }

    public UserProfile ImportCv(string sourcePath)
    {
        if (new FileInfo(sourcePath).Length > 20_000_000) throw new InvalidOperationException("PDF_REQUIRED_MAX_20MB");
        var bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length > 20_000_000 || !bytes.AsSpan().StartsWith("%PDF-"u8)) throw new InvalidOperationException("PDF_REQUIRED_MAX_20MB");
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
        var id = Guid.NewGuid().ToString("N");
        var path = ManagedPath("cv", id + ".pdf");
        File.WriteAllBytes(path, bytes);
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO UserProfile(Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed,CreatedAt)
            SELECT $id,COALESCE(MAX(Version),0)+1,$path,$hash,$experiences,0,$now FROM UserProfile
            """, ("$id", id), ("$path", path), ("$hash", Convert.ToHexString(SHA256.HashData(bytes))),
            ("$experiences", ResearchStore.Serialize(new { extractedText = text, researchInterests = "", projects = "", skills = "" })),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
        return GetProfile(id)!;
    }

    public UserProfile? GetProfile(string? id = null, bool confirmedOnly = false)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed FROM UserProfile
            WHERE ($id IS NULL OR Id=$id) AND ($confirmed=0 OR Confirmed=1) ORDER BY Version DESC LIMIT 1
            """, ("$id", id), ("$confirmed", confirmedOnly ? 1 : 0));
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
    }

    public UserProfile ConfirmProfile(string id, string interests, string projects, string skills)
    {
        var previous = GetProfile(id) ?? throw new KeyNotFoundException("PROFILE_NOT_FOUND");
        VerifyCv(previous);
        if (string.IsNullOrWhiteSpace(interests + projects + skills)) throw new InvalidOperationException("EXPERIENCES_REQUIRED");
        var newId = Guid.NewGuid().ToString("N");
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO UserProfile(Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed,CreatedAt)
            SELECT $id,COALESCE(MAX(Version),0)+1,$path,$hash,$json,1,$now FROM UserProfile
            """, ("$id", newId), ("$path", previous.CvPath), ("$hash", previous.CvHash),
            ("$json", ResearchStore.Serialize(new { researchInterests = interests, projects, skills })), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
        return GetProfile(newId)!;
    }

    public string VerifyCv(UserProfile profile)
    {
        EnsureManaged(profile.CvPath, "cv");
        using var stream = File.OpenRead(profile.CvPath);
        if (Convert.ToHexString(SHA256.HashData(stream)) != profile.CvHash) throw new InvalidOperationException("CV_CHANGED_IMPORT_AGAIN");
        return profile.CvPath;
    }

    public void SaveSource(WebDocument document)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO SourceDocument VALUES($url,$title,$text,$at)
            ON CONFLICT(Url) DO UPDATE SET Title=excluded.Title,Text=excluded.Text,FetchedAt=excluded.FetchedAt
            """, ("$url", document.Url), ("$title", document.Title), ("$text", document.Text), ("$at", document.FetchedAt.ToString("O")));
        command.ExecuteNonQuery();
    }

    public string? SourceText(string url)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Text FROM SourceDocument WHERE Url=$url", ("$url", url));
        return command.ExecuteScalar() as string;
    }

    private string ManagedPath(string category, string filename)
    {
        var directory = Path.GetFullPath(Path.Combine(dataDirectory, category));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, filename);
    }
    private void EnsureManaged(string path, string category)
    {
        var directory = Path.GetFullPath(Path.Combine(dataDirectory, category)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("UNMANAGED_FILE_FORBIDDEN");
    }
}
