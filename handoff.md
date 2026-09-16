# 工作区与会话交接

文档基线：0.1.0｜更新时间：2026-09-16｜当前唯一切片：S01 / T010｜状态：DONE。

本文件描述真实现场，不是未来设计。恢复会话必须核对文件和 Git 状态，不能把这里的计划当作已经实现。

## 1. 一分钟接续信息

**最终验收（2026-09-16）：S01/T010 = DONE。** 刷新系统/用户 PATH 后，`C:\Program Files\dotnet\dotnet.exe` 可用；`dotnet --info` 识别 SDK 10.0.401、Host 10.0.12、RID win-x64，`dotnet --list-sdks` 输出 `10.0.401 [C:\Program Files\dotnet\sdk]`。已创建 `global.json` 固定 10.0.401（rollForward=disable）。

实际验收：`dotnet restore TikTokAudio.slnx` 退出码 0；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出码 0（0 Errors / 0 Warnings）；从 `.vscode/tasks.json` 读取并执行 `dotnet build TikTokAudio.slnx -c Debug` 退出码 0（0 Errors / 0 Warnings）。`dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build` 启动宿主与 WPF 进程；主窗口句柄有效，标题为“TikTok 直播音频工具”，Responding=True；优雅关闭后宿主与应用进程均退出。

验收结论：solution 四个成员、全部引用目标及方向、TFM、Desktop WinExe/UseWPF/x64、App.StartupUri 和 MainWindow 静态检查退出码 0；NuGet 还原、编译、WPF 启动/关闭均通过。构建期间无编译错误或警告，无代码侧修复；未修改业务源码、项目引用、TFM 或 `.vscode/tasks.json`。

Git 复核：仍为 `E:/Live_audio`、main、无 HEAD/BaseCommit，仅一个主工作树；19 个已有文件均未跟踪（含新增 `global.json`）。保留全部文件，没有 reset/clean、覆盖、提交或推送；未创建工作者或启动应用服务。

S01/T010 已完成并停止。本轮没有 TikTok/TTS/音频设备/真实直播验证，没有下载模型、调用平台或发布。下一任务为 T020，尚未开始。

### 上轮现场与排查记录（历史，不替代以上最新复验）

本轮已获 S01/T010 开发授权，由 Codex 主会话担任整合负责人，在 `E:\Live_audio` 顺序执行；不启动工作者。已完成六文档阅读、环境检查及 Git 初始化，原六文档保留。原文档交付来源见第 2 节，仅作历史来源，不作为源码根目录。

当前发现：Windows 10 专业版 x64（10.0.19045），Git 2.53.0.windows.2，VS Code 1.137.0；当前终端、常见安装目录、注册表和 VS Code SDK 存储均未找到 .NET SDK。`dotnet --info` / `dotnet --list-sdks` 报命令不存在。官方元数据列出 10.0.401，尚未安装验证。构建与窗口验证未执行。

实际 Git：`git init -b main` 退出码 0；`git rev-parse --show-toplevel` 为 `E:/Live_audio`；分支 `main`，尚无 HEAD/BaseCommit；`git worktree list` 仅主目录。初始 `git status --short` 为六份未跟踪文档。无重叠源码、无额外工作树、无后台开发进程。

已创建的未验收文件：`TikTokAudio.slnx`、`Directory.Build.props`、`.gitignore`、`.vscode/tasks.json`；`src/TikTokAudio.{Domain,Application,Infrastructure,Desktop}/` 的四个项目文件；Desktop 的 `App.xaml`、`App.xaml.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`。四层依赖符合 agents；前三层 `net10.0`，Desktop `net10.0-windows` / WPF / x64；nullable 开启；无第三方 PackageReference，无业务服务/功能按钮。应用版本字段暂为 `0.1.0-s01`，并非已构建分发版本。

静态验证：PowerShell `[xml]` 解析 `.slnx`、`.csproj`、`.xaml`、props；`Get-Content .vscode/tasks.json -Raw -Encoding UTF8 | ConvertFrom-Json`；核对项目引用及目标文件存在、无 PackageReference、Nullable=enable。最终退出码均为 0。引用核对脚本首轮因 PowerShell `-join` 与 `-ne` 优先级写法报告错误，拆分比较变量后通过；未因此改动项目引用。`git diff --check` 退出码 0，但全部文件未跟踪，不能作为完整差异或编译验证。

阻塞事实与最小解决方式：常见 SDK 安装目录、注册表、VS Code 存储均未找到工具链。已读取微软官方 `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`，当前发布日为 2026-09-08，候选 SDK 为 10.0.401，Windows x64 ZIP 为 `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip`，官方 SHA-512 为 `24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430`。尚未下载或安装。已询问是否安装到用户目录（无需管理员权限），或提供现有 SDK 路径，尚未收到答复。

