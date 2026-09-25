# 项目地图：任务、依赖与并行计划

Document baseline: 0.1.0 | Requirements: 0.1.1 | Updated: 2026-09-25 | Current slice: S24 / T090 | Status: DONE.

T081 is complete. T044 remains DEFERRED. S23/T081 is DONE; READY means dependencies are satisfied but does not authorize unrelated work. See current_task.md and handoff.md for the stop condition.

## 2026-09-23 main 整合与远程同步

- 用户明确授权合并、提交与推送，并提供提交身份和远程地址；已仅在本仓库配置 Git 身份，origin 为 `https://github.com/ywyline/Live_audio.git`。
- 起点为 main/a2883e7，30 个文件已暂存；本地只有 main 和一个主工作树。`git fetch origin --prune` 退出 0，首次 `git ls-remote --heads origin` 为空；远程没有分支需要合并。
- 成果提交：`0cc962b69140e09ba1b722bd6e42c9540a81a03f`，包含 T020 共享契约、T021 内存夹具与测试、T030/T040 报告和治理文档；仅清理 9 个新增 C# 文件末尾空行，业务与契约语义不变。
- `git push -u origin main` 退出 0，创建远程 main 并建立 origin/main 跟踪；`git ls-remote --heads origin main` 确认远程指向上述成果提交。原 Git 身份及远程地址阻塞已解除。
- 验证沿用同一份源码的本轮结果：`dotnet test TikTokAudio.slnx -c Debug --no-restore` 退出 0（5/5）；`dotnet build TikTokAudio.slnx -c Debug --no-restore` 退出 0（0 警告、0 错误）；暂存与工作区差异检查均退出 0。后续仅更新本同步记录，不重复运行无变化的构建/测试。
- 本同步记录作为独立文档提交保存，最终 HEAD 以 `git log -1` 为准；提交后推送并核对本地/远程 HEAD 和干净工作区。此前未 stage/commit/push 的旧日期记录仅为历史。
- 产品任务不变：T020/T021 DONE、T030 BLOCKED、S05/T040 ACTIVE。下一项产品动作仍需补齐 T040 已授权本地越南语引擎、模型/音色版本、许可及运行方式；本轮不开展真实平台或 TTS 操作。

## 1. 状态与完成门槛

| 状态 | 定义 |
|---|---|
| TODO | 尚未开始，依赖未全部完成 |
| READY | 前置任务完成，可选择进当前切片；开始前仍须检查环境和所需授权 |
| ACTIVE | 已登记负责人、范围、工作区和执行步骤，正在做 |
| REVIEW | 工作者已提交成果，等待整合与验收，不算完成 |
| BLOCKED | 已尝试当前任务后被具体条件阻止，必须记录原因及解除条件 |
| DEFERRED | 用户明确暂缓；保留接口与验收，恢复须重新安排，不计为完成 |
| DONE | 已形成可集成成果，负责人完成相应验证并更新记录 |

优先级：P0 是正确运行/接入可行性；P1 是完整 V1 必需的配置、效果与交付。P1 不是可随意删掉的功能。状态只能根据证据推进；模拟测试通过不能把真实接入任务标为 DONE。

任务进入 ACTIVE 前，在 current_task 登记 Owner、依赖证据、AllowedPaths、验收方式。任务完成后在本表更新状态，在 changelog 引用实际成果和验证，在 handoff 删除已解决阻塞。没有分配的任务 Owner 均为“未分配”，不得推断存在后台工作者。

## 2. Task Tree

```text
V1 Windows TikTok 直播音频工具
├─ 治理与基础：T000 → T010 → T020 → T021
├─ 平台能力：T030 → (T031, T032) → (T033, T034, T071)
├─ 本地 TTS：T040 → T041 → T042 → T043 → T044
├─ 基础语音：T050 / T051 → T052 → T053 → (T054, T055)
├─ 事件互动：T060 → T061 → T062
├─ 场控：T070 / T071 → T072；T073 → T074
├─ 数据恢复：T080 → T081 → T082
├─ 桌面界面：T090 → T091
└─ 验收交付：T100 → T101 → T102 → T103 → T104
```

树只展示分组和主要顺序；完整依赖以以下任务表为准，逗号表示所有前置任务都必须完成。

## 3. 完整任务索引

