using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

public sealed class GitWorktreeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dataRoot;
    private readonly string _stateBaseRoot;
    private readonly string _worktreesBaseRoot;
    private readonly string _gitExecutable;
    private readonly CentralProfile _profile;

    public GitWorktreeService(string dataRoot, string stateBaseRoot, string worktreesBaseRoot, CentralProfile profile)
    {
        _dataRoot = dataRoot;
        _stateBaseRoot = stateBaseRoot;
        _worktreesBaseRoot = worktreesBaseRoot;
        _profile = profile;
        _gitExecutable = profile.GetString("git_executable") ?? "git";
    }

    public async Task<JsonObject> Prepare(string ticketId, CancellationToken cancellationToken)
    {
        var id = ValidateTicketId(ticketId);
        var recordPath = TicketFile(id);
        var record = ReadObject(recordPath) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        using var prepareLock = AcquirePrepareLock(id);
        record = ReadObject(recordPath) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        var components = record["components"] as JsonArray ?? [];
        await PrepareOne(record, recordPath, null, cancellationToken);
        for (var index = 0; index < components.Count; index++)
        {
            if (record["components"] is not JsonArray componentArray || componentArray[index] is not JsonObject component) continue;
            var componentRecord = (JsonObject)record.DeepClone();
            foreach (var pair in component) componentRecord[pair.Key] = pair.Value?.DeepClone();
            componentRecord["components"] = new JsonArray();
            var updated = await PrepareOne(componentRecord, recordPath, index, cancellationToken);
            if (updated["prepared"]?.GetValue<bool>() != true) throw new TicketOperationException($"Worktree de {component["repositoryId"]} não foi preparada.", 409);
        }
        return ReadObject(recordPath) ?? throw new TicketOperationException("O cadastro sumiu durante a preparação.", 409);
    }

    public async Task<object> Verify(JsonObject record, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(record);
        var root = WorktreeRoot(repository);
        var worktree = record["worktree"]?.GetValue<string>();
        var branch = record["branch"]?.GetValue<string>();
        var baseSha = record["baseSha"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(worktree) || string.IsNullOrWhiteSpace(branch)
            || !Inside(root, worktree) || !Directory.Exists(worktree))
            throw new TicketOperationException("Worktree ausente ou fora do diretório gerenciado. Nada foi sobrescrito.", 409);
        var common = await Git(worktree, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken);
        var mainCommon = await Git(repository.Path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken);
        var currentBranch = await Git(worktree, ["branch", "--show-current"], cancellationToken);
        var head = await Git(worktree, ["rev-parse", "HEAD"], cancellationToken);
        if (!Path.GetFullPath(common).Equals(Path.GetFullPath(mainCommon), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentBranch, branch, StringComparison.Ordinal))
            throw new TicketOperationException("A worktree não corresponde ao repositório e à branch deste ticket.", 409);
        return new { worktree, branch, baseSha, head };
    }

    public async Task<object> Preflight(string ticketId, CancellationToken cancellationToken)
    {
        var id = ValidateTicketId(ticketId);
        var record = ReadObject(TicketFile(id)) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        var blocking = new List<string>();
        var warnings = new List<string>();
        var repository = RepositoryFor(record);
        if (!Directory.Exists(Path.Combine(repository.Path, ".git")) && !File.Exists(Path.Combine(repository.Path, ".git")))
            blocking.Add("repositório Git não encontrado");
        var agent = record["agent"]?.GetValue<string>() ?? "";
        if (agent is not ("claude" or "codex")) blocking.Add("agente inválido");
        else if (!File.Exists(_profile.AgentExecutable(agent))) blocking.Add($"{agent} não localizado");

        var referenceRoot = _profile.GetString("references_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "Ref Dev");
        if (record["attachments"] is JsonArray attachments)
        {
            var missing = attachments.Select(item => item?.GetValue<string>() ?? "")
                .Where(name => name.Length > 0 && !File.Exists(Path.Combine(referenceRoot, id.ToLowerInvariant(), name))).ToArray();
            if (missing.Length > 0) blocking.Add($"anexos ausentes: {string.Join(", ", missing)}");
        }
        if (record["prepared"]?.GetValue<bool>() == true)
        {
            try { await Verify(record, cancellationToken); }
            catch (TicketOperationException error) { blocking.Add("worktree principal: " + error.Message); }
        }
        else blocking.Add("worktree principal ainda não preparada");

        if (record["components"] is JsonArray components)
        {
            foreach (var componentNode in components)
            {
                if (componentNode is not JsonObject component) continue;
                var repositoryId = component["repositoryId"]?.GetValue<string>() ?? "componente";
                if (component["prepared"]?.GetValue<bool>() != true)
                {
                    blocking.Add($"{repositoryId}: worktree complementar não preparada");
                    continue;
                }
                var componentRecord = (JsonObject)record.DeepClone();
                foreach (var pair in component) componentRecord[pair.Key] = pair.Value?.DeepClone();
                try { await Verify(componentRecord, cancellationToken); }
                catch (TicketOperationException error) { blocking.Add($"{repositoryId}: {error.Message}"); }
            }
        }

        var stateDirectory = StateRoot(repository);
        var stateFile = Path.Combine(stateDirectory, id.ToLowerInvariant(), "estado.json");
        if (!File.Exists(stateFile)
            || !File.Exists(Path.Combine(stateDirectory, id.ToLowerInvariant(), "test-matrix.json"))
            || !File.Exists(Path.Combine(stateDirectory, id.ToLowerInvariant(), "evidence-manifest.json")))
            blocking.Add("arquivos de controle de qualidade ausentes");
        if (string.IsNullOrWhiteSpace(_profile.GetString("repository"))) blocking.Add("perfil sem repositório padrão");
        if (string.IsNullOrWhiteSpace(_profile.GetString("ai_memory_url"))) warnings.Add("AI Memory usa o endereço local padrão; falhas serão preservadas no outbox.");
        return new { ok = blocking.Count == 0, blocking, warnings, checked_at = DateTimeOffset.UtcNow.ToString("O") };
    }

    public async Task<object> Diff(string ticketId, CancellationToken cancellationToken)
    {
        var id = ValidateTicketId(ticketId);
        var record = ReadObject(TicketFile(id)) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        await Verify(record, cancellationToken);
        var worktree = record["worktree"]!.GetValue<string>();
        var branch = record["branch"]!.GetValue<string>();
        var baseSha = record["baseSha"]!.GetValue<string>();
        var status = await Git(worktree, ["status", "--short"], cancellationToken);
        var diff = await Git(worktree, ["diff", "--no-ext-diff", "--no-textconv", baseSha, "--", "."], cancellationToken);
        var text = $"Branch: {branch}\nBase: {baseSha}\n\nArquivos (inclui não rastreados):\n{(string.IsNullOrWhiteSpace(status) ? "(limpo)" : status)}\n\nDiff dos arquivos rastreados:\n{(string.IsNullOrWhiteSpace(diff) ? "(sem diferenças rastreadas)" : diff)}";
        return new { text = Redact(text) };
    }

    private async Task<JsonObject> PrepareOne(JsonObject record, string recordPath, int? componentIndex, CancellationToken cancellationToken)
    {
        var repo = RepositoryFor(record);
        var repoPath = repo.Path;
        var stateRoot = StateRoot(repo);
        var worktreeRoot = WorktreeRoot(repo);
        if (!Directory.Exists(Path.Combine(repoPath, ".git")) && !File.Exists(Path.Combine(repoPath, ".git")))
            throw new TicketOperationException($"O repositório {repo.Id} não existe ou não é um clone Git.", 409);

        if (record["prepared"]?.GetValue<bool>() == true)
        {
            await Verify(record, cancellationToken);
            EnsureState(record, stateRoot);
            return record;
        }

        if (record["preparation"] is JsonObject prior && prior["worktree"]?.GetValue<string>() is { } priorWorktree && Directory.Exists(priorWorktree))
        {
            var recovered = (JsonObject)record.DeepClone();
            recovered["branch"] = prior["branch"]?.DeepClone();
            recovered["worktree"] = prior["worktree"]?.DeepClone();
            recovered["baseSha"] = prior["base"]?.DeepClone();
            recovered["prepared"] = true;
            var verified = await Verify(recovered, cancellationToken);
            var head = verified.GetType().GetProperty("head")?.GetValue(verified)?.ToString();
            var baseCommit = prior["base"]?.GetValue<string>();
            if (!string.Equals(head, baseCommit, StringComparison.OrdinalIgnoreCase))
                throw new TicketOperationException("A preparação interrompida contém commits posteriores. Confira o trabalho existente antes de recuperar.", 409);
            SavePrepared(recordPath, recovered, componentIndex);
            EnsureState(recovered, stateRoot);
            AppendEvent(record["id"]!.GetValue<string>(), "recuperacao", "Preparação interrompida recuperada após conferir worktree, branch e commit de origem.");
            return recovered;
        }

        var release = record["release"]?.GetValue<string>() ?? "";
        var branchType = record["branch_type"]?.GetValue<string>() ?? "feature";
        var sprint = record["sprint"]?.GetValue<string>() ?? "";
        var id = record["id"]?.GetValue<string>() ?? "";
        if (branchType is not ("feature" or "hotfix") || !Regex.IsMatch(sprint, "^[1-9][0-9]{0,5}$"))
            throw new TicketOperationException("Tipo de branch ou sprint inválidos no cadastro.");
        await Git(repoPath, ["check-ref-format", "--branch", release], cancellationToken);
        var branch = $"{branchType}/{sprint}/{id.ToLowerInvariant()}";
        var worktree = Path.Combine(worktreeRoot, $"{sprint}-{id.ToLowerInvariant()}");
        Directory.CreateDirectory(worktreeRoot);
        if (!Inside(worktreeRoot, worktree) || Directory.Exists(worktree) || File.Exists(worktree))
            throw new TicketOperationException("A pasta prevista para este ticket já existe. Nada foi substituído.", 409);

        var branches = await Git(repoPath, ["for-each-ref", "--format=%(refname:short)", $"refs/heads/{branch}"], cancellationToken);
        if (branches.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains(branch, StringComparer.Ordinal))
            throw new TicketOperationException("A branch já existe fora deste cadastro. Retome o trabalho original antes de iniciar outro.", 409);
        var remotesText = await Git(repoPath, ["remote"], cancellationToken);
        var remotes = remotesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var remote = remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes.Length == 1 ? remotes[0] : null;
        if (remote is null || !Regex.IsMatch(remote, "^[a-zA-Z0-9_-]+$"))
            throw new TicketOperationException("Não foi possível identificar um único remoto para a origem da branch.", 409);

        AppendEvent(id, "preparacao", $"Atualizando {release} e preparando worktree exclusiva.");
        await Git(repoPath, ["fetch", "--no-tags", remote, $"{release}:refs/remotes/{remote}/{release}"], cancellationToken);
        var refName = $"refs/remotes/{remote}/{release}";
        var baseSha = await Git(repoPath, ["rev-parse", "--verify", refName], cancellationToken);
        if (!Regex.IsMatch(baseSha, "^[a-f0-9]{40,64}$", RegexOptions.IgnoreCase))
            throw new TicketOperationException("O Git não retornou um commit válido para a origem.", 409);

        record["preparation"] = new JsonObject { ["branch"] = branch, ["worktree"] = worktree, ["base"] = baseSha, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        SavePrepared(recordPath, record, componentIndex);
        await Git(repoPath, ["worktree", "add", "--no-track", "-b", branch, worktree, baseSha], cancellationToken);
        record["prepared"] = true;
        record["branch"] = branch;
        record["worktree"] = worktree;
        record["baseSha"] = baseSha;
        SavePrepared(recordPath, record, componentIndex);
        EnsureState(record, stateRoot);
        AppendEvent(id, "preparacao", "Worktree criada. O checkout principal foi preservado.");
        return record;
    }

    private void SavePrepared(string recordPath, JsonObject changed, int? componentIndex)
    {
        var whole = ReadObject(recordPath) ?? throw new TicketOperationException("Cadastro de ticket não encontrado.", 404);
        if (componentIndex is null)
        {
            foreach (var key in new[] { "prepared", "preparation", "branch", "worktree", "baseSha" }) whole[key] = changed[key]?.DeepClone();
        }
        else
        {
            var components = whole["components"] as JsonArray ?? throw new TicketOperationException("A lista de repositórios complementares foi alterada.", 409);
            if (componentIndex >= components.Count || components[componentIndex.Value] is not JsonObject component) throw new TicketOperationException("Componente do ticket não encontrado.", 409);
            foreach (var key in new[] { "prepared", "preparation", "branch", "worktree", "baseSha" }) component[key] = changed[key]?.DeepClone();
        }
        whole["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        WriteJson(recordPath, whole);
    }

    private void EnsureState(JsonObject record, string stateRoot)
    {
        var id = record["id"]!.GetValue<string>().ToLowerInvariant();
        var directory = Path.Combine(stateRoot, id);
        var stateFile = Path.Combine(directory, "estado.json");
        Directory.CreateDirectory(Path.Combine(directory, "scratch", "evidence"));
        if (!File.Exists(stateFile))
        {
            var delivery = record["delivery"]?.GetValue<string>() == "local_commit_only";
            WriteJson(stateFile, new JsonObject
            {
                ["schema_version"] = 2, ["ticket"] = record["id"]?.DeepClone(), ["sprint"] = record["sprint"]?.DeepClone(),
                ["repository"] = record["repository"]?.DeepClone(), ["worktree"] = record["worktree"]?.DeepClone(),
                ["branch"] = record["branch"]?.DeepClone(), ["base_ref"] = record["release"]?.DeepClone(), ["base_sha"] = record["baseSha"]?.DeepClone(),
                ["stage"] = "analise", ["scope_revision"] = 1, ["implementation_approval"] = null, ["approved_scope_sha256"] = null,
                ["active_session"] = record["sessionId"]?.DeepClone(), ["integration_branch"] = record["integration_branch"]?.DeepClone(),
                ["delivery"] = new JsonObject { ["commit_allowed"] = delivery, ["push_allowed"] = false, ["pr_allowed"] = false },
                ["build"] = new JsonArray(), ["tests"] = new JsonArray(), ["review"] = null,
                ["resources_created"] = new JsonObject { ["databases"] = new JsonArray(), ["backups"] = new JsonArray(), ["scratch_files"] = new JsonArray() },
                ["scope_guard"] = new JsonObject { ["default_mode"] = "minimal_patch", ["max_planned_files"] = 6, ["max_planned_added_lines"] = 180, ["max_planned_deleted_lines"] = 120, ["max_correction_rounds"] = 1, ["new_projects_or_tables_require_reapproval"] = true },
                ["change_budget"] = new JsonObject { ["planned_files"] = new JsonArray(), ["estimated_added_lines"] = null, ["estimated_deleted_lines"] = null, ["approved_expansion"] = false, ["correction_rounds"] = 0 },
                ["blockers"] = new JsonArray(), ["next_action"] = "Provar a menor correção, registrar o plano mínimo e aguardar aprovação.", ["updated_at"] = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        WriteJson(Path.Combine(directory, "test-matrix.json"), ReadObject(Path.Combine(directory, "test-matrix.json")) ?? new JsonObject
        {
            ["schema_version"] = 1, ["ticket"] = record["id"]?.DeepClone(), ["criteria"] = new JsonArray(),
            ["instruction"] = "Uma entrada por critério/variante; registre esperado, obtido, resultado e evidência.", ["updated_at"] = DateTimeOffset.UtcNow.ToString("O")
        });
        WriteJson(Path.Combine(directory, "evidence-manifest.json"), ReadObject(Path.Combine(directory, "evidence-manifest.json")) ?? new JsonObject
        {
            ["schema_version"] = 1, ["ticket"] = record["id"]?.DeepClone(), ["entries"] = new JsonArray(),
            ["instruction"] = "Liste somente evidências existentes, com caminho relativo ao estado ou documento.", ["updated_at"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private CentralRepository RepositoryFor(JsonObject record)
    {
        var value = record["repository"]?.GetValue<string>() ?? record["repositoryId"]?.GetValue<string>() ?? "";
        return _profile.ResolveRepository(value) ?? throw new TicketOperationException("Repositório registrado não está mais disponível ou não é confiável.", 409);
    }
    private string StateRoot(CentralRepository repo) => Path.Combine(_stateBaseRoot, RepoSlug(repo));
    private string WorktreeRoot(CentralRepository repo) => Path.Combine(_worktreesBaseRoot, RepoSlug(repo));
    private static string RepoSlug(CentralRepository repo) => Path.GetFileName(Path.TrimEndingDirectorySeparator(repo.Path)).ToLowerInvariant();
    private string TicketFile(string id) => Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant(), "ticket.json");
    private string EventFile(string id) => Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant(), "events.jsonl");
    private void AppendEvent(string id, string type, string message) => File.AppendAllText(EventFile(id), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }, JsonOptions) + Environment.NewLine);
    private PrepareFileLock AcquirePrepareLock(string id)
    {
        var path = Path.Combine(Path.GetDirectoryName(TicketFile(id))!, ".prepare.lock");
        try { return new PrepareFileLock(path); }
        catch (IOException) { throw new TicketOperationException("Outra preparação deste ticket está em andamento.", 409); }
    }

    private async Task<string> Git(string repository, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_gitExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repository };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(repository);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new TicketOperationException("Não foi possível iniciar o Git configurado.", 409); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TicketOperationException("O comando Git excedeu o limite de 2 minutos.", 504);
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new TicketOperationException($"Git {string.Join(' ', args)}: {Redact(stderr.Length > 0 ? stderr : stdout)}", 409);
        return stdout.Trim();
    }

    private static bool Inside(string root, string target)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetPath = Path.GetFullPath(target);
        return targetPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || targetPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }
    private static string ValidateTicketId(string value)
    {
        var id = value.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(id, "^[A-Z]{2,8}[0-9]{2,8}$")) throw new TicketOperationException("Código de ticket inválido.");
        return id;
    }
    private static string Redact(string text)
    {
        var safe = Regex.Replace(text, @"https?://[^\s/@]+:[^\s/@]+@", "[url-credencial]@");
        return string.Join("\n", safe.Split('\n').Where(line => !Regex.IsMatch(line, @"password\s*[=:]|pwd\s*=|api[_-]?key\s*[=:]|access[_-]?token\s*[=:]|connectionString\s*=", RegexOptions.IgnoreCase)).TakeLast(12));
    }
    private static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new TicketOperationException($"Não foi possível ler {Path.GetFileName(path)}; o arquivo foi preservado.", 409); }
    }
    private static void WriteJson(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, value.ToJsonString(JsonOptions) + Environment.NewLine); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class PrepareFileLock(string path) : IDisposable
{
    private readonly FileStream _stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    public void Dispose() => _stream.Dispose();
}
