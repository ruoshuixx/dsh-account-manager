import test from 'node:test'
import assert from 'node:assert/strict'
import { METHOD, PROVIDER, SERVICE_TIER, baseModelId, fastModels, installOn, withPriorityTier } from '../lib/fast.js'

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
