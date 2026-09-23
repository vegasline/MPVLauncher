/**
 * MPV Launcher - popup
 */

const browserApi = typeof browser !== "undefined" ? browser : chrome;

document.addEventListener("DOMContentLoaded", async () => {
  const pageTitleEl = document.getElementById("current-page-title");
  const btnPlayPage = document.getElementById("btn-play-page");
  const btnRescan = document.getElementById("btn-rescan");
  const mediaListEl = document.getElementById("media-list");
  const mediaCountBadge = document.getElementById("media-count-badge");
  const loadingSpinner = document.getElementById("loading-spinner");
  const emptyState = document.getElementById("empty-state");
  const statusMessageEl = document.getElementById("status-message");

  let activeTab = null;

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

  async function sendToMpv(url, referrer = null) {
    setStatus("Starting MPV...", "warning");
    try {
      const ref = referrer || (activeTab ? activeTab.url : "");
      const response = await browserApi.runtime.sendMessage({ action: "open_in_mpv", url: url, referrer: ref });
      if (response && response.success) {
        setStatus("MPV started.", "success");
      } else {
        const errMsg = response && response.error ? response.error : "Could not start.";
        setStatus("Error: " + errMsg, "error");
      }
    } catch (err) {
      setStatus("Error: " + (err && err.message ? err.message : String(err)), "error");
    }
  }

  function copyToClipboard(text, btn) {
    navigator.clipboard.writeText(text).then(() => {
      const orig = btn.textContent;
      btn.textContent = "Copied";
      setStatus("Link copied to clipboard.", "success");
      setTimeout(() => { btn.textContent = orig; }, 1500);
    }).catch(() => {
      setStatus("Copy failed.", "error");
    });
  }

  function renderMediaList(mediaItems) {
    mediaListEl.innerHTML = "";
    mediaCountBadge.textContent = `${mediaItems.length} found`;

    if (mediaItems.length === 0) {
      emptyState.classList.remove("hidden");
      return;
    }

    emptyState.classList.add("hidden");

    mediaItems.forEach((item) => {
      const li = document.createElement("li");
      li.className = "media-item";

      const header = document.createElement("div");
      header.className = "media-header";

      const tag = document.createElement("span");
      tag.className = `media-type-tag tag-${item.type || "video"}`;
      tag.textContent = item.type ? item.type.toUpperCase() : "MEDIA";

      const label = document.createElement("span");
      label.className = "media-label";
      label.textContent = item.label || item.url;
      label.title = item.label || item.url;

      header.appendChild(label);
      header.appendChild(tag);

      const urlDiv = document.createElement("div");
      urlDiv.className = "media-url";
      urlDiv.textContent = item.url;
      urlDiv.title = item.url;

      const actions = document.createElement("div");
      actions.className = "media-actions";

      const btnCopy = document.createElement("button");
      btnCopy.className = "btn btn-secondary btn-small";
      btnCopy.textContent = "Copy";
      btnCopy.onclick = () => copyToClipboard(item.url, btnCopy);

      const btnPlay = document.createElement("button");
      btnPlay.className = "btn btn-primary btn-small";
      btnPlay.textContent = "Open in MPV";
      btnPlay.onclick = () => sendToMpv(item.url);

      actions.appendChild(btnCopy);
      actions.appendChild(btnPlay);

      li.appendChild(header);
      li.appendChild(urlDiv);
      li.appendChild(actions);

      mediaListEl.appendChild(li);
    });
  }

  async function scanCurrentTab() {
    if (!activeTab || !activeTab.id) return;

    loadingSpinner.classList.remove("hidden");
    emptyState.classList.add("hidden");
    mediaListEl.innerHTML = "";
    setStatus("Scanning page and iframes...");

    try {
      const results = await browserApi.scripting.executeScript({
        target: { tabId: activeTab.id, allFrames: true },
        files: ["content_script.js"]
      });

      loadingSpinner.classList.add("hidden");

      const allFound = [];
      const seen = new Set();

      if (results && Array.isArray(results)) {
        for (const frameResult of results) {
          if (frameResult && Array.isArray(frameResult.result)) {
            for (const item of frameResult.result) {
              if (!seen.has(item.url)) {
                seen.add(item.url);
                allFound.push(item);
              }
            }
          }
        }
      }

      renderMediaList(allFound);
      setStatus(`${allFound.length} media source(s) listed.`);
    } catch (err) {
      loadingSpinner.classList.add("hidden");
      setStatus("Scripts cannot run on this page.", "warning");
      renderMediaList([]);
    }
  }

  activeTab = await getActiveTab();
  if (activeTab) {
    pageTitleEl.textContent = activeTab.title || activeTab.url;
    btnPlayPage.onclick = () => sendToMpv(activeTab.url);
    btnRescan.onclick = () => scanCurrentTab();
    await scanCurrentTab();
  } else {
    pageTitleEl.textContent = "No tab found";
    setStatus("Could not detect the active tab.", "error");
  }
});
