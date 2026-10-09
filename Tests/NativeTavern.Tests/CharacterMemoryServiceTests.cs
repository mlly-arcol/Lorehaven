using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

/// Character memories are distilled by the model but stored and gated locally, so both the JSON
/// parsing and the extraction rules get their own tests on a throwaway database.
public sealed class CharacterMemoryServiceTests
{
    [Fact]
    public void ParseResponseExtractsTrimmedDistinctMemories()
    {
        var parsed = CharacterMemoryService.ParseResponse(
            "前置噪音 {\"memories\":[\" 用户叫阿澄 \",\"- 用户喜欢雨天\",\"用户喜欢雨天\",\"\"] } 后续噪音");
        Assert.Equal(["用户叫阿澄", "用户喜欢雨天"], parsed);
    }

    [Fact]
    public void ParseResponseLimitsCountAndLength()
    {
        var longMemory = new string('记', CharacterMemoryService.MemoryLengthLimit + 50);
        var memories = string.Join(",", Enumerable.Range(1, 8).Select(x => $"\"记忆{x}\""));
        var parsed = CharacterMemoryService.ParseResponse($"{{\"memories\":[{memories},\"{longMemory}\"]}}");
        Assert.Equal(5, parsed.Count);
        Assert.All(parsed, x => Assert.True(x.Length <= CharacterMemoryService.MemoryLengthLimit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"other\":[]}")]
    [InlineData("{\"memories\":\"oops\"}")]
    public void ParseResponseRejectsMalformedOutput(string response)
    {
        Assert.Empty(CharacterMemoryService.ParseResponse(response));
    }

    [Fact]
    public async Task ExtractionStaysOffUntilEnabled()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);
        await env.Service.ExtractIfNeededAsync(session);
        Assert.Equal(0, env.Provider.Calls);
        Assert.Empty(await env.Memories.GetByCharacterAsync(env.CharacterId));
    }

    [Fact]
    public async Task ExtractionWaitsForTheInterval()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval - 1);
        await env.Service.ExtractIfNeededAsync(session);
        Assert.Equal(0, env.Provider.Calls);
    }

    [Fact]
    public async Task ExtractionStoresMemoriesAndMovesTheWatermark()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        env.Provider.Response = "{\"memories\":[\"用户叫阿澄\",\"用户喜欢雨天\"]}";
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);

        await env.Service.ExtractIfNeededAsync(session);

        var stored = await env.Memories.GetByCharacterAsync(env.CharacterId);
        Assert.Equal(["用户叫阿澄", "用户喜欢雨天"], stored.Select(x => x.Content).ToArray());
        var reloaded = await env.Sessions.GetAsync(session.Id);
        Assert.Equal(CharacterMemoryService.ExtractionInterval, reloaded!.MemoryCoveredCount);
    }

    [Fact]
    public async Task ExtractionDeduplicatesAgainstExistingMemories()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        await env.Service.AddManualAsync(env.CharacterId, "用户喜欢雨天");
        env.Provider.Response = "{\"memories\":[\"用户喜欢雨天\",\"用户约好了周六见面\"]}";
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);

        await env.Service.ExtractIfNeededAsync(session);

        var stored = await env.Memories.GetByCharacterAsync(env.CharacterId);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, x => x.Content == "用户约好了周六见面");
    }

    [Fact]
    public async Task ForcedExtractionIgnoresTheInterval()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        env.Provider.Response = "{\"memories\":[\"用户喜欢雨天\"]}";
        var session = await env.CreateSessionWithMessagesAsync(2);

        await env.Service.ExtractIfNeededAsync(session, force: true);

        Assert.Equal(1, env.Provider.Calls);
        Assert.Single(await env.Memories.GetByCharacterAsync(env.CharacterId));
    }

    [Fact]
    public async Task GroupChatsAreSkipped()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);
        session.IsGroupChat = true;
        await env.Service.ExtractIfNeededAsync(session, force: true);
        Assert.Equal(0, env.Provider.Calls);
    }

    [Fact]
    public async Task ManualAddValidatesInput()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.AddManualAsync(env.CharacterId, "   "));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Service.AddManualAsync(env.CharacterId, new string('长', CharacterMemoryService.MemoryLengthLimit + 1)));
        await env.Service.AddManualAsync(env.CharacterId, "  用户喜欢雨天  ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.AddManualAsync(env.CharacterId, "用户喜欢雨天"));
        var stored = await env.Memories.GetByCharacterAsync(env.CharacterId);
        Assert.Equal("用户喜欢雨天", Assert.Single(stored).Content);
    }

    [Fact]
    public async Task DeletingACharacterCascadesMemories()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        await env.Service.AddManualAsync(env.CharacterId, "用户喜欢雨天");
        await env.Characters.DeleteAsync(env.CharacterId);
        Assert.Empty(await env.Memories.GetByCharacterAsync(env.CharacterId));
    }

    private sealed class Env : IDisposable
    {
        private readonly string _directory;

        private Env(string directory) => _directory = directory;

        public required CharacterMemoryService Service { get; init; }
        public required CharacterMemoryRepository Memories { get; init; }
        public required ChatSessionRepository Sessions { get; init; }
        public required CharacterRepository Characters { get; init; }
        public required StubProvider Provider { get; init; }
        public long CharacterId { get; private set; }
        private ChatMessageRepository Messages { get; init; } = null!;
        private ChatSessionRepository SessionRepository { get; init; } = null!;

        public static async Task<Env> CreateAsync(bool enableMemory)
        {
            var directory = Path.Combine(Path.GetTempPath(), "NativeTavernMemory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var factory = new DatabaseConnectionFactory(Path.Combine(directory, "data.db"));
            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            var messages = new ChatMessageRepository(factory);
            var sessions = new ChatSessionRepository(factory);
            var characters = new CharacterRepository(factory);
            var memories = new CharacterMemoryRepository(factory);
            var settings = new SettingsService(new SettingsRepository(factory), new NoopProtector());
            await settings.SaveAsync(new ProviderSettings
            {
                BaseUrl = "http://127.0.0.1:8080/v1",
                Model = "m",
                CharacterMemoryEnabled = enableMemory
            }, null);
            var provider = new StubProvider();
            var service = new CharacterMemoryService(
                memories, messages, sessions, characters, settings, provider,
                NullLogger<CharacterMemoryService>.Instance);
            var env = new Env(directory)
            {
                Service = service, Memories = memories, Sessions = sessions,
                Characters = characters, Provider = provider,
                Messages = messages, SessionRepository = sessions
            };
            var character = new Character { Name = "苔", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            env.CharacterId = await characters.CreateAsync(character);
            return env;
        }

        public async Task<ChatSession> CreateSessionWithMessagesAsync(int count)
        {
            var now = DateTimeOffset.UtcNow;
            var session = new ChatSession
            {
                Title = "测试对话", CharacterId = CharacterId, CreatedAt = now, UpdatedAt = now
            };
            session.Id = await SessionRepository.CreateAsync(session);
            for (var index = 0; index < count; index++)
                await Messages.AddAsync(new ChatMessage
                {
                    ChatSessionId = session.Id,
                    Role = index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    Content = "第 " + (index + 1) + " 条消息，聊聊最近的安排。",
                    CreatedAt = now
                });
            return session;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private sealed class StubProvider : ILLMProvider
    {
        public string Response { get; set; } = "{\"memories\":[]}";
        public int Calls { get; private set; }
        public string Id => "stub";
        public string DisplayName => "Stub";
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<string> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            yield return Response;
        }
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
