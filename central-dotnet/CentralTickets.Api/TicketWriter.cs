using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

public sealed class TicketWriter(string dataRoot, string stateRoot, CentralProfile profile)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dataRoot = dataRoot;
    private readonly string _stateRoot = stateRoot;
    private readonly CentralProfile _profile = profile;

    public JsonElement Create(CreateTicketRequest input)
    {
        var id = ValidateId(input.Ticket);
        var sprint = ValidateSprint(input.Sprint);
        var scope = (input.Scope ?? "").Trim();
        if (scope.Length is < 20 or > 100_000) throw new TicketOperationException("O escopo deve ter entre 20 e 100.000 caracteres.");
        var delivery = ValidateDelivery(input.Delivery);
        var branchType = ValidateBranchType(input.BranchType ?? "feature");
        var agent = ValidateAgent(input.Agent ?? "claude");
        var repository = ResolveRepository(input.Repository ?? input.RepositoryId ?? "");
        var release = ValidateRelease(repository.Standard?.Base ?? input.Release ?? "main");
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0) title = scope.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? id;
        title = title[..Math.Min(160, title.Length)];
        var attachments = ValidateAttachments(input.Attachments ?? []);
        var components = ResolveComponents(input.Components, repository);

        var ticketDirectory = TicketDirectory(id);
        var stateDirectory = StateDirectory(repository.Id, id);
        if (File.Exists(Path.Combine(ticketDirectory, "ticket.json")) || File.Exists(Path.Combine(stateDirectory, "estado.json")))
            throw new TicketOperationException("Este ticket já possui cadastro ou estado do coordenador. O trabalho existente foi preservado.", 409);

        Directory.CreateDirectory(ticketDirectory);
        using var creationLock = AcquireLock(Path.Combine(ticketDirectory, ".write.lock"));
        if (File.Exists(Path.Combine(ticketDirectory, "ticket.json")) || File.Exists(Path.Combine(stateDirectory, "estado.json")))
            throw new TicketOperationException("Este ticket já possui cadastro ou estado do coordenador. O trabalho existente foi preservado.", 409);

        var now = DateTimeOffset.UtcNow.ToString("O");
        var sessionId = Guid.NewGuid().ToString();
        var record = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ticket"] = id,
            ["sprint"] = sprint,
            ["release"] = release,
            ["scope"] = scope,
            ["title"] = title,
            ["delivery"] = delivery,
            ["branch_type"] = branchType,
            ["agent"] = agent,
            ["repository"] = repository.Path,
            ["repositoryId"] = repository.Id,
            ["repository_standard"] = repository.Standard,
            ["integration_branch"] = repository.Standard?.Integration,
            ["attachments"] = attachments,
            ["components"] = components,
            ["azure_source"] = input.AzureSource,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["prepared"] = false,
            ["sessionId"] = sessionId,
            ["sessionIds"] = new[] { sessionId }
        };
        WriteJson(Path.Combine(ticketDirectory, "ticket.json"), record);
        AppendEvent(id, "cadastro", "Ticket cadastrado. Nenhuma sessão ou alteração de código foi iniciada.");
        WriteJson(Path.Combine(_dataRoot, "preferences.json"), new { sprint, release });
        return JsonSerializer.SerializeToElement(record, JsonOptions);
    }

    public JsonElement? UpdateRegistration(string id, RegistrationUpdate input)
    {
        id = ValidateId(id);
        var ticketDirectory = TicketDirectory(id);
        var ticketFile = Path.Combine(ticketDirectory, "ticket.json");
        var current = ReadObject(ticketFile) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        using var writeLock = AcquireLock(Path.Combine(ticketDirectory, ".write.lock"));

        var repository = ResolveRepository(input.Repository ?? GetString(current, "repositoryId") ?? GetString(current, "repository") ?? "");
        var agent = ValidateAgent(input.Agent ?? GetString(current, "agent") ?? "claude");
        var delivery = ValidateDelivery(input.Delivery ?? GetString(current, "delivery") ?? "");
        var sprint = ValidateSprint(input.Sprint ?? GetString(current, "sprint") ?? "");
        var release = ValidateRelease(repository.Standard?.Base ?? input.Release ?? GetString(current, "release") ?? "");
        var branchType = ValidateBranchType(input.BranchType ?? GetString(current, "branch_type") ?? "feature");

        var runtime = ReadObject(Path.Combine(ticketDirectory, "runtime.json"));
        var processActive = IsAlive(GetInt(runtime, "pid")) || IsAlive(GetInt(runtime, "childPid"));
        var phase = GetString(runtime, "phase");
        var running = processActive && phase is "starting" or "running";
        var prepared = GetBool(current, "prepared") || GetString(current, "branch") is not null || GetString(current, "worktree") is not null;
        var coreFieldsChanged = !Equal(GetString(current, "repositoryId") ?? GetString(current, "repository"), repository.Id)
            || !Equal(GetString(current, "sprint"), sprint)
            || !Equal(GetString(current, "release"), release)
            || !Equal(GetString(current, "branch_type"), branchType)
            || !Equal(GetString(current, "agent"), agent);

        if (running && coreFieldsChanged)
            throw new TicketOperationException("Enquanto a conversa estiver aberta, só é seguro alterar a forma de entrega. Feche-a antes de trocar agente, origem ou repositório.", 409);
        if (prepared && coreFieldsChanged)
            throw new TicketOperationException("O ticket já tem worktree/branch. Repositório, sprint, origem, branch e agente não podem ser trocados para não desencontrar o trabalho existente.", 409);

        current["sprint"] = sprint;
        current["release"] = release;
        current["branch_type"] = branchType;
        current["delivery"] = delivery;
        current["agent"] = agent;
        current["repository"] = repository.Path;
        current["repositoryId"] = repository.Id;
        current["repository_standard"] = JsonSerializer.SerializeToNode(repository.Standard, JsonOptions);
        current["integration_branch"] = repository.Standard?.Integration;
        current["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        WriteJson(ticketFile, current);

        var stateFile = Path.Combine(StateDirectory(repository.Id, id), "estado.json");
        var state = ReadObject(stateFile);
        if (state is not null)
        {
            var deliveryState = state["delivery"] as JsonObject ?? new JsonObject();
            deliveryState["commit_allowed"] = delivery == "local_commit_only";
            deliveryState["push_allowed"] = false;
            deliveryState["pr_allowed"] = false;
            state["delivery"] = deliveryState;
            state["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
            WriteJson(stateFile, state);
        }

        AppendEvent(id, "cadastro_atualizado", $"Cadastro atualizado: agente {agent}; entrega {(delivery == "no_commit" ? "sem commit" : "commit local")}.");
        using var updated = JsonDocument.Parse(File.ReadAllText(ticketFile));
        return updated.RootElement.Clone();
    }

    public JsonElement AddAttachments(string id, IEnumerable<string> names)
    {
        id = ValidateId(id);
        var directory = TicketDirectory(id);
        var file = Path.Combine(directory, "ticket.json");
        using var writeLock = AcquireLock(Path.Combine(directory, ".write.lock"));
        var current = ReadObject(file) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        var existing = current["attachments"] is JsonArray values
            ? values.OfType<JsonValue>().Where(value => value.TryGetValue<string>(out _)).Select(value => value.GetValue<string>()).ToArray()
            : [];
        current["attachments"] = new JsonArray(ValidateAttachments(existing.Concat(names).ToArray()).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        current["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        WriteJson(file, current);
        AppendEvent(id, "anexos", $"{names.Count()} anexo(s) associado(s) ao ticket.");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.Clone();
    }

    public object CompleteByUser(string id)
    {
        id = ValidateId(id);
        var ticketDirectory = TicketDirectory(id);
        using var writeLock = AcquireLock(Path.Combine(ticketDirectory, ".write.lock"));
        var ticketFile = Path.Combine(ticketDirectory, "ticket.json");
        var ticket = ReadObject(ticketFile) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        var stateFile = Path.Combine(StateDirectory(GetString(ticket, "repositoryId") ?? Path.GetFileName(GetString(ticket, "repository") ?? ""), id), "estado.json");
        var state = ReadObject(stateFile) ?? throw new TicketOperationException("O ticket ainda não possui estado de desenvolvimento.", 409);
        var at = DateTimeOffset.UtcNow.ToString("O");
        var revision = GetInt(state, "scope_revision") ?? 1;
        var oldDecision = ticket["deliveryDecision"] as JsonObject;
        if (GetString(state, "stage") is "concluido" or "entregue"
            && GetInt(oldDecision, "scopeRevision") == revision
            && GetString(oldDecision, "requestedBy") == "usuario")
            return new { id, stage = "concluido", alreadyCompleted = true };

        var decision = new JsonObject
        {
            ["at"] = at,
            ["requestedBy"] = "usuario",
            ["scopeRevision"] = revision,
            ["previousStage"] = GetString(state, "stage"),
            ["reason"] = "Encerramento manual solicitado pelo usuário; evidências e pendências preservadas."
        };
        var historyDirectory = Path.Combine(ticketDirectory, "delivery-history");
        Directory.CreateDirectory(historyDirectory);
        WriteJson(Path.Combine(historyDirectory, $"{Guid.NewGuid():N}.json"), new { ticket, state, decision });
        state["stage"] = "concluido";
        var delivered = state["delivered"] as JsonObject ?? new JsonObject();
        delivered["at"] ??= at;
        delivered["user_decision"] = decision.DeepClone();
        state["delivered"] = delivered;
        state["updated_at"] = at;
        WriteJson(stateFile, state);
        ticket["deliveryDecision"] = decision;
        ticket["updatedAt"] = at;
        WriteJson(ticketFile, ticket);
        AppendEvent(id, "entrega_manual", "Encerramento manual solicitado pelo usuário; evidências e pendências preservadas.");
        return new { id, stage = "concluido", alreadyCompleted = false };
    }

    private CentralRepository ResolveRepository(string value) => _profile.ResolveRepository(value)
        ?? throw new TicketOperationException("Selecione um repositório Git válido dentro da pasta configurada.");

    private List<object> ResolveComponents(JsonElement? input, CentralRepository primary)
    {
        if (input is null || input.Value.ValueKind == JsonValueKind.Null) return [];
        JsonElement value = input.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            try { value = JsonDocument.Parse(value.GetString() ?? "[]").RootElement.Clone(); }
            catch (JsonException) { throw new TicketOperationException("Componentes inválidos."); }
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2)
            throw new TicketOperationException("Selecione no máximo dois repositórios complementares.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary.Id };
        var result = new List<object>();
        foreach (var component in value.EnumerateArray())
        {
            var id = ElementString(component, "repositoryId") ?? ElementString(component, "repository") ?? "";
            var repository = ResolveRepository(id);
            if (!(repository.Id.Equals("ss_fva", StringComparison.OrdinalIgnoreCase)
                || repository.Id.Equals("ss_wcf_fvan", StringComparison.OrdinalIgnoreCase)
                || repository.Id.Equals(Path.GetFileName(_profile.GetString("repository")), StringComparison.OrdinalIgnoreCase))
                || !seen.Add(repository.Id))
                throw new TicketOperationException("Componente repetido ou fora do ecossistema FVA/WCF/ERP.");
            var release = ValidateRelease(repository.Standard?.Base ?? ElementString(component, "release") ?? "");
            result.Add(new
            {
                repository = repository.Path,
                repositoryId = repository.Id,
                release,
                integration_branch = repository.Standard?.Integration,
                repository_standard = repository.Standard
            });
        }
        return result;
    }

    private string StateDirectory(string repositoryId, string id)
    {
        var configured = _profile.GetString("repository");
        var configuredId = configured is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(configured));
        var folder = string.Equals(configuredId, repositoryId, StringComparison.OrdinalIgnoreCase) ? configuredId : repositoryId;
        var safe = string.Concat((folder ?? "").Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        return Path.Combine(_stateRoot, safe, id.ToLowerInvariant());
    }

    private string TicketDirectory(string id) => Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant());

    private void AppendEvent(string id, string type, string message)
    {
        var file = Path.Combine(TicketDirectory(id), "events.jsonl");
        var record = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }, JsonOptions);
        File.AppendAllText(file, record + Environment.NewLine);
    }

    private static string ValidateId(string? value)
    {
        var id = (value ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(id, "^[A-Z]{2,8}[0-9]{2,8}$", RegexOptions.CultureInvariant))
            throw new TicketOperationException("Informe o ticket com prefixo e número, por exemplo GER5800.");
        return id;
    }

    private static string ValidateSprint(string? value)
    {
        var sprint = (value ?? "").Trim();
        if (!Regex.IsMatch(sprint, "^[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant))
            throw new TicketOperationException("Informe uma sprint numérica válida.");
        return sprint;
    }

    private static string ValidateRelease(string? value)
    {
        var release = (value ?? "").Trim();
        if (!Regex.IsMatch(release, "^(?:main|pre_main|release/[a-zA-Z0-9][a-zA-Z0-9._/-]{0,90})$", RegexOptions.CultureInvariant)
            || release.Contains("..", StringComparison.Ordinal) || release.Contains("//", StringComparison.Ordinal)
            || release.EndsWith('/') || release.EndsWith('.') || release.Contains("/.", StringComparison.Ordinal)
            || release.Contains(".lock/", StringComparison.OrdinalIgnoreCase))
            throw new TicketOperationException("Informe uma origem válida: release/106.4.3, main ou pre_main.");
        return release;
    }

    private string ValidateAgent(string value)
    {
        var agent = value.Trim().ToLowerInvariant();
        if (agent is not ("claude" or "codex")) throw new TicketOperationException("Escolha Claude Code ou Codex CLI.");
        var executable = _profile.AgentExecutable(agent);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new TicketOperationException($"{(agent == "codex" ? "Codex CLI" : "Claude Code")} não está instalado nesta máquina.", 409);
        return agent;
    }

    private static string ValidateDelivery(string? value) => value switch
    {
        "local_commit_only" => value,
        "no_commit" => value,
        _ => throw new TicketOperationException("Escolha commit local ou deixar as alterações sem commit.")
    };

    private static string ValidateBranchType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "feature" => "feature",
        "hotfix" => "hotfix",
        _ => throw new TicketOperationException("Escolha branch feature ou hotfix.")
    };

    private static string[] ValidateAttachments(string[] values)
    {
        if (values.Length > 40) throw new TicketOperationException("Liste no máximo 40 anexos.");
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value is "." or ".."
                || value.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))
                || value.EndsWith('.') || value.EndsWith(' '))
                throw new TicketOperationException("Nome de anexo inválido. Escolha um arquivo com nome simples, sem caminho.");
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private TicketFileLock AcquireLock(string path)
    {
        try { return new TicketFileLock(path); }
        catch (IOException) { throw new TicketOperationException("Outra ação está atualizando este ticket. Aguarde e tente novamente.", 409); }
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteJson(string path, JsonNode value) => WriteJson(path, (object)value);

    private static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new TicketOperationException($"Não foi possível ler {Path.GetFileName(path)}. O arquivo foi preservado.", 409); }
    }

    private static string? GetString(JsonObject? value, string key) => value?[key]?.GetValue<string>();
    private static int? GetInt(JsonObject? value, string key) => value?[key]?.GetValue<int>();
    private static bool GetBool(JsonObject? value, string key) => value?[key]?.GetValue<bool>() ?? false;
    private static bool Equal(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsAlive(int? pid)
    {
        if (pid is null or <= 0) return false;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static string? ElementString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class TicketOperationException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed class TicketFileLock : IDisposable
{
    private readonly FileStream _stream;

    public TicketFileLock(string path)
    {
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

public sealed record CreateTicketRequest(
    string? Ticket, string? Sprint, string? Release, string? Scope, string? Title,
    string? Delivery, string? BranchType, string? Agent, string? Repository, string? RepositoryId,
    string[]? Attachments, JsonElement? Components, JsonElement? AzureSource);

public sealed record RegistrationUpdate(
    string? Sprint, string? Release, string? BranchType, string? Delivery,
    string? Agent, string? Repository);
