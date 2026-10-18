# EqLink 0.2.0 测试版

当前存在待定位的虚拟网络就绪状态反复变化和间歇丢包；完整验收尚未完成，见[验收状态](docs/validation-status.md)。

本轮增加 Windows 虚拟网段保护、物理出口变化恢复、主动健康检查、脱敏诊断以及使用 TUN 设备插件的 k3s 部署和安全升级工具。设备包仍兼容 0.1.x。完整 Windows 生命周期、多客户端互访和真实集群升级是独立验收门槛；测试版不代表原计划全部验收完成。

开发与维护：[环境要求](docs/development.md) · [贡献说明](CONTRIBUTING.md) · [安全报告](SECURITY.md) · [更新记录](CHANGELOG.md)。项目级许可证尚未指定；第三方组件遵循各自许可证，见 [THIRD_PARTY](packaging/THIRD_PARTY.md)。

EqLink 是单服务器、Windows 客户端的虚拟 IPv4 网络。设备用固定虚拟 IP 访问服务端及其他设备，支持 ping、TCP 和 UDP。首版不提供物理局域网路由、全局上网代理或设备名发现。

```text
Windows 应用 → OpenVPN TUN/TCP → 本机 CONNECT 选择器
                                  ├→ Hysteria 2/UDP（首选） ─┐
                                  └→ XHTTP+REALITY/TCP（回退）├→ Linux → OpenVPN(127.0.0.1)
                                                            └→ 健康检查(127.0.0.1)
```

同一时刻只有一条 OpenVPN 会话。外层切换会让 OpenVPN 重连，已有应用会话可能中断。服务端仅允许已认证设备经外层访问本机 OpenVPN 与健康检查端口；其他目标由 Hysteria ACL 和 Xray 路由拒绝。OpenVPN 使用 `client-to-client` 完成设备互访。

## 构建

在 Windows 上准备 `global.json` 指定的 .NET 10 SDK、Python 3.12+ 和 PowerShell：

```powershell
pwsh -File packaging/build-windows.ps1
pwsh -File packaging/build-server.ps1 -Arch amd64
pwsh -File packaging/build-server.ps1 -Arch arm64
pwsh -File packaging/build-tools.ps1
```

也可使用 `powershell -File`。构建脚本按 [锁定清单](packaging/third_party.lock.json) 下载并校验版本和 SHA-256，并检查官方 OpenVPN MSI 的 Authenticode 签名。产物在 `artifacts/package/EqLink-Setup.exe`、`artifacts/server/eqlink-server-amd64.tar.gz` 和 `artifacts/server/eqlink-server-arm64.tar.gz`。`artifacts/` 不纳入 Git。Windows 安装包包括 EqLink、Hysteria、Xray 和官方 OpenVPN MSI；若机器已有 OpenVPN 2.6 或更高版本则跳过 OpenVPN 安装，卸载 EqLink 时保留 OpenVPN。

## Linux 服务端

ARM64 k3s 测试部署使用独立的离线镜像包与 [k3s 说明](packaging/k3s/README.zh-CN.md)。下文的宿主机安装脚本适用于 systemd；k3s 容器使用新的 `serve` 监督程序。

构建离线 k3s 测试包可执行 `powershell -NoProfile -File packaging/build-k3s.ps1`，需要现有 Debian WSL、`debootstrap`、Python `jsonschema` 和管理员构建权限。脚本在独立 `/tmp/eqlink-k3s-build.*` 目录构建 Debian ARM64 根文件系统，使用已校验的临时 QEMU 注册进行只读镜像运行检查，产物为 `artifacts/k3s/eqlink-k3s-arm64-test-0.2.0.tar.gz`。构建不会调用对端集群。默认 `privileged: false`，仅 `NET_ADMIN`，依赖节点扩展资源 `armhpc.home.arpa/tun`。已有部署使用 `upgrade.py`；首次安装使用 `deploy.py`。

要求 Debian/Ubuntu amd64 或 arm64、OpenVPN Community 2.6+、OpenSSL、`iproute2`、Python 3 和 systemd。先准备与 Hysteria 域名匹配的 TLS 证书及私钥，用打包的 `xray x25519` 生成 REALITY 密钥，并选择服务器可访问、支持 TLS 1.3 的 REALITY 目标。

