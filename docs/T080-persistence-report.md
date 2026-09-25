# T080 本地持久化验收报告

2026-09-25；S20 / T080 DONE；需求0.1.1 / REQ-DATA-001；基线main / 9912252aa6edbe2fce515dd3b193d9f29de8fce4；Windows / .NET SDK 10.0.401。

## 成果与边界

- 实现冻结 `IStateStore` 全部13个方法，不改Domain/Contracts。SQLite保存规则/计划元数据、SessionId、事件指纹、互动预留/完成、按基础模式区分的检查点、缓存元数据和动作账本。音频仍为文件，数据库不存大音频。
- 冻结规则/计划模型只含元数据；额外 `LocalStateDocument` 按设置、规则正文、素材索引、洗牌袋和商品状态分区，显式携带文档版本、JSON与UTC时间。这里只提供持久化能力，宿主装配/恢复留T081/T082；不自动恢复音频、商品、文字或重放Unknown动作。
- schema v1为核心表与互动记录，v2增加动作账本/本地文档；`application_id=0x4C415544`、`user_version=2`。新建/升级在单事务中执行，重复初始化幂等。已有库先只读检查身份、版本、必需表列及 `quick_check(1)`，再允许写入；外来、未来版本、缺表、坏文件头与数据页损坏明确拒绝。
- SQL值全部参数化；规则/计划/会话/文档upsert；指纹首条保留；互动ReservationId及业务三元键防重复，成功完成保留首次时间；ActionId内容不可变且允许同内容幂等重交，最多1024条账本原子批次。检查点在调用入口复制有界marker集合，保留源采样游标与效果种子。
- 单实例无等待队列，忙时写返回Failed、读抛显式异常；同步SQLite I/O在单个受限后台任务运行。锁等待由调用方配置1–30秒；取消在提交前检查，失败回滚，提交已开始而结果不明返回Unknown。取消不能强制中断正在进行的同步SQLite调用，不宣称实时取消或已完成恢复编排。
- 标识符最大256字符、拒绝控制字符；marker最多10000；JSON深度32，UTF-8有效载荷由调用方显式限额（1–16MiB）。读前SQL先限额，避免将超大payload先送入托管字符串。递归拒绝已知敏感JSON字段名，错误不回传原始SQL/路径/内容。任意文本值中是否夹带凭证仍需调用方保证，不能把字段拦截当成通用秘密识别器；账本Detail不得放原始异常/凭证/评论。
- 开发目录 `.local-data/` 已被Git忽略；用户目录由最终产品名解析到LOCALAPPDATA，没有静默fallback。拒绝非规范绝对路径、越界、UNC/设备/ADS、保留设备名、既有祖先/目标重解析点以及Windows多硬链接文件。数据库及journal/wal/shm侧车均检查路径；缓存元数据只能引用选定cache内路径。
- 日志只接受枚举、UTC和可选GUID，不接受正文/异常；大小及文件数显式配置（文件数1–1000）。零等待实例背压及 `diagnostic.lock` 跨实例互斥覆盖追加/轮转/清理，准确保留无关及相似文件。清理入口保留零字节协调锁文件。
- 凭证单独封装Windows Generic Credential安全存储，以产品/凭证名隔离，最大2560 UTF-8字节；Secret格式化/序列化不暴露值，释放时清零托管/原生暂存。`LocalMachine`持久类型指当前用户凭证在本机持久化，不是全机器可读明文文件。普通测试全部注入fake API，不触碰真实凭证库。

## 验证

以下均在E:\Live_audio顺序执行，退出0，最终专属/整方案无失败、无跳过：

