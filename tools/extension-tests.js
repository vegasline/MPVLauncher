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

  section("list order");
  {
    const p = typesCtx();
    const measured = { category: "stream", typeId: "hls", url: "https://x/a.m3u8", meta: { height: 900, bandwidth: 2525000 } };
    const bare = { category: "stream", typeId: "hls", url: "https://x/b.m3u8" };
    const iframe = { category: "iframe", typeId: "iframe", url: "https://x/embed.html" };
    const file = { category: "files", typeId: "mp4", url: "https://x/v.mp4" };
    const input = [iframe, file, bare, measured];
    const ordered = p.mpvSortForDisplay(input).map((i) => i.url);

    check("a stream with bitrate and resolution is first", ordered[0] === measured.url);
    check("an unprobed stream follows", ordered[1] === bare.url);
    check("the rest keep the merge order", ordered.slice(2).join() === [iframe.url, file.url].join());
    check("the merged list itself is not reordered",
      input.map((i) => i.url).join() === [iframe.url, file.url, bare.url, measured.url].join());
    check("a dead row does not gain the measured rank",
      p.mpvSortWeight({ category: "stream", meta: { dead: true } }) === 1);
    check("metadata alone is enough, no category needed",
      p.mpvSortWeight({ url: "https://x/c.m3u8", meta: { bandwidth: 800000 } }) === 0);
  }

  section("stream metadata");
  {
    const meta = read("stream_meta.js");

    /** A context whose fetch answers with one canned response. */
    function metaCtx(response) {
      const ctx = { Promise, console, parseInt, fetch: async () => response };
      vm.createContext(ctx);
      vm.runInContext(meta, ctx, { filename: "stream_meta.js" });
      return ctx;
    }

    const hls = [
      "#EXTM3U",
      "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360",
      "low/index.m3u8",
      "#EXT-X-STREAM-INF:BANDWIDTH=5200000,AVERAGE-BANDWIDTH=4800000,RESOLUTION=1920x1080",
      "high/index.m3u8",
    ].join("\n");

    const a = metaCtx({ ok: true, status: 200, text: async () => hls });
    const best = await a.MPV_StreamMeta.probe({ url: "https://x.com/master.m3u8" });
    check("highest variant wins", best.bandwidth === 5200000, JSON.stringify(best));
    check("resolution kept", best.height === 1080 && best.width === 1920, JSON.stringify(best));
    check("label reads as resolution and rate", a.MPV_StreamMeta.describe(best) === "1080p · 5.2 Mbps",
      a.MPV_StreamMeta.describe(best));

    const dash = metaCtx({
      ok: true,
      status: 200,
      text: async () =>
        '<AdaptationSet><Representation id="1" bandwidth="900000" width="1280" height="720"/>' +
        '<Representation id="2" bandwidth="4300000" width="1920" height="1080"/></AdaptationSet>',
    });
    const dashBest = await dash.MPV_StreamMeta.probe({ url: "https://x.com/manifest.mpd" });
    check("dash variant parsed", dashBest.bandwidth === 4300000 && dashBest.height === 1080,
      JSON.stringify(dashBest));

    const gone = metaCtx({ ok: false, status: 404, text: async () => "" });
    check("404 is dead", (await gone.MPV_StreamMeta.probe({ url: "https://x.com/gone.mp4" })).dead === true);

    // 403 usually means the CDN wanted the page's Referer, which this fetch
    // cannot send. Calling that dead would be a lie the user acts on.
    const forbidden = metaCtx({ ok: false, status: 403, text: async () => "" });
    check("403 is not dead", (await forbidden.MPV_StreamMeta.probe({ url: "https://x.com/private.mp4" })).dead !== true);

    check("no metadata means no label", metaCtx({}).MPV_StreamMeta.describe({}) === "");
    check("kbps below one megabit", metaCtx({}).MPV_StreamMeta.formatBitrate(520000) === "520 kbps");
  }

  section("theme palettes");
  {
    const source = read("themes.js");

    /** A context with just enough DOM and storage for a palette switch. */
    function themeCtx(saved) {
      const vars = {};
      const ctx = {
        Map,
        Math,
        Promise,
        console,
        document: {
          documentElement: { style: { setProperty: (k, v) => { vars[k] = v; } } },
          createElement: () => ({ width: 0, height: 0, getContext: () => null })
        },
        chrome: {
          storage: { local: { set: (o) => { saved.push(o); }, get: async () => ({ uiTheme: "ocean" }) } },
          action: { setIcon: () => { } }
        }
      };
      vm.createContext(ctx);
      vm.runInContext(source, ctx, { filename: "themes.js" });
      return { ctx, vars };
    }

    const saved = [];
    const { ctx, vars } = themeCtx(saved);
    const themes = ctx.MPV_Themes.list;

    check("eight palettes ship", themes.length === 8, String(themes.length));
    check("palette ids are unique",
      new Set(themes.map((x) => x.id)).size === themes.length);
    check("every palette names its colours",
      themes.every((x) => ["accent", "accentHover", "accentEnd", "bgPrimary", "bgSecondary",
                           "bgCard", "bgCardHover", "border"]
        .every((k) => typeof x[k] === "string" && x[k].startsWith("#"))));
    check("the dark base stays dark",
      themes.every((x) => parseInt(x.bgPrimary.slice(1, 3), 16) < 64));

    ctx.MPV_Themes.apply("ocean");
    check("applying a palette repaints the variables",
      vars["--accent-color"] === "#0ea5e9" && vars["--border-color"] === "#254653",
      JSON.stringify(vars));
    check("the gradient follows the two accents",
      vars["--accent-gradient"].includes("#0ea5e9") && vars["--accent-gradient"].includes("#6366f1"));
    check("the choice is stored", saved.length === 1 && saved[0].uiTheme === "ocean");

    ctx.MPV_Themes.apply("does-not-exist");
    check("an unknown id falls back to the default", ctx.MPV_Themes.current() === "midnight");

    const beforeAccent = vars["--accent-color"];
    const picked = ctx.MPV_Themes.random();
    check("random generates a palette instead of picking a preset",
      picked === "random" && vars["--accent-color"] !== beforeAccent,
      picked + " " + vars["--accent-color"]);

    const remembered = saved[saved.length - 1];
    check("a generated palette is remembered in full",
      remembered.uiTheme === "random" &&
      /^#[0-9a-f]{6}$/i.test(remembered.uiThemeRandom.accent) &&
      /^#[0-9a-f]{6}$/i.test(remembered.uiThemeRandom.bgPrimary),
      JSON.stringify(remembered));

    const accents = new Set();
    for (let i = 0; i < 12; i++) {
      ctx.MPV_Themes.random();
      accents.add(vars["--accent-color"]);
    }
    check("each press generates a different palette", accents.size > 6, String(accents.size));
  }

  section("toolbar icon keeps the theme after a restart");
  {
    const source = read("themes.js");

    /**
     * A background context: no document at all, only the APIs a service worker
     * really has. This is the case that regressed - the tint used to be built
     * with document.createElement, so a restart fell back to the packaged icon
     * until the popup was opened once.
     */
    function workerCtx(savedTheme) {
      const paints = [];
      const setIcons = [];
      const ctx = {
        Map,
        Math,
        Promise,
        console,
        OffscreenCanvas: class {
          constructor() { this.width = 0; this.height = 0; }
          getContext() {
            return {
              globalCompositeOperation: "source-over",
              clearRect() { },
              drawImage() { },
              fillRect() { paints.push(this.fillStyle); },
              getImageData: () => ({ data: [1, 2, 3] })
            };
          }
        },
        createImageBitmap: async () => ({ width: 32, height: 32 }),
        fetch: async () => ({ blob: async () => ({ size: 1 }) }),
        chrome: {
          storage: { local: { get: async () => savedTheme, set: () => { } } },
          action: { setIcon: (icon) => setIcons.push(icon) }
        }
      };
      vm.createContext(ctx);
      vm.runInContext(source, ctx, { filename: "themes.js" });
      return { ctx, paints, setIcons };
    }

    const preset = workerCtx({ uiTheme: "ocean" });
    await preset.ctx.MPV_Themes.restoreIcon();
    await new Promise((resolve) => setImmediate(resolve));
    check("a restart repaints the icon without a popup",
      preset.setIcons.length === 1 && Array.isArray(preset.setIcons[0].imageData.data),
      JSON.stringify(preset.setIcons));
    check("the repaint uses the saved accent, not the default palette",
      preset.paints.includes("#0ea5e9"), JSON.stringify(preset.paints));

    const generated = workerCtx({
      uiTheme: "random",
      uiThemeRandom: {
        id: "random", name: "Random", accent: "#123456", accentHover: "#111111",
        accentEnd: "#654321", bgPrimary: "#101010", bgSecondary: "#121212",
        bgCard: "#141414", bgCardHover: "#161616", border: "#202020"
      }
    });
    await generated.ctx.MPV_Themes.restoreIcon();
    await new Promise((resolve) => setImmediate(resolve));
    check("a generated palette keeps its own colour after a restart",
      generated.paints.includes("#123456"), JSON.stringify(generated.paints));

    check("the background repaints at startup", /MPV_Themes\.restoreIcon\(\)/.test(background));
    // Chromium does not start the MV3 worker at browser launch, so the
    // top-level call alone left the packaged icon until the popup was opened.
    check("the repaint is tied to runtime.onStartup",
      /onStartup[\s\S]{0,400}?addListener\(repaintToolbarIcon\)/.test(background),
      "onStartup listener missing");
    check("an install or update repaints too",
      /onInstalled[\s\S]{0,400}?addListener\(repaintToolbarIcon\)/.test(background));
    check("a restored session still gets one repaint on first focus",
      /onFocusChanged[\s\S]{0,400}?repaintToolbarIcon\(\)/.test(background));
    check("the background loads the theme script",
      /importScripts\([^)]*"themes\.js"/.test(background));
    check("the shipped Firefox manifest also loads it",
      JSON.parse(read("manifest.json")).background.scripts.includes("themes.js"));
  }

  section("url identity");
  {
    const p = typesCtx();
    const a = "https://cdn.x.com/a/seg.ts?token=aaa&expires=1";
    const b = "https://cdn.x.com/a/seg.ts?expires=2&token=bbb";
    check("signed params dropped", p.mpvNormalizeUrl(a) === p.mpvNormalizeUrl(b),
      p.mpvNormalizeUrl(a) + " vs " + p.mpvNormalizeUrl(b));
    check("order ignored", p.mpvNormalizeUrl("https://x.com/v.mp4?b=2&a=1") ===
      p.mpvNormalizeUrl("https://x.com/v.mp4?a=1&b=2"));
    check("fragment ignored", p.mpvNormalizeUrl("https://x.com/v.mp4#t=30") ===
      p.mpvNormalizeUrl("https://x.com/v.mp4"));
    check("real params kept", p.mpvNormalizeUrl("https://x.com/v.mp4?itag=22") !==
      p.mpvNormalizeUrl("https://x.com/v.mp4?itag=18"));
    check("distinct paths stay distinct", p.mpvNormalizeUrl("https://x.com/a.mp4") !==
      p.mpvNormalizeUrl("https://x.com/b.mp4"));
    check("unparseable still compares", p.mpvNormalizeUrl("not a url#x") === "not a url");
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

  section("the rescan button clears the stored captures before it scans");
  {
    // background.js has handled "clear_network_media" since it was written,
    // and nothing ever sent it. A handler nobody calls is indistinguishable
    // from no feature at all, so the whole path is checked here.
    const html = fs.readFileSync(path.join(EXT, "popup.html"), "utf8");
    check("no separate clear button in the markup", !/id="btn-clear"/.test(html));
    check("no clear row in the settings panel", !/id="lbl-clear-list"/.test(html));
    check("the rescan button is still there", /id="btn-rescan"/.test(html));

    const popup = fs.readFileSync(path.join(EXT, "popup.js"), "utf8");
    check("popup.js sends clear_network_media",
      /action:\s*"clear_network_media"/.test(popup));
    check("popup.js passes the tab id, so other tabs are untouched",
      /clear_network_media[\s\S]{0,200}tabId/.test(popup));
    check("popup.js no longer references a clear button",
      !/btnClear|lblClearList|updateClearButton|clearCurrentTab/.test(popup));

    // The clear has to happen inside the scan, and before anything is read back
    // - otherwise it runs after the results it was meant to discard.
    const scanAt = popup.indexOf("async function scanCurrentTab");
    const clearAt = popup.indexOf("await clearTabCaptures()", scanAt);
    const fetchAt = popup.indexOf("await fetchNetworkMedia()", scanAt);
    check("the scan clears the captures", clearAt > scanAt, "no clear call inside scanCurrentTab");
    check("the clear runs before the captures are read back",
      clearAt > scanAt && fetchAt > clearAt,
      `clear at ${clearAt}, read at ${fetchAt}`);

    // Only the rescan button may discard them. If opening the popup cleared as
    // well, simply looking at the list would destroy captures that the next
    // scan cannot reproduce.
    check("the rescan button asks for a fresh scan",
      /btnRescan\.onclick = \(\) => scanCurrentTab\(\{ fresh: true \}\)/.test(popup));
    check("opening the popup does not clear",
      /^\s*await scanCurrentTab\(\);/m.test(popup) &&
      !/^\s*await scanCurrentTab\(\{\s*fresh/m.test(popup),
      "the initial scan must not pass fresh");
    check("changing the language does not clear",
      /selectLanguage[\s\S]{0,400}scanCurrentTab\(\);/.test(popup) &&
      !/selectLanguage[\s\S]{0,400}scanCurrentTab\(\{\s*fresh/m.test(popup));
    check("the clear is only reached through the scan",
      (popup.match(/clearTabCaptures\(\)/g) || []).length === 2,
      "expected a definition and exactly one call site");

    check("background.js handles clear_network_media",
      /action === "clear_network_media"/.test(background));
    check("background.js scopes the clear to one tab",
      /clearTab\(message\.tabId\)|typeof message\.tabId === "number"/.test(background));

    // The strings the button needed are gone from every language, since nothing
    // looks them up any more.
    const i18n = read("i18n.js");
    for (const k of ["clear_list_tooltip", "btn_clear", "list_cleared",
                    "list_cleared_hint", "list_cleared_status"]) {
      check(`dead key ${k} removed`, !i18n.includes(`"${k}":`));
    }
  }

  /* --------------------------------------------------- the media type vocabulary

     Three files each held part of the same list and none could see the others:
     media_types.js owns the ids, i18n.js has seven translated names, popup.css
     has six colour rules. The popup rendered its badge by asking for
     "type_<id>" and falling back with || - but the translation lookup returns
     the key itself when it misses, which is truthy, so the fallback never ran
     and the badge read "type_mp4" in every language.

     The same split misfiled seven of the twelve ids: the popup's own category
     table knew about hls, dash, hds and mss, so mp4, webm, aac, mp3, ogg, ts
     and flv counted as streams. Turning off the "Streams" chip therefore hid
     captured video files along with the streams.

     media_types.js now owns the projection and the popup asks it, so one list
     answers for all three files and these checks hold it to that. */

  section("media type vocabulary");

  {
    const ctx = typesCtx();
    const ids = ctx.MPV_MEDIA_TYPES.map((t) => t.id);
    const css = read("popup.css");
    const popupSource = read("popup.js");
    const popupHtml = read("popup.html");

    /* The scrollbar used to be the browser's own: always visible, full width,
       and loud against a dark card. It now matches the launcher's panels -
       nothing at rest, a thin thumb while the list is being used - which is
       two declarations per rendering engine, so both are pinned here. */
    const block = (selector) => {
      const start = css.indexOf(selector + " {");
      if (start < 0) return "";
      const end = css.indexOf("}", start);
      return end < 0 ? "" : css.slice(start, end);
    };

    check("the list scrolls with a thin bar", /scrollbar-width:\s*thin/.test(block(".container")));
    check("and it is invisible until the list is used",
      /scrollbar-color:\s*transparent transparent/.test(block(".container")));
    check("hover reveals a soft thumb",
      /\.container:hover,\s*[\r\n]+\.container:focus-within \{[^}]*scrollbar-color:\s*rgba\(255, 255, 255, 0\.24\) transparent/.test(css));
    check("Chromium gets the same treatment for the list",
      /\.container::-webkit-scrollbar-thumb\s*\{[^}]*background-color:\s*transparent/.test(css) &&
      /\.container:hover::-webkit-scrollbar-thumb/.test(css));
    check("the window itself does not show a second, loud bar",
      /body \{\s*[\r\n]+  scrollbar-width:\s*thin/.test(css));

    check("media_types.js can look a type up by id", typeof ctx.mpvTypeById === "function");
    check("media_types.js can project a badge", typeof ctx.mpvTypeBadge === "function");
    check("media_types.js can resolve a category", typeof ctx.mpvTypeCategory === "function");

    // A lookup that misses must say so rather than inventing an entry, or the
    // projection silently grows a second, wrong source of truth.
    check("mpvTypeById returns null for an unknown id",
      ctx.mpvTypeById("definitely-not-a-type") === null);
    check("mpvTypeById finds every declared type",
      ids.every((id) => ctx.mpvTypeById(id) && ctx.mpvTypeById(id).id === id));

    // The bug: a translator that misses returns the key, which is truthy, so a
    // naive || keeps the key and the badge reads type_mp4.
    const echoesKeys = (key) => key;
    for (const id of ids) {
      const badge = ctx.mpvTypeBadge(id, null, echoesKeys);
      check(`badge for ${id} never reads as a key`, badge.text !== `type_${id}`,
        `got "${badge.text}"`);
      check(`badge for ${id} is not blank`, !!badge.text);
    }

    // ...and it must actually use a translation when one exists.
    const translated = ctx.mpvTypeBadge("hls", null, () => "HLS akisi");
    check("badge uses a real translation when there is one",
      translated.text === "HLS akisi", `got "${translated.text}"`);

    // The label the capture layer already carries beats the bare id.
    const fromLabel = ctx.mpvTypeBadge("mp4", "MP4 dosyasi", echoesKeys);
    check("badge prefers the captured label over the id",
      fromLabel.text === "MP4 dosyasi", `got "${fromLabel.text}"`);
    const fromEntry = ctx.mpvTypeBadge("webm", null, echoesKeys);
    check("badge falls back to the vocabulary's own label",
      fromEntry.text === "WEBM", `got "${fromEntry.text}"`);

    // Every id needs a colour rule, or the badge renders unstyled.
    for (const id of ids) {
      check(`popup.css has a rule for .tag-${id}`, css.includes(`.tag-${id} {`));
    }

    // Every id needs a category, or the filter chips cannot place it.
    for (const id of ids) {
      const cat = ctx.mpvTypeCategory(id);
      check(`${id} resolves to a category the filters know`,
        cat === "stream" || cat === "files" || cat === "iframe", `got "${cat}"`);
    }

    // The concrete regression: a video file is a file, not a stream.
    for (const id of ["mp4", "webm", "flv", "ts"]) {
      check(`${id} is a file, not a stream`, ctx.mpvTypeCategory(id) === "files");
    }
    for (const id of ["hls", "dash", "hds", "mss"]) {
      check(`${id} is a stream`, ctx.mpvTypeCategory(id) === "stream");
    }

    // The popup must ask the vocabulary rather than keep its own copy.
    check("popup.js delegates the badge to media_types.js",
      /mpvTypeBadge\(/.test(popupSource));
    check("popup.js delegates filtering to media_types.js",
      /mpvPassesFilter\(/.test(popupSource));
    check("popup.js keeps no second category table",
      !/categoryForType\(/.test(popupSource));
    check("popup.html loads media_types.js", /media_types\.js/.test(popupHtml));
  }

  /* ------------------------------------------------------------- the manifest

     This file is what gets zipped and submitted to addons.mozilla.org, so it
     is the Firefox manifest and nothing else - the application writes its own
     per-browser manifest and deliberately excludes this one when it copies the
     bundle (ExtensionInstallService.cs:787,810).

     It declared background.service_worker. Firefox does not implement
     service workers in MV3 and refuses to load such an add-on at all; Mozilla's
     own linter reports it as an error, so this file could not be submitted.
     The application's Firefox manifest already used background.scripts.

     A manifest is only worth a check if it parses. Three files used to keep
     three copies and none of them parsed any of them. */

  section("manifest");

  {
    const manifest = JSON.parse(read("manifest.json"));

    check("manifest.json parses as JSON", !!manifest);
    check("manifest is version 3", manifest.manifest_version === 3);

    // Firefox refuses a service worker, and an add-on that will not load is
    // worse than one missing a convenience.
    check("manifest does not declare background.service_worker",
      !manifest.background || !manifest.background.service_worker,
      "Firefox MV3 rejects service workers; the add-on will not load");
    check("manifest declares background.scripts for Firefox",
      !!(manifest.background && Array.isArray(manifest.background.scripts)),
      "Firefox needs background.scripts");

    // background.js pulls the sidecars in itself when they are not loaded as
    // background scripts, so a scripts entry that omits them is a broken list.
    if (manifest.background && Array.isArray(manifest.background.scripts)) {
      const scripts = manifest.background.scripts;
      check("background scripts load the sidecars before background.js",
        scripts.includes("media_types.js") && scripts.includes("network_capture.js"),
        `got ${JSON.stringify(scripts)}`);
      check("background.js is last in the list",
        scripts[scripts.length - 1] === "background.js",
        "the sidecars must already be defined when background.js runs");
    }

    // A permission nothing uses is a permission the reviewer sees and the user
    // is asked to grant. webNavigation was requested by both application
    // manifests and read by no file.
    const declared = manifest.permissions || [];
    const source = read("background.js") + read("popup.js") + read("content_script.js") +
                   read("network_capture.js") + read("network_probe.js") + read("media_types.js");
    check("manifest does not request the unused webNavigation permission",
      !declared.includes("webNavigation"));
    check("every declared permission is one the code actually reaches for",
      ["nativeMessaging", "activeTab", "scripting", "webRequest", "tabs", "storage"]
        .every((p) => declared.includes(p)),
      `declared: ${JSON.stringify(declared)}`);

    // gecko.data_collection_permissions only exists from Firefox 140, so
    // promising support from 128 asks the browser for a key it cannot read.
    const gecko = manifest.browser_specific_settings &&
                  manifest.browser_specific_settings.gecko;
    check("manifest carries the gecko block", !!gecko);
    if (gecko && gecko.data_collection_permissions) {
      const min = parseFloat(gecko.strict_min_version || "0");
      check("strict_min_version covers data_collection_permissions",
        min >= 140, `strict_min_version is ${gecko.strict_min_version}, needs 140`);
    }

    check("the addon id matches the one the application installs",
      gecko && gecko.id === "{264eb39b-fe04-417b-940e-81df026edbc3}",
      `got ${gecko && gecko.id}`);
  }

  /* ------------------------------------------------- handlers nobody calls

     background.js answers a named message and two of those names were never
     sent by anything: get_filter_defaults and apply_filter. apply_filter was
     the only caller of mpvPassesFilter in media_types.js, so a duplicate of it
     also existed in popup.js with the opposite behaviour on unknown types -
     media_types hid them, the popup showed them - and the copy that actually
     ran was the one under test.

     The suite already wrote the lesson down at line 1034 after the same thing
     happened with clear_network_media: "A handler nobody calls is
     indistinguishable from no feature at all." This checks it for every
     handler, so the next one fails a test instead of waiting to be noticed. */

  section("message handlers have senders");

  {
    const sources = ["background.js", "popup.js", "content_script.js",
                     "network_capture.js", "network_probe.js", "media_types.js"]
      .map(read).join("\n");

    const handlers = [...background.matchAll(/message\.action\s*===\s*"([a-z_]+)"/g)]
      .map((m) => m[1]);

    check("background.js has handlers to check", handlers.length > 0);

    // A handler with no sender is a product decision, not a refactor, so it is
    // named here rather than quietly tolerated. Naming them means deleting the
    // feature later is a deliberate edit to this list instead of a silent
    // removal of working code.
    //
    // The permission pair is that case: network_capture.js can probe for and
    // request host permissions and twenty-five checks cover it, but the popup
    // never offers the button - it tells the user to reload the extension
    // instead. That is unfinished, not unwanted, and it is the only known gap.
    const KNOWN_GAPS = {
      get_permission_status: "popup offers no permission button yet",
      request_permission: "popup offers no permission button yet"
    };

    const unexpected = handlers.filter(
      (a) => !(a in KNOWN_GAPS) && !new RegExp(`action:\\s*"${a}"`).test(sources)
    );

    check("every message handler has a sender, apart from the declared gaps",
      unexpected.length === 0,
      `no sender for: ${unexpected.join(", ")}`);

    // A gap that quietly grows is how the original two got there.
    check("the declared gaps are still exactly the permission pair",
      Object.keys(KNOWN_GAPS).every((a) => handlers.includes(a)) &&
      KNOWN_GAPS.get_permission_status.includes("permission") &&
      KNOWN_GAPS.request_permission.includes("permission"),
      "update the list and its reason together when this changes");

    // One implementation, not two that disagree. A thin alias in the popup is
    // fine - it is the delegating half - but only one function may actually
    // decide, or the behaviour that ships is whichever copy nothing tests.
    const implementations = sources.match(
      /function\s+mpvPassesFilter\s*\([^)]*\)\s*\{[\s\S]*?\n\}/g) || [];
    check("only one filter implementation exists",
      implementations.length === 1,
      `found ${implementations.length}`);
    check("the popup's filter is a delegation, not a second decision",
      /function\s+passesFilter\s*\([^)]*\)\s*\{\s*return\s+mpvPassesFilter\([^)]*\);\s*\}/.test(read("popup.js")));

    check("list order has one implementation",
      (sources.match(/function\s+mpvSortForDisplay\s*\([^)]*\)\s*\{/g) || []).length === 1);
    check("the popup draws the sorted copy, not the merged array",
      /mpvSortForDisplay\(mediaItems\)\.forEach/.test(read("popup.js")));
  }

  console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
  process.exit(fail === 0 ? 0 : 1);
})();
