# P00 基线、包环境与工程边界

状态：计划。前置：[总体设计](../README.md)。产出：可重复开发环境及旧功能回归基线。

## 编码范围

根 package.json／pnpm-workspace.yaml／pnpm-lock.yaml、pyproject.toml／uv.lock／.python-version；agent-host 包声明；scripts/package.py、现有 CI、开发说明。Application／Infrastructure 工程拆分在 P01-A。`pi/` 上游和已有用户数据不改动。

## 子步骤

1. 记录 git 工作区已有修改、.NET／Node／Pi 实际版本和当前测试结果；基线失败先定位，不用新增重构掩盖。
2. 固定 pnpm 精确版本；创建仅包含应用自有 Node 包的 workspace，从现有 package-lock 导入并核对解析结果。
3. 冻结安装验证 Pi SDK 导入、声明依赖、Node↔C# 握手；检查 Windows 原生依赖与可移植目录。成功后统一应用锁文件和命令。
4. 建立 uv workspace，把 baoyan-cli 现有 requirements 转为项目依赖；开发脚本纳入根环境，固定 Python 版本。先保留 CLI 人用入口兼容。
5. 打包／测试脚本改为 pnpm 和 uv 管理；最终应用调用随包运行时。更新 README／WINDOWS／CI 中活跃开发命令。
6. 记录需要保持的迁移资源与既有命名空间兼容点；实际工程机械迁移放在 P01-A，P00 不修改业务行为。

## 验收

执行锁定安装、AgentHost 测试、既有 Core 可执行回归、baoyan 现有 unittest；Windows 上验证生产依赖实际启动。无正式 Gmail 发送、无付费模型请求。旧界面可构建；新工程引用及循环依赖在 P01-A 验证。

## 退出条件与回退

锁文件、CI、开发与打包命令一致；现有测试结果有证据；工作区他人修改保留。若 pnpm 下暴露未声明依赖，在应用包显式修复并记录；不以 broad hoist 或修改 Pi 源码代替原因分析。回退仅限本步骤变更，未触及业务数据。
