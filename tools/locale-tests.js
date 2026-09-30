// Locale-file checks that do not need the compiled app.
//
// Run:  node tools/locale-tests.js
//
// The install summary is assembled from localization keys plus substitution
// values, which makes two things easy to get wrong: a locale that is missing a
// key the service emits, and a translation whose placeholder count does not
// match what the code passes. Both render as raw text in a dialog, so they are
// checked here rather than left to a manual pass through twelve languages.
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const LOCALES = path.join(ROOT, "locales");

let pass = 0, fail = 0;
const check = (name, cond, detail = "") => {
  if (cond) { console.log(`  PASS  ${name}`); pass++; }
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); fail++; }
};
const section = (t) => console.log(`\n=== ${t} ===`);

const files = fs.readdirSync(LOCALES).filter(f => f.endsWith(".json")).sort();
const langs = files.map(f => path.basename(f, ".json"));
const data = {};
for (const f of files) {
  try {
    data[path.basename(f, ".json")] =
      JSON.parse(fs.readFileSync(path.join(LOCALES, f), "utf8").replace(/^\uFEFF/, ""));
  } catch (e) {
    data[path.basename(f, ".json")] = null;
  }
}

section("every locale file is valid JSON with the expected shape");
{
  check("12 locale files", files.length === 12, String(files.length));
  for (const l of langs) {
    const d = data[l];
    const ok = d &&
      typeof d.language_code === "string" && d.language_code.length > 0 &&
      typeof d.language_name === "string" && d.language_name.length > 0 &&
      d.translations && typeof d.translations === "object";
    check(`${l}: parses and has code/name/translations`, ok,
      ok ? "" : JSON.stringify(d && Object.keys(d)));
  }
}

section("all locales share one key set, in one order");
{
  const ref = langs.find(l => data[l]);
  const refKeys = Object.keys(data[ref].translations);
  for (const l of langs) {
    if (!data[l]) continue;
    const keys = Object.keys(data[l].translations);
    const missing = refKeys.filter(k => !keys.includes(k));
    const extra = keys.filter(k => !refKeys.includes(k));
    check(`${l}: identical key set (${keys.length} keys)`,
      missing.length === 0 && extra.length === 0,
      missing.length ? "missing " + missing.join(",") : extra.length ? "extra " + extra.join(",") : "");
  }
}

section("every value is a non-empty string and is not quote-wrapped");
{
  const wrapped = [], empty = [];
  for (const l of langs) {
    if (!data[l]) continue;
    for (const [k, v] of Object.entries(data[l].translations)) {
      if (typeof v !== "string" || v.trim() === "") empty.push(`${l}/${k}`);
      // A locale generator that wrapped values would show the quotes on screen.
      if (/^".*"$/.test(v.trim()) || /^'.*'$/.test(v.trim())) wrapped.push(`${l}/${k}`);
    }
  }
  check("no empty values", empty.length === 0, empty.join(", "));
  check("no quote-wrapped values", wrapped.length === 0, wrapped.join(", "));
}

section("install-summary placeholders match the arguments the services pass");
{
  // Mirrors ExtensionInstallService.InstallAll and
  // BrowserIntegrationService.InstallAll. Getting this wrong renders a raw
  // "{0}" in the install dialog, or silently drops a path.
  const expected = {
    install_item_chromium_dir: [0, 1],
    install_item_chromium_id: [0],
    install_item_chromium_cmd: [0],
    install_item_firefox_dir: [0, 1],
    install_item_firefox_id: [0],
    install_item_firefox_xpi: [0, 1],
    install_item_firefox_profile: [0],
    install_item_host_chromium: [0],
    install_item_host_firefox: [0],
    install_success_head: [],
    install_checking: [],
    install_repair_hint: [],
    install_hint_chromium: [],
    install_hint_firefox: [],
    install_error: [],
  };

  const problems = [];
  for (const l of langs) {
    if (!data[l]) continue;
    for (const [k, want] of Object.entries(expected)) {
      const text = data[l].translations[k];
      if (typeof text !== "string") { problems.push(`${l}/${k}: absent`); continue; }
      const got = [...new Set((text.match(/\{\d+\}/g) || [])
        .map(m => Number(m.slice(1, -1))))].sort((a, b) => a - b);
      const wantSorted = [...want].sort((a, b) => a - b);
      if (got.join(",") !== wantSorted.join(",")) {
        problems.push(`${l}/${k}: has [${got}] expected [${wantSorted}]`);
      }
    }
  }
  check(`all ${Object.keys(expected).length} install keys present with the right placeholders`,
    problems.length === 0, problems.join(" | "));
}

section("no key is referenced by the code but missing from a locale");
{
  // Every key the window looks up. A miss falls back to the hardcoded English
  // string, so the dialog is readable but never translated.
  const cs = require("child_process")
    .execFileSync("powershell", ["-NoProfile", "-Command",
      "Get-ChildItem MpvLauncher.Gui -Recurse -Filter *.cs | ForEach-Object { Get-Content $_.FullName -Raw }"],
      { cwd: ROOT, encoding: "utf8", maxBuffer: 1 << 26 });

  const referenced = [...cs.matchAll(/_locService\.(?:Get|Format)\(\s*"([a-z0-9_]+)"/g)]
    .map(m => m[1]);
  // The install/uninstall item keys arrive as InstallDetail.Key /
  // UninstallItem.Key at runtime rather than as literals, so they are added
  // explicitly.
  for (const m of cs.matchAll(/Key\s*=\s*"((?:install|uninstall)_item_[a-z0-9_]+)"/g)) referenced.push(m[1]);

  const ref = langs.find(l => data[l]);
  const known = new Set(Object.keys(data[ref].translations));
  const missing = [...new Set(referenced)].filter(k => !known.has(k));
  check(`all ${new Set(referenced).size} keys the code looks up exist`,
    missing.length === 0, missing.join(", "));
}

console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
