using System.Diagnostics;
using System.Text.Json;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class GitWorktreeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-worktree-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _state;
    private readonly string _worktrees;
    private readonly string _repo;
    private readonly string _remote;
    private readonly string _agent;
    private readonly string _git = FindGit();

    public GitWorktreeServiceTests()
    {
        _data = Path.Combine(_root, "data");
        _state = Path.Combine(_root, "states");
        _worktrees = Path.Combine(_root, "worktrees");
        _repo = Path.Combine(_root, "git", "ss_erp_tag1");
        _remote = Path.Combine(_root, "remote.git");
        _agent = Path.Combine(_root, "claude.exe");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.GetDirectoryName(_repo)!);
        File.WriteAllText(_agent, "test executable placeholder");
        Git(_root, "init", "--bare", _remote);
        Git(_root, "init", "--initial-branch=main", _repo);
        Git(_repo, "config", "user.email", "central-test@example.invalid");
        Git(_repo, "config", "user.name", "Central Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "isolated git fixture\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-m", "initial fixture");
        Git(_repo, "remote", "add", "origin", _remote);
        Git(_repo, "push", "-u", "origin", "main");
        Git(_repo, "checkout", "-b", "release/106.4.3");
        File.WriteAllText(Path.Combine(_repo, "release.txt"), "release fixture\n");
        Git(_repo, "add", "release.txt");
        Git(_repo, "commit", "-m", "release fixture");
        Git(_repo, "push", "-u", "origin", "release/106.4.3");
        Git(_repo, "checkout", "main");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _repo,
            repositories_root = Path.GetDirectoryName(_repo),
            git_executable = _git,
            state_base_root = _state,
            worktrees_base_root = _worktrees,
            claude = _agent,
            codex = (string?)null
        }));
    }

    [Fact]
    public async Task Prepare_creates_isolated_branch_and_worktree_without_moving_primary_checkout()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _state, profile);
        writer.Create(new CreateTicketRequest(
            "GER5800", "166", "release/106.4.3", "Testar a criação segura da worktree isolada.", "Fixture de Git",
            "local_commit_only", "hotfix", "claude", "ss_erp_tag1", null, [], null, null));
        var service = new GitWorktreeService(_data, _state, _worktrees, profile);

        var prepared = await service.Prepare("GER5800", default);
        var preflight = await service.Preflight("GER5800", default);
        var diff = await service.Diff("GER5800", default);

        Assert.Equal("hotfix/166/ger5800", prepared["branch"]?.GetValue<string>());
        Assert.True(preflight.GetType().GetProperty("ok")?.GetValue(preflight) as bool?);
        Assert.Contains("(limpo)", diff.GetType().GetProperty("text")?.GetValue(diff)?.ToString());
        Assert.Equal("main", Git(_repo, "branch", "--show-current"));
        var worktree = prepared["worktree"]!.GetValue<string>();
        Assert.Equal("hotfix/166/ger5800", Git(worktree, "branch", "--show-current"));
        Assert.Equal(Git(_repo, "rev-parse", "refs/remotes/origin/release/106.4.3"), prepared["baseSha"]?.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_state, "ss_erp_tag1", "ger5800", "test-matrix.json")));
        Assert.True(File.Exists(Path.Combine(_state, "ss_erp_tag1", "ger5800", "evidence-manifest.json")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(_state, "ss_erp_tag1", "ger5800", "estado.json")));
        Assert.Equal("analise", state.RootElement.GetProperty("stage").GetString());
        Assert.False(state.RootElement.GetProperty("delivery").GetProperty("push_allowed").GetBoolean());
    }

    [Fact]
    public async Task Prepare_refuses_an_existing_ticket_folder_without_overwriting_it()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _state, profile);
        writer.Create(new CreateTicketRequest(
            "GER5801", "166", "release/106.4.3", "Testar que a pasta existente seja preservada.", "Fixture de colisão",
            "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var existing = Path.Combine(_worktrees, "ss_erp_tag1", "166-ger5801");
        Directory.CreateDirectory(existing);
        var marker = Path.Combine(existing, "do-not-delete.txt");
        File.WriteAllText(marker, "user data");
        var service = new GitWorktreeService(_data, _state, _worktrees, profile);

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => service.Prepare("GER5801", default));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("user data", File.ReadAllText(marker));
        Assert.False(File.Exists(Path.Combine(_state, "ss_erp_tag1", "ger5801", "estado.json")));
    }

    private static string FindGit()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git"))
            .FirstOrDefault(File.Exists);
        return path ?? throw new InvalidOperationException("Git is required for this isolated integration test.");
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
        Directory.Delete(_root, recursive: true);
    }
}
