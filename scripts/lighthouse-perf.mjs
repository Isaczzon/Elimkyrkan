import { readFileSync } from "node:fs";
for (const fil of process.argv.slice(2)) {
  const r = JSON.parse(readFileSync(`.lighthouse/${fil}.json`, "utf8"));
  const a = r.audits;
  const m = (id) => a[id]?.displayValue || "-";
  console.log(`\n=== ${fil}  FCP=${m("first-contentful-paint")} LCP=${m("largest-contentful-paint")} TBT=${m("total-blocking-time")} CLS=${m("cumulative-layout-shift")} SI=${m("speed-index")}`);
  const lcpEl = a["largest-contentful-paint-element"]?.details?.items?.[0]?.items?.[0]?.node?.snippet;
  if (lcpEl) console.log("LCP-element:", lcpEl.slice(0, 140));
  for (const [id, audit] of Object.entries(a)) {
    if (audit.details?.overallSavingsMs > 100) console.log(`  ${id}: spara ~${Math.round(audit.details.overallSavingsMs)} ms`);
  }
}
