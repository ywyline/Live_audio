# 当前唯一执行切片

文档基线：0.1.0｜更新日期：2026-09-16。

## 1. 当前指针

| 字段 | 当前值 |
|---|---|
| SliceId | S01 |
| 主任务 | T010：开发环境确认与最小项目骨架 |
| 状态 | DONE：S01/T010 最终验收通过 |
| 上一成果 | T000：六份文档基线，已完成 |
| 执行负责人 | Codex 主会话（Integrator），单人顺序执行 |
| 活动工作者 | 无 |
| 开发授权状态 | 用户已明确授权仅执行 S01/T010 的建项目、还原、构建、窗口启动验证和常规修复；完成后停止 |
| 实际源码仓库 | `E:\Live_audio`；2026-09-16 在原六文档目录初始化 Git，保留全部已有文件 |
| 当前分支 / HEAD / BaseCommit | `main` / 尚无提交 / 无基线提交（新仓库） |
| 外部平台或模型操作 | 本切片不需要 |

本轮已于环境核对后登记 ACTIVE；因实际缺少 SDK，完成独立文件准备后记录为 BLOCKED。解除 SDK 阻塞后沿用 S01 范围恢复 ACTIVE。已有明确开发授权后，不为每个可逆的常规操作重复询问。

## 2. 唯一目标

在选定 Windows 项目目录建立可从 VS Code 构建的最小 C#/.NET 10 WPF 解决方案，固定工具链边界并验证启动。只完成基础工程，不实现 TikTok、TTS、播放调度或商品功能。

前置：T000 DONE；已有六文档；执行时能确定用户希望作为源码根目录的位置。若用户已在目标 VS Code 项目内发起开发，以实际打开的目录为准，不误用原始附件目录或文档打包目录。

## 3. 允许改动

所有相对路径均相对于 `E:\Live_audio`。最小工程文件及经 SDK 验证后生成的 `global.json` 现已创建。实际路径、Branch、BaseCommit 见第 6 节。

| 范围 | 允许内容 |
|---|---|
| `global.json`、`Directory.Build.props`、必要的 `Directory.Packages.props` | 实测 SDK 固定、nullable、通用构建设置；只加入本切片需要的依赖 |
| `TikTokAudio.slnx`、`.gitignore` | 最小解决方案和生成文件/本地数据忽略规则 |
| `src/TikTokAudio.Domain/TikTokAudio.Domain.csproj` | 领域项目，不引入第三方运行库 |
| `src/TikTokAudio.Application/TikTokAudio.Application.csproj` | 应用项目，仅配置正确依赖方向 |
| `src/TikTokAudio.Infrastructure/TikTokAudio.Infrastructure.csproj` | 基础设施项目与项目引用，不预建具体服务 |
| `src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj`、`App.xaml`、`App.xaml.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs` | 能启动的最小 WPF 窗口与组合入口；不得堆出未实现功能按钮 |
| `.vscode/launch.json`、`.vscode/tasks.json` | 确有需要时添加构建/调试配置；不得覆盖已有用户配置 |
| 六份治理文件 | 负责人仅更新实际状态和环境信息；不变更业务需求 |

若无既有代码，可移除模板自动生成且确认无用户内容的无用 Class1 文件。Git 尚未初始化时，在实际开发指令涵盖建项目的范围内初始化；已有仓库则保留分支和改动，先记录现场。不得创建远程仓库或自动推送。

## 4. 禁止改动与范围停止点

- 不写事件采集器，不访问真实直播间，不读取旧软件的 Cookie、浏览器配置或授权文件。
- 不下载/安装/启动 TTS 模型，不接云端服务，不写业务播放/弹窗逻辑。
- 不添加 NAudio、SQLite、FFmpeg 等尚未使用的实现依赖；这里只确定后续技术基线。
- 不修改 OBS、不做视频、不做收费或授权码体系。
- 不提前执行 T020/T030/T040，不创建工作者会话，不生成空的全功能目录树。
- 不清理文档交付目录之外的旧资料，不覆盖现有业务文件，不以重置仓库解决问题。

若 SDK 缺失、系统不支持或已有源码与计划冲突，记录真实发现和最小解决方式。可完成独立只读检查；受影响动作暂停，不伪造 build 结果，也不擅自更换技术栈。

## 5. 顺序执行与完成条件

以下为验收步骤；本次已执行与未执行结果分别见第 6 节，不能把步骤列表当作通过证据：

1. 按 agents 恢复顺序读文档，确认真实目录及 Git 状态。存在仓库时检查 `git status --short`、`git branch --show-current`、`git rev-parse HEAD`、`git worktree list`；无仓库如实登记。
2. 检查 `dotnet --info`、`dotnet --list-sdks` 和 Windows 版本，记录实际安装结果；确定兼容的 .NET 10 SDK 补丁版本及 Windows 目标。
3. 检查已有项目和配置；只建立缺失的最小工程。Domain/Application 使用纯 .NET 目标，Desktop 使用 Windows/WPF 目标；Windows 音频基础设施的目标框架由项目实际引用决定。
4. 固定 SDK，配置项目引用及忽略规则；只安装实际必要且已授权的工具/依赖。
5. 在项目根目录执行 `dotnet restore TikTokAudio.slnx` 和 `dotnet build TikTokAudio.slnx -c Debug --no-restore`，记录精确命令及退出码。
6. 用 `dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build` 或 VS Code 调试验证窗口可开关、没有启动异常。若环境无法显示窗口，构建证据保留，但图形启动验收记为未完成。
7. 核对 diff 和生成文件未误入源码，更新 tasks、handoff、changelog；只有完成下列条件才将 T010 与 S01 标为 DONE。

