# EqLink 0.2.0 ARM64 k3s 两地网络测试包

本包包含可离线导入 containerd 的 ARM64 镜像、k3s 资源模板、部署和设备管理工具。默认公网入口为 TCP/30443、UDP/30444；服务端虚拟地址为 10.88.0.1，设备从 10.88.0.10 开始分配。

这是测试版本。默认容器为 `privileged: false`，丢弃所有 capabilities 后仅添加 `NET_ADMIN`，只读根文件系统、禁止提权、使用 `RuntimeDefault` seccomp。TUN 设备由节点设备插件分配，默认扩展资源为 `armhpc.home.arpa/tun`，可用 `--tun-resource` 指定。缺少可分配资源时部署会停止。它使用独立 Pod 网络，不使用 hostNetwork/hostPID，不挂载宿主机根目录或 ServiceAccount Token。设备插件本身需要由集群管理员事先部署并评审；本包不自动安装插件或放宽权限。

## 文件

- `images/eqlink-server-arm64.tar`：完整 Docker 镜像归档；导入后不需要访问镜像仓库。
- `import-image.sh`：校验整包并导入选定节点的 k8s.io containerd 命名空间。
- `deploy.py` / `common.py`：检查节点/端口/资源归属，生成受限权限 Secret 清单并调用 Kubernetes API 校验和应用。
- `upgrade.py`：已有部署的计划、私密备份、有限字段更新、运行检查和回滚；默认不应用。
- `devices.py`：交互式生成、列出和吊销设备，不把密码放进命令参数。
- `tools/xray`：ARM64 REALITY 密钥生成工具。
- `PROVENANCE.json`、`debian-packages-arm64.txt`、`VALIDATION.json`、`SHA256SUMS`：构建来源、组件清单、实际验证范围和校验值。

## 1. 准备节点、域名和证书

选择一台固定的 ARM64 节点，保留稳定内网 IP。核对节点 Ready、`/dev/net/tun` 存在、`local-path` StorageClass 可用、TCP/30443 与 UDP/30444 未占用。

```bash
sudo k3s kubectl get nodes -o wide
sudo k3s kubectl get storageclass
sudo k3s kubectl get service -A
sudo k3s kubectl get node YOUR_ARM64_NODE_NAME -o jsonpath='{.status.allocatable}'
ls -l /dev/net/tun
```

10.88.0.0/24 必须与两端局域网、其他 VPN、k3s 实际 Pod/Service CIDR 不重叠。部署工具支持 `--overlay 10.89.0.0/24`。Pod 内的路由检查不能代替整个集群的 CIDR 核对。

准备一个客户端可解析的域名，以及客户端信任的 CA 签发且域名匹配的完整证书链和私钥。只允许公网高端口时，可通过 DNS-01 申请证书；无需映射公网 80/443。本包不包含实际证书、私钥或已授权设备。

REALITY 默认目标 `www.cloudflare.com:443` 只是测试候选。必须确认服务端能访问它且目标适合 REALITY；可用 `--reality-target host:443 --reality-server-name host` 替换。这个出站连接与对外 NAT 高端口是不同方向。

## 2. 解包并导入镜像

把整个压缩包复制到选定的 ARM64 k3s 节点：

```bash
tar -xzf eqlink-k3s-arm64-test-0.2.0.tar.gz
cd eqlink-k3s-arm64-test-0.2.0
sudo bash import-image.sh
```

镜像只被导入当前节点。调度配置固定在你后续指定的节点上；导入和调度必须指向同一台机器。`imagePullPolicy: Never` 会让缺失镜像明确报错，不尝试外网拉取。

## 3. 首次创建资源

在具备集群管理 kubeconfig 的机器执行以下命令。所有节点均为 ARM64 时，可在控制节点解包同一份包执行。若导入镜像的节点是工作节点，也可在另一台控制节点运行此步骤。

```bash
sudo python3 deploy.py \
  --node YOUR_ARM64_NODE_NAME \
  --public-host vpn.example.net \
  --tls-server-name vpn.example.net \
  --tls-cert /root/fullchain.pem \
  --tls-key /root/privkey.pem

sudo k3s kubectl -n eqlink-test rollout status deployment/eqlink-server --timeout=300s
sudo k3s kubectl -n eqlink-test logs deployment/eqlink-server -c server --tail=100
```

