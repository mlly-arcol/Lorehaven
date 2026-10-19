using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ConversationDataAccessTests
{
    [Fact]
    public async Task RecentMessagesAreLimitedInSqlAndReturnedInConversationOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nt-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new DatabaseConnectionFactory(Path.Combine(directory, "data.db"));
            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            var sessions = new ChatSessionRepository(factory);
            var messages = new ChatMessageRepository(factory);
            var session = await CreateSessionAsync(sessions);
            var other = await CreateSessionAsync(sessions);

            for (var i = 0; i < 40; i++)
            {
                await AddMessageAsync(messages, session.Id, $"turn-{i:D2}");
                await AddMessageAsync(messages, other.Id, $"other-{i:D2}");
            }

            var recent = await messages.GetRecentBySessionAsync(session.Id, 16);

            Assert.Equal(Enumerable.Range(24, 16).Select(i => $"turn-{i:D2}"), recent.Select(x => x.Content));
            Assert.True(recent.Zip(recent.Skip(1)).All(pair => pair.First.Id < pair.Second.Id));
            Assert.Empty(await messages.GetRecentBySessionAsync(session.Id, 0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BatchedAttachmentReadMatchesIndividualReadsAcrossSqliteParameterBatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nt-attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new DatabaseConnectionFactory(Path.Combine(directory, "data.db"));
            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            var sessions = new ChatSessionRepository(factory);
            var messages = new ChatMessageRepository(factory);
            var attachments = new ChatAttachmentRepository(factory);
            var session = await CreateSessionAsync(sessions);
            var ids = new List<long>();
            for (var i = 0; i < 502; i++)
                ids.Add(await AddMessageAsync(messages, session.Id, $"turn-{i}"));
            await attachments.AddAsync(Attachment(ids[0], "first.png"));
            await attachments.AddAsync(Attachment(ids[0], "second.png"));
            await attachments.AddAsync(Attachment(ids[501], "last.png"));

            var batched = await attachments.GetByMessageIdsAsync(ids.Concat([ids[0]]));

            Assert.Equal(new[] { "first.png", "second.png", "last.png" }, batched.Select(x => x.FileName));
            Assert.Equal((await attachments.GetByMessageAsync(ids[0])).Select(x => x.Id),
                batched.Where(x => x.ChatMessageId == ids[0]).Select(x => x.Id));
            Assert.Empty(await attachments.GetByMessageIdsAsync([]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PromptKeepsEachImagesAttachedToItsOwnMessageInStoredOrder()
    {
        using var harness = new ChatViewModelHarness();
        var settings = new SettingsService(new SettingsRepository(harness.Factory), new NoopProtector());
        await settings.SaveAsync(new ProviderSettings
        {
            BaseUrl = "http://127.0.0.1:8080/v1", Model = "m",
            IncludeImageContext = true, TrimHistoryToContext = false
        }, null);
        var session = await harness.CreateSessionAsync("图片上下文");
        var messages = new ChatMessageRepository(harness.Factory);
        var attachments = new ChatAttachmentRepository(harness.Factory);
        var first = await AddMessageAsync(messages, session.Id, "第一张和第二张");
        var second = await AddMessageAsync(messages, session.Id, "第三张");
        Directory.CreateDirectory(AppPaths.AttachmentsDirectory);
        var files = new List<string>();
        try
        {
            foreach (var (messageId, name, bytes) in new[]
            {
                (first, "first.png", new byte[] { 1 }),
                (first, "second.png", new byte[] { 2 }),
                (second, "third.png", new byte[] { 3 })
            })
            {
                var path = Path.Combine(AppPaths.AttachmentsDirectory, Guid.NewGuid().ToString("N") + ".png");
                files.Add(path);
                await File.WriteAllBytesAsync(path, bytes);
                await attachments.AddAsync(new ChatAttachment
                {
                    ChatMessageId = messageId, FileName = name, FilePath = path,
                    MimeType = "image/png", SizeBytes = bytes.Length, CreatedAt = DateTimeOffset.UtcNow
                });
            }

            harness.Provider.Release();
            await harness.Service.SendAsync(session, "继续", [],
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, CancellationToken.None);

            var request = Assert.Single(harness.Provider.SnapshotRequests());
            var firstPayload = Assert.Single(request.Messages, x => x.SourceMessageId == first);
            var secondPayload = Assert.Single(request.Messages, x => x.SourceMessageId == second);
            Assert.Equal(new[] { "data:image/png;base64,AQ==", "data:image/png;base64,Ag==" }, firstPayload.ImageDataUrls);
            Assert.Equal(new[] { "data:image/png;base64,Aw==" }, secondPayload.ImageDataUrls);
            Assert.All(request.Messages.Where(x => x.SourceMessageId != first && x.SourceMessageId != second),
                x => Assert.Empty(x.ImageDataUrls));
        }
        finally
        {
            foreach (var file in files) File.Delete(file);
        }
    }

    private static async Task<ChatSession> CreateSessionAsync(ChatSessionRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession { Title = "test", CreatedAt = now, UpdatedAt = now };
        session.Id = await repository.CreateAsync(session);
        return session;
    }

    private static Task<long> AddMessageAsync(ChatMessageRepository repository, long sessionId, string content) =>
        repository.AddAsync(new ChatMessage
        {
            ChatSessionId = sessionId, Role = ChatRole.User, Content = content, CreatedAt = DateTimeOffset.UtcNow
        });

    private static ChatAttachment Attachment(long messageId, string fileName) => new()
    {
        ChatMessageId = messageId, FileName = fileName, FilePath = fileName,
        MimeType = "image/png", SizeBytes = 1, CreatedAt = DateTimeOffset.UtcNow
    };
}
