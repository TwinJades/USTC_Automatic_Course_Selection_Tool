# 项目说明

## README 编写要求

README 面向使用者，采用 emoji 标题风格，只描述项目当前状态。开头说明程序内置使用教程。自动监测是说明主体，简洁介绍功能和必要的运行限制；定时抢课保留简短的独立说明。具体操作由内置教程讲解。功能表统一命名为“详细功能表”，放在 README 文末，保留各项功能说明。下载条件和风险提示保持简洁。版本变化记录放在 CHANGELOG 和 GitHub Release 发布说明中。开发细节放在项目文档中，由 README 提供入口。

## 项目定位与当前工程

USTC 选课助手是面向中国科学技术大学综合教务系统的非官方 Windows 本机工具，采用 [MIT License](../LICENSE)。项目与学校无隶属、合作或授权关系；没有自建服务器、云同步或用户账户系统。课程读取和真实选退课依赖校方未公开接口，结果需要本人回到教务系统确认。

当前正式实现为路线二，源码版本为 `2.2.1`，见[项目配置](../src/UstcCourseAssistant.Route2/UstcCourseAssistant.Route2.csproj)。[解决方案](../UstcCourseAssistant.sln)包含三个工程：

| 工程 | 当前职责 |
| --- | --- |
| [UstcCourseAssistant](../src/UstcCourseAssistant/) | 独立 WPF 应用，使用 WebView2 网页 DOM 读取课程、执行页面操作。 |
| [UstcCourseAssistant.Route2](../src/UstcCourseAssistant.Route2/) | 独立 WPF 应用，通过 WebView2 完成认证，以 HTTP 客户端读取课程并执行受控选退课。 |
| [UstcCourseAssistant.Route2.EngineChecks](../tests/UstcCourseAssistant.Route2.EngineChecks/) | 引用路线二的控制台检查程序，覆盖任务、安全保护、存储及 WPF 界面。 |

两个应用工程互不引用，浏览器资料目录相互隔离。源码位于 `src/`，离线检查位于 `tests/`，图标处理和路线一基线工具位于 [tools/](../tools/)。

## 当前功能与使用流程

主程序和教务系统使用两个独立窗口。启动时显示主窗口并独立检查 GitHub 更新；点击“打开教务系统”后创建 WebView2 窗口，预约补执行需要自动登录时也会创建它。关闭教务窗口只会隐藏它。主窗口包含“任务监测”“定时抢课”“活动记录”“调试工具”“账号与安全”五个页面；调试工具内包含“课程影子”和“单门受控操作”。

### 课程任务

1. 打开教务系统，由本人完成登录；程序自动识别会话，也可点击“验证只读会话”。
2. 添加任务并填写完整教学班代码，例如 `001683EX.01`。数字越小优先级越高，可用优先级按钮调整。
3. 冲突旧课只填写本人明确允许退掉的完整教学班代码，多个代码以逗号分隔，没有则留空。
4. 启用准备执行的任务，先点击“立即检查（不选课）”，核对课程、余量和当前已选状态。
5. 需要真实监测时，勾选左侧真实操作确认，再点击“开始监测”并确认弹窗。

教学班代码和冲突旧课支持单击输入，启用复选框单击切换。点击行头或只读单元格选择整行后，`Delete` 删除任务；输入框内的 `Delete` 仅删除文字。监测或只读检查期间任务表禁止编辑。

检查间隔支持 5 秒、15 秒、60 秒和自定义秒数；可选随机抖动，每轮重新抽取基准周期的 `2/3～4/3`。运行时段支持跨午夜，时段外保持等待。任务按优先级串行处理；高优先级目标已选中后，会停用与其冲突的低优先级任务。

监测使用左侧当前开放轮次；首次验证默认选择第一个开放轮次。每轮读取与排队执行捕获账号和轮次构成的运行上下文，课程配置不保存账号或轮次。切换轮次后旧读取结果和等待操作失效；账号变化或原轮次关闭时暂停，不自动切换。启用课程按去除首尾空白且不区分大小写的教学班代码检查重复。

### 定时抢课

定时页面始终显示一个预约的摘要、北京时间设置和课程列表，支持直接填写与保存草稿。草稿不要求登录或开放轮次；启用时验证账号并明确授权，到点使用左侧当前选择，没有选择时默认第一个开放轮次，无开放轮次或账号变化时停止。执行一轮后停止，或者将剩余有效课程导入监测；导入按全部已有教学班代码去重，保留原任务设置和顺序，新任务按预约优先级追加。自动启动监测与手动开始共用重复课程及安全校验。

