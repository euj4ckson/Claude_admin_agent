using System.Net;
using System.Text;
using System.Text.Json;
using CentralTickets.Api;
using Microsoft.Extensions.Http;

namespace CentralTickets.Api.Tests;

public sealed class AzureDevOpsClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-azure-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _state;
    private readonly string _references;
    private readonly string _repo;
    private readonly string _defaultRepo;
    private readonly string _agent;

    public AzureDevOpsClientTests()
    {
        _data = Path.Combine(_root, "data");
        _state = Path.Combine(_root, "state");
        _references = Path.Combine(_root, "references");
        _repo = Path.Combine(_root, "git", "ss_fva");
        _defaultRepo = Path.Combine(_root, "git", "ss_erp_tag1");
        _agent = Path.Combine(_root, "claude.exe");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(Path.Combine(_defaultRepo, ".git"));
        File.WriteAllText(_agent, "test executable placeholder");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _defaultRepo,
            repositories_root = Path.GetDirectoryName(_defaultRepo),
            claude = _agent,
            codex = (string?)null,
            references_root = _references,
            azure = new { organization = "sistemasunion", project = "SSUnion" }
        }));
    }

    [Fact]
    public async Task Preview_extracts_ticket_iteration_description_and_attachment_metadata()
    {
        using var handler = new AzureHandler((request, _) => JsonResponse(WorkItemJson()));
        var client = CreateClient(handler);

        var preview = await client.Preview("https://dev.azure.com/sistemasunion/SSUnion/_boards/board?workitem=1900", default);

        Assert.Equal("FVA0351", preview.Suggested.Ticket);
        Assert.Equal("166", preview.Suggested.Sprint);
        Assert.Equal("ss_fva", preview.Suggested.RepositoryId);
        Assert.Contains("pedido com kit", preview.Suggested.Scope, StringComparison.OrdinalIgnoreCase);
        Assert.Single(preview.Attachments);
        Assert.Equal("evidencia.txt", preview.Attachments[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(preview.PreviewId));
    }

    [Fact]
    public async Task Import_uses_confirmed_preview_and_downloads_selected_file_into_ticket_reference_folder()
    {
        using var handler = new AzureHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("attachments", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("evidência")) }
            : JsonResponse(WorkItemJson()));
        var client = CreateClient(handler);
        var preview = await client.Preview("1900", default);

        var components = JsonSerializer.SerializeToElement(new[] { new { repositoryId = "ss_erp_tag1", release = "release/106.4.3" } });
        var imported = await client.Import(new AzureImportRequest(
            "1900", preview.PreviewId, "FVA0351", "166", null, null, null,
            "ss_fva", "claude", "feature", "no_commit", ["evidencia.txt"], true, components), default);

        Assert.Equal(1, imported.Downloaded);
        Assert.Empty(imported.Warnings);
        Assert.True(File.Exists(Path.Combine(_references, "fva0351", "evidencia.txt")));
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(_data, "tickets", "fva0351", "ticket.json")));
        Assert.Equal("ss_fva", record.RootElement.GetProperty("repositoryId").GetString());
        Assert.Equal("1900", record.RootElement.GetProperty("azure_source").GetProperty("id").ToString());
        Assert.Equal("ss_erp_tag1", record.RootElement.GetProperty("components")[0].GetProperty("repositoryId").GetString());
    }

    [Theory]
    [InlineData("http://dev.azure.com/sistemasunion/SSUnion?workitem=1900")]
    [InlineData("https://evil.example/sistemasunion/SSUnion?workitem=1900")]
    [InlineData("https://dev.azure.com/another/SSUnion?workitem=1900")]
    [InlineData("https://dev.azure.com/sistemasunion/SSUnion/board")]
    public async Task Preview_rejects_non_azure_or_wrong_project_references(string reference)
    {
        using var handler = new AzureHandler((_, _) => throw new Xunit.Sdk.XunitException("Request should not reach Azure."));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<TicketOperationException>(() => client.Preview(reference, default));
    }

    [Fact]
    public async Task Preview_reports_authentication_failure_without_exposing_pat()
    {
        using var handler = new AzureHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => client.Preview("1900", default));

        Assert.Contains("PAT do Azure", error.Message);
        Assert.DoesNotContain("test-secret-pat-value", error.Message);
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_an_attachment_over_the_per_file_limit_before_creating_ticket()
    {
        var oversized = WorkItemJson().Replace("\"resourceSize\": 9", "\"resourceSize\": 52428801", StringComparison.Ordinal);
        using var handler = new AzureHandler((_, _) => JsonResponse(oversized));
        var client = CreateClient(handler);
        var preview = await client.Preview("1900", default);

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => client.Import(new AzureImportRequest(
            "1900", preview.PreviewId, "FVA0351", "166", null, null, null,
            "ss_fva", "claude", "feature", "no_commit", ["evidencia.txt"], true, null), default));

        Assert.Contains("50 MB", error.Message);
        Assert.False(File.Exists(Path.Combine(_data, "tickets", "fva0351", "ticket.json")));
    }

    [Fact]
    public async Task Configure_rejects_invalid_pat_without_writing_credentials()
    {
        var client = CreateClient(new AzureHandler((_, _) => throw new Xunit.Sdk.XunitException("No request expected.")));

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => client.Configure("short", default));

        Assert.Contains("PAT válido", error.Message);
        Assert.False(File.Exists(Path.Combine(_data, "azure-credentials.json")));
    }

    [Fact]
    public async Task Configure_protects_pat_with_current_windows_user_and_preview_can_read_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var observedAuthorization = "";
        using var handler = new AzureHandler((request, _) =>
        {
            observedAuthorization = request.Headers.Authorization?.Parameter ?? "";
            return JsonResponse(WorkItemJson());
        });
        var factory = new StubHttpClientFactory(handler);
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _state, profile);
        var client = new AzureDevOpsClient(_data, _references, profile, writer, factory);
        const string pat = "test-secret-pat-value-that-is-not-real";

        await client.Configure(pat, default);
        var credentialFile = File.ReadAllText(Path.Combine(_data, "azure-credentials.json"));
        Assert.DoesNotContain(pat, credentialFile);
        Assert.Contains("windows-dpapi", credentialFile);
        await client.Preview("1900", default);

        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")), observedAuthorization);
    }

    private AzureDevOpsClient CreateClient(HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _state, profile);
        return new AzureDevOpsClient(_data, _references, profile, writer, factory, _ => Task.FromResult("test-secret-pat-value"));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string WorkItemJson() => """
        {
          "id": 1900,
          "fields": {
            "System.Title": "FVA0351 - Salvar pedido com kit de venda",
            "System.Description": "<p>O pedido com kit deve ser salvo sem erro 0940.</p><p>Validar o envio do pedido e as regras de promoção.</p>",
            "System.State": "Approved",
            "System.Tags": "feature; Android",
            "System.IterationPath": "SSUnion\\Sprint 166"
          },
          "relations": [{
            "rel": "AttachedFile",
            "url": "https://dev.azure.com/sistemasunion/SSUnion/_apis/wit/attachments/abc",
            "attributes": { "name": "evidencia.txt", "resourceSize": 9, "comment": "captura de teste" }
          }]
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class AzureHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request, cancellationToken));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
    }
}
