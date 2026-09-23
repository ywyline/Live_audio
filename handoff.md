# 工作区与会话交接

文档基线：0.1.0｜更新时间：2026-09-23｜当前唯一切片：S05 / T040｜状态：ACTIVE。

本文件描述真实现场，不是未来设计。恢复会话必须核对文件和 Git 状态，不能把这里的计划当作已经实现。

## 2026-09-23 main 整合与远程同步

- 用户明确授权合并、提交与推送，并提供提交身份和远程地址；已仅在本仓库配置 Git 身份，origin 为 `https://github.com/ywyline/Live_audio.git`。
- 起点为 main/a2883e7，30 个文件已暂存；本地只有 main 和一个主工作树。`git fetch origin --prune` 退出 0，首次 `git ls-remote --heads origin` 为空；远程没有分支需要合并。
- 成果提交：`0cc962b69140e09ba1b722bd6e42c9540a81a03f`，包含 T020 共享契约、T021 内存夹具与测试、T030/T040 报告和治理文档；仅清理 9 个新增 C# 文件末尾空行，业务与契约语义不变。
- `git push -u origin main` 退出 0，创建远程 main 并建立 origin/main 跟踪；`git ls-remote --heads origin main` 确认远程指向上述成果提交。原 Git 身份及远程地址阻塞已解除。
- 验证沿用同一份源码的本轮结果：`dotnet test TikTokAudio.slnx -c Debug --no-restore` 退出 0（5/5）；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0（0 警告、0 错误）；暂存与工作区差异检查均退出 0。后续仅更新本同步记录，不重复运行无变化的构建/测试。
- 本同步记录作为独立文档提交保存，最终 HEAD 以 `git log -1` 为准；提交后推送并核对本地/远程 HEAD 和干净工作区。此前未 stage/commit/push 的旧日期记录仅为历史。
- 产品任务不变：T020/T021 DONE、T030 BLOCKED、S05/T040 ACTIVE。下一项产品动作仍需补齐 T040 已授权本地越南语引擎、模型/音色版本、许可及运行方式；本轮不开展真实平台或 TTS 操作。

- 本轮只读复核发现现有夹具限制（不在本次 Git 同步中扩展实现）：FakeClock 取消后 pending 项需等推进才清理，取消登记与推进存在资源清理竞态；模拟事件源断开后 channel 已完成，复用重连尚未支持；模拟队列/动作记录无界。5 个现有测试未覆盖这些场景，后续取消、重连和长跑测试前需另行修复/验收，当前提交仅保存阶段成果。

## 1. 一分钟接续信息

**当前验收（2026-09-18）：S05/T040 = ACTIVE。** 基线为 `main` / `a2883e7`，SDK 10.0.401、Host 10.0.12、RID win-x64；本轮未 commit/push。

实际验收：`dotnet restore TikTokAudio.slnx`、T021 专属 `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore`、`dotnet test TikTokAudio.slnx -c Debug --no-restore` 和 `dotnet build TikTokAudio.slnx -c Debug --no-restore` 均退出 0；两次测试均为 5 passed / 0 failed，build 为 0 Errors / 0 Warnings；本轮 `git diff --check` 退出 0（仅 LF/CRLF 提示）。

验收结论：T020 已冻结 Domain 模型、状态/结果枚举、版本修订号、Application 端口及取消语义；T021 已在这些端口之上形成可断言的四类内存测试夹具。`ContractVersion`/`StateSchemaVersion` 为 1.0；`ProductEpoch`、`EngineRevision`、`PlanRevision` 隔离过期副作用；Unknown/Cancelled/Unsupported 不等同成功。

Git 复核：`E:/Live_audio`、`main`、HEAD `a2883e7`、仅一个主工作树；工作区包含 T020 允许路径内的新增源码和四份治理文档修改。未使用 reset/clean，未 commit/push；未创建工作者或启动应用服务。

S01/T010、S02/T020 与 S03/T021 已完成。S04/T030 已形成 `docs/T030-source-report.md`，但仍因缺少授权直播间、测试账号和客户端范围而 BLOCKED。当前已按 READY 顺序启动 S05/T040，先核对本机硬件、引擎许可和本地运行条件。

### T030 交付与阻塞

