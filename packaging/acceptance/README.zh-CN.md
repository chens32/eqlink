# EqLink 0.3.0 验收工具

`result.json` 的 `checks[]` 使用 `passed / failed / skipped / unavailable`。`unavailable` 表示条件、工具或有效故障注入不足，不计为通过。恢复时间从有效注入开始，到正确路径、有效 WFP 放行及随机负载 TCP/UDP 互访成功为止（用 -PeerIp 指定运行 echo 工具的另一台）。未指定 PeerIp 时仅验证内层认证健康，不代表双客户端恢复通过。ICMP 单独记录。默认每种关键恢复场景连续 5 次；30 秒是优化目标，60 秒是硬门槛。

## 本机基本测试和故障测试

先用管理员身份导入设备、启用网段保护、选择物理网卡及可信的显式 IPv4 DNS。服务端需要同时启用两条路径。测试会重建 EqLink 连接，业务会话可能断开；Meta 和全局防火墙配置不被修改。使用 Windows PowerShell 5.1 或 PowerShell 7，在管理员终端运行：

```powershell
.\Test-EqLink.ps1 -Scenario Baseline -OutputDirectory C:\EqLinkEvidence\baseline
.\Test-EqLink.ps1 -Scenario Recovery -Repeat 5 -InjectFaults -Capture `
  -OutputDirectory C:\EqLinkEvidence\recovery
.\Test-EqLink.ps1 -Scenario StableTcp -Repeat 5 -InjectFaults `
  -OutputDirectory C:\EqLinkEvidence\stable-tcp

# 先查看网卡索引，确认所选是 Meta。只添加一条临时的 server-IP /32 竞争路由。
Get-NetAdapter | Format-Table Name,ifIndex,InterfaceDescription
.\Test-EqLink.ps1 -Scenario RouteConflict -ConflictInterfaceIndex 5 -InjectFaults -Capture `
  -OutputDirectory C:\EqLinkEvidence\route
.\Test-EqLink.ps1 -Scenario ServiceCrash -InjectFaults -Capture `
  -OutputDirectory C:\EqLinkEvidence\crash
```

`-Scenario All` 合并上述测试。可用 `-ServerIp`、`-HealthPort`、`-OpenVpnPort` 指定非默认参数。进程挂起只针对 EqLink 服务的、路径匹配的直属 HY2/Xray 子进程，挂起前后认证健康检查分别须成功和失败。挂起的进程在 `finally` 中恢复；服务重建已经清理该进程时不会操作复用后的 PID。进程退出场景须确认原进程确实退出。单路径模式不允许换到另一协议，双路径无响应时应保持阻断。

测试脚本会恢复开始时的连接模式和连接/断开状态。脚本执行期间不要同时操作 GUI、另跑故障脚本或手工修改网卡。`Ctrl+C` 后必须核对 cleanup 和服务状态；主机突然断电等无法执行 finally 的情况，应重启 EqLink 服务清理其子进程。

抓包使用独有过滤器，若存在 PktMon ETW 会话则拒绝启动。先核对 `capture-components.txt`，**组件 ID 与 Windows ifIndex 不同**，分别指定所有物理网卡和 Meta：

```powershell
python .\analyze-capture.py C:\EqLinkEvidence\recovery\nics.txt `
  --public-ip SERVER_PUBLIC_IPV4 --overlay 172.21.10.0/24 `
  --physical-components 17,22,282 --meta-components 1 `
  --output C:\EqLinkEvidence\recovery\capture-analysis.json
```

组件 ID 是示例，必须从本次清单确定。分析要求观察到物理外层包、解析到丢失计数且为零；没有包或无法解析不能给出零泄露结论。动态公网地址变化时应针对每个实际端点分别检查，并保留原始 ETL/pcapng。抓包只支持窗口内的地址/端口边界结论，不证明外层无法识别或封锁。

## 第二台 Windows 客户端：固定地址和双向 TCP/UDP

A、B 使用完全相同的 v2 通用 `.eqlink` 包，记录各自登记后的独立设备身份与服务器确认的虚拟 IP。不要复制机器 DPAPI 身份或私钥。在两台设备上各启动只绑定自身虚拟地址的限时 echo 服务，辅助脚本会建立范围限于该 TAP、虚拟 IP 和虚拟网段的临时入站防火墙规则：

```powershell
# A（另开终端保留服务运行）
.\Start-PeerProbe.ps1 -VirtualIp 172.21.10.10 -OverlayCidr 172.21.10.0/24 `
  -Executable .\win-x64\EqLink.PeerProbe.exe -Seconds 300
# B
.\Start-PeerProbe.ps1 -VirtualIp 172.21.10.11 -OverlayCidr 172.21.10.0/24 `
  -Executable .\win-x64\EqLink.PeerProbe.exe -Seconds 300

