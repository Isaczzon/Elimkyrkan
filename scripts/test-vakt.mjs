// Testar redaktörsvaktens logik lokalt med simulerade commits
import { readFileSync } from "node:fs";

const yml = readFileSync(".github/workflows/redaktorsvakt.yml", "utf8");

const cfg = {
  admins: ["Isaczzon"],
  gemensamt: ["images/uploads/**"],
  redaktorer: {
    "anna-svensson": ["src/verksamheter/*/barn.md"],
    "erik-larsson": ["src/handelser/**", "src/_data/pages/*/kalender.json"],
  },
};

// Samma logik som i workflowen
const admins = cfg.admins.map((s) => s.toLowerCase());
const gemensamt = cfg.gemensamt || [];
const redaktorer = Object.fromEntries(Object.entries(cfg.redaktorer).map(([k, v]) => [k.toLowerCase(), v]));
const bots = ["github-actions[bot]", "web-flow", "dependabot[bot]"];
const globTillRegex = (glob) =>
  new RegExp(
    "^" +
      glob
        .replace(/[.+^${}()|[\]\\]/g, "\\$&")
        .split("**").join("")
        .replace(/\*/g, "[^/]*")
        .split("").join(".*") +
      "$"
  );

const granska = (login, filer) => {
  const l = login.toLowerCase();
  if (bots.includes(l) || admins.includes(l)) return [];
  const tillatna = [...(redaktorer[l] || []), ...gemensamt].map(globTillRegex);
  return filer.filter((f) => !tillatna.some((re) => re.test(f)));
};

const fall = [
  ["anna redigerar sin sida (sv)", "Anna-Svensson", ["src/verksamheter/sv/barn.md"], 0],
  ["anna redigerar sin sida (th)", "anna-svensson", ["src/verksamheter/th/barn.md"], 0],
  ["anna laddar upp bild", "anna-svensson", ["src/verksamheter/ru/barn.md", "images/uploads/dop.jpg"], 0],
  ["anna redigerar fel sida", "anna-svensson", ["src/verksamheter/sv/mission.md"], 1],
  ["anna försöker ändra vaktens regler", "anna-svensson", [".github/redaktorer.json"], 1],
  ["erik skapar händelse", "erik-larsson", ["src/handelser/sv/sommarfest.md", "src/handelser/en/sommarfest.md"], 0],
  ["erik ändrar kalendersidans rubrik", "erik-larsson", ["src/_data/pages/ru/kalender.json"], 0],
  ["erik ändrar startsidan", "erik-larsson", ["src/_data/pages/sv/hem.json"], 1],
  ["okänd redaktör ändrar något", "ny-person", ["src/verksamheter/sv/barn.md"], 1],
  ["admin ändrar allt", "Isaczzon", [".github/redaktorer.json", "src/index.njk"], 0],
  ["vaktens egna återställningar", "github-actions[bot]", ["src/verksamheter/sv/mission.md"], 0],
];

let fel = 0;
for (const [namn, login, filer, forvantat] of fall) {
  const otillatna = granska(login, filer);
  const ok = otillatna.length === forvantat || (forvantat > 0 && otillatna.length > 0 && forvantat === 1);
  const exaktOk = forvantat === 0 ? otillatna.length === 0 : otillatna.length > 0;
  console.log(`${exaktOk ? "PASS" : "FAIL"}  ${namn}  (otillåtna: ${otillatna.length})`);
  if (!exaktOk) fel++;
}

// Säkerställ att workflow-filens inbäddade logik innehåller samma regexbygge
if (!yml.includes(".split('**').join('\\u0001')")) {
  console.log("FAIL  workflow-filen saknar korrekt globkonvertering");
  fel++;
}

process.exit(fel ? 1 : 0);
