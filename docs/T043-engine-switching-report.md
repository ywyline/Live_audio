# T043 多引擎配置与切换验收报告

日期：2026-09-24

## 已实现的局部功能（T043 REVIEW，非完整验收）

- 新增 `TtsEngineConfiguration`，以配置注册多个已部署的 `ITtsProvider`；引擎实现、推理环境和模型继续独立于 Live_audio EXE。
- 新增 `TtsEngineRegistry`，提供活动引擎快照、默认音色、配置查询和线程安全切换。
- 每次真正切换引擎时递增 `EngineRevision`；无播放占用时相同引擎切换为幂等操作，未注册引擎返回 `Unsupported`。
- 播放占用期间拒绝切换并保留当前引擎和 revision；播放占用结束后可以切换。实际音频播放驱动仍属于后续 T050/T055，当前用显式播放占用计数表达边界。
- 现有 `TtsPreGenerator` 仅核对其固定 provider 的ID/revision，注册器revision尚未传递进去。因此旧provider晚返回仍可能发布；此前“已隔离”结论撤回，待补跨模块实现与竞态测试。

## 验证

- `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore`：92/92 通过。
- `dotnet test TikTokAudio.slnx -c Debug --no-restore`：应用 92/92、集成 89/89，总计 181/181 通过。
- `dotnet build TikTokAudio.slnx -c Debug --no-restore`：0 警告、0 错误。
- `git diff --check`：通过。

专属测试覆盖配置标识与 provider 一致性、引擎切换递增 revision、未知引擎、相同引擎幂等、播放占用期间阻止切换及占用结束后重试。

## 边界

本切片没有接入第二个真实引擎，没有修改 Desktop UI，也没有开始真实设备播放或平台操作。引擎地址、模型和运行时仍由外部独立部署提供；下一切片 T044 负责第二个真实本地引擎和往返切换实测。

## 2026-09-24 复核更正

进程内注册provider实例已实现；外部配置地址读取/provider装配、全链代际失效、自动等待当前片段结束后切换尚未完成。四项注册器测试不能证明这些验收条件，T043从DONE更正为REVIEW。保留源码及测试，不在T050夹带修复。T044按用户要求DEFERRED，现有ITtsProvider扩展接口保留。上述181项测试证据仍有效，但不代表T043全验收。
