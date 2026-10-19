# Lorehaven 2.0 架构与维护边界

## 产品边界

Lorehaven 2.0 是面向个人使用的 Windows 原生、离线优先 AI 角色聊天工作区。核心功能在 2.0 冻结；后续版本优先处理缺陷、安全、兼容性、性能和小型体验改进。

以下能力不属于当前核心范围：

- 执行第三方插件代码
- 云同步和多账户服务
- Web、移动端和其他桌面平台客户端
- 完整复刻 SillyTavern 扩展生态
- 内置模型下载和托管服务

## 分层

```text
Views → ViewModels → Services → Repositories / Providers
```

- `Views` 使用 WPF XAML 描述界面，只处理文件选择、窗口和控件事件。
- `ViewModels` 保存界面状态并通过命令组织用户操作。
- `Services` 实现聊天、提示词、角色、知识库、备份、导出和本地模型等业务规则。
- `Repositories` 使用 Dapper 访问 SQLite，负责模型与数据库行之间的转换。
- `Providers` 将统一聊天请求路由到 OpenAI-compatible 或 Anthropic Messages API。

依赖在 `App.ConfigureServices` 中集中注册。主要服务使用单例生命周期，HTTP Provider 由 `IHttpClientFactory` 创建客户端。

## 聊天请求链路

```text
ChatViewModel.SendAsync
→ ChatService.SendAsync
→ PromptService.BuildAsync
→ ProviderRouter.StreamAsync
→ Provider SSE stream
→ UI chunk update
→ SQLite persistence
```

用户消息先落库；模型回复使用空的助手消息作为流式占位。若请求在第一个文本块之前失败或取消，空助手消息会被删除。成功内容会同时写入消息表和 Swipe 表。

## 聊天分支

分支是独立的 `ChatSession`，通过 `ParentSessionId` 和 `BranchedFromMessageId` 记录来源。创建分支时复制分支点之前的：

- 消息及当前选择状态
- 助手消息 Swipe 历史
- 群聊成员快照
- 图片附件文件和附件记录
- Persona、Lorebook、Prompt Preset 与 Author Note 绑定

分支不复制已有摘要，以免把分支点之后的信息带回旧时间线。删除源对话时，SQLite 将来源字段设为 `NULL`，分支内容继续独立存在。

## 数据和安全

- 主要结构化数据保存在 SQLite；外键删除负责清理消息、Swipe 和关联记录。
- API Key 使用 Windows DPAPI `CurrentUser` 范围保护。
- 头像、附件、知识库与插件文件的删除被限制在应用管理目录中。
- 备份恢复与数据导出使用临时文件和替换流程，避免留下不完整目标。
- 插件中心只管理经过大小、路径、哈希和身份校验的插件包，不加载插件代码。

## 发布要求

发布前应满足：

1. `dotnet test Tests\NativeTavern.Tests\NativeTavern.Tests.csproj -c Release`
2. `dotnet build Lorehaven.csproj -c Release`
3. `dotnet publish Lorehaven.csproj -c Release -r win-x64 --self-contained true -o publish`
4. `git diff --check`

推送 `v*` 标签后，GitHub Actions 会重新测试、发布单文件 Windows x64 程序并生成 SHA-256 校验文件。
