const fs = require('fs');
const path = require('path');
const roots = ['Emuera.Maui/obj-aot', 'Emuera.Maui/obj/Release', 'Emuera.Headless.Core/obj-aot'];
let removed = 0, failed = [];
function walk(d) {
  let entries;
  try { entries = fs.readdirSync(d, { withFileTypes: true }); } catch (e) { return; }
  for (const e of entries) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) { walk(p); }
    else if (e.name.endsWith('.cs') || e.name.endsWith('.xaml') || e.name.includes('AndroidManifest')) {
      try { fs.unlinkSync(p); removed++; } catch (err) { failed.push(p); }
    }
  }
}
for (const r of roots) { if (fs.existsSync(r)) walk(r); }
console.log('removed:', removed, 'failed:', failed.length);
if (failed.length) console.log(failed.slice(0, 5).join('\n'));
