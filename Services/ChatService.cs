using Microsoft.Extensions.Logging;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Providers;

namespace NativeTavern.Services;

public sealed class ChatService(
    ChatSessionRepository sessionRepository,
    ChatMessageRepository messageRepository,
    MessageSwipeRepository swipeRepository,
    ChatAttachmentRepository attachmentRepository,
    AttachmentService attachmentService,
    ConversationSummaryService summaryService,
    CharacterMemoryService memoryService,
    CharacterRepository characterRepository,
    PromptService promptService,
    RegexScriptService regexScripts,
    SettingsService settingsService,
    ILLMProvider provider,
    ILogger<ChatService> logger)
{
    public async Task<(ChatSession Session, IReadOnlyList<ChatMessage> Messages)> LoadLatestAsync()
    {
        var sessions = await sessionRepository.GetAllAsync();
        var session = sessions.FirstOrDefault() ?? await CreateSessionAsync();
        return (session, await messageRepository.GetBySessionAsync(session.Id));
    }

    public Task<IReadOnlyList<ChatSession>> GetSessionsAsync() => sessionRepository.GetAllAsync();

    /// <summary>Case-insensitive substring search across every conversation, newest match first.</summary>
    public Task<IReadOnlyList<MessageSearchResult>> SearchMessagesAsync(string query, int limit = 50) =>
        messageRepository.SearchAsync(query, limit);

    public async Task<ChatSession> GetOrCreateEmptySessionAsync() =>
        await sessionRepository.FindEmptyOrdinaryAsync() ?? await CreateSessionAsync();

    public Task<ChatSession?> GetSessionAsync(long id) => sessionRepository.GetAsync(id);

    public async Task<IReadOnlyList<Character>> GetGroupMembersAsync(long sessionId) =>
        await characterRepository.GetByIdsAsync(await sessionRepository.GetCharacterIdsAsync(sessionId));

    public Task<IReadOnlyList<Character>> GetAllCharactersAsync() =>
        characterRepository.SearchAsync(string.Empty, false);

    public async Task UpdateSessionTitleAsync(ChatSession session, string title)
    {
        title = title.Trim();
        if (title.Length == 0) throw new InvalidOperationException("对话标题不能为空。");
        if (title.Length > 120) throw new InvalidOperationException("对话标题不能超过 120 个字符。");
        session.Title = title;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessionRepository.UpdateAsync(session);
    }

    /// <summary>The recap goes into every later request, so a hand written one is capped by the service.</summary>
    public Task SaveStorySummaryAsync(ChatSession session, string summary) => summaryService.SaveManualAsync(session, summary);
    public Task RebuildStorySummaryAsync(ChatSession session) => summaryService.RebuildAsync(session);

    public Task ClearStorySummaryAsync(ChatSession session) => summaryService.ClearAsync(session);

    /// <summary>Only the flag moves, so pinning keeps the conversation's own ordering stamp.</summary>
    public async Task SetSessionPinnedAsync(ChatSession session, bool pinned)
    {
        session.IsPinned = pinned;
        await sessionRepository.SetPinnedAsync(session.Id, pinned);
    }

    public async Task SetSessionGroupAsync(ChatSession session, string groupName)
    {
        groupName = groupName.Trim();
        if (groupName.Length > 40) throw new InvalidOperationException("对话分组名称不能超过 40 个字符。");
        session.GroupName = groupName;
        await sessionRepository.SetGroupAsync(session.Id, groupName);
    }

    public Task<IReadOnlyList<string>> GetSessionGroupsAsync() => sessionRepository.GetGroupNamesAsync();

    public async Task<ChatSession> BranchSessionAsync(ChatSession source, long throughMessageId)
    {
        var history = (await messageRepository.GetBySessionAsync(source.Id)).ToList();
        var branchPoint = history.FindIndex(x => x.Id == throughMessageId);
        if (branchPoint < 0) throw new InvalidOperationException("分支起点不属于当前对话。");

        var now = DateTimeOffset.UtcNow;
        var branch = new ChatSession
        {
            Title = CreateBranchTitle(source.Title),
            CharacterId = source.CharacterId,
            IsGroupChat = source.IsGroupChat,
            ParentSessionId = source.Id,
            BranchedFromMessageId = throughMessageId,
            PersonaId = source.PersonaId,
            LorebookId = source.LorebookId,
            PromptPresetId = source.PromptPresetId,
            AuthorNote = source.AuthorNote,
            CreatedAt = now,
            UpdatedAt = now
        };
        await sessionRepository.CreateAsync(branch);
        try
        {
            if (source.IsGroupChat)
                await sessionRepository.SetCharactersAsync(
                    branch.Id, await sessionRepository.GetCharacterIdsAsync(source.Id));

            foreach (var original in history.Take(branchPoint + 1))
            {
                var clone = new ChatMessage
                {
                    ChatSessionId = branch.Id,
                    Role = original.Role,
                    SpeakerCharacterId = original.SpeakerCharacterId,
                    Content = original.Content,
                    CreatedAt = original.CreatedAt,
                    UpdatedAt = original.UpdatedAt,
                    CurrentSwipeIndex = original.CurrentSwipeIndex
                };
                await messageRepository.AddAsync(clone);
                foreach (var swipe in await swipeRepository.GetByMessageAsync(original.Id))
                    await swipeRepository.AddAsync(new MessageSwipe
                    {
                        ChatMessageId = clone.Id,
                        SwipeIndex = swipe.SwipeIndex,
                        Content = swipe.Content,
                        CreatedAt = swipe.CreatedAt
                    });
                await attachmentService.CloneAsync(
                    clone.Id, await attachmentRepository.GetByMessageAsync(original.Id));
            }
            return branch;
        }
        catch
        {
            var attachments = await attachmentRepository.GetBySessionAsync(branch.Id);
            await sessionRepository.DeleteAsync(branch.Id);
            attachmentService.DeleteManagedFiles(attachments);
            throw;
        }
    }

    public async Task<ChatSession> CreateGroupSessionAsync(string groupName)
    {
        var members = await characterRepository.GetByGroupAsync(groupName);
        if (members.Count < 2) throw new InvalidOperationException("群聊至少需要两个角色。");
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession
        {
            Title = groupName.Trim(),
            IsGroupChat = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        await sessionRepository.CreateAsync(session);
        await sessionRepository.SetCharactersAsync(session.Id, members.Select(x => x.Id).ToList());
        return session;
    }

    public async Task UpdateGroupSessionAsync(
        ChatSession session,
        string title,
        IReadOnlyCollection<long> characterIds)
    {
        var ids = characterIds.Distinct().ToList();
        if (ids.Count < 2) throw new InvalidOperationException("群聊至少需要两个角色。");
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("群聊名称不能为空。");
        session.Title = title.Trim();
        session.IsGroupChat = true;
        session.CharacterId = null;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessionRepository.SetCharactersAsync(session.Id, ids);
        await sessionRepository.UpdateAsync(session);
    }

    public async Task<ChatSession> CreateSessionAsync(Character? character = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession
        {
            Title = character?.Name ?? "New Chat", CharacterId = character?.Id,
            CreatedAt = now, UpdatedAt = now
        };
        await sessionRepository.CreateAsync(session);
        if (character is not null && !string.IsNullOrWhiteSpace(character.FirstMessage))
        {
            var greeting = new ChatMessage
            {
                ChatSessionId = session.Id, Role = ChatRole.Assistant,
                Content = RenderCharacterText(character.FirstMessage, character.Name), CreatedAt = now
            };
            await messageRepository.AddAsync(greeting);
            await swipeRepository.AddAsync(new MessageSwipe
            {
                ChatMessageId = greeting.Id, SwipeIndex = 0,
                Content = greeting.Content, CreatedAt = now
            });
        }
        return session;
    }

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(long sessionId) =>
        messageRepository.GetBySessionAsync(sessionId);

    /// Reads a conversation the way the chat page needs it: messages, swipe counts and attachments in
    /// four statements, so a long history does not turn opening it into hundreds of queries.
    public async Task<SessionBundle> GetSessionBundleAsync(long sessionId)
    {
        await swipeRepository.BackfillFirstSwipesAsync(sessionId);
        var messages = await messageRepository.GetBySessionAsync(sessionId);
        var swipeCounts = await swipeRepository.GetCountsBySessionAsync(sessionId);
        var attachments = (await attachmentRepository.GetBySessionAsync(sessionId))
            .GroupBy(x => x.ChatMessageId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ChatAttachment>)x.ToList());
        return new SessionBundle(messages, swipeCounts, attachments);
    }

    public Task<Character?> GetCharacterAsync(long characterId) => characterRepository.GetAsync(characterId);

    public Task<IReadOnlyList<ChatAttachment>> GetAttachmentsAsync(long messageId) => attachmentRepository.GetByMessageAsync(messageId);

    public async Task<PromptBuildResult> GetPromptPreviewAsync(ChatSession session)
    {
        var settings = await LoadConfiguredSettingsAsync();
        return await promptService.BuildAsync(session, await messageRepository.GetBySessionAsync(session.Id), settings);
    }

    public async Task<int> GetSwipeCountAsync(ChatMessage message) =>
        message.Role == ChatRole.Assistant ? (await EnsureSwipeHistoryAsync(message)).Count : 0;

    public async Task DeleteSessionAsync(long id)
    {
        var attachments = await attachmentRepository.GetBySessionAsync(id);
        await sessionRepository.DeleteAsync(id);
        attachmentService.DeleteManagedFiles(attachments);
    }

    public async Task DeleteMessageAsync(long id)
    {
        var attachments = await attachmentRepository.GetByMessageAsync(id);
        await messageRepository.DeleteAsync(id);
        attachmentService.DeleteManagedFiles(attachments);
    }

    public async Task UpdateSessionPromptAsync(
        ChatSession session, long? personaId, long? lorebookId, long? presetId, string authorNote)
    {
        session.PersonaId = personaId;
        session.LorebookId = lorebookId;
        session.PromptPresetId = presetId;
        session.AuthorNote = authorNote;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await sessionRepository.UpdateAsync(session);
    }

    public async Task UpdateMessageAsync(ChatMessage message)
    {
        message.Content = message.Content.Trim();
        message.UpdatedAt = DateTimeOffset.UtcNow;
        if (message.Role == ChatRole.Assistant)
        {
            message.CurrentSwipeIndex = 0;
            await swipeRepository.ReplaceWithSingleAsync(message.Id, message.Content);
        }
        await messageRepository.UpdateAsync(message);
    }

    // Only the flag moves, so the content, swipes and UpdatedAt stamp stay untouched.
    public async Task SetMessagePinnedAsync(ChatMessage message, bool pinned)
    {
        message.IsPinned = pinned;
        await messageRepository.SetPinnedAsync(message.Id, pinned);
    }

    public async Task<(ChatMessage Message, int SwipeCount)> SelectSwipeAsync(ChatMessage message, int targetIndex)
    {
        var swipes = await EnsureSwipeHistoryAsync(message);
        if (targetIndex < 0 || targetIndex >= swipes.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        message.CurrentSwipeIndex = targetIndex;
        message.Content = swipes[targetIndex].Content;
        message.UpdatedAt = DateTimeOffset.UtcNow;
        await messageRepository.UpdateAsync(message);
        return (message, swipes.Count);
    }

    public async Task<ChatMessage> SendAsync(
        ChatSession session, string input, IReadOnlyList<string> imagePaths,
        Func<ChatMessage, ChatMessage, Task> onStarted,
        Func<ChatMessage, string, Task> onChunk,
        CancellationToken cancellationToken)
    {
        var settings = await LoadConfiguredSettingsAsync();
        var now = DateTimeOffset.UtcNow;
        var user = new ChatMessage
        {
            ChatSessionId = session.Id, Role = ChatRole.User,
            Content = await regexScripts.ApplyAsync(input.Trim(), RegexScriptTarget.UserInput), CreatedAt = now
        };
        await messageRepository.AddAsync(user);
        if (imagePaths.Count > 0) await attachmentService.SaveImagesAsync(user.Id, imagePaths);
        var history = await messageRepository.GetBySessionAsync(session.Id);
        var speaker = await SelectSpeakerAsync(session, history);
        var assistant = new ChatMessage
        {
            ChatSessionId = session.Id, Role = ChatRole.Assistant,
            SpeakerCharacterId = speaker?.Id,
            Content = string.Empty, CreatedAt = DateTimeOffset.UtcNow
        };
        await messageRepository.AddAsync(assistant);

        // Rename before onStarted so the chat header immediately shows the new title;
        // an image-only first message has no text and must not blank the title.
        if (history.Count == 1 && !session.IsGroupChat && !string.IsNullOrWhiteSpace(user.Content))
        {
            session.Title = user.Content.Length > 32 ? user.Content[..32] + "…" : user.Content;
            session.UpdatedAt = now;
            await sessionRepository.UpdateAsync(session);
        }

        await onStarted(user, assistant);

        var request = await CreateRequestAsync(session, history, settings, assistant.SpeakerCharacterId);
        var presentation = new ProgressiveParagraphBuffer();
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                assistant.Content += chunk;
                foreach (var paragraph in presentation.Append(chunk))
                {
                    await onChunk(assistant, await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput));
                    await Task.Delay(35);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Generation cancelled by user.");
        }
        finally
        {
            foreach (var paragraph in presentation.Flush())
                await onChunk(assistant, await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput));
            assistant.UpdatedAt = DateTimeOffset.UtcNow;
            assistant.Content = await regexScripts.ApplyAsync(assistant.Content, RegexScriptTarget.AssistantOutput);
            if (string.IsNullOrEmpty(assistant.Content))
            {
                await messageRepository.DeleteAsync(assistant.Id);
            }
            else
            {
                await messageRepository.UpdateAsync(assistant);
                await swipeRepository.AddAsync(new MessageSwipe
                {
                    ChatMessageId = assistant.Id, SwipeIndex = 0,
                    Content = assistant.Content, CreatedAt = assistant.UpdatedAt.Value
                });
            }
            session.UpdatedAt = assistant.UpdatedAt.Value;
            await sessionRepository.UpdateAsync(session);
            try { await summaryService.UpdateIfNeededAsync(session); }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic summary update failed."); }
            // Memory extraction costs an extra model call, so it never blocks the send pipeline.
            _ = memoryService.ExtractIfNeededAsync(session);
        }
        return assistant;
    }

    public async Task<ChatMessage> GenerateNextSpeakerAsync(
        ChatSession session,
        long? requestedSpeakerId,
        Func<ChatMessage, Task> onStarted,
        Func<ChatMessage, string, Task> onChunk,
        CancellationToken cancellationToken)
    {
        if (!session.IsGroupChat) throw new InvalidOperationException("当前会话不是群聊。");
        var settings = await LoadConfiguredSettingsAsync();
        var history = await messageRepository.GetBySessionAsync(session.Id);
        Character? speaker;
        if (requestedSpeakerId is long characterId)
        {
            speaker = (await GetGroupMembersAsync(session.Id)).FirstOrDefault(x => x.Id == characterId)
                      ?? throw new InvalidOperationException("所选角色不属于当前群聊。");
        }
        else
        {
            speaker = await SelectSpeakerAsync(session, history)
                      ?? throw new InvalidOperationException("群聊没有可用角色。");
        }
        var assistant = new ChatMessage
        {
            ChatSessionId = session.Id,
            Role = ChatRole.Assistant,
            SpeakerCharacterId = speaker.Id,
            Content = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await messageRepository.AddAsync(assistant);
        await onStarted(assistant);
        var request = await CreateRequestAsync(session, history, settings, speaker.Id);
        var presentation = new ProgressiveParagraphBuffer();
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                assistant.Content += chunk;
                foreach (var paragraph in presentation.Append(chunk))
                {
                    await onChunk(assistant, await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput));
                    await Task.Delay(35);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Group chat generation cancelled by user.");
        }
        finally
        {
            foreach (var paragraph in presentation.Flush())
                await onChunk(assistant, await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput));
            assistant.UpdatedAt = DateTimeOffset.UtcNow;
            assistant.Content = await regexScripts.ApplyAsync(assistant.Content, RegexScriptTarget.AssistantOutput);
            if (string.IsNullOrEmpty(assistant.Content))
            {
                await messageRepository.DeleteAsync(assistant.Id);
            }
            else
            {
                await messageRepository.UpdateAsync(assistant);
                await swipeRepository.AddAsync(new MessageSwipe
                {
                    ChatMessageId = assistant.Id,
                    SwipeIndex = 0,
                    Content = assistant.Content,
                    CreatedAt = assistant.UpdatedAt.Value
                });
            }
            session.UpdatedAt = assistant.UpdatedAt.Value;
            await sessionRepository.UpdateAsync(session);
        }
        return assistant;
    }

    public async Task<(ChatMessage Message, int SwipeCount)> GenerateAlternativeAsync(
        ChatSession session, ChatMessage target,
        Func<string, Task> onContentChanged,
        CancellationToken cancellationToken)
    {
        if (target.Role != ChatRole.Assistant)
            throw new InvalidOperationException("只能为助手消息生成候选回复。");

        var settings = await LoadConfiguredSettingsAsync();
        var allMessages = (await messageRepository.GetBySessionAsync(session.Id)).ToList();
        var targetPosition = allMessages.FindIndex(x => x.Id == target.Id);
        if (targetPosition < 0) throw new InvalidOperationException("消息不属于当前聊天。");

        var swipes = await EnsureSwipeHistoryAsync(target);
        var originalContent = target.Content;
        var newContent = string.Empty;
        var displayedContent = string.Empty;
        var presentation = new ProgressiveParagraphBuffer();
        var request = await CreateRequestAsync(
            session, allMessages.Take(targetPosition), settings, target.SpeakerCharacterId);
        await onContentChanged(string.Empty);
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                newContent += chunk;
                foreach (var paragraph in presentation.Append(chunk))
                {
                    displayedContent += await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput);
                    await onContentChanged(displayedContent);
                    await Task.Delay(35);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Alternative generation cancelled by user.");
        }
        finally
        {
            foreach (var paragraph in presentation.Flush())
            {
                displayedContent += await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput);
                await onContentChanged(displayedContent);
            }
            newContent = await regexScripts.ApplyAsync(newContent, RegexScriptTarget.AssistantOutput);
            if (!string.IsNullOrEmpty(newContent))
            {
                var index = swipes.Count;
                await swipeRepository.AddAsync(new MessageSwipe
                {
                    ChatMessageId = target.Id, SwipeIndex = index,
                    Content = newContent, CreatedAt = DateTimeOffset.UtcNow
                });
                target.Content = newContent;
                target.CurrentSwipeIndex = index;
                target.UpdatedAt = DateTimeOffset.UtcNow;
                await messageRepository.UpdateAsync(target);
                session.UpdatedAt = target.UpdatedAt.Value;
                await sessionRepository.UpdateAsync(session);
            }
            else
            {
                target.Content = originalContent;
                await onContentChanged(originalContent);
            }
        }
        return (target, swipes.Count + (string.IsNullOrEmpty(newContent) ? 0 : 1));
    }

    /// Asks the model to carry on from where the last reply stopped. The text already written stays
    /// where it is, so this grows the message instead of replacing it, and the shorter version is kept
    /// as an earlier swipe.
    public async Task<(ChatMessage Message, int SwipeCount)> ContinueAssistantAsync(
        ChatSession session, ChatMessage target,
        Func<string, Task> onContentChanged,
        CancellationToken cancellationToken)
    {
        if (target.Role != ChatRole.Assistant)
            throw new InvalidOperationException("只能续写助手的回复。");
        if (string.IsNullOrWhiteSpace(target.Content))
            throw new InvalidOperationException("这条回复还没有内容，请先重新生成。");

        var settings = await LoadConfiguredSettingsAsync();
        var allMessages = (await messageRepository.GetBySessionAsync(session.Id)).ToList();
        var targetPosition = allMessages.FindIndex(x => x.Id == target.Id);
        if (targetPosition < 0) throw new InvalidOperationException("消息不属于当前聊天。");

        var swipes = await EnsureSwipeHistoryAsync(target);
        var baseContent = target.Content;
        var request = await CreateRequestAsync(
            session, allMessages.Take(targetPosition + 1), settings, target.SpeakerCharacterId);
        request = new ChatCompletionRequest
        {
            Model = request.Model,
            Messages = [.. request.Messages, new ChatCompletionMessage { Role = "user", Content = ContinuationCue }],
            Temperature = request.Temperature, TopP = request.TopP,
            MaxTokens = request.MaxTokens, Stream = true
        };

        var addition = string.Empty;
        var displayed = string.Empty;
        var grew = false;
        var presentation = new ProgressiveParagraphBuffer();
        await onContentChanged(baseContent);
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                addition += chunk;
                foreach (var paragraph in presentation.Append(chunk))
                {
                    displayed += await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput);
                    await onContentChanged(baseContent + displayed);
                    await Task.Delay(35);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Continuation cancelled by user.");
        }
        finally
        {
            foreach (var paragraph in presentation.Flush())
            {
                displayed += await regexScripts.ApplyAsync(paragraph, RegexScriptTarget.AssistantOutput);
                await onContentChanged(baseContent + displayed);
            }
            addition = await regexScripts.ApplyAsync(addition, RegexScriptTarget.AssistantOutput);
            if (!string.IsNullOrWhiteSpace(addition))
            {
                var combined = baseContent + addition;
                var index = swipes.Count;
                await swipeRepository.AddAsync(new MessageSwipe
                {
                    ChatMessageId = target.Id, SwipeIndex = index,
                    Content = combined, CreatedAt = DateTimeOffset.UtcNow
                });
                target.Content = combined;
                target.CurrentSwipeIndex = index;
                target.UpdatedAt = DateTimeOffset.UtcNow;
                await messageRepository.UpdateAsync(target);
                session.UpdatedAt = target.UpdatedAt.Value;
                await sessionRepository.UpdateAsync(session);
                grew = true;
            }
            else
            {
                target.Content = baseContent;
                await onContentChanged(baseContent);
            }
        }
        return (target, grew ? swipes.Count + 1 : swipes.Count);
    }

    private const string ContinuationCue =
        "请从上一条回复停下的地方接着往下写，不要重复已经写出的内容，也不要重新开头，只输出后续部分。";

    public static IReadOnlyList<ChatCompletionMessage> BuildMessages(
        IEnumerable<ChatMessage> messages,
        IReadOnlyDictionary<long, string>? speakerNames = null) =>
        messages.Select(message => new ChatCompletionMessage
        {
            Role = message.Role.ToString().ToLowerInvariant(),
            Content = message.Role == ChatRole.Assistant &&
                      message.SpeakerCharacterId is long speakerId &&
                      speakerNames?.TryGetValue(speakerId, out var speakerName) == true
                ? $"[{speakerName}]\n{message.Content}"
                : message.Content,
            SourceMessageId = message.Id
        }).ToList();

    private async Task<ProviderSettings> LoadConfiguredSettingsAsync()
    {
        var settings = await settingsService.LoadAsync();
        if (!settings.IsConfigured)
            throw new InvalidOperationException("尚未配置模型 Provider，请先前往 Settings。");
        return settings;
    }

    private async Task<ChatCompletionRequest> CreateRequestAsync(
        ChatSession session,
        IEnumerable<ChatMessage> history,
        ProviderSettings settings,
        long? speakingCharacterId = null)
    {
        var historyList = history.ToList();
        Character? characterContext = null;
        if (session.CharacterId is long characterId)
            characterContext = await characterRepository.GetAsync(characterId);

        IEnumerable<ChatMessage> requestHistory = historyList;
        if (characterContext is not null && !settings.IncludeCharacterContext &&
            historyList.FirstOrDefault() is { Role: ChatRole.Assistant } greeting &&
            greeting.Content == RenderCharacterText(characterContext.FirstMessage, characterContext.Name))
            requestHistory = historyList.Skip(1);

        var prompt = await promptService.BuildAsync(
            session, requestHistory, settings, speakingCharacterId);
        var messages = new List<ChatCompletionMessage>();
        messages.Add(new ChatCompletionMessage
        {
            Role = "system",
            Content = "请先在内部组织本次回答的整体框架，再开始作答。使用清晰、完整且不过长的自然段；不要展示内部思考过程。"
        });
        foreach (var message in prompt.Messages)
        {
            var images = settings.IncludeImageContext && message.SourceMessageId is long messageId
                ? await AttachmentService.ToDataUrlsAsync(await attachmentRepository.GetByMessageAsync(messageId))
                : [];
            messages.Add(new ChatCompletionMessage
            {
                Role = message.Role, Content = message.Content, SourceMessageId = message.SourceMessageId,
                ImageDataUrls = images
            });
        }
        return new ChatCompletionRequest
        {
            Model = prompt.Model, Messages = messages,
            Temperature = prompt.Temperature, TopP = prompt.TopP,
            MaxTokens = prompt.MaxTokens, Stream = true
        };
    }

    private async Task<Character?> SelectSpeakerAsync(
        ChatSession session,
        IReadOnlyList<ChatMessage> history)
    {
        if (!session.IsGroupChat)
            return session.CharacterId is long characterId
                ? await characterRepository.GetAsync(characterId)
                : null;

        var members = (await GetGroupMembersAsync(session.Id)).ToList();
        if (members.Count == 0) return null;
        var latestUserText = history.LastOrDefault(x => x.Role == ChatRole.User)?.Content ?? string.Empty;
        var lastSpeakerId = history.LastOrDefault(x => x.Role == ChatRole.Assistant)?.SpeakerCharacterId;
        var weighted = members.Select(member =>
        {
            var score = 1.0;
            if (latestUserText.Contains(member.Name, StringComparison.OrdinalIgnoreCase)) score += 12;
            var keywords = member.Tags.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            score += keywords.Count(x => x.Length >= 2 && latestUserText.Contains(x, StringComparison.OrdinalIgnoreCase)) * 2;
            if (lastSpeakerId == member.Id && members.Count > 1) score *= 0.25;
            return (Member: member, Score: score);
        }).ToList();
        var pick = Random.Shared.NextDouble() * weighted.Sum(x => x.Score);
        foreach (var candidate in weighted)
        {
            pick -= candidate.Score;
            if (pick <= 0) return candidate.Member;
        }
        return weighted[^1].Member;
    }

    private async Task<IReadOnlyList<MessageSwipe>> EnsureSwipeHistoryAsync(ChatMessage message)
    {
        var swipes = await swipeRepository.GetByMessageAsync(message.Id);
        if (swipes.Count > 0 || string.IsNullOrEmpty(message.Content)) return swipes;
        await swipeRepository.AddAsync(new MessageSwipe
        {
            ChatMessageId = message.Id, SwipeIndex = 0,
            Content = message.Content, CreatedAt = message.CreatedAt
        });
        return await swipeRepository.GetByMessageAsync(message.Id);
    }

    private static string RenderCharacterText(string text, string characterName) =>
        text.Replace("{{char}}", characterName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{user}}", "User", StringComparison.OrdinalIgnoreCase);

    private static string CreateBranchTitle(string title)
    {
        const string suffix = " · Branch";
        var trimmed = title.Trim();
        return (trimmed.Length > 120 - suffix.Length ? trimmed[..(120 - suffix.Length)] : trimmed) + suffix;
    }
}
