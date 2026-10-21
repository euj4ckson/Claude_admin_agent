using System.Security.Cryptography;
using System.Text.Json;
using CentralTickets.Api;

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DOTNET_PORT"), out var configuredPort)
    ? configuredPort
    : 17862;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var dataRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralTicketsClaude");
var defaultStateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ticket-state");
var defaultReferenceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Claude Referencias", "Ref Dev");
var stateBaseRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_STATE_ROOT") ?? ConfiguredPath("state_base_root", defaultStateRoot);
var referenceRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_REFERENCES_ROOT") ?? ConfiguredPath("references_root", defaultReferenceRoot);

var accessToken = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DOTNET_TOKEN")
    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
builder.Services.AddSingleton(new LocalAccess(accessToken));
builder.Services.AddSingleton(new TicketCatalog(dataRoot, stateBaseRoot, referenceRoot));

var app = builder.Build();
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

    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    app = "central-tickets-dotnet",
    version = "0.1.0",
    pid = Environment.ProcessId
}));

app.MapGet("/", () => Results.Content("""
    <!doctype html>
    <html lang="pt-BR">
    <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Central de Tickets · C#</title></head>
    <body>
      <main>
        <h1>Central de Tickets · etapa C#</h1>
        <p>Esta versão é uma API de leitura em migração. O servidor Node continua sendo a Central completa.</p>
        <ul>
          <li><a href="/api/tickets?stage=active">Tickets em andamento</a></li>
          <li><a href="/api/tickets?stage=done">Tickets entregues</a></li>
          <li><a href="/api/dashboard?stage=active">Resumo do painel</a></li>
          <li><a href="/api/stats">Contagens</a></li>
        </ul>
        <p>Os dados dos tickets são somente leitura nesta etapa.</p>
      </main>
    </body>
    </html>
    """, "text/html; charset=utf-8"));

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
