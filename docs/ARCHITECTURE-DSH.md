# DSH 账号管理器 — 后端架构

这里记录 dsh 后端的结构与映射决策：账号、供应商如何落在 dsh 的文件里，以及上层业务（映射、导入导出、额度、切号、删除）如何通过一个统一的后端接口使用它们。
C# 命名空间仍是 `AccountManager.*`。

## 1. 分层

```
App（Avalonia / MVVM）
  AppServices                组合根：DshHome + AccountVault + DshBackend + 各服务
Core 上层服务（几乎原样）
  AccountSnapshotService     读取 → 账号行
  SwitchService              切号 + 回读校验
  AccountActions(+Add)       删除 / API 编辑 / 添加（改写，见 §5）
  ImportService / ExportService
  AccountQuotaSelector / AccountResetService …
        │  只依赖 IAccountBackend（统一的凭据 JSON 形态）
Core/Dsh
  IAccountBackend ──► DshBackend   适配层：vault + grant + 供应商 ⇄ 凭据 JSON
                       ├─ AccountVault            加密账号库（Cockpit 形态记录）
                       ├─ VaultCredentialMapper   记录 ⇄ 凭据；grant 载荷；当前账号判定
                       ├─ ProviderViewMapper      dsh 供应商条目 ⇄ “供应商视图”
                       ├─ CredentialsDocument     .credentials.yaml 严格读写（按文本区间原位编辑）
                       ├─ ProfilePatchDocument    cordis.patch.yml（只改 llm-pi-ai 的 config.providers.<ID>）
                       ├─ DshFileLock / DshAtomicWrite   与 @deepseek-ai/dsh-atomic-write 协议兼容
                       ├─ DshHome                 DSH_HOME 与档案检测
                       └─ DshRuntimeVerifier      dsh --profile <p> --dump-config 校验
```

## 2. 凭据 JSON 形态（上层看到的）

`GetCredentialsAsync` 返回统一的凭据 JSON 形态（与 Cockpit 账号一一对应），所以 `AccountMapper`、导入导出、额度选择、自动切号无需理解 dsh：

* Codex：`{ id, integrationID:"openai", label, active, value:{ type:"oauth", methodID:"chatgpt-browser", access, refresh, expires, metadata:{ accountID, cockpitEmail, id_token, planType, subscriptionActiveUntil, created_at, last_used } } }`
* API：`{ id:"key_<供应商ID>", integrationID:<供应商ID>, label, active:true, value:{ type:"key", key, metadata:{ cockpitID? } } }`，只对“有 `apiKeyEnv` 且 refs 里有值”的受管供应商出现。

供应商配置由 `GetConfigEntriesAsync` 以单个 `{type:"file", path, info:{providers}}` 返回，其中 `providers.<ID>` 是**供应商视图**：
`{ name, package, settings:{baseURL}, models:{ <id>:{ name, variants? } }, "x-dsh":{…} }`。

## 3. 映射决策