### 3.1 治理、契约与验证基础

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T000 | 建立六文档基线；校验交叉引用并生成文档包 | 无 | P0 | DONE | 六文件齐全；见 changelog 0.1.0 |
| T010 | 检查目标机器、确定项目根目录、建立最小 .NET/WPF 解决方案及构建配置 | T000 | P0 | DONE | Owner：Codex 主会话（Integrator）；`E:\Live_audio`；SDK 10.0.401 x64、restore/build/run、VS Code build task 和 WPF 窗口验收通过；详见 current_task 第 6 节、handoff 第 1 节 |
| T020 | 冻结共享接口、结果类型、状态模型、时钟、版本号和取消语义；记录兼容规则 | T010 | P0 | DONE | Owner：Codex 主会话（Integrator）；Domain/Application 契约已编译；状态仓储覆盖规则、计划、会话、去重、缓存元数据和动作账本；兼容规则、ProductEpoch/EngineRevision/PlanRevision 和取消语义见 current_task/handoff |
| T021 | 建立模拟事件源、模拟场控、可控时钟和确定性随机测试夹具，隔离真实外部操作 | T020 | P0 | DONE | Owner：Codex 主会话（Integrator）；四类内存夹具已实现，事件注入顺序、场控行为、时间推进和 seed 复现均有专属测试；专属测试、解决方案测试和 build 通过 |

### 3.2 平台验证与事件接入

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T030 | 比较授权条件下的 PC 工具/网页来源，选择首个来源；形成来源、限制和操作入口报告 | T000 | P0 | BLOCKED | 报告：`docs/T030-source-report.md`；缺少授权直播间/测试账号与客户端范围，公开入口核对不足以完成 REQ-SRC-001/002 |
| T031 | 验证四类事件、稳定 UserId/EventId、房间状态、缺失范围和重放行为 | T030 | P0 | TODO | AC-13 事件部分；明确哪些只能部分获取 |
| T032 | 验证商品 ID、展示确认、30 秒消失及重复展示、发文字权限和结果查询 | T030 | P0 | TODO | AC-13 场控部分；读取和写入能力分别结论 |
| T033 | 按冻结契约实现一个真实事件适配器、能力报告和规范事件转换 | T020, T031 | P0 | TODO | REQ-SRC-001/002；去标识化事件样本可重放 |
| T034 | 登录失效、重连、房间变化与会话绑定；避免多源重复订阅 | T021, T033 | P0 | TODO | REQ-SRC-003；AC-01/11 的源生命周期部分 |

T030–T032 的报告必须列出日期、账号/地区/客户端版本的非敏感范围、验证方法、真实结果和未知项。若某必需能力无法获得，登记 BLOCKED 或明确的能力缺口，交由用户决定产品降级；不能改成“点击按钮成功即验收”。没有实测条件时先保留模拟链路，继续已分配的独立本地工作。

### 3.3 本地越南语 TTS

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T040 | 核对目标硬件、引擎许可证和本地运行条件；验证第一个真实越南语引擎、音色与耗时 | T000 | P0 | DONE | VieNeu 桌面0.18.3/SDK3.8.3已部署；14模型文件哈希通过；五条禁网合成与本机API合成/鉴权通过，P50 2.5368秒/P95 5.3269秒（小样本）；2026-09-24用户确认当前样音符合要求；首引擎验收完成，见 docs/T040-engine-report.md；后续T041已完成 |
| T041 | 首个本地 loopback HTTP 引擎适配器：健康、音色、合成、取消、错误 | T020, T040 | P0 | DONE | REQ-TTS-001；仅本机请求，失败不转云端；引擎独立部署，适配器仅调用本机接口，不向 Live_audio EXE 嵌入引擎/推理环境/模型；Owner Integrator；50项模拟及真实C#合成通过，见docs/T041-adapter-report.md |
| T042 | UTF-8 TXT 分段、标记边界、音频缓存、预生成与取消；失败缓存不命中 | T021, T041 | P0 | DONE | REQ-TTS-002/003/004；Owner Integrator；新增122项/全177项通过；导入/边界元数据/文件缓存/取消预生成完成，见docs/T042-preparation-report.md；实际播放边界仍属T055 |
| T043 | 多引擎配置与切换，EngineRevision 隔离旧结果；当前音频播放完毕再换 | T042 | P0 | DONE | Owner：Integrator；S22；配置装配、registry-aware 预生成代际隔离与竞态证据已完成；专属35/35、配置2/2、Application567/567、Integration385/385，Build零警告错误；验收报告：`docs/T043-engine-switching-report.md` |
| T044 | 接入第二个真实本地引擎并完成往返切换、越南语试听、离线测试 | T040, T043 | P0 | DEFERRED | 用户2026-09-24明确暂缓，保留接口，恢复需重新安排；AC-06 完整引擎部分；两套模拟器不算完成；两个引擎均独立部署，记录同一 Live_audio 产物通过配置往返切换的实测证据 |

