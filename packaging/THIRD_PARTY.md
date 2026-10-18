# Bundled dependencies

The Windows build downloads exact artifacts named in `third_party.lock.json`, verifies SHA-256, and checks the OpenVPN MSI Authenticode signature before packaging.

- OpenVPN Community 2.7.7-I001: official MSI from [OpenVPN Community Downloads](https://community.openvpn.net/Downloads). The MSI remains installed when EqLink is removed because it may be shared with other OpenVPN configurations.
- Hysteria 2 v2.12.3: official release from [HyNetworks/hysteria](https://github.com/HyNetworks/hysteria/releases/tag/app/v2.12.3). Its license is installed with EqLink.
- Xray-core v26.3.27: official release from [XTLS/Xray-core](https://github.com/XTLS/Xray-core/releases/tag/v26.3.27). Its license is installed with EqLink.
- WiX Toolset 5.0.2: build tool only, from [wixtoolset/wix](https://github.com/wixtoolset/wix/releases/tag/v5.0.2).

The ARM64 k3s test image also contains Debian Bookworm packages including OpenVPN, OpenSSL, Python and iperf3. Exact package versions are recorded in `debian-packages-arm64.txt`; their copyright and license notices remain under `/usr/share/doc` inside the image. Debian archive signatures and package checksums are verified during construction. The offline archive's own hashes and executable/source provenance are included with the test package.

Windows release builds should be Authenticode-signed before distribution. The unsigned build is for controlled testing.
