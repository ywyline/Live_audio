# 可集成成果与版本记录

文档基线：0.1.0｜记录日期：2026-09-23。

本文件只记录已经形成可集成成果的开发/文档变更。进行中过程、失败尝试和待整合工作者成果放在 handoff；每轮实施都要更新交接，但没有成果时不能制造一条“已完成”历史。

当前版本 0.1.0 是文档基线版本，不是可运行应用版本。后续条目区分“文档版本”和“应用版本”；两者不必同步增长。新记录置于最上方，保留旧记录，不改写历史结果。

## 2026-09-23 — main 首次提交成果并同步远程

- 已提交成果 `0cc962b69140e09ba1b722bd6e42c9540a81a03f`：T020 共享契约、T021 测试夹具及 T030/T040 报告和治理记录，30 个文件；基线 a2883e7。
- 使用用户提供的本仓库 Git 身份和 origin；远程初始无分支。`git push -u origin main` 退出 0，远程 main 与成果提交一致，已设置跟踪分支，无强制推送。
- 验证：5/5 测试通过，构建 0 警告/0 错误，暂存及工作区格式检查退出 0。当前文档更新单独提交并同步，实际最终 HEAD 以 Git 历史为准。
- 原提交身份和远程地址阻塞已解除；T030 平台验收与 T040 本地真实引擎验收仍未完成，产品任务状态不变。

## 2026-09-23 — 现有成果回归验证与格式检查通过

- 关联成果：T020/S02、T021/S03 已验收代码，T030/S04 与 T040/S05 现有验证报告；当前产品切片仍为 S05/T040 ACTIVE。
- 按用户授权，复核原暂存的 30 个文件，基线 main/a2883e7；没有其他本地分支需要合并。清理 9 个新增 C# 文件末尾空行，业务实现和契约语义不变。
- 验证：`dotnet test TikTokAudio.slnx -c Debug --no-restore` 退出 0（5/5）；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0（0 警告、0 错误）。
- 更新 current_task/tasks/handoff 的本轮授权与同步记录；旧日期的未提交记录作为历史保留。暂存差异检查通过，形成已验证文件成果，尚无新 Git 提交。
- 提交因缺少 user.name/user.email 退出 128；远程亦未配置，尚未推送。下一项同步动作是取得提交姓名、邮箱和目标仓库 URL 后提交、核对远程历史并正常推送。未进行真实平台、TTS 或音频设备验证，不改变 T030 BLOCKED 或 T040 ACTIVE 状态。

## 2026-09-18 — T040 / S05 本地引擎条件核对记录（阻塞）

- 关联任务/切片：T040 / S05，ACTIVE；整合负责人：Codex 主会话；基线 `a2883e7`；无 commit/push（按用户要求保留工作区改动）。
- 交付物：新增 `docs/T040-engine-report.md`，记录目标机器硬件、音频端点、系统语音、已存在的本地工具和 T040 的最小解除条件。
- 只读证据：Acer Nitro AN515-54；Intel i7-9750H，6 核/12 线程；GTX 1660 Ti；Realtek 播放/录音端点状态 OK；Windows SAPI 仅有 `zh-CN` Huihui 与 `en-US` Zira 音色。
- 阻塞：未发现越南语音色、Piper/Coqui/eSpeak/Python TTS 包、本地 TTS 服务或可核对的引擎/模型许可证；未生成音频、试听或 P50/P95，保持 ACTIVE，不提前进入 T041。
- 限制：未下载模型、安装软件、访问凭据或调用云端 TTS；硬件核对不构成越南语引擎可用证据。
- 回归验证：应用测试 5 passed / 0 failed，解决方案测试 5 passed / 0 failed；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 为 0 warnings / 0 errors；`git diff --check` 退出 0（仅 LF/CRLF 提示）。这些结果不替代真实引擎试听和 P50/P95。
- 下一动作：提供已授权的本地越南语引擎及模型/音色版本、许可证和运行方式后复验 T040；本轮停止。

## 2026-09-18 — T030 / S04 来源核对报告形成，等待授权条件

