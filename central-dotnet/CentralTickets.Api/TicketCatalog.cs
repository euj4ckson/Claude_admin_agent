using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CentralTickets.Api;

public sealed partial class TicketCatalog
{
    private readonly string _dataRoot;
    private readonly string _stateBaseRoot;
    private readonly string _referenceRoot;
    private readonly string? _configuredRepository;

    public TicketCatalog(string dataRoot, string stateBaseRoot, string referenceRoot)
    {
        _dataRoot = dataRoot;
        _stateBaseRoot = stateBaseRoot;
        _referenceRoot = referenceRoot;
        _configuredRepository = ReadJson<ProfileRecord>(Path.Combine(dataRoot, "config.json"))?.Repository;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly IReadOnlyDictionary<string, string> StageLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cadastrado"] = "Cadastrado",
        ["preparando"] = "Preparando trabalho",
        ["analise"] = "Em análise",
        ["aguardando_aprovacao"] = "Aguardando aprovação",
        ["implementacao"] = "Implementando",
        ["compilacao"] = "Compilando",
        ["testes"] = "Testando",
        ["revisao"] = "Em revisão",
        ["aguardando_decisao_revisao"] = "Decisão de revisão",
        ["aguardando_validacao_manual"] = "Validação manual",
        ["bloqueado"] = "Precisa de atenção",
        ["entrega"] = "Preparando entrega",
        ["concluido"] = "Entregue localmente"
    };

    public IReadOnlyList<TicketSummary> List(string stage, string? query, string? repository, string? agent, int limit)
    {
        var root = Path.Combine(_dataRoot, "tickets");
        if (!Directory.Exists(root)) return [];

        var normalizedQuery = query?.Trim() ?? "";
        limit = Math.Clamp(limit, 1, 500);
        var results = new List<TicketSummary>();

        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(path => TicketIdRegex().IsMatch(Path.GetFileName(path)))
                     .Select(path => new { Path = path, Record = ReadJson<TicketRecord>(Path.Combine(path, "ticket.json")) })
                     .Where(item => item.Record is not null)
                     .OrderByDescending(item => item.Record!.UpdatedAt, StringComparer.Ordinal))
        {
            var record = directory.Record!;
            if (!string.IsNullOrWhiteSpace(repository) && !string.Equals(record.RepositoryId, repository, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(agent) && !string.Equals(record.Agent, agent, StringComparison.OrdinalIgnoreCase)) continue;
            if (normalizedQuery.Length > 0 && !string.Join(' ', record.Id, record.Title, record.Release).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) continue;

            var summary = Summarize(directory.Path, record);
            if (stage == "active" && summary.Stage == "concluido") continue;
            if (stage == "done" && summary.Stage != "concluido") continue;
            results.Add(summary);
            if (results.Count == limit) break;
        }

        return results;
    }

    public static object Stats(IReadOnlyCollection<TicketSummary> all) => new
    {
        total = all.Count,
        active = all.Count(ticket => ticket.Stage != "concluido"),
        running = all.Count(ticket => ticket.Session is "running" or "uncertain"),
        attention = all.Count(ticket => ticket.Stage.Contains("aguardando", StringComparison.Ordinal) || ticket.Stage == "bloqueado" || ticket.StateError is not null),
        done = all.Count(ticket => ticket.Stage == "concluido")
    };

