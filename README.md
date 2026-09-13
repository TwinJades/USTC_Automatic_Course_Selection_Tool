# USTC 选课助手

[![Build](https://github.com/TwinJades/USTC_Automatic_Course_Selection_Tool/actions/workflows/build.yml/badge.svg)](https://github.com/TwinJades/USTC_Automatic_Course_Selection_Tool/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

面向中国科学技术大学综合教务系统的非官方 Windows 桌面辅助工具，提供多课程任务、只读核对、受控选退课和安全暂停机制。

> [!WARNING]
> 本工具可能执行真实选课或退课。所有操作结果都必须回到教务系统人工确认；换课及旧课恢复不保证成功。

## 下载与文档

- [下载最新正式版（请选择 ZIP）](https://github.com/TwinJades/USTC_Automatic_Course_Selection_Tool/releases)
- [完整使用说明](https://twinjades.github.io/USTC_Automatic_Course_Selection_Tool/USER_GUIDE.html)
- [系统架构图](https://twinjades.github.io/USTC_Automatic_Course_Selection_Tool/architecture/SYSTEM_OVERVIEW.html)
- [版本历史](CHANGELOG.md)

发布附件按 `USTC_Automatic_Course_Selection_Tool_<版本号>.zip` 命名；解压后即可运行。编译产物不提交到源码仓库。

## 主要能力

- 管理多门课程任务、优先级、冲突旧课、运行时段和检查周期。
- 通过“立即检查（不选课）”核对课程、余量和已选状态。
- 在逐次确认后执行受控的选课、退课和串行换课流程。
- 对网络中断、休眠延迟和结果不确定状态进行安全暂停。
- 使用原子配置、最近 14 天脱敏活动记录和单实例保护。
- 可选把密码保存到 Windows 凭据管理器；遇到验证码或二次认证时停止自动提交。

## 快速开始

1. 从 Releases 下载并解压对应版本的 ZIP 发布包。
2. 运行 `USTC_Automatic_Course_Selection_Tool_<版本号>.exe`。
3. 点击“打开教务系统”，由本人完成登录。
4. 添加课程任务，先运行“立即检查（不选课）”。
5. 核对无误后，再确认并开始监测。

详细的任务配置、登录安全、异常保护和隐私说明请阅读[完整使用说明](https://twinjades.github.io/USTC_Automatic_Course_Selection_Tool/USER_GUIDE.html)。

## 开发与离线检查

需要 Windows x64、.NET 9 SDK 和 WebView2 Runtime。

还原并构建整个解决方案：

```powershell
dotnet restore UstcCourseAssistant.sln
dotnet build UstcCourseAssistant.sln -c Release
```

运行路线二：

```powershell
dotnet run --project src/UstcCourseAssistant.Route2
```

运行路线二离线任务引擎与界面回归：

```powershell
dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks -c Release
```

离线构建和回归不得打开真实教务网页，也不得为测试发送选课或退课请求。

## 仓库结构

```text
.
├─ .github/workflows/             # GitHub Actions 构建与离线检查
├─ docs/                          # 使用说明、架构图和开发资料
├─ release/                       # 本地历史发布包（不提交 Git）
├─ src/
│  ├─ UstcCourseAssistant/        # 路线一（早期实现）
│  └─ UstcCourseAssistant.Route2/ # 路线二（当前正式实现）
├─ tests/                         # 路线二离线回归检查
├─ tools/                         # 发布资源辅助工具
├─ UstcCourseAssistant.sln        # Visual Studio / dotnet 解决方案入口
├─ README.md
├─ CONTRIBUTING.md
├─ LICENSE
└─ CHANGELOG.md
```

`release/`、各项目的 `bin/obj`、运行日志和测试数据只保留在本地，并由 `.gitignore` 排除。发布成品应作为 GitHub Release 附件上传。

## 免责声明

本项目为非官方学生工具，与中国科学技术大学无隶属、合作或授权关系。使用者应遵守学校规章和教务系统规则，并自行承担使用风险。真实选退课结果始终以教务系统页面为准。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。
