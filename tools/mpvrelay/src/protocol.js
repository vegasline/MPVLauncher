// One JSON line per event on stdout, like mpvtorrent. Every field is optional
// and unknown types are ignored by the launcher, so a newer helper never
// breaks an older launcher.

export const EventType = Object.freeze({
  Ready: "ready",
  Progress: "progress",
  Error: "error",
  Log: "log",
  // Asks the launcher to fetch a URL inside the viewer's own browser tab,
  // because the site refuses the same request from outside that context.
  Fetch: "fetch",
});

export function writeEvent(type, fields) {
  const payload = { type, contract: 1, ...fields };
  try {
    process.stdout.write(JSON.stringify(payload) + "\n");
  } catch {
    // A closed pipe means the launcher is gone; the process will exit shortly.
  }
}

export function writeError(code, message) {
  writeEvent(EventType.Error, { code, message });
}
