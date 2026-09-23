# T040 本地越南语引擎与目标机器核对报告

任务：T040  目标硬件、引擎许可和本地运行条件核对
切片：S05
日期：2026-09-18（Asia/Shanghai）
状态：ACTIVE（缺少已授权的真实越南语引擎，尚未满足 AC-06）

## 结论

目标机器具备本地音频播放/录音端点和可用于本地推理的 CPU/GPU，但当前没有发现可验证的越南语 TTS 引擎、越南语音色、模型、loopback HTTP 服务或已知引擎许可证。因此本切片不能形成真实试听样例、音频资产或 P50/P95 合成耗时证据，不能将 Windows 中文/英文 SAPI 音色当作越南语支持。

## 硬件与音频端点

| 项目 | 只读核对结果 |
|---|---|
| 设备 | Acer Nitro AN515-54 |
| CPU | Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz；6 核 / 12 线程 |
| GPU | NVIDIA GeForce GTX 1660 Ti；驱动 30.0.15.1274；另有 Intel UHD Graphics 630 |
| 内存 | 7.8 GB（操作系统报告的总物理内存） |
| 播放端点 | 扬声器 (Realtek(R) Audio)，AudioEndpoint 状态 OK |
| 录音端点 | 麦克风阵列 (Realtek(R) Audio)，AudioEndpoint 状态 OK |

硬件核对只能说明具备本地音频输出基础，不能说明目标引擎或模型可用。

## 引擎与许可核对

- Windows SAPI 已安装音色：`Microsoft Huihui Desktop`（`zh-CN`）和 `Microsoft Zira Desktop`（`en-US`）；没有 `vi-VN` 音色。
- 未发现 `piper`、`tts`、`espeak`、`espeak-ng`、`ffmpeg`、`sox` 或 `aplay` 命令。
- Python 3.12 可用，但未安装 `TTS`、`piper`、`pyttsx3`、`onnxruntime`、`espeakng`、`edge_tts`、`soundfile` 或 `torch` 包。
- 未发现本地 TTS 服务、越南语模型目录或可核对的引擎/模型许可证。未读取凭据，未下载模型，未安装软件，也未调用云端 TTS。

## 2026-09-18 复核证据

- 枚举了 64 位、32 位和 OneCore SAPI 注册表路径；结果仍仅有 `en-US` Zira 及中文 Huihui/Kangkang/Yaoyao，两个当前用户 SAPI 路径不存在，未发现 `vi-VN` 音色。
- `Get-Command -All` 未找到 `piper`、`tts`、`espeak`、`espeak-ng`、`festival`、`flite`、`rhvoice` 及对应可执行文件；卸载注册表中也没有匹配的本地 TTS 产品登记。
- `py -3.12 -m pip show TTS piper-tts piper pyttsx3 onnxruntime espeakng edge-tts soundfile torch` 未返回任何指定包元数据。
- 本次只执行本机只读检查；没有下载、安装、启动引擎或服务、调用合成、访问网络或读取凭据。

## 未执行与原因

- 未生成越南语音频、未做音色试听、未测 P50/P95：当前没有越南语引擎和音色可运行。
- 未实现 T041 loopback HTTP 适配器：T041 依赖 T040 DONE，当前不得提前开发。
- 未运行真实音频播放验收：没有可播放的越南语合成资产；现有端点核对为只读证据。

## 最小解除条件

提供一个明确授权的本地越南语 TTS 引擎及其模型/音色来源、版本和许可证，或提供已安装的本地 loopback 服务地址、健康检查方式和测试许可。获得输入后，在本机生成固定越南语样句（含变音符号和昵称），记录实际引擎/模型标识、试听结果、合成样本和 P50/P95，再决定是否将 T040 标记 DONE。

## 2026-09-19 verification update

- Read-only recheck: .NET SDK 10.0.401, Windows 10 x64 build 19045, and the existing Realtek playback/recording endpoints remain available.
- Dedicated tests: `dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore` exited 0 with 5 passed and 0 failed.
- Solution tests: `dotnet test TikTokAudio.slnx -c Debug --no-restore` exited 0 with 5 passed and 0 failed.
- Build: `dotnet build TikTokAudio.slnx -c Debug --no-restore` exited 0 with 0 warnings and 0 errors.
- No authorized Vietnamese engine, model/voice, license, synthesis sample, listening result, or P50/P95 evidence was supplied or found. T040 remains ACTIVE; no download, installation, service startup, network TTS call, credential access, or platform side effect was performed.
