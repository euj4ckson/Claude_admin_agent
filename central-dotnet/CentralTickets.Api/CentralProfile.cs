using System.Text.Json;

namespace CentralTickets.Api;

public sealed class CentralProfile
{
    private readonly string _dataRoot;
    private readonly JsonDocument _settings;

    public CentralProfile(string dataRoot)
    {
        _dataRoot = dataRoot;
        _settings = ReadConfiguration(Path.Combine(dataRoot, "config.json"));
    }

    public string? GetString(string name) =>
        _settings.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public object Repositories()
    {
        var configured = GetString("repository");
        if (string.IsNullOrWhiteSpace(configured)) return Array.Empty<object>();
        var root = GetString("repositories_root") ?? Path.GetDirectoryName(configured)!;
        var entries = new List<RepositoryInfo>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git"))) continue;
                var name = Path.GetFileName(directory);
                var isDefault = Path.GetFullPath(directory).Equals(Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase);
                entries.Add(new RepositoryInfo(name, name, directory, isDefault, StandardFor(name)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!entries.Any(item => item.Default) && (Directory.Exists(Path.Combine(configured, ".git")) || File.Exists(Path.Combine(configured, ".git"))))
            entries.Add(new RepositoryInfo(Path.GetFileName(configured), Path.GetFileName(configured), configured, true, null));

        return entries.OrderByDescending(item => item.Default).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<object> GetAsync(IHttpClientFactory clientFactory, CancellationToken cancellationToken)
    {
        var repository = GetString("repository");
        var claude = GetString("claude");
        var codex = GetString("codex");
        var git = GetString("git_executable");
        var skill = GetString("skill");
        var outbox = Path.Combine(_dataRoot, "ai-memory-outbox.jsonl");
        var memory = false;

        try
        {
            using var client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(1);
            using var response = await client.GetAsync("http://127.0.0.1:49374/admin/status", cancellationToken);
            memory = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }

        return new
        {
            repository,
            repositories = Repositories(),
            agents = new[]
            {
                new { id = "claude", name = "Claude Code", available = Exists(claude) },
                new { id = "codex", name = "Codex CLI", available = Exists(codex) }
            },
            preferences = ReadJson(Path.Combine(_dataRoot, "preferences.json")),
            checks = new
            {
                claude = Exists(claude),
                git = Exists(git),
                repository = !string.IsNullOrWhiteSpace(repository) && (Directory.Exists(Path.Combine(repository, ".git")) || File.Exists(Path.Combine(repository, ".git"))),
                skill = !string.IsNullOrWhiteSpace(skill) && File.Exists(Path.Combine(skill, "SKILL.md"))
            },
            memory,
            aiMemory = new { reachable = memory, outbox, pending = CountLines(outbox) },
            executionOwner = ReadJson(Path.Combine(_dataRoot, "execution.lock")) is { } execution
                && execution.TryGetProperty("id", out var owner) ? owner.ToString() : null,
            demo = GetBoolean("demo"),
            azure = new
            {
                configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT")) || File.Exists(Path.Combine(_dataRoot, "azure-credentials.json")),
                organization = GetNestedString("azure", "organization") ?? "sistemasunion",
                project = GetNestedString("azure", "project") ?? "SSUnion",
                readOnly = true
            }
        };
    }

    private bool GetBoolean(string name) =>
        _settings.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private string? GetNestedString(string parent, string name) =>
        _settings.RootElement.TryGetProperty(parent, out var section)
        && section.ValueKind == JsonValueKind.Object
        && section.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static int CountLines(string file)
    {
        try { return File.ReadLines(file).Count(); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static JsonElement? ReadJson(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.Clone();
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static JsonDocument ReadConfiguration(string file)
    {
        try { return JsonDocument.Parse(File.ReadAllText(file)); }
        catch (IOException) { return JsonDocument.Parse("{}"); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static object? StandardFor(string name)
    {
        var normalized = name.ToLowerInvariant();
        var standardized = normalized.EndsWith("_pdv")
            || normalized.EndsWith("_fva")
            || normalized.EndsWith("_forca_de_vendas_web")
            || normalized.EndsWith("_wcf_fvan")
            || normalized.EndsWith("_modulo_especifico")
            || normalized.Contains("forca-de-vendas", StringComparison.Ordinal);
        if (!standardized) return null;
        var wcf = normalized.Contains("wcf_fvan", StringComparison.Ordinal);
        return new
        {
            @base = "main",
            integration = "pre_main",
            pull_before_start = wcf || normalized.Contains("modulo_especifico", StringComparison.Ordinal),
            restore_nuget = wcf,
            no_binary_commit = true
        };
    }

    private sealed record RepositoryInfo(string Id, string Name, string Path, bool Default, object? Standard);
}