250 毫秒计时器收集到期预约，所有阶段先保存再执行。正常关闭将未开始的 Queued 恢复 Armed；异常退出后的 Queued/Running 转 Interrupted，禁止重跑。完整保存 SelectionFinished 后，只恢复幂等导入。备份恢复的 Armed 转 Draft，其余执行阶段转 Interrupted。补执行选项默认关闭，控制启动与休眠恢复后如何处理错过的时间。编辑已启用预约先保存 Draft，再要求重新确认。

完成、失败、错过、取消或中断后保留时间与课程，编辑时转为未授权草稿，下次执行必须重新确认。升级旧多个预约配置时先完整备份原始字节到 `scheduled-selections.json.pre-2.2.1-<GUID>.json`，仅展示最早未完成预约为草稿；没有未完成项时保留最新结果对应内容。其余旧预约只留在备份中，不执行。备份或保存失败时停止加载与执行。

### 自动更新

独立且不携带教务 Cookie 的客户端读取本项目 GitHub latest 正式发布。下载仅接受版本匹配的 ZIP，核对附件大小、可用 digest、安全路径及根程序 FileVersion。用户在单个更新窗口确认安装后，暂停新写入，等待已有真实操作结束并保存全部配置。

暂存的新程序通过 --apply-update 辅助模式运行，父进程退出后复制为原目录内包含新版本号的文件名，按原开关更新启动注册项，以 --updated 启动并等待就绪及安装结果确认。成功后旧程序进入回收站；失败保留旧文件并恢复原注册项。新程序确认结果前禁止自动选退课。开发输出仅支持检查和下载，安装要求可写目录内的 Windows 单文件发布版。


### 活动记录与引导

活动记录默认显示关键事件，可切换为全部记录；真实选课、退课和恢复事件加粗显示，文字可跨行选择复制。两个视图分别缓存只读 `FlowDocument`，每个文档使用一个段落保存日志行。

没有既有偏好、任务、日志或浏览器资料的新用户首次启动时会看到六步引导，也可通过右上角问号重新打开。引导只由用户点击“上一步”“下一步”“跳过”“完成”推进，不操作底层控件或执行真实请求。

### 登录与账号

账号、密码保存均为可选功能，自动登录默认关闭，保存密码不会自动开启它。自动登录只在 `https://jw.ustc.edu.cn/login` 激活统一身份认证入口，并仅在 `id.ustc.edu.cn` 填写正常账号密码表单，每次登录流程最多提交一次。遇到短信、验证码、OTP、新设备或二次认证时停止自动提交，交由本人操作。

“关闭程序后保留登录状态”控制正常退出时是否清除浏览器 Cookie。主动点击“退出教务登录”后，本次运行会抑制自动登录；取消再重新勾选自动登录可解除抑制。认证成功只恢复会话识别，不会开始或恢复监测。

## 架构与请求流程

界面采用 WPF 事件处理与服务类协作的结构，没有独立 ViewModel 层。主窗口协调调度和状态，任务引擎执行一轮任务。

