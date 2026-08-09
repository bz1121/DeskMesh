# Changelog

DeskMesh（桌联）的重要变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)，版本遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [Unreleased]

### Added

- 新增“直接信号”和“无缝远程”两种持久化切换模式；无缝模式保持显示器输入不变，首帧成功显示后才进入全视口远程控制。
- 无缝模式的全局快捷键通过已打开的控制台发起会话，不会静默降级为 DDC/CI 实体切源。

### Changed

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

[Unreleased]: https://github.com/bz1121/DeskMesh/compare/v0.1.0-alpha.1...HEAD
[0.1.0-alpha.1]: https://github.com/bz1121/DeskMesh/releases/tag/v0.1.0-alpha.1
