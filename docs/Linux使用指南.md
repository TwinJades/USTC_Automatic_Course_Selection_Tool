# Linux 版使用指南

本文按当前这台 VPS 已安装好的状态说明操作，不需要重新上传或安装程序。适合第一次通过 SSH 使用选课助手的用户。

## 先了解这几个区别

- **Windows PowerShell**：你电脑上的终端，用于连接 VPS、把验证码图片取回电脑。
- **VPS 终端**：执行 `ssh vps` 后进入的 Linux 终端，绝大多数操作在这里完成。
- **后台服务**：选课助手已经交给 systemd 管理，关闭 SSH 不会关闭服务；但必须先成功登录并明确授权，才会执行选课。
- **没有网页管理面板**：通过命令操作，不要在浏览器输入 VPS IP 寻找选课界面。
- **时间统一使用北京时间**：即使服务器在东京，预约和运行时段也不用换算。

当前程序目录为 `/opt/ustc-course-assistant`，服务名为 `ustc-course-assistant.service`。下面所有 `./ustc-assistant` 命令都要在程序目录执行；重新连接 SSH 后，也需要重新进入该目录。

> 当前 HTTP 候选在真实账号登录中已出现短信页面解析失败，尚不能作为已验收可用版本。VPS 服务与无凭据初始化通过，不代表完整登录通过。遇到本文所述缺字段错误时停止重试，不要反复输入密码或继续请求短信；修复验收前不要授权监测。

## 1. 连接 VPS，确认程序正常

在 **Windows PowerShell** 执行：

```powershell
ssh vps
```

连接后，在 **VPS 终端**执行：

```sh
cd /opt/ustc-course-assistant
sudo ./ustc-assistant service status
sudo ./ustc-assistant status
```

第一条看系统服务，正常时应有 `ActiveState=active`、`SubState=running`。第二条看选课助手自身状态；初次使用应是未登录、无任务、监测未运行。

如果服务没有运行：

```sh
sudo ./ustc-assistant service start
```

不要再手动运行 `serve`，否则会和已有后台服务重复。

## 2. 登录学校账号

在 **VPS 终端**执行：

```sh
sudo ./ustc-assistant auth login
```

程序会依次询问：

1. `使用服务已保存的系统凭据？[y/N]`：第一次没有保存过，直接按 Enter。
2. `账号`：输入学校账号，再按 Enter。
3. `密码`：输入密码，再按 Enter。输入时不会显示字符或星号，这是正常的；可按 Esc 取消。

密码提交后，登录可能还在后台处理。执行：

```sh
sudo ./ustc-assistant auth status
```

**以状态中的 `Message` 为准**，不要把“认证已启动”当作“登录成功”。成功时应提示教务身份已验证。输出可能包含数字状态：

命令行版不会弹出网页或自动接着询问验证码。仅当 `State=2` 且 `ChallengeId` 非空时，才能按第 3 节继续人工验证。`State=4` 且 `ChallengeId=null` 表示认证已经失败，不要执行 `auth screenshot` 或 `auth submit-code`。

程序没有手动输入手机号的命令。普通 `normal` 短信登录沿用本次明确输入的学校账号，由学校查找其绑定号码；`group` 二次验证页使用学校页面提供的绑定号码。程序不把 normal 页面直接当作密码成功或新设备二验。短信页仅在本页风控开关为 USTC 时使用本页密钥；不会复用上一步密钥。修复已通过离线协议回归，但仍需本人完成实际短信验收。

| State | 含义 |
| --- | --- |
| 0 | 未登录 |
| 1 | 正在认证 |
| 2 | 等待人工验证，可能是图形验证码，也可能是短信 |
| 3 | 已认证 |
| 4 | 认证失败 |

密码、短信数字、图形验证码都只在程序提示时输入。不要将它们附在命令后，也不要通过聊天、环境变量或管道传入。

## 3. 遇到验证码怎么处理

### 3.1 获取图形验证码

如果状态提示需要图形验证码，或要求先请求验证码，在 **VPS 终端**执行：

```sh
sudo ./ustc-assistant auth send-code
sudo ./ustc-assistant auth screenshot
```

注意：当前 HTTP 版的 `auth send-code` 会先获取图形验证码，**不代表短信已经发出**。

这不是程序多此一举：学校官方短信组件默认要求图形验证码（发送接口收到错误图形码会返回业务码 `412`、错误码 `2180000`），所以发送短信前必须先认出图片字符。

`auth screenshot` 会输出服务器上的图片路径，类似：

```text
/opt/ustc-course-assistant/data/auth-challenge-某个标识.png
```

这是验证码图片文件，不是整个网页截图。记录实际返回的路径，不要照抄示例文件名。

### 3.2 在电脑上查看图片

图片受服务用户权限保护，直接 `scp` 可能没有读取权限。保留现有 SSH 窗口，再开一个 **Windows PowerShell** 窗口。

以下代码第一行中的图片路径必须替换为刚才实际返回的路径：

