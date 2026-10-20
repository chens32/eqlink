# Kubernetes / k3s 安装

[English](kubernetes-installation.md) | [简体中文](kubernetes-installation.zh-CN.md)

使用 release 中对应架构的 **Linux 服务端压缩包构建镜像**。标准 release 不再另附 Kubernetes 离线包。下面是首次安装流程；已有环境使用文末的升级入口。

## 准备条件

- 固定一台 Ready Linux 节点，架构为 amd64 或 arm64。节点需要 `/dev/net/tun` 及独立安装的 TUN 设备插件；示例资源名为 `armhpc.home.arpa/tun`，按实际插件调整。
- 可用的 StorageClass/PVC；k3s 默认示例为 `local-path`。需要对应 Kubernetes 管理权限；节点导入镜像、维护 TUN 需要 root/sudo。
- Linux 构建机具备 Git、Python 3、curl、jq 和 Docker Buildx，能够访问 GitHub 与 Debian 软件源。优先用相同架构构建；跨架构时自行准备构建模拟环境。不需要 .NET SDK。
- 公网域名、Hysteria TLS 证书和私钥、不冲突的私有 IPv4 `/24`，以及外层高端口映射。默认 TCP `30443`、UDP `30444`。

Pod 保持单副本、Recreate、固定节点、PVC 持久保存 `/etc/eqlink`，容器 `privileged: false`、只添加 `NET_ADMIN`、只读根文件系统、禁止提权、`RuntimeDefault` seccomp。命名空间准入策略必须允许所需网络能力；生成的命名空间采用 `privileged` 准入级别，容器本身仍不启用 privileged。TUN 设备由插件分配，不直接挂宿主机设备绕过检查。

## 1. 下载、校验并构建镜像

在 Linux 构建机执行。AMD64 节点把 `ARCH=arm64` 改为 `ARCH=amd64`。部署脚本使用当前仓库源码，单独记录脚本提交和二进制版本：

```bash
set -euo pipefail
VERSION=1.0.0
ARCH=arm64
IMAGE="docker.io/eqlink/server:${VERSION}-${ARCH}"
WORK="$HOME/eqlink-kubernetes-${VERSION}-${ARCH}"
mkdir -p "$WORK"
git clone https://github.com/chens32/eqlink.git "$WORK/source"
cd "$WORK/source"
git rev-parse HEAD > "$WORK/deployment-tools-commit.txt"

CONTEXT="$WORK/image-context"
mkdir -p "$CONTEXT"
ARCHIVE="eqlink-server-${ARCH}-${VERSION}.tar.gz"
curl --fail --location --silent --show-error \
  "https://api.github.com/repos/chens32/eqlink/releases/tags/v${VERSION}" \
  --output "$WORK/release.json"
EXPECTED=$(jq -er --arg name "$ARCHIVE" \
  '.assets[] | select(.name == $name and .state == "uploaded") | .digest | select(startswith("sha256:")) | ltrimstr("sha256:")' \
  "$WORK/release.json")
curl --fail --location --show-error \
  "https://github.com/chens32/eqlink/releases/download/v${VERSION}/${ARCHIVE}" \
  --output "$CONTEXT/$ARCHIVE"
printf '%s  %s\n' "$EXPECTED" "$CONTEXT/$ARCHIVE" | sha256sum --check -
cp packaging/k3s/device-request.py "$CONTEXT/"
docker buildx build --load --platform "linux/$ARCH" \
  --build-arg "SERVER_ARCHIVE=$ARCHIVE" \
  --tag "$IMAGE" --file packaging/kubernetes/Dockerfile "$CONTEXT"
docker image inspect --format '{{.Id}} {{.Architecture}}' "$IMAGE" \
  > "$WORK/image-identity.txt"
docker save --output "$WORK/eqlink-server-${ARCH}-image.tar" "$IMAGE"
sha256sum "$WORK/eqlink-server-${ARCH}-image.tar"
```

Dockerfile 检查 EqLink.Server、Hysteria、Xray 的 ELF 架构，保留服务端包内及系统包的许可文件。构建上下文只放上面的公开文件，证书、私钥、设备包和持久状态存放在其外。GitHub 附件摘要用于校验文件一致性，不是独立签名。

## 2. 导入固定节点

把镜像 tar 复制到运行 EqLink 的节点，对照构建机核对 SHA-256。在 k3s 节点执行：

```bash
sudo k3s ctr --namespace k8s.io images import /path/to/eqlink-server-arm64-image.tar
sudo k3s crictl inspecti docker.io/eqlink/server:1.0.0-arm64
```

