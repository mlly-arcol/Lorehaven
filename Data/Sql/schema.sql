PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS Characters (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL,
    Personality TEXT NOT NULL,
    Scenario TEXT NOT NULL,
    FirstMessage TEXT NOT NULL,
    ExampleMessages TEXT NOT NULL,
    Creator TEXT NOT NULL,
    Tags TEXT NOT NULL,
    GroupName TEXT NOT NULL DEFAULT '',
    Aliases TEXT NOT NULL DEFAULT '',
    IsFavorite INTEGER NOT NULL DEFAULT 0,
    AvatarPath TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Characters_Name ON Characters(Name);
CREATE TABLE IF NOT EXISTS CharacterGroups (
    Name TEXT PRIMARY KEY COLLATE NOCASE,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ChatSessions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT NOT NULL,
    CharacterId INTEGER NULL,
    IsGroupChat INTEGER NOT NULL DEFAULT 0,
    ParentSessionId INTEGER NULL,
    BranchedFromMessageId INTEGER NULL,
    PersonaId INTEGER NULL,
    LorebookId INTEGER NULL,
    PromptPresetId INTEGER NULL,
    AuthorNote TEXT NOT NULL DEFAULT '',
    Summary TEXT NOT NULL DEFAULT '',
    SummaryCoveredCount INTEGER NOT NULL DEFAULT 0,
    SummaryIsManual INTEGER NOT NULL DEFAULT 0,
    MemoryCoveredCount INTEGER NOT NULL DEFAULT 0,
    GroupName TEXT NOT NULL DEFAULT '',
    IsPinned INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (ParentSessionId) REFERENCES ChatSessions(Id) ON DELETE SET NULL,
    FOREIGN KEY (BranchedFromMessageId) REFERENCES ChatMessages(Id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS ChatSessionCharacters (
    ChatSessionId INTEGER NOT NULL,
    CharacterId INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(ChatSessionId, CharacterId),
    FOREIGN KEY (ChatSessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE,
    FOREIGN KEY (CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS ChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ChatSessionId INTEGER NOT NULL,
    Role TEXT NOT NULL,
    SpeakerCharacterId INTEGER NULL,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NULL,
    CurrentSwipeIndex INTEGER NOT NULL DEFAULT 0,
    IsPinned INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (ChatSessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ChatMessages_ChatSessionId ON ChatMessages(ChatSessionId);
CREATE TABLE IF NOT EXISTS MessageSwipes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ChatMessageId INTEGER NOT NULL,
    SwipeIndex INTEGER NOT NULL,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (ChatMessageId) REFERENCES ChatMessages(Id) ON DELETE CASCADE,
    UNIQUE(ChatMessageId, SwipeIndex)
);
CREATE INDEX IF NOT EXISTS IX_MessageSwipes_ChatMessageId ON MessageSwipes(ChatMessageId);
CREATE INDEX IF NOT EXISTS IX_ChatSessions_CharacterId ON ChatSessions(CharacterId);
CREATE TABLE IF NOT EXISTS Personas (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS Lorebooks (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS LoreEntries (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    LorebookId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Keywords TEXT NOT NULL,
    SecondaryKeywords TEXT NOT NULL,
    Content TEXT NOT NULL,
    Priority INTEGER NOT NULL DEFAULT 100,
    Depth INTEGER NOT NULL DEFAULT 4,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    IsConstant INTEGER NOT NULL DEFAULT 0,
    IsSelective INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (LorebookId) REFERENCES Lorebooks(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_LoreEntries_LorebookId ON LoreEntries(LorebookId);
CREATE TABLE IF NOT EXISTS PromptPresets (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    SystemPrompt TEXT NOT NULL,
    MainPrompt TEXT NOT NULL,
    Model TEXT NOT NULL,
    Temperature REAL NULL,
    TopP REAL NULL,
    MaxTokens INTEGER NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS KnowledgeDocuments (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    SourcePath TEXT NOT NULL,
    ManagedPath TEXT NOT NULL,
    MimeType TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    ChunkCount INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS KnowledgeChunks (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DocumentId INTEGER NOT NULL,
    ChunkIndex INTEGER NOT NULL,
    Content TEXT NOT NULL,
    FOREIGN KEY (DocumentId) REFERENCES KnowledgeDocuments(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_DocumentId ON KnowledgeChunks(DocumentId);
CREATE TABLE IF NOT EXISTS ChatAttachments (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ChatMessageId INTEGER NOT NULL,
    FileName TEXT NOT NULL,
    FilePath TEXT NOT NULL,
    MimeType TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    FOREIGN KEY (ChatMessageId) REFERENCES ChatMessages(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ChatAttachments_ChatMessageId ON ChatAttachments(ChatMessageId);
CREATE TABLE IF NOT EXISTS CharacterStatuses (
    ChatSessionId INTEGER NOT NULL,
    CharacterId INTEGER NOT NULL,
    CharacterName TEXT NOT NULL,
    Summary TEXT NOT NULL,
    AttributesJson TEXT NOT NULL,
    SourceMessageId INTEGER NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(ChatSessionId, CharacterId),
    FOREIGN KEY (ChatSessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE,
    FOREIGN KEY (CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE,
    FOREIGN KEY (SourceMessageId) REFERENCES ChatMessages(Id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS IX_CharacterStatuses_CharacterId ON CharacterStatuses(CharacterId);
-- Long-term facts a character remembers across conversations, distilled from the chat history.
CREATE TABLE IF NOT EXISTS CharacterMemories (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CharacterId INTEGER NOT NULL,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CharacterMemories_CharacterId ON CharacterMemories(CharacterId);
CREATE TABLE IF NOT EXISTS RegexScripts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Pattern TEXT NOT NULL,
    Replacement TEXT NOT NULL,
    Target TEXT NOT NULL DEFAULT 'AssistantOutput',
    Mode TEXT NOT NULL DEFAULT 'Regex',
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_RegexScripts_Target ON RegexScripts(Target, IsEnabled, SortOrder);
CREATE INDEX IF NOT EXISTS IX_ChatSessions_UpdatedAt ON ChatSessions(UpdatedAt DESC);
-- Text typed but never sent, one row per conversation, dropped together with the conversation.
CREATE TABLE IF NOT EXISTS ComposerDrafts (
    ChatSessionId INTEGER PRIMARY KEY,
    DraftText TEXT NOT NULL,
    DraftImages TEXT NOT NULL DEFAULT '[]',
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (ChatSessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE
);
