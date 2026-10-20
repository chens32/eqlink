# Contributing to EqLink

Read the [README](README.md), [development guide](docs/development.md), [security policy](SECURITY.md) and [validation status](docs/validation-status.md) before changing behavior. EqLink v1.0.0 is a draft release with outstanding runtime acceptance gates.

## Local checks

Use the SDK pinned in global.json, Python 3.12+ and PowerShell for Windows packaging:

```text
python tools/check-repository.py
python tools/check-release.py
dotnet build src/EqLink.Server/EqLink.Server.csproj -c Release
dotnet run --project src/EqLink.Tests/EqLink.Tests.csproj -c Release
python packaging/k3s/test-upgrade.py
python packaging/k3s/test-migration.py
python packaging/k3s/test_bundle_trust.py
git diff --check
```

On Windows, compile ClientGui, ClientService, GuiTests and WindowsTests. The manifest checks use .github/requirements-ci.txt and official Kubernetes schemas; CI shows how to prepare them. Native WFP, installer, routing, NIC and fault tests require a dedicated authorized environment.

CI runs on PRs and main and can be started manually. It checks source hygiene/history secrets, offline deployment/migration/CA tools, Windows/Linux core tests, Windows compilation and CodeQL. It does not deploy, modify WFP, interrupt Meta, install/uninstall software or establish full end-to-end acceptance.

## Pull requests

- Use a focused branch and reviewable commits. Describe behavior, tests, compatibility, recovery and remaining limitations.
- Preserve fixed virtual IPv4 addressing, independent device identities, the single OpenVPN session, mode restrictions and fail-closed behavior.
- Generate test credentials at runtime. Never commit production identities, bundles, state, captures, private backups or raw diagnostics.
- Review the staged tree before committing. .gitignore does not remove files already tracked by Git.
- Pin dependency versions/downloads/hashes and Actions to complete commit SHAs. Review licenses, notices and corresponding-source requirements when changing redistributed dependencies.
- Keep original failures and distinguish passed, failed, skipped and unavailable. A fixture or local loopback test is not a second-machine or native-cluster test.
- Do not add pull_request_target jobs that execute untrusted PR code with write tokens or secrets. Do not use production/self-hosted privileged runners for public PR tests.

EqLink's original code is GPL-3.0-only. By contributing, you agree that your contribution is distributed under that project license. Third-party code keeps its own notices and terms; see [license review](docs/licensing.md).
