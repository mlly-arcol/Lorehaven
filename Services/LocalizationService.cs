using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NativeTavern.Services;

public sealed class LocalizationService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly IReadOnlyDictionary<string, string> EnglishToChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Local AI workspace"] = "本地 AI 工作区",
            ["New chat"] = "新建对话",
            ["WORKSPACE"] = "工作区",
            ["◉   Chat"] = "◉   对话",
            ["♙   Characters"] = "♙   角色",
            ["⌁   Prompt Studio"] = "⌁   提示词工作室",
            ["▤   Knowledge Base"] = "▤   知识库",
            ["⌕   Prompt Inspector"] = "⌕   提示词检查器",
            ["⬡   Plugins"] = "⬡   插件",
            ["⚙   Settings"] = "⚙   设置",
            ["Plugins"] = "插件中心",
            ["Discover and manage Lorehaven extensions. This version manages packages without loading or executing plugin code."] = "发现和管理 Lorehaven 扩展。当前版本仅管理插件包，不加载或执行插件代码。",
            ["Open plugins folder"] = "打开插件目录",
            ["Import plugin package"] = "导入插件包",
            ["Marketplace"] = "插件商城",
            ["Installed"] = "已安装",
            ["Install"] = "安装",
            ["Enable"] = "启用",
            ["Disable"] = "停用",
            ["Uninstall"] = "卸载",
            ["No matching marketplace plugins"] = "商城中暂无匹配插件",
            ["No matching installed plugins"] = "没有匹配的已安装插件",
            ["Drop a plugin package here, or choose Import plugin package."] = "把插件安装包拖到此页面，或点击“导入插件包”。",
            ["Settings"] = "设置",
            ["Configure model connections, generation behavior and context privacy."] = "配置模型连接、生成行为和上下文隐私。",
            ["Interface"] = "界面",
            ["Choose the language used throughout Lorehaven."] = "选择 Lorehaven 使用的界面语言。",
            ["Choose your language, theme and color mode."] = "选择界面语言、主题和明暗模式。",
            ["Provider connection"] = "模型连接",
            ["Connect to an OpenAI-compatible, cloud or local model service."] = "连接 OpenAI 兼容、云端或本地模型服务。",
            ["Generation"] = "生成参数",
            ["Local thinking mode"] = "本地思考模式",
            ["GPU layers"] = "显卡加载层数",
            ["Model folder"] = "模型目录",
            ["llama.cpp executable"] = "llama.cpp 程序",
            ["Browse"] = "选择…",
            ["Refresh model files"] = "刷新模型文件",
            ["Startup diagnostics"] = "启动诊断",
            ["Copy diagnostics"] = "复制诊断信息",
            ["Recent output from the model service. Check this when a model fails to start."] = "模型服务最近的输出。启动失败时可在这里查看原因。",
            ["Stop model"] = "停止模型",
            ["0 uses CPU only; lower the layer count if GPU memory runs out. Changes apply the next time you start a model."] = "0 表示仅使用 CPU；显存不足时可降低层数。修改在下次启动模型时生效。",
            ["For llama.cpp models with switchable thinking, such as Qwen3.5. Quick reply skips thinking; deep thinking may take longer and use more response tokens. Save to apply."] = "适用于支持切换思考模式的 llama.cpp 模型，例如 Qwen3.5。快速回复跳过思考；深度思考可能耗时更长，并占用更多回复额度。保存后生效。",
            ["Tune response creativity and request size."] = "调整回复创造性和请求长度。",
            ["Context privacy"] = "上下文隐私",
            ["Data backup"] = "数据备份",
            ["Back up or restore characters, chats, settings, avatars, attachments and knowledge files."] = "备份或恢复角色、聊天、设置、头像、附件和知识库文件。",
            ["Create backup"] = "创建备份",
            ["Restore backup"] = "恢复备份",
            ["Choose which local content may be included in model requests."] = "选择允许加入模型请求的本地内容。",
            ["Local model runtime"] = "本地模型运行环境",
            ["Discover running services or start a GGUF model with llama.cpp."] = "发现运行中的服务，或使用 llama.cpp 启动 GGUF 模型。",
            ["Interface Language"] = "界面语言",
            ["Appearance"] = "外观",
            ["Color mode"] = "明暗模式",
            ["Theme"] = "主题",
            ["Themes and color mode apply immediately and are saved automatically."] = "主题和明暗模式立即生效，并自动保存。",
            ["Regex scripts"] = "正则脚本",
            ["Clean up text as it is written"] = "在文字生成时自动整理",
            ["No scripts yet"] = "还没有脚本",
            ["Replace recurring wording in replies or in your own input."] = "替换回复或你自己输入里的固定说法。",
            ["+  New script"] = "+  新建脚本",
            ["Script details"] = "脚本详情",
            ["Applied in list order, and saved with the script."] = "按列表顺序生效，并随脚本一起保存。",
            ["Import…"] = "导入…",
            ["Export all"] = "导出全部",
            ["Import a resource pack or a SillyTavern world info file"] = "导入资源包，或 SillyTavern 世界书文件",
            ["Save personas, lorebooks, presets and scripts to one JSON file"] = "把人设、世界书、预设和脚本保存到一个 JSON 文件",
            ["Export Prompt Resources"] = "导出提示词资源",
            ["Import Prompt Resources"] = "导入提示词资源",
            ["Pinned messages"] = "收藏的消息",
            ["Plugin prompt resources…"] = "插件提示词资源…",
            ["Dice and random tables…"] = "掷骰与随机表…",
            ["Story timeline…"] = "剧情事件时间线…",
            ["Plugin resources"] = "插件资源",
            ["Roll dice"] = "掷骰",
            ["Story timeline"] = "事件时间线",
            ["Pin this message"] = "收藏这条消息",
            ["Find"] = "查找",
            ["Replace with"] = "替换为",
            ["Applies to"] = "作用于",
            ["Match"] = "匹配方式",
            ["Try it on sample text"] = "用示例文本试用",
            ["Regular expressions ignore letter case. Use $1, $2 in the replacement to keep captured text."] =
                "正则表达式忽略大小写；在替换内容中用 $1、$2 保留被匹配到的文字。",
            ["Save script"] = "保存脚本",
            ["Script name and pattern cannot be empty."] = "脚本名称和查找内容不能为空。",
            ["The pattern cannot be compiled. Check the message under the pattern field."] = "该表达式无法编译，请查看查找框下方的提示。",
            ["Script saved."] = "脚本已保存。",
            ["Script deleted."] = "脚本已删除。",
            ["New regex script"] = "新建正则脚本",
            ["No matching messages"] = "没有匹配的消息",
            ["Search messages"] = "搜索消息",
            ["Generate a title from recent messages"] = "根据最近消息自动命名对话",
            ["Generate conversation title"] = "自动命名对话",
            ["Cancel title generation"] = "取消自动命名",
            ["  ·  Generating title…"] = "  ·  正在生成标题…",
            ["Search"] = "搜索",
            ["Search messages · Enter to search"] = "搜索消息 · 回车执行",
            ["Current conversation only"] = "仅当前对话",
            ["Pinned messages only"] = "仅收藏消息",
            ["Clear search"] = "清空搜索",
            ["Searching messages…"] = "正在搜索消息…",
            ["Search failed. Please try again."] = "搜索失败，请重试。",
            ["Showing up to 30 recent matches"] = "最多显示最近的 30 条匹配消息",
            ["Enter a keyword, or choose a filter to browse recent messages"] = "输入关键词，或选择筛选条件浏览最近消息",
            ["Search every conversation · Enter to search"] = "搜索全部对话 · 回车执行",
            ["Provider"] = "模型服务商",
            ["Clear"] = "清除",
            ["Model"] = "模型",
            ["Discover Models"] = "获取模型列表",
            ["Temperature"] = "温度",
            ["Max Tokens"] = "最大生成长度",
            ["Context Length"] = "上下文长度",
            ["Character Context"] = "角色上下文",
            ["Knowledge Context"] = "知识库上下文",
            ["Image Context"] = "图片上下文",
            ["Local Models"] = "本地模型",
            ["GGUF model files"] = "GGUF 模型文件",
            ["Start and Connect"] = "启动并连接",
            ["Running local services"] = "运行中的本地服务",
            ["Include the selected character card and First Message in model requests"] = "在模型请求中包含所选角色卡和首条消息",
            ["Allow matched knowledge-base excerpts to be sent in model requests"] = "允许将匹配的知识库片段发送给模型",
            ["Allow attached images to be sent in model requests"] = "允许将附加图片发送给模型",
            ["Scan local services at startup"] = "启动时扫描本地模型服务",
            ["Scan Local"] = "扫描本地服务",
            ["Use Selected"] = "使用所选服务",
            ["Test Connection"] = "测试连接",
            ["Save"] = "保存",
            ["Reference context window; local server context size is configured by the server"] = "参考上下文窗口；本地服务的上下文大小由服务器配置",
            ["Trim the oldest turns when a request would exceed the context window"] = "请求超出上下文窗口时自动省略最早的轮次",
            ["Newest messages are always kept; short chats are never cut"] = "始终保留最近的消息，短对话不会被裁剪",
            ["History: "] = "历史裁剪：",
            ["Characters"] = "角色",
            ["Create, import and manage character cards"] = "创建、导入和管理角色卡",
            ["Import Card"] = "导入角色卡",
            ["Export Card"] = "导出角色卡",
            ["Saves JSON, or embeds the card in a PNG avatar for sharing"] = "可保存为 JSON，或把角色卡嵌入 PNG 头像以便分享",
            ["+ New Character"] = "+ 新建角色",
            ["Search name, tags or description"] = "搜索名称、标签或描述",
            ["Favorites only"] = "仅显示收藏",
            ["Groups"] = "分组",
            ["Group"] = "分组",
            ["+ Create Group"] = "+ 创建分组",
            ["Create Group"] = "创建分组",
            ["Edit Group"] = "编辑分组",
            ["Save Group"] = "保存分组",
            ["Group Chat"] = "群聊",
            ["Members"] = "成员",
            ["Next speaker"] = "下一位",
            ["Start a group chat with this group"] = "使用该分组创建群聊",
            ["Manage group chat members"] = "管理群聊成员",
            ["Let the next character speak"] = "让下一位角色发言",
            ["Automatically select or specify the next speaking character"] = "自动选择或指定下一位发言角色",
            ["Auto select"] = "自动选择",
            ["Edit Group Chat"] = "编辑群聊",
            ["Save Group Chat"] = "保存群聊",
            ["Group chat title"] = "群聊名称",
            ["Select group chat members"] = "选择群聊成员",
            ["Edit group"] = "编辑分组",
            ["Delete group"] = "删除分组",
            ["Group name"] = "分组名称",
            ["Select characters for this group"] = "选择该分组包含的角色",
            ["Use the group editor to change membership"] = "使用分组编辑器更改角色归属",
            ["Choose an existing group or type a new name"] = "选择已有分组或输入新分组名称",
            ["Choose Avatar"] = "选择头像",
            ["Favorite"] = "收藏",
            ["Name *"] = "名称 *",
            ["Name"] = "名称",
            ["Creator"] = "创建者",
            ["Tags"] = "标签",
            ["Aliases"] = "别名",
            ["Description"] = "描述",
            ["Personality"] = "性格",
            ["Scenario"] = "场景",
            ["First Message"] = "首条消息",
            ["Example Messages"] = "示例消息",
            ["Start Chat"] = "开始对话",
            ["Delete"] = "删除",
            ["Prompt Studio"] = "提示词工作室",
            ["Manage personas, lorebooks and reusable prompt presets."] = "管理人设、世界书和可复用的提示词预设。",
            ["Manage personas, lorebooks and prompt presets used to shape each conversation."] = "管理用于塑造每次对话的人设、世界书和提示词预设。",
            ["Personas"] = "人设",
            ["Persona"] = "人设",
            ["Choose a voice for your conversations"] = "为对话选择一种表达风格",
            ["Persona library"] = "人设库",
            ["Reusable author voices"] = "可复用的作者口吻",
            ["+  New persona"] = "+  新建人设",
            ["Persona details"] = "人设详情",
            ["Define how this persona speaks and behaves."] = "定义该人设的说话方式和行为。",
            ["Define a reusable voice or point of view."] = "定义可复用的口吻或视角。",
            ["Persona content"] = "人设内容",
            ["Delete persona"] = "删除人设",
            ["Save persona"] = "保存人设",
            ["No personas yet"] = "还没有人设",
            ["Create one to define a reusable voice."] = "新建人设以定义可复用的表达风格。",
            ["Lorebooks"] = "世界书",
            ["Lorebook"] = "世界书",
            ["Lorebook settings"] = "世界书设置",
            ["Lorebook library"] = "世界书库",
            ["Keyword-triggered context"] = "由关键词触发的上下文",
            ["+  New lorebook"] = "+  新建世界书",
            ["Lorebook details"] = "世界书详情",
            ["Create entries that activate when keywords appear."] = "创建在关键词出现时自动激活的条目。",
            ["Enabled"] = "已启用",
            ["Entries"] = "条目",
            ["+ New entry"] = "+ 新建条目",
            ["Keywords"] = "关键词",
            ["Entry content"] = "条目内容",
            ["Entry details"] = "条目详情",
            ["Entry name"] = "条目名称",
            ["Primary keywords"] = "主要关键词",
            ["Secondary keywords"] = "次要关键词",
            ["Comma separated"] = "使用英文逗号分隔",
            ["Content"] = "内容",
            ["Priority"] = "优先级",
            ["Depth"] = "深度",
            ["Constant"] = "始终启用",
            ["Selective"] = "选择性触发",
            ["Delete entry"] = "删除条目",
            ["Save entry"] = "保存条目",
            ["No lorebooks yet"] = "还没有世界书",
            ["No entries yet"] = "还没有条目",
            ["Prompt presets"] = "提示词预设",
            ["Prompt preset"] = "提示词预设",
            ["Reusable model instructions"] = "可复用的模型指令",
            ["+  New preset"] = "+  新建预设",
            ["Preset details"] = "预设详情",
            ["Configure instructions and optional model overrides."] = "配置指令和可选的模型参数覆盖。",
            ["System prompt"] = "系统提示词",
            ["Main prompt"] = "主提示词",
            ["Model override"] = "指定模型",
            ["Max tokens"] = "最大生成长度",
            ["Leave overrides blank to use the values from Settings."] = "留空则使用设置页中的参数。",
            ["Save preset"] = "保存预设",
            ["No presets yet"] = "还没有预设",
            ["Create one to reuse model instructions."] = "新建预设以复用模型指令。",
            ["Knowledge Base"] = "知识库",
            ["Drop or import TXT, Markdown and PDF files. Context sharing stays disabled until enabled in Settings."] = "拖放或导入 TXT、Markdown 和 PDF 文件。只有在设置中启用后才会共享上下文。",
            ["Import Documents"] = "导入文档",
            ["Enable / Disable Selected"] = "启用 / 禁用所选项",
            ["No documents yet"] = "还没有知识库文档",
            ["Drop TXT, Markdown or PDF files here, or choose Import Documents."] = "将 TXT、Markdown 或 PDF 文件拖到这里，或选择“导入文档”。",
            ["Search characters"] = "搜索角色",
            ["Previous variation"] = "上一个版本",
            ["Next variation"] = "下一个版本",
            ["Prompt Inspector"] = "提示词检查器",
            ["Inspect the exact text context before sending it to your Provider."] = "检查发送给模型服务商之前的完整文本上下文。",
            ["Refresh"] = "刷新",
            ["Estimated tokens: "] = "预估 token：",
            ["Lore: "] = "世界书：",
            ["Local workspace"] = "本地工作区",
            [" tokens"] = " token",
            ["Switch conversation"] = "切换对话",
            ["Inspect prompt"] = "检查提示词",
            ["Delete conversation"] = "删除对话",
            ["Author note"] = "作者备注",
            ["Conversation settings"] = "对话设置",
            ["⚙ Conversation settings"] = "⚙ 对话设置",
            ["Adjust the optional context used only by this conversation."] = "调整仅用于当前对话的可选上下文。",
            ["Apply"] = "应用",
            ["今天想聊些什么？"] = "今天想聊些什么？",
            ["选择角色或直接输入消息，开始一段新的对话"] = "选择角色或直接输入消息，开始一段新的对话",
            ["Connect a model to start chatting"] = "连接模型后开始对话",
            ["Configure a local or cloud provider in Settings."] = "请先在设置中配置本地或云端模型服务。",
            ["Open settings"] = "打开设置",
            ["Copy"] = "复制",
            ["Edit"] = "编辑",
            ["Regenerate"] = "重新生成",
            ["Cancel"] = "取消",
            ["Message Lorehaven · Enter to send · Shift+Enter for new line"] = "给 Lorehaven 发送消息 · Enter 发送 · Shift+Enter 换行",
            ["给 Lorehaven 发送消息"] = "给 Lorehaven 发送消息",
            ["Attach image"] = "添加图片",
            ["Stop"] = "停止",
            ["Send"] = "发送",
            ["Branch"] = "分支",
            ["Create a new conversation from this message"] = "从此消息新建分支对话",
            ["Export conversation"] = "导出对话",
            ["Rename conversation"] = "重命名对话",
            ["Story summary"] = "剧情回顾",
            ["Conversation stats"] = "对话统计",
            ["Pin this conversation"] = "置顶这个对话",
            ["Unpin this conversation"] = "取消置顶",
            ["Greater than 0"] = "需大于 0",
            ["Enable global hotkeys"] = "启用全局热键",
            ["System-wide shortcuts work even when Lorehaven is not focused."] = "全局快捷键在窗口没有焦点时也能使用。",
            ["Hotkeys need a modifier such as Ctrl or Alt, e.g. Ctrl+Alt+T. Keys already owned by other apps are skipped."] = "热键需要 Ctrl、Alt 等修饰键，例如 Ctrl+Alt+T；被其他程序占用的热键会被跳过。",
            ["Show / hide hotkey"] = "呼出 / 隐藏热键",
            ["Boss key (hide to tray)"] = "老板键（立即隐藏到托盘）",
            ["Distill character memory from chats and include it in model requests"] = "自动提炼角色记忆，并随模型请求发送",
            ["Long-term facts the character remembers; view and edit them from the chat header"] = "角色长期记住的要点；可在聊天页顶部查看和编辑",
            ["Character memory"] = "角色记忆",
            ["Story workspace"] = "故事工作区",
            ["Welcome to Lorehaven"] = "欢迎使用 Lorehaven",
            ["Every conversation opens a new possibility."] = "每段对话，\n都有新的可能。",
            ["Create characters, collect ideas and make room for your next story."] = "在这里创造角色、整理灵感，开启属于你的 AI 对话空间。",
            ["A place for your imagination"] = "一个空间，容纳你的灵感",
            ["Conversations that spark new ideas"] = "自由对话，探索新想法",
            ["Characters, memories and branching stories"] = "塑造角色，延续记忆与故事",
            ["A library for your worlds and references"] = "整理设定与资料，让灵感有迹可循",
            ["Your characters. Your world. Your workspace."] = "你的角色，你的世界，你的工作区。",
            ["Your creative space"] = "你的本地创作空间",
            ["Step into your workspace to continue a conversation or begin a new story."] = "进入工作区，继续已有的对话，或开始一个新的故事。",
            ["Connect a model, import a character card or pick up where you left off."] = "进入后可连接模型、导入角色卡，也可以继续上次的对话。",
            ["Enter workspace"] = "进入工作区",
            ["STORY WORKSPACE"] = "故事工作区",
            ["Your next chapter starts here"] = "下一章，从这里开始",
            ["CREATE & EXPLORE"] = "创作与探索",
            ["TOOLS & RESOURCES"] = "工具与资源",
            ["Model configuration"] = "模型配置",
            ["Configure model"] = "配置模型",
            ["Configured"] = "已配置",
            ["Ready to create"] = "开始创作",
            ["Chat"] = "对话",
            ["Recent stories"] = "最近的故事",
            ["Story notes"] = "故事札记",
            ["Current branch"] = "当前分支",
            ["View branch tree"] = "查看分支树",
            ["Conversation tools"] = "对话工具与搜索",
            ["Close notes"] = "收起札记",
            ["Generating title…"] = "正在生成标题…",
            ["No memories yet. Important details will stay here."] = "还没有记忆。重要的细节会留在这里。",
            ["Write your reply, or add a new turn to the story…"] = "写下你的回应，或为故事加入新的转折…",
            ["Group performance"] = "群聊自动演出",
            ["Let the story unfold"] = "让故事继续发生",
            ["Long-term memory"] = "长期记忆",
            ["Story routes"] = "故事路线",
            ["  ·  Branch"] = "  ·  分支",
            ["Roleplay · World building · Shared stories"] = "角色扮演 · 世界设定 · 多人共创",
            ["Enter to send · Shift+Enter for a new line"] = "Enter 发送 · Shift+Enter 换行",
            ["CHARACTER LIBRARY"] = "角色资料库",
            ["Your cast"] = "你的角色阵容",
            ["Character card"] = "角色卡",
            ["Identity & organization"] = "身份与归属",
            ["Story & personality"] = "故事与个性",
            ["No characters to show"] = "暂时没有角色",
            ["Create a character, import a card or adjust your filters."] = "创建角色、导入角色卡，或调整筛选条件。",
            ["REFERENCE LIBRARY"] = "参考资料库",
            ["Your references, on your terms"] = "让参考资料为你的故事服务",
            ["Give your stories a library of useful references."] = "为故事准备随时可用的参考资料。",
            ["Imported documents"] = "已导入文档",
            ["Select a document to manage its availability"] = "选择文档，管理是否在对话中使用",
            ["Context sharing stays disabled until enabled in Settings."] = "在设置中主动开启前，资料不会加入模型请求。",
            ["CREATIVE TOOLKIT"] = "创作工具箱",
            ["EXTENSIONS"] = "扩展能力",
            ["Extension library"] = "扩展资料库",
            ["Add new capabilities to your workspace."] = "为工作区添加更多能力。",
            ["Find an extension"] = "搜索扩展",
            ["Import a local .ntplugin or .zip package to add an extension."] = "导入本地 .ntplugin 或 .zip 安装包，添加扩展。",
            ["Disabled"] = "已停用",
            ["WORKSPACE PREFERENCES"] = "工作区偏好",
            ["Assembled context"] = "本次请求的完整上下文",
            ["Story branches"] = "剧情分支树",
            ["Memory character"] = "记忆所属角色",
            ["Locked memory"] = "已锁定的记忆",
            ["Edit / lock memories"] = "编辑 / 锁定记忆",
            ["Start performance…"] = "自动演出…",
            ["Pause performance"] = "暂停演出",
            ["Resume performance"] = "继续演出",
            ["End performance"] = "结束演出",
            ["No memories yet"] = "还没有记忆",
            ["Extract now"] = "立即提炼",
            ["Add"] = "添加",
            ["Add a memory manually"] = "手动添加一条记忆",
            ["Lorehaven can make mistakes. Check important information."] = "Lorehaven 可能会出错，请核查重要信息。"
        };

    private static readonly IReadOnlyDictionary<string, string> ChineseToEnglish = BuildChineseToEnglish();

    private bool _applying;

    public string CurrentLanguage { get; private set; } = Chinese;
    public event Action? LanguageChanged;

    public LocalizationService()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
    }

    public void SetLanguage(string? languageCode)
    {
        CurrentLanguage = Normalize(languageCode);
        ApplyToAllWindows();
        LanguageChanged?.Invoke();
    }

    public string Text(string chinese, string english) => CurrentLanguage == English ? english : chinese;

    public void Refresh()
    {
        if (Application.Current is null) return;
        Application.Current.Dispatcher.BeginInvoke(ApplyToAllWindows, DispatcherPriority.Loaded);
    }

    public static string Normalize(string? languageCode) =>
        string.Equals(languageCode, English, StringComparison.OrdinalIgnoreCase) ? English : Chinese;

    private static IReadOnlyDictionary<string, string> BuildChineseToEnglish()
    {
        var translations = EnglishToChinese.GroupBy(x => x.Value)
            .ToDictionary(x => x.Key, x => x.First().Key, StringComparer.Ordinal);
        translations["今天想聊些什么？"] = "What would you like to talk about?";
        translations["选择角色或直接输入消息，开始一段新的对话"] = "Choose a character or type a message to start a new conversation.";
        translations["给 Lorehaven 发送消息"] = "Message Lorehaven";
        return translations;
    }

    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        element.Dispatcher.BeginInvoke(() => Apply(element));
    }

    private void ApplyToAllWindows()
    {
        if (Application.Current is null) return;
        foreach (Window window in Application.Current.Windows) Apply(window);
    }

    private void Apply(DependencyObject root)
    {
        if (_applying) return;
        _applying = true;
        try { ApplyRecursive(root); }
        finally { _applying = false; }
    }

    private void ApplyRecursive(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock textBlock when !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty):
                textBlock.Text = Translate(textBlock.Text);
                break;
            case HeaderedContentControl headered when headered.Header is string header:
                headered.Header = Translate(header);
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                contentControl.Content = Translate(content);
                break;
        }

        if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string tooltip)
            frameworkElement.ToolTip = Translate(tooltip);

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            ApplyRecursive(VisualTreeHelper.GetChild(element, index));
    }

    private string Translate(string value)
    {
        if (CurrentLanguage == Chinese)
            return EnglishToChinese.TryGetValue(value, out var chinese) ? chinese : value;
        return ChineseToEnglish.TryGetValue(value, out var english) ? english : value;
    }
}
