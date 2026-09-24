/**
 * MPV Launcher - Content Script
 */

(function () {
  const VIDEO_EXTENSIONS = /\.(m3u8|mpd|mp4|webm|mkv|avi|mov|flv|ts)(\?.*)?$/i;
  const STREAM_REGEX = /(https?:\\?\/\\?\/[^\s"'<>]+\.(?:m3u8|mp4|webm|mpd)[^\s"'<>]*)/gi;
  const VIDEO_HOSTS = [
    'youtube.com',
    'youtu.be',
    'vidmoly.',
    'ok.ru',
    'streamtape.com',
    'doodstream.',
    'dood.',
    'mixdrop.',
    'fembed.',
    'dailymotion.com',
    'vimeo.com',
    'voe.sx',
    'vidoza.net',
    'streamwish.',
    'filelions.',
    'dropload.'
  ];

  function sanitizeUrl(raw) {
    if (!raw || typeof raw !== 'string') return null;
    let clean = raw.trim().replace(/\\\//g, '/').replace(/\\"/g, '').replace(/\\'/g, '');
    if (clean.startsWith('blob:') || clean.startsWith('javascript:') || clean.startsWith('data:')) {
      return null;
    }
    try {
      return new URL(clean, window.location.href).href;
    } catch {
      return null;
    }
  }

  function classifyUrl(url) {
    if (!url) return null;
    const lower = url.toLowerCase();
    if (lower.includes('youtube.com') || lower.includes('youtu.be')) return 'youtube';
    if (lower.includes('.m3u8') || lower.includes('.mpd')) return 'hls';
    if (VIDEO_EXTENSIONS.test(lower)) return 'video';
    for (const host of VIDEO_HOSTS) {
      if (lower.includes(host)) return 'iframe';
    }
    return null;
  }

  function getHostLabel(url) {
    try {
      const u = new URL(url);
      return u.hostname.replace(/^www\./, '');
    } catch {
      return 'Media source';
    }
  }

  function scanMedia() {
    const results = [];
    const seen = new Set();

    function addCandidate(rawUrl, fallbackLabel, fallbackType) {
      const url = sanitizeUrl(rawUrl);
      if (!url || seen.has(url)) return;

      const detectedType = classifyUrl(url) || fallbackType;
      if (detectedType) {
        seen.add(url);
        const hostName = getHostLabel(url);
        results.push({
          url: url,
          type: detectedType,
          label: fallbackLabel ? `${fallbackLabel} (${hostName})` : hostName
        });
      }
    }

    // 1. HTML5 Video Etiketleri
    document.querySelectorAll('video').forEach((video, idx) => {
      if (video.src) addCandidate(video.src, `Video #${idx + 1}`, 'video');
      if (video.currentSrc) addCandidate(video.currentSrc, `Active video #${idx + 1}`, 'video');
      video.querySelectorAll('source').forEach((srcEl, sIdx) => {
        if (srcEl.src) addCandidate(srcEl.src, `Source #${idx + 1}.${sIdx + 1}`, 'video');
      });
    });

    // 2. Iframe ve Lazy-load Kaynakları
    document.querySelectorAll('iframe').forEach((iframe, idx) => {
      const src = iframe.getAttribute('src') || 
                  iframe.getAttribute('data-src') || 
                  iframe.getAttribute('data-lazy-src') || 
                  iframe.src;
      if (src) addCandidate(src, `Embedded frame #${idx + 1}`, 'iframe');
    });

    // 3. Inline Script içi Stream URL'leri ve Packed JS Tarayıcısı
    function unpackDeanEdwards(scriptText) {
      const match = scriptText.match(/eval\(function\(p,a,c,k,e,d\)\{[\s\S]*?\}\(([\s\S]*?)\)\)/);
      if (!match) return "";
      try {
        const argsStr = match[1];
        // Basit p,a,c,k unpack
        const fn = new Function("return (function(p,a,c,k,e,d){" + match[0].substring(match[0].indexOf("{") + 1, match[0].lastIndexOf("}")) + "}(" + argsStr + "))");
        return fn() || "";
      } catch {
        return "";
      }
    }

    const FILE_REGEX = /(?:sources\s*:\s*\[\s*\{\s*file\s*:\s*|file\s*:\s*|src\s*:\s*)["']([^"']+\.(?:m3u8|mpd|mp4|webm)[^"']*)["']/gi;

    document.querySelectorAll('script').forEach((script) => {
      const text = script.textContent || "";
      if (text.length > 0 && text.length < 1000000) {
        let match;
        while ((match = STREAM_REGEX.exec(text)) !== null) {
          addCandidate(match[1], 'Direct stream', 'hls');
        }
        while ((match = FILE_REGEX.exec(text)) !== null) {
          addCandidate(match[1], 'Player source', 'hls');
        }

        // Eval-packed kodları çöz
        if (text.includes("eval(function(p,a,c,k,e,d)")) {
          const unpacked = unpackDeanEdwards(text);
          if (unpacked) {
            while ((match = STREAM_REGEX.exec(unpacked)) !== null) {
              addCandidate(match[1], 'Unpacked stream', 'hls');
            }
            while ((match = FILE_REGEX.exec(unpacked)) !== null) {
              addCandidate(match[1], 'Unpacked player source', 'hls');
            }
          }
        }
      }
    });

    // 4. Tarayıcı Ağ Kaynakları (Performance Timing Resource API)
    try {
      const entries = performance.getEntriesByType('resource');
      if (entries && entries.length > 0) {
        entries.forEach((entry) => {
          const name = entry.name || "";
          if (name.includes('.m3u8') || name.includes('.mpd') || VIDEO_EXTENSIONS.test(name)) {
            addCandidate(name, 'Network stream', 'hls');
          }
        });
      }
    } catch { }

    // 5. Bağlantı Etiketleri (a[href])
    document.querySelectorAll('a[href]').forEach((a) => {
      const href = a.getAttribute('href');
      const detected = classifyUrl(href);
      if (detected) {
        addCandidate(href, a.innerText.trim() || 'Video link', detected);
      }
    });

    return results;
  }

  return scanMedia();
})();
