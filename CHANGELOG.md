# Changelog

DeskMesh（桌联）的重要变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)，版本遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [Unreleased]

### Changed

- 本机管理员密码的最小长度调整为 6 个字符；仍建议使用更长、仅用于 DeskMesh 的密码，登录失败锁定策略保持不变。

### Fixed

- 本地控制台 HTML、JavaScript 与 CSS 现在禁止复用旧缓存，避免升级后出现“新 Agent + 旧网页”并把管理员首次设置误报为 Agent 离线。
- 修复退出时音频后台服务被依赖注入容器重复释放，导致弹出 `The CancellationTokenSource has been disposed.` 的问题。

## [0.1.0-alpha.3] - 2026-08-10

### Added

- 新增仅限本机的单一管理员登录、首次设置、独立管理员页面、密码修改和其他会话撤销功能。
- DeskMesh 托盘现在负责签发 10 分钟有效的一次性首次设置链接，并可在本机确认后重置控制台登录；设备身份、配对关系和显示器映射不会被删除。

### Changed

- 本地控制台采用 Quiet Relay 浅色工作台设计：全局状态栏、图标导航、设备接力控制区和诊断事件卡片在桌面、平板与手机上分别适配，同时保留独立工作区和深链接。
- 总览页现在直接展示真实本机、目标设备、连接状态和近期诊断信息；控制动作继续复用原有的直接信号、无缝远程和安全回本机流程。

### Security

- 管理员凭据使用 PBKDF2-HMAC-SHA512 派生并由 Windows DPAPI CurrentUser 加密保存；损坏或未来版本的凭据文件会保持锁定并要求托盘恢复。
- 本地 API 与浏览器 WebSocket 现在统一要求管理员会话和会话级 CSRF；会话使用 HttpOnly、SameSite=Strict 的实例隔离 Cookie，并具有空闲/绝对到期、令牌轮换和撤销联动。
- 10 分钟内连续登录失败 5 次会锁定 5 分钟；锁定状态跨 Agent 重启保留。

## [0.1.0-alpha.2] - 2026-08-09

### Added

- 新增“直接信号”和“无缝远程”两种持久化切换模式；无缝模式保持显示器输入不变，首帧成功显示后才进入全视口远程控制。
- 无缝模式的全局快捷键通过已打开的控制台发起会话，不会静默降级为 DDC/CI 实体切源。

### Changed

- 本地控制台拆分为总览、设备、远程桌面、剪贴板、文件、显示器、安全和诊断八个独立工作区，并支持深链接、浏览器前进/后退及移动端页面选择。
- 远程桌面现在区分连接、等待首帧和已显示状态；首帧超时、解码失败或断流会清除旧画面并安全退出会话。
- 浏览器远程桌面与系统级焦点切换现在互斥；回本机操作会关闭无缝会话，输入心跳超过两秒会在远端释放所有按键。
- 远程桌面子协议升级为 v2；两台电脑必须使用同一新版构建，旧版远程桌面不会与新版混用。

## [0.1.0-alpha.1] - 2026-08-09

### Added

- 两台 Windows 设备通过一次性配对码、双方确认、ECDSA 身份、DPAPI 和双向 TLS 建立局域网信任。
- 中文 localhost 控制台和托盘应用。
- 全局键鼠转发、可配置切换快捷键及不可修改的紧急回本机快捷键。
- 文本和图片剪贴板同步，以及双方确认、SHA-256 校验的文件传输。
- DDC/CI 输入源探测、映射、切换和实验性的实体按钮跟随。
- 实验性远程桌面、系统音频和最高 90 FPS 目标模式。
- 建立公开仓库的安全、隐私、贡献、CI、CodeQL 和发布流程。

### Security

- 输入会话使用 epoch 和序号拒绝旧包；断线或异常时释放按键并恢复本机输入。
- 本地控制页面仅监听回环地址；设备协议限制在已配对身份和证书指纹。

### Known limitations

- 这是未签名、未经独立安全审计的 Alpha。
- 软件输入注入不能跨越 UAC 安全桌面、锁屏、登录前、Ctrl+Alt+Del、BIOS 或部分反作弊边界。
- 90 FPS 是实验性目标上限，不是所有硬件和网络下的保证值。
- DDC/CI 行为依赖具体显示器，必须保留实体切源手段。

[Unreleased]: https://github.com/bz1121/DeskMesh/compare/v0.1.0-alpha.3...HEAD
[0.1.0-alpha.3]: https://github.com/bz1121/DeskMesh/compare/v0.1.0-alpha.2...v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/bz1121/DeskMesh/compare/v0.1.0-alpha.1...v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/bz1121/DeskMesh/releases/tag/v0.1.0-alpha.1
