import { readFileSync } from "node:fs";
const d = JSON.parse(readFileSync(process.env.TEMP + "/umbraco-dump.json", "utf8"));
const n = (id) => d.find((x) => x.id === id);

const blockAliases = (node) => {
  const raw = node.properties.contentBlocks?.inv;
  if (!raw) return "none";
  const j = JSON.parse(raw);
  return j.contentData.map((b) => b.values.map((v) => v.alias).join("+")).join(" | ");
};
for (const id of [1077, 1078, 1079, 1088, 1089, 1090]) {
  console.log(n(id).name, "::", blockAliases(n(id)).slice(0, 300));
}
console.log("=== LOPPIS gallery first block ===");
const lop = JSON.parse(n(1077).properties.contentBlocks.inv);
console.log(JSON.stringify(lop.contentData[0].values, null, 1).slice(0, 600));
console.log("=== BARN mainContent sv ===");
console.log((n(1071).properties.mainContent?.["sv-SE"] || "").slice(0, 500));
console.log("=== OMOSS mainContent sv (first 600) ===");
console.log((n(1069).properties.mainContent?.["sv-SE"] || "").slice(0, 600));
console.log("=== FORBON mainContent sv (first 400) ===");
console.log((n(1079).properties.mainContent?.["sv-SE"] || "").slice(0, 400));
