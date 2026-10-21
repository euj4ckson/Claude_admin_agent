using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    public TicketDetails? GetDetails(string id)
    {
        if (!TicketIdRegex().IsMatch(id)) return null;
        var ticketDirectory = Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant());
        var record = ReadJson<TicketRecord>(Path.Combine(ticketDirectory, "ticket.json"));
        if (record is null) return null;

        var stateDirectory = StateDirectory(record);
        var stateFile = Path.Combine(stateDirectory, record.Id.ToLowerInvariant(), "estado.json");
        var state = ReadElement(stateFile);
        var runtime = ReadJson<TicketRuntime>(Path.Combine(ticketDirectory, "runtime.json"));
        var summary = Summarize(ticketDirectory, record, checkDiff: true);

        var attachmentDirectory = Path.Combine(_referenceRoot, record.Id.ToLowerInvariant());
        var attachmentNames = Directory.Exists(attachmentDirectory)
            ? Directory.EnumerateFiles(attachmentDirectory).Select(Path.GetFileName).Where(name => name is not null).ToArray()
            : [];
        var missing = record.Attachments.Where(name => !attachmentNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var events = ReadEvents(Path.Combine(ticketDirectory, "events.jsonl"));
        return new TicketDetails(
            record.Id, record.Ticket, record.Sprint, record.Release, record.Scope, record.Title,
            record.Delivery, record.BranchType ?? "feature",
            record.Repository, record.RepositoryId ?? DefaultRepositoryId(), record.RepositoryStandard,
            record.IntegrationBranch, record.Agent, record.Components ?? [], record.CreatedAt,
            record.UpdatedAt, record.Prepared, record.SessionId, record.SessionIds ?? [],
            record.Branch, record.Worktree, record.BaseSha, summary.Stage,
            summary.StageLabel, state, summary.StateError,
            summary.Session, runtime, ReadElement(Path.Combine(ticketDirectory, "approval.json")),
            events, new AttachmentDetails(attachmentDirectory, Directory.Exists(attachmentDirectory),
                attachmentNames!, missing), summary.Risk, summary.Quality, summary.Metrics);
    }

    public static object Stats(IReadOnlyCollection<TicketSummary> all) => new
    {
        total = all.Count,
        active = all.Count(ticket => ticket.Stage != "concluido"),
        running = all.Count(ticket => ticket.Session is "running" or "uncertain"),
        attention = all.Count(ticket => ticket.Stage.Contains("aguardando", StringComparison.Ordinal) || ticket.Stage == "bloqueado" || ticket.StateError is not null),
        done = all.Count(ticket => ticket.Stage == "concluido")
    };

    private TicketSummary Summarize(string ticketDirectory, TicketRecord record, bool checkDiff = false)
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

        var stateNode = ReadNode(stateFile);
        var ticketNode = ReadNode(Path.Combine(ticketDirectory, "ticket.json")) ?? new JsonObject();
        var shouldCheckDiff = checkDiff && state?.Stage is "revisao" or "entrega" or "aguardando_validacao_manual" or "concluido" or "entregue";
        var qualityIssues = stateError is null && stateNode is JsonObject qualityState
            ? new HookPolicy(_dataRoot).CheckCommitQuality(qualityState, ticketNode as JsonObject ?? new JsonObject(), record.Id, shouldCheckDiff).ToList()
            : stateError is null ? ["Estado do ticket ausente"] : [stateError];
        var deliveryIssues = new List<string>();
        if (stage == "concluido")
        {
            var documentsRoot = ReadJson<ProfileRecord>(Path.Combine(_dataRoot, "config.json"))?.DocumentsRoot;
            var documentDirectory = string.IsNullOrWhiteSpace(documentsRoot) ? null : Path.Combine(documentsRoot, record.Id.ToLowerInvariant());
            var hasDocument = documentDirectory is not null && Directory.Exists(documentDirectory)
                && Directory.EnumerateFiles(documentDirectory, "*.docx").Any(file => !Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal));
            if (!hasDocument) deliveryIssues.Add("Documento final não registrado ou não encontrado");
        }
        var allQualityIssues = qualityIssues.Concat(deliveryIssues).Distinct(StringComparer.Ordinal).ToArray();
        var quality = new QualitySummary(qualityIssues.Count == 0, allQualityIssues.Length == 0, allQualityIssues,
            new QualityCounts(qualityIssues.Count(issue => !issue.Contains("evidência", StringComparison.OrdinalIgnoreCase)),
                qualityIssues.Count(issue => issue.Contains("evidência", StringComparison.OrdinalIgnoreCase)), deliveryIssues.Count));
        var risk = CalculateRisk(stage, stateNode, quality);
        var metrics = BuildMetrics(record.Id, stateNode, ticketDirectory, quality);

        return new TicketSummary(
            record.Id, record.Ticket, record.Sprint, record.Release, record.Title,
            record.Delivery, record.Agent, record.RepositoryId ?? DefaultRepositoryId(), record.Prepared,
            record.Branch, record.Worktree, record.UpdatedAt, stage,
            StageLabels.GetValueOrDefault(stage, "Cadastrado"), stateError, session,
            runtime is null ? null : new RuntimeSummary(runtime.Phase, runtime.Error, runtime.Heartbeat),
            risk, quality, metrics,
            new AttachmentSummary(missing, found.Length, Directory.Exists(attachmentDirectory), null));
    }

    private RiskSummary CalculateRisk(string stage, JsonNode? state, QualitySummary quality)
    {
        if (stage == "concluido") return new RiskSummary("none", "Histórico de entrega", []);
        if (stage is not ("revisao" or "entrega" or "aguardando_validacao_manual" or "bloqueado" or "aguardando_decisao_revisao"))
            return new RiskSummary("unknown", "Não avaliado", ["Avaliação disponível após testes e revisão."]);

        var tests = state?["tests"] as JsonArray ?? [];
        var latest = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in tests.OfType<JsonObject>())
        {
            if (test["superseded_by"] is not null || test["resolved"]?.GetValue<bool>() == true) continue;
            var key = GetString(test, "id") ?? GetString(test, "name") ?? $"test-{latest.Count}";
            latest[key] = test;
        }
        var failed = latest.Select(pair => (pair.Key, Result: FirstString(pair.Value, "result", "status", "resultado")))
            .Where(item => item.Result is not null && Regex.IsMatch(item.Result, "^(FALHA|FALHOU|FAILED|FAIL|ERRO|ERROR)(\\b|:)", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(item.Result, "AMBIENTE|DEPENDENCIA", RegexOptions.IgnoreCase)).ToArray();
        if (failed.Length > 0) return new RiskSummary("red", "Falha registrada", failed.Take(5).Select(item => $"{item.Key}: {item.Result}").ToArray());

        var review = state?["review"] as JsonObject;
        var findings = new[] { "findings", "suspected", "escaped_findings", "out_of_scope" }
            .SelectMany(key => review?[key] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(item => item["confirmed"]?.GetValue<bool>() == true && item["resolved"]?.GetValue<bool>() != true
                && Regex.IsMatch(FirstString(item, "status") ?? "", "^(OPEN|ABERTO|PENDENTE)$", RegexOptions.IgnoreCase)
                && Regex.IsMatch(FirstString(item, "severity") ?? "", "^(HIGH|CRITICAL|ALTA|CRITICA)$", RegexOptions.IgnoreCase)).ToArray();
        if (findings.Length > 0) return new RiskSummary("red", "Regressão confirmada", findings.Take(5).Select(item => FirstString(item, "title", "description", "id") ?? "Achado de revisão").ToArray());
        if (quality.Issues.Length > 0) return new RiskSummary("yellow", "Validação pendente", quality.Issues.Take(5).ToArray());
        if (quality.DeliveryReady && latest.Count > 0 && review is not null) return new RiskSummary("green", "Validação registrada", ["Testes e revisão registrados com as evidências exigidas."]);
        return new RiskSummary("unknown", "Não avaliado", ["Não há resultados suficientes para classificar."]);
    }

    private object BuildMetrics(string id, JsonNode? state, string ticketDirectory, QualitySummary quality)
    {
        var raw = ReadNode(Path.Combine(ticketDirectory, "metrics.json")) as JsonObject ?? new JsonObject();
        var totals = raw["totals"] as JsonObject ?? new JsonObject();
        var phases = raw["phases"] as JsonObject ?? new JsonObject();
        var tests = state?["tests"] as JsonArray ?? [];
        var notExecuted = tests.OfType<JsonObject>().Count(test => Regex.IsMatch(FirstString(test, "result", "status") ?? "", "não executado|nao executado|bloqueado|pendente", RegexOptions.IgnoreCase));
        var review = state?["review"] as JsonObject;
        var findingCount = new[] { "suspected", "findings", "escaped_findings", "out_of_scope" }.Sum(key => (review?[key] as JsonArray)?.Count ?? 0);
        var blockedSeconds = phases["bloqueado"]?["seconds"]?.GetValue<double>() ?? 0;
        var reviews = (raw["reviews"] as JsonArray)?.Count ?? 0;
        reviews = Math.Max(reviews, ReadEvents(Path.Combine(ticketDirectory, "events.jsonl")).Count(item => Regex.IsMatch($"{item.Type} {item.Message}", "review|revis|correção|correcao", RegexOptions.IgnoreCase)));
        if (review is not null) reviews = Math.Max(reviews, 1);
        var revisions = Math.Max(0, (state?["scope_revision"]?.GetValue<int>() ?? 1) - 1);
        return new
        {
            ticket = id,
            tokens = new
            {
                estimated_input = Number(totals, "estimated_input_tokens"), estimated_output = Number(totals, "estimated_output_tokens"),
                reported_input = Number(totals, "reported_input_tokens"), reported_output = Number(totals, "reported_output_tokens"),
                estimated_total = Number(totals, "estimated_input_tokens") + Number(totals, "estimated_output_tokens"), by_phase = phases
            },
            scope_expansions = Math.Max((raw["scope_expansions"] as JsonArray)?.Count ?? 0, revisions),
            reviews, blocked_seconds = blockedSeconds, tests_not_executed = notExecuted,
            tests_not_executed_rate = tests.Count == 0 ? 0 : (double)notExecuted / tests.Count,
            test_count = tests.Count, escaped_findings = findingCount,
            quality = new { commit_ready = quality.CommitReady, delivery_ready = quality.DeliveryReady }
        };
    }

    private static string? FirstString(JsonObject? value, params string[] keys)
    {
        foreach (var key in keys) if (GetString(value, key) is { Length: > 0 } result) return result;
        return null;
    }

    private static string? GetString(JsonObject? value, string key) => value?[key] is JsonValue item && item.TryGetValue<string>(out var result) ? result : null;

    private static double Number(JsonObject? value, string key) => value?[key] is JsonValue number && number.TryGetValue<double>(out var result) ? result : 0;

    private static JsonNode? ReadNode(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
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

    private static JsonElement? ReadElement(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static IReadOnlyList<TicketEvent> ReadEvents(string file)
    {
        try
        {
            return File.ReadLines(file).TakeLast(60).Select(line =>
            {
                try { return JsonSerializer.Deserialize<TicketEvent>(line, JsonOptions); }
                catch (JsonException) { return null; }
            }).Where(item => item is not null).Select(item => item!).Reverse().ToArray();
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (IOException) { return []; }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z]{2,8}\\d{2,8}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TicketIdRegex();

    private sealed record ProfileRecord(string? Repository, [property: JsonPropertyName("documents_root")] string? DocumentsRoot);
    private sealed record TicketRecord(
        string Id = "", string Ticket = "", string Sprint = "", string Release = "", string Scope = "", string Title = "",
        string Delivery = "", string Agent = "claude", string? RepositoryId = null, string Repository = "",
        bool Prepared = false, string? Branch = null, string? Worktree = null, string? UpdatedAt = null,
        [property: JsonPropertyName("attachments")] string[]? AttachmentsValue = null,
        DeliveryDecision? DeliveryDecision = null,
        [property: JsonPropertyName("branch_type")] string? BranchType = null,
        [property: JsonPropertyName("repository_standard")] JsonElement? RepositoryStandard = null,
        [property: JsonPropertyName("integration_branch")] string? IntegrationBranch = null,
        [property: JsonPropertyName("createdAt")] string? CreatedAt = null,
        string? SessionId = null,
        string[]? SessionIds = null,
        string? BaseSha = null,
        JsonElement[]? Components = null)
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
public sealed record TicketEvent(string? At, string? Type, string? Message);
public sealed record AttachmentDetails(string Directory, bool Exists, string[] Names, string[] Missing);
public sealed record TicketDetails(
    string Id, string Ticket, string Sprint, string Release, string Scope, string Title,
    string Delivery, string BranchType, string Repository, string? RepositoryId,
    JsonElement? RepositoryStandard, string? IntegrationBranch, string Agent, JsonElement[] Components,
    string? CreatedAt, string? UpdatedAt, bool Prepared, string? SessionId, string[] SessionIds,
    string? Branch, string? Worktree, string? BaseSha, string Stage, string StageLabel,
    JsonElement? State, string? StateError, string Session, object? Runtime, JsonElement? Approval,
    IReadOnlyList<TicketEvent> Events,
    [property: JsonPropertyName("attachments")] AttachmentDetails AttachmentInfo,
    RiskSummary Risk, QualitySummary Quality, object? Metrics);
