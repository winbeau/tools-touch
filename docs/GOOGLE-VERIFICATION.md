# Google 公开验证准备

2026-09-05：按用户要求暂缓 Google 验证。本文仅保留准备材料；未提交审核，也未确认数据访问页面中的范围声明已保存。

当前客户端请求 gmail.send 与 gmail.readonly。项目已是 External / Production；品牌主页、隐私政策、使用条款以及授权域名 winbeau.github.io 已保存。生产状态不等于品牌或范围通过审核。

## 权限用途说明（供 Google 表单使用）

### gmail.send

Tools Touch is a Windows desktop research and outreach workbench. The signed-in Gmail account is the application identity and sender. Users review the recipient, subject, body and optional CV attachment and explicitly confirm each send. The desktop app calls users.messages.send directly; it has no unattended mail sending service. Basic identity scopes cannot send the reviewed message. We request gmail.send instead of Gmail modify or full mailbox access for sending.

### gmail.readonly

The desktop app reconciles uncertain sends by searching the sent mailbox for the draft's RFC Message-ID, then checking Message-ID and To headers. It reads thread metadata, timestamps, labels and From headers to show whether the recipient has replied. Requests use metadata format; received email bodies are not sent to AI models. gmail.metadata does not support the q search needed for sent-message reconciliation. No mailbox modification or deletion is required. Gmail data and refresh credentials are stored locally; the app has no developer-operated backend for Gmail data.

## 仍需完成

1. 在 Google Search Console 验证网站归属，并确保主页、隐私政策和服务条款均在可验证的网站下；目前没有声称网站归属已验证。可使用 GitHub Pages 的 URL 前缀验证，或先绑定自有域名再验证。
2. 在 Google Auth Platform 数据访问中保存实际的两项范围、用途及相应应用类别。仅声明范围不授予任何用户邮箱访问权，用户仍需单独授权。
3. 准备真实运行的演示视频：展示应用名称、完整 OAuth 流程、两项范围的用途、人工发送确认和回复状态。不要展示密钥、令牌、真实收件人隐私或未获同意的邮箱内容。审核表要求提供 YouTube 视频链接，不能编造链接或用模拟测试冒充真实验收。
4. 完成品牌验证并提交敏感/受限范围验证。根据 Google 对实际数据流的判定补交材料；不能笼统保证免安全评估，也不能把所有本地桌面应用都说成必需付费评估。
5. Google 审核通过后，再以独立 Google 账号完成安装、授权、重启和邮件功能验收。不要把绕过警告作为面向公众的正式登录流程。

官方说明：https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification
