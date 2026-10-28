using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class HookPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-hook-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _state;
    private readonly string _repo;
    private readonly string? _oldData = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA");
    private readonly string? _oldId = Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID");
    private readonly string? _oldRun = Environment.GetEnvironmentVariable("CENTRAL_TICKET_RUN");

    public HookPolicyTests()
    {
        _data = Path.Combine(_root, "data"); _state = Path.Combine(_root, "state"); _repo = Path.Combine(_root, "git", "ss_erp_tag1");
        Directory.CreateDirectory(Path.Combine(_repo, ".git")); Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_root, "claude.exe"), "fixture");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new { repository = _repo, state_base_root = _state, documents_root = Path.Combine(_root, "docs"), claude = Path.Combine(_root, "claude.exe") }));
        Environment.SetEnvironmentVariable("CENTRAL_TICKETS_DATA", _data);
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_ID", "GER5811");
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_RUN", "run-hook-fixture");
        var writer = new TicketWriter(_data, _state, new CentralProfile(_data));
        writer.Create(new CreateTicketRequest("GER5811", "166", "main", "Descrição de escopo de teste suficientemente longa.", "Fixture", "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var directory = Path.Combine(_data, "tickets", "ger5811");
        File.WriteAllText(Path.Combine(directory, "runtime.json"), "{\"runId\":\"run-hook-fixture\",\"phase\":\"running\",\"pid\":" + Environment.ProcessId + "}");
        File.WriteAllText(Path.Combine(directory, "active.lock"), "{\"runId\":\"run-hook-fixture\"}");
        Directory.CreateDirectory(Path.Combine(_state, "ss_erp_tag1", "ger5811"));
    }

    [Fact]
    public void Read_command_parser_fails_closed_for_shell_execution_and_external_drivers()
    {
        Assert.True(HookPolicy.IsReadCommand("git diff --no-ext-diff --no-textconv --stat"));
        Assert.True(HookPolicy.IsReadCommand("cd \"/c/Users/test/repo\" 2>/dev/null && ls -la"));
        Assert.True(HookPolicy.IsReadCommand("cat estado.json"));
        Assert.True(HookPolicy.IsReadCommand("cd \"/c/Users/test/repo\" 2>/dev/null && ls -la; cat estado.json"));
        foreach (var command in new[] { "cat x > y", "cat $(touch file)", "find . -exec rm {} +", "git diff --output=x", "node script.js", "cat x &" })
            Assert.False(HookPolicy.IsReadCommand(command), command);
    }

    [Fact]
    public void Fva_tests_may_read_only_from_the_approved_cloudss_database()
    {
        PrepareSqlTicket("FVA0351", "ss_fva");
        var policy = new HookPolicy(_data);
        var allowed = policy.PreTool(Input("Bash", new JsonObject
        {
            ["command"] = "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDFV_PD -Q \"SELECT TOP (1) IdEmpresa FROM dbo.SSEmpresas\""
        }));
        Assert.True(Decision(allowed) == "allow", allowed.ToJsonString());

        foreach (var command in new[]
        {
            "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDFV_PD -Q \"UPDATE dbo.SSEmpresas SET Nome = 'x'\"",
            "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDFV_PD -Q \"SELECT NEXT VALUE FOR dbo.SeqTeste\"",
            "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDPROD -Q \"SELECT 1\"",
            "sqlcmd -S other.example,667 -E -d SSBDFV_PD -Q \"SELECT 1\"",
            "sqlcmd -S cloudss.dyndns.org,667 -d SSBDFV_PD -Q \"SELECT 1\"",
            "sqlcmd -S cloudss.dyndns.org,667 -E -U fixture -P fixture -d SSBDFV_PD -Q \"SELECT 1\""
        })
        {
            Assert.Equal("deny", Decision(policy.PreTool(Input("Bash", new JsonObject { ["command"] = command }))));
        }

        PrepareSqlTicket("GER5811", "ss_erp_tag1");
        var localDatabase = policy.PreTool(Input("Bash", new JsonObject
        {
            ["command"] = "sqlcmd -S localhost -E -d qualquer_banco_local -Q \"SELECT 1\""
        }));
        Assert.Equal("allow", Decision(localDatabase));
        var nonFva = policy.PreTool(Input("Bash", new JsonObject
        {
            ["command"] = "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDFV_PD -Q \"SELECT 1\""
        }));
        Assert.Equal("deny", Decision(nonFva));
    }

    [Fact]
    public void Background_execution_is_allowed_only_for_isolated_or_read_only_fva_tests()
    {
        PrepareSqlTicket("FVA0351", "ss_fva");
        var policy = new HookPolicy(_data);
        var readOnlyRemoteQuery = policy.PreTool(Input("Bash", new JsonObject
        {
            ["command"] = "sqlcmd -S cloudss.dyndns.org,667 -E -d SSBDFV_PD -Q \"SELECT TOP (1) IdEmpresa FROM dbo.SSEmpresas\"",
            ["run_in_background"] = true
        }));
        Assert.Equal("allow", Decision(readOnlyRemoteQuery));
        Assert.False(File.Exists(Path.Combine(_data, "execution.lock")));

        PrepareSqlTicket("GER5811", "ss_erp_tag1");
        var scratch = Path.Combine(_state, "ss_erp_tag1", "ger5811", "scratch");
        var queryFile = Path.Combine(scratch, "isolated-test.sql");
        File.WriteAllText(queryFile, "SELECT 1;");
        var isolatedDatabase = policy.PreTool(Input("PowerShell", new JsonObject
        {
            ["command"] = $"sqlcmd -S localhost -E -d SSBD_TESTE_GER5811 -i \"{queryFile}\"",
            ["run_in_background"] = true
        }));
        Assert.Equal("allow", Decision(isolatedDatabase));
        Assert.False(File.Exists(Path.Combine(_data, "execution.lock")));

        var sharedTest = policy.PreTool(Input("PowerShell", new JsonObject
        {
            ["command"] = "dotnet test C:\\git\\ss_erp_tag1\\CentralTickets.Api.Tests\\CentralTickets.Api.Tests.csproj",
            ["run_in_background"] = true
        }));
        Assert.Equal("deny", Decision(sharedTest));
        Assert.Contains("recurso compartilhado", sharedTest["hookSpecificOutput"]?["permissionDecisionReason"]?.GetValue<string>() ?? "");
    }

    private void PrepareSqlTicket(string id, string repositoryId)
    {
        var ticketDirectory = Path.Combine(_data, "tickets", id.ToLowerInvariant());
        Directory.CreateDirectory(ticketDirectory);
        var repository = Path.Combine(_root, "git", repositoryId);
        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(ticketDirectory, "ticket.json"), new JsonObject
        {
            ["id"] = id,
            ["repositoryId"] = repositoryId,
            ["repository"] = repository
        }.ToJsonString());
        File.WriteAllText(Path.Combine(ticketDirectory, "runtime.json"), "{\"runId\":\"run-hook-fixture\",\"phase\":\"running\",\"pid\":" + Environment.ProcessId + "}");

        var stateDirectory = Path.Combine(_state, repositoryId.ToLowerInvariant(), id.ToLowerInvariant());
        Directory.CreateDirectory(Path.Combine(stateDirectory, "scratch"));
        const string scope = "Plano de teste remoto somente leitura para validar o destino SQL.";
        File.WriteAllText(Path.Combine(stateDirectory, "escopo.md"), scope);
        File.WriteAllText(Path.Combine(stateDirectory, "estado.json"), "{\"stage\":\"testes\"}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        File.WriteAllText(Path.Combine(ticketDirectory, "approval.json"), new JsonObject { ["scopeHash"] = hash }.ToJsonString());
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_ID", id);
    }

    [Fact]
    public void PreTool_allows_analysis_reads_and_ticket_state_but_denies_unplanned_writes_and_publish()
    {
        var policy = new HookPolicy(_data);
        var stateDir = Path.Combine(_state, "ss_erp_tag1", "ger5811");
        var tree = Path.Combine(_root, "worktree"); Directory.CreateDirectory(tree);
        var ticketPath = Path.Combine(_data, "tickets", "ger5811", "ticket.json");
        var ticket = JsonNode.Parse(File.ReadAllText(ticketPath))!.AsObject(); ticket["worktree"] = tree; ticket["prepared"] = true;
        File.WriteAllText(ticketPath, ticket.ToJsonString());

        var read = policy.PreTool(Input("Bash", new JsonObject { ["command"] = "git status --short" }));
        var stateWrite = policy.PreTool(Input("Write", new JsonObject { ["file_path"] = Path.Combine(stateDir, "escopo.md") }));
        var sourceWrite = policy.PreTool(Input("Write", new JsonObject { ["file_path"] = Path.Combine(tree, "code.cs") }));
        var publish = policy.PreTool(Input("Bash", new JsonObject { ["command"] = "git push origin HEAD" }));

        Assert.Equal("allow", Decision(read));
        Assert.Equal("allow", Decision(stateWrite));
        Assert.Equal("deny", Decision(sourceWrite));
        Assert.Equal("deny", Decision(publish));
    }

    [Fact]
    public void Approval_binds_exact_phrase_to_session_plan_and_file_budget()
    {
        var policy = new HookPolicy(_data);
        var stateFile = Path.Combine(_state, "ss_erp_tag1", "ger5811", "estado.json");
        var scopeFile = Path.Combine(_state, "ss_erp_tag1", "ger5811", "escopo.md");
        File.WriteAllText(scopeFile, "Plano para ss_erp_tag1: alterar somente code.cs, estimativa 1 linha.");
        var state = new JsonObject
        {
            ["stage"] = "aguardando_aprovacao",
            ["component_plans"] = new JsonObject { ["ss_erp_tag1"] = new JsonObject { ["mode"] = "change", ["planned_files"] = new JsonArray("code.cs") } },
            ["scope_guard"] = new JsonObject { ["max_planned_files"] = 6, ["max_planned_added_lines"] = 180, ["max_planned_deleted_lines"] = 120 },
            ["change_budget"] = new JsonObject { ["planned_files"] = new JsonArray("code.cs"), ["estimated_added_lines"] = 1, ["estimated_deleted_lines"] = 0 }
        };
        File.WriteAllText(stateFile, state.ToJsonString());

        Assert.Null(policy.ApprovalPrompt(new JsonObject { ["prompt"] = "Exemplo APROVAR GER5811", ["session_id"] = "session-fixture" }));
        Assert.Throws<InvalidDataException>(() => policy.ApprovalPrompt(new JsonObject { ["prompt"] = "APROVAR GER5811", ["session_id"] = "outra-sessao" }));
        var accepted = policy.ApprovalPrompt(new JsonObject { ["prompt"] = "APROVAR GER5811", ["session_id"] = ReadSessionId() });
        Assert.NotNull(accepted);
        Assert.True(File.Exists(Path.Combine(_data, "tickets", "ger5811", "approval.json")));
        var ticket = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "ger5811", "ticket.json")))!.AsObject();
        ticket["worktree"] = Path.Combine(_root, "worktree"); ticket["prepared"] = true; Directory.CreateDirectory(ticket["worktree"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(_data, "tickets", "ger5811", "ticket.json"), ticket.ToJsonString());
        var edit = policy.PreTool(Input("Write", new JsonObject { ["file_path"] = Path.Combine(ticket["worktree"]!.GetValue<string>(), "code.cs") }));
        Assert.Equal("allow", Decision(edit));
        state["change_budget"]!["estimated_added_lines"] = 2;
        File.WriteAllText(stateFile, state.ToJsonString());
        Assert.Equal("deny", Decision(policy.PreTool(Input("Write", new JsonObject { ["file_path"] = Path.Combine(ticket["worktree"]!.GetValue<string>(), "code.cs") }))));
    }

    [Fact]
    public async Task Hook_processor_updates_session_and_phase_usage_without_losing_existing_ticket_data()
    {
        var ticketFile = Path.Combine(_data, "tickets", "ger5811", "ticket.json");
        var original = JsonNode.Parse(File.ReadAllText(ticketFile))!.AsObject();
        var sessionId = original["sessionId"]!.GetValue<string>();
        original["customField"] = "preserve-me";
        File.WriteAllText(ticketFile, original.ToJsonString());
        var processor = new HookProcessor(_data);

        await processor.HandleAsync(new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = sessionId });
        await processor.HandleAsync(new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = sessionId, ["prompt"] = "status?" });

        var saved = JsonNode.Parse(File.ReadAllText(ticketFile))!;
        Assert.Equal("preserve-me", saved["customField"]?.GetValue<string>());
        Assert.True(saved["sessionStarted"]?.GetValue<bool>());
        var metrics = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "ger5811", "metrics.json")))!;
        Assert.Equal(1, metrics["totals"]?["prompts"]?.GetValue<int>());
        Assert.True(metrics["totals"]?["estimated_input_tokens"]?.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Hook_processor_adds_new_managed_session_id_without_reparenting_existing_json_array()
    {
        var ticketFile = Path.Combine(_data, "tickets", "ger5811", "ticket.json");
        var original = JsonNode.Parse(File.ReadAllText(ticketFile))!.AsObject();
        var previousSessionId = original["sessionId"]!.GetValue<string>();
        var newSessionId = "managed-session-fixture";
        var processor = new HookProcessor(_data);

        await processor.HandleAsync(new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = newSessionId });

        var saved = JsonNode.Parse(File.ReadAllText(ticketFile))!.AsObject();
        Assert.Equal(newSessionId, saved["sessionId"]?.GetValue<string>());
        Assert.Equal(new[] { previousSessionId, newSessionId }, saved["sessionIds"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.True(saved["sessionStarted"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Codex_session_lifecycle_records_metrics_and_queues_learning_on_end()
    {
        var ticketPath = Path.Combine(_data, "tickets", "ger5811", "ticket.json");
        var ticket = JsonNode.Parse(File.ReadAllText(ticketPath))!.AsObject();
        ticket["agent"] = "codex";
        File.WriteAllText(ticketPath, ticket.ToJsonString());
        var configPath = Path.Combine(_data, "config.json");
        File.WriteAllText(configPath, "{\"repository\":\"" + _repo.Replace("\\", "\\\\") + "\",\"state_base_root\":\"" + _state.Replace("\\", "\\\\") + "\",\"ai_memory_url\":\"http://127.0.0.1:1\"}");
        var processor = new HookProcessor(_data);

        await processor.HandleCodexSessionEventAsync("GER5811", "run-hook-fixture", "SessionStart");
        await processor.HandleCodexSessionEventAsync("GER5811", "run-hook-fixture", "SessionEnd");

        var metrics = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "ger5811", "metrics.json")))!;
        Assert.NotNull(metrics["started_at"]);
        Assert.NotNull(metrics["ended_at"]);
        Assert.NotNull(metrics["phases"]?["outro"]?["ended_at"]);
        Assert.Equal("queued", metrics["memory_sync"]?.AsArray().LastOrDefault()?["status"]?.GetValue<string>());
        Assert.Contains("GER5811", File.ReadAllText(Path.Combine(_data, "ai-memory-outbox.jsonl")));
    }

    [Fact]
    public async Task Codex_session_event_rejects_a_stale_run_reservation()
    {
        var ticketPath = Path.Combine(_data, "tickets", "ger5811", "ticket.json");
        var ticket = JsonNode.Parse(File.ReadAllText(ticketPath))!.AsObject();
        ticket["agent"] = "codex";
        File.WriteAllText(ticketPath, ticket.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HookProcessor(_data).HandleCodexSessionEventAsync("GER5811", "stale-run", "SessionStart"));
        Assert.False(File.Exists(Path.Combine(_data, "tickets", "ger5811", "metrics.json")));
    }

    private string ReadSessionId() => JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "ger5811", "ticket.json")))!["sessionId"]!.GetValue<string>();

    private static JsonObject Input(string tool, JsonObject args) => new() { ["tool_name"] = tool, ["tool_input"] = args };
    private static string? Decision(JsonObject output) => output["hookSpecificOutput"]?["permissionDecision"]?.GetValue<string>();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CENTRAL_TICKETS_DATA", _oldData);
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_ID", _oldId);
        Environment.SetEnvironmentVariable("CENTRAL_TICKET_RUN", _oldRun);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
