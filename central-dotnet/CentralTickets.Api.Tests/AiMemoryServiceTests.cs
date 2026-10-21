using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class AiMemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-memory-{Guid.NewGuid():N}");

    [Fact]
    public async Task Successful_write_sends_sanitized_learning_to_memory()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), "{\"ai_memory_url\":\"http://memory.test\"}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent("{\"result\":{\"content\":[]}}") });
        var service = new AiMemoryService(_root, new CentralProfile(_root), new ClientFactory(handler));

        var result = await service.SyncTicket(Ticket(), CancellationToken.None);

        Assert.Equal("synced", result["remote"]?.GetValue<string>());
        Assert.Contains("notes/central-tickets/ger5811.md", handler.Body);
        var sentBody = JsonNode.Parse(handler.Body)?["params"]?["arguments"]?["body"]?.GetValue<string>() ?? "";
        Assert.Contains("escopo: 2", sentBody);
        Assert.Contains("executados: 3", sentBody);
        Assert.Contains("Pronto para commit: sim; pronto para entrega: sim", sentBody);
        Assert.DoesNotContain("password=hunter2", handler.Body);
        Assert.False(File.Exists(Path.Combine(_root, "ai-memory-outbox.jsonl")));
    }

    [Fact]
    public async Task Memory_failure_keeps_learning_in_local_outbox()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), "{\"ai_memory_url\":\"http://memory.test\"}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = new AiMemoryService(_root, new CentralProfile(_root), new ClientFactory(handler));

        var result = await service.SyncTicket(Ticket(), CancellationToken.None);

        Assert.Equal("queued", result["remote"]?.GetValue<string>());
        var outbox = File.ReadAllText(Path.Combine(_root, "ai-memory-outbox.jsonl"));
        Assert.Contains("GER5811", outbox);
        Assert.DoesNotContain("password=hunter2", outbox);
    }

    private static TicketDetails Ticket() => new(
        "GER5811", "GER5811", "166", "main", "Escopo seguro de teste", "Teste do AI Memory",
        "no_commit", "feature", "C:/git/ss_erp_tag1", "ss_erp_tag1", null, null, "claude", [],
        null, null, false, null, [], null, null, null, "entregue", "Entregue", JsonDocument.Parse("{\"stage\":\"entregue\",\"review\":{\"verdict\":\"OK password=hunter2\"},\"tests\":[]}").RootElement,
        null, "none", null, null, [], new AttachmentDetails("", false, [], []),
        new RiskSummary("none", "Histórico de entrega", []),
        new QualitySummary(true, true, [], new QualityCounts(0, 0, 0)), new { scope_expansions = 2, tests_not_executed = 3 });

    private static StringContent JsonContent(string content) => new(content, Encoding.UTF8, "application/json");
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
