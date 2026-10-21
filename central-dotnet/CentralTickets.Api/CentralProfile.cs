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

    public IReadOnlyList<CentralRepository> Repositories()
    {
        var configured = GetString("repository");
        if (string.IsNullOrWhiteSpace(configured)) return Array.Empty<CentralRepository>();
        var root = GetString("repositories_root") ?? Path.GetDirectoryName(configured)!;
        var entries = new List<CentralRepository>();

        try
        {
            foreach (var directory in EnumerateRepositories(root, 2))
            {
                var name = Path.GetFileName(directory);
                var isDefault = Path.GetFullPath(directory).Equals(Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase);
                entries.Add(new CentralRepository(name, name, directory, isDefault, StandardFor(name)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!entries.Any(item => item.Default) && (Directory.Exists(Path.Combine(configured, ".git")) || File.Exists(Path.Combine(configured, ".git"))))
            entries.Add(new CentralRepository(Path.GetFileName(configured), Path.GetFileName(configured), configured, true, null));

        return entries.OrderByDescending(item => item.Default).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateRepositories(string root, int maxDepth)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.TryDequeue(out var current))
        {
            if (IsGitRepository(current.Path))
            {
                yield return current.Path;
                if (current.Depth < maxDepth)
                {
                    IEnumerable<string> nested;
                    try { nested = Directory.EnumerateDirectories(current.Path).ToArray(); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    foreach (var directory in nested)
                    {
                        var eligible = false;
                        try { eligible = (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && IsGitRepository(directory); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        if (eligible) yield return directory;
                    }
                }
                continue;
            }
            if (current.Depth >= maxDepth) continue;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current.Path).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue((directory, current.Depth + 1));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsGitRepository(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    public CentralRepository? ResolveRepository(string value) => Repositories().FirstOrDefault(item =>
        string.Equals(item.Id, value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Path, value, StringComparison.OrdinalIgnoreCase));

    public string? AgentExecutable(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => GetString("claude"),
        "codex" => GetString("codex"),
        _ => null
    };

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

    private static RepositoryStandard? StandardFor(string name)
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
        return new RepositoryStandard(
            "main",
            "pre_main",
            wcf || normalized.Contains("modulo_especifico", StringComparison.Ordinal),
            wcf,
            true);
    }

}

public sealed record CentralRepository(string Id, string Name, string Path, bool Default, RepositoryStandard? Standard);
public sealed record RepositoryStandard(string Base, string Integration, bool PullBeforeStart, bool RestoreNuget, bool NoBinaryCommit);