# A -> B
.\win-x64\EqLink.PeerProbe.exe check --bind 172.21.10.10 --peer 172.21.10.11 --repeat 5 --output A-to-B.json
# B -> A
.\win-x64\EqLink.PeerProbe.exe check --bind 172.21.10.11 --peer 172.21.10.10 --repeat 5 --output B-to-A.json
```

每次检查包含 ICMP、随机负载 TCP、随机负载 UDP。TCP 是固定 64 字节帧；UDP 验证完整 echo 内容和应答来源。关闭/超时后只清理脚本自有入站规则。Linux 同版本二进制可在服务器虚拟 IP 绑定运行，不能用服务器 `.2` 管理网关代替第二台 Windows 客户端。

测试重连和重启后地址不变，再由管理员吊销 A：A 旧会话应断开且无法重新登录，B 仍能访问服务端；核对 B 未受影响且服务进程未重启。两台间主动连接需双方都运行 echo 服务和入站允许规则。未具备第二台 Windows 时，以上结果保持 `skipped`。

Linux 解压 ZIP 后先恢复执行权限，例如 `chmod +x linux-arm64/EqLink.PeerProbe`，再在服务器执行 `./linux-arm64/EqLink.PeerProbe serve --bind 172.21.10.1 --seconds 300`。绑定地址必须已配置在该机器上；不需要创建公网 Service 或 NAT 规则。

## 隔离环境/维护窗口补充门槛

以下项目不在正常使用的本机自动执行；逐项执行并附同格式结果及证据，不能用本机编译或 Ready 状态替代：

1. **WFP 启动与生命周期**：在可丢弃 Windows VM 安装 0.1.2 并放入独立 OpenVPN 哨兵配置，记录 SHA-256；升级 0.3.0、断开、服务停止/强制退出、重启、卸载，各阶段抓包虚拟网段。重启至服务启动前应仍阻断；正常卸载后 EqLink Provider 消失，其他 VPN 文件哈希不变。故意使清理返回失败，验证卸载中止且恢复程序仍在。
2. **防火墙/BFE**：在隔离 VM 的当前网络配置文件启用、关闭时重复上述 WFP 阻断测试；BFE 不可用时 connect 应失败并显示 `PROTECTION_UNAVAILABLE`。恢复 BFE 后验证重新建立保护。不得通过放宽全局防火墙来判为通过。
3. **NIC**：在有 VM 控制台的环境，对选中 NIC 断开/恢复、DHCP 换 IPv4、删除重建网卡使索引改变而 GUID 保持/改变。原 GUID 恢复应自动重连；GUID 被替换应保持等待，不应自动选择其他接口。抓包核对旧源地址套接字退出。
4. **DNS**：用测试 DNS 控制首个地址、TTL、超时和 A 记录切换。健康会话不迁移；下一次故障重连使用新的有效地址；解析失败不回到系统 Fake-IP。不要用真实服务地址指向不受控主机。
5. **k3s**：在隔离集群实际验证 TUN 插件缺失/资源用尽预检、设备注入和 seccomp/capability 组合；分别使一条外层退出，另一条仍须 Ready 并能通信。执行安全备份、升级、单设备吊销和持久状态恢复。真实集群升级由已有管理端执行，再提交脱敏 `EqLink.Server inspect --json`。

## 恢复命令

虚拟网段保护预期在断开、服务停止和崩溃后继续阻断。如果管理员决定移除 EqLink 自有 WFP 策略，先停止服务，在管理员终端执行：

```powershell
Stop-Service EqLink
& 'C:\Program Files\EqLink\EqLink.ClientService.exe' --network-protection-reset
# reset 清理自有规则并将保护设置为关闭；修复后启动服务，通过 GUI 重新启用保护。
Start-Service EqLink
```

`--network-protection-remove` 仅移除规则，不修改启用设置，供卸载器使用；服务运行时恢复命令拒绝并行清理。返回 5 表示权限不足，1618 表示服务进程仍在，1603 表示原生清理失败。不要删除其他 Provider、停 BFE 或清空整个系统防火墙。应用主动交给其他显式代理转发的请求不在本地址过滤机制覆盖范围。


## 0.3.0 GUI 统计和定向流量

先在 B 启动范围受限的流量服务（临时规则和进程清理写入 session.json）：

```powershell
.\Start-PeerProbe.ps1 -Mode Traffic -VirtualIp 172.21.10.11 -OverlayCidr 172.21.10.0/24 `
  -Executable .\win-x64\EqLink.PeerProbe.exe -Port 36556 -Seconds 300
```

A 使用两个终端分别记录统计和产生流量；Meta 保持运行：

```powershell
.\Record-Statistics.ps1 -Seconds 120 -Output A-statistics.json
.\win-x64\EqLink.PeerProbe.exe hold --bind 172.21.10.10 --peer 172.21.10.11 --port 36556 --connections 3 --seconds 24
.\win-x64\EqLink.PeerProbe.exe transfer --bind 172.21.10.10 --peer 172.21.10.11 --port 36556 --direction upload --bytes 134217728 --seconds 180 --output upload.json
.\win-x64\EqLink.PeerProbe.exe transfer --bind 172.21.10.10 --peer 172.21.10.11 --port 36556 --direction download --bytes 134217728 --seconds 180 --output download.json
```

hold 按三段时间产生 3→2→0；关闭其他应用连接后比对 GUI 和统计快照。transfer 使用 SHA-256 核对完整负载，分别检查 A 上行/B 下行和 A 下行/B 上行。速率窗口、报文开销、首尾采样会带来差异，应对同一稳定窗口用字节差核对，不能把有效载荷平均值视作网卡精确值。切换适配器/地址后首个速率样本应为“—”，断开应为零。

两台同时运行普通 Echo 模式时，故障验收加 `-PeerIp 172.21.10.11 -PeerPort 36555`，关键场景各五次。有效故障注入先核对认证健康成功，再确认其失败；失败注入不计为通过。单次和连续 ICMP 丢失均应保持正常业务，真实认证内层连续故障则应重连。实际 TAP/TUN 抓包丢包位置仍需另行分析。
