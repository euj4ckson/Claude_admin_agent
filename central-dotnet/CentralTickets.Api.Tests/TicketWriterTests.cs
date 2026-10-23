using System.Text.Json;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class TicketWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-writer-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _state;
    private readonly string _repository;
    private readonly string _claude;

    public TicketWriterTests()
    {
        _data = Path.Combine(_root, "data");
        _state = Path.Combine(_root, "state");
        _repository = Path.Combine(_root, "git", "ss_erp_tag1");
        _claude = Path.Combine(_root, "claude.exe");
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
        Directory.CreateDirectory(_data);
        File.WriteAllText(_claude, "test executable placeholder");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _repository,
            repositories_root = Path.GetDirectoryName(_repository),
            claude = _claude,
            codex = (string?)null
        }));
    }

    [Fact]
    public void Create_persists_ticket_and_event_without_preparing_or_overwriting_state()
    {
        var writer = CreateWriter();

        var created = writer.Create(NewTicket("GER5800"));

        Assert.Equal("GER5800", created.GetProperty("id").GetString());
        Assert.Equal("no_commit", created.GetProperty("delivery").GetString());
        Assert.False(created.GetProperty("prepared").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_state, "ss_erp_tag1", "ger5800", "estado.json")));
        Assert.True(File.Exists(Path.Combine(_data, "tickets", "ger5800", "events.jsonl")));
        Assert.Throws<TicketOperationException>(() => writer.Create(NewTicket("GER5800")));
    }

    [Fact]
    public void Create_uses_repository_default_base_when_no_origin_was_provided()
    {
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _repository,
            repositories_root = Path.GetDirectoryName(_repository),
            claude = _claude,
            default_branch_by_repository = new Dictionary<string, string> { ["ss_erp_tag1"] = "release/106.4.3" }
        }));

        var writer = new TicketWriter(_data, _state, new CentralProfile(_data));
        var created = writer.Create(NewTicket("GER5805") with { Release = null });

        Assert.Equal("release/106.4.3", created.GetProperty("release").GetString());
    }

    [Fact]
    public void Delivery_can_be_changed_during_a_live_session_and_updates_the_state_gate()
    {
        var writer = CreateWriter();
        writer.Create(NewTicket("GER5801"));
        var ticketDirectory = Path.Combine(_data, "tickets", "ger5801");
        File.WriteAllText(Path.Combine(ticketDirectory, "runtime.json"), JsonSerializer.Serialize(new
        {
            phase = "running",
            pid = Environment.ProcessId,
            childPid = 0,
            startedAt = DateTimeOffset.UtcNow.ToString("O")
        }));
        var stateFile = Path.Combine(_state, "ss_erp_tag1", "ger5801", "estado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, "{\"stage\":\"implementacao\",\"scope_revision\":1,\"delivery\":{\"commit_allowed\":false,\"push_allowed\":false,\"pr_allowed\":false}} ");

        var updated = writer.UpdateRegistration("GER5801", new RegistrationUpdate(null, null, null, "local_commit_only", null, null));
        using var state = JsonDocument.Parse(File.ReadAllText(stateFile));

        Assert.Equal("local_commit_only", updated?.GetProperty("delivery").GetString());
        Assert.True(state.RootElement.GetProperty("delivery").GetProperty("commit_allowed").GetBoolean());
        Assert.False(state.RootElement.GetProperty("delivery").GetProperty("push_allowed").GetBoolean());
    }

    [Fact]
    public void Manual_completion_keeps_prior_state_in_history_and_records_user_decision()
    {
        var writer = CreateWriter();
        writer.Create(NewTicket("GER5802"));
        var stateFile = Path.Combine(_state, "ss_erp_tag1", "ger5802", "estado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, "{\"stage\":\"testes\",\"scope_revision\":2,\"blockers\":[\"homologar\"]}");

        var result = writer.CompleteByUser("GER5802");
        using var state = JsonDocument.Parse(File.ReadAllText(stateFile));
        var history = Directory.GetFiles(Path.Combine(_data, "tickets", "ger5802", "delivery-history"), "*.json");

        Assert.Equal("concluido", state.RootElement.GetProperty("stage").GetString());
        Assert.Equal("usuario", state.RootElement.GetProperty("delivered").GetProperty("user_decision").GetProperty("requestedBy").GetString());
        Assert.Equal("testes", JsonDocument.Parse(File.ReadAllText(history[0])).RootElement.GetProperty("state").GetProperty("stage").GetString());
        Assert.Equal("concluido", JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("stage").GetString());
    }

    private TicketWriter CreateWriter() => new(_data, _state, new CentralProfile(_data));

    private static CreateTicketRequest NewTicket(string id) => new(
        id,
        "166",
        "release/106.4.3",
        "Escopo de validação isolada para testar o cadastro de tickets.",
        "Ticket de teste",
        "no_commit",
        "feature",
        "claude",
        "ss_erp_tag1",
        null,
        [],
        null,
        null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
