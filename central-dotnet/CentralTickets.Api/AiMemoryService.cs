using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

namespace CentralTickets.Api;

/// <summary>Sanitizes ticket learnings, writes them to AI Memory, and durably queues failures.</summary>
public sealed class AiMemoryService(string dataRoot, CentralProfile profile, IHttpClientFactory clients)
{
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private readonly string _dataRoot = dataRoot;
    private readonly CentralProfile _profile = profile;
    private readonly IHttpClientFactory _clients = clients;
    private string Outbox => Path.Combine(_dataRoot, "ai-memory-outbox.jsonl");

    public async Task<JsonObject> SyncTicket(TicketDetails ticket, CancellationToken cancellationToken)
    {
        var state = ticket.State is { } json ? JsonNode.Parse(json.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
        var learning = CreateLearning(ticket, state);
        learning["fingerprint"] = Fingerprint(ticket, state);
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            var flushed = await Flush(cancellationToken);
            var result = await WriteOrQueue(learning, cancellationToken);
            result["sent"] = flushed.Sent + (GetString(result, "remote") == "synced" ? 1 : 0);
            result["pending"] = flushed.Pending + (GetString(result, "remote") == "queued" ? 1 : 0);
            result["fingerprint"] = GetString(learning, "fingerprint");
            AppendSync(ticket.Id, result);
            return result;
        }
        finally { SyncLock.Release(); }
    }

    public async Task<JsonObject> SyncDelivered(TicketCatalog catalog, CancellationToken cancellationToken)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            var flushed = await Flush(cancellationToken);
            var result = new JsonObject { ["generated"] = 0, ["sent"] = flushed.Sent, ["pending"] = flushed.Pending, ["alreadyCurrent"] = 0 };
            foreach (var summary in catalog.List("done", null, null, null, 500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ticket = catalog.GetDetails(summary.Id);
                if (ticket is null) continue;
                var state = ticket.State is { } json ? JsonNode.Parse(json.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
                var fingerprint = Fingerprint(ticket, state);
                var prior = ReadMemoryRows(ticket.Id).OfType<JsonObject>().LastOrDefault(row => GetString(row, "fingerprint") == fingerprint);
                if (GetString(prior, "status") == "synced") { result["alreadyCurrent"] = result["alreadyCurrent"]!.GetValue<int>() + 1; continue; }
                if (GetString(prior, "status") == "queued") { result["pending"] = result["pending"]!.GetValue<int>() + 1; continue; }
                var learning = CreateLearning(ticket, state);
                learning["fingerprint"] = fingerprint;
                learning["centralFingerprint"] = fingerprint;
                var response = await WriteOrQueue(learning, cancellationToken);
                result["generated"] = result["generated"]!.GetValue<int>() + 1;
                result[GetString(response, "remote") == "synced" ? "sent" : "pending"] = result[GetString(response, "remote") == "synced" ? "sent" : "pending"]!.GetValue<int>() + 1;
                AppendSync(ticket.Id, response);
            }
            return result;
        }
        finally { SyncLock.Release(); }
    }

