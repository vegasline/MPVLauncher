// Local HTTP server that mpv reads from. Everything it serves is fetched inside
// the browser by the driver, so the origin cookies and the site's own rules
// apply; the launcher and mpv never touch the site directly.
import http from "node:http";
import crypto from "node:crypto";
import { decodeRemote, encodeRemote, isHttpUrl, isPrivateHost } from "./urls.js";
import { isPlaylist, rewritePlaylist } from "./playlist.js";
import { startsWithTsSync, stripToTsSync } from "./mpegts.js";

const MAX_PARALLEL = 5;
const ALLOWED_METHODS = new Set(["GET", "HEAD"]);

function jsonError(res, status, code, message) {
  const body = JSON.stringify({ error: true, code, message });
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
    "cache-control": "no-store",
  });
  res.end(body);
}

function hostIsLocal(value) {
  if (!value) return false;
  const name = value.split(":")[0].toLowerCase();
  return name === "127.0.0.1" || name === "localhost" || name === "[::1]";
}

export class RelayServer {
  constructor({ fetchRemote, log = () => {} }) {
    this.fetchRemote = fetchRemote;
    this.log = log;
    this.token = crypto.randomBytes(16).toString("hex");
    this.allowedHosts = new Set();
    this.server = null;
    this.port = 0;
    this.active = 0;
    this.waiters = [];
  }

  allowHost(remoteUrl) {
    try {
      const url = new URL(remoteUrl);
      if (isHttpUrl(url.href) && !isPrivateHost(url.hostname)) {
        this.allowedHosts.add(url.host.toLowerCase());
        return url.host.toLowerCase();
      }
    } catch {
      /* ignore */
    }
    return null;
  }

  urlFor(remoteUrl) {
    this.allowHost(remoteUrl);
    return `http://127.0.0.1:${this.port}/${this.token}/${encodeRemote(remoteUrl)}`;
  }

  async start() {
    this.server = http.createServer((req, res) => {
      this.handle(req, res).catch((error) => {
        this.log(`relay handler failed: ${error.message}`);
        if (!res.headersSent) jsonError(res, 502, "relay_failed", "Relay request failed.");
        else res.destroy();
      });
    });
    this.server.keepAliveTimeout = 5000;
    await new Promise((resolve, reject) => {
      this.server.once("error", reject);
      this.server.listen(0, "127.0.0.1", () => {
        this.port = this.server.address().port;
        resolve();
      });
    });
    return this;
  }

  async stop() {
    if (!this.server) return;
    await new Promise((resolve) => this.server.close(() => resolve()));
    this.server = null;
  }

  async acquire() {
    if (this.active < MAX_PARALLEL) {
      this.active++;
      return;
    }
    await new Promise((resolve) => this.waiters.push(resolve));
    this.active++;
  }

  release() {
    this.active = Math.max(0, this.active - 1);
    const next = this.waiters.shift();
    if (next) next();
  }

  async handle(req, res) {
    if (!hostIsLocal(req.headers.host)) {
      jsonError(res, 403, "bad_host", "Unexpected Host header.");
      return;
    }
    if (!ALLOWED_METHODS.has(req.method || "")) {
      jsonError(res, 405, "bad_method", "Only GET and HEAD are served.");
      return;
    }
    const rawPath = (req.url || "/").split("?")[0];
    const parts = rawPath.split("/").filter(Boolean);
    if (parts.length !== 2 || parts[0] !== this.token) {
      jsonError(res, 404, "not_found", "Unknown relay path.");
      return;
    }
    const remote = decodeRemote(parts[1]);
    if (!remote || !isHttpUrl(remote)) {
      jsonError(res, 400, "bad_target", "Relay target is not an http(s) URL.");
      return;
    }
    let host;
    try {
      host = new URL(remote).host.toLowerCase();
    } catch {
      jsonError(res, 400, "bad_target", "Relay target could not be parsed.");
      return;
    }
    if (isPrivateHost(host) || !this.allowedHosts.has(host)) {
      jsonError(res, 403, "host_not_allowed", "Relay target host is not allowed.");
      return;
    }

    this.log(`request ${req.method || "GET"} ${host}${new URL(remote).pathname.slice(0, 48)}`);

    await this.acquire();
    try {
      const range = req.headers.range;
      const upstream = await this.fetchRemote(remote, { range, method: req.method === "HEAD" ? "HEAD" : "GET" });
      // Status 0 is the extension saying "I could not fetch this at all"
      // (blocked, CORS, dead tab). Without this branch the server tried to send
      // HTTP 0 and the handler crashed with "Invalid status code: 0".
      if (!upstream.status) {
        this.log(`upstream failed: ${upstream.error || "no status"}`);
        jsonError(res, 502, "upstream_unreachable", `Relay fetch failed: ${upstream.error || "no status"}.`);
        return;
      }
      if (upstream.status === 403) {
        jsonError(res, 403, "token_expired", "The site refused this stream (token invalid or expired).");
        return;
      }
      if (upstream.status >= 400) {
        jsonError(res, 502, "upstream_error", `Upstream returned ${upstream.status}.`);
        return;
      }

      const body = upstream.body || Buffer.alloc(0);
      this.log(`answer ${upstream.status} ${body.length}B ${upstream.contentType || "no-ctype"}`);
      // isPlaylist() takes text: handed a Buffer it always says no, and the
      // playlist then reaches mpv as application/octet-stream, which it refuses
      // to parse. Sniff a latin1 copy and keep the utf8 one for rewriting.
      const playlist = isPlaylist(body.toString("latin1"));
      const isText = upstream.text || playlist;
      if (playlist) {
        // Only the page's own host needs the tab's session. CDN segments play
        // straight from mpv: relaying them means one brokered fetch per
        // segment, and a cross-origin one can stall inside the page.
        const relayOrigin = new URL(remote).origin;
        const rewritten = rewritePlaylist(body.toString("utf8"), remote, (url) => {
          try {
            return new URL(url).origin === relayOrigin ? this.urlFor(url) : url;
          } catch {
            return url;
          }
        });
        for (const uri of rewritten.uris) this.allowHost(uri);
        this.send(res, 200, "application/vnd.apple.mpegurl", Buffer.from(rewritten.text, "utf8"), {
          range: false,
          total: null,
        });
        return;
      }

      if (!isText && upstream.imageLike && !startsWithTsSync(body)) {
        const fixed = stripToTsSync(body);
        if (fixed) {
          this.send(res, upstream.status, "video/mp2t", fixed, upstream);
          return;
        }
      }
      this.send(res, upstream.status, upstream.contentType || "application/octet-stream", body, upstream);
    } finally {
      this.release();
    }
  }

  send(res, status, contentType, body, upstream) {
    const headers = {
      "content-type": contentType,
      "cache-control": "no-store",
      "access-control-allow-origin": "*",
    };
    if (upstream && upstream.contentRange) headers["content-range"] = upstream.contentRange;
    headers["accept-ranges"] = "bytes";
    headers["content-length"] = String(body.length);
    res.writeHead(status, headers);
    if (res.req && res.req.method === "HEAD") res.end();
    else res.end(body);
  }
}

export { jsonError };
