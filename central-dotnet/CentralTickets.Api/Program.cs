using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using CentralTickets.Api;

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DOTNET_PORT"), out var configuredPort)
    ? configuredPort
    : 17861;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var dataRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralTicketsClaude");
var appRoot = FindApplicationRoot(builder.Environment.ContentRootPath, AppContext.BaseDirectory);
CentralConfiguration.Ensure(
    dataRoot,
    appRoot,
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    Environment.GetEnvironmentVariable("CENTRAL_TICKETS_REPOSITORIES_ROOT"));
var defaultStateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
var defaultReferenceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "Ref Dev");
var defaultWorktreesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "erp-worktrees");
var stateBaseRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_STATE_ROOT") ?? ConfiguredPath("state_base_root", defaultStateRoot);
var referenceRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_REFERENCES_ROOT") ?? ConfiguredPath("references_root", defaultReferenceRoot);
var worktreesBaseRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_WORKTREES_ROOT") ?? ConfiguredPath("worktrees_base_root", defaultWorktreesRoot);

var accessToken = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DOTNET_TOKEN")
    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
builder.Services.AddSingleton(new LocalAccess(accessToken));
builder.Services.AddAntiforgery(options =>
{
    options.FormFieldName = "__RequestVerificationToken";
    options.Cookie.Name = "central_dotnet_csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
});
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("azure", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("azure-attachments", client => client.Timeout = TimeSpan.FromMinutes(3));
var profile = new CentralProfile(dataRoot);
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton(new TicketCatalog(dataRoot, stateBaseRoot, referenceRoot));
builder.Services.AddSingleton(new TicketWriter(dataRoot, stateBaseRoot, profile, referenceRoot));
builder.Services.AddSingleton(new GitWorktreeService(dataRoot, stateBaseRoot, worktreesBaseRoot, profile));
builder.Services.AddSingleton<SessionService>(services => new(dataRoot, profile, services.GetRequiredService<GitWorktreeService>()));
builder.Services.AddSingleton<AzureDevOpsClient>(services => new(
    dataRoot, referenceRoot, profile, services.GetRequiredService<TicketWriter>(), services.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<AiMemoryService>(services => new(dataRoot, profile, services.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<UsageService>();
builder.Services.AddSingleton<CentralEventHub>();
builder.Services.AddHostedService(services => services.GetRequiredService<CentralEventHub>());

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (TicketOperationException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
    catch (BadHttpRequestException error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
    catch (Exception error)
    {
        var incidentId = context.TraceIdentifier;
        app.Logger.LogError(error, "Unhandled request {Method} {Path}; incident {IncidentId}.",
            context.Request.Method, context.Request.Path, incidentId);
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = $"Falha interna ao processar a solicitacao. Referencia: {incidentId}",
            incidentId
        });
    }
});
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'";

    if (!string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var access = context.RequestServices.GetRequiredService<LocalAccess>();
    if (context.Request.Path == "/bootstrap" && HttpMethods.IsGet(context.Request.Method))
    {
        if (!access.Matches(context.Request.Query["token"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Response.Cookies.Append("central_dotnet_session", access.SessionToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            IsEssential = true,
            Path = "/"
        });
        context.Response.Redirect("/");
        return;
    }

    if (!context.Request.Cookies.TryGetValue("central_dotnet_session", out var session)
        || !access.MatchesSession(session))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var isNativeActionForm = context.Request.Path == "/memory-sync"
        || Regex.IsMatch(context.Request.Path.Value ?? "", "^/tickets/[A-Za-z0-9]+/(start|memory-sync)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (HttpMethods.IsPost(context.Request.Method)
        && !isNativeActionForm
        && (!string.Equals(context.Request.Headers.Origin, $"http://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Request.Headers["X-Central-Request"], "1", StringComparison.Ordinal)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Solicitação de outra página bloqueada." });
        return;
    }

    if (HttpMethods.IsPost(context.Request.Method) && isNativeActionForm)
    {
        try
        {
            await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()
                .ValidateRequestAsync(context);
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "FormulÃ¡rio expirado. Atualize a pÃ¡gina e tente novamente." });
            return;
        }
    }

    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    app = "central-tickets-dotnet",
    version = "0.1.0",
    pid = Environment.ProcessId
}));

app.MapGet("/", (HttpRequest request, TicketCatalog catalog, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    var stage = request.Query["stage"].FirstOrDefault() ?? "active";
    if (stage is not ("active" or "attention" or "done" or "all")) stage = "active";
    var query = request.Query["q"].FirstOrDefault();
    var repository = request.Query["repository"].FirstOrDefault();
    var agent = request.Query["agent"].FirstOrDefault();
    var tickets = catalog.List(stage, query, repository, agent, 500);
    var all = catalog.List("all", null, null, null, 500);
    var profile = request.HttpContext.RequestServices.GetRequiredService<CentralProfile>();
    var memoryMessage = request.Query["memoryMessage"].FirstOrDefault();
    var memoryError = request.Query["memoryError"] == "1";
    var antiforgeryToken = antiforgery.GetAndStoreTokens(request.HttpContext).RequestToken;
    return Results.Content(TicketPageRenderer.Index(tickets, all, stage, query, profile.Repositories(), repository, agent, memoryMessage, memoryError, antiforgeryToken), "text/html; charset=utf-8");
});

app.MapGet("/tickets/{id}", (string id, HttpRequest request, TicketCatalog catalog, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    var ticket = catalog.GetDetails(id);
    var sessionMessage = request.Query["sessionMessage"].FirstOrDefault();
    var sessionError = request.Query["sessionError"] == "1";
    return ticket is null
        ? Results.Content(TicketPageRenderer.NotFound(id), "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound)
        : Results.Content(TicketPageRenderer.Details(ticket, sessionMessage, sessionError, antiforgery.GetAndStoreTokens(request.HttpContext).RequestToken), "text/html; charset=utf-8");
});

app.MapGet("/usage", () => Results.Content(TicketPageRenderer.Usage(), "text/html; charset=utf-8"));

app.MapGet("/api/tickets", (HttpRequest request, TicketCatalog catalog) =>
{
    var tickets = catalog.List(
        request.Query["stage"].FirstOrDefault() ?? "active",
        request.Query["q"].FirstOrDefault(),
        request.Query["repository"].FirstOrDefault(),
        request.Query["agent"].FirstOrDefault(),
        int.TryParse(request.Query["limit"], out var limit) ? limit : 250);
    return Results.Ok(tickets);
});

app.MapGet("/api/tickets/{id}", (string id, TicketCatalog catalog) =>
    catalog.GetDetails(id) is { } ticket ? Results.Ok(ticket) : Results.NotFound(new { error = "Ticket não encontrado." }));

app.MapPost("/api/tickets", (CreateTicketRequest input, TicketWriter writer) =>
{
    var created = writer.Create(input);
    return Results.Json(created, statusCode: StatusCodes.Status201Created);
});

app.MapPost("/api/tickets/{id}/registration", (string id, RegistrationUpdate input, TicketWriter writer) =>
{
    var updated = writer.UpdateRegistration(id, input);
    if (updated is null) return Results.NotFound(new { error = "Ticket não encontrado." });
    var body = JsonNode.Parse(updated.Value.GetRawText()) as JsonObject ?? new JsonObject();
    body["message"] = "Cadastro atualizado. Nenhuma sessão foi iniciada.";
    return Results.Json(body);
});

app.MapPost("/api/tickets/{id}/complete", (string id, TicketWriter writer) => Results.Ok(writer.CompleteByUser(id)));
app.MapPost("/api/tickets/{id}/delete", (string id, TicketWriter writer) => Results.Ok(writer.DeleteByUser(id)));
app.MapPost("/api/tickets/{id}/attachments", async (string id, HttpRequest request, TicketCatalog catalog, TicketWriter writer, CancellationToken cancellationToken) =>
{
    var ticket = catalog.GetDetails(id);
    if (ticket is null) return Results.NotFound(new { error = "Ticket não encontrado." });
    var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 52L * 1024 * 1024;
    var form = await request.ReadFormAsync(cancellationToken);
    if (form.Files.Count is < 1 or > 40) throw new TicketOperationException("Selecione entre 1 e 40 arquivos.");
    var directory = Path.GetFullPath(Path.Combine(referenceRoot, ticket.Id.ToLowerInvariant()));
    if (!IsWithin(referenceRoot, directory)) throw new TicketOperationException("Pasta de anexos inválida.", 403);
    Directory.CreateDirectory(directory);
    long total = 0; var written = new List<string>(); var skipped = new List<string>();
    foreach (var upload in form.Files)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(upload.FileName);
        if (string.IsNullOrWhiteSpace(name) || name != upload.FileName || name.Any(ch => ch < 32 || "<>:\"/\\|?*".Contains(ch))) throw new TicketOperationException("Nome de anexo inválido.");
        total += upload.Length;
        if (total > 50L * 1024 * 1024) throw new TicketOperationException("Os anexos ultrapassam o limite de 50 MB.", 413);
        var target = Path.GetFullPath(Path.Combine(directory, name));
        if (!IsWithin(directory, target)) throw new TicketOperationException("Arquivo de anexo fora da pasta permitida.", 403);
        if (File.Exists(target)) { skipped.Add(name); continue; }
        try
        {
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await upload.CopyToAsync(output, cancellationToken);
            written.Add(name);
        }
        catch (IOException) when (File.Exists(target)) { skipped.Add(name); }
    }
    if (written.Count > 0) writer.AddAttachments(ticket.Id, written);
    var updated = catalog.GetDetails(ticket.Id);
    return Results.Json(new { attachments = updated?.AttachmentInfo, message = $"{written.Count} anexo(s) adicionado(s){(skipped.Count > 0 ? $"; {skipped.Count} existente(s) preservado(s)" : "")}." }, statusCode: StatusCodes.Status201Created);
}).DisableAntiforgery();
app.MapGet("/api/tickets/{id}/content", (string id, string? kind, TicketCatalog catalog) =>
{
    var ticket = catalog.GetDetails(id);
    if (ticket is null) return Results.NotFound(new { error = "Ticket não encontrado." });
    if (kind is not ("plan" or "input")) throw new TicketOperationException("Conteúdo inválido.");
    var file = kind == "plan"
        ? Path.Combine(stateBaseRoot, ticket.RepositoryId ?? Path.GetFileName(ticket.Repository), ticket.Id.ToLowerInvariant(), "escopo.md")
        : Path.Combine(dataRoot, "tickets", ticket.Id.ToLowerInvariant(), "entrada.md");
    var root = kind == "plan" ? stateBaseRoot : dataRoot;
    if (!IsWithin(root, file)) throw new TicketOperationException("Arquivo fora da pasta do ticket.", 403);
    var content = File.Exists(file) ? File.ReadAllText(file) : null;
    var text = content is not null ? content[..Math.Min(150_000, content.Length)] : kind == "input" ? ticket.Scope : null;
    return Results.Ok(new { text });
});
app.MapPost("/api/tickets/{id}/prepare", async (string id, GitWorktreeService worktrees, CancellationToken cancellationToken) =>
    Results.Ok(await worktrees.Prepare(id, cancellationToken)));
app.MapGet("/api/tickets/{id}/preflight", async (string id, GitWorktreeService worktrees, CancellationToken cancellationToken) =>
    Results.Ok(await worktrees.Preflight(id, cancellationToken)));
app.MapGet("/api/tickets/{id}/diff", async (string id, GitWorktreeService worktrees, CancellationToken cancellationToken) =>
    Results.Ok(await worktrees.Diff(id, cancellationToken)));
app.MapPost("/api/tickets/{id}/memory-sync", async (string id, TicketCatalog catalog, AiMemoryService memory, CancellationToken cancellationToken) =>
{
    var ticket = catalog.GetDetails(id);
    return ticket is null ? Results.NotFound(new { error = "Ticket não encontrado." }) : Results.Ok(await memory.SyncTicket(ticket, cancellationToken));
});
app.MapPost("/api/tickets/{id}/start", async (string id, HttpRequest request, SessionService sessions, CancellationToken cancellationToken) =>
    Results.Ok(await sessions.Start(id, cancellationToken, request.Query["fresh"] == "1")));
app.MapPost("/tickets/{id}/start", async (string id, HttpRequest request, SessionService sessions, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await sessions.Start(id, cancellationToken, request.Query["fresh"] == "1");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
        return Results.Redirect($"/tickets/{Uri.EscapeDataString(id)}?sessionMessage={Uri.EscapeDataString(message ?? "Solicitação de início enviada à Central.")}");
    }
    catch (TicketOperationException error)
    {
        return Results.Redirect($"/tickets/{Uri.EscapeDataString(id)}?sessionError=1&sessionMessage={Uri.EscapeDataString(error.Message)}");
    }
});
app.MapPost("/tickets/{id}/memory-sync", async (string id, TicketCatalog catalog, AiMemoryService memory, CancellationToken cancellationToken) =>
{
    var ticket = catalog.GetDetails(id);
    if (ticket is null) return Results.Redirect($"/tickets/{Uri.EscapeDataString(id)}?sessionError=1&sessionMessage={Uri.EscapeDataString("Ticket não encontrado.")}");
    var result = await memory.SyncTicket(ticket, cancellationToken);
    var message = result["detail"]?.GetValue<string>() ?? "Sincronização registrada.";
    var error = result["remote"]?.GetValue<string>() != "synced";
    return Results.Redirect($"/tickets/{Uri.EscapeDataString(id)}?sessionError={(error ? "1" : "0")}&sessionMessage={Uri.EscapeDataString(message)}");
});
app.MapPost("/memory-sync", async (TicketCatalog catalog, AiMemoryService memory, CancellationToken cancellationToken) =>
{
    var result = await memory.SyncDelivered(catalog, cancellationToken);
    var sent = result["sent"]?.GetValue<int>() ?? 0;
    var pending = result["pending"]?.GetValue<int>() ?? 0;
    var current = result["alreadyCurrent"]?.GetValue<int>() ?? 0;
    var message = $"AI Memory: {sent} enviado(s), {pending} aguardando nova tentativa; {current} já atualizado(s).";
    return Results.Redirect($"/?memoryError={(pending > 0 ? "1" : "0")}&memoryMessage={Uri.EscapeDataString(message)}");
});
app.MapPost("/api/tickets/{id}/focus", (string id, SessionService sessions) => Results.Ok(sessions.Focus(id)));
app.MapGet("/api/tickets/{id}/metrics", (string id, TicketCatalog catalog) =>
{
    var ticket = catalog.GetDetails(id);
    return ticket is null
        ? Results.NotFound(new { error = "Ticket não encontrado." })
        : Results.Ok(new { ticket = ticket.Id, risk = ticket.Risk, quality = ticket.Quality, metrics = ticket.Metrics });
});
app.MapPost("/api/tickets/{id}/open", (string id, OpenTargetRequest input, TicketCatalog catalog) =>
{
    if (!OperatingSystem.IsWindows()) throw new TicketOperationException("Abrir caminhos locais é suportado somente no Windows.", 501);
    var ticket = catalog.GetDetails(id);
    if (ticket is null) return Results.NotFound(new { error = "Ticket não encontrado." });
    string target;
    if (input.Kind == "references")
    {
        target = Path.GetFullPath(Path.Combine(referenceRoot, ticket.Id.ToLowerInvariant()));
        if (!IsWithin(referenceRoot, target)) throw new TicketOperationException("Pasta de anexos inválida.", 403);
        Directory.CreateDirectory(target);
    }
    else if (input.Kind == "worktree")
    {
        if (string.IsNullOrWhiteSpace(ticket.Worktree) || !Directory.Exists(ticket.Worktree)) throw new TicketOperationException("A worktree ainda não existe.", 404);
        target = Path.GetFullPath(ticket.Worktree);
    }
    else if (input.Kind == "document")
    {
        var documentsRoot = ConfiguredPath("documents_root", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "doc dev"));
        target = Path.GetFullPath(Path.Combine(documentsRoot, ticket.Id.ToLowerInvariant()));
        if (!IsWithin(documentsRoot, target) || !Directory.Exists(target)) throw new TicketOperationException("A pasta do documento ainda não existe.", 404);
        var docs = Directory.EnumerateFiles(target, "*.docx").Where(file => !Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)).ToArray();
        if (docs.Length == 1) target = docs[0];
    }
    else throw new TicketOperationException("Ação de abertura inválida.");
    var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(target);
    _ = Process.Start(start) ?? throw new TicketOperationException("Não foi possível abrir o caminho no Windows.", 500);
    return Results.Ok(new { message = "Aberto no Windows." });
});

