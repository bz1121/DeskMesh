# DeskMesh（桌联）

[English](README.md) | [简体中文](README.zh-CN.md)

[![CI](https://github.com/bz1121/deskmesh/actions/workflows/ci.yml/badge.svg)](https://github.com/bz1121/deskmesh/actions/workflows/ci.yml)
[![CodeQL](https://github.com/bz1121/deskmesh/actions/workflows/codeql.yml/badge.svg)](https://github.com/bz1121/deskmesh/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

DeskMesh 是面向 Windows 10/11 x64 的局域网桌面互联工具。两台可信电脑运行同一个托盘 Agent，即可共享键鼠、系统音频、远程桌面、文本/图片剪贴板和确认式文件传输；共用一台支持 DDC/CI 的显示器时，还可联动 HDMI1 与 DP 输入源。

> **Alpha 提示：** 当前公开版本为 `v0.1.0-alpha.2`。远程桌面、音频与 DDC/CI 属于实验能力；90 FPS 是配置上限，不是所有电脑或网络都能达到的保证值。发行 EXE 暂未进行 Authenticode 签名，Windows SmartScreen 可能提示“未知发布者”。

DeskMesh 不依赖云服务。本地中文控制台只监听 `127.0.0.1:5616`；设备间使用 `45832/TCP`、双向 TLS、ECDSA 设备身份及证书指纹固定，自动发现使用 `45830/UDP`。

每台电脑各有一个仅在本机验证的 DeskMesh 管理员，用于解锁本机网页控制台。首次设置必须从该电脑的托盘图标打开，由 Agent 签发短时、一次性的设置链接；直接在浏览器输入地址不能抢先注册未配置的控制台。此应用登录与设备配对、mTLS 是两套独立边界。

从没有管理员登录功能的旧版本升级时，设备身份、已信任设备、显示器映射和其他设置保持不变；升级后的控制台会处于“尚未设置管理员”状态，只需从托盘打开一次并创建本机管理员，无需重新配对或重新校准。

## 功能

- 两种明确切换方式：**直接信号**联动 DDC/CI 与全局键鼠，保持原生画质；**无缝远程**不改变显示器输入源，通过加密远程画面避免切信号时的短暂黑屏。
- 全局键鼠软件转发，可配置切换快捷键；固定紧急键 `Ctrl+Alt+Shift+Esc` 随时释放回本机。
- 远程桌面查看与控制，支持选择远端屏幕、JPEG 兼容传输和 2–90 FPS 配置。
- 远端系统音频转发到控制端 Windows 默认输出，不采集麦克风。
- Unicode 文本与 PNG 图片剪贴板同步，带大小限制、去环和防重放。
- 拖放单文件发送，接收端明确批准并选择保存位置；TLS 流式传输、最终 SHA-256 校验，最大 2 GB。
- DDC/CI VCP `0x60` 双样本探测、HDMI1/DP 映射、切源确认及双 Agent 实体切源跟随。
- DDC 只写兼容模式只允许 HDMI1 `0x11` 和 DP `0x0F`，必须逐项测试并目视确认。
- 六位一次性配对码、安全短语、mTLS、epoch/序号防重放、组播/定向广播发现和手动 IP。
- 本机管理员登录保护的中文响应式控制台、仅托盘可发起的首次设置与恢复，以及当前 Windows 用户登录后自启动。

为保护隐私，**新安装默认关闭自动剪贴板、音频和远程桌面**。请只与可信设备配对，并在本机逐项开启需要的能力。

## 下载与校验

从 [GitHub Releases](https://github.com/bz1121/DeskMesh/releases) 下载
`DeskMesh-0.1.0-alpha.2-win-x64.zip` 和 `SHA256SUMS.txt`。两台电脑必须使用同一版本。

PowerShell 校验：

```powershell
Get-FileHash .\DeskMesh-0.1.0-alpha.2-win-x64.zip -Algorithm SHA256
```

确认输出与 Release 页面及 `SHA256SUMS.txt` 一致后再解压运行。DeskMesh 是便携程序，不安装 SYSTEM 服务，也没有局域网自更新或远程推送程序功能。

## 五分钟快速开始

1. 在两台 Windows 电脑分别解压发行 ZIP，运行 `DeskMesh.exe`。
2. Windows 防火墙询问时仅允许“专用网络”。
3. 两台电脑都从 DeskMesh 托盘图标打开控制台；首次打开使用一次性设置链接创建各自的本机管理员，后续访问需要登录。
4. 目标电脑在“安全与配对”生成新配对码；另一台填写目标 IP 与配对码。
5. 两端核对安全短语和证书指纹，在目标电脑批准。
6. 在网页中按需开启剪贴板、音频或远程桌面。
7. 选择“直接信号”获得原生画质，或选择“无缝远程”避免切换信号时黑屏；无缝模式要求本机控制台标签保持打开并位于前台、获得焦点，且对方允许远程桌面。
8. 若使用同一显示器，再按照 [中文完整使用说明](docs/QUICKSTART.zh-CN.md) 校准 HDMI1/DP。

默认数据目录是 `%LOCALAPPDATA%\DeskMesh`。为兼容早期测试版本，如果仅检测到
`%LOCALAPPDATA%\LanSwitch`，程序会继续使用旧目录，不会丢失配对和显示器映射。

## 重要边界

DeskMesh 是软件转发，不会真的把 USB 设备重新插到另一台电脑。Windows `SendInput` 不能可靠控制 UAC 安全桌面、锁屏、登录前界面、`Ctrl+Alt+Del`、BIOS、权限更高的管理员窗口或部分反作弊游戏；这些场景请使用硬件 KVM。

DeskMesh 管理员只是应用内账户，不是 Windows 管理员。解锁控制台不会提升 DeskMesh 权限、绕过 UAC，也不会扩大 `SendInput` 的 Windows 安全边界；它同样不能防御已经以同一 Windows 用户身份运行的恶意代码、已失陷的浏览器配置或 Windows 管理员。

网页登录只保护 `127.0.0.1` 上的本机控制面；两台 Agent 之间仍通过 mTLS、证书指纹和配对关系验证身份。网页会话不能代替设备配对，设备配对也不会登录本机管理员控制台。忘记管理员密码时，必须在该电脑的托盘菜单中重置控制台登录；该操作会结束本机网页会话并重置管理员凭据，但不会删除设备身份、已配对设备或显示器映射。

DDC/CI 是否可靠取决于显示器、线材、显卡驱动和当前输入。切源后原输入端可能失去 DDC 通道。任何自动切换失败时都应保留显示器实体摇杆和紧急快捷键作为兜底。

“无缝远程”不会更改 HDMI/DP，因此可避开显示器重新同步信号时的黑屏；它使用浏览器远程桌面会话，而不是系统级全局键鼠路由，并且必须等第一帧成功解码和显示后才进入控制。全局切换快捷键只有在控制台标签保持打开、位于前台且获得焦点时才能请求无缝会话；托盘切换命令只会打开控制中心并请你选择设备，不会猜测目标。需要原生画质、HDR、高刷新率或受保护程序时，请使用“直接信号”。

当前配对模型面向可信家庭专用网络：批准配对即建立设备级信任，尚未提供逐设备的细粒度能力权限。详细说明见 [安全模型](docs/SECURITY-MODEL.md) 和 [隐私说明](PRIVACY.md)。

## 从源码构建

要求：

- Windows 10/11 x64
- Node.js 22+
- .NET SDK 10.0.302（见 `global.json`）

```powershell
npm ci
.\scripts\build.ps1
.\scripts\package.ps1
```

构建脚本执行 TypeScript 类型检查、ESLint、Vite 生产构建及全部 .NET Release 测试。打包脚本生成自包含 `win-x64` ZIP、版本/提交清单和 SHA-256；用户包不包含 PDB 或 Development 配置。

## 文档

- [English README](README.md)
- [中文完整使用说明](docs/QUICKSTART.zh-CN.md)
- [架构说明](docs/ARCHITECTURE.md)
- [安全模型](docs/SECURITY-MODEL.md)
- [隐私说明](PRIVACY.md)
- [发布流程](docs/RELEASING.md)
- [支持与故障反馈](SUPPORT.md)
- [贡献指南](CONTRIBUTING.md)
- [变更日志](CHANGELOG.md)

安全漏洞请按 [SECURITY.md](SECURITY.md) 私密报告，不要在公开 Issue 中附上配对码、证书、IP、文件名或未经脱敏的诊断日志。

## 许可证

DeskMesh 采用 [MIT License](LICENSE)。第三方组件声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
