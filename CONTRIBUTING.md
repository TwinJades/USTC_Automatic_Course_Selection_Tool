# 参与贡献

感谢关注 USTC 选课助手。提交改动前，请确认改动不会绕过项目现有的真实操作确认、安全暂停、日志脱敏或凭据存储边界。

## 开发环境

- Windows x64
- .NET 9 SDK
- Microsoft Edge WebView2 Runtime

```powershell
dotnet restore UstcCourseAssistant.sln
dotnet build UstcCourseAssistant.sln -c Release
dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks -c Release --no-build
```

上述检查应保持离线，不得访问真实教务系统，也不得发送真实选课或退课请求。

## 提交建议

1. 每个提交只处理一个明确问题。
2. 不要提交 `bin`、`obj`、本机 SDK、运行日志、测试数据或发布二进制。
3. 界面或业务行为变化应同步更新 `README.md` 和 `VERSION_HISTORY.md`。
4. Pull Request 中说明改动目的、用户可见变化和已运行的验证。

