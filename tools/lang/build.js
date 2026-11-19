// Writes RedTune\lang\<code>.txt from the translations in tr\<code>.json and says what is missing or looks wrong.
// A translation is kept under the pair's key: its kind (w = a whole string, f = a fragment of a composed one) and the
// id of its Russian text, as in keys.json: { "w1a2b3c4d": "text", … }. The key does not change when pairs are added
// or moved in src\Lang.cs / Lang2.cs, so new pairs only show up as missing.
//   node build.js <RedTune folder>            all languages
//   node build.js <RedTune folder> --list     also writes en.tsv: «key <tab> English» to translate from, and
//                                             missing.tsv: the same for the pairs some language has no line for
// After changing the Russian or English texts: node extract.js <RedTune\src> .  first.
const fs = require('fs'), path = require('path');
const root = process.argv[2], here = __dirname;
const keys = JSON.parse(fs.readFileSync(path.join(here, 'keys.json'), 'utf8'));
const keyOf = k => k.kind[0] + k.id;

// the language's own name, its word for a laptop (the one the translation uses everywhere) and for a desktop PC
const langs = {
  es: ['Español', 'portátil', 'PC'], pt: ['Português (Brasil)', 'notebook', 'PC'], zh: ['简体中文', '笔记本电脑', '电脑'],
  ko: ['한국어', '노트북', 'PC'], th: ['ไทย', 'แล็ปท็อป', 'พีซี'], vi: ['Tiếng Việt', 'laptop', 'PC'], id: ['Bahasa Indonesia', 'laptop', 'PC'],
};
const esc = s => s.replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/\t/g, '\\t');
const edge = s => [s.match(/^\s*/)[0], s.match(/\s*$/)[0]];
fs.mkdirSync(path.join(root, 'lang'), { recursive: true });
const everMissing = new Set();

for (const code of Object.keys(langs)) {
  const file = path.join(here, 'tr', code + '.json');
  const tr = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {};
  const lines = ['# Red Tune: ' + langs[code][0] + '. Made by tools\\lang\\build.js; w = a whole string, f = a fragment of a composed one',
                 '@laptop\t' + langs[code][1] + '\t' + langs[code][2]];
  const missing = [], odd = [], known = new Set();
  for (const k of keys) {
    const key = keyOf(k); known.add(key);
    let t = tr[key];
    if (t === undefined) { missing.push(key); everMissing.add(key); continue; }
    // the spaces and line breaks at the ends join a fragment to its neighbours: always the English ones
    const [a, b] = edge(k.en);
    t = a + t.trim() + b;
    if ((t.match(/\n/g) || []).length !== (k.en.match(/\n/g) || []).length) odd.push(key + ' line breaks');
    for (const ch of ['«', '»', '%', '▾', '→', '…']) if ((t.split(ch).length !== k.en.split(ch).length)) odd.push(key + ' ' + ch);
    if (/[А-Яа-яЁё]/.test(t)) odd.push(key + ' cyrillic');
    lines.push(k.kind[0] + '\t' + k.id + '\t' + esc(t));
  }
  const stale = Object.keys(tr).filter(key => !known.has(key));
  fs.writeFileSync(path.join(root, 'lang', code + '.txt'), lines.join('\n') + '\n', 'utf8');
  console.log(code + ': ' + (keys.length - missing.length) + ' of ' + keys.length +
    (missing.length ? ', missing ' + missing.length + ': ' + missing.slice(0, 12).join(' ') : '') +
    (stale.length ? ', no longer used ' + stale.length : '') + (odd.length ? '\n   check: ' + odd.slice(0, 40).join('; ') : ''));
}

if (process.argv.includes('--list')) {
  const row = k => keyOf(k) + '\t' + JSON.stringify(k.en);
  fs.writeFileSync(path.join(here, 'en.tsv'), keys.map(row).join('\n') + '\n', 'utf8');
  fs.writeFileSync(path.join(here, 'missing.tsv'), keys.filter(k => everMissing.has(keyOf(k))).map(k => row(k) + '\t' + JSON.stringify(k.ru)).join('\n') + '\n', 'utf8');
  console.log('en.tsv: ' + keys.length + ' pairs, missing.tsv: ' + everMissing.size);
}
