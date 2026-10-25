// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { createEventWriter, parseCommand } from '../src/protocol.js'

test('commands are parsed strictly', () => {
  assert.deepEqual(parseCommand('{"cmd":"close"}'), { ok: true, cmd: 'close' })
  assert.deepEqual(parseCommand('{"cmd":"select","fileIndex":2}'), { ok: true, cmd: 'select', fileIndex: 2 })
  assert.equal(parseCommand('').code, 'empty_command')
  assert.equal(parseCommand('{').code, 'invalid_json')
  assert.equal(parseCommand('[]').code, 'invalid_command')
  assert.equal(parseCommand('{"cmd":"stop"}').code, 'unknown_command')
  assert.equal(parseCommand('{"cmd":"select","fileIndex":-1}').code, 'invalid_file_index')
  assert.equal(parseCommand('{"cmd":"select","fileIndex":"2"}').code, 'invalid_file_index')
})

test('the writer emits one JSON object per line', () => {
  const chunks = []
  const stream = {
    destroyed: false,
    writable: true,
    write: chunk => { chunks.push(chunk) },
    end: () => {}
  }
  const writer = createEventWriter(stream)
  writer.send({ type: 'hello', contract: 1 })
  writer.send({ type: 'state', peers: 3 })
  assert.equal(chunks.length, 2)
  assert.deepEqual(JSON.parse(chunks[0]), { type: 'hello', contract: 1 })
  assert.ok(chunks[1].endsWith('\n'))
})

test('a destroyed stream never throws', () => {
  const stream = { destroyed: true, writable: true, write: () => { throw new Error('boom') } }
  const writer = createEventWriter(stream)
  assert.equal(writer.send({ type: 'state' }), false)
})
