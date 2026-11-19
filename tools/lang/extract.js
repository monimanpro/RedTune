// Pulls every Russian → English pair out of src\Lang.cs and src\Lang2.cs, in file order, into keys.json:
//   [{ n, kind: "whole" | "frag", id: <FNV-1a of the Russian text, 8 hex>, ru, en }]
// The other languages are keyed by that id (see build.js), so their files do not repeat the Russian text.
//   node extract.js <RedTune\src> <out folder>
const fs = require('fs'), path = require('path');
const src = process.argv[2], out = process.argv[3];

function unescape(s) {
  return s.replace(/\\(u[0-9a-fA-F]{4}|.)/g, (m, c) => c === 'n' ? '\n' : c === 't' ? '\t' : c === 'r' ? '\r' : c[0] === 'u' ? String.fromCharCode(parseInt(c.slice(1), 16)) : c);
}
function fnv(s) { // FNV-1a, 32 bit, over UTF-16 code units: the same in L.Id (src\Lang.cs)
  let h = 0x811C9DC5;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return h.toString(16).padStart(8, '0');
}

const STR = '"((?:[^"\\\\]|\\\\.)*)"';
const pair = new RegExp('\\{\\s*' + STR + '\\s*,\\s*' + STR + '\\s*\\}', 'g');
const items = []; const seen = new Map(); let braces = 0;

for (const file of ['Lang.cs', 'Lang2.cs']) {
  const text = fs.readFileSync(path.join(src, file), 'utf8');
  // the fragment tables start at these declarations; everything before them in a file is whole strings
  const marks = [];
  for (const m of text.matchAll(/static (?:readonly )?(?:KeyValuePair<string, string>\[\]|Dictionary<string, string>) (\w+) = /g)) marks.push({ at: m.index, name: m[1] });
  const kindAt = pos => { let k = 'whole'; for (const mk of marks) if (mk.at <= pos) k = /Fragments/.test(mk.name) ? 'frag' : 'whole'; return k; };
  braces += (text.match(/\{\s*"/g) || []).length;
  for (const m of text.matchAll(pair)) {
    const ru = unescape(m[1]), en = unescape(m[2]), kind = kindAt(m.index), key = kind + '\u0000' + ru;
    if (seen.has(key)) continue; // a later duplicate: the first one stays, as in the app
    seen.set(key, true);
    items.push({ n: items.length + 1, kind, id: fnv(ru), ru, en });
  }
}

const ids = new Map(); let clash = 0;
for (const it of items) { const k = it.kind + it.id; if (ids.has(k) && ids.get(k) !== it.ru) clash++; ids.set(k, it.ru); }
fs.mkdirSync(out, { recursive: true });
fs.writeFileSync(path.join(out, 'keys.json'), JSON.stringify(items, null, 0).replace(/\},\{/g, '},\n{'), 'utf8');
const whole = items.filter(i => i.kind === 'whole').length;
console.log('pairs: ' + items.length + ' (whole ' + whole + ', fragments ' + (items.length - whole) + '), "{ \\"" openings: ' + braces + ', id clashes: ' + clash);
console.log('english chars: ' + items.reduce((a, i) => a + i.en.length, 0) + ', russian chars: ' + items.reduce((a, i) => a + i.ru.length, 0));
