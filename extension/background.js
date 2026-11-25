/**
 * MPV Launcher - background script
 */

// MV3 service worker: pull in the sidecar scripts. Firefox loads the same
// files through manifest background.scripts, where importScripts is absent.
if (typeof importScripts === "function" && typeof MPV_MEDIA_TYPES === "undefined") {
  try {
    importScripts("media_types.js", "network_capture.js", "themes.js");
  } catch (e) {
    // Firefox path - files already loaded by the manifest.
  }
}

const HOST_NAME = "com.mpv.launcher";
const api = (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage)
  ? browser
  : chrome;
// The host log names the picking build: without it a stale reload and a real
// fix look the same in the log.
const EXT_VERSION = (() => {
  try {
    return api.runtime.getManifest().version;
  } catch (e) {
    return "?";
  }
})();

// The toolbar icon is tinted with the theme's accent, and that tint used to be
// rebuilt only while the popup was open: a browser restart dropped it and the
// packaged colour came back until the extension was clicked once.
//
// A top-level call is not enough on Chromium. The MV3 service worker is not
// started when the browser starts - it starts on the first event - so at launch
// nothing runs and the toolbar keeps manifest's default icon. runtime.onStartup
// is that first event: it fires once per browser start, which is exactly when
// the icon has to be repainted. The other two cover the rest: a worker restart
// mid-session (top level) and a fresh install or update (onInstalled).
function repaintToolbarIcon() {
  if (typeof MPV_Themes === "undefined") return;
  try {
    MPV_Themes.restoreIcon();
  } catch (e) {
    // A browser that refuses the repaint keeps the packaged icon.
  }
}

repaintToolbarIcon();

for (const event of [api.runtime && api.runtime.onStartup,
                      api.runtime && api.runtime.onInstalled]) {
  try {
    if (event && typeof event.addListener === "function") event.addListener(repaintToolbarIcon);
  } catch (e) {
    // An event this browser does not implement is not worth failing over.
  }
}

// Belt and braces: a restored session can deliver a window event before
// onStartup arrives. The first focus repaints once, then stays quiet.
let iconChecked = false;
try {
  api.windows.onFocusChanged.addListener(function () {
    if (iconChecked) return;
    iconChecked = true;
    repaintToolbarIcon();
  });
} catch (e) {
  // No windows API in this browser: onStartup is enough.
}

/**
 * Finds the headers of the request that actually fetched this media URL.
 *
 * Some CDNs answer 403 unless the playback presents the same User-Agent the
 * page used, so those two headers are forwarded to mpv. The Referer is
 * forwarded only as the browser actually sent it - never guessed. Guessing
 * breaks page URLs outright: yt-dlp answers "Unsupported URL" for an embed
 * page given --referer pointing at the embed host instead of the site that
 * embeds it.
 *
 * Credentials are deliberately not included: cookies and Authorization are
 * dropped in the capture layer and never leave the browser. A video behind a
 * login will therefore not play from mpv, which is the intended trade-off.
 */
function findCapturedRequest(url) {
  if (typeof MPV_Network === "undefined" || !url) return null;
  const items = MPV_Network.peekAll();
  for (let i = items.length - 1; i >= 0; i--) {
    if (items[i] && items[i].url === url) return items[i];
  }
  return null;
}

function headerValue(headers, name) {
  if (!headers) return "";
  for (let i = 0; i < headers.length; i++) {
    if (headers[i].name && headers[i].name.toLowerCase() === name) {
      return headers[i].value || "";
    }
  }
  return "";
}

