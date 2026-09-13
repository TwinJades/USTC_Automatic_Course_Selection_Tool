# USTC 选课助手

面向中国科学技术大学教务系统的 Windows 桌面辅助工具。项目由学生制作，属于非官方学生工具，与中国科学技术大学无隶属、合作或授权关系。

项目目前保留两条实现路线：

- **路线二 `2.1.0`（当前正式版）**：优化课程任务的直接输入与删除交互，新增首次使用引导、关键活动记录视图，并消除只读文字中的输入光标观感。
- **路线一 `1.0.0`（早期实现）**：基于 WebView2 页面与 DOM 操作的早期实现及验证方案，继续保留且由哈希基线保护。

## 获取正式版

正式版通过 GitHub Releases 发布，不把编译产物提交到源码仓库。请从仓库右侧的 **Releases** 页面下载对应版本：

- `UstcCourseAssistant.Route2.exe`：单文件 Windows x64 成品。
- `使用说明.html`：可离线双击打开的完整使用说明。
- `SHA256.txt`：成品完整性校验值。

当前 exe SHA-256：

```text
FEAE191671FA147AA951D44166C500F04ABC23D320FDF9F147EDF0D2BF3E58C0
```

## 路线二主要能力

- 自动识别当前教务登录会话，并保持主程序与教务网页为两个独立窗口。
- 管理多门课程任务、启用状态、优先级、冲突旧课、运行时段和可选周期抖动。
- 教学班代码和冲突旧课可单击直接输入；启用复选框可一次切换。选中整行后可按 `Delete` 删除任务，在输入框内按 `Delete` 只删除文字。
- 首次使用会显示六步被动引导；引导只作说明，不替用户点击或提交，之后可由左下角问号重新查看。
- 活动记录默认只显示验证、监测和真实选退课等关键事件，切换到“全部记录”后可查看最近 14 天的完整脱敏信息；真实选课、退课和恢复记录整行加粗，关键/全部视图使用缓存只读文档快速切换并支持跨行复制。
- 提供课程影子和“立即检查（不选课）”等只读能力。
- 在逐次确认后执行受控的选课、退课和串行换课流程。
- 监测计时器明显晚于计划触发时，会在发送任何请求前安全暂停，避免休眠、Modern Standby 或系统长时间停顿后的短暂唤醒继续监测。
- 纯读取网络超时会等待网络恢复，并在一次只读会话核对成功后从下一轮继续；已经发送或可能发送的真实写入仍保持暂停和人工核对。
- 使用原子配置保存、损坏恢复、最近 14 天脱敏日志和单实例保护。
- 可选将密码保存到 Windows 凭据管理器，并仅在受限的校方登录页面尝试自动登录；遇到短信、验证码、OTP、新设备或二次认证时停止并交由本人操作。
- 可选开机启动程序，但开机后不会自动开始监测或执行选退课。

## 快速使用

1. 从 GitHub Releases 下载并打开 `UstcCourseAssistant.Route2.exe`。
2. 点击“打开教务系统”，由本人完成登录或确认自动登录结果。
3. 添加并核对课程任务、优先级和冲突旧课。
4. 先使用“立即检查（不选课）”确认读取结果。
5. 确认无误后，再勾选黄色区域中的真实操作确认并开始监测。

“开始监测”可能执行真实选课或退课。换课不是原子交换，目标课选择失败后不能保证旧课一定恢复成功；最终状态必须回到教务系统人工确认。详细步骤和异常处理请阅读发布包中的 `使用说明.html`。

## 本机数据与隐私

路线二的普通配置、任务、脱敏日志和 WebView2 浏览器资料存放在：

```text
%LOCALAPPDATA%\USTC Course Assistant Route2
```

保存密码时，密码只写入当前 Windows 用户的凭据管理器，目标名为 `USTC Course Assistant Route2`；普通配置和活动日志不记录密码、验证码、Cookie、登录令牌或完整服务器响应。

正式版 exe 本身不包含用户任务、账号、浏览器会话或日志。程序在同一台电脑上运行时会继续读取上述本机目录，因此旧测试版留下的数据也会显示在后续版本中。需要清理时，应先关闭程序，在应用内删除已保存密码或从 Windows 凭据管理器删除对应条目，再将上述数据目录移入系统回收站；不要使用绕过回收站的永久删除方式。

路线一使用独立目录 `%LOCALAPPDATA%\USTC Course Assistant`，不要与路线二数据混用。

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

验证路线一保护基线：

```powershell
.\Verify-Route1Baseline.ps1
```

离线构建和回归不得打开真实教务网页，也不得为测试发送选课或退课请求。路线二的阶段演进、验收边界和发布记录见 `ROUTE2_HANDOFF.md`，版本摘要见 `VERSION_HISTORY.md`。

## 仓库结构

```text
.
├─ .github/workflows/             # GitHub Actions 构建与离线检查
├─ src/
│  ├─ UstcCourseAssistant/        # 路线一（早期实现）
│  └─ UstcCourseAssistant.Route2/ # 路线二（当前正式实现）
├─ tests/                         # 路线二离线回归检查
├─ tools/                         # 发布资源辅助工具
├─ UstcCourseAssistant.sln        # Visual Studio / dotnet 解决方案入口
├─ README.md
├─ CONTRIBUTING.md
└─ VERSION_HISTORY.md
```

`release/`、本机 `.dotnet-cli/`、各项目的 `bin/obj`、运行日志和测试数据只保留在本地，并由 `.gitignore` 排除。发布成品应作为 GitHub Release 附件上传。

## 免责声明

本项目为非官方学生工具，与中国科学技术大学无隶属、合作或授权关系。使用者应遵守学校规章和教务系统规则，并自行承担使用风险。真实选退课结果始终以教务系统页面为准。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。
