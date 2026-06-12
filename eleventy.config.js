import { readFileSync } from "node:fs";
import { HtmlBasePlugin } from "@11ty/eleventy";
import CleanCSS from "clean-css";

// Måndag = 0 … Söndag = 6 (samma ordning som Umbraco-vyerna sorterade på)
const DAG_INDEX = {
  "måndag": 0, "tisdag": 1, "onsdag": 2, "torsdag": 3, "fredag": 4, "lördag": 5, "söndag": 6,
  "monday": 0, "tuesday": 1, "wednesday": 2, "thursday": 3, "friday": 4, "saturday": 5, "sunday": 6,
};

const isoVecka = (date) => {
  const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
  const dayNum = d.getUTCDay() || 7;
  d.setUTCDate(d.getUTCDate() + 4 - dayNum);
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d - yearStart) / 86400000 + 1) / 7);
};

const matcharIsoVecka = (vecka, typ) => {
  switch ((typ || "").toLowerCase()) {
    case "weekly": return true;
    case "even weeks": return vecka % 2 === 0;
    case "odd weeks": return vecka % 2 === 1;
    case "every third week": return vecka % 3 === 0;
    default: return false;
  }
};

const startMinuter = (tid) => {
  if (!tid) return Number.MAX_SAFE_INTEGER;
  const m = String(tid).trim().match(/^(\d{1,2}):(\d{2})/);
  if (!m) return Number.MAX_SAFE_INTEGER;
  return parseInt(m[1], 10) * 60 + parseInt(m[2], 10);
};

const datumAv = (e) => {
  const d = e.data.datum;
  if (!d) return null;
  const dt = new Date(d);
  return Number.isNaN(dt.getTime()) ? null : dt;
};

const idagDatum = () => {
  const n = new Date();
  return new Date(n.getFullYear(), n.getMonth(), n.getDate());
};

const dagOrdning = (e) => {
  const d = datumAv(e);
  if (d) return (d.getDay() + 6) % 7;
  return DAG_INDEX[(e.data.dag || "").toLowerCase()] ?? 99;
};

const datumNyckel = (d) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

const tillDatumStrang = (v) => (v instanceof Date ? v.toISOString().slice(0, 10) : String(v).slice(0, 10));

// Uppehåll: återkommande pass döljs mellan uppehall_fran och uppehall_till (inklusive)
const iUppehall = (e, d) => {
  if (!e.data.uppehall_fran) return false;
  const dag = datumNyckel(d);
  if (dag < tillDatumStrang(e.data.uppehall_fran)) return false;
  return !e.data.uppehall_till || dag <= tillDatumStrang(e.data.uppehall_till);
};

