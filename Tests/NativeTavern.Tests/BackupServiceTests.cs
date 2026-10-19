using Microsoft.Data.Sqlite;
using NativeTavern.Services;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace NativeTavern.Tests;

public sealed class BackupServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupAndRestoreRoundTripDatabaseAndManagedFiles(bool legacyBrand)
    {
        var parent = Path.Combine(Path.GetTempPath(), "NativeTavernBackupTest-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "UserData");
        var database = Path.Combine(root, "Data", "NativeTavern.db");
        var avatar = Path.Combine(root, "Avatars", "avatar.png");
        var plugin = Path.Combine(root, "Plugins", "sample.plugin", "plugin.json");
        var backup = Path.Combine(parent, "backup.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            Directory.CreateDirectory(Path.GetDirectoryName(avatar)!);
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            await ExecuteAsync(database, "CREATE TABLE Sample(Value TEXT); INSERT INTO Sample VALUES('before');");
            await File.WriteAllTextAsync(avatar, "avatar-before");
            await File.WriteAllTextAsync(plugin, "plugin-before");
            var service = new BackupService(root);

            await service.CreateAsync(backup);
            using (var archive = ZipFile.Open(backup, ZipArchiveMode.Update))
            {
                var manifestEntry = archive.GetEntry("backup.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(manifestEntry.Open()))
                    manifest = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                Assert.Equal("LorehavenBackup", manifest["format"]!.GetValue<string>());
                Assert.NotNull(archive.GetEntry("UserData/Data/NativeTavern.db"));
                if (legacyBrand)
                {
                    manifestEntry.Delete();
                    manifest["format"] = "NativeTavernBackup";
                    await using var writer = new StreamWriter(archive.CreateEntry("backup.json").Open());
                    await writer.WriteAsync(manifest.ToJsonString());
                }
            }
            await ExecuteAsync(database, "DELETE FROM Sample; INSERT INTO Sample VALUES('after');");
            await File.WriteAllTextAsync(avatar, "avatar-after");
            await File.WriteAllTextAsync(plugin, "plugin-after");
            var staleAvatar = Path.Combine(root, "Avatars", "stale.png");
            await File.WriteAllTextAsync(staleAvatar, "not-in-backup");

            var safetyBackup = await service.RestoreAsync(backup);

            Assert.Equal("before", await ScalarAsync(database, "SELECT Value FROM Sample"));
            Assert.Equal("avatar-before", await File.ReadAllTextAsync(avatar));
            Assert.False(File.Exists(staleAvatar));
            Assert.Equal("plugin-before", await File.ReadAllTextAsync(plugin));
            Assert.True(File.Exists(safetyBackup));
            Assert.StartsWith("Lorehaven-before-restore-", Path.GetFileName(safetyBackup));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task RestoreRejectsArchiveWithoutBackupManifest()
    {
        var parent = Path.Combine(Path.GetTempPath(), "NativeTavernBackupTest-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "UserData");
        var database = Path.Combine(root, "Data", "NativeTavern.db");
        var archivePath = Path.Combine(parent, "invalid.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await ExecuteAsync(database, "CREATE TABLE Sample(Value TEXT);");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("unrelated.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("not a backup");
            }

            var service = new BackupService(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(archivePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{not-json")]
    [InlineData("{\"format\":\"NativeTavernBackup\",\"version\":1,\"userDataRoot\":42}")]
    [InlineData("{\"format\":\"LorehavenBackup\",\"version\":1,\"userDataRoot\":42}")]
    public async Task InvalidManifestIsRejectedBeforeLiveDataChanges(string manifest)
    {
        var parent = Path.Combine(Path.GetTempPath(), "NativeTavernBackupTest-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "UserData");
        var database = Path.Combine(root, "Data", "NativeTavern.db");
        var archivePath = Path.Combine(parent, "invalid.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await ExecuteAsync(database, "CREATE TABLE Sample(Value TEXT); INSERT INTO Sample VALUES('backup');");
            var service = new BackupService(root);
            await service.CreateAsync(archivePath);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                archive.GetEntry("backup.json")!.Delete();
                await using var writer = new StreamWriter(archive.CreateEntry("backup.json").Open());
                await writer.WriteAsync(manifest);
            }
            await ExecuteAsync(database, "DELETE FROM Sample; INSERT INTO Sample VALUES('current');");

            await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(archivePath));

            Assert.Equal("current", await ScalarAsync(database, "SELECT Value FROM Sample"));
            Assert.Empty(Directory.EnumerateFiles(parent, "Lorehaven-before-restore-*.zip"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }
}
