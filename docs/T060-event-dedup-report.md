# T060 事件去重与会话状态报告

日期：2026-09-24。S14/T060 DONE；需求版本0.1.1；工作区 main/6e5dcf29ee6825e238cc9b7c17128df741dc1636。成果未提交。

## 实现范围

新增应用层 `ILiveEventProcessor`、`EventProcessResult` 与 `EventDeduplicationCoordinator`。完整 EventId 使用 `SourceId + RoomId + EventType + EventId` 严格指纹；缺少 EventId 时按源、房间、事件类型、用户、规范化内容和 UTC 时间桶生成降级短期指纹，并明确 `MissingEventId`/`Incomplete` 质量。事件记录先进入 `IStateStore`，再允许后续动作处理；同一指纹的并发注册在协调器内串行化。

Enter 和 Follow 分别使用 `SessionId + RoomId + UserId + ActionType` 业务键；缺少稳定 UserId 时跳过针对性动作并保留事件记录，绝不使用 DisplayName 代替。Comment 只按事件去重，不做用户一次性业务预留。预留状态与事件接收记录分离，Reserved 只有在完整播放后才能 Complete；取消、失败和 Unknown 不写成功。重连校验 SessionId/RoomId、保存 SessionState，不清理进程内事件和业务去重记录。

## 验证

- 专属：`dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T060EventDeduplicationTests`，11/11 通过，0 失败、0 跳过。
- 全方案：`dotnet test TikTokAudio.slnx -c Debug --no-restore`，Application271 + Integration189 = 460/460 通过，0 失败、0 跳过。
- Build：`dotnet build TikTokAudio.slnx -c Debug --no-restore`，退出0，0警告、0错误。
- 静态检查：`git diff --check` 通过；新增T060源码、测试和报告执行空白检查，无空白诊断。生产实现未发现 `.Result`、`.Wait()`、`Thread.Sleep` 或阻塞等待。

## 限制

本切片没有修改冻结 `IStateStore` 契约或数据库实现，因此跨进程重启后的业务 Completed 查询仍需后续持久化切片；当前证据是内存状态存储和应用层并发测试。未访问网络、账号、平台、真实 TTS 或音频设备。T062 再负责将预留接入实际语音调度并在播放成功后调用 Complete。
