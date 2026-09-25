# T100 离线整链回归报告

2026-09-25；S21/T100 DONE；需求0.1.1；基线main / 9912252aa6edbe2fce515dd3b193d9f29de8fce4；Windows / SDK10.0.401。

## 范围与成果

T021/T055/T062/T070/T073/T080均DONE后登记本切片。新增40项跨模块自动化测试及测试专用接线，复用实际应用组件和SQLite；未修改任何生产源码、共享Contract/Domain、依赖版本、数据库迁移或UI。

三条测试链路互补覆盖事件、两种基础模式、抢占、模拟商品/文字及持久化：

- `SimulatedLiveEventSource → EventDeduplicationCoordinator/SQLite → InteractionRuleCoordinator → InteractionPlaybackCoordinator/TtsPreGenerator → AudioPlaybackScheduler/PreRecordedPlaybackPlanner`，共用同一规则实例的文字协调器将实际DrainLedger结果写入SQLite。
- 预制目录模型或实际 `OperatorScriptImporter → TtsPreGenerator → TtsPlaybackPlanner → AudioPlaybackScheduler`，仅把实际发出的ProductBoundary转交ProductControlCoordinator和模拟场控，不在测试接线中重写边界规则。
- `ProductControlCoordinator + TextDispatchCoordinator/TimedTextPlanner → 模拟场控/延迟确认 → 实际Snapshot/DrainLedger → SQLite → 重开读取`，观察Unknown、旧Epoch、生命周期和存储失败后的移交行为。

T021四份夹具通过Integration.csproj显式Compile Link复用，夹具源文件未改。音频输出/TTS/cache仅在内存模拟端口；事件/冷却/计划/边界/调度/场控决策和成功完成均由实际组件产生，测试不手写成功记录。故障场控只延迟现有模拟器的确认，不伪造协调器状态。

## AC证据映射

| 验收 | 新增跨模块证据 | 仍不代表 |
|---|---|---|
| AC-01 | 实际事件先入SQLite；欢迎/关注同用户独立占位；播放完整结束才完成；同场重连及数据库重开后相同EventId被拒绝 | 完整业务占位的生产崩溃恢复/Unknown预览已接线，仍留T081/T082 |
| AC-02 | K/V/W各自从完整结束计时，到期前1tick不启动；慢欢迎被新候选替换、TTL、未匹配评论与点赞不触发、关注队列最多20 | 真实事件覆盖完整性 |
| AC-03 | 慢/失败合成不暂停基础；内存输出在每次成功Play前拒绝已有主流运行；旧EngineRevision结果被丢弃 | 真实TTS耗时及设备≤500ms抢占指标 |
| AC-04 | 插播/输出失败/暂停恢复保留精确源采样位置、ClipId、Cycle、EffectSeed及已消费标记；SQLite重开读回检查点 | 真实解码/设备≤100ms误差及恢复宿主实现 |
| AC-05 | 真实预制planner经scheduler验证1→2→10排序、不等组数完整循环、各组独立洗牌袋与袋边界不重复 | 目标机器4小时稳定性 |
| AC-07 | 0/30/60秒同品、80秒切品后90秒不旧续显；实际InterruptPlaying中推进30秒仍续显同品、Epoch不变，恢复不重复发边界 | 真实平台展示/30秒行为 |
| AC-08 | 延迟旧商品确认不能覆盖新Epoch；超时Unknown先读回；同边界人工目标优先；晚文字确认不改已持久化Unknown | 真实网络故障率或平台成功 |
| AC-09 | 实际TXT导入/内存生成/计划/场控验证标记不朗读、仅开播边界一次、连续/末尾标记、失败开播不消费；远程昵称/评论标记不成为商品边界 | T072生产组合根或真实商品控制已完成 |
| AC-10 | 语音/文字单独或同时启用；同评论每渠道一次；总间隔、过窗、账本背压；断线后只排未来；取消落库可重交原batch不重发消息 | T074真实文字渠道已接线 |
| AC-11 | 暂停/停止/换场使未来操作失效；晚生成结果经实际预生成器清理后断言；重开Unknown账本不触发新协调器自动发言 | 真实进程崩溃、生产恢复预览、T081/T082实现 |