AMD64 对应替换文件和标签后缀。其他使用 containerd 的 Kubernetes 发行版，应导入该节点实际使用的 CRI/containerd 的 `k8s.io` 命名空间。导入到 Docker 自身并不代表 Kubernetes 已获得镜像。部署采用 `imagePullPolicy: Never`，示例镜像名不代表远端仓库已有镜像可拉取。

## 3. 首次部署

在拥有源码和管理员 kubeconfig 的管理机上检查：

```bash
kubectl get nodes -o wide
kubectl get node YOUR_NODE -o jsonpath='{.status.allocatable}'
kubectl get storageclass
```

确认目标节点 Ready、架构正确、TUN 资源空闲、NodePort 未占用，虚拟网段不与 LAN/Pod/Service/其他 VPN 网段重叠。证书和私钥放在仓库外的私密目录。生成 REALITY 初始密钥需要管理机可执行的 `xray`；下面假设管理机也是 ARM64，架构不同时从另一份服务端包提取管理机架构的工具。

```bash
umask 077
mkdir -p "$HOME/eqlink-private/tools"
tar -xzf /path/to/eqlink-server-arm64-1.0.0.tar.gz \
  -C "$HOME/eqlink-private/tools"
chmod 0755 "$HOME/eqlink-private/tools/xray"
python3 packaging/k3s/deploy.py \
  --node YOUR_NODE --arch arm64 \
  --image docker.io/eqlink/server:1.0.0-arm64 \
  --namespace eqlink-test --storage-class local-path \
  --public-host vpn.example.net --tls-server-name vpn.example.net \
  --tls-cert /private/tls.crt --tls-key /private/tls.key \
  --overlay 10.88.0.0/24 --tcp-port 30443 --udp-port 30444 \
  --tun-resource armhpc.home.arpa/tun \
  --xray "$HOME/eqlink-private/tools/xray" \
  --output "$HOME/eqlink-private/rendered" --render-only
```

检查生成的私密清单，避免展示 Secret 正文。审核后原样重新执行、只去掉 `--render-only`，工具才会访问集群并检查依赖、归属和冲突后应用。不要手动 apply 渲染文件绕过预检。首次安装命令拒绝替换已有 EqLink Deployment。

路由器只配置两个外层映射，目标为 Pod 所在固定节点或其 VIP：

```text
公网 TCP 30443 -> 固定节点/VIP:30443/TCP
公网 UDP 30444 -> 固定节点/VIP:30444/UDP
```

Service 使用 `externalTrafficPolicy: Local`。控制、认证、管理与 OpenVPN 回环端口不配置公网 Service 或路由器映射。

## 4. 检查与生成通用包

```bash
kubectl -n eqlink-test rollout status deployment/eqlink-server --timeout=300s
kubectl -n eqlink-test get pods -l app=eqlink-server -o wide
kubectl -n eqlink-test exec deployment/eqlink-server -c server -- EqLink.Server inspect --json
umask 077
python3 packaging/k3s/devices.py enrollment-create --namespace eqlink-test \
  --name network --output "$HOME/eqlink-private/network.eqlink"
```

Hysteria 使用私有 CA 时，在生成命令追加 `--hysteria-ca /private/root-ca.cer --expected-ca-sha256 VERIFIED_CERTIFICATE_SHA256`，指纹应由管理员独立核对，是证书 DER 内容的 SHA-256，不是 PEM 文件文本的哈希。设备包私密传输，多台 Windows 可以导入同一文件并登记各自身份和地址。

Ready 只要求内层及至少一条外层可用，仍需用真实客户端验证两条路径、虚拟 IP 互访和回退；此说明没有把清单验证当作现场部署验收。

## 已有环境升级与回滚

保留现有 CA、REALITY 密钥、虚拟网段、VIP/NAT、PVC、设备身份、启动包装、探针和管理网关扩展；不要重新运行首次初始化。构建/导入新镜像后，用 `packaging/k3s/upgrade.py --help` 查看升级参数，先生成计划，执行时指定 `--arch`、`--image` 和经 CRI 核对的 `--expected-image-id`。提前检查私密备份与回滚材料，并保留不依赖 EqLink 的管理入口。旧身份迁移见 [migration-0.3.0.md](migration-0.3.0.md)。

当前 ARMHPC 环境继续保留 `eqlink-test`、`armhpc-node10`、VIP `192.168.100.248`、`172.21.10.0/24` 和 Rancher 网关。本轮发布附件调整不部署或重启该环境。
