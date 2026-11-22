using System.Text.Json;
using Explorer.Host.Logging;
using Microsoft.Extensions.Logging;

namespace Explorer.Host.Tests;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Logs-{Guid.NewGuid():N}");

    [Fact]
    public void WritesStructuredPropertiesAndKeepsOnlyTheConfiguredNumberOfFiles()
    {
        using var provider = new RollingFileLoggerProvider(_root, maximumBytes: 1, retainedFiles: 2);
        var logger = provider.CreateLogger("test.audit");

        logger.LogInformation("Datei {Path} von {User} gespeichert", @"C:\Dokumente\Grüße.txt", "test@example.com");

        var firstFile = Assert.Single(Directory.GetFiles(_root, "app-*.jsonl"));
        using (var document = JsonDocument.Parse(File.ReadAllText(firstFile)))
        {
            var record = document.RootElement;
            Assert.Equal("Datei C:\\Dokumente\\Grüße.txt von test@example.com gespeichert", record.GetProperty("Message").GetString());
            var properties = record.GetProperty("Properties");
            Assert.Equal(@"C:\Dokumente\Grüße.txt", properties.GetProperty("Path").GetString());
            Assert.Equal("test@example.com", properties.GetProperty("User").GetString());
        }

        for (var index = 0; index < 4; index++) logger.LogInformation("Eintrag {Index}", index);
        Assert.Equal(2, Directory.GetFiles(_root, "app-*.jsonl").Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
