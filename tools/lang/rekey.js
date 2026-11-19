// One-time (17.11): the first translation pass kept its texts by the pair's number in keys.json (tr\<code>.NN.json),
// and a number moves as soon as a pair is added in the middle of Lang.cs. This puts them under the pair's own key
// (kind + id of the Russian text) into tr\<code>.json and moves the numbered files to tr\by-number\.
// Run with the keys.json the numbered files were made from, before extract.js is run again.
const fs = require('fs'), path = require('path');
const here = __dirname, dir = path.join(here, 'tr'), old = path.join(dir, 'by-number');
const keys = JSON.parse(fs.readFileSync(path.join(here, 'keys.json'), 'utf8'));
fs.mkdirSync(old, { recursive: true });
for (const code of ['es', 'pt', 'zh', 'ko', 'th', 'vi', 'id']) {
  const files = fs.readdirSync(dir).filter(f => new RegExp('^' + code + '\\.\\d+\\.json$').test(f)).sort();
  if (files.length === 0) { console.log(code + ': no numbered files'); continue; }
  const byN = {};
  for (const f of files) Object.assign(byN, JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
  const out = {}; let lost = 0;
  for (const k of keys) { const t = byN[k.n]; if (t === undefined) { lost++; continue; } out[k.kind[0] + k.id] = t; }
  fs.writeFileSync(path.join(dir, code + '.json'), '{\n' + Object.keys(out).map(k => JSON.stringify(k) + ':' + JSON.stringify(out[k])).join(',\n') + '\n}\n', 'utf8');
  for (const f of files) fs.renameSync(path.join(dir, f), path.join(old, f));
  console.log(code + ': ' + Object.keys(out).length + ' texts rekeyed from ' + files.length + ' files' + (lost ? ', without a text: ' + lost : ''));
}
