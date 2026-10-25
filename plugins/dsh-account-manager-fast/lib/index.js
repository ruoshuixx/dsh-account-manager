// dsh-account-manager-fast (MIT): Codex Fast mode for DeepSeek Harness. See ./fast.js for what it does.
import { PiAiAdapter } from '@deepseek-ai/dsh-llm-pi-ai'
import { PROVIDER, SERVICE_TIER, SUFFIX, installOn } from './fast.js'

export const name = 'dsh-account-manager-fast'

export function apply(ctx) {
  ctx.effect(() => installOn(PiAiAdapter), 'dsh-account-manager-fast: Codex Fast mode')
  ctx.logger.info(`dsh-account-manager-fast: models of "${PROVIDER}" ending in "${SUFFIX}" are sent as the base model with the "${SERVICE_TIER}" service tier`)
}
