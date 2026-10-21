using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

/// <summary>Fail-closed policy decisions used by the managed Claude session hook.</summary>
public sealed class HookPolicy(string dataRoot)
{
    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _root = dataRoot;

    public JsonObject PreTool(JsonObject input)
    {
        var id = ValidateId(Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID") ?? "");
        var runId = Environment.GetEnvironmentVariable("CENTRAL_TICKET_RUN") ?? "";
        var dir = TicketDir(id);
        var ticket = Read(Path.Combine(dir, "ticket.json")) ?? throw new InvalidDataException("ticket.json ausente ou inválido.");
        var runtime = Read(Path.Combine(dir, "runtime.json")) ?? throw new InvalidDataException("runtime.json ausente ou inválido.");
        if (GetString(runtime, "runId") != runId) throw new InvalidDataException("A sessão não corresponde à reserva deste ticket.");
        var args = input["tool_input"] as JsonObject ?? new JsonObject();
        var tool = GetString(input, "tool_name") ?? "";
        var state = Read(StateFile(ticket, id));
        var approval = CurrentApproval(ticket, id, state);

        if (tool is "Write" or "Edit" or "NotebookEdit")
        {
            var target = GetString(args, "file_path") ?? GetString(args, "notebook_path");
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return Deny("A gravação precisa informar um caminho absoluto.");
            var stateDir = Path.GetDirectoryName(StateFile(ticket, id))!;
            if (Inside(stateDir, target)) return Allow("Registro de análise/evidências na pasta do próprio ticket.");
            var component = (ticket["components"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item =>
                GetBool(item, "prepared") && !string.IsNullOrWhiteSpace(GetString(item, "worktree")) && Inside(GetString(item, "worktree")!, target));
            var worktree = GetString(component, "worktree") ?? GetString(ticket, "worktree");
            var documents = Read(Path.Combine(_root, "config.json")) is { } cfg ? GetString(cfg, "documents_root") : null;
            var documentRoot = string.IsNullOrWhiteSpace(documents) ? "" : Path.Combine(documents, id.ToLowerInvariant());
            var withinWorktree = !string.IsNullOrWhiteSpace(worktree) && Inside(worktree, target);
            if (!withinWorktree && (string.IsNullOrWhiteSpace(documentRoot) || !Inside(documentRoot, target))) return Deny("Este arquivo está fora da worktree e das pastas de entrega deste ticket.");
            if (withinWorktree && Regex.IsMatch(target, @"(?:^|[\\/])\.git(?:[\\/]|$)|[\\/]\.claude[\\/]settings[^\\/]*\.json$", RegexOptions.IgnoreCase)) return Deny("Não altere os controles Git ou as permissões da sessão por edição de arquivo.");
            if (approval is null) return Deny($"O plano ainda não tem aprovação válida. Aguarde o usuário enviar APROVAR {id} na conversa após ler a análise.");
            var budget = component is null ? state?["change_budget"] as JsonObject : state?["component_plans"]?[GetString(component, "repositoryId") ?? ""] as JsonObject;
            var planned = GetStringArray(budget, "planned_files").Select(NormalizePlanPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (component is not null && approval["componentPlansHash"]?.GetValue<string>() != ComponentPlansHash(state)) return Deny("O plano de arquivos do componente mudou depois da aprovação. Registre revisão e solicite nova aprovação.");
            if (component is not null && (GetString(budget, "mode") != "change" || planned.Count == 0)) return Deny("O componente não possui autorização change e lista de arquivos no plano aprovado.");
            if (withinWorktree && planned.Count > 0)
            {
                var relative = NormalizePlanPath(Path.GetRelativePath(worktree!, target));
                if (!planned.Contains(relative)) return Deny($"Arquivo fora do patch mínimo aprovado ({relative}). Pare e solicite nova aprovação.");
            }
            return Allow("Arquivo autorizado pelo plano vigente.");
        }

        if (tool is not ("Bash" or "PowerShell")) return new JsonObject();
        var command = GetString(args, "command") ?? "";
        if (IsReadCommand(command)) return Allow("Consulta de análise sem alteração; não exige confirmação nem reserva de testes.");
        if (Regex.IsMatch(command, @"\bgit(?:\.exe)?\b[\s\S]*\bpush\b|\b(?:gh\s+pr\s+(?:create|merge)|az\s+repos\s+pr\s+(?:create|update))\b", RegexOptions.IgnoreCase)) return Deny("Publicação não faz parte desta sessão. A entrega é local, sem push ou PR.");
        if (Regex.IsMatch(command, @"\bgit(?:\.exe)?\b[\s\S]*\b(?:reset|clean)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(command, @"(?:--hard|\s-[a-z]*[fd])", RegexOptions.IgnoreCase)) return Deny("Comando Git destrutivo bloqueado. Preserve alterações e worktrees.");
        var isCommit = Regex.IsMatch(command, @"\bgit(?:\.exe)?\b[\s\S]*\bcommit\b", RegexOptions.IgnoreCase);
        if (isCommit)
        {
            if (GetString(ticket, "delivery") == "no_commit" || state?["delivery"]?["commit_allowed"]?.GetValue<bool>() == false) return Deny("Este ticket está configurado sem commit.");
            if (approval is null) return Deny("Não há aprovação válida do plano para commitar.");
            if ((state?["build"] as JsonArray)?.Count is not > 0 || (state?["tests"] as JsonArray)?.Count is not > 0 || state?["review"] is not JsonObject) return Deny("Registre compilação, testes e revisão no estado antes do commit; esses registros ainda precisam de evidências.");
            if (GetString(state, "stage") is not ("entrega" or "aguardando_validacao_manual")) return Deny("Conclua a revisão e registre pendências antes de commitar.");
            var quality = CommitQuality(state, ticket, id, checkDiff: true);
            if (quality.Count > 0) return Deny($"Gate de qualidade bloqueou o commit: {string.Join("; ", quality.Take(4))}. Registre evidências concretas antes de commitar.");
        }

        var shared = Regex.IsMatch(command, @"\b(?:msbuild(?:\.exe)?|sqlcmd(?:\.exe)?|vstest(?:\.console)?(?:\.exe)?|dotnet\s+(?:build|test))\b", RegexOptions.IgnoreCase);
        var stage = GetString(state, "stage");
        var critical = stage is "compilacao" or "testes";
        if (shared || critical)
        {
            if (approval is null) return Deny("Compilação e testes aguardam aprovação do plano.");
            if (GetBool(args, "run_in_background")) return Deny("Execute compilação e testes de forma síncrona para manter a reserva até a conclusão.");
            if (!critical) return Deny("Antes de compilar ou testar, registre stage=compilacao ou stage=testes no estado.");
            if (Regex.IsMatch(command, @"\bsqlcmd(?:\.exe)?\b", RegexOptions.IgnoreCase) && !SqlTargetAllowed(id, stage, command)) return Deny("SQLCMD deve usar -S localhost -E; a exceção temporária CMX0223 é limitada ao banco de teste documentado.");
            if (IsolatedExecution(ticket, id, command)) return Allow("Execução isolada na worktree/scratch ou banco exclusivo do ticket.");
            var owner = AcquireExecution(id, runId, IntValue(runtime, "pid"), GetString(input, "tool_use_id"));
            if (owner is not null) return Deny($"Operação compartilhada em execução por {owner}. Use build em OutputPath isolado ou banco SSBD_TESTE_{id}; não compartilhe instalação ou banco em paralelo.");
            var safeBuild = Regex.IsMatch(command, @"(?:MSBuild(?:\.exe)?|msbuild)\b[\s\S]*\/t\s*:\s*Build\b", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(command, @"(?:\/t\s*:\s*(?:Clean|Rebuild|Restore)|-t\s*(?:Clean|Rebuild|Restore)|restore|clean|rebuild)", RegexOptions.IgnoreCase);
            if (safeBuild && critical) return Allow("Build MSBuild aprovado automaticamente na etapa de compilação; sem confirmação extra.");
        }
        if (approval is null) return Deny("Sem aprovação do plano, use consultas somente leitura e registre análise/plano na pasta do ticket. Comando não reconhecido como leitura; não peça confirmação avulsa para contornar o plano.");
        return Allow("Execução autorizada pelo plano vigente após controles de publicação, commit e recursos.");
    }

    public JsonObject? ApprovalPrompt(JsonObject input)
    {
        var id = ValidateId(Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID") ?? "");
        var prompt = GetString(input, "prompt")?.Trim() ?? "";
        if (!prompt.Equals($"APROVAR {id}", StringComparison.OrdinalIgnoreCase)) return null;
        var dir = TicketDir(id);
        var ticket = Read(Path.Combine(dir, "ticket.json")) ?? throw new InvalidDataException("ticket.json ausente ou inválido.");
        var sessionId = GetString(input, "session_id") ?? "";
        var accepted = new HashSet<string>(StringComparer.Ordinal) { GetString(ticket, "sessionId") ?? "" };
        foreach (var value in ticket["sessionIds"] as JsonArray ?? []) if (value is JsonValue item && item.TryGetValue<string>(out var session)) accepted.Add(session);
        if (!accepted.Contains(sessionId)) throw new InvalidDataException("A aprovação pertence a outra sessão. Abra a conversa deste ticket.");
        var stateFile = StateFile(ticket, id);
        var state = Read(stateFile) ?? throw new InvalidDataException("estado.json ausente ou inválido.");
        var scopeFile = Path.Combine(Path.GetDirectoryName(stateFile)!, "escopo.md");
        if (!File.Exists(scopeFile)) throw new InvalidDataException("A análise precisa estar concluída e o plano salvo antes da aprovação.");
        var scopeBytes = File.ReadAllBytes(scopeFile);
        if (scopeBytes.Length < 40) throw new InvalidDataException("O plano salvo está incompleto.");
        if (CurrentApproval(ticket, id, state) is not null)
            return Additional("UserPromptSubmit", $"A aprovação vigente de {id} continua válida para o mesmo escopo, arquivos e orçamento. A etapa {GetString(state, "stage") ?? "atual"} foi preservada; prossiga sem reiniciar a análise nem alterar o escopo.");
        if (GetString(state, "stage") != "aguardando_aprovacao") throw new InvalidDataException("A aprovação expirou porque o plano mudou. Registre revisão em escopo.md e change_budget, mude stage para aguardando_aprovacao e solicite nova aprovação.");
        var readiness = ValidateApprovalPlan(state, Encoding.UTF8.GetString(scopeBytes));
        if (readiness.Issues.Count > 0) throw new InvalidDataException("Plano ainda não está pronto para aprovação: " + string.Join("; ", readiness.Issues.Take(8)) + ".");
        state["component_plans"] = readiness.NormalizedPlans;
        var budget = state["change_budget"] as JsonObject ?? new JsonObject();
        state["change_budget"] = budget;
        if (readiness.OverGuard)
        {
            budget["approved_expansion"] = true;
            budget["approved_expansion_at"] = DateTimeOffset.UtcNow.ToString("O");
            budget["approved_expansion_by"] = "usuario";
        }
        state["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        WriteJson(stateFile, state);
        var approval = new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["ticket"] = id, ["sessionId"] = sessionId,
            ["scopeHash"] = Hash(scopeBytes), ["componentPlansHash"] = ComponentPlansHash(state),
            ["changeBudgetHash"] = Hash(Encoding.UTF8.GetBytes(BudgetSnapshot(budget).ToJsonString(CompactJson))), ["prompt"] = prompt
        };
        WriteJson(Path.Combine(dir, "approval.json"), approval);
        AppendEvent(dir, "aprovacao", "Implementação aprovada na conversa, vinculada ao escopo e aos planos de componentes.");
        if (readiness.OverGuard) AppendEvent(dir, "escopo", "Expansão acima do scope_guard aprovada junto com o plano; hard-stop preservado.");
        return Additional("UserPromptSubmit", $"Aprovação humana registrada para {id}, plano SHA256 {GetString(approval, "scopeHash")}. Registre o OK no estado e prossiga dentro do escopo aprovado. A entrega continua sem push.");
    }

    public void ReleaseExecution(JsonObject input)
    {
        var runtime = Read(Path.Combine(TicketDir(ValidateId(Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID") ?? "")), "runtime.json"));
        if (runtime is null) return;
        var file = Path.Combine(_root, "execution.lock");
        var owner = Read(file);
        var toolUseId = GetString(input, "tool_use_id");
        if (GetString(owner, "runId") == GetString(runtime, "runId") && (toolUseId is null || GetString(owner, "toolUseId") is null || GetString(owner, "toolUseId") == toolUseId)) TryDelete(file);
    }

    private JsonObject? CurrentApproval(JsonObject ticket, string id, JsonObject? state)
    {
        var approval = Read(Path.Combine(TicketDir(id), "approval.json"));
        if (approval is null) return null;
        var scopeFile = Path.Combine(Path.GetDirectoryName(StateFile(ticket, id))!, "escopo.md");
        if (!File.Exists(scopeFile) || GetString(approval, "scopeHash") != Hash(File.ReadAllBytes(scopeFile))) return null;
        var plans = state?["component_plans"] as JsonObject ?? new JsonObject();
        if (GetString(approval, "componentPlansHash") is { } planHash && planHash != Hash(Encoding.UTF8.GetBytes(plans.ToJsonString(CompactJson)))) return null;
        if (GetString(approval, "changeBudgetHash") is { } budgetHash)
        {
            var current = BudgetSnapshot(state?["change_budget"] as JsonObject);
            if (budgetHash != Hash(Encoding.UTF8.GetBytes(current.ToJsonString(CompactJson))))
            {
                if (IntValue(state?["change_budget"] as JsonObject, "correction_rounds") is > 0)
                {
                    var legacy = state?["change_budget"]?.DeepClone() as JsonObject ?? new JsonObject();
                    legacy["correction_rounds"] = 0;
                    if (budgetHash != Hash(Encoding.UTF8.GetBytes(BudgetSnapshot(legacy).ToJsonString(CompactJson)))) return null;
                }
                else return null;
            }
        }
        return approval;
    }

    private static (List<string> Issues, JsonObject NormalizedPlans, bool OverGuard) ValidateApprovalPlan(JsonObject state, string scope)
    {
        var issues = new List<string>();
        var plans = state["component_plans"] as JsonObject ?? new JsonObject();
        var allFiles = plans.SelectMany(pair => GetStringArray(pair.Value as JsonObject, "planned_files")).ToArray();
        var normalized = new JsonObject();
        var normalizedScope = NormalizePlanPath(scope);
        foreach (var pair in plans)
        {
            var source = pair.Value as JsonObject;
            var plan = source is null ? new JsonObject() : (JsonObject)source.DeepClone();
            var mode = GetString(plan, "mode");
            if (mode is not ("change" or "read_only"))
            {
                var compatibility = plan["changes_required"]?.GetValue<bool?>();
                mode = compatibility is null ? null : compatibility.Value ? "change" : "read_only";
            }
            if (mode is null) issues.Add($"{pair.Key}: informe mode change/read_only ou changes_required booleano");
            var paths = plan["planned_files"] as JsonArray;
            if (!normalizedScope.Contains(pair.Key.ToLowerInvariant(), StringComparison.Ordinal)) issues.Add($"{pair.Key}: identificador do repositório ausente em escopo.md");
            if (paths is null) issues.Add($"{pair.Key}: planned_files precisa ser uma lista");
            else if (mode == "change" && paths.Count == 0) issues.Add($"{pair.Key}: componente change sem planned_files");
            else if (mode == "read_only" && paths.Count > 0) issues.Add($"{pair.Key}: componente read_only contém arquivos planejados");
            else foreach (var path in paths.OfType<JsonValue>().Select(value => value.ToString())) if (!ScopeMentionsFile(scope, path, allFiles)) issues.Add($"{pair.Key}: caminho não localizado no plano: {path}");
            if (mode is not null) plan["mode"] = mode;
            normalized[pair.Key] = plan;
        }
        var budget = state["change_budget"] as JsonObject ?? new JsonObject();
        var guard = state["scope_guard"] as JsonObject ?? new JsonObject();
        var plannedCount = budget["planned_files"] is JsonArray files ? files.Count : allFiles.Length;
        var added = Number(budget, "estimated_added_lines"); var deleted = Number(budget, "estimated_deleted_lines");
        var overGuard = plannedCount > Number(guard, "max_planned_files", double.PositiveInfinity)
            || added > Number(guard, "max_planned_added_lines", double.PositiveInfinity)
            || deleted > Number(guard, "max_planned_deleted_lines", double.PositiveInfinity);
        var hard = budget["hard_stop"] as JsonObject ?? guard["hard_stop"] as JsonObject ?? new JsonObject();
        if (plannedCount > Number(hard, "max_files", double.PositiveInfinity) || added > Number(hard, "max_added_lines", double.PositiveInfinity) || deleted > Number(hard, "max_deleted_lines", double.PositiveInfinity))
            issues.Add("plano excede o hard-stop; reduza o escopo antes de solicitar aprovação");
        if (overGuard && (string.IsNullOrWhiteSpace(GetString(budget, "exceeds_reason")) || !normalizedScope.Contains("scope_guard", StringComparison.Ordinal)))
            issues.Add("plano excede o scope_guard sem justificativa explícita no estado e em escopo.md");
        return (issues, normalized, overGuard);
    }

    private static bool ScopeMentionsFile(string scope, string file, string[] allFiles)
    {
        var text = NormalizePlanPath(scope); var target = NormalizePlanPath(file);
        if (target.Length > 0 && text.Contains(target, StringComparison.Ordinal)) return true;
        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 1; start < parts.Length - 1; start++)
        {
            var suffix = string.Join('/', parts.Skip(start));
            var matches = allFiles.Count(other => NormalizePlanPath(other).EndsWith('/' + suffix, StringComparison.Ordinal));
            if (matches == 1 && text.Contains(suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static JsonObject BudgetSnapshot(JsonObject? source)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "planned_files", "estimated_added_lines", "estimated_deleted_lines", "hard_stop", "exceeds_scope_guard", "exceeds_reason", "approved_expansion" })
            result[key] = source?[key]?.DeepClone();
        return result;
    }

    private static double Number(JsonObject? obj, string key, double fallback = 0) => obj?[key] is JsonValue value && value.TryGetValue<double>(out var result) ? result : fallback;
    private static JsonObject Additional(string hookEvent, string context) => new() { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = hookEvent, ["additionalContext"] = context } };
    private static void WriteJson(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { TryDelete(temporary); }
    }
    private static void AppendEvent(string dir, string type, string message) => File.AppendAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }) + Environment.NewLine);

    private string? AcquireExecution(string id, string runId, int? pid, string? toolUseId)
    {
        var file = Path.Combine(_root, "execution.lock");
        var owner = Read(file);
        if (GetString(owner, "runId") == runId) return null;
        if (owner is not null && !IsAlive(IntValue(owner, "pid")) && !RuntimeChildAlive(GetString(owner, "id"))) { TryDelete(file); owner = null; }
        if (owner is not null) return GetString(owner, "id") ?? "outro ticket";
        var lockValue = new JsonObject { ["id"] = id, ["runId"] = runId, ["pid"] = pid, ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["toolUseId"] = toolUseId };
        try { using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read); JsonSerializer.Serialize(stream, lockValue); return null; }
        catch (IOException) { return GetString(Read(file), "id") ?? "outro ticket"; }
    }

    private bool RuntimeChildAlive(string? id) => !string.IsNullOrWhiteSpace(id) && IsAlive(IntValue(Read(Path.Combine(TicketDir(id), "runtime.json")), "childPid"));
    private static bool IsAlive(int? pid) { if (pid is null or <= 0) return false; try { using var process = Process.GetProcessById(pid.Value); return !process.HasExited; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } }

    private bool SqlTargetAllowed(string id, string? stage, string command)
    {
        var targets = Regex.Matches(command, "(?:^|\\s)-S\\s*[\\\"']?([^\\s\\\"';|]+)", RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value.ToLowerInvariant()).ToArray();
        var databases = Regex.Matches(command, "(?:^|\\s)-d\\s*[\\\"']?([^\\s\\\"';|]+)", RegexOptions.IgnoreCase).Select(match => match.Groups[1].Value.ToLowerInvariant()).ToArray();
        var integrated = Regex.IsMatch(command, @"(?:^|\s)-E(?:\s|$)", RegexOptions.IgnoreCase);
        if (targets.Length == 1 && targets[0] == "localhost" && integrated) return true;
        return id == "CMX0223" && stage == "testes" && integrated && targets.SequenceEqual(["cloudss.dyndns.org,667"]) && databases.SequenceEqual(["ssbdlordpampa_pd"]);
    }

    private bool IsolatedExecution(JsonObject ticket, string id, string command)
    {
        if (Regex.IsMatch(command, @"[;|\n`$]|&&|\|\|")) return false;
        var stateDir = Path.GetDirectoryName(StateFile(ticket, id))!;
        var scratch = Path.Combine(stateDir, "scratch");
        if (Regex.IsMatch(command, @"\bmsbuild(?:\.exe)?\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(command, @"\bsqlcmd\b", RegexOptions.IgnoreCase))
        {
            var output = CapturePathArguments(command, @"[/-]p:OutputPath=(?:""([^""\r\n]+)""|'([^'\r\n]+)'|([^\s""']+))");
            var projects = CapturePathArguments(command, @"(?:""([^""\r\n]+\.(?:vbproj|csproj|sln))""|'([^'\r\n]+\.(?:vbproj|csproj|sln))'|([^\s""']+\.(?:vbproj|csproj|sln)))");
            return output.Length == 1 && Inside(scratch, output[0]) && projects.Length == 1 && Path.IsPathFullyQualified(projects[0])
                && Inside(GetString(ticket, "worktree") ?? "", projects[0]) && Regex.IsMatch(command, "[/-]p:PostBuildEvent=(?:\\\"\\\"|'')?(?=\\s|$)", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(command, @"[/-]p:(?:OutDir|PreBuildEvent|CustomAfter\w*|CustomBefore\w*)=", RegexOptions.IgnoreCase);
        }
        if (!Regex.IsMatch(command, @"\bsqlcmd(?:\.exe)?\b", RegexOptions.IgnoreCase)) return false;
        var db = CapturePathArguments(command, @"(?:^|\s)-d\s+(?:""([^""\r\n]+)""|'([^'\r\n]+)'|([^\s""']+))");
        if (db.Length != 1 || !Regex.IsMatch(db[0], $"^SSBD_TESTE_{Regex.Escape(id)}(?:_[A-Z0-9]+)*$", RegexOptions.IgnoreCase)) return false;
        var files = CapturePathArguments(command, @"(?:^|\s)-i\s+(?:""([^""\r\n]+)""|'([^'\r\n]+)'|([^\s""']+))");
        var queries = CapturePathArguments(command, @"(?:^|\s)-[Qq]\s+(?:""([^""\r\n]+)""|'([^'\r\n]+)'|([^\s""']+))");
        if (files.Length + queries.Length != 1 || files.Any(file => !Inside(scratch, file) || !File.Exists(file))) return false;
        var sql = queries.Length == 1 ? queries[0] : File.ReadAllText(files[0]);
        return !Regex.IsMatch(sql, @"\b(?:USE|EXEC(?:UTE)?|RESTORE|BACKUP|DATABASE|OPENROWSET|OPENDATASOURCE|xp_\w+)\b|\]\s*\.\s*\[|\b\w+\.\w+\.\w+|^\s*[:!]", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private List<string> CommitQuality(JsonObject? state, JsonObject ticket, string id, bool checkDiff)
    {
        var issues = new List<string>();
        var directory = Path.GetDirectoryName(StateFile(ticket, id))!;
        var manifest = Read(Path.Combine(directory, "evidence-manifest.json"));
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest?["entries"] is JsonArray entries)
            foreach (var entry in entries) if (entry is JsonValue stringValue && stringValue.TryGetValue<string>(out var text)) refs.Add(text);
            else if (entry is JsonObject obj) foreach (var key in new[] { "path", "file", "evidence" }) if (GetString(obj, key) is { } path) refs.Add(path);
        if (Number(state, "schema_version") >= 2 && manifest?["entries"] is not JsonArray) issues.Add("manifesto de evidências ausente ou inválido");
        foreach (var key in new[] { "build", "tests" })
        {
            var rows = state?[key] as JsonArray ?? [];
            if (rows.Count == 0) issues.Add($"{key} não registrado");
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is not JsonObject row) { issues.Add($"{key}[{i}] inválido"); continue; }
                var result = FirstString(row, "result", "status");
                if (string.IsNullOrWhiteSpace(result)) issues.Add($"{key}[{i}] sem resultado");
                if (FirstString(row, "expected", "criterio", "criteria") is null) issues.Add($"{key}[{i}] sem critério");
                if (FirstString(row, "obtained", "detail", "details", "resultado", "output") is null) issues.Add($"{key}[{i}] sem resultado observado");
                var evidence = EvidenceReferences(row);
                if (evidence.Count == 0) issues.Add($"{key}[{i}] sem evidência");
                var foundImage = false;
                foreach (var path in evidence)
                {
                    if (Number(state, "schema_version") >= 2 && !refs.Contains(path)) issues.Add($"evidência fora do manifesto: {path}");
                    var candidate = Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path);
                    var documents = Read(Path.Combine(_root, "config.json"))?["documents_root"]?.GetValue<string>() ?? "";
                    var documentDir = string.IsNullOrWhiteSpace(documents) ? "" : Path.Combine(documents, id.ToLowerInvariant());
                    if (!Inside(directory, candidate) && (documentDir.Length == 0 || !Inside(documentDir, candidate))) issues.Add($"evidência fora das pastas autorizadas: {path}");
                    else if (!File.Exists(candidate)) issues.Add($"evidência ausente: {path}");
                    else if (Regex.IsMatch(path, @"\.(?:png|jpe?g|gif)$", RegexOptions.IgnoreCase)) foundImage = true;
                }
                var isUi = Regex.IsMatch(string.Join(' ', GetString(row, "type") ?? "", GetString(row, "name") ?? "", GetString(row, "command") ?? ""), "tela|ui|manual|visual|interface|click|clique|screen|screenshot|print", RegexOptions.IgnoreCase);
                if (isUi && !foundImage) issues.Add($"{key}[{i}] teste de tela sem screenshot existente");
                if (Regex.IsMatch(result ?? "", @"^(?:falha|failed|erro|bloqueado|pendente|não executado|nao executado|inconclusivo|unknown)$", RegexOptions.IgnoreCase)) issues.Add($"{key}[{i}] resultado não aprovável: {result}");
            }
        }
        var review = state?["review"] as JsonObject;
        if (review is null || FirstString(review, "verdict", "resultado") is null) issues.Add("revisão final sem veredicto");
        var verdict = FirstString(review, "verdict", "resultado") ?? "";
        if (Regex.IsMatch(verdict, @"^(?:falha|failed|erro|bloqueado|pendente|não executado|nao executado|inconclusivo|unknown)$|bloque|pend|falh|fora do escopo não resolvido", RegexOptions.IgnoreCase)) issues.Add("revisão final não aprovada");
        var budget = state?["change_budget"] as JsonObject;
        var guard = state?["scope_guard"] as JsonObject;
        var hard = budget?["hard_stop"] as JsonObject ?? guard?["hard_stop"] as JsonObject;
        var approvedExpansion = GetBool(budget, "approved_expansion");
        var plans = state?["component_plans"] as JsonObject ?? new JsonObject();
        var repositories = new List<(JsonObject Record, string Id, JsonObject Plan, bool Primary)>();
        var primaryId = GetString(ticket, "repositoryId") ?? Path.GetFileName(GetString(ticket, "repository") ?? "");
        repositories.Add((ticket, primaryId, budget ?? new JsonObject(), true));
        foreach (var component in ticket["components"] as JsonArray ?? [])
        {
            if (component is not JsonObject record) continue;
            var componentId = GetString(record, "repositoryId") ?? "";
            repositories.Add((record, componentId, plans[componentId] as JsonObject ?? new JsonObject(), false));
        }
        var totalFiles = 0; var totalAdded = 0; var totalDeleted = 0;
        foreach (var repository in repositories)
        {
            var repoPlan = repository.Plan;
            var mode = repository.Primary ? "change" : GetString(repoPlan, "mode");
            if (!repository.Primary && mode is not ("change" or "read_only")) issues.Add($"{repository.Id}: componente sem modo change/read_only aprovado");
            if (!repository.Primary && mode == "change")
            {
                var repoTests = state?["tests"] as JsonArray ?? [];
                var repoBuild = state?["build"] as JsonArray ?? [];
                if (GetBool(repository.Record, "prepared") != true) issues.Add($"{repository.Id}: worktree não preparada");
                if (GetStringArray(repoPlan, "planned_files").Length == 0) issues.Add($"{repository.Id}: arquivos aprovados ausentes");
                if (!repoBuild.OfType<JsonObject>().Any(row => GetString(row, "repositoryId") == repository.Id)) issues.Add($"{repository.Id}: build não registrado");
                if (!repoTests.OfType<JsonObject>().Any(row => GetString(row, "repositoryId") == repository.Id)) issues.Add($"{repository.Id}: testes não registrados");
                if (review?["components"]?[repository.Id] is not JsonObject componentReview || FirstString(componentReview, "verdict", "resultado") is not { } componentVerdict)
                    issues.Add($"{repository.Id}: revisão não registrada");
                else if (Regex.IsMatch(componentVerdict, @"bloque|pend|falh|não executado|nao executado|inconclusivo", RegexOptions.IgnoreCase)) issues.Add($"{repository.Id}: revisão não aprovada");
            }
            if (!checkDiff) continue;
            var baseSha = repository.Primary ? FirstString(state, "base_sha") ?? GetString(repository.Record, "baseSha") : GetString(repository.Record, "baseSha");
            var diff = InspectDiff(repository.Record, baseSha, GetStringArray(repoPlan, "planned_files"));
            if (!diff.Inspected) issues.Add($"{repository.Id}: diff real não pôde ser conferido");
            else
            {
                if (mode == "read_only" && diff.Files > 0) issues.Add($"{repository.Id}: foram detectadas alterações em componente somente leitura");
                if (diff.Outside.Count > 0) issues.Add($"{repository.Id}: arquivos fora do plano: " + string.Join(", ", diff.Outside.Take(5)));
                totalFiles += diff.Files; totalAdded += diff.Added; totalDeleted += diff.Deleted;
            }
        }
        if (totalFiles > Number(hard, "max_files", double.PositiveInfinity)) issues.Add("diff total excede hard-stop de arquivos");
        if (totalAdded > Number(hard, "max_added_lines", double.PositiveInfinity)) issues.Add("diff total excede hard-stop de linhas adicionadas");
        if (totalDeleted > Number(hard, "max_deleted_lines", double.PositiveInfinity)) issues.Add("diff total excede hard-stop de linhas removidas");
        if (!approvedExpansion && guard is not null && totalFiles > Number(guard, "max_planned_files", double.PositiveInfinity)) issues.Add("diff total acima do scope_guard sem expansão aprovada");
        if (!approvedExpansion && guard is not null && totalAdded > Number(guard, "max_planned_added_lines", double.PositiveInfinity)) issues.Add("linhas adicionadas totais acima do scope_guard sem expansão aprovada");
        if (!approvedExpansion && guard is not null && totalDeleted > Number(guard, "max_planned_deleted_lines", double.PositiveInfinity)) issues.Add("linhas removidas totais acima do scope_guard sem expansão aprovada");
        return issues;
    }

    /// <summary>Uses the same fail-closed checks for the UI and the commit hook.</summary>
    public IReadOnlyList<string> CheckCommitQuality(JsonObject? state, JsonObject ticket, string id, bool checkDiff = false) => CommitQuality(state, ticket, id, checkDiff);

    private (bool Inspected, int Files, int Added, int Deleted, List<string> Outside) InspectDiff(JsonObject ticket, string? baseSha, string[] plannedFiles)
    {
        var worktree = GetString(ticket, "worktree");
        if (GetBool(ticket, "prepared") != true || string.IsNullOrWhiteSpace(worktree) || string.IsNullOrWhiteSpace(baseSha)) return (false, 0, 0, 0, []);
        try
        {
            var tracked = GitLines(worktree, "diff", "--name-only", baseSha, "--");
            var untracked = GitLines(worktree, "ls-files", "--others", "--exclude-standard");
            var files = tracked.Concat(untracked).Where(value => value.Length > 0).Select(value => value.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var planned = plannedFiles.Select(NormalizePlanPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outside = planned.Count == 0 ? [] : files.Where(file => !planned.Contains(NormalizePlanPath(file))).ToList();
            var added = 0; var deleted = 0;
            foreach (var row in GitLines(worktree, "diff", "--numstat", baseSha, "--"))
            {
                var fields = row.Split('\t'); if (fields.Length < 2) continue;
                if (int.TryParse(fields[0], out var a)) added += a;
                if (int.TryParse(fields[1], out var d)) deleted += d;
            }
            foreach (var file in untracked)
            {
                var full = Path.GetFullPath(Path.Combine(worktree, file));
                if (!Inside(worktree, full) || !File.Exists(full)) continue;
                var info = new FileInfo(full); if (info.Length > 2_000_000) continue;
                added += File.ReadAllLines(full).Length;
            }
            return (true, files.Length, added, deleted, outside);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.TimeoutException) { return (false, 0, 0, 0, []); }
    }

    private string[] GitLines(string worktree, params string[] args)
    {
        var git = Read(Path.Combine(_root, "config.json"))?["git_executable"]?.GetValue<string>() ?? "git";
        var start = new ProcessStartInfo(git) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = worktree };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(worktree);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Git não iniciou.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token); process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException(awaitText(error));
        return awaitText(output).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        static string awaitText(Task<string> task) => task.GetAwaiter().GetResult();
    }

    private static List<string> EvidenceReferences(JsonObject item)
    {
        var result = new List<string>();
        foreach (var key in new[] { "evidence", "evidencias", "evidence_files", "evidence_images", "screenshots", "screenshot", "log", "artifact", "output_file", "raw_output" })
        {
            if (item[key] is JsonValue scalar && scalar.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one)) result.Add(one.Trim());
            else if (item[key] is JsonArray values) result.AddRange(values.OfType<JsonValue>().Where(value => value.TryGetValue<string>(out _)).Select(value => value.GetValue<string>().Trim()).Where(value => value.Length > 0));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsReadCommand(string command)
    {
        var segments = new List<List<string>>(); var words = new List<string>(); var token = new StringBuilder(); var quote = '\0'; var started = false;
        void EndWord() { if (started) words.Add(token.ToString()); token.Clear(); started = false; }
        bool EndSegment() { EndWord(); if (words.Count == 0) return false; segments.Add(words); words = []; return true; }
        var source = command.Trim();
        for (var index = 0; index < source.Length; index++)
        {
            var c = source[index];
            if (quote != '\0') { if (c == quote) quote = '\0'; else { if (quote == '"' && c is '$' or '`') return false; token.Append(c); } continue; }
            if (c is '"' or '\'') { quote = c; started = true; continue; }
            if (c == '2' && !started)
            {
                var redirect = Regex.Match(source[index..], @"^2>\s*/dev/null(?=\s|$)");
                if (redirect.Success) { index += redirect.Length - 1; continue; }
            }
            if (c is '$' or '`' or '<' or '>' or '(' or ')' or '{' or '}' or '\\') return false;
            if (c is ';' or '|' or '&' or '\n')
            {
                if (c == '&' && (index + 1 >= source.Length || source[index + 1] != '&')) return false;
                if ((c is '&' or '|') && index + 1 < source.Length && source[index + 1] == c) index++;
                if (!EndSegment()) return false;
            }
            else if (char.IsWhiteSpace(c)) EndWord(); else { token.Append(c); started = true; }
        }
        if (quote != '\0' || !EndSegment()) return false;
        foreach (var segment in segments)
        {
            var exe = segment[0].ToLowerInvariant(); var args = segment.Skip(1).ToList();
            if (exe is "cat" or "ls" or "head" or "tail" or "wc" or "pwd" or "echo" or "cd" or "get-content" or "get-childitem" or "get-location" or "set-location" or "select-object" or "measure-object" or "grep" or "findstr") continue;
            if (exe == "sort" && args.All(arg => !Regex.IsMatch(arg, "^-o|^--output|^--compress-program"))) continue;
            if (exe == "rg" && args.All(arg => !Regex.IsMatch(arg, "^--pre(?:=|$)|^--hostname-bin(?:=|$)"))) continue;
            if (exe == "find" && args.All(arg => !Regex.IsMatch(arg, "^-(?:exec|execdir|ok|okdir|delete|fprint|fprintf|fls)"))) continue;
            if (exe != "git") return false;
            if (args.Count > 0 && args[0] == "-C") args = args.Skip(2).ToList();
            if (args.Count == 0) return false;
            var verb = args[0]; args = args.Skip(1).ToList();
            if (verb is not ("status" or "diff" or "log" or "show" or "rev-parse" or "ls-files" or "branch")) return false;
            if (verb == "branch" && !(args.Count == 1 && args[0] == "--show-current")) return false;
            if (args.Any(arg => Regex.IsMatch(arg, "^(?:--output|--ext-diff|--textconv|--no-index|--exec-path)(?:=|$)"))) return false;
            if (verb is "diff" or "show" or "log" && !(args.Contains("--no-ext-diff") && args.Contains("--no-textconv"))) return false;
        }
        return true;
    }

    private static JsonObject Allow(string reason) => new() { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "allow", ["permissionDecisionReason"] = reason } };
    private static JsonObject Deny(string reason) => new() { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = reason } };
    private static string ComponentPlansHash(JsonObject? state) => Hash(Encoding.UTF8.GetBytes((state?["component_plans"] as JsonObject ?? new JsonObject()).ToJsonString(CompactJson)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static string NormalizePlanPath(string value)
    {
        var normalized = Regex.Replace(value.Replace('\\', '/'), "/+", "/");
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimEnd('/').ToLowerInvariant();
    }
    private static bool Inside(string root, string target)
    {
        try { var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); var full = Path.GetFullPath(target); return full.Equals(basePath, StringComparison.OrdinalIgnoreCase) || full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.StartsWith(basePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
    private string StateFile(JsonObject ticket, string id)
    {
        var config = Read(Path.Combine(_root, "config.json"));
        var root = config?["state_base_root"]?.GetValue<string>() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
        var repository = GetString(ticket, "repositoryId") ?? Path.GetFileName(GetString(ticket, "repository") ?? "");
        var configured = Path.GetFileName((config?["repository"]?.GetValue<string>() ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = string.Equals(configured, repository, StringComparison.OrdinalIgnoreCase) ? configured : repository;
        return Path.Combine(root, folder.ToLowerInvariant(), id.ToLowerInvariant(), "estado.json");
    }
    private string TicketDir(string id) => Path.Combine(_root, "tickets", id.ToLowerInvariant());
    private static string[] CapturePathArguments(string input, string pattern) => Regex.Matches(input, pattern, RegexOptions.IgnoreCase).Select(match => match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value ?? "").ToArray();
    private static string? FirstString(JsonObject? obj, params string[] keys) { foreach (var key in keys) if (GetString(obj, key) is { } value) return value; return null; }
    private static string[] GetStringArray(JsonObject? obj, string key) => (obj?[key] as JsonArray ?? []).OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var value) ? value : "").ToArray();
    private static string? GetString(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private static int? IntValue(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
    private static bool GetBool(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static JsonObject? Read(string path) { try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } catch (JsonException) { throw new InvalidDataException($"Não foi possível ler {Path.GetFileName(path)}; arquivo preservado."); } }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    private static string ValidateId(string id) => Regex.IsMatch(id, "^[A-Z]{2,8}[0-9]{2,8}$", RegexOptions.IgnoreCase) ? id.ToUpperInvariant() : throw new InvalidDataException("Código de ticket inválido.");
}