将下列内容保存为仅管理员可读的 `server-settings.json`，替换示例域名、证书路径和密钥。UDP/TCP 可以使用同一个数字端口。关闭路径时将 `enableUdp` 或 `enableTcp` 设为 `false`。

```json
{
  "publicHost": "vpn.example.net",
  "udpPort": 443,
  "tcpPort": 443,
  "openVpnPort": 1194,
  "healthPort": 1195,
  "managementPort": 7505,
  "overlayCidr": "10.88.0.0/24",
  "enableUdp": true,
  "enableTcp": true,
  "hysteriaCertificate": "/etc/ssl/eqlink/fullchain.pem",
  "hysteriaPrivateKey": "/etc/ssl/eqlink/privkey.pem",
  "hysteriaServerName": "vpn.example.net",
  "realityTarget": "www.cloudflare.com:443",
  "realityServerName": "www.cloudflare.com",
  "realityPrivateKey": "REPLACE_WITH_XRAY_PRIVATE_KEY",
  "realityPublicKey": "REPLACE_WITH_XRAY_PUBLIC_KEY",
  "realityShortId": "0123456789abcdef",
  "xhttpPath": "/eqlink"
}
```

在专用 Linux VM 解压相应架构的包并安装：

```bash
sudo bash install-server.sh /root/server-settings.json
sudo systemctl status eqlink-openvpn eqlink-health eqlink-hysteria eqlink-xray
sudo ss -lntup
```

安装前检查网段、路由、端口和证书；初始化 CA、设备吊销列表、OpenVPN `tls-crypt-v2` 密钥和 systemd 服务。每周的 systemd timer 更新 30 天有效的吊销列表。配置及设备状态保存在仅 root 可读的 `/etc/eqlink`。重新运行安装脚本时，现有部署使用 `/etc/eqlink/state.json` 中的设置，不从新的 JSON 静默修改网络参数。

**防火墙是部署必做步骤。** 安装脚本无法猜测 SSH 管理来源和端口。用包内 `firewall-preview.py` 生成专用服务器的 nftables 输入规则，审核管理入口与远程恢复方式，再按主机现有防火墙的持久化机制应用。云安全组同样只应放行启用的外层 UDP/TCP 端口及管理入口。OpenVPN、健康检查和管理端口必须只监听 `127.0.0.1`，不可映射公网。示例：

```bash
python3 firewall-preview.py /etc/eqlink/state.json --admin-cidr 203.0.113.4/32 --admin-tcp-port 22 > /root/eqlink-input.nft
sudo nft -c -f /root/eqlink-input.nft
# 审核后再应用，并配置开机持久化。
```

该规则会丢弃其他入站流量，不适合与其他公共服务共存的主机。客户端互访由 OpenVPN 服务端进程完成，不要求启用 Linux IP 转发。

### 设备管理

每台设备分配 `10.88.0.10` 至 `.254` 中未使用的固定 IP，并生成独立 OpenVPN 证书、`tls-crypt-v2` 密钥、Hysteria 凭据和 VLESS UUID。每个导入包应使用单独的强密码，并通过不同渠道交付包和密码。密码文件必须只允许所有者读取和写入（`chmod 600`），否则 `add` 会拒绝执行。

```bash
sudo eqlink-server add --name win-a --bundle /root/win-a.eqlink --passphrase-file /root/win-a.pass
sudo eqlink-server add --name win-b --bundle /root/win-b.eqlink --passphrase-file /root/win-b.pass
sudo eqlink-server list
sudo eqlink-server revoke --id DEVICE_ID
```

吊销会更新 CA 吊销列表、移除设备的固定地址配置及两条外层认证，并通过 OpenVPN 管理接口断开该设备；管理接口失败时会重启 OpenVPN。Hysteria 证书指纹和 REALITY 公钥写入导入包，轮换这两项后须重新向设备发包。请安全备份 `/etc/eqlink`，尤其是 CA 私钥和设备状态；不要将其提交到 Git。

## Windows 客户端