T040/T044 若缺少硬件、模型或授权，记录所需最小输入；不得为“完成切换”强行使用云服务。引擎下载、安装、启动及模型来源以执行时获得的具体授权为准。

### 3.4 播放、素材与音效

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T050 | 音频设备枚举、解码、单一主语音、源采样游标、暂停/恢复/停止、必要缓存转码 | T020 | P0 | DONE | REQ-AUD-001/003；Owner Integrator；新增43项/全224项通过、build零警告错误；Realtek真实播放/暂停/源定位/恢复/停止18.5297ms通过，见docs/T050-audio-report.md |
| T051 | 产品/子组目录导入、数字排序、素材校验、产品映射预检查 | T020 | P0 | DONE | REQ-PRE-001/CTRL-001；Owner Integrator；专属45/45、全269/269通过；build零警告错误；数字排序、空组/坏文件/重复编号/映射集中预检与产品阻止，见docs/T051-import-report.md |
| T052 | 分组顺序、独立洗牌袋、循环、产品进入事件、计划修订号与恢复游标 | T021, T051 | P0 | DONE | Owner Integrator；AC-04逻辑恢复/AC-05模拟层；专属66/66、全335/335通过，build零警告错误；见docs/T052-planner-report.md |
| T053 | 基础/互动音频调度、准备后抢占、优先级、恢复、暂停/紧急停止 | T021, T050, T052 | P0 | DONE | Owner Integrator；docs/T053-scheduler-report.md；AC-03/04/11本地调度逻辑，真实设备指标留T101 |
| T054 | 可关闭的随机速度/音调/声道/EQ/环境音、效果种子保持、削波保护和试听 | T053 | P1 | DONE | Owner Integrator；S17；确定性效果/源帧映射/旁路/削波保护/预览已实现；专属28/28、全580/580、Build零警告错误；用户2026-09-25明确“这个问题先通过，继续后续开发”，接受当前听感并保留机器感限制；见 docs/T054-effects-progress.md |
| T055 | 将 TXT 音频接入基础计划，实现模式互斥、循环/一次播放、一次性标记 | T042, T053 | P0 | DONE | Owner Integrator；REQ-TTS-002/004及AC-03/04/09应用逻辑；专属51/51、全449/449通过，build零警告错误；docs/T055-tts-playback-report.md |

### 3.5 事件规则与互动插播

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T060 | 事件去重、每场用户去重、Reserved/Completed、缺少 ID 的降级与重连状态 | T021 | P0 | DONE | Owner Integrator；REQ-EVT-001及AC-01应用层逻辑；专属11/11、全460/460通过，build零警告错误；docs/T060-event-dedup-report.md |
| T061 | K/V/W、TTL、有界队列、模板、关键词库匹配和语音/文字独立决策 | T060 | P0 | DONE | Owner Integrator；专属84/84、全方案544/544，Build零警告错误；单调时钟、场次/租约隔离、独立渠道、多模板与有界释放验收通过；docs/T061-interaction-rules-report.md |
| T062 | 互动决策接本地 TTS 与调度器，完成后写成功记录，失败释放占位 | T041, T053, T061 | P0 | DONE | Owner Integrator；新增互动接线协调器及专属测试；慢合成、取消、连续关注、欢迎替换；docs/T062-interaction-playback-report.md |

