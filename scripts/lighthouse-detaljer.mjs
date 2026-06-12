import { readFileSync } from "node:fs";
for (const fil of process.argv.slice(2)) {
  const r = JSON.parse(readFileSync(`.lighthouse/${fil}.json`, "utf8"));
  console.log(`\n=== ${fil}`);
  for (const id of ["color-contrast", "heading-order", "inspector-issues"]) {
    const a = r.audits[id];
    if (!a || a.score === 1 || a.score === null) continue;
    console.log(`-- ${id}:`);
    for (const item of a.details?.items || []) {
      console.log("   ", (item.node?.selector || "") + " | " + (item.node?.snippet || item.issueType || "").slice(0, 120));
    }
  }
}
