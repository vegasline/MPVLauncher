import assert from "node:assert/strict";
import test from "node:test";

import { startsWithTsSync, stripToTsSync } from "../src/mpegts.js";
import { isMasterPlaylist, isPlaylist, rewritePlaylist } from "../src/playlist.js";
import {
  bareUrl,
  decodeRemote,
  encodeRemote,
  hasMediaExtension,
  isHttpUrl,
  isPrivateHost,
  sameOrigin,
} from "../src/urls.js";
import { looksLikeStreamUrl } from "../src/driver.js";

test("bareUrl keeps origin and path, drops query and fragment", () => {
  assert.equal(
    bareUrl("https://cdn.x/a/video.mp4?t=1#frag"),
    "https://cdn.x/a/video.mp4"
  );
  assert.equal(bareUrl("https://x/hls/master.m3u8"), "https://x/hls/master.m3u8");
});

test("media extensions are recognised case-insensitively", () => {
  assert.equal(hasMediaExtension("https://cdn.x/a/video.mp4?t=1"), true);
  assert.equal(hasMediaExtension("https://x/hls/master.M3U8"), true);
  assert.equal(hasMediaExtension("https://site/w/g_pUnzFwmnA4"), false);
});

test("private and loopback hosts are refused", () => {
  for (const url of [
    "http://127.0.0.1/x",
    "http://localhost/x",
    "http://10.1.2.3/x",
    "http://192.168.0.1/x",
    "http://172.16.0.1/x",
    "http://169.254.1.1/x",
    "http://[::1]/x",
  ]) {
    assert.equal(isPrivateHost(url), true, url);
  }
  assert.equal(isPrivateHost("https://sv99.example.top/x"), false);
});

test("only http and https are accepted", () => {
  assert.equal(isHttpUrl("https://x/y"), true);
  assert.equal(isHttpUrl("file:///c:/x"), false);
  assert.equal(isHttpUrl("magnet:?xt=urn:btih:x"), false);
});

test("encodeRemote and decodeRemote round-trip", () => {
  const url = "https://site/w/token/v2/file001.jpeg?a=1&b=2";
  assert.equal(decodeRemote(encodeRemote(url)), url);
  assert.equal(/[:/?&=]/.test(encodeRemote(url)), false);
});

test("sameOrigin compares host and protocol", () => {
  assert.equal(sameOrigin("https://a.com/x", "https://a.com/y"), true);
  assert.equal(sameOrigin("https://a.com/x", "http://a.com/y"), false);
  assert.equal(sameOrigin("https://a.com/x", "https://b.com/y"), false);
});

test("playlist detection uses content, not the URL", () => {
  assert.equal(isPlaylist("#EXTM3U\n#EXT-X-VERSION:3\n"), true);
  assert.equal(isPlaylist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nv0\n"), true);
  assert.equal(isPlaylist("<!doctype html><html>"), false);
  assert.equal(isMasterPlaylist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nv0\n"), true);
  assert.equal(isMasterPlaylist("#EXTM3U\n#EXTINF:2,\nseg.ts\n"), false);
});

test("rewritePlaylist absolutises relative URIs and rewrites remote ones", () => {
  const master = [
    "#EXTM3U",
    "#EXT-X-STREAM-INF:BANDWIDTH=800000",
    "/w/token/v0",
    "#EXT-X-STREAM-INF:BANDWIDTH=1200000",
    "/w/token/v1",
  ].join("\n");

  const rewrite = (remote) => `relay:${remote}`;
  const result = rewritePlaylist(master, "https://site/w/token", rewrite);
  assert.match(result.text, /relay:https:\/\/site\/w\/token\/v0/);
  assert.match(result.text, /relay:https:\/\/site\/w\/token\/v1/);
  assert.equal(result.uris.length, 2);
});

test("rewritePlaylist handles URI attributes in KEY and MAP tags", () => {
  const media = [
    "#EXTM3U",
    '#EXT-X-KEY:METHOD=AES-128,URI="https://site/key.bin"',
    '#EXT-X-MAP:URI="init.mp4"',
    "#EXTINF:2,",
    "https://cdn.x/file001.jpeg",
  ].join("\n");

  const result = rewritePlaylist(media, "https://site/w/token/v2", (remote) => `relay:${remote}`);
  assert.match(result.text, /URI="relay:https:\/\/site\/key\.bin"/);
  assert.match(result.text, /URI="relay:https:\/\/site\/w\/token\/init\.mp4"/);
  assert.match(result.text, /relay:https:\/\/cdn\.x\/file001\.jpeg/);
  assert.equal(result.uris.length, 3);
});

test("rewritePlaylist leaves comments and tags untouched", () => {
  const media = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nhttps://cdn.x/a.ts\n";
  const result = rewritePlaylist(media, "https://cdn.x/a.m3u8", (remote) => `relay:${remote}`);
  assert.match(result.text, /^#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\n/);
});

test("MPEG-TS sync detection requires three aligned packets", () => {
  const good = Buffer.alloc(188 * 3);
  good[0] = 0x47;
  good[188] = 0x47;
  good[376] = 0x47;
  assert.equal(startsWithTsSync(good), true);

  const bad = Buffer.alloc(188 * 3, 0x11);
  assert.equal(startsWithTsSync(bad), false);
});

test("stripToTsSync drops a leading image header", () => {
  const header = Buffer.from("\xff\xd8\xff\xe0 fake jpeg header ", "latin1");
  const payload = Buffer.alloc(188 * 3, 0x47);
  const input = Buffer.concat([header, payload]);

  const stripped = stripToTsSync(input);
  assert.equal(stripped[0], 0x47);
  assert.equal(stripped.length, payload.length);
});

test("stripToTsSync leaves a clean payload alone", () => {
  const payload = Buffer.alloc(188 * 3, 0x47);
  assert.equal(stripToTsSync(payload), payload);
});

test("the page's own URL is never mistaken for a stream", () => {
  const page = "https://www.tranimeizle.live/jujutsu-kaisen-2-sezon-17-bolum-izle";
  assert.equal(looksLikeStreamUrl(page, page), false);
  assert.equal(looksLikeStreamUrl("https://www.tranimeizle.live/w/AbCdEfGhIjKlMnOp", page), true);
  assert.equal(looksLikeStreamUrl("https://www.tranimeizle.live/w/AbCdEfGhIjKlMnOp?x=1", page), true);
  assert.equal(looksLikeStreamUrl("https://cdn.example.com/w/AbCdEfGhIjKlMnOp", page), false);
  assert.equal(looksLikeStreamUrl("https://www.tranimeizle.live/w/stream.m3u8", page), false);
});
