# 项目说明

## 项目定位与发布状态

USTC 选课助手是面向中国科学技术大学综合教务系统的非官方工具，采用 [MIT License](../LICENSE)，与学校无隶属、合作或授权关系。程序不提供云账户或公网管理接口；读取及真实选退依赖学校接口，最终结果须本人到教务系统核对。换课、退课后的恢复均不保证成功。

线上正式 2.2.1 是 Windows x64 单 EXE ZIP。源码版本为 `3.0.0`，Windows 和 Linux 目录包随源码交付，尚未作为 GitHub Release 附件发布；不得覆盖已有线上 2.2.1 附件。目录包命名为：

- `USTC_Automatic_Course_Selection_Tool_<版本号>_win-x64.zip`
- `USTC_Automatic_Course_Selection_Tool_<版本号>_linux-x64.zip`

ZIP 自包含运行库，根目录包括 `app/`、空的 `data/`、`package-manifest.json` 和对应平台的稳定启动器；Linux 另有 `service/`。运行时保留整个目录，不是将单个 EXE 抽出来使用。

**验证边界：**东京 Ubuntu 26.04 VPS 已用真实账号完成一次 HTTP 登录：密码提交 → 短信登录页 → 按账号绑定的图形码 → 短信码 → 只读身份核验返回本人学号；当时选课轮次未开放，未执行任何选退课。systemd 安装、专用 UID、Unix socket 控制、服务重启、CLI 复用维护模式同版本目录替换，以及下载中断和旧 app 已移走的自动恢复也已实测。VPS 现运行 3.0.0 目录包，升级后内存登录按预期失效，当前未登录且无任务或监测授权。系统加密凭据、不同版本在线更新和 2 核 2 GB 性能未验收。

本地验证已观察：解决方案 Release 构建成功（0 警告、0 错误）；CoreChecks（含预约监测上下文边界回归）、完整 WPF 界面与 EngineChecks 通过。HTTP 认证检查覆盖 AES 表单、风控令牌缺失拒绝、身份核验、密码及短信验证码人工重试、退出挑战失效、307/308 凭据重放和跨域拒绝；模拟响应不访问学校。控制器、IPC 和迁移检查，以及跨进程启动身份一致性检查通过。

Windows 本地完整进程更新 smoke 已按 3.0.0 → 3.0.1 通过：复用真实桌面 EXE 的辅助模式，真实根启动器在包含单引号的路径运行，覆盖 Downloading 无 helper、缺失 journal、旧程序或新程序已放置、ready 前及 Committed 后恢复，以及 A→B 提交、数据哨兵不变和活动目录回收。此结果不代表真实 GUI 在线 GitHub 更新或 Linux 实机在线更新通过。

Windows 和 HTTP Linux 3.0.0 目录包已生成并通过清单、二进制版本（入口、Core、CLI 均为 3.0.0.0）和文件摘要校验，`data/` 为空，两平台 Core 摘要一致。旧 `deliverables-*` 已移入回收站；以下包尚未在线发布：

| 包（均位于 `artifacts/`） | 字节数 | ZIP 条目 | SHA256 |
| --- | ---: | ---: | --- |
| `candidate-3.0.0-win-final/USTC_Automatic_Course_Selection_Tool_3.0.0_win-x64.zip` | 63,063,641 | 406 | `9ffcdaf4c305aa225baaa59d30d2d948af0159dc13deefe4f21c557a6eec23a2` |
| `candidate-3.0.0-linux-final/USTC_Automatic_Course_Selection_Tool_3.0.0_linux-x64.zip` | 35,027,645 | 194 | `1d30370d62a0765da10698d7f8aeac69eae7a56db569f82dbf5875d75fe503b3` |

Linux 包的 launcher 和 CLI 设置 Unix `100755`；包中没有 Node、Chromium、Playwright 或独立更新器。HTTP 认证使用内存 Cookie，会话不持久保存。两平台 `app/UstcCourseAssistant.Core.dll` 摘要均为 `3711b168e0f8c346be6cb8bf3109aef99f80ed214f02784a8904d8c4f5545bbe`。

3.0.0 Windows 包解压到隔离数据目录后以最小化方式实际启动，主窗口正常出现并可正常关闭；本次未重新打开 WebView2 登录页，也未输入或提交凭据。该检查不同于开发输出的完整界面检查。打包后在调度层补充了预约上下文不匹配的前置阻断，避免每 250 毫秒重复发起只读教务请求；CoreChecks 与 EngineChecks 已重跑通过。

## Windows 使用与迁移

