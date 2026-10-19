using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class PluginManifestValidationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "NativeTavern-manifest-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("[null]", "[]", "权限")]
    [InlineData("[\"\"]", "[]", "权限")]
    [InlineData("[\"   \"]", "[]", "权限")]
    [InlineData("[]", "[null]", "能力")]
    [InlineData("[]", "[\"\"]", "能力")]
    [InlineData("[]", "[\"   \"]", "能力")]
    public async Task BlankListEntriesProduceValidationErrorsInsteadOfCrashing(
        string permissionsJson, string capabilitiesJson, string expectedField)
    {
        Directory.CreateDirectory(root);
        var manifest = $$"""
            {"id":"valid.plugin","name":"Test","version":"1.0.0",
             "permissions":{{permissionsJson}},"capabilities":{{capabilitiesJson}}}
            """;
        var package = Path.Combine(root, "invalid.ntplugin");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("plugin.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(manifest);
        }
        var service = new PluginService(root, NullLogger<PluginService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallPackageAsync(package));
        Assert.Contains(expectedField, error.Message);
        Assert.Contains("空白", error.Message);

        var installedDirectory = Path.Combine(root, "valid.plugin");
        Directory.CreateDirectory(installedDirectory);
        await File.WriteAllTextAsync(Path.Combine(installedDirectory, "plugin.json"), manifest);
        Assert.Empty(await service.GetInstalledAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
