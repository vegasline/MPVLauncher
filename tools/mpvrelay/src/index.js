// mpvrelay - Browser relay for MPVLauncher.
//
// Renders the page in an isolated browser profile, captures the opaque stream
// URL the player asks for, and serves playlists and segments to mpv from a
// loopback-only HTTP server. The capture is done in the browser's own renderer,
// so the site's origin checks keep working while mpv only ever talks to
// 127.0.0.1.
//
// stdout carries exactly one JSON line per event (see protocol.js). The browser
// profile is the only thing written to disk, one folder per host, and it is
// kept between runs on purpose (see the profileDir comment below). Nothing in
// it is ever read by this process, and nothing about the page's credentials is
// logged.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { parseArgs } from "./args.js";
import { createBrokerFetcher } from "./broker.js";
import { findBrowser, launchBrowser, showWindow } from "./browser.js";
import { createRemoteFetcher, findStream, openNeutralPage } from "./driver.js";
import { EventType, writeError, writeEvent } from "./protocol.js";
import { isPlaylist, rewritePlaylist } from "./playlist.js";
import { RelayServer } from "./server.js";
import { bareUrl } from "./urls.js";

const MAX_PLAYLIST_BYTES = 4 * 1024 * 1024;

function logLine(file, line) {
  if (!file) return;
  try {
    fs.appendFileSync(file, `${new Date().toISOString()} ${line}\n`);
  } catch {
    // Logging must never be the reason the relay fails.
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
    fs.rmSync(dir, { recursive: true, force: true });
  } catch {
    // A locked file in the ephemeral profile is not worth failing the session.
  }
}

