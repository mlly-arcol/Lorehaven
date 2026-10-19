# Lorehaven 插件包格式

Lorehaven 2.3 提供插件商城、插件包管理和受控的声明式能力。应用不加载或执行插件代码；该格式用于分发、安装、更新、启停和卸载扩展。

## 安装包

安装包扩展名为 `.ntplugin` 或 `.zip`，根目录必须包含 UTF-8 编码的 `plugin.json`：

```json
{
  "id": "author.example-plugin",
  "name": "Example Plugin",
  "version": "1.0.0",
  "author": "Author",
  "description": "A short description.",
  "homepage": "https://example.com",
  "minimumAppVersion": "1.3.0",
  "permissions": [],
  "capabilities": []
}
```

`id` 必须为 2–64 个字符，只能使用小写字母、数字、点、短横线和下划线。`version` 和 `minimumAppVersion` 使用可由 .NET `Version` 解析的版本号。

Lorehaven 不执行包内 DLL、脚本或可执行文件。插件可以通过 `capabilities` 请求应用提供的受控声明式能力。当前支持：

| 能力 | 包内数据文件 | 生效方式 |
| --- | --- | --- |
| `character-status-v1` | 无 | 回复后维护角色状态 |
| `prompt-resources-v1` | `prompt-resources.json` | 按对话选择提示词资源，或由资源显式声明 `autoApply` |
| `dice-random-tables-v1` | `random-tables.json` | 用户手动掷骰或抽取加权随机表 |
| `story-timeline-v1` | 无 | 用户管理事件，或主动调用模型提取 |

未知能力会被保留并在插件中心标明，但不会执行。`permissions` 用于向用户展示能力所需的数据访问。声明受支持能力的包在安装和重新启用时会验证对应数据文件；损坏的包不会替换已安装的可用版本。

`prompt-resources.json` 使用 `formatVersion: 1`，可包含 `personas`、`lorebooks`、`presets`、`textRules`。每项需要稳定的 `id` 和 `name`，`autoApply` 默认 `false`；`scope` 可为 `all`、`single` 或 `group`。世界书条目可设置关键词、优先级和扫描深度。资源作为只读插件数据加入提示词，不写入用户自己的 Persona、世界书或预设表。一个请求最多加入 12 段、共 12,000 字符的插件内容。

`random-tables.json` 使用 `formatVersion: 1`，在 `tables` 中定义表的 `id`、`name` 和 `entries`。每个结果包含 `text` 和正整数 `weight`。随机结果在本机生成、按对话保存；插件不会自行调用模型，也不会自动把结果加入上下文。掷骰表达式支持 `d20`、`2d6+1` 等 `NdS±M` 形式。

仓库的 `PluginPackages/` 包含四个示例插件的源码和可直接安装的 `.ntplugin`。GitHub Release 也会附上这四个安装包；下载后在“插件中心 → 导入插件包”中安装。新增能力要求 Lorehaven 2.3.0 或更高版本。

示例插件继续使用 `nativetavern.*` ID 与安装包文件名，以便已有安装和对话中的插件选择继续匹配；插件作者显示为 Lorehaven。

## 商城目录

插件中心首次启动后会创建 `UserData/Plugins/catalog.json`。目录格式如下：

```json
{
  "formatVersion": 1,
  "plugins": [
    {
      "id": "author.example-plugin",
      "name": "Example Plugin",
      "version": "1.0.0",
      "author": "Author",
      "description": "A short description.",
      "homepage": "https://example.com",
      "packageUrl": "https://example.com/example.ntplugin",
      "sha256": "64-character lowercase or uppercase SHA-256"
    }
  ]
}
```

商城安装只接受 HTTPS 地址，并在安装前验证 SHA-256、插件 ID 和版本。默认在线商城目录为空；随 Release 提供的示例插件是独立安装包，不会自动安装。

## 目录与安全限制

- 插件程序文件：`UserData/Plugins/<plugin-id>/`
- 插件持久数据预留目录：`UserData/PluginData/`
- 单个压缩包最大 100 MB，解压后最大 500 MB，最多 2,000 个条目
- 拒绝目录穿越、重复路径和符号链接
- 更新会先保留旧目录，只有新包完整就位后才替换
- 插件与插件数据会进入 Lorehaven 备份；旧版备份没有这些目录时仍可正常恢复
