FROM debian:bookworm-slim

ARG SERVER_ARCHIVE
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates openvpn openssl iproute2 python3 procps \
    libicu72 libgssapi-krb5-2 libstdc++6 \
    && rm -rf /var/lib/apt/lists/*
COPY ${SERVER_ARCHIVE} /tmp/eqlink-server.tar.gz
COPY device-request.py /usr/local/lib/eqlink/device-request.py
RUN mkdir -p /opt/eqlink /etc/eqlink /run/eqlink /run/eqlink-bootstrap /run/eqlink-tls \
    && tar -xzf /tmp/eqlink-server.tar.gz -C /opt/eqlink \
    && python3 -c 'import pathlib,struct,sys; machine={"amd64":62,"arm64":183}[sys.argv[1]]; binaries=[pathlib.Path("/opt/eqlink")/n for n in ("EqLink.Server","hysteria","xray")]; assert all(p.read_bytes()[:4]==b"\x7fELF" and struct.unpack("<H",p.read_bytes()[18:20])[0]==machine for p in binaries),"Archive architecture does not match target"' "$TARGETARCH" \
    && chmod 0755 /opt/eqlink/EqLink.Server /opt/eqlink/hysteria /opt/eqlink/xray \
    && ln -s /opt/eqlink/EqLink.Server /usr/local/bin/EqLink.Server \
    && ln -s /opt/eqlink/hysteria /usr/local/bin/hysteria \
    && ln -s /opt/eqlink/xray /usr/local/bin/xray \
    && chmod 0700 /etc/eqlink \
    && rm /tmp/eqlink-server.tar.gz

ENV EQLINK_RUNTIME=container DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net LANG=C.UTF-8
WORKDIR /etc/eqlink
STOPSIGNAL SIGTERM
ENTRYPOINT ["/usr/local/bin/EqLink.Server"]
CMD ["serve", "--settings", "/run/eqlink-bootstrap/server-settings.json"]