| 项 | 决策 |
|---|---|
| 账号 ID | 账号库记录的 `id`：导入/授权新建时为 `codex_<32 hex>`（调用方也可指定）；Cockpit 原始 `id` 另存于本地镜像，导出时合并回去 |
| API 凭据 ID | `key_<供应商ID>`（每个供应商至多一个 Key）；供应商 ID：UI 新建为 `api-<32 hex>`，Cockpit 导入为 `cockpit-<sha256(源ID)前12位>`，dsh 里已有的保持原名 |
| 当前账号（active） | `records["llm-pi-ai/openai-codex"]` 的 grant 对应的账号库记录。判定顺序：refresh 令牌相同 → access 令牌相同 → 唯一的同 `accountId` → 多个同 `accountId` 时按 access JWT 里的邮箱区分；仍无法判定则视为“账号库里没有” |
| dsh 自己登录的账号 | 若 grant 不属于任何账号库记录（用 dsh 自己登录的），读取时**自动收进账号库**（只写账号库，不改 dsh 文件），因此一定显示、可切回、不会被切号覆盖丢失 |
| grant 载荷 | **恰好** `{type:"oauth", access, refresh, expires:<毫秒整数>, accountId}`；`expires` 缺省时取 access JWT 的 `exp`×1000 |
| 令牌新旧 | 比较 `expires`：grant 比账号库记录新 → 展示/导出用 grant 的令牌；切号前写回账号库；账号库更新则不被旧 grant 覆盖 |
| Key 引用名（refs） | 保留已有的 `apiKeyEnv`；新供应商用 `<供应商ID大写，非 [A-Z0-9_] 换成 _>_API_KEY`，与已有 refs / 其它供应商冲突则加 `_2`、`_3`…；多个供应商共享同一引用时，给被换 Key 的那个新建独立引用，不改动另一个；删除供应商只在没有别的供应商使用时才删它的引用 |
| 协议 | Chat → `api: openai-completions`；Responses → `api: openai-responses`（视图的 `package` 即这两个字符串）。只有这两种且有 `baseURL` 的供应商是“受管”的，`openai-codex` 内置路由和 anthropic 等其它协议的条目不列出、不修改 |
| 推理档位 | 视图的 `variants:[{id, body.reasoning.effort}]` ⇄ 模型的 `reasoningEfforts:{ <级别>: <线上取值> }`；级别取自 dsh 的 `off/minimal/low/medium/high/xhigh/max`，`off:`（空值）对应 `{id:"off"}`；`reasoningEfforts: false` 等不能表达为 variants 的内容放在模型的 `x-dsh` 里原样带回 |
| 视图里放不下的字段 | 供应商级（`apiKeyEnv`、headers、compat、transport、defaultContextWindow …）与模型级（`contextWindow`、`maxTokens`、`input`、`compat` …）统一放 `x-dsh`，写回时原样合并，所以编辑不会丢 dsh 认识的字段 |
| Cockpit API 账号标识 | 用本地镜像把 `cockpit-<hash>` 反查回 Cockpit 源 ID，作为凭据 `metadata.cockpitID`（导入去重、导出标识用） |
| 镜像 / 订阅缓存 | `account-manager-state.json` 放在账号库同一应用数据目录（`SubscriptionStore(directory)`，可注入） |
| 备份 | 写入前沿用 `BackupService`（加密）：凭据列表 + 供应商文件原文 |

## 4. 写入语义

* **切号**（`ActivateCredentialAsync`）：在**同一个** `CredentialsDocument.Update`（30 s 锁，锁内重读）里 ① 若 dsh 已刷新了当前账号的令牌（grant 的 `expires` 更晚），先把令牌写回账号库记录；若当前 grant 不属于任何记录则先收进账号库；② `SetGrant` 目标账号；之后重新从磁盘读回校验，`SwitchService` 再校验“目标唯一当前”。
* **删除 Codex**：账号库删除；若它是当前账号，同一次更新里 `RemoveRecord(grant)`。结果的 `Warning` 说明“dsh 在切换前没有可用的 Codex 账号”。
* **创建 Codex**：只写账号库；`activate=true`，或 dsh 当前没有任何 grant 时，才随后执行切号（“第一个账号成为当前账号”）。
* **API 供应商写入**：先写 Key 引用（`SetRef`），再写 `cordis.patch.yml` 里该供应商的文本；后者失败则回滚引用。读回校验 `api`、`baseURL`。
* **文本级编辑**：`.credentials.yaml` 与 `cordis.patch.yml` 都用 YamlDotNet 的节点区间定位，只替换被改条目的文本，其它字节（他人的 refs/records、注释、未知键、其它插件、其它供应商）逐字节不变；流式/空的父节点才按其内容重新渲染。写入前后都做严格解析，无法解析（含没有 `version: 1` 的旧布局）则拒绝写入并报错。
* **并发**：所有写入走 `DshFileLock`（`<文件>.lock`，`wx` 独占创建，内容 `"<pid>\n"`，指数退避 20→200 ms，持有者进程已退出可接管）+ `DshAtomicWrite`（同目录随机临时文件 `<文件>.<12 hex>.tmp` → 重命名），与 dsh 自己的写入互相等待，已用真实 dsh 的 `withFileLock` 做过双向互操作测试。

## 5. 上层服务

