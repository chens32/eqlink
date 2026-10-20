# 开发环境

## 平台与 SDK

- 使用 `global.json` 固定的 .NET 10 SDK：10.0.401，允许同 feature band 的补丁更新。
- Python 3.12 或更高；CI 使用 3.12。PowerShell 7 用于现有 `.ps1` 构建入口。
- Core、Server、PeerProbe 与大部分纯逻辑测试可在 Linux/Windows 编译。
- ClientGui、ClientService、GuiTests 和 WindowsTests 使用 Windows 目标。WPF、WFP、TAP、DPAPI 及安装生命周期验证需要实际 Windows x64 环境。

Linux 上可以用普通用户编译 Server 和运行隔离逻辑测试。依赖安装、TUN/路由、命名空间/chroot、ARM64 根文件系统构建等步骤按需提权。日常开发不要求 root SSH。

## 常用命令

```text
dotnet --version
python tools/check-repository.py
dotnet build src/EqLink.Server/EqLink.Server.csproj -c Release
dotnet run --project src/EqLink.Tests/EqLink.Tests.csproj -c Release
python packaging/k3s/test-upgrade.py
```

以上默认核心测试不连接现网服务端，不安装 WFP，不修改生产部署。带 `--real` 的外层集成测试和 `packaging/acceptance` 中的故障测试需单独准备环境；执行前阅读相应说明。

## 打包

- Windows：`packaging/build-windows.ps1` 校验依赖及 OpenVPN MSI 签名，构建 WiX MSI/Burn；保留 Windows 构建节点。
- Linux 服务端：`packaging/build-server.ps1 -Arch amd64` 或 `-Arch arm64` 交叉发布并打包依赖。
- k3s：当前 `packaging/build-k3s.ps1` 从 Windows 调用 WSL。原生 Linux 迁移需要适配入口；底层脚本使用 debootstrap、chroot、unshare，amd64 跨架构测试另需 QEMU/binfmt。
- 依赖清单为 `packaging/third_party.lock.json`。构建产物、依赖缓存和验收材料写入被忽略的 `artifacts/`。

将项目迁移到其他目录后重新生成 `bin/obj`，不要复用包含旧绝对路径的构建缓存。宿主机服务安装、真实 k3s apply、Windows 安装与故障注入不属于常规源码 CI。

## 源码与证据

Git 保存可审查源码、脚本和文档；`.eqlink`、生产证书/状态、诊断原文、抓包及私密备份独立保存。既有 0.2.0 二进制的来源以原 `release-manifest.json` / `SHA256SUMS` 为准，不能用新的源码提交替换其历史构建身份。

EqLink 原创源码使用 GPL-3.0-only；第三方组件保留各自许可，见 [许可审查](licensing.md)和 [THIRD_PARTY](../packaging/THIRD_PARTY.md)。
