import { DatabaseSync } from "node:sqlite";
const db = new DatabaseSync(process.env.TEMP + "/Umbraco.sqlite.db", { readOnly: true });
const rows = db
  .prepare(
    `SELECT n.uniqueId AS guid, n.text AS name, pd.textValue, pd.varcharValue
     FROM umbracoNode n
     JOIN umbracoContent c ON c.nodeId = n.id
     JOIN umbracoContentVersion v ON v.nodeId = n.id AND v.current = 1
     JOIN umbracoPropertyData pd ON pd.versionId = v.id
     JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId AND pt.Alias = 'umbracoFile'
     WHERE n.nodeObjectType = 'B796F64C-1F99-4FFB-B886-4BF4BC011A9C'`
  )
  .all();
for (const r of rows) console.log(r.guid, "|", r.name, "|", r.varcharValue ?? r.textValue);