- 关联任务/切片：T030 / S04，BLOCKED；整合负责人：Codex 主会话；基线 `a2883e7`；无 commit/push（按用户要求保留工作区改动）。
- 交付物：新增 `docs/T030-source-report.md`，比较 TikTok 直播网页入口与 LIVE Studio PC 入口，并记录来源选择、操作入口、验证方法、限制和解除阻塞所需的最小输入。
- 验证：公开入口 `https://www.tiktok.com/live` 与 `https://www.tiktok.com/studio/download` 均返回 HTTP 200；本机未发现 LIVE Studio 命令、相关环境变量或常见安装目录；未读取 Cookie、Token 或浏览器配置，也未执行直播评论、商品或文字操作。
- 阻塞：缺少获授权的测试直播间/账号、非敏感地区范围及客户端/网页版本，尚未取得四类事件、稳定 ID、房间状态、商品展示确认或文字发送能力的真实平台证据；不进入 T031/T032。
- 限制：本条只记录来源报告和安全只读核对，不代表 TikTok 已接通，不实现真实适配器或平台副作用。
- 下一切片：补齐 T030 最小授权输入后复验；若继续缺少平台授权，则由后续会话单独评估 T040 的条件，本轮停止。

## 2026-09-18 — T021 / S03 模拟测试夹具与自动化测试验收完成

- 关联任务/切片：T021 / S03，DONE；整合负责人：Codex 主会话；基线 `a2883e7`；无 commit/push（按用户要求保留工作区改动）。
- 测试基础设施：新增 `SimulatedLiveEventSource`、`SimulatedLiveRoomController`、`FakeClock` 和 `SeededRandomSource`，全部使用内存状态；事件源支持主动注入并保持顺序，场控记录商品/文字动作，时钟由显式推进控制，随机序列由 seed 决定。
- 测试项目：新增 `tests/TikTokAudio.Application.Tests/` 并纳入 `TikTokAudio.slnx`；5 个 T021 专属测试覆盖事件注入顺序、场控行为记录、时间推进、随机可复现/分歧及无外部连接。
- 验证：`dotnet restore TikTokAudio.slnx`、专属 `dotnet test`、`dotnet test TikTokAudio.slnx -c Debug --no-restore`、`dotnet build TikTokAudio.slnx -c Debug --no-restore` 和 `git diff --check` 均退出 0；两次测试均为 5 passed / 0 failed，build 为 0 Errors / 0 Warnings。
- 限制：未连接真实 TikTok、账号、Token、网络、TTS 或音频设备；未实现 T022 或其他后续业务功能。
- 下一切片：T030 或 T040，待后续授权选择；本轮停止。

## 2026-09-17 — T020 / S02 共享契约冻结并验收完成

- 关联任务/切片：T020 / S02，DONE；整合负责人：Codex 主会话；基线 `a2883e7`；无 commit/push（按用户要求保留工作区改动）。
- 领域契约：新增 `ContractVersion`/`StateSchemaVersion`、`PlanRevision`、`EngineRevision`、`ProductEpoch`，规范事件、会话、播放检查点、音频资产、TTS、商品目标、去重/预留/缓存/动作账本模型及状态/结果枚举。
- 应用端口：冻结 `ILiveEventSource`、`ILiveRoomController`、`ITtsProvider`、`IAudioOutput`、`IPlaybackPlanner`、`IClock`、`IRandomSource`、`IStateStore`；所有外部异步动作带 `CancellationToken`，TTS 明确返回 `TtsSynthesisOutcome`。
- 兼容语义：契约和状态模式当前为 `1.0`；主版本变更需 Integrator 统一更新调用者/迁移。`ProductEpoch`、`EngineRevision`、`PlanRevision` 隔离过期副作用；Unknown/Cancelled/Unsupported 不等同成功，取消为正常结果。
- 影响文件：`src/TikTokAudio.Domain/`、`src/TikTokAudio.Application/Contracts/` 及 `current_task.md`、`tasks.md`、`handoff.md`、`changelog.md`。
- 验证：`dotnet restore TikTokAudio.slnx` 退出 0；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0（0 Errors / 0 Warnings）；`dotnet test TikTokAudio.slnx --no-restore` 退出 0（当前无测试项目）；契约静态核对与 `git diff --check` 退出 0。
- 限制：未连接真实 TikTok、TTS、音频设备；未实现 T021 模拟夹具、真实适配器、播放调度、数据库或 UI 功能。
- 下一任务：T021，建立模拟事件源、模拟场控、可控时钟和确定性随机夹具；本轮不提前执行。