function buildNativePayload(url, referrer, title, pageUrl, extra) {
  const payload = { url: url, referrer: referrer || "" };

  // Some players keep the picture and the sound in two separate playlists
  // (idlixku does). mpv takes the second one as an extra file, so both travel
  // in the same message.
  if (extra) {
    if (extra.audioUrl) payload.audioUrl = extra.audioUrl;
    if (extra.subUrl) payload.subUrl = extra.subUrl;
    // Counts only (no URLs): the host writes this into its log so a wrong pick
    // can be read back without exposing a signed path.
    if (extra.note) {
      // sweepFresh hands back a note that may already carry the tag.
      const tag = "ext=" + EXT_VERSION + " ";
      payload.pick = extra.note.indexOf(tag) === 0 ? extra.note : tag + extra.note;
    }
  }

  // An extension-less stream on the tab's own origin has usually been fetched
  // by the page's player already, and those tokens are single use. The relay
  // then renders the page instead of replaying the captured URL, so the page
  // travels with the message.
  const kind = typeof mpvRelayKind === "function" ? mpvRelayKind(url, pageUrl) : "";
  if (kind) {
    payload.kind = kind;
    payload.pageUrl = pageUrl;
  }

  // The picker can decide that what it found is spent (an earlier episode's
  // token) and ask for the page itself to be rendered again.
  if (extra && extra.kind) {
    payload.kind = extra.kind;
    payload.pageUrl = pageUrl;
  }

  // The tab title is the site's own heading ("Paws & Palaces 4. Bölüm İzle"),
  // which is what the launcher's history should show - the media URL is a
  // stream behind a player and says nothing about what was being watched.
  if (title) payload.title = String(title).slice(0, 300);

  // Attach whatever the capture layer recorded for this exact URL.
  const captured = findCapturedRequest(url);
  if (captured) {
    const ua = headerValue(captured.headers, "user-agent");
    const ref = headerValue(captured.headers, "referer");

    if (ua) payload.userAgent = ua;
    // prefer the Referer the page really sent over the tab URL
    if (ref) payload.referrer = ref;
    else if (captured.referer && /^https?:/i.test(captured.referer)) {
      payload.referrer = captured.referer;
    }
  }

  return payload;
}

function sendNative(url, referrer, title, pageUrl, pick) {
  const payload = buildNativePayload(url, referrer, title, pageUrl, pick ? { note: pick } : null);

  if (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage) {
    return browser.runtime.sendNativeMessage(HOST_NAME, payload);
  }

  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST_NAME, payload, (response) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message || "Native host could not be started."));
      else resolve(response);
    });
  });
}

// Plan A: the relay fetches through this very tab. The site answers only
// requests made from the page's own session, and a second browser is what
// makes it block the network - so a port stays open for the whole playback and
// every request is replayed inside the page the viewer already has open.
async function fetchInPage(url) {
  try {
    // A CDN that accepts the connection and then stalls must not eat the whole
    // playback: abort well before the helper's own 60 s broker timeout so the
    // log gets a reason instead of one silent gap.
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), 18000);
    let response;
    try {
      response = await fetch(url, {
        credentials: "include",
        cache: "no-store",
        signal: abort.signal
      });
    } finally {
      clearTimeout(timer);
    }
    const buffer = new Uint8Array(await response.arrayBuffer());
    let binary = "";
    const chunk = 0x8000;
    for (let i = 0; i < buffer.length; i += chunk) {
      binary += String.fromCharCode.apply(null, buffer.subarray(i, i + chunk));
    }
    return {
      status: response.status,
      contentType: response.headers.get("content-type") || "",
      body: btoa(binary)
    };
  } catch (error) {
    return { error: (error && error.message) || "fetch_failed" };
  }
}

function answerFetch(port, tabId, request) {
  const id = request.id;
  const diag = (text) => {
    // Narrated into host.log: when a segment never answers, the log has to say
    // which stage stopped - the injection, the page fetch, or the port itself.
    try {
      port.postMessage({ type: "diag", text: String(text).slice(0, 200) });
    } catch (error) {
      // A closed port is the answer in that case.
    }
  };
  const reply = (fields) => {
    diag("reply " + String(fields.status || 0) + " " + String(fields.error || ""));
    try {
      port.postMessage(Object.assign({ cmd: "fetch", id: id }, fields));
    } catch (error) {
      // The port is gone; the helper reports the timeout to the user.
    }
  };

  diag("start " + String(request.url || "").slice(0, 70));

  if (typeof tabId !== "number" || !api.scripting || !api.scripting.executeScript) {
    reply({ status: 0, error: "no_page" });
    return;
  }

  api.scripting.executeScript(
    {
      target: { tabId: tabId },
      world: "MAIN",
      args: [String(request.url || "")],
      func: fetchInPage
    },
    (results) => {
      const err = api.runtime.lastError;
      if (err) {
        reply({ status: 0, error: err.message || "inject_failed" });
        return;
      }
      const result = results && results[0] ? results[0].result : null;
      if (!result || result.error || !result.status) {
        // The page fetch can fail when the player runs in a frame whose origin
        // differs from the stream's: the browser then blocks it as CORS. The
        // extension holds <all_urls>, so its own fetch is not subject to that
        // check and can still deliver the bytes.
        Promise.resolve(fetchDirect(String(request.url || ""))).then((direct) => {
          if (direct && !direct.error && direct.status) {
            reply({ status: direct.status, contentType: direct.contentType, body: direct.body });
          } else {
            reply({ status: 0, error: (result && result.error) || (direct && direct.error) || "empty_reply" });
          }
        });
        return;
      }
      reply({ status: result.status, contentType: result.contentType, body: result.body });
    }
  );
}

