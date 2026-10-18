# 开发与提交

EqLink 当前为 0.2.0 测试版。开始修改前阅读 [README](README.md)、[开发环境](docs/development.md)和[验收状态](docs/validation-status.md)。

## 本地检查

使用 `global.json` 指定的 .NET SDK，在普通用户终端执行：

```text
python tools/check-repository.py
dotnet build src/EqLink.Server/EqLink.Server.csproj -c Release
dotnet run --project src/EqLink.Tests/EqLink.Tests.csproj -c Release
python packaging/k3s/test-upgrade.py
git diff --check
```

Windows 上还应编译 ClientGui、ClientService 和对应测试项目。Python 清单测试依赖 `.github/requirements-ci.txt`；官方 Kubernetes schema 的准备方式见 CI 工作流。

当前 GitHub CI 已暂停，工作流只保留手动入口，仓库中的工作流也已停用；后续按项目进度明确恢复。开发期间按需执行本地检查。

恢复后，现有 CI 配置覆盖源码边界与历史秘密扫描、离线清单/升级检查、Windows/Linux 核心测试及 Windows 客户端编译。CI 不执行真实服务器部署、WFP 修改、网卡故障或安装卸载；绿色 CI 不能代替这些验收。

## 提交要求

- 使用独立分支和可复核的提交，说明改动、验证和未执行的场景。
- 保持固定虚拟 IPv4、单 OpenVPN 会话、三种模式及故障时阻断的语义。
- 生产配置、设备包、密钥、抓包、诊断和备份保存在 Git 之外。使用运行时生成的测试身份或明确的占位值。
- 提交前审查 `git diff --cached` 和 `git ls-files`；`.gitignore` 不会移除已经跟踪的文件。
- 修改依赖时更新版本、下载来源及 SHA-256。工作流中的第三方 Action 使用完整提交 SHA。
- 保留原始失败和复测记录；未执行或缺少条件的结果使用 `skipped` / `unavailable`。
- 代码的项目级许可证尚未确定；第三方组件遵守各自许可证，见 [THIRD_PARTY](packaging/THIRD_PARTY.md)。

## 基础维护范围

源码通过 Git 管理，安装包和私密验收材料单独保存。发布前按 [验收工具](packaging/acceptance/README.zh-CN.md)执行对应场景，并将脱敏结论与完整证据分开。对真实 k3s 的修改需要目标环境的管理授权及备份/回滚条件。
