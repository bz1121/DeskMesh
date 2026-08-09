# 为 DeskMesh（桌联）贡献

感谢你帮助改进 DeskMesh。项目当前处于 `0.1.x` Alpha：优先保证断线后安全回本机、明确授权和可诊断性，其次才是功能数量和峰值性能。

参与即表示你同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。安全问题必须按 [SECURITY.md](SECURITY.md) 私密报告。

## 开始之前

- 小型修复可直接提交 Pull Request；大功能、协议变更或用户数据格式变更请先创建讨论或功能 Issue。
- 不要在 Issue、测试数据或截图中提交真实配对码、私钥、身份文件、IP、证书指纹或个人剪贴板内容。
- 一个 Pull Request 聚焦一个问题，并说明用户可见行为和失败时的安全回退。

## 开发环境

- Windows 10/11 x64；
- Git；
- Node.js `22.13.0` 或兼容的 Node.js 22 版本；
- `global.json` 指定的 .NET 10 SDK。

首次检出后运行：

```powershell
npm ci
dotnet restore .\LanSwitch.slnx --locked-mode --configfile .\NuGet.Config
```

完整验证：

```powershell
npm run typecheck
npm run lint
npm run build
dotnet test .\LanSwitch.slnx --configuration Release
```

也可以运行 `./scripts/build.ps1`。该脚本不会替代首次的 `npm ci`。

## 代码和测试要求

- C# 保持 nullable 开启，并处理取消、断线、撤销信任和重复消息。
- 涉及键鼠的改动必须保证异常路径释放全部按键，且紧急回本机仍有效。
- 涉及网络的改动必须有尺寸上限、身份校验、序号或会话边界，并避免在 HTTP 错误中泄露内部异常。
- 涉及文件的改动必须测试拒绝、取消、断线、空间不足、路径穿越和摘要不匹配。
- 前端改动应保持中文主界面、键盘可操作、可见焦点和合理的窄屏布局。
- 为修复增加回归测试；依赖硬件的行为应补充可测试的纯策略层，并在 PR 中记录实机结果。

## 提交与 Pull Request

推荐使用简洁的 Conventional Commit 前缀，例如 `fix:`、`feat:`、`docs:`、`test:`、`build:`。PR 描述应包含：问题、方案、风险、验证结果、截图（如有界面变化）以及回退方式。

维护者可以要求拆分不相关改动。合并表示贡献者同意按项目的 [MIT License](LICENSE) 发布其贡献。