需要 Windows 10/11 x64、WebView2 Runtime 和学校网络。关闭运行中的旧版，将 3.0.0 ZIP **手动解压到新的可写目录**，保留原 EXE，不覆盖线上附件或其下载目录。旧版应用内更新器不能跨格式升级。双击包根的 `USTC选课助手.cmd`，路径稳定，不依赖当前工作目录；需要程序持续运行并保持电脑清醒。

首次启动在读取新配置前迁移 `%LOCALAPPDATA%/USTC Course Assistant Route2` 的偏好、任务、预约、操作记录、活动日志及其恢复副本到包内 `data/`。迁移会复制并核验，目标已有不同数据时中止而不覆盖；验证提交后才将旧目录移入 Windows 回收站。若旧目录占用或权限不足，会提示清理待完成并在之后启动时重试。路线一资料不迁移；旧浏览器资料和旧更新下载不复制。不要为迁移手工删除旧目录或未决操作证据。

点击“打开教务系统”，在 WebView2 窗口完成学校登录；“验证只读会话”用于核验身份。可选保存账号和密码；密码由当前用户的 Windows 凭据管理器保护，不写入普通 JSON。自动登录默认关闭，仅处理正常账号密码表单，短信、验证码或其他二次验证由本人在窗口完成。保留登录状态选项控制正常退出后的浏览器 Cookie；主动退出会抑制本次自动登录。系统管理员或控制当前用户的程序仍可能访问其凭据或会话，操作系统保护不是绝对安全保证。

主窗口提供任务监测、定时抢课、活动记录、调试工具及账号与安全页；首次启动有内置引导。先登录、添加完整教学班代码和本人允许退掉的冲突旧课，通过只读检查核对，再明确确认真实操作并启动监测。可在界面停止、修改任务和查看状态。Windows 启动或重新登录不会自动恢复持续监测；重新启动需再次授权。

## Linux 安装和 SSH 操作

候选目标为 x86_64、glibc、systemd 250 及以上，需 `systemd-creds`、systemd 服务管理工具、访问学校的网络和 .NET 的系统运行依赖（含 ICU；Ubuntu 26.04 为 `libicu78`）。Linux 使用 HTTP 认证，不携带浏览器、Playwright 或 Node，不需要 NSS/GBM 等浏览器共享库。无图形桌面也可通过 SSH 使用；不承诺 Alpine、ARM 或其他不符合基线的环境。

将 Linux 候选包解压到受 root 管理的稳定目录；包根和各级父目录不能由其他普通用户或组写入。不要让不可信用户修改程序文件或启动器。安装程序创建或校验不可登录的专用系统账户 `ustc-assistant`，程序文件归 root，包内 `data/` 由服务账户使用；不会递归更改已有任意数据的所有权。已有数据若不属于专用 UID，安装会拒绝，须先安全处理。服务以该专用账户访问教务，而非 root；systemd 注册和加密凭据属于操作系统集成例外。

以下命令均在解压后的包根执行，管理服务需要 root。首次安装后可选择保存凭据，也可跳过此步骤、启动后交互登录：

```sh
sudo ./ustc-assistant service install
sudo ./ustc-assistant credentials set
sudo ./ustc-assistant service start
sudo ./ustc-assistant service status
sudo ./ustc-assistant status
```

`credentials set` 在交互终端读取账号及隐藏密码，服务会安全停稳后修改，原先运行时再启动。密码通过 `systemd-creds` 使用主机密钥加密，密文位于 `/etc/credstore.encrypted/ustc-course-assistant.cred`，systemd 为服务提供解密结果；不使用明文文件替代。`sudo ./ustc-assistant credentials status` 查询，`sudo ./ustc-assistant credentials delete` 删除已保存凭据。root、服务账户被控制或系统密钥泄露时无法保证凭据安全。

SSH 日常命令可用服务账户或 `sudo` 执行；控制 socket 只接受服务 UID 或 root。没有凭据时亦可交互登录。密码、短信和图形验证码不要放在命令行、环境变量、管道或 shell 历史中：

```sh
sudo ./ustc-assistant auth login
sudo ./ustc-assistant auth status
sudo ./ustc-assistant auth send-code
sudo ./ustc-assistant auth submit-code
```

登录询问是否使用已保存系统凭据，否则交互输入账号和隐藏密码。Linux 通过 HTTP 完成学校动态表单、AES 密码处理、设备风控令牌和认证跳转；仅在只读验证学生身份后报告认证成功。学校要求第三方验证码时停止，不伪造风控错误来跳过验证；认证接口若改为强制加密，服务器错误会按阶段和 HTTP 状态报告，不会静默继续。真实账号和二次验证仍需人工验收。

