// Engångskonvertering: images/*.png -> optimerad .webp (max 1920px bred)
// och uppdaterar alla referenser i css/json/md.
import sharp from "sharp";
import { readdirSync, readFileSync, writeFileSync, statSync } from "node:fs";
import { join } from "node:path";

const KATALOG = "images";
const filer = readdirSync(KATALOG).filter((f) => f.toLowerCase().endsWith(".png"));

let foreMB = 0, efterMB = 0;
for (const fil of filer) {
  const kalla = join(KATALOG, fil);
  const mal = kalla.replace(/\.png$/i, ".webp");
  foreMB += statSync(kalla).size / 1048576;
  await sharp(kalla).resize({ width: 1920, withoutEnlargement: true }).webp({ quality: 78 }).toFile(mal);
  efterMB += statSync(mal).size / 1048576;
}
console.log(`${filer.length} bilder: ${foreMB.toFixed(1)} MB PNG -> ${efterMB.toFixed(1)} MB WebP`);

// Uppdatera referenser
const sokvagar = [];
const ga = (dir) => {
  for (const f of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, f.name);
    if (f.isDirectory()) ga(p);
    else if (/\.(css|json|md|njk)$/.test(f.name)) sokvagar.push(p);
  }
};
ga("src");

const namn = filer.map((f) => f.replace(/\.png$/i, ""));
let uppdaterade = 0;
for (const p of sokvagar) {
  let text = readFileSync(p, "utf8");
  const fore = text;
  for (const n of namn) {
    text = text.split(`images/${n}.png`).join(`images/${n}.webp`);
  }
  if (text !== fore) {
    writeFileSync(p, text);
    uppdaterade++;
  }
}
console.log(`${uppdaterade} filer med uppdaterade bildreferenser`);