app.MapGet("/api/dashboard", (HttpRequest request, TicketCatalog catalog) =>
{
    var stage = request.Query["stage"].FirstOrDefault() ?? "active";
    var repository = request.Query["repository"].FirstOrDefault();
    var agent = request.Query["agent"].FirstOrDefault();
    var all = catalog.List("all", null, null, null, 500);
    var filtered = all.Where(ticket =>
        (stage != "active" || ticket.Stage != "concluido")
        && (stage != "done" || ticket.Stage == "concluido")
        && (string.IsNullOrWhiteSpace(repository) || string.Equals(ticket.RepositoryId, repository, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(agent) || string.Equals(ticket.Agent, agent, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    return Results.Ok(new { tickets = filtered, stats = TicketCatalog.Stats(all) });
});

app.MapGet("/api/stats", (TicketCatalog catalog) => Results.Ok(TicketCatalog.Stats(catalog.List("all", null, null, null, 500))));
app.MapGet("/api/usage", (UsageService usage) => Results.Ok(usage.ReadClaude()));
app.MapGet("/api/usage/codex", async (UsageService usage, CancellationToken cancellationToken) => Results.Ok(await usage.ReadCodex(cancellationToken)));
app.MapGet("/api/stream", async (HttpContext context, CentralEventHub hub) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    await context.Response.StartAsync(context.RequestAborted);
    await using var subscription = hub.Subscribe();
    await context.Response.WriteAsync("event: ready\ndata: {}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    try
    {
        await foreach (var item in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"event: {item.Name}\ndata: {item.Data.ToJsonString()}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
app.MapPost("/api/memory-sync", async (JsonElement _, TicketCatalog catalog, AiMemoryService memory, CancellationToken cancellationToken) =>
    Results.Ok(await memory.SyncDelivered(catalog, cancellationToken)));

app.MapGet("/api/profile", async (CentralProfile profile, IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
    Results.Ok(await profile.GetAsync(clientFactory, cancellationToken)));

app.MapGet("/api/azure/status", (AzureDevOpsClient azure) => Results.Ok(azure.Status()));
app.MapPost("/api/azure/configure", async (AzurePatRequest input, AzureDevOpsClient azure, CancellationToken cancellationToken) =>
    Results.Ok(await azure.Configure(input.Pat, cancellationToken)));
app.MapPost("/api/azure/preview", async (AzurePreviewRequest input, AzureDevOpsClient azure, CancellationToken cancellationToken) =>
    Results.Ok(await azure.Preview(input.Reference, cancellationToken)));
app.MapPost("/api/azure/import", async (AzureImportRequest input, AzureDevOpsClient azure, CancellationToken cancellationToken) =>
{
    var result = await azure.Import(input, cancellationToken);
    return Results.Json(result, statusCode: result.Warnings.Count == 0 ? StatusCodes.Status201Created : 207);
});
app.MapPost("/api/tickets/{id}/attachments/retry", async (string id, AzureDevOpsClient azure, CancellationToken cancellationToken) =>
    Results.Ok(await azure.RetryAttachments(id, cancellationToken)));

var launchFile = Path.Combine(dataRoot, "launch-dotnet.json");
Directory.CreateDirectory(dataRoot);
await File.WriteAllTextAsync(launchFile, JsonSerializer.Serialize(new
{
    pid = Environment.ProcessId,
    port,
    token = accessToken,
    app = "central-tickets-dotnet"
}, new JsonSerializerOptions { WriteIndented = true }));

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { File.Delete(launchFile); } catch (IOException) { }
});

app.Run();

static bool IsWithin(string root, string target)
{
    var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var fullPath = Path.GetFullPath(target);
    return fullPath.Equals(basePath, StringComparison.OrdinalIgnoreCase)
        || fullPath.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || fullPath.StartsWith(basePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

static string FindApplicationRoot(params string[] starts)
{
    foreach (var start in starts)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CentralTickets.DotNet.sln"))) return current.FullName;
            current = current.Parent;
        }
    }
    return Path.GetFullPath(starts[0]);
}

string ConfiguredPath(string property, string fallback)
{
    try
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "config.json")));
        if (configuration.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }
    }
    catch (IOException) { }
    catch (JsonException) { }
    return fallback;
}

public partial class Program;
public sealed record OpenTargetRequest(string Kind);
