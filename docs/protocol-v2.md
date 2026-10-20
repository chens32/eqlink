# 通用入网包、身份与地址协议 v2

## 数据分离

`.eqlink` 是无密码、`schemaVersion: 2` 的 JSON，最大 128000 字节。`network` 包含网络标识、公网域名/端口、传输参数、OpenVPN/控制接口 CA 和 HY2 证书指纹；顶层包含入网授权 ID、入网 token、授权使用的 HY2 凭据和 VLESS UUID。包中没有机器私钥、机器证书、设备 IP。

Windows 0.3.1 增加可选 `network.hysteriaCaPem`，承载 HY2 CA 公有证书。导入时校验，DPAPI 保存；仅以运行时 `tls.ca` 文件提供给 HY2。正常链验证、SNI 和叶证书固定指纹同时保留；不写系统全局 Root。兼容缺少该字段的旧 v2 包和 0.3.0 服务端响应，已导入的专用 CA 不被旧响应或旧包重新导入清空。同网络已有专用 CA 被替换时拒绝静默更新。

本机 `identity.dpapi` 保存通用包、本机 RSA 3072 私钥/CSR/公钥指纹、登记响应和已确认运行配置。文件位于 `%ProgramData%\EqLink`，机器 DPAPI 加密，仅 SYSTEM/管理员可读写。`address.json` 只保存自动或静态申请偏好。旧 `profile.dpapi` 保持原样，不转为通用凭据。

首次导入离线生成密钥；首次连接在线登记。重复导入同一网络保留身份；同一网络 CA 变化会被拒绝。丢失本机 DPAPI 身份后重新导入会登记新设备，管理员应吊销旧身份。DPAPI 文件不能跨机器克隆为新设备。

## 控制接口

控制 TLS 证书由 OpenVPN CA 签发，SAN 为 `server`。客户端使用自定义根信任和名称校验，关闭系统 HTTP 代理。外层 CONNECT 目标固定为 `127.0.0.1:healthPort`，没有直连公网控制接口。

| 接口 | 认证 | 行为 |
|---|---|---|
| `POST /v2/enroll` | Bearer 通用入网 token | 校验 CSR 签名、RSA 3072～4096；按 SPKI SHA-256 幂等签发设备凭据 |
| `GET /v2/network` | 独立设备双向 TLS | 返回公共网络配置和版本 |
| `POST /v2/address` | 独立设备双向 TLS | 提交 `{mode:"Auto"}` 或 `{mode:"Static",requestedIp:"172.21.10.10"}` |
| `GET /v2/health?nonce=32位十六进制` | 独立设备双向 TLS | 返回 `{ok:true,nonce:原值}` |

服务端虚拟 IP 上的相同 TLS 端口仅映射健康接口；登记、地址、网络配置接口在那里不存在。认证后端 HTTP、HY2 stats/kick、Xray gRPC API、OpenVPN management 只监听回环。

CSR 的请求主题和扩展不复制到证书；服务器指定 `CN=device-{id}` 和 clientAuth 扩展，使用现有 OpenSSL CA 数据库签发，使 CRL 吊销与 OpenVPN 一致。私钥始终在客户端生成。

HY2 HTTP 认证每次读取原子持久状态。Xray `api adu/rmu` 只更新目标用户，私密请求文件权限 0600，用后删除。固定版本命令可能在操作失败时仍退出 0，必须核对成功操作数量；原始输出不进入日志。持久配置包含完整授权，外层进程重启时恢复。

## 外部管理扩展的设备记录契约

设备身份登记与地址分配是两次独立事务。`POST /v2/enroll` 可以持久保存 `active=true`、`addressAssigned=false`、`virtualIp=""` 的设备，随后客户端才通过独立证书调用 `POST /v2/address`。断网、客户端退出或响应丢失都可能使该中间状态长期存在；它不是需要手动填 IP 或删除的损坏记录。

读取持久状态的 Rancher 网关、DNS、访问控制和启动包装等外部扩展须遵守以下规则：

