// Original implementation, MIT licensed.

/** Cross-platform "is this PID still running" check (Windows included). */
export function isProcessAlive (pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false
  try {
    process.kill(pid, 0)
    return true
  } catch (error) {
    // EPERM means the process exists but belongs to someone else.
    return error.code === 'EPERM'
  }
}