function openRelayPort(payload, tabId) {
  return new Promise((resolve, reject) => {
    let port;
    try {
      port = api.runtime.connectNative(HOST_NAME);
    } catch (error) {
      reject(new Error(error && error.message ? error.message : "Native host could not be started."));
      return;
    }

    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      resolve(result);
    };

    port.onMessage.addListener((message) => {
      if (!message || typeof message !== "object") return;
      if (message.type === "fetch") {
        answerFetch(port, tabId, message);
        return;
      }
      if (message.status === "error") {
        finish({ success: false, error: message.message || "Unknown host error." });
        return;
      }
      finish({ success: true, data: message });
    });

    port.onDisconnect.addListener(() => {
      const err = api.runtime.lastError;
      if (!settled) {
        finish({ success: false, error: (err && err.message) || "Native host disconnected." });
      }
    });

    try {
      port.postMessage(payload);
    } catch (error) {
      finish({ success: false, error: (error && error.message) || "Native host is unreachable." });
    }
  });
}

function updateBadge(tabId, count) {
  api.storage.local.get(["showBadge"], (result) => {
    const show = result && typeof result.showBadge === "boolean" ? result.showBadge : true;
    const text = (!show || count <= 0) ? "" : String(count);

    if (api.action && api.action.setBadgeText) {
      api.action.setBadgeText({ tabId: tabId, text: text });
      api.action.setBadgeBackgroundColor({ tabId: tabId, color: "#6366f1" });
    } else if (api.browserAction && api.browserAction.setBadgeText) {
      api.browserAction.setBadgeText({ tabId: tabId, text: text });
      api.browserAction.setBadgeBackgroundColor({ tabId: tabId, color: "#6366f1" });
    }
  });
}

