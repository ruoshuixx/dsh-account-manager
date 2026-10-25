# DSH 账号管理（DSH Account Manager）

给 DeepSeek Harness（**dsh**）用的桌面小工具：切换 dsh 内置的 Codex（ChatGPT）账号、管理 API 供应商、查询额度、导入导出账号文件。
**同一套 C# / Avalonia 代码，在 Windows 与 Ubuntu 桌面上运行**，界面完全一致。

- 技术栈：.NET 8 + Avalonia 11。
- 许可证：[MIT](LICENSE)。
- 下载：[GitHub Releases](https://github.com/ruoshuixx/dsh-account-manager/releases/latest)（Windows 单文件 exe；Ubuntu / Debian 的 .deb 安装包）。

> **声明**：这是个人开发的非官方工具，与 DeepSeek、OpenAI 没有任何关联。它只改它自己负责的那一小部分 dsh 配置；
> 第三方插件（例如 `dsh-subscriptions`）的数据一律不读不写。账号令牌和 API Key 是敏感信息，请自行妥善保管导出的文件。

## 它能做什么

- **切换 dsh 内置的 Codex 账号**：dsh 同一时间只认一个 Codex 账号。本工具把你所有的 Codex 账号保存在自己的加密账号库里，
  点「切号」就把目标账号写成 dsh 当前的 Codex 凭据（dsh 运行中会热加载，不用重启）。
  - 切号前，如果 dsh 已经刷新了当前账号的令牌，会先把**较新**的令牌写回账号库，绝不用旧令牌覆盖新令牌。
  - dsh 里原本登录的、账号库里还没有的账号，会被自动收进账号库并显示为当前账号。
  - 在一个还没有 Codex 路由的档案里切号时，会顺带启用内置的 `openai-codex` 路由并写入官方现行 Codex 模型（`gpt-6.1-sol`、`gpt-6-astra`、`gpt-6-luna`，只写 id；已有的条目不动；只有“完全是空的”条目（0.1.2 写入的 `openai-codex: {}`，没有任何用户选择）会在导入或切号时补全成这份列表，补全前先做加密备份；只要条目里有任何一个键就保持原样）。
- **管理 API 供应商**：读写档案里的 `cordis.patch.yml`（`llm-pi-ai` 的 `config.providers`）和凭据文件里的 Key 引用，
  支持新增、编辑（名称 / 地址 / 模型 / Chat Completions 或 Responses 协议 / 换 Key）、删除。只替换被修改的那个供应商的文本。
- **查询额度**：只读 `GET https://chatgpt.com/backend-api/wham/usage`，显示 5 小时与本周期剩余；可设置自动刷新，也可开启“额度快用完时自动切号”。
- **导入 / 导出 Cockpit 格式 JSON**：解析 Cockpit 导出的账号数组（Codex 账号与 API Key 账号），导入前先做加密备份，导入不会切换当前账号；
  导出是**明文** JSON，默认不覆盖已有文件，文件名 `dsh_yyyy-MM-dd_HH-mm-ss.json`；也可以通过 WebDAV 上传 / 下载。
  **WebDAV 同步**（设置里填地址和应用密码，手动上传 / 下载）：云端目录是 `/AccountManager/`，这个目录是中立的，写同样 Cockpit 格式 JSON 的工具可以共用；
  文件名前缀（`dsh_…`）用来区分是谁上传的。上传总是写进 `/AccountManager/`；下载时取该目录里**最新的一个 `.json`**（不看前缀，这就是共享），
  下载的文件一律先进入导入预览，勾选并“确认导入”后才会写入本机。只有 `/AccountManager/` 不存在或里面没有 `.json` 时，才会去看旧版本用的目录 `/DshAccountManager/`
  并取其中最新的文件（确认框和状态栏会写明实际用的是哪个目录）；旧目录只读取，不会写入、删除或创建。
- **引用保护与修复**：dsh 的设置里有些地方按“供应商 + 模型”点名路由（默认模型、子代理可用模型等）。
  删除供应商或去掉它的模型前，本工具会列出仍在引用它的设置，让你选择“改用其他供应商 / 模型”或“连同引用一起删除（仅列表项）”；
  删除后状态栏有“撤销”（从删除前的加密备份恢复，原 ID、Key 引用和 Key 都在）。每次读取账号后还会自检，发现失效引用时状态栏出现“修复”入口。
  导入 API 账号时，按接口地址 + Key 匹配 dsh 里已有的供应商并更新它，不会再新建重复的供应商。
- **让主代理能把任务委派给你的模型**：dsh 只允许子代理使用 `subagent-model-selection-settings`（`cordis.patch.yml`）里 `enabled: true` 且列在 `allowedModels` 里的“供应商 + 模型”。
  每次**导入**成功后，以及切号时**首次启用内置 Codex 路由**时，本工具都会把候选路由合并进这一项：你档案里每个供应商列出的模型；Codex 则是你在 `openai-codex` 下明确列出的模型（本工具启用路由时写入的就是官方现行列表），
  没有列出时用官方当前的 `gpt-6.1-sol`、`gpt-6-astra`、`gpt-6-luna`（能找到 dsh 自带的模型目录时，只保留目录里有的；不会加入 gpt-6-sol、gpt-5.6 系列或已下线的 gpt-5.5 / gpt-5.3-codex-spark）。
  你自己加的条目不会被改动或删除，也不会写入重复项；写入前先加密备份并在锁内回读校验。结果只出现在导入结果和运行日志里。
  **改动只对 dsh 里的新对话生效**：请在 dsh 中新建对话后，再让主代理调用子代理。
- **Codex Fast 模式（不需要任何第三方插件）**：dsh 本身没有“服务档位”设置，所以本工具自带一个只有几十行的小插件 `dsh-account-manager-fast`（MIT，源码在 `plugins/dsh-account-manager-fast/`，已打包进程序）。
  每次**导入**成功后（以及切号时首次启用 Codex 路由时），如果 `openai-codex` 路由里有 `gpt-6.1-sol`、`gpt-6-astra`、`gpt-6-luna`，本工具会：① 把这个插件装进 dsh 档案（`<DSH_HOME>/profiles/<档案>/node_modules/dsh-account-manager-fast/`，并把它的名字追加到档案 `package.json` 的 `dsh.profile.bundles`；不需要 dsh 命令行、pnpm 或联网，写之前先加密备份 `package.json`，其余字节一个不动）；② 在路由里补上 `gpt-6.1-sol-fast`、`gpt-6-astra-fast`、`gpt-6-luna-fast`（名字是“… (Fast)”，上下文、输出上限、输入类型和推理档位照搬基础模型）。
  选这些 `-fast` 模型时，请求会以基础模型发出并带上 `service_tier: "priority"`（Codex 的 Fast 模式）；其他请求不受影响。不会改默认模型，也不会把 `-fast` 模型交给子代理候选。
  **首次安装需要完全退出 DSH 再打开**，Fast 模型才可用（dsh 只在启动时读取插件列表）。**Fast 按官方规则以 2.5 倍消耗订阅额度。**
  只在组件装好并列在档案里时才会有 `-fast` 模型；组件缺失或装不上时，本工具会把它自己写入的 `-fast` 模型再移除（避免把 `-fast` 模型名原样发给 OpenAI），你自己写的条目不动。档案还没有 `package.json`（dsh 从没启动过）或它的格式不认识时会拒绝安装并说明原因。
  手动移除：删除 `<DSH_HOME>/profiles/<档案>/node_modules/dsh-account-manager-fast/` 文件夹，从 `package.json` 的 `dsh.profile.bundles` 里去掉 `"dsh-account-manager-fast"`，再删掉路由里的 `*-fast` 模型，重启 dsh。
- **加密账号库与备份**：账号保存在加密文件里（Windows 用 DPAPI，Linux 用 AES-GCM + 0600 密钥文件）；每次写入前都先做加密备份。
- **其他**：添加账号（OpenAI OAuth 浏览器授权 / 添加 API）、托盘常驻、多选、已脱敏的运行日志、程序内升级。

## 平台与目标客户端

| 平台 | dsh 客户端 | DSH 主目录 | 配置档案 |
|---|---|---|---|
| Windows | 官方 DeepSeek Harness 桌面版 | `%USERPROFILE%\.dsh`（或环境变量 `DSH_HOME`） | `profiles/desktop` |
| Ubuntu | dsh-tauri | `~/.dsh`（或环境变量 `DSH_HOME`） | `profiles/tauri` |

配置档案自动检测：依次取 `desktop`，`tauri`，再取 `profiles/` 下按名称排序的第一个其它目录；也可以在「设置」里手动指定 **DSH 主目录** 和 **配置档案**（留空 = 自动）。
Ubuntu 版在 Ubuntu 桌面上使用过；Windows 版已在真机上验证过导入和切号，其它场景欢迎反馈。

## 下载与安装

在 [Releases](https://github.com/ruoshuixx/dsh-account-manager/releases/latest) 下载，每个文件旁边都有同名的 `.sha256` 校验文件：

| 平台 | 文件 |
|---|---|
| Windows x64 | `DshAccountManager-win-x64.exe`（单文件，自带 .NET 运行时，放在可写目录里双击运行） |
| Ubuntu / Debian amd64 | `dsh-account-manager_<版本>_amd64.deb` |

校验：`sha256sum -c dsh-account-manager_<版本>_amd64.deb.sha256`（在文件所在目录执行）。

Ubuntu：

```bash
sudo apt install ./dsh-account-manager_<版本>_amd64.deb     # 安装；之后在“应用程序”列表里能找到，也可以命令行运行 dsh-account-manager
sudo apt remove dsh-account-manager                          # 卸载
```

程序装在 `/opt/dsh-account-manager`。卸载不会删除你的账号库、设置和备份（见下面的数据位置），需要的话自己删。

### 程序内升级

在主界面或托盘手动点“检查更新”（启动时不会自动联网检查）。发现新版后确认一次，程序下载对应资产、校验 SHA-256，然后：

- Windows：替换自身并重启。
- Ubuntu（.deb 安装）：下载完成后退出，弹出系统的管理员授权窗口（polkit），自动安装新 .deb 并启动新版；授权被取消或安装失败时仍启动旧版，并在下次启动时提示原因。

检查更新只访问公开的 GitHub Release，不需要 GitHub 账号或令牌；预发布版本不会被当作更新。

## 数据放在哪里

本工具只改自己负责的那一小段，不碰 dsh 的其它内容（其它工具写的凭据、注释、未知键逐字节保留）：

| 内容 | 位置 |
|---|---|
| dsh 当前的 Codex 账号 | `$DSH_HOME/.credentials.yaml` 的 `records["llm-pi-ai/openai-codex"]`（`kind: grant`） |
| API Key | 同一文件的 `refs.<名称>`（名称 = 供应商的 `apiKeyEnv`；新建供应商用 `<供应商ID大写>_API_KEY`） |
| API 供应商 | `$DSH_HOME/profiles/<档案>/cordis.patch.yml` 里 `llm-pi-ai` 的 `config.providers.<ID>` |
| 全部 Codex 账号（多账号） | 加密账号库：Linux `~/.config/DshAccountManager/accounts.vault`，Windows `%LOCALAPPDATA%\DshAccountManager\accounts.vault` |
| 导入的 Cockpit 原始字段缓存 | 账号库同目录的 `account-manager-state.json`（敏感字段已密封） |
| 加密备份 | Linux `~/.local/share/DshAccountManager/Backups`，Windows `%LOCALAPPDATA%\DshAccountManager\Backups` |
| 设置和日志 | 程序所在目录（可写时），否则 Linux `~/.config/DshAccountManager/`、Windows `%LOCALAPPDATA%\DshAccountManager\`；日志在其中的 `logs/` |

写入与 dsh 自己的文件写入协议兼容（同目录 `<文件>.lock` 锁、临时文件 + 重命名、权限 0600 / 目录 0700，并在锁内重新读取再改自己的键），
所以可以在 dsh 运行时使用。格式不符合 dsh 要求的文件（例如没有 `version: 1` 的旧布局）会被拒绝改写，并给出提示。
详细设计见 [docs/ARCHITECTURE-DSH.md](docs/ARCHITECTURE-DSH.md)。

> **关于第三方插件 `dsh-subscriptions`**：它注册的是自己的 `codex` 路由和账号池（自动轮换），本工具切换的是 dsh 内置的 `openai-codex`。
> 如果 dsh 的默认模型选的是插件的路由，本工具切号不会影响正在用的账号；想让“切哪个就用哪个”，请把默认模型改回 `openai-codex`。本工具不读写该插件的数据。

## 安全边界

- 所有写入（添加 / 删除 / API 保存 / 导入 / 修复引用）先做加密备份；删除后会回读确认。
- 令牌和 Key 不会出现在界面、日志或错误信息里；导出的 JSON 是明文，仅在你明确选择导出时生成。
- 切号只改写 dsh 的当前 Codex 凭据，不重启 dsh，也不会自动刷新登录；令牌过期的账号需要重新授权。

## 添加 Codex 账号（浏览器授权）

1. 「添加账号」→「OpenAI OAuth」→「在浏览器中打开」，在 OpenAI 官方页面登录并授权。
2. 正常情况下自动接收本机回调；没有自动完成时，粘贴浏览器地址栏里完整的 `http://localhost:端口/auth/callback?code=...&state=...` 地址，点击「完成授权」。
3. 核对显示的邮箱和工作区，点击「保存账号」。已存在的账号需要确认后才会更新。

授权使用 PKCE 与随机 state，只监听 `127.0.0.1`（优先 1455，被占用时用 1457），10 分钟超时；关闭窗口或切到 API 选项卡会取消授权并释放端口。
添加不会切换 dsh 已有的当前账号；dsh 还没有当前 Codex 账号时，新账号会成为当前账号。

## 从源码构建

需要 .NET 8 SDK。

```bash
dotnet run --project src/AccountManager.App                  # 开发运行
dotnet test AccountManager.sln -c Release                    # 测试
```

Windows 单文件 exe（也可以在 Linux 上交叉编译）：

```bash
dotnet publish src/AccountManager.App/AccountManager.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/win-x64
```

Ubuntu .deb：

```bash
scripts/Publish-Linux.sh <版本> /tmp/dam-publish
scripts/Build-Deb.sh /tmp/dam-publish /tmp/dam-deb <版本>    # 产出 dsh-account-manager_<版本>_amd64.deb 和 .sha256
```

发布流程见 [docs/RELEASING.md](docs/RELEASING.md)。

## 项目结构

```
src/AccountManager.Core/     业务核心（无 UI 依赖）
  Dsh/                       dsh 存储层与后端：DshHome、文件锁与原子写、.credentials.yaml / cordis.patch.yml 编辑、账号库、模型引用扫描与修复
  Accounts/ Quota/ Actions/  账号映射与切换、额度、添加 / 删除 / 编辑
  Import/ Export/ Sync/      Cockpit 格式 JSON 导入导出、WebDAV
  Security/ Updates/         加密与备份、程序内升级
src/AccountManager.App/      Avalonia 界面（MVVM）
tests/                       单元测试；与 dsh 的互操作测试在本机装有 dsh 和 node 时运行，否则自动跳过
```

测试全部使用临时的 `DSH_HOME` 和应用数据目录，不会触碰真实的 `~/.dsh`。

## 其他说明

- 界面只有一套 XAML，两个平台共用。Linux 使用已安装的系统字体（优先黑体 / 微软雅黑 / Noto Sans CJK SC / 文泉驿），可用环境变量 `DAM_UI_FONT` 指定字体族。
- 界面跟随系统缩放；可用区域放不下设计最小尺寸（1000×560）时整体按比例缩小，需要固定倍数时可设置 `AVALONIA_GLOBAL_SCALE_FACTOR`。
- OAuth 授权和额度查询沿用系统的代理设置（Windows 系统代理、Linux 的 `http_proxy` 等环境变量）。

## 许可证

[MIT](LICENSE)
