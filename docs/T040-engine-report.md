# T040 本地越南语引擎与目标机器核对报告

任务：T040｜切片：S05｜更新：2026-09-24（Asia/Shanghai）｜状态：DONE，首引擎技术验证与用户试听确认完成。

## 当前结论

用户已明确授权下载模型、部署和配置 VieNeu。模型、独立 Python 环境与本机 API 已部署；真实离线合成和 HTTP 合成检查通过。此前缺少引擎的条件已解除。2026-09-24 用户明确反馈“人工确认，这个声音符合要求”，补齐当前样音的人工验收。T040 标记 DONE；T041 前置已满足，标记 READY，适配器仍未实施。

## 硬件与运行选择

- Acer Nitro AN515-54；Intel i7-9750H，6 核/12 线程；总内存约 7.8 GB。
- GTX 1660 Ti 6 GB，驱动 512.74；本次未升级驱动或配置 CUDA。
- Realtek 播放/录音端点曾完成只读核对；本轮未验证真实播放设备。
- 离线验证使用 ONNX、CPUExecutionProvider、FP32、6 threads；未验证 GPU 或 INT8。
- 桌面 VieNeu 0.18.3 安装在 `D:\Program Files\VieNeu`；独立 SDK `vieneu==3.8.3`、Python 3.12、`onnxruntime==1.30.0`。

## 模型、许可与本地配置

部署根为 `%LOCALAPPDATA%\LiveAudio`，以下路径均相对此目录。运行环境和模型不进入仓库。

| 项目 | 固定标识 / 证据 |
|---|---|
| Turbo 模型 | `pnnbao-ump/VieNeu-TTS-v3-Turbo` @ `8b7e9cffb4b41918cb638b9f62f0a751184d14a6` |
| Codec | `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX` @ `ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae` |
| 桌面模型 | `vieneu-desktop-models`；14 文件共 686,655,748 字节，逐一验证官方 manifest 的 SHA-256 与大小 |
| SDK 缓存 | `vieneu-cache`；模型硬链接复用、snapshot 与 refs/main 固定 revision |
| SDK 补充文件 | 同一 codec revision 的 decode_step ONNX 与 metadata；哈希记录在 `vieneu-sdk-extra-models.json` |
| 依赖锁定 | `vieneu-requirements.lock.txt` |
| 音色 | 25 个预置音色，默认 Hải Đăng |
| 模型锁定与下载证据 | `vieneu-model-lock.json`、`desktop-model-manifest.json`、`desktop-download-results.json` |

官方固定 revision 模型卡保存为 `vieneu-model-license.md`；其中标明模型/ONNX/预置音色采用 Apache-2.0，并声明预置音色生成音频可商业使用。SDK 安装包包含 Apache-2.0 LICENSE。该记录是分发声明核对，不代表本产品已经具备商业上线条件。

用户环境变量 `VIENEU_MODELS` 已设为桌面模型目录，桌面程序已用该值启动。GUI 的模型就绪、合成及可能的账户要求未验收；独立 SDK 服务已实际验证，不依赖桌面账户。

## 真实离线合成

`vieneu-local.py verify` 使用 Python audit hook 拒绝 socket connect/DNS；HF 离线缓存下生成五段越南语，检查非空、有限、非静音及 PCM16 WAV 写入/读回一致。采样率 48 kHz；自动化验证时未作人工听辨，后续用户试听结论见下节。

| 样本 | 合成秒 | 音频秒 | RTF |
|---|---:|---:|---:|
| vietnamese-01.wav | 4.8046 | 3.12 | 1.5399 |
| vietnamese-02.wav | 5.4575 | 4.00 | 1.3644 |
| vietnamese-03.wav | 2.2642 | 2.16 | 1.0482 |
| vietnamese-04.wav | 2.5368 | 2.88 | 0.8808 |
| vietnamese-05.wav | 2.0267 | 2.24 | 0.9048 |

加载 19.5002 秒，P50 2.5368 秒，P95 5.3269 秒。这是五条不同短句、包含首轮合成的小样本测量，不是稳定生产基准。部分 RTF > 1，需要预生成/缓冲，不能承诺即时播报。

样句包含越南语变音符号、欢迎昵称和商品价格；文本、帧数、RMS、SHA-256 与耗时见 `verification.json`。音频在 `samples/`。原始 `verification.json` 保留生成时的 `pending human review` 历史值；后续人工结论单独记录在 `listening-review.json`，不改写原始测量证据。

## 人工试听确认（2026-09-24）

用户原话：“人工确认，这个声音符合要求”。据此接受当前 VieNeu 样音声音（本轮默认音色 Hải Đăng），并将 T040 标记 DONE。确认来自用户，不能写成代理自行听辨，也不推断全部五条样句逐项评分或全部25个音色均已验收。

技术证据沿用已通过的模型校验、离线合成、API合成/鉴权及小样本耗时记录。本机独立证据文件为 `%LOCALAPPDATA%\LiveAudio\listening-review.json`。此结论只完成首引擎任务，AC-06 的第二真实引擎切换和最终发布产物检查仍待后续任务。

## 本机 API 验证

官方 `apps.openai_speech` 服务以正常模块导入启动；先前 runpy 引发 Pydantic 解析失败已修复，未改官方 SDK。当前只监听 `127.0.0.1:17863`，单合成流、等待队列 4、队列超时 10 秒。服务启用 HF 离线模式和关闭遥测，但未施加全面出站网络封禁。

- `GET /health`：ok、onnx、48000 Hz。
- 带本地密钥的 `GET /v1/voices`：返回 25 个音色。
- `POST /v1/audio/speech`：200；PCM 转 WAV 并验证帧数。
- 未带密钥：401；不支持的 MP3：400。
- 本地随机密钥保存于 `vieneu-api-key.txt`，未输出其值；helper 拒绝空密钥文件。
- 最终复验：API 耗时 3.3970 秒、音频 2.72 秒；见 `api-verification.json` 和 `samples/local-api-test.wav`。

## 执行命令与证据

在 PowerShell 中：

```powershell
& "$env:LOCALAPPDATA\LiveAudio\vieneu-venv\Scripts\python.exe" -m pip check
& "$env:LOCALAPPDATA\LiveAudio\vieneu-venv\Scripts\python.exe" "$env:LOCALAPPDATA\LiveAudio\vieneu-local.py" prepare-cache
& "$env:LOCALAPPDATA\LiveAudio\vieneu-venv\Scripts\python.exe" "$env:LOCALAPPDATA\LiveAudio\vieneu-local.py" verify
& "$env:LOCALAPPDATA\LiveAudio\vieneu-venv\Scripts\python.exe" "$env:LOCALAPPDATA\LiveAudio\verify-local-api.py"
```

以上均退出 0；pip 无依赖冲突，缓存准备验证 14 文件，离线验证输出 VERIFICATION PASSED，API 最终复验通过。服务命令 `vieneu-local.py serve` 常驻当前终端，健康与监听已确认。手动使用说明在部署根 `使用说明.md`。

本轮项目仅改文档，未重跑无变化的 .NET 测试。用户已确认当前样音音质；应用内真实音频设备集成、GPU、桌面 GUI、长跑/取消行为或直播平台仍未验收。下一项允许动作是登记并实施 T041 本机 HTTP 适配器；双引擎切换和最终分发检查按后续任务推进。

创建后台启动 PowerShell 脚本被自动审批拒绝，返回 `blocked by policy`；脚本未创建，也未设置开机自启。现有 Python helper 可手动启动。

`git diff --check` exited 0 on 2026-09-24; only the five authorized project documents changed. Existing untracked screenshot was preserved.
