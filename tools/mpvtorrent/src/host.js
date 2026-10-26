// Original implementation, MIT licensed.
//
// DNS-rebinding protection for the local streaming server. The server binds to
// 127.0.0.1, but a page on the internet can resolve its own hostname to
// 127.0.0.1 and then reach us; the Host header still carries the attacker's
// name, so requiring a loopback literal in Host blocks that. The random URL
// token is a second, independent gate.

/** Host header must be a loopback literal with exactly our port. */
export function isAllowedHost (header, port) {
  if (typeof header !== 'string' || header.trim() === '') return false
  if (!Number.isInteger(port) || port <= 0 || port > 65535) return false
  const value = header.trim().toLowerCase()
  const expectedPort = String(port)

  for (const host of ['127.0.0.1', 'localhost', '[::1]']) {
    if (value === `${host}:${expectedPort}`) return true
  }
  return false
}

/**
 * The request path must be /s/<token>, optionally followed by one decorative
 * segment holding the file name.
 *
 * The name exists only so players can derive a readable title from the URL;
 * it is never used to pick a file or to touch the disk. The token is compared
 * exactly, and the optional segment may not contain a slash or a control
 * character, so a crafted path cannot walk upwards.
 */
export function isAuthorizedPath (requestPath, token) {
  if (typeof requestPath !== 'string' || typeof token !== 'string' || token.length < 16) return false

  const prefix = `/s/${token}`
  if (requestPath === prefix) return true
  if (!requestPath.startsWith(prefix + '/')) return false

  const name = requestPath.slice(prefix.length + 1)
  if (name.length === 0 || name.length > 300) return false
  if (name.includes('/') || name.includes('\\')) return false
  // Percent-encoded separators too: the segment is only cosmetic, so there is
  // no reason to let a path-shaped string through.
  if (/%2f|%5c/i.test(name)) return false
  // eslint-disable-next-line no-control-regex
  return !/[\u0000-\u001f\u007f]/.test(name)
}

export function createStreamToken (randomBytes) {
  return randomBytes(24).toString('hex')
}