    private TicketSummary Summarize(string ticketDirectory, TicketRecord record)
    {
        var stateDirectory = StateDirectory(record);
        var stateFile = Path.Combine(stateDirectory, record.Id.ToLowerInvariant(), "estado.json");
        var state = ReadJson<TicketState>(stateFile);
        var runtime = ReadJson<TicketRuntime>(Path.Combine(ticketDirectory, "runtime.json"));
        var stage = state?.Stage == "entregue" ? "concluido" : state?.Stage;
        var deliveryDecision = record.DeliveryDecision;
        var userClosedThisRevision = deliveryDecision?.RequestedBy == "usuario"
            && deliveryDecision.ScopeRevision == (state?.ScopeRevision ?? 1)
            && !string.IsNullOrWhiteSpace(deliveryDecision.At);
        if (stage == "concluido" && !userClosedThisRevision) stage = "bloqueado";
        if (string.IsNullOrWhiteSpace(stage) || !StageLabels.ContainsKey(stage)) stage = record.Prepared ? "analise" : "cadastrado";

        var session = GetSession(runtime);
        var attachmentDirectory = Path.Combine(_referenceRoot, record.Id.ToLowerInvariant());
        var found = Directory.Exists(attachmentDirectory)
            ? Directory.EnumerateFiles(attachmentDirectory).Select(Path.GetFileName).Where(name => name is not null).ToArray()
            : [];
        var missing = record.Attachments.Where(name => !found.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var stateError = File.Exists(stateFile) && state is null ? "Estado do ticket inválido" : null;
        if (stateError is not null) stage = "bloqueado";

        return new TicketSummary(
            record.Id, record.Ticket, record.Sprint, record.Release, record.Title,
            record.Delivery, record.Agent, record.RepositoryId ?? DefaultRepositoryId(), record.Prepared,
            record.Branch, record.Worktree, record.UpdatedAt, stage,
            StageLabels.GetValueOrDefault(stage, "Cadastrado"), stateError, session,
            runtime is null ? null : new RuntimeSummary(runtime.Phase, runtime.Error, runtime.Heartbeat),
            new RiskSummary("unknown", "Não avaliado", []),
            new QualitySummary(false, false, stateError is null ? [] : [stateError], new QualityCounts(stateError is null ? 0 : 1, 0, 0)),
            null, new AttachmentSummary(missing, found.Length, Directory.Exists(attachmentDirectory), null));
    }

    private string StateDirectory(TicketRecord record)
    {
        var configured = _configuredRepository;
        var configuredId = configured is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(configured));
        var repositoryPath = string.IsNullOrWhiteSpace(record.Repository) ? configured : record.Repository;
        var repositoryName = repositoryPath is null ? record.RepositoryId : Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryPath));
        var folderName = string.Equals(configuredId, repositoryName, StringComparison.OrdinalIgnoreCase)
            ? configuredId
            : record.RepositoryId ?? repositoryName;
        var safeFolder = string.Concat((folderName ?? "").Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        return Path.Combine(_stateBaseRoot, safeFolder);
    }

    private string? DefaultRepositoryId()
    {
        var configured = _configuredRepository;
        return configured is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(configured));
    }

    private static string GetSession(TicketRuntime? runtime)
    {
        if (runtime is null) return "none";
        if (runtime.Phase is not ("starting" or "running")) return "stopped";
        var alive = IsAlive(runtime.Pid) || IsAlive(runtime.ChildPid);
        if (!alive) return "stopped";
        var heartbeat = DateTimeOffset.TryParse(runtime.Heartbeat ?? runtime.StartedAt, out var at) ? at : DateTimeOffset.MinValue;
        return DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(25) ? "uncertain" : "running";
    }

    private static bool IsAlive(int? pid)
    {
        if (pid is null or <= 0) return false;
        try { using var process = Process.GetProcessById(pid.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static T? ReadJson<T>(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z]{2,8}\\d{2,8}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TicketIdRegex();

    private sealed record ProfileRecord(string? Repository);
    private sealed record TicketRecord(
        string Id = "", string Ticket = "", string Sprint = "", string Release = "", string Title = "",
        string Delivery = "", string Agent = "claude", string? RepositoryId = null, string Repository = "",
        bool Prepared = false, string? Branch = null, string? Worktree = null, string? UpdatedAt = null,
        [property: JsonPropertyName("attachments")] string[]? AttachmentsValue = null,
        DeliveryDecision? DeliveryDecision = null)
    {
        [JsonIgnore] public string[] Attachments => AttachmentsValue ?? [];
    }
    private sealed record TicketState(string? Stage, [property: JsonPropertyName("scope_revision")] int? ScopeRevision);
    private sealed record DeliveryDecision(string? RequestedBy, int? ScopeRevision, string? At);
    private sealed record TicketRuntime(string? Phase, int? Pid, int? ChildPid, string? Heartbeat, string? StartedAt, string? Error);
}

public sealed record TicketSummary(
    string Id, string Ticket, string Sprint, string Release, string Title, string Delivery,
    string Agent, string? RepositoryId, bool Prepared, string? Branch, string? Worktree,
    string? UpdatedAt, string Stage, string StageLabel, string? StateError, string Session,
    RuntimeSummary? Runtime, RiskSummary Risk, QualitySummary Quality, object? Metrics,
    AttachmentSummary Attachments);

public sealed record RuntimeSummary(string? Phase, string? Error, string? Heartbeat);
public sealed record RiskSummary(string Level, string Label, string[] Reasons);
public sealed record QualitySummary(bool CommitReady, bool DeliveryReady, string[] Issues, QualityCounts Counts);
public sealed record QualityCounts(int Structural, int Evidence, int Delivery);
public sealed record AttachmentSummary(string[] Missing, int Count, bool Exists, string? Error);
