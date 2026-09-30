/**
 * MPV Launcher - Content Script
 *
 * Detection strategy (in priority order):
 *   1. Challenge pages (reCAPTCHA / hCaptcha / Turnstile / Cloudflare) are
 *      rejected outright so their helper frames are never reported as media.
 *   2. Real player signals: <video> sources, MSE blob resolution, JSON-LD,
 *      player config blobs, inline script URLs, network timings, anchors.
 */

(function () {
  // ---------------------------------------------------------------- constants

  // Extensions that are unambiguously a media file.
  const MEDIA_EXT = /\.(m3u8|mpd|mp4|webm|mkv|avi|mov|flv|m4v|ogv|ts)(?:$|[?#])/i;

  // MPEG-DASH manifests, HLS playlists, DASH and progressive files.
  const STREAM_EXT = /\.(m3u8|mpd)(?:$|[?#])/i;

  // ".ts" is a real transport stream but also a very common non-media suffix
  // (.ts = TypeScript, .ts? as a cache buster). Require it to be the last
  // path segment and not be preceded by a word character.
  const TS_SEGMENT = /\.ts(?:$|[?#])/i;

  const DIRECT_FILE = /\.(mp4|webm|mkv|avi|mov|flv|m4v|ogv|ts)(?:$|[?#])/i;

  // Extension-less HLS endpoints. Very common on CDNs: /hls/master, /playlist,
  // /v1/video/1234/index, ?format=hls
  const HLS_HINT = /(?:[?&#/]|^)(?:hls|m3u8|playlist|manifest|master)(?:[/?&#]|$)/i;
  const HLS_QUERY = /[?&](?:format|type|output|stream|manifest)=(?:hls|m3u8|dash|mpd)\b/i;

  // Known video hosts. Used for iframe classification only.
  const VIDEO_HOSTS = [
    'youtube.com', 'youtu.be', 'vimeo.com', 'dailymotion.com',
    'vidmoly.', 'ok.ru', 'streamtape.com', 'doodstream.', 'dood.',
    'mixdrop.', 'fembed.', 'vidoza.net', 'voe.sx', 'streamwish.',
    'filelions.', 'dropload.', 'mp4upload.', 'abyssa.', 'membed.',
    'mycloud.', 'cloudbuny.', 'filemoon.', 'hydrax.', 'gofile.',
    'mega.nz', 'send.cm', 'pixeldrain.com', 'btnat.com'
  ];

  // Frames/elements that belong to a challenge widget. These are never media.
  const CHALLENGE_HOSTS = [
    'google.com/recaptcha', 'recaptcha.net', 'hcaptcha.com', 'challenges.cloudflare.com',
    'gstatic.com/recaptcha', 'captcha.duckcaptcha.com'
  ];

  const CHALLENGE_MARKERS = [
    'g-recaptcha', 'grecaptcha', 'h-captcha', 'hcaptcha', 'cf-turnstile',
    'challenges.cloudflare.com', 'recaptcha/api.js', 'recaptcha/enterprise'
  ];

  // Never worth reporting: trackers, ads, captchas, CDNs of assets.
  const NOISE_HOSTS = [
    'google-analytics.com', 'googletagmanager.com', 'doubleclick.net',
    'facebook.net', 'adservice.', 'adnxs.com', 'criteo.', 'taboola.',
    'outbrain.com', 'scorecardresearch.com', 'quantserve.com', 'hotjar.com'
  ];

  // ------------------------------------------------------------------ helpers

  function hostOf(url) {
    try {
      return new URL(url).hostname;
    } catch {
      return '';
    }
  }

  function isChallengeUrl(url) {
    if (!url) return false;
    const lower = url.toLowerCase();
    return CHALLENGE_HOSTS.some((h) => lower.includes(h));
  }

  function isNoiseUrl(url) {
    if (!url) return false;
    const lower = url.toLowerCase();
    if (NOISE_HOSTS.some((h) => lower.includes(h))) return true;
    // Static asset noise.
    return /\.(?:js|css|png|jpe?g|gif|svg|webp|ico|woff2?|ttf|eot|map)(?:$|[?#])/i.test(lower);
  }

  /**
   * True only when THIS document is the challenge itself, not when it merely
   * embeds a captcha widget. Most sites load an invisible reCAPTCHA script
   * that the visitor never interacts with, so a mere script tag proves
   * nothing. We require an explicit challenge URL or a blocking notice.
   *
   * Only the top frame is asked: a captcha inside an ad iframe must not
   * mark the whole page as blocked.
   */
  function isChallengePage() {
    if (isChallengeUrl(location.href)) return true;

    var isTop = true;
    try { isTop = (window.top === window); } catch (e) { isTop = false; }
    if (!isTop) return false;

    // A visible challenge notice is the only reliable DOM signal.
    var body = document.body;
    if (!body) return false;

    var text = (body.innerText || '').slice(0, 1200).toLowerCase();
    if (text) {
      if (text.includes("verify you are human") ||
          text.includes("i'm not a robot") ||
          text.includes("i am not a robot") ||
          text.includes("checking your browser") ||
          text.includes("unusual traffic from your computer") ||
          text.includes("enable javascript and cookies to continue")) {
        return true;
      }
    }

    // An interstitial also shows a widget with no media anywhere on the page.
    var hasWidget = CHALLENGE_MARKERS.some(function (m) {
      return !!document.querySelector('[id*="' + m + '"], [class*="' + m + '"]');
    });
    if (hasWidget) {
      var hasMedia = document.querySelector('video, audio, iframe[src*="player"], iframe[src*="embed"]');
      if (!hasMedia) return true;
    }

    return false;
  }

  // Path fragments that appear in embed/player URLs on custom domains.
  // The literal slashes inside the group must be escaped or they terminate
  // the regex literal early.
  const PLAYER_PATH = /\/(?:embed|player|play|video|videos|watch|media|stream|html5|jwplayer|videojs|player\.php|play\.php|watch\.php|e\/|v\/?$)/i;

  // Hosts whose iframes are never video (widgets, comments, payments).
  const NON_PLAYER_HOSTS = [
    'facebook.com', 'twitter.com', 'x.com', 'instagram.com', 'tiktok.com',
    'pinterest.', 'reddit.com', 'linkedin.com', 'whatsapp.com', 'telegram.',
    'paypal.', 'stripe.', 'google.com/maps', 'doubleclick.', 'adnxs.',
    'disqus.com', 'livechat', 'zendesk', 'intercom', 'crisp.chat',
    'sharethis', 'addthis', 'hotjar', 'cookiebot', 'onetrust', 'cookielaw',
    'cloudflareinsights', 'gstatic.com/recaptcha', 'accounts.google'
  ];

  /**
   * True when an iframe is worth offering as a playable source.
   *
   * Deliberately permissive: most embed players run on a site-specific domain
   * that no host list can enumerate, so a size threshold plus URL hints are
   * used instead of an allowlist. The popup exposes a filter chip so the
   * user can hide these again.
   */
  function isPlayerPage(url, element) {
    if (!url) return false;
    const lower = url.toLowerCase();
    if (isChallengeUrl(lower) || isNoiseUrl(lower)) return false;

    // about:blank and same-document frames carry nothing.
    if (/^about:|^javascript:|^data:|^blob:/i.test(lower)) return false;

    for (const h of NON_PLAYER_HOSTS) {
      if (lower.includes(h)) return false;
    }

    // Direct media inside the frame URL is the strongest signal.
    if (MEDIA_EXT.test(lower) || HLS_HINT.test(lower) || HLS_QUERY.test(lower)) return true;

    const host = hostOf(lower);
    if (host && VIDEO_HOSTS.some((h) => host.includes(h.replace(/\.$/, '')))) return true;

    // Known player path shapes.
    let path = '';
    try { path = new URL(lower).pathname; } catch { path = ''; }
    if (PLAYER_PATH.test(path)) return true;
    if (/[?&](?:embed|player|autoplay)=1/i.test(lower)) return true;

    // Otherwise fall back to geometry: real players are large, comment boxes
    // and trackers are not.
    if (element && looksLarge(element)) return true;

    return false;
  }

  /** A visible, reasonably sized box - how players present themselves. */
  function looksLarge(element) {
    try {
      const w = element.offsetWidth || 0;
      const h = element.offsetHeight || 0;
      if (!w || !h) {
        // offsetWidth is 0 on some layouts; fall back to attributes
        const aw = parseInt(element.getAttribute('width') || '0', 10) || 0;
        const ah = parseInt(element.getAttribute('height') || '0', 10) || 0;
        if (!aw || !ah) return false;
        return aw >= 300 && ah >= 180;
      }
      if (h < 120 || w < 200) return false;
      // ignore 1x1 tracking pixels and thin bars
      return !(w <= 2 || h <= 2);
    } catch {
      return false;
    }
  }

  function looksLikeStream(url) {
    if (!url) return false;
    if (STREAM_EXT.test(url)) return true;
    if (DIRECT_FILE.test(url)) return true;
    if (TS_SEGMENT.test(url) && !/\.ts\b(?=.*\.js)/i.test(url)) return true;
    if (HLS_QUERY.test(url)) return true;
    if (HLS_HINT.test(url) && /\/(?:hls|manifest|playlist|master)(?:[/?&#]|$)/i.test(url)) return true;
    return false;
  }

  function classifyUrl(url) {
    if (!url) return null;
    if (isChallengeUrl(url) || isNoiseUrl(url)) return null;

    const lower = url.toLowerCase();
    if (lower.includes('youtube.com') || lower.includes('youtu.be')) return 'youtube';
    if (/\.mpd(?:$|[?#])/i.test(lower)) return 'dash';
    if (STREAM_EXT.test(lower)) return 'hls';
    if (TS_SEGMENT.test(lower) || DIRECT_FILE.test(lower)) return 'video';

    const host = hostOf(lower);
    if (host && VIDEO_HOSTS.some((h) => host.includes(h.replace(/\.$/, '')))) return 'iframe';

    if (HLS_QUERY.test(lower)) return 'hls';
    if (/\/(?:hls|manifest|playlist|master)(?:[/?&#]|$)/i.test(lower)) return 'hls';
    return null;
  }

  /** Undo JSON/JS string escaping: backslash-slash and \uXXXX forms. */
  function decodeEscapes(raw) {
    return String(raw)
      .replace(/\\u002[fF]/g, '/')
      .replace(/\\u003[aA]/g, ':')
      .replace(/\\u0026/g, '&')
      .replace(/\\\//g, '/')
      .replace(/\\"/g, '')
      .replace(/\\'/g, '');
  }

  /**
   * Absolute-ise a possibly relative / escaped URL against the document base.
   * Handles protocol-relative ("//host/x.m3u8") and JSON-escaped forms,
   * both of which naive string matching misses.
   */
  function absolutize(raw) {
    if (!raw) return null;

    const clean = decodeEscapes(raw).trim();
    if (!clean || /^(?:blob|data|javascript|about|mailto|tel):/i.test(clean)) return null;

    try {
      const base = document.baseURI || location.href;
      const candidate = /^https?:\/\//i.test(clean)
        ? clean
        : clean.replace(/^\/\//, location.protocol + '//');
      const abs = new URL(candidate, base);
      return /^https?:$/.test(abs.protocol) ? abs.href : null;
    } catch {
      return null;
    }
  }

  function sanitizeUrl(raw) {
    if (!raw || typeof raw !== 'string') return null;
    return absolutize(raw);
  }

  function getHostLabel(url) {
    const host = hostOf(url);
    if (!host) return 'Media source';
    return host.replace(/^www\./, '');
  }

  // -------------------------------------------------------------- blob to URL

  /**
   * hls.js / dash.js hand MSE a blob: URL for the media element. The blob
   * itself is not playable, so walk back to the manifest that produced it.
   */
  function resolveBlobUrl(blobUrl) {
    if (!blobUrl || blobUrl.indexOf('blob:') !== 0) return null;

    // The probe runs in the page world, so its data arrives via <html> dataset.
    const el = document.documentElement;
    if (!el) return null;

    const map = el.dataset ? el.dataset.mpvBlobMap : null;
    if (map) {
      try {
        const parsed = JSON.parse(map);
        if (parsed[blobUrl]) return parsed[blobUrl];
      } catch { /* malformed probe data */ }
    }

    // Fallback: any playlist the page fetched is a better answer than the blob.
    const streams = el.dataset ? el.dataset.mpvStreams : '';
    if (streams) {
      const first = streams.split('\n').find((u) => STREAM_EXT.test(u));
      if (first) return first;
    }
    return null;
  }

  // Attributes lazy players use before the real src is applied.
  const LAZY_ATTRS = [
    'data-src', 'data-url', 'data-file', 'data-hls', 'data-media',
    'data-video-src', 'data-stream', 'data-movie', 'data-video'
  ];

  const mpegurl = /mpegurl|m3u8/;

  /**
   * Maps a <source type="..."> MIME hint onto our internal type, so a signed
   * or extension-less CDN URL is still classified as HLS rather than dropped.
   */
  function typeHint(mime) {
    if (!mime) return null;
    const m = String(mime).toLowerCase();
    if (mpegurl.test(m)) return 'hls';
    if (m.includes('dash+xml')) return 'dash';
    if (m.startsWith('video/')) return 'video';
    if (m.startsWith('audio/')) return 'audio';
    return null;
  }

  // ------------------------------------------------------------------ scanner

  function scanMedia() {
    // The scan always runs. A captcha merely adds a warning; it must not
    // suppress media that the page does expose, or a page that embeds a
    // captcha widget (most sites do) would report nothing at all.
    const challenged = isChallengePage();

    const results = [];
    const seen = new Set();

    function addCandidate(rawUrl, fallbackLabel, fallbackType) {
      const url = sanitizeUrl(rawUrl);
      if (!url || seen.has(url)) return false;
      if (isChallengeUrl(url) || isNoiseUrl(url)) return false;

      const detectedType = classifyUrl(url) || fallbackType;
      if (!detectedType) return false;

      seen.add(url);
      const hostName = getHostLabel(url);
      results.push({
        url: url,
        type: detectedType,
        label: fallbackLabel ? `${fallbackLabel} (${hostName})` : hostName
      });
      return true;
    }

    // 1. HTML5 video elements. MSE blobs are resolved back to their manifest.
    document.querySelectorAll('video, audio').forEach((media, idx) => {
      const tag = media.tagName.toLowerCase() === 'audio' ? 'Audio' : 'Video';

      function pushVideo(src, suffix, hintType) {
        if (!src) return;
        if (src.startsWith('blob:')) {
          const resolved = resolveBlobUrl(src);
          if (resolved) addCandidate(resolved, `${tag} ${suffix} (MSE)`.trim(), 'hls');
          else addCandidate(location.href, `${tag} ${suffix} (MSE)`.trim(), 'video');
          return;
        }
        addCandidate(src, `${tag} #${idx + 1}${suffix}`, hintType || 'video');
      }

      pushVideo(media.src, '');
      if (media.currentSrc && media.currentSrc !== media.src) pushVideo(media.currentSrc, ' active');

      // <source> children. The type attribute is used as a hint so a URL with
      // no usable extension (signed CDN paths) is still classified correctly.
      media.querySelectorAll('source').forEach((srcEl, sIdx) => {
        const rawSrc = srcEl.src || srcEl.getAttribute('src');
        const hint = typeHint(srcEl.getAttribute('type') || srcEl.getAttribute('data-type'));
        if (rawSrc) {
          pushVideo(rawSrc, ` source #${sIdx + 1}`, hint);
        }
      });

      // Player-specific attributes used by common embed libraries. The tag
      // carries a "data-vds" marker but the URL always lives in a src-ish
      // attribute, so every plausible one is read.
      LAZY_ATTRS.forEach((attr) => {
        const raw = media.getAttribute(attr);
        if (!raw) return;
        const abs = absolutize(raw);
        if (abs) addCandidate(abs, `${tag} #${idx + 1} (${attr})`, 'video');
      });

      // An empty <video> whose real URL only appears on a wrapper element.
      if (!media.src && !media.querySelectorAll('source').length) {
        let holder = null;
        try {
          holder = media.closest ? media.closest('[data-src],[data-url],[data-media],[data-file]') : null;
        } catch { holder = null; }
        // closest() is unavailable in some contexts; walk up manually.
        if (!holder && media.parentElement) {
          let node = media.parentElement;
          for (let hops = 0; node && hops < 4; hops++, node = node.parentElement) {
            if (node.getAttribute && LAZY_ATTRS.some((a) => node.getAttribute(a))) { holder = node; break; }
          }
        }
        if (holder && holder.getAttribute) {
          for (const attr of LAZY_ATTRS) {
            const abs = absolutize(holder.getAttribute(attr));
            if (abs) { addCandidate(abs, `${tag} #${idx + 1} (container)`, 'video'); break; }
          }
        }
      }
    });

    // 1b. Standalone <source> elements not inside a media element.
    document.querySelectorAll('source').forEach((srcEl) => {
      const parentTag = srcEl.parentElement && (srcEl.parentElement.tagName || '');
      if (/^(VIDEO|AUDIO)$/.test(parentTag)) return;
      const raw = srcEl.src || srcEl.getAttribute('src');
      if (!raw) return;
      const hint = typeHint(srcEl.getAttribute('type') || srcEl.getAttribute('data-type'));
      addCandidate(raw, 'Source element', hint || 'video');
    });

    // 1c. Player containers that carry the URL only in data attributes. The
    //     reported markup uses data-media-provider, so the wrapper itself and
    //     everything inside it are scanned.
    var containerSelector = '[data-media-provider], [data-media], [data-player], [data-vds]';
    document.querySelectorAll(containerSelector).forEach(function (container) {
      var targets = [container];
      try {
        var inner = container.querySelectorAll('*');
        for (var i = 0; i < inner.length && i < 40; i++) targets.push(inner[i]);
      } catch { /* ignore */ }

      targets.forEach(function (el) {
        if (!el || !el.getAttribute) return;
        if (/^(SCRIPT|STYLE|LINK|META)$/.test(el.tagName || '')) return;
        for (var a = 0; a < LAZY_ATTRS.length; a++) {
          var abs = absolutize(el.getAttribute(LAZY_ATTRS[a]));
          if (abs) { addCandidate(abs, 'Player data', 'video'); break; }
        }
      });
    });

    // 2. JSON-LD VideoObject — the declared canonical stream for the page.
    document.querySelectorAll('script[type="application/ld+json"]').forEach((script) => {
      const text = (script.textContent || '').trim();
      if (!text || text.length > 200000) return;
      let data;
      try {
        data = JSON.parse(text);
      } catch {
        return;
      }
      const nodes = Array.isArray(data) ? data : (data['@graph'] || [data]);
      nodes.forEach((node) => {
        if (!node || typeof node !== 'object') return;
        if (!/video/i.test(String(node['@type'] || ''))) return;
        const target = node.contentUrl || node.embedUrl ||
          (Array.isArray(node.associatedMedia) && node.associatedMedia[0] &&
            (node.associatedMedia[0].contentUrl || node.associatedMedia[0].embedUrl));
        if (target) addCandidate(target, 'Structured data', 'hls');
      });
    });

    // 3. Iframes — reported when they look like a player, which now also
    //    accepts large frames on unknown domains.
    document.querySelectorAll('iframe').forEach((iframe, idx) => {
      const raw = iframe.getAttribute('src') ||
                  iframe.getAttribute('data-src') ||
                  iframe.getAttribute('data-lazy-src') ||
                  iframe.getAttribute('data-url') ||
                  iframe.getAttribute('data-media-src') ||
                  iframe.src;

      if (raw && isPlayerPage(absolutize(raw) || raw, iframe)) {
        addCandidate(raw, `Embedded player #${idx + 1}`, 'iframe');
      }

      // Same-origin frames: pull their media out directly.
      try {
        const doc = iframe.contentDocument || (iframe.contentWindow && iframe.contentWindow.document);
        if (doc) {
          doc.querySelectorAll('video, audio').forEach((media, vIdx) => {
            if (media.src && !media.src.startsWith('blob:')) {
              addCandidate(media.src, `Frame video #${idx + 1}.${vIdx + 1}`, 'video');
            }
            media.querySelectorAll('source').forEach((srcEl, sIdx) => {
              if (srcEl.src) addCandidate(srcEl.src, `Frame source #${idx + 1}.${vIdx + 1}.${sIdx + 1}`, 'video');
            });
          });
          doc.querySelectorAll('iframe').forEach((inner, fIdx) => {
            const innerSrc = inner.getAttribute('src') ||
                             inner.getAttribute('data-src') ||
                             inner.getAttribute('data-lazy-src') ||
                             inner.src;
            if (innerSrc && isPlayerPage(absolutize(innerSrc) || innerSrc, inner)) {
              addCandidate(innerSrc, `Nested player #${idx + 1}.${fIdx + 1}`, 'iframe');
            }
          });
        }
      } catch {
        // Cross-origin: the popup's allFrames injection covers it instead.
      }
    });

    // 4. object/embed players
    document.querySelectorAll('object, embed').forEach((el, idx) => {
      const data = el.getAttribute('data') || el.getAttribute('src');
      const abs = absolutize(data);
      if (abs && (looksLikeStream(abs) || isPlayerPage(abs))) {
        addCandidate(abs, `Object/embed #${idx + 1}`, 'video');
      }
    });

    // 5. Inline scripts: stream URLs, player configs, packed code.
    const STREAM_REGEX = /((?:https?:)?\\?(?:\/|u002[fF])\\?(?:\/|u002[fF])[^\s"'<>]+?\.(?:m3u8|mpd|mp4|webm|mkv|avi|mov|flv|m4v)(?:[?#][^\s"'<>]*)?)/gi;
    const CONFIG_REGEX = /["'](?:sources?|file|src|url|uri|manifest|playlist|stream|hls|dashUrl|contentUrl|embedUrl|playbackUrl|mediaUrl)["']\s*:\s*["']([^"']{4,600}?)["']/gi;
    const BARE_HLS_REGEX = /["']([^"'\s]{0,200}?(?:\/hls\/|\/master\.m3u8|\/playlist(?:\.\w+)?|\/manifest\.mpd|\?format=hls)[^"'\s]{0,200})["']/gi;

    /**
     * Decodes a JavaScript string literal without ever evaluating it.
     *
     * The only supported escapes are the ones a JS engine would apply; anything
     * else is passed through verbatim. This exists so the unpacker below never
     * needs eval()/new Function().
     */
    function unescapeJsLiteral(literal) {
      const body = literal.slice(1, -1);
      return body.replace(/\\(u\{[0-9a-fA-F]+\}|u[0-9a-fA-F]{4}|x[0-9a-fA-F]{2}|\r\n|[\s\S])/g, (_, esc) => {
        if (esc === "\r\n") return "";
        if (esc[0] === "x") return String.fromCharCode(parseInt(esc.slice(1), 16));
        if (esc[0] === "u") {
          if (esc[1] === "{") return String.fromCodePoint(parseInt(esc.slice(2, -1), 16));
          return String.fromCharCode(parseInt(esc.slice(1), 16));
        }
        const simple = { n: "\n", t: "\t", r: "\r", b: "\b", f: "\f", v: "\v", 0: "\0" };
        return Object.prototype.hasOwnProperty.call(simple, esc) ? simple[esc] : esc;
      });
    }

    /**
     * Dean Edwards packer reader - string substitution only, no execution.
     *
     * The packed form is
     *   eval(function(p,a,c,k,e,d){...}(radix, count, "dictionary"))
     * and the dictionary holds every original string literal, including any
     * embedded media URL. An earlier version rebuilt the original source by
     * handing it to new Function(), which executed page-controlled code inside
     * the extension's isolated world and therefore handed the page the whole
     * extension API (including the native-messaging bridge). Rebuilding the
     * token substitution by hand gives the same recovered text with no way for
     * a hostile page to run code here.
     */
    function unpackDeanEdwards(scriptText) {
      const start = scriptText.indexOf("function(p,a,c,k,e");
      if (start === -1) return "";

      const args = readInvocationArgs(scriptText, start);
      if (!args || args.length < 3) return "";

      // Packer versions disagree on argument order - the older form ends with
      // (radix, count, dictionary) and the newer one with (radix, dictionary,
      // count, ...) - so the dictionary is located by shape, not by position.
      let dictionary = null, count = NaN;
      for (const arg of args) {
        const parsed = readDictionary(arg);
        if (parsed) { dictionary = parsed; break; }
      }
      if (!dictionary || !dictionary.raw || dictionary.raw.length < 4) return "";
      for (const arg of args) {
        if (/^\s*\d+\s*$/.test(arg)) { count = parseInt(arg, 10); break; }
      }

      // The dictionary alone already exposes whole URLs. Splitting it into its
      // declared entries additionally recovers the "key":"url" pairs the packer
      // reassembled at runtime; when no delimiter yields the declared entry
      // count the raw blob is still returned.
      const out = [dictionary.raw];
      const split = pickDictionarySplit(dictionary, count);
      if (split) {
        out.push(split.entries.join("\n"));
        out.push(substituteTokens(scriptText.slice(start), split.entries));
      }
      return out.join("\n");
    }

    const STRING_LITERAL = /"(?:[^"\\]|\\[\s\S])*"|'(?:[^'\\]|\\[\s\S])*'/g;

    /**
     * Returns the argument list of the packer's trailing `(...)` call, keeping
     * quoted strings, nested brackets and inner calls intact.
     */
    function readInvocationArgs(scriptText, fromIndex) {
      // The call follows the function body, so the closing brace that opens it
      // is the last one followed - possibly across a line break - by "(".
      let open = -1;
      for (let i = fromIndex; i < scriptText.length; i++) {
        if (scriptText[i] !== "}") continue;
        let j = i + 1;
        while (j < scriptText.length && /\s/.test(scriptText[j])) j++;
        if (scriptText[j] === "(") open = j;
      }
      if (open === -1) return null;

      let depth = 0, quote = "", escaped = false;
      for (let i = open; i < scriptText.length; i++) {
        const ch = scriptText[i];
        if (quote) {
          if (escaped) escaped = false;
          else if (ch === "\\") escaped = true;
          else if (ch === quote) quote = "";
          continue;
        }
        if (ch === '"' || ch === "'" || ch === "`") { quote = ch; continue; }
        if (ch === "(") depth++;
        else if (ch === ")") {
          depth--;
          if (depth === 0) return splitTopLevelArgs(scriptText.slice(open + 1, i));
        }
      }
      return null;
    }

    /** Splits an argument list on commas that are not nested or quoted. */
    function splitTopLevelArgs(text) {
      const args = [];
      let depth = 0, quote = "", escaped = false, current = "";
      for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (quote) {
          current += ch;
          if (escaped) escaped = false;
          else if (ch === "\\") escaped = true;
          else if (ch === quote) quote = "";
          continue;
        }
        if (ch === '"' || ch === "'" || ch === "`") { quote = ch; current += ch; continue; }
        if (ch === "(" || ch === "[" || ch === "{") depth++;
        else if (ch === ")" || ch === "]" || ch === "}") depth--;
        if (ch === "," && depth === 0) { args.push(current); current = ""; continue; }
        current += ch;
      }
      if (current.trim() !== "") args.push(current);
      return args;
    }

    /**
     * Reads the dictionary argument in whichever form the packer emitted it:
     * a bare literal, a literal followed by .split('delim'), or an array.
     */
    function readDictionary(arg) {
      const src = (arg || "").trim();
      if (!src) return null;

      if (src[0] === "[") {
        const items = src.match(STRING_LITERAL) || [];
        return {
          raw: items.map(unescapeJsLiteral).join("\n"),
          delimiter: null
        };
      }

      const literal = STRING_LITERAL.exec(src);
      STRING_LITERAL.lastIndex = 0;
      if (!literal) return null;

      let raw;
      try {
        raw = unescapeJsLiteral(literal[0]);
      } catch (e) {
        return null;
      }

      const split = /^\.split\(\s*("(?:[^"\\]|\\[\s\S])*"|'(?:[^'\\]|\\[\s\S])')\s*\)/.exec(src.slice(literal[0].length).trim());
      return { raw, delimiter: split ? unescapeJsLiteral(split[1]) : null };
    }

    /**
     * Packer versions differ in the delimiter that joins the dictionary, so the
     * declared one is used when known and otherwise the candidate that yields
     * exactly `count` entries. Returns null when neither works, which means the
     * caller keeps the raw dictionary.
     */
    function pickDictionarySplit(dictionary, count) {
      if (!count || count < 1) return null;
      const candidates = [dictionary.delimiter, ",", "-", "|", "~", "\u0000", "/"];
      for (const delim of candidates) {
        if (!delim) continue;
        const parts = dictionary.raw.split(delim);
        if (parts.length === count) return { delim, entries: parts };
      }
      return null;
    }

    /**
     * Replaces base-36 identifier tokens with their dictionary entry, which is
     * the same substitution the packer performs at runtime - as text, not code.
     */
    function substituteTokens(body, entries) {
      return body.replace(/(^|[^\w$.])([0-9a-z]+)(?![\w$])/g, (all, lead, word) => {
        const index = parseInt(word, 36);
        if (!isFinite(index) || index < 0 || index >= entries.length) return all;
        // Only the canonical spelling counts, otherwise ordinary identifiers
        // that happen to be valid base-36 digits ("alpha", "video") would be
        // rewritten and the recovered text would fill with noise.
        if (index.toString(36) !== word.toLowerCase()) return all;
        return lead + entries[index];
      });
    }

    function harvest(text, labels) {
      let match;
      STREAM_REGEX.lastIndex = 0;
      while ((match = STREAM_REGEX.exec(text)) !== null) {
        addCandidate(match[1], labels[0], 'hls');
        if (match[0].length === 0) STREAM_REGEX.lastIndex++;
      }
      CONFIG_REGEX.lastIndex = 0;
      while ((match = CONFIG_REGEX.exec(text)) !== null) {
        const abs = absolutize(match[1]);
        if (abs && looksLikeStream(abs)) addCandidate(abs, labels[1], 'hls');
        if (match[0].length === 0) CONFIG_REGEX.lastIndex++;
      }
      BARE_HLS_REGEX.lastIndex = 0;
      while ((match = BARE_HLS_REGEX.exec(text)) !== null) {
        const abs = absolutize(match[1]);
        if (abs && looksLikeStream(abs)) addCandidate(abs, labels[1], 'hls');
        if (match[0].length === 0) BARE_HLS_REGEX.lastIndex++;
      }
    }

    document.querySelectorAll('script').forEach((script) => {
      const text = script.textContent || "";
      if (!text || text.length > 2000000) return;
      harvest(text, ['Direct stream', 'Player source']);

      if (text.includes("eval(function(p,a,c,k,e,d")) {
        const unpacked = unpackDeanEdwards(text);
        if (unpacked) harvest(unpacked, ['Unpacked stream', 'Unpacked source']);
      }
    });

    // 6. Network probe: manifests the player fetched after page load.
    //    Covers the common case where hls.js runs in a worker or the playlist
    //    is requested long after the DOM settled.
    try {
      const el = document.documentElement;
      const captured = el && el.dataset ? el.dataset.mpvStreams : '';
      if (captured) {
        captured.split('\n').forEach((url) => {
          if (!url) return;
          if (STREAM_EXT.test(url) || DIRECT_FILE.test(url)) addCandidate(url, 'Captured stream', 'hls');
        });
      }
    } catch { }

    // 7. Performance timings — catches manifests fetched before the probe.
    try {
      const entries = performance.getEntriesByType('resource');
      if (entries && entries.length) {
        entries.forEach((entry) => {
          const name = entry.name || "";
          if (STREAM_EXT.test(name) || DIRECT_FILE.test(name)) {
            addCandidate(name, 'Network stream', 'hls');
          } else if (TS_SEGMENT.test(name) && !isNoiseUrl(name)) {
            addCandidate(name, 'Transport segment', 'video');
          }
        });
      }
    } catch { }

    // 8. Anchors that look like media.
    document.querySelectorAll('a[href]').forEach((a) => {
      const abs = absolutize(a.getAttribute('href'));
      if (!abs) return;
      if (classifyUrl(abs)) {
        addCandidate(abs, (a.innerText || '').trim() || 'Video link', classifyUrl(abs));
      }
    });

    return { challenged: challenged, media: results };
  }

  return scanMedia();
})();
