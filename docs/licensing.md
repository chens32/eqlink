# License and redistribution review

EqLink's original source is licensed under **GPL-3.0-only**, as selected by the project owner. See [LICENSE](../LICENSE). Third-party components retain their own licenses; the EqLink license does not relicense them.

## Component boundaries

EqLink invokes OpenVPN, Hysteria and Xray as separate executables through command lines, sockets and their documented management APIs. It does not link their implementation into the EqLink assemblies. The source review found no imported OpenVPN or Xray implementation requiring EqLink's original files to adopt those components' licenses. The GNU project's [aggregation guidance](https://www.gnu.org/licenses/gpl-faq.html#MereAggregation) distinguishes this arrangement from incorporating implementation into a combined program.

| Component | Pinned version | License and release requirement |
| --- | --- | --- |
| EqLink | 1.0.0 | GPL-3.0-only; release the exact corresponding source and build/installation scripts alongside binaries. |
| OpenVPN Community Windows MSI | 2.7.7-I001 | OpenVPN itself is GPLv2 with linking exceptions; the MSI contains additional independently licensed components. Retain upstream notices and provide the corresponding sources/build inputs for redistributed components. Do not describe the MSI as GPLv3. |
| Hysteria | 2.12.3 | MIT for Hysteria; preserve its notice and the notices/licenses applicable to its Go dependencies. |
| Xray-core | 26.3.27 | MPL-2.0 for Xray; preserve the license and make its corresponding source available, including any covered modifications. It is redistributed unmodified. Its dependencies have additional notices. |
| .NET runtime, Windows Desktop and ASP.NET Core | 10.0.12 | MIT plus third-party notices from the exact runtime packs. Self-contained publication must retain these notices. |
| WiX | 5.0.2 | Microsoft Reciprocal License (MS-RL). Preserve notices and source obligations for bootstrapper code included in the installer. Using WiX to package EqLink does not change the license of EqLink's separate executables. |
| Debian ARM64 image packages | Recorded in the image's package manifest | Mixed licenses. Keep `/usr/share/doc/*/copyright`; source availability must cover the corresponding package versions, not only EqLink's source. |

Primary pinned references:

- [OpenVPN COPYING](https://github.com/OpenVPN/openvpn/blob/v2.7.7/COPYING) and [Windows MSI build tag](https://github.com/OpenVPN/openvpn-build/tree/OpenVPN-2.7.7-I001).
- [Hysteria license](https://github.com/HyNetworks/hysteria/blob/app/v2.12.3/LICENSE.md).
- [Xray license](https://github.com/XTLS/Xray-core/blob/v26.3.27/LICENSE).
- [.NET runtime license](https://github.com/dotnet/runtime/blob/v10.0.12/LICENSE.TXT).
- [WiX license](https://github.com/wixtoolset/wix/blob/v5.0.2/LICENSE.TXT).

## Distribution policy

`packaging/prepare-licenses.py` copies the pinned component notices and runtime-pack notices into Windows and Linux build staging directories. Release manifests identify every shipped asset and its SHA-256. The EqLink source archive is collected from the committed Git tree, not from local runtime directories.

GPLv3 for EqLink does **not** eliminate OpenVPN's GPLv2 source obligations, Xray's MPL obligations, Debian package obligations, or third-party notice requirements. An upstream homepage link by itself is not recorded as proof of complete corresponding-source compliance. Source acquisition/provenance and any unresolved redistribution gates are recorded in the draft release acceptance record. The release must remain a draft until these gates are resolved and reviewed.

The existing test bundle included an embedded official OpenVPN MSI. This review preserves that installer architecture and records its redistribution requirements explicitly. Do not publish the draft on the strength of the license inventory alone.

## Future changes

Copying or linking third-party implementation into EqLink requires a new compatibility review. Update the dependency lock, notices and source inventory when upgrading dependencies. Test-only tools and GitHub Actions do not automatically become runtime dependencies.
