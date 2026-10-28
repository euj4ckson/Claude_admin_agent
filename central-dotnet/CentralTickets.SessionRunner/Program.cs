using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CentralTickets.SessionRunner;

if (args.Length != 3) return 2;
var (dataRoot, id, runId) = (args[0], args[1].ToUpperInvariant(), args[2]);
if (!Regex.IsMatch(id, "^[A-Z]{2,8}[0-9]{2,8}$") || !Guid.TryParse(runId, out _)) return 2;
var dir = Path.Combine(dataRoot, "tickets", id.ToLowerInvariant());
var ticketFile = Path.Combine(dir, "ticket.json");
var runtimeFile = Path.Combine(dir, "runtime.json");
var lockFile = Path.Combine(dir, "active.lock");
var config = Read(Path.Combine(dataRoot, "config.json"));
var ticket = Read(ticketFile);
var runtime = Read(runtimeFile);
if (ticket is null || runtime?["runId"]?.GetValue<string>() != runId || Read(lockFile)?["runId"]?.GetValue<string>() != runId)
{
    Console.Error.WriteLine("Reserva de sessão inválida; nenhuma conversa foi iniciada.");
    return 3;
}
var worktree = ticket["worktree"]?.GetValue<string>() ?? "";
if (!Directory.Exists(worktree)) { Console.Error.WriteLine("Worktree ausente."); return FinishError("Worktree ausente."); }
var agent = ticket["agent"]?.GetValue<string>() ?? "claude";
var executable = ResolveExecutable(agent, config);
if (executable is null) { Console.Error.WriteLine($"Executável de {agent} não encontrado."); return FinishError($"Executável de {agent} não encontrado."); }

var contextFile = Path.Combine(dir, "session-context.txt");
var sessionId = ticket["sessionId"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
var freshSession = ticket["freshSessionId"]?.GetValue<string>() == sessionId;
var promptFile = Path.Combine(dir, freshSession ? "session-checkpoint.md" : "entrada.md");
if (!File.Exists(contextFile) || !File.Exists(promptFile)) return FinishError("Contexto da sessão ausente.");
var sessionStarted = ticket["sessionStarted"]?.GetValue<bool>() == true;
var firstPromptSubmitted = ticket["firstPromptSubmitted"]?.GetValue<bool>() == true
    || (!freshSession && Read(Path.Combine(dir, "metrics.json"))?["totals"]?["prompts"]?.GetValue<int>() > 0);
var shouldSendEntryPrompt = ClaudeSessionPolicy.ShouldSendEntryPrompt(sessionStarted, firstPromptSubmitted);
var context = File.ReadAllText(contextFile);
var prompt = File.ReadAllText(promptFile);
var stateRoot = config?["state_base_root"]?.GetValue<string>() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
var stateFile = Path.Combine(stateRoot, ticket["repositoryId"]?.GetValue<string>() ?? "", id.ToLowerInvariant(), "estado.json");
var psi = new ProcessStartInfo(executable) { WorkingDirectory = worktree, UseShellExecute = false };
psi.Environment["CENTRAL_TICKETS_DATA"] = dataRoot;
psi.Environment["CENTRAL_TICKET_ID"] = id;
psi.Environment["CENTRAL_TICKET_RUN"] = runId;
psi.Environment["CENTRAL_TICKET_AGENT"] = agent;
if (agent == "claude")
{
    var background = !shouldSendEntryPrompt ? await FindBackgroundSession(executable, sessionId) : null;
    if (background is not null) Add("attach", background);
    else
    {
        var appRoot = config?["appRoot"]?.GetValue<string>() ?? "";
        Add("--name", id); Add("--permission-mode", "bypassPermissions");
        Add("--plugin-dir", Path.Combine(appRoot, "central-dotnet", "CentralTickets.Plugin"));
        Add("--settings", Path.Combine(dir, "session-settings.json"));
        Add("--mcp-config", Path.Combine(dir, "mcp-servers.json"));
        Add("--append-system-prompt", context);
        Add("--add-dir", Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
        var references = Path.Combine(config?["references_root"]?.GetValue<string>() ?? "", id.ToLowerInvariant());
        if (Directory.Exists(references)) Add("--add-dir", references);
        if (sessionStarted)
        {
            Add("--resume", sessionId);
            if (shouldSendEntryPrompt) Add(prompt);
        }
        else { Add("--session-id", sessionId); Add(prompt); }
    }
}
else
{
    Add("--ask-for-approval", "on-request"); Add("--sandbox", "workspace-write");
    Add($"Leia integralmente {promptFile} e {contextFile}; consulte {stateFile}. Use a worktree {worktree} e a branch {ticket["branch"]?.GetValue<string>()}. Comece pela análise e aguarde APROVAR {id} antes de editar código. Preserve o escopo e registre evidências verificáveis. Entrega: {ticket["delivery"]?.GetValue<string>()}; nunca faça push/PR.\n\n{prompt}");
    if (Environment.GetEnvironmentVariable("TERM")?.Equals("dumb", StringComparison.OrdinalIgnoreCase) == true) psi.Environment.Remove("TERM");
}

Process? child = null;
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); try { if (child is { HasExited: false }) child.Kill(true); } catch { } };
try
{
    child = Process.Start(psi) ?? throw new InvalidOperationException("O processo do agente não iniciou.");
    runtime["phase"] = "running"; runtime["pid"] = Environment.ProcessId; runtime["childPid"] = child.Id; runtime["heartbeat"] = DateTimeOffset.UtcNow.ToString("O");
    WriteJson(runtimeFile, runtime);
    WriteJson(lockFile, new JsonObject { ["runId"] = runId, ["pid"] = Environment.ProcessId, ["childPid"] = child.Id });
    if (agent == "codex") await ReportCodexSessionEvent(dataRoot, id, runId, "SessionStart");
    using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(3));
    var wait = child.WaitForExitAsync();
    while (!wait.IsCompleted && await heartbeat.WaitForNextTickAsync())
    {
        runtime["heartbeat"] = DateTimeOffset.UtcNow.ToString("O"); WriteJson(runtimeFile, runtime);
    }
    await wait;
    if (agent == "codex") await ReportCodexSessionEvent(dataRoot, id, runId, "SessionEnd");
    runtime["phase"] = child.ExitCode == 0 ? "stopped" : "error";
    runtime["exitCode"] = child.ExitCode; runtime["endedAt"] = DateTimeOffset.UtcNow.ToString("O");
    if (child.ExitCode != 0) runtime["error"] = $"O agente encerrou com código {child.ExitCode}.";
    WriteJson(runtimeFile, runtime);
    Event("sessao", $"Processo {agent} encerrado ({child.ExitCode}); o ticket não foi concluído automaticamente.");
    return child.ExitCode;
}
catch (Exception error)
{
    return FinishError(error.Message);
}
finally
{
    if (Read(lockFile)?["runId"]?.GetValue<string>() == runId) try { File.Delete(lockFile); } catch (IOException) { }
}

