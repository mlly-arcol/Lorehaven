using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class MemoryBackupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectiveImportPreservesLocksAndAcceptsBackupsFromBeforeLockSupport(bool legacy)
    {
        var parent = Path.Combine(Path.GetTempPath(), "NativeTavernMemoryBackup-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Library");
        var factory = new DatabaseConnectionFactory(Path.Combine(root, "Data", "NativeTavern.db"));
        var initializer = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            await initializer.InitializeAsync();
            var characters = new CharacterRepository(factory);
            var character = new Character { Name = "阿澄", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            await characters.CreateAsync(character);
            var memories = new CharacterMemoryRepository(factory);
            await memories.AddAsync(new CharacterMemory
            {
                CharacterId = character.Id, Content = "满月前一起修好灯塔", IsLocked = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            if (legacy)
            {
                await using var connection = factory.CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE CharacterMemories DROP COLUMN IsLocked";
                await command.ExecuteNonQueryAsync();
            }
            var backup = new BackupService(root);
            var path = Path.Combine(parent, "memories.zip");
            await backup.CreateAsync(path);
            if (legacy) await initializer.InitializeAsync();

            await backup.ImportSelectedAsync(path, [character.Id], [], Path.Combine(parent, "Safety"));

            var imported = Assert.Single(await characters.SearchAsync(string.Empty, false), x => x.Id != character.Id);
            var memory = Assert.Single(await memories.GetByCharacterAsync(imported.Id));
            Assert.Equal("满月前一起修好灯塔", memory.Content);
            Assert.Equal(!legacy, memory.IsLocked);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }
}
