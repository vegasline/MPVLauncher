// MPVLauncher browser relay. MIT licensed.
var __create = Object.create;
var __defProp = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __getProtoOf = Object.getPrototypeOf;
var __hasOwnProp = Object.prototype.hasOwnProperty;
var __copyProps = (to, from, except, desc) => {
  if (from && typeof from === "object" || typeof from === "function") {
    for (let key of __getOwnPropNames(from))
      if (!__hasOwnProp.call(to, key) && key !== except)
        __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
  }
  return to;
};
var __toESM = (mod, isNodeMode, target) => (target = mod != null ? __create(__getProtoOf(mod)) : {}, __copyProps(
  // If the importer is in node compatibility mode or this is not an ESM
  // file that has been converted to a CommonJS file using a Babel-
  // compatible transform (i.e. "__esModule" has not been set), then set
  // "default" to the CommonJS "module.exports" for node compatibility.
  isNodeMode || !mod || !mod.__esModule ? __defProp(target, "default", { value: mod, enumerable: true }) : target,
  mod
));

// src/index.js
var import_node_fs2 = __toESM(require("node:fs"), 1);
var import_node_os = __toESM(require("node:os"), 1);
var import_node_path2 = __toESM(require("node:path"), 1);

// src/args.js
var CONTRACT_VERSION = 1;
var NUMERIC = /* @__PURE__ */ new Map([
  ["--mpv-pid", "mpvPid"],
  ["--launcher-pid", "launcherPid"],
  ["--contract", "contract"],
  ["--timeout", "timeoutSeconds"]
]);
var FLAGS = /* @__PURE__ */ new Map([
  ["--headless", "headless"],
  ["--verbose", "verbose"],
  ["--detach", "detach"],
  ["--broker", "broker"]
]);
var STRINGS = /* @__PURE__ */ new Map([
  ["--stream", "stream"],
  ["--page", "page"],
  ["--title", "title"],
  ["--session-dir", "sessionDir"],
  ["--log-file", "logFile"],
  ["--browser", "browser"]
]);
function parseArgs(argv) {
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
    verbose: false
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

// src/protocol.js
var EventType = Object.freeze({
  Ready: "ready",
  Progress: "progress",
  Error: "error",
  Log: "log",
  // Asks the launcher to fetch a URL inside the viewer's own browser tab,
  // because the site refuses the same request from outside that context.
  Fetch: "fetch"
});
function writeEvent(type, fields) {
  const payload = { type, contract: 1, ...fields };
  try {
    process.stdout.write(JSON.stringify(payload) + "\n");
  } catch {
  }
}
function writeError(code, message) {
  writeEvent(EventType.Error, { code, message });
}

// src/broker.js
function createBrokerFetcher(log = () => {
}, timeoutMs = 6e4) {
  const pending = /* @__PURE__ */ new Map();
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
          error: line.status ? "" : "no reply"
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
    }
  };
}

// src/browser.js
var import_node_child_process = require("node:child_process");
var import_node_fs = __toESM(require("node:fs"), 1);
var import_node_path = __toESM(require("node:path"), 1);

