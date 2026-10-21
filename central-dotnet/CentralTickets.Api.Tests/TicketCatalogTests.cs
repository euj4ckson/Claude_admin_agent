using System.Text.Json;

namespace CentralTickets.Api.Tests;

public sealed class TicketCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-dotnet-tests-{Guid.NewGuid():N}");
    private readonly string _dataRoot;
    private readonly string _stateRoot;
    private readonly string _referenceRoot;

    public TicketCatalogTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _stateRoot = Path.Combine(_root, "state");
        _referenceRoot = Path.Combine(_root, "references");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_stateRoot);
        Directory.CreateDirectory(_referenceRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "config.json"), "{\"repository\":\"C:/git/ss_erp_tag1\"}");
    }

    [Fact]
    public void List_reads_existing_json_and_separates_active_and_completed_tickets()
    {
        AddTicket("GER5800", "concluido", "Ticket entregue");
        AddTicket("GER5801", "testes", "Ticket em andamento");
        var catalog = new TicketCatalog(_dataRoot, _stateRoot, _referenceRoot);

        var all = catalog.List("all", null, null, null, 500);
        var active = catalog.List("active", null, null, null, 500);
        var done = catalog.List("done", null, null, null, 500);

        Assert.Equal(2, all.Count);
        Assert.Equal("GER5801", Assert.Single(active).Id);
        Assert.Equal("GER5800", Assert.Single(done).Id);
    }

    [Fact]
    public void List_applies_query_repository_agent_and_limit_filters()
    {
        AddTicket("GER5800", "testes", "Corrigir emissão", "ss_erp_tag1", "claude");
        AddTicket("FVA0351", "analise", "Integrar estoque", "ss_fva", "codex");
        var catalog = new TicketCatalog(_dataRoot, _stateRoot, _referenceRoot);

        Assert.Equal("FVA0351", Assert.Single(catalog.List("all", "estoque", "ss_fva", "codex", 25)).Id);
        Assert.Single(catalog.List("all", null, null, null, 1));
    }

    [Fact]
    public void List_uses_legacy_default_repository_state_folder_when_record_has_no_repository()
    {
        AddTicket("GER5800", "revisao", "Ticket antigo", repositoryId: null, agent: "claude");
        var catalog = new TicketCatalog(_dataRoot, _stateRoot, _referenceRoot);

        var ticket = Assert.Single(catalog.List("all", null, null, null, 10));

        Assert.Equal("revisao", ticket.Stage);
        Assert.Equal("ss_erp_tag1", ticket.RepositoryId);
    }

    [Fact]
    public void GetDetails_returns_scope_state_and_recent_history()
    {
        AddTicket("GER5800", "analise", "Ticket de detalhe");
        var ticketDirectory = Path.Combine(_dataRoot, "tickets", "ger5800");
        File.WriteAllText(Path.Combine(ticketDirectory, "events.jsonl"),
            "{\"at\":\"2026-10-21T12:00:00Z\",\"type\":\"cadastro\",\"message\":\"Ticket cadastrado\"}\n");
        var catalog = new TicketCatalog(_dataRoot, _stateRoot, _referenceRoot);

        var details = catalog.GetDetails("GER5800");

        Assert.NotNull(details);
        Assert.Equal("Escopo mínimo de teste para validar o ticket.", details.Scope);
        Assert.Equal("analise", details.Stage);
        Assert.Equal("cadastro", Assert.Single(details.Events).Type);
        Assert.Null(catalog.GetDetails("../../fora"));
    }

    [Fact]
    public void List_reports_ticket_risk_and_quality_from_saved_state()
    {
        AddTicket("GER5802", "revisao", "Teste com falha registrada");
        var stateFile = Path.Combine(_stateRoot, "ss_erp_tag1", "ger5802", "estado.json");
        File.WriteAllText(stateFile, """
            {"stage":"revisao","scope_revision":2,"tests":[{"id":"T1","result":"FALHA: comparação não bate"}],"build":[],"review":{"verdict":"pendente"}}
            """);
        var ticketDirectory = Path.Combine(_dataRoot, "tickets", "ger5802");
        File.WriteAllText(Path.Combine(ticketDirectory, "metrics.json"), """
            {"totals":{"estimated_input_tokens":120,"estimated_output_tokens":45},"scope_expansions":[{}],"phases":{"bloqueado":{"seconds":90}}}
            """);

        var summary = Assert.Single(new TicketCatalog(_dataRoot, _stateRoot, _referenceRoot).List("all", null, null, null, 10));
        var metrics = JsonSerializer.SerializeToNode(summary.Metrics);

        Assert.Equal("red", summary.Risk.Level);
        Assert.Contains("FALHA", summary.Risk.Reasons[0]);
        Assert.False(summary.Quality.CommitReady);
        Assert.Equal(1, metrics?["scope_expansions"]?.GetValue<int>());
        Assert.Equal(90, metrics?["blocked_seconds"]?.GetValue<int>());
        Assert.Equal(165, metrics?["tokens"]?["estimated_total"]?.GetValue<int>());
    }

    private void AddTicket(string id, string stage, string title, string? repositoryId = "ss_erp_tag1", string agent = "claude")
    {
        var ticketDirectory = Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant());
        Directory.CreateDirectory(ticketDirectory);
        var record = new
        {
            id,
            ticket = id,
            sprint = "166",
            release = "main",
            scope = "Escopo mínimo de teste para validar o ticket.",
            title,
            delivery = "local_commit_only",
            agent,
            repositoryId,
            repository = repositoryId is null or "ss_erp_tag1" ? "C:/git/ss_erp_tag1" : "C:/git/ss_fva",
            prepared = true,
            updatedAt = "2026-10-21T12:00:00.000Z",
            attachments = Array.Empty<string>(),
            deliveryDecision = stage == "concluido"
                ? new { requestedBy = "usuario", scopeRevision = 1, at = "2026-10-21T12:00:00.000Z" }
                : null
        };
        File.WriteAllText(Path.Combine(ticketDirectory, "ticket.json"), JsonSerializer.Serialize(record));

        var repoState = Path.Combine(_stateRoot, repositoryId ?? "ss_erp_tag1", id.ToLowerInvariant());
        Directory.CreateDirectory(repoState);
        File.WriteAllText(Path.Combine(repoState, "estado.json"), JsonSerializer.Serialize(new { stage, scope_revision = 1 }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