未执行：SDK 实测与 `global.json` 固定、`dotnet restore TikTokAudio.slnx`、`dotnet build TikTokAudio.slnx -c Debug --no-restore`、`dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build`、窗口显示/关闭/退出码验证。原因是缺 SDK，不能编译启动；XML 检查不替代构建。Windows Computer Use API 已初始化，可供后续窗口验收使用，但尚未控制/启动任何应用窗口。VS Code 未安装 C# 扩展；本次仅配置 build task，不宣称 F5 调试已验证。

下一条允许动作：取得 SDK 安装选择/已有路径后，将 S01 恢复 ACTIVE，核验实际 SDK、固定版本并执行上述验收。新会话先核对已有工程，禁止重复生成。S01 全部通过后才 DONE 并停止；下一切片建议 T020，尚未进入。没有 TikTok/TTS/音频设备/真实直播验证，本轮未下载模型、调用平台或发布。

## 2. T000 文档交付来源（历史记录，非当前源码现场）

| 项目 | 已核对的状态 |
|---|---|
| 本轮工作目录 | `C:\Users\ywyline\Documents\Codex\2026-09-14\x` |
| 六文档交付目录 | `C:\Users\ywyline\Documents\Codex\2026-09-14\x\outputs\tiktok-audio-dev-docs` |
| 文档压缩包 | `C:\Users\ywyline\Documents\Codex\2026-09-14\x\outputs\tiktok-audio-dev-docs.zip`；包含六个根层级 Markdown 文件 |
| 源码仓库 | 尚未建立/指定；上述工作目录不在 Git 仓库中 |
| 分支、HEAD、整合提交 | 不适用，没有 Git 提交 |
| 应用源码和解决方案 | 本轮未创建；交付目录无 `.csproj`、`.slnx` 或应用代码 |
| 当前切片（历史记录） | S01 / T010，READY（已由最新验收更新为 DONE） |
| 活动工作者/工作树 | 无，由本轮创建的工作树为 0 |
| .NET/NAudio/SQLite/FFmpeg 版本 | 未检查、未安装，不能推断可用 |
| 实际 TTS 引擎、模型、音色 | 未选择、未验证 |
| TikTok 账号/房间/事件来源 | 未配置、未验证 |
| 构建、程序测试、平台实测 | 未执行；本轮只有文档校验 |

工作区原有 `work/` 与其他资料不属于六文档交付，本轮未清理。不要把旧软件包中的程序、授权数据、浏览器缓存或个人信息复制进新项目，也不要为建立新项目删除这些原有资料。

移动/复制文档后，本表的交付路径仅作来源记录。第一次开发会话必须新增并记录“实际源码根目录”，不得把本机绝对来源路径硬编码进应用或脚本。

## 3. 文档成果与验证

T000 已完成：

- 六个指定文件均为 UTF-8 Markdown，文件名为 `requirements.md`、`agents.md`、`tasks.md`、`current_task.md`、`handoff.md`、`changelog.md`。
- 文档基线统一为 0.1.0；历史基线记录 S01/T010 READY，最新验收已将其更新为 DONE；无后续功能任务成果。
- 38 个任务、27 条需求、13 项验收条件的 ID 与引用核对；任务依赖无环，任务所有权未虚构。
- 重点复核 30 秒计时、旧产品任务取消、本地 TTS 切换、插播游标和三层证据边界。
- 文档包只包含上述六文件，不包含旧资料、代码、日志、模型或凭据。

验证方式是目录清点、UTF-8 解码、引用/依赖一致性检查及人工语义复核；不是程序单元测试，也没有宣称 TikTok 能力可用。详细交付范围见 changelog 0.1.0。

## 4. 已确定决策，不要重新发散

| 决策 | 接续时的处理 |
|---|---|
| Windows 桌面，本地音频为主 | 使用 agents 的 C#/.NET/WPF 基线；先验证目标环境 |
| V1 不修改 OBS，不开发视频 | 音频输出至用户选择的设备；推流由外部工具完成 |
| 基础语音二选一 | TTS 稿或预制片段；主语音唯一，互动准备好才插播 |
| 本地 TTS 引擎可替换 | 首个真引擎 T040，第二个真引擎 T044；不能用两个 mock 宣称交付 |
| 产品链接规则 | n-1 时显示 n；同目标确认显示后每 30 秒续显；新产品使旧计时/响应失效 |
| 关键词规则回复 | 不新增自由 LLM 问答；点赞只监控，暂不生成感谢语音 |
| 平台来源未证实 | 用可替换接口和模拟器开发；真实能力需逐项实测 |
| 单一当前切片 | 主会话维护六文档；并行工作者只做明确分配的子任务和路径 |

