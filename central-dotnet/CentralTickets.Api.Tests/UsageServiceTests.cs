using System.Text.Json;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class UsageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-usage-{Guid.NewGuid():N}");

    [Fact]
    public void Claude_usage_reads_only_valid_recent_snapshots_and_never_fabricates_zero()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "usage.json");
        File.WriteAllText(Path.Combine(_root, "config.json"), JsonSerializer.Serialize(new { claude_usage_file = file }));
        var captured = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(file, JsonSerializer.Serialize(new { capturedAt = captured, rateLimits = new { fiveHour = new { usedPercentage = 42.5, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() }, sevenDay = (object?)null } }));

        var service = new UsageService(new CentralProfile(_root));
        var snapshot = service.ReadClaude();

        Assert.Equal("claude", snapshot["provider"]?.GetValue<string>());
        Assert.Equal("current", snapshot["windows"]?["fiveHour"]?["status"]?.GetValue<string>());
        Assert.Equal(42.5, snapshot["windows"]?["fiveHour"]?["used"]?.GetValue<double>());
        Assert.Equal("unavailable", snapshot["windows"]?["sevenDay"]?["status"]?.GetValue<string>());

        File.WriteAllText(file, "{bad json");
        var invalid = service.ReadClaude();
        Assert.True(invalid["unavailable"]?.GetValue<bool>());
        Assert.Null(invalid["windows"]?["fiveHour"]?["used"]);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
