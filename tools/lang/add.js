// Adds translations of one or more pairs to tr\<code>.json:  node add.js <file.json>
// where the file is { "<key>": { "es": "…", "pt": "…", "zh": "…", "ko": "…", "th": "…", "vi": "…", "id": "…" }, … }
const fs = require('fs'), path = require('path');
const add = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
for (const code of ['es', 'pt', 'zh', 'ko', 'th', 'vi', 'id']) {
  const file = path.join(__dirname, 'tr', code + '.json');
  const tr = JSON.parse(fs.readFileSync(file, 'utf8')); let n = 0;
  for (const key of Object.keys(add)) if (add[key][code] !== undefined) { tr[key] = add[key][code]; n++; }
  fs.writeFileSync(file, '{\n' + Object.keys(tr).map(k => JSON.stringify(k) + ':' + JSON.stringify(tr[k])).join(',\n') + '\n}\n', 'utf8');
  console.log(code + ': ' + n + ' added');
}
