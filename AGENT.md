# 项目文档规则

本项目的交接文档和 `README.md` 只描述项目现状，不涉及历史。

- 开发者与 AGENT 从[项目说明](docs/PROJECT.md)了解项目信息，再阅读[交接断点](docs/development/ROUTE2_HANDOFF.md)。
- 项目说明文档统一放入 `docs/`；根目录保留面向用户的 `README.md`、`LICENSE` 和本文件 `AGENT.md`。
- 面向用户的 `README.md` 只说明功能、下载方式，并指向开发者文档。
- 交接文档只维护当前断点，每次写入实际时间（包含时区）、agent 和模型；版本历史统一维护在[版本历史](docs/CHANGELOG.md)。
- 发布 ZIP 只包含最终 `.exe`，不生成或附带 SHA256 文本。
- 删除文件或目录必须移入操作系统回收站。