// The player fetches its playlist through the page's own code, which webRequest
// does not always see (MSE players often keep it in a worker). The performance
// timeline still lists every request, so ask the tab when the capture is empty.
//
// The resource timeline only knows requests the page already made. A player
// that is sitting idle has made none, yet it already holds the stream it was
// built with. Asking the player is therefore not a shortcut but the only
// source that is correct before anything is fetched: the second anime opened
// from the homepage used to hand mpv an empty page URL for this reason.
// The player object and the Nuxt payload live in the page world, so this probe
// runs there and returns only absolute http(s) stream URLs.
function probePageStream(tabId) {
  if (!api.scripting || typeof tabId !== "number") return Promise.resolve({ urls: [] });
  return api.scripting
    .executeScript({
      target: { tabId, allFrames: true },
      world: "MAIN",
      func: () => {
        const urls = [];
        const add = (value) => {
          if (typeof value !== "string" || !/^https?:/i.test(value)) return;
          if (urls.indexOf(value) < 0) urls.push(value);
        };
        // Every source is scanned and the scanned size is reported back: a pick
        // that finds nothing has to say whether the page held no token at all or
        // held it somewhere this probe does not look.
        const seen = { nuxt: 0, scripts: 0, jw: 0, vids: 0 };
        const scan = (text, limit) => {
          if (!text) return 0;
          const body = String(text);
          const part = body.length > limit ? body.slice(0, limit) : body;
          for (const url of part.match(/https?:\/\/[^\s"\\<>]+/g) || []) {
            if (/\/w\/[\w-]{8,}|\.m3u8|\.mpd/i.test(url)) add(url);
          }
          // Inline configs often carry the token as a path only.
          for (const path of part.match(/\/w\/[\w-]{8,}(?:\/[a-z0-9]{1,12})?/gi) || []) {
            add(location.origin + path);
          }
          return part.length;
        };
        try {
          seen.nuxt = scan(JSON.stringify(window.__NUXT__), 4000000);
        } catch (e) {}
        try {
          const data = document.getElementById("__NUXT_DATA__");
          scan(data && data.textContent, 4000000);
        } catch (e) {}
        for (const script of document.scripts || []) {
          if (script.src) continue;
          seen.scripts += scan(script.textContent, 1000000);
        }
        for (const el of document.querySelectorAll(
          "[data-src],[data-file],[data-sources],[data-stream],[data-player],[data-video],[data-config]")) {
          for (const attr of el.attributes || []) {
            if (/^data-/i.test(attr.name) && attr.value && attr.value.length < 4000) add(attr.value);
          }
        }
        try {
          const jw = window.jwplayer;
          if (typeof jw === "function") {
            seen.jw = 1;
            const player = jw();
            const playlist = player && typeof player.getPlaylist === "function"
              ? player.getPlaylist()
              : null;
            for (const item of playlist || []) {
              add(item && item.file);
              for (const source of (item && item.sources) || []) add(source && source.file);
            }
            const config = player && typeof player.getConfig === "function" ? player.getConfig() : null;
            if (config) scan(JSON.stringify(config), 1000000);
          }
        } catch (e) {}
        for (const video of document.querySelectorAll("video")) {
          seen.vids += 1;
          add(video.currentSrc);
          add(video.src);
        }
        return { urls: urls.slice(0, 20), seen };
      },
    })
    .then((results) => {
      const urls = [];
      const seen = { nuxt: 0, scripts: 0, jw: 0, vids: 0 };
      for (const frame of results || []) {
        const out = frame && frame.result;
        if (!out) continue;
        const list = out.urls;
        if (Array.isArray(list)) {
          for (const url of list) if (urls.indexOf(url) < 0) urls.push(url);
        }
        for (const key of Object.keys(seen)) seen[key] += Number(out.seen && out.seen[key]) || 0;
      }
      return { urls, seen };
    })
    .catch(() => ({ urls: [], seen: {} }));
}
// The timeline keeps every request the document ever made. A site that swaps
// episodes inside one document therefore still lists the previous episode's
// token, and relaying that one brings back the first video. content_script.js
// stamps `__mpvNavAt` on load and on every in-page route change; both scripts
// run in the same isolated world, so the sweep can read it and ignore anything
// the document asked for before it navigated here.
function sweepPageForStream(tabId, narrowed) {
  if (!api.scripting || typeof tabId !== "number") {
    return Promise.resolve({ urls: [], ages: [], scanned: -1, dropped: 0 });
  }
  return api.scripting
    .executeScript({
      target: { tabId, allFrames: true },
      func: () => {
        const navAt = Number(window.__mpvNavAt) || 0;
        const origin =
          Number(performance.timeOrigin) || Date.now() - performance.now();
        const all = performance.getEntriesByType("resource");
        const items = all
          .filter(
            (entry) =>
              /^https?:/i.test(entry.name) &&
              /\/w\/|\.m3u8|urlset/i.test(entry.name) &&
              !/\.(?:ts|m4s|jpe?g|png|mp4|m4a|aac)$/i.test(entry.name)
          )
          .map((entry) => {
            // Epoch milliseconds, so entries from different frames (each with
            // its own timeOrigin) can be compared with one cutoff.
            const at = Math.round(origin + (Number(entry.startTime) || 0));
            return { url: entry.name, at, age: Math.round(Date.now() - at) };
          });
        return { scanned: all.length, navAt: Math.round(navAt), items };
      },
    })
    .then((results) => {
      // The player often lives in an iframe, so every frame is asked. Frames
      // answer in their own order, which is not time order: an old episode in
      // the first frame would otherwise beat the new one in the second. Merged
      // entries are therefore sorted again, newest request first.
      const items = [];
      let scanned = 0;
      let navAt = 0;
      for (const frame of results || []) {
        if (!frame || !frame.result) continue;
        if (Array.isArray(frame.result.items)) items.push(...frame.result.items);
        scanned += Number(frame.result.scanned) || 0;
        // The newest navigation across frames is the episode the viewer chose:
        // a top-level route change is what makes the player iframe load the
        // next video.
        navAt = Math.max(navAt, Number(frame.result.navAt) || 0);
      }
      // The navigation stamp can lag the real route change by up to the page
      // poll interval (1 s), and the player's first request may land inside
      // that gap. Accepting entries slightly older than the stamp keeps that
      // request without readmitting an earlier episode, which is minutes old.
      const cutoff = navAt - 1000;
      const fresh = narrowed && navAt > 0
        ? items.filter((item) => item.at >= cutoff)
        : items;
      fresh.sort((a, b) => b.at - a.at);
      return {
        urls: fresh.map((item) => item.url),
        ages: fresh.map((item) => item.age),
        scanned,
        navAt,
        dropped: items.length - fresh.length
      };
    })
    .catch(() => ({ urls: [], ages: [], scanned: -2, navAt: 0, dropped: 0 }));
}

// The popup knows the page, not the media: relaying a page URL only makes the
// host answer "no stream". When the capture still holds the player's own
// request, that URL is relayed instead - the viewer's tab is allowed to fetch
// it, and the helper never has to start a browser of its own.
function pickCapturedStream(tabId, pageUrl) {
  // A sweep that finds nothing still reports how many URLs it looked at: an
  // empty Pick line in the host log is otherwise indistinguishable from a
  // picker that never ran.
  // `filtered` narrows the timeline to requests the page made since its latest
  // navigation, so an earlier episode cannot win. When that leaves nothing the
  // player has not asked yet: the caller waits for it. Falling back to the
  // unfiltered timeline looked harmless but replayed the previous episode, and
  // that is the case this filter exists to stop.
  const sweep = (note, filtered = true) =>
    sweepPageForStream(tabId, filtered).then((result) => {
      const urls = (result && result.urls) || [];
      // `scanned` is how many timeline entries the frames held: "sweep=0/210"
      // means the filter matched nothing, "sweep=0/-1" means the tab could not
      // be inspected at all.
      const scanned = result && typeof result.scanned === "number" ? result.scanned : 0;
      const ages = (result && result.ages) || [];
      // How many timeline entries the navigation filter threw away: non-zero
      // means the page held older episodes, which is exactly the case that used
      // to replay the wrong one.
      const dropped = result && typeof result.dropped === "number" ? result.dropped : 0;
      // Age of the navigation stamp the page reported. nav=-1 means the page
      // never marked a navigation, so the filter could not narrow anything.
      const navAt = result && typeof result.navAt === "number" ? result.navAt : 0;
      const navAge = navAt > 0 ? Date.now() - navAt : -1;
      // Host + name + age for the first few sweep hits: a log line then shows
      // whether the timeline holds the episode being watched or only an older
      // one, without ever printing a signed query string.
      const picks = urls.slice(0, 4).map((url, index) =>
        hostAndName(url) + "@" + (typeof ages[index] === "number" ? ages[index] : "?") + "ms");
      const seen = (note ? note + " " : "") + "sweep=" + urls.length + "/" + scanned +
        (dropped > 0 ? " drop=" + dropped : "") +
        " nav=" + navAge + "ms" +
        (picks.length ? " refs=" + picks.join(" ") : "");
      if (!urls.length && filtered) {
        return { url: "", referrer: "", note: seen, waiting: true };
      }
      const found = firstRelayable(urls, pageUrl);
      if (found) {
        // How old the chosen request is tells the host log whether the sweep
        // picked the request the page is playing now or one from an older
        // episode that is still in the timeline.
        const index = urls.indexOf(found.url);
        const age = index >= 0 ? ages[index] : undefined;
        found.note = seen + (typeof age === "number" ? " age=" + age + "ms" : "");
        return found;
      }
      return { url: "", referrer: "", note: seen };
    });
  // The page navigated but the player has not requested its stream yet: poll
  // the narrowed timeline while the player starts. Twenty tries at 700 ms
  // (14 s) covers a page whose player mounts lazily or a slow connection;
  // and the gap between a route change and the first request.
  // `wait=` in the log says whether the fallback ran, and how often it polled.
  const sweepFresh = (note) => {
    const attempt = (left, waited) =>
      sweep(note, true).then((found) => {
        if (found.waiting && left > 0) {
          return new Promise((resolve) => setTimeout(resolve, 700))
            .then(() => attempt(left - 1, waited + 1));
        }
        if (waited > 0) found.note = (found.note || "") + " wait=" + waited;
        return found;
      });
    return attempt(20, 0);
  };
  // The player already holds its stream while it sits idle: JW Player keeps the
  // playlist entry it loaded, and Nuxt keeps the page payload. Reading those is
  // instant, so a click right after the page opens still works instead of
  // waiting for a request the page may never repeat.
  // A miss is reported as well: `probe=0u nuxt=<chars> sc=<chars> jw=<0|1>
  // v=<n>` in the pick note says whether the page held no token at all or the
  // probe simply does not look where the token is. Counts only, never the URL.
  const probe = (note) =>
    probePageStream(tabId).then((result) => {
      const urls = (result && result.urls) || [];
      const seen = (result && result.seen) || {};
      const tag = "probe=" + urls.length + "u" +
        " nuxt=" + (Number(seen.nuxt) || 0) +
        " sc=" + (Number(seen.scripts) || 0) +
        " jw=" + (Number(seen.jw) || 0) +
        " v=" + (Number(seen.vids) || 0);
      const found = firstRelayable(urls, pageUrl);
      if (!found) return { hit: null, tag };
      found.note = (note ? note + " " : "") + tag + " " + hostAndName(found.url);
      return { hit: found, tag };
    });
  // MPV_Network is a top-level var from network_capture.js, not a member of the
  // `api` object (that is chrome/browser). Looking it up on `api` always said
  // "no capture", which silently disabled the picker.
  if (typeof MPV_Network === "undefined" || typeof MPV_Network.getItems !== "function") {
    return probe("cap=absent").then((r) => r.hit || sweepFresh(("cap=absent " + r.tag).trim()));
  }
  return Promise.resolve(MPV_Network.getItems(tabId))
    .then((items) => {
      const list = (Array.isArray(items) ? items : []).filter((item) => item && item.url);
      // A stream on the page's own origin is a spent token: the relay has to
      // render the page again. A cross-origin playlist is mirrored through the
      // tab too, but it arrives on a CDN that mpv can also open by itself.
      const relays = list.filter((item) => mpvRelayKind(item.url, pageUrl) === "resolve");
      // Manifests only. A captured segment, or a stream disguised as .jpg,
      // is not something mpv can open: handing one over produced a player with
      // a single picture or sound track and no seeking. Some hosts (majorplay)
      // serve their own config/data JSON with a playlist content-type, so a
      // .json path is never a manifest no matter how it was classified.
      // Newest first. A site that swaps episodes in place keeps the previous
      // episode's manifest in the capture list, and taking the oldest one made
      // the relay replay episode one until the tab was reloaded.
      const streams = list
        .filter((item) => isPlaylistItem(item))
        .filter((item) => !/\.json(?:$|\?)/i.test(item.url))
        .sort((a, b) => (b.seenAt || 0) - (a.seenAt || 0));
      // Counts, not URLs: the host log then shows why a pick came out empty
      // without ever holding a signed path.
      const kinds = {};
      for (const item of list) {
        const key = item.typeId || "?";
        kinds[key] = (kinds[key] || 0) + 1;
      }
      const types = Object.keys(kinds).sort()
        .map((key) => key + ":" + kinds[key]).join(",");
      // Host + file name only: enough to see what a site is serving without
      // logging a signed query string.
      const candidates = list
        .filter((item) => isPlaylistItem(item))
        .slice(0, 4)
        .map((item) => hostAndName(item.url));
      const note =
        `items=${list.length} resolve=${relays.length} streams=${streams.length} types=${types}` +
        (candidates.length ? " cand=" + candidates.join(" ") : "");
      if (relays.length) {
        const newest = relays[relays.length - 1];
        return { url: newest.url, referrer: newest.referrer || pageUrl, note: note };
      }

      if (streams.length) {
        // The master playlist is the player's first request: audio-only
        // renditions and the segment lists come after it.
        const audio = streams.find((item) => isAudioTrack(item.url));
        const primary = streams.find((item) => !isAudioTrack(item.url)) || streams[0];
        return {
          url: primary.url,
          referrer: primary.referrer || pageUrl,
          audioUrl: audio && audio.url !== primary.url ? audio.url : "",
          note: note
        };
      }

      // No manifest was captured: a lone media file is still something mpv can
      // open by itself, so use the oldest non-audio one before giving up.
      // An unrelated host is not this page's media: the preroll ad that played
      // on IDLIX came from a stranger's domain. Only a file on the page's own
      // site may be handed to mpv.
      const files = list
        .filter((item) => isLooseMediaFile(item.url))
        .filter((item) => sameSite(item.url, pageUrl))
        .sort((a, b) => (b.seenAt || 0) - (a.seenAt || 0));
      if (files.length) {
        const primary = files.find((item) => !isAudioTrack(item.url)) || files[0];
        return {
          url: primary.url,
          referrer: primary.referrer || pageUrl,
          note: note + " files=" + files.length
        };
      }

      return probe(note).then((r) => r.hit || sweepFresh((note + " " + r.tag).trim()));
    })
    .catch(() => probe("").then((r) => r.hit || sweepFresh(r.tag)));
}

// A captured manifest (m3u8/mpd/...): the only capture entry that describes
// the whole stream. Segments are useless to mpv as a start point, so the
// picker prefers these over every other candidate.
function isPlaylistItem(item) {
  if (!item || !item.url) return false;
  if (typeof mpvTypeCategory === "function" && item.typeId) {
    return mpvTypeCategory(item.typeId) === "stream";
  }
  return /\.(?:m3u8|mpd|f4m|ism)(?:$|\?)/i.test(String(item.url));
}

// A single media file mpv can open by itself when no manifest was captured.
function isLooseMediaFile(url) {
  if (!url) return false;
  if (typeof mpvPathEndsWithMediaExtension === "function" && mpvPathEndsWithMediaExtension(url)) {
    return true;
  }
  return /\.(?:mp4|mkv|webm|ts|m4s|m4a|aac|mp3|ogg|flv)(?:$|\?)/i.test(String(url));
}

// Host and last path segment only: the host log can then say what a site was
// serving without ever holding a signed query string.
function hostAndName(url) {
  try {
    const parsed = new URL(String(url || ""));
    return parsed.hostname + parsed.pathname.replace(/^.*\//, "/");
  } catch (error) {
    return "?";
  }
}

// The last two labels: "a.example.com" and "cdn.example.com" belong to one
// site, while an ad served from an unrelated domain does not.
function sameSite(url, otherUrl) {
  const site = (value) => {
    try {
      return new URL(String(value || "")).hostname.split(".").slice(-2).join(".");
    } catch (error) {
      return "";
    }
  };
  const left = site(url);
  return !!left && left === site(otherUrl);
}

// "audio", "audio-only", "_a1", "eng.m4a": the names players give the sound
// track when it lives in its own playlist.
function isAudioTrack(url) {
  return /(?:^|[/_.-])audio(?:[/_.-]|$)|audio[-_]?only|(?:^|[/_.-])a\d(?:[/_.-]|$)|\.m4a(?:$|\?)|\.aac(?:$|\?)/i.test(
    String(url || "")
  );
}

// The master playlist is the first request the player made, so the oldest
// candidate is the one the helper wants.
function firstRelayable(urls, pageUrl) {
  const list = Array.isArray(urls) ? urls : [];
  for (const url of list) {
    if (url && mpvRelayKind(url, pageUrl)) return { url, referrer: pageUrl };
  }
  return null;
}

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === "open_in_mpv") {
    const targetUrl = message.url;
    const referrer = message.referrer || (sender && sender.tab ? sender.tab.url : "");
    // Prefer the title the caller measured (the popup knows its tab); fall back
    // to the sender's tab. A content-script message has both, a popup message
    // only the first.
    // The page the media came from: the relay renders it when the captured URL
    // is a spent token, and it is the honest Referer for a direct launch.
    const pageUrl = message.pageUrl || (sender && sender.tab ? sender.tab.url : "") || referrer;
    const pageTitle = message.title || (sender && sender.tab ? sender.tab.title : "");
    if (!targetUrl) {
      sendResponse({ success: false, error: "Invalid or empty URL." });
      return false;
    }

    // A stream the site only serves to its own page is fetched through this
    // tab over a long-lived port; everything else keeps the one-shot message.
    // Relay is reserved for the page the viewer is on. A row in the found-media
    // list is a plain media URL, and sending it through the relay made mpv
    // replay an unrelated capture, so those launches stay direct.
    const allowRelay = message.pageLevel === true;
    const relayPayload = allowRelay
      ? buildNativePayload(targetUrl, referrer, pageTitle, pageUrl)
      : null;
    // The popup names its tab; a content script's message carries the sender's.
    // When neither names one, the active tab is still the page the user is
    // looking at, and the capture is keyed by that id.
    const relayTabId = typeof message.tabId === "number"
      ? message.tabId
      : sender && sender.tab ? sender.tab.id : null;
    const knownTabId = () =>
      typeof relayTabId === "number"
        ? Promise.resolve(relayTabId)
        : api.tabs && typeof api.tabs.query === "function"
          ? Promise.resolve(api.tabs.query({ active: true, currentWindow: true }))
              .then((tabs) => (tabs && tabs[0] && typeof tabs[0].id === "number" ? tabs[0].id : null))
              .catch(() => null)
          : Promise.resolve(null);
    const start = (payload) =>
      payload.kind && typeof relayTabId === "number"
        ? openRelayPort(payload, relayTabId)
        : sendNative(
            payload.url || targetUrl,
            payload.referrer || referrer,
            payload.title || pageTitle,
            payload.pageUrl || pageUrl,
            payload.pick || ""
          );

    // A row in the found-media list is a plain media URL: mpv opens it
    // directly. Only the page button asks the relay to fetch through the tab.
    const direct = () =>
      sendNative(targetUrl, referrer, pageTitle, pageUrl, message.pick || "");

    // Relaying the page URL itself only makes the host answer "no stream": the
    // queue holds the address of the media, not the page. When the capture saw
    // the player's own request, relay that instead.
    const request = !allowRelay
      ? direct()
      : relayPayload.kind
      ? start(relayPayload)
      : knownTabId().then((tabId) =>
          tabId === null
            ? // No tab means no capture to read: say so in the host log instead
              // of sending the page URL and letting the host answer "no stream".
              start(
                buildNativePayload(targetUrl, referrer, pageTitle, pageUrl, { note: "no-tab" })
              )
            : pickCapturedStream(tabId, pageUrl).then((found) =>
                start(
                  buildNativePayload(
                    found.url,
                    found.referrer || pageUrl,
                    pageTitle,
                    pageUrl,
                    found
                  )
                )
              )
        );

    request
      .then((response) => {
        if (response && response.status === "error") {
          sendResponse({ success: false, error: response.message || "Unknown host error." });
        } else {
          sendResponse({ success: true, data: response });
        }
      })
      .catch((err) => {
        sendResponse({
          success: false,
          error: err && err.message ? err.message : "Native host could not be started."
        });
      });

    return true;
  }

  if (message.action === "update_badge") {
    const tabId = message.tabId || (sender && sender.tab ? sender.tab.id : null);
    if (tabId) {
      updateBadge(tabId, message.count || 0);
    }
    sendResponse({ success: true });
    return false;
  }

  if (message.action === "toggle_badge") {
    const tabId = message.tabId || (sender && sender.tab ? sender.tab.id : null);
    if (tabId) {
      updateBadge(tabId, message.showBadge ? (message.count || 0) : 0);
    }
    sendResponse({ success: true });
    return false;
  }

  // ------------------------------------------------- network capture queries
  if (message.action === "get_network_media") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: true, available: false, items: [] });
      return false;
    }
    const tabId = message.tabId;
    // getItems is async: captures saved by a previous event-page instance are
    // read back from storage first.
    MPV_Network.getItems(tabId).then((items) => {
      sendResponse({ success: true, available: MPV_Network.isRegistered(), items: items });
    }).catch(() => {
      sendResponse({ success: true, available: MPV_Network.isRegistered(), items: [] });
    });
    return true;
  }

  if (message.action === "clear_network_media") {
    if (typeof MPV_Network !== "undefined") {
      if (typeof message.tabId === "number") MPV_Network.clearTab(message.tabId);
      else MPV_Network.clearAll();
    }
    sendResponse({ success: true });
    return false;
  }

  if (message.action === "get_permission_status") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: true, available: false, hostPermission: false });
      return false;
    }
    Promise.resolve(MPV_Network.ready())
      .then(() => sendResponse({
        success: true,
        available: MPV_Network.isRegistered(),
        hostPermission: MPV_Network.hasHostPermission()
      }))
      .catch(() => sendResponse({ success: true, available: false, hostPermission: false }));
    return true;
  }

  if (message.action === "request_permission") {
    if (typeof MPV_Network === "undefined") {
      sendResponse({ success: false, hostPermission: false });
      return false;
    }
    MPV_Network.requestPermission().then((granted) => {
      sendResponse({
        success: granted,
        hostPermission: granted,
        available: MPV_Network.isRegistered()
      });
    }).catch(() => {
      sendResponse({ success: false, hostPermission: false, available: false });
    });
    return true;
  }

  return false;
});