* `AccountActions`：删除、API 供应商编辑、添加账号都是“备份 → 写入 → 回读校验”；多步骤的一致性由文件锁 + 锁内重读保证。
* **删除当前 Codex 账号是允许的**，并会提示（UI 确认框也会说明）“dsh 将没有可用的 Codex 账号”。
* `ImportService`：API 行先合并出最终供应商视图，再 `WriteApiProviderAsync`；Codex 行是“先建后删”（两次账号库写入）。
* `AccountSnapshotService` / `ExportService`：直接读后端当前的文件内容。
* `OpenAiApiProtocol` 的两个值（`openai-completions`、`openai-responses`）就是 dsh 的 `api` 字段；`ModelVariantBuilder`、`ModelReasoningCatalog` 负责推理档位。

## 6. 运行时校验

`DshRuntimeVerifier` 找到 `dsh` 命令（Linux `~/.local/bin/dsh` 与 PATH；Windows PATH 上的 `dsh.cmd/.exe`），以 `DSH_HOME=<主目录>` 运行 `dsh --profile <档案> --dump-config`（20 s 超时，只读），解析输出里 `llm-pi-ai` 的 `config.providers.<ID>.api`：一致 = 已识别；不一致或没出现 = 待确认；找不到 dsh / 失败 / 超时 = “已写入，未能校验”（警告，不是错误）。
注意：Ubuntu 上 dsh-tauri 生成的 `~/.local/bin/dsh` 脚本会把 `DSH_HOME` 固定为 `~/.dsh`，因此主目录不是 `~/.dsh` 时校验会看不到供应商，只会得到“待确认”提示。

## 7. 模型引用（provider + model）的安全

dsh 的设置里有些地方按 `provider` + `model` 点名某个 LLM 路由（例如 `agent-default-model` 的默认模型、`subagent-model-selection-settings` 的子代理模型列表）。删掉供应商后这些引用会失效。本工具为此做了：

* **扫描（`ReferenceScanner`）**：引用 = 任何同时有字符串 `provider` 和字符串 `model` 的映射（只有 `provider: spawn` 这类没有 `model` 的不算）。来源有两个：用户层 `cordis.patch.yml`（按源码位置精确定位，**可修改**；区分列表项与单个值、能否移除）和 `dsh --profile <p> --dump-config` 的合成结果（覆盖所有插件/bundle 层，**只读**，用 `YamlStream` 解析以容忍 `!!js` 等自定义标签）。dsh 跑不起来时只读用户层并提示“可能有未检查到的引用”。
* **什么叫“已知的供应商”（失效判定规则）**——证据来自 dsh 程序文件：
  * `dsh-llm-pi-ai` 默认挂载时**没有任何路由**（`dsh-base/cordis.patch.yml` 注释：“mounted dormant: zero routes … until a `llm-pi-ai:` settings section supplies provider profiles”），注册代码是 `routes = [...profiles().keys()]` + `ctx.llm.registerAdapter(routes, adapter)`。所以 llm-pi-ai 的路由 = 合成配置里 `config.providers` 的键；`openai-codex` 这类 pi-ai 目录里的 id **只有被配置了才存在**，不是内置的。
  * 其余适配器只有两个：`dsh-llm-deepseek-api-key`（路由常量 `deepseek-official`）和 `dsh-llm-deepseek-account`（`deepseek-account`）。出现这两个插件条目就认为这两个路由存在（离线时也默认存在）。
  * 合成配置里若出现本工具不认识的 LLM 适配器插件（`@deepseek-ai/dsh-llm-*` 以外名单之外的，或第三方名字含 `llm-`），就无法枚举路由——此时**一律不报失效**，并提示原因。
  * 引用的供应商不在上述集合里就是“失效引用”。
