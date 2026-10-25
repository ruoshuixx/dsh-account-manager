# dsh-account-manager-fast

**中文**

让 DeepSeek Harness（dsh）支持 Codex 的 Fast 模式，不需要任何第三方插件。

* 做什么：`openai-codex` 路由里 id 以 `-fast` 结尾的模型（例如 `gpt-6.1-sol-fast`）发给 ChatGPT 时，模型名换回基础模型（`gpt-6.1-sol`），并在请求里加上 `service_tier: "priority"`（Codex 的 Fast 模式）。其他任何请求都原样发出，一个字节都不改。
* 代价：按官方规则，Fast 模式以 **2.5 倍** 速度消耗订阅里包含的额度。会话标题等辅助调用用的是你选中的模型，所以同样走 Fast。
* 实现：dsh 没有“服务档位”设置，所以本插件只包装 dsh 公开导出的 `PiAiAdapter` 类的一个方法（`streamWithSnapshot`）：仅对 `-fast` 请求，改用基础模型，并在发送前给请求体加上 `service_tier`（WebSocket 和 HTTP 两种传输都生效）。没有状态、没有设置、不联网、不读凭据。dsh 里没有这个方法时，插件加载失败并报错，而不是悄悄不生效。
* 前提：路由里要同时列出基础模型和它的 `-fast` 兄弟（DSH Account Manager 会这样写）。
* 版本：`engines.dsh` 声明了支持的 dsh 版本范围；不在范围内时 dsh 会跳过本插件（并在启动时打印一行提示）。
* 手动卸载：删除 `<DSH_HOME>/profiles/<档案>/node_modules/dsh-account-manager-fast/` 文件夹，并从档案 `package.json` 的 `dsh.profile.bundles` 里去掉 `"dsh-account-manager-fast"`，再删掉路由里的 `*-fast` 模型，然后重启 dsh。

**English**

Codex Fast mode for DeepSeek Harness (dsh), with no third-party plugin.

* What it does: a model of the `openai-codex` route whose id ends in `-fast` (for example `gpt-6.1-sol-fast`) is sent to ChatGPT as its base model (`gpt-6.1-sol`) with `service_tier: "priority"`, which is how Codex asks for Fast mode. Every other request is sent exactly as dsh builds it.
* Cost: by OpenAI's rules Fast mode uses your included subscription limits at **2.5x**. Auxiliary calls such as session titles use the model you selected, so they run Fast too.
* How: dsh has no service-tier setting, so the plugin wraps one method (`streamWithSnapshot`) of the `PiAiAdapter` class that dsh exports. For `-fast` requests only, the call is made for the base model and the request body gets `service_tier` right before it is sent (WebSocket and HTTP transport alike). No state, no settings, no network access, no credential access. On a dsh without that method the plugin fails to load with an error instead of silently doing nothing.
* Requirement: the route must list the base model next to its `-fast` twin (DSH Account Manager writes it that way).
* Versions: `engines.dsh` declares the supported dsh range; outside it dsh skips the plugin and prints one line at start-up.
* Manual removal: delete `<DSH_HOME>/profiles/<profile>/node_modules/dsh-account-manager-fast/`, remove `"dsh-account-manager-fast"` from `dsh.profile.bundles` in the profile's `package.json`, remove the `*-fast` models from the route, and restart dsh.

MIT licensed.