K=15、V=10、W=10 秒、候选 TTL、队列大小、效果参数及技术选型是本基线的明确默认值，不是“播个够”实测事实。业务语义以 requirements 为准；发现不合适时记录建议，不在实现中静默改动。

## 5. 未决事项与影响

| 未决事项 | 对应任务 | 影响 | 下一步最小动作 |
|---|---|---|---|
| .NET SDK 10.0.401 x64 复验未检测到（历史阻塞，已解除）；源码根 `E:\Live_audio`，Windows 10 x64 / 19045 | T010，历史 BLOCKED → DONE | PATH 刷新后 restore/build/run/task 全部通过 | 已完成 SDK 安装；最终证据见第 1 节 |
| PC 工具或网页哪个来源可用 | T030 | 阻止选择真实适配器 | 核对当前合法可用入口与账号条件 |
| 四类事件及稳定标识能否取得 | T031 | 阻止保证针对性互动覆盖 | 分项记录真实事件样例和缺失项 |
| 商品/文字操作权限及展示确认 | T032 | 阻止真实自动场控交付 | 在已授权房间验证；未知不当成功 |
| 30 秒实际失效/重显行为 | T032 | 影响续显时间和接口可行性 | 用实际平台版本测量，必要调整须记录需求变更 |
| 本地越南语引擎和目标硬件 | T040、T044 | 阻止真音色、性能、切换验收 | 对明确许可的候选进行安装条件和试听验证 |
| 越南语模板及昵称质量 | T040、T101 | 影响播报自然度 | 使用含变音符号的样句，请用户试听确认 |

T010 的 SDK 缺失曾现场核对并登记 BLOCKED，现已因 SDK 安装和全部验收通过而解除；其他平台事项仍未执行，不能写成已经完成。

## 6. 新 VS Code 会话启动方法

将六文件复制到实际项目根目录，保留这些文件名。`agents.md` 按用户要求为小写；不同 AI 扩展的自动读取规则不同，不要只靠文件名自动生效。可把下面提示放入新会话首条消息：

```text
请先读取本项目根目录的 agents.md、current_task.md、handoff.md、tasks.md，
首次接手时通读 requirements.md，并查看 changelog.md 最近记录。
核对真实目录、Git 状态和已有文件；以最新用户要求及文档为准，不依赖旧聊天猜测。
本次开始执行 current_task.md 中的 S01/T010，完成后更新交接与任务状态。
只做该切片允许的内容，不增加新功能，不启动真实 TikTok 操作或下载模型。
如已有开发成果，先核对再接续，不要重新生成或覆盖。
```

这是供用户开始开发时使用的提示，不是本次生成文档会话的额外执行指令。后续切片更新后，把其中 S01/T010 换为实际当前 ID；其余恢复原则保留。

## 7. 中断时必须更新的状态

主会话在准备中断或接近上下文上限时更新下面各项；未执行写“未执行”，未知写“未知”，不要留下一段“差不多完成”。

```text
更新时间 / 当前 SliceId / Task ID / 状态：
用户最新要求及已获授权的范围：
实际源码根 / 分支 / HEAD / git status 摘要：
本轮已形成的文件与行为：
尚未完成的步骤和准确停止位置：
已执行验证（命令、结果、证据位置）：
未执行验证及原因：
正在运行的进程/服务（用途、PID、如何停止；无则写无）：
工作者/Worktree/Branch/BaseCommit/文件所有权：
未整合提交或补丁与复核状态：
阻塞事实、已尝试替代、最小所需输入：
下一条可执行动作及范围停止点：
```

不在交接中记录令牌、Cookie、账户密码或完整个人评论；使用安全存储引用和去标识化样例。

## 8. 当前未整合成果与工作者交接

当前：S01 最小工程文件已保存在 `E:\Live_audio`，未通过构建和启动，待补齐 SDK 验收；没有可宣称完成的应用成果。无后台应用/开发服务、无工作者、无额外工作树。Git 为 main，尚无提交，所有源码与六文档未跟踪；未 stage、未 push。后续不要覆盖这些未完成文件。`agents.md`、`requirements.md` 内容保持原样，未修改业务需求。

后续工作者完成时给负责人以下内容，不自己修改全局治理文件：

```text
Task ID / Owner / 当前状态（建议 REVIEW）：
分支 / Worktree / BaseCommit / 成果提交或补丁路径：
改动文件及是否全在 AllowedPaths 内：
完成行为与对应 REQ/AC：
精确验证命令和结果，是否使用真实设备/平台：
尚未解决事项 / 共享契约变更请求：
整合注意事项：
```

会话中断后先核对这些工作树和差异，再接管或重派。不要因为工作者暂时不在线就删除其分支、重做相同模块或将任务直接置为 DONE。