// src/cdp.js
var CdpClient = class {
  constructor(writable, readable) {
    this.writable = writable;
    this.readable = readable;
    this.buffer = Buffer.alloc(0);
    this.nextId = 1;
    this.pending = /* @__PURE__ */ new Map();
    this.handlers = /* @__PURE__ */ new Map();
    this.closed = false;
    this.readable.on("data", (chunk) => this.onData(chunk));
    this.readable.on("error", () => this.dispose());
    this.readable.on("close", () => this.dispose());
    this.onClose = null;
  }
  onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    let index;
    while ((index = this.buffer.indexOf(0)) >= 0) {
      const raw = this.buffer.subarray(0, index).toString("utf8");
      this.buffer = this.buffer.subarray(index + 1);
      let message;
      try {
        message = JSON.parse(raw);
      } catch {
        continue;
      }
      if (message.id && this.pending.has(message.id)) {
        const entry = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) {
          entry.reject(new Error(`${entry.method}: ${message.error.message || "error"}`));
        } else {
          entry.resolve(message.result || {});
        }
      } else if (message.method) {
        const handler = this.handlers.get(message.method);
        if (handler) handler(message.params || {}, message.sessionId);
      }
    }
  }
  send(method, params = {}, sessionId = void 0, timeoutMs = 3e4) {
    if (this.closed) return Promise.reject(new Error("cdp closed"));
    const id = this.nextId++;
    const message = { id, method, params };
    if (sessionId) message.sessionId = sessionId;
    this.writable.write(JSON.stringify(message) + "\0");
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method}: timeout`));
      }, timeoutMs);
      this.pending.set(id, {
        method,
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        }
      });
    });
  }
  on(method, handler) {
    this.handlers.set(method, handler);
  }
  dispose() {
    if (this.closed) return;
    this.closed = true;
    for (const entry of this.pending.values()) entry.reject(new Error("cdp closed"));
    this.pending.clear();
    if (this.onClose) this.onClose();
  }
  async waitForReady(attempts = 40, delayMs = 250) {
    let lastError = null;
    for (let i = 0; i < attempts; i++) {
      try {
        return await this.send("Browser.getVersion", {}, void 0, 5e3);
      } catch (error) {
        lastError = error;
        await new Promise((resolve) => setTimeout(resolve, delayMs));
      }
    }
    throw lastError || new Error("browser did not answer");
  }
};

// src/browser.js
var KNOWN = [
  ["edge", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe"],
  ["edge", "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe"],
  ["chrome", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"],
  ["chrome", "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe"],
  ["chrome", import_node_path.default.join(process.env.LOCALAPPDATA || "", "Google\\Chrome\\Application\\chrome.exe")]
];
function queryAppPath(exe) {
  const key = `HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`;
  const hkcu = `HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`;
  for (const k of [hkcu, key]) {
    const result = (0, import_node_child_process.spawnSync)("reg", ["query", k, "/ve"], { windowsHide: true, encoding: "utf8" });
    if (result.status !== 0 || !result.stdout) continue;
    const match = /REG_SZ\s+(.+?)\s*$/m.exec(result.stdout);
    if (match && import_node_fs.default.existsSync(match[1])) return match[1].replace(/^"|"$/g, "");
  }
  return null;
}
function findBrowser(preferred = "") {
  const order = preferred ? [preferred] : ["chrome", "edge"];
  for (const kind of order) {
    for (const [candidateKind, candidatePath] of KNOWN) {
      if (candidateKind !== kind) continue;
      if (candidatePath && import_node_fs.default.existsSync(candidatePath)) return { path: candidatePath, kind };
    }
    const exe = kind === "edge" ? "msedge.exe" : "chrome.exe";
    const fromRegistry = queryAppPath(exe);
    if (fromRegistry) return { path: fromRegistry, kind };
  }
  return null;
}
var COMMON_FLAGS = [
  "--no-first-run",
  "--no-default-browser-check",
  "--disable-extensions",
  "--disable-component-extensions-with-background-pages",
  "--disable-sync",
  "--disable-background-networking",
  "--disable-background-timer-throttling",
  "--disable-backgrounding-occluded-windows",
  "--disable-features=Translate,MediaRouter,OptimizationHints,InterestFeedContentSuggestions",
  "--metrics-recording-only",
  "--mute-audio",
  "--autoplay-policy=no-user-gesture-required",
  "--disable-popup-blocking",
  "--remote-debugging-pipe"
];
async function launchBrowser(browser, options = {}) {
  const userDataDir = options.userDataDir;
  if (!userDataDir) throw new Error("userDataDir is required");
  import_node_fs.default.mkdirSync(userDataDir, { recursive: true });
  const args = [...COMMON_FLAGS, `--user-data-dir=${userDataDir}`];
  if (options.headless) {
    args.unshift("--headless=new");
  } else {
    args.push("--window-size=1280,720", "--window-position=-32000,-32000");
  }
  args.push("about:blank");
  const child = (0, import_node_child_process.spawn)(browser.path, args, {
    stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"],
    windowsHide: true
  });
  const client = new CdpClient(child.stdio[3], child.stdio[4]);
  let exited = false;
  child.on("exit", () => {
    exited = true;
    client.dispose();
  });
  try {
    await client.waitForReady();
  } catch (error) {
    try {
      child.kill();
    } catch {
    }
    throw new Error(`browser did not start: ${error.message}`);
  }
  return {
    client,
    child,
    get exited() {
      return exited;
    },
    close() {
      if (exited) return Promise.resolve();
      return client.send("Browser.close", {}, void 0, 4e3).catch(() => {
      }).then(
        () => new Promise((resolve) => {
          const timer = setTimeout(() => {
            try {
              child.kill();
            } catch {
            }
            resolve();
          }, 4e3);
          child.on("exit", () => {
            clearTimeout(timer);
            resolve();
          });
        })
      );
    }
  };
}
async function showWindow(client, targetId) {
  try {
    const { windowId } = await client.send(
      "Browser.getWindowForTarget",
      { targetId },
      void 0,
      5e3
    );
    await client.send(
      "Browser.setWindowBounds",
      { windowId, bounds: { windowState: "normal" } },
      void 0,
      5e3
    );
    await client.send(
      "Browser.setWindowBounds",
      { windowId, bounds: { left: 60, top: 60, width: 1280, height: 720 } },
      void 0,
      5e3
    );
    return true;
  } catch {
    return false;
  }
}

// src/urls.js
var PRIVATE_V4 = /^(0\.|10\.|127\.|169\.254\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/;
function encodeRemote(url) {
  return Buffer.from(url, "utf8").toString("base64url");
}
function decodeRemote(value) {
  return Buffer.from(value, "base64url").toString("utf8");
}
function isHttpUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}
function isPrivateHost(hostname) {
  let host = String(hostname || "").toLowerCase();
  if (!host) return true;
  if (host.includes("://")) {
    try {
      host = new URL(host).hostname.toLowerCase();
    } catch {
      return true;
    }
  }
  if (!host) return true;
  if (host === "localhost" || host.endsWith(".localhost")) return true;
  if (host.startsWith("[") || host.includes(":")) return true;
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) {
    return PRIVATE_V4.test(host) || host === "0.0.0.0";
  }
  return false;
}
function bareUrl(value) {
  try {
    const url = new URL(value);
    return url.origin + url.pathname;
  } catch {
    return String(value || "").split(/[?#]/)[0];
  }
}
var MEDIA_EXTENSIONS = [
  ".m3u8",
  ".m3u",
  ".mpd",
  ".mp4",
  ".m4v",
  ".mkv",
  ".webm",
  ".mov",
  ".avi",
  ".ts",
  ".m2ts",
  ".mp3",
  ".aac",
  ".flac",
  ".ogg",
  ".opus",
  ".wav"
];
function hasMediaExtension(value) {
  const path3 = bareUrl(value).toLowerCase();
  return MEDIA_EXTENSIONS.some((ext) => path3.endsWith(ext));
}
function sameOrigin(a, b) {
  try {
    return new URL(a).origin === new URL(b).origin;
  } catch {
    return false;
  }
}

// src/driver.js
var PLAY_SCRIPT = `(() => {
  const out = { jw: typeof window.jwplayer, video: !!document.querySelector("video"), clicks: 0 };
  try {
    if (window.jwplayer && typeof window.jwplayer === "function") {
      const player = window.jwplayer();
      if (player && typeof player.play === "function") { player.play(); out.jwPlay = true; }
    }
  } catch (e) { out.jwError = String(e && e.message ? e.message : e); }
  const video = document.querySelector("video");
  if (video) {
    try { video.muted = true; const p = video.play(); if (p && p.catch) p.catch(() => {}); out.videoPlay = true; }
    catch (e) { out.videoError = String(e && e.message ? e.message : e); }
  }
  const selectors = [
    ".jw-icon-playback", ".jw-display-icon-container", "[aria-label*='Play']",
    "[aria-label*='oynat' i]", ".vjs-big-play-button", "button.play", ".player-play", ".play-button"
  ];
  for (const selector of selectors) {
    const nodes = document.querySelectorAll(selector);
    for (const node of nodes) { try { node.click(); out.clicks++; } catch (e) {} }
  }
  return out;
})()`;
var FETCH_SCRIPT = (url, pageUrl, headers) => `(async () => {
  try {
    const response = await fetch(${JSON.stringify(url)}, {
      credentials: "same-origin",
      referrer: ${JSON.stringify(pageUrl)},
      headers: ${JSON.stringify(headers || {})}
    });
    const buffer = new Uint8Array(await response.arrayBuffer());
    let binary = "";
    const CHUNK = 0x8000;
    for (let i = 0; i < buffer.length; i += CHUNK) {
      binary += String.fromCharCode.apply(null, buffer.subarray(i, i + CHUNK));
    }
    return {
      ok: true,
      status: response.status,
      contentType: response.headers.get("content-type") || "",
      length: buffer.length,
      body: btoa(binary)
    };
  } catch (e) {
    return { ok: false, error: String(e && e.message ? e.message : e) };
  }
})()`;
function looksLikeStreamUrl(candidate, pageUrl) {
  if (!candidate || !sameOrigin(candidate, pageUrl)) return false;
  let parsed;
  let page;
  try {
    parsed = new URL(candidate);
    page = new URL(pageUrl);
  } catch {
    return false;
  }
  if (parsed.pathname === page.pathname) return false;
  if (hasMediaExtension(candidate)) return false;
  if (isPrivateHost(parsed.hostname)) return false;
  if (/\/cdn-cgi\/|\/challenge-platform\/|turnstile/i.test(parsed.pathname)) return false;
  const segments = parsed.pathname.split("/").filter(Boolean);
  const last = segments[segments.length - 1] || "";
  if (!last || last.includes(".")) return false;
  return /[A-Za-z0-9_-]{12,}/.test(last);
}
var CHALLENGE_REQUEST = /challenges\.cloudflare\.com|cdn-cgi\/challenge-platform|\/network-challenge\/|turnstile/i;
var RESTRICTED_PAGE = /eri[sş]im k[sı][ıi]tland[ıi]|access restricted|barındırma a[gğ]ı|hosting network|anonimleştirme|anonymi[sz]ation|vpn or proxy|not allowed from this network/i;
var VERDICT_SCRIPT = `(() => {
  const text = (document.title + " " + (document.body ? document.body.innerText : "")).slice(0, 1200);
  return { title: document.title, text };
})()`;
async function findStream(client, options) {
  const { pageUrl, timeoutMs = 45e3, log = () => {
  }, verbose = false, onProgress = () => {
  } } = options;
  const target = await client.send("Target.createTarget", { url: "about:blank" });
  const attached = await client.send("Target.attachToTarget", { targetId: target.targetId, flatten: true });
  const sessionId = attached.sessionId;
  const state = { found: null, requests: 0, failed: false, challenge: false, blocked: false };
  const startedAt = Date.now();
  const deadline = startedAt + timeoutMs;
  await client.send("Page.enable", {}, sessionId);
  await client.send("Runtime.enable", {}, sessionId);
  await client.send(
    "Fetch.enable",
    {
      // Only the request kinds a player uses for its stream are paused. Pausing
      // everything (urlPattern "*") leaves some sites unable to finish loading
      // their own document, which looks exactly like a page that never plays.
      patterns: ["XHR", "Fetch", "Media", "Other"].map((resourceType) => ({
        urlPattern: "*",
        resourceType,
        requestStage: "Request"
      }))
    },
    sessionId
  );
  client.on("Fetch.requestPaused", (params, eventSession) => {
    if (eventSession !== sessionId) return;
    const url = params.request.url;
    state.requests++;
    if (CHALLENGE_REQUEST.test(url)) state.challenge = true;
    if (verbose) log(`request ${url.slice(0, 160)}`);
    if (!state.found && looksLikeStreamUrl(url, pageUrl)) {
      state.found = url;
      client.send("Fetch.failRequest", { requestId: params.requestId, errorReason: "Aborted" }, sessionId).catch(() => {
      });
      return;
    }
    client.send("Fetch.continueRequest", { requestId: params.requestId }, sessionId).catch(() => {
    });
  });
  await client.send("Page.navigate", { url: pageUrl }, sessionId, 6e4);
  log(`page opened (${state.requests} requests so far)`);
  let waited = 0;
  while (!state.found && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    waited += 1500;
    try {
      await client.send("Runtime.evaluate", { expression: PLAY_SCRIPT, returnByValue: true }, sessionId);
    } catch {
    }
    if (waited >= 3e3) {
      try {
        const verdict = await client.send(
          "Runtime.evaluate",
          { expression: VERDICT_SCRIPT, returnByValue: true },
          sessionId,
          5e3
        );
        const value = verdict && verdict.result && verdict.result.value || {};
        if (RESTRICTED_PAGE.test(`${value.title || ""} ${value.text || ""}`)) {
          state.blocked = true;
          log(`the site refuses this network: ${String(value.title || "").slice(0, 80)}`);
          break;
        }
      } catch {
      }
    }
    onProgress(Date.now() - startedAt);
    if (waited % 9e3 === 0) log(`still waiting for the stream (${state.requests} requests)`);
    if (state.challenge && waited >= 4500 && !state.windowShown) {
      state.windowShown = true;
      log("the site is asking for human verification; showing the window");
      try {
        await onChallenge(target.targetId);
      } catch {
      }
    }
  }
  if (!state.found) {
    return {
      sessionId,
      streamUrl: null,
      requests: state.requests,
      challenge: state.challenge,
      blocked: state.blocked
    };
  }
  await client.send("Fetch.disable", {}, sessionId).catch(() => {
  });
  return { sessionId, streamUrl: state.found, requests: state.requests };
}
async function openNeutralPage(client, sessionId, pageUrl, log = () => {
}) {
  const neutral = new URL("/robots.txt", pageUrl).toString();
  try {
    await client.send("Page.navigate", { url: neutral }, sessionId, 3e4);
    await new Promise((resolve) => setTimeout(resolve, 1200));
    log("neutral page ready");
  } catch (error) {
    log(`neutral page failed, keep the current document: ${error.message}`);
  }
}
function createRemoteFetcher(client, sessionId, pageUrl, log = () => {
}) {
  return async function fetchRemote(url, { headers = {} } = {}) {
    let result;
    try {
      result = await client.send(
        "Runtime.evaluate",
        {
          expression: FETCH_SCRIPT(url, pageUrl, headers),
          awaitPromise: true,
          returnByValue: true
        },
        sessionId,
        9e4
      );
    } catch (error) {
      log(`fetch failed: ${error.message}`);
      return { ok: false, status: 0, error: error.message, body: Buffer.alloc(0) };
    }
    const value = result && result.result && result.result.value || {};
    if (!value.ok) {
      log(`fetch rejected: ${value.error || "unknown"}`);
      return { ok: false, status: 0, error: value.error || "fetch failed", body: Buffer.alloc(0) };
    }
    return {
      ok: true,
      status: value.status,
      contentType: value.contentType || "",
      body: Buffer.from(value.body || "", "base64")
    };
  };
}

// src/playlist.js
var URI_ATTRIBUTE = /URI="([^"]*)"/g;
function isPlaylist(text) {
  if (typeof text !== "string") return false;
  const head = text.slice(0, 64).replace(/^\uFEFF/, "").trimStart();
  if (head.startsWith("#EXTM3U")) return true;
  return text.includes("#EXT-X-STREAM-INF") || text.includes("#EXTINF:");
}
function rewritePlaylist(text, remoteUrl, toRelay) {
  const uris = [];
  const base = remoteUrl;
  const map = (raw) => {
    const value = raw.trim();
    if (!value) return raw;
    let absolute;
    try {
      absolute = new URL(value, base).toString();
    } catch {
      return raw;
    }
    if (!/^https?:\/\//i.test(absolute)) return raw;
    uris.push(absolute);
    return toRelay(absolute);
  };
  const lines = String(text).replace(/\r\n/g, "\n").split("\n");
  const out = lines.map((line) => {
    if (line.startsWith("#")) {
      if (line.includes('URI="')) {
        return line.replace(URI_ATTRIBUTE, (_m, uri) => `URI="${map(uri)}"`);
      }
      return line;
    }
    if (!line.trim()) return line;
    return map(line);
  });
  return { text: out.join("\n"), uris };
}

// src/server.js
var import_node_http = __toESM(require("node:http"), 1);
var import_node_crypto = __toESM(require("node:crypto"), 1);

// src/mpegts.js
var PACKET = 188;
var SYNC = 71;
function startsWithTsSync(buffer) {
  if (!buffer || buffer.length < PACKET * 3) return false;
  return buffer[0] === SYNC && buffer[PACKET] === SYNC && buffer[PACKET * 2] === SYNC;
}
function stripToTsSync(buffer) {
  if (!buffer) return buffer;
  const limit = Math.min(buffer.length - PACKET * 3, 4096);
  for (let i = 1; i <= limit; i++) {
    if (buffer[i] === SYNC && buffer[i + PACKET] === SYNC && buffer[i + PACKET * 2] === SYNC) {
      return buffer.subarray(i);
    }
  }
  return buffer;
}

// src/server.js
var MAX_PARALLEL = 5;
var ALLOWED_METHODS = /* @__PURE__ */ new Set(["GET", "HEAD"]);
function jsonError(res, status, code, message) {
  const body = JSON.stringify({ error: true, code, message });
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
    "cache-control": "no-store"
  });
  res.end(body);
}
function hostIsLocal(value) {
  if (!value) return false;
  const name = value.split(":")[0].toLowerCase();
  return name === "127.0.0.1" || name === "localhost" || name === "[::1]";
}
var RelayServer = class {
  constructor({ fetchRemote, log = () => {
  } }) {
    this.fetchRemote = fetchRemote;
    this.log = log;
    this.token = import_node_crypto.default.randomBytes(16).toString("hex");
    this.allowedHosts = /* @__PURE__ */ new Set();
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
    }
    return null;
  }
  urlFor(remoteUrl) {
    this.allowHost(remoteUrl);
    return `http://127.0.0.1:${this.port}/${this.token}/${encodeRemote(remoteUrl)}`;
  }
  async start() {
    this.server = import_node_http.default.createServer((req, res) => {
      this.handle(req, res).catch((error) => {
        this.log(`relay handler failed: ${error.message}`);
        if (!res.headersSent) jsonError(res, 502, "relay_failed", "Relay request failed.");
        else res.destroy();
      });
    });
    this.server.keepAliveTimeout = 5e3;
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
      const playlist = isPlaylist(body.toString("latin1"));
      const isText = upstream.text || playlist;
      if (playlist) {
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
          total: null
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
      "access-control-allow-origin": "*"
    };
    if (upstream && upstream.contentRange) headers["content-range"] = upstream.contentRange;
    headers["accept-ranges"] = "bytes";
    headers["content-length"] = String(body.length);
    res.writeHead(status, headers);
    if (res.req && res.req.method === "HEAD") res.end();
    else res.end(body);
  }
};