以管理员身份运行 `EqLink-Setup.exe`；首次导入设备时也需要以管理员身份运行 GUI。GUI 中选择 `.eqlink` 导入包、输入密码，选择“自动 / 仅 UDP / 仅 TCP”后点击连接。界面显示虚拟 IP、当前路径和错误。导入内容以 Windows DPAPI 机器范围加密保存，`ProgramData\EqLink` 仅供 SYSTEM 和管理员访问。运行时 OpenVPN/外层配置只写在该受限目录，并在断开或服务启动时清理。OpenVPN 配置只有本机选择器入口，没有直连服务器的备用地址。

默认网段为 `10.88.0.0/24`，服务端 `.1`。本机现有网络若与虚拟网段重叠，客户端会拒绝连接。UDP 恢复时不会强制中断稳定的 TCP 会话，下次重连才优先尝试 UDP。

Windows 客户端可通过 GUI 的“出口设置…”选择[指定出口网卡](docs/windows-physical-egress.md)及专用 IPv4 DNS，与本机已有 TUN 代理并行运行；也可选择“系统路由”。保存需要管理员权限，连接或重连期间禁止修改。选择保存在受限的 `ProgramData\EqLink\egress.json`，GUI 显示出口网卡。域名通过该网卡独立解析，两条外层的加密流量经仅监听 loopback 的定向转发器发送；不修改系统 hosts、DNS 或默认路由。0.2.0 按 GUID 监听网卡/路由变化，事件合并并以 5 秒周期检查补漏；指定出口失效时等待同一网卡恢复。健康会话固定已验证端点，新解析用于下一连接代次。

### 虚拟网段保护与诊断

导入后默认通过 WFP 安装当前私有 IPv4 `/24` 的持久及启动期阻断。连接确认 OpenVPN 实际 TAP 身份、虚拟地址和路由后，服务才创建绑定该接口的动态放行；服务端虚拟 IP 探测通过后才报告 `Connected`。竞争的更具体路由会使客户端保持阻断；不会删除其他软件的路由。服务退出或崩溃后持久阻断仍保留，Job Object 清理自有外层及 OpenVPN 子进程。

后台路径健康检查间隔 5 秒 ±1 秒，超时 3 秒，连续两次失败关闭旧连接；每条候选路径独立尝试最多 4 秒。稳定 TCP 不被恢复的 UDP 打断。OpenVPN 使用静态 TAP 地址初始化，避免保护策略与虚拟 DHCP 启动互相等待。

GUI 提供保护状态、管理员启停及“导出诊断”。兼容的只读 IPC `diagnostics` 仅导出公开端点、版本、网卡、解析、相关路由和状态时间线。服务端提供 `EqLink.Server inspect --json` 和受限权限的 `backup --output /root/state.tar.gz`；安装的宿主机命令可能名为 `eqlink-server`。原始解密配置、认证 UUID、密码和私钥不进入诊断。

断开后的阻断是预期行为。卸载器在移除程序文件前清理 EqLink 自有策略，清理失败则中止。管理员恢复操作见[验收工具说明](packaging/acceptance/README.zh-CN.md)。保护范围是实际目的地址/入站来源属于该 IPv4 网段的系统流量；应用主动交给其他显式代理的请求不在地址过滤覆盖范围。

## 验证

本地测试覆盖导入包加密、配置约束、选择器、真实 Hysteria/Xray 外层通道、出口拒绝，以及 UDP 外层退出后的 TCP 回退：

```powershell
dotnet run --project src/EqLink.Tests/EqLink.Tests.csproj -- artifacts/package/stage/bin/xray.exe artifacts/package/stage/bin/hysteria.exe --real
```

`--real` 使用可从测试机器访问的 `www.cloudflare.com:443` 作为 REALITY 目标；网络条件变化时需替换为适合的 TLS 1.3 目标。完整验收仍需一台 Linux VM 和至少两台 Windows VM，按[验收清单](docs/acceptance.md)和[参数化工具说明](packaging/acceptance/README.zh-CN.md)检查固定 IP、双向 TCP/UDP、60 秒硬门槛、吊销、抓包、安装和卸载。恢复时间以实际虚拟通信计，30 秒是优化目标。已有 0.1.x 报告不能作为 0.2.0 的验收结果。