### 3.6 商品弹窗与文字场控

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T070 | 商品目标状态机、单串行控制通道、30 秒续显、ProductEpoch、覆盖/取消/未知结果 | T021 | P0 | DONE | Owner Integrator；S18；专属65/65、相关76/76、全645/645，Build零警告错误；AC-07/08/11应用模拟层；0/30/60/80/90秒、非合作取消、超时读回、恢复隔离通过；docs/T070-product-control-report.md |
| T071 | 经验证的真实商品/文字控制适配器；动作结果 Confirmed/Rejected/Unknown/Unsupported | T020, T032 | P0 | TODO | REQ-SRC-002、REQ-CTRL-003；不将页面点击等同成功 |
| T072 | 联动 n-1 与商品 n、平台 ID 映射、TXT 标记/手动覆盖、互斥循环模式 | T070, T071, T052, T055 | P0 | TODO | REQ-CTRL-001…003；AC-07/08/09 |
| T073 | 定时文字、关键词文字决策、有界发送队列、总间隔、结果账本与过期清理 | T021, T061 | P0 | DONE | Owner Integrator；S19；专属127/127、相关219/219、全772/772，Build零警告错误；REQ-CTRL-004/AC-10应用模拟层；文字/语音租约隔离、未来定时与限流、Unknown和有界账本；docs/T073-text-dispatch-report.md |
| T074 | 文字调度接真实控制器，确认/未知结果处理、断线暂停与重连不补发 | T071, T073 | P0 | TODO | AC-10/13 文字部分 |

### 3.7 数据、恢复与控制台

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T080 | SQLite 仓储、版本化迁移、缓存元数据、凭证边界、日志轮转、开发数据隔离 | T020 | P0 | DONE | Owner Integrator；S20；REQ-DATA-001；专属137/137、全909/909，Build零警告错误，依赖审计通过；只读坏库预检/迁移回滚/路径日志与fake凭证验证；docs/T080-persistence-report.md |
| T081 | Local playback checkpoints, shuffle bags, deduplication and engine configuration recovery; preview only after crash | T043, T055, T062, T080 | P0 | DONE | Owner: Integrator; S23; dedicated Application 4/4, Integration 3/3, related Application 571/571, Integration 388/388, build 0 warnings/0 errors; docs/T081-recovery-report.md |
| T082 | 场控动作恢复、连接状态核对、未知动作不重放、停止后的旧回调失效 | T034, T072, T074, T081 | P0 | TODO | AC-08/10/11 平台生命周期部分 |
| T090 | Local console: base mode, material, engine, device, effects, progress and stop operations | T010, T043, T054, T055 | P1 | DONE | Owner: Integrator; S24; dedicated 4/4, solution Application 571/571 + Integration 388/388 + Desktop 4/4, Build 0 warnings/0 errors, Windows startup/close smoke passed; docs/T090-desktop-console-report.md |
| T091 | 直播控制台：能力、事件、K/V/W、规则、商品映射/倒计时、定时文字、日志和故障 | T034, T062, T072, T074, T080, T090 | P1 | TODO | REQ-UI-001 完整；不支持的能力不能假启用 |

### 3.8 集成与交付

| ID | 任务 / 必须形成的成果 | 前置 | 优先级 | 状态 | 验收依据 |
|---|---|---|---|---|---|
| T100 | 离线模拟整链回归：事件、模式、抢占、弹窗、文字和持久化，覆盖竞争及故障 | T021, T055, T062, T070, T073, T080 | P0 | DONE | Owner Integrator；S21；专属40/40、全949/949，Build零警告错误、静态检查通过；AC-01…05、07…11仅模拟层，完整恢复/真实指标另验；docs/T100-offline-regression-report.md |
| T101 | 目标机器真实本地 TTS/音频集成验收，含双引擎、延迟、恢复误差及人工试听 | T044, T054, T081, T090, T100 | P0 | TODO | AC-03/04/06；记录硬件与真实耗时，不混入平台结论 |
| T102 | 授权测试房间整场联调，验证四类事件和商品/文字副作用，记录能力边界 | T032, T091, T082, T101 | P0 | TODO | AC-07…11/13 的平台层证据 |
| T103 | 目标机器 4 小时连续运行，记录负载、内存、队列峰值、音频中断和平台错误 | T101, T102 | P0 | TODO | AC-12；模拟事件长跑与真实平台观察分别标记 |
| T104 | Windows 本地分发包、配置/数据说明、第三方许可证、已知限制及可重复打包 | T103 | P1 | TODO | 受支持机器安装/启动检查；三层证据可追溯；不自动发布；REQ-TTS-001/AC-06：核对发布配置与产物，Live_audio EXE 不含引擎/推理环境/模型及内嵌自解压载荷，附独立部署说明 |

## 4. 需求覆盖索引