    private async Task<(int Sent, int Pending)> Flush(CancellationToken cancellationToken)
    {
        if (!File.Exists(Outbox)) return (0, 0);
        var rows = new List<JsonObject>(); var invalid = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(Outbox, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonNode.Parse(line) is JsonObject row) rows.Add(row); else invalid.Add(line); }
            catch (JsonException) { invalid.Add(line); }
        }
        var sent = 0; var remaining = new List<string>(invalid);
        foreach (var row in rows)
        {
            try
            {
                await Send(row, cancellationToken); sent++;
                if (GetString(row, "ticket") is { } id) AppendSync(id, new JsonObject { ["remote"] = "synced", ["detail"] = "Aprendizado enviado pela fila local.", ["fingerprint"] = GetString(row, "fingerprint") });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { remaining.Add(row.ToJsonString()); }
        }
        if (remaining.Count == 0) File.Delete(Outbox);
        else await AtomicWrite(Outbox, string.Join(Environment.NewLine, remaining) + Environment.NewLine, cancellationToken);
        return (sent, remaining.Count);
    }

    private async Task<JsonObject> WriteOrQueue(JsonObject learning, CancellationToken cancellationToken)
    {
        try
        {
            var page = LearningPage(learning);
            await Send(new JsonObject { ["id"] = GetString(learning, "id"), ["ticket"] = GetString(learning, "ticket"), ["path"] = page.Path, ["body"] = page.Body }, cancellationToken);
            return new JsonObject { ["remote"] = "synced", ["detail"] = $"Conhecimento gravado no AI Memory ({page.Path}).", ["fingerprint"] = GetString(learning, "fingerprint") };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Directory.CreateDirectory(_dataRoot);
            await File.AppendAllTextAsync(Outbox, learning.ToJsonString() + Environment.NewLine, cancellationToken);
            return new JsonObject { ["remote"] = "queued", ["detail"] = $"{Redact(ex.Message)} Registro preservado na fila local.", ["fingerprint"] = GetString(learning, "fingerprint") };
        }
    }

    private async Task Send(JsonObject row, CancellationToken cancellationToken)
    {
        var page = GetString(row, "path") is { } savedPath && GetString(row, "body") is { } savedBody
            ? (Path: savedPath, Body: savedBody) : LearningPage(row);
        var baseUrl = (_profile.GetString("ai_memory_url") ?? "http://127.0.0.1:49374").TrimEnd('/');
        using var client = _clients.CreateClient("ai-memory");
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var payload = new { jsonrpc = "2.0", id = GetString(row, "id") ?? Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name = "memory_write_page", arguments = new { path = page.Path, body = page.Body, tier = "semantic", tags = new[] { "central-tickets", (GetString(row, "ticket") ?? "").ToLowerInvariant() } } } };
        using var response = await client.PostAsJsonAsync(baseUrl + "/mcp", payload, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AI Memory respondeu HTTP {(int)response.StatusCode}.");
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken) ?? new JsonObject();
        if (body["error"] is JsonObject error)
            throw new InvalidOperationException("AI Memory rejeitou a gravação: " + Redact(error["message"]?.ToString() ?? "erro MCP"));
        if (body["result"]?["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("AI Memory rejeitou a gravação: MCP recusou a gravação.");
    }

    private static JsonObject CreateLearning(TicketDetails ticket, JsonObject state)
    {
        var review = state["review"] as JsonObject ?? new JsonObject();
        var metrics = ticket.Metrics is null ? new JsonObject() : JsonSerializer.SerializeToNode(ticket.Metrics) as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["source"] = "central-tickets",
            ["ticket"] = ticket.Id, ["repository"] = ticket.RepositoryId, ["title"] = Redact(ticket.Title),
            ["verdict"] = Redact(GetString(review, "verdict") ?? GetString(review, "resultado") ?? ""),
            ["confirmed"] = SafeList(review["confirmed"]), ["escaped_findings"] = SafeList(review["suspected"] ?? review["findings"]),
            ["scope_expansions"] = GetInt(metrics, "scope_expansions"),
            ["tests_not_executed"] = GetInt(metrics, "tests_not_executed"),
            ["quality"] = new JsonObject { ["commit_ready"] = ticket.Quality.CommitReady, ["delivery_ready"] = ticket.Quality.DeliveryReady }
        };
    }

    private static (string Path, string Body) LearningPage(JsonObject learning)
    {
        var ticket = Regex.Replace((GetString(learning, "ticket") ?? "unknown").ToUpperInvariant(), "[^A-Z0-9_-]", "-");
        if (!Regex.IsMatch(ticket, "^[A-Z0-9_-]{1,40}$")) throw new InvalidOperationException("Identificador de ticket invÃ¡lido para o AI Memory.");
        static string Bullets(JsonNode? value, string fallback) => value is JsonArray array && array.Count > 0 ? string.Join('\n', array.Select(item => "- " + Redact(item?.ToString() ?? ""))) : "- " + fallback;
        var body = $"# {ticket} â€” {Redact(GetString(learning, "title") ?? "Aprendizado da Central")}\n\n- Origem: Central de Tickets ({Redact(GetString(learning, "repository") ?? "repositÃ³rio nÃ£o informado")})\n- Atualizado em: {Redact(GetString(learning, "at") ?? DateTimeOffset.UtcNow.ToString("O"))}\n- RevisÃ£o: {Redact(GetString(learning, "verdict") ?? "sem conclusÃ£o registrada")}\n- ExpansÃµes de escopo: {learning["scope_expansions"]?.ToString() ?? "0"}\n- Testes nÃ£o executados: {learning["tests_not_executed"]?.ToString() ?? "0"}\n\n## EvidÃªncias confirmadas\n{Bullets(learning["confirmed"], "Nenhuma evidÃªncia confirmada registrada.")}\n\n## Achados que escaparam para revisÃ£o\n{Bullets(learning["escaped_findings"], "Nenhum achado registrado.")}";
        var quality = learning["quality"] as JsonObject;
        body += $"\n- Pronto para commit: {(quality?["commit_ready"]?.GetValue<bool>() == true ? "sim" : "nÃ£o")}; pronto para entrega: {(quality?["delivery_ready"]?.GetValue<bool>() == true ? "sim" : "nÃ£o")}";
        return ($"notes/central-tickets/{ticket.ToLowerInvariant()}.md", body);
    }

    private void AppendSync(string id, JsonObject result)
    {
        var dir = Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant()); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "metrics.json"); JsonObject metrics;
        try { metrics = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (IOException) { metrics = new JsonObject(); }
        catch (JsonException) { metrics = new JsonObject(); }
        var rows = metrics["memory_sync"] as JsonArray ?? new JsonArray(); metrics["memory_sync"] = rows;
        rows.Add(new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["status"] = GetString(result, "remote") == "synced" ? "synced" : "queued", ["detail"] = GetString(result, "detail"), ["fingerprint"] = GetString(result, "fingerprint") });
        var temp = path + ".tmp"; File.WriteAllText(temp, metrics.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }

    private JsonArray ReadMemoryRows(string id)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(_dataRoot, "tickets", id.ToLowerInvariant(), "metrics.json")))?["memory_sync"] as JsonArray ?? new JsonArray(); }
        catch (IOException) { return new JsonArray(); }
        catch (JsonException) { return new JsonArray(); }
    }

    private static string Fingerprint(TicketDetails ticket, JsonObject state)
    {
        var reducedTests = new JsonArray((state["tests"] as JsonArray ?? []).Select(test => (JsonNode?)new JsonObject
        {
            ["id"] = test?["id"]?.DeepClone(), ["result"] = test?["result"]?.DeepClone() ?? test?["status"]?.DeepClone(),
            ["name"] = test?["name"]?.DeepClone(), ["type"] = test?["type"]?.DeepClone()
        }).ToArray());
        var stable = new JsonObject { ["ticket"] = ticket.Id, ["repository"] = ticket.RepositoryId ?? Path.GetFileName(ticket.Repository), ["title"] = ticket.Title,
            ["delivery"] = ticket.Delivery, ["registration_updated"] = ticket.UpdatedAt, ["state_updated"] = GetString(state, "updated_at"),
            ["stage"] = GetString(state, "stage"), ["scope_revision"] = state["scope_revision"]?.DeepClone(), ["review"] = state["review"]?.DeepClone(), ["tests"] = reducedTests };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stable.ToJsonString())));
    }

    private static JsonArray SafeList(JsonNode? source) => source is JsonArray values ? new JsonArray(values.Take(12).Select(item => (JsonNode?)JsonValue.Create(Redact(item?.ToString() ?? ""))).ToArray()) : new JsonArray();
    private static string Redact(string text) { var safe = Regex.Replace(text ?? "", "(password|pwd|token|api[_-]?key|connectionstring)\\s*[=:]\\s*[^\\s;]+", "$1=[REDACTED]", RegexOptions.IgnoreCase); return safe[..Math.Min(4000, safe.Length)]; }
    private static string? GetString(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int GetInt(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0 ? number : 0;
    private static async Task AtomicWrite(string path, string content, CancellationToken cancellationToken) { var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; try { await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); } }
}
