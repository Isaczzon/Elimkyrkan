// Skapar .gz-syskon till text-filer i _site så att `http-server --gzip`
// serverar komprimerat — som GitHub Pages gör i produktion.
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { gzipSync } from "node:zlib";

let antal = 0;
const ga = (dir) => {
  for (const f of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, f.name);
    if (f.isDirectory()) ga(p);
    else if (/\.(html|css|js|svg|json|xml)$/.test(f.name)) {
      writeFileSync(p + ".gz", gzipSync(readFileSync(p), { level: 9 }));
      antal++;
    }
  }
};
ga("_site");
console.log(`${antal} filer gzippade`);
