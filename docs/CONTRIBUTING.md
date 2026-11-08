# 参与贡献

## 开发与安全

开发环境、构建和离线检查命令见[项目说明的开发与验证章节](PROJECT.md#开发与验证)。离线检查不得打开真实教务网页，也不得发送真实选课或退课请求。

修改不得绕过现有真实操作确认、安全暂停、日志脱敏、凭据存储和统一写入保护。

## 提交要求

1. 每个提交只处理一个明确问题。
2. 不要提交 `bin`、`obj`、本机 SDK、运行日志、测试数据或发布二进制。
3. 用户功能或下载方式变化时更新[用户 README](../README.md)；开发事实变化时更新[项目说明](PROJECT.md)；版本变化时更新[版本历史](CHANGELOG.md)；交接时更新[当前断点](development/ROUTE2_HANDOFF.md)，写入实际时间、agent 和模型。
4. Pull Request 说明改动目的、用户可见变化和实际运行的验证。
5. 遵守[项目文档规则](../AGENT.md)，删除文件和目录时移入操作系统回收站。

## 标签与发布

- [Release 工作流](../.github/workflows/release.yml)配置了 `win-x64` 与 `linux-x64` 目录候选包构建、校验、草稿附件核对后发布；配置存在不等于工作流已运行或发布成功。
- 打标签前同步路线二项目的 `Version=X.Y.Z`、`AssemblyVersion=X.Y.Z.0`、`FileVersion=X.Y.Z.0`，并在 [CHANGELOG](CHANGELOG.md)中保留恰好一条非空的对应版本行；自动说明只读取该行。
- 本地打包命令必须指定 `-Tag`、`-Runtime win-x64|linux-x64`、`-OutputDirectory artifacts/<新目录>`，完整命令和校验见[发布说明](PROJECT.md#发布)。本地打包不创建或推送标签，也不发布线上版本。
- 候选附件分别带 `_win-x64.zip`、`_linux-x64.zip` 后缀，包含目录包和清单。当前源码版本仍为 2.2.1，候选尚未正式发布，线上 2.2.1 仍是 Windows 单 EXE，不得覆盖。中间产物放在 `artifacts/`，旧文件移入回收站，不删除 GitHub 历史 Releases。
- 项目不使用 GitHub Pages，不恢复 Pages 部署或失效网站入口；README、源码、Releases 和应用更新保持可用。离线检查、线上权限与发布结果必须如实记录，未运行不得写成已通过。
