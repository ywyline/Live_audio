# T041 首个本机 HTTP TTS 适配器验收

日期：2026-09-24（Asia/Shanghai）｜S06 / T041 DONE｜需求版本0.1.1｜基线e67e2e5。

## 实现与部署边界

`VieNeuTtsProvider`实现已冻结的`ITtsProvider`，提供健康、音色、合成、取消和明确的失败结果。Domain/Application契约不变；Desktop尚未接线。Infrastructure只包含C# HTTP适配器和WAV封装，不包含VieNeu推理实现、Python运行时或模型权重，也不增加运行时NuGet依赖。正式发布包检查仍归T104。

现有引擎独立安装于`%LOCALAPPDATA%\LiveAudio`，服务地址`http://127.0.0.1:17863/`，密钥从本地`vieneu-api-key.txt`读取，不写入仓库。SDK/模型固定版本与哈希、既有人工试听证据见`docs/T040-engine-report.md`。本切片没有重新下载模型、改变部署或启动第二份服务。

调用示例（由后续组合根提供配置与安全读取的密钥）：

```csharp
using var provider = new VieNeuTtsProvider(new VieNeuTtsOptions
{
    Endpoint = new Uri("http://127.0.0.1:17863/"),
    OutputDirectory = selectedAbsoluteOutputDirectory,
    ApiKey = locallyLoadedApiKey
}, new EngineRevision(1));
var outcome = await provider.SynthesizeAsync(
    new TtsSynthesisRequest("Xin chào mọi người.", "Hải Đăng", "vi-VN",
        1, 0, 1, provider.Revision), cancellationToken);
```

## 能力与结果语义

- 地址只接受根路径的http/https回环IP或localhost；拒绝用户名、查询和片段。localhost固定转127.0.0.1，正式HTTP禁代理、禁重定向。
- 健康检查要求`/health`的status=ok、sample_rate=48000，并检查`/v1/models`声明包含`vieneu-v3-turbo`；合成前同样检查模型。模型权重哈希沿用T040，服务声明不等于重新验证权重。
- 音色从`/v1/voices`读取；越南语vi/vi-VN，NFC规范化保留重音；稿件最多20000个UTF-16代码单元、音色ID最多256。
- Rate=1、Pitch=0、Volume=1为中性值；其他参数/语言明确Unsupported。当前官方服务未实现这些效果参数，不静默忽略。
- 每个provider只有一个合成；忙时立即Failed，没有无界排队、自动重试或云端回退。构造时固定EngineRevision，旧版本请求在HTTP前Cancelled。跨provider的切换协调仍归T043。
- 默认超时60秒，覆盖模型发现、响应头、正文读取与文件提交；允许配置至1小时。默认音频上限32MiB，配置范围2字节至256MiB；JSON最多256KiB、深度16、音色最多1024且ID唯一。
- `/v1/audio/speech`请求pcm/audio流/48000Hz。合法但不支持的音频格式或采样率为Unsupported；缺失/非法头部、非音频正文、空PCM、奇数字节、超限、已声明长度不符为Failed。
- 用户取消音色查询/合成返回Cancelled；健康接口无取消状态，调用方取消传播OperationCanceledException。超时、网络、IO或损坏数据返回Failed/Unavailable；HTTP404/405/501返回Unsupported。Detail不暴露稿件、密钥、原始响应或异常消息。

## 资产生命周期

PCM16小端单声道48kHz写入选定目录的随机`.part`，验证后生成44字节标准WAV头、flush并原子更名`.wav`，提交后再次检查取消。失败/取消清理本请求资产；清理出错会明确Failed。成功资产交给调用方持有和后续清理。本切片没有自动缓存、分段、预生成或播放调度。

原始PCM在没有Content-Length时只能验证非空、偶数字节及大小界限，无法证明服务完整朗读了稿件。文件结构有效也不代表音质已获人工接受。

## 验证证据

工具链.NET SDK10.0.401；以下命令在`E:\Live_audio`执行，dotnet完整路径为`C:\Program Files\dotnet\dotnet.exe`。

| 命令 | 结果 |
|---|---|
| `dotnet restore tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj --ignore-failed-sources` | 退出0 |
| `dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore` | 退出0，50通过/0失败 |
| `dotnet test TikTokAudio.slnx -c Debug --no-restore` | 退出0，共55通过/0失败（原5+新50） |
| `dotnet build TikTokAudio.slnx -c Debug --no-restore` | 退出0，0警告/0错误 |
| `git diff --check` | 退出0 |

普通测试仅使用内存HTTP、受控流与独立临时目录，不访问网络、真实引擎或音频设备。覆盖Unicode、Bearer、模型核验、WAV头、音色、旧revision、能力不匹配、错误脱敏、重定向响应拒绝、长度/格式/限额、读写失败、取消清理、超时、busy及取消后复用。流超时测试使用100ms取消期限和5秒失败保护；其余竞态通过显式信号协调，无sleep。

独立审查先发现合法不支持音频的状态映射问题，已修复并补充测试；测试发现InvalidDataException未纳入预期异常映射，已修复。最终只读复核未发现新的阻塞问题。

### 显式真实本机验证

本机独立验收程序位于`%LOCALAPPDATA%\LiveAudio\t041-adapter-check`，引用当前Infrastructure工程，不属于普通测试：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project "$env:LOCALAPPDATA\LiveAudio\t041-adapter-check\AdapterCheck.csproj" -c Debug
```

退出0，结果保存到同目录`result.json`，实测时间2026-09-24 00:53（+08:00）：

| 项目 | 结果 |
|---|---|
| 健康 / 模型 | Healthy / vieneu-v3-turbo，revision=1 |
| 音色 | 25个，实际使用Hải Đăng |
| 生成 | Succeeded；本次端到端8.0974秒，不是P50/P95 |
| WAV | 337964字节，48kHz / 单声道 / PCM16，3.52秒，有非零PCM |
| 文件 | `audio/694bf6b05ea0442eb3f1731104a19c80.wav` |
| 取消 | 请求前已取消→Cancelled，无新增文件；没有声称是真实中途取消 |

另由部署venv的Python标准库`wave.open`独立读取上述WAV，退出0，168960帧、337920数据字节、采样率48000、声道1、样本宽2字节。C#程序同时检查RIFF/fmt/data头、长度和非零数据。

真实调用只向现有本机服务发送合成样句；没有播放设备输出、直播或商品操作。本轮没有对新样音做人耳试听；人工声音接受依据来自T040。模拟测试验证了客户端中途取消及文件清理，不证明服务端推理立即停止，现有服务没有单独的取消确认接口。

## 整合与停止点

测试工作者基于e67e2e5，在`task/t041-tts-tests` / `E:\Live_audio_t041_tests`只生成专属测试文件，没有提交；主会话已复制、修正预期、测试并完成整合。该工作树保留，仅供追溯，主工作区版本是已验收版本。

T041完成；T042因T021/T041完成而READY，待下一切片登记。T043引擎切换编排、T044第二真实引擎、Desktop选择界面、播放和完整AC-06均未完成。本轮未commit/push，保留此前文档改动和`vieneu-window.png`。