| 需求 | 主实现任务 | 最终验证 |
|---|---|---|
| REQ-SRC-001/002/003 | T030–T034、T071 | T102 / AC-13 |
| REQ-EVT-001/002/003 | T060–T062、T080、T081 | T100 / AC-01/02 |
| REQ-INT-001/002/003/004 | T061、T062 | T100、T101 / AC-01…04、09/10 |
| REQ-AUD-001/002/003/004 | T050、T052、T053、T055、T081、T082 | T100、T101 / AC-03/04/11 |
| REQ-TTS-001/002/003/004 | T040–T044、T055 | T101 / AC-06/09 |
| REQ-PRE-001/002/003 | T051、T052、T054 | T100、T101 / AC-04/05/12 |
| REQ-CTRL-001/002/003 | T032、T070–T072、T082 | T100、T102 / AC-07/08/09/11 |
| REQ-CTRL-004 | T061、T071、T073、T074 | T100、T102 / AC-10/13 |
| REQ-UI-001、REQ-DATA-001 | T080–T082、T090、T091 | T101–T104 / AC-11/12 |

## 5. 并行开发规划

### 5.1 计划波次

以下是未来分配建议，尚未启动任何工作者。每波开始时仍须把实际 Task ID 和所有者写入唯一 current_task；不能复制整张表让多个 AI 各自自由实施。

| 波次 | 整合负责人 | 可独立并行的候选工作 | 进入条件 |
|---|---|---|---|
| W0 | T010 工具链与项目骨架 | 无；先稳定真实项目根和 Git 状态 | T000 DONE；用户要求开始开发 |
| W1 | T020 冻结契约，之后 T021 夹具 | T030 平台调研、T040 引擎验证 | 各自依赖完成；真实操作条件单独核对 |
| W2 | T080 数据实现与共享工程配置 | A：T041→T042→T043；B：T050/T051→T052→T053；C：T060→T061 | T020/T021 已整合；T040 可用；实际每项依赖按表检查 |
| W3 | 整合 W2，串行处理必要契约修改 | A：T044；B：T054；C：T070；候补 T033→T034 | 每项依赖 DONE；并发上限建议 3 名工作者 |
| W4 | T055 接入 TTS 基础计划 | T062 互动接线、T071 平台控制、T073 文字调度 | W2 相关成果整合；T032 可用才安排 T071 |
| W5 | T072/T074 场控接线，T081/T082 恢复按依赖推进 | T090 本地 UI、T100 模拟集成；随后 T091 | 下游任务只能使用已整合接口和实现 |
| W6 | T101→T102→T103→T104 验收交付 | 可并行整理已有证据；真实声卡/房间测试串行 | 所有对应前置 DONE |

平台条件缺失时，T101 所需本地路径仍可独立推进。T101 只能宣称“本地播放及模拟互动可用”；完整 TikTok V1 需要 T102–T104，不能用改名字绕过验收。

### 5.2 文件所有权模板

| 工作线 | 可分配的路径前缀；实际切片进一步缩小 |
|---|---|
| A 本地 TTS | `src/TikTokAudio.Infrastructure/Tts/`、`src/TikTokAudio.Application/Tts/`、`tests/TikTokAudio.Application.Tests/Tts/`、`tests/TikTokAudio.Integration.Tests/Tts/` |
| B 播放与素材 | `src/TikTokAudio.Infrastructure/Audio/`、`src/TikTokAudio.Application/Playback/`、`src/TikTokAudio.Application/Media/`、对应测试中的 `Playback/`、`Media/`、`Audio/` |
| C 事件互动 | `src/TikTokAudio.Application/Interactions/`、`tests/TikTokAudio.Application.Tests/Interactions/` |
| 平台来源 | `src/TikTokAudio.Infrastructure/LiveSources/`、`tests/TikTokAudio.Integration.Tests/LiveSources/` |
| 场控 | `src/TikTokAudio.Application/Control/`、`src/TikTokAudio.Infrastructure/Control/`、对应测试中的 `Control/`；状态机与适配器同时做时再分开这两棵源码树 |
| UI | `src/TikTokAudio.Desktop/Views/`、`src/TikTokAudio.Desktop/ViewModels/`、`src/TikTokAudio.Desktop/Resources/`；组合根由负责人接线 |
| 整合负责人 | 六份治理文档、`src/TikTokAudio.Domain/`、`Application/Contracts/`、所有项目/依赖文件、测试共享夹具、SQLite 迁移、组合根 |

