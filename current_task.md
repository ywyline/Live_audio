# 当前唯一执行切片

文档基线：0.1.0｜更新日期：2026-09-23。

## 2026-09-23 main 整合与远程同步

- 用户明确授权合并、提交与推送，并提供提交身份和远程地址；已仅在本仓库配置 Git 身份，origin 为 `https://github.com/ywyline/Live_audio.git`。
- 起点为 main/a2883e7，30 个文件已暂存；本地只有 main 和一个主工作树。`git fetch origin --prune` 退出 0，首次 `git ls-remote --heads origin` 为空；远程没有分支需要合并。
- 成果提交：`0cc962b69140e09ba1b722bd6e42c9540a81a03f`，包含 T020 共享契约、T021 内存夹具与测试、T030/T040 报告和治理文档；仅清理 9 个新增 C# 文件末尾空行，业务与契约语义不变。
- `git push -u origin main` 退出 0，创建远程 main 并建立 origin/main 跟踪；`git ls-remote --heads origin main` 确认远程指向上述成果提交。原 Git 身份及远程地址阻塞已解除。
- 验证沿用同一份源码的本轮结果：`dotnet test TikTokAudio.slnx -c Debug --no-restore` 退出 0（5/5）；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0（0 警告、0 错误）；暂存与工作区差异检查均退出 0。后续仅更新本同步记录，不重复运行无变化的构建/测试。
- 本同步记录作为独立文档提交保存，最终 HEAD 以 `git log -1` 为准；提交后推送并核对本地/远程 HEAD 和干净工作区。此前未 stage/commit/push 的旧日期记录仅为历史。
- 产品任务不变：T020/T021 DONE、T030 BLOCKED、S05/T040 ACTIVE。下一项产品动作仍需补齐 T040 已授权本地越南语引擎、模型/音色版本、许可及运行方式；本轮不开展真实平台或 TTS 操作。

## 1. 当前指针

| 字段 | 当前值 |
|---|---|
| SliceId | S05 |
| 主任务 | T040：核对目标硬件、引擎许可证和本地运行条件；验证第一个真实越南语引擎、音色与耗时 |
| 状态 | ACTIVE：已确认 T000 DONE 且 T040 为当前最小 P0 READY；按授权范围开展本机核对和真实本地引擎验证 |
| 上一成果 | S04/T030：来源报告已形成但因缺少授权直播间/账号仍为 BLOCKED |
| 执行负责人 | Codex 主会话（Integrator），单人顺序执行 |
| 活动工作者 | 无；共享契约由 Integrator 统一维护 |
| 开发授权状态 | 用户已明确授权继续开发；本切片仅执行 T040 硬件/许可/本地引擎核对和验证，不执行 T041/T042/T044 或平台操作 |
| 实际源码仓库 | `E:\Live_audio`；保留既有工程与用户修改 |
| 当前分支 / HEAD / BaseCommit | `main`（跟踪 origin/main）；成果 0cc962b，最终 HEAD 见 git log -1 / BaseCommit a2883e7 |
| 外部平台或模型操作 | 仅完成本机硬件、系统音色和本地工具只读核对；未下载模型、未访问凭据/Token、未执行云端或直播副作用 |

本轮于 2026-09-18（Asia/Shanghai）启动。按依赖、优先级和 READY 顺序，T030 仍 BLOCKED，T040 为首个可执行切片，现登记 S05/T040 ACTIVE。前置 T000 已 DONE；T040 直接核对本机硬件、引擎许可和本地运行条件，若缺少真实引擎授权则记录阻塞，不伪造试听或耗时证据。

## 2. 唯一目标

核对目标 Windows 机器的音频/运行条件和明确许可的第一个本地越南语 TTS 引擎，记录引擎版本、音色、试听样例、P50/P95 耗时及限制。仅执行 T040，不实现 T041 loopback 适配器、不切换多引擎、不执行平台操作。

前置：T010 DONE（其前置 T000 已完成）；已有六文档和最小工程；实际源码根为 `E:\Live_audio`。

## 3. 允许改动

