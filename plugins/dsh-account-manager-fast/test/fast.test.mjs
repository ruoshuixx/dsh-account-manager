import test from 'node:test'
import assert from 'node:assert/strict'
import { METHOD, PROVIDER, SERVICE_TIER, baseModelId, fastModels, installOn, isOfficialResponsesModel, withPriorityTier } from '../lib/fast.js'

/** A stand-in for dsh's PiAiAdapter: records what the original method was called with. */
function fakeAdapter() {
  const calls = []
  class Adapter {
    streamWithSnapshot(options, snapshot) {
      calls.push({ self: this, options, snapshot })
      return 'stream'
    }
  }
  return { Adapter, calls }
}

function fakeModels() {
  const streams = []
  return {
    streams,
    models: {
      tag: 'models',
      getModel(provider, id) { return { provider, id, self: this.tag } },
      streamSimple(model, context, options) { streams.push({ model, context, options }); return 'pi-stream' },
    },
  }
}

test('baseModelId strips exactly one trailing -fast and nothing else', () => {
  assert.equal(baseModelId('gpt-6.1-sol-fast'), 'gpt-6.1-sol')
  assert.equal(baseModelId('gpt-6-astra-fast'), 'gpt-6-astra')
  assert.equal(baseModelId('gpt-6.1-sol'), undefined)
  assert.equal(baseModelId('-fast'), undefined)
  assert.equal(baseModelId('fast'), undefined)
  assert.equal(baseModelId('gpt-fast-6'), undefined)
  assert.equal(baseModelId(undefined), undefined)
  assert.equal(baseModelId(7), undefined)
})

test('a -fast request of the openai-codex route runs as the base model with the priority tier', async () => {
  const { Adapter, calls } = fakeAdapter()
  installOn(Adapter)
  const { models, streams } = fakeModels()
  const snapshot = { profiles: 'p', models }
  const adapter = new Adapter()

  assert.equal(adapter[METHOD]({ provider: PROVIDER, model: 'gpt-6.1-sol-fast', messages: [] }, snapshot), 'stream')

  const [call] = calls
  assert.equal(call.self, adapter)
  assert.equal(call.options.model, 'gpt-6.1-sol')
  assert.equal(call.options.provider, PROVIDER)
  assert.equal(call.snapshot.profiles, 'p')
  assert.deepEqual(call.snapshot.models.getModel('openai-codex', 'x'), { provider: 'openai-codex', id: 'x', self: 'models' })

  const hook = { type: 'x' }
  call.snapshot.models.streamSimple({ id: 'gpt-6.1-sol' }, 'ctx', { headers: { a: '1' }, onPayload: async (body) => ({ ...body, text: 'edited' }) })
  const options = streams[0].options
  assert.deepEqual(options.headers, { a: '1' })
  const body = await options.onPayload({ model: 'gpt-6.1-sol', input: [] }, { id: 'gpt-6.1-sol' })
  assert.deepEqual(body, { model: 'gpt-6.1-sol', input: [], text: 'edited', service_tier: SERVICE_TIER })
  assert.equal(hook.type, 'x')
})

test('every other request reaches the original method untouched', () => {
  const { Adapter, calls } = fakeAdapter()
  installOn(Adapter)
  const { models } = fakeModels()
  const snapshot = { profiles: 'p', models }
  const adapter = new Adapter()
  for (const options of [
    { provider: PROVIDER, model: 'gpt-6.1-sol' },
    { provider: 'other-gw', model: 'gw-fast' },
    { provider: PROVIDER, model: '-fast' },
    {},
  ]) {
    adapter[METHOD](options, snapshot)
    const last = calls.at(-1)
    assert.equal(last.options, options)
    assert.equal(last.snapshot, snapshot)
  }
})

test('a missing method fails loudly, installing twice wraps once, and the undo restores the original', () => {
  assert.throws(() => installOn(class {}), /does not support it/)
  assert.throws(() => installOn(undefined), /does not support it/)

  const { Adapter } = fakeAdapter()
  const original = Adapter.prototype[METHOD]
  const undo = installOn(Adapter)
  const wrapped = Adapter.prototype[METHOD]
  assert.notEqual(wrapped, original)
  const undoAgain = installOn(Adapter)
  assert.equal(Adapter.prototype[METHOD], wrapped)
  undoAgain()
  assert.equal(Adapter.prototype[METHOD], wrapped)
  undo()
  assert.equal(Adapter.prototype[METHOD], original)
})

