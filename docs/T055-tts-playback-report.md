# T055 TXT基础播放接入

日期：2026-09-24。S13/T055 DONE；需求0.1.1，main/6e5dcf29ee6825e238cc9b7c17128df741dc1636。工作区未提交。

## 范围

复用T042操作员稿件导入/去标记/缓存/预生成与T053唯一主输出调度，增加TXT基础计划；默认顺序循环，可选只播一次。应用层IBasePlaybackPlan桥接原冻结IPlaybackPlanner，不修改Domain或Application.Contracts。PreRecordedPlaybackPlanner仅适配模式、可用状态、开始确认和游标保存。

TXT计划使用同一稿件及修订的准备资产，每次新段的当前/下一段齐备才开始（单段稿及一次模式末段只需该段，循环尾段的下一段为第0段）；供给不足明确Preparing/等待合成，一次模式正常结束为Idle。调度在成功Play后才确认一次商品标记，插播/暂停恢复沿用原资产与源游标，不重发已消费标记。独立本地引擎与模型继续外部部署。

## 调用及生命周期

宿主仍负责驱动PumpAsync并处理结果/边界；本轮不添加UI或自动轮询。新计划通过SwitchBasePlanAsync显式替换，修订必须严格增加，保留原场次和暂停状态。接受切换立即取消旧待处理互动/准备及播放生命周期，停止旧输出后才启动新计划。内部切换等待一个串行槽位，不因普通命令队列满而遗失重启；Stop可使等待中的切换失效。停止失败不得启动新计划。

TXT预生成来源及手动准备快照属于受信任操作员调用；不能将远程用户文本或任意外部资产作为已校验稿件。T043配置装配/旧引擎联动仍REVIEW，本轮不声称完成引擎切换。完整控制台、持久化/崩溃恢复、商品控制副作用与真实设备误差验收仍属于后续切片。

## TXT准备与身份

TtsPlaybackPlanner冻结操作员稿件，按原稿索引/文本/边界校验准备快照，并按分段索引累积资产。ApplyPreparation明确要求当前PlanRevision，错误快照原子拒绝，已准备资产不可替换。此接口只接受受信任的同计划结果，不证明任意外部文件有效。

PrepareAsync复用T042 TtsPreGenerator，同一计划固定一个生成器并只允许一个在途批次。宿主在等待后续段时继续Pump，可导入当前批次逐步就绪的快照；缺段不会推进位置。取消/模式切换以准备代次隔离迟到结果，不响应取消的批次仍保留占位。后续显式批次需在前一批实际完成后启动；不无限追加后台生成任务。

标记身份包含稿件指纹、计划修订及循环，当前段消费记录有界；无标记段继承最近已确认开始的产品目标，但不会产生新的商品动作。保存/恢复使用相同本地资产及源采样位置；检查点恢复只支持当前已选项，跨进程恢复留T081。

## 验证结果

- 原T053专属调度回归：`dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AudioPlaybackSchedulerTests`，51/51通过，退出0（桥接首轮）。
- 最终本轮专属：`dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~TtsPlaybackPlannerTests|FullyQualifiedName~TtsPlaybackSchedulerTests'`，51/51通过（计划28、调度23），退出0。
- 最终全方案：`dotnet test TikTokAudio.slnx -c Debug --no-restore`，Application260 + Integration189 =449/449通过，0失败、0跳过，退出0。
- 最终构建：`dotnet build TikTokAudio.slnx -c Debug --no-restore`，退出0，0警告、0错误。
- 静态检查：`git diff --check`退出0，仅既有LF/CRLF转换提示。本轮7个源码/测试/报告文件逐个执行 `git -c core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol -c core.autocrlf=false diff --no-index --check -- NUL <file>`，无空白诊断；no-index退出1表示新文件与NUL有差异，不是空白错误。首次检查脚本误将此退出1作为失败，已更正判定，无源码调整。
- 定向检索4个生产文件的 `.Result`、`.Wait(`、`GetAwaiter().GetResult(` 和 `Thread.Sleep`：仅命中SchedulerUpdate.Result数据属性，无阻塞等待。测试中阻塞取消回调是显式故障注入，使用人工门闩释放，不依赖真实长时间等待。
- 内存垂直链使用实际OperatorScriptImporter、TtsPreGenerator、TXT计划和调度器；越南语保留重音、操作员标记不进入合成、两段缓存及一次模式结束、商品边界只发一次均通过。仅引擎/缓存/输出为内存夹具，不代表真实TTS或设备验收。
- 独立审查及负责人整合通过。修复普通命令容量已满导致切换重启丢失；修复调用者先取消时未等取消回调结束即释放准备占位。调用者取消和Stop使用同一owned CTS/CancelAsync任务，注册及回调收尾完成后才释放占位与CTS；不合作旧任务不得启动重叠批次。专属竞争回归已通过。
- Scope核对通过：仅本切片7文件及四份治理；未修改冻结契约、Domain、UI、项目配置或迁移。本轮Scan → Plan → Execute → Test → Build → Verify → Update完成，T055 DONE。

## 协作与Git

Integrator独占调度/桥接/预制适配及四份治理；t055_planner只写TXT计划及其测试，t055_tests只写TXT调度测试，t055_review只读。共享目录互斥文件，仅Integrator执行test/build；保留全部既有源码/测试/报告/截图及三个旧worktree。无stage/commit/push/reset/clean；本轮无真实网络、TTS、平台或音频设备操作。
