# T030 来源比较与验证报告

报告日期：2026-09-18（Asia/Shanghai）
任务：T030  选择首个事件来源
验证级别：公开入口与本机环境只读核对；未进行授权直播间实测

## 结论

暂选 TikTok 直播网页入口作为首个候选来源，入口为 `https://www.tiktok.com/live`。该选择只是后续适配器验证的候选，不是“已接通”结论。T030 当前登记为 BLOCKED，原因是缺少用户授权的直播间/测试账号、账号地区范围和客户端版本，无法按 REQ-SRC-002 取得事件及写操作的真实证据。

## 候选比较

| 候选 | 本机/公开入口核对 | 可用于 T030 的事实 | 未知或限制 |
|---|---|---|---|
| TikTok 直播网页 | `https://www.tiktok.com/live` 于 2026-09-18 返回 HTTP 200；本机未发现登录会话或直播房间 | 公开网页入口可访问；后续可在授权浏览器会话中验证事件读取 | HTTP 200 不证明事件流、稳定 UserId、完整覆盖、商品展示或发文字；需要登录和授权房间 |
| TikTok LIVE Studio（PC） | `https://www.tiktok.com/studio/download` 于 2026-09-18 返回 HTTP 200；本机命令、环境变量和常见安装目录未发现 LIVE Studio | 官方下载入口可访问；未安装客户端 | 未验证客户端是否可提供所需事件/写能力；需要安装、登录、地区资格和授权房间 |

## 验证范围与结果

- 非敏感账号/地区/客户端范围：未知。当前没有提供可用于测试的账号、房间或地区/客户端版本。
- 验证方法：PowerShell 只读 HTTP HEAD 请求；本机命令、环境变量和常见安装目录只读检查。未读取 Cookie、浏览器配置或授权文件，未发送直播评论、商品操作或其他平台副作用。
- 真实结果：两个公开入口均可访问；未取得任何 `Enter`、`Follow`、`Like`、`Comment`、`RoomStatus` 事件，也未验证 `ShowProduct`、`ReadProductVisibility`、`SendText`。
- 模拟结果：现有 `SimulatedLiveEventSource` 和 `SimulatedLiveRoomController` 仅用于内存测试，不能作为 TikTok 能力证据。

| 能力 | T030 结论 |
|---|---|
| ReadEnter / ReadFollow / ReadLike / ReadComment / ReadRoomStatus | 未知，待授权直播间实测 |
| ShowProduct / ReadProductVisibility / SendText | 未知，待具备相应场控权限的授权房间实测；读取权限不等于写权限 |
| 稳定 UserId、EventId、断线/重连、事件完整度 | 未知，留给 T031/T032 分项验证 |

## 操作入口与解除条件

候选网页入口：`https://www.tiktok.com/live`。候选 PC 入口：`https://www.tiktok.com/studio/download`，需用户自行确认安装和地区资格。解除 T030 阻塞至少需要：一个获授权的测试直播间或等价测试条件、非敏感账号/地区范围、客户端/网页版本，以及允许观察事件和能力结果的测试许可。获得这些输入后，按能力逐项记录真实事件样本、登录要求、断线行为和未知项，再决定是否冻结网页或 PC 工具为首个来源。

## 范围停止

本报告未创建适配器，未修改共享 Contract，未执行 T031/T032，也未访问真实账号、Token、Cookie、TTS、音频设备或直播副作用。
