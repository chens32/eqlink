# EqLink 1.0.0 ARM64 k3s 离线包（发布草稿）

> 此处描述可选的内部离线构建产物，不再作为标准 release 附件上传。新部署优先使用对应架构的 Linux 服务端包构建镜像，完整步骤见[当前 Kubernetes/k3s 安装说明](../../docs/kubernetes-installation.zh-CN.md)。标准 release 只有 Windows 一键安装器与 amd64/arm64 服务端三个文件。

包含离线 Docker 镜像、首次部署、v2 常规升级、旧身份迁移、设备/入网授权管理与完整 SHA-256。公网高端口默认 TCP30443、UDP30444，不增加公开登记端口。

## 节点与权限

固定 ARM64 Ready 节点、PVC/StorageClass、可分配的 TUN 设备插件资源（默认 `armhpc.home.arpa/tun`）、管理员 kubectl 权限。节点导入镜像及宿主机 TUN/设备插件维护需要 root；日常管理可使用具备相应 RBAC 的 kubeconfig。Rancher 只读账号只能查看，不能迁移。

容器 `privileged: false`，丢弃 ALL 后仅 NET_ADMIN、只读根、禁止提权、RuntimeDefault seccomp、无宿主机网络/PID、无 ServiceAccount Token。设备插件须由集群管理员另行维护。

## 文件

- `images/eqlink-server-arm64.tar`、`import-image.sh`：校验并导入指定节点的 k8s.io containerd。
- `deploy.py`：仅首次安装，预检节点、资源、CIDR、端口和归属。
- `upgrade.py`：v2 状态常规镜像升级，默认只出计划，保留包装、探针、挂载、网关扩展；旧状态拒绝直接升级。
- `migrate-v2.py`：旧状态的协调离线迁移/整组回滚，默认只出计划，执行时必须明确独立管理入口及镜像 ID。
- `devices.py`：导出同一份通用包、列出/吊销入网授权、列出/吊销独立设备。无导入密码。
- `tools/xray`：固定版本工具；`PROVENANCE.json`、`VALIDATION.json`、`SHA256SUMS`：来源和验证边界。

## 首次部署（现有环境禁止照此重建）

先在目标节点核对 `/dev/net/tun`、设备资源、NodePort 占用、PVC、局域网/Pod/Service CIDR。为 HY2 准备域名证书和私钥。专用私有 CA 的公有根证书可通过 bundle_trust.py 加入通用包，使用客户端进程范围信任；不能通过关闭 TLS 校验来替代。

```bash
sha256sum -c SHA256SUMS
sudo bash import-image.sh
sudo k3s crictl inspecti docker.io/eqlink/server:1.0.0-arm64
# 先只生成私密清单，替换域名、节点、证书和网段：
python3 deploy.py --node YOUR_ARM64_NODE --public-host vpn.example.net \
  --tls-cert /root/tls.crt --tls-key /root/tls.key --tls-server-name vpn.example.net \
  --overlay 10.88.0.0/24 --tcp-port 30443 --udp-port 30444 \
  --tun-resource armhpc.home.arpa/tun --render-only
# 审核后去掉 --render-only 执行；不要改造已有部署的 CA 或 REALITY 密钥。
```

NodePort 为 Local，NAT 必须指向固定节点或该节点 VIP。公网只映射两个外层端口，控制/认证/管理/统计不映射。服务端虚拟 IP 的健康 TLS 不承载登记或管理操作。

## 现有测试环境迁移

迁移流程见仓库 `docs/migration-0.3.0.md`，发行包 README 也列出入口。维持 `eqlink-test`、armhpc-node10、VIP192.168.100.248、172.21.10.0/24、既有信任与 Rancher 扩展。保留旧镜像和客户端安装包，先让两台断开，再安排维护窗口。

```bash
python3 migrate-v2.py --node armhpc-node10 --namespace eqlink-test
# 独立管理入口、私密差异/备份和导入镜像 ID 复核后：
python3 migrate-v2.py --node armhpc-node10 --namespace eqlink-test \
  --expected-image-id sha256:VERIFIED_IMAGE_CONFIG_ID \
  --independent-management-verified --apply
```

工具默认不写集群。执行阶段保存 Secret/ConfigMap/部署/Service/PVC 清单，缩容停机，维护 Pod 离线备份状态，保留 CA/REALITY/网络并停用旧设备，再只修改管理字段和镜像。失败保持停止，不自动重建状态。私密目录含可用凭据，不能发到聊天或 Git。

## 通用包与设备

```bash
umask 077
python3 devices.py enrollment-create --namespace eqlink-test \
  --name test-network --output /root/test-network.eqlink
python3 devices.py enrollment-list --namespace eqlink-test
python3 devices.py list --namespace eqlink-test
python3 devices.py enrollment-revoke --namespace eqlink-test --id GRANT_ID
python3 devices.py revoke --namespace eqlink-test --id DEVICE_ID
```

同一文件供多台导入；每台首次连接生成独立身份并申请地址。相同网络重复导入保留本机身份。导出失败/超时保留 `.request.json` 并用同一操作 ID 重试，禁止把 base64 包打印到共享日志。通用授权吊销不影响已登记设备；持包者能用新密钥登记新身份。

## 回滚与验收

`migrate-v2.py --rollback-from PRIVATE_MIGRATION_DIRECTORY --expected-image-id sha256:VERIFIED_030_MAINTENANCE_ID --independent-management-verified --apply` 恢复离线状态与旧模板；失败停在零副本也支持。管理员核对旧镜像 ID、探针、网关，然后恢复旧客户端和 DPAPI。仅回滚镜像不够。

Readiness 要求内层、虚拟健康接口及至少一条外层可用。ARM64 QEMU/网络命名空间测试不等于真实 k3s、两台 Windows、Meta 共存抓包或完整安装生命周期通过。以 `VALIDATION.json` 和发行验收矩阵为准。
