# 当前唯一执行切片

文档基线：0.1.0｜需求版本：0.1.1｜更新日期：2026-09-24（Asia/Shanghai）。

## 当前指针与范围

- SliceId S07 / Task T042 / 状态 DONE / Integrator Codex主会话。
- 用户要求继续；T021/T041均DONE。实现UTF-8 TXT导入、分段预览模型、操作员标记边界、确定性音频缓存键、文件缓存、顺序预生成与取消。
- main / HEAD与BaseCommit e67e2e5421a32861cbf14ec3497fd94208bd6ae8；保留所有T040/T041源码、文档和未跟踪截图。不重置、不处理无关文件。
- 停止于T042，不实施T043引擎切换、T044第二引擎、Desktop UI、音频播放、平台/商品副作用或SQLite迁移。不下载模型，不启动真实平台测试。

## 允许路径与分配

负责人可写：src/TikTokAudio.Application/Tts/、src/TikTokAudio.Infrastructure/Tts/Cache/、tests/TikTokAudio.Application.Tests/Tts/、tests/TikTokAudio.Integration.Tests/TtsAudioCacheTests.cs、tests/TikTokAudio.Integration.Tests/TtsPreparationWorkflowTests.cs、docs/T042-preparation-report.md、current_task/tasks/handoff/changelog；必要时仅登记现有测试项目内文件，不改共享包版本。既有Domain/Application.Contracts接口不变；新T042内部协作契约由负责人统一冻结于TtsPreparationModels.cs。

| Owner | Task | Branch / Worktree | AllowedPaths | BaseCommit |
|---|---|---|---|---|
| Integrator | 新协作契约、缓存键、预生成、整合与文档 | main / E:\Live_audio | 上述负责人范围；工作者返回前不改其专属文件 | e67e2e5 |
| /root/t042_script | 操作员稿件导入/分段及专属测试 | task/t042-script / E:\Live_audio_t042_script | src/TikTokAudio.Application/Tts/OperatorScriptImporter.cs、tests/TikTokAudio.Application.Tests/Tts/OperatorScriptImporterTests.cs | e67e2e5 |
| /root/t042_cache | 本地文件缓存及专属测试 | task/t042-cache / E:\Live_audio_t042_cache | src/TikTokAudio.Infrastructure/Tts/Cache/、tests/TikTokAudio.Integration.Tests/TtsAudioCacheTests.cs | e67e2e5 |

工作树从已提交基线创建；新共享契约以主工作区文件为准，工作者只读，不各自定义。仅负责人在主工作区整合后运行构建/测试，工作者不运行修改共享产物的任务。T041工作者已结束，旧工作树保留追溯。

## 冻结的本切片约定

- 新类型位于Application.Tts，具体签名见TtsPreparationModels.cs。没有修改T020冻结接口。ITtsAudioCache仅供预生成使用；SQLite索引仍属T080。
- OperatorScriptImporter(TtsScriptImportLimits limits)，Import(ReadOnlyMemory<byte> utf8, IReadOnlyDictionary<int,string> products)返回TtsScriptImportResult，Version常量用于缓存键。只用于操作员导入，不接受评论/昵称事件对象。严格UTF-8含可选BOM，保留原文和越南语重音，按段落/句子/明确长度上限分段，不切开Unicode文本元素。所有限额由调用方显式传入，不新增产品默认值。
- 标记@[正整数]必须映射产品；无效/未映射编号阻止导入。标记不送TTS，标记切开语音边界；连续取最后一个，末尾提示不发边界。边界ID随同一稿件/位置确定；仅生成元数据，真正开始/恢复去重归T055。
- TtsCacheKey.Create(text, settings, segmenterVersion)：SHA256规范JSON，包含NFC文本、实际引擎/版本/模型/版本、音色/语言/效果、分段器及词典版本；EngineRevision不是模型版本，不替代版本字段。
- FileTtsAudioCache(TtsAudioCacheOptions)：选定缓存目录和独立生成暂存目录、MaxEntries/MaxTotalBytes/MaxAssetBytes均显式配置；PCM16 RIFF WAV验证可读取的数据结构，限长且哈希校验；临时目录内写完音频/元数据后原子目录提交，损坏条目不命中。拒绝路径穿越/重解析路径；容量满明确失败，无自动删除在用资产。Store接管合法暂存文件所有权，成功/失败/取消后清理，不删除外部文件。DiscardAsync无取消令牌且幂等，供编排层丢弃过期资产时安全清理。
- TtsPreGenerator(ITtsProvider, ITtsAudioCache, int maxSegmentsPerBatch)：PrepareAsync(document,startIndex,count,settings,token)，按顺序复用缓存/生成；Snapshot通过不可变快照显示WaitingForSynthesis/Ready/Failed/Cancelled。当前与下一段（末尾只有一段时取剩余）就绪才CanStartPlayback。单实例一次批次、忙时失败；token和provider revision在提交及发布前核对，旧/取消结果不发布。未来引擎切换编排仍属T043。
- 普通测试只用内存provider/cache与独立临时目录，不连真实网络/TTS/设备。T041已有真实引擎证据；本切片使用构造WAV验证文件缓存，不冒充设备播放/完整AC-06或AC-09。

## 验收与停止点

执行新增专属测试、依赖回归、解决方案build、差异/状态一致性检查，记录准确命令与限制。T042完成后停止，下一允许任务T043仅转READY，不提前实施。

## 2026-09-24 验收完成

T042 DONE。实现与限制见docs/T042-preparation-report.md。新增122项（导入53、缓存键/预生成30、文件缓存36、跨模块3）；加原有55，共177/177通过。Debug build零警告零错误，差异检查无空白错误。普通测试仅内存模拟和临时WAV，不访问真实网络/TTS/设备。

两名工作者成果已顺序整合，所有权分配结束；工作树/分支保留且无提交，主工作区为验收版本。没有待整合补丁。本轮保留全部前序改动与截图，未stage/commit/push。

T043依赖T042已完成，转READY，尚未分配。下一项允许动作是登记T043唯一活动切片、范围与契约后开始配置/切换；本轮停止。实际播放边界触发、循环控制和UI仍属后续任务，不宣称完整AC-06/AC-09通过。
