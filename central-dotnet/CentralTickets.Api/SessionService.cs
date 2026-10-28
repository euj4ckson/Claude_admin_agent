using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CentralTickets.Api;

/// <summary>Owns session reservation and launch orchestration for the .NET Central.</summary>
public sealed class SessionService(string dataRoot, CentralProfile profile, GitWorktreeService worktrees)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, byte> RecoveredMonitors = new(StringComparer.Ordinal);
    private readonly string _dataRoot = dataRoot;
    private readonly CentralProfile _profile = profile;
    private readonly GitWorktreeService _worktrees = worktrees;

    public async Task<object> Start(string ticketId, CancellationToken cancellationToken, bool freshSession = false)
    {
        var id = ValidateId(ticketId);
        var config = ReadObject(Path.Combine(_dataRoot, "config.json"));
        if (BoolValue(config, "demo")) throw new TicketOperationException("A simulação não pode iniciar sessões reais.", 409);
        var dir = Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant());
        var ticketPath = Path.Combine(dir, "ticket.json");
        var ticket = ReadObject(ticketPath) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        using var gate = Acquire(Path.Combine(dir, ".session.lock"));
        ticket = ReadObject(ticketPath) ?? throw new TicketOperationException("Ticket não encontrado.", 404);
        var old = ReadObject(Path.Combine(dir, "runtime.json"));
        var active = ReadObject(Path.Combine(dir, "active.lock"));
        var starting = StringValue(old, "phase") == "starting"
            && StringValue(old, "runId") == StringValue(active, "runId")
            && DateTimeOffset.TryParse(StringValue(old, "startedAt"), out var startedAt)
            && DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(60);
        if (starting || IsAlive(IntValue(old, "pid")))
            return new { id, alreadyRunning = true, focused = FocusWindow(id), message = "A conversa existente foi localizada; nenhuma sessão duplicada foi aberta." };

        var agent = StringValue(ticket, "agent") ?? "claude";
        var executable = _profile.AgentExecutable(agent);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new TicketOperationException($"{agent} não está instalado ou configurado.", 409);

        if (IsSameProcess(IntValue(old, "childPid"), executable))
        {
            var recoveredRunId = StringValue(old, "runId");
            if (!Guid.TryParse(recoveredRunId, out _))
                throw new TicketOperationException("O processo do agente está ativo, mas não tem uma reserva de sessão recuperável.", 409);
            var heartbeat = DateTimeOffset.UtcNow.ToString("O");
            var recovered = (JsonObject)old!.DeepClone();
            recovered["phase"] = "running";
            recovered["heartbeat"] = heartbeat;
            recovered["pid"] = null;
            recovered.Remove("error");
            WriteJson(Path.Combine(dir, "runtime.json"), recovered);
            WriteJson(Path.Combine(dir, "active.lock"), new JsonObject
            {
                ["runId"] = recoveredRunId,
                ["startedAt"] = StringValue(recovered, "startedAt") ?? heartbeat,
                ["childPid"] = IntValue(recovered, "childPid")
            });
            MonitorRecoveredProcess(dir, recoveredRunId, IntValue(recovered, "childPid")!.Value);
            AppendEvent(dir, "recuperacao", "Reserva restaurada para o processo do agente que continuava ativo; nenhuma sessão duplicada foi aberta.");
            return new { id, alreadyRunning = true, recovered = true, focused = FocusWindow(id), message = "A conversa ativa foi recuperada e a reserva restaurada; nenhuma sessão duplicada foi aberta." };
        }

        var preparedAutomatically = !BoolValue(ticket, "prepared");
        if (preparedAutomatically)
        {
            await _worktrees.Prepare(id, cancellationToken);
            ticket = ReadObject(ticketPath) ?? throw new TicketOperationException("O cadastro do ticket sumiu após preparar a worktree.", 409);
        }

        var detail = await _worktrees.Preflight(id, cancellationToken);
        using var preflight = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        if (!preflight.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var blockers = preflight.RootElement.TryGetProperty("blocking", out var blocking)
                && blocking.ValueKind == JsonValueKind.Array
                ? string.Join("; ", blocking.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
                : "motivo não informado";
            throw new TicketOperationException($"Pré-flight bloqueado: {blockers}", 409);
        }

        if (freshSession)
        {
            if (agent != "claude") throw new TicketOperationException("Sessão nova com checkpoint está disponível para Claude Code.", 409);
            var stateRoot = _profile.GetString("state_base_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
            var stateDir = Path.Combine(stateRoot, StringValue(ticket, "repositoryId") ?? "", id.ToLowerInvariant());
            var statePath = Path.Combine(stateDir, "estado.json");
            var state = ReadObject(statePath) ?? throw new TicketOperationException("Estado do ticket ausente; inicie a análise pela conversa normal.", 409);
            if (!string.Equals(StringValue(state, "ticket"), id, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(StringValue(state, "id"), id, StringComparison.OrdinalIgnoreCase))
                throw new TicketOperationException("Estado do ticket não corresponde ao cadastro.", 409);
            var stage = StringValue(state, "stage") ?? "analise";
            if (stage != "analise" && !File.Exists(Path.Combine(stateDir, "escopo.md")))
                throw new TicketOperationException("Plano do ticket ausente; não é seguro abrir uma sessão nova sem ele.", 409);
            var nextAction = Regex.Replace(StringValue(state, "next_action") ?? "consultar estado.json", @"\s+", " ").Trim();
            if (nextAction.Length > 600) nextAction = nextAction[..600] + "… (leia o restante no estado.json)";
            var checkpoint = $"# Retomada de {id}\n\nEtapa registrada: {stage}.\nPróxima ação registrada: {nextAction}.\n\nLeia estado.json, escopo.md, test-matrix.json e evidence-manifest.json se existirem em {stateDir}. Confira worktree, branch, HEAD, diff e aprovação vigente antes de agir. Preserve resultados e pendências; não repita testes já comprovados salvo se o código ou ambiente relevante mudou. Não trate este checkpoint como nova aprovação.\n";
            File.WriteAllText(Path.Combine(dir, "session-checkpoint.md"), checkpoint, new System.Text.UTF8Encoding(false));
            var previousSessionId = StringValue(ticket, "sessionId");
            var ids = ticket["sessionIds"] as JsonArray;
            if (ids is null) ticket["sessionIds"] = ids = new JsonArray();
            if (!string.IsNullOrWhiteSpace(previousSessionId) && !ids.Any(item => item?.GetValue<string>() == previousSessionId)) ids.Add(previousSessionId);
            var newSessionId = Guid.NewGuid().ToString();
            ids.Add(newSessionId);
            ticket["sessionId"] = newSessionId;
            ticket["freshSessionId"] = newSessionId;
            ticket["sessionStarted"] = false;
            ticket["firstPromptSubmitted"] = false;
            ticket.Remove("firstPromptSubmittedAt");
            WriteJson(ticketPath, ticket);
        }

        var runId = Guid.NewGuid().ToString();
        var started = DateTimeOffset.UtcNow.ToString("O");
        var runtime = new JsonObject { ["runId"] = runId, ["phase"] = "starting", ["startedAt"] = started, ["heartbeat"] = started };
        WriteJson(Path.Combine(dir, "runtime.json"), runtime);
        WriteJson(Path.Combine(dir, "active.lock"), new JsonObject { ["runId"] = runId, ["startedAt"] = started });
        try
        {
            WriteSessionFiles(ticket, dir, id, freshSession);
            var script = Path.Combine(_profile.GetString("appRoot") ?? "", "central-dotnet", "session-runner.ps1");
            if (!File.Exists(script)) throw new TicketOperationException("O launcher .NET da sessão ainda não foi instalado.", 409);
            var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (!File.Exists(terminal)) terminal = "wt.exe";
            var launch = new ProcessStartInfo(terminal) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-w", "0", "new-tab", "--title", $"{id} - Central", "powershell.exe", "-NoProfile", "-NoExit", "-File", script, _dataRoot, id, runId }) launch.ArgumentList.Add(arg);
            using var process = Process.Start(launch) ?? throw new TicketOperationException("Não foi possível abrir o terminal da conversa.", 409);
            AppendEvent(dir, "abertura", "Janela do agente solicitada pela Central .NET.");
            var message = freshSession
                ? "Nova conversa do mesmo ticket abrindo com checkpoint; aprovação e evidências preservadas."
                : preparedAutomatically
                ? "Worktree preparada automaticamente; a janela do agente está abrindo."
                : "A janela do agente está abrindo.";
            return new { id, runId, preparedAutomatically, message };
        }
        catch
        {
            TryDelete(Path.Combine(dir, "active.lock"));
            WriteJson(Path.Combine(dir, "runtime.json"), new JsonObject { ["runId"] = runId, ["phase"] = "error", ["error"] = "Falha ao iniciar a sessão.", ["endedAt"] = DateTimeOffset.UtcNow.ToString("O") });
            throw;
        }
    }

    private void WriteSessionFiles(JsonObject ticket, string dir, string id, bool freshSession)
    {
        var stateRoot = _profile.GetString("state_base_root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
        var repositoryId = StringValue(ticket, "repositoryId") ?? Path.GetFileName(StringValue(ticket, "repository") ?? "");
        var stateDir = Path.Combine(stateRoot, repositoryId, id.ToLowerInvariant());
        var scope = StringValue(ticket, "scope") ?? "";
        var title = StringValue(ticket, "title") ?? id;
        var worktree = StringValue(ticket, "worktree") ?? "";
        var branch = StringValue(ticket, "branch") ?? "";
        var sessionId = StringValue(ticket, "sessionId") ?? Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(dir, "entrada.md"), $"# {id} — {title}\n\nTicket: {id}\n\n## Escopo funcional\n\n{scope}\n", new System.Text.UTF8Encoding(false));
        var skill = _profile.GetString("skill") ?? "";
        var contexts = $"Esta sessão pertence ao ticket {id}. Leia integralmente {Path.Combine(dir, "entrada.md")}, {Path.Combine(dir, "components.json")} e {Path.Combine(stateDir, "estado.json")}. Use o skill de desenvolvimento em {skill}; aplique apenas as regras de domínio relevantes do repositório.\n" +
            $"Use a worktree existente {worktree}, branch {branch}, base registrada no ticket; não crie outra. Em multirrepositório, respeite o modo change/read_only de cada componente.\n" +
            $"Comece pela análise: registre em escopo.md o comportamento atual, causa com referência concreta, patch mínimo, arquivos/métodos, estimativa, critérios/testes, limites negativos e fora de escopo. Confira que o estado e o manifesto de arquivos coincidem. Aguarde APROVAR {id} antes de alterar código. Se o plano/estimativa mudar, pare, atualize a revisão e aguarde nova aprovação.\n" +
            $"Antes de editar, compare caminhos equivalentes, rotinas paralelas e transições de estado; preserve o que funciona. Não expanda escopo nem corrija defeito preexistente sem autorização.\n" +
            $"Teste cada critério positivo e negativo, execute build/testes de forma síncrona e em ambiente isolado. Registre comando, ambiente, esperado/obtido, resultado e evidência real por critério. Para testes de tela, use a ferramenta central-computer se disponível, capture antes/ação/resultado e anexe ao documento; se indisponível, marque NÃO EXECUTADO, nunca invente evidência.\n" +
            $"Antes da entrega, releia o escopo e o diff completo, rode diff --check, procure arquivos fora do plano e faça revisão de regressão. Não marque concluído com falhas ou testes pendentes sem declarar bloqueio. Nunca edite cadastro, components.json, approval.json, active.lock ou scripts da Central. Não faça push nem PR. Entrega configurada: {StringValue(ticket, "delivery")}.\n";
        if (freshSession)
            contexts = $"Esta é uma sessão nova do ticket {id}. Leia primeiro {Path.Combine(dir, "session-checkpoint.md")} e o estado do ticket. Use a worktree {worktree} na branch {branch}. Confira aprovação, diff, testes e pendências antes de agir. Use o skill em {skill} e apenas as regras de domínio pertinentes. Retome da etapa registrada; não reinicie a análise nem repita testes comprovados sem mudança relevante. Preserve evidências e limites do plano. Nunca faça push ou PR sem pedido explícito.\n";
        File.WriteAllText(Path.Combine(dir, "session-context.txt"), contexts, new System.Text.UTF8Encoding(false));
        var componentManifest = new JsonArray();
        foreach (var item in new[] { ticket }.Concat((ticket["components"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            var component = new JsonObject();
            foreach (var key in new[] { "repositoryId", "repository", "worktree", "branch", "baseSha", "release", "integration_branch" })
                if (item[key] is { } value) component[key] = value.DeepClone();
            componentManifest.Add(component);
        }
        WriteJson(Path.Combine(dir, "components.json"), componentManifest);
        WriteJson(Path.Combine(dir, "session-settings.json"), CreateSessionSettings());
        var dotnet = _profile.GetString("dotnet") ?? "dotnet";
        var appRoot = _profile.GetString("appRoot") ?? "";
        var computerServerPath = Path.Combine(appRoot, "central-dotnet", "CentralTickets.ComputerMcp", "bin", "Release", "net10.0-windows", "CentralTickets.ComputerMcp.dll");
        if (!File.Exists(computerServerPath)) computerServerPath = Path.Combine(appRoot, "central-dotnet", "CentralTickets.ComputerMcp", "bin", "Debug", "net10.0-windows", "CentralTickets.ComputerMcp.dll");
        if (!File.Exists(computerServerPath)) throw new TicketOperationException("Servidor Computer Use .NET não compilado. Compile a Central antes de iniciar sessões com testes visuais.", 409);
        var computerServer = new JsonObject
        {
            ["command"] = dotnet,
            ["args"] = new JsonArray(computerServerPath),
            ["env"] = new JsonObject { ["CENTRAL_TICKET_ID"] = id, ["CENTRAL_TICKET_EVIDENCE"] = Path.Combine(stateDir, "scratch", "evidence") }
        };
        WriteJson(Path.Combine(dir, "mcp-servers.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["central-computer"] = computerServer } });
        var pluginRoot = Path.Combine(appRoot, "central-dotnet", "CentralTickets.Plugin");
        if (!File.Exists(Path.Combine(pluginRoot, ".claude-plugin", "plugin.json"))
            || !File.Exists(Path.Combine(pluginRoot, "hooks", "hooks.json")))
            throw new TicketOperationException("Plugin de hooks C# não encontrado. Compile/instale a Central .NET antes de iniciar sessões.", 409);
        var hookDll = Path.Combine(appRoot, "central-dotnet", "CentralTickets.Hook", "bin", "Release", "net10.0", "CentralTickets.Hook.dll");
        if (!File.Exists(hookDll)) hookDll = Path.Combine(appRoot, "central-dotnet", "CentralTickets.Hook", "bin", "Debug", "net10.0", "CentralTickets.Hook.dll");
        if (!File.Exists(hookDll)) throw new TicketOperationException("Hook C# nÃ£o compilado. Execute instalar-central.cmd antes de iniciar a sessÃ£o.", 409);
        var documentsRoot = _profile.GetString("documents_root");
        if (!string.IsNullOrWhiteSpace(documentsRoot)) Directory.CreateDirectory(Path.Combine(documentsRoot, id.ToLowerInvariant()));
        ticket["sessionId"] = sessionId;
        var ids = ticket["sessionIds"] as JsonArray;
        if (ids is null) ticket["sessionIds"] = ids = new JsonArray();
        if (!ids.Any(item => item?.GetValue<string>() == sessionId)) ids.Add(sessionId);
        WriteJson(Path.Combine(dir, "ticket.json"), ticket);
    }

    public static JsonObject CreateSessionSettings() => new()
    {
        ["permissions"] = new JsonObject
        {
            ["allow"] = new JsonArray(
                "mcp__central-computer__computer_screenshot",
                "mcp__central-computer__computer_active_window",
                "mcp__central-computer__computer_click",
                "mcp__central-computer__computer_type",
                "mcp__central-computer__computer_key",
                "mcp__central-computer__computer_wait",
                "mcp__central-computer__computer_wait_for_title",
                "mcp__central-computer__computer_sequence"),
            ["deny"] = new JsonArray(
                "Bash(git push *)",
                "Bash(git -C * push *)",
                "PowerShell(git push *)")
        }
    };

    public object Focus(string ticketId)
    {
        var id = ValidateId(ticketId);
        var runtime = ReadObject(Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant(), "runtime.json"));
        var phase = StringValue(runtime, "phase");
        if (phase is not ("starting" or "running")) throw new TicketOperationException("A conversa não está ativa. Use Retomar conversa.", 409);
        var focused = FocusWindow(id);
        return new { id, focused, message = focused ? "Conversa em primeiro plano." : $"A sessão de {id} está ativa; localize a janela correspondente na barra de tarefas. Nenhuma sessão duplicada foi criada." };
    }

    private static FileStream Acquire(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new TicketOperationException("Outra ação está preparando ou iniciando este ticket. Tente novamente em instantes.", 409); }
    }
    private static void WriteJson(string path, JsonNode node)
    {
        SharedFileAccess.WriteAllTextAtomically(path, node.ToJsonString(JsonOptions));
    }
    private static void AppendEvent(string dir, string type, string message) => File.AppendAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }, JsonOptions) + Environment.NewLine);
    private static JsonObject? ReadObject(string path) { try { return JsonNode.Parse(SharedFileAccess.ReadAllText(path)) as JsonObject; } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } catch (JsonException) { throw new TicketOperationException("Cadastro ou runtime inválido; os arquivos foram preservados.", 409); } }
    private static string? StringValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool BoolValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static int? IntValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    private static bool IsAlive(int? pid) { if (pid is null or <= 0) return false; try { using var process = Process.GetProcessById(pid.Value); return !process.HasExited; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } }
    private static bool IsSameProcess(int? pid, string executable)
    {
        if (pid is null or <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (process.HasExited || string.IsNullOrWhiteSpace(process.MainModule?.FileName)) return false;
            return string.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private static void MonitorRecoveredProcess(string directory, string runId, int processId)
    {
        if (!RecoveredMonitors.TryAdd(runId, 0)) return;
        _ = Task.Run(async () =>
        {
            var runtimePath = Path.Combine(directory, "runtime.json");
            var lockPath = Path.Combine(directory, "active.lock");
            try
            {
                using var process = Process.GetProcessById(processId);
                while (!process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    if (process.HasExited) break;
                    var current = ReadObject(runtimePath);
                    if (StringValue(current, "runId") != runId || StringValue(current, "phase") != "running") return;
                    current!["heartbeat"] = DateTimeOffset.UtcNow.ToString("O");
                    WriteJson(runtimePath, current);
                }

                var final = ReadObject(runtimePath);
                if (StringValue(final, "runId") != runId) return;
                final!["phase"] = process.ExitCode == 0 ? "stopped" : "error";
                final["exitCode"] = process.ExitCode;
                final["endedAt"] = DateTimeOffset.UtcNow.ToString("O");
                if (process.ExitCode != 0) final["error"] = $"O agente encerrou com codigo {process.ExitCode}.";
                else final.Remove("error");
                WriteJson(runtimePath, final);
                if (StringValue(ReadObject(lockPath), "runId") == runId) File.Delete(lockPath);
                AppendEvent(directory, "sessao", $"Processo recuperado encerrado ({process.ExitCode}); trabalho preservado.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AppendEvent(directory, "monitoramento", "Falha ao acompanhar a sessao recuperada: " + error.Message);
            }
            finally { RecoveredMonitors.TryRemove(runId, out _); }
        });
    }
    private static bool FocusWindow(string id)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var title = new System.Text.StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString().Contains(id, StringComparison.OrdinalIgnoreCase)) { found = handle; return false; }
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero && SetForegroundWindow(found);
    }
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    private static string ValidateId(string id) { id = id.Trim().ToUpperInvariant(); if (!Regex.IsMatch(id, "^[A-Z]{2,8}[0-9]{2,8}$")) throw new TicketOperationException("Código de ticket inválido."); return id; }
}
