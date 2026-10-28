using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class SharedFileAccessTests
{
    [Fact]
    public void OpenRead_allows_atomic_replacement_while_another_reader_is_open()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"central-shared-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "runtime.json");
        var temporary = Path.Combine(directory, "runtime.json.next");
        try
        {
            File.WriteAllText(target, "old-state");
            using var reader = SharedFileAccess.OpenRead(target);
            File.WriteAllText(temporary, "new-state");

            File.Replace(temporary, target, null);

            using var previousContent = new StreamReader(reader);
            Assert.Equal("old-state", previousContent.ReadToEnd());
            Assert.Equal("new-state", SharedFileAccess.ReadAllText(target));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
