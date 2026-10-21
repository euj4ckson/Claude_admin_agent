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
    }

    private static string DotnetExecutable()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent ?? throw new InvalidOperationException("Raiz do .NET nÃ£o encontrada.");
        var executable = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(executable)) throw new FileNotFoundException("Host dotnet nÃ£o encontrado.", executable);
        return executable;
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
