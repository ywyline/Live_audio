# T050 音频输出验收报告

日期：2026-09-24（Asia/Shanghai）｜S09/T050｜DONE｜基线 main/6e5dcf2。

## 范围与授权

用户要求暂缓T044第二真实本地引擎、预留接口并优先后续开发。保留ITtsProvider以及现有引擎注册器；不下载/启动第二引擎。T050仅依赖T020 DONE，与T043剩余配置装配/预生成隔离无依赖。本轮未改Domain/Application.Contracts、Desktop、模型部署、平台行为或数据库。

## 实现

- Infrastructure/Audio：NAudioSessionFactory枚举Active Render WASAPI设备；必须指定DeviceId，失效不静默换设备。NAudio依赖固定2.2.1（MIT），基础库仍net10.0；平台API标注Windows边界。
- AudioFilePreparation：素材限定选定本地根目录，缓存根必须独立；拒绝越界、路径遍历、UNC、ADS和重解析祖先。WAV PCM/float直接解码，其他音频经Windows可用解码器（含MP3路径）；先生成独占PCM16 WAV，再允许定位/播放，不直接对不可定位压缩流猜测位置。输入与解码字节上限由调用方显式提供。
- 准备阶段不播放；源文件只读复制。每个会话拥有一个session目录，取消/失败/释放后删除自己的文件。并存准备会话默认上限2（活动+下一段），WASAPI延迟默认100ms，均是可配置工程参数，不改变业务冷却或排队规则。磁盘准入有独占锁；崩溃残留也占上限，不自动删除旧成果。最大临时磁盘量受会话数×（输入上限+解码上限+WAV头）约束。
- 源游标为每声道一组的采样帧；从WASAPI设备时钟与实际输出格式换算到源采样率，不使用解码预读位置。暂停冻结游标并释放缓冲输出；恢复从保存位置重建输出。正常结束才设末帧，设备故障保留最近有效游标并报告Error。
- SingleVoiceAudioOutput实现冻结IAudioOutput。准备失败保持原流；新音频准备成功后先停止/释放原流再启动下一条。generation使Stop、Dispose或更新请求后的迟到Prepare失效；旧会话结束/取消回调不影响新流。暂停、恢复、停止、自然结束、设备失败与释放均有明确状态。成功PlayAsync表示已开始播放，完成通过State/CurrentCursor观察。
- 环境混音开启返回Unsupported，关闭成功；效果/混音仍属T054。一个输出实例由未来Desktop组合根持有；本轮不实现基础/互动业务调度（T053）。

## 自动化验证

执行位置E:\Live_audio，SDK 10.0.401，实际命令使用 `C:\Program Files\dotnet\dotnet.exe`。

| 精确命令（省略可执行文件绝对前缀） | 结果 |
|---|---|
| `dotnet restore TikTokAudio.slnx` | 退出0，固定NAudio2.2.1依赖解析成功 |
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~AudioOutputTests\|FullyQualifiedName~AudioFilePreparationTests"` | 修复后43/43通过；最终修改由下行全套覆盖 |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 退出0；Application92/92、Integration132/132，共224/224 |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出0；0警告/0错误 |
| `dotnet build tools/T050.AudioSmoke/T050.AudioSmoke.csproj -c Debug --no-restore` | 退出0；0警告/0错误 |

新增43项，其中单路输出16项、PCM准备27项。普通自动化只模拟session、生成本地WAV和临时文件，不连网络、设备、TTS或平台。覆盖最新请求胜出、旧回调、取消、准备失败保持原音频、停止失败不启动第二条、故障位置读取不阻碍释放、Dispose仍取消准备、单声道/双声道及多采样率、float转PCM、坏/截断文件、大小上限、容量、路径边界与缓存清理。

整合时修复了NAudio/Domain PlaybackState命名冲突、InvalidDataException结果映射以及设备故障误记末帧等问题；最终测试/构建是修复后的证据。

## 独立真实设备检查

工具不加入解决方案或普通dotnet test。无参数仅打印用法；需要显式 `--verify --device` 才发声。使用振幅0.006的330Hz两秒合成测试音，未调用TTS/平台。

执行命令：

```powershell
dotnet run --project tools/T050.AudioSmoke/T050.AudioSmoke.csproj -c Debug -- --list
dotnet run --project tools/T050.AudioSmoke/T050.AudioSmoke.csproj -c Debug --no-restore -- --verify --device '{0.0.0.00000000}.{e06b6a90-064a-4fd2-8ec1-6d607142fb60}'
```

两项最终退出0。枚举为扬声器（Realtek(R) Audio）。第一次检查停止是在暂停态，后续补强为恢复并实际播放120ms后Stop；下表是补强后的最终输出，不把暂停停止耗时充当正在播放的停止耗时。

| 观察 | 最终值 |
|---|---|
| 源采样率/总帧 | 24,000 Hz / 48,000 |
| 暂停源帧 | 7,249 |
| 等待150ms后源帧 | 7,249（冻结） |
| 恢复播放再暂停 | 13,226 |
| 重新打开并从7,249帧播放200ms后 | 12,063 |
| 播放中停止耗时 | 18.5297 ms（本次样本，低于500ms） |
| 自然结束源帧 | 48,000；State=Idle |
| Stop后自有session缓存 | 全部释放 |

合成测试音保留于 `C:\Users\ywyline\AppData\Local\Temp\LiveAudio-T050-174fa2831abe4267969fba74d205758a`；无常驻测试进程。只证明该设备这次播放/时钟定位/恢复，不证明全部设备、压缩编解码器或人工音质/录音回测误差。MP3/其他格式依赖本机解码器，未用真实压缩素材做本轮验收；格式不支持则明确失败。高阶变速源位置映射留T054，不能以此次通过宣称完整AC-04/06。

## 静态检查与治理

git diff --check及新增T050文件逐项空白检查无错误（LF/CRLF提示为既有Git策略）。T044 DEFERRED，恢复仍须用户安排；完整双引擎AC-06/T101依赖不绕过。T043注册器单元功能保留，但由于revision未接预生成器、外部地址配置尚缺，原DONE声明撤回为REVIEW，报告已更正。

T050 DONE；下一最小独立任务T051 READY（仅依赖T020），本轮不提前实现。main工作区保留原T043未提交成果；未stage/commit/push，旧worktree未删除。负责人已整合audio_backend四份文件、完成审查修复与验证，无待整合补丁。