- 候选报告：`docs/T030-source-report.md`。暂选 TikTok 直播网页入口作为后续验证候选，同时记录 TikTok LIVE Studio 下载入口。
- 只读证据：2026-09-18 两个公开入口 HEAD 均返回 HTTP 200；本机未发现 LIVE Studio 命令、环境配置或常见安装目录。
- 未知能力：`ReadEnter`、`ReadFollow`、`ReadLike`、`ReadComment`、`ReadRoomStatus`、`ShowProduct`、`ReadProductVisibility`、`SendText`、稳定 UserId/EventId、完整度和断线行为均未实测。
- 阻塞：缺少用户授权的测试直播间/账号、非敏感地区范围和客户端/网页版本，不能把公开入口或模拟夹具当成 TikTok 接通证据。
- 解除条件：提供上述最小测试输入后，按能力逐项记录真实事件、登录要求、平台限制和断线行为，再复验并决定首个来源。

### T040 当前执行

- 目标：核对目标 Windows 机器的音频/运行条件和首个真实本地越南语引擎，记录版本、音色、试听样例与 P50/P95 耗时。
- 允许范围：T040 报告、只读硬件/环境检查及明确授权的本地引擎验证；不进入 T041/T042/T044，不执行平台副作用。
- 当前状态：ACTIVE，尚未形成真实引擎验收证据；若缺少引擎、模型或许可，保持 ACTIVE 并记录最小阻塞输入。相关回归测试和 Build 已通过，但不能替代真实越南语试听与 P50/P95。
- 本次复核：64 位、32 位和 OneCore SAPI 仅见 `en-US` 与中文音色，当前用户 SAPI 根不存在；常见离线 TTS 命令、TTS 产品卸载登记和 Python 3.12 指定 TTS 包均未发现。未下载/安装/启动引擎或服务，未调用合成、访问网络或凭据；详见 `docs/T040-engine-report.md`。

### T021 交付

- 允许路径：`tests/TikTokAudio.Application.Tests/`、`TikTokAudio.slnx` 及治理文档；T020 Domain/Application 契约未修改。
- 模拟事件源通过内存 Channel 接收主动注入的规范事件，按注入顺序输出并校验会话/房间；模拟场控记录商品和文字动作，返回可配置的确定性结果。
- `FakeClock` 只在显式 `Advance` 后推进单调时间和 UTC 时间，挂起延迟无需真实等待；`SeededRandomSource` 使用注入 seed，序列可复现。
- 专属测试覆盖事件注入与顺序、场控动作记录、时间推进、相同/不同 seed 序列和无外部连接；测试不需要真实账号、Token、网络、TTS 或音频设备。
- 下一切片：完成 T040 后重新按依赖和优先级判断；T030 仍 BLOCKED，T041 需 T040 DONE 后才可选择。

### T020 交付

- 允许路径：`src/TikTokAudio.Domain/`、`src/TikTokAudio.Application/Contracts/`、`current_task.md`、`tasks.md`、`handoff.md`、`changelog.md`。
- 冻结端口：事件源、场控、TTS、音频输出、播放规划、时钟/随机、状态仓储；模型涵盖规范事件、会话、规则/计划、播放检查点、TTS 资产、商品目标、去重/预留、缓存元数据和动作账本。
- 兼容边界：契约/状态模式 1.0；后续主版本需 Integrator 统一迁移。所有调用方须在外部副作用前核对 SessionId、目标及修订号；取消、Unknown、Unsupported 必须保留原语义。
- 当前工作：T021 已完成并验证；所有真实平台、TTS、音频实现仍未开始。

### 上轮现场与排查记录（历史，不替代以上最新复验）

本轮已获 S01/T010 开发授权，由 Codex 主会话担任整合负责人，在 `E:\Live_audio` 顺序执行；不启动工作者。已完成六文档阅读、环境检查及 Git 初始化，原六文档保留。原文档交付来源见第 2 节，仅作历史来源，不作为源码根目录。

当前发现：Windows 10 专业版 x64（10.0.19045），Git 2.53.0.windows.2，VS Code 1.137.0；当前终端、常见安装目录、注册表和 VS Code SDK 存储均未找到 .NET SDK。`dotnet --info` / `dotnet --list-sdks` 报命令不存在。官方元数据列出 10.0.401，尚未安装验证。构建与窗口验证未执行。