完成条件：

- 实际项目根、操作系统、SDK、Git 状态均已记录；没有虚构版本或提交。
- 最小工程引用方向符合 agents，nullable 生效，VS Code 可构建。
- restore/build 成功，WPF 窗口实际启动与关闭经过验证。
- 没有凭证、真实评论或大音频入仓库，没有业务功能越界。
- 本阶段不为模板脚手架写“恒为真”测试；业务测试夹具留给 T021。若用户项目已有测试，只运行受本次改动影响的必要检查。
- 状态、差异、验证与下一切片建议已写入交接。完成本切片即停止本轮实施；后续按用户已有持续开发授权和任务选择规则推进，不把当前范围默默变为全部 V1。

## 6. 分配与证据

| Owner | Task | 状态 | 实际工作区 | Branch / BaseCommit | AllowedPaths |
|---|---|---|---|---|---|
| Codex 主会话（Integrator） | T010 | DONE | `E:\Live_audio` | `main` / 无基线提交 | `E:\Live_audio` 下第 3 节精确文件；无工作者 |

开始时间：2026-09-16 00:48（Asia/Shanghai）。已按顺序读取六文档，确认初始目录仅含六文档，无源码、无 Git 仓库、无重叠改动。`git init -b main` 成功（退出码 0）；根目录为 `E:/Live_audio`，仅一个主工作树，HEAD 尚不存在。

环境：Windows 10 专业版 x64，10.0.19045；PowerShell 5.1.19041.6456；Git 2.53.0.windows.2；VS Code 1.137.0 x64。刷新系统/用户 PATH 后，`C:\Program Files\dotnet\dotnet.exe` 可用。`dotnet --info` 成功识别 SDK 10.0.401、Host 10.0.12、RID win-x64；`dotnet --list-sdks` 输出 `10.0.401 [C:\Program Files\dotnet\sdk]`，此前 SDK 阻塞已解除。

已创建：`global.json`（SDK 10.0.401，rollForward=disable）、`TikTokAudio.slnx`、四层 `.csproj`、Desktop 的 App/MainWindow XAML 与代码、`Directory.Build.props`、`.gitignore`、`.vscode/tasks.json`。未创建具体服务或测试占位。Domain/Application/Infrastructure 为 `net10.0`，Desktop 为 `net10.0-windows` + WPF + x64；Infrastructure 尚无 Windows API，暂不加 Windows TFM。文案集中在 App 资源中，无业务按钮。

已验证：XML/JSON 可解析；四层项目引用符合 agents 3.2 且引用路径存在；无 PackageReference；nullable 配置为 enable。静态核对脚本最终退出码 0；`git diff --check` 退出码 0（新仓库文件未跟踪，此命令不代替源码构建）。

本轮实际尝试的验收命令（工作目录均为 `E:\Live_audio`）：

| 命令 | 结果 |
|---|---|
| `dotnet restore TikTokAudio.slnx` | 退出码 0；四项目全部还原成功 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出码 0；0 Errors，0 Warnings |
| 从 `.vscode/tasks.json` 读取并执行 build task：`dotnet build TikTokAudio.slnx -c Debug` | 退出码 0；0 Errors，0 Warnings |
| `dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build` | dotnet run 宿主与 WPF 进程均启动；主窗口句柄 `0x80468`，标题“TikTok 直播音频工具”，Responding=True；优雅关闭后两进程均退出 |
| PowerShell XML 检查 solution 四项目、全部引用/TFM、Desktop WinExe/UseWPF/x64、StartupUri 及 MainWindow 文件 | 退出码 0，仅静态结构通过 |

此前失败根因是终端 PATH 未刷新；本轮刷新系统/用户环境变量后恢复验证并固定 SDK。所有完成条件已满足，未发生代码侧编译错误或范围外修改。

完成结果：SDK、restore、solution build、VS Code build task、WPF 实际启动/关闭和结构引用验收全部通过。S01/T010 标记 DONE；T020 仍为 TODO，本轮停止，不自动进入。

已登记开始时间、环境结果、变更文件、验证命令/退出码、失败与修复及停止原因；较长历史转入 handoff 或 changelog。本切片已完成并停止。

## 7. 后续切片维护模板

替换当前切片时保留以下字段，不把多个独立当前任务并排追加：

```text
SliceId / 主任务 / 子任务：
状态 / 开始时间 / Owner：
用户授权范围与目标：
前置任务及 DONE 证据：
实际仓库 / Branch / HEAD / BaseCommit：
允许文件 / 禁止范围：
工作者分配（Task、Owner、Worktree、AllowedPaths）：
按依赖顺序的步骤：
完成条件 / 精确验证方式：
当前证据 / 阻塞 / 下一条动作：
本轮停止点：
```