export default function (eleventyConfig) {
  eleventyConfig.addPassthroughCopy("images");
  eleventyConfig.addPassthroughCopy("docs");
  eleventyConfig.addPassthroughCopy({ "src/admin": "admin" });
  eleventyConfig.addPassthroughCopy({ "src/favicon.svg": "favicon.svg" });
  eleventyConfig.addPassthroughCopy({ "src/js": "js" });
  eleventyConfig.ignores.add("src/css/**");
  eleventyConfig.addWatchTarget("src/css/");

  // CSS:en är liten (~6 KB gzippad) — minifieras och inlineas i <head>,
  // vilket tar bort en renderblockerande nätverksrunda
  eleventyConfig.addGlobalData("inlineCss", () =>
    new CleanCSS({}).minify(readFileSync("src/css/site.css", "utf8")).styles
  );
  eleventyConfig.ignores.add("src/admin/**");

  // Gör att alla länkar fungerar även när sajten ligger under /Elimkyrkan/ på GitHub Pages
  eleventyConfig.addPlugin(HtmlBasePlugin);

  eleventyConfig.addGlobalData("aret", () => new Date().getFullYear());

  eleventyConfig.addFilter("medLang", (arr, lang) => (arr || []).filter((p) => p.data.lang === lang));

  eleventyConfig.addFilter("sorteraOrdning", (arr) =>
    [...(arr || [])].sort((a, b) => (a.data.ordning ?? 99) - (b.data.ordning ?? 99))
  );

  eleventyConfig.addFilter("lokalFor", (locales, kod) => locales.find((l) => l.code === kod) || locales[0]);

  eleventyConfig.addFilter("bytLokal", (pageUrl, franPrefix, tillPrefix) => {
    let rest = String(pageUrl || "/").replace(/^\//, "");
    if (franPrefix && rest.startsWith(franPrefix)) rest = rest.slice(franPrefix.length);
    return "/" + tillPrefix + rest;
  });

  eleventyConfig.addFilter("nl2br", (s) =>
    String(s || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\n/g, "<br>")
  );

  eleventyConfig.addFilter("datumLokal", (d, kod) =>
    new Intl.DateTimeFormat(kod, { day: "numeric", month: "long", year: "numeric" }).format(new Date(d))
  );

  // Veckodagsetikett: datum → lokaliserat dagnamn; annars översätts dropdown-värdet (sv dagnamn)
  eleventyConfig.addFilter("dagEtikett", (e, kod) => {
    const d = datumAv(e);
    let index;
    if (d) {
      index = (d.getDay() + 6) % 7;
    } else {
      index = DAG_INDEX[(e.data.dag || "").toLowerCase()];
      if (index === undefined) return e.data.dag || "";
    }
    // 2024-01-01 är en måndag; ger oss ett referensdatum per veckodag
    const ref = new Date(Date.UTC(2024, 0, 1 + index));
    const namn = new Intl.DateTimeFormat(kod, { weekday: "long", timeZone: "UTC" }).format(ref);
    return namn.charAt(0).toUpperCase() + namn.slice(1);
  });

  // Veckoschemat: händelser som infaller den här veckan (mån–sön)
  eleventyConfig.addFilter("veckansPass", (arr) => {
    const idag = idagDatum();
    const delta = (idag.getDay() + 6) % 7;
    const veckoStart = new Date(idag); veckoStart.setDate(idag.getDate() - delta);
    const veckoSlut = new Date(veckoStart); veckoSlut.setDate(veckoStart.getDate() + 6);
    const vecka = isoVecka(idag);

    return (arr || [])
      .filter((e) => {
        const typ = e.data.typ || "";
        if (!typ || typ.toLowerCase() === "monthly") return false;
        const d = datumAv(e);
        if (d) return d >= veckoStart && d <= veckoSlut;
        if (!matcharIsoVecka(vecka, typ)) return false;
        const dagIndex = DAG_INDEX[(e.data.dag || "").toLowerCase()];
        if (dagIndex === undefined) return true;
        const forekomst = new Date(veckoStart);
        forekomst.setDate(veckoStart.getDate() + dagIndex);
        return !iUppehall(e, forekomst);
      })
      .sort((a, b) => dagOrdning(a) - dagOrdning(b) || startMinuter(a.data.tid) - startMinuter(b.data.tid));
  });

  // "Vad som händer": alla händelser i innevarande kalendermånad,
  // plus stående händelser utan datum (t.ex. Öppet café)
  eleventyConfig.addFilter("manadsHandelser", (arr) => {
    const idag = idagDatum();
    const manad = datumNyckel(idag).slice(0, 7);
    return (arr || [])
      .filter((e) => (e.data.typ || "").toLowerCase() === "monthly")
      .filter((e) => !e.data.datum || tillDatumStrang(e.data.datum).slice(0, 7) === manad)
      .sort((a, b) => {
        const da = datumAv(a)?.getTime() ?? Number.MAX_SAFE_INTEGER;
        const db = datumAv(b)?.getTime() ?? Number.MAX_SAFE_INTEGER;
        return da - db || startMinuter(a.data.tid) - startMinuter(b.data.tid);
      });
  });

  // Kalendersidan: karta datum → händelser, med återkommande pass utrullade −2…+13 månader
  eleventyConfig.addFilter("kalenderKarta", (arr) => {
    const idag = idagDatum();
    const start = new Date(idag); start.setMonth(start.getMonth() - 2);
    const slut = new Date(idag); slut.setMonth(slut.getMonth() + 13);

    const poster = [];
    for (const e of arr || []) {
      const d = datumAv(e);
      if (d) {
        poster.push([d, e]);
        continue;
      }
      const dagIndex = DAG_INDEX[(e.data.dag || "").toLowerCase()];
      if (dagIndex === undefined) continue;
      for (let dt = new Date(start); dt <= slut; dt.setDate(dt.getDate() + 1)) {
        if ((dt.getDay() + 6) % 7 !== dagIndex) continue;
        if (!matcharIsoVecka(isoVecka(dt), e.data.typ || "")) continue;
        if (iUppehall(e, dt)) continue;
        poster.push([new Date(dt), e]);
      }
    }

    const karta = {};
    for (const [d, e] of poster) {
      const nyckel = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
      (karta[nyckel] ||= []).push({
        title: e.data.titel || "",
        time: e.data.tid || "",
        desc: e.data.beskrivning || "",
        link: e.data.lank || "",
        _sort: startMinuter(e.data.tid),
      });
    }
    for (const nyckel of Object.keys(karta)) {
      karta[nyckel].sort((a, b) => a._sort - b._sort);
      karta[nyckel].forEach((p) => delete p._sort);
    }
    return karta;
  });

  // Delar upp renderat innehåll vid ELIM-BLOCKS-markören (samma mekanism som Umbraco-vyerna)
  eleventyConfig.addFilter("delaBlock", (content) => {
    const html = String(content || "");
    const reversedMark = "<!-- ELIM-BLOCKS-REVERSED -->";
    const mark = "<!-- ELIM-BLOCKS -->";
    let idx = html.indexOf(reversedMark);
    if (idx >= 0) {
      return { pre: html.slice(0, idx), post: html.slice(idx + reversedMark.length), omvand: true, marker: true };
    }
    idx = html.indexOf(mark);
    if (idx >= 0) {
      return { pre: html.slice(0, idx), post: html.slice(idx + mark.length), omvand: false, marker: true };
    }
    return { pre: html, post: "", omvand: false, marker: false };
  });

  eleventyConfig.addFilter("youtubeId", (url) => {
    const m = String(url || "").match(/(?:youtube\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/)|youtu\.be\/)([A-Za-z0-9_-]{11})/);
    return m ? m[1] : "";
  });

  eleventyConfig.addFilter("omvant", (arr, ja) => (ja ? [...(arr || [])].reverse() : arr || []));

  return {
    dir: {
      input: "src",
      includes: "_includes",
    },
    pathPrefix: process.env.PATH_PREFIX || "/",
  };
}