所有相对路径均相对于 `E:\Live_audio`。最小工程文件及经 SDK 验证后生成的 `global.json` 现已创建。实际路径、Branch、BaseCommit 见第 6 节。

| 范围 | 允许内容 |
|---|---|
| `docs/` 或项目根下的 T040 验证报告文件 | 硬件、引擎许可、本地运行条件、试听样例和耗时证据；不得复制凭据、模型私有路径或原始用户信息 |
| `current_task.md`、`tasks.md`、`handoff.md`、`changelog.md` | 负责人登记 ACTIVE/DONE 或 BLOCKED、范围、验证证据和交接；不改产品需求 |

禁止修改公共包版本、Desktop UI、Infrastructure 实现、真实平台适配器、数据库迁移及 T021 范围外功能；本轮按用户明确授权提交并推送 main 至已配置 origin；不创建额外远程仓库。

## 4. 禁止改动与范围停止点

- 不写事件采集器，不执行真实直播发言/商品动作，不读取旧软件的 Cookie、浏览器配置或授权文件。
- 不下载/安装/启动 TTS 模型，不接云端服务，不写业务播放/弹窗逻辑。
- 不添加 NAudio、SQLite、FFmpeg 等尚未使用的实现依赖；这里只确定后续技术基线。
- 不修改 OBS、不做视频、不做收费或授权码体系。
- 不提前执行 T031/T032/T033/T034/T041/T042/T044 或任何后续任务，不生成空的全功能目录树。
- 不清理文档交付目录之外的旧资料，不覆盖现有业务文件，不以重置仓库解决问题。

若 SDK 缺失、系统不支持或已有源码与计划冲突，记录真实发现和最小解决方式。可完成独立只读检查；受影响动作暂停，不伪造 build 结果，也不擅自更换技术栈。

## 5. 顺序执行与完成条件

以下为 T040 验收步骤；本次已执行与未执行结果分别见第 6 节，不能把步骤列表当作通过证据：

1. 按 agents 恢复顺序读文档，确认真实目录及 Git 状态。存在仓库时检查 `git status --short`、`git branch --show-current`、`git rev-parse HEAD`、`git worktree list`；无仓库如实登记。
2. 检查 `dotnet --info`、`dotnet --list-sdks` 和 Git 状态，记录实际工具链与基线。
3. 只读核对目标硬件、音频端点、系统语音和已存在的本地 TTS 工具，不下载模型或访问凭据。
4. 对照 REQ-TTS-001 和 AC-06，记录引擎、模型、音色、语言、版本、许可、试听样例和 P50/P95 条件。
5. 形成 T040 硬件/引擎报告；若缺少真实越南语引擎或许可则登记阻塞，不伪造试听和耗时。
6. 核对 diff 和生成文件未越界，更新 tasks、handoff、changelog；不得进入 T041/T042/T044。

完成条件：

- 目标硬件、音频端点、本地运行条件和报告字段完整；没有虚构引擎、许可、音色或性能结果。
- 报告明确区分硬件事实、系统语音事实、真实 TTS 证据和未知项。
- 缺少已授权的真实越南语引擎、模型或许可时登记阻塞，记录最小输入，不下载或安装替代品。
- T041/T042/T044 及后续适配器仍未开始。

## 6. 分配与证据

| Owner | Task | 状态 | 实际工作区 | Branch / BaseCommit | AllowedPaths |
|---|---|---|---|---|---|
| Codex 主会话（Integrator） | T040 | ACTIVE | `E:\Live_audio` | `main` / `a2883e7` | `docs/T040-engine-report.md`、本文件及 tasks/handoff/changelog；无工作者 |

开始时间：2026-09-18（Asia/Shanghai）。已按顺序读取治理文档，确认实际根目录 `E:/Live_audio`，当前为 `main`、HEAD `a2883e7`、仅一个主工作树；保留 T020 和 T021 未提交成果，本切片新增 T040 报告并仅修改治理文档。

