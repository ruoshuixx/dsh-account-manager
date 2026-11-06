# 发布流程

源码与发布都在本仓库（`ruoshuixx/dsh-account-manager`）。从 v0.2.1 起，每个版本的 Release 必须上传以下新资产及校验文件：

| 平台 | 资产 | 校验文件 | 程序内升级 |
|---|---|---|---|
| Windows x64 | `AIAccountManager-<version>-win-x64.exe` | `AIAccountManager-<version>-win-x64.exe.sha256` | 下载 → 校验 → 替换自身 → 重启 |
| Ubuntu / Debian amd64 | `AIAccountManager-<version>-amd64.deb` | `AIAccountManager-<version>-amd64.deb.sha256` | 下载 → 校验 → 管理员授权（polkit）→ `apt` 安装 → 重启 |

`<version>` 是去掉开头 `v` 的标签，例如 `v0.2.1` 对应 `0.2.1`。资产名由程序在本地按标签推算，不读取远程给出的名字。

每次发布还必须保留以下兼容副本及校验文件，供 v0.2.0 及更早的已安装版本更新；合计上传八个文件：

| 平台 | 兼容副本 | 校验文件 |
|---|---|---|
| Windows x64 | `DshAccountManager-win-x64.exe` | `DshAccountManager-win-x64.exe.sha256` |
| Ubuntu / Debian amd64 | `dsh-account-manager_<version>_amd64.deb` | `dsh-account-manager_<version>_amd64.deb.sha256` |

同平台的新旧文件内容相同，但每份 `.sha256` 必须使用 sha256sum 格式（`哈希值  文件名`），其中的文件名必须对应它旁边的资产。
`.deb` 内部的包名始终为 `dsh-account-manager`，以便 dpkg 升级同一个包；仅发布文件名改变。
不再发布便携 Linux 资产 `DshAccountManager-linux-x64`，其更新配置保持不变。

## 发新版本

1. 修改 `src/AccountManager.App/AccountManager.App.csproj` 的 `<Version>`，补 `docs/release-notes/v版本.md`。
2. 测试：`dotnet test AccountManager.sln -c Release`
3. 在 Windows 上生成 exe：`powershell -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1`
   脚本在 `release/v<version>/` 生成新名称与兼容名称的 exe，以及各自的 `.sha256`。
   （也可以在 Linux 上交叉编译：`dotnet publish … -r win-x64 --self-contained -p:PublishSingleFile=true`）。
4. 在 Ubuntu 上生成 deb（以下以 v0.2.1 为例）：
   ```bash
   scripts/Publish-Linux.sh 0.2.1 /tmp/dam-publish
   scripts/Build-Deb.sh /tmp/dam-publish /tmp/dam-deb 0.2.1
   ```
   `Build-Deb.sh` 保留原来的包文件，并额外生成新名称的副本和各自的校验文件；`/tmp/dam-publish` 中的便携文件不上传。
5. 将 Windows 产物放在当前仓库的 `release/v0.2.1/`，从仓库根目录创建 Release 并上传全部八个文件：
   ```bash
   gh release create v0.2.1 --title "AI Account Manager v0.2.1" --notes-file docs/release-notes/v0.2.1.md \
       release/v0.2.1/AIAccountManager-0.2.1-win-x64.exe \
       release/v0.2.1/AIAccountManager-0.2.1-win-x64.exe.sha256 \
       /tmp/dam-deb/AIAccountManager-0.2.1-amd64.deb \
       /tmp/dam-deb/AIAccountManager-0.2.1-amd64.deb.sha256 \
       release/v0.2.1/DshAccountManager-win-x64.exe \
       release/v0.2.1/DshAccountManager-win-x64.exe.sha256 \
       /tmp/dam-deb/dsh-account-manager_0.2.1_amd64.deb \
       /tmp/dam-deb/dsh-account-manager_0.2.1_amd64.deb.sha256
   ```
   GitHub 会自动附带该标签的 Source code 压缩包。不要使用预发布标记（程序只认正式版）。

不要打包运行过的程序目录，它可能含个人设置和日志。发布资产只放编译产物和校验文件。

## 自动构建（可选）

`docs/ci/release.yml` 是一份现成的 GitHub Actions 工作流：推送 `v*` 标签（或手动触发）时测试并构建新旧名称的 exe、deb 及各自的校验文件，
保存为 Actions 产物；如果该标签的 Release 里还没有对应资产，就补传缺失的文件，已有的资产不会被覆盖。

启用方法：把它复制到 `.github/workflows/release.yml` 再提交。推送工作流文件需要带 `workflow` 权限的令牌
（`gh auth refresh -s workflow`）。

## 程序中的更新流程

更新检查只在主界面或托盘手动触发，启动时不联网检查。发现新版后确认一次，显示下载百分比和 MB 进度。

- 当匿名 API 返回 403 / 429 时，改从公开 Release 页面的 latest 重定向识别稳定版本。
- 程序核对发布地址、文件长度和 SHA-256；Windows 还校验 PE 类型，deb 校验包头。
- 失败时停止更新并保留旧版本；Windows 尽力恢复旧 exe，deb 在授权被取消或安装失败时重新启动旧版并在下次启动提示原因。
- 开发运行和不满足条件的安装方式只提供下载页面。

### 各平台的实现约束

- Windows：单文件 exe 把自己改名为同目录唯一的 `.old-<随机值>.exe`，放入新版并启动，新版握手成功后清理旧文件。目录不可写时提示错误，不自动提权。
- Ubuntu .deb：程序在 root 所有的 `/opt/dsh-account-manager`，不能替换自身。下载完成后程序退出，
  分离的脚本 `apply-update.sh`（用户身份）通过 `pkexec` 调用 root 辅助脚本 `install-update`；后者重新计算安装包的 SHA-256，
  核对包名、架构，并要求版本比已安装的新，再用 `apt-get install` 安装，最后启动程序。
- Ubuntu 便携版（`DshAccountManager-linux-x64` 单文件，装在用户可写目录）：沿用与 Windows 相同的“替换自身”流程。
