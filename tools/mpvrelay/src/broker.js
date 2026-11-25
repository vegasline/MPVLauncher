// Fetches on behalf of the viewer's own tab.
//
// The page's stream URL only answers requests made from the page's own origin:
// a plain HTTP GET returns 403, and opening the page in a second browser is
// exactly what makes the site's bot wall block the whole network. So the relay
// owns no browser in this mode. The launcher does: it holds the tab the viewer
// already has open, and this module asks it for every byte - one JSON request
// line on stdout, one JSON reply line on stdin (see protocol.js).
import { EventType, writeEvent } from "./protocol.js";

/**
 * Returns a fetchRemote(url, {headers}) shaped like the browser one, backed by
 * the launcher's tab.
 */
export function createBrokerFetcher(log = () => {}, timeoutMs = 60000) {
  const pending = new Map();
  let nextId = 1;

  const settle = (id, value) => {
    const entry = pending.get(id);
    if (!entry) return;
    pending.delete(id);
    clearTimeout(entry.timer);
    entry.resolve(value);
  };

  return {
    /** Called by the stdin reader for every reply line. */
    handle(line) {
      const id = Number(line && line.id) || 0;
      if (!id) return;
      if (line.cmd === "fetch" || line.cmd === "fetchEnd") {
        if (line.error) {
          settle(id, { ok: false, status: 0, error: String(line.error), body: Buffer.alloc(0) });
          return;
        }
        settle(id, {
          ok: Number(line.status) >= 200 && Number(line.status) < 300,
          status: Number(line.status) || 0,
          contentType: String(line.contentType || ""),
          body: Buffer.from(String(line.body || ""), "base64"),
          error: line.status ? "" : "no reply",
        });
      }
    },

    fetchRemote(url, { headers = {} } = {}) {
      const id = nextId++;
      return new Promise((resolve) => {
        const timer = setTimeout(() => {
          log(`the page did not answer for ${url.slice(0, 80)}`);
          settle(id, { ok: false, status: 0, error: "timeout", body: Buffer.alloc(0) });
        }, timeoutMs);
        pending.set(id, { resolve, timer });
        writeEvent(EventType.Fetch, { id, url, headers });
      });
    },
  };
}
