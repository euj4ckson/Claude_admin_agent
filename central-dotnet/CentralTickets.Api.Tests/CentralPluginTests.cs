using System.Text.Json;

namespace CentralTickets.Api.Tests;

public sealed class CentralPluginTests
{
    [Fact]
    public void Claude_plugin_routes_every_managed_event_to_the_compiled_dotnet_hook()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var plugin = Path.Combine(repository, "central-dotnet", "CentralTickets.Plugin");
        var manifestPath = Path.Combine(plugin, ".claude-plugin", "plugin.json");
        var hooksPath = Path.Combine(plugin, "hooks", "hooks.json");
        var hookAssembly = Path.Combine(repository, "central-dotnet", "CentralTickets.Hook", "bin", "Release", "net10.0", "CentralTickets.Hook.dll");

        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(hookAssembly), "The Windows launcher must build the Release hook before starting a session.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("central-tickets", manifest.RootElement.GetProperty("name").GetString());
        using var hooks = JsonDocument.Parse(File.ReadAllText(hooksPath));
        var events = hooks.RootElement.GetProperty("hooks");
        Assert.Equal(new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "SessionEnd" }, events.EnumerateObject().Select(item => item.Name));
        foreach (var hook in events.EnumerateObject().SelectMany(item => item.Value.EnumerateArray()).SelectMany(item => item.GetProperty("hooks").EnumerateArray()))
        {
            var command = hook.GetProperty("command").GetString();
            Assert.Contains("dotnet", command, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CentralTickets.Hook.dll", command, StringComparison.Ordinal);
            Assert.DoesNotContain("node", command, StringComparison.OrdinalIgnoreCase);
        }
    }
}
