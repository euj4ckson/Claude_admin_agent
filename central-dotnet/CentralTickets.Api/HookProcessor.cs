using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

/// <summary>Handles Claude hook events for a session started and owned by the .NET Central.</summary>
public sealed class HookProcessor(string dataRoot)
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly string _root = dataRoot;

    public async Task<JsonObject> HandleCodexSessionEventAsync(string ticketId, string runId, string eventName)
    {
        var id = ValidateId(ticketId);
        if (eventName is not ("SessionStart" or "SessionEnd")) throw new InvalidDataException("Evento de sessÃ£o invÃ¡lido.");
        var dir = Path.Combine(_root, "tickets", id.ToLowerInvariant());
        var ticket = Read(Path.Combine(dir, "ticket.json")) ?? throw new InvalidDataException("ticket.json ausente ou invÃ¡lido.");
        var runtime = Read(Path.Combine(dir, "runtime.json")) ?? throw new InvalidDataException("runtime.json ausente ou invÃ¡lido.");
        var active = Read(Path.Combine(dir, "active.lock"));
        if (!string.Equals(GetString(ticket, "agent"), "codex", StringComparison.OrdinalIgnoreCase)
            || GetString(runtime, "runId") != runId
            || GetString(active, "runId") != runId
            || GetString(runtime, "phase") != "running")
            throw new InvalidDataException("O evento nÃ£o corresponde a uma sessÃ£o Codex ativa deste ticket.");

        var input = new JsonObject { ["hook_event_name"] = eventName, ["session_id"] = GetString(ticket, "sessionId") };
        var metrics = RecordMetric(ticket, id, input, eventName);
        AppendEvent(dir, "sessao", eventName == "SessionStart" ? "Codex conectado Ã  sessÃ£o deste ticket." : "SessÃ£o Codex encerrada; trabalho preservado.");
        if (eventName == "SessionEnd")
        {
            try
            {
                var config = Read(Path.Combine(_root, "config.json"));
                var stateRoot = GetString(config, "state_base_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
                var references = GetString(config, "references_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "Ref Dev");
                var detail = new TicketCatalog(_root, stateRoot, references).GetDetails(id);
                if (detail is not null)
                {
                    var memory = new AiMemoryService(_root, new CentralProfile(_root), new HookHttpClientFactory());
                    var result = await memory.SyncTicket(detail, CancellationToken.None);
                    var status = GetString(result, "remote") == "synced" ? "sincronizado" : "enfileirado";
                    AppendEvent(dir, "memory", $"Aprendizado {status} para o AI Memory.");
                }
            }
            catch (Exception error) { AppendEvent(dir, "memory", "Falha ao sincronizar aprendizado; trabalho preservado localmente: " + error.Message); }
        }
        WriteJson(Path.Combine(dir, "hook-status.json"), new JsonObject
        {
            ["event"] = eventName, ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["sessionId"] = GetString(ticket, "sessionId"),
            ["metrics"] = metrics["totals"]?.DeepClone()
        });
        return metrics;
    }

    public async Task<JsonObject> HandleAsync(JsonObject input)
    {
        var id = Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID")?.Trim().ToUpperInvariant() ?? "";
        if (id.Length == 0) return new JsonObject();
        var runId = Environment.GetEnvironmentVariable("CENTRAL_TICKET_RUN") ?? "";
        var dir = Path.Combine(_root, "tickets", id.ToLowerInvariant());
        var ticketPath = Path.Combine(dir, "ticket.json");
        var ticket = Read(ticketPath) ?? throw new InvalidDataException("ticket.json ausente ou inválido.");
        var runtime = Read(Path.Combine(dir, "runtime.json")) ?? throw new InvalidDataException("runtime.json ausente ou inválido.");
        if (GetString(runtime, "runId") != runId) throw new InvalidDataException("A sessão não corresponde à reserva deste ticket.");
        var sessionId = GetString(input, "session_id");
        var active = Read(Path.Combine(dir, "active.lock"));
        var managed = GetString(active, "runId") == runId && GetString(runtime, "phase") is "starting" or "running";
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId != GetString(ticket, "sessionId"))
        {
            var known = (ticket["sessionIds"] as JsonArray ?? []).Any(item => item?.GetValue<string>() == sessionId);
            if (!known && !managed) throw new InvalidDataException("Esta sessão não corresponde à reserva ativa deste ticket.");
            if (!known)
            {
                var ids = ticket["sessionIds"] as JsonArray;
                if (ids is null)
                {
                    ids = new JsonArray();
                    if (GetString(ticket, "sessionId") is { } previousSessionId) ids.Add(previousSessionId);
                    ticket["sessionIds"] = ids;
                }
                ids.Add(sessionId);
                ticket["sessionId"] = sessionId;
                WriteJson(ticketPath, ticket); AppendEvent(dir, "sessao", "Identificador de conversa atualizado na sessão gerenciada; histórico preservado.");
            }
        }
        var eventName = GetString(input, "hook_event_name") ?? "unknown";
        JsonObject output = new();
        var policy = new HookPolicy(_root);
        switch (eventName)
        {
            case "SessionStart":
                if (sessionId != GetString(ticket, "sessionId")) throw new InvalidDataException("Esta janela pertence a outra sessão. Abra a conversa deste ticket pela Central.");
                ticket["sessionStarted"] = true; WriteJson(ticketPath, ticket);
                AppendEvent(dir, "sessao", "Claude conectado à sessão deste ticket.");
                var initialGuidance = TakeGuidance(ticket, input, eventName, dir);
                if (initialGuidance.Length > 0) output = Additional("SessionStart", initialGuidance);
                break;
            case "UserPromptSubmit":
                output = policy.ApprovalPrompt(input) ?? new JsonObject();
                var promptGuidance = TakeGuidance(ticket, input, eventName, dir);
                if (promptGuidance.Length > 0)
                {
                    var previous = output["hookSpecificOutput"]?["additionalContext"]?.GetValue<string>() ?? "";
                    output = Additional("UserPromptSubmit", string.Join(Environment.NewLine, new[] { previous, promptGuidance }.Where(value => value.Length > 0)));
                }
                break;
            case "PreToolUse":
                output = policy.PreTool(input);
                if (output["hookSpecificOutput"]?["permissionDecision"]?.GetValue<string>() == "deny")
                    AppendEvent(dir, "controle", output["hookSpecificOutput"]?["permissionDecisionReason"]?.GetValue<string>() ?? "Operação negada pela política.");
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                policy.ReleaseExecution(input);
                break;
            case "Stop":
                AppendEvent(dir, "turno", "Claude concluiu uma resposta. Isso não significa que o ticket foi concluído.");
                policy.ReleaseExecution(input);
                break;
            case "SessionEnd":
                policy.ReleaseExecution(input);
                AppendEvent(dir, "sessao", "Conversa encerrada. Trabalho e histórico foram preservados.");
                break;
        }
        var metrics = RecordMetric(ticket, id, input, eventName);
        if (eventName == "SessionEnd")
        {
            try
            {
                var config = Read(Path.Combine(_root, "config.json"));
                var stateRoot = GetString(config, "state_base_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
                var references = GetString(config, "references_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "Ref Dev");
                var detail = new TicketCatalog(_root, stateRoot, references).GetDetails(id);
                if (detail is not null)
                {
                    var memory = new AiMemoryService(_root, new CentralProfile(_root), new HookHttpClientFactory());
                    var result = await memory.SyncTicket(detail, CancellationToken.None);
                    var status = GetString(result, "remote") == "synced" ? "sincronizado" : "enfileirado";
                    AppendEvent(dir, "memory", $"Aprendizado {status} para o AI Memory.");
                }
            }
            catch (Exception error) { AppendEvent(dir, "memory", "Falha ao sincronizar aprendizado; trabalho preservado localmente: " + error.Message); }
        }
        WriteJson(Path.Combine(dir, "hook-status.json"), new JsonObject
        {
            ["event"] = eventName, ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["sessionId"] = sessionId,
            ["metrics"] = metrics["totals"]?.DeepClone()
        });
        return output;
    }

    private JsonObject RecordMetric(JsonObject ticket, string id, JsonObject input, string eventName)
    {
        var statePath = StatePath(ticket, id);
        var state = Read(statePath);
        var directory = Path.GetDirectoryName(statePath)!;
        var path = Path.Combine(Path.GetDirectoryName(Path.Combine(_root, "tickets", id.ToLowerInvariant(), "ticket.json"))!, "metrics.json");
        var metrics = Read(path) ?? EmptyMetrics(id);
        var at = DateTimeOffset.UtcNow.ToString("O");
        var phase = Phase(GetString(state, "stage"));
        if (metrics["phases"] is not JsonObject phases) metrics["phases"] = phases = new JsonObject();
        if (metrics["totals"] is not JsonObject totals)
        {
            totals = new JsonObject
            {
                ["estimated_input_tokens"] = 0, ["estimated_output_tokens"] = 0,
                ["reported_input_tokens"] = 0, ["reported_output_tokens"] = 0,
                ["tool_calls"] = 0, ["prompts"] = 0, ["failures"] = 0, ["turns"] = 0
            };
            metrics["totals"] = totals;
        }
        var previous = GetString(metrics, "last_stage");
        var previousPhase = previous is null ? null : Phase(previous);
        if (GetString(metrics, "started_at") is null) metrics["started_at"] = at;
        if (previousPhase is not null && previousPhase != phase && phases[previousPhase] is JsonObject previousEntry) ClosePhase(previousEntry, at);
        if (phases[phase] is not JsonObject current)
        {
            current = new JsonObject { ["started_at"] = at, ["ended_at"] = null, ["seconds"] = 0,
                ["estimated_input_tokens"] = 0, ["estimated_output_tokens"] = 0, ["reported_input_tokens"] = 0,
                ["reported_output_tokens"] = 0, ["prompts"] = 0, ["tool_calls"] = 0, ["failures"] = 0, ["turns"] = 0 };
            phases[phase] = current;
        }
        var usage = input["usage"] as JsonObject ?? input["token_usage"] as JsonObject;
        var inTokens = (long)Number(usage, "input_tokens", Number(usage, "prompt_tokens"));
        var outTokens = (long)Number(usage, "output_tokens", Number(usage, "completion_tokens"));
        var estimatedIn = eventName == "UserPromptSubmit" ? Estimate(input["prompt"]) : eventName == "PreToolUse" ? Estimate(input["tool_input"]) : 0;
        var estimatedOut = eventName.StartsWith("PostToolUse", StringComparison.Ordinal) ? Estimate(input["tool_response"] ?? input["tool_result"] ?? input["tool_output"]) : 0;
        Add(totals, "estimated_input_tokens", estimatedIn); Add(totals, "estimated_output_tokens", estimatedOut);
        Add(totals, "reported_input_tokens", inTokens); Add(totals, "reported_output_tokens", outTokens);
        Add(current, "estimated_input_tokens", estimatedIn); Add(current, "estimated_output_tokens", estimatedOut);
        Add(current, "reported_input_tokens", inTokens); Add(current, "reported_output_tokens", outTokens);
        if (eventName == "UserPromptSubmit") { Add(totals, "prompts", 1); Add(current, "prompts", 1); }
        if (eventName == "PreToolUse") { Add(totals, "tool_calls", 1); Add(current, "tool_calls", 1); }
        if (eventName == "PostToolUseFailure") { Add(totals, "failures", 1); Add(current, "failures", 1); }
        if (eventName == "Stop") { Add(totals, "turns", 1); Add(current, "turns", 1); }
        if (phase == "bloqueado" && (metrics["blockers"] as JsonArray ?? []).LastOrDefault()?["stage"]?.GetValue<string>() != GetString(state, "stage"))
        {
            var blockers = metrics["blockers"] as JsonArray ?? new JsonArray(); metrics["blockers"] = blockers;
            blockers.Add(new JsonObject { ["at"] = at, ["stage"] = GetString(state, "stage") ?? "bloqueado", ["reason"] = GetString(state, "next_action") ?? "bloqueio informado no estado" });
        }
        var revision = (int)Number(state, "scope_revision", 1); var budget = state?["change_budget"] as JsonObject;
        var rounds = (int)Number(budget, "correction_rounds"); var expansion = GetBool(budget, "approved_expansion");
        if (revision > 1 || expansion || rounds > 0)
        {
            var key = $"{revision}:{rounds}:{expansion.ToString().ToLowerInvariant()}";
            var history = metrics["scope_expansions"] as JsonArray ?? new JsonArray(); metrics["scope_expansions"] = history;
            if (!history.OfType<JsonObject>().Any(item => GetString(item, "key") == key)) history.Add(new JsonObject { ["key"] = key, ["at"] = at, ["revision"] = revision, ["correction_rounds"] = rounds, ["approved"] = expansion });
        }
        metrics["last_stage"] = GetString(state, "stage") ?? GetString(metrics, "last_stage"); metrics["last_event_at"] = at;
        if (eventName == "SessionEnd") { metrics["ended_at"] = at; ClosePhase(current, at); }
        WriteJson(path, metrics);
        return metrics;
    }

    private string TakeGuidance(JsonObject ticket, JsonObject input, string eventName, string ticketDir)
    {
        const string version = "2026-10-21-dotnet-1";
        var sessionId = GetString(input, "session_id") ?? GetString(ticket, "sessionId") ?? "";
        var file = Path.Combine(ticketDir, "guidance-receipt.json");
        var receipt = Read(file);
        var key = $"{sessionId}:{version}";
        if (GetString(receipt, "key") == key && !(eventName == "SessionStart" && GetString(input, "source") == "compact")) return "";
        WriteJson(file, new JsonObject { ["key"] = key });
        var text = new StringBuilder();
        text.AppendLine("Economia sem reduzir qualidade: preserve aprovação humana, análise de impacto e rotinas paralelas, revisão independente, matriz integral, build/testes e evidências verificáveis. Não marque teste não executado como OK.");
        text.AppendLine("Leia a entrada, o estado e o skill do repositório; use rg para localizar símbolos e leia métodos, chamadores e consumidores. Evite dumps e releituras desnecessárias. Após qualquer correção, repita os testes afetados. Guarde logs completos no scratch e registre comando, exit code, resumo e caminho.");
        text.AppendLine("Para testes em tela, capture antes/ação/resultado e inclua imagens no DOCX; se a ferramenta estiver indisponível, registre SCREENSHOT_NAO_EXECUTADO. Mantenha o diff estritamente dentro do escopo, registre progresso sem repetir o plano e crie checkpoint antes de compactar.");
        text.AppendLine("Compilação/testes: execute de forma síncrona e isolada. ERP: SQL local -S localhost -E e base SSBD_TESTE do ticket; build em worktree com OutputPath no scratch. Recursos isolados não devem bloquear outros tickets; recursos compartilhados são reservados somente durante o comando.");
        var repository = GetString(ticket, "repository") ?? "";
        var repositoryId = GetString(ticket, "repositoryId") ?? Path.GetFileName(repository);
        if (repositoryId.Equals("ss_fva", StringComparison.OrdinalIgnoreCase) || repositoryId.Equals("ss_wcf_fvan", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(GetString(ticket, "id") ?? "", "^FVA\\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var config = Read(Path.Combine(_root, "config.json"));
            var repositoriesRoot = GetString(config, "repositories_root") ?? Path.GetDirectoryName(repository) ?? "";
            text.AppendLine("Ecossistema FVA/WCF/ERP: mapear tabela/view ERP → DAO/contrato SOAP WCF → DTO/SQLite/sincronização/tela e caminho de volta. Usar main como origem e pre_main como homologação em FVA/WCF; ERP segue seu fluxo. Restaurar NuGet no WCF; não versionar bin/obj/.vs/packages; build automático do WCF é obrigatório em PR.");
            text.AppendLine($"Repos para consulta: ERP={GetString(config, "repository")}; ss_fva={Path.Combine(repositoriesRoot, "ss_fva")}; ss_wcf_fvan={Path.Combine(repositoriesRoot, "ss_wcf_fvan")}. Só os componentes classificados como change podem ser editados.");
            text.AppendLine("Autorização funcional confirmada: tickets FVA/WCF podem usar SSBDFV_PD no CloudSS para o fluxo previsto, inclusive exportação pelo WCF. Antes de gravar, confirme endpoint/banco e se o cenário já existe. Limite gravação ao registro e ação previstos; sem DML manual remoto, migrations, limpeza ou exclusão sem autorização específica. Preserve Web.config e dados pessoais.");
            text.AppendLine("Teste integrado deve comprovar APK e DLL carregados das worktrees, endpoint, emulador e base sem segredos; confira instalação existente/upgrade SQLite, sincronização repetida, offline/timeout, troca de vendedor e variantes equivalentes no ERP.");
        }
        return text.ToString().Trim();
    }

    private string StatePath(JsonObject ticket, string id)
    {
        var config = Read(Path.Combine(_root, "config.json"));
        var root = GetString(config, "state_base_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
        var repository = GetString(ticket, "repositoryId") ?? Path.GetFileName(GetString(ticket, "repository") ?? "");
        var configured = Path.GetFileName((GetString(config, "repository") ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = string.Equals(configured, repository, StringComparison.OrdinalIgnoreCase) ? configured : repository;
        return Path.Combine(root, folder.ToLowerInvariant(), id.ToLowerInvariant(), "estado.json");
    }

    private static JsonObject EmptyMetrics(string id) => new()
    {
        ["schema_version"] = 1, ["ticket"] = id, ["started_at"] = null, ["ended_at"] = null,
        ["last_stage"] = null, ["last_event_at"] = null, ["phases"] = new JsonObject(),
        ["totals"] = new JsonObject { ["estimated_input_tokens"] = 0, ["estimated_output_tokens"] = 0,
            ["reported_input_tokens"] = 0, ["reported_output_tokens"] = 0, ["tool_calls"] = 0, ["prompts"] = 0,
            ["failures"] = 0, ["turns"] = 0 }, ["scope_expansions"] = new JsonArray(), ["reviews"] = new JsonArray(),
        ["blockers"] = new JsonArray(), ["escaped_findings"] = new JsonArray(), ["memory_sync"] = new JsonArray()
    };

    private static string Phase(string? stage) => stage switch
    {
        "analise" => "analise", "aguardando_aprovacao" => "aprovacao", "implementacao" => "implementacao",
        "compilacao" => "compilacao", "testes" => "testes", "revisao" => "revisao",
        "aguardando_validacao_manual" => "validacao_manual", "entrega" or "concluido" or "entregue" => "entrega",
        "bloqueado" => "bloqueado", _ => "outro"
    };
    private static long Estimate(JsonNode? value) { var text = value is JsonValue scalar && scalar.TryGetValue<string>(out var content) ? content : value?.ToJsonString() ?? ""; return (Encoding.UTF8.GetByteCount(text) + 3) / 4; }
    private static void Add(JsonObject target, string key, long amount) => target[key] = NumericValue(target[key]) + amount;
    private static long NumericValue(JsonNode? value) => value is JsonValue item && item.TryGetValue<long>(out var number) ? number : value is JsonValue integer && integer.TryGetValue<int>(out var intValue) ? intValue : 0;
    private static double Number(JsonObject? obj, string key, double fallback = 0) => obj?[key] is JsonValue item && item.TryGetValue<double>(out var number) ? number : obj?[key] is JsonValue integer && integer.TryGetValue<int>(out var intValue) ? intValue : fallback;
    private static bool GetBool(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static void ClosePhase(JsonObject phase, string at)
    {
        if (!DateTimeOffset.TryParse(GetString(phase, "started_at"), out var start)) return;
        phase["ended_at"] = at; phase["seconds"] = Math.Max(0, (long)Math.Round((DateTimeOffset.Parse(at) - start).TotalSeconds));
    }

    private static void AppendEvent(string directory, string type, string message) =>
        File.AppendAllText(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }) + Environment.NewLine);
    private static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, node.ToJsonString(PrettyJson), new System.Text.UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
    private static JsonObject? Read(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new InvalidDataException($"Não foi possível ler {Path.GetFileName(path)}; arquivo preservado."); }
    }
    private static string? GetString(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string ValidateId(string id)
    {
        var normalized = id.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z]{2,8}[0-9]{2,8}$")) throw new InvalidDataException("Identificador de ticket invÃ¡lido.");
        return normalized;
    }
    private sealed class HookHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static JsonObject Additional(string eventName, string context) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = eventName,
            ["additionalContext"] = context
        }
    };
}
