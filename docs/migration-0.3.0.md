# 0.3.0 协调迁移与整组回滚

迁移必须由对端管理端执行，使用不依赖 EqLink 的 SSH、Rancher 管理入口或节点控制台。当前 Rancher 只读账号不能代替管理权限。迁移会停机，两台客户端先断开，Meta 保持运行。

## 保存基线

保留原安装包、原镜像和哈希、部署/Service/PVC/Secret/ConfigMap 清单、服务端持久状态、两端 DPAPI 状态和历史失败证据。私密备份只保存在管理员目录，禁止纳入 Git、公共报告或聊天。旧设备包不转换，不再导入新 GUI。

当前环境应保持：`eqlink-test/eqlink-server`、固定 `armhpc-node10`、VIP `192.168.100.248`、NodePort TCP30443/UDP30444、`externalTrafficPolicy: Local`、`172.21.10.0/24`、已有 CA/REALITY 密钥和 Rancher `.2` 网关扩展。实际值迁移前再次核对。

## k3s 管理端流程

在选定节点校验离线包 `SHA256SUMS` 后运行 `import-image.sh`，用 `k3s crictl inspecti` 记录实际 `sha256:` 镜像配置 ID。集群须已有可分配 `armhpc.home.arpa/tun` 资源和管理员 RBAC。

```bash
# 解压的 0.3.0 离线包目录；默认只生成计划和服务端 dry-run，不停服务。
python3 migrate-v2.py --node armhpc-node10 --namespace eqlink-test \
  --tun-resource armhpc.home.arpa/tun --output /root/eqlink-030-private

# 已复核差异、离线镜像及独立管理入口后，管理员主动执行：
python3 migrate-v2.py --node armhpc-node10 --namespace eqlink-test \
  --tun-resource armhpc.home.arpa/tun --output /root/eqlink-030-private \
  --expected-image-id sha256:REPLACE_WITH_VERIFIED_IMAGE_ID \
  --independent-management-verified --apply
```

工具保存清单，将单副本缩到 0 并等待旧 Pod 完全结束；使用独立维护 Pod 挂载原 PVC，离线导出一致状态，再执行 `EqLink.Server migrate-v2 --backup ...`。迁移保留 CA、服务器证书、tls-crypt-v2 服务端密钥、网络和 REALITY 密钥，吊销旧设备并创建 v2 网络标识。完成后仅更新原模板的管理字段和镜像，保留启动包装、探针、挂载和网关扩展。维护 Pod 不带服务选择标签，不提供公网入口。

任何状态步骤失败时保持停止，并保留私密备份；不要盲目反复执行 `--apply`。执行中机器掉电也须从独立管理入口核对副本数、维护 Pod、journal 和私密备份。该工具已提供可执行流程，真实集群迁移仍须维护窗口验证。

普通 `upgrade.py` 只允许 v2 状态的常规镜像升级；遇到旧状态会停止并指向迁移工具。

## 生成一份包，登记两台机器

```bash
umask 077
python3 devices.py enrollment-create --namespace eqlink-test \
  --name eqlink-test-network --output /root/eqlink-test-network.eqlink
python3 devices.py enrollment-list --namespace eqlink-test
python3 devices.py list --namespace eqlink-test
```

同一文件复制到 A、B；核对 SHA-256 相同。先安装 0.3.0 Burn EXE，按原方式保留/安装 Hysteria 专用私有 CA 信任。两台分别选择该文件直接导入，配置各自物理出口和专用 DNS，在断开时选择静态申请 `.10` / `.11`，然后连接。核对独立设备 ID、证书、外层凭据、实际 IP，重复导入不应新增身份。不要复制机器 DPAPI 身份。

按验收工具重跑两台双向 ICMP/TCP/UDP、五次有效故障回退、WFP 边界、Meta 物理网卡抓包、单设备吊销及清理。单次 ICMP 不断网的修复不代表底层 ICMP 丢失位置已定位；保留客户端 TAP 与服务端 TUN 同步抓包。

## 回滚

回滚必须同时恢复服务端状态和旧镜像，并恢复两台旧客户端与其原 DPAPI 状态。只回滚镜像会遇到状态格式不兼容；只恢复旧状态也不能让新客户端继续入网。

```bash
# 指向本次成功导出 state.tar.gz/state.sha256/deployment.json/pod.json 的私密目录。
# 此时维护镜像仍使用已验证的 0.3.0 镜像来离线恢复数据。
python3 migrate-v2.py --node armhpc-node10 --namespace eqlink-test \
  --rollback-from /root/eqlink-030-private/migration-TIMESTAMP-SUFFIX \
  --expected-image-id sha256:VERIFIED_030_MAINTENANCE_IMAGE_ID \
  --independent-management-verified --apply
```

工具验证备份哈希、命名空间和调度策略，再停止数据服务并从私密归档恢复整个状态根。恢复原模板后，管理员核对旧镜像 ID、原探针、网关和虚拟互访。迁移失败留在 0 副本时也支持此恢复路径。恢复工具和备份不包含在公共发行包内。

## 非 k3s 宿主机

停用本项目的旧 systemd 单元，保留其他 OpenVPN 服务。解压 0.3.0 包，先运行新二进制 `EqLink.Server migrate-v2 --backup /root/pre-030.tar.gz --root /etc/eqlink`，再运行 `install-server.sh`。新安装器运行单一 `eqlink.service`，不会自动迁移旧状态。现有 CA/网络不应重新初始化。
