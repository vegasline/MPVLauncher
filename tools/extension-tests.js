/**
 * MPV Launcher extension test suite.
 *
 * Run:  node tools/extension-tests.js      (from the repo root)
 *
 * Covers the areas that regressed before:
 *   - reCAPTCHA false positives must not blank the scan
 *   - .vtt / SRT / TTML are never captured
 *   - the capture layer must survive a Firefox MV3 event-page restart
 *   - i18n values must not be quote-wrapped
 */
const fs = require("fs");
const vm = require("vm");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const EXT = path.join(ROOT, "extension");
const read = (f) => fs.readFileSync(path.join(EXT, f), "utf8");

const types = read("media_types.js");
const capture = read("network_capture.js");
const background = read("background.js");
const content = read("content_script.js");
const i18nRaw = read("i18n.js");

const REPORTED =
  "https://prx-vi-a-1.vmpx.online/hls2/03/02917/u5a7bn68clte_n/index-v1-a1.m3u8" +
  "?t=3B1WRUxVXiHlgxGJ9GE6gQnVefzGLCXEO5kaPAGng9g=&s=1790721129&e=43200&v=&srv=box-1643-d1&i=0.4&sp=0&asn=12735";

let pass = 0, fail = 0;
const check = (name, cond, detail = "") => {
  if (cond) { console.log(`  PASS  ${name}`); pass++; }
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); fail++; };
};
const section = (t) => console.log(`\n=== ${t} ===`);

/* ------------------------------------------------------------------ helpers */

function typesCtx() {
  const ctx = { URL, Object, Array, Map, Set, JSON, String, Number, Date, Math, RegExp, console, isNaN };
  ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
  vm.createContext(ctx);
  vm.runInContext(types, ctx, { filename: "media_types.js" });
  return ctx;
}

/**
 * Boots media_types + network_capture + background, in manifest order.
 *
 * @param sessionStore   storage.session backing store; shared across boots to
 *                       model an event page that is suspended and re-evaluated
 * @param shape          "firefox" | "chrome"
 * @param honourExtraHeaders  whether the browser tolerates "extraHeaders"
 * @param permOptions    { granted, canRequest, hasApi } for host permission
 */
function boot(sessionStore, shape = "firefox", honourExtraHeaders = false, permOptions = {}) {
  const { granted = true, canRequest = true, hasApi = true } = permOptions;
  const listeners = { beforeSend: [], headersReceived: [] };
  const messageHandlers = [];
  const calls = [];
  const asked = [];

  const evt = (name, bag) => ({
    addListener(fn, filter, extraInfo) {
      calls.push({ name, extraInfo: extraInfo || [] });
      if (shape === "firefox" && !honourExtraHeaders && (extraInfo || []).includes("extraHeaders")) {
        throw new Error('Invalid value for argument 2: "extraHeaders"');
      }
      bag.push(fn);
    },
    removeListener() {}
  });

  const store = {
    local: { get: () => Promise.resolve({}), set: () => Promise.resolve() },
    session: {
      get(keys) {
        const out = {};
        for (const k of [].concat(keys || [])) {
          if (sessionStore && k in sessionStore) out[k] = JSON.parse(JSON.stringify(sessionStore[k]));
        }
        return Promise.resolve(out);
      },
      set(obj) {
        for (const [k, v] of Object.entries(obj)) {
          if (sessionStore) sessionStore[k] = JSON.parse(JSON.stringify(v));
        }
        return Promise.resolve();
      }
    }
  };

  const webext = {
    webRequest: {
      onBeforeSendHeaders: evt("onBeforeSendHeaders", listeners.beforeSend),
      onHeadersReceived: evt("onHeadersReceived", listeners.headersReceived)
    },
    runtime: {
      onMessage: { addListener: (fn) => messageHandlers.push(fn) },
      sendNativeMessage: null
    },
    storage: store,
    permissions: {
      contains: (spec) => { asked.push(["contains", spec]); return Promise.resolve(granted); },
      request: (spec) => {
        asked.push(["request", spec]);
        return canRequest ? Promise.resolve(true) : Promise.reject(new Error("no request API"));
      }
    }
  };
  if (!hasApi) delete webext.permissions;

  const ctx = {
    browser: webext, chrome: webext,
    URL, Object, Array, Map, Set, JSON, String, Number, Boolean, Date, Math, RegExp, Error,
    isNaN, parseInt, parseFloat, console, setTimeout, clearTimeout
  };
  if (shape === "chrome") delete ctx.browser;
  ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
  vm.createContext(ctx);

  vm.runInContext(types, ctx, { filename: "media_types.js" });
  vm.runInContext(capture, ctx, { filename: "network_capture.js" });
  vm.runInContext(background, ctx, { filename: "background.js" });

  // Host permission is resolved asynchronously; tests must wait for it before
  // asserting on the listener.
  const ready = vm.runInContext("MPV_Network && MPV_Network.ready ? MPV_Network.ready() : Promise.resolve()", ctx);

  return { ctx, listeners, messageHandlers, calls, webext, ready, asked, session: sessionStore };
}

/** Boots and waits for the permission check to settle. */
async function bootReady(...args) {
  const b = boot(...args);
  await b.ready;
  return b;
}

function fire(b, url, ct, extra = {}) {
  const headers = [{ name: "Content-Type", value: ct }];
  if (extra.length !== undefined) headers.push({ name: "Content-Length", value: String(extra.length) });
  const req = {
    tabId: extra.tabId ?? 1, requestId: extra.requestId || "req-" + Math.random(),
    url, documentUrl: "https://site.com/watch"
  };
  for (const fn of b.listeners.beforeSend) fn({ ...req, requestHeaders: extra.requestHeaders || [] });
  for (const fn of b.listeners.headersReceived) fn({ ...req, responseHeaders: headers });
}

function ask(b, action) {
  return new Promise((resolve) => {
    for (const h of b.messageHandlers) {
      let done = false;
      const ret = h({ action, tabId: 1 }, { tab: { id: 1 } }, (r) => { done = true; resolve(r); });
      if (ret === true) return;
      if (done) return;
    }
    resolve(undefined);
  });
}

