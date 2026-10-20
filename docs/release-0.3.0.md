# EqLink 0.3.0 测试版

本版升级 Windows 客户端和 Linux 服务端：通用入网包、自动登记、地址管理、新 WPF 界面、本机 TCP 连接数/虚拟网卡速率，以及认证 TCP 健康检查。

## 安装包

- `EqLink-Setup-0.3.0.exe`：Windows x64 Burn 安装器，包含 EqLink 和固定官方 OpenVPN MSI；已有符合版本要求的 OpenVPN 会复用。
- `EqLink-0.3.0.msi`：独立 EqLink MSI，机器应已安装受支持的 OpenVPN。
- `eqlink-server-{amd64,arm64}-0.3.0.tar.gz`：Debian/Ubuntu systemd 服务端。
- `eqlink-k3s-arm64-test-0.3.0.tar.gz`：完整 ARM64 离线镜像、首次部署、迁移/升级/回滚和授权管理工具。
- `eqlink-tests-0.3.0.zip`：互访、故障、抓包、统计和带校验的流量测试工具。
- 源码归档、`release-manifest.json`、`SHA256SUMS`、`ACCEPTANCE.json`：构建来源、哈希和具体验收范围。

发行包来自 manifest 中标明的 Git 基线与工作区快照。测试版安装器未签署项目代码签名；其中官方 OpenVPN MSI 仍经过固定哈希和签名验证。不得把安装器构建成功理解为完整安装生命周期通过。

## 使用变化

在“设备与诊断”选择 v2 `.eqlink` 文件即导入，无密码框或再次确认步骤。同一文件分给多台机器，本机各自生成独立私钥，首次连接在线登记。重复导入同一网络不重置身份。

连接设置提供自动／仅 UDP／仅 TCP、物理网卡/专用 DNS、服务器自动分配／静态地址申请。静态申请只保存偏好，服务器拒绝时保持断开，不直接修改 TAP。当前私有 `/24` 的 `.1～.9` 保留，设备 `.10～.254`。

总览的 TCP 数是本机虚拟 IP 与网段内对端的 ESTABLISHED 连接；速率是虚拟网卡字节差的约 3 秒平均。上传为本机发送，下载为接收；不含外层加密开销，包含内层报文/探测开销。无样本或故障显示“—”，断开显示零。

单次或连续 ICMP 诊断失败不直接撤销虚拟网放行。结构性错误仍立即阻断；认证内层 TCP 健康连续两次失败才受控重连。每次新 OpenVPN 会话都重新验证，旧探测响应不能把新会话提前标为已连接。

## 新安装 Linux

解压对应架构包，准备 OpenVPN 2.6+、OpenSSL、iproute2、Python 和 systemd。复制 `server-settings.example.json` 到管理员私密目录，填入真实域名、证书与 REALITY 密钥（可用包中 `xray x25519` 生成）；证书路径不能指向私密状态目录中尚不存在的文件。

```bash
umask 077
# 编辑 /root/server-settings.json 后：
sudo bash install-server.sh /root/server-settings.json
sudo systemctl status eqlink.service
sudo /usr/local/bin/eqlink-server inspect --json
sudo /usr/local/bin/eqlink-server enrollment-create --name network --bundle /root/network.eqlink
```

安装器初始化一次 CA，之后保留持久状态；监督程序定期刷新 CRL。主机/云防火墙只开放启用的外层端口及已有管理入口。不可公开 OpenVPN、控制、认证、API 或 stats 端口。生成的专用 nftables 规则须先核对管理来源。

## 旧环境迁移

旧加密包被拒绝，旧本机 DPAPI 配置保留，不静默转换。真实旧 k3s 必须在独立管理入口和停机窗口执行 `migrate-v2.py`，保留 CA、REALITY 密钥、虚拟网段、VIP 和网关扩展；普通 `upgrade.py` 会拒绝旧身份格式。客户端先断开，迁移后两台使用相同新包重新登记，并按需要静态申请原 `.10/.11`。

服务端长期状态和 Secret 的私密备份、原镜像及旧安装包必须同时保留。回滚应恢复原状态、原模板/镜像和两台旧客户端身份；仅切镜像不足。详见源码中的 `docs/migration-0.3.0.md`，k3s 离线包也附具体操作说明。

## 验证边界

以本包 `ACCEPTANCE.json` 为准。已有 Windows 原生/回环、WPF、独立 WFP Provider、ARM64 QEMU 和实际外层/OpenVPN 认证流程的本地验证。**真实 k3s 迁移、0.3.0 两台 Windows 互访/五次故障/Meta 抓包、完整 Windows 安装升级卸载重启和长期稳定性仍需协调执行。**

0.2.0 的失败样本与实测窗口被保留；探测误阻断修复不等于底层 ICMP 丢包原因已解决。第二台环境和真实网络不能由回环、Linux/QEMU 或构建替代。CI 继续保持停用。
