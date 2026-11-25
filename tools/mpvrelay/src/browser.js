// Finds an installed Edge or Chrome and starts it in a throwaway profile that the
// launcher (and the user) never sees. No download, no Playwright, no TCP port.
import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { CdpClient } from "./cdp.js";

const KNOWN = [
  ["edge", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe"],
  ["edge", "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe"],
  ["chrome", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"],
  ["chrome", "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe"],
  ["chrome", path.join(process.env.LOCALAPPDATA || "", "Google\\Chrome\\Application\\chrome.exe")],
];

function queryAppPath(exe) {
  const key = `HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`;
  const hkcu = `HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\${exe}`;
  for (const k of [hkcu, key]) {
    const result = spawnSync("reg", ["query", k, "/ve"], { windowsHide: true, encoding: "utf8" });
    if (result.status !== 0 || !result.stdout) continue;
    const match = /REG_SZ\s+(.+?)\s*$/m.exec(result.stdout);
    if (match && fs.existsSync(match[1])) return match[1].replace(/^"|"$/g, "");
  }
  return null;
}

/**
 * Returns { path, kind } for the first usable browser, or null.
 * `preferred` is "edge", "chrome" or empty for "any".
 */
export function findBrowser(preferred = "") {
  // Chrome first: Edge's bundled profile is the one sites tend to challenge.
  const order = preferred ? [preferred] : ["chrome", "edge"];
  for (const kind of order) {
    for (const [candidateKind, candidatePath] of KNOWN) {
      if (candidateKind !== kind) continue;
      if (candidatePath && fs.existsSync(candidatePath)) return { path: candidatePath, kind };
    }
    const exe = kind === "edge" ? "msedge.exe" : "chrome.exe";
    const fromRegistry = queryAppPath(exe);
    if (fromRegistry) return { path: fromRegistry, kind };
  }
  return null;
}

const COMMON_FLAGS = [
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
  "--remote-debugging-pipe",
];

/**
 * Starts the browser hidden. Returns { client, child, close() }.
 * `headless` switches to --headless=new; otherwise the window is placed far
 * off-screen so it never shows on any monitor.
 */
export async function launchBrowser(browser, options = {}) {
  const userDataDir = options.userDataDir;
  if (!userDataDir) throw new Error("userDataDir is required");
  fs.mkdirSync(userDataDir, { recursive: true });

  const args = [...COMMON_FLAGS, `--user-data-dir=${userDataDir}`];
  if (options.headless) {
    args.unshift("--headless=new");
  } else {
    args.push("--window-size=1280,720", "--window-position=-32000,-32000");
  }
  args.push("about:blank");

  const child = spawn(browser.path, args, {
    stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"],
    windowsHide: true,
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
      /* ignore */
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
      return client
        .send("Browser.close", {}, undefined, 4000)
        .catch(() => {})
        .then(
          () =>
            new Promise((resolve) => {
              const timer = setTimeout(() => {
                try {
                  child.kill();
                } catch {
                  /* ignore */
                }
                resolve();
              }, 4000);
              child.on("exit", () => {
                clearTimeout(timer);
                resolve();
              });
            })
        );
    },
  };
}

/**
 * Brings the off-screen window back into view so the viewer can solve a bot
 * wall themselves. Best effort: a headless browser has no window to show, and
 * a failed move must never fail the session.
 */
export async function showWindow(client, targetId) {
  try {
    const { windowId } = await client.send(
      "Browser.getWindowForTarget",
      { targetId },
      undefined,
      5000
    );
    await client.send(
      "Browser.setWindowBounds",
      { windowId, bounds: { windowState: "normal" } },
      undefined,
      5000
    );
    await client.send(
      "Browser.setWindowBounds",
      { windowId, bounds: { left: 60, top: 60, width: 1280, height: 720 } },
      undefined,
      5000
    );
    return true;
  } catch {
    return false;
  }
}
