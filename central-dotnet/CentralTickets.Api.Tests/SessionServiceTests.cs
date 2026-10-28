using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class SessionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-session-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _states;
    private readonly string _repo;
    private readonly string _remote;
    private readonly string _worktrees;
    private readonly string _git = FindGit();

    public SessionServiceTests()
    {
        _data = Path.Combine(_root, "data");
        _states = Path.Combine(_root, "states");
        _repo = Path.Combine(_root, "git", "ss_erp_tag1");
        _remote = Path.Combine(_root, "remote.git");
        _worktrees = Path.Combine(_root, "worktrees");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.GetDirectoryName(_repo)!);

        Git(_root, "init", "--bare", _remote);
        Git(_root, "init", "--initial-branch=main", _repo);
        Git(_repo, "config", "user.email", "central-test@example.invalid");
        Git(_repo, "config", "user.name", "Central Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "isolated session fixture\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-m", "initial fixture");
        Git(_repo, "remote", "add", "origin", _remote);
        Git(_repo, "push", "-u", "origin", "main");

        File.WriteAllText(Path.Combine(_root, "claude.exe"), "fixture");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _repo,
            repositories_root = Path.GetDirectoryName(_repo),
            git_executable = _git,
            state_base_root = _states,
            worktrees_base_root = _worktrees,
            appRoot = Path.Combine(_root, "app"),
            claude = Path.Combine(_root, "claude.exe"),
            codex = (string?)null
        }));
    }

    [Fact]
    public async Task Start_prepares_worktree_automatically_before_session_preflight_and_launch()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _states, profile);
        writer.Create(new CreateTicketRequest("GER5810", "166", "main", "Uma descricao de escopo valida para teste.", "Fixture", "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var worktrees = new GitWorktreeService(_data, _states, _worktrees, profile);
        var service = new SessionService(_data, profile, worktrees);

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => service.Start("GER5810", default));

        Assert.Contains("Computer Use", error.Message, StringComparison.OrdinalIgnoreCase);
        var ticketDir = Path.Combine(_data, "tickets", "ger5810");
        var ticket = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "ticket.json")))!.AsObject();
        Assert.True(ticket["prepared"]?.GetValue<bool>());
        Assert.Equal("feature/166/ger5810", ticket["branch"]?.GetValue<string>());
        var worktree = ticket["worktree"]!.GetValue<string>();
        Assert.True(Directory.Exists(worktree));
        Assert.Equal("feature/166/ger5810", Git(worktree, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(_states, "ss_erp_tag1", "ger5810", "test-matrix.json")));
        Assert.True(File.Exists(Path.Combine(_states, "ss_erp_tag1", "ger5810", "evidence-manifest.json")));
        Assert.False(File.Exists(Path.Combine(ticketDir, "active.lock")));
        Assert.Equal("error", JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "runtime.json")))?["phase"]?.GetValue<string>());
    }

    [Fact]
    public async Task Fresh_session_keeps_ticket_state_and_approval_and_creates_a_short_checkpoint()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _states, profile);
        writer.Create(new CreateTicketRequest("GER5812", "166", "main", "Escopo da retomada isolada.", "Fixture", "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var worktrees = new GitWorktreeService(_data, _states, _worktrees, profile);
        var service = new SessionService(_data, profile, worktrees);
        await Assert.ThrowsAsync<TicketOperationException>(() => service.Start("GER5812", default));

        var ticketDir = Path.Combine(_data, "tickets", "ger5812");
        var stateDir = Path.Combine(_states, "ss_erp_tag1", "ger5812");
        var statePath = Path.Combine(stateDir, "estado.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["stage"] = "testes";
        state["next_action"] = "Validar CA2 na tela";
        WriteJson(statePath, state);
        File.WriteAllText(Path.Combine(stateDir, "escopo.md"), "Plano aprovado de exemplo para a retomada.");
        File.WriteAllText(Path.Combine(ticketDir, "approval.json"), "approval-preserved");
        var before = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "ticket.json")))!.AsObject();
        var oldSession = before["sessionId"]?.GetValue<string>();

        await Assert.ThrowsAsync<TicketOperationException>(() => service.Start("GER5812", default, freshSession: true));

        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "ticket.json")))!.AsObject();
        var newSession = after["sessionId"]?.GetValue<string>();
        Assert.NotEqual(oldSession, newSession);
        Assert.Equal(newSession, after["freshSessionId"]?.GetValue<string>());
        Assert.Contains(oldSession, after["sessionIds"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains(newSession, after["sessionIds"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(after["sessionStarted"]?.GetValue<bool>());
        Assert.Equal("approval-preserved", File.ReadAllText(Path.Combine(ticketDir, "approval.json")));
        Assert.Equal("testes", JsonNode.Parse(File.ReadAllText(statePath))?["stage"]?.GetValue<string>());
        Assert.Contains("Validar CA2 na tela", File.ReadAllText(Path.Combine(ticketDir, "session-checkpoint.md")));
        Assert.DoesNotContain("Comece pela análise", File.ReadAllText(Path.Combine(ticketDir, "session-context.txt")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_recovers_a_live_agent_whose_runtime_and_reservation_were_lost()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _states, profile);
        writer.Create(new CreateTicketRequest("GER5811", "166", "main", "Uma descricao de escopo valida para teste.", "Fixture", "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var ticketDir = Path.Combine(_data, "tickets", "ger5811");
        var runtime = new JsonObject
        {
            ["runId"] = Guid.NewGuid().ToString(), ["phase"] = "error",
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["childPid"] = Environment.ProcessId,
            ["error"] = "Access to the path is denied."
        };
        WriteJson(Path.Combine(ticketDir, "runtime.json"), runtime);
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "config.json")))!.AsObject();
        config["claude"] = Process.GetCurrentProcess().MainModule!.FileName;
        WriteJson(Path.Combine(_data, "config.json"), config);
        profile = new CentralProfile(_data);

        var worktrees = new GitWorktreeService(_data, _states, _worktrees, profile);
        var service = new SessionService(_data, profile, worktrees);
        var result = await service.Start("GER5811", default);
        var updatedRuntime = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "runtime.json")))!.AsObject();
        var active = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "active.lock")))!.AsObject();

        Assert.Equal("running", updatedRuntime["phase"]?.GetValue<string>());
        Assert.Equal(runtime["runId"]?.GetValue<string>(), active["runId"]?.GetValue<string>());
        Assert.Null(updatedRuntime["error"]);
        Assert.True((bool?)result.GetType().GetProperty("recovered")?.GetValue(result));
        var recoveredHeartbeat = updatedRuntime["heartbeat"]?.GetValue<string>();
        await Task.Delay(TimeSpan.FromMilliseconds(3300));
        var monitoredRuntime = JsonNode.Parse(File.ReadAllText(Path.Combine(ticketDir, "runtime.json")))!.AsObject();
        Assert.Equal("running", monitoredRuntime["phase"]?.GetValue<string>());
        Assert.NotEqual(recoveredHeartbeat, monitoredRuntime["heartbeat"]?.GetValue<string>());
    }

    private static string FindGit()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git"))
            .FirstOrDefault(File.Exists);
        return path ?? throw new InvalidOperationException("Git is required for this isolated integration test.");
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    private static void WriteJson(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
        Directory.Delete(_root, recursive: true);
    }
}