// One profile folder per site. The name has to survive on disk, so it is the
// hostname with anything that is not a plain name character folded away.
function profileHost(pageUrl) {
  try {
    return new URL(pageUrl).hostname.replace(/[^a-z0-9.-]+/gi, "_") || "default";
  } catch {
    return "default";
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  // Brokered capture is handed the stream URL instead of a page: the viewer's
  // tab already has the page open, so no page argument is needed or expected.
  if (!options.page && !options.broker) {
    writeError("no_page", "No page URL was provided.");
    return 1;
  }

  // The page URL comes from a browser message or a pasted link, so it is
  // checked before a browser is started: a typo must not cost a profile, a
  // window and 45 seconds of waiting.
  if (options.page) {
    let pageUri = null;
    try {
      pageUri = new URL(options.page);
    } catch {
      pageUri = null;
    }
    if (!pageUri || (pageUri.protocol !== "http:" && pageUri.protocol !== "https:")) {
      writeError("bad_page", "The page URL must be an http(s) address.");
      return 1;
    }
  }

  const sessionDir =
    options.sessionDir || fs.mkdtempSync(path.join(os.tmpdir(), "mpvrelay-"));
  fs.mkdirSync(sessionDir, { recursive: true });

  // The profile outlives the session on purpose. A fresh profile on every run
  // looks like a brand-new browser to the site: no clearance cookie from the
  // last visit, so a bot wall challenges every single time - and repeatedly
  // registering new browsers is what gets a network blocked. One profile per
  // host keeps the site's own view of us stable. It sits next to the session
  // folders (relay/profiles/<host>) rather than inside one, and cleanup never
  // removes it.
  const profileDir = path.join(
    path.dirname(sessionDir),
    "profiles",
    profileHost(options.page)
  );
  const fallbackProfileDir = path.join(sessionDir, "profile");
  const logFile = options.logFile || "";
  const log = (line) => logLine(logFile, line);
  // Plan A: every remote fetch is performed by the viewer's own tab, so the
  // launcher brokers requests over stdin/stdout instead of the helper starting
  // a second browser (which is what made sites block the whole network).
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
      // ignore
    }
    try {
      if (browser) browser.close();
    } catch {
      // ignore
    }
    // The profile is deliberately left behind: it is what stops every run from
    // looking like a brand-new browser to the site. Only the session goes.
    removeDir(sessionDir);
  };

  // The launcher cannot know mpv's pid before mpv exists, and mpv cannot start
  // before the URL exists. The pid therefore arrives on stdin after "ready".
  let attachedMpvPid = 0;
  let stdinBuffer = "";

  const shutdown = (code) => {
    cleanup();
    process.exit(code);
  };

  process.on("SIGINT", () => shutdown(130));
  process.on("SIGTERM", () => shutdown(143));
  // A detached helper is started by the native host, a process that exits the
  // moment it has handed the URL over, so its pipe closing says nothing about
  // mpv. There the pipe only aborts a session that has not attached yet.
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
        // Unknown input is ignored; the contract only defines "attach".
        continue;
      }
      if (brokerFetcher && brokerFetcher.handle(command)) continue;
      if (command && command.cmd === "attach" && Number(command.mpvPid) > 0) {
        attachedMpvPid = Number(command.mpvPid);
      }
    }
  });
  process.stdin.resume();

  // Brokered capture has no page argument: the stream URL is same-origin with
  // the tab that already holds it, so it supplies the origin too.
  const pageOrigin = new URL(options.page || options.stream).origin;
  let fetchRemote = null;
  let capture;

  if (brokerFetcher && options.stream) {
    // The viewer's own browser already owns the page session, so no second
    // browser is opened: every fetch is brokered to the tab that has the URL.
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
      headless: options.headless,
    });
  } catch (error) {
    // A profile a crashed run left locked must not end the session: fall back
    // to a throwaway profile for this session only.
    log(`profile unusable (${error.message}), using a temporary one`);
    browser = await launchBrowser(found, {
      userDataDir: fallbackProfileDir,
      headless: options.headless,
    });
  }
  browser.client.onClose = () => shutdown(0);

  await browser.client.waitForReady();
  log("browser ready");

  writeEvent(EventType.Progress, { phase: "navigating" });
  capture = await findStream(browser.client, {
    pageUrl: options.page,
    timeoutMs: options.timeoutSeconds * 1000,
    log,
    verbose: options.verbose,
    onProgress: (elapsedMs) =>
      writeEvent(EventType.Progress, {
        phase: "waiting",
        elapsedMs,
      }),
    onChallenge: (targetId) => {
      if (options.headless) return false;
      return showWindow(browser.client, targetId);
    },
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

  // The first playlist is fetched through the browser as well, so the token is
  // consumed exactly once and mpv starts on a URL the relay already understands.
  const first = await fetchRemote(capture.streamUrl, {});
  if (!first.ok) {
    writeError(
      first.status === 403 ? "token_expired" : "stream_unavailable",
      first.status === 403
        ? "The stream token was rejected. Reload the page and try again."
        : `The stream answered HTTP ${first.status}.`
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
      segments: rewritten.uris.length,
    });
  } else {
    writeEvent(EventType.Ready, {
      url: server.urlFor(capture.streamUrl),
      playlist: "direct",
    });
  }

  // Stay alive while mpv runs. The launcher also kills the whole job, but a
  // clean exit removes the profile folder and stops the server.
  const startedAt = Date.now();
  const tick = setInterval(() => {
    if (options.mpvPid && !processAlive(options.mpvPid)) shutdown(0);
    if (attachedMpvPid && !processAlive(attachedMpvPid)) shutdown(0);
    if (options.detach) {
      // The native host is gone by now, so its pid proves nothing. A session
      // that never got an mpv is an orphan: wait five minutes, then clean up.
      if (!attachedMpvPid && Date.now() - startedAt > 300000) shutdown(0);
      return;
    }
    if (options.launcherPid && !processAlive(options.launcherPid)) shutdown(0);
  }, 2000);
  tick.unref?.();

  // Keep the event loop anchored on the browser pipes.
  await new Promise(() => {});
  return 0;
}

main()
  .then((code) => {
    if (typeof code === "number" && code !== 0) process.exit(code);
  })
  .catch((error) => {
    writeError("unexpected", String(error && error.message ? error.message : error));
    process.exit(1);
  });