三组用例：T100EventPlaybackTests 21、T100PlanBoundaryTests 8、T100ControlPersistenceTests 11。普通测试只用合成事件/账号名及自建GUID临时数据库，未连接真实网络、凭证、平台、TTS、音频设备，未读取用户现有库。测试移交ProductState为实际Snapshot，文字ActionLedger为实际DrainLedger；商品生产动作账本/恢复装配未提前实现。

## 验证命令与结果

在E:\Live_audio由Integrator顺序执行：

| 命令 | 最终结果 |
|---|---|
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T100` | 退出0；40/40，无失败/跳过 |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 退出0；949/949（Application566+Integration383），含全部依赖相关回归，无失败/跳过 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出0；0 warnings / 0 errors |
| `git -c core.safecrlf=false diff --check` | 退出0；仅本次命令设置，不修改仓库配置 |

新增文件尾随空白/EOF逐项检查通过。范围限定静态检查未发现同步阻塞、真实外部客户端/音频/凭证调用或TODO/HACK。Task.Delay(1)只用于等待线程池推进的异步轮询；5/10秒WaitAsync/CTS仅防测试悬挂，所有K/V/W、TTL、30秒续显及定时文字业务时间均推进FakeClock，不真实等待这些时长。临时数据清理先校验系统temp直接子目录和自建GUID，不删除工作区或用户数据。

过程证据：首批事件/播放17/17通过时出现xUnit2031断言写法警告，已改用Assert.Single的predicate重载，未抑制分析器；整合36/36通过后按审查补齐实际迟到结果清理信号、插播中的30秒续显、K/V/W、旧引擎和暂停组合场景，最终40/40且无警告。没有失败/跳过用例被忽略，没有为通过测试改生产业务语义。

## 修改文件与整合

共10个本切片文件：

- `tests/TikTokAudio.Integration.Tests/T100PlaybackHarness.cs`
- `tests/TikTokAudio.Integration.Tests/T100EventPlaybackTests.cs`
- `tests/TikTokAudio.Integration.Tests/T100PlanBoundaryTests.cs`
- `tests/TikTokAudio.Integration.Tests/T100ControlPersistenceTests.cs`
- `tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj`
- `docs/T100-offline-regression-report.md`
- `tasks.md`、`current_task.md`、`handoff.md`、`changelog.md`

Integrator负责事件/播放及共享测试配置；t073_schedule仅计划边界文件，t070_tests仅场控持久化文件，t073_review只读复核。共享目录互斥，无并行build/test；三名工作者成果已整合，分配关闭，终审无阻断项。所有成果是未经Git提交的已验证文件，不宣称已发布。

main/9912252及三个旧worktree不变；进入时10修改/34未跟踪全部保留，结束时11修改/39未跟踪，暂存0。无commit/push/reset/clean/覆盖checkout。

## 下一动作与未解前置

T100仅离线模拟层DONE，不改变AC-06/12/13的真实验收状态，也不将T072/T074/T081/T082/T101标为完成。重新核对所有任务后没有READY切片，本轮停止。

| 阻塞链 | 已确认事实 / 不能继续的动作 | 安全替代与最小下一输入 |
|---|---|---|
| T043 REVIEW → T081/T090 | 既有交接确认注册器revision未连通provider/pre-generator，配置地址载入未实现；不能越过它做恢复/本地UI | T100已完成不依赖该缺口的离线验证；需确认重新安排T043复核/补齐切片并登记ACTIVE，不能默认为DONE |
| T030 BLOCKED → T031/T032及真实适配器 | 缺授权测试房间/账号与客户端范围，不能真实采集/展示商品/发言 | 本轮仅模拟；解除仍需上述授权条件，不尝试登录/平台操作 |
| T044 DEFERRED → T101 | 用户暂缓第二真实引擎；T101还缺T081/T090 | 保留接口与单引擎现有成果；恢复T044需用户明确决定，不下载模型绕过；不以模拟结果代替双引擎实测 |

建议最小下一动作是确认恢复T043的既有配置装配与revision联动缺口；此建议不包含新增引擎、模型下载或真实平台授权。其他任务状态保持不变。
