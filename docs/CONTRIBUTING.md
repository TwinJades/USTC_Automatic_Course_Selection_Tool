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

- 正式发布由推送 `vX.Y.Z` 标签触发 [Release 工作流](../.github/workflows/release.yml)，执行 Windows 构建、离线检查、self-contained `win-x64` 单文件发布、ZIP 打包和 GitHub Release 上传。
- 打标签前同步路线二项目的 `Version=X.Y.Z`、`AssemblyVersion=X.Y.Z.0`、`FileVersion=X.Y.Z.0`，并在 [CHANGELOG](CHANGELOG.md)中保留恰好一条非空的对应版本行；自动说明只读取该行。
- 本地准备命令为 `python -m pip install -r tools/release/requirements.txt`、`python tools/release/release_notes.py --tag v2.2.1 --output artifacts/release-notes.md` 和 `./tools/release/Publish-Release.ps1 -Tag v2.2.1`。这些命令不创建或推送标签，也不发布线上版本；完整流程见[发布说明](PROJECT.md#发布)。
- 附件统一为 `USTC_Automatic_Course_Selection_Tool_<版本号>.zip`，根目录仅含同名 `.exe`，不增加 SHA256 或其他附件。本地 `release/` 只保留最新最终 ZIP，旧产物移入系统回收站；中间结果放在 `artifacts/`，GitHub 历史 Releases 保留。
- 项目不使用 GitHub Pages，不恢复 Pages 部署或失效网站入口；README、源码、Releases 和应用更新保持可用。离线检查、线上权限与发布结果必须如实记录，未运行不得写成已通过。
