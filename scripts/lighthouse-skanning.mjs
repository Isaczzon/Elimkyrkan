// Kör Lighthouse mot en lista sidor och sammanfattar poäng + fallerande audits.
//   node scripts/lighthouse-skanning.mjs [bas-url] [sida ...]
import { execSync } from "node:child_process";
import { readFileSync, mkdirSync } from "node:fs";

const bas = process.argv[2] || "http://localhost:8090";
const sidor = process.argv.length > 3 ? process.argv.slice(3) : ["/"];
mkdirSync(".lighthouse", { recursive: true });

const KRAV = { performance: 80, accessibility: 100, "best-practices": 100, seo: 100 };

for (const sida of sidor) {
  const namn = sida.replace(/[^a-z0-9]+/gi, "_") || "_";
  const ut = `.lighthouse/${namn}.json`;
  try {
    execSync(
      `npx lighthouse "${bas}${sida}" --output=json --output-path="${ut}" --quiet ` +
        `--chrome-flags="--headless=new" --only-categories=performance,accessibility,best-practices,seo`,
      { stdio: ["ignore", "ignore", "ignore"] }
    );
  } catch {
    // chrome-launcher kastar EPERM vid temp-städning på Windows; rapporten skrivs ändå
  }
  const r = JSON.parse(readFileSync(ut, "utf8"));
  const poang = Object.fromEntries(
    Object.entries(r.categories).map(([k, v]) => [k, Math.round(v.score * 100)])
  );
  const rad = Object.entries(poang)
    .map(([k, v]) => `${k}=${v}${v < (KRAV[k] ?? 0) ? "!" : ""}`)
    .join("  ");
  console.log(`\n=== ${sida}  ${rad}`);

  for (const [kat, info] of Object.entries(r.categories)) {
    if (Math.round(info.score * 100) >= (KRAV[kat] ?? 0) && kat !== "performance") continue;
    for (const ref of info.auditRefs) {
      const a = r.audits[ref.id];
      if (a.score !== null && a.score < 1 && a.scoreDisplayMode !== "informative") {
        const detalj = a.details?.items?.length ? ` (${a.details.items.length} st)` : "";
        console.log(`  [${kat}] ${ref.id}: ${Math.round(a.score * 100)}${detalj}`);
      }
    }
  }
}