## 2026-09-16 — T010 / S01 最小工程最终验收完成

- 关联任务/切片：T010 / S01，DONE；整合负责人：Codex 主会话。
- SDK：刷新系统/用户 PATH 后确认 `C:\Program Files\dotnet\dotnet.exe`；.NET SDK 10.0.401、Host 10.0.12、RID win-x64。新增 `global.json`，固定 SDK 10.0.401 且 `rollForward=disable`。
- 验证：`dotnet restore TikTokAudio.slnx` 退出 0；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0，0 Errors / 0 Warnings；读取并执行 `.vscode/tasks.json` 的 `dotnet build TikTokAudio.slnx -c Debug` 退出 0，0 Errors / 0 Warnings。
- WPF：`dotnet run --project src/TikTokAudio.Desktop/TikTokAudio.Desktop.csproj --no-build` 实际启动；窗口标题“TikTok 直播音频工具”、主窗口句柄有效、Responding=True；优雅关闭后应用和 dotnet run 宿主均退出。
- 结构：四层项目、项目引用、TFM、WinExe/UseWPF/x64、StartupUri 静态核对通过。未加入第三方包、业务服务或超出 T010 范围的功能。
- 状态：已同步 `current_task.md`、`tasks.md`、`handoff.md`、`changelog.md` 为 S01/T010 DONE；T020 保持 TODO，尚未开始。本轮未执行 TikTok、TTS、音频业务或真实平台操作。

## 2026-09-16 — T010 环境核对与 Git 初始化记录

后续验证记录（2026-09-16 01:02）：重新检查当前/注册 PATH、标准及用户目录、安装注册表，仍无 dotnet.exe。已实际尝试 restore、build、run 和 VS Code build task 对应命令，均因 CommandNotFoundException 退出 1；编译器与应用未启动。四层结构/引用、TFM 和 WPF 启动配置静态复核通过。S01/T010 保持 BLOCKED，已同步四份状态文档；本次没有新的已验收代码成果，没有修改既有架构。唯一解除动作是完成官方 .NET SDK 10.0.401 x64 安装。T020 仍为 TODO，尚未开始。详细命令和结果见 current_task 第 6 节及 handoff 第 1 节。

- 关联任务/切片：T010 / S01，BLOCKED；本条仅记录已核实的环境与治理文档成果，不表示最小应用已完成。
- 文档基线：0.1.0，业务需求未变；应用版本：尚无已构建版本。
- 实际源码根：`E:\Live_audio`；初始仅含六文档，`git init -b main` 退出码 0，现为 main 分支、单一工作树、尚无 HEAD/BaseCommit/整合提交。原文件保留，未创建远程仓库或推送。
- 环境：Windows 10 专业版 x64（10.0.19045）、PowerShell 5.1.19041.6456、Git 2.53.0.windows.2、VS Code 1.137.0 x64。`dotnet --info` / `dotnet --list-sdks` 因命令不存在失败；常见目录与安装注册表未发现 SDK。
- 已更新 tasks/current_task/handoff 的实际负责人、范围、现场和阻塞。最小工程文件已准备、静态结构核对通过；尚未构建的代码明细及排查记录留在 handoff，不记为已完成应用成果。
- 未执行 restore/build、窗口启动/关闭及业务测试。最小解除条件：允许安装官方 .NET 10.0.401 x64 SDK 到用户目录，或提供已有 SDK 路径；确认已提出，尚未答复。实际安装验证前不伪造 `global.json` 的可用版本。
- 下一步仅恢复 S01 验收；T020 尚未开始。无共享契约、数据库、用户数据或平台操作变更。

## 0.1.0 — 2026-09-16 — 六文档需求与开发基线

