// Drives the page in the throwaway browser and hands back a fresh stream URL.
//
// Nothing is fetched from outside the browser: the page is opened, playback is
// started with the same gestures a viewer would make, the stream request is
// captured and immediately failed (so a single-use token is not spent), and all
// later traffic is issued by fetch() from a neutral same-origin document.
import { isPrivateHost, hasMediaExtension, sameOrigin } from "./urls.js";

const PLAY_SCRIPT = `(() => {
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

const FETCH_SCRIPT = (url, pageUrl, headers) => `(async () => {
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

// A stream URL is what a media player would have asked for: same-origin with
// the page, no media extension, and an obviously opaque final segment.
//
// The page's own address is excluded: an episode slug ("...-17-bolum-izle") is
// opaque enough to pass every other check, and capturing it makes the relay
// serve the HTML document to mpv - which opens an empty window and plays
// nothing.
export function looksLikeStreamUrl(candidate, pageUrl) {
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
  // Bot-wall plumbing: a challenge-platform or cdn-cgi request is site
  // infrastructure, never the stream, and capturing it plays nothing.
  if (/\/cdn-cgi\/|\/challenge-platform\/|turnstile/i.test(parsed.pathname)) return false;
  const segments = parsed.pathname.split("/").filter(Boolean);
  const last = segments[segments.length - 1] || "";
  if (!last || last.includes(".")) return false;
  return /[A-Za-z0-9_-]{12,}/.test(last);
}

// Bot walls ask for a challenge in the page itself: Cloudflare Turnstile or the
// site's own "network challenge" image question. Neither can be solved by a
// hidden, freshly-profiled browser, so the relay says so instead of waiting out
// the whole timeout and then claiming the player never asked for a stream.
const CHALLENGE_REQUEST =
  /challenges\.cloudflare\.com|cdn-cgi\/challenge-platform|\/network-challenge\/|turnstile/i;

// Cloudflare's plain block page ("Access restricted" / "Erişim kısıtlandı") is a
// 200 response with a human-readable reason: the network is a VPN, proxy or
// hosting range. No cookie, profile or click gets past it, so the relay reports
// the real cause instead of blaming the player.
const RESTRICTED_PAGE =
  /eri[sş]im k[sı][ıi]tland[ıi]|access restricted|barındırma a[gğ]ı|hosting network|anonimleştirme|anonymi[sz]ation|vpn or proxy|not allowed from this network/i;

// Reads the page's verdict without touching site scripts: title plus the first
// lines of body text are enough to tell a challenge page from a block page.
const VERDICT_SCRIPT = `(() => {
  const text = (document.title + " " + (document.body ? document.body.innerText : "")).slice(0, 1200);
  return { title: document.title, text };
})()`;

export async function findStream(client, options) {
  const { pageUrl, timeoutMs = 45000, log = () => {}, verbose = false, onProgress = () => {} } = options;
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
        requestStage: "Request",
      })),
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
      // Fail the player's own request: a single-use token must not be spent by
      // the browser, the relay needs it fresh for fetch().
      client
        .send("Fetch.failRequest", { requestId: params.requestId, errorReason: "Aborted" }, sessionId)
        .catch(() => {});
      return;
    }
    client.send("Fetch.continueRequest", { requestId: params.requestId }, sessionId).catch(() => {});
  });

  await client.send("Page.navigate", { url: pageUrl }, sessionId, 60000);
  log(`page opened (${state.requests} requests so far)`);

  let waited = 0;
  while (!state.found && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    waited += 1500;
    try {
      await client.send("Runtime.evaluate", { expression: PLAY_SCRIPT, returnByValue: true }, sessionId);
    } catch {
      /* navigation in flight */
    }
    if (waited >= 3000) {
      // A block page answers 200 and looks like a normal document: read what it
      // actually says before spending the rest of the timeout on it.
      try {
        const verdict = await client.send(
          "Runtime.evaluate",
          { expression: VERDICT_SCRIPT, returnByValue: true },
          sessionId,
          5000
        );
        const value = (verdict && verdict.result && verdict.result.value) || {};
        if (RESTRICTED_PAGE.test(`${value.title || ""} ${value.text || ""}`)) {
          state.blocked = true;
          log(`the site refuses this network: ${String(value.title || "").slice(0, 80)}`);
          break;
        }
      } catch {
        /* navigation in flight */
      }
    }
    onProgress(Date.now() - startedAt);
    if (waited % 9000 === 0) log(`still waiting for the stream (${state.requests} requests)`);
    if (state.challenge && waited >= 4500 && !state.windowShown) {
      // The profile is kept between runs, so a bot wall is usually solved once
      // and never seen again. When it is not, the page is useless while the
      // window sits off-screen: bring it back so the viewer can solve the
      // challenge instead of watching a timeout.
      state.windowShown = true;
      log("the site is asking for human verification; showing the window");
      try {
        await onChallenge(target.targetId);
      } catch {
        /* showing the window is best effort */
      }
    }
  }

  if (!state.found) {
    return {
      sessionId,
      streamUrl: null,
      requests: state.requests,
      challenge: state.challenge,
      blocked: state.blocked,
    };
  }

  await client.send("Fetch.disable", {}, sessionId).catch(() => {});
  return { sessionId, streamUrl: state.found, requests: state.requests };
}

// The neutral document is same-origin with the page, so fetch() carries the
// site's own cookies while credentials stay "same-origin" as the prototype showed.
export async function openNeutralPage(client, sessionId, pageUrl, log = () => {}) {
  const neutral = new URL("/robots.txt", pageUrl).toString();
  try {
    await client.send("Page.navigate", { url: neutral }, sessionId, 30000);
    await new Promise((resolve) => setTimeout(resolve, 1200));
    log("neutral page ready");
  } catch (error) {
    log(`neutral page failed, keep the current document: ${error.message}`);
  }
}

export function createRemoteFetcher(client, sessionId, pageUrl, log = () => {}) {
  return async function fetchRemote(url, { headers = {} } = {}) {
    let result;
    try {
      result = await client.send(
        "Runtime.evaluate",
        {
          expression: FETCH_SCRIPT(url, pageUrl, headers),
          awaitPromise: true,
          returnByValue: true,
        },
        sessionId,
        90000
      );
    } catch (error) {
      log(`fetch failed: ${error.message}`);
      return { ok: false, status: 0, error: error.message, body: Buffer.alloc(0) };
    }
    const value = (result && result.result && result.result.value) || {};
    if (!value.ok) {
      log(`fetch rejected: ${value.error || "unknown"}`);
      return { ok: false, status: 0, error: value.error || "fetch failed", body: Buffer.alloc(0) };
    }
    return {
      ok: true,
      status: value.status,
      contentType: value.contentType || "",
      body: Buffer.from(value.body || "", "base64"),
    };
  };
}