工具默认使用 `k3s kubectl`；也可指定 `--kubectl /path/to/kubectl --kubeconfig /path/to/config`。从 Windows 运行时还需要 `--xray /path/to/xray.exe`；包内 `tools/xray` 适用于 Linux ARM64。

资源为独立 namespace `eqlink-test`、1Gi PVC、两个 Secret、单副本 Recreate Deployment 和混合协议 NodePort Service。`deploy.py` 遇到已有 Deployment 会拒绝继续；已有环境使用下一节的 `upgrade.py`。CA、CCD、CRL、设备状态保存在 PVC `/etc/eqlink`，重建 Pod 不重新生成 CA。不要删除 PVC。

## 3a. 已有环境升级（由对端管理员执行）

先停止设备创建、吊销和其他配置写入，保留一个可独立于 EqLink 使用的集群管理通道。单副本升级会中断虚拟网络。当前 Rancher 只读账号不能代替拥有 Secret 读取、Pod exec 和 Deployment patch 权限的管理端。

在固定节点导入镜像并检查包内校验值。导入后读取 **该节点 CRI 实际报告的镜像 ID**；`PROVENANCE.json` 同时给出构建配置 ID，供核对。先生成计划：

```bash
sudo bash import-image.sh
sudo k3s crictl inspecti docker.io/eqlink/server:0.2.0-k3s-test-arm64
sudo python3 upgrade.py --namespace eqlink-test --node armhpc-node10 \
  --tun-resource armhpc.home.arpa/tun
```

核对生成的 `private/upgrade-*/diff-public.json` 和私密的原模板/计划模板。正常升级仅更新镜像、EqLink 容器安全字段、TUN 资源和旧版自有 TUN 挂载；启动包装、args、探针、ConfigMap 挂载和管理网关扩展都保留。确认镜像 ID 后执行：

```bash
sudo python3 upgrade.py --namespace eqlink-test --node armhpc-node10 \
  --tun-resource armhpc.home.arpa/tun \
  --expected-image-id sha256:REPLACE_WITH_64_HEX_CRI_IMAGE_ID --apply
```

脚本应用前备份 PVC 状态、bootstrap/TLS Secret、挂载的 ConfigMap、Service、PVC 和 Deployment；备份目录 700、文件 600。备份包含私钥，须另行安全保管，不发送到聊天。Deployment 使用 resourceVersion 比较，期间有并发更改会中止。新 Pod 必须 Ready、实际 imageID 一致且 `inspect --json` 的检查通过；失败时，在模板未被第三方更改的前提下恢复原 Pod 模板。0.2.0 不迁移持久状态格式，自动回滚不覆盖 PVC。

人工回滚时，确保旧镜像仍在固定节点上，用该次操作输出的私密目录：

```bash
sudo python3 upgrade.py --namespace eqlink-test --node armhpc-node10 \
  --rollback-from /root/eqlink/private/upgrade-TIMESTAMP-ID \
  --expected-image-id sha256:REPLACE_WITH_OLD_CRI_IMAGE_ID --apply
```

回滚到 0.1.x 时该旧程序没有 `inspect`，脚本会保留备份并报告运行检查不可用，需人工核对原探针和客户端虚拟 IP 通信。恢复数据仅在持久状态确实损坏时进行：先停 Deployment 并确认 Pod 已退出，在同一 PVC 上以 root 解包 `state.tar.gz`，恢复原 Secret/模板后再启动；不得在运行服务上覆盖 CA 或设备数据。当前公网域名、30443/30444、`172.21.10.0/24` 和既有密钥不由升级脚本重置。

生成的 `private/deployment-*.json` 包含私钥，目录权限 700，文件权限 600。不要发到聊天或提交 Git。`--render-only` 可以只生成清单供核对，不访问集群。

## 4. 配置 NAT

路由器分别配置：

| 公网 | 内网目标 |
| --- | --- |
| TCP/30443 | 选定节点内网 IP 的 TCP/30443 |
| UDP/30444 | 同一节点内网 IP 的 UDP/30444 |

入口是直接 TCP/UDP 四层转发。OpenVPN 1194、健康检查 1195、管理接口 7505 只监听 Pod 的 127.0.0.1，不创建 Service、不映射公网。`externalTrafficPolicy: Local` 要求 NAT 指向实际运行 Pod 的节点；其他节点没有本地后端时不接入。

