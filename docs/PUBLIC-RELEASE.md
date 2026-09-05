# 面向公众的 Google 登录发布

普通用户只下载安装包并使用 Google 登录，不创建 Cloud 项目，不导入 JSON。开发者负责 Google 应用注册和审核，使用桌面应用 OAuth 客户端，并随 Windows 安装包提供客户端配置；个人 Gmail 刷新凭据仍各自通过 Windows DPAPI 保存在本机，不能随包发布。

## 构建

```sh
python scripts/package.py --public --google-client /private/google-desktop-client.json --output artifacts/ToolsTouch-win-x64-v0.2.1
```

公开打包必须带有效桌面客户端 JSON，缺失时直接拒绝构建。JSON 来自构建机私有文件，不提交源码；安装包内为 config/google-client.json，应用自动发现，登录页不向普通用户展示开发者配置。

## Google Cloud 发布者工作

1. 启用该项目的 Gmail API。
2. Google Auth Platform 的应用名称和品牌对应 Tools Touch；提供真实的支持联系方式、应用主页及隐私说明。
3. 目标对象选择 External，并完成从 Testing 到 Production 的发布。
4. 数据访问说明当前实际使用 gmail.send 与 gmail.readonly：人工确认发送，以及发送结果核对和同线程回复状态。不得声称只读权限仅是基本登录所需。
5. 按 Google 要求完成敏感和受限权限验证；准备演示视频、权限用途说明及适用的其他材料。仅切换 Production 不代表通过审核，也不能据此宣称不再有未验证应用用户限制。
6. 用未加入测试名单的独立 Google 账号验证安装、授权、重启刷新和邮件功能；真实邮件测试须经账户持有人明确同意。

当前实现是本地 WPF 工作台，无自建云端账户数据库、资料同步或邮件转发服务器。Google 发布和审核状态无法靠客户端代码改变，获得 Google 的确认之前，安装包应标为测试版。

官方资料：[桌面 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)、[OAuth 应用状态](https://developers.google.com/identity/protocols/oauth2/production-readiness/overview)、[Gmail 权限](https://developers.google.com/workspace/gmail/api/auth/scopes)。

## 2026-09-05 发布准备状态

- Gmail API 已在项目内启用。
- 目标对象是 External / Testing，测试用户为 0；尚未完成品牌信息与权限审核。
- 应用主页：https://winbeau.github.io/tools-touch/
- 隐私政策：https://winbeau.github.io/tools-touch/privacy.html
- 使用条款：https://winbeau.github.io/tools-touch/terms.html
- GitHub Pages 从 main 的 site/ 目录通过工作流发布。
- 计划自定义域名 touch.icthub.top；尚未绑定 DNS，也未声称通过 Google 网站所有权验证。

后续应验证网站归属、将实际页面链接填写到 Google 品牌信息，并准备真实权限演示和数据流说明。提交审核和切换生产状态不能替代 Google 的审核结果。
