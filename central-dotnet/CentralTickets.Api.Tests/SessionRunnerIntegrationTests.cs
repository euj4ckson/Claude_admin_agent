using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentralTickets.Api.Tests;

public sealed class SessionRunnerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-session-runner-{Guid.NewGuid():N}");

    [Fact]
    public async Task Codex_runner_records_start_end_and_memory_queue_without_a_real_agent()
    {
        var data = Path.Combine(_root, "data");
        var app = Path.Combine(_root, "app");
        var worktree = Path.Combine(_root, "worktree");
        var states = Path.Combine(_root, "states");
        var references = Path.Combine(_root, "references");
        var ticketDir = Path.Combine(data, "tickets", "ger5901");
        var stateDir = Path.Combine(states, "ss_erp_tag1", "ger5901");
        var hookDir = Path.Combine(app, "central-dotnet", "CentralTickets.Hook", "bin", "Release", "net10.0");
        Directory.CreateDirectory(ticketDir);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(hookDir);

        var hookArtifacts = Directory.GetFiles(AppContext.BaseDirectory)
            .Where(file => Path.GetFileName(file).StartsWith("CentralTickets.Hook", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).StartsWith("CentralTickets.Api", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(hookArtifacts, file => Path.GetFileName(file) == "CentralTickets.Hook.dll");
        foreach (var file in hookArtifacts) File.Copy(file, Path.Combine(hookDir, Path.GetFileName(file)), overwrite: true);

        var runId = Guid.NewGuid().ToString();
        var sessionId = Guid.NewGuid().ToString();
        var fakeAgent = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/usr/bin/false";
        Assert.True(File.Exists(fakeAgent));
        WriteJson(Path.Combine(data, "config.json"), new JsonObject
        {
            ["appRoot"] = app,
            ["repository"] = worktree,
            ["codex"] = fakeAgent,
            ["dotnet"] = DotnetExecutable(),
            ["state_base_root"] = states,
            ["references_root"] = references,
            ["ai_memory_url"] = "http://127.0.0.1:1"
        });
        WriteJson(Path.Combine(ticketDir, "ticket.json"), new JsonObject
        {
            ["id"] = "GER5901", ["ticket"] = "GER5901", ["agent"] = "codex", ["repository"] = worktree,
            ["repositoryId"] = "ss_erp_tag1", ["worktree"] = worktree, ["branch"] = "feature/166/ger5901",
            ["prepared"] = true, ["title"] = "Fixture Codex", ["scope"] = "Teste isolado do ciclo de sessÃ£o.",
            ["delivery"] = "no_commit", ["sessionId"] = sessionId, ["sessionIds"] = new JsonArray(sessionId)
        });
        WriteJson(Path.Combine(ticketDir, "runtime.json"), new JsonObject { ["runId"] = runId, ["phase"] = "starting", ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") });
        WriteJson(Path.Combine(ticketDir, "active.lock"), new JsonObject { ["runId"] = runId });
        WriteJson(Path.Combine(stateDir, "estado.json"), new JsonObject { ["ticket"] = "GER5901", ["stage"] = "analise", ["scope_revision"] = 1 });
        File.WriteAllText(Path.Combine(ticketDir, "entrada.md"), "Fixture session prompt.");
        File.WriteAllText(Path.Combine(ticketDir, "session-context.txt"), "Fixture session context.");

        var runner = Path.Combine(AppContext.BaseDirectory, "CentralTickets.SessionRunner.dll");
        Assert.True(File.Exists(runner));
        var start = new ProcessStartInfo(DotnetExecutable()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { runner, data, "GER5901", runId }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("SessionRunner nÃ£o iniciou.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        _ = await standardOutput;
        _ = await standardError;

        var runtime = ReadJson(Path.Combine(ticketDir, "runtime.json"));
        var metrics = ReadJson(Path.Combine(ticketDir, "metrics.json"));
        Assert.Equal("error", runtime["phase"]?.GetValue<string>());
        Assert.NotNull(metrics["started_at"]);
        Assert.NotNull(metrics["ended_at"]);
        Assert.Equal("queued", metrics["memory_sync"]?.AsArray().LastOrDefault()?["status"]?.GetValue<string>());
        Assert.Contains("GER5901", File.ReadAllText(Path.Combine(data, "ai-memory-outbox.jsonl")));
        Assert.False(File.Exists(Path.Combine(ticketDir, "active.lock")));
        var events = File.ReadAllText(Path.Combine(ticketDir, "events.jsonl"));
        Assert.Contains("Codex conectado", events);
        Assert.Contains("Codex encerrada", events);

        var claudeDir = Path.Combine(data, "tickets", "ger5902");
        var claudeState = Path.Combine(states, "ss_erp_tag1", "ger5902");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(claudeState);
        var claudeRun = Guid.NewGuid().ToString();
        var claudeSession = Guid.NewGuid().ToString();
        var originalClaudeSession = Guid.NewGuid().ToString();
        WriteJson(Path.Combine(claudeDir, "ticket.json"), new JsonObject
        {
            ["id"] = "GER5902", ["ticket"] = "GER5902", ["agent"] = "claude", ["repository"] = worktree,
            ["repositoryId"] = "ss_erp_tag1", ["worktree"] = worktree, ["branch"] = "feature/166/ger5902",
            ["prepared"] = true, ["title"] = "Fixture Claude", ["scope"] = "Teste isolado do hook Claude.",
            ["delivery"] = "no_commit", ["sessionId"] = originalClaudeSession, ["sessionIds"] = new JsonArray(originalClaudeSession)
        });
        WriteJson(Path.Combine(claudeDir, "runtime.json"), new JsonObject { ["runId"] = claudeRun, ["phase"] = "running", ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") });
        WriteJson(Path.Combine(claudeDir, "active.lock"), new JsonObject { ["runId"] = claudeRun });
        File.WriteAllText(Path.Combine(claudeDir, "metrics.json"), "{}");
        WriteJson(Path.Combine(claudeState, "estado.json"), new JsonObject { ["ticket"] = "GER5902", ["stage"] = "analise", ["scope_revision"] = 1 });
        var hookDll = Path.Combine(hookDir, "CentralTickets.Hook.dll");
        using var hookProcess = StartHook(hookDll, data, "GER5902", claudeRun);
        await hookProcess.StandardInput.WriteLineAsync(new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = claudeSession }.ToJsonString());
        hookProcess.StandardInput.Close();
        var hookOutput = await hookProcess.StandardOutput.ReadToEndAsync();
        var hookError = await hookProcess.StandardError.ReadToEndAsync();
        await hookProcess.WaitForExitAsync(timeout.Token);

        Assert.True(hookProcess.ExitCode == 0, hookError);
        Assert.Contains("SessionStart", hookOutput);
        Assert.True(ReadJson(Path.Combine(claudeDir, "ticket.json"))["sessionStarted"]?.GetValue<bool>());
        Assert.Equal("SessionStart", ReadJson(Path.Combine(claudeDir, "hook-status.json"))["event"]?.GetValue<string>());

        using var promptHook = StartHook(hookDll, data, "GER5902", claudeRun);
        await promptHook.StandardInput.WriteLineAsync(new JsonObject
        {
            ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = claudeSession, ["prompt"] = "Analise o escopo do ticket."
        }.ToJsonString());
        promptHook.StandardInput.Close();
        var promptOutput = await promptHook.StandardOutput.ReadToEndAsync();
        var promptError = await promptHook.StandardError.ReadToEndAsync();
        await promptHook.WaitForExitAsync(timeout.Token);

        Assert.True(promptHook.ExitCode == 0, promptError);
        Assert.DoesNotContain("The node already has a parent", promptError, StringComparison.OrdinalIgnoreCase);
        var updatedTicket = ReadJson(Path.Combine(claudeDir, "ticket.json"));
        Assert.Equal(claudeSession, updatedTicket["sessionId"]?.GetValue<string>());
        Assert.Equal(new[] { originalClaudeSession, claudeSession }, updatedTicket["sessionIds"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.True(ReadJson(Path.Combine(claudeDir, "metrics.json"))["totals"]?["prompts"]?.GetValue<int>() > 0);
        Assert.True(updatedTicket["firstPromptSubmitted"]?.GetValue<bool>());

        var claudeStateFile = Path.Combine(claudeState, "estado.json");
        var blockedState = ReadJson(claudeStateFile);
        blockedState["stage"] = "bloqueado";
        blockedState["next_action"] = "aguardando cenário reproduzível";
        WriteJson(claudeStateFile, blockedState);
        var blockedMetricsFile = Path.Combine(claudeDir, "metrics.json");
        var blockedMetrics = ReadJson(blockedMetricsFile);
        blockedMetrics["blockers"] = new JsonArray(new JsonObject { ["stage"] = "outro" });
        WriteJson(blockedMetricsFile, blockedMetrics);

        using var blockedPrompt = StartHook(hookDll, data, "GER5902", claudeRun);
        await blockedPrompt.StandardInput.WriteLineAsync(new JsonObject
        {
            ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = claudeSession, ["prompt"] = "Continue o diagnóstico."
        }.ToJsonString());
        blockedPrompt.StandardInput.Close();
        var blockedOutput = await blockedPrompt.StandardOutput.ReadToEndAsync();
        var blockedError = await blockedPrompt.StandardError.ReadToEndAsync();
        await blockedPrompt.WaitForExitAsync(timeout.Token);
        Assert.True(blockedPrompt.ExitCode == 0, blockedError);
        Assert.Contains("{}", blockedOutput);
        Assert.Equal("bloqueado", ReadJson(blockedMetricsFile)["blockers"]!.AsArray().Last()?["stage"]?.GetValue<string>());

        var expandedState = ReadJson(claudeStateFile);
        expandedState["stage"] = "analise";
        expandedState["scope_revision"] = 2;
        WriteJson(claudeStateFile, expandedState);
        var expandedMetrics = ReadJson(blockedMetricsFile);
        expandedMetrics["scope_expansions"] = new JsonArray(new JsonObject { ["key"] = "old-revision" });
        WriteJson(blockedMetricsFile, expandedMetrics);

        using var expandedPrompt = StartHook(hookDll, data, "GER5902", claudeRun);
        await expandedPrompt.StandardInput.WriteLineAsync(new JsonObject
        {
            ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = claudeSession, ["prompt"] = "Continue após revisão."
        }.ToJsonString());
        expandedPrompt.StandardInput.Close();
        var expandedOutput = await expandedPrompt.StandardOutput.ReadToEndAsync();
        var expandedError = await expandedPrompt.StandardError.ReadToEndAsync();
        await expandedPrompt.WaitForExitAsync(timeout.Token);
        Assert.True(expandedPrompt.ExitCode == 0, expandedError);
        Assert.Contains("{}", expandedOutput);
        Assert.Equal(2, ReadJson(blockedMetricsFile)["scope_expansions"]!.AsArray().Count);
    }

    private static string DotnetExecutable()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent ?? throw new InvalidOperationException("Raiz do .NET nÃ£o encontrada.");
        var executable = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(executable)) throw new FileNotFoundException("Host dotnet nÃ£o encontrado.", executable);
        return executable;
    }

    private static Process StartHook(string hookDll, string dataRoot, string ticketId, string runId)
    {
        var start = new ProcessStartInfo(DotnetExecutable()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(hookDll);
        start.Environment["CENTRAL_TICKETS_DATA"] = dataRoot;
        start.Environment["CENTRAL_TICKET_ID"] = ticketId;
        start.Environment["CENTRAL_TICKET_RUN"] = runId;
        return Process.Start(start) ?? throw new InvalidOperationException("Hook Claude nÃ£o iniciou.");
    }

    private static void WriteJson(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
