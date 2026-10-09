using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task NewChatReusesEmptySessionAndCreatesOnlyAfterItHasMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeTavernEmptyChat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, messages) = await CreateServiceAsync(Path.Combine(directory, "chat.db"), new FailingProvider());
            var empty = await service.CreateSessionAsync();
            var other = await service.CreateSessionAsync();
            await messages.AddAsync(new ChatMessage { ChatSessionId = other.Id, Role = ChatRole.User, Content = "hello", CreatedAt = DateTimeOffset.UtcNow });
            var reused = await service.GetOrCreateEmptySessionAsync();
            Assert.Equal(empty.Id, reused.Id);
            Assert.Equal(2, (await service.GetSessionsAsync()).Count);
            await messages.AddAsync(new ChatMessage { ChatSessionId = empty.Id, Role = ChatRole.User, Content = "started", CreatedAt = DateTimeOffset.UtcNow });
            var created = await service.GetOrCreateEmptySessionAsync();
            Assert.NotEqual(empty.Id, created.Id);
            Assert.NotEqual(other.Id, created.Id);
            Assert.Equal(created.Id, (await service.GetOrCreateEmptySessionAsync()).Id);
            Assert.Equal(3, (await service.GetSessionsAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DatabaseInitializerMigratesBranchColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeTavernMigrationTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "chat.db");
        try
        {
            await using (var connection = new SqliteConnection("Data Source=" + database))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE ChatSessions (Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT NOT NULL, CharacterId INTEGER NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync();
            }

            var factory = new DatabaseConnectionFactory(database);
            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            await using var migrated = new SqliteConnection("Data Source=" + database);
            await migrated.OpenAsync();
            var inspect = migrated.CreateCommand();
            inspect.CommandText = "SELECT name FROM pragma_table_info('ChatSessions') WHERE name IN ('ParentSessionId','BranchedFromMessageId') ORDER BY name";
            var columns = new List<string>();
            await using var reader = await inspect.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            Assert.Equal(["BranchedFromMessageId", "ParentSessionId"], columns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BranchCopiesHistoryThroughSelectedMessageAndTracksOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeTavernBranchTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, messages) = await CreateServiceAsync(Path.Combine(directory, "chat.db"), new FailingProvider());
            var source = await service.CreateSessionAsync();
            var first = new ChatMessage
            {
                ChatSessionId = source.Id,
                Role = ChatRole.User,
                Content = "branch here",
                CreatedAt = DateTimeOffset.UtcNow
            };
            await messages.AddAsync(first);
            await messages.AddAsync(new ChatMessage
            {
                ChatSessionId = source.Id,
                Role = ChatRole.Assistant,
                Content = "do not copy",
                CreatedAt = DateTimeOffset.UtcNow
            });

            var branch = await service.BranchSessionAsync(source, first.Id);
            var copied = Assert.Single(await service.GetMessagesAsync(branch.Id));

            Assert.Equal(source.Id, branch.ParentSessionId);
            Assert.Equal(first.Id, branch.BranchedFromMessageId);
            Assert.Equal("branch here", copied.Content);
            Assert.Equal(branch.Id, copied.ChatSessionId);

            await service.DeleteSessionAsync(source.Id);
            var survivingBranch = await service.GetSessionAsync(branch.Id);
            Assert.NotNull(survivingBranch);
            Assert.Null(survivingBranch!.ParentSessionId);
            Assert.Single(await service.GetMessagesAsync(branch.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ConversationTitleIsTrimmedAndValidated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeTavernTitleTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, _) = await CreateServiceAsync(Path.Combine(directory, "chat.db"), new FailingProvider());
            var session = await service.CreateSessionAsync();

            await service.UpdateSessionTitleAsync(session, "  New title  ");

            Assert.Equal("New title", session.Title);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateSessionTitleAsync(session, "   "));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProviderFailureBeforeFirstChunkDoesNotLeaveBlankAssistantMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NativeTavernChatTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "chat.db");
            var (service, messages) = await CreateServiceAsync(database, new FailingProvider());
            var session = await service.CreateSessionAsync();

            await Assert.ThrowsAsync<ProviderException>(() => service.SendAsync(
                session,
                "保留用户消息",
                [],
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                CancellationToken.None));

            var stored = await messages.GetBySessionAsync(session.Id);
            var user = Assert.Single(stored);
            Assert.Equal(ChatRole.User, user.Role);
            Assert.Equal("保留用户消息", user.Content);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(ChatService Service, ChatMessageRepository Messages)> CreateServiceAsync(
        string database,
        ILLMProvider provider)
    {
        var factory = new DatabaseConnectionFactory(database);
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var sessions = new ChatSessionRepository(factory);
        var messages = new ChatMessageRepository(factory);
        var swipes = new MessageSwipeRepository(factory);
        var attachments = new ChatAttachmentRepository(factory);
        var characters = new CharacterRepository(factory);
        var prompts = new PromptRepository(factory);
        var knowledgeRepository = new KnowledgeRepository(factory);
        var settings = new SettingsService(new SettingsRepository(factory), new PassthroughProtector());
        await settings.SaveAsync(new ProviderSettings
        {
            BaseUrl = "http://127.0.0.1:8080/v1",
            Model = "test-model"
        }, null);
        var knowledge = new KnowledgeService(knowledgeRepository, NullLogger<KnowledgeService>.Instance);
        var promptService = new PromptService(prompts, characters, sessions, knowledge);
        var attachmentService = new AttachmentService(attachments, NullLogger<AttachmentService>.Instance);
        var summary = new ConversationSummaryService(messages, sessions);
        var memory = new CharacterMemoryService(
            new CharacterMemoryRepository(factory), messages, sessions, characters,
            settings, provider, NullLogger<CharacterMemoryService>.Instance);
        return (new ChatService(
            sessions, messages, swipes, attachments, attachmentService, summary, memory, characters,
            promptService,
            new RegexScriptService(new RegexScriptRepository(factory), NullLogger<RegexScriptService>.Instance),
            settings, provider, NullLogger<ChatService>.Instance), messages);
    }

    private sealed class FailingProvider : ILLMProvider
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<string> StreamAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new ProviderException("模拟服务故障。");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
