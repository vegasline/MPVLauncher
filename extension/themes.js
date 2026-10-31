// Original implementation, MIT licensed.

/**
 * Popup colour themes.
 *
 * The popup already draws everything from a handful of CSS custom properties,
 * so a theme is just a set of values for those - no new stylesheets, no copied
 * rules, and switching costs one style recalculation. The toolbar icon follows
 * the accent colour: the source PNG is tinted once per colour in a small
 * offscreen canvas and handed to action.setIcon, which is the only way an
 * extension may change its own icon without shipping one image per palette.
 *
 * Everything is lazy. Nothing runs on popup open beyond reading the saved id,
 * and a palette's icon is rendered the first time that palette is used, then
 * kept in a Map - so clicking through themes repeatedly costs no redraws.
 */
var MPV_Themes = (function () {
  "use strict";

  var STORAGE_KEY = "uiTheme";
  // A generated palette is not in THEMES, so its values travel with the id.
  var RANDOM_KEY = "uiThemeRandom";
  var RANDOM_ID = "random";

  /**
   * Eight palettes on the same dark base as the launcher's themes, so the popup
   * and the app do not look like two different products. `bg` values stay dark
   * in every one of them: the status colours and tag borders are tuned for dark
   * backgrounds, and a light palette would need a second set of them.
   */
  var THEMES = [
    {
      id: "midnight",
      name: "Midnight",
      accent: "#6366f1",
      accentHover: "#4f46e5",
      accentEnd: "#8b5cf6",
      bgPrimary: "#12141a",
      bgSecondary: "#1a1e27",
      bgCard: "#212634",
      bgCardHover: "#293042",
      border: "#2e3648"
    },
    {
      id: "halloween",
      name: "Halloween",
      accent: "#f97316",
      accentHover: "#ea580c",
      accentEnd: "#a855f7",
      bgPrimary: "#150f1a",
      bgSecondary: "#1d1327",
      bgCard: "#271834",
      bgCardHover: "#332042",
      border: "#432a55"
    },
    {
      id: "newyear",
      name: "New Year",
      accent: "#eab308",
      accentHover: "#ca8a04",
      accentEnd: "#0ea5e9",
      bgPrimary: "#0c1218",
      bgSecondary: "#121b23",
      bgCard: "#18242e",
      bgCardHover: "#1f2e3a",
      border: "#274050"
    },
    {
      id: "neon",
      name: "Neon Blossom",
      accent: "#ec4899",
      accentHover: "#db2777",
      accentEnd: "#22d3ee",
      bgPrimary: "#140f1a",
      bgSecondary: "#1c1424",
      bgCard: "#261a30",
      bgCardHover: "#32213e",
      border: "#452c55"
    },
    {
      id: "cyberpunk",
      name: "Cyberpunk",
      accent: "#facc15",
      accentHover: "#eab308",
      accentEnd: "#06b6d4",
      bgPrimary: "#0b0f19",
      bgSecondary: "#111726",
      bgCard: "#172033",
      bgCardHover: "#1e2a42",
      border: "#27395a"
    },
    {
      id: "forest",
      name: "Forest",
      accent: "#22c55e",
      accentHover: "#16a34a",
      accentEnd: "#14b8a6",
      bgPrimary: "#0d1512",
      bgSecondary: "#12201b",
      bgCard: "#182b24",
      bgCardHover: "#1f372d",
      border: "#2a4a3c"
    },
    {
      id: "sunset",
      name: "Sunset",
      accent: "#fb7185",
      accentHover: "#f43f5e",
      accentEnd: "#fbbf24",
      bgPrimary: "#171013",
      bgSecondary: "#21151a",
      bgCard: "#2c1c22",
      bgCardHover: "#39242c",
      border: "#4f323c"
    },
    {
      id: "ocean",
      name: "Ocean",
      accent: "#0ea5e9",
      accentHover: "#0284c7",
      accentEnd: "#6366f1",
      bgPrimary: "#0b1418",
      bgSecondary: "#101e24",
      bgCard: "#152831",
      bgCardHover: "#1b3440",
      border: "#254653"
    }
  ];

  var byId = {};
  THEMES.forEach(function (theme) { byId[theme.id] = theme; });

  var currentId = "midnight";
  var iconCache = new Map();
  var canvas = null;

  /** hsl (degrees, percent, percent) to #rrggbb, so a generated palette matches the hand-written ones. */
  function hslToHex(h, s, l) {
    h = ((h % 360) + 360) % 360;
    s = s / 100;
    l = l / 100;

    var c = (1 - Math.abs(2 * l - 1)) * s;
    var x = c * (1 - Math.abs(((h / 60) % 2) - 1));
    var m = l - c / 2;

    var r = 0;
    var g = 0;
    var b = 0;
    if (h < 60) { r = c; g = x; }
    else if (h < 120) { r = x; g = c; }
    else if (h < 180) { g = c; b = x; }
    else if (h < 240) { g = x; b = c; }
    else if (h < 300) { r = x; b = c; }
    else { r = c; b = x; }

    function channel(value) {
      var n = Math.round((value + m) * 255);
      n = Math.max(0, Math.min(255, n));
      return ("0" + n.toString(16)).slice(-2);
    }

    return "#" + channel(r) + channel(g) + channel(b);
  }

  /**
   * A palette that is not one of the presets: one random hue family, with a
   * second hue for the gradient end. The background stays dark at every hue,
   * because the tag and status colours are tuned for dark.
   */
  function generatedTheme() {
    var hue = Math.floor(Math.random() * 360);
    var partner = (hue + 40 + Math.floor(Math.random() * 40)) % 360;

    return {
      id: RANDOM_ID,
      name: "Random",
      accent: hslToHex(hue, 72, 60),
      accentHover: hslToHex(hue, 74, 50),
      accentEnd: hslToHex(partner, 68, 62),
      bgPrimary: hslToHex(hue, 26, 7),
      bgSecondary: hslToHex(hue, 24, 10),
      bgCard: hslToHex(hue, 22, 14),
      bgCardHover: hslToHex(hue, 22, 18),
      border: hslToHex(hue, 20, 24)
    };
  }

  function api() {
    return typeof browser !== "undefined" ? browser : chrome;
  }

  function actionApi() {
    var a = api();
    if (!a) return null;
    return a.action || a.browserAction || null;
  }

  /** Writes a palette into the document, so every rule that uses the variables follows. */
  function paint(theme) {
    var root = document.documentElement;
    if (!root || !root.style) return;

    root.style.setProperty("--accent-color", theme.accent);
    root.style.setProperty("--accent-hover", theme.accentHover);
    root.style.setProperty("--accent-gradient",
      "linear-gradient(135deg, " + theme.accent + " 0%, " + theme.accentEnd + " 100%)");
    root.style.setProperty("--bg-primary", theme.bgPrimary);
    root.style.setProperty("--bg-secondary", theme.bgSecondary);
    root.style.setProperty("--bg-card", theme.bgCard);
    root.style.setProperty("--bg-card-hover", theme.bgCardHover);
    root.style.setProperty("--border-color", theme.border);
    root.style.setProperty("--theme-accent", theme.accent);
  }

  /**
   * Tints icon32.png with the theme's accent. Multiply keeps the mark's shading,
   * destination-in restores the original transparency; both are single GPU
   * operations on a 32x32 canvas.
   *
   * Works in both contexts this file runs in: the popup has a document, and the
   * background page or service worker does not - so the canvas is created with
   * OffscreenCanvas where one exists, and the PNG is fetched rather than loaded
   * through an <img> element.
   */
  function tintIcon(theme) {
    var cached = iconCache.get(theme.accent);
    if (cached) return Promise.resolve(cached);

    return loadIconSource().then(function (source) {
      if (!source) return null;

      var target = createCanvas();
      if (!target) return null;

      var ctx = target.getContext("2d", { willReadFrequently: true });
      if (!ctx) return null;

      ctx.clearRect(0, 0, 32, 32);
      ctx.drawImage(source, 0, 0, 32, 32);

      // "color" takes hue and saturation from the fill and keeps the
      // mark's own shading, so the icon lands on the accent exactly instead
      // of the muddier multiply blend. A browser without that operator
      // reports it unchanged, and multiply at least stays in the family.
      ctx.globalCompositeOperation = "color";
      if (ctx.globalCompositeOperation !== "color")
        ctx.globalCompositeOperation = "multiply";
      ctx.fillStyle = theme.accent;
      ctx.fillRect(0, 0, 32, 32);

      ctx.globalCompositeOperation = "destination-in";
      ctx.drawImage(source, 0, 0, 32, 32);
      ctx.globalCompositeOperation = "source-over";

      var data = ctx.getImageData(0, 0, 32, 32);
      iconCache.set(theme.accent, data);
      return data;
    }).catch(function () { return null; });
  }

  /** A 32x32 canvas, whichever kind this context can make. */
  function createCanvas() {
    if (typeof OffscreenCanvas === "function") return new OffscreenCanvas(32, 32);
    if (typeof document === "undefined") return null;
    if (!canvas) canvas = document.createElement("canvas");
    canvas.width = 32;
    canvas.height = 32;
    return canvas;
  }

  /** icon32.png as something drawImage accepts: an ImageBitmap, or an <img>. */
  function loadIconSource() {
    if (typeof fetch === "function" && typeof createImageBitmap === "function") {
      return fetch("icon32.png").then(function (response) {
        return response.blob();
      }).then(function (blob) {
        return createImageBitmap(blob);
      }).catch(function () { return null; });
    }

    if (typeof document === "undefined" || typeof Image === "undefined")
      return Promise.resolve(null);

    return new Promise(function (resolve) {
      var image = new Image();
      image.onload = function () { resolve(image); };
      image.onerror = function () { resolve(null); };
      image.src = "icon32.png";
    });
  }

  function pushIcon(theme) {
    var action = actionApi();
    if (!action || typeof action.setIcon !== "function") return;

    tintIcon(theme).then(function (data) {
      if (!data) return;
      try {
        var result = action.setIcon({ imageData: data });
        if (result && typeof result.catch === "function") result.catch(function () { });
      } catch (e) {
        // A browser that refuses ImageData keeps the packaged icon; the popup
        // colours still apply, so the theme is not lost.
      }
    });
  }

  function save(theme) {
    var a = api();
    if (!a || !a.storage || !a.storage.local) return;
    try {
      // A preset is just its id; a generated one also carries its colours,
      // otherwise reopening the popup could only fall back to a preset.
      if (theme.id === RANDOM_ID) a.storage.local.set({ uiTheme: theme.id, uiThemeRandom: theme });
      else a.storage.local.set({ uiTheme: theme.id });
    } catch (e) {
      // Not being able to remember the choice is not a reason to fail.
    }
  }

  function applyTheme(theme, options) {
    currentId = theme.id;
    paint(theme);
    pushIcon(theme);
    if (!options || options.remember !== false) save(theme);
    return theme.id;
  }

  /**
   * The palette the user last chose, or null when storage cannot be read.
   *
   * Shared by the popup's restore and the background's icon repaint, so both
   * agree on what "the saved theme" means - including a generated palette,
   * whose colours travel with its id.
   */
  function savedTheme() {
    var a = api();
    if (!a || !a.storage || !a.storage.local) return Promise.resolve(null);

    try {
      return Promise.resolve(a.storage.local.get([STORAGE_KEY, RANDOM_KEY])).then(function (stored) {
        var id = stored && stored[STORAGE_KEY];

        if (id === RANDOM_ID) {
          var custom = stored && stored[RANDOM_KEY];
          if (custom && custom.accent) return custom;
        }

        return byId[id] || byId.midnight;
      }).catch(function () { return null; });
    } catch (e) {
      return Promise.resolve(null);
    }
  }

  return {
    list: THEMES,

    current: function () { return currentId; },

    /** Applies a palette by id, paints it, tints the icon and remembers it. */
    apply: function (id, options) {
      return applyTheme(byId[id] || byId.midnight, options);
    },

    /** A palette built from random hues, so the button is never a shortcut to a preset. */
    random: function () {
      return applyTheme(generatedTheme(), { remember: true });
    },

    /**
     * Applies the saved palette. Called once when the popup opens; before the
     * storage read resolves the document shows the default, which is the same
     * palette most people keep.
     */
    restore: function () {
      var self = this;

      return savedTheme().then(function (theme) {
        return theme ? applyTheme(theme, { remember: false })
                     : self.apply("midnight", { remember: false });
      }).catch(function () {
        return self.apply("midnight", { remember: false });
      });
    },

    /**
     * Repaints the toolbar icon from the saved palette without opening the
     * popup.
     *
     * The tinted icon is only ever built in a document, so it used to live in
     * the popup's memory alone: closing the browser dropped it and the toolbar
     * showed the packaged colour until the popup was opened once more. The
     * background calls this at startup, so the icon and the popup agree from
     * the first paint.
     */
    restoreIcon: function () {
      return savedTheme().then(function (theme) {
        if (!theme) return null;
        currentId = theme.id;
        pushIcon(theme);
        return theme.id;
      }).catch(function () { return null; });
    }
  };
})();
