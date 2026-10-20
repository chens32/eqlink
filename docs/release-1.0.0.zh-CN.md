# EqLink v1.0.0

本次将 Windows 客户端与 Linux 服务端统一为 1.0.0，提供通用 v2 入网包、独立设备身份、自动/静态地址申请、HY2 优先与 REALITY 回退、指定物理出口、WFP 网段保护、连接/速率统计及域名覆写。保持现有 v2 协议与身份兼容。

源码审查修复控制接口响应读取边界、未定义的地址模式；统一组件版本并更新 MSI ProductCode，补充公开仓库维护文件、许可清单、CI 与 CodeQL。

## 下载

每个 release 固定上传三个文件，后续版本沿用此命名：

- `eqlink-client-1.0.0.exe`：Windows x64 一键安装器，仍处理官方 OpenVPN 依赖。
- `eqlink-server-arm64-1.0.0.tar.gz`：Linux ARM64 服务端。
- `eqlink-server-amd64-1.0.0.tar.gz`：Linux AMD64 服务端。

客户端本次仅调整公开文件名，安装器内容不变。独立 MSI、工具、校验文件与验收材料保留在本地；源码与文档从仓库获取。

## Kubernetes / k3s

不再单独下载 k3s 离线发行包。使用节点对应架构的服务端包构建镜像，导入固定节点后运行部署工具。完整命令见 [Kubernetes / k3s 安装说明](kubernetes-installation.zh-CN.md)，包含 TUN 插件、PVC、高端口映射、生成通用包及已有环境升级。现有环境保留 CA、REALITY 密钥、虚拟网段、VIP 与 Rancher 网关。

历史实测和新版本验证仍区分记录，完整验收状态见[验收说明](validation-status.md)。原创源码使用 GPL-3.0-only，第三方组件保留原许可，见[许可审查](licensing.md)。