* **编辑（`ProfilePatchDocument.RetargetReference/RemoveReference`）**：只替换引用里的 `provider`/`model` 两个标量，或移除列表项所在的行（最后一项移除后列表写成 `[]`）；改之前核对当前值与扫描时一致，不一致就拒绝；其余字节（注释、其它条目）不变。改指向后若同一列表出现与别的项完全相同的一项，只会去掉**本次改指向的那一项**。
* **删除/去模型守卫**：`AccountActions` 在删除供应商或保存时去掉模型之前扫描引用；有引用且没给处理方式就抛 `ReferencesInUseException`（绝不悄悄留下失效引用）。处理方式 = 全部改指向某个供应商+模型，或（仅当所有可修改的引用都是列表项时）连列表项一起移除；dsh 内置层的引用无法修改，界面会列出并说明。
* **撤销**：删除前已有加密备份（凭据 + `cordis.patch.yml` 原文）。`ReferenceService.RestoreProviderAsync` 按备份把供应商原样写回（同 ID、同 `apiKeyEnv`、同模型、同 Key）；已存在同 ID 的供应商则拒绝。界面在删除后显示“撤销”，修复对话框里也有“从备份恢复”。
* **健康检查**：每次读取账号后后台扫描；有失效引用时状态栏出现“dsh 设置中有 N 处引用了不存在的供应商 · 修复”。修复对话框对每处给出：改指向提供**同名模型**的供应商（恰好一个时自动选中）、从备份恢复、移除（仅列表项）、改选其它供应商和模型。应用前先备份，写入在锁内进行，之后重新读取。
* **导入**：API 账号按“规范化后的 baseURL（主机名大小写、末尾 `/` 不计）+ 解析出的 Key”匹配 dsh 里已有的供应商，匹配上就更新它（保留 ID、`apiKeyEnv`、名称、协议，只补缺失的模型），不再新建重复的 `cockpit-*`；账号自带的供应商 ID 等于已有或“被引用但已不存在”的 ID 时同样按该 ID 更新/恢复。预览里列出“将修复：…”（默认勾选，可取消），导入写入后才应用；导入结束再次扫描并报告仍然失效的引用。预览只为“导入账号提供了该模型且只有一个候选”的引用生成修复，不替用户在多个候选里猜。
* **日志**：切号记录“切换到 <邮箱>（<工作区后 8 位>）”和令牌写回；删除/修复/恢复也有记录；令牌经 `SecretRegistry` 脱敏，不会出现在日志里。

## 8. 子代理可用模型

* dsh 的 `@deepseek-ai/dsh-tool-subagent`：子代理要选择别的供应商/模型，宿主设置项 `subagent-model-selection-settings`（`@deepseek-ai/dsh-tool-subagent/model-selection-settings`，由 dsh-web-app bundle 声明）必须 `enabled: true` 且 `allowedModels: [{provider, model}, …]` 非空（精确路由，重复或“启用但为空”都会报错）。策略在**新的顶层会话**组装时采样一次，子代理继承；已有会话保持旧策略，所以改完要新建对话。
* `ProfilePatchDocument.GetSubagentSelection / AddSubagentRoutes`：只改该条目的 `config.enabled` 和 `config.allowedModels`——条目不存在时在用户层末尾追加（不写 `name:`，dsh 按 id 合并）；已存在时只把新项追加到列表末尾、必要时把 `enabled` 的值改成 `true`，其余字节、注释、未知键不动。行内（flow）格式、非列表、非 provider/model 项、已有重复项一律拒绝并报 `DshException`，不重写。重复项和“启用但为空”永远不会写出。
* `SubagentRouteService.ComputeCandidates`：用户层每个供应商 × 它列出的模型（不带 `text` 输入的模型跳过）；Codex 路由存在时：`openai-codex.models` 明确列出的就用这些；否则取官方当前列表 `OfficialCodexModels`（`gpt-6.1-sol`、`gpt-6-astra`、`gpt-6-luna`，由用户选定的三个；来源 OpenAI Codex 模型页 https://learn.chatgpt.com/docs/models，2026-10-20；集中在一个常量里便于更新），若能找到 dsh 自带的 pi-ai 模型目录（`@earendil-works/pi-ai/dist/providers/data/openai-codex.json`，只取 `type: chat`）就只保留目录里也有的（空路由时 dsh 只能运行目录里的模型）；找不到目录就原样使用。不会加入 gpt-6-sol、gpt-5.6 系列或已下线的 gpt-5.5 / gpt-5.3-codex-spark。
* 目录查找（`CodexCatalog`，只读、有界）：`dsh` 启动脚本里 `DSH_BIN='…'` 指向的 `node_modules`；Linux 的 `~/.local/share/dsh-tauri/dependencies/dsh/node_modules`；Windows 上额外在 `%LOCALAPPDATA%\Programs`、`%ProgramFiles%`、`%LOCALAPPDATA%`、`%APPDATA%` 下名字含 deepseek / dsh 的文件夹里向下找（深度 ≤ 6、不进入 `node_modules`、支持 pnpm 的 `.pnpm`）。官方桌面版的实际安装位置和是否把依赖打进 asar 尚未验证，找不到时按官方列表原样使用。
* `SubagentRouteService.Merge`：加密备份 → 在 `cordis.patch.yml` 的锁内用最新内容重算候选并合并 → 回读校验。触发点：每次导入成功后（`ImportService`），以及 `EnsureCodexRoute` 首次写入 `openai-codex` 条目时（仅当用户层还没有该条目；写入的是带 `models:` 的条目，列出官方现行 Codex 模型 `OfficialCodexModels` 的 id，dsh 按自带目录补全 name/上下文等，目录里没有的 id 由路由的 api/baseURL 兜底；已有条目只要有任何一个键（models、modelOverrides、displayName……）就逐字节不动；完全空的条目（`{}` 或只有键名没有内容，即 0.1.2 写入的）没有任何用户选择，由 `SubagentRouteService.UpgradeEmptyCodexRoute` 补全成同样的列表：先加密备份，在锁内再确认仍为空，回读校验并写日志；`Merge`（每次导入）开头和 `EnsureCodexRoute` 都会调用它。这样 dsh 的模型选择器只显示这几个，不会出现目录里已下线的 gpt-5.5 / gpt-5.3-codex-spark）。没有新增时不写、不备份。结果只写进导入结果和日志，并附一行“改动只对 dsh 里的新对话生效”。

