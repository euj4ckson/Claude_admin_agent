using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class HookCommitGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-commit-gate-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _states;
    private readonly string _primary;
    private readonly string _component;
    private readonly string? _oldData = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA");
    private readonly string? _oldId = Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID");
    private readonly string? _oldRun = Environment.GetEnvironmentVariable("CENTRAL_TICKET_RUN");

    public HookCommitGateTests()
    {
        _data = Path.Combine(_root, "data"); _states = Path.Combine(_root, "states");
        _primary = Path.Combine(_root, "worktrees", "ss_erp_tag1"); _component = Path.Combine(_root, "worktrees", "ss_fva");
        Directory.CreateDirectory(_data);
        var git = FindGit();
        var fakeClaude = Path.Combine(_root, "claude.exe");
        Directory.CreateDirectory(_root); File.WriteAllText(fakeClaude, "test fixture");
        var primaryBase = InitRepository(_primary, git, "code.cs", "baseline");
        var componentBase = InitRepository(_component, git, "b.cs", "baseline");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _primary, git_executable = git, state_base_root = _states, claude = fakeClaude,
            documents_root = Path.Combine(_root, "documents"), references_root = Path.Combine(_root, "references")
        }));
        Environment.SetEnvironmentVariable("CENTRAL_TICKETS_DATA", _data);
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_ID", "GER5812");
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_RUN", "run-commit-fixture");
        var writer = new TicketWriter(_data, _states, new CentralProfile(_data));
        writer.Create(new CreateTicketRequest("GER5812", "166", "main", "Plano aprovado para testar gate de commit.", "Fixture de commit", "local_commit_only", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var ticketFile = Path.Combine(_data, "tickets", "ger5812", "ticket.json");
        var ticket = JsonNode.Parse(File.ReadAllText(ticketFile))!.AsObject();
        ticket["worktree"] = _primary; ticket["baseSha"] = primaryBase; ticket["prepared"] = true;
        ticket["components"] = new JsonArray(new JsonObject { ["repositoryId"] = "ss_fva", ["repository"] = _component,
            ["worktree"] = _component, ["baseSha"] = componentBase, ["prepared"] = true });
        File.WriteAllText(ticketFile, ticket.ToJsonString());
        var stateDir = Path.Combine(_states, "ss_erp_tag1", "ger5812");
        Directory.CreateDirectory(Path.Combine(stateDir, "scratch"));
        File.WriteAllText(Path.Combine(stateDir, "escopo.md"), "Plano para ss_erp_tag1: code.cs; ss_fva: b.cs. Limites e testes registrados.");
        File.WriteAllText(Path.Combine(stateDir, "scratch", "build.log"), "build ok");
        File.WriteAllText(Path.Combine(stateDir, "scratch", "test.log"), "test ok");
        File.WriteAllText(Path.Combine(stateDir, "evidence-manifest.json"), "{\"schema_version\":2,\"entries\":[\"scratch/build.log\",\"scratch/test.log\"]}");
        var stateFile = Path.Combine(stateDir, "estado.json");
        var state = JsonNode.Parse("""
        {"schema_version":2,"stage":"aguardando_aprovacao","scope_revision":1,
         "base_sha":"PRIMARY_SHA","component_plans":{
           "ss_erp_tag1":{"mode":"change","planned_files":["code.cs"]},
           "ss_fva":{"mode":"read_only","planned_files":[]}},
         "scope_guard":{"max_planned_files":10,"max_planned_added_lines":100,"max_planned_deleted_lines":100,
           "hard_stop":{"max_files":10,"max_added_lines":100,"max_deleted_lines":100}},
         "change_budget":{"planned_files":["code.cs"],"estimated_added_lines":2,"estimated_deleted_lines":0,
           "approved_expansion":false,"correction_rounds":0,"hard_stop":{"max_files":10,"max_added_lines":100,"max_deleted_lines":100}},
         "build":[{"result":"OK","expected":"build passa","obtained":"build ok","evidence":"scratch/build.log"}],
         "tests":[{"id":"T1","result":"OK","expected":"resultado esperado","obtained":"resultado conferido","evidence":"scratch/test.log"}],
         "review":{"verdict":"APROVADO","findings":[]}}
        """)!.AsObject();
        state["base_sha"] = primaryBase;
        File.WriteAllText(stateFile, state.ToJsonString());
        File.WriteAllText(Path.Combine(_data, "tickets", "ger5812", "runtime.json"), "{\"runId\":\"run-commit-fixture\",\"phase\":\"running\",\"pid\":" + Environment.ProcessId + "}");
        File.WriteAllText(Path.Combine(_data, "tickets", "ger5812", "active.lock"), "{\"runId\":\"run-commit-fixture\"}");
        var policy = new HookPolicy(_data);
        var savedTicket = JsonNode.Parse(File.ReadAllText(ticketFile))!.AsObject();
        policy.ApprovalPrompt(new JsonObject { ["prompt"] = "APROVAR GER5812", ["session_id"] = savedTicket["sessionId"]!.GetValue<string>() });
        state["stage"] = "entrega";
        File.WriteAllText(stateFile, state.ToJsonString());
    }

    [Fact]
    public void Commit_is_denied_when_read_only_component_contains_a_change()
    {
        File.WriteAllText(Path.Combine(_component, "unexpected.cs"), "must not be committed by this ticket");
        var output = new HookPolicy(_data).PreTool(Input("git commit -m local"));
        Assert.Equal("deny", Decision(output));
        Assert.Contains("somente leitura", output["hookSpecificOutput"]?["permissionDecisionReason"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Commit_checks_combined_file_count_across_change_repositories()
    {
        var stateFile = Path.Combine(_states, "ss_erp_tag1", "ger5812", "estado.json");
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        state["component_plans"]!["ss_fva"] = new JsonObject { ["mode"] = "change", ["planned_files"] = new JsonArray("b.cs") };
        state["build"]!.AsArray().Add(new JsonObject { ["repositoryId"] = "ss_fva", ["result"] = "OK", ["expected"] = "build passa", ["obtained"] = "build ok", ["evidence"] = "scratch/build.log" });
        state["tests"]!.AsArray().Add(new JsonObject { ["repositoryId"] = "ss_fva", ["id"] = "T2", ["result"] = "OK", ["expected"] = "resultado esperado", ["obtained"] = "resultado conferido", ["evidence"] = "scratch/test.log" });
        state["review"]!["components"] = new JsonObject { ["ss_fva"] = new JsonObject { ["verdict"] = "APROVADO" } };
        state["change_budget"]!["hard_stop"]!["max_files"] = 1;
        ((JsonObject)state["scope_guard"]!["hard_stop"]!)["max_files"] = 1;
        state["stage"] = "aguardando_aprovacao";
        File.WriteAllText(stateFile, state.ToJsonString());
        // Re-approve because the component plan is part of the approval hash.
        var ticket = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "ger5812", "ticket.json")))!.AsObject();
        new HookPolicy(_data).ApprovalPrompt(new JsonObject { ["prompt"] = "APROVAR GER5812", ["session_id"] = ticket["sessionId"]!.GetValue<string>() });
        state["stage"] = "entrega"; File.WriteAllText(stateFile, state.ToJsonString());
        File.WriteAllText(Path.Combine(_primary, "code.cs"), "baseline\nprimary change");
        File.WriteAllText(Path.Combine(_component, "b.cs"), "baseline\ncomponent change");

        var output = new HookPolicy(_data).PreTool(Input("git commit -m local"));
        Assert.Equal("deny", Decision(output));
        Assert.Contains("hard-stop", output["hookSpecificOutput"]?["permissionDecisionReason"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject Input(string command) => new() { ["tool_name"] = "Bash", ["tool_input"] = new JsonObject { ["command"] = command } };
    private static string? Decision(JsonObject output) => output["hookSpecificOutput"]?["permissionDecision"]?.GetValue<string>();
    private static string FindGit() => Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(path => Path.Combine(path, "git.exe")).FirstOrDefault(File.Exists) ?? "git";

    private static string InitRepository(string path, string git, string file, string contents)
    {
        Directory.CreateDirectory(path); RunGit(git, path, "init", "-b", "main");
        RunGit(git, path, "config", "user.name", "Central Test"); RunGit(git, path, "config", "user.email", "central-test@example.invalid");
        File.WriteAllText(Path.Combine(path, file), contents); RunGit(git, path, "add", file); RunGit(git, path, "commit", "-m", "baseline");
        return RunGit(git, path, "rev-parse", "HEAD");
    }

    private static string RunGit(string git, string path, params string[] args)
    {
        var start = new ProcessStartInfo(git) { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(path); foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CENTRAL_TICKETS_DATA", _oldData); Environment.SetEnvironmentVariable("CENTRAL_TICKET_ID", _oldId); Environment.SetEnvironmentVariable("CENTRAL_TICKET_RUN", _oldRun);
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(directory, FileAttributes.Directory);
                File.SetAttributes(_root, FileAttributes.Directory);
                Directory.Delete(_root, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
