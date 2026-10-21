using System.Text.Json;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class SessionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-session-tests-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _states;
    private readonly string _repo;

    public SessionServiceTests()
    {
        _data = Path.Combine(_root, "data");
        _states = Path.Combine(_root, "states");
        _repo = Path.Combine(_root, "git", "ss_erp_tag1");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_root, "claude.exe"), "fixture");
        File.WriteAllText(Path.Combine(_data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = _repo,
            repositories_root = Path.GetDirectoryName(_repo),
            state_base_root = _states,
            claude = Path.Combine(_root, "claude.exe"),
            codex = (string?)null
        }));
    }

    [Fact]
    public async Task Start_refuses_to_launch_before_worktree_preflight_without_reserving_session()
    {
        var profile = new CentralProfile(_data);
        var writer = new TicketWriter(_data, _states, profile);
        writer.Create(new CreateTicketRequest("GER5810", "166", "main", "Uma descrição de escopo válida para teste.", "Fixture", "no_commit", "feature", "claude", "ss_erp_tag1", null, [], null, null));
        var worktrees = new GitWorktreeService(_data, _states, Path.Combine(_root, "worktrees"), profile);
        var service = new SessionService(_data, profile, worktrees);

        var error = await Assert.ThrowsAsync<TicketOperationException>(() => service.Start("GER5810", default));

        Assert.Contains("worktree", error.Message, StringComparison.OrdinalIgnoreCase);
        var ticketDir = Path.Combine(_data, "tickets", "ger5810");
        Assert.False(File.Exists(Path.Combine(ticketDir, "active.lock")));
        Assert.False(File.Exists(Path.Combine(ticketDir, "runtime.json")));
        Assert.False(File.Exists(Path.Combine(ticketDir, "session-context.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