环境：Windows 10 专业版 x64，10.0.19045；PowerShell 5.1.19041.6456；Git 2.53.0.windows.2；VS Code 1.137.0 x64。刷新系统/用户 PATH 后，`C:\Program Files\dotnet\dotnet.exe` 可用。`dotnet --info` 成功识别 SDK 10.0.401、Host 10.0.12、RID win-x64；`dotnet --list-sdks` 输出 `10.0.401 [C:\Program Files\dotnet\sdk]`，此前 SDK 阻塞已解除。

T040 当前范围：核对本机硬件和已授权的第一个真实本地越南语引擎；不修改 T020 Domain/Application 契约，不创建 T041 适配器、数据库迁移或 UI 功能。

兼容规则：契约主版本 `1.0` 与状态模式版本 `1.0`；同一主版本允许新增可选数据而不改变既有语义，主版本变化需由 Integrator 统一更新调用者和迁移说明。`ProductEpoch` 使旧场控回调失效，`EngineRevision` 隔离旧 TTS 结果，`PlanRevision` 隔离旧基础计划；外部结果为 Unknown 时不得伪造成功或盲目重试。取消是正常结果，调用方必须在副作用前再次核对会话/目标/修订号。

本轮实际尝试的验收命令（工作目录均为 `E:\Live_audio`）：

| 命令 | 结果 |
|---|---|
| `dotnet restore TikTokAudio.slnx` | 退出码 0；解决方案项目均为最新 |
| `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore` | 退出码 0；5 passed，0 failed（相关回归） |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 退出码 0；5 passed，0 failed（相关回归） |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出码 0；0 Errors，0 Warnings |
| `git diff --check` | 退出码 0；仅有 Git 的 LF/CRLF 提示，无 whitespace 错误 |

| 硬件/音频端点/SAPI/本地工具只读核对 | 已完成；发现 Realtek 端点、CPU/GPU，但仅有 `zh-CN`/`en-US` SAPI 音色，无越南语引擎 |

本轮未执行真实越南语 TTS 合成、试听、P50/P95、模型安装或本地音频播放；原因是缺少已授权引擎/模型/许可。未读取 Cookie、Token 或浏览器配置，没有云端或平台副作用。

本轮回归验证：应用测试 5 passed / 0 failed，解决方案测试 5 passed / 0 failed；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出码 0，0 warnings / 0 errors。T040 的真实引擎验收条件仍未满足，因此保持 ACTIVE。

本次复核：枚举 64 位、32 位和 OneCore SAPI 注册表以及当前用户语音根，仍仅发现 `en-US` 与中文音色，没有 `vi-VN`；`Get-Command -All` 和卸载注册表均未发现 Piper、Coqui、eSpeak、Festival、Flite、RHVoice 或其他本地 TTS 产品；Python 3.12 已安装但指定 TTS 包均不存在。未下载、安装、启动引擎或服务，未调用合成、访问网络或读取凭据。详细结果见 `docs/T040-engine-report.md`。

当前结果：T040 已形成 `docs/T040-engine-report.md`，完成目标机器硬件、音频端点、系统音色和本地工具只读核对；因缺少真实越南语引擎/模型/许可，S05/T040 保持 ACTIVE 并记录最小解除条件；T020/T021 原有未提交成果保持存在。

已登记开始时间、环境结果、变更文件、验证命令/退出码、阻塞事实和停止原因；下一步需补齐 T040 最小引擎输入后复验，本轮不进入 T041/T042/T044。

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

## 2026-09-19 verification update

- Read-only recheck confirmed SDK 10.0.401, Windows 10 x64 19045, and the existing Realtek audio endpoints; no `vi-VN` voice, local Vietnamese engine, model/license, or loopback service was found.
- Dedicated tests and solution tests each exited 0 with 5 passed / 0 failed. `dotnet build TikTokAudio.slnx -c Debug --no-restore` exited 0 with 0 warnings / 0 errors; `git diff --check` exited 0.
- No synthesis, listening, P50/P95 measurement, model installation, network TTS, credential access, or platform side effect was performed. T040 remains ACTIVE; the stop point is still waiting for an explicitly authorized local engine and model/voice version, license, and run method.
