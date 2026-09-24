# T042 TXT 与音频准备验收报告

日期：2026-09-24（Asia/Shanghai）｜S07 / T042 DONE｜需求版本0.1.1｜基线e67e2e5。

## 实现成果

本切片在Application.Tts新增操作员稿件导入、分段/商品边界模型、缓存身份和顺序预生成；Infrastructure.Tts.Cache负责PCM16 WAV文件校验、磁盘缓存和生成暂存文件清理。没有新增包依赖，没有修改T020冻结的Domain/Application.Contracts签名、Desktop、数据库或引擎部署。T042专用协作类型由Integrator统一定义于TtsPreparationModels.cs。

### 操作员稿件与预览

`OperatorScriptImporter(TtsScriptImportLimits).Import(utf8Bytes, products)`接收操作员选择的稿件字节和编号到产品ID的映射，返回原文、分段预览数据、错误及警告。三个限制MaxInputBytes/MaxSegments/MaxSegmentLength必须由调用方显式提供，本切片不新增产品默认值。

- 严格UTF-8，支持起始BOM；拒绝无效字节、UTF-16/UTF-32及异常控制字符，不猜测其他编码。原文保留（不含BOM），合成文字NFC规范化，保留越南语重音。
- 按换行、句末标点、明确长度上限切分；长度按UTF-16代码单元计，优先词间空白，不切开Unicode文本元素。单个文本元素超过上限时明确失败。分句为确定性标点规则，不声称识别所有越南语缩写。
- 只解析此操作员入口的`@[正整数]`，每个编号都必须有效且已映射。拒绝溢出、路径/命令样式、缺括号及未知产品；不把这些内容当文件路径或命令。
- 标记不送TTS；其后首段携带ProductBoundary。连续标记取最后目标；尾部标记仅提示、无独立边界。边界ID取稿件与标记位置的确定性哈希，相同稿件重复导入保持一致，重复标记位置彼此不同。
- 只有标记/空白而没有语音的稿件无法开始。此模块没有商品控制器依赖；评论、昵称不得路由到此导入入口。真正播放开始时触发、恢复不重复及远程事件接线属于T055/T062，不能将本次元数据测试视为完整AC-09。

### 缓存身份

`TtsCacheKey.Create(text, settings, segmenterVersion)`以确定性JSON编码再计算SHA256。包含文本、引擎ID/版本、模型ID/版本、音色、语言、语速、音调、音量、分段器版本和发音词典版本。只有自然语言文本做NFC；不透明ID/版本按原值保存，避免把不同版本合并。所有字符串检查有效UTF-16，参数拒绝非有限数值，字段结构防止拼接歧义。

EngineRevision是运行会话隔离编号，不替代模型版本。ITtsProvider现有签名没有权重版本查询能力，因此调用方必须从已核验的部署信息提供真实engine/model版本；本切片不会自行证明版本对应权重。无发音词典时使用明确的无词典版本标识；本切片没有实现词典编辑或转换器。

相同文本/设置的不同商品边界可以复用同一个音频文件，但PreparedTtsSegment分别保留各自的商品边界，不复用商品动作。

### 本地文件缓存

`FileTtsAudioCache(TtsAudioCacheOptions)`实现FindAsync、StoreAsync与DiscardAsync。CacheDirectory与StagingDirectory必须为彼此独立的绝对目录；MaxEntries、MaxTotalBytes、MaxAssetBytes均显式提供。VieNeuTtsOptions.OutputDirectory在未来组合根应配置为同一StagingDirectory。

- 键必须是64位小写十六进制，路径限制在选定目录；拒绝重解析祖先、路径越界、备用数据流。应用层不直接删除provider返回的任意路径。
- Store只接管通过路径验证的生成文件，成功/失败/取消后清理源文件。Discard无取消token且幂等，用于丢弃迟到或旧revision结果；外部文件不删除。
- 有界复制到`.pending-<随机ID>`目录，校验RIFF/WAVE块结构、PCM16、声道/采样率/字节率/块对齐、完整非空数据块，计算SHA256并写入元数据，然后原子目录改名为缓存键。实际采样率/时长从WAV读取，不信任输入提示。
- 支持PCM16的1–32声道、1–384000Hz及有限块数量；非PCM16、格式扩展、空/截断/不对齐数据明确失败。PCM16按结构可直接读取；本轮没有调用真实音频设备或通用压缩格式解码器。
- 元数据上限8192字节、JSON深度8；每次命中重新校验格式、长度、SHA256。缓存缺失为Succeeded+null；损坏条目为Failed，绝不当命中。当前损坏条目不会自动修复或覆盖，需显式处理后重试。
- `.cache.lock`独占文件句柄协调跨实例操作；竞争立即Failed，无无限排队。已有成功条目不覆盖，也不自动淘汰可能仍在使用的音频。
- 写入前计入预留条目检查容量，发布前复验总字节和条目数。历史崩溃残留也占容量，不因重启继续无界复制；复制期间最多额外需要一个受MaxAssetBytes限制的暂存资产和元数据空间。
- 不自动清理历史崩溃残留；此类残留可能要求操作员处理。路径每个操作前复核，但没有宣称抵抗另一恶意本地进程主动替换目录的系统调用竞态。本轮未创建真实重解析点测试，该部分依据实现审查。
- 文件目录与小型元数据支持重建实例后的缓存复用；SQLite索引/恢复仍属T080，不更改已有迁移契约。

