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

/** The request path must be exactly /s/<token>. */
export function isAuthorizedPath (requestPath, token) {
  if (typeof requestPath !== 'string' || typeof token !== 'string' || token.length < 16) return false
  return requestPath === `/s/${token}`
}

export function createStreamToken (randomBytes) {
  return randomBytes(24).toString('hex')
}
