# T073 文字调度验收

2026-09-25；S19/T073 DONE；需求0.1.1；main / 9912252aa6edbe2fce515dd3b193d9f29de8fce4；SDK10.0.401。

## 本轮文件

- `src/TikTokAudio.Application/Control/TextDispatchModels.cs`
- `src/TikTokAudio.Application/Control/TextDispatchCoordinator.cs`
- `src/TikTokAudio.Application/Control/TextDispatchResults.cs`
- `src/TikTokAudio.Application/Control/TimedTextPlanner.cs`
- `src/TikTokAudio.Application/Control/TimedTextContent.cs`
- `src/TikTokAudio.Application/Interactions/InteractionRuleCoordinator.cs`（文字清理与在途租约保护接缝）
- `tests/TikTokAudio.Application.Tests/T073TextDispatchTests.cs`
- `tests/TikTokAudio.Application.Tests/T073TextDispatchRaceTests.cs`
- `tests/TikTokAudio.Application.Tests/T073TimedTextTests.cs`
- `tests/TikTokAudio.Application.Tests/T062InteractionPlaybackTests.cs`（仅Until测试等待）
- `docs/T073-text-dispatch-report.md`
- `tasks.md`、`current_task.md`、`handoff.md`、`changelog.md`

其他T054/T070未提交产物原样保留；无冻结Contract、Domain、共享夹具、项目/包、Audio、TTS、UI、平台适配器或迁移修改。

## 实现与验收边界

REQ-CTRL-004 / AC-10应用模拟层：

- 手动、定时与T061关键词文字共用有界FIFO和唯一物理在途请求。默认总间隔30秒，与调用方提供的已验证平台下限取较大值；每次实际提交起算，失败/Unknown也消耗间隔，不能靠暂停、恢复或新Start绕过。
- 定时计划支持多任务、顺序循环或注入随机、多条话术、UTC时间窗口和启停。间隔使用单调时钟；每次启动/恢复从未来重新计时，迟到Pump每任务只产生一次然后安排未来，不补断线积压。窗口排他结束；已入FIFO文字在窗口关闭后也不得发送。
- Configure完整验证、复制列表后原子替换，同ID重配置会清除旧配置的待发文字。随机可用种子复现；恢复保留顺序游标，重配置重新开始序列。
- 手动与定时正文是操作员文字，只做NFC/Trim、非空、UTF-16/长度与控制字符校验，保留价格货币、标点及字面标记，不解析商品动作。关键词复用T061已渲染的安全文字。
- 发送前重新校验关键词租约/TTL/场次/房间，使用BeginTextSend保护已提交文字；新评论仍替换旧未开始语音，但不丢失旧文字结果。确认时立即MarkTextCompleted，不因晚Pump推迟规则文字冷却；文字不启动或重置K/V/W。
- Pause/Disconnect/Stop与Resume均清理文字待发项，包括T061冷却中不可选择的项和断线期间新到的文字。只释放文字渠道，不清语音候选/已开始租约或语音冷却；Stop后不能Resume，必须显式Start。
- 超时、异常、取消及无定义状态按Unknown保守记录；不重发原条，不用商品可见状态推断文字成功。非合作取消保持物理通道，后续独立消息只能在旧调用实际结束且总间隔满足后提交。旧场次/晚响应只留有界诊断，不复活已停止的任务。
- 复用冻结ActionLedgerEntry形成有界待移交结果账本，实际提交前预留一个结果位置；满则背压，DrainLedger后才继续。超时与迟到结果不重复占槽，不记录评论正文或异常原始消息。结果按实际确认/Unknown/Rejected/Unsupported区分，不伪造成功。

容量、请求超时、手动/定时TTL由调用方显式提供，没有新设业务默认；平台限额未实测，模拟测试的下限配置不代表真实许可。

## 接缝与验证过程

审查发现T061原来只有VoiceStarted保护：新评论替换或TTL清理可移除正在发送的textLease，使旧发送Confirmed漏记独立TextCooldown。已登记并实施最小BeginTextSend/TextStarted接缝，保护结果回收同时释放旧未开始语音。专属回归验证在途替换、发送期间TTL到期、晚Pump不改变确认计时、文字单开以及voice最新候选正确。

T062相关专属首跑7/8通过，MismatchedEngineRevisionIsCancelledWithoutPlaybackOrCompletion在Harness.Until失败；随后全方案772/772通过。只读诊断确认固定1000次Yield不能保证后台Task.Run已完成，且该测试TextEnabled=false，新在途文字逻辑不参与。已先登记最小范围，仅将Until改为真实predicate等待及5秒测试悬挂保险；业务FakeClock不推进、功能断言和T062实现不变。最终相关219/219及全772/772通过，未忽略首轮失败。

## 实际命令与结果

所有test/build由Integrator顺序运行；工作者只在互斥源码/专属测试路径编辑，复核代理只读。

```text
dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T073TimedTextTests
退出0；55/55。

dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T073
退出0；127/127（定时55 + 通道52 + 竞态20）。

dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~T061|FullyQualifiedName~T062|FullyQualifiedName~T073"
退出0；219/219（含T061 84、T062 8），无跳过。

dotnet test TikTokAudio.slnx -c Debug --no-restore
最终退出0；Application566/566 + Integration206/206 = 772/772，无失败/跳过。

dotnet build TikTokAudio.slnx -c Debug --no-restore
最终退出0；0警告、0错误。

git diff --check
退出0；仅既有Git LF/CRLF转换策略提示，不是编译警告。
```

额外静态检查：新实现无同步Task等待、真实延时/Timer、文件或网络直接访问；新增源码/测试逐文件尾随空白检查通过。三名工作者成果已整合、写入分配关闭，无待整合产物。

## 使用与交接

宿主必须先设置T061场次并通过T060去重后入队，持续驱动PumpAsync，只有本协调器能发送文字；不要另行领取/完成其文字租约。普通Pump取消只取消该次调度，已接收请求的生命周期由Pause/Disconnect/Stop控制。永久不响应且不合作取消的适配器将占用通道，不为推进而打开第二通道。

宿主负责消费T061的DrainDiscards以及本协调器DrainLedger；有界账本尚在内存，Drain代表移交而不是已经持久化。T080通过IStateStore.AppendActionLedgerAsync持久化时应保留失败的移交记录，不能自动重放未知平台动作；本切片不增加SQLite或自动恢复。

此成果不证明真实TikTok发言权限/限额/送达。T074仍依赖T071，真实账号/平台写入未获新授权；T030 BLOCKED、T043 REVIEW、T044 DEFERRED保持。下一最小READY为T080（前置T020 DONE），本轮未启动。main/HEAD未变化，全部既有修改与三个旧worktree保留，无stage/commit/push/reset/clean/覆盖checkout。
