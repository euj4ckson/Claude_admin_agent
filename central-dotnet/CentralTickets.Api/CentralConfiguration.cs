using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentralTickets.Api;

/// <summary>Creates the local runtime configuration without rewriting an existing profile.</summary>
public static class CentralConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static JsonObject Ensure(string dataRoot, string appRoot, string userProfile, string localAppData, string roamingAppData, string? repositoriesRootOverride = null)
    {
        Directory.CreateDirectory(dataRoot);
        var file = Path.Combine(dataRoot, "config.json");
        JsonObject config;
        try
        {
            if (File.Exists(file)) config = JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new InvalidDataException("config.json precisa conter um objeto JSON; o arquivo foi preservado.");
            else config = ReadProfile(userProfile);
        }
        catch (FileNotFoundException) { config = ReadProfile(userProfile); }
        catch (DirectoryNotFoundException) { config = ReadProfile(userProfile); }
        catch (JsonException) { throw new InvalidDataException("config.json inválido; o arquivo foi preservado. Corrija-o antes de iniciar a Central."); }
        var originalConfiguration = config.ToJsonString(JsonOptions);

        var skill = Path.Combine(userProfile, ".claude", "skills", "desenvolver-ticket");
        var defaultRepoRoots = new[]
        {
            repositoriesRootOverride,
            @"C:\git",
            Path.Combine(userProfile, "git")
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var repo = Text(config, "repository");
        var repositoryRoot = Text(config, "repositories_root");
        if (string.IsNullOrWhiteSpace(repo))
        {
            repo = !string.IsNullOrWhiteSpace(repositoryRoot) ? FindGitRepository(repositoryRoot) : null;
            foreach (var root in defaultRepoRoots)
            {
                if (repo is not null) break;
                repo = FindGitRepository(root!);
            }
        }
        var fallbackRepositoryRoot = !string.IsNullOrWhiteSpace(repositoriesRootOverride)
            ? repositoriesRootOverride
            : !string.IsNullOrWhiteSpace(repositoryRoot)
            ? repositoryRoot
            : repo is not null ? Path.GetDirectoryName(repo)! : defaultRepoRoots.FirstOrDefault() ?? Path.Combine(userProfile, "git");
        var stateRoot = Text(config, "state_root");
        var worktreeRoot = Text(config, "new_worktrees_root");

        SetIfMissing(config, "schema_version", JsonValue.Create(1));
        SetIfEmpty(config, "appRoot", JsonValue.Create(Path.GetFullPath(appRoot)));
        SetIfEmpty(config, "dataRoot", JsonValue.Create(Path.GetFullPath(dataRoot)));
        SetIfEmpty(config, "skill", JsonValue.Create(skill));
        SetIfEmpty(config, "repository", JsonValue.Create(repo ?? ""));
        SetIfEmpty(config, "repositories_root", JsonValue.Create(fallbackRepositoryRoot));
        SetIfEmpty(config, "git_executable", JsonValue.Create(FindExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "git.exe")) ?? "git.exe"));
        SetIfEmpty(config, "claude", JsonValue.Create(FindExisting(
            Path.Combine(roamingAppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            Path.Combine(userProfile, ".local", "bin", "claude.exe"))));
        SetIfEmpty(config, "codex", JsonValue.Create(FindExisting(
            Path.Combine(roamingAppData, "npm", "codex.cmd"),
            Path.Combine(roamingAppData, "npm", "codex.exe"),
            Path.Combine(userProfile, ".local", "bin", "codex.exe"))));
        SetIfEmpty(config, "references_root", JsonValue.Create(Path.Combine(userProfile, "Downloads", "Claude Referencias", "Ref Dev")));
        SetIfEmpty(config, "documents_root", JsonValue.Create(Path.Combine(userProfile, "Downloads", "Claude Referencias", "doc dev")));
        SetIfEmpty(config, "state_base_root", JsonValue.Create(ParentOr(stateRoot, Path.Combine(userProfile, ".claude", "ticket-state"))));
        SetIfEmpty(config, "worktrees_base_root", JsonValue.Create(ParentOr(worktreeRoot, Path.Combine(userProfile, "erp-worktrees"))));
        SetIfMissing(config, "branch_type", JsonValue.Create("feature"));
        SetIfMissing(config, "delivery", JsonValue.Create("local_commit_only"));
        if (config["azure"] is not JsonObject) config["azure"] = new JsonObject { ["organization"] = "sistemasunion", ["project"] = "SSUnion" };

        if (!File.Exists(file) || !string.Equals(originalConfiguration, config.ToJsonString(JsonOptions), StringComparison.Ordinal))
        {
            var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, config.ToJsonString(JsonOptions), new System.Text.UTF8Encoding(false));
                File.Move(temporary, file, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        return config;
    }

    private static JsonObject ReadProfile(string userProfile)
    {
        var file = Path.Combine(userProfile, ".claude", "skills", "desenvolver-ticket", "perfil.json");
        if (!File.Exists(file)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("perfil.json da skill desenvolver-ticket precisa conter um objeto JSON; o arquivo foi preservado."); }
        catch (JsonException) { throw new InvalidDataException("perfil.json da skill desenvolver-ticket é inválido; o arquivo foi preservado."); }
    }

    private static string? FindGitRepository(string root)
    {
        if (!Directory.Exists(root)) return null;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        try
        {
            while (pending.TryDequeue(out var current))
            {
                if (Directory.Exists(Path.Combine(current.Path, ".git")) || File.Exists(Path.Combine(current.Path, ".git")))
                    return current.Path;
                if (current.Depth >= 2) continue;

                foreach (var directory in Directory.EnumerateDirectories(current.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue((directory, current.Depth + 1));
                }
            }
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? FindExisting(params string[] candidates) => candidates.FirstOrDefault(path => File.Exists(path));
    private static string? Text(JsonObject source, string name) => source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string ParentOr(string? path, string fallback) => !string.IsNullOrWhiteSpace(path) ? Path.GetDirectoryName(path) ?? fallback : fallback;
    private static void SetIfMissing(JsonObject target, string key, JsonNode? value)
    {
        if (!target.ContainsKey(key)) target[key] = value?.DeepClone();
    }
    private static void SetIfEmpty(JsonObject target, string key, JsonNode? value)
    {
        if (target[key] is null || target[key] is JsonValue existing && existing.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
            target[key] = value?.DeepClone();
    }
}
