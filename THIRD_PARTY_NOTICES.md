# Third-party notices

DeskMesh（桌联）本身按 [MIT License](LICENSE) 发布。发行包和构建过程还使用下列主要第三方组件；各组件仍受其自己的许可证约束。

## 发行时组件

| 组件 | 用途 | 许可证 | 上游项目 |
| --- | --- | --- | --- |
| .NET Runtime 10 | 自包含 Windows 运行时 | MIT | https://github.com/dotnet/runtime |
| ASP.NET Core 10 | 本地 HTTP/WebSocket Agent | MIT | https://github.com/dotnet/aspnetcore |
| Windows Forms 10 | Windows 托盘和本机对话框 | MIT | https://github.com/dotnet/winforms |
| NAudio 2.3.0 | Windows 系统音频捕获和播放 | MIT | https://github.com/naudio/NAudio |
| React / React DOM 19、Scheduler 0.27 | 本地控制台运行时代码 | MIT | https://github.com/facebook/react |
| Phosphor Icons for React 2.1.10 | 本地控制台界面图标 | MIT | https://github.com/phosphor-icons/react |

官方便携 ZIP 的 `licenses/` 目录随包提供以下原始许可与通知：

- .NET Runtime、ASP.NET Core Runtime 和 Windows Desktop Runtime 的许可文件；
- .NET Runtime 与 ASP.NET Core Runtime 的完整第三方通知；
- NAudio 的 MIT License；
- React、React DOM 与 Scheduler 共用的 MIT License。
- Phosphor Icons 的 MIT License。

## 仅构建或测试时组件

项目还使用 Vite（MIT）、TypeScript（Apache-2.0）、ESLint（MIT）、xUnit.net（Apache-2.0）以及它们的传递依赖。它们不随便携 ZIP 分发；准确版本以 `package-lock.json`、各 `.csproj` 和 `packages.lock.json` 为准。

`CODE_OF_CONDUCT.md` 改编自 Contributor Covenant 3.0，原文按 CC BY-SA 4.0 提供，详见该文件的 Attribution。

此清单是导航页，不替代 ZIP `licenses/` 中的上游许可证与第三方通知原文。发布维护者应在每次 Release 前根据锁文件重新核对完整清单，并确保发行包保留所有上游要求的版权及通知。
