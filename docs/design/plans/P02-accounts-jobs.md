# P02 账号、跨进程协议与持久化任务

状态：计划。前置：P01。设计依据：[账号与任务](../details/02-integrations-jobs.md)。

## 编码范围

agent-host/src/{index,authentication,tools,outputs,session}.ts；contracts/；Infrastructure Pi／Google／Collection；Application Accounts／Jobs；Desktop Accounts 与任务 ViewModel；相应契约测试。

## 子步骤

1. 保存现有 provider catalog 行为，增加授权能力 DTO 和已配置／已验证状态；UI 按能力渲染 OAuth／API Key。
2. 验证 Gmail 单账号恢复、Google 配置、token 过期与重连；保留来源账号隔离。
3. C# 与 Node 同步引入 protocol v2、版本握手、run／stage／attempt、定向取消和消息限额；明确 v1 不匹配处理。
4. 定义每种业务任务的工具白名单及输出 Schema，继续禁止任意 shell、环境资源发现和发送工具。
5. 将现有阶段恢复扩展为持久化任务队列、JobStage、lease、父子任务、预算、重试与账号等待。
6. 建立 CollectorBridge 和机器结果 manifest 契约；调用 Python 使用 ArgumentList、暂存目录、数量／哈希校验。
7. 用合成 Host 和真实本地 Pi 匿名握手验证，任务中心显示阶段、部分失败、取消与继续。

## 验收

非法 schema、过大 JSONL、stdout 污染、桥接断开、旧 attempt 迟到、任务接收失败不误取消别人、登录取消清理、阶段结果与检查点原子提交、半份 Python 文件拒绝发布、总预算约束。真实 provider 登录另记录，不由 catalog 测试替代。

## 退出条件与回退

匿名完整任务／工具往返在本地可执行；账号 UI 与错误可理解；取消／恢复通过。发送不进入普通任务重试。协议变更必须连同应用和随包 Host 交付，握手不匹配时停止该组件而保留业务库。
