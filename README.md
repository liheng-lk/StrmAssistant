# StrmAssistant Custom（Emby 神医助手社区增强版）

![logo](StrmAssistant/Properties/thumb.png "logo")

## [[English]](README.en.md)

> [!IMPORTANT]
> 本仓库是 [`sjtuross/StrmAssistant`](https://github.com/sjtuross/StrmAssistant) 的 **Fork / 二次开发版本**，不是上游项目的官方发行版。
>
> 本 Fork 保留上游项目的核心功能，并针对 Emby 新版本兼容性、STRM 媒体信息可靠性、在线片头片尾、深度删除、Webhook 自动化和运行时诊断进行了独立开发。由本 Fork 引入的问题请优先在本仓库反馈，不要直接归因于上游项目。

## 上游项目与致谢

- 上游项目：[`sjtuross/StrmAssistant`](https://github.com/sjtuross/StrmAssistant)
- 上游 Wiki：[`sjtuross/StrmAssistant/wiki`](https://github.com/sjtuross/StrmAssistant/wiki)
- 本 Fork：[`liheng-lk/StrmAssistant`](https://github.com/liheng-lk/StrmAssistant)
- 许可证：[`GNU GPL-3.0`](LICENSE)

感谢原项目作者和所有贡献者。本 Fork 基于 GPL-3.0 继续开发，原项目的版权声明、许可证和贡献历史应继续得到保留与尊重。

> 上游 Wiki 主要描述原项目行为。涉及本 Fork 新增功能时，请以本仓库 README、代码、提交记录和实际运行结果为准。

## 项目定位

StrmAssistant Custom 是面向 Emby 的社区增强插件，重点服务于大量 STRM、远端媒体、自动化媒体库和新版本 Emby 场景。

本项目不会以绕过 Emby 授权、DRM 或解锁付费功能为目标，也不分发修改后的 Emby 服务端二进制文件。部分增强功能会通过 Emby 插件 API、通知系统和运行时补丁实现兼容与扩展。

## 主要功能

### 继承并维护的上游能力

1. 提高首次播放的起播速度
2. 视频截图与章节/缩略图预览增强
3. 片头片尾探测增强
4. 自动合并同目录视频为多版本
5. 媒体信息提取与持久化
6. 独立外挂字幕扫描
7. 自定义 TMDB 备选语言与相关元数据增强
8. 原语言海报
9. 中文搜索与拼音排序
10. TMDB 剧集组等上游已有能力

### 本 Fork 的重点增强

- **Emby 4.8 / 4.9 / 4.10 兼容构建**：CI 使用多个 Emby Core 版本进行编译与行为测试。
- **设置页重构**：GenericUI 按常规、媒体信息、元数据、片头片尾、体验增强、关于等标签组织。
- **在线片头片尾数据库**：支持 IntroDB.app 与 TheIntroDB.org，并提供 Preview / Plan / Apply 与诊断接口。
- **STRM MediaInfo 可靠性**：增加持久化备份、Shadow 缓存、恢复队列、完整性检查、Fleet Health 与 Runtime Test，目标是减少“已有媒体信息后来消失”以及重复探测导致的启播延迟。
- **深度删除安全链**：提供 Plan、Dry Run、Allowed Roots、删除后验证、事务恢复与级联删除保护。
- **通用 `deep.delete` Webhook 语义**：删除前读取 STRM 原始目标，并通过 Emby NotificationManager 发送 `deep.delete` 事件；外部 Webhook/自动化程序可自行决定如何处理 115、夸克、阿里云盘、CDN、签名 URL 或其他存储来源。
- **OpenList / WebDAV 直删**：作为可选 Provider 保留；启用后插件可以直接执行远端删除并进行删除后验证。它们不是使用深度删除功能的前提。
- **可靠性诊断**：提供 ReliabilityAudit、MediaInfo RuntimeTest、DeepDelete Plan/Probe 等诊断能力。
- **行为测试门禁**：核心纯逻辑、文件系统和 HTTP 事务先运行 Contract Tests；测试失败时 CI 不生成可发布 Artifact。

## 深度删除与 Webhook

本 Fork 将“深度删除”分为两种并列工作方式。

### 1. 通用 Webhook / 外部自动化

这是与网盘厂商无关的通用方式：

```text
用户执行深度删除
    ↓
插件在删除前读取 .strm 原始内容
    ↓
发送 Emby Notification Event: deep.delete
    ↓
Emby Webhook / 通知提供器
    ↓
外部自动化程序
    ↓
外部程序自行处理实际网盘文件
```

`deep.delete` 保持兼容的通知描述结构：

```text
Item Name:
<媒体名称>

Item Path:
<本地 STRM 路径>

Mount Paths:
<STRM 中的原始目标/直链>
<可选的 Provider 映射路径>
```

插件不要求先识别 STRM 属于哪个网盘，也不会为了发送 Webhook 而强制要求 OpenList/WebDAV 配置。

### 2. OpenList / WebDAV 直接删除

当管理员明确配置远端 Provider、路径映射和允许删除根目录后，插件可直接删除远端对象。此模式属于高风险功能，必须先查看 Plan / Probe，并建议先使用测试媒体验证。

## 测试状态说明

本仓库不把“日志显示 `Info`”“Harmony Patch 成功”或“CI 能编译”视为功能已经可用。

采用以下证据等级：

- **CONTRACT PASS**：核心算法、文件操作或 HTTP 事务已通过自动行为测试。
- **RUNTIME PASS**：已在真实 Emby 运行环境中验证最终副作用和重新读取结果。
- **DESTRUCTIVE PASS**：高风险删除功能已在明确可丢弃的测试媒体上完成端到端验证。

> CI Green 只代表当前自动化门禁通过，不代表所有 Emby 版本、客户端、网盘和外部 Webhook 环境都已经完成 Runtime/Destructive 验证。

## 兼容性

当前 CI 主要覆盖以下 Emby Core 构建目标：

- 4.8.0.80
- 4.9.1.90
- 4.10.0.1-beta

实际 Emby Server 小版本可能存在 API/运行时差异。安装前建议备份配置与数据库，并优先在测试环境验证。

## 安装

1. 从本 Fork 的 GitHub Actions Artifact 或 Release 获取与目标 Emby 版本对应的 `StrmAssistantCustom.dll`。
2. 备份现有插件 DLL 和 Emby 配置/数据库。
3. 停止 Emby Server。
4. 将 `StrmAssistantCustom.dll` 放入 Emby 插件目录；升级本 Fork 时替换旧版本。
5. 启动 Emby Server。
6. 在插件设置页确认版本和配置，并先运行只读诊断，再启用深度删除等高风险功能。

> 不建议同时加载多个实现相同 Harmony/媒体增强功能的 StrmAssistant 派生 DLL，以免产生重复 Patch 或不可预测行为。

## 开发与测试原则

- PR 开发阶段保持 Draft，未完成真实运行验收前不应仅凭编译通过宣布功能完成。
- 高风险文件删除必须优先提供 Preview / Plan / Dry Run / Verification。
- MediaInfo、Webhook、数据库写入等功能应通过“执行后重新读取”证明真实副作用，而不是只检查日志。
- 新功能应尽量加入可重复的 Contract Test，并在需要时增加真实 Emby Runtime Test。

## 许可证

本仓库继续使用 **GNU General Public License v3.0 (GPL-3.0)**，详见 [`LICENSE`](LICENSE)。

GPL-3.0 允许在遵守许可证条款的前提下使用、研究、修改和再分发软件；README 不额外添加与 GPL-3.0 冲突的“仅限非商业用途”等限制。若分发修改后的二进制版本，应同时履行 GPL-3.0 对应源代码和许可证告知等义务。

## 声明与免责声明

1. 本项目及本 Fork 与 Emby LLC 无隶属关系，也未获得 Emby LLC 的官方授权或认可。
2. 用户应自行确保 Emby、媒体内容、远端存储和自动化工具的使用符合相关许可证、服务条款和适用法律。
3. 深度删除、远端删除、Webhook 自动化、数据库迁移等功能具有数据丢失风险。请在执行前备份，并先使用可丢弃测试数据验证。
4. 本 Fork 的问题不应自动归因于上游 `sjtuross/StrmAssistant`；提交 Issue 时请提供本 Fork 版本、Emby 版本和必要的诊断信息。
5. 软件按 GPL-3.0 的无担保条款提供；开发者和贡献者不对因使用本项目导致的数据丢失、服务中断或其他损失承担超出适用法律要求的责任。
