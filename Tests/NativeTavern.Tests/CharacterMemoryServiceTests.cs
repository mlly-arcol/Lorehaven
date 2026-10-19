using System.Runtime.CompilerServices;
using Dapper;
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
    public async Task GroupChatsWithoutAttributedParticipantsAreSkipped()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);
        session.IsGroupChat = true;
        await env.Service.ExtractIfNeededAsync(session, force: true);
        Assert.Equal(0, env.Provider.Calls);
    }

    [Fact]
    public async Task ManualEditingAndLockingPersistWithoutChangingOwnershipOrCreationTime()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        var memory = await env.Service.AddManualAsync(env.CharacterId, "旧的约定", isLocked: true);
        var edited = await env.Service.UpdateManualAsync(env.CharacterId, memory.Id, "  新的约定  ");

        Assert.True(edited.IsLocked);
        Assert.Equal("新的约定", edited.Content);
        Assert.Equal(memory.CreatedAt, edited.CreatedAt);
        Assert.Equal(env.CharacterId, edited.CharacterId);
        await env.Service.SetLockedAsync(env.CharacterId, memory.Id, false);
        Assert.False((await env.Memories.GetAsync(env.CharacterId, memory.Id))!.IsLocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.UpdateManualAsync(env.CharacterId + 1, memory.Id, "错误的角色"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.SetLockedAsync(env.CharacterId + 1, memory.Id, true));
        await env.Service.AddManualAsync(env.CharacterId, "另一条记忆");
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.UpdateManualAsync(env.CharacterId, memory.Id, "另一条记忆"));
    }

    [Fact]
    public async Task InitializerMigratesOldMemoriesWithoutLosingTheirContent()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        var memory = await env.Service.AddManualAsync(env.CharacterId, "旧数据库中的记忆");
        await using (var connection = env.Factory.CreateConnection())
            await connection.ExecuteAsync("ALTER TABLE CharacterMemories DROP COLUMN IsLocked");

        await new DatabaseInitializer(env.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await new DatabaseInitializer(env.Factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

        var stored = Assert.Single(await env.Memories.GetByCharacterAsync(env.CharacterId));
        Assert.Equal(memory.Id, stored.Id);
        Assert.Equal(memory.Content, stored.Content);
        Assert.False(stored.IsLocked);
    }

    [Fact]
    public async Task AutomaticExtractionEvictsOnlyExistingUnlockedFactsAtCapacity()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        for (var index = 0; index < CharacterMemoryService.MaxMemoriesPerCharacter - 1; index++)
            await env.Service.AddManualAsync(env.CharacterId, $"重要事实 {index}", isLocked: true);
        var old = await env.Service.AddManualAsync(env.CharacterId, "不再重要的旧事实");
        env.Provider.Response = "{\"memories\":[\"新的重要事件\",\"第二件新事件\"]}";
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);

        await env.Service.ExtractIfNeededAsync(session);

        var stored = await env.Service.GetAsync(env.CharacterId);
        Assert.Equal(CharacterMemoryService.MaxMemoriesPerCharacter, stored.Count);
        Assert.Equal(CharacterMemoryService.MaxMemoriesPerCharacter - 1, stored.Count(x => x.IsLocked));
        Assert.DoesNotContain(stored, x => x.Id == old.Id);
        Assert.Contains(stored, x => x.Content == "新的重要事件");
        Assert.DoesNotContain(stored, x => x.Content == "第二件新事件");
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.AddManualAsync(env.CharacterId, "手动添加超过容量"));
    }

    [Fact]
    public async Task AutomaticExtractionLeavesAllLockedFactsUntouchedAtCapacity()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        for (var index = 0; index < CharacterMemoryService.MaxMemoriesPerCharacter; index++)
            await env.Service.AddManualAsync(env.CharacterId, $"锁定事实 {index}", isLocked: true);
        env.Provider.Response = "{\"memories\":[\"新的事件\"]}";
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);

        await env.Service.ExtractIfNeededAsync(session);

        Assert.Equal(0, env.Provider.Calls);
        Assert.Equal(CharacterMemoryService.MaxMemoriesPerCharacter, (await env.Service.GetAsync(env.CharacterId)).Count);
        Assert.Equal(CharacterMemoryService.ExtractionInterval, (await env.Sessions.GetAsync(session.Id))!.MemoryCoveredCount);
    }

    [Fact]
    public async Task ConcurrentManualSavesShareTheLastAvailableSlotAndPreserveLocks()
    {
        using var env = await Env.CreateAsync(enableMemory: false);
        var originalIds = new List<long>();
        for (var index = 0; index < CharacterMemoryService.MaxMemoriesPerCharacter - 1; index++)
            originalIds.Add((await env.Service.AddManualAsync(env.CharacterId, $"已锁定事实 {index}", isLocked: true)).Id);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await env.Service.AddManualAsync(env.CharacterId, $"同时保存的事实 {index}", isLocked: true);
                return true;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains($"最多保存 {CharacterMemoryService.MaxMemoriesPerCharacter} 条记忆", exception.Message);
                return false;
            }
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(saves);

        Assert.Single(results, x => x);
        var stored = await env.Service.GetAsync(env.CharacterId);
        Assert.Equal(CharacterMemoryService.MaxMemoriesPerCharacter, stored.Count);
        Assert.All(stored, memory => Assert.True(memory.IsLocked));
        Assert.All(originalIds, id => Assert.Contains(stored, memory => memory.Id == id));
        Assert.Single(stored, memory => memory.Content.StartsWith("同时保存的事实", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FactsEditedDuringTheModelRequestAreProtectedFromEviction()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        for (var index = 0; index < CharacterMemoryService.MaxMemoriesPerCharacter - 1; index++)
            await env.Service.AddManualAsync(env.CharacterId, $"锁定事实 {index}", isLocked: true);
        var old = await env.Service.AddManualAsync(env.CharacterId, "即将被手动编辑");
        env.Provider.BeforeResponse = () => env.Service.UpdateManualAsync(env.CharacterId, old.Id, "用户刚刚保存的事实");
        env.Provider.Response = "{\"memories\":[\"自动提炼的新事件\"]}";
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);

        await env.Service.ExtractIfNeededAsync(session);

        var stored = await env.Service.GetAsync(env.CharacterId);
        Assert.Contains(stored, x => x.Id == old.Id && x.Content == "用户刚刚保存的事实");
        Assert.DoesNotContain(stored, x => x.Content == "自动提炼的新事件");
    }

    [Fact]
    public async Task GroupExtractionStoresEachParticipatingCharactersOwnMemories()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var other = await env.CreateCharacterAsync("琳");
        var silent = await env.CreateCharacterAsync("未发言的角色");
        var session = await env.CreateGroupSessionAsync([env.CharacterId, other.Id, silent.Id], [env.CharacterId, other.Id]);
        env.Provider.Responder = request => request.Messages.Last().Content!.StartsWith($"目标角色：苔（角色 ID {env.CharacterId}）")
            ? "{\"memories\":[\"苔与用户约好去森林\"]}" : "{\"memories\":[\"琳向用户借了一本书\"]}";

        await env.Service.ExtractIfNeededAsync(session);

        Assert.Equal(2, env.Provider.Calls);
        Assert.Equal("苔与用户约好去森林", Assert.Single(await env.Service.GetAsync(env.CharacterId)).Content);
        Assert.Equal("琳向用户借了一本书", Assert.Single(await env.Service.GetAsync(other.Id)).Content);
        Assert.Empty(await env.Service.GetAsync(silent.Id));
        Assert.All(env.Provider.Requests, request =>
        {
            Assert.Contains("本次唯一目标角色", request.Messages[0].Content);
            Assert.Contains("不要把其他角色的经历", request.Messages[0].Content);
            Assert.Contains($"苔（角色 ID {env.CharacterId}）", request.Messages.Last().Content);
            Assert.Contains($"琳（角色 ID {other.Id}）", request.Messages.Last().Content);
        });
    }

    [Fact]
    public async Task GroupPromptIncludesOnlyTheCurrentSpeakersPrivateMemoriesAndPrioritizesLockedFacts()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var other = await env.CreateCharacterAsync("琳");
        var session = await env.CreateGroupSessionAsync([env.CharacterId, other.Id], [env.CharacterId]);
        await env.Service.AddManualAsync(env.CharacterId, "苔的普通事实");
        await env.Service.AddManualAsync(env.CharacterId, "苔的锁定事实", isLocked: true);
        await env.Service.AddManualAsync(other.Id, "琳的私密事实", isLocked: true);
        var promptService = new PromptService(new PromptRepository(env.Factory), env.Characters, env.Sessions,
            new KnowledgeService(new KnowledgeRepository(env.Factory), NullLogger<KnowledgeService>.Instance), env.Memories);
        var settings = new ProviderSettings { CharacterMemoryEnabled = true, IncludeCharacterContext = false, IncludeKnowledgeContext = false };

        var prompt = await promptService.BuildAsync(session, [], settings, env.CharacterId);
        var text = string.Join("\n", prompt.Messages.Select(x => x.Content));

        Assert.Contains("苔的普通事实", text);
        Assert.Contains("苔的锁定事实", text);
        Assert.DoesNotContain("琳的私密事实", text);
        Assert.True(text.IndexOf("苔的锁定事实", StringComparison.Ordinal) < text.IndexOf("苔的普通事实", StringComparison.Ordinal));
        var unattributedPrompt = await promptService.BuildAsync(session, [], settings);
        Assert.DoesNotContain("Character Memory", string.Join("\n", unattributedPrompt.Messages.Select(x => x.Content)));
    }

    [Fact]
    public async Task OverlappingExtractionsReadTheCommittedWatermarkBeforeRequestingAgain()
    {
        using var env = await Env.CreateAsync(enableMemory: true);
        var session = await env.CreateSessionWithMessagesAsync(CharacterMemoryService.ExtractionInterval);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Provider.BeforeResponse = async () => { entered.TrySetResult(); await release.Task; };
        env.Provider.Response = "{\"memories\":[\"唯一的事实\"]}";
        var first = env.Service.ExtractIfNeededAsync(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = env.Service.ExtractIfNeededAsync(session);

        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, env.Provider.Calls);
        Assert.Single(await env.Service.GetAsync(env.CharacterId));
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
        public required DatabaseConnectionFactory Factory { get; init; }
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
                Characters = characters, Provider = provider, Factory = factory,
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

        public async Task<Character> CreateCharacterAsync(string name)
        {
            var character = new Character { Name = name, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            await Characters.CreateAsync(character);
            return character;
        }

        public async Task<ChatSession> CreateGroupSessionAsync(IReadOnlyList<long> members, IReadOnlyList<long> speakers)
        {
            var session = new ChatSession { Title = "群聊", IsGroupChat = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            await Sessions.CreateAsync(session);
            await Sessions.SetCharactersAsync(session.Id, members.ToArray());
            for (var index = 0; index < CharacterMemoryService.ExtractionInterval; index++)
                await Messages.AddAsync(new ChatMessage
                {
                    ChatSessionId = session.Id, Role = index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    SpeakerCharacterId = index % 2 == 0 ? null : speakers[(index / 2) % speakers.Count],
                    Content = "群聊中发生的长期事件 " + index, CreatedAt = DateTimeOffset.UtcNow
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
        public List<ChatCompletionRequest> Requests { get; } = [];
        public Func<ChatCompletionRequest, string>? Responder { get; set; }
        public Func<Task>? BeforeResponse { get; set; }
        public string Id => "stub";
        public string DisplayName => "Stub";
        public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public async IAsyncEnumerable<string> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            if (BeforeResponse is not null) await BeforeResponse();
            await Task.Yield();
            yield return Responder?.Invoke(request) ?? Response;
        }
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
