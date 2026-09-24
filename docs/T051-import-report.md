# T051 产品目录导入与预检查

日期：2026-09-24。切片S10，需求版本0.1.1。负责人Integrator，基线main/6e5dcf29ee6825e238cc9b7c17128df741dc1636；未提交。

## 范围与使用

`Application.Media.ProductDirectoryImportOptions`显式指定素材根目录、独立临时校验缓存目录、输入字节上限、解码字节上限及MaxEntries。没有新增产品默认数值。`Infrastructure.Media.ProductDirectoryImporter.ImportAsync`接收操作者的编号→平台商品ID映射以及可选片段相对路径→覆盖产品编号元数据，返回只读产品/组/片段快照与集中问题清单。

只枚举指定根下产品、子组、文件三级，不递归扫描磁盘或深层目录。产品/组编号为正ASCII十进制Int32，按数值排列，支持1/2/10和各产品不同组数；允许单独的前导零目录，但1与01视为重复。片段ID使用根相对路径并保留越南语Unicode，组内片段按ordinal路径稳定输出，本轮不随机抽取。

每个产品的基础映射必须明确存在；显式片段覆盖目标也须存在映射。覆盖键使用导入结果相同的相对路径、正斜杠及大小写，未知键报告InvalidMetadata，不读取任意元数据路径，不将文件名中的@[n]解释为命令。编号仅是本地标识，不把平台列表位置当作商品ID；本地映射存在不等于真实房间验证成功。

## 失败与资源策略

- 空目录、重复编号、错误层级、不支持/损坏文件、缺失映射、非法元数据集中列出。产品问题阻止该产品，重复产品编号的所有同号项均被阻止；独立健康产品保留CanStart。IsValid表示整份结果没有问题。
- 无法归属的根错误、未知元数据及扫描超限令所有产品CanStart=false，不发布可启动的部分快照。取消抛出OperationCanceledException，不返回部分结果。
- MaxEntries限制本轮累计枚举目录项，也分别限制输入映射/覆盖字典数量；每个文件按显式输入/解码字节上限完整校验，顺序释放缓存后再校验下一个文件。
- 复用T050 AudioFilePreparation：绝对本地路径、禁止越界/重解析点、独立缓存，受限解码为可定位PCM。WAV PCM/float使用托管解码；mp3/aiff/aif/wma/m4a/aac依赖系统已有解码器，无法解码明确报InvalidAudio，不声称全部格式在所有系统可用。
- 校验缓存仅临时使用并释放本次session目录；原素材不修改，返回原素材路径，实际播放仍使用T050重新准备和验证。素材导入后被修改或平台列表刷新，调用方需重新预检；本轮不做目录监视、持久化或平台连接。

## 边界

不修改T020冻结契约、已有T050实现、依赖版本/项目配置、UI或数据库。T052分组/洗牌/循环/进入事件未实现；完整AC-05待其验收。T043仍REVIEW、T044仍DEFERRED，未下载引擎、未执行设备/平台/TTS操作。本次证据限临时合成素材的本地自动化测试。

## 验证

2026-09-24负责人顺序执行，均退出0：

```powershell
dotnet build src/TikTokAudio.Infrastructure/TikTokAudio.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/TikTokAudio.Integration.Tests/TikTokAudio.Integration.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~ProductDirectoryImporterTests
dotnet test TikTokAudio.slnx -c Debug --no-restore
dotnet build TikTokAudio.slnx -c Debug --no-restore
git diff --check
```

- 首次组件编译与最终解决方案build均0警告/0错误，无新增包或项目配置。
- 专属45/45通过（无跳过），全方案Application 92/92 + Integration 177/177 = 269/269通过。
- 专属覆盖数字排序、不同组数、重复编号、非法数字、空目录、坏文件与不支持文件集中诊断、产品隔离、基础/覆盖映射、元数据无效/越界、标记文件名、扫描范围、缺失根、预先取消、输入/解码/目录项/字典上限、空帧/截断WAV、源文件不变及缓存清理。
- `git diff --check`及四个T051新增文件分别执行`git diff --no-index --check -- NUL <file>`，无空白错误；仅既有LF→CRLF策略提示。
- 独立审查已整合：显式捕获InvalidDataException，避免一个坏文件中断整份预检；重复产品编号阻止所有同号项，独立好产品可启动。测试worker与审查worker均已结束，无待整合内容。
- 所有测试仅用独立临时目录与合成WAV；无设备、TTS、平台或网络访问。导入器测试直接证明预先取消；解码中各取消时机、重解析点竞态和真实压缩格式兼容性未在本切片实测。路径检查及解码取消清理由复用T050实现和代码审查支持，不冒充新的实测证据。

T051验收通过，S10 DONE。T052依赖T021/T051均DONE，转READY且未分配；本轮停止于T051。main/HEAD未变，全部成果留在工作区，未stage/commit/push。
