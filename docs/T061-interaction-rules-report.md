# T061 互动规则报告

日期：2026-09-24｜S15/T061 DONE｜需求版本0.1.1｜main/6e5dcf29ee6825e238cc9b7c17128df741dc1636。成果未提交。

## 实现与验收

新增 Application/Interactions 的 InteractionModels、InteractionRuleCoordinator，以及 T061InteractionRulesTests。冻结共享契约、Domain、项目配置、数据库和UI均未改动。

- K/V/W 默认15/10/10秒，允许0–3600秒，从当前有效语音完整播放成功结束起按 IClock.Now 计时；三类独立，关键词同时满足 W 和规则冷却。失败、取消、重复/旧完成回调不启动或延长冷却。
- TTL 默认20/60/30秒，入队按 ReceivedAt 扣除已过去时间，剩余寿命固定为单调时钟期限；到期等号即过期。未来 ReceivedAt 保守按零初始年龄处理，不能延长超过一个TTL。准备前、播放前检查；开始播放后不因候选TTL截断语音。
- 欢迎按 OccurredAt（缺失回退ReceivedAt）保留最新未播放项；关键词保留最新匹配评论；关注FIFO最多20个未播放项，满时丢最旧。准备中的候选仍受替换/到期约束；已经开始的互动不被新候选替换。选择优先级关注 > 关键词 > 欢迎，全局最多一个语音租约。
- 规则按优先级降序、Order升序、稳定RuleId决胜；精确/包含、任一/全部、NFC及保留越南语重音。匹配与朗读清理分离，符号、标签、产品标记不能因被清理而形成虚假的精确命中。
- VoiceTemplates/TextTemplates独立，支持注入IRandomSource的多模板随机、安全文本预览。昵称/评论按Unicode字符安全限长，清除控制/装饰字符及混淆产品标记，模板占位符只替换一遍。规则及模板集合复制冻结，拒绝非法枚举、空关键词、重复ID和越界配置；规则最多1000条。
- 语音、文字独立领取和完成；文字不启动K/V/W，也不消费等待语音。按通道Release只结束失败通道；整体Release、Stop、切场或TTL取消整个候选。欢迎/关注仅提供语音，文字仅关键词。
- SetContext显式选场；同场重连保留状态，换场/换房清理旧候选与冷却。Stop拒绝后续事件，须显式SetContext重启；旧租约回调不能复活。
- 输入必须携带T060成功的EventProcessResult；欢迎/关注必须Reserved且有ReservationId，评论允许缺UserId。没有新增事件或用户永久去重集合。候选保留SourceId/EventId/Fingerprint/ReservationId，丢弃结果可直接用于后续释放占位。
- DrainDiscards返回替换、过期、溢出、换场和取消候选。通知及在途候选合计最多256，满时拒绝新输入，排空后恢复；为每个已接受候选预留通知空间，避免静默丢失取消结果。

## 最终验证证据

以下命令均在最终源码上执行，退出0：

```powershell
dotnet test tests/TikTokAudio.Application.Tests/TikTokAudio.Application.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~T061InteractionRulesTests
dotnet test TikTokAudio.slnx -c Debug --no-restore
dotnet build TikTokAudio.slnx -c Debug --no-restore
git diff --check
```

- 专属84/84通过；覆盖单调冷却/TTL精确边界、墙钟前跳回拨、未来接收时间、准备后替换、已播放保护、Follow并发与容量、场次/停止/旧回调、独立渠道成功与失败、多模板与注入防护。
- 全方案544/544通过：Application355、Integration189；无失败、无跳过。
- Build通过，0 warnings、0 errors。
- git diff --check通过；对本轮新增源码、测试、报告与治理文件额外用.NET UTF-8逐行扫描尾随空白、文件末尾多余空行和冲突标记，覆盖尚未跟踪的文件。静态模式检查未发现.Result、.Wait()、Thread.Sleep、Task.Delay、HttpClient、DateTimeOffset.UtcNow、new Random或HashSet。
- Git仅提示既有LF/CRLF自动转换，不是编译警告；保留现有换行配置。

首轮11项通过后仍发现需求缺口，未据此验收；最终扩展测试中修正了使用会被朗读清理移除的分隔符所导致的断言偏差，改为直接验证占位符不递归展开。最终证据以上述84项及全方案结果为准。

## T062接线要求与实测限制

宿主先调用T060.ProcessAsync，再将结果交给Enqueue。非Accepted结果由宿主处理尚未移交的reservation；Accepted但立即过期/被替换的候选经DrainDiscards释放。宿主必须持续消费丢弃通知及取消对应准备任务，不能只读取Snapshot。

语音流程：TryPrepareNext → 本地合成 → CanPlay/BeginPlayback → 实际完整结束才MarkVoiceCompleted及T060.CompleteAsync；失败用对应通道Release并释放预留。准备结束后必须重新检查当前租约和TTL。模型没有实际调用TTS、调度器、输出设备或状态仓储。

文字流程独立，领取后发送前检查CanPlay，仅确认成功后MarkTextCompleted。这里TextCooldown默认0仅为规则层可配置门控，不代表平台发送许可或总间隔；REQ-CTRL-004的默认30秒发送总间隔、平台限制和账本仍由T073负责。

全部自动化仅内存、显式事件、可控时钟和注入随机，无真实网络/账号/TTS/音频设备/平台操作。模板真实试听、实际插播与成功账本回写留T062及后续UI/实测任务。T043仍REVIEW，T044仍DEFERRED，引擎与模型继续外置。下一最小READY为T062，尚未启动。
