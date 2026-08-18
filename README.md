# DeskMesh

[English](README.md) | [简体中文](README.zh-CN.md)

[![CI](https://github.com/bz1121/deskmesh/actions/workflows/ci.yml/badge.svg)](https://github.com/bz1121/deskmesh/actions/workflows/ci.yml)
[![CodeQL](https://github.com/bz1121/deskmesh/actions/workflows/codeql.yml/badge.svg)](https://github.com/bz1121/deskmesh/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

DeskMesh is a local-network desktop companion for Windows 10 and Windows 11 x64. Run the same tray agent on two trusted PCs to share keyboard and mouse input, system audio, a remote desktop view, text and image clipboards, and approval-based file transfers. If both PCs share a DDC/CI-capable monitor, DeskMesh can also coordinate HDMI1 and DisplayPort input switching.

> **Alpha notice:** The current public release is `v0.1.0-alpha.5`. Remote desktop, audio forwarding, and DDC/CI integration are experimental. The 90 FPS setting is an upper limit, not a performance guarantee for every computer or network. Release executables are not yet Authenticode-signed, so Windows SmartScreen may report an unknown publisher.

DeskMesh has no cloud dependency. Its local control panel listens only on `127.0.0.1:5616`. Peer traffic uses `45832/TCP` with mutual TLS, ECDSA device identities, and pinned certificate fingerprints; automatic discovery uses `45830/UDP`.

Each PC has one local DeskMesh administrator account that unlocks its own web control panel. First-time setup must be opened from that PC's tray icon so the Agent can issue a short-lived, one-time setup link; browsing directly to the address cannot claim an unconfigured console. This application login is local to one PC and is separate from peer-device pairing and mutual TLS.

When upgrading from a release that did not have administrator login, the existing device identity, trusted peers, display mappings, and other settings remain in place. The upgraded console starts in the unconfigured-administrator state; open it from the tray once to create the local administrator. Re-pairing and display recalibration are not required.

## Features

- Two explicit switching modes: **Direct Signal** coordinates DDC/CI and global input for native image quality, while **Seamless Remote** keeps the monitor input unchanged and opens a full-viewport encrypted remote desktop session without the signal-switch blackout.
- Software-based global keyboard and mouse forwarding with a configurable switch shortcut. The fixed emergency shortcut `Ctrl+Alt+Shift+Esc` always releases control back to the local PC.
- Remote desktop viewing and control with remote-display selection, JPEG-compatible transport, and a configurable 2–90 FPS range.
- Remote system-audio forwarding to the controller's default Windows output device. Microphones are never captured.
- Unicode text and PNG image clipboard synchronization with size limits, loop prevention, and replay protection.
- Drag-and-drop single-file transfers that require explicit approval and a destination on the receiving PC, using TLS streaming and final SHA-256 verification for files up to 2 GB.
- DDC/CI VCP `0x60` two-sample probing, HDMI1/DisplayPort mappings, switch verification, and physical-input-following when both agents can read the monitor state.
- A restricted write-only DDC compatibility mode that permits only HDMI1 `0x11` and DisplayPort `0x0F`; every mapping must be tested individually and confirmed visually.
- Six-digit one-time pairing codes, human-verifiable security phrases, mutual TLS, epoch and sequence replay protection, multicast/directed-broadcast discovery, and manual IP entry.
- A responsive Simplified Chinese control panel protected by a local administrator login, a tray-only first-run and recovery path, and optional startup after the current Windows user signs in.
- An optional, explicitly installed Windows service that relays only authenticated keyboard and mouse events to the UAC secure desktop without disabling UAC or approving prompts automatically.

For privacy, **automatic clipboard synchronization, audio forwarding, and remote desktop access are disabled on new installations**. Pair only with trusted devices and enable each capability locally as needed.

## Download and verify

Download `DeskMesh-0.1.0-alpha.5-win-x64.zip` and `SHA256SUMS.txt` from [GitHub Releases](https://github.com/bz1121/DeskMesh/releases). Both PCs must run the same version.

Verify the archive in PowerShell:

```powershell
Get-FileHash .\DeskMesh-0.1.0-alpha.5-win-x64.zip -Algorithm SHA256
```

Compare the result with `SHA256SUMS.txt` on the release page before extracting the archive. DeskMesh is portable: it does not install a SYSTEM service and does not include LAN self-update or remote software-push functionality.

After extraction, launch the single self-contained `DeskMesh.exe` in the package root. Runtime assemblies are bundled into that executable; the adjacent `wwwroot`, `docs`, and `licenses` directories must remain beside it.

The current Alpha is not Authenticode-signed. After verifying the official checksum, Windows SmartScreen may still show **Unknown publisher**; choose **More info → Run anyway** only for the verified GitHub release. Do not disable SmartScreen globally. A future public-trust code-signing identity or Microsoft Store package is required to remove this warning for other users.

## Five-minute quick start

1. Extract the release ZIP on both Windows PCs and run `DeskMesh.exe` on each one.
2. If Windows Firewall prompts you, allow access only on **Private networks**.
3. On each PC, open the control panel from the DeskMesh tray icon. The first launch uses a one-time setup link to create that PC's local administrator; later visits require that administrator login.
4. On the target PC, open **Security and pairing** and generate a fresh pairing code. On the other PC, enter the target IP address and that code.
5. Compare the security phrase and certificate fingerprint on both PCs, then approve the request on the target PC.
6. Enable clipboard synchronization, audio forwarding, or remote desktop access only where needed.
7. Choose **Direct Signal** for native monitor output or **Seamless Remote** for a no-signal-switch remote view. The seamless mode requires the local control-panel tab to remain open and focused, and the target PC to allow remote desktop access.
8. If both PCs share one monitor, follow the [complete setup guide in Simplified Chinese](docs/QUICKSTART.zh-CN.md) to calibrate HDMI1 and DisplayPort.

The default data directory is `%LOCALAPPDATA%\DeskMesh`. For compatibility with early test builds, DeskMesh continues to use `%LOCALAPPDATA%\LanSwitch` when that legacy directory is the only one present, preserving existing pairings and display mappings.

## Important limitations

DeskMesh forwards input in software; it does not physically reconnect USB devices to another PC. Normal `SendInput` cannot cross the UAC secure desktop. An optional privileged bridge can be installed from **Administrator settings** on each target PC to relay DeskMesh's fixed keyboard/mouse protocol while the UAC desktop is active. Windows still displays the consent prompt and the user must approve it; the bridge cannot execute commands or approve UAC by itself. Lock screens, pre-login interfaces, `Ctrl+Alt+Del`, BIOS/UEFI, and some anti-cheat-protected games remain unsupported—use a hardware KVM for those scenarios.

The DeskMesh administrator is an application account, not a Windows administrator. Unlocking the control panel does not elevate DeskMesh or bypass UAC. Installing or removing the optional UAC bridge is a separate Windows administrator action and always triggers a Windows consent prompt. DeskMesh also cannot defend against malicious code already running as the same Windows user, a compromised browser profile, or a Windows administrator.

The web login protects the loopback control panel; peer authentication is a different boundary. Paired Agents still authenticate over the LAN with mutual TLS and pinned device certificates. A web session never replaces peer pairing, and pairing a device never signs that device into the local administrator panel. If the administrator password is forgotten, use the tray recovery command on that PC. Recovery ends local web sessions and resets only the control-panel credential; it does not delete the device identity, trusted peer records, or display mappings.

DDC/CI reliability depends on the monitor, cables, graphics driver, and active input. A monitor may stop exposing its DDC channel to the previous input after switching. Always keep the monitor's physical controls and the emergency shortcut available as fallback options.

Seamless Remote avoids the monitor's HDMI/DisplayPort resynchronization blackout because it does not change the physical input. It uses the browser remote-desktop stream instead of the system-wide input route; the first decoded frame must be displayed before control starts. The global switch shortcut can request this mode only while a control-panel tab is open, visible, and focused. In this mode, the tray switch action opens the control panel and asks you to select a device instead of guessing a target. Use Direct Signal when native image quality, HDR, high refresh rate, or protected applications matter more than a blackout-free transition.

The current trust model targets private home networks. Approving a pairing establishes device-level trust; per-device, fine-grained capability permissions are not yet available. See the [security model](docs/SECURITY-MODEL.md) and [privacy notice](PRIVACY.md) for details.

## Build from source

Requirements:

- Windows 10 or Windows 11 x64
- Node.js 22 or later
- .NET SDK 10.0.302 (see `global.json`)

```powershell
npm ci
.\scripts\build.ps1
.\scripts\package.ps1
```

The build script runs TypeScript type checking, ESLint, the Vite production build, and all .NET tests in Release configuration. The packaging script produces a self-contained `win-x64` ZIP, a version/commit manifest, and a SHA-256 digest. Public packages exclude PDB files and development configuration.

## Documentation

The detailed project documentation is currently maintained in Simplified Chinese:

- [Simplified Chinese README](README.zh-CN.md)
- [Complete setup guide (Simplified Chinese)](docs/QUICKSTART.zh-CN.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Security model](docs/SECURITY-MODEL.md)
- [Privacy notice](PRIVACY.md)
- [Release process](docs/RELEASING.md)
- [Support and troubleshooting](SUPPORT.md)
- [Contributing guide](CONTRIBUTING.md)
- [Changelog](CHANGELOG.md)

Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Do not include pairing codes, certificates, IP addresses, file names, or unsanitized diagnostic logs in public issues.

## License

DeskMesh is released under the [MIT License](LICENSE). Third-party notices are available in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