| 模块 | 职责与源码 |
| --- | --- |
| `App` | [应用入口](../src/UstcCourseAssistant.Route2/App.xaml.cs)，通过命名互斥量限制单实例。 |
| `MainWindow` | [主窗口](../src/UstcCourseAssistant.Route2/MainWindow.xaml.cs)，管理页面、任务编辑、确认操作、单次计时调度、运行时段、网络与电源事件、活动记录和引导。 |
| `LoginWindow` | [教务窗口](../src/UstcCourseAssistant.Route2/LoginWindow.xaml.cs)，管理 WebView2、认证导航、受限制自动登录和窗口隐藏。 |
| `AuthenticatedSessionBridge` | [会话桥接](../src/UstcCourseAssistant.Route2/Services/AuthenticatedSessionBridge.cs)，复制允许域的 Cookie，创建短生命周期的内存 HTTP 会话。 |
| `ReadOnlyEamsClient` | [只读客户端](../src/UstcCourseAssistant.Route2/Services/ReadOnlyEamsClient.cs)，识别学生编号、开放轮次，读取已选、可选课程和批量人数。 |
| `Route2AutomationEngine` | [任务引擎](../src/UstcCourseAssistant.Route2/Services/Route2AutomationEngine.cs)，按优先级执行单轮任务，检查教学班唯一性、余量、冲突旧课及恢复条件。 |
| `RealOperationCoordinator` / `ProtectedOperationLease` | [操作协调器](../src/UstcCourseAssistant.Route2/Services/RealOperationCoordinator.cs)，共用非阻塞写入保护、持久化操作阶段、核验最终状态并处理中断保护。 |
| `ControlledEamsClient` | [受控客户端](../src/UstcCourseAssistant.Route2/Services/ControlledEamsClient.cs)，单次提交选课或退课，有限次数查询结果。 |
| 存储服务 | [AutomationStore](../src/UstcCourseAssistant.Route2/Services/AutomationStore.cs)、[LocalPreferences](../src/UstcCourseAssistant.Route2/Services/LocalPreferences.cs)、[OperationJournalStore](../src/UstcCourseAssistant.Route2/Services/OperationJournalStore.cs)、[LocalActivityLog](../src/UstcCourseAssistant.Route2/Services/LocalActivityLog.cs)，分别管理任务、偏好、操作记录与活动日志。 |
| Windows 集成 | [WindowsCredentialVault](../src/UstcCourseAssistant.Route2/Services/WindowsCredentialVault.cs)管理可选密码保存；[StartupRegistrationService](../src/UstcCourseAssistant.Route2/Services/StartupRegistrationService.cs)管理当前用户开机启动。 |
| `CourseOperationQueue` | 单消费者优先队列，到期预约先于等待中的监测，已开始的换课保持完整执行。只读检查独立运行。 |
| `ScheduledSelectionService` / `ScheduledSelectionStore` | 预约状态转换、原子存储、重启保护、轮次解析和剩余课程幂等导入。 |
| `GitHubUpdateService` / `AppUpdateInstaller` | 正式发布检查、下载校验、辅助进程替换、启动注册与结果握手。 |

登录完成后，主窗口从 WebView2 建立会话桥接，通过只读客户端验证身份及轮次。每轮任务创建客户端并调用引擎；只读检查不取得写入许可。单门操作和真实任务监测共用同一个操作协调器。

任务执行顺序为：读取目标与冲突课状态，依次退掉明确配置且当前已选的旧课，再选择目标课。目标明确未选中时尝试恢复本轮已确认退掉的旧课，随后暂停。每个真实步骤依次持久化阶段、单次提交、查询结果、只读复核、更新操作记录；任一步骤结果不确定便停止后续操作。换课与旧课恢复均不保证成功。

## 请求与安全边界

“只读”指请求不改变选课状态，其中部分接口使用 HTTP POST。

| 客户端 | 方法与固定路径 | 用途 |
| --- | --- | --- |
| 只读 | `GET /for-std/course-table` 及允许的同域重定向 | 识别内部学生编号。 |
| 只读 | `POST /ws/for-std/course-select/open-turns` | 读取开放轮次。 |
| 只读 | `POST /ws/for-std/course-select/selected-lessons` | 读取已选课程。 |
| 只读 | `POST /ws/for-std/course-select/addable-lessons` | 读取可选课程。 |
| 只读 | `POST /ws/for-std/course-select/std-count` | 批量读取人数。 |
| 受控 | `POST /ws/for-std/course-select/add-request` | 提交一次选课。 |
| 受控 | `POST /ws/for-std/course-select/drop-request` | 提交一次退课。 |
| 受控 | `POST /ws/for-std/course-select/add-drop-response` | 查询该次操作结果。 |