/* ---------------------------------------------------------- DOM shim for cs.js */

function el(tag, attrs = {}) {
  return {
    tagName: tag.toUpperCase(), attrs, children: [], dataset: {},
    getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
    get src() { return this.attrs.src || ""; },
    set src(v) { this.attrs.src = v; },
    get currentSrc() { return this.attrs.currentSrc || ""; },
    get textContent() { return this.attrs.__text || ""; },
    set textContent(v) { this.attrs.__text = v; },
    get innerText() { return this.attrs.innerText || ""; },
    querySelectorAll(sel) { return sel.includes("video") ? (this.attrs.__videos || []) : []; },
    querySelector(sel) {
      if (sel.includes("video")) return (this.attrs.__videos || [])[0] || null;
      if (sel.includes("iframe")) return (this.attrs.__iframes || [])[0] || null;
      return this.attrs.__match || null;
    }
  };
}

function runContentScript({ url, videos = [], widgets = [], innerText = "", hasMatch = null }) {
  const vids = videos.map((src) => el("video", { src }));
  const body = el("body", { innerText });
  body.attrs.__videos = vids;
  body.attrs.__match = hasMatch || widgets[0] || null;
  const html = el("html", {});
  body.children = vids.concat(widgets);
  html.children = [body];

  const ctx = {
    document: {
      documentElement: html, body, baseURI: url,
      querySelectorAll: (sel) => (sel.includes("video") ? vids : []),
      querySelector: (sel) => {
        if (sel.includes("g-recaptcha") || sel.includes("cf-turnstile")) return widgets[0] || null;
        if (sel.includes("video") || sel.includes("iframe")) return vids[0] || null;
        return null;
      }
    },
    location: new URL(url),
    URL, Object, Array, Set, Map, JSON, String, Number, Boolean, RegExp, Error, Date, Math,
    isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent,
    performance: { getEntriesByType: () => [] },
    console, setTimeout, clearTimeout
  };
  ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
  ctx.top = ctx; ctx.parent = ctx;
  vm.createContext(ctx);
  return vm.runInContext(content, ctx, { filename: "content_script.js" });
}

/* ---------------------------------------------------------------------- tests */