路径是未来目标。分配时必须把“对应测试”等描述换成实际绝对目录/文件列表。工作者不能把“对应”解释成可修改整个 tests。需要新包、迁移、共享类型时提交最小请求，负责人顺序修改后同步基线。

每个工作者使用独立 worktree；记录 BaseCommit。同一目录不能同时归两个活动工作者。需要同一声卡、音频缓存、测试库或直播间的集成测试预约串行运行，单元测试可并行。

### 5.3 整合门禁

1. 工作者回传成果和证据，进入 REVIEW；未分配功能不能随成果搭车。
2. 负责人核对基线、diff、文件所有权、契约和最小相关测试。
3. 顺序整合；遇到冲突保留双方意图并重新验证受影响行为。
4. 记录实际整合提交，或明确“无 Git 提交的已验证文件成果”；更新 tasks、handoff、changelog。
5. 依赖只有在 DONE 后才能被下一任务使用；不得依赖另一工作者尚未提交的目录状态。

## 6. 当前状态与选择规则

- 已完成：T000 文档基线、T010 最小工程、T020 共享契约冻结。
- 唯一当前切片：S05，主任务 T040，ACTIVE，Owner：Codex 主会话（Integrator），实际源码根 `E:\Live_audio`，无活动工作者。
- T030 已完成公开入口与本机环境只读核对并形成报告，仍因缺少授权直播间/测试账号与客户端范围而 BLOCKED。T040 已登记 ACTIVE，当前仅核对本机硬件、引擎许可和本地运行条件，不提前实现 T041。
- T010 已刷新 PATH 并确认官方 .NET SDK 10.0.401 x64，实际完成 restore/build/run 与 VS Code build task；0 错误、0 警告，WPF 窗口句柄/标题/响应状态和优雅关闭均通过，四层引用保持正确。平台及引擎未验证，尚未尝试其对应任务。
- S01/T010、S02/T020 与 S03/T021 已完成；T022 及后续任务不得在本轮提前开始。

### 6.1 T040 verification update (2026-09-19)

The T040 read-only recheck found no Vietnamese local engine, `vi-VN` voice, model/license, or loopback service. Dedicated and solution tests both passed 5/5, and `dotnet build TikTokAudio.slnx -c Debug --no-restore` completed with 0 warnings and 0 errors. Because AC-06 still lacks real synthesis/listening and P50/P95 evidence, T040 remains ACTIVE. The next allowed action is to obtain the minimum authorized engine input recorded in `docs/T040-engine-report.md`; T041 remains unavailable until T040 is DONE.

### 6.2 T040 本机部署更新（2026-09-24，当前状态）

用户已授权模型下载与部署，取代 2026-09-19 的缺少引擎条件。真实模型、离线合成与本机 API 已验证；T040 仍 ACTIVE，仅待人工试听结论。T041 保持 TODO，不提前改应用源码；本轮只更新五份允许文档，本地运行文件不进 Git。下一动作是试听并记录评价。

### 6.3 引擎独立部署与切换约束（2026-09-24）

需求版本 0.1.1 已记录用户明确要求：语音引擎可切换，引擎实现、推理环境及模型不嵌入 Live_audio EXE。已同步 T041/T043/T044/T104 验收依据；任务依赖和状态不变，本轮未实现适配器、界面或打包流程。

### 6.4 T040 人工验收完成（2026-09-24，当前状态）

用户明确确认“人工确认，这个声音符合要求”。已有真实本地模型/许可/合成/耗时证据，加上本次试听确认，T040由ACTIVE转DONE。T041的T020/T040前置均DONE，转READY、Owner未分配，尚未启动。T043/T044及完整AC-06仍未完成；下一步登记T041活动切片并按独立引擎、本机接口边界实施。

### 6.5 T041 启动（2026-09-24）

用户要求继续；S06/T041 ACTIVE。T020/T040均DONE，分配、允许路径和验收约定见current_task。适配器由Integrator实现，隔离工作树的测试工作者只写专属测试文件。停止于T041，不实施T042/T043/T044。

### 6.6 T041 验收完成（2026-09-24，当前状态）