```powershell
$remoteImage = '/opt/ustc-course-assistant/data/auth-challenge-替换为实际标识.png'
$localImage = Join-Path $env:TEMP 'ustc-auth-captcha.png'
$b64 = ssh vps "sudo base64 -w 0 -- '$remoteImage'"
if ($LASTEXITCODE -ne 0) { throw '验证码图片读取失败，请检查路径或重新生成。' }
[IO.File]::WriteAllBytes($localImage, [Convert]::FromBase64String(($b64 -join '')))
Invoke-Item $localImage
```

不要使用 `ssh ... > captcha.png`，Windows PowerShell 的文本重定向可能破坏图片字节。

### 3.3 提交图片中的字符

回到原来的 **VPS 终端**：

```sh
sudo ./ustc-assistant auth submit-captcha
sudo ./ustc-assistant auth status
```

按提示隐藏输入图片中的字符。后续分两种情况：

- **密码前图形验证码**：完成后才首次提交密码，接着可能登录成功，也可能转入短信验证。
- **发送短信前图形验证码**：完成后才发送短信；看到“短信验证码已发送”后再检查手机。

**这是唯一会真实发送短信的步骤。** 失败时状态会显示请求阶段（如“图形验证码校验及短信发送失败：HTTP 404”）和学校返回码；把 `Message` 原文发我即可，不要重复发送。

如果进入另一个验证挑战，按新的状态提示重新执行本节，不要复用旧图片。验证码错误或过期时，重新获取图片；不要连续反复发送短信。

### 3.4 提交手机短信数字

确认短信已经发送后，在 **VPS 终端**执行：

```sh
sudo ./ustc-assistant auth submit-code
sudo ./ustc-assistant auth status
```

隐藏输入手机收到的数字。只有出现“教务身份已验证”后才继续。

验证命令默认使用唯一待处理挑战。若提示挑战不匹配，可用状态里的实际 `ChallengeId`：

```sh
sudo ./ustc-assistant auth submit-code --challenge '实际的ChallengeId'
```

若提示第三方验证码不支持，或认证请求返回非预期 HTTP 状态，请保留 `Message` 原文，不要反复尝试密码。当前没有自动切回浏览器的备用登录。

## 4. 先只读检查课程

下面命令不会执行选课或退课：

```sh
sudo ./ustc-assistant courses turns
sudo ./ustc-assistant courses selected
```

- `courses turns`：查看可用选课轮次，记录本人要用的轮次 ID。
- `courses selected`：查看当前已选课程。

`courses turns` 返回 `[]` 并提示“选课轮次未开放”是正常状态：学校当前没开放选课，不是登录失败。可用 `sudo ./ustc-assistant status` 里的 `Session.StudentId` 确认登录的是本人；等轮次开放后再添加任务并授权监测。

检查某个完整教学班代码：

```sh
sudo ./ustc-assistant courses check --turn 轮次ID --code '完整教学班代码'
```

其中 `轮次ID`、`完整教学班代码` 都要替换成真实值，不能原样执行。教学班代码不是课程名称，也不一定是短课程号。

## 5. 添加需要监测的课程

在 **VPS 终端**执行，替换真实教学班代码：

```sh
sudo ./ustc-assistant tasks add --code '完整教学班代码' --priority 1
sudo ./ustc-assistant tasks list
```

添加任务本身不是启动选课授权。`tasks list` 会列出任务 ID，后面修改、删除和预约都用这个 ID。

常用管理命令：

```sh
sudo ./ustc-assistant tasks edit --id '任务ID' --priority 2
sudo ./ustc-assistant tasks disable --id '任务ID'
sudo ./ustc-assistant tasks enable --id '任务ID'
sudo ./ustc-assistant tasks remove --id '任务ID'
```

**第一次使用不要添加 `--conflict`。** 它用于指定你允许退掉的冲突旧课，会让任务具备换课能力；退课后目标课不一定选得上，旧课也不保证恢复。

确实需要换课时，确认代码后再添加，例如：

```sh
sudo ./ustc-assistant tasks add --code '目标教学班代码' --priority 1 --conflict '允许退掉的旧教学班代码'
```

多个允许退掉的旧课使用多个 `--conflict`。不要把想保留的课程填进去。

## 6. 设置检查间隔和运行时段

查看当前设置：

```sh
sudo ./ustc-assistant settings show
```

例如设置每 15 秒检查，开启间隔抖动，不限制每天运行时段：

```sh
sudo ./ustc-assistant settings set --interval 15 --jitter true --schedule false
```

也可以只在每天北京时间 08:00 至 23:00 运行：

```sh
sudo ./ustc-assistant settings set --schedule true --start '08:00' --end '23:00'
```

`settings set` 只修改提供的字段。运行时段支持跨午夜；不要为了抢课无限缩短间隔。

## 7. 明确授权后启动监测

**下面的启动命令可能执行真实选课；配置了冲突旧课时也可能退课。** 请先确认登录身份、轮次、教学班代码和任务列表。

```sh
sudo ./ustc-assistant tasks list
sudo ./ustc-assistant monitor start --turn 轮次ID --authorize
sudo ./ustc-assistant status
```