实际 Git：`git init -b main` 退出码 0；`git rev-parse --show-toplevel` 为 `E:/Live_audio`；分支 `main`，尚无 HEAD/BaseCommit；`git worktree list` 仅主目录。初始 `git status --short` 为六份未跟踪文档。无重叠源码、无额外工作树、无后台开发进程。

已创建的未验收文件：`TikTokAudio.slnx`、`Directory.Build.props`、`.gitignore`、`.vscode/tasks.json`；`src/TikTokAudio.{Domain,Application,Infrastructure,Desktop}/` 的四个项目文件；Desktop 的 `App.xaml`、`App.xaml.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`。四层依赖符合 agents；前三层 `net10.0`，Desktop `net10.0-windows` / WPF / x64；nullable 开启；无第三方 PackageReference，无业务服务/功能按钮。应用版本字段暂为 `0.1.0-s01`，并非已构建分发版本。

静态验证：PowerShell `[xml]` 解析 `.slnx`、`.csproj`、`.xaml`、props；`Get-Content .vscode/tasks.json -Raw -Encoding UTF8 | ConvertFrom-Json`；核对项目引用及目标文件存在、无 PackageReference、Nullable=enable。最终退出码均为 0。引用核对脚本首轮因 PowerShell `-join` 与 `-ne` 优先级写法报告错误，拆分比较变量后通过；未因此改动项目引用。`git diff --check` 退出码 0，但全部文件未跟踪，不能作为完整差异或编译验证。

阻塞事实与最小解决方式：常见 SDK 安装目录、注册表、VS Code 存储均未找到工具链。已读取微软官方 `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`，当前发布日为 2026-09-08，候选 SDK 为 10.0.401，Windows x64 ZIP 为 `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip`，官方 SHA-512 为 `24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430`。尚未下载或安装。已询问是否安装到用户目录（无需管理员权限），或提供现有 SDK 路径，尚未收到答复。

未执行：SDK 实测与 `global.json` 固定、`dotnet restore TikTokAudio.slnx`、`dotnet build TikTokAudio.slnx -c Debug --no-restore`、`dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build`、窗口显示/关闭/退出码验证。原因是缺 SDK，不能编译启动；XML 检查不替代构建。Windows Computer Use API 已初始化，可供后续窗口验收使用，但尚未控制/启动任何应用窗口。VS Code 未安装 C# 扩展；本次仅配置 build task，不宣称 F5 调试已验证。

下一条允许动作：后续会话在新授权后从 T030 或 T040 中选择一个最小切片并重新登记；本轮已完成 T021 并停止。新会话先核对已有工程，禁止重复生成。没有 TikTok/TTS/音频设备/真实直播验证，本轮未下载模型、调用平台或发布。

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

当前：S05/T040 在 `E:\Live_audio` 完成硬件/系统条件核对并保持 ACTIVE；真实越南语引擎、模型许可、试听和 P50/P95 证据仍缺失。T020/T021 未提交成果仍保留。无后台应用/开发服务、无工作者、无额外工作树。Git 为 main、HEAD `a2883e7`，未 commit、未 stage、未 push；当前改动包含 T020 源码、T021 测试项目、T040 报告、解决方案登记及治理文档。后续不要覆盖这些未提交文件。`requirements.md` 未修改，业务需求保持原样。

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

## 2026-09-19 T040 verification handoff

- S05/T040 remains ACTIVE. No worker, branch, service, or extra worktree was created; no commit, stage, or push was performed.
- Passed: dedicated tests 5/5, solution tests 5/5, `dotnet build TikTokAudio.slnx -c Debug --no-restore` (0 warnings / 0 errors), and `git diff --check` (0).
- Blocker unchanged: no verifiable Vietnamese engine/model/voice/license is available, so synthesis, listening, and P50/P95 evidence cannot be produced. Minimum input remains an explicitly authorized local engine with model/voice version, license, and run method, or an installed loopback service address, health-check method, and test permission.
- Next allowed action: re-run T040 only after that input arrives; T041/T042/T044 remain disallowed until T040 is DONE. No platform, network, credential, or audio-device side effect was performed.