S06/T041 DONE，Integrator完成实现与测试整合；专属50/50、解决方案55/55、build零警告零错误，真实本机健康/25音色/合成及请求前取消通过。证据和边界见docs/T041-adapter-report.md。工作者分配结束，隔离工作树保留，无提交。T042依赖T021/T041均DONE，转READY、Owner未分配；本轮停止，下一轮先登记切片再实施。T043/T044与完整AC-06未完成；此前ACTIVE/READY叙述为历史。

### 6.7 T042 验收完成（2026-09-24，当前状态）

用户继续后登记S07/T042并完成：UTF-8导入/分段/标记边界、确定性缓存键、受限磁盘WAV缓存、顺序预生成/取消与重试。T042 DONE，Owner Integrator；Application88/88、Integration89/89，全177/177，build零警告零错误。普通测试没有真实网络/TTS/设备访问。两个独立工作树成果均已整合，分配结束且无提交。T043依赖满足转READY、Owner未分配，本轮停止。证据见docs/T042-preparation-report.md；当前状态以前面的任务表和本条为准，旧日志保留历史。

## 2026-09-24 调整后当前状态

用户明确暂缓T044第二真实引擎，保留ITtsProvider和既有注册扩展点，不下载/安装新引擎；AC-06双引擎完整验收仍未完成。T043代码复核发现注册器revision与provider/pre-generator未连通，配置地址载入未实现，完成声明撤回至REVIEW（无活动开发）。T050只依赖T020，依赖DONE且P0顺序最早，登记S09/T050 ACTIVE；T051及后续不提前实施。原有未提交T043文件保留，无commit/push。

## 2026-09-24 S09/T050完成（当前状态）

T050 DONE，新增43项、全224/224测试通过；解决方案及独立smoke build均零警告错误。Realtek源采样游标及播放中停止实测通过，报告docs/T050-audio-report.md。T051依赖T020已满足，转READY（未分配）；本轮停止。T044 DEFERRED不计DONE；T043 REVIEW的配置装配与revision联动仍需补齐，T081/T090相关依赖不绕过。此前S08已完成的声明以本次复核更正为准。

## 2026-09-24 S10/T051完成（当前状态）

用户继续后已完成T051，专属45/45、全269/269测试通过，build零警告错误，静态检查通过，报告docs/T051-import-report.md。S10/T051 DONE；按依赖、P0及任务顺序，下一个最小切片为T052，T021/T051均DONE，转READY未分配。本轮停止于T051。T043 REVIEW、T044 DEFERRED不变，无commit/push。

## 2026-09-24 S11/T052完成（当前状态）

用户继续后完成T052。预制计划按数字组序循环，各组独立洗牌袋；快照恢复保留源游标/片段/效果种子/袋与随机状态，确认开始才消费一次边界，旧修订隔离。缺组1计划明确拒绝，T051导入保持。专属66/66、全335/335、build零警告错误及静态检查通过。S11/T052 DONE；下一最小T053依赖T021/T050/T052均DONE，READY未分配。T043 REVIEW/T044 DEFERRED不变，未commit/push。

## 2026-09-24 S12/T053完成（当前状态）

本地基础/互动调度已整合：准备后短淡出插播、优先级、源游标恢复、一次边界、暂停/停止/换场与旧结果隔离；输出准备阶段TTL及终态通知竞态均有测试。专属51/51、淡出/输出相关26/26、实际输出内存集成2/2、全398/398通过，Build零警告错误，静态检查通过。证据docs/T053-scheduler-report.md。S12/T053 DONE；T055依赖T042/T053已DONE，P0优先于T054(P1)，为下一最小READY；T054同步READY但未分配。停止于当前切片，不提前接入TXT/UI。T043 REVIEW、T044 DEFERRED不变；无stage/commit/push。

## 2026-09-25 S23/T081 closeout

T081 DONE. Added local playback recovery coordinator, TTS checkpoint capture, and a SQLite ShuffleBag document adapter. Pre-recorded recovery preserves catalog fingerprint, checkpoint, shuffle bags, effect random state and consumed markers; TTS recovery preserves the checkpoint and engine snapshot. Recovery first validates and creates a read-only preview; only explicit confirmation restores an in-memory planner. No automatic audio, text dispatch, product action, or Unknown-action replay is performed.

Evidence: dedicated Application 4/4 and Integration 3/3; related Application 571/571 and Integration 388/388; solution build 0 warnings/0 errors. T082 remains blocked by T034/T072/T074. T090 is the next dependency-ready P1 slice, but was not started in this round.
