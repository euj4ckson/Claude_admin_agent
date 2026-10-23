using System.Text.Json;
using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class CentralProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-profile-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Repositories_lists_git_directories_and_identifies_the_default()
    {
        var data = Path.Combine(_root, "data");
        var repositories = Path.Combine(_root, "git");
        var erp = Path.Combine(repositories, "ss_erp_tag1");
        var fva = Path.Combine(repositories, "ss_fva");
        Directory.CreateDirectory(Path.Combine(erp, ".git"));
        Directory.CreateDirectory(Path.Combine(fva, ".git"));
        Directory.CreateDirectory(Path.Combine(repositories, "not-a-repository"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = erp,
            repositories_root = repositories
        }));

        var profile = new CentralProfile(data);
        var result = JsonSerializer.SerializeToElement(profile.Repositories(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal("ss_erp_tag1", result[0].GetProperty("id").GetString());
        Assert.True(result[0].GetProperty("default").GetBoolean());
        Assert.Equal("ss_fva", result[1].GetProperty("id").GetString());
        Assert.Equal("main", result[1].GetProperty("standard").GetProperty("base").GetString());
        Assert.Equal("pre_main", result[1].GetProperty("standard").GetProperty("integration").GetString());
    }

    [Fact]
    public void ResolveRepository_accepts_equivalent_windows_path_separators()
    {
        var data = Path.Combine(_root, "data");
        var repositories = Path.Combine(_root, "git");
        var fva = Path.Combine(repositories, "ss_fva");
        Directory.CreateDirectory(Path.Combine(fva, ".git"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = fva,
            repositories_root = repositories
        }));

        var profile = new CentralProfile(data);
        var mixedSeparators = fva.Replace('\\', '/');

        var result = profile.ResolveRepository(mixedSeparators);

        Assert.NotNull(result);
        Assert.Equal("ss_fva", result.Id);
        Assert.Equal(Path.GetFullPath(fva), Path.GetFullPath(result.Path));
    }

    [Fact]
    public void Repositories_exposes_configured_default_base_for_repositories_without_a_standard_flow()
    {
        var data = Path.Combine(_root, "data");
        var repositories = Path.Combine(_root, "git");
        var erp = Path.Combine(repositories, "ss_erp_tag1");
        Directory.CreateDirectory(Path.Combine(erp, ".git"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = erp,
            repositories_root = repositories,
            default_branch_by_repository = new Dictionary<string, string> { ["ss_erp_tag1"] = "release/106.4.3" }
        }));

        var profile = new CentralProfile(data);
        var repository = Assert.Single(profile.Repositories());

        Assert.Null(repository.Standard);
        Assert.Equal("release/106.4.3", repository.DefaultBase);
    }

    [Fact]
    public void Repositories_discovers_nested_clones_but_does_not_scan_inside_a_repository()
    {
        var data = Path.Combine(_root, "data");
        var repositories = Path.Combine(_root, "git");
        var defaultRepo = Path.Combine(repositories, "main-repo");
        var nestedRepo = Path.Combine(repositories, "group", "mobile");
        var nestedInsideRepo = Path.Combine(defaultRepo, "fixtures", "not-a-clone");
        var repoContainer = Path.Combine(repositories, "canhoto");
        var nestedFrontend = Path.Combine(repoContainer, "ss_canhoto_frontend");
        var nestedBackend = Path.Combine(repoContainer, "ss_canhoto_backend");
        Directory.CreateDirectory(Path.Combine(defaultRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(nestedRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(nestedInsideRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(repoContainer, ".git"));
        Directory.CreateDirectory(Path.Combine(nestedFrontend, ".git"));
        Directory.CreateDirectory(Path.Combine(nestedBackend, ".git"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
        {
            repository = defaultRepo,
            repositories_root = repositories
        }));

        var profile = new CentralProfile(data);
        var result = profile.Repositories();

        Assert.Equal(new[] { "main-repo", "canhoto", "mobile", "ss_canhoto_backend", "ss_canhoto_frontend" }, result.Select(item => item.Name).ToArray());
        Assert.Contains(result, item => item.Path == nestedRepo);
        Assert.Contains(result, item => item.Path == nestedFrontend);
        Assert.Contains(result, item => item.Path == nestedBackend);
        Assert.DoesNotContain(result, item => item.Path == nestedInsideRepo);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