## 9. Codex Fast 模式

* 目标：不装第三方插件，让 dsh 里的 Codex 模型能用 Fast（官方：ChatGPT 登录下 GPT-6.1 Sol / GPT-6 Astra / GPT-6 Luna，按 2.5 倍消耗订阅额度，Codex 用 `service_tier`）。dsh 的路由 / 模型 / compat schema 没有任何服务档位字段，传输层只在 pi-ai 的 `openai-codex-responses` 里读 `options.serviceTier`，而 `buildBaseOptions` 会丢掉它，唯一能到达请求体的是 `onPayload`（WebSocket 和 HTTP 两种传输都在发送前调用它）。所以必须有一小段代码在 dsh 进程里跑。
* 组件 `plugins/dsh-account-manager-fast/`（包名 `dsh-account-manager-fast`，随程序以 EmbeddedResource 打包，`FastComponentInstaller.EmbeddedFiles`）：包装 `@deepseek-ai/dsh-llm-pi-ai` 公开导出的 `PiAiAdapter.prototype.streamWithSnapshot`。对 `provider === openai-codex` 且模型 id 以 `-fast` 结尾的请求：改用基础模型调用（保留基础模型在目录里的完整信息，包括 dsh 不允许配置的 compat 开关），并让这一次调用的 pi-ai `Models.streamSimple` 带上 `onPayload`，把 `service_tier: "priority"`（值与两个第三方插件、pi-ai 的计价代码一致）写进请求体；其余请求原样放行。无状态、无设置、不联网、不读凭据；没有该方法时加载失败并报错（不会悄悄不生效）。`engines.dsh` 为 `>=0.2.1-rc.2 <0.3.0-0`（用 dsh-app-boot 的 `evaluatePluginCompatibility` 验证：rc.2、rc.3、0.2.1 通过，0.2.1-rc.1 和 0.3.0 被拒绝；不兼容时 dsh 只是跳过这个包并在启动时打印一行）。有 `node --test` 单元测试，`scripts/Test-Linux.sh` 在有 Node 22+ 时运行。
* 安装 `FastComponentInstaller`（不用 dsh 命令行）：dsh 解析 bundle 时先看安装目录、再看 `<档案>/node_modules`，所以只要 ① `<档案>/node_modules/dsh-account-manager-fast/` 里是真实文件（逐个文件原子写入，`package.json` 最后写），② 档案 `package.json` 的 `dsh.profile.bundles` 里有这个名字，下次启动就会加载。不写 `dependencies`、不动 `pnpm-lock.yaml`（实测之后的 `dsh plugin add / remove / install / prune` 都不会动它）。`package.json` 的修改是文本级追加：取 dsh 插件管理器同款的 `<file>.lock`（`DshFileLock` 与 dsh-atomic-write 的 `withFileLock` 同协议，等待时会持有同一把锁），在锁内重读后只在 `bundles` 数组末尾插入一项（沿用前一项的缝隙和缩进），临时文件 + rename 写回；之前先用加密备份（`BackupService`）保存原文。同版本且内容一致则什么都不写；版本更旧或文件不一致则只重写文件；比内置版本更新的不动。状态：`Installed / Missing / Outdated / NotListed`。档案没有 `package.json`（dsh 从没启动过；桌面版档案由桌面应用初始化）或形态不认识（不是对象、没有 `dsh.profile.bundles`、里面有非字符串、重复）一律拒绝，什么都不写。
* 模型 `CodexFastService`：`openai-codex` 路由里每个在 `OfficialCodexModels` 里的基础模型，在组件已装并已列出时追加一个 `<base>-fast` 条目（只追加缺的，追加在最后一项后面、沿用列表缩进；不改也不删用户自己的条目）。元数据逐字段按“用户在路由里写的基础条目 → dsh 的 pi-ai 目录（`CodexCatalog`）→ 内置表”取：名字加 ` (Fast)`，`contextWindow`、`maxTokens`、`input`，以及由 `thinkingLevelMap` 推出的 `reasoningEfforts`（映射为 null 的档位不提供；没写的 minimal/low/medium/high 用自己的名字，没写的 off/xhigh/max 不提供；`reasoning: false` 给 `false`）。内置表取自 2026-10-25 安装的 pi-ai 1.3.0 目录。原因：显式 `-fast` 条目不在目录里，dsh 配置不允许设置 supportsToolSearch 等 compat 开关，所以组件调用时改用基础模型。
* 清理：组件没有装好或没有列在档案里时（或 `-fast` 的基础模型已不在列表里），本工具移除它自己写入的 `-fast` 条目（识别：id 是 `<官方基础模型>-fast` 且名字以 ` (Fast)` 结尾），否则 `-fast` 模型名会原样发给 OpenAI（实测对照）。空的 `openai-codex: {}` 先按之前的规则升级成官方模型列表。
* 时机：每次导入成功后（站在供应商 / 路由工作之后、子代理合并之前），以及 `EnsureCodexRoute` 新建路由时。结果写进导入结果和日志；首次装好 / 更新组件时说明要完全退出 DSH 再打开，并说明 2.5 倍消耗。不改默认模型，不加界面。子代理候选（`SubagentRouteService`）排除 `-fast`，用户自己写在 `allowedModels` 里的不动。
* 风险：dsh 只在启动时读取插件列表，装好后必须完全退出再打开；Windows 上 dsh 运行时第一次安装只新增文件和改一个 JSON（原子 rename），但若要“更新”已被 dsh 载入的组件文件，写入可能被占用，此时报错并提示先退出 dsh；桌面应用若在更新时重写自己的档案 `package.json`，列表项可能被去掉，本工具每次导入都会重新检查并补回，被去掉期间会自动移除 `-fast` 模型；dsh 升级到 `engines.dsh` 范围之外时 dsh 会跳过组件，同样会让 `-fast` 模型被移除；插件靠 `PiAiAdapter.streamWithSnapshot` 这个内部方法工作（rc.2 与 rc.3 的实现逐字节相同），方法变了插件会加载失败而不是悄悄失效。