- 请求目标限定在允许的教务域名和路径；受控操作只接受预检查取得的学生、轮次、教学班数字 ID。
- 单门操作先预检查，再勾选并确认弹窗；任务监测需要全局勾选和启动确认。真实写请求不自动重试，结果查询最多六次并带等待。
- 所有真实写入先经过同一优先队列，再取得现有非阻塞互斥保护；监测与预约的读取可以同时进行，每门真实操作开始前重新验证账号、轮次与课程状态。
- 提交前记录请求可能已经发送。崩溃、取消、超时、认证失效或最终核验失败时保留中断保护，禁止新写入。
- 出现红色中断保护时，先登录并只读核对，再到教务网页处理或确认实际状态，最后明确解除保护。禁止直接重复提交。
- 普通启动、开机启动和重新认证均不自动恢复持续监测；已确认预约按照持久化时间和补执行设置处理，选择导入监测的预约包含自动启动授权。开机启动默认关闭。
- 监测计时器因休眠或系统停顿明显延迟时暂停；预约按补执行选项处理。程序不提供保持唤醒功能，不修改合盖动作或全局电源计划，实际睡眠期间无法执行。
- 尚未开始真实写入的纯读取网络故障进入等待；网络恢复后验证学生身份和开放轮次，一致才安排下一轮，不重放中断请求。认证失效、身份或轮次不一致及读取失败保持暂停。
- 已发送或可能发送的真实请求视为结果不确定，禁止自动重试。仅在旧课已确认退课成功且可证明目标请求从未开始时，才允许网络恢复后严格核对身份、轮次、目标未选、旧课身份唯一且未选，并串行尝试一次恢复旧课；核对失败禁止恢复，尝试结束后仍保持暂停和中断保护。

## 数据与隐私

默认本机数据目录为 `%LOCALAPPDATA%/USTC Course Assistant Route2`，不随 exe 发布。

| 位置 | 当前保存内容与方式 |
| --- | --- |
| `preferences.json` | 登录保留选项、账号文字、自动登录选项和引导完成状态；直接写入普通 JSON。 |
| `automation-settings.json` | 任务 ID、启用状态、教学班代码、课程名称、优先级、冲突旧课、任务状态及运行设置；使用原子文件写入，支持备份和完整临时文件恢复。 |
| `scheduled-selections.json` | 一个预约的时间、授权账号、课程、执行阶段与实际执行轮次及结果；原子保存，恢复副本禁止重放，旧多个预约升级前完整备份。 |
| `updates/` | 已下载 ZIP、验证后的程序、安装清单、就绪与安装结果文件；失败产物保留供核对。 |
| `operation-journal.json` | 真实操作身份、阶段、已核验步骤及中断恢复证据；使用原子文件写入，损坏恢复后继续保持安全保护。 |
| `activity.jsonl` | 最近 14 天的脱敏活动记录，保存事件类型。 |
| `browser-profile/` | 独立 WebView2 浏览器资料，登录保留选项影响退出时的 Cookie 清理。 |
| Windows 凭据管理器 | 当前 Windows 用户可选保存的密码，目标名为 `USTC Course Assistant Route2`。 |

Cookie 桥接中的 `CookieContainer` 只存在于内存。普通配置与活动日志不保存密码、验证码、Cookie、登录令牌或完整服务器响应；账号文字可出现在偏好文件中。活动日志不记录账号和请求编号。

安全清理顺序：确认真实操作已结束并处理所有中断保护，在“账号与安全”删除已保存密码，关闭程序，再通过文件资源管理器将数据目录移入回收站。

## 开发与验证

开发环境需要 Windows x64、.NET 9 SDK 和 Microsoft Edge WebView2 Runtime。以下命令均在仓库根目录执行。

还原并构建解决方案：

```powershell
dotnet restore UstcCourseAssistant.sln
dotnet build UstcCourseAssistant.sln -c Release
```

运行路线二：

```powershell
dotnet run --project src/UstcCourseAssistant.Route2
```

运行完整离线检查（包含 WPF 界面）：

```powershell
dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks -c Release --no-build
```

[CI 配置](../.github/workflows/build.yml)使用 Windows runner，跳过依赖桌面布局与性能条件的界面检查：

```powershell
dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks -c Release --no-build -- --skip-ui
```

离线检查不得打开真实教务网页或发送真实选退课请求。[检查入口](../tests/UstcCourseAssistant.Route2.EngineChecks/Program.cs)覆盖自动登录脚本、偏好、调度、配置、日志、写入保护和任务流程。

`USTC_ROUTE2_TEST_DATA_ROOT` 可重定向任务配置、操作记录、日志和偏好；主窗口在该变量非空时使用内存凭据替身。它不隔离 `LoginWindow` 的浏览器资料路径，浏览器仍直接使用上述 LocalAppData 目录。启动检查不可因此打开真实教务窗口。

## 发布

### 标签自动发布