按状态提示运行 `auth send-code` 获取图形验证码图片，再用下列命令保存并查看图片、隐藏输入答案；密码前图形验证码完成后才首次提交密码，短信前图形验证码完成后才发送短信。`auth submit-code` 提交本人收到的短信数字。命令可通过 `--challenge ID` 指定挑战，省略时使用唯一待处理挑战。请求失败不自动重发密码或短信。

```sh
sudo ./ustc-assistant auth screenshot
sudo ./ustc-assistant auth submit-captcha
```

`auth screenshot` 返回包内受权限保护的验证码图片文件路径，不需要浏览器截图。图片可能包含敏感挑战内容，勿公开分享；认证完成或退出后回收。真实账号已完成一次完整登录与只读身份核验；其他短信/图形码分支与真实选退课仍未在线验收。

查看全部实际命令与选项：`./ustc-assistant help`。以下示例中的轮次、教学班和任务 ID 必须换成本人只读核对得到的真实值；只有明确授权才会启动真实操作：

```sh
sudo ./ustc-assistant courses turns
sudo ./ustc-assistant courses selected
sudo ./ustc-assistant courses check --turn 1 --code 001683EX.01
sudo ./ustc-assistant tasks add --code 001683EX.01 --priority 1
sudo ./ustc-assistant tasks list
sudo ./ustc-assistant monitor start --turn 1 --authorize
sudo ./ustc-assistant monitor stop
```

任务还可用 `tasks edit --id ID` 搭配 `--code`、`--priority`、`--conflict` 修改，或用 `tasks enable|disable|remove --id ID` 操作；`--conflict` 可重复以指定本人允许退掉的旧课。`settings show` 查看设置，`settings set` 可指定 `--interval SECONDS`、`--jitter true|false`、`--schedule true|false`、`--start HH:mm`、`--end HH:mm`；仅修改提供的字段。`status`、`logs --count N`、`auth status` 和只读查询支持 `--json`，日志数量为 1–1000。

保存预约时使用 `tasks list` 中的任务 ID，时间为北京时间；`--task` 可重复。示例不应直接复制执行：

```sh
sudo ./ustc-assistant appointment save --at '2026-11-04 09:00:00' --run-when-missed false --completion stop --task 00000000-0000-0000-0000-000000000001
sudo ./ustc-assistant appointment show
sudo ./ustc-assistant appointment arm --authorize --turn 1
sudo ./ustc-assistant appointment cancel
```

预约启用会授权真实选退；编辑使用 `appointment edit` 后需重新授权。服务管理另有 `sudo ./ustc-assistant service stop`、`service start` 和 `service uninstall`；停止会等待安全停机，不应强杀正在进行的真实操作。`serve` 是前台服务入口，无 SSH 标准输入依赖；使用 systemd 时 SSH 断开不会要求结束服务。

## 授权、预约与安全保护

监测任务的启用标志不等于真实运行授权。启动监测时校验当前学生和开放轮次；Linux 正常重启仅在已保存授权、无安全暂停及未决事务、身份和原轮次验证一致且任务配置有效时恢复。手动停止、退出登录、身份错配或安全异常会撤销或暂停授权；不会自动更换轮次。认证等待期间不发起新的选退请求，短信成功后还要核对上下文。Windows 不自动恢复重启前的持续监测。

所有预约和每日监测时段采用北京时间 UTC+08:00，与宿主时区无关，支持跨午夜。一个预约保存独立课程快照；`--run-when-missed` 控制错过后的补执行。到期时未认证先等待，认证后按该设置决定是否补执行。未开始的排队预约在正常停机时可恢复待命；崩溃时已排队或运行且尚未完成选课的预约标记中断，不自动重放；已完成选课阶段仅恢复幂等导入。

所有真实选退经过统一队列和操作保护；请求已发送或可能发送时不自动重试。结果不确定时先查看 `operations status`，登录后 `operations verify` 只读核验，并到教务系统确认实际状态；完成最新核验后才可用 `operations acknowledge --authorize` 明确解除保护。不要直接删除操作日志或反复提交。

## 数据、更新和回收

候选包运行数据位于包内 `data/`：任务、偏好、预约、操作日志、活动记录、运行授权状态、迁移暂存、验证码图片、临时文件和本地控制对象。Linux HTTP 认证会话及 Cookie 仅在内存中；Windows WebView2 浏览器资料位于包内数据目录。密码由上述操作系统凭据机制单独保护；不将密码、验证码或 Cookie 写入普通配置和活动日志。服务控制通过本地 Unix socket，不开放 HTTP 管理端口；包路径过长或包含不安全链接时应按错误提示调整，不能改用公共临时目录绕过保护。

