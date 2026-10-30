// dsh-account-manager-fast (MIT): the whole mechanism, free of any dsh import so it can be tested on its own.
//
// A model whose id ends in "-fast" (for example "gpt-6.1-sol-fast") is sent as its base model ("gpt-6.1-sol")
// with `service_tier: "priority"`. That applies to
//   * the `openai-codex` route (ChatGPT's Fast mode), and
//   * any other route whose "-fast" model speaks the OpenAI Responses API at https://api.openai.com (OpenAI's
//     priority processing), provided the base model is listed on the same route.
// Every other request is left exactly as dsh builds it.
//
// dsh has no setting for the service tier, so the plugin wraps one method of the adapter class. For a "-fast"
// request only, the call is made for the base model (it keeps the base model's full catalog entry, including
// the compat flags dsh cannot configure) and the pi-ai model collection of that one call gets an `onPayload`
// hook that adds the tier to the request body right before it is sent (WebSocket and HTTP transport alike).
// The route has to list the base model next to its "-fast" twin.
// No state, no network access, no settings and no credential access: the request already carries what it needs.

/** The provider route this acts on. */
export const PROVIDER = 'openai-codex'
/** Model-id suffix that asks for Fast mode. */
export const SUFFIX = '-fast'
/** Value of the wire field Codex uses for Fast mode. */
export const SERVICE_TIER = 'priority'
/** The api name of the Responses protocol in dsh's model objects. */
export const RESPONSES_API = 'openai-responses'
/** The only host (besides the codex route) whose requests may be sent as Fast. */
export const OPENAI_HOST = 'api.openai.com'
/** The adapter method that is wrapped. */
export const METHOD = 'streamWithSnapshot'

const MARK = Symbol.for('dsh-account-manager-fast.wrapped')

/** The base model id of a "-fast" model id, or undefined for any other id. */
export function baseModelId(id) {
  return typeof id === 'string' && id.length > SUFFIX.length && id.endsWith(SUFFIX)
    ? id.slice(0, -SUFFIX.length)
    : undefined
}

/** Whether a dsh model object is a Responses-protocol model served by the official OpenAI API. */
export function isOfficialResponsesModel(model) {
  if (model === undefined || model === null || model.api !== RESPONSES_API || typeof model.baseUrl !== 'string') return false
  try {
    const url = new URL(model.baseUrl)
    return url.protocol === 'https:' && url.hostname.toLowerCase() === OPENAI_HOST
  } catch {
    return false
  }
}

/** The model of a route as the adapter resolves it, or undefined when it does not (a lookup that throws counts as "not there"). */
function resolve(adapter, snapshot, provider, id) {
  try {
    return adapter.modelOf(snapshot, provider, id) ?? undefined
  } catch {
    return undefined
  }
}

/** Stream options whose request body gets the priority tier (an earlier onPayload hook still runs first). */
export function withPriorityTier(options) {
  const previous = options?.onPayload
  return {
    ...options,
    async onPayload(payload, model) {
      const replaced = await previous?.(payload, model)
      const body = replaced === undefined ? payload : replaced
      return body !== null && typeof body === 'object' ? { ...body, service_tier: SERVICE_TIER } : body
    },
  }
}

/** A view of a pi-ai model collection whose streamSimple() asks for Fast mode. */
export function fastModels(models) {
  return new Proxy(models, {
    get(target, property) {
      if (property === 'streamSimple') {
        return (model, context, options) => target.streamSimple(model, context, withPriorityTier(options))
      }
      const value = Reflect.get(target, property, target)
      return typeof value === 'function' ? value.bind(target) : value
    },
  })
}

/**
 * Wraps `adapterClass.prototype.streamWithSnapshot`. Returns the function that undoes it.
 * @throws when the class has no such method: this dsh is not supported and the plugin must not pretend otherwise.
 */
export function installOn(adapterClass) {
  const original = adapterClass?.prototype?.[METHOD]
  if (typeof original !== 'function') {
    throw new Error(`dsh-account-manager-fast: this dsh has no PiAiAdapter.${METHOD}(); the plugin does not support it`)
  }
  if (original[MARK] === true) return () => {}

  const wrapped = function (options, snapshot) {
    const base = baseModelId(options?.model)
    if (base === undefined || snapshot?.models === undefined) return original.call(this, options, snapshot)
    if (options.provider !== PROVIDER) {
      // Any other route: only an official-OpenAI Responses twin whose base model is listed next to it.
      if (typeof this?.modelOf !== 'function') return original.call(this, options, snapshot)
      if (!isOfficialResponsesModel(resolve(this, snapshot, options.provider, options.model))) return original.call(this, options, snapshot)
      if (!isOfficialResponsesModel(resolve(this, snapshot, options.provider, base))) return original.call(this, options, snapshot)
    }
    return original.call(this, { ...options, model: base }, { ...snapshot, models: fastModels(snapshot.models) })
  }
  wrapped[MARK] = true
  adapterClass.prototype[METHOD] = wrapped
  return () => {
    if (adapterClass.prototype[METHOD] === wrapped) adapterClass.prototype[METHOD] = original
  }
}
