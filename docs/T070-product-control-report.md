# T070 商品目标控制验收

2026-09-25；S18/T070 DONE；需求0.1.1；基线 main / 9912252aa6edbe2fce515dd3b193d9f29de8fce4。

## 范围与实现

新增 Application.Control 的 ProductControlModels、ProductControlCoordinator、ProductControlResults；复用冻结的 ILiveRoomController、IClock、ProductTarget、RoomOperationContext 和 ProductEpoch，无契约或项目配置变更。

本轮修改文件（既有其他未提交成果原样保留）：

- `src/TikTokAudio.Application/Control/ProductControlModels.cs`
- `src/TikTokAudio.Application/Control/ProductControlCoordinator.cs`
- `src/TikTokAudio.Application/Control/ProductControlResults.cs`
- `tests/TikTokAudio.Application.Tests/T070ProductControlTests.cs`
- `tests/TikTokAudio.Application.Tests/T070ProductControlRaceTests.cs`
- `tests/TikTokAudio.Application.Tests/T054PlaybackEffectsTests.cs`（仅异步等待修正）
- `docs/T070-product-control-report.md`
- `docs/T054-effects-progress.md`（接受决定及回归维护记录）
- `tasks.md`、`current_task.md`、`handoff.md`、`changelog.md`

- Start 只设置场次/房间，SetTarget 只更新有界最新目标，宿主显式 PumpAsync 后才提交。只有一个物理在途调用；取消/超时不释放非合作适配器占用的通道，实际结束后才可处理新目标。
- 目标/场次变化递增 ProductEpoch；提交与上下文校验在线程安全状态锁内线性化。旧结果不能更新当前确认/续显，最近一条忽略响应保留在 LastIgnoredResponse，不保存无限历史。
- 默认30秒从适配器确认被接收的单调时间起算，不从请求发起或晚到的 Pump 起算。只存一个未来续显；错过多个周期只执行一次，不补发积压。
- 每次 Pump 是一个目标合并批次：人工 > 操作员标记 > 产品进入 > 定时续显；同级保留最新。即使旧请求仍占通道，批次结束也解除来源优先级限制，后续产品进入可覆盖之前的人工目标。
- 默认5秒请求超时；结果 Unknown、异常、当前请求取消及晚确认先读回。只有完整 ProductTarget 且 IsVisible 的确认可建立新续显；无法证实则 NeedsConfirmation，不盲目重发。平台 ConfirmedAt 墙钟不驱动计时。
- 明确 Rejected 且 AllowRejectedRetry=true 时最多重试1次、间隔2秒；默认不授权重试。Unsupported 不重试。适配器未就绪时不提交写入并显式降级。
- Pause/Disconnect 保留目标但取消未来动作；Resume 必须匹配原场次/房间，先读回，再建立续显或纠正已证实的不匹配。Stop 清空场次/目标，不能 Resume 复活；新 Start 不恢复旧目标。Dispose 同样失效隔离。

## 自动化验收

使用 FakeClock、已有 SimulatedLiveRoomController 和专属内存可控控制器。65项专属测试（常规41、竞态24），无真实网络、凭据、直播间、TTS、设备或数据库操作。

| 验收点 | 证据 |
|---|---|
| REQ-CTRL-002 / AC-07模拟部分 | 产品1在0/30/60秒，80秒切产品2，90秒没有产品1动作，产品2确认后110秒续显；提前1 tick不执行 |
| 确认起算 | t=7确认、t=18才Pump，仍在t=37续显；及时确认晚Pump有效，超deadline未曾Pump也须读回 |
| REQ-CTRL-003 / AC-08模拟部分 | 非合作取消、超时、旧展示/旧读回、新房间、串行并发Pump、边界换品、跨批次优先级、单次拒绝重试 |
| Unknown降级 | 未知/不可见/目标不匹配/同本地ID不同平台ID/读回超时/无能力均不伪造确认、不盲重发 |
| AC-11应用生命周期部分 | 暂停、断线、恢复先读回、停止不可恢复、Dispose和迟到结果隔离 |

实际最终命令与结果：

```text
dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T070
退出0；65/65通过，无跳过。

dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~T070|FullyQualifiedName~T054PlaybackEffectsTests"
退出0；76/76通过，无跳过。

dotnet test TikTokAudio.slnx -c Debug --no-restore
退出0；Application439/439 + Integration206/206 = 645/645，无失败/跳过。

dotnet build TikTokAudio.slnx -c Debug --no-restore
退出0；0警告、0错误。

git diff --check
退出0；Git仅提示既有LF/CRLF转换策略，不是编译警告。
```

另对全部新增C#逐文件检查行尾空白；新控制实现无同步Task等待、真实延时、Timer、文件/网络直接访问。范围核对确认未修改冻结Contracts、Domain、共享夹具、csproj、数据库、UI、TTS或平台实现。源码复核发现的跨批次优先级问题已修复并有回归测试。

## 验证过程中的修正

- 首轮常规40/40通过，追加ID隔离用例后为41项。首轮组合65项中的16个竞态失败来自专属fake双层异步续体尚未观察结果就推进时钟；fake完成期间临时清除测试同步上下文并在finally恢复，让确认收据确定完成后才推进FakeClock，未放宽业务断言。最终65项全通过。
- 首轮全方案出现既有 T054PlaybackEffectsTests.InteractionRestoresSelectedEffectsAndSourceCursor 失败（BasePlaying，而非预期InterruptPlaying）。单独重跑1/1通过；只读诊断确认固定100次Yield不保证后台Task.Run完成，与T070无共享静态状态或调用关系。
- 已在 current_task 登记最小回归修正：仅该测试添加准备进入信号，等待真实状态条件，并用5秒测试超时防止悬挂；保留所有效果、源游标、边界断言，FakeClock不推进，未改音频实现。修正后的76项相关及645项全方案均通过。

## 使用边界与交接

宿主必须独占控制器并持续驱动 PumpAsync；Pump取消仅阻止本次调度，不撤销已提交动作，生命周期取消由Pause/Disconnect/Stop负责。异步适配器必须及时返回Task；如它永久忽略取消，通道保持占用并明确显示待确认，不为继续操作而启动第二条写通道。

此成果仅证明应用规则在模拟输入下正确，不证明真实TikTok商品展示、权限、30秒消失或账号可用。真实适配器留T071（前置T032未完成），音频/标记接线与平台映射留T072；文字、持久化、UI及长跑留对应后续切片。互动没有接入本协调器，T072不得把互动插播当作产品切换或场控暂停。

所有工作者成果已整合，所有权关闭；无提交或推送，既有T054产物与三个旧worktree保留。T073为下一最小READY（T021/T061 DONE，P0顺序先于T080）；T072不READY，T043 REVIEW、T044 DEFERRED保持。
