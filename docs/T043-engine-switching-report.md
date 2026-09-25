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

## 2026-09-25 复核补齐与收口

本轮补齐并验证此前复核中登记的两个缺口：

- 新增受限外部 JSON 配置读取。配置仅支持已知 `vieneu` provider，读取 `engineId`、本机回环 `baseAddress`、输出目录、默认音色、超时和输出大小上限；地址必须是 `127.0.0.1` 或 `localhost`，不会读取凭证、下载模型或调用真实服务。加载器将现有 `VieNeuTtsOptions` 校验结果转换为 `VieNeuTtsProvider`，再装配到 `TtsEngineRegistry`。
- 互动预生成路径改用 registry-aware `TtsPreGenerator`。生成开始时同时捕获 registry revision 与 provider revision；registry 切换或旧 provider 迟到时取消/丢弃结果，不提交旧缓存。直接传入 provider 的兼容构造仍保留。

专属与相关验证命令及结果：

```text
dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~TtsPreparationTests|FullyQualifiedName~TtsEngineRegistryTests"
35/35 通过

dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~T043ConfigurationTests"
2/2 通过

dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore
567/567 通过

dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore
385/385 通过

dotnet build TikTokAudio.slnx -c Debug --no-restore
退出 0；0 warnings；0 errors

git diff --check
退出 0
```

测试使用 Fake provider、临时 JSON 和临时本地输出目录；本轮未访问真实网络、VieNeu 服务、TTS、音频设备、账号、凭证或平台。T044 第二真实引擎仍按用户指示 `DEFERRED`，因此本报告只证明配置装配和模拟/本地代码代际隔离，不宣称 AC-06 的双真实引擎验收完成。

T043 现可从 `ACTIVE` 收口为 `DONE`。下一依赖满足的最小 P0 切片为 T081；T090 仍按任务依赖和顺序不启动。
