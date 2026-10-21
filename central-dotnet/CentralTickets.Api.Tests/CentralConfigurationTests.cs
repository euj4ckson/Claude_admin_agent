using System.Text.Json.Nodes;

namespace CentralTickets.Api.Tests;

public sealed class CentralConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "central-config-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ensure_imports_the_installed_skill_profile_and_preserves_its_settings()
    {
        var home = Path.Combine(_root, "home");
        var app = Path.Combine(_root, "app");
        var repo = Path.Combine(_root, "git", "ss_erp_tag1");
        var profile = Path.Combine(home, ".claude", "skills", "desenvolver-ticket", "perfil.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(profile, new JsonObject
        {
            ["repository"] = repo,
            ["repositories_root"] = Path.GetDirectoryName(repo),
            ["state_root"] = Path.Combine(home, ".claude", "ticket-state", "ss_erp_tag1"),
            ["new_worktrees_root"] = Path.Combine(home, "erp-worktrees", "ss_erp_tag1"),
            ["author"] = "Jackson",
            ["custom_setting"] = "keep-me"
        }.ToJsonString());

        var config = CentralConfiguration.Ensure(Path.Combine(_root, "data"), app, home, Path.Combine(_root, "local"), Path.Combine(_root, "roaming"));

        Assert.Equal(repo, config["repository"]!.GetValue<string>());
        Assert.Equal(app, config["appRoot"]!.GetValue<string>());
        Assert.Equal(Path.Combine(home, ".claude", "ticket-state"), config["state_base_root"]!.GetValue<string>());
        Assert.Equal("keep-me", config["custom_setting"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_root, "data", "config.json")));
    }

    [Fact]
    public void Ensure_preserves_existing_configuration_and_is_idempotent_after_completion()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var configFile = Path.Combine(data, "config.json");
        var json = "{\"repository\":\"C:/custom/repo\",\"appRoot\":\"C:/custom/app\",\"repositories_root\":\"C:/custom\",\"opaque\":{\"value\":17}}";
        File.WriteAllText(configFile, json);
        var config = CentralConfiguration.Ensure(data, Path.Combine(_root, "new-app"), _root, Path.Combine(_root, "local"), Path.Combine(_root, "roaming"));

        Assert.Equal("C:/custom/repo", config["repository"]!.GetValue<string>());
        Assert.Equal("C:/custom/app", config["appRoot"]!.GetValue<string>());
        Assert.Equal(17, config["opaque"]!["value"]!.GetValue<int>());
        var afterFirstRun = File.ReadAllText(configFile);
        CentralConfiguration.Ensure(data, Path.Combine(_root, "another-app"), _root, Path.Combine(_root, "local"), Path.Combine(_root, "roaming"));
        Assert.Equal(afterFirstRun, File.ReadAllText(configFile));
    }

    [Fact]
    public void Ensure_refuses_to_replace_malformed_configuration()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var configFile = Path.Combine(data, "config.json");
        File.WriteAllText(configFile, "not-json");

        Assert.Throws<InvalidDataException>(() => CentralConfiguration.Ensure(data, _root, _root, _root, _root));
        Assert.Equal("not-json", File.ReadAllText(configFile));
    }

    [Fact]
    public void Ensure_discovers_a_repository_from_an_explicit_clone_root_without_a_skill_profile()
    {
        var home = Path.Combine(_root, "home-without-profile");
        var repoRoot = Path.Combine(_root, "custom-clones");
        var repo = Path.Combine(repoRoot, "mobile-app");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var config = CentralConfiguration.Ensure(Path.Combine(_root, "fresh-data"), Path.Combine(_root, "app"), home,
            Path.Combine(_root, "local"), Path.Combine(_root, "roaming"), repoRoot);

        Assert.Equal(repo, config["repository"]!.GetValue<string>());
        Assert.Equal(repoRoot, config["repositories_root"]!.GetValue<string>());
    }

    [Fact]
    public void Ensure_discovers_a_nested_repository_from_an_explicit_clone_root()
    {
        var home = Path.Combine(_root, "home-without-profile");
        var repoRoot = Path.Combine(_root, "custom-clones");
        var repo = Path.Combine(repoRoot, "group", "mobile-app");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var config = CentralConfiguration.Ensure(Path.Combine(_root, "fresh-nested-data"), Path.Combine(_root, "app"), home,
            Path.Combine(_root, "local"), Path.Combine(_root, "roaming"), repoRoot);

        Assert.Equal(repo, config["repository"]!.GetValue<string>());
        Assert.Equal(repoRoot, config["repositories_root"]!.GetValue<string>());
    }

    [Fact]
    public void Session_settings_allow_computer_use_but_keep_publication_denied()
    {
        var settings = SessionService.CreateSessionSettings();
        var allow = Assert.IsType<JsonArray>(settings["permissions"]!["allow"]);
        var deny = Assert.IsType<JsonArray>(settings["permissions"]!["deny"]);

        Assert.Contains(allow, item => item!.GetValue<string>() == "mcp__central-computer__computer_screenshot");
        Assert.Contains(allow, item => item!.GetValue<string>() == "mcp__central-computer__computer_click");
        Assert.Contains(deny, item => item!.GetValue<string>() == "Bash(git push *)");
        Assert.Contains(deny, item => item!.GetValue<string>() == "Bash(git -C * push *)");
        Assert.Contains(deny, item => item!.GetValue<string>() == "PowerShell(git push *)");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
