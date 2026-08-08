const fs = require('fs');
const path = require('path');
// 普通版构建用默认 obj/Release——彻底清空可再生成的中间产物，避免文件锁/残留
const roots = ['Emuera.Maui/obj', 'Emuera.Maui/bin'];
let removed = 0, failed = [];
function walk(d) {
  let entries;
  try { entries = fs.readdirSync(d, { withFileTypes: true }); } catch (e) { return; }
  for (const e of entries) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) { walk(p); }
    else {
      try { fs.unlinkSync(p); removed++; } catch (err) { failed.push(p); }
    }
  }
}
for (const r of roots) { if (fs.existsSync(r)) walk(r); }
console.log('removed files:', removed, 'failed:', failed.length);
if (failed.length) console.log(failed.slice(0, 5).join('\n'));