若改用另一组端口，用 `--tcp-port`、`--udp-port` 同时生成监听、NodePort 和导入包设置。这一版工具要求端口在 30000–32767，以适配默认 NodePort 范围。已有集群防火墙应只添加所需规则；不要把宿主机版的全局默认丢弃规则套在 k3s 节点上。

## 5. 为本机创建设备

```bash
sudo python3 devices.py add --name local-windows --output /root/local-windows.eqlink
sudo python3 devices.py list
sudo python3 devices.py status
sudo k3s kubectl -n eqlink-test exec deployment/eqlink-server -c server -- EqLink.Server inspect --json
```

密码至少 12 个字符，由终端隐藏输入。设备包通过管理员选择的渠道复制给 Windows，密码分开交付。Windows 安装 EqLink 后以管理员身份导入，并手动点击连接。

每次创建操作保留一个 `.request.json` 操作 ID。若 kubectl 连接中断，保留该文件，用原密码重试相同的命令，获取原设备包；不会自动创建另一个设备。服务端把加密导入包暂存于 PVC 的 `exports/`，仅 root 可读。吊销命令：

```bash
sudo python3 devices.py revoke --id DEVICE_ID
```

新增或吊销会同步重载两条外层认证，可能中断其他已连接测试设备的外层会话。吊销同时更新 CRL、CCD，并尝试踢出该设备的 OpenVPN 会话；管理接口失败时监督程序重启 OpenVPN。若提示状态已保存但重载失败，先检查日志，再执行：

```bash
sudo k3s kubectl -n eqlink-test exec deployment/eqlink-server -c server -- EqLink.Server apply
```

## 6. 运行流量测试

镜像包含 iperf3。待 Pod Ready，在一个终端启动虚拟网络里的测速服务：

```bash
sudo k3s kubectl -n eqlink-test exec -it deployment/eqlink-server -c server -- iperf3 -s -B 10.88.0.1
```

Windows 准备好可信的 iperf3 构建后执行：

```powershell
ping 10.88.0.1
iperf3.exe -c 10.88.0.1 -t 30
iperf3.exe -c 10.88.0.1 -R -t 30
iperf3.exe -c 10.88.0.1 -u -b 5M -l 1200 -t 30
iperf3.exe -c 10.88.0.1 -u -R -b 5M -l 1200 -t 30
```

依次测试仅 UDP、仅 TCP、自动模式下阻断 UDP、双路径阻断、恢复 UDP、重建 Pod，以及单设备吊销。阻断规则限定服务器公网 IP 和外层端口，完成后撤销。稳定 TCP 会话不会在 UDP 恢复时被主动切断。就绪检查要求 OpenVPN/TUN/健康检查和至少一条外层；一条外层退出时另一条仍能接入。

OpenVPN 内层仍为 TCP；业务 UDP 的丢包/抖动/停顿会受到内层重传影响。记录路径、吞吐、延迟、停顿、CPU 和恢复时间。60 秒恢复目标以虚拟 IP 重新可通信为准。物理网卡抓包覆盖启动、切换、双路径故障，确认无 OpenVPN 直连公网；抓包不能证明外层无法被识别或封锁。一台 client 只能测试 client/server，客户端互访需要第二台 client。

## 生命周期与诊断

```bash
sudo k3s kubectl -n eqlink-test get pods -o wide
sudo k3s kubectl -n eqlink-test describe deployment eqlink-server
sudo k3s kubectl -n eqlink-test logs deployment/eqlink-server -c server --timestamps --tail=200
sudo python3 devices.py status
```

日志含 UTC 时间戳、子进程名和退出信息，不输出私钥或包密码。每周自动更新 30 天有效的 CRL；每次容器启动也会更新。Hysteria 证书指纹写入设备包，证书换新后现有设备需要重新发包。

停止测试可删除 Deployment 和 Service，保留 PVC、Secret 和客户端导入包，以便继续测试。删除 namespace/PVC 会删除服务端身份与设备状态，须事先备份。包内 `VALIDATION.json` 列明本地实际验证范围；目标集群和两地公网验收仍应在你自己的环境执行。