[Release 工作流](../.github/workflows/release.yml)在推送 `vX.Y.Z` 标签时自动发布：使用 Windows runner 还原并构建解决方案，执行 CI 安全离线检查，再发布 self-contained `win-x64` 单文件程序、打包标准 ZIP。工作流先创建草稿 Release，上传并核对唯一 ZIP 的名称、大小及发布说明，最后公开发布；同名 Release 已存在或任一步骤失败都会明确报错，上传失败时保留草稿供检查。普通分支与 Pull Request 的 [Build 工作流](../.github/workflows/build.yml)仍只负责构建和离线检查。

标签必须与[路线二项目配置](../src/UstcCourseAssistant.Route2/UstcCourseAssistant.Route2.csproj)的版本一致：`vX.Y.Z` 对应 `Version=X.Y.Z`、`AssemblyVersion=X.Y.Z.0`、`FileVersion=X.Y.Z.0`，任一不匹配即停止发布。当前源码对应 `v2.2.1`；本地检查、打包不会创建或推送标签，也不会发布线上版本。

发布说明仅取自 [CHANGELOG](CHANGELOG.md)中对应版本的唯一表格行，由 Python 3 和固定版本的 `markdown-it-py` 解析；缺失、重复或空内容均失败，不混入其他版本说明。安装依赖并单独生成说明：

```powershell
python -m pip install -r tools/release/requirements.txt
python tools/release/release_notes.py --tag v2.2.1 --output artifacts/release-notes.md
```

默认读取 `docs/CHANGELOG.md`，边界检查可用 `--changelog <路径>` 指定输入。Windows 本地执行同一构建、离线检查、单文件发布和打包流程：

```powershell
./tools/release/Publish-Release.ps1 -Tag v2.2.1
```

ZIP 校验使用 `pefile` 读取真实 PE，检查 Windows x64 GUI 类型及 FileVersion、ProductVersion；只允许一个根目录 EXE。生成说明、解压校验、发布暂存及其他中间结果统一放入被 Git 忽略的 `artifacts/`。

### 附件与本地产物

唯一标准发布附件为 `USTC_Automatic_Course_Selection_Tool_<版本号>.zip`，ZIP 根目录只包含最终的 `USTC_Automatic_Course_Selection_Tool_<版本号>.exe`。不附带子目录、HTML、日志、用户数据、未压缩 EXE 或 SHA256 文本，不生成发布哈希附件；应用更新继续从 GitHub 正式 Releases 获取对应标准 ZIP。

被 `.gitignore` 排除的本地 `release/` 只保留最新版本的一个最终标准 ZIP，不保留版本目录、展开程序、旧 ZIP 或其他文件。旧文件和目录必须移入 Windows 回收站，不直接删除。此规则仅约束本地产物，不删除 GitHub 历史 Releases。

历史发布说明可使用 `python tools/release/sync_release_notes.py --report artifacts/release-notes-sync.json` 从 CHANGELOG 补全；默认仅处理 `v1.0.0`、`v2.0.0`、`v2.0.1`、`v2.1.0`，可重复指定 `--tag` 缩小范围。命令只更新已有 Release 的说明并重新读取核查，不创建新版本、不删除历史 Release，也不改附件；报告不保存凭据。认证使用 `GH_TOKEN` / `GITHUB_TOKEN` 或 Windows 凭据管理器已有的 `gh:github.com:` 凭据；缺少凭据时立即报告失败。

### GitHub Pages

项目不使用 GitHub Pages，用户入口为仓库 README、源码、GitHub Releases 和程序内应用更新。仓库 Pages 分支发布配置已通过 GitHub API 删除；真实复查结果为 `has_pages=false`、读取 Pages 配置返回 `404`，不再从 `main:/docs` 自动部署。

GitHub 保留历史 Pages 构建与部署记录；站点配置及发布源已删除。

源码上传由 [`.gitignore`](../.gitignore)排除构建产物、发布目录、本机任务与账号偏好、活动日志、浏览器资料、临时恢复文件、Python 缓存及 `.env` 本机配置。源码、项目文档、应用图标和环境配置模板保留可提交；已被 Git 跟踪的文件需要单独检查，忽略规则不会取消跟踪。

## 文档入口

- [参与贡献](CONTRIBUTING.md)：开发协作与提交要求。
- [版本历史](CHANGELOG.md)：各版本的功能变化。
- [交接断点](development/ROUTE2_HANDOFF.md)：当前工作状态、时间、agent、模型与验证结果。
- [用户入口](../README.md)：功能与下载。
- [规则入口](../AGENT.md)：项目文档维护规则。
- [许可证](../LICENSE)：MIT License。
