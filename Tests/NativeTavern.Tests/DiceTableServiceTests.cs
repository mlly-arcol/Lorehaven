using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class DiceTableServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "NativeTavern-dice-tests-" + Guid.NewGuid().ToString("N"));

    private PluginService Plugins => new(root, NullLogger<PluginService>.Instance);

    [Fact]
    public void DiceRollsAreBoundedAndDoNotOptIntoModelContext()
    {
        var service = new DiceTableService(Plugins, new SequenceRandom(3, 5));

        var result = service.RollDice("2d6+1");

        Assert.Equal(DiceRandomKind.Dice, result.Kind);
        Assert.Equal("2d6+1", result.SourceLabel);
        Assert.Equal("9", result.ResultText);
        Assert.Equal("3 + 5 + 1 = 9", result.DetailText);
        Assert.Null(result.ModelContextText);
        Assert.Equal("2d6+1：9", (result with { IncludeInModelContext = true }).ModelContextText);
        Assert.DoesNotContain("ModelContextText", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("0d6")]
    [InlineData("101d6")]
    [InlineData("d0")]
    [InlineData("d1001")]
    [InlineData("d6+100001")]
    [InlineData("d6-100001")]
    [InlineData("1d6; delete")]
    public void RejectsInvalidDiceExpressions(string notation)
    {
        var service = new DiceTableService(Plugins);
        Assert.ThrowsAny<ArgumentException>(() => service.RollDice(notation));
    }

    [Fact]
    public async Task WeightedTableDrawUsesEnabledPluginAndStaysOutOfModelContext()
    {
        WritePlugin("dice.plugin", ValidTables);
        var plugins = Plugins;
        var service = new DiceTableService(plugins, new SequenceRandom(2));

        var table = Assert.Single(await service.GetTablesAsync());
        Assert.Equal("dice.plugin/weather", table.Id);
        Assert.Equal("weather", table.LocalId);
        var result = await service.DrawFromTableAsync(table.Id);
        Assert.Equal(DiceRandomKind.Table, result.Kind);
        Assert.Equal("dice.plugin", result.PluginId);
        Assert.Equal("Rain", result.ResultText);
        Assert.Null(result.ModelContextText);

        await plugins.SetEnabledAsync("dice.plugin", false);
        Assert.Empty(await service.GetTablesAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DrawFromTableAsync(table.Id));
    }

    [Fact]
    public async Task InvalidPluginDataDoesNotHideOtherTables()
    {
        WritePlugin("good.plugin", ValidTables);
        WritePlugin("bad.plugin", """{"formatVersion":1,"tables":[{"id":"bad","name":"Bad","entries":[{"text":"oops","weight":0}]}]}""");
        var service = new DiceTableService(Plugins);

        var catalog = await service.GetCatalogAsync();

        Assert.Single(catalog.Tables);
        Assert.Equal("good.plugin/weather", catalog.Tables[0].Id);
        Assert.Equal("bad.plugin", Assert.Single(catalog.Issues).PluginId);
    }

    [Fact]
    public async Task InvalidSiblingCapabilityDisablesRandomTablesUntilRepaired()
    {
        var directory = WritePlugin("multi.plugin", ValidTables);
        var manifest = Manifest("multi.plugin");
        manifest.Capabilities = [.. manifest.Capabilities, PluginPromptResourceService.Capability];
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
        var resourcesPath = Path.Combine(directory, PluginPromptResourceService.DataFileName);
        const string validResources = """{"formatVersion":1,"personas":[],"lorebooks":[],"presets":[],"textRules":[]}""";
        File.WriteAllText(resourcesPath, validResources);
        var plugins = Plugins;
        var service = new DiceTableService(plugins);
        Assert.Single(await service.GetTablesAsync());

        File.WriteAllText(resourcesPath, "{invalid");

        Assert.False(await plugins.IsCapabilityEnabledAsync(DiceTableService.Capability));
        var catalog = await service.GetCatalogAsync();
        Assert.Empty(catalog.Tables);
        Assert.Equal("multi.plugin", Assert.Single(catalog.Issues).PluginId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DrawFromTableAsync("multi.plugin/weather"));

        File.WriteAllText(resourcesPath, validResources);
        Assert.Single(await service.GetTablesAsync());
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"tables":[]}""")]
    [InlineData("""{"formatVersion":1,"tables":[]}""")]
    [InlineData("""{"formatVersion":1,"tables":[{"id":"same","name":"A","entries":[{"text":"a","weight":1}]},{"id":"same","name":"B","entries":[{"text":"b","weight":1}]}]}""")]
    [InlineData("""{"formatVersion":1,"tables":[{"id":"bad","name":"A","entries":[{"text":"a","weight":10001}]}]}""")]
    [InlineData("""{"formatVersion":1,"tables":[{"id":"bad","name":"A","entries":[{"text":"a","weight":"2"}]}]}""")]
    public async Task PackageValidationRejectsInvalidTables(string json)
    {
        var directory = WritePlugin("test.plugin", json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiceTableService.ValidatePackageDirectoryAsync(Manifest("test.plugin"), directory));
    }

    [Fact]
    public async Task PackageValidationRequiresDataFileWhenCapabilityIsDeclared()
    {
        var directory = Path.Combine(root, "missing.plugin");
        Directory.CreateDirectory(directory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DiceTableService.ValidatePackageDirectoryAsync(Manifest("missing.plugin"), directory));
    }

    [Fact]
    public async Task PackageValidationIgnoresOtherCapabilities()
    {
        var directory = Path.Combine(root, "other.plugin");
        Directory.CreateDirectory(directory);
        var manifest = Manifest("other.plugin");
        manifest.Capabilities = ["character-status-v1"];

        await DiceTableService.ValidatePackageDirectoryAsync(manifest, directory);
    }

    private string WritePlugin(string id, string tables)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(Manifest(id)));
        File.WriteAllText(Path.Combine(directory, DiceTableService.TablesFileName), tables);
        return directory;
    }

    private static PluginManifest Manifest(string id) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        Author = "Tests",
        Capabilities = [DiceTableService.Capability]
    };

    private const string ValidTables = """
        {
          "formatVersion": 1,
          "tables": [
            {
              "id": "weather",
              "name": "Weather",
              "entries": [
                { "text": "Sun", "weight": 2 },
                { "text": "Rain", "weight": 1 }
              ]
            }
          ]
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class SequenceRandom(params int[] values) : Random
    {
        private int index;

        public override int Next(int minValue, int maxValue)
        {
            var value = values[index++];
            Assert.InRange(value, minValue, maxValue - 1);
            return value;
        }

        public override int Next(int maxValue)
        {
            var value = values[index++];
            Assert.InRange(value, 0, maxValue - 1);
            return value;
        }
    }
}