- 将身份有效性与地址是否分配分开处理。未分配地址的设备可以存在于身份列表，但不能加入按来源 IP 授权的网关列表。
- 解析或使用 `virtualIp` 前，跳过 `addressAssigned=false` 的设备；已吊销身份继续按原策略排除。旧记录缺少 `addressAssigned` 时，遵循产品默认值 `true`，仍检查有效地址。
- 对已分配记录继续执行现有 IPv4、网段、保留地址、重复地址及访问控制校验。不能通过捕获所有异常、默认授予整个网段或把空地址替换成固定地址来绕过校验。
- 地址分配或吊销完成后，使用新的原子状态更新来源 IP 授权结果。启动时跳过一条记录不代表可以永久缓存空列表；具体刷新方式由扩展维护者实现和验证。
- 不修改私钥、证书、授权或设备身份来适配网关。状态和 CCD 由 EqLink 的地址事务维护，扩展只读取必要字段。

`ServerFiles.Render` 已仅为 `active && addressAssigned` 的设备生成 CCD。迁移时除保留外部扩展的启动包装、ConfigMap 和探针外，还须用包含未分配设备的状态验证其兼容性。外部扩展源码及真实 k3s 验证不包含在核心服务端的本地通过结论中。

## 地址事务

只支持私有 IPv4 `/24`；`.1～.9` 保留，设备池 `.10～.254`。管理员可通过 `reservedAddresses` 额外保留设备池地址。

自动地址一经分配持久保留；重复申请返回相同值。静态申请不自动降级。活动设备及管理员保留地址占用池资源。所有写入使用跨进程状态锁；可恢复 journal 将 CCD/运行配置和状态文件的多个原子重命名串起来。启动和每次写入前恢复未完成提交。现有地址变更会临时撤下 CCD，踢出并再次确认旧会话已结束，失败则恢复原 CCD/地址。

常见拒绝代码：`ADDRESS_IN_USE`、`ADDRESS_RESERVED`、`ADDRESS_OUTSIDE_NETWORK`、`ADDRESS_POOL_EXHAUSTED`、`ADDRESS_SESSION_ACTIVE`。拒绝后保持断开，由用户修改设置或重试。

## 连通状态与统计

接口/地址/路由/WFP/出口/认证是结构性条件；异常立即撤销放行。内层 TLS 健康检查每 5±1 秒，3 秒期限，连续两次失败才重连。单次 ICMP 超时只更新诊断提示；3 秒 ICMP 任务不阻塞网络协调流程。

TCP 表使用 GetExtendedTcpTable，限定本地虚拟 IP、远端虚拟 /24、ESTABLISHED，排除后台与所拥有连接组件 PID。网卡使用 GUID→LUID→GetIfEntry2；单调时钟、约 3 秒窗口，GUID/LUID/索引/地址或计数器变化重置基线。

IPC 保留已有状态字段，追加 `metrics`、`registration`、`address`、`server`、`healthWarning`；增加 `address-get`、`address-set`，以及只读 `health-probe`（UDP/TCP，用于有效故障注入）。配置写入必须管理员，普通交互用户可连接/断开及读取诊断。无任何 IPC 返回私钥或外层凭据。

## 客户端独立域名覆写（0.3.3）

`hosts-get` 只读返回当前网络的 `{enabled,entries:[{hostname,address}],revision}`；管理员 `hosts-set` 提交同结构，版本不等于当前值时拒绝覆盖。条目限制为当前私有 IPv4 /24 内的主机地址，每个完整域名一条，最多 128 条。保存不重连 VPN。

`status.hosts` 追加 `{enabled,applied,entryCount,error,existingMatches}`，诊断仅使用该状态，不包含映射正文。持久配置位于 `%ProgramData%\EqLink\hosts\<networkIdN>.json`，不进入 `.eqlink` 包。Windows 系统解析通过带固定所有者标记的受管 hosts 区块生效；不创建 NRPT 或本地 DNS 监听。详见 [域名覆写](hosts-overrides.md)。
