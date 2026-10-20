# 首次登记失败：外层认证与证书

0.3.1 起优先使用通用包的 `network.hysteriaCaPem`：导入时校验、DPAPI 保存，连接时提供进程专用 CA 文件。参见 [0.3.1 操作](release-0.3.1.md)。下面的系统根安装流程用于旧客户端/旧包，0.3.1 含 CA 包无需此步骤。

`imported=true`、`registration=Pending`、空虚拟 IP 表示本机还没有完成独立身份与地址登记。入网包通过格式检查不能证明它对应的服务端授权有效。

0.3.0 已发行客户端把外层输出中的 `x509:` / `certificate verify failed` 和账号拒绝合并成 `OUTER_AUTH_REJECTED`。因此该码不能证明登记接口返回了 HTTP 403。工作区后续修复按 CA 信任、证书时间、名称、固定指纹和账号拒绝分类，诊断追加 `outerFailures.udp/tcp`。其中只有固定码和操作提示，不保存原始输出；既有 0.3.0 安装包不包含此修复。

## 排查次序

1. 管理端运行 `EqLink.Server inspect --json` 和 `enrollment-list`，检查实际版本、v2 状态、有效授权、HY2 HTTP 认证、Xray 运行时用户和证书。服务端证书有效不能证明 Windows 已信任签发 CA。
2. 在失败的 Windows 上检查 `Cert:\LocalMachine\Root` 和 `Cert:\CurrentUser\Root`。后台服务需要其运行身份可用的信任库；当前产品使用 LocalSystem。
3. 将公有根证书的 SHA-256 与管理员的既有可信记录核对，使用下面的工具进行只读检查或安装。证书固定指纹与正常 TLS 校验同时生效；保留 `insecure=false`。
4. 在 GUI 断开状态设置实际物理出口和可用的专用 DNS。`egress.settings=null` 表示使用系统路由，不能证明流量已绕开 Meta。
5. 重连，记录实际错误、设备登记和服务器确认的地址。若需要保留原 `.11`，在断开状态静态申请该地址；旧 TAP 上存在 `.11` 不代表本次已经登记成功。

```powershell
# CertificatePath 和 ExpectedSha256 从经过核对的管理员渠道取得。
.\Install-HysteriaRoot.ps1 -CertificatePath .\root.cer -ExpectedSha256 VERIFIED_SHA256 -CheckOnly
# 管理员 PowerShell；仅向 LocalMachine Root 安装指纹完全匹配的公有 CA。
.\Install-HysteriaRoot.ps1 -CertificatePath .\root.cer -ExpectedSha256 VERIFIED_SHA256
```

工具验证公有 X.509 类型、SHA-256、CA 属性、自颁发名称和有效期，重复执行不会添加不同根；`-CheckOnly` 不写证书库。它不修改服务端或其他 VPN 配置，不操作 Meta。安装后断开并重新连接 EqLink，以新建外层进程完成复测。

## 2026-10-19 测试环境记录

- 对端 Agent 报告 11:58 的 0.3.0/v2 检查 20 项通过，有效授权 1 个、已登记设备 0 个；这属于对端提供的现场结果，本执行端没有直接读取其原始报告。
- `DESKTOP-TGPU5VE` 当时提供的诊断停在登记阶段；两个 Root 库按 `*EqLink*` 筛选未找到证书。缺少专用 CA 是当时的主要排查方向，不能据此认定后续所有登记失败仍由 CA 引起；最终结论须以导入和重连后的证据确认。
- 本执行端读取了此前提供的公有根证书：`CN=EqLink Test Dedicated Root CA, O=ARMHPC SSPU`；证书 DER SHA-256 为 `C5C86F02DF73A82FE5FD091930D40F7DB1D43971F9CD1D60BF64D54669818085`，有效期为 2026-10-15 至 2036-10-12。该指纹只适用于这张既有测试根，不作为其他部署的默认信任锚。
- 根证书导入后的设备登记、两客户端互访和回退仍须在实际机器上验证。

## 管理扩展导致 CrashLoopBackOff：未分配设备的空 virtualIp

2026-10-19 的后续现场诊断中，本机 0.3.1 已加载通用包内的 HY2 CA，但仍为 `Enrolling / Pending`，记录 `CONTROL_UNREACHABLE`。对端管理 Agent 报告服务端 Pod 反复退出；上一实例的异常位于外部挂载的 `eqlink_management_common.py`：

```text
address = str(ipaddress.ip_address(item['virtualIp']))
ValueError: '' does not appear to be an IPv4 or IPv6 address
```

该日志已确认直接退出点，但仍应核对相关记录的 `active`、`addressAssigned` 和 `virtualIp`，不发送原始状态或凭据。对于 `addressAssigned=false` 的空地址，核心服务端有意保留这个登记中间状态；外部扩展应在解析地址前跳过它。参见 [设备记录契约](protocol-v2.md#外部管理扩展的设备记录契约)。

排查和修复顺序：

1. 先检查 Pod 状态、`logs --previous`、`lastState.terminated` 和事件。容器退出时 `inspect` 的 exec 不可用，不应继续把客户端的网络超时当作账号或证书拒绝。
2. 从实际挂载的 ConfigMap 取得管理扩展源码，保留现有启用状态、地址、网段及网关权限校验。只读确认未分配记录的字段；不手动为其填 IP，不删除该身份，也不重建入网授权。
3. 准备并验证针对实际源码的补丁：未分配和已吊销设备不进入来源 IP 授权列表，正常已分配及合法旧记录继续工作，非法已分配记录保持原严格校验。确认地址后续分配时会刷新授权结果。
4. 管理端应用前私密备份实际 ConfigMap、Deployment 和持久状态，记录资源版本，使用独立管理入口。实际修改和重载由具备对端权限的管理端执行；本地源码审查、候选补丁或合成样本不代表已修复真实集群。
5. 恢复后核对稳定 Ready、重启次数不再增长、`inspect`、两台独立登记与地址，然后继续双向 ICMP/TCP/UDP、Rancher 网关和自动回退验证。Meta 保持运行，保留失败样本及临时规则清理记录。

修复管理扩展对状态的读取不要求更换现有 CA、REALITY 密钥、网段、VIP 或通用包。仅手动填空地址或删除未完成身份，无法解决下一台设备登记时会再次出现同类状态的问题。

15:19:59 对端返回的实际网关源码含有每 2 秒执行一次的 heartbeat：重新读取状态并调用 `sync_client_files`；`permitted` 逐次重读状态。因此本环境的最小修复可限定在 common 的设备解析中，无需另加刷新进程。此处为源码审查结论；真实地址分配后的 CCD 更新、身份授权和客户端互访仍须在集群验证。新身份不会因沿用旧虚拟 IP 自动继承 Rancher 的设备 ID 授权。
