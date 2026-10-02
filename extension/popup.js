/**
 * MPV Launcher - Popup Script
 *
 * The popup is the only user-facing part of the extension. It has to answer one
 * question - "what is playing here?" - and it gets its answers from two
 * independent sources, because neither one alone is reliable:
 *
 *   1. the network layer (network_capture.js) sees every request the page makes,
 *      including ones no DOM node refers to, but it only knows about media the
 *      page has already started loading;
 *   2. the DOM scan in content_script.js sees markup and player configuration
 *      including a stream the page has not fetched yet, but it can be fooled by
 *      a challenge page.
 *
 * Results are merged and deduplicated here, then filtered by the user's
 * preferences. Nothing is sent to the native host until the user clicks a row,
 * and the URL that is sent is exactly what was listed.
 *
 * Everything runs from one DOMContentLoaded handler because the popup document
 * is created fresh each time it opens, so there is no state to restore and no
 * teardown to do.
 */

const browserApi = typeof browser !== "undefined" ? browser : chrome;

/** Runs on popup open: binds the controls, loads the filters and scans. */
document.addEventListener("DOMContentLoaded", async () => {
  // ---- Elements, grouped by the section of the popup they belong to ----
  const pageTitleEl = document.getElementById("current-page-title");
  const btnPlayPage = document.getElementById("btn-play-page");
  const btnPlayPageText = document.getElementById("btn-play-page-text");
  const btnRescan = document.getElementById("btn-rescan");
  const mediaListEl = document.getElementById("media-list");
  const mediaCountBadge = document.getElementById("media-count-badge");
  const loadingSpinner = document.getElementById("loading-spinner");
  const emptyState = document.getElementById("empty-state");
  const statusMessageEl = document.getElementById("status-message");

  const btnToggleSettings = document.getElementById("btn-toggle-settings");
  const settingsPanel = document.getElementById("settings-panel");
  const selectLanguage = document.getElementById("select-language");
  const chkBadgeCounter = document.getElementById("chk-badge-counter");

  const chipStreams = document.getElementById("chip-streams");
  const chipFiles = document.getElementById("chip-files");
  const chipIframes = document.getElementById("chip-iframes");
  const inputMinSize = document.getElementById("input-min-size");
  const inputBlacklist = document.getElementById("input-blacklist");

  // ---- Translatable labels ----
  const lblActivePage = document.getElementById("lbl-active-page");
  const lblDetectedMedia = document.getElementById("lbl-detected-media");
  const lblScanning = document.getElementById("lbl-scanning");
  const lblEmptyMain = document.getElementById("lbl-empty-main");
  const lblEmptySub = document.getElementById("lbl-empty-sub");
  const lblLanguage = document.getElementById("lbl-language");
  const lblBadgeCounter = document.getElementById("lbl-badge-counter");
  const lblFilterTitle = document.getElementById("lbl-filter-title");
  const lblMinSize = document.getElementById("lbl-min-size");
  const lblBlacklist = document.getElementById("lbl-blacklist");
  const lblFilterNote = document.getElementById("lbl-filter-note");

  // ---- Session state ----
  let currentLang = "en";
  let showBadge = true;
  let activeTab = null;
  let currentFoundCount = 0;

  // Results from the DOM scan and from the network listener, kept apart so
  // filters can be re-applied to the network half without re-scanning.
  let domItems = [];
  let networkItems = [];
  let challenged = false;
  let networkAvailable = null;    // whether webRequest capture is installed

  // User preferences, persisted in extension storage. These are the defaults;
  // loadFilters replaces them with whatever was saved.
  const filters = {
    showStreams: true,
    showFiles: true,
    showIframes: true,
    minSizeKb: 0,
    blacklist: []
  };

  /** The keys above, so loading and saving can loop instead of repeating them. */
  const FILTER_KEYS = ["showStreams", "showFiles", "showIframes", "minSizeKb", "blacklist"];

  /**
   * Looks a key up in the active language, then in English.
   *
   * Returns the key itself when nothing matches, which makes a missing
   * translation obvious on screen instead of rendering an empty label.
   */
  function t(key) {
    if (typeof I18N !== "undefined" && I18N[currentLang] && I18N[currentLang][key]) {
      return I18N[currentLang][key];
    }
    if (typeof I18N !== "undefined" && I18N["en"] && I18N["en"][key]) {
      return I18N["en"][key];
    }
    return key;
  }

  /** Writes every visible label in the popup's current language. */
  function applyTranslations() {
    lblActivePage.textContent = t("active_page");
    btnPlayPageText.textContent = t("open_page_mpv");
    btnPlayPage.title = t("open_page_tooltip");
    lblDetectedMedia.textContent = t("detected_media");
    btnRescan.title = t("scan_again");
    lblScanning.textContent = t("scanning");
    lblEmptyMain.textContent = t("no_media");
    lblEmptySub.textContent = t("no_media_hint");
    lblLanguage.textContent = t("language") + ":";
    lblBadgeCounter.textContent = t("badge_counter");
    btnToggleSettings.title = t("settings");
    lblFilterTitle.textContent = t("filter_title");
    lblMinSize.textContent = t("filter_min_size");
    lblBlacklist.textContent = t("filter_blacklist");
    lblFilterNote.textContent = t("filter_note");
    chipStreams.textContent = t("filter_streams");
    chipFiles.textContent = t("filter_files");
    chipIframes.textContent = t("filter_iframes");

    mediaCountBadge.textContent = `${currentFoundCount} ${t("found_badge")}`;
  }

  /**
   * Loads the saved language and badge preference.
   *
   * With nothing saved the browser's own locale is matched against the table,
   * first exactly and then by its primary subtag, so "pt-BR" finds "pt" when
   * only that is present. Wrapped in a promise because storage.local uses
   * callbacks in Chromium and promises in Firefox; the callback form works in
   * both.
   */
  async function loadSettings() {
    return new Promise((resolve) => {
      browserApi.storage.local.get(["language", "showBadge"], (result) => {
        if (result && result.language) {
          currentLang = result.language;
        } else {
          const sysLang = (navigator.language || navigator.userLanguage || "en").toLowerCase();
          if (typeof I18N !== "undefined") {
            if (I18N[sysLang]) {
              currentLang = sysLang;
            } else {
              const prefix = sysLang.split("-")[0];
              const match = Object.keys(I18N).find(k => k === prefix || k.startsWith(prefix + "-"));
              if (match) currentLang = match;
            }
          }
        }

        if (result && typeof result.showBadge === "boolean") {
          showBadge = result.showBadge;
        } else {
          showBadge = true;
        }

        resolve();
      });
    });
  }

  /** Builds the language picker and binds the two settings-panel toggles. */
  function initLanguageSelect() {
    // Rebuilt from scratch so a language removed from the table disappears.
    selectLanguage.innerHTML = "";
    if (typeof I18N !== "undefined") {
      Object.keys(I18N).forEach((code) => {
        const opt = document.createElement("option");
        opt.value = code;
        opt.textContent = I18N[code].language_name || code;
        if (code === currentLang) opt.selected = true;
        selectLanguage.appendChild(opt);
      });
    }

    selectLanguage.addEventListener("change", () => {
      currentLang = selectLanguage.value;
      browserApi.storage.local.set({ language: currentLang });
      applyTranslations();
      if (mediaListEl.children.length > 0) {
        scanCurrentTab();
      }
    });

    chkBadgeCounter.checked = showBadge;
    chkBadgeCounter.addEventListener("change", () => {
      showBadge = chkBadgeCounter.checked;
      browserApi.storage.local.set({ showBadge: showBadge });
      browserApi.runtime.sendMessage({ action: "toggle_badge", showBadge: showBadge, tabId: activeTab ? activeTab.id : null, count: currentFoundCount });
    });

    btnToggleSettings.addEventListener("click", () => {
      settingsPanel.classList.toggle("hidden");
    });
  }

  // ---------------------------------------------------------------- filters

  function parseBlacklist(raw) {
    return String(raw || "")
      .split(",")
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
  }

  /** Mirrors mpvPassesFilter in media_types.js so the popup can filter locally. */
  function passesFilter(item) {
    if (filters.blacklist.length) {
      const haystacks = [item.url || "", item.referer || "", item.typeLabel || ""];
      for (const entry of filters.blacklist) {
        const needle = entry.toLowerCase();
        for (const h of haystacks) {
          if (h && h.toLowerCase().indexOf(needle) !== -1) return false;
        }
      }
    }

    const category = item.category || categoryForType(item.typeId || item.type);
    if (category === "stream") return filters.showStreams;
    if (category === "iframe") return filters.showIframes;
    if (category === "files") {
      if (!filters.showFiles) return false;
      if (filters.minSizeKb > 0 && item.sizeBytes && item.sizeBytes < filters.minSizeKb * 1024) return false;
      return true;
    }
    return true;
  }

  /**
   * Maps a media type onto one of the three things the filter chips switch.
   *
   * Anything not recognised is treated as a stream: that is the safer default,
   * because hiding a stream the user wanted to play is worse than showing an
   * extra row they can ignore.
   */
  function categoryForType(type) {
    if (type === "hls" || type === "dash" || type === "hds" || type === "mss") return "stream";
    if (type === "iframe") return "iframe";
    if (type === "video" || type === "audio") return "files";
    return "stream";
  }

  /**
   * Combines both sources into one deduplicated, filtered list.
   *
   * Network entries come first on purpose: they are requests the page actually
   * made, so they are the ones most likely to be the real video rather than an
   * advert or a preview the markup merely mentions. A URL already taken from
   * the network half is never replaced by the DOM half, which may describe it
   * less precisely.
   */
  function mergeResults() {
    const seen = new Set();
    const merged = [];

    // Network hits first: they are the ones the page itself cannot see.
    for (const item of networkItems) {
      if (!item || !item.url || seen.has(item.url)) continue;
      if (!passesFilter(item)) continue;
      seen.add(item.url);
      merged.push(item);
    }

    for (const item of domItems) {
      if (!item || !item.url || seen.has(item.url)) continue;
      if (!passesFilter(item)) continue;
      seen.add(item.url);
      merged.push(item);
    }

    return merged;
  }

  /** Re-renders the list and updates the toolbar badge from the merged result. */
  function refreshView() {
    const merged = mergeResults();
    renderMediaList(merged, challenged);

    browserApi.runtime.sendMessage({
      action: "update_badge",
      tabId: activeTab ? activeTab.id : null,
      count: merged.length
    });
  }

  /** Persists the filters. Called after every change so nothing needs a Save button. */
  function saveFilters() {
    browserApi.storage.local.set({ filters: filters });
  }

  /** Reflects the current filter state in the chips' highlight. */
  function syncFilterControls() {
    chipStreams.classList.toggle("active", filters.showStreams);
    chipFiles.classList.toggle("active", filters.showFiles);
    chipIframes.classList.toggle("active", filters.showIframes);
  }

  /**
   * Binds the filter panel.
   *
   * Every control re-filters and re-renders immediately rather than waiting for
   * a scan: both halves of the result are already in memory, so a filter change
   * never has to touch the page.
   */
  function initFilterControls() {
    syncFilterControls();

    function toggle(key) {
      filters[key] = !filters[key];
      syncFilterControls();
      saveFilters();
      refreshView();
    }

    chipStreams.addEventListener("click", () => toggle("showStreams"));
    chipFiles.addEventListener("click", () => toggle("showFiles"));
    chipIframes.addEventListener("click", () => toggle("showIframes"));

    // An unparseable or negative size means "no limit" rather than "hide all".
    inputMinSize.addEventListener("change", () => {
      const value = parseInt(inputMinSize.value, 10);
      filters.minSizeKb = isNaN(value) || value < 0 ? 0 : value;
      saveFilters();
      refreshView();
    });

    inputBlacklist.addEventListener("change", () => {
      filters.blacklist = parseBlacklist(inputBlacklist.value);
      saveFilters();
      refreshView();
    });
  }

  /**
   * Restores saved filters over the defaults.
   *
   * Keys are copied one by one rather than merging the stored object wholesale,
   * so a filter added in a later version still gets its default instead of
   * becoming undefined and hiding everything.
   */
  async function loadFilters() {
    let stored = null;
    try {
      const result = await browserApi.storage.local.get(["filters"]);
      stored = result && result.filters;
    } catch (e) {
      stored = null;
    }

    const defaults = (typeof MPV_FILTER_DEFAULTS !== "undefined")
      ? MPV_FILTER_DEFAULTS
      : { showStreams: true, showFiles: true, showIframes: true, minSizeKb: 0, blacklist: [] };

    const source = stored || defaults;
    for (const key of FILTER_KEYS) {
      if (source[key] !== undefined) filters[key] = source[key];
    }
    if (!Array.isArray(filters.blacklist)) filters.blacklist = [];

    inputMinSize.value = String(filters.minSizeKb || 0);
    inputBlacklist.value = filters.blacklist.join(", ");
  }

  /**
   * Asks the background page for everything the network layer captured.
   *
   * A failure here is expected and not fatal: Firefox tears down the MV3 event
   * page when idle, and an older background may not know this action at all. The
   * flag is set so the empty state can say "capture is unavailable" rather than
   * implying the page has no video.
   */
  async function fetchNetworkMedia() {
    networkItems = [];
    networkAvailable = null;      // null = not known yet
    if (!activeTab || !activeTab.id) return;
    try {
      const res = await browserApi.runtime.sendMessage({
        action: "get_network_media",
        tabId: activeTab.id
      });
      if (res && res.success) {
        networkAvailable = res.available !== false;
        if (Array.isArray(res.items)) networkItems = res.items;
      }
    } catch (e) {
      // background unreachable or webRequest missing: the DOM scan still works
      networkAvailable = false;
      networkItems = [];
    }
  }

  /** Writes the one-line status strip under the toolbar. */
  function setStatus(text, type = "normal") {
    statusMessageEl.textContent = text;
    statusMessageEl.className = "status-message";
    if (type === "success") statusMessageEl.classList.add("status-success");
    if (type === "error") statusMessageEl.classList.add("status-error");
    if (type === "warning") statusMessageEl.classList.add("status-warning");
  }

  async function getActiveTab() {
    try {
      const tabs = await browserApi.tabs.query({ active: true, currentWindow: true });
      return tabs && tabs.length > 0 ? tabs[0] : null;
    } catch {
      return null;
    }
  }

  /**
   * Hands a URL to the native host, which is what actually starts mpv.
   *
   * No referrer is invented here. The tab URL is NOT a usable substitute for a
   * Referer header: sites disagree about what they accept, and yt-dlp refuses
   * a page URL outright when given a made-up origin
   * ("ERROR: Unsupported URL" for an embed page with
   * --referer pointing at the embed host). Whatever the browser really sent is
   * filled in by the background script from the captured request; if nothing
   * was captured, no Referer is sent at all.
   */
  async function sendToMpv(url, referrer = null) {
    setStatus(t("starting_mpv"), "warning");
    try {
      const response = await browserApi.runtime.sendMessage({
        action: "open_in_mpv",
        url: url,
        referrer: referrer || ""
      });
      if (response && response.success) {
        setStatus(t("mpv_started"), "success");
      } else {
        const errMsg = response && response.error ? response.error : "Could not start.";
        setStatus(t("error_prefix") + errMsg, "error");
      }
    } catch (err) {
      setStatus(t("error_prefix") + (err && err.message ? err.message : String(err)), "error");
    }
  }

  /** Human-readable byte size; unknown or absent sizes render as 0 B. */
  function formatSize(bytes) {
    const n = Number(bytes) || 0;
    if (n >= 1024 * 1024 * 1024) return (n / 1024 / 1024 / 1024).toFixed(2) + " GB";
    if (n >= 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + " MB";
    if (n >= 1024) return Math.round(n / 1024) + " KB";
    return n + " B";
  }

  /**
   * Copies text and flashes the button's label as confirmation.
   *
   * The popup closes as soon as it loses focus, so the confirmation has to be
   * quick and the original label is restored on a timer.
   */
  function copyToClipboard(text, btn) {
    navigator.clipboard.writeText(text).then(() => {
      const orig = btn.textContent;
      btn.textContent = t("copied");
      setStatus(t("link_copied"), "success");
      setTimeout(() => { btn.textContent = orig; }, 1500);
    }).catch(() => {
      setStatus(t("copy_failed"), "error");
    });
  }

  /**
   * Builds the result list.
   *
   * Rows are assembled with createElement and textContent, never with innerHTML,
   * so a URL or title taken from a hostile page cannot inject markup into the
   * extension's own popup. innerHTML is used only to empty the list first.
   */
  /**
   * Drops this tab's stored network captures.
   *
   * The only list that accumulates. Captures live in the background keyed by tab
   * id, and a single-page app keeps the same tab across navigations, so watching
   * several videos on one site leaves every earlier stream in the list. The DOM
   * scan cannot accumulate: it re-runs on every popup open.
   *
   * Runs before each scan rather than from a button of its own. A separate
   * control for a rare action crowded the header, and a clear-only button left
   * the user looking at an empty list with nothing to repopulate it; folding it
   * into the scan makes "give me a fresh look at this page" one action.
   *
   * A failure is deliberately swallowed. The background may be asleep or
   * restarting, the scan that follows reads whatever is there, and a stale
   * entry is better than an error the user can do nothing about.
   */
  async function clearTabCaptures() {
    if (!activeTab || !activeTab.id) return;
    try {
      await browserApi.runtime.sendMessage({
        action: "clear_network_media",
        tabId: activeTab.id
      });
    } catch (e) {
      console.warn("clear_network_media failed", e);
    }
  }

  function renderMediaList(mediaItems, challenged) {
    mediaListEl.innerHTML = "";
    currentFoundCount = mediaItems.length;
    mediaCountBadge.textContent = `${currentFoundCount} ${t("found_badge")}`;

    if (mediaItems.length === 0) {
      emptyState.classList.remove("hidden");
      const lblMain = document.getElementById("lbl-empty-main");
      const lblSub = document.getElementById("lbl-empty-sub");

      if (networkAvailable === false) {
        // Capture is not installed: say so instead of blaming the page.
        lblMain.textContent = t("capture_off");
        lblSub.textContent = t("capture_off_hint");
      } else if (challenged) {
        lblMain.textContent = t("captcha_page");
        lblSub.textContent = t("captcha_page_hint");
      } else {
        lblMain.textContent = t("no_media");
        lblSub.textContent = t("no_media_hint");
      }
      return;
    }

    emptyState.classList.add("hidden");

    mediaItems.forEach((item) => {
      const li = document.createElement("li");
      li.className = "media-item";

      const header = document.createElement("div");
      header.className = "media-header";

      const rawType = item.typeId || item.type || "media";
      const typeKey = `type_${rawType}`;
      const tag = document.createElement("span");
      tag.className = `media-type-tag tag-${rawType}`;
      tag.textContent = t(typeKey) || (item.typeLabel || rawType.toUpperCase());

      const label = document.createElement("span");
      label.className = "media-label";
      label.textContent = item.label || item.typeLabel || item.url;
      label.title = item.label || item.url;

      header.appendChild(label);
      header.appendChild(tag);

      const urlDiv = document.createElement("div");
      urlDiv.className = "media-url";
      urlDiv.textContent = item.url;
      urlDiv.title = item.url;

      // Provenance for items the network listener found.
      if (item.source === "network") {
        li.className = "media-item from-network";
        const meta = document.createElement("div");
        meta.className = "media-meta";

        const via = document.createElement("span");
        via.textContent = t("filter_note_via") + " " + (item.matchedBy === "content-type" ? "Content-Type" : t("filter_note_ext"));
        meta.appendChild(via);

        if (item.sizeBytes) {
          const size = document.createElement("span");
          size.className = "size";
          size.textContent = formatSize(item.sizeBytes);
          meta.appendChild(size);
        }

        if (item.headers && item.headers.length) {
          const hdr = document.createElement("span");
          hdr.textContent = t("filter_note_headers") + " " + item.headers.length;
          meta.appendChild(hdr);
        }

        li.appendChild(meta);
      }

      const actions = document.createElement("div");
      actions.className = "media-actions";

      const btnCopy = document.createElement("button");
      btnCopy.className = "btn btn-secondary btn-small";
      btnCopy.textContent = t("btn_copy");
      btnCopy.onclick = () => copyToClipboard(item.url, btnCopy);

      const btnPlay = document.createElement("button");
      btnPlay.className = "btn btn-primary btn-small";
      btnPlay.textContent = t("btn_open_mpv");
      btnPlay.onclick = () => sendToMpv(item.url);

      actions.appendChild(btnCopy);
      actions.appendChild(btnPlay);

      li.appendChild(header);
      li.appendChild(urlDiv);
      li.appendChild(actions);

      mediaListEl.appendChild(li);
    });
  }

  /**
   * The probe must live in the page world to wrap fetch/XHR, so it is injected
   * as a real <script>. It publishes results on <html>'s dataset, which the
   * content script reads back. If the page CSP blocks it we simply lose the
   * extra signals, not the scan.
   */
  async function injectNetworkProbe() {
    try {
      await browserApi.scripting.executeScript({
        target: { tabId: activeTab.id, allFrames: true },
        files: ["network_probe.js"],
        world: "MAIN"
      });
      return true;
    } catch {
      return false;
    }
  }

  /**
   * The main scan.
   *
   * Order matters. The probe is injected first and given a moment to run,
   * because it can only record requests that happen while it is installed - a
   * manifest the player already fetched is recovered later by the network
   * listener instead. The DOM scan then runs across every frame, and only after
   * that are the network results requested, because the background needs a
   * moment to answer for a tab that was just opened.
   */
  /**
   * @param {{fresh?: boolean}} options
   *   fresh - discard the tab's stored captures first. Set only by the rescan
   *   button. Opening the popup leaves them alone, because just looking at the
   *   list should not destroy what the tab has captured, and the scan below
   *   cannot reproduce it: the probe only records requests made while it is
   *   installed and the webRequest listener only fires on new ones.
   */
  async function scanCurrentTab(options = {}) {
    if (!activeTab || !activeTab.id) return;

    loadingSpinner.classList.remove("hidden");
    emptyState.classList.add("hidden");
    mediaListEl.innerHTML = "";
    setStatus(t("scanning_detail"));

    try {
      // Only on an explicit rescan: see the parameter note above.
      if (options.fresh) await clearTabCaptures();

      await injectNetworkProbe();

      // Give the probe a moment to record the manifest a player just fetched.
      await new Promise((r) => setTimeout(r, 350));

      const results = await browserApi.scripting.executeScript({
        target: { tabId: activeTab.id, allFrames: true },
        files: ["content_script.js"]
      });

      loadingSpinner.classList.add("hidden");

      const domFound = [];
      const seen = new Set();
      challenged = false;

      if (results && Array.isArray(results)) {
        for (const frameResult of results) {
          const payload = frameResult && frameResult.result;
          if (!payload) continue;

          // content_script returns { challenged, media }
          if (payload.challenged) challenged = true;

          // A challenged frame can still expose real media (most pages embed a
          // captcha widget), so its items are kept rather than discarded.
          const items = Array.isArray(payload) ? payload : payload.media;
          if (!Array.isArray(items)) continue;

          for (const item of items) {
            if (!item || !item.url || seen.has(item.url)) continue;
            seen.add(item.url);
            domFound.push(item);
          }
        }
      }
      domItems = domFound;

      await fetchNetworkMedia();
      const merged = mergeResults();
      renderMediaList(merged, challenged);

      // Break the counts down so a silent miss is visible instead of guessed.
      // Break the counts down so a silent miss is visible instead of guessed:
      // "0 DOM / 0 network" points at a different problem from
      // "0 DOM / 14 network", and neither is the same as capture being off.
      const netCount = networkItems.length;
      const domCount = domItems.length;
      if (merged.length === 0) {
        if (networkAvailable === false) {
          setStatus(t("capture_off"), "warning");
        } else if (challenged) {
          setStatus(t("captcha_page"), "warning");
        } else {
          setStatus(`${domCount} ${t("diag_dom")} / ${netCount} ${t("diag_net")}`);
        }
      } else {
        setStatus(`${merged.length} ${t("media_sources_listed")} (${t("diag_dom")}: ${domCount}, ${t("diag_net")}: ${netCount})`);
      }

      browserApi.runtime.sendMessage({
        action: "update_badge",
        tabId: activeTab.id,
        count: merged.length
      });
    } catch (err) {
      // Scripting was refused outright - a browser internal page, or a page
      // where injection is not permitted. Show the empty state rather than
      // leaving the spinner running.
      loadingSpinner.classList.add("hidden");
      setStatus(t("scripts_blocked"), "warning");
      renderMediaList([], false);
    }
  }

  // Startup order: preferences first, because the very first render already
  // depends on the language and the filters, then the scan.
  await loadSettings();
  await loadFilters();
  initLanguageSelect();
  initFilterControls();
  applyTranslations();

  activeTab = await getActiveTab();
  if (activeTab) {
    pageTitleEl.textContent = activeTab.title || activeTab.url;
    btnPlayPage.onclick = () => sendToMpv(activeTab.url);
    btnRescan.onclick = () => scanCurrentTab({ fresh: true });
    await scanCurrentTab();
  } else {
    pageTitleEl.textContent = t("no_tab_found");
    setStatus(t("tab_detect_error"), "error");
  }
});
