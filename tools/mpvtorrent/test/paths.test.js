// Original implementation, MIT licensed.

import test from 'node:test'
import assert from 'node:assert/strict'
import { join, sep } from 'node:path'
import {
  PathError,
  checkFileList,
  displayName,
  safeJoin,
  safeSegments
} from '../src/paths.js'

test('normal nested paths are split into segments', () => {
  assert.deepEqual(safeSegments('Show/Season 1/Episode.mkv'), ['Show', 'Season 1', 'Episode.mkv'])
  assert.deepEqual(safeSegments(['a', 'b.mp4']), ['a', 'b.mp4'])
})

test('traversal and absolute paths are rejected', () => {
  for (const value of ['../evil.exe', 'a/../../evil', '/etc/passwd', 'C:\\Windows\\evil', '\\\\server\\share\\x', 'a/./b']) {
    assert.throws(() => safeSegments(value), PathError, String(value))
  }
})

test('Windows device names and illegal characters are rejected', () => {
  for (const value of ['CON', 'nul.txt', 'a/COM1.mkv', 'a/PRN', 'a:b.mkv', 'a?.mkv', 'a<b', 'trail. ', 'a/\u0001x']) {
    assert.throws(() => safeSegments(value), PathError, String(value))
  }
})

test('safeJoin keeps the result under the root', () => {
  const root = join(process.cwd(), 'downloads')
  const target = safeJoin(root, 'a/b.mkv')
  assert.ok(target.startsWith(root + sep))
  assert.throws(() => safeJoin(root, '../b.mkv'), PathError)
})

test('displayName never leaks directories', () => {
  assert.equal(displayName('a/b/c.mkv'), 'c.mkv')
  assert.equal(displayName('../evil'), 'download')
})

test('file lists are bounded and validated', () => {
  assert.equal(checkFileList([]).code, 'bad_metadata')
  assert.equal(checkFileList(new Array(2001).fill({ path: 'a', length: 1 })).code, 'too_many_files')
  assert.equal(checkFileList([{ path: 'a', length: -1 }]).code, 'bad_metadata')
  const ok = checkFileList([{ path: 'a', length: 10 }, { path: 'b/c', length: 5 }])
  assert.equal(ok.ok, true)
  assert.equal(ok.total, 15)
})