### 预生成、等待与取消

`TtsPreGenerator(provider, cache, maxSegmentsPerBatch).PrepareAsync(document, startIndex, count, settings, token)`处理显式有界批次，按段落顺序查缓存、必要时调用引擎、验证/提交资产。没有自动重试，调用方可在修复后再次发起同一批次，成功缓存可继续复用。

Snapshot为不可变快照。当前与下一段均就绪后CanStartPlayback才为true；只请求一段且后面仍有内容时保持WaitingForSynthesis。请求处于文档末尾仅剩一段时按剩余窗口判断。前两段就绪后可在后续分段继续生成期间得到Ready快照。这里是有限文档窗口的准备状态；循环跨尾首的下一段安排、实际播放/等待展示由T055/界面负责。

单实例只执行一个批次，忙时返回失败且不覆盖已有任务快照。缓存命中、合成返回、缓存提交及发布前均检查取消和provider revision；旧结果不进入快照，合法暂存文件仍会清理。清理失败明确Failed；Unsupported/Unknown等结果保留其区分。已完成且有效的缓存条目不会因后续批次取消自动删除，也不能被误解为已开始播放。新引擎选择与当前音频播放完毕再换仍属于T043。

全部缓存命中时无需调用provider健康或合成方法，因此引擎不可用时可准备已有文件；这不等于本轮完成了实际设备离线播放验收。

## 验证证据

.NET SDK10.0.401；工作目录`E:\Live_audio`。实际可执行文件为`C:\Program Files\dotnet\dotnet.exe`。

| 精确命令 | 最终结果 |
|---|---|
| `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore` | 退出0，88通过/0失败 |
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore` | 退出0，88通过/0失败（盘符根回归前；最终89项见解决方案回归） |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 退出0，177通过/0失败 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出0，0警告/0错误 |
| `git diff --check`及新增文件逐项`git diff --no-index --check -- NUL <file>` | 无空白错误；新增文件可返回1表示差异，LF/CRLF为Git配置提示 |

新增122项：稿件导入53、缓存身份/预生成30、文件缓存36、跨模块流程3。加上原有55项，共177项。所有普通测试仅使用内存模拟provider/cache、构造PCM16 WAV与每例独立临时目录；不调用真实网络、VieNeu、账号、平台或音频设备。

跨模块流程证明：UTF-8稿件→去标记分段→顺序合成→有效WAV缓存→清理暂存→重建缓存实例并在禁止合成的provider下全部复用。同文本不同商品边界仍独立；取消后迟到WAV被清理；改动缓存PCM但保留合法头也会因哈希不符拒绝就绪。

取消竞态使用显式信号控制，无真实长等待；覆盖Find/Store返回时取消或revision改变、引擎忽略取消返回成功、清理失败、busy和取消后重试。独立审查发现不透明版本字段NFC合并风险，已修复并补回归；另补了写入前容量准入，保留发布前复查。最终审查发现盘符根目录分隔符影响包含关系的边界问题，已修复并增加仅构造器检查的回归，不对盘符根写文件。

本轮没有重跑真实VieNeu合成或人耳试听；真实引擎及C#调用证据沿用T040/T041。没有把构造WAV或模拟离线复用当作真实音质、设备播放、平台成功或完整AC-06/AC-09证据。

## 整合与下一任务

两个工作者均基于e67e2e5，无提交；Integrator已复制整合、复核和运行测试。`E:\Live_audio_t042_script` / `task/t042-script`与`E:\Live_audio_t042_cache` / `task/t042-cache`保留追溯；主工作区版本为准，无待整合补丁。

S07/T042 DONE。T043因T042完成而READY，尚未开始；下一轮先登记唯一活动切片再实施多引擎配置与切换。T044第二真实引擎、Desktop、播放调度、完整AC-06/AC-09仍未完成。本轮未commit/push，既有T040/T041修改与vieneu-window.png保留。
