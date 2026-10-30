# dsh-account-manager-fast

**中文**

让 DeepSeek Harness（dsh）支持 Fast 模式（Codex 订阅和 OpenAI 官方 API），不需要任何第三方插件。

* 做什么：id 以 `-fast` 结尾的模型（例如 `gpt-6.1-sol-fast`）发出时，模型名换回基础模型（`gpt-6.1-sol`），并在请求里加上 `service_tier: "priority"`。适用于两处：`openai-codex` 路由（Codex 的 Fast 模式），以及其它路由中走 OpenAI 官方 API（`https://api.openai.com`、Responses 协议）且同一路由里也列有基础模型的 `-fast` 模型（OpenAI 的优先处理 / Fast mode）。其他任何请求都原样发出，一个字节都不改。
* 代价：Codex 订阅按官方规则以 **2.5 倍** 速度消耗额度；官方 API 的优先处理按更高单价计费。会话标题等辅助调用用的是你选中的模型，所以同样走 Fast。
* 0.2.0 起支持官方 API；更新组件后需要完全退出并重新打开 dsh。
* 实现：dsh 没有“服务档位”设置，所以本插件只包装 dsh 公开导出的 `PiAiAdapter` 类的一个方法（`streamWithSnapshot`）：仅对 `-fast` 请求，改用基础模型，并在发送前给请求体加上 `service_tier`（WebSocket 和 HTTP 两种传输都生效）。没有状态、没有设置、不联网、不读凭据。dsh 里没有这个方法时，插件加载失败并报错，而不是悄悄不生效。
* 前提：路由里要同时列出基础模型和它的 `-fast` 兄弟（DSH Account Manager 会这样写）。
* 版本：`engines.dsh` 声明了支持的 dsh 版本范围；不在范围内时 dsh 会跳过本插件（并在启动时打印一行提示）。
* 手动卸载：删除 `<DSH_HOME>/profiles/<档案>/node_modules/dsh-account-manager-fast/` 文件夹，并从档案 `package.json` 的 `dsh.profile.bundles` 里去掉 `"dsh-account-manager-fast"`，再删掉路由里的 `*-fast` 模型，然后重启 dsh。

**English**

Fast mode for DeepSeek Harness (dsh), for Codex subscriptions and the official OpenAI API, with no third-party plugin.

* What it does: a model whose id ends in `-fast` (for example `gpt-6.1-sol-fast`) is sent as its base model (`gpt-6.1-sol`) with `service_tier: "priority"`. This applies to the `openai-codex` route (how Codex asks for Fast mode) and to a `-fast` model of any other route that speaks the Responses API at `https://api.openai.com` and lists the base model on the same route (OpenAI's priority processing). Every other request is sent exactly as dsh builds it.
* Cost: by OpenAI's rules Codex Fast mode uses your included subscription limits at **2.5x**; priority processing on the API is billed at a premium rate. Auxiliary calls such as session titles use the model you selected, so they run Fast too.
* Since 0.2.0 the official API is supported; restart dsh fully after the component is updated.
* How: dsh has no service-tier setting, so the plugin wraps one method (`streamWithSnapshot`) of the `PiAiAdapter` class that dsh exports. For `-fast` requests only, the call is made for the base model and the request body gets `service_tier` right before it is sent (WebSocket and HTTP transport alike). No state, no settings, no network access, no credential access. On a dsh without that method the plugin fails to load with an error instead of silently doing nothing.
* Requirement: the route must list the base model next to its `-fast` twin (DSH Account Manager writes it that way).
* Versions: `engines.dsh` declares the supported dsh range; outside it dsh skips the plugin and prints one line at start-up.
* Manual removal: delete `<DSH_HOME>/profiles/<profile>/node_modules/dsh-account-manager-fast/`, remove `"dsh-account-manager-fast"` from `dsh.profile.bundles` in the profile's `package.json`, remove the `*-fast` models from the routes, and restart dsh.

MIT licensed.