| 命令 | 结果 |
|---|---|
| `dotnet restore TikTokAudio.slnx` | 最终通过，无警告 |
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T080` | 最终137/137（仓储57、安全31、本地边界49） |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 最终909/909；Application566、Integration343，包含全部相关回归 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 0 warnings / 0 errors |
| `dotnet list src/TikTokAudio.Infrastructure/TikTokAudio.Infrastructure.csproj package --vulnerable --include-transitive --no-restore` | 当前NuGet源未发现已知易受攻击包 |
| `git -c core.safecrlf=false diff --check` | 通过；不修改Git配置 |

新增源码/测试逐文件尾随空白与EOF检查通过；范围限定的静态搜索无 `.Result`、`.Wait()`、真实等待、外部客户端、原始日志输出或TODO/HACK。`git diff --no-index --check -- NUL <新增文件>`的无输出退出1来自新增文件差异语义，未作为失败测试或通过证据，另用明确空白/EOF检查验证。

关键证据：新库/重开、v1保留数据升级；第二条DDL失败时版本仍1且第一条新表回滚；拒绝库文件SHA-256不变；schema可读但独立数据页损坏也不迁移；越南语/UTC；全部port取消与未初始化；超限/敏感JSON/SQL式ID；批次冲突回滚；外部数据库锁超时、忙时背压与提交前取消；检查点可变集合快照；路径/Windows硬链接；日志跨实例占用、限额收紧与精确清理；fake凭证异常/取消/命名空间/脱敏。

验证过程：首轮专属132/132；补硬链接和快照/背压后136/136、整方案908/908；终审补数据页检查后重新跑出最终137/137及909/909。未忽略失败/跳过用例。取消用例证明取消未提交，不冒称确定在已写第一条SQL之后的中断故障注入；原子回滚由迁移与账本冲突测试另证。

首次只添加Microsoft.Data.Sqlite10.0.9时restore报告传递原生库SQLitePCLRaw.lib.e_sqlite3 2.1.11的NU1903（GHSA-2m69-gcr7-jv3q）。核对同维护系列后固定SQLitePCLRaw.bundle_e_sqlite3 2.1.13，重新restore清除警告，最终审计无已知漏洞；未抑制审计或批量升级其他包。

## 修改文件与所有权

Integrator：

- `src/TikTokAudio.Infrastructure/Persistence/SqliteStateModels.cs`
- `src/TikTokAudio.Infrastructure/Persistence/SqliteSchema.cs`
- `src/TikTokAudio.Infrastructure/Persistence/SqliteStateStore.cs`
- `src/TikTokAudio.Infrastructure/Persistence/SqliteStatePorts.cs`
- `src/TikTokAudio.Infrastructure/TikTokAudio.Infrastructure.csproj`
- `tests/TikTokAudio.Integration.Tests/T080SqliteSafetyTests.cs`
- `docs/T080-persistence-report.md`
- `tasks.md`、`current_task.md`、`handoff.md`、`changelog.md`

共享目录互斥工作者，已整合并关闭写入分配：

- t070_tests：`tests/TikTokAudio.Integration.Tests/T080SqliteStateStoreTests.cs`
- t073_schedule：`src/TikTokAudio.Infrastructure/Persistence/LocalDataPaths.cs`、`RotatingDiagnosticLog.cs`、`WindowsCredentialStore.cs`；`tests/TikTokAudio.Integration.Tests/T080LocalDataTests.cs`
- t073_review：只读复核，无修改；最终无阻断项。

共16个本切片文件。未修改需求、共享契约、调度/UI/音频/TTS/平台代码或解决方案；未改写之前切片成果。

## 实测限制与交接

只操作自建GUID临时数据库/日志/硬链接，清理范围经校验；没有读取或迁移用户现有库、访问真实凭证/账号/平台/TTS/设备。凭证native接口仍待独立授权实测；重解析点检查已审查但未创建符号链接实测；路径预检不宣称消除恶意同权限进程并发替换路径的TOCTOU风险。没有进行进程强杀/断电测试，也未声称所有损坏形态都可修复；检查失败只拒绝，不恢复/覆盖数据。

main/9912252不变，进入时9修改/23未跟踪全部保留，结束时10修改/34未跟踪，三个旧worktree保留。无stage/commit/push/reset/clean/覆盖checkout，无待整合成果。

按依赖→P0→READY→顺序，下一最小切片为T100：T021/T055/T062/T070/T073/T080均DONE，转READY、未分配且未实施。T043 REVIEW使T081/T090仍不可启动；T030 BLOCKED与T044 DEFERRED保持。下一轮先登记T100唯一ACTIVE和允许路径，再做离线模拟整链，不以此替代真实房间/设备证据。
