# EqLink

[English](README.md) | [简体中文](README.zh-CN.md)

EqLink 为 Windows 设备提供由单台 Linux 服务端承载的虚拟 IPv4 网络。每台登记设备获得独立 IP，支持与服务端及其他客户端进行 ICMP、TCP、UDP 通信。

内层采用 **OpenVPN TCP**，外层优先 **Hysteria 2 / UDP**，回退 **VLESS + XHTTP + REALITY / TCP**。

**[v1.0.0 已发布](https://github.com/chens32/eqlink/releases/tag/v1.0.0)，完整验收尚未完成。** 本次构建、CI、隔离测试和历史两地测试各有范围，详见[发布说明](docs/release-1.0.0.zh-CN.md)和[验收状态](docs/validation-status.md)。

## 主要功能

- 同一份无导入密码的 v2 `.eqlink` 通用包可以供多台机器使用。
- 本机生成独立密钥，服务端签发各自的证书与外层凭据；支持单设备吊销。
- 服务端自动分配并持久保留地址，或验证客户端申请的静态地址。
- 自动、仅 UDP、仅 TCP 三种模式；同一时刻只有一个 OpenVPN 会话。
- 指定物理出口与专用 DNS，可与 Meta 等 TUN 共存。
- WFP 为已配置虚拟 IPv4 网段提供阻断和受限放行。
- WPF 界面显示连接状态、本机 TCP 连接数、虚拟网卡上下行速率、诊断与按网络保存的域名覆写。
- Debian/Ubuntu amd64、arm64 服务端，以及 Kubernetes/k3s 镜像构建、部署与升级工具。

## 接入流程

1. 管理员私密分发一份有效的 v2 通用包。
2. 新机器选择文件直接导入，生成本机身份，显示“已导入，待登记”。
3. 首次点击“连接”，经已选出口与外层登记，取得独立凭据并申请地址。
4. 使用服务端确认的地址建立 OpenVPN，验证接口、路由、WFP 与内层认证健康后显示“已连接”。
5. 设备通过虚拟 IP 互访，由服务端转发。外层切换会重建 OpenVPN，业务连接可能断开；UDP 恢复不会主动打断健康 TCP 会话。

公网仅开放配置的外层 TCP/UDP 端口；OpenVPN、控制、认证、管理等回环端口不能映射到公网。外层仅允许指定回环目标，并以拒绝规则结束。

## 快速开始

### Windows 客户端

1. 使用 Windows 10/11 x64。下载 [eqlink-client-1.0.0.exe](https://github.com/chens32/eqlink/releases/download/v1.0.0/eqlink-client-1.0.0.exe)，与 GitHub 附件摘要核对 SHA-256。
2. 以管理员授权安装。单入口安装器处理 EqLink 与固定版本官方 OpenVPN 依赖；卸载 EqLink 保留 OpenVPN 和其他配置。
3. 在“设备与诊断”导入管理员提供的当前 v2 包，保持自动地址或填写静态申请，点击连接。
4. 如果本机运行 Meta 等 TUN，在连接设置选择实际物理网卡与专用 DNS；无需停用 Meta。
5. 使用设备虚拟 IP 访问服务。需要域名时，可在“域名覆写”保存 `10.88.0.20 service.example.internal` 等条目。

安装器包含自包含 .NET 运行时，当前安装器未签名。通用包是可复用入网凭据，持有人在授权有效时可以登记新身份；不要公开上传包正文、本机身份数据或私钥。

### Linux / k3s 服务端

宿主机要求 Debian/Ubuntu、amd64 或 arm64、TUN、OpenVPN 2.6+、OpenSSL、iproute2、Python 和 systemd。安装与网络配置需要 root/sudo，见[宿主机安装说明](docs/server-installation.md)。

Kubernetes/k3s 使用同一份 Linux 服务端包构建镜像，见[安装说明](docs/kubernetes-installation.zh-CN.md)。支持 amd64/arm64，保持单副本、固定节点、Recreate、PVC、高端口 NodePort、仅 NET_ADMIN、只读根文件系统和 TUN 资源预检。首次安装与既有部署升级分别使用 deploy.py 和 upgrade.py；保留既有 CA、REALITY 密钥、地址、VIP 与管理网关扩展。

每个 release 固定上传 `eqlink-client-X.Y.Z.exe` 和两份 `eqlink-server-{arm64,amd64}-X.Y.Z.tar.gz`，Kubernetes 离线镜像、MSI、工具和验收材料保留在本地。后续发布遵循[发布流程](docs/releasing.md)。

## 边界与验收

- 当前为单服务端 IPv4 虚拟网络；不提供任意物理 LAN 路由、同 CIDR 多站点路由、全局上网出口、自动设备发现或服务端 HA。
- WFP 覆盖已配置虚拟网段的系统 IP 流量。管理员可修改保护，应用主动交给其他显式代理的请求不在此地址过滤范围。
- 域名覆写维护 Windows hosts 的 EqLink 专属区块，保留其他内容并拒绝冲突；独立 DNS/DoH 或远端代理可能绕过系统解析。后台服务运行时保持覆写，VPN 断开也不自动移除。
- TCP 连接数是本机网段内已建立连接，不是服务端在线设备数；速率为虚拟网卡字节计数，不含外层加密开销。
- 两台当前版本客户端、回退、地址/吊销、完整安装卸载重启、BFE/网卡/DNS、原生 k3s 恢复和长期稳定性仍需按验收记录补齐。CI 通过不等于上述现场验收完成。
- 抓包窗口没有观察到泄露，不代表所有时间都无泄露，也不保证外层不可被识别或封锁。

## 开发与构建

SDK 版本由 global.json 固定；另需 Python 3.12+、PowerShell。依赖版本、下载来源与 SHA-256 固定在 packaging/third_party.lock.json。命令见[英文 README](README.md#build-and-test)、[开发环境](docs/development.md)和[验收工具](packaging/acceptance/README.zh-CN.md)。

CI 按本次发布要求恢复，覆盖源码/历史秘密扫描、离线部署检查、Windows/Linux 核心测试、Windows 编译和 CodeQL；不自动部署真实服务端或运行安装卸载。

## 维护与许可证

[贡献指南](CONTRIBUTING.md) · [安全报告](SECURITY.md) · [变更记录](CHANGELOG.md) · [v2 协议](docs/protocol-v2.md)

EqLink 原创源码使用 **GPL-3.0-only**，见 [LICENSE](LICENSE)。第三方组件保留原许可，OpenVPN 的 GPLv2、Xray 的 MPL-2.0 等分发义务不因 EqLink 采用 GPLv3 而取消。公开发布前必须满足对应源码与通知要求，见[许可审查](docs/licensing.md)。
