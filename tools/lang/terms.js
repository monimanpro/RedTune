// How earlier translations put a few English terms: node terms.js "Auto-tune" "Curve" ...
// (exact English text of a pair, or /regex/ to list the first pairs whose English matches)
const fs = require("fs"), path = require("path");
const en = fs.readFileSync(path.join(__dirname, "en.tsv"), "utf8").split(/\r?\n/).filter(Boolean).map(l => l.split("\t"));
const codes = ["es", "pt", "zh", "ko", "th", "vi", "id"], tr = {};
for (const c of codes) tr[c] = JSON.parse(fs.readFileSync(path.join(__dirname, "tr", c + ".json"), "utf8"));
for (const q of process.argv.slice(2)) {
  const rx = q.startsWith("re:") ? new RegExp(q.slice(3), "i") : null;
  const hits = en.filter(p => { let t; try { t = JSON.parse(p[1]); } catch (e) { t = p[1]; } return rx ? rx.test(t) : t === q; }).slice(0, rx ? 3 : 1);
  for (const p of hits) {
    if (!tr.es[p[0]]) continue;
    console.log(p[1] + "  ->  " + codes.map(c => c + ": " + tr[c][p[0]]).join(" | "));
  }
}