- 关联任务：T000，DONE。
- 成果类型：文档，可直接放入项目根目录；应用尚未开发。
- 文档版本：0.1.0；应用版本：无。
- 整合提交：无，本次交付工作区不是 Git 仓库。

### 形成的成果

| 文件 | 用途 |
|---|---|
| requirements.md | 唯一产品真相；功能、默认值、异常语义和 AC-01…AC-13 验收 |
| agents.md | 架构、编码、测试、范围、并行所有权与 AI 决策边界 |
| tasks.md | 完整任务树、依赖、优先级、状态、需求追溯和未来并行波次 |
| current_task.md | 唯一下一执行切片 S01/T010，READY，尚未启动 |
| handoff.md | 实际交付现场、未决事项、新会话启动提示及中断交接模板 |
| changelog.md | 可集成成果记录与未来条目规范 |

本次需求具体化：

- 将“弹窗”定义为平台商品讲解卡/链接。n-1 开始时展示 n；同目标在上次确认展示成功后每 30 秒续显，切品取消旧任务，用 ProductEpoch 隔离延迟回调。
- 明确 `@[n]` 的播放边界、手动覆盖与音频联动/独立顺序模式，防止用户昵称和评论触发控制命令。
- TTS 改为本地可切换引擎；首引擎与第二真实引擎分别验收，旧生成结果不能污染新引擎队列。
- 基础模式互斥，互动音频准备好才暂停基础语音，播放后恢复源位置、产品/组/片段及效果参数。
- 定义 K/V/W、事件和用户去重、有界队列、关键词语音/文字独立规则、异常及崩溃后的未知状态处理。
- 固定 V1 范围，采用 C#/.NET 10/WPF 等待验证的工程基线，OBS 保持外部工具；平台能力不预设可用。
- 规定一个当前切片、一个整合负责人、独立工作树和互斥文件所有权，支持新会话接续及并行模块开发。

### 验证与限制

已完成文档结构检查：六文件名、UTF-8、版本/状态一致性、38 个任务唯一性、依赖引用及无环检查、27 条 REQ 与 13 项 AC 引用、压缩包清单与内容一致性；人工复核上述核心规则。

未执行应用编译、音频/TTS 测试、TikTok 真实房间接入、商品展示或文字发送。未安装工具、模型和驱动，未创建源码仓库。30 秒平台隐藏规则来自用户描述，真实行为仍待 T032 验证。

影响范围：六份文档及同内容文档包。无数据库迁移，无用户数据变更，无应用 API 变更，无发布行为；原有工作资料保留。

## 后续成果记录模板

复制下列字段形成新条目，填写真实值；不要把模板本身当作一次开发成果。

```text
日期与标题：
成果类型：文档 / 已整合代码 / 本地分发包
关联 Task ID / SliceId：
文档版本 / 应用版本：
基线提交 / 成果提交 / 整合提交（没有则说明）：
具体问题与变更后的行为：
影响文件和模块：
关联 REQ / AC：
共享契约变更及调用者影响：
数据/配置迁移和兼容性（无则写无）：
验证命令、退出结果、证据路径：
本次证据层级：模拟 / 本地真引擎设备 / 真实平台
未执行验证、原因与已知限制：
恢复方式（如适用，不能使用破坏性重置覆盖用户修改）：
剩余工作及下一任务（不冒充已完成）：
```

工作者提交但未整合的代码先登记 REVIEW 和 handoff。负责人验证并整合后才记录为 DONE；失败或中断的实现继续保留在当前切片，不通过变更日志掩盖缺口。

## 2026-09-19 | S05/T040 verification update

T040 remains ACTIVE. A read-only recheck confirmed SDK 10.0.401, the target Windows environment, and the existing audio endpoints. Dedicated and solution tests each passed 5/5; the solution build completed with 0 warnings and 0 errors. No authorized Vietnamese local engine, model/voice, license, synthesis sample, listening result, or P50/P95 evidence is available, so the task cannot be marked DONE. No model was downloaded, no service or network TTS was called, and no platform side effect occurred. The next allowed action is to supply the minimum authorized engine input documented in `docs/T040-engine-report.md`.
