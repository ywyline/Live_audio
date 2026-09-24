# T052 预制音频播放计划器

日期：2026-09-24。S11/T052，需求0.1.1，基线main/6e5dcf29ee6825e238cc9b7c17128df741dc1636。负责人Integrator，未提交。

## 范围与调用约定

新增Application.Playback.PreRecordedPlaybackPlanner，实现冻结IPlaybackPlanner；输入T051素材快照、显式PlanRevision和IRandomSource。仅接受无预检查问题的明确素材集合，不能偷偷排除坏产品。计划启动额外核对每个产品具有子组1，否则明确拒绝，避免无法触发n-1而沿用上一商品；这不改变T051导入结果，也不要求组号全部连续（1/3/7有效）。产品/组按数字排序，各组每次进入选一片，各组袋内全部用完再洗牌；多片组跨袋不相邻重复，单片允许重复；不同组数按完整目录顺序循环，Cycle从0开始。

- 首次SelectNextAsync上下文为PreRecorded、当前修订、空产品/组、Cycle0；后续上下文须匹配当前item的ProductId/GroupId/Cycle。不匹配返回null，取消抛OperationCanceledException，均不推进。
- ProductId是本地产品编号的Invariant字符串；GroupId是导入的子组相对路径；ClipId沿用根相对素材路径。ProductBoundary.ProductId是显式映射的平台商品标识，与现有稿件边界语义一致。
- SelectNext表示调用方主动前进一个基础组。准备失败需要重试同片时重用CurrentItem，不再次Select。当前素材不读盘，不启动音频或平台。
- 只有n-1生成隐式产品进入候选；片段操作者覆盖元数据优先于隐式编号。每轮候选ID包含目录指纹、修订、Cycle、组位置和片段ID，恢复同片保持不变。
- 只有在音频成功开始后，调度调用方才可调用AcknowledgePlaybackStarted；它一次返回并消费边界，重复确认返回空集合，旧item确认失败。Select中的Boundaries只是候选，不能直接视为已开始事件。T053负责真实音频开始联动，T070负责场控，不在本轮执行。
- 每组选片使用独立随机状态，效果种子使用单独状态；只在初始化/替换计划通过IRandomSource取得种子。采用明确保存状态的确定性洗牌算法，快照恢复不依赖另一个Random实例已调用多少次；算法版本纳入目录指纹版本。

## 恢复与计划修订

冻结PlaybackCheckpoint没有完整洗牌历史。新增PreRecordedPlannerSnapshot保存当前checkpoint、每组剩余片段/最近片段/随机状态、效果随机状态及目录指纹；不修改Domain或共享接口。

CaptureSnapshot验证并保存源帧游标，返回复制后的只读集合；游标须在片段范围且采样率匹配（零起点可省略采样率）。RestoreCheckpointAsync只恢复本实例当前已选择的同一片段、轮次、种子、修订，保留所有袋；新实例必须先RestoreSnapshot。完整快照先校验目录指纹、全部组恰好一次、剩余数量/片段归属/最近抽样/当前位置一致性、游标与已消费边界，然后原子更新；失败保持原状态。

恢复后下一次Select返回原片段，不前进、不抽样、不重新生成效果种子、不重发已消费边界；再下一次Select正常前进。恢复不会自动播放或调用平台。同实例拒绝快照回退到已越过的组/轮；同item恢复合并已消费边界，防止旧快照重放。跨进程外部副作用账本及崩溃后的人工恢复预览仍由后续持久化/集成任务完成。

ReplacePlan只接受严格递增修订；验证新计划后清空旧片段/袋/游标状态，旧上下文、checkpoint、snapshot和item确认被拒绝。指纹覆盖排序后的组、所有片段/素材定位与参数、覆盖和显式映射；并非素材内容哈希，不替代T050播放前重新校验实际文件。

## 验证及限制

2026-09-24负责人顺序执行：

```powershell
dotnet build src/TikTokAudio.Application/TikTokAudio.Application.csproj -c Debug --no-restore
dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~PreRecordedPlaybackPlannerTests
dotnet test TikTokAudio.slnx -c Debug --no-restore
dotnet build TikTokAudio.slnx -c Debug --no-restore
git diff --check
```

以上命令均退出0。专属66/66通过；全方案Application158/158 + Integration177/177 = 335/335通过，无跳过。组件build与最终解决方案build均0警告/0错误。6个本轮新文件分别执行`git diff --no-index --check -- NUL <file>`，仅文件差异返回1，无空白错误；既有LF→CRLF策略提示不属于编译警告。

测试覆盖数字排序/不同M完整循环、两组60轮独立袋及跨袋不重复、单片循环、播放开始确认幂等/旧确认拒绝、取消与旧上下文不推进、跨不同随机源实例恢复后连续60次选片及效果种子一致、游标/边界消费保持、坏快照原子拒绝、同修订素材/映射变化拒绝、单独checkpoint缺历史拒绝、严格递增计划替换、输入集合隔离、缺组1拒绝及1/3/7仍可循环。

独立只读审查通过；缺组1的启动前置校验已实现并测试，未更改T051导入器。专属测试worker成果已整合，所有分配结束，无待整合补丁。普通测试仅使用内存素材和T021注入随机；本轮新增测试无真实文件、设备、网络、TTS或平台访问。AC-04在本轮只验证逻辑源游标/片段/种子保持，不能替代真实音频恢复误差验收。

Scan → Plan → Execute → Test → Build → Verify → Update完成，S11/T052 DONE。下一最小切片T053 READY，T021/T050/T052均DONE，尚未启动。main/HEAD未变，无stage/commit/push，所有既有未提交成果与旧worktree保留。

T043 REVIEW、T044 DEFERRED保持；不实现T053调度、T054音效、T055稿件模式或持久化/UI。没有修改冻结契约、旧素材/音频实现、依赖包或项目配置。