(async () => {

  section("subtitles are never captured");
  {
    const p = typesCtx();
    for (const [url, ct] of [
      ["https://cdn.x.com/s.en.vtt", "text/vtt"],
      ["https://cdn.x.com/s.en.srt", "application/x-subrip"],
      ["https://cdn.x.com/s.en.ttml", "application/ttml+xml"],
      ["https://cdn.x.com/s.en.dfxp", "application/ttaf+xml"],
      ["https://cdn.x.com/s", "text/vtt"],
      ["https://cdn.x.com/s.en.vtt", "text/plain"]
    ]) {
      check(`${path.basename(url)} (${ct}) ignored`, p.mpvClassifyResponse(url, ct) === null);
    }
    check("no subtitle entries in the table",
      p.MPV_MEDIA_TYPES.filter((t) => t.category === "subtitles").length === 0);
    check("showSubtitles gone from defaults", p.MPV_FILTER_DEFAULTS.showSubtitles === undefined);
  }

  section("streams and files are still captured");
  {
    for (const [url, ct, expect] of [
      ["https://cdn.x.com/a.m3u8", "application/vnd.apple.mpegurl", "hls"],
      ["https://cdn.x.com/m", "application/dash+xml", "dash"],
      ["https://cdn.x.com/f.f4m", "application/f4m", "hds"],
      ["https://cdn.x.com/v.mp4", "video/mp4", "mp4"],
      ["https://cdn.x.com/a.ts", "video/mp2t", "ts"],
      ["https://cdn.x.com/a.mp3", "audio/mpeg", "mp3"],
      ["https://cdn.x.com/a.webm", "video/webm", "webm"]
    ]) {
      const b = await bootReady({});
      fire(b, url, ct);
      const items = await b.ctx.MPV_Network.getItems(1);
      check(`${expect} captured`, items.length === 1 && items[0].typeId === expect, JSON.stringify(items));
    }
  }

  section("non-media traffic is ignored");
  {
    const b = await bootReady({});
    for (const [url, ct] of [
      ["https://x.com/a.js", "application/javascript"],
      ["https://x.com/a.css", "text/css"],
      ["https://x.com/a.png", "image/png"],
      ["https://x.com/api", "application/json"],
      ["https://www.google.com/recaptcha/api2/bframe", "text/html"],
      ["https://challenges.cloudflare.com/turnstile/v0/api.js", "application/javascript"]
    ]) {
      fire(b, url, ct);
    }
    check("scripts/css/images/json/captcha ignored", (await b.ctx.MPV_Network.getItems(1)).length === 0);
  }

  section("the reported m3u8 on both browsers");
  {
    for (const shape of ["chrome", "firefox"]) {
      const b = await bootReady({}, shape, true);
      fire(b, REPORTED, "application/vnd.apple.mpegurl");
      const items = await b.ctx.MPV_Network.getItems(1);
      check(`${shape}: captured`, items.length === 1 && items[0].typeId === "hls", JSON.stringify(items));
    }
    for (const ct of ["", "application/octet-stream", "text/plain", "binary/octet-stream"]) {
      const b = await bootReady({}, "firefox", true);
      fire(b, REPORTED, ct);
      const items = await b.ctx.MPV_Network.getItems(1);
      check(`ct="${ct || "(none)"}" -> hls`, items.length === 1 && items[0].typeId === "hls");
    }
  }

  section("listener registration is robust");
  {
    const ff = await bootReady({}, "firefox", true);
    check("firefox: registered", ff.ctx.MPV_Network.isRegistered() === true);
    check("firefox: onHeadersReceived installed", ff.listeners.headersReceived.length === 1);

    const cr = await bootReady({}, "chrome", true);
    check("chrome: registered", cr.ctx.MPV_Network.isRegistered() === true);
    check("chrome: onHeadersReceived installed", cr.listeners.headersReceived.length === 1);

    // Firefox that rejects extraHeaders must still end up registered
    const strict = await bootReady({}, "firefox", false);
    check("extraHeaders rejection recovered", strict.ctx.MPV_Network.isRegistered() === true);
    fire(strict, REPORTED, "application/x-mpegurl");
    check("captures after the fallback", (await strict.ctx.MPV_Network.getItems(1)).length === 1);
  }

  section("webRequest missing entirely");
  {
    const ctx = {
      chrome: { runtime: { sendNativeMessage: null } },
      URL, Object, Array, Map, Set, JSON, String, Number, Date, Math, RegExp,
      console, setTimeout, clearTimeout
    };
    ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
    vm.createContext(ctx);
    vm.runInContext(types, ctx, { filename: "media_types.js" });
    vm.runInContext(capture, ctx, { filename: "network_capture.js" });
    check("reports registered=false instead of throwing",
      vm.runInContext("MPV_Network.isRegistered()", ctx) === false);
    const items = await vm.runInContext("MPV_Network.getItems(1)", ctx);
    check("getItems stays safe", Array.isArray(items) && items.length === 0);
  }

  section("Firefox event page restart (MV3 background is not persistent)");
  {
    const session = {};
    const first = await bootReady(session);
    check("listener registered", first.ctx.MPV_Network.isRegistered() === true);
    fire(first, REPORTED, "application/vnd.apple.mpegurl");
    fire(first, "https://cdn.site.com/seg/1.ts", "video/mp2t");

    const live = await ask(first, "get_network_media");
    check("captured while running", live && live.items && live.items.length === 2,
      JSON.stringify(live && live.items && live.items.map((i) => i.url)));
    check("reports available", live && live.available === true);

    await new Promise((r) => setTimeout(r, 600));
    check("persisted to storage.session", Object.keys(session).length > 0,
      Object.keys(session).join(","));

    const second = await bootReady(session);
    const restored = await ask(second, "get_network_media");
    check("background answers after restart", restored && restored.success === true);
    check("items restored", restored && restored.items && restored.items.length === 2,
      JSON.stringify(restored && restored.items && restored.items.map((i) => i.url)));
    check("m3u8 restored", restored && restored.items.some((i) => i.url === REPORTED));
    check("listener re-registered", second.ctx.MPV_Network.isRegistered() === true);

    fire(second, "https://cdn.site.com/other.m3u8", "application/x-mpegurl");
    await new Promise((r) => setTimeout(r, 600));
    const third = await bootReady(session);
    const after = await ask(third, "get_network_media");
    check("a later capture also survives", after && after.items && after.items.length === 3,
      JSON.stringify(after && after.items && after.items.map((i) => i.url)));
  }

  section("per-tab isolation, de-duplication and headers");
  {
    const b = await bootReady({});
    fire(b, "https://cdn.x.com/a.m3u8", "application/x-mpegurl", { tabId: 1, requestId: "a" });
    fire(b, "https://cdn.x.com/b.m3u8", "application/x-mpegurl", { tabId: 2, requestId: "b" });
    fire(b, "https://cdn.x.com/a.m3u8", "application/x-mpegurl", { tabId: 1, requestId: "c" });
    check("tab 1 deduplicated", (await b.ctx.MPV_Network.getItems(1)).length === 1);
    check("tab 2 isolated", (await b.ctx.MPV_Network.getItems(2)).length === 1);

    const h = await bootReady({});
    fire(h, "https://cdn.x.com/h.m3u8", "application/x-mpegurl", {
      requestHeaders: [
        { name: "Referer", value: "https://site.com/watch" },
        { name: "User-Agent", value: "Mozilla/5.0" },
        { name: "Cookie", value: "s=secret" },
        { name: "Authorization", value: "Bearer t" }
      ]
    });
    const got = await h.ctx.MPV_Network.getItems(1);
    const names = (got[0] && got[0].headers || []).map((x) => x.name.toLowerCase());
    check("referer kept", names.includes("referer"), JSON.stringify(names));
    check("user-agent kept", names.includes("user-agent"), JSON.stringify(names));
    check("cookie dropped", !names.includes("cookie"), JSON.stringify(names));
    check("authorization dropped", !names.includes("authorization"), JSON.stringify(names));

    const s = await bootReady({});
    fire(s, "https://cdn.x.com/v.mp4", "video/mp4", { length: 5242880 });
    check("content-length recorded", (await s.ctx.MPV_Network.getItems(1))[0].sizeBytes === 5242880);
  }

  section("filters");
  {
    const p = typesCtx();
    const base = { showStreams: true, showFiles: true, minSizeKb: 0, blacklist: [] };
    const stream = { category: "stream", typeId: "hls", url: "https://x.com/a.m3u8" };
    const file = { category: "files", typeId: "mp4", url: "https://x.com/v.mp4", sizeBytes: 5e6 };
    const ad = { category: "files", typeId: "mp4", url: "https://ads.t.com/v.mp4", sizeBytes: 9e9 };

    check("stream passes", p.mpvPassesFilter(stream, base) === true);
    check("file passes", p.mpvPassesFilter(file, base) === true);
    check("stream hidden when off", p.mpvPassesFilter(stream, { ...base, showStreams: false }) === false);
    check("file hidden when off", p.mpvPassesFilter(file, { ...base, showFiles: false }) === false);
    check("small file dropped", p.mpvPassesFilter({ ...file, sizeBytes: 1000 }, { ...base, minSizeKb: 512 }) === false);
    check("manifest ignores size gate", p.mpvPassesFilter(stream, { ...base, minSizeKb: 1e6 }) === true);
    check("blacklist by url", p.mpvPassesFilter(ad, { ...base, blacklist: ["ads.t.com"] }) === false);
    check("blacklist case-insensitive", p.mpvPassesFilter(ad, { ...base, blacklist: ["ADS.T"] }) === false);
    check("blacklist keeps others", p.mpvPassesFilter(file, { ...base, blacklist: ["ads.t.com"] }) === true);
  }

  section("content script: captcha handling");
  {
    const a = runContentScript({
      url: "https://site.com/watch",
      videos: ["https://cdn.site.com/v/720/index.m3u8"],
      innerText: "Verify you are human before continuing"
    });
    check("challenge flagged", a.challenged === true, JSON.stringify(a));
    check("media NOT suppressed", a.media.length > 0, JSON.stringify(a));
    check("m3u8 still reported", a.media.some((m) => m.url.includes("index.m3u8")));

    const b = runContentScript({
      url: "https://site.com/watch",
      videos: ["https://cdn.site.com/v/720/index.m3u8"],
      widgets: [el("div", { class: "g-recaptcha" })],
      innerText: "Watch the full movie online"
    });
    check("invisible captcha not flagged", b.challenged === false, JSON.stringify(b));
    check("media found", b.media.length > 0);

    const c = runContentScript({
      url: "https://site.com/",
      widgets: [el("div", { class: "g-recaptcha" })],
      innerText: "Just a moment... Checking your browser before accessing"
    });
    check("genuine interstitial flagged", c.challenged === true, JSON.stringify(c));

    const d = runContentScript({
      url: "https://www.google.com/recaptcha/api2/anchor?k=x",
      videos: []
    });
    check("captcha URL flagged", d.challenged === true, JSON.stringify(d));
  }

  section("content script: m3u8 sources");
  {
    const cases = [
      ["https://site.com/p", { text: 'var u = "https://cdn.x.com/hls/master.m3u8?token=1";' }, "plain m3u8 in script"],
      ["https://site.com/p", { text: 'var s = "\\u002F\\u002Fcdn.x.com\\u002Fmaster.m3u8";' }, "unicode-escaped URL"],
      ["https://site.com/p", { text: 'var s = "//cdn.x.com/hls/master.m3u8";' }, "protocol-relative URL"],
      ["https://site.com/p", { text: 'var c = {"sources":[{"file":"https://cdn.x.com/720/index.m3u8"}]};' }, "player config"],
      ["https://site.com/p", { text: 'var s = {"manifest":"https://cdn.x.com/chunklist.m3u8"};' }, "manifest key"],
      ["https://site.com/p", { text: 'var s = {"file":"https://cdn.x.com/1080.mp4"};' }, "progressive mp4"]
    ];
    for (const [url, script, label] of cases) {
      const s = el("script", { type: "text/javascript" });
      s.textContent = script.text;
      const r = runContentScript({ url, videos: [] });
      // re-run with the script present
      const ctxScripts = [s];
      const body = el("body", { innerText: "" });
      const html = el("html", {});
      body.children = ctxScripts;
      html.children = [body];
      const ctx = {
        document: {
          documentElement: html, body, baseURI: url,
          querySelectorAll: (sel) => (sel === "script" ? ctxScripts : []),
          querySelector: () => null
        },
        location: new URL(url),
        URL, Object, Array, Set, Map, JSON, String, Number, Boolean, RegExp, Error, Date, Math,
        isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent,
        performance: { getEntriesByType: () => [] },
        console, setTimeout, clearTimeout
      };
      ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
      ctx.top = ctx; ctx.parent = ctx;
      vm.createContext(ctx);
      const res = vm.runInContext(content, ctx, { filename: "content_script.js" });
      check(label, res.media.length > 0, JSON.stringify(res.media));
    }
  }

  section("content script: noise and recaptcha frames");
  {
    const results = [];
    const results2 = runContentScript({ url: "https://x.com/", videos: [] });
    check("no media on an empty page", results2.media.length === 0, JSON.stringify(results2));
  }

  section("video <source> handling");
  {
    let pass = 0, fail = 0;
    const check = (n, c, d = "") => {
      if (c) { console.log(`  PASS  ${n}`); pass++; }
      else { console.log(`  FAIL  ${n}${d ? " -> " + d : ""}`); fail++; }
    };
    
    const CDN = "https://cdn.streamhost.net/api/v4/video/12345/stream?policy=eyJTdGF0ZW1lbnQiOltFT0tFTiwidHlwIjoiaG1sIn0&signature=abc123";
    
    /** Builds a context whose document returns whatever elements we hand it. */
    function run({ videos = [], sources = [], others = [], url = "https://site.com/watch", innerText = "" }) {
      const vids = videos;
      const srcs = sources;
      const rest = others;
    
      function qsa(sel) {
        const s = sel.trim();
        if (s.startsWith("video") || s.startsWith("audio")) return vids;
        if (s.startsWith("source")) return srcs;
        if (s.startsWith("[")) {
          // attribute selector: match against every provided element
          const names = [...s.matchAll(/\[([\w-]+)/g)].map((m) => m[1]);
          return rest.filter((e) => names.some((n) => e.attrs && e.attrs[n] !== undefined));
        }
        return rest.filter((e) => s.startsWith(e.tagName.toLowerCase()));
      }
    
      const body = {
        innerText,
        querySelectorAll: qsa,
        querySelector: (sel) => qsa(sel)[0] || null
      };
      const docEl = { dataset: {} };
    
      const ctx = {
        document: {
          documentElement: docEl, body, baseURI: url,
          querySelectorAll: qsa,
          querySelector: (sel) => {
            if (sel.startsWith("video") || sel.startsWith("audio")) return vids[0] || null;
            return qsa(sel)[0] || null;
          }
        },
        location: new URL(url),
        URL, Object, Array, Set, Map, JSON, String, Number, Boolean, RegExp, Error, Date, Math,
        isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent,
        performance: { getEntriesByType: () => [] },
        console, setTimeout, clearTimeout
      };
      ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
      ctx.top = ctx; ctx.parent = ctx;
      vm.createContext(ctx);
      return vm.runInContext(content, ctx, { filename: "content_script.js" });
    }
    
    function mkSource(attrs) {
      return {
        tagName: "SOURCE", attrs, children: [], dataset: {},
        getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
        get src() { return this.attrs.src || ""; },
        get textContent() { return ""; },
        set textContent(v) {},
        get innerText() { return ""; },
        querySelectorAll: () => [],
        querySelector: () => null,
        get parentElement() { return this.parent || null; }
      };
    }
    
    function mkVideo(attrs) {
      return {
        tagName: "VIDEO", attrs, children: [], dataset: {},
        getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
        get src() { return this.attrs.src || ""; },
        get currentSrc() { return this.attrs.currentSrc || ""; },
        get textContent() { return ""; },
        set textContent(v) {},
        get innerText() { return ""; },
        querySelectorAll: (sel) => (sel.startsWith("source") ? (this.children || []) : []),
        querySelector: () => null,
        get parentElement() { return this.parent || null; }
      };
    }
    
    const HAS = (r) => r.media.length > 0;
    const URLS = (r) => r.media.map((m) => m.url);
    
    console.log("=== the exact reported markup ===");
    {
      const v = mkVideo({ crossorigin: "anonymous", "aria-hidden": "true", preload: "metadata", playsinline: "" });
      const s = mkSource({ src: "https://rhyzoku-4.asia/file/tau-video/abc7b72e-5aef-416b-b803-c67d310d8b7d.mp4", type: "video/mp4", "data-vds": "" });
      v.children = [s];
      const r = run({ videos: [v], sources: [s] });
      check("reported <video><source> detected", HAS(r), JSON.stringify(URLS(r)));
      check("url preserved", URLS(r).some((u) => u.includes("abc7b72e")), JSON.stringify(URLS(r)));
      check("type is video", r.media.some((m) => m.type === "video"), JSON.stringify(r.media));
    }
    
    console.log("");
    console.log("=== signed CDN url with no file extension ===");
    {
      const v = mkVideo({});
      const s = mkSource({ src: CDN, type: "video/mp4" });
      v.children = [s];
      const r = run({ videos: [v], sources: [s] });
      check("detected despite no extension", HAS(r), JSON.stringify(URLS(r)));
    }
    
    console.log("");
    console.log("=== type hint drives classification ===");
    {
      const v = mkVideo({});
      const s = mkSource({ src: CDN, type: "application/vnd.apple.mpegurl" });
      v.children = [s];
      const r = run({ videos: [v], sources: [s] });
      check("hls from the type attribute", r.media.some((m) => m.type === "hls"), JSON.stringify(r.media));
    }
    {
      const v = mkVideo({});
      const s = mkSource({ src: CDN, type: "application/dash+xml" });
      v.children = [s];
      const r = run({ videos: [v], sources: [s] });
      check("dash from the type attribute", r.media.some((m) => m.type === "dash"), JSON.stringify(r.media));
    }
    
    console.log("");
    console.log("=== url moved to a data-* attribute by the player ===");
    {
      const v = mkVideo({ "data-src": CDN, "data-url": CDN });
      const r = run({ videos: [v], sources: [] });
      check("data-src picked up", HAS(r), JSON.stringify(URLS(r)));
    }
    {
      const v = mkVideo({});
      const holder = {
        tagName: "DIV", attrs: { "data-src": CDN }, children: [], dataset: {},
        getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
        get textContent() { return ""; }, set textContent(v) {},
        get innerText() { return ""; },
        querySelectorAll: () => [], querySelector: () => null
      };
      v.parent = holder;
      // closest() exists in a real browser; model it via the parent chain
      v.closest = (sel) => {
        const wanted = sel.split(",").map((s) => s.trim());
        let node = v.parentElement;
        while (node) {
          for (const w of wanted) {
            const am = w.match(/^\[([\w-]+)\]$/);
            if (am && node.getAttribute(am[1])) return node;
          }
          node = node.parentElement;
        }
        return null;
      };
      const r = run({ videos: [v], sources: [] });
      check("container data-src picked up", HAS(r), JSON.stringify(URLS(r)));
    }
    
    console.log("");
    console.log("=== orphan <source> outside a video element ===");
    {
      const s = mkSource({ src: CDN, type: "video/mp4" });
      s.parent = { tagName: "BODY" };
      const r = run({ videos: [], sources: [s] });
      check("orphan source detected", HAS(r), JSON.stringify(URLS(r)));
    }
    
    console.log("");
    console.log("=== data-media-provider container ===");
    {
      // wrapper carries the marker, the inner element carries the URL
      const inner = {
        tagName: "DIV", attrs: { "data-file": CDN }, children: [], dataset: {},
        getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
        get textContent() { return ""; }, set textContent(v) {},
        get innerText() { return ""; },
        querySelectorAll: () => [], querySelector: () => null
      };
      const wrapper = {
        tagName: "DIV", attrs: { "data-media-provider": "" }, children: [inner], dataset: {},
        getAttribute(n) { return this.attrs[n] !== undefined ? this.attrs[n] : null; },
        get textContent() { return ""; }, set textContent(v) {},
        get innerText() { return ""; },
        querySelectorAll: (sel) => (sel === "*" ? [inner] : []),
        querySelector: () => null
      };
      const r = run({ videos: [], sources: [], others: [wrapper] });
      check("data-media container scanned", HAS(r), JSON.stringify(URLS(r)));
    }
  }

  section("iframe detection");
  {
    // Build a page with iframes and run the content script over it.
    function page(iframes) {
      const frames = iframes.map((f) => el("iframe", { src: f.src, width: f.w, height: f.h }));
      const body = el("body", { innerText: "" });
      const html = el("html", {});
      body.children = frames;
      html.children = [body];

      const ctx = {
        document: {
          documentElement: html, body, baseURI: "https://site.com/watch",
          querySelectorAll: (sel) => {
            if (sel.startsWith("iframe")) return frames;
            if (sel.includes("video")) return [];
            return [];
          },
          querySelector: () => null
        },
        location: new URL("https://site.com/watch"),
        URL, Object, Array, Set, Map, JSON, String, Number, Boolean, RegExp, Error, Date, Math,
        isNaN, parseInt, parseFloat, encodeURIComponent, decodeURIComponent,
        performance: { getEntriesByType: () => [] },
        console, setTimeout, clearTimeout
      };
      ctx.window = ctx; ctx.globalThis = ctx; ctx.self = ctx;
      ctx.top = ctx; ctx.parent = ctx;
      vm.createContext(ctx);
      return vm.runInContext(content, ctx, { filename: "content_script.js" });
    }

    // Player-like frames on unknown domains: detected thanks to the path hint
    const shouldDetect = [
      { src: "https://custom-cdn.tv/embed/abc123", label: "/embed/ path" },
      { src: "https://myplayer.io/player?id=9", label: "/player path" },
      { src: "https://videos.example.org/watch?v=1", label: "/watch path" },
      { src: "https://unknown-host.example/xyz", w: 720, h: 405, label: "large unknown frame" }
    ];
    for (const c of shouldDetect) {
      const r = page([c]);
      check(c.label, r.media.some((m) => m.type === "iframe"),
        JSON.stringify(r.media));
    }

    // A frame URL carrying a manifest is reported as that stream, which is more
    // specific - and more useful - than a generic iframe entry.
    const yt = page([{ src: "https://www.youtube.com/embed/dQw4w9WgXcQ" }]);
    check("youtube frame classified as youtube", yt.media.some((m) => m.type === "youtube"),
      JSON.stringify(yt.media));
    const hlsFrame = page([{ src: "https://stream.site.net/player/v-123.m3u8" }]);
    check("frame with m3u8 classified as hls", hlsFrame.media.some((m) => m.type === "hls"),
      JSON.stringify(hlsFrame.media));

    // Widgets, trackers and tiny frames: must stay out
    const shouldReject = [
      { src: "https://www.facebook.com/plugins/like.php", label: "facebook widget" },
      { src: "https://twitter.com/widget/tweet", label: "twitter widget" },
      { src: "https://www.google.com/recaptcha/api2/bframe?k=x", label: "recaptcha frame" },
      { src: "https://challenges.cloudflare.com/turnstile/v0/api.js", label: "cloudflare turnstile" },
      { src: "https://ads.tracker.com/pixel", w: 1, h: 1, label: "1x1 tracking pixel" },
      { src: "https://comments.example.com/thread", w: 320, h: 90, label: "short comment box" },
      { src: "about:blank", label: "about:blank" }
    ];
    for (const c of shouldReject) {
      const r = page([c]);
      check(c.label + " ignored", !r.media.some((m) => m.type === "iframe"),
        JSON.stringify(r.media));
    }

    // A large frame on an otherwise unknown domain is offered as a player
    const bigUnknown = page([{ src: "https://unknown-host.example/xyz", w: 720, h: 405 }]);
    check("large unknown frame accepted", bigUnknown.media.some((m) => m.type === "iframe"),
      JSON.stringify(bigUnknown.media));

    // Multiple frames on one page are all listed
    const many = page([
      { src: "https://a.tv/embed/1" },
      { src: "https://b.tv/embed/2" },
      { src: "https://c.tv/embed/3" }
    ]);
    check("all three player frames listed",
      many.media.filter((m) => m.type === "iframe").length === 3,
      JSON.stringify(many.media.map((m) => m.url)));
  }

  section("iframe filter");
  {
    const p = typesCtx();
    const base = { showStreams: true, showFiles: true, showIframes: true, minSizeKb: 0, blacklist: [] };
    const frame = { category: "iframe", type: "iframe", url: "https://site.com/embed/1" };

    check("iframe passes by default", p.mpvPassesFilter(frame, base) === true);
    check("iframe hidden when off", p.mpvPassesFilter(frame, { ...base, showIframes: false }) === false);
    check("default has showIframes", p.MPV_FILTER_DEFAULTS.showIframes === true);

    // a stream must be unaffected by the iframe toggle
    const stream = { category: "stream", typeId: "hls", url: "https://x.com/a.m3u8" };
    check("stream unaffected by iframe toggle", p.mpvPassesFilter(stream, { ...base, showIframes: false }) === true);
  }

  // ---- host permission flow (Firefox does not grant these automatically) ----
  {

      console.log("=== permission DENIED (the Firefox failure case) ===");
      {
        const b = await bootReady(undefined, "firefox", false, { granted: false });
        await b.ctx.MPV_Network.ready();
        check("permission was checked", b.asked.some((a) => a[0] === "contains"), JSON.stringify(b.asked.map((a) => a[0])));
        check("listener NOT installed", b.listeners.headersReceived.length === 0);
        check("reports not registered", b.ctx.MPV_Network.isRegistered() === false);
        check("reports no host permission", b.ctx.MPV_Network.hasHostPermission() === false);

        fire(b, REPORTED, "application/vnd.apple.mpegurl");
        const items = await b.ctx.MPV_Network.getItems(1);
        check("captures nothing without permission", items.length === 0, JSON.stringify(items));

        const status = await ask(b, "get_permission_status");
        check("popup learns permission is missing",
          status && status.hostPermission === false && status.available === false, JSON.stringify(status));
      }

      console.log("");
      console.log("=== permission GRANTED (normal case) ===");
      {
        const b = await bootReady(undefined, "firefox", false, { granted: true });
        await b.ctx.MPV_Network.ready();
        check("listener installed", b.listeners.headersReceived.length === 1);
        check("reports registered", b.ctx.MPV_Network.isRegistered() === true);
        check("reports permission granted", b.ctx.MPV_Network.hasHostPermission() === true);

        fire(b, REPORTED, "application/vnd.apple.mpegurl");
        const items = await b.ctx.MPV_Network.getItems(1);
        check("captures the stream", items.length === 1, JSON.stringify(items));
      }

      console.log("");
      console.log("=== user grants the permission later ===");
      {
        const b = await bootReady(undefined, "firefox", false, { granted: false });
        await b.ctx.MPV_Network.ready();
        check("initially no listener", b.listeners.headersReceived.length === 0);

        const res = await ask(b, "request_permission");
        check("request succeeded", res && res.success === true && res.hostPermission === true, JSON.stringify(res));
        check("listener installed after the grant", b.listeners.headersReceived.length === 1);
        check("reports registered", b.ctx.MPV_Network.isRegistered() === true);

        fire(b, REPORTED, "application/vnd.apple.mpegurl");
        const items = await b.ctx.MPV_Network.getItems(1);
        check("captures after the grant", items.length === 1, JSON.stringify(items));
      }

      console.log("");
      console.log("=== user denies the prompt ===");
      {
        const b = await bootReady(undefined, "firefox", false, { granted: false });
        await b.ctx.MPV_Network.ready();
        const perms = b.ctx.browser.permissions;
        perms.request = () => Promise.resolve(false);
        const res = await ask(b, "request_permission");
        check("denial reported honestly", res && res.success === false, JSON.stringify(res));
        check("still no listener", b.listeners.headersReceived.length === 0);
      }

      console.log("");
      console.log("=== no permissions API at all (older/other shape) ===");
      {
        const b = await bootReady(undefined, "firefox", false, { hasApi: false });
        await b.ctx.MPV_Network.ready();
        check("assumes the manifest grant", b.ctx.MPV_Network.hasHostPermission() === true);
        check("listener installed", b.listeners.headersReceived.length === 1);
        fire(b, REPORTED, "application/vnd.apple.mpegurl");
        const items = await b.ctx.MPV_Network.getItems(1);
        check("captures normally", items.length === 1, JSON.stringify(items));
      }

      console.log("");
      console.log("=== registering twice must not duplicate listeners ===");
      {
        const b = await bootReady(undefined, "firefox", false, { granted: true });
        await b.ctx.MPV_Network.ready();
        await b.ctx.MPV_Network.ready();
        const again = await b.ctx.MPV_Network.requestPermission();
        check("request after grant is a no-op", again === true);
        check("still exactly one listener", b.listeners.headersReceived.length === 1,
          "count=" + b.listeners.headersReceived.length);
      }

      console.log("");
      console.log("=== Firefox event page restart still works with permissions ===");
      {
        // one shared session store models storage.session surviving the restart
        const session = {};
        const b = await bootReady(session, "firefox", false, { granted: true });
        await b.ctx.MPV_Network.ready();
        fire(b, REPORTED, "application/vnd.apple.mpegurl");
        await new Promise((r) => setTimeout(r, 600));
        check("persisted", Object.keys(session).length > 0, Object.keys(session).join(","));

        const b2 = await bootReady(session, "firefox", false, { granted: true });
        await b2.ctx.MPV_Network.ready();
        const res = await ask(b2, "get_network_media");
        check("restored after restart", res && res.items && res.items.length === 1,
          JSON.stringify(res && res.items));
      }
  }

  section("i18n table is clean");
  {
    // Slice from the declaration rather than assuming the file starts with it,
    // so a header comment can be added without breaking this.
    const decl = i18nRaw.lastIndexOf("const I18N =");
    check("declaration found", decl >= 0);
    const body = i18nRaw.slice(decl + "const I18N =".length).trim().replace(/;\s*$/, "");
    const data = vm.runInNewContext("(" + body + ")");
    const langs = Object.keys(data);
    check("13 languages", langs.length === 13, String(langs.length));

    let wrapped = 0, escaped = 0;
    for (const lang of langs) {
      for (const [k, v] of Object.entries(data[lang])) {
        if (typeof v !== "string") continue;
        if (v.startsWith('"') || v.endsWith('"')) wrapped++;
        if (/\\"/.test(v)) escaped++;
      }
    }
    check("no value is quote-wrapped", wrapped === 0, String(wrapped));
    check("no leftover escaped quotes", escaped === 0, String(escaped));

    const ref = Object.keys(data.en).sort().join(",");
    let mismatch = 0;
    for (const lang of langs) if (Object.keys(data[lang]).sort().join(",") !== ref) mismatch++;
    check("identical key sets in all languages", mismatch === 0, String(mismatch));

    for (const k of ["capture_off", "capture_off_hint", "diag_dom", "diag_net", "captcha_page", "filter_title"]) {
      check(`en/${k} present`, typeof data.en[k] === "string" && data.en[k].length > 0);
    }
    check("filter_subtitles removed", data.en.filter_subtitles === undefined);
  }

  section("all extension files parse");
  {
    for (const f of ["background.js", "content_script.js", "media_types.js",
                     "network_capture.js", "network_probe.js", "popup.js", "i18n.js"]) {
      let ok = true;
      try { new vm.Script(read(f), { filename: f }); } catch (e) { ok = false; }
      check(`${f} parses`, ok);
    }
  }

  /* ------------------------------------------------- packer reader safety */

  // The Dean Edwards unpacker used to rebuild packed code with new Function(),
  // which executed page-controlled code inside the extension's isolated world.
  // It now performs string substitution only. These tests pin that down.
  function unpackerCtx() {
    const src = content;
    const from = src.indexOf("    function unescapeJsLiteral(");
    const to = src.indexOf("\n    function harvest(");
    const block = src.slice(from, to).replace(/^ {4}/gm, "");
    const ctx = { console, JSON, String, Object, Array, Math, isFinite, parseInt };
    vm.createContext(ctx);
    // The helpers are lifted out of the content script's IIFE and returned so
    // they can be driven directly; nothing from the page is involved.
    const api = vm.runInContext(
      "(function(){" + block + "\nreturn {unpackDeanEdwards, unescapeJsLiteral, substituteTokens};})()",
      ctx, { filename: "unpack-block" });
    return { api, ctx };
  }

  /**
   * Blanks out comments and string literals so a scan for a code pattern is not
   * fooled by the pattern appearing inside documentation or a detection string.
   */
  const stripNonCode = (js) => js
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/(^|[^:])\/\/[^\n]*/g, "$1 ")
    .replace(/"(?:[^"\\\n]|\\.)*"/g, '""')
    .replace(/'(?:[^'\\\n]|\\.)*'/g, "''")
    .replace(/`(?:[^`\\]|\\.)*`/g, "``");

  section("packed-script reader never executes page code");
  {
    const { api, ctx } = unpackerCtx();
    const unpack = api.unpackDeanEdwards;

    const variants = {
      "split() form": `eval(function(p,a,c,k,e,d){while(c--){if(k[c])p=p.replace(c,k[c])}}return p}
