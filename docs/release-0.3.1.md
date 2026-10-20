# EqLink Windows 客户端 0.3.1 测试版

本次交付 Windows 客户端更新和兼容现有 0.3.0/v2 服务端的通用包管理工具。服务端镜像、CA、REALITY 密钥、设备/入网授权、虚拟地址和 Rancher 网关不需要为此修改。

## 使用变化

- 通用 v2 `.eqlink` 的 `network.hysteriaCaPem` 可携带 HY2 CA 公有证书；`caPem` 仍用于原有内层/控制接口信任。
- 导入时校验 CA 类型、有效期、签名用途和 PEM 边界，随身份配置用 DPAPI 保存。连接时派生出 EqLink 受限目录中的 CA 文件，通过 HY2 的 `tls.ca` 使用；不向 Windows 全局 Root 库自动安装证书。
- 保留正常 TLS 校验、SNI 和原有服务端证书 SHA-256 固定。信任限定于 EqLink 的 HY2 进程。
- 登记、地址获取、重连及路径切换保留该 CA。0.3.0 服务端响应没有此字段时，客户端保留本机已导入的 CA。
- 同一网络补入 CA 不重置本机身份；旧 v2 包再次导入不能清空已有专用信任。替换既有同网络 CA 需要明确的信任迁移，本版拒绝静默替换。
- 外层错误细分为 CA 不受信任、证书时间无效、名称不匹配、固定指纹不匹配和认证拒绝，诊断仅记录固定码和操作提示。

旧 v2 包继续兼容原有系统信任方式。要使用本次自动处理能力，需要先安装 0.3.1，再导入含 CA 的包；旧 0.3.0 客户端会忽略新增字段。

## 交付文件

- `EqLink-Setup-0.3.1.exe`：单文件 Burn 安装器，包含 EqLink MSI 和固定、经过签名/哈希检查的官方 OpenVPN MSI。
- `EqLink-0.3.1.msi`：独立客户端 MSI，适用于已有受支持 OpenVPN 的机器。
- `EqLink-BundleTools-0.3.1.zip`：管理端 Python 工具和说明，可给现有通用包补 CA，也可在创建入网授权时包含 CA。
- 源码快照、验收状态、构建来源和 `SHA256SUMS`。

项目安装器尚未做项目代码签名。测试版的完整 Windows 安装/升级/卸载/重启验收仍是发布门槛。CI 保持停用。

## 当前 eqlink-test 管理端：沿用已有授权补 CA

解压管理工具到现有 Linux 管理主机。需要 Python 3、OpenSSL，以及可对 `eqlink-test` 执行 Pod exec 的管理员 kubeconfig。

工具包中的 `eqlink-hy2-root-ca.cer` 是本次既有测试环境的公有根证书，其证书 SHA-256 是：

`C5C86F02DF73A82FE5FD091930D40F7DB1D43971F9CD1D60BF64D54669818085`

它只用于该既有测试部署。其他部署应提供自己的 CA 与经过核对的指纹。安装器本身不预设此测试根。

```bash
umask 077
cd EqLink-BundleTools-0.3.1
install -d -m 700 /root/eqlink-031-private

# 使用已配置的管理员 kubeconfig。k3s 存在时工具默认使用 k3s kubectl。
# 沿用 2026-10-19 对端报告的有效授权，不新建授权/设备，不重启服务。
python3 devices.py bundle-add-ca \
  --namespace eqlink-test \
  --id 506e652aad074197b8cb2939023a2cc1 \
  --hysteria-ca ./eqlink-hy2-root-ca.cer \
  --expected-ca-sha256 C5C86F02DF73A82FE5FD091930D40F7DB1D43971F9CD1D60BF64D54669818085 \
  --output /root/eqlink-031-private/eqlink-test-network-ca.eqlink

sha256sum /root/eqlink-031-private/eqlink-test-network-ca.eqlink
```

`--id` 从 Pod 的受限导出缓存恢复已有通用包，数据在工具内部捕获并写入 0600 文件，不输出到终端。工具还会核对运行网络、授权有效性、当前服务端证书固定指纹，以及该 CA 对 TLS server name 的证书链验证。

若原导出没有缓存，保留原授权，改用管理员已经保存的通用包文件：

```bash
python3 devices.py bundle-add-ca \
  --namespace eqlink-test \
  --input /实际私密路径/eqlink-test-network.eqlink \
  --hysteria-ca ./eqlink-hy2-root-ca.cer \
  --expected-ca-sha256 C5C86F02DF73A82FE5FD091930D40F7DB1D43971F9CD1D60BF64D54669818085 \
  --output /root/eqlink-031-private/eqlink-test-network-ca.eqlink
```

`--id` 与 `--input` 二选一。输出文件必须尚不存在；原通用包和凭据保持不变。不要把真实 `.eqlink` 正文、Base64、令牌或私钥发送到聊天或 Git。

## 新建入网授权时直接含 CA

只有管理员明确要新建一个授权时才使用本段：

```bash
python3 devices.py enrollment-create \
  --namespace eqlink-test \
  --name another-network-grant \
  --hysteria-ca ./eqlink-hy2-root-ca.cer \
  --expected-ca-sha256 C5C86F02DF73A82FE5FD091930D40F7DB1D43971F9CD1D60BF64D54669818085 \
  --output /root/eqlink-031-private/another-network-ca.eqlink
```

若执行结果未知，保留 `.request.json` 并按原操作重试。Windows 管理主机若使用 Git OpenSSL，可通过 `--openssl` 指定其完整路径。

## Windows 升级与复测

1. GUI 断开并关闭窗口，保存本机原配置备份。
2. 安装 `EqLink-Setup-0.3.1.exe`，检查 GUI/后台服务实际版本均为 0.3.1。
3. A/B 导入完全相同的 `eqlink-test-network-ca.eqlink`，无需密码或独立 CA 安装。
4. 断开状态设置各自物理出口和专用 DNS，Meta 保持运行；需要原地址时分别静态申请 `.10` / `.11`。
5. 连接后检查登记、实际虚拟 IP 和 `hysteriaTrust.source=Bundle`；仅 CA SHA-256 等元数据进入诊断。
6. 验证两台双向 ICMP/TCP/UDP 和自动回退，保留原始失败与清理记录。

本版本未替用户在真实机器上安装或操作集群。原有 0.3.0 安装包和服务端包保留原哈希；完整生命周期、真实两客户端互访、Meta 抓包和故障恢复以新实测为准。