void Add(params string[] values) { foreach (var value in values) psi.ArgumentList.Add(value); }
int FinishError(string message)
{
    var current = Read(runtimeFile) ?? new JsonObject();
    if (current["runId"]?.GetValue<string>() == runId) { current["phase"] = "error"; current["error"] = message; current["endedAt"] = DateTimeOffset.UtcNow.ToString("O"); WriteJson(runtimeFile, current); }
    Event("erro", message); return 1;
}
void Event(string type, string message) => File.AppendAllText(Path.Combine(dir, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type, message }) + Environment.NewLine);

static string? ResolveExecutable(string agent, JsonObject? config)
{
    var configured = config?[agent]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(configured)) return null;
    if (File.Exists(configured) && !configured.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !configured.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return configured;
    if (agent == "codex")
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var npm = Path.GetDirectoryName(configured) ?? Path.Combine(home, "AppData", "Roaming", "npm");
        foreach (var arch in new[] { "x64", "arm64" })
        {
            var machine = arch == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
            var candidate = Path.Combine(npm, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{arch}", "vendor", machine, "bin", "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
    }
    return null;
}
static JsonObject? Read(string path) { try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (IOException) { return null; } catch (JsonException) { return null; } }
static void WriteJson(string path, JsonNode value)
{
    var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllText(temp, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return;
            }
            catch (Exception error) when (attempt < 7 && (error is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
    finally
    {
        try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
static async Task<string?> FindBackgroundSession(string executable, string sessionId)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "agents --json") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        using var json = JsonDocument.Parse(output);
        foreach (var agent in json.RootElement.EnumerateArray())
        {
            var kind = agent.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
            var session = agent.TryGetProperty("sessionId", out var sessionValue) ? sessionValue.GetString() : null;
            var id = agent.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (kind == "background" && (session == sessionId || id == sessionId)) return id ?? session?[..Math.Min(8, session.Length)];
        }
    }
    catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    return null;
}
static async Task ReportCodexSessionEvent(string dataRoot, string ticketId, string runId, string eventName)
{
    try
    {
        var config = Read(Path.Combine(dataRoot, "config.json"));
        var appRoot = config?["appRoot"]?.GetValue<string>() ?? "";
        var hook = Path.Combine(appRoot, "central-dotnet", "CentralTickets.Hook", "bin", "Release", "net10.0", "CentralTickets.Hook.dll");
        if (!File.Exists(hook)) hook = Path.Combine(appRoot, "central-dotnet", "CentralTickets.Hook", "bin", "Debug", "net10.0", "CentralTickets.Hook.dll");
        if (!File.Exists(hook)) throw new InvalidOperationException("Hook .NET ausente; execute instalar-central.cmd.");
        var dotnet = config?["dotnet"]?.GetValue<string>() ?? "dotnet";
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { hook, "--managed-session-event", ticketId, runId, eventName }) start.ArgumentList.Add(argument);
        start.Environment["CENTRAL_TICKETS_DATA"] = dataRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("O hook .NET nÃ£o iniciou.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Hook encerrou com cÃ³digo {process.ExitCode}." : error.Trim());
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Central: nÃ£o foi possÃ­vel registrar {eventName} do Codex; sessÃ£o preservada ({error.Message}).");
    }
}