// src/index.js
var MAX_PLAYLIST_BYTES = 4 * 1024 * 1024;
function logLine(file, line) {
  if (!file) return;
  try {
    import_node_fs2.default.appendFileSync(file, `${(/* @__PURE__ */ new Date()).toISOString()} ${line}
`);
  } catch {
  }
}
function processAlive(pid) {
  if (!pid) return true;
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
function removeDir(dir) {
  try {
    import_node_fs2.default.rmSync(dir, { recursive: true, force: true });
  } catch {
  }
}
function profileHost(pageUrl) {
  try {
    return new URL(pageUrl).hostname.replace(/[^a-z0-9.-]+/gi, "_") || "default";
  } catch {
    return "default";
  }
}
async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (!options.page && !options.broker) {
    writeError("no_page", "No page URL was provided.");
    return 1;
  }
  if (options.page) {
    let pageUri = null;
    try {
      pageUri = new URL(options.page);
    } catch {
      pageUri = null;
    }
    if (!pageUri || pageUri.protocol !== "http:" && pageUri.protocol !== "https:") {
      writeError("bad_page", "The page URL must be an http(s) address.");
      return 1;
    }
  }
  const sessionDir = options.sessionDir || import_node_fs2.default.mkdtempSync(import_node_path2.default.join(import_node_os.default.tmpdir(), "mpvrelay-"));
  import_node_fs2.default.mkdirSync(sessionDir, { recursive: true });
  const profileDir = import_node_path2.default.join(
    import_node_path2.default.dirname(sessionDir),
    "profiles",
    profileHost(options.page)
  );
  const fallbackProfileDir = import_node_path2.default.join(sessionDir, "profile");
  const logFile = options.logFile || "";
  const log = (line) => logLine(logFile, line);
  const brokerFetcher = options.broker ? createBrokerFetcher(log) : null;
  let browser = null;
  let server = null;
  let closed = false;
  const cleanup = () => {
    if (closed) return;
    closed = true;
    try {
      if (server) server.stop();
    } catch {
    }
    try {
      if (browser) browser.close();
    } catch {
    }
    removeDir(sessionDir);
  };
  let attachedMpvPid = 0;
  let stdinBuffer = "";
  const shutdown = (code) => {
    cleanup();
    process.exit(code);
  };
  process.on("SIGINT", () => shutdown(130));
  process.on("SIGTERM", () => shutdown(143));
  const pipeClosed = () => {
    if (!options.detach) shutdown(0);
  };
  process.stdin.on("end", pipeClosed);
  process.stdin.on("close", pipeClosed);
  process.stdin.on("data", (chunk) => {
    stdinBuffer += chunk.toString("utf8");
    let index;
    while ((index = stdinBuffer.indexOf("\n")) >= 0) {
      const line = stdinBuffer.slice(0, index).trim();
      stdinBuffer = stdinBuffer.slice(index + 1);
      if (!line) continue;
      let command = null;
      try {
        command = JSON.parse(line);
      } catch {
        continue;
      }
      if (brokerFetcher && brokerFetcher.handle(command)) continue;
      if (command && command.cmd === "attach" && Number(command.mpvPid) > 0) {
        attachedMpvPid = Number(command.mpvPid);
      }
    }
  });
  process.stdin.resume();
  const pageOrigin = new URL(options.page || options.stream).origin;
  let fetchRemote = null;
  let capture;
  if (brokerFetcher && options.stream) {
    log("brokered capture: no browser will be started");
    capture = { streamUrl: options.stream, sessionId: null, requests: 0 };
    fetchRemote = brokerFetcher.fetchRemote;
  } else {
    const found = findBrowser(options.browser);
    if (!found) {
      writeError(
        "no_browser",
        "Neither Edge nor Chrome was found. Install one, or pick a browser in Settings."
      );
      return 2;
    }
    log(`browser: ${found.kind} ${found.path}`);
    writeEvent(EventType.Progress, { phase: "launching" });
    try {
      browser = await launchBrowser(found, {
        userDataDir: profileDir,
        headless: options.headless
      });
    } catch (error) {
      log(`profile unusable (${error.message}), using a temporary one`);
      browser = await launchBrowser(found, {
        userDataDir: fallbackProfileDir,
        headless: options.headless
      });
    }
    browser.client.onClose = () => shutdown(0);
    await browser.client.waitForReady();
    log("browser ready");
    writeEvent(EventType.Progress, { phase: "navigating" });
    capture = await findStream(browser.client, {
      pageUrl: options.page,
      timeoutMs: options.timeoutSeconds * 1e3,
      log,
      verbose: options.verbose,
      onProgress: (elapsedMs) => writeEvent(EventType.Progress, {
        phase: "waiting",
        elapsedMs
      }),
      onChallenge: (targetId) => {
        if (options.headless) return false;
        return showWindow(browser.client, targetId);
      }
    });
  }
  if (!capture.streamUrl) {
    if (capture.blocked) {
      writeError(
        "access_restricted",
        "The site refuses this network (VPN, proxy or hosting range), so it never serves the player."
      );
      return 7;
    }
    if (capture.challenge) {
      writeError(
        "challenge_blocked",
        "The site is asking for human verification, which the relay does not bypass."
      );
      return 6;
    }
    writeError(
      "stream_not_found",
      `The player never asked for a stream (${capture.requests} requests seen).`
    );
    return 5;
  }
  const streamLog = new URL(capture.streamUrl);
  log(`stream: ${streamLog.host}${streamLog.pathname.replace(/[^/]+$/, "<token>")}`);
  if (browser) {
    writeEvent(EventType.Progress, { phase: "neutral" });
    await openNeutralPage(browser.client, capture.sessionId, options.page, log);
    fetchRemote = createRemoteFetcher(
      browser.client,
      capture.sessionId,
      options.page,
      log
    );
  }
  server = new RelayServer({ fetchRemote, log });
  server.allowHost(capture.streamUrl);
  server.allowHost(pageOrigin);
  const base = await server.start();
  const first = await fetchRemote(capture.streamUrl, {});
  if (!first.ok) {
    writeError(
      first.status === 403 ? "token_expired" : "stream_unavailable",
      first.status === 403 ? "The stream token was rejected. Reload the page and try again." : `The stream answered HTTP ${first.status}.`
    );
    return 3;
  }
  const head = first.body.subarray(0, 64).toString("latin1");
  const playlist = isPlaylist(head) || /mpegurl/i.test(first.contentType);
  if (playlist) {
    if (first.body.length > MAX_PLAYLIST_BYTES) {
      writeError("playlist_too_large", "The playlist is unexpectedly large.");
      return 4;
    }
    const rewritten = rewritePlaylist(
      first.body.toString("utf8"),
      capture.streamUrl,
      (remote) => {
        server.allowHost(remote);
        return server.urlFor(remote);
      }
    );
    const localUrl = server.urlFor(capture.streamUrl);
    writeEvent(EventType.Ready, {
      url: localUrl,
      playlist: "captured",
      segments: rewritten.uris.length
    });
  } else {
    writeEvent(EventType.Ready, {
      url: server.urlFor(capture.streamUrl),
      playlist: "direct"
    });
  }
  const startedAt = Date.now();
  const tick = setInterval(() => {
    if (options.mpvPid && !processAlive(options.mpvPid)) shutdown(0);
    if (attachedMpvPid && !processAlive(attachedMpvPid)) shutdown(0);
    if (options.detach) {
      if (!attachedMpvPid && Date.now() - startedAt > 3e5) shutdown(0);
      return;
    }
    if (options.launcherPid && !processAlive(options.launcherPid)) shutdown(0);
  }, 2e3);
  tick.unref?.();
  await new Promise(() => {
  });
  return 0;
}
main().then((code) => {
  if (typeof code === "number" && code !== 0) process.exit(code);
}).catch((error) => {
  writeError("unexpected", String(error && error.message ? error.message : error));
  process.exit(1);
});