(0,36,3,'fn,https://cdn.test/live/master.m3u8,window'.split('|'),0,{})`,
      "bare literal":  `eval(function(p,a,c,k,e,d){x=1}(0,36,2,"a,https://cdn.test/v.mp4"))`,
      "array form":    `eval(function(p,a,c,k,e,r){y=2}(36,["https://cdn.test/hls/i.m3u8","z"],0,{}))`,
    };
    for (const [label, packed] of Object.entries(variants)) {
      let out = "";
      try { out = unpack(packed); } catch (e) { out = ""; }
      check(`${label}: stream URL recovered`,
        /https:\/\/cdn\.test\/[^"'\s]+/.test(out), JSON.stringify(out).slice(0, 90));
    }

    // The core security property: nothing from the page runs.
    ctx.PWNED = false;
    let hostileOut = "";
    try {
      hostileOut = unpack(`eval(function(p,a,c,k,e,d){PWNED=true;return 1}(0,36,1,'x',0,{}))`);
    } catch (e) { /* inert is fine too */ }
    check("hostile payload does not execute", ctx.PWNED !== true);
    // Nothing ran, so the return value is irrelevant - it just must be inert
    // text (possibly empty, since a one-character dictionary is discarded).
    check("hostile payload yields inert text",
      typeof hostileOut === "string", typeof hostileOut);

    check("non-packed input is ignored", unpack("var a = 1;") === "");
    check("truncated input is ignored", unpack("eval(function(p,a,c,k,e,d){x") === "");
    check("unbalanced quotes are ignored", unpack("eval(function(p,a,c,k,e,d){x}(0,36,1,'oops)") === "");

    check("escape sequences are decoded",
      api.unescapeJsLiteral("\"a\\x2Eb\\u002Fc\\n\"") === "a.b/c\n");

    // Only the canonical base-36 spelling is a packer token; ordinary
    // identifiers that merely parse as base-36 must be left alone.
    const entries = ["", "", "REAL"];
    check("non-canonical tokens are not substituted",
      api.substituteTokens("alpha video", entries) === "alpha video",
      api.substituteTokens("alpha video", entries));
    check("canonical token is substituted",
      api.substituteTokens("go(2)", ["", "", "REAL"]).includes("REAL"),
      api.substituteTokens("go(2)", ["", "", "REAL"]));
  }

  section("content script contains no dynamic code evaluation");
  {
    // new Function / eval would hand a hostile page the whole extension API.
    const dynamic = [];
    for (const f of ["background.js", "content_script.js", "network_capture.js",
                     "network_probe.js", "popup.js", "media_types.js"]) {
      const body = stripNonCode(read(f));
      if (/\bnew\s+Function\s*\(/.test(body)) dynamic.push(`${f}: new Function`);
      if (/(^|[^.\w])eval\s*\(/.test(body)) dynamic.push(`${f}: eval`);
      if (/\binstanceof\s+Function\b/.test(body)) dynamic.push(`${f}: instanceof Function`);
      if (/\bsetTimeout\s*\(\s*["'`]/.test(body)) dynamic.push(`${f}: string setTimeout`);
    }
    check("no new Function / eval anywhere", dynamic.length === 0, dynamic.join(", "));
  }

  section("native message payload carries no credentials");
  {
    const body = stripNonCode(background);
    check("no cookies field is sent", !/\bcookies\b/.test(body));
    check("no authorization is forwarded", !/authorization/i.test(body));
    check("user-agent is forwarded", /userAgent/.test(body));
    check("referer is forwarded", /referrer/.test(body));
  }

  section("the clear-list button is wired end to end");
  {
    // background.js has handled "clear_network_media" since it was written,
    // and nothing ever sent it. A handler nobody calls is indistinguishable
    // from no feature at all, so the whole path is checked here rather than
    // only the parts that are new.
    const html = fs.readFileSync(path.join(EXT, "popup.html"), "utf8");

    check("popup.html has a clear button", /id="btn-clear"/.test(html));
    // It lives in the settings panel, not the header: it is a rare action and a
    // second icon beside rescan crowded the control that gets used often.
    check("clear button is inside the settings panel",
      /id="settings-panel"[\s\S]{0,2000}id="btn-clear"/.test(html));
    check("clear button is not in the section header",
      !/class="section-header"[\s\S]{0,400}id="btn-clear"/.test(html));
    check("clear button shows text rather than being icon-only",
      /id="btn-clear"[^>]*class="[^"]*btn-small/.test(html) &&
      /id="btn-clear"[^>]*>\s*\w/.test(html));
    check("clear button has a label beside it", /id="lbl-clear-list"/.test(html));

    const popup = fs.readFileSync(path.join(EXT, "popup.js"), "utf8");
    check("popup.js looks the button up", /getElementById\("btn-clear"\)/.test(popup));
    check("popup.js wires a click handler", /btnClear\.onclick/.test(popup));
    check("popup.js localises the button text and its label",
      /btnClear\.textContent = t\("btn_clear"\)/.test(popup) &&
      /lblClearList\.textContent = t\("clear_list_tooltip"\)/.test(popup));
    check("popup.js sends clear_network_media",
      /action:\s*"clear_network_media"/.test(popup));
    check("popup.js passes the tab id, so other tabs are untouched",
      /clear_network_media[\s\S]{0,200}tabId/.test(popup));
    check("popup.js empties its own copy of the results",
      /clearCurrentTab[\s\S]{0,600}domItems = \[\]/.test(popup) &&
      /clearCurrentTab[\s\S]{0,600}networkItems = \[\]/.test(popup));

    check("background.js handles clear_network_media",
      /action === "clear_network_media"/.test(background));
    check("background.js scopes the clear to one tab",
      /clearTab\(message\.tabId\)|typeof message\.tabId === "number"/.test(background));

    // The button's label has to exist in every language or it shows the raw key.
    const i18n = read("i18n.js");
    for (const k of ["clear_list_tooltip", "btn_clear", "list_cleared",
                    "list_cleared_hint", "list_cleared_status"]) {
      const n = (i18n.match(new RegExp(`"${k}":`, "g")) || []).length;
      check(`i18n defines ${k} in all 13 locales`, n === 13, `found ${n}`);
    }
  }

  console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
  process.exit(fail === 0 ? 0 : 1);
})();
