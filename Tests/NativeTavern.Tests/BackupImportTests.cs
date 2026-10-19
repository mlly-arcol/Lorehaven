using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text.Json.Nodes;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class BackupImportTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "NativeTavernImport-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectingAConversationImportsItsActualCharacterWithoutUnrelatedCharacters(bool legacyBrand)
    {
        var source = await CreateLibraryAsync("Source");
        var characters = new CharacterRepository(source.Factory);
        var unrelated = await AddCharacterAsync(characters, "Unrelated");
        var cast = await AddCharacterAsync(characters, "Story character");
        var session = new ChatSession
        {
            Title = "Selected story", CharacterId = cast.Id,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await new ChatSessionRepository(source.Factory).CreateAsync(session);
        Assert.Equal(unrelated.Id, session.Id); // Character IDs and conversation IDs use separate sequences.
        var archive = Path.Combine(parent, "source.zip");
        await new BackupService(source.Root).CreateAsync(archive);
        if (legacyBrand)
        {
            using var legacyArchive = ZipFile.Open(archive, ZipArchiveMode.Update);
            var manifestEntry = legacyArchive.GetEntry("backup.json")!;
            JsonObject manifest;
            using (var reader = new StreamReader(manifestEntry.Open()))
                manifest = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            manifestEntry.Delete();
            manifest["format"] = "NativeTavernBackup";
            await using var writer = new StreamWriter(legacyArchive.CreateEntry("backup.json").Open());
            await writer.WriteAsync(manifest.ToJsonString());
        }
        var target = await CreateLibraryAsync("Target");

        var result = await new BackupService(target.Root).ImportSelectedAsync(archive, [], [session.Id]);

        Assert.Equal(1, result.Characters);
        Assert.Equal(1, result.Sessions);
        var importedCharacter = Assert.Single(await new CharacterRepository(target.Factory).SearchAsync("", false));
        Assert.Equal(cast.Name, importedCharacter.Name);
        var importedSession = Assert.Single(await new ChatSessionRepository(target.Factory).GetAllAsync());
        Assert.Equal(importedCharacter.Id, importedSession.CharacterId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestoringKeepsManagedAvatarsAndRejectsEscapingStoredPaths(bool selective, bool sameLibrary)
    {
        var source = await CreateLibraryAsync("Source");
        var characters = new CharacterRepository(source.Factory);
        var avatar = Path.Combine(source.Root, "Avatars", "portrait.png");
        Directory.CreateDirectory(Path.GetDirectoryName(avatar)!);
        await File.WriteAllTextAsync(avatar, "portrait bytes");
        var valid = await AddCharacterAsync(characters, "Valid", avatar);
        // This path would reach backup.json at the extraction root instead of a managed avatar.
        var escaping = await AddCharacterAsync(characters, "Escaping", source.Root + "/../backup.json");
        var archive = Path.Combine(parent, "source.zip");
        await new BackupService(source.Root).CreateAsync(archive);
        var target = sameLibrary ? source : await CreateLibraryAsync("Target");
        var service = new BackupService(target.Root);

        if (selective)
            await service.ImportSelectedAsync(archive, [valid.Id, escaping.Id], []);
        else
            await service.RestoreAsync(archive);

        var imported = await new CharacterRepository(target.Factory).SearchAsync("", false);
        var good = Assert.Single(imported, character => character.Name == valid.Name);
        Assert.StartsWith(Path.Combine(target.Root, "Avatars") + Path.DirectorySeparatorChar, good.AvatarPath);
        Assert.Equal("portrait bytes", await File.ReadAllTextAsync(good.AvatarPath));
        Assert.Empty(Assert.Single(imported, character => character.Name == escaping.Name).AvatarPath);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(target.Root, "Avatars")));
    }

    private async Task<(string Root, DatabaseConnectionFactory Factory)> CreateLibraryAsync(string name)
    {
        var root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var factory = new DatabaseConnectionFactory(Path.Combine(root, "Data", "NativeTavern.db"));
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        return (root, factory);
    }

    private static async Task<Character> AddCharacterAsync(CharacterRepository repository, string name, string avatarPath = "")
    {
        var character = new Character
        {
            Name = name, AvatarPath = avatarPath,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await repository.CreateAsync(character);
        return character;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(parent)) Directory.Delete(parent, true);
    }
}
