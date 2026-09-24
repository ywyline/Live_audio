# T053 基础与互动音频调度

日期：2026-09-24。S12/T053，需求0.1.1，基线main/6e5dcf29ee6825e238cc9b7c17128df741dc1636。状态DONE，负责人Integrator；工作区整合完成，尚未提交。

## 范围与调用约定

Application.Playback.AudioPlaybackScheduler独占一个PreRecordedPlaybackPlanner和IAudioOutput。基础仅接预制模式；TXT基础模式接入与切换属于T055，事件去重、K/V/W和欢迎/关键词最新候选规则属于T060/T061/T062。本轮不触发平台、TTS、设备、UI、数据库或网络操作。T062接入约定：规则层保留各类别候选/关注队列，同一类别最多提交一个尚未终结的请求；收到终态并满足该类冷却后才能提交下一条。否则调度层不能代替规则层保证K/V/W。

- StartAsync显式启动；QueueInteractionAsync准入后在有界后台准备，不等待合成完成、不先暂停基础。InteractionRequest含请求/场次ID、优先级、ReceivedAt单调时刻、TTL及异步准备委托。上游应在接收时将新鲜度映射到同一IClock时间轴，不能在合成时重新开始TTL。
- 宿主必须串行调用PumpAsync（准备结果、设备状态变化或有界轮询时）；该对象不自动创建后台轮询。普通命令最多准入capacity+1个（含执行中的命令），满额返回忙，避免无界命令队列；宿主需要处理返回状态，不可忽略失败。Start/Pump/Pause/Resume的调用令牌只控制命令准入；命令接受后由播放生命周期、Stop或场次变更控制，不承诺调用令牌在操作中途取消输出。Queue令牌持续控制对应互动。Stop和ChangeSession即时使旧工作失效。
- 关注感谢>关键词>欢迎；只选择已就绪且仍合格的下一条，互动不互相打断。准备失败/取消/过期报告终态，基础继续。准备前和播放前重新检查TTL/场次/取消，等于TTL视为过期；等待输出Play开始期间也使用IClock剩余TTL取消截止，防止输出打开/解码阶段到期后仍出声；成功开始后取消截止监控，已播放互动不因TTL到达而强制结束。
- 准备上限默认22（与现有20关注+1欢迎+1关键词的上游上限对应），构造可指定1–1024。调度仅做总容量准入；T061负责各类业务队列。忽略取消且未结束的准备仍占用容量，跨Stop/Start不能无限积累。
- 插播前PauseAsync并读取暂停后的稳定源帧，检查输出状态，保存T052快照游标；结束或失败使用同一基础资产/源帧恢复。不重新选片、不更换效果种子、不调用RestoreCheckpoint制造二次选片，也不重放已消费边界。仅基础自然结束推进组/循环。
- 基础Play成功表示开始，之后调用T052 AcknowledgePlaybackStarted；返回SchedulerUpdate.Boundaries只含这次确认的边界，消费一次。Interactions仅报告终态并携带原SessionId，自然结束才Succeeded，播放启动不计成功。停止/换场丢弃过期商品边界，但不能丢弃已确定的互动终态；上游须根据原SessionId处理去重/Reserved账本。调用方仍必须通过后续唯一场控/动作账本处理外部副作用；不能直接在音频代码操作商品。
- Pause取消未播放互动并暂停当前主语音；Resume恢复当前角色。换房间/场次取消旧互动，保留基础；Stop清空并失效待执行工作，必须显式Start才可再次出声。旧输出回调、准备结果和取消任务不改变新会话状态。

## 当前切片所需的最小音频依赖调整

审查发现T050即时Pause不包含REQ-AUD-002要求的短淡出。Integrator登记最小范围调整：冻结IAudioOutput和IAudioPlaybackSession不变，新增可选IFadingAudioPlaybackSession及AudioFade，SingleVoiceAudioOutput.PauseAsync在锁外淡出后再暂停，并校验源会话/代次/调用取消；NAudioSessionFactory在WASAPI Shared模式对本流AudioStreamVolume执行5步×6ms的短淡出。没有修改设备主音量或其它应用音量，也没有提前实现T054随机效果。

音量操作依据现有NAudio2.2.1本机包的XML API说明，未升级依赖。每步检查实际会话及代次；停止不等待淡出。新Play仍在准备或调用取消时，旧Fade完成会恢复尚在播放的原流音量，避免准备失败后永久静音。暂停后释放旧output，重新播放使用新output，不能继承零增益。

5×6ms是配置的技术过渡时长，不是实测设备耗时保证。真实抢占≤500ms、恢复误差≤100ms和淡出听感须在后续本地集成验收记录；本轮不启动音频设备。

## 审查与验证证据

- 独立测试工作者只写AudioPlaybackSchedulerTests；独立审查只读需求/契约/相关实现；Integrator独占音频依赖调整、集成验证、报告和四份治理。共享目录互斥文件，只有Integrator运行test/build。
- 初次调度测试38/39通过；失败用例的模拟输出无视Stop代次、在旧Play迟到后无条件出声，与T050输出契约不符。保留该场景，将模拟器按真实SingleVoice的代次/取消语义修正，并新增真实SingleVoice+内存工厂的集成回归（停止以及重新启动后迟到准备）。未删除场景、未用补偿Stop误停新音频。
- 最终独立只读复核通过；TTL覆盖输出异步打开/解码阶段，成功开始后结束期限监测。竞态修复包括终态通知保留、原SessionId归属、Stop屏障复检、旧任务隔离和淡出失效后的音量恢复。
- 以下命令均由Integrator顺序执行，退出0，无跳过；普通测试无真实设备、网络、TTS或平台操作。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AudioPlaybackSchedulerTests` | 51/51通过 |
| 音频相关测试（精确命令如下） | 26/26通过 |
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AudioSchedulerIntegrationTests` | 2/2通过 |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | Application209 + Integration189 = 398/398通过；本轮新增63项 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 0警告、0错误 |
| `git diff --check` | 退出0；仅Git既有LF/CRLF转换提示 |


音频相关测试精确命令：

```powershell
dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~AudioFadeTests|FullyQualifiedName~AudioOutputTests'
```
新增及本轮修改的10个切片文件逐个执行 `git -c core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol -c core.autocrlf=false diff --no-index --check -- NUL <file>`，接受Windows CRLF，均无空白问题。最初强制autocrlf=false且未启用cr-at-eol时将行尾CR识别为空白；采用显式CRLF规则后通过，未为此改写文件。定向静态检索未发现新增同步阻塞Wait/GetResult/Thread.Sleep或真实等待测试；SchedulerUpdate.Result为结果模型访问。文件范围及冻结契约/项目配置未改核对通过。

## 后续限制

本轮证据属于内存规则及设备隔离的集成层，不代表真实平台/设备验收完成。不执行云端合成，不下载第二引擎或将引擎/模型加入EXE。T043 REVIEW、T044 DEFERRED保持。既有未提交源码、报告、截图和三个旧worktree全部保留，未stage/commit/push。
