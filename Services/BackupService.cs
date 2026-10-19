using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NativeTavern.Helpers;

namespace NativeTavern.Services;

public sealed class BackupService
{
    private const string BackupFormat = "LorehavenBackup";
    private const string LegacyBackupFormat = "NativeTavernBackup";
    private static readonly string[] ManagedDirectories = ["Avatars", "Attachments", "Documents"];
    private static readonly string[] OptionalManagedDirectories = ["Plugins", "PluginData"];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    private readonly string root;
    private readonly string databaseFile;

    public BackupService(string? userDataRoot = null)
    {
        root = Path.GetFullPath(userDataRoot ?? AppPaths.Root);
        // The database and archive layout stay compatible with existing NativeTavern libraries.
        databaseFile = Path.Combine(root, "Data", "NativeTavern.db");
    }

    public async Task CreateAsync(string destinationPath)
    {
        if (!File.Exists(databaseFile)) throw new FileNotFoundException("找不到 Lorehaven 数据库。", databaseFile);
        var destination = Path.GetFullPath(destinationPath);
        if (IsInsideRoot(destination)) throw new InvalidOperationException("备份文件不能保存在 UserData 文件夹内。");

        var temporary = CreateTemporaryDirectory();
        string? temporaryArchive = null;
        try
        {
            var backupRoot = Path.Combine(temporary, "UserData");
            Directory.CreateDirectory(Path.Combine(backupRoot, "Data"));
            await SnapshotDatabaseAsync(Path.Combine(backupRoot, "Data", "NativeTavern.db"));
            foreach (var name in ManagedDirectories)
            {
                var managedBackupDirectory = Path.Combine(backupRoot, name);
                Directory.CreateDirectory(managedBackupDirectory);
                CopyDirectory(Path.Combine(root, name), managedBackupDirectory);
            }
            foreach (var name in OptionalManagedDirectories)
            {
                var managedBackupDirectory = Path.Combine(backupRoot, name);
                Directory.CreateDirectory(managedBackupDirectory);
                CopyDirectory(Path.Combine(root, name), managedBackupDirectory);
            }

            await File.WriteAllTextAsync(
                Path.Combine(temporary, "backup.json"),
                JsonSerializer.Serialize(new
                {
                    format = BackupFormat,
                    version = 1,
                    applicationVersion = Helpers.AppVersion.DisplayVersion,
                    userDataRoot = root,
                    createdAt = DateTimeOffset.UtcNow
                }, ManifestJsonOptions));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporaryArchive = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            ZipFile.CreateFromDirectory(temporary, temporaryArchive, CompressionLevel.Optimal, false);
            File.Move(temporaryArchive, destination, true);
            temporaryArchive = null;
        }
        finally
        {
            if (temporaryArchive is not null && File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    public async Task<string> RestoreAsync(string backupPath, string? safetyBackupDirectory = null)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到备份文件。", source);
        var temporary = CreateTemporaryDirectory();
        try
        {
            ValidateArchive(source, temporary);
            ZipFile.ExtractToDirectory(source, temporary);
            var restoredRoot = Path.Combine(temporary, "UserData");
            var restoredDatabase = Path.Combine(restoredRoot, "Data", "NativeTavern.db");
            foreach (var name in ManagedDirectories)
                if (!Directory.Exists(Path.Combine(restoredRoot, name)))
                    throw new InvalidDataException($"备份中缺少 {name} 文件夹。");
            await ValidateDatabaseAsync(restoredDatabase);

            // Callers on Android pass the externally reachable folder, because the default
            // (next to UserData) sits in app-internal storage a file manager cannot open.
            var safetyDirectory = string.IsNullOrWhiteSpace(safetyBackupDirectory)
                ? Directory.GetParent(root)?.FullName ?? root
                : Path.GetFullPath(safetyBackupDirectory);
            var safetyBackup = Path.Combine(
                safetyDirectory,
                $"Lorehaven-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateAsync(safetyBackup);

            SqliteConnection.ClearAllPools();
            Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
            File.Copy(restoredDatabase, databaseFile, true);
            foreach (var name in ManagedDirectories)
                ReplaceDirectory(Path.Combine(restoredRoot, name), Path.Combine(root, name));
            foreach (var name in OptionalManagedDirectories)
            {
                var sourceDirectory = Path.Combine(restoredRoot, name);
                if (Directory.Exists(sourceDirectory)) ReplaceDirectory(sourceDirectory, Path.Combine(root, name));
            }
            await RemapStoredPathsAsync(ReadOriginRoot(temporary));
            return safetyBackup;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private async Task SnapshotDatabaseAsync(string destination)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
    }

    private static async Task ValidateDatabaseAsync(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("备份中缺少 Data/NativeTavern.db。");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var result = await command.ExecuteScalarAsync() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份数据库完整性检查失败。");
    }

    private static string? ReadOriginRoot(string extractionDirectory)
    {
        var manifest = Path.Combine(extractionDirectory, "backup.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("userDataRoot", out var value) &&
                   value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // AvatarPath, FilePath, ManagedPath and the composer draft images are stored as absolute
    // paths, so restoring a backup produced on another device (a Windows library onto a phone)
    // would otherwise leave every row pointing at the origin machine's folders.
    private async Task RemapStoredPathsAsync(string? originRoot)
    {
        if (string.IsNullOrWhiteSpace(originRoot)) return;
        var origin = originRoot.Replace('\\', '/').TrimEnd('/');
        var target = Path.TrimEndingDirectorySeparator(root);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RemapColumnAsync(connection, "Characters", "Id", "AvatarPath", origin, target);
        await RemapColumnAsync(connection, "ChatAttachments", "Id", "FilePath", origin, target);
        // KnowledgeDocuments.SourcePath is skipped on purpose: it records where the user imported
        // a document from, which lives outside UserData and must not be rewritten.
        await RemapColumnAsync(connection, "KnowledgeDocuments", "Id", "ManagedPath", origin, target);
        await RemapDraftImagesAsync(connection, origin, target);
        await transaction.CommitAsync();
    }

    private static string? RemapPath(string value, string origin, string target)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\\', '/');
        if (!normalized.StartsWith(origin + '/', StringComparison.Ordinal)) return null;
        var segments = normalized[(origin.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return ResolveContainedPath(target, segments) ?? string.Empty;
    }

    // Table, key and column arrive only from the literal call sites above.
    private static async Task RemapColumnAsync(
        SqliteConnection connection, string table, string key, string column, string origin, string target)
    {
        if (!await HasColumnAsync(connection, table, column)) return;
        var updates = new List<(object Key, string Value)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT {key}, {column} FROM {table}";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(1)) continue;
                var stored = reader.GetString(1);
                var remapped = RemapPath(stored, origin, target);
                if (remapped is not null && remapped != stored) updates.Add((reader.GetValue(0), remapped));
            }
        }
        foreach (var (id, value) in updates)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE {table} SET {column} = $value WHERE {key} = $key";
            update.Parameters.AddWithValue("$value", value);
            update.Parameters.AddWithValue("$key", id);
            await update.ExecuteNonQueryAsync();
        }
    }

    private static async Task RemapDraftImagesAsync(SqliteConnection connection, string origin, string target)
    {
        if (!await HasColumnAsync(connection, "ComposerDrafts", "DraftImages")) return;
        var updates = new List<(long SessionId, string Json)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT ChatSessionId, DraftImages FROM ComposerDrafts";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var stored = reader.GetString(1);
                string[]? paths;
                try
                {
                    paths = JsonSerializer.Deserialize<string[]>(stored);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (paths is not { Length: > 0 }) continue;
                var remapped = paths.Select(path => RemapPath(path, origin, target) ?? path)
                    .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                if (remapped.SequenceEqual(paths)) continue;
                updates.Add((reader.GetInt64(0), JsonSerializer.Serialize(remapped)));
            }
        }
        foreach (var (sessionId, json) in updates)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE ComposerDrafts SET DraftImages = $images WHERE ChatSessionId = $session";
            update.Parameters.AddWithValue("$images", json);
            update.Parameters.AddWithValue("$session", sessionId);
            await update.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info($table) WHERE name=$column)";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    public sealed record BackupCharacter(long Id, string Name, string GroupName);

    public sealed record BackupSession(long Id, string Title, int MessageCount, bool IsGroupChat);

    public sealed record BackupPreview(IReadOnlyList<BackupCharacter> Characters, IReadOnlyList<BackupSession> Sessions);

    public sealed record BackupImportResult(int Characters, int Sessions, int Messages);

    // Reads a backup archive without touching the live library, so a UI can offer a selection.
    public async Task<BackupPreview> PreviewAsync(string backupPath)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到备份文件。", source);
        var temporary = CreateTemporaryDirectory();
        try
        {
            ValidateArchive(source, temporary);
            ZipFile.ExtractToDirectory(source, temporary);
            var restoredDatabase = Path.Combine(temporary, "UserData", "Data", "NativeTavern.db");
            await ValidateDatabaseAsync(restoredDatabase);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = restoredDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            var characters = await ReadRowsAsync(
                connection, "SELECT Id, Name, GroupName FROM Characters ORDER BY Name",
                reader => new BackupCharacter(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            var sessions = await ReadRowsAsync(
                connection,
                @"SELECT s.Id, s.Title, s.IsGroupChat,
                         (SELECT COUNT(*) FROM ChatMessages m WHERE m.ChatSessionId = s.Id)
                    FROM ChatSessions s ORDER BY s.UpdatedAt DESC",
                reader => new BackupSession(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(3), reader.GetInt64(2) != 0));
            return new BackupPreview(characters, sessions);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    /// <summary>
    /// Copies the chosen characters and conversations out of a backup into the live library.
    /// Insert-only: existing rows are never modified or removed, and every identifier is
    /// regenerated so a desktop and a phone library can hold the same-named character twice.
    /// </summary>
    public async Task<BackupImportResult> ImportSelectedAsync(
        string backupPath,
        IReadOnlyCollection<long> characterIds,
        IReadOnlyCollection<long> sessionIds,
        string? safetyBackupDirectory = null)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到备份文件。", source);
        if (!File.Exists(databaseFile)) throw new FileNotFoundException("找不到 Lorehaven 数据库。", databaseFile);
        if (characterIds.Count == 0 && sessionIds.Count == 0) return new BackupImportResult(0, 0, 0);

        var temporary = CreateTemporaryDirectory();
        try
        {
            ValidateArchive(source, temporary);
            ZipFile.ExtractToDirectory(source, temporary);
            var restoredRoot = Path.Combine(temporary, "UserData");
            var restoredDatabase = Path.Combine(restoredRoot, "Data", "NativeTavern.db");
            await ValidateDatabaseAsync(restoredDatabase);
            var origin = (ReadOriginRoot(temporary) ?? string.Empty).Replace('\\', '/').TrimEnd('/');

            await CreateSafetyBackupAsync(safetyBackupDirectory);

            SqliteConnection.ClearAllPools();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS src;";
                attach.Parameters.AddWithValue("$path", restoredDatabase);
                await attach.ExecuteNonQueryAsync();
            }
            await using var transaction = await connection.BeginTransactionAsync();
            var characters = await ImportCharactersAsync(connection, restoredRoot, origin, characterIds, sessionIds);
            var resources = await ImportResourcesAsync(connection, sessionIds);
            var (sessions, messages) = await ImportSessionsAsync(connection, restoredRoot, origin, sessionIds, characters, resources);
            await transaction.CommitAsync();
            return new BackupImportResult(characters.Count, sessions, messages);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private async Task<string> CreateSafetyBackupAsync(string? safetyBackupDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(safetyBackupDirectory)
            ? Directory.GetParent(root)?.FullName ?? root
            : Path.GetFullPath(safetyBackupDirectory);
        var safetyBackup = Path.Combine(directory, $"Lorehaven-before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await CreateAsync(safetyBackup);
        return safetyBackup;
    }

    private static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        SqliteConnection connection, string sql, Func<SqliteDataReader, T> project)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(project(reader));
        return rows;
    }

    private static string JoinIds(IReadOnlyCollection<long> ids) =>
        string.Join(',', ids.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static long? MapId(Dictionary<long, long> map, long? oldId) =>
        oldId is { } id && map.TryGetValue(id, out var mapped) ? mapped : null;

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(
        SqliteConnection connection, string sql, Func<SqliteDataReader, T> project,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(project(reader));
        return rows;
    }

    private static async Task<long> ExecuteInsertAsync(
        SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = sql;
            foreach (var (name, value) in parameters) insert.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        await using var identity = connection.CreateCommand();
        identity.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(await identity.ExecuteScalarAsync() ?? 0L);
    }

    private static bool IsOriginPath(string? storedPath, string origin) =>
        !string.IsNullOrWhiteSpace(storedPath) && !string.IsNullOrEmpty(origin) &&
        storedPath.Replace('\\', '/').StartsWith(origin + '/', StringComparison.Ordinal);

    // Turns a path stored by the origin machine into the file inside the extracted archive.
    private static string? ResolveArchivedFile(string? storedPath, string restoredRoot, string origin)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrEmpty(origin)) return null;
        var normalized = storedPath.Replace('\\', '/');
        if (!normalized.StartsWith(origin + '/', StringComparison.Ordinal)) return null;
        var segments = normalized[(origin.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidate = ResolveContainedPath(restoredRoot, segments);
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveContainedPath(string directory, string[] segments)
    {
        if (segments.Length == 0) return null;
        try
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var candidate = Path.GetFullPath(Path.Combine([parent, .. segments]));
            return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Characters referenced by the chosen conversations come along even when they were not
    // ticked, otherwise the imported chat would speak with a blank cast.
    private async Task<Dictionary<long, long>> ImportCharactersAsync(
        SqliteConnection connection, string restoredRoot, string origin,
        IReadOnlyCollection<long> characterIds, IReadOnlyCollection<long> sessionIds)
    {
        var sessions = sessionIds.Count > 0 ? JoinIds(sessionIds) : "0";
        var memoryColumns = await QueryAsync(connection, "PRAGMA src.table_info(CharacterMemories);", reader => reader.GetString(1));
        var lockedColumn = memoryColumns.Contains("IsLocked", StringComparer.OrdinalIgnoreCase) ? "IsLocked" : "0";
        var referenced = await QueryAsync(connection,
            $@"SELECT CharacterId FROM src.ChatSessions WHERE Id IN ({sessions}) AND CharacterId IS NOT NULL
               UNION SELECT CharacterId FROM src.ChatSessionCharacters WHERE ChatSessionId IN ({sessions})
               UNION SELECT SpeakerCharacterId FROM src.ChatMessages WHERE ChatSessionId IN ({sessions}) AND SpeakerCharacterId IS NOT NULL",
            reader => reader.GetInt64(0));
        var map = new Dictionary<long, long>();
        foreach (var oldId in characterIds.Concat(referenced).Where(id => id > 0).Distinct().Order())
        {
            var rows = await QueryAsync(connection,
                @"SELECT Name, Description, Personality, Scenario, FirstMessage, ExampleMessages, Creator, Tags,
                         GroupName, Aliases, IsFavorite, AvatarPath, CreatedAt, UpdatedAt
                    FROM src.Characters WHERE Id = $id;",
                reader => new
                {
                    Name = reader.GetString(0), Description = reader.GetString(1), Personality = reader.GetString(2),
                    Scenario = reader.GetString(3), FirstMessage = reader.GetString(4), ExampleMessages = reader.GetString(5),
                    Creator = reader.GetString(6), Tags = reader.GetString(7), GroupName = reader.GetString(8),
                    Aliases = reader.GetString(9), IsFavorite = reader.GetInt64(10), AvatarPath = reader.GetString(11),
                    CreatedAt = reader.GetString(12), UpdatedAt = reader.GetString(13)
                },
                ("$id", oldId));
            if (rows.Count == 0) continue;
            var row = rows[0];

            var avatarPath = row.AvatarPath;
            var archivedAvatar = ResolveArchivedFile(row.AvatarPath, restoredRoot, origin);
            if (archivedAvatar is not null)
            {
                Directory.CreateDirectory(Path.Combine(root, "Avatars"));
                var destination = Path.Combine(root, "Avatars", Guid.NewGuid().ToString("N") + Path.GetExtension(archivedAvatar));
                File.Copy(archivedAvatar, destination);
                avatarPath = destination;
            }
            else if (IsOriginPath(row.AvatarPath, origin))
            {
                // The file did not survive in the archive. Keep it empty rather than storing a
                // foreign absolute path that could never resolve on this device.
                avatarPath = string.Empty;
            }

            var newId = await ExecuteInsertAsync(connection,
                @"INSERT INTO Characters (Name, Description, Personality, Scenario, FirstMessage, ExampleMessages,
                        Creator, Tags, GroupName, Aliases, IsFavorite, AvatarPath, CreatedAt, UpdatedAt)
                  VALUES ($name, $description, $personality, $scenario, $firstMessage, $exampleMessages,
                        $creator, $tags, $groupName, $aliases, $isFavorite, $avatarPath, $createdAt, $updatedAt);",
                ("$name", row.Name), ("$description", row.Description), ("$personality", row.Personality),
                ("$scenario", row.Scenario), ("$firstMessage", row.FirstMessage), ("$exampleMessages", row.ExampleMessages),
                ("$creator", row.Creator), ("$tags", row.Tags), ("$groupName", row.GroupName), ("$aliases", row.Aliases),
                ("$isFavorite", row.IsFavorite), ("$avatarPath", avatarPath),
                ("$createdAt", row.CreatedAt), ("$updatedAt", row.UpdatedAt));
            if (newId == 0) continue;
            map[oldId] = newId;

            if (!string.IsNullOrWhiteSpace(row.GroupName))
                await ExecuteInsertAsync(connection,
                    "INSERT OR IGNORE INTO CharacterGroups (Name, CreatedAt) VALUES ($name, $createdAt);",
                    ("$name", row.GroupName), ("$createdAt", row.CreatedAt));

            var memories = await QueryAsync(connection,
                $"SELECT Content, CreatedAt, UpdatedAt, {lockedColumn} FROM src.CharacterMemories WHERE CharacterId = $id ORDER BY Id;",
                reader => (Content: reader.GetString(0), CreatedAt: reader.GetString(1), UpdatedAt: reader.GetString(2), IsLocked: reader.GetInt64(3)),
                ("$id", oldId));
            foreach (var memory in memories)
                await ExecuteInsertAsync(connection,
                    "INSERT INTO CharacterMemories (CharacterId, Content, CreatedAt, UpdatedAt, IsLocked) VALUES ($character, $content, $createdAt, $updatedAt, $isLocked);",
                    ("$character", newId), ("$content", memory.Content),
                    ("$createdAt", memory.CreatedAt), ("$updatedAt", memory.UpdatedAt), ("$isLocked", memory.IsLocked));
        }
        return map;
    }

    private static async Task<(Dictionary<long, long> Personas, Dictionary<long, long> Lorebooks, Dictionary<long, long> Presets)>
        ImportResourcesAsync(SqliteConnection connection, IReadOnlyCollection<long> sessionIds)
    {
        var sessions = sessionIds.Count > 0 ? JoinIds(sessionIds) : "0";
        var personas = new Dictionary<long, long>();
        var lorebooks = new Dictionary<long, long>();
        var presets = new Dictionary<long, long>();

        foreach (var oldId in await QueryAsync(connection,
            $"SELECT DISTINCT PersonaId FROM src.ChatSessions WHERE Id IN ({sessions}) AND PersonaId IS NOT NULL",
            reader => reader.GetInt64(0)))
        {
            var rows = await QueryAsync(connection, "SELECT Name, Content, CreatedAt, UpdatedAt FROM src.Personas WHERE Id = $id;",
                reader => (Name: reader.GetString(0), Content: reader.GetString(1), CreatedAt: reader.GetString(2), UpdatedAt: reader.GetString(3)),
                ("$id", oldId));
            if (rows.Count == 0) continue;
            var newId = await ExecuteInsertAsync(connection,
                "INSERT INTO Personas (Name, Content, CreatedAt, UpdatedAt) VALUES ($name, $content, $createdAt, $updatedAt);",
                ("$name", rows[0].Name), ("$content", rows[0].Content), ("$createdAt", rows[0].CreatedAt), ("$updatedAt", rows[0].UpdatedAt));
            if (newId != 0) personas[oldId] = newId;
        }

        foreach (var oldId in await QueryAsync(connection,
            $"SELECT DISTINCT LorebookId FROM src.ChatSessions WHERE Id IN ({sessions}) AND LorebookId IS NOT NULL",
            reader => reader.GetInt64(0)))
        {
            var rows = await QueryAsync(connection, "SELECT Name, Description, IsEnabled, CreatedAt, UpdatedAt FROM src.Lorebooks WHERE Id = $id;",
                reader => (Name: reader.GetString(0), Description: reader.GetString(1), IsEnabled: reader.GetInt64(2),
                    CreatedAt: reader.GetString(3), UpdatedAt: reader.GetString(4)),
                ("$id", oldId));
            if (rows.Count == 0) continue;
            var newId = await ExecuteInsertAsync(connection,
                "INSERT INTO Lorebooks (Name, Description, IsEnabled, CreatedAt, UpdatedAt) VALUES ($name, $description, $isEnabled, $createdAt, $updatedAt);",
                ("$name", rows[0].Name), ("$description", rows[0].Description), ("$isEnabled", rows[0].IsEnabled),
                ("$createdAt", rows[0].CreatedAt), ("$updatedAt", rows[0].UpdatedAt));
            if (newId == 0) continue;
            lorebooks[oldId] = newId;

            var entries = await QueryAsync(connection,
                @"SELECT Name, Keywords, SecondaryKeywords, Content, Priority, Depth, IsEnabled, IsConstant, IsSelective
                    FROM src.LoreEntries WHERE LorebookId = $id ORDER BY Id;",
                reader => (Name: reader.GetString(0), Keywords: reader.GetString(1), SecondaryKeywords: reader.GetString(2),
                    Content: reader.GetString(3), Priority: reader.GetInt64(4), Depth: reader.GetInt64(5),
                    IsEnabled: reader.GetInt64(6), IsConstant: reader.GetInt64(7), IsSelective: reader.GetInt64(8)),
                ("$id", oldId));
            foreach (var entry in entries)
                await ExecuteInsertAsync(connection,
                    @"INSERT INTO LoreEntries (LorebookId, Name, Keywords, SecondaryKeywords, Content, Priority, Depth, IsEnabled, IsConstant, IsSelective)
                      VALUES ($book, $name, $keywords, $secondary, $content, $priority, $depth, $isEnabled, $isConstant, $isSelective);",
                    ("$book", newId), ("$name", entry.Name), ("$keywords", entry.Keywords), ("$secondary", entry.SecondaryKeywords),
                    ("$content", entry.Content), ("$priority", entry.Priority), ("$depth", entry.Depth),
                    ("$isEnabled", entry.IsEnabled), ("$isConstant", entry.IsConstant), ("$isSelective", entry.IsSelective));
        }

        foreach (var oldId in await QueryAsync(connection,
            $"SELECT DISTINCT PromptPresetId FROM src.ChatSessions WHERE Id IN ({sessions}) AND PromptPresetId IS NOT NULL",
            reader => reader.GetInt64(0)))
        {
            var rows = await QueryAsync(connection,
                @"SELECT Name, SystemPrompt, MainPrompt, Model, Temperature, TopP, MaxTokens, CreatedAt, UpdatedAt
                    FROM src.PromptPresets WHERE Id = $id;",
                reader => (Name: reader.GetString(0), SystemPrompt: reader.GetString(1), MainPrompt: reader.GetString(2),
                    Model: reader.GetString(3), Temperature: reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4),
                    TopP: reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),
                    MaxTokens: reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6),
                    CreatedAt: reader.GetString(7), UpdatedAt: reader.GetString(8)),
                ("$id", oldId));
            if (rows.Count == 0) continue;
            var row = rows[0];
            var newId = await ExecuteInsertAsync(connection,
                @"INSERT INTO PromptPresets (Name, SystemPrompt, MainPrompt, Model, Temperature, TopP, MaxTokens, CreatedAt, UpdatedAt)
                  VALUES ($name, $system, $main, $model, $temperature, $topP, $maxTokens, $createdAt, $updatedAt);",
                ("$name", row.Name), ("$system", row.SystemPrompt), ("$main", row.MainPrompt), ("$model", row.Model),
                ("$temperature", row.Temperature), ("$topP", row.TopP), ("$maxTokens", row.MaxTokens),
                ("$createdAt", row.CreatedAt), ("$updatedAt", row.UpdatedAt));
            if (newId != 0) presets[oldId] = newId;
        }
        return (personas, lorebooks, presets);
    }

    private async Task<(int Sessions, int Messages)> ImportSessionsAsync(
        SqliteConnection connection, string restoredRoot, string origin, IReadOnlyCollection<long> sessionIds,
        Dictionary<long, long> characters, (Dictionary<long, long> Personas, Dictionary<long, long> Lorebooks, Dictionary<long, long> Presets) resources)
    {
        var importedSessions = 0;
        var importedMessages = 0;
        foreach (var oldSessionId in sessionIds.Order())
        {
            var rows = await QueryAsync(connection,
                @"SELECT Title, CharacterId, IsGroupChat, PersonaId, LorebookId, PromptPresetId, AuthorNote, Summary,
                         SummaryCoveredCount, SummaryIsManual, MemoryCoveredCount, GroupName, IsPinned, CreatedAt, UpdatedAt
                    FROM src.ChatSessions WHERE Id = $id;",
                reader => (Title: reader.GetString(0), CharacterId: reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
                    IsGroupChat: reader.GetInt64(2), PersonaId: reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                    LorebookId: reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    PromptPresetId: reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    AuthorNote: reader.GetString(6), Summary: reader.GetString(7),
                    SummaryCoveredCount: reader.GetInt64(8), SummaryIsManual: reader.GetInt64(9),
                    MemoryCoveredCount: reader.GetInt64(10), GroupName: reader.GetString(11), IsPinned: reader.GetInt64(12),
                    CreatedAt: reader.GetString(13), UpdatedAt: reader.GetString(14)),
                ("$id", oldSessionId));
            if (rows.Count == 0) continue;
            var session = rows[0];

            var newSessionId = await ExecuteInsertAsync(connection,
                @"INSERT INTO ChatSessions (Title, CharacterId, IsGroupChat, ParentSessionId, BranchedFromMessageId,
                        PersonaId, LorebookId, PromptPresetId, AuthorNote, Summary, SummaryCoveredCount, SummaryIsManual,
                        MemoryCoveredCount, GroupName, IsPinned, CreatedAt, UpdatedAt)
                  VALUES ($title, $character, $isGroupChat, NULL, NULL, $persona, $lorebook, $preset, $authorNote, $summary,
                        $summaryCovered, $summaryManual, $memoryCovered, $groupName, $isPinned, $createdAt, $updatedAt);",
                ("$title", session.Title), ("$character", MapId(characters, session.CharacterId)),
                ("$isGroupChat", session.IsGroupChat), ("$persona", MapId(resources.Personas, session.PersonaId)),
                ("$lorebook", MapId(resources.Lorebooks, session.LorebookId)), ("$preset", MapId(resources.Presets, session.PromptPresetId)),
                ("$authorNote", session.AuthorNote), ("$summary", session.Summary),
                ("$summaryCovered", session.SummaryCoveredCount), ("$summaryManual", session.SummaryIsManual),
                ("$memoryCovered", session.MemoryCoveredCount), ("$groupName", session.GroupName),
                ("$isPinned", session.IsPinned), ("$createdAt", session.CreatedAt), ("$updatedAt", session.UpdatedAt));
            if (newSessionId == 0) continue;
            importedSessions++;

            var members = await QueryAsync(connection,
                "SELECT CharacterId, SortOrder FROM src.ChatSessionCharacters WHERE ChatSessionId = $id ORDER BY SortOrder;",
                reader => (CharacterId: reader.GetInt64(0), SortOrder: reader.GetInt64(1)),
                ("$id", oldSessionId));
            foreach (var member in members)
            {
                var characterId = MapId(characters, member.CharacterId);
                if (characterId is null) continue;
                await ExecuteInsertAsync(connection,
                    "INSERT OR IGNORE INTO ChatSessionCharacters (ChatSessionId, CharacterId, SortOrder) VALUES ($session, $character, $order);",
                    ("$session", newSessionId), ("$character", characterId.Value), ("$order", member.SortOrder));
            }

            var messages = await QueryAsync(connection,
                @"SELECT Id, Role, SpeakerCharacterId, Content, CreatedAt, UpdatedAt, CurrentSwipeIndex, IsPinned
                    FROM src.ChatMessages WHERE ChatSessionId = $id ORDER BY Id;",
                reader => (Id: reader.GetInt64(0), Role: reader.GetString(1),
                    SpeakerCharacterId: reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                    Content: reader.GetString(3), CreatedAt: reader.GetString(4),
                    UpdatedAt: reader.IsDBNull(5) ? null : reader.GetString(5),
                    CurrentSwipeIndex: reader.GetInt64(6), IsPinned: reader.GetInt64(7)),
                ("$id", oldSessionId));
            foreach (var message in messages)
            {
                var newMessageId = await ExecuteInsertAsync(connection,
                    @"INSERT INTO ChatMessages (ChatSessionId, Role, SpeakerCharacterId, Content, CreatedAt, UpdatedAt, CurrentSwipeIndex, IsPinned)
                      VALUES ($session, $role, $speaker, $content, $createdAt, $updatedAt, $swipeIndex, $isPinned);",
                    ("$session", newSessionId), ("$role", message.Role),
                    ("$speaker", MapId(characters, message.SpeakerCharacterId)), ("$content", message.Content),
                    ("$createdAt", message.CreatedAt), ("$updatedAt", message.UpdatedAt),
                    ("$swipeIndex", message.CurrentSwipeIndex), ("$isPinned", message.IsPinned));
                if (newMessageId == 0) continue;
                importedMessages++;

                var swipes = await QueryAsync(connection,
                    "SELECT SwipeIndex, Content, CreatedAt FROM src.MessageSwipes WHERE ChatMessageId = $id ORDER BY SwipeIndex;",
                    reader => (SwipeIndex: reader.GetInt64(0), Content: reader.GetString(1), CreatedAt: reader.GetString(2)),
                    ("$id", message.Id));
                foreach (var swipe in swipes)
                    await ExecuteInsertAsync(connection,
                        "INSERT INTO MessageSwipes (ChatMessageId, SwipeIndex, Content, CreatedAt) VALUES ($message, $index, $content, $createdAt);",
                        ("$message", newMessageId), ("$index", swipe.SwipeIndex), ("$content", swipe.Content), ("$createdAt", swipe.CreatedAt));

                var attachments = await QueryAsync(connection,
                    "SELECT FileName, FilePath, MimeType, SizeBytes, CreatedAt FROM src.ChatAttachments WHERE ChatMessageId = $id ORDER BY Id;",
                    reader => (FileName: reader.GetString(0), FilePath: reader.GetString(1), MimeType: reader.GetString(2),
                        SizeBytes: reader.GetInt64(3), CreatedAt: reader.GetString(4)),
                    ("$id", message.Id));
                foreach (var attachment in attachments)
                {
                    var archived = ResolveArchivedFile(attachment.FilePath, restoredRoot, origin);
                    if (archived is null) continue;   // the bytes never made it into the archive
                    Directory.CreateDirectory(Path.Combine(root, "Attachments"));
                    var destination = Path.Combine(root, "Attachments", Guid.NewGuid().ToString("N") + Path.GetExtension(archived));
                    File.Copy(archived, destination);
                    await ExecuteInsertAsync(connection,
                        "INSERT INTO ChatAttachments (ChatMessageId, FileName, FilePath, MimeType, SizeBytes, CreatedAt) VALUES ($message, $name, $path, $mime, $size, $createdAt);",
                        ("$message", newMessageId), ("$name", attachment.FileName), ("$path", destination),
                        ("$mime", attachment.MimeType), ("$size", attachment.SizeBytes), ("$createdAt", attachment.CreatedAt));
                }
            }

            var statuses = await QueryAsync(connection,
                "SELECT CharacterId, CharacterName, Summary, AttributesJson, UpdatedAt FROM src.CharacterStatuses WHERE ChatSessionId = $id;",
                reader => (CharacterId: reader.GetInt64(0), CharacterName: reader.GetString(1), Summary: reader.GetString(2),
                    AttributesJson: reader.GetString(3), UpdatedAt: reader.GetString(4)),
                ("$id", oldSessionId));
            foreach (var status in statuses)
            {
                var characterId = MapId(characters, status.CharacterId);
                if (characterId is null) continue;
                await ExecuteInsertAsync(connection,
                    @"INSERT OR IGNORE INTO CharacterStatuses (ChatSessionId, CharacterId, CharacterName, Summary, AttributesJson, SourceMessageId, UpdatedAt)
                      VALUES ($session, $character, $name, $summary, $attributes, NULL, $updatedAt);",
                    ("$session", newSessionId), ("$character", characterId.Value), ("$name", status.CharacterName),
                    ("$summary", status.Summary), ("$attributes", status.AttributesJson), ("$updatedAt", status.UpdatedAt));
            }

            var drafts = await QueryAsync(connection,
                "SELECT DraftText, DraftImages, UpdatedAt FROM src.ComposerDrafts WHERE ChatSessionId = $id;",
                reader => (DraftText: reader.GetString(0), DraftImages: reader.GetString(1), UpdatedAt: reader.GetString(2)),
                ("$id", oldSessionId));
            foreach (var draft in drafts)
                await ExecuteInsertAsync(connection,
                    "INSERT OR REPLACE INTO ComposerDrafts (ChatSessionId, DraftText, DraftImages, UpdatedAt) VALUES ($session, $text, $images, $updatedAt);",
                    ("$session", newSessionId), ("$text", draft.DraftText), ("$images", draft.DraftImages), ("$updatedAt", draft.UpdatedAt));
        }
        return (importedSessions, importedMessages);
    }

    private bool IsInsideRoot(string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lorehaven-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ValidateArchive(string archivePath, string extractionDirectory)
    {
        const long maxExpandedBytes = 16L * 1024 * 1024 * 1024;
        const int maxEntries = 100_000;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > maxEntries)
            throw new InvalidDataException("备份包含过多文件。");
        var manifest = archive.GetEntry("backup.json");
        if (manifest is null)
            throw new InvalidDataException("不是有效的 Lorehaven 备份。");
        if (manifest.Length > 65_536) throw new InvalidDataException("备份清单过大。");
        try
        {
            using var manifestStream = manifest.Open();
            using var document = JsonDocument.Parse(manifestStream);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() is not (BackupFormat or LegacyBackupFormat) ||
                !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) || versionNumber != 1 ||
                (value.TryGetProperty("userDataRoot", out var origin) &&
                 origin.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw new InvalidDataException("备份清单无效或版本不受支持。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("备份清单不是有效 JSON。", ex);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory)) +
                   Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > maxExpandedBytes - expandedBytes)
                throw new InvalidDataException("备份解压后的大小超过 16 GB 限制。");
            expandedBytes += entry.Length;
            var target = Path.GetFullPath(Path.Combine(extractionDirectory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份包含不安全的文件路径。");
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new InvalidDataException($"备份中缺少 {Path.GetFileName(destination)} 文件夹。");
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        CopyDirectory(source, destination);
    }
}
