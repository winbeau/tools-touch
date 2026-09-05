using System.IO;
using System.Net.Http;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Collection;
using ToolsTouch.Infrastructure.Legacy;
using ToolsTouch.Infrastructure.Persistence;
using ToolsTouch.Infrastructure.Research;
using ToolsTouch.Infrastructure.Recommendation;
using ToolsTouch.Infrastructure.Tracking;

namespace ToolsTouch.Desktop;

// The desktop project is the composition root until application ports replace the legacy services.
public sealed class AppServices : IDisposable
{
    private readonly HttpClient http;

    public LocalDatabase Database { get; }
    public ResearchStore Store { get; }
    public LibraryService Library { get; }
    public OutreachService Outreach { get; }
    public IGmailAccountPort GmailAuth { get; }
    public GmailService Gmail { get; }
    public PublicWeb Web { get; }
    public DiagnosticLog Diagnostics { get; }
    public OrganizationService Organizations { get; }
    public IWorkspaceMetadata Workspace { get; }
    public ArtifactStore Artifacts { get; }
    public SendCoordinator SendCoordinator { get; }
    public SourceRepository Sources { get; }
    public AdmissionImportService Admissions { get; }
    public AdmissionQueryService AdmissionQueries { get; }
    public OfficialFacultyDirectoryAdapter FacultyDirectoryAdapter { get; }
    public FacultyDirectoryCrawler FacultyCrawler { get; }
    public FacultyIdentityImportService FacultyIdentityImporter { get; }
    public FacultyResearchService FacultyResearch { get; }
    public FacultyQueryService FacultyQueries { get; }
    public ProfileFactsService ProfileFacts { get; }
    public PreferenceService Preferences { get; }
    public StatisticsBuilder Statistics { get; }
    public EligibilityEvaluator Eligibility { get; }
    public Ranker Ranker { get; }
    public RecommendationRepository Recommendations { get; }
    public SemanticEvaluationService SemanticEvaluation { get; }
    public DraftService Drafts { get; }
    public BackupService Backup { get; }
    public JobRepository JobRepository { get; }
    public JobScheduler Jobs { get; }
    public ICollectorBridge Collector { get; }
    public ApplicationCaseService ApplicationCases { get; }
    public SystemCollectionAdapter SystemCollections { get; }
    public RecordWorkspaceService RecordWorkspace { get; }
    public FieldSchemaService FieldSchemas { get; }
    public RecordQueryService RecordQueries { get; }
    public ViewService Views { get; }
    public WorkspaceExportService WorkspaceExport { get; }
    public ImportPreviewService Imports { get; }

    public AppServices(string dataDirectory, HttpClient http, Func<GoogleClient> clientConfig, string pythonPath = "python")
    {
        this.http = http;
        Diagnostics = new DiagnosticLog(dataDirectory);
        Database = new(Path.Combine(dataDirectory, "tools-touch.db"));
        Database.Initialize();
        Web = new PublicWeb();
        Store = new ResearchStore(Database);
        Library = new LibraryService(Database, dataDirectory, Web);
        Outreach = new OutreachService(Database);
        Drafts = new DraftService(Outreach);
        var organizationRepository = new OrganizationRepository(Database);
        Organizations = new OrganizationService(organizationRepository, organizationRepository);
        Workspace = organizationRepository;
        Artifacts = new ArtifactStore(Database.ArtifactDirectory);
        SendCoordinator = new SendCoordinator(Database, Outreach, Artifacts);
        Sources = new SourceRepository(Database, Artifacts);
        Admissions = new AdmissionImportService(Database, Artifacts);
        AdmissionQueries = new AdmissionQueryService(Database);
        FacultyDirectoryAdapter = new OfficialFacultyDirectoryAdapter(Web);
        FacultyCrawler = new FacultyDirectoryCrawler(Database, Artifacts, Sources);
        FacultyIdentityImporter = new FacultyIdentityImportService(Database);
        FacultyResearch = new FacultyResearchService(Database, Artifacts, Sources);
        FacultyQueries = new FacultyQueryService(Database);
        ProfileFacts = new ProfileFactsService(Database);
        Preferences = new PreferenceService(Database);
        Statistics = new StatisticsBuilder(Database);
        Eligibility = new EligibilityEvaluator();
        Ranker = new Ranker();
        Recommendations = new RecommendationRepository(Database, Artifacts);
        SemanticEvaluation = new SemanticEvaluationService();
        Backup = new BackupService(Database);
        JobRepository = new JobRepository(Database);
        Jobs = new JobScheduler(JobRepository);
        Jobs.RecoverStale();
        Collector = new CollectorBridge(pythonPath, dataDirectory);
        GmailAuth = new GmailAuth(http, new WindowsSecretStore(dataDirectory), clientConfig);
        Gmail = new GmailService(http, GmailAuth);
        ApplicationCases = new ApplicationCaseService(Database);
        SystemCollections = new SystemCollectionAdapter(Database);
        SystemCollections.EnsureSystemCatalog();
        RecordWorkspace = new RecordWorkspaceService(Database);
        FieldSchemas = new FieldSchemaService(RecordWorkspace);
        Views = new ViewService(Database);
        RecordQueries = new RecordQueryService(Database);
        WorkspaceExport = new WorkspaceExportService(Database, PythonPathOrDefault(pythonPath));
        Imports = new ImportPreviewService(Database, PythonPathOrDefault(pythonPath));
    }

    public void Dispose()
    {
        Web.Dispose();
        http.Dispose();
    }

    private static string PythonPathOrDefault(string value) => string.IsNullOrWhiteSpace(value) ? "python" : value;
}
