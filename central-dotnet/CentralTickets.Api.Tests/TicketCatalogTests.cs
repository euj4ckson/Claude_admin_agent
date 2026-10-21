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
