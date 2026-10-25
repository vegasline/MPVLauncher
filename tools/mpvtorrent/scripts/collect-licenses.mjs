// Original implementation, MIT licensed.
//
// Walks the installed dependency tree and reports every package's declared
// licence plus where its licence text can be read from. Used to build
// THIRD_PARTY_NOTICES.md and to fail the build when a copyleft or unknown
// licence sneaks in through a transitive dependency.

import { readFileSync, readdirSync, existsSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(dirname(fileURLToPath(import.meta.url)))
export const ALLOWED_IDS = new Set([
  'MIT', 'ISC', 'BSD-2-Clause', 'BSD-3-Clause', 'Apache-2.0', '0BSD', 'CC0-1.0',
  'Unlicense', 'BlueOak-1.0.0', 'Python-2.0', 'WTFPL'
])

/**
 * True when every licence named in an SPDX expression is permissive. A
 * compound expression passes only if all of its parts do: "MIT OR GPL-3.0"
 * would still let a recipient pick the copyleft option, which is not a risk
 * this project accepts.
 */
export function licenceAllowed (expression) {
  if (typeof expression !== 'string' || expression.trim() === '') return false
  const ids = expression.replace(/[()]/g, ' ').split(/\s+(?:OR|AND)\s+|\//i).map(s => s.trim()).filter(Boolean)
  return ids.length > 0 && ids.every(id => ALLOWED_IDS.has(id))
}

/** Every package.json reachable under node_modules, de-duplicated by path. */
export function installedPackages (base = root) {
  const out = []
  const seen = new Set()
  const walk = (dir) => {
    const nm = join(dir, 'node_modules')
    if (!existsSync(nm)) return
    for (const name of readdirSync(nm)) {
      if (name === '.bin' || name === '.package-lock.json') continue
      const dirs = name.startsWith('@') ? readdirSync(join(nm, name)).map(n => join(nm, name, n)) : [join(nm, name)]
      for (const pkgDir of dirs) {
        const file = join(pkgDir, 'package.json')
        if (!existsSync(file) || seen.has(pkgDir)) continue
        seen.add(pkgDir)
        let pkg
        try { pkg = JSON.parse(readFileSync(file, 'utf8')) } catch { continue }
        out.push({ dir: pkgDir, name: pkg.name ?? name, version: pkg.version ?? '0.0.0', license: pkg.license ?? licenceFromExpression(pkg), text: findLicenceText(pkgDir) })
        walk(pkgDir)
      }
    }
  }
  walk(base)
  return out.sort((a, b) => a.name.localeCompare(b.name))
}

function licenceFromExpression (pkg) {
  if (Array.isArray(pkg.licenses)) return pkg.licenses.map(l => l.type ?? l).join(' OR ')
  return undefined
}

/** First licence-ish file in the package root, or null. */
export function findLicenceText (dir) {
  const names = readdirSync(dir).filter(n => /^(LICEN[CS]E|COPYING|UNLICENSE|NOTICE)(\..*)?$/i.test(n))
  const order = ['license', 'licence', 'copying', 'unlicense', 'notice']
  names.sort((a, b) => {
    const ai = order.findIndex(o => a.toLowerCase().startsWith(o))
    const bi = order.findIndex(o => b.toLowerCase().startsWith(o))
    return ai - bi
  })
  for (const name of names) {
    const p = join(dir, name)
    if (statSync(p).isFile()) return p
  }
  return null
}

export function audit (packages = installedPackages()) {
  return packages.map(p => ({
    ...p,
    ok: licenceAllowed(p.license),
    text: p.text == null ? null : relative(root, p.text)
  }))
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const rows = audit()
  const bad = rows.filter(r => !r.ok)

  if (process.argv.includes('--write')) {
    const sections = []
    const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
    sections.push('# Third-party notices')
    sections.push('')
    sections.push(`mpvtorrent ${pkg.version} is distributed under the MIT licence (see LICENSE).`)
    sections.push('')
    sections.push('The Windows executable embeds the Node.js runtime. Node.js and the')
    sections.push('components bundled into that runtime are licensed as follows:')
    sections.push('')
    sections.push(indent(readFileSync(join(root, 'vendor', 'node', 'LICENSE'), 'utf8')))
    sections.push('')

    sections.push('## Vendored stand-ins')
    sections.push('')
    sections.push('These directories replace packages whose licences were unclear or')
    sections.push('whose native code is not shipped. The stand-ins are original code')
    sections.push('written for this project and are covered by the same MIT licence.')
    sections.push('')
    for (const name of readdirSync(join(root, 'vendor'))) {
      if (name === 'node') continue
      const p = join(root, 'vendor', name, 'package.json')
      if (!existsSync(p)) continue
      const v = JSON.parse(readFileSync(p, 'utf8'))
      sections.push(`### ${v.name}@${v.version}`)
      sections.push('')
      sections.push('MIT (original stand-in, no third-party code)')
      sections.push('')
    }

    sections.push('## npm dependencies')
    sections.push('')
    for (const row of rows) {
      sections.push(`### ${row.name}@${row.version}`)
      sections.push('')
      sections.push(`License: ${row.license ?? 'unknown'}`)
      if (row.text) {
        sections.push('')
        sections.push(indent(readFileSync(join(root, row.text), 'utf8')))
      } else {
        sections.push('')
        sections.push('No licence file was shipped by this package; the declared licence is shown above.')
      }
      sections.push('')
    }

    writeFileSync(join(root, 'THIRD_PARTY_NOTICES.md'), sections.join('\n') + '\n')
    console.log(`wrote THIRD_PARTY_NOTICES.md for ${rows.length} packages`)
  }

  for (const r of rows) {
    console.log(`${r.ok ? 'OK  ' : 'FAIL'} ${r.name}@${r.version} ${r.license ?? '(no license field)'} ${r.text ?? '(no text)'}`)
  }
  console.log(`\n${rows.length} packages, ${bad.length} not allowed`)
  process.exit(bad.length === 0 ? 0 : 1)
}

/** Four-space indentation keeps licence text readable without fences. */
function indent (text) {
  return String(text).replace(/\r\n/g, '\n').split('\n').map(line => line === '' ? '' : '    ' + line).join('\n')
}
