// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { MAX_TRACKERS, parseTrackerList } from '../src/trackers.js'

test('comments, blanks and unsupported schemes are ignored', () => {
  const list = parseTrackerList([
    '# comment',
    '',
    'udp://tracker.opentrackr.org:1337/announce',
    'https://tracker.example/announce',
    'ws://tracker.example/announce',
    'ftp://tracker.example/announce',
    'not a url'
  ].join('\n'))
  assert.deepEqual(list, [
    'udp://tracker.opentrackr.org:1337/announce',
    'https://tracker.example/announce'
  ])
})

test('duplicates are removed and the list is capped', () => {
  assert.deepEqual(parseTrackerList('udp://a:1/x\nudp://a:1/x'), ['udp://a:1/x'])
  const many = Array.from({ length: MAX_TRACKERS + 20 }, (_, i) => `udp://t${i}:1/announce`).join('\n')
  assert.equal(parseTrackerList(many).length, MAX_TRACKERS)
})

test('non-string input yields an empty list', () => {
  assert.deepEqual(parseTrackerList(null), [])
  assert.deepEqual(parseTrackerList(undefined), [])
})
