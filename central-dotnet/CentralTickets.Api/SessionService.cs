using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CentralTickets.Api;

/// <summary>Owns session reservation and launch orchestration for the .NET Central.</summary>
public sealed class SessionService(string dataRoot, CentralProfile profile, GitWorktreeService worktrees)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _dataRoot = dataRoot;
    private readonly CentralProfile _profile = profile;
    private readonly GitWorktreeService _worktrees = worktrees;

    public async Task<object> Start(string ticketId, CancellationToken cancellationToken)
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
        if (starting || IsAlive(IntValue(old, "pid")) || IsAlive(IntValue(old, "childPid")))
            return new { id, alreadyRunning = true, focused = FocusWindow(id), message = "A conversa existente foi localizada; nenhuma sessão duplicada foi aberta." };

        var agent = StringValue(ticket, "agent") ?? "claude";
        var executable = _profile.AgentExecutable(agent);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new TicketOperationException($"{agent} não está instalado ou configurado.", 409);
        if (!BoolValue(ticket, "prepared"))
            throw new TicketOperationException("Prepare a worktree e confira o pré-flight antes de iniciar a conversa.", 409);
        var detail = await _worktrees.Preflight(id, cancellationToken);
        using var preflight = JsonDocument.Parse(JsonSerializer.Serialize(detail));
        if (!preflight.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new TicketOperationException("Pré-flight bloqueado. Resolva os itens apontados antes de iniciar a conversa.", 409);

        var runId = Guid.NewGuid().ToString();
        var started = DateTimeOffset.UtcNow.ToString("O");
        var runtime = new JsonObject { ["runId"] = runId, ["phase"] = "starting", ["startedAt"] = started, ["heartbeat"] = started };
        WriteJson(Path.Combine(dir, "runtime.json"), runtime);
        WriteJson(Path.Combine(dir, "active.lock"), new JsonObject { ["runId"] = runId, ["startedAt"] = started });
        try
        {
            WriteSessionFiles(ticket, dir, id);
            var script = Path.Combine(_profile.GetString("appRoot") ?? "", "central-dotnet", "session-runner.ps1");
            if (!File.Exists(script)) throw new TicketOperationException("O launcher .NET da sessão ainda não foi instalado.", 409);
            var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (!File.Exists(terminal)) terminal = "wt.exe";
            var launch = new ProcessStartInfo(terminal) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-w", "0", "new-tab", "--title", $"{id} - Central", "powershell.exe", "-NoProfile", "-NoExit", "-File", script, _dataRoot, id, runId }) launch.ArgumentList.Add(arg);
            using var process = Process.Start(launch) ?? throw new TicketOperationException("Não foi possível abrir o terminal da conversa.", 409);
            AppendEvent(dir, "abertura", "Janela do agente solicitada pela Central .NET.");
            return new { id, runId, message = "A janela do agente está abrindo." };
        }
        catch
        {
            TryDelete(Path.Combine(dir, "active.lock"));
            WriteJson(Path.Combine(dir, "runtime.json"), new JsonObject { ["runId"] = runId, ["phase"] = "error", ["error"] = "Falha ao iniciar a sessão.", ["endedAt"] = DateTimeOffset.UtcNow.ToString("O") });
            throw;
        }
    }

    private void WriteSessionFiles(JsonObject ticket, string dir, string id)
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
        var documentsRoot = _profile.GetString("documents_root");
        if (!string.IsNullOrWhiteSpace(documentsRoot)) Directory.CreateDirectory(Path.Combine(documentsRoot, id.ToLowerInvariant()));
        ticket["sessionId"] = sessionId;
        var ids = ticket["sessionIds"] as JsonArray ?? new JsonArray();
        if (!ids.Any(item => item?.GetValue<string>() == sessionId)) ids.Add(sessionId);
        ticket["sessionIds"] = ids;
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
                "mcp__central-computer__computer_wait"),
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, node.ToJsonString(JsonOptions), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    private static void AppendEvent(string dir, string type, string message) => File.AppendAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }, JsonOptions) + Environment.NewLine);
    private static JsonObject? ReadObject(string path) { try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } catch (JsonException) { throw new TicketOperationException("Cadastro ou runtime inválido; os arquivos foram preservados.", 409); } }
    private static string? StringValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool BoolValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static int? IntValue(JsonObject? obj, string name) => obj?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    private static bool IsAlive(int? pid) { if (pid is null or <= 0) return false; try { using var process = Process.GetProcessById(pid.Value); return !process.HasExited; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } }
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
