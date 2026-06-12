// Engångsskript: dumpar allt publicerat innehåll (alla språk) ur Umbracos
// SQLite-databas till JSON, som underlag för migreringen till Eleventy.
import { DatabaseSync } from "node:sqlite";
import { writeFileSync } from "node:fs";

const dbPath = process.argv[2];
const outPath = process.argv[3] || "umbraco-content-dump.json";
const db = new DatabaseSync(dbPath, { readOnly: true });

const langs = db.prepare("SELECT id, languageISOCode FROM umbracoLanguage").all();
const langById = Object.fromEntries(langs.map((l) => [l.id, l.languageISOCode]));

// Node tree (published documents only)
const nodes = db
  .prepare(
    `SELECT n.id, n.parentId, n.text AS name, n.level, n.sortOrder, ct.alias AS contentType
     FROM umbracoNode n
     JOIN umbracoContent c ON c.nodeId = n.id
     JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
     JOIN umbracoDocument d ON d.nodeId = n.id
     WHERE n.trashed = 0
     ORDER BY n.level, n.parentId, n.sortOrder`
  )
  .all();

// Per-culture node names
const cultureNames = db
  .prepare(
    `SELECT v.nodeId, cv.languageId, cv.name
     FROM umbracoContentVersionCultureVariation cv
     JOIN umbracoContentVersion v ON v.id = cv.versionId
     WHERE v.current = 1`
  )
  .all();
const namesByNode = {};
for (const r of cultureNames) {
  (namesByNode[r.nodeId] ||= {})[langById[r.languageId] || "inv"] = r.name;
}

// Property data for current versions
const props = db
  .prepare(
    `SELECT v.nodeId, pt.Alias AS alias, pd.languageId, pd.textValue, pd.varcharValue,
            pd.intValue, pd.decimalValue, pd.dateValue
     FROM umbracoPropertyData pd
     JOIN umbracoContentVersion v ON v.id = pd.versionId
     JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
     WHERE v.current = 1`
  )
  .all();
const propsByNode = {};
for (const p of props) {
  const lang = p.languageId ? langById[p.languageId] || "inv" : "inv";
  const value = p.textValue ?? p.varcharValue ?? p.intValue ?? p.decimalValue ?? p.dateValue;
  if (value === null || value === undefined) continue;
  ((propsByNode[p.nodeId] ||= {})[p.alias] ||= {})[lang] = value;
}

const out = nodes.map((n) => ({
  id: n.id,
  parentId: n.parentId,
  contentType: n.contentType,
  name: n.name,
  sortOrder: n.sortOrder,
  names: namesByNode[n.id] || {},
  properties: propsByNode[n.id] || {},
}));

writeFileSync(outPath, JSON.stringify(out, null, 2));
console.log(`Dumped ${out.length} nodes to ${outPath}`);
