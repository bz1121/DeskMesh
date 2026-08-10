# DeskMesh（桌联）发布流程

DeskMesh 使用语义化版本。首个公开版本是 `0.1.0-alpha.1`，Git tag 必须是 `v0.1.0-alpha.1`。Alpha 发布物当前未做 Authenticode 签名，Release 标题和说明必须明确写出这一点。

## 一次性仓库设置

首次公开时只发布已经审计的 `v0.1.0-alpha.1` 当前源码快照，不上传本机的开发提交历史。公开仓库的第一个提交使用 GitHub 隐私邮箱；本地开发分支继续保留，但不得推送到公开远端。完成首次发布后，后续开发直接基于公开 `main` 正常提交。

在 GitHub 仓库中启用：

- Actions；
- Private vulnerability reporting；
- Dependabot alerts 与 security updates；
- secret scanning 和 push protection；
- GitHub Actions artifact attestations；
- `main` ruleset：要求 CI 和 CodeQL、禁止 force-push、至少一次审核；
- tag ruleset：保护 `v*`，只允许维护者创建或删除。

Release 工作流只授予 `contents: write`、`id-token: write` 和 `attestations: write`。其他工作流保持只读权限。

## 发布前检查

1. 确认 `main` 工作树干净，CI、CodeQL 和依赖检查全部通过。
2. 更新 `CHANGELOG.md`，把目标版本从 `Unreleased` 改为实际 UTC 日期。
3. 确认 `Directory.Build.props`、Windows 产品/信息版本、前端包版本和用户界面显示一致；Windows 四段文件版本可保持对应的数字版本（例如 `0.1.0.0`）。
4. 在两台干净的 Windows 10/11 x64 设备完成配对、断网回退、紧急快捷键、剪贴板、文件和远程桌面测试。
5. 对实验性音频和 90 FPS 分别记录实际 FPS、CPU、网络、声卡和失败结果；不能只记录成功案例。
6. 对目标显示器做 HDMI1↔DP、休眠唤醒和 DDC 通道消失测试。
7. 检查 ZIP 不含 PDB、开发配置、私钥、真实设置、日志或硬件测试数据，并包含 LICENSE、英文 `README.md`、中文 `README.zh-CN.md`、快速开始和第三方通知。
8. 使用历史扫描工具检查整个 Git 历史中没有密钥或身份文件。

## 首次公开快照

首次发布的 `v0.1.0-alpha.1` tag 必须指向无父提交的公开快照，且 commit author、committer 与 tagger 都使用 GitHub 隐私邮箱。**不要在保留本机开发历史的 `main` 上执行下面的 tag 命令，也不要对公开远端使用 `git push --all`、`git push --tags` 或 `--mirror`。** 先确认公开远端的 `main` 只有审计后的单个根提交，再为该提交创建 tag。

## 公开仓库建立后的后续发布

```powershell
git switch main
git pull --ff-only
$version = "0.1.0-alpha.3"
git tag -s "v$version" -m "DeskMesh $version"
git push origin "v$version"
```

如果维护者暂时没有可验证的 GPG/SSH 签名，可创建受保护的 annotated tag，但必须在发布记录中说明。tagger 也应使用 GitHub 隐私邮箱。不要为了让工作流通过而重用、移动或覆盖已有 tag。

`.github/workflows/release.yml` 会：

1. 验证 tag 与 `Directory.Build.props` 完全一致；
2. 在固定主版本的 Windows Server 2025、Node.js 22.13.0 和 `global.json` 指定的 .NET 10 环境运行全部检查；
3. 创建自包含 `win-x64` ZIP；
4. 生成 `SHA256SUMS.txt`；
5. 为 ZIP 创建 GitHub build provenance；
6. 创建标记为 prerelease 的未签名 Alpha Release。

工作流不会声称发布物已进行 Authenticode 签名。

## 验证发布物

下载者可运行：

```powershell
Get-FileHash .\DeskMesh-0.1.0-alpha.3-win-x64.zip -Algorithm SHA256
```

结果必须与 Release 中 `SHA256SUMS.txt` 一致。安装了 GitHub CLI 的用户还可验证 provenance：

```powershell
gh attestation verify .\DeskMesh-0.1.0-alpha.3-win-x64.zip --repo bz1121/DeskMesh
```

维护者应在一台未参与构建的 Windows 电脑下载 Release 资产、重复验证摘要、解压启动并完成最小配对测试后，再对外宣布发布。

## 后续代码签名

稳定版前应使用受信任的 Authenticode 证书或托管签名服务。签名步骤必须在打包和 SHA-256 计算之前完成，私钥不得存入仓库或普通 Actions secret。带可信时间戳的签名会改变二进制字节，因此官方签名包主要通过签名和 provenance 验证；可另行保留可复现的未签名构建用于源码核验。

## 撤回有问题的版本

- 不要替换同名 Release 资产或移动 tag。
- 立即将受影响 Release 标为有已知问题，并在安全公告或 Issue 中给出缓解措施。
- 修复后发布递增版本，例如 `0.1.0-alpha.4`。
- 若涉及安全问题，按 `SECURITY.md` 协调披露并在 Changelog 标出受影响范围。
