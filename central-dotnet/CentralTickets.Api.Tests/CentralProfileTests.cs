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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
