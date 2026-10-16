// Print printable strings (ASCII and UTF-16LE, 6+ chars) from a binary file, optionally filtered by a regex.
// Usage: node strings.js <file> [regex]
//   node strings.js "D:/Games/Foo/Binaries/Win64/Foo.exe" "upscal|framegen|dlss|fsr|xess|raytrac|motionblur"
//   node strings.js "%USERPROFILE%/Saved Games/Studio/Game/GameUserSettings.sav"
// Names found this way are candidates only (no values); confirm them in the config file before relying on them.
const fs = require("fs");
const [file, pattern] = process.argv.slice(2);
if (!file) {
  console.error("usage: node strings.js <file> [regex]");
  process.exit(2);
}
const buf = fs.readFileSync(file);
const re = /[\x20-\x7e]{6,}/g;
const found = new Set([
  ...(buf.toString("latin1").match(re) || []),
  ...(buf.toString("utf16le").match(re) || []),
  ...(buf.subarray(1).toString("utf16le").match(re) || []),
]);
const filter = pattern ? new RegExp(pattern, "i") : null;
for (const s of found) if (!filter || filter.test(s)) console.log(s);