## 10. Codex 模型清单（跟着账号走）

* 接口：`CodexModelCatalogClient`——一次只读 `GET https://chatgpt.com/backend-api/codex/models?client_version=0.159.0`，头 `authorization: Bearer <access>`、`chatgpt-account-id`、`accept: application/json`、`originator: pi`（dsh 自己的 Codex 请求用的值）、`user-agent: dsh-account-manager/<版本>`；和额度查询同样的 `HttpClient`（环境代理照常生效），不跟随重定向，15 秒超时，响应上限 1 MB，失败只给固定的安全文字（不含令牌或响应体，日志里也没有令牌）。解析宽松：每个模型取 `slug`、`display_name`、`visibility`、`priority`、`context_window`、`input_modalities`、`supported_reasoning_levels[].effort`、`service_tiers[].id` / `additional_speed_tiers`（含 `priority` 或 `fast` 即支持 Fast）；没有最大输出字段。
* 令牌：只用已有的访问令牌，**从不刷新**（刷新会轮换 refresh token，同一账号在别处会失效）。优先 dsh 当前账号，有效期至少还剩 10 分钟；否则账号库里别的账号、未过期（至少 1 分钟）的；都没有就跳过并说明。
* 选择（`CodexModelPolicy`，你 2026-10-26 定的规则）：`visibility == "list"`、输入含 `text`，排除精确 `gpt-6-sol` 和前缀 `gpt-5`，按 `priority` 升序（稳定）。显示名规范化：`GPT-6-Astra` → `GPT-6 Astra`。
* 写路由（`CodexFastService.Sync`，J2 的 `ProfilePatchDocument` 路由操作）：缺的基础模型追加到 `openai-codex.models`。dsh 自带的 pi-ai 目录（`CodexCatalog`；读不到时用内置的 rc.2 目录 id 表）认识的 id 只写 `- id:`，让 dsh 用目录里完整的信息（包括不能在配置里设置的 compat 开关）；不认识的写明确的 `name`、`contextWindow`、`maxTokens`（128000，来源见 `CodexBuiltInModels` 注释）、`input`、`reasoningEfforts`——档位只取 dsh 接受的（`off minimal low medium high xhigh max`），值与名字相同，`ultra` 等不认识的丢掉。Fast 孪生沿用 J2：只在组件装好并列出时写，元数据按“用户的基础条目 → dsh 目录 → OpenAI 条目 → 内置表”逐字段取。
* 所有权：`CodexModelOwnership` 在应用数据目录的 `codex-models-state.json` 里按“DSH_HOME + 档案”记住本工具写过的 id。没有记录时（0.1.3 / 0.1.4 升级上来）把路由里只有 id 的 `gpt-6.1-sol` / `gpt-6-astra` / `gpt-6-luna` 和形状匹配的孪生认作自己的。账号不再提供、或被排除规则命中的自己的模型连同孪生一起移除；仍被 `agent-default-model` 等设置引用的（`ReferenceScanner`）不移除，只在日志和结果里说明，保留的孪生连同它的基础模型一起留着。本工具填写的子代理 `allowedModels` 里指向被移除模型的项一并去掉（否则它们会让模型永远删不掉），之后的子代理合并会补回候选。用户自己写的条目、以及导入后用户手写的 id 都不是本工具的，永远不动。
* 取不到清单（没有令牌、网络、接口变化、清单里没有可提供的模型）：不改现有列表，只按内置规则维护孪生；新建路由用内置清单 `CodexBuiltInModels`（`gpt-6.1-sol`、`gpt-6.1-terra`、`gpt-6-astra`、`gpt-6-luna`；其中 terra 不在 dsh 目录里，带 2026-10-26 OpenAI 清单里的数据：上下文 500000、文本+图片输入，推理档位取通用的五档）。
* 时机：每次导入成功后（供应商 / 路由工作之后、子代理合并之前）以及 `EnsureCodexRoute` 新建 / 升级路由时（后者没有令牌，用内置清单）。结果写进导入结果和日志。不改默认模型，不加界面。子代理候选是路由里的基础模型（不含 `-fast`）。
* 风险：显式条目（如 terra）拿不到目录里的 compat 开关（`supportsToolSearch` 等，dsh 配置不允许设置），dsh 自带目录收录它之后，已写入的显式条目不会被本工具自动改写成只有 id（想要目录里的完整信息，可手动删掉该条目再导入一次）；OpenAI 的清单没有最大输出，128000 是假设；接口、`client_version` 或字段变化会让获取失败（现有模型保持不变）。

## 11. 已知限制

* OAuth 授权请求里的 `originator` 参数使用固定值（`CodexOAuthService`），这是授权服务认可的取值，不要随意更改。
* Windows 目前只保证编译与单元测试通过。