Windows 从界面检查和确认安装；Linux 检查使用 `sudo ./ustc-assistant update check`，安装使用 `sudo ./ustc-assistant update apply --confirm`，仅支持已运行的 systemd 服务。更新先阻止新真实操作并等待已开始操作结束。Linux 由 root 维护进程管理程序文件，学校登录服务不提权；事务位于 root 保护的 `.update-maintenance/active/`，不放在服务账户可写的 `data/`。systemd 的 root `ExecStartPre` 在业务 UID 启动前调用 `recover-update`。VPS 已验证同版本替换提交和两类中断恢复，不等同在线不同版本更新验收。前台服务不支持应用内安装，需安全停止后手动换包。

Windows 删除进入系统回收站；Linux 服务数据的应用回收区位于 `data/trash/`，root 更新维护事务清理使用包内受 root 保护的 `.update-maintenance/trash/`，不使用服务账户可写的回收区。系统服务注册和系统凭据的维护回收位置另由系统集成管理。回收是可恢复移动，仍占磁盘空间，不会自动清空。系统日志、系统凭据、服务注册和系统回收区不在包内数据完全自包含的范围内；VPS 已观察维护事务回收完成。

## 架构与开发

[解决方案](../UstcCourseAssistant.sln)包含路线一 WPF、路线二 WPF、跨平台 Core 和 CLI；路线一不迁移用户资料。桌面使用 WebView2，Linux CLI 使用 Core 的 `HttpAuthenticationService`。Core 不依赖 Playwright，包含 HTTP 认证与课程客户端、监测预约、安全事务、状态持久化、北京时间、包路径及更新服务。更新复用桌面 EXE 或 Linux CLI 的辅助模式，不额外发布更新器工程或重复运行库。版本元数据仍在路线二项目配置中。

需要 .NET 9 SDK；Windows 桌面编译与界面检查还需 Windows 和 WebView2 Runtime。仓库根目录可运行：

```powershell
dotnet restore UstcCourseAssistant.sln
dotnet build UstcCourseAssistant.sln -c Release
dotnet run --project tests/UstcCourseAssistant.CoreChecks -c Release --no-build
dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks -c Release --no-build -- --skip-ui
```

完整 WPF 检查去掉 `-- --skip-ui`；离线检查不得使用真实教务凭据或执行真实选退。`USTC_ROUTE2_TEST_DATA_ROOT` 是测试隔离钩子，不能作为生产登录安全开关。实际运行情况须在交付中单独报告。

## 发布

[Release 工作流](../.github/workflows/release.yml)配置了 Windows/Linux 两平台目录包构建、校验和草稿 Release 汇总；工作流存在不等于已经运行或发布。标签须与路线二项目版本元数据及 [CHANGELOG](CHANGELOG.md) 对应版本行一致。候选输出目录必须位于 `artifacts/`，同名候选已存在时选择新目录；本地打包不会创建标签、推送或发布：

```powershell
python -m pip install -r tools/release/requirements.txt
python tools/release/release_notes.py --tag v3.0.0 --output artifacts/release-notes.md
./tools/release/Publish-Release.ps1 -Tag v3.0.0 -Runtime win-x64 -OutputDirectory artifacts/candidate-3.0.0-win
./tools/release/Publish-Release.ps1 -Tag v3.0.0 -Runtime linux-x64 -OutputDirectory artifacts/candidate-3.0.0-linux
python tools/release/verify_archive.py --tag v3.0.0 --runtime win-x64 --archive artifacts/candidate-3.0.0-win/USTC_Automatic_Course_Selection_Tool_3.0.0_win-x64.zip
python tools/release/verify_archive.py --tag v3.0.0 --runtime linux-x64 --archive artifacts/candidate-3.0.0-linux/USTC_Automatic_Course_Selection_Tool_3.0.0_linux-x64.zip
```

发布脚本按平台组装：Windows 只发布桌面及其运行依赖，Linux 发布 CLI；两端均不包含 Playwright、Chromium 或独立更新器。清单覆盖包文件，验证平台、版本与文件摘要。应用更新还要求 GitHub Release 附件摘要匹配，不能仅凭同包清单信任来源。现有线上 2.2.1 是旧格式，首次切换目录包必须手动解压，不用旧版应用内更新器切换。启动器协议变化时也须手动解压新候选，不覆盖旧目录。项目不使用 GitHub Pages。

## 文档入口

- [用户 README](../README.md)
- [版本历史](CHANGELOG.md)
- [参与贡献](CONTRIBUTING.md)
- [项目规则](../AGENT.md)
