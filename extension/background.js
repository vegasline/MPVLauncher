/**
 * MPV Launcher - background script
 */

const HOST_NAME = "com.mpv.launcher";
const api = (typeof browser !== "undefined" && browser.runtime && browser.runtime.sendNativeMessage)
  ? browser
  : chrome;

function sendNative(url, referrer) {
  const payload = { url: url, referrer: referrer || "" };

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

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action !== "open_in_mpv") return false;

  const targetUrl = message.url;
  const referrer = message.referrer || (sender && sender.tab ? sender.tab.url : "");
  if (!targetUrl) {
    sendResponse({ success: false, error: "Invalid or empty URL." });
    return false;
  }

  sendNative(targetUrl, referrer)
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
});