test('withPriorityTier leaves a non-object body alone and runs an earlier hook first', async () => {
  const options = withPriorityTier({ onPayload: async () => undefined })
  assert.deepEqual(await options.onPayload({ a: 1 }), { a: 1, service_tier: 'priority' })
  assert.equal(await options.onPayload(null), null)
  assert.equal(typeof fastModels({ streamSimple() {} }).streamSimple, 'function')
})

/** A fake adapter that, like dsh's, resolves models by route and id through modelOf(). */
function routedAdapter(table, { throws = false } = {}) {
  const calls = []
  class Adapter {
    modelOf(snapshot, provider, id) {
      if (throws) throw new Error('unknown model')
      return table[`${provider}/${id}`]
    }
    streamWithSnapshot(options, snapshot) {
      calls.push({ options, snapshot })
      return 'stream'
    }
  }
  return { Adapter, calls }
}

const official = { api: 'openai-responses', baseUrl: 'https://api.openai.com/v1' }

test('isOfficialResponsesModel accepts only https api.openai.com with the Responses api', () => {
  assert.equal(isOfficialResponsesModel(official), true)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses', baseUrl: 'https://API.OPENAI.COM/v1/' }), true)
  assert.equal(isOfficialResponsesModel({ api: 'openai-completions', baseUrl: 'https://api.openai.com/v1' }), false)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses', baseUrl: 'http://api.openai.com/v1' }), false)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses', baseUrl: 'https://ark.cn-beijing.volces.com/api/plan/v3' }), false)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses', baseUrl: 'https://api.openai.com.evil.example/v1' }), false)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses', baseUrl: 'not a url' }), false)
  assert.equal(isOfficialResponsesModel({ api: 'openai-responses' }), false)
  assert.equal(isOfficialResponsesModel(undefined), false)
})

test('a -fast model of an api.openai.com Responses route runs as its base model with the priority tier', async () => {
  const { Adapter, calls } = routedAdapter({ 'my-openai/gpt-6-astra-fast': official, 'my-openai/gpt-6-astra': official })
  installOn(Adapter)
  const { models, streams } = fakeModels()
  new Adapter()[METHOD]({ provider: 'my-openai', model: 'gpt-6-astra-fast' }, { models })
  assert.equal(calls[0].options.model, 'gpt-6-astra')
  assert.equal(calls[0].options.provider, 'my-openai')
  calls[0].snapshot.models.streamSimple({}, 'ctx', {})
  const body = await streams[0].options.onPayload({ model: 'gpt-6-astra' })
  assert.deepEqual(body, { model: 'gpt-6-astra', service_tier: SERVICE_TIER })
})

test('other hosts, other protocols, an unlisted base model and a failing lookup are left untouched', () => {
  const ark = { api: 'openai-responses', baseUrl: 'https://ark.cn-beijing.volces.com/api/plan/v3' }
  const chat = { api: 'openai-completions', baseUrl: 'https://api.openai.com/v1' }
  const { models } = fakeModels()
  const cases = [
    [{ 'p/x-fast': ark, 'p/x': ark }, false],
    [{ 'p/x-fast': chat, 'p/x': chat }, false],
    [{ 'p/x-fast': official }, false],
    [{}, false],
    [{ 'p/x-fast': official, 'p/x': official }, false],
    [{ 'p/x-fast': official, 'p/x': official }, true],
  ]
  for (const [table, throws] of cases) {
    const { Adapter, calls } = routedAdapter(table, { throws })
    installOn(Adapter)
    const options = { provider: 'p', model: 'x-fast' }
    const snapshot = { models }
    new Adapter()[METHOD](options, snapshot)
    if (table['p/x-fast'] === official && table['p/x'] === official && !throws) {
      assert.equal(calls[0].options.model, 'x')
    } else {
      assert.equal(calls[0].options, options)
      assert.equal(calls[0].snapshot, snapshot)
    }
  }
})
