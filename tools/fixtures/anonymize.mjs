#!/usr/bin/env node
// Turns a raw RSI capture into a test fixture: same markup, same classes, but
// every handle, display name, avatar, citizen number and free text replaced by
// deterministic placeholders.
//
//   node tools/fixtures/anonymize.mjs members <raw.json> <out.json>
//   node tools/fixtures/anonymize.mjs profile <raw.html> <out.html> <handle> [displayName]
//
// Raw captures stay out of the repository; only the output is committed. The
// script fails if an original value survives in the output.

import { readFileSync, writeFileSync } from "node:fs";

const [kind, input, output, ...rest] = process.argv.slice(2);
if (!kind || !input || !output) {
  console.error("usage: anonymize.mjs members|profile <input> <output> [handle] [displayName]");
  process.exit(2);
}

const pad = (n) => String(n).padStart(3, "0");
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

function pseudonyms(prefix) {
  const map = new Map();
  return (value) => {
    if (!map.has(value)) map.set(value, `${prefix}${pad(map.size + 1)}`);
    return map.get(value);
  };
}

function assertGone(text, originals) {
  const leaks = originals.filter((o) => o && o.length >= 3 && text.includes(o));
  if (leaks.length) {
    console.error(`original values left in output: ${leaks.slice(0, 5).join(", ")}`);
    process.exit(1);
  }
}

function anonymizeMembers(raw) {
  const json = JSON.parse(raw);
  const html = json?.data?.html;
  if (!html) return JSON.stringify(json, null, 2) + "\n";

  const handle = pseudonyms("pilot-");
  const display = pseudonyms("Display ");
  const avatar = pseudonyms("avatar-");
  const role = pseudonyms("Role ");
  const originals = [];

  let out = html
    .replace(/data-org-sid="[^"]*"/g, 'data-org-sid="FIXTURE"')
    .replace(/data-org-name="[^"]*"/g, 'data-org-name="Fixture Org"')
    .replace(/href="\/citizens\/([^"]+)"/g, (_, h) => {
      originals.push(h);
      return `href="/citizens/${handle(h)}"`;
    })
    .replace(/(<span class="[^"]*\bnick\b[^"]*">)([^<]*)(<\/span>)/g, (_, open, text, close) => {
      if (/^(&nbsp;|\s)*$/.test(text)) return open + text + close; // redacted / hidden rows
      originals.push(text.trim());
      return open + handle(text.trim()) + close;
    })
    .replace(/(<span class="[^"]*\bname\b[^"]*">)([^<]*)(<\/span>)/g, (_, open, text, close) => {
      if (/^(&nbsp;|\s)*$/.test(text)) return open + text + close;
      originals.push(text.trim());
      return open + display(text.trim()) + close;
    })
    .replace(/<img src="([^"]+)"/g, (_, src) => {
      originals.push(src);
      return `<img src="https://robertsspaceindustries.com/media/fixture/${avatar(src)}.jpg"`;
    })
    .replace(/(<li class="role">)([^<]*)(<\/li>)/g, (_, open, text, close) => open + role(text.trim()) + close);

  assertGone(out, originals);
  json.data.html = out;
  return JSON.stringify(json, null, 2) + "\n";
}

function anonymizeProfile(raw, realHandle, realName) {
  if (!realHandle) {
    console.error("profile: the real handle is required so every occurrence can be replaced");
    process.exit(2);
  }
  const citizenNumbers = [...raw.matchAll(/class="value">#(\d+)</g)].map((m) => m[1]);
  let out = raw
    // Scripts carry nothing the parser reads (and may carry tokens).
    .replace(/(<script\b[^>]*>)[\s\S]*?(<\/script>)/gi, "$1$2")
    .replace(new RegExp(escapeRe(realHandle), "gi"), "fixture-pilot")
    .replace(/(<strong class="value">)#\d+(<\/strong>)/g, "$1#100001$2")
    .replace(/(src|href)="(https?:\/\/[^"]*|\/)media\/[^"]+"/g, '$1="https://robertsspaceindustries.com/media/fixture/avatar.jpg"')
    .replace(/(<div class="entry bio">[\s\S]*?<div class="value">)[\s\S]*?(<\/div>)/, "$1Fixture bio.$2")
    .replace(/(<span class="label">Enlisted<\/span>\s*<strong class="value">)[^<]*(<\/strong>)/, "$1Jan 1, 2020$2")
    .replace(/(<span class="label">Location<\/span>\s*<strong class="value">)[\s\S]*?(<\/strong>)/, "$1Fixture Land$2");
  if (realName) out = out.replace(new RegExp(escapeRe(realName), "g"), "Fixture Pilot");
  assertGone(out, [realHandle, realName, ...citizenNumbers.filter((n) => n !== "100001")]);
  return out;
}

const raw = readFileSync(input, "utf8");
const result = kind === "members" ? anonymizeMembers(raw)
  : kind === "profile" ? anonymizeProfile(raw, rest[0], rest[1])
  : (console.error(`unknown kind ${kind}`), process.exit(2));
writeFileSync(output, result);
console.log(`${output}: ${result.length} bytes`);
