// Command line for the browser relay helper.
//
// Same shape as TorrentHelperArguments: plain --flag value pairs, every value
// already validated by the launcher, and a contract version so a newer helper
// can still talk to an older launcher.

export const CONTRACT_VERSION = 1;

const NUMERIC = new Map([
  ["--mpv-pid", "mpvPid"],
  ["--launcher-pid", "launcherPid"],
  ["--contract", "contract"],
  ["--timeout", "timeoutSeconds"],
]);

const FLAGS = new Map([
  ["--headless", "headless"],
  ["--verbose", "verbose"],
  ["--detach", "detach"],
  ["--broker", "broker"],
]);

const STRINGS = new Map([
  ["--stream", "stream"],
  ["--page", "page"],
  ["--title", "title"],
  ["--session-dir", "sessionDir"],
  ["--log-file", "logFile"],
  ["--browser", "browser"],
]);

/**
 * Parses the helper's own argv.
 *
 * Unknown flags are ignored rather than rejected: the launcher may send a
 * newer option to an older helper, and the helper should still start.
 */
export function parseArgs(argv) {
  const out = {
    page: "",
    title: "",
    sessionDir: "",
    logFile: "",
    browser: "",
    mpvPid: 0,
    launcherPid: 0,
    contract: CONTRACT_VERSION,
    timeoutSeconds: 45,
    headless: false,
    verbose: false,
  };

  for (let i = 0; i < argv.length; i++) {
    const key = argv[i];
    if (FLAGS.has(key)) {
      out[FLAGS.get(key)] = true;
      continue;
    }
    if (NUMERIC.has(key)) {
      const value = Number(argv[i + 1]);
      if (Number.isFinite(value)) out[NUMERIC.get(key)] = Math.trunc(value);
      i++;
      continue;
    }
    if (STRINGS.has(key)) {
      out[STRINGS.get(key)] = String(argv[i + 1] ?? "");
      i++;
    }
  }

  if (out.timeoutSeconds < 10) out.timeoutSeconds = 10;
  if (out.timeoutSeconds > 120) out.timeoutSeconds = 120;
  return out;
}
