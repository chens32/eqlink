# EqLink 独立域名覆写

Windows 客户端 0.3.3 起，可以将完整域名映射到当前 EqLink VPN 网段的 IPv4。配置只影响本机，服务端和其他客户端无需更新，也不会改变通用入网包。

## 使用

1. 打开 EqLink 的“域名覆写”页。
2. 勾选“启用当前网络的域名覆写”。
3. 每行填写 `IP 域名`，点击“保存覆写”，按需通过 UAC 管理员授权。
4. 使用 Windows 系统解析的应用即可按域名访问；已有应用的内部 DNS 缓存可能需要刷新或重启应用。

以当前测试网段为例：

```text
# VPN 内的 Rancher 网关
172.21.10.2 rancher.armhpc.home.arpa
# 其他客户端；同一行可以列出多个别名
172.21.10.10 workstation.armhpc.home.arpa files.armhpc.home.arpa
```

地址必须属于导入网络的 /24，不能是 `.0` 或 `.255`。支持域名大小写、尾部点及国际化域名转换；不支持通配符、URL、端口、单标签名称和 `.local` mDNS 名称。重复域名会被拒绝，最多 128 个域名。保存后规范化为每个域名一行，注释不持久保存。

## 存储及生效方式

独立配置保存在 `%ProgramData%\EqLink\hosts\<networkIdN>.json`，由后台服务管理，继承 EqLink 目录的 SYSTEM/管理员权限。不同 EqLink 网络各自保存映射；切换网络会先清理原网络的生效条目。不要通过手动编辑正在使用的文件绕过 GUI 的版本检查。

为让普通 Windows 应用使用映射，服务仅在系统 `%SystemRoot%\System32\drivers\etc\hosts` 中维护以下标记之间的专属区块，并在修改后刷新 Windows DNS 缓存：

```text
# BEGIN EQLINK HOSTS 720e9b36-40af-48e6-91a8-ed63dd39cf61
172.21.10.2 rancher.armhpc.home.arpa
# END EQLINK HOSTS 720e9b36-40af-48e6-91a8-ed63dd39cf61
```

区块外的内容、字节编码、换行及其他软件条目均保留。同名且不同地址的既有条目会导致保存失败，需管理员先处理冲突；同名且同地址的既有条目保留并显示提示，停用 EqLink 也不会删除它们。文件只读、被独占或区块标记损坏时显示错误，不自动解除文件保护或覆盖外部内容。

手动断开 VPN 时映射保留，域名继续指向虚拟 IP，系统流量由现有 WFP 网段保护约束。停用覆写、正常退出服务或卸载会清理自有区块，配置仍保留；下次启动服务时重新应用已启用设置。服务意外终止可能留下受管区块，服务重启或恢复工具可清理它。

## 边界及恢复

- 这是本机静态映射，不是动态设备发现或服务端 DNS。设备地址变化后需更新映射。
- 不修改 Meta 配置；应用自带 DoH、显式代理或独立解析器可能绕过 Windows hosts，需要在对应应用中配置。`nslookup` 等直接查询 DNS 服务器的工具不能作为 hosts 生效判断，使用 `ping 域名` 或系统 `Dns.GetHostAddresses` 检查。
- 映射不改变 HTTPS 证书信任，服务证书仍需匹配域名并受客户端信任。
- 不应将 EqLink 自身公网入口域名覆写到虚拟地址，否则系统路由模式可能无法建立外层连接。
- 使用独立配置是为了集中编辑和按网络切换；其系统应用效果依赖上面的受管 hosts 区块。没有后台常驻 DNS 端口，也不修改全局 DNS 服务器或 NRPT。

异常恢复应先尝试 GUI 停用覆写。需要管理员恢复时，停止 EqLink 服务后运行：

```powershell
Stop-Service EqLink
& 'C:\Program Files\EqLink\EqLink.ClientService.exe' --network-protection-remove
```

该命令同时移除 EqLink 自有 WFP 网段保护及 hosts 区块，保留其他程序的规则。只有确认需要解除网段保护时才使用。若专属区块标记被手动破坏，先备份系统 hosts 并由管理员核对修复标记；工具会拒绝猜测删除范围。正常启动服务会恢复所导入网络的保护和启用的映射。