成功后核对 `MonitoringRunning` 等状态和提示。不要只看到命令退出就认为监测已经运行。

查看最近记录：

```sh
sudo ./ustc-assistant logs --count 30
```

停止监测：

```sh
sudo ./ustc-assistant monitor stop
```

手动停止会撤销监测运行授权。以后想继续，需要重新执行带 `--authorize` 的启动命令。

监测授权成功后可以关闭 SSH；但会话过期或需要二次验证时，需要重新连接处理。Linux Cookie 仅保存在内存中，服务重启后也可能需要重新登录。

## 8. 预约某个北京时间执行

先用 `tasks list` 取得任务 ID。以下命令中的日期、时间和 ID 必须改成你自己的，示例不是可直接执行的预约：

```sh
sudo ./ustc-assistant appointment save --at 'YYYY-MM-DD HH:mm:ss' --run-when-missed false --completion stop --task '任务ID'
sudo ./ustc-assistant appointment show
```

- `--run-when-missed false`：错过预约时间不补执行。
- `--completion stop`：这一轮完成后停止，不把剩余任务转入持续监测。
- 多个任务重复添加 `--task '任务ID'`。

保存后只是草稿。核对无误后再授权：

```sh
sudo ./ustc-assistant appointment arm --authorize --turn 轮次ID
```

**预约授权也允许真实选退课。** 取消预约：

```sh
sudo ./ustc-assistant appointment cancel
```

需要修改已授权预约时，先执行 `appointment edit`，再保存修改并重新核对、授权。监测与预约是不同的运行入口：如果要让程序不再执行任何已安排的选课，分别停止监测并取消预约。

## 9. 遇到“结果不确定”或安全暂停

不要反复提交，也不要删除日志。先执行：

```sh
sudo ./ustc-assistant operations status
sudo ./ustc-assistant operations verify
```

`operations verify` 需要有效登录，进行只读核验。还要到学校教务网页确认实际选课结果。

只有完成最新核验、明确知道当前结果后，才考虑：

```sh
sudo ./ustc-assistant operations acknowledge --authorize
```

这不是通用的“清除错误”按钮；不要把它和重新启动监测一起盲目执行。

## 10. 日常停止、退出和重新连接

| 目的 | 在 VPS 执行 |
| --- | --- |
| 查看选课助手状态 | `sudo ./ustc-assistant status` |
| 停止持续监测 | `sudo ./ustc-assistant monitor stop` |
| 取消预约 | `sudo ./ustc-assistant appointment cancel` |
| 退出学校登录 | `sudo ./ustc-assistant auth logout` |
| 安全停止整个后台服务 | `sudo ./ustc-assistant service stop` |
| 启动后台服务 | `sudo ./ustc-assistant service start` |
| 查看系统服务状态 | `sudo ./ustc-assistant service status` |
| 查看全部命令 | `./ustc-assistant help` |

停止整个服务会等待安全停机；不要对正在选退课的进程使用强杀命令。正常重启不会无条件恢复监测：程序还需重新核验登录身份、原轮次、授权和安全状态。

退出 SSH 使用 `exit`。重新连接后，执行：

```sh
cd /opt/ustc-course-assistant
sudo ./ustc-assistant status
```

## 11. 可选：保存账号密码

建议第一次先用交互登录验证成功，再决定是否保存。

```sh
sudo ./ustc-assistant credentials set
sudo ./ustc-assistant credentials status
```

按提示输入账号和隐藏密码。此操作可能安全停止并重新启动服务，应在没有运行任务时进行；之后重新检查登录、监测和预约状态。

凭据通过系统 `systemd-creds` 加密保存，不写入普通配置。下次 `auth login` 可选择使用已保存凭据，但这不会省掉学校要求的短信或验证码。

删除已保存凭据：

```sh
sudo ./ustc-assistant credentials delete
```

系统加密凭据的真实使用流程仍待验收；不要将其当作保证永久免登录。

## 12. 更新与排错

检查更新：

```sh
sudo ./ustc-assistant update check
```

目前 GitHub 正式版本仍是旧 Windows 包，本 Linux 候选尚未上传；检查可能没有可用的 Linux 附件。**现在不用为了更新而执行安装命令。**

未来有匹配的正式 Linux 目录包后，才使用：

```sh
sudo ./ustc-assistant update apply --confirm
```

这是程序更新，不会授权选课，但会安全暂停业务并重启服务。更新使用 CLI 自身的辅助模式，没有另带更新器。

服务启动失败时查看：

```sh
sudo journalctl -u ustc-course-assistant.service -n 50 --no-pager
```

分享错误前检查并隐藏个人信息。不要直接改 `data/` 中的状态或事务文件，也不要自行清空 `.update-maintenance/`。

## 第一次建议只做到这里

1. SSH 连接，确认服务运行。
2. 输入账号密码，完成可能出现的图形和短信验证。
3. 确认 `auth status` 提示身份已验证。
4. 只读查看轮次和已选课程。
5. 核对目标教学班代码，再决定是否添加任务和授权监测。

更完整的功能与安全规则见 [项目说明](PROJECT.md)。
