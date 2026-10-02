// Patches YesPlayMusic's app.asar: the /player API response gains a "playing" field.
// Usage:  node patch-yesplaymusic-asar.mjs "C:\Program Files\YesPlayMusic\resources\app.asar"
// Needs:  npm install @electron/asar   (in this folder), Node 18+
// Always back up app.asar first; copying into Program Files needs admin rights.
import asar from '@electron/asar';
import fs from 'fs';

const target = process.argv[2];
if (!target || !fs.existsSync(target)) {
  console.error('usage: node patch-yesplaymusic-asar.mjs <path-to-app.asar>');
  process.exit(1);
}

const work = 'ypm-asar-work';
if (fs.existsSync(work)) fs.rmSync(work, { recursive: true });
fs.mkdirSync(work);
asar.extractAll(target, work + '/app');

const bgPath = work + '/app/background.js';
const data = fs.readFileSync(bgPath, 'utf8');
const oldStr = 't.send({currentTrack:e._isPersonalFM?e._personalFMTrack:e._currentTrack,progress:e._progress})';
const newStr = 't.send({currentTrack:e._isPersonalFM?e._personalFMTrack:e._currentTrack,progress:e._progress,playing:e._playing})';
const occurrences = data.split(oldStr).length - 1;
if (occurrences !== 1) {
  console.error('expected exactly 1 occurrence of the /player route, found ' + occurrences);
  process.exit(1);
}
fs.writeFileSync(bgPath, data.replace(oldStr, newStr));
await asar.createPackage(work + '/app', 'app.asar.patched');
fs.rmSync(work, { recursive: true });
console.log('written app.asar.patched — back up the original, then copy it over ' + target);
