# 0.2.0 测试版实现与交付边界

## 已实现

- Windows x64 WFP Provider `7324d715-31c7-4ac6-9618-169738369a44`，私有 IPv4 `/24` 的入站来源、出站目的持久/启动期阻断，按已确认 TAP LUID 和本机虚拟地址动态放行；自身虚拟 IP 的 loopback 访问单独保留。GUI 默认启用、管理员断开后可关闭；策略建立失败拒绝连接。服务退出保留阻断，卸载仅清理自有规则，清理失败停止卸载。
- OpenVPN 使用 `tap-windows6` 和 `ip-win32 netsh`，避免阻断规则影响虚拟 DHCP 初始化。支持 OpenVPN 2.7 的适配器名称日志和旧式 GUID/ARP 日志，从实际启动的 OpenVPN 确认适配器身份。适配器/地址/路由就绪后才放行，虚拟服务端探测通过后报告 Connected。
- Windows Job Object 关闭时清理自有 OpenVPN、HY2、Xray；按 GUID 监测物理网卡和 IPv4、接口索引变化；路由通知、地址事件和 5 秒补漏检查经同一协调流程合并。指定网卡失效时阻断并等待恢复；不自动替换为其他网卡。
- 活动路径认证健康探测每 5 秒 ±1 秒，3 秒超时，连续两次失败关闭旧连接；UDP/TCP 候选各有 4 秒期限。DNS 变化不迁移健康会话，新连接代次重新解析。账号/证书拒绝停止相应路径，无可用授权路径时报告认证错误，等待用户处理。
- 竞争路由使虚拟网段阻断并显示错误；只修复此前观测过的 EqLink 路由，不删除他人路由。三种模式、单 OpenVPN 会话、稳定 TCP 不抢切保持不变。
- 保持现有 IPC 与设备包格式，追加阶段、错误码、保护状态和只读 diagnostics；GUI 导出白名单诊断，不导出密码、认证 UUID、私钥和解密配置。Linux `inspect --json` 区分期望和实际监听、虚拟地址、认证计数/一致性、证书/CRL、会话；`backup` 生成 600 权限备份。
- k3s 默认容器非 privileged、只保留 NET_ADMIN、只读根、禁止提权、RuntimeDefault seccomp；通过 `--tun-resource` 指定节点插件。部署前验证 Ready ARM64 固定节点和可用资源。单副本 Recreate/PVC/高端口 NodePort、单条外层可用即可 Ready 均保留。
- `deploy.py` 拒绝覆盖已有 Deployment；`upgrade.py` 默认生成计划，应用前私密备份、比较 resourceVersion，只更新管理字段并保留外部管理网关扩展，验证镜像 ID/Ready/inspect，失败时有条件恢复原模板。设备、CA、REALITY 密钥和网络参数不迁移。
- 参数化故障、抓包分析、独立设备双向 ICMP/TCP/UDP 工具；所有未执行或不具条件的验收明确 skipped/unavailable。

## 安装器兼容处理

0.1.2 程序的产品版本为 0.1.2，但 PE 文件版本是 1.0.2.0。0.2.0 统一为 0.2.0.0 时，默认 MSI 版本规则可能跳过新组件。安装器在检测到主版本升级后、costing 前设置 EqLink 自身的 `REINSTALLMODE=amus`，并将移除旧产品放入安装事务，支持失败回滚。该策略不作用于独立的 OpenVPN MSI。0.2.0 的 ProductCode 在本测试版内固定，下次主版本升级需要更新。

相关机制见 [Microsoft REINSTALLMODE 文档](https://learn.microsoft.com/en-us/windows/win32/msi/reinstallmode) 和 [WiX MajorUpgrade 调度文档](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/)。TAP 配置方式见 [OpenVPN Windows 选项](https://github.com/OpenVPN/openvpn/blob/master/doc/man-sections/windows-options.rst)。

## 验证证据与状态

实际结果以交付目录 `ACCEPTANCE.json`、`release-manifest.json`、`SHA256SUMS` 及对应证据文件为准。0.1.x 的旧网络测试保留作基线，不计为 0.2.0 通过。独立 MSI 夹具仅验证版本比较与事务，不代替真实产品的全生命周期。

本轮具备一台 Windows 和既有 ARM64 k3s 服务端。多客户端主动互访、单客户端吊销后另一台继续运行、Windows 重启/完整卸载/清理失败回滚、BFE/防火墙矩阵、真实 NIC 换址/索引变化、可控 DNS 换址以及目标集群新镜像升级，均需独立设备或维护环境。真实服务端由对端现有管理员按升级说明执行；当前 Rancher 只读权限不承担升级。

保护对象仅限实际属于导入 IPv4 网段的系统地址流量。应用显式交给其他代理转发的请求、IPv6 全局策略、物理 LAN 路由和全局上网代理不在本轮范围。抓包只证明窗口内的流量边界，不能保证外层不可识别或不可封锁。完成全部发布门槛前，版本始终为测试版。
