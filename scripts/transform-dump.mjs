// Engångsmigrering: genererar Eleventy-innehållsfiler (alla 5 språk) från
// Umbraco-dumpen (scripts/dump-umbraco.mjs). Körs från repo-roten:
//   node scripts/dump-umbraco.mjs <db> %TEMP%/umbraco-dump.json
//   node scripts/transform-dump.mjs %TEMP%/umbraco-dump.json
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import YAML from "yaml";

const dump = JSON.parse(readFileSync(process.argv[2], "utf8"));
const ROOT = new URL("..", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const SRC = join(ROOT, "src");

const LOCS = [
  ["sv", "sv-SE"],
  ["en", "en-US"],
  ["uk", "uk-UA"],
  ["es", "es-ES"],
  ["th", "th-TH"],
];
const PREFIX = { sv: "", en: "en/", uk: "uk/", es: "es/", th: "th/" };

// mediaKey (gemener) -> bildfil under /images
const MEDIA = {
  "4295b42d-8eac-4a9d-8991-723f04bd9562": "Activities.png",
  "c04e8eb0-1fcb-48c7-aca0-db65b71348e3": "Axplocket_book_area.png",
  "2be61ece-3aff-42fb-b273-418e6c2002af": "Axplocket_decoration_area.png",
  "c4d6f8eb-842a-465e-ad12-c3cb7096efc9": "Axplocket_entry.png",
  "45d0d588-693b-400a-bb26-4ee473eb21a0": "Axplocket_open_area.png",
  "ca29315e-9ed4-4728-8bb8-6c92beae1231": "Axplocket_tableware_and_music_area.png",
  "2ae6fa9b-9a4a-453e-8287-2381b08125e3": "Axplocket_technical_area.png",
  "7abff9f1-6d73-4f72-90f7-c0f66539f13a": "Axplocket_toys_area.png",
  "8ea49797-0fa7-40e5-9d5f-fcca855d015d": "Axplocket_warm_area.png",
  "076f29f9-7c5f-4de2-a41d-f6b592b3c358": "Calendar.png",
  "a8b25bcf-572b-4f4e-a42c-c0b9515003d1": "Children_playing.png",
  "b9bce295-8c04-4c8c-92ff-2a1016085d26": "Contact.png",
  "2fe765b2-2223-4e21-9c5f-87adcfb45d6a": "Elimkyrkan_bulding.png",
  "14dbe11c-aed8-4e51-ab40-1fc154148f61": "Home_group.png",
  "3caad0c1-943b-4481-8380-e66459cbebad": "Mens_breakfast.png",
  "3cd92057-891c-48de-bd70-f2df7329263a": "Missionary_work.png",
  "4d176ff8-cc7e-4a63-a51e-9b99c53f0a6b": "Next_Generation.png",
  "138ddb21-4091-4512-add9-801c6e79c513": "People_talking_inside_church.png",
  "f1c3bfcc-8e9e-477d-a25e-bc15f14761c9": "Prayer.png",
  "d0382973-0e66-45b0-a0da-221efb316b0b": "Sermon.png",
  "14c2bd00-f3fb-4fc1-ab1d-3a59660f4ca1": "Teachings.png",
  "19a703e5-8e64-4538-bd9d-756749e1442c": "Teaching_parents.png",
  "a1ccb94e-0f22-4cec-b5c0-8a9c87b93609": "Thuseday_cafe.png",
};

const byId = Object.fromEntries(dump.map((n) => [n.id, n]));
const node = (id) => byId[id];

const val = (n, alias, iso) =>
  n.properties[alias]?.[iso] ?? n.properties[alias]?.["sv-SE"] ?? n.properties[alias]?.inv ?? "";
const inv = (n, alias) => n.properties[alias]?.inv ?? "";
const namn = (n, iso) => (n.names[iso] ?? n.names["sv-SE"] ?? n.name).replace(/\s*\(\d+\)\s*$/, "");

const bild = (n) => {
  const raw = inv(n, "heroImage");
  if (!raw) return "";
  try {
    const key = JSON.parse(raw)[0]?.mediaKey?.toLowerCase();
    return MEDIA[key] ? `/images/${MEDIA[key]}` : "";
  } catch {
    return "";
  }
};

const slugify = (s) =>
  s
    .replace(/\s*\(\d+\)\s*$/, "")
    .toLowerCase()
    .replace(/[åä]/g, "a")
    .replace(/ö/g, "o")
    .replace(/é/g, "e")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");

const dropList = (raw) => {
  // Umbraco.DropDown.Flexible lagrar '["Värde"]'
  if (!raw) return "";
  try {
    const p = JSON.parse(raw);
    return Array.isArray(p) ? p[0] ?? "" : String(p);
  } catch {
    return String(raw);
  }
};

const blocks = (n) => {
  const raw = inv(n, "contentBlocks");
  if (!raw) return [];
  const j = JSON.parse(raw);
  return j.contentData.map((b) => {
    const o = {};
    for (const v of b.values) o[v.alias] = v.value ?? "";
    return o;
  });
};

const sortBlocks = (list) => {
  const out = { missionLander: [], bibelverser: [], principer: [], galleri: [], videor: [], resurskort: [] };
  for (const b of list) {
    if ("countryName" in b)
      out.missionLander.push({ namn: b.countryName, flagga: b.flagUrl, beskrivning: b.description, gavomarkning: b.giftMark });
    else if ("reference" in b) out.bibelverser.push({ referens: b.reference, text: b.verseText });
    else if ("imageUrl" in b) out.galleri.push({ bild: b.imageUrl, text: b.caption });
    else if ("youtubeUrl" in b) out.videor.push({ titel: b.title, url: b.youtubeUrl, beskrivning: b.description });
    else if ("ctaUrl" in b)
      out.resurskort.push({ ikon: b.icon, titel: b.title, beskrivning: b.description, lank: b.ctaUrl, lanktext: b.ctaLabel });
    else out.principer.push({ ikon: b.icon, titel: b.title });
  }
  return out;
};

const writeJson = (rel, data) => {
  const p = join(SRC, rel);
  mkdirSync(join(p, ".."), { recursive: true });
  writeFileSync(p, JSON.stringify(data, null, 2) + "\n");
};
const writeMd = (rel, front, body) => {
  const p = join(SRC, rel);
  mkdirSync(join(p, ".."), { recursive: true });
  const clean = Object.fromEntries(Object.entries(front).filter(([, v]) => v !== "" && v !== undefined && v !== null));
  writeFileSync(p, `---\n${YAML.stringify(clean)}---\n${body ? body + "\n" : ""}`);
};

// ---------- Hämta noder ----------
const HOME = dump.find((n) => n.contentType === "home");
const SETTINGS = dump.find((n) => n.contentType === "siteSettings");
const ABOUT = dump.find((n) => n.contentType === "aboutPage");
const ACTPAGE = dump.find((n) => n.contentType === "activitiesPage");
const CALPAGE = dump.find((n) => n.contentType === "calendarPage");
const TEACHPAGE = dump.find((n) => n.contentType === "teachingPage");
const CONTACT = dump.find((n) => n.contentType === "contactPage");
const ACTIVITIES = dump.filter((n) => n.contentType === "activity").sort((a, b) => a.sortOrder - b.sortOrder);
const EVENTS = dump.filter((n) => n.contentType === "event").sort((a, b) => a.sortOrder - b.sortOrder);
const RESOURCES = dump.filter((n) => n.contentType === "teachingResource").sort((a, b) => a.sortOrder - b.sortOrder);

// Undervisningskorten var hårdkodade i TeachingPage.cshtml – samma texter, alla språk.
const TEACH_CARDS = {
  sv: [
    { stil: "predikan", titel: "Predikan", stycken: ["Varje söndag förmedlas Guds ord genom predikan. Våra predikanter delar aktuella och livsförvandlande budskap som hjälper dig att växa i tro och vardagsliv.", "Här kan du lyssna på tidigare predikningar och följa med i aktuella predikoserier."], lanktext: "Lyssna på predikningar →", resurs: "predikan" },
    { stil: "foraldrar", titel: "Föräldrar", stycken: ["Att vara förälder är livets viktigaste uppdrag. Vi erbjuder undervisning och resurser som hjälper dig att bygga en trygg och kärleksfull familj grundad i tro.", "Kurser, samtalsgrupper och material för föräldrar i alla faser."], lanktext: "Läs mer om föräldraresurser →", resurs: "foraldrar" },
    { stil: "hemgrupper", titel: "Hemgrupper", stycken: ["I hemgrupperna fördjupar vi oss i Bibeln tillsammans. Genom studium, samtal och bön växer vi i förståelse och tro – och stöttar varandra på vägen.", "Aktuellt studiematerial och information om hemgruppernas undervisningsplan."], lanktext: "Studiematerial och länkar →", resurs: "hemgrupper" },
  ],
  en: [
    { stil: "predikan", titel: "Sermon", stycken: ["Every Sunday God's word is shared through the sermon. Our preachers deliver timely, life-changing messages that help you grow in faith and everyday life.", "Listen to previous sermons and follow current sermon series here."], lanktext: "Listen to sermons →", resurs: "predikan" },
    { stil: "foraldrar", titel: "Parents", stycken: ["Being a parent is life's most important task. We offer teaching and resources to help you build a safe, loving family grounded in faith.", "Courses, discussion groups and material for parents at all stages."], lanktext: "Read more about parent resources →", resurs: "foraldrar" },
    { stil: "hemgrupper", titel: "Home Groups", stycken: ["In home groups we go deeper into the Bible together. Through study, conversation and prayer we grow in understanding and faith – and support one another along the way.", "Current study material and information about the home groups' teaching plan."], lanktext: "Study material and links →", resurs: "hemgrupper" },
  ],
  uk: [
    { stil: "predikan", titel: "Проповідь", stycken: ["Щонеділі Боже слово передається через проповідь. Наші проповідники діляться актуальними посланнями, що змінюють життя.", "Тут ви можете слухати попередні проповіді та слідкувати за поточними серіями."], lanktext: "Слухати проповіді →", resurs: "predikan" },
    { stil: "foraldrar", titel: "Батьки", stycken: ["Бути батьком – найважливіше завдання життя. Ми пропонуємо навчання та ресурси для побудови безпечної, люблячої родини на основі віри.", "Курси, дискусійні групи та матеріали для батьків на всіх етапах."], lanktext: "Дізнатися більше про ресурси для батьків →", resurs: "foraldrar" },
    { stil: "hemgrupper", titel: "Домашні групи", stycken: ["У домашніх групах ми разом заглиблюємось у Біблію. Через вивчення, бесіди та молитву ми зростаємо у розумінні та вірі.", "Поточні навчальні матеріали та інформація про навчальний план домашніх груп."], lanktext: "Навчальні матеріали та посилання →", resurs: "hemgrupper" },
  ],
  es: [
    { stil: "predikan", titel: "Predicación", stycken: ["Cada domingo se comparte la palabra de Dios a través de la predicación. Nuestros predicadores ofrecen mensajes actuales y transformadores.", "Aquí puedes escuchar sermones anteriores y seguir las series actuales."], lanktext: "Escuchar sermones →", resurs: "predikan" },
    { stil: "foraldrar", titel: "Padres", stycken: ["Ser padre o madre es la tarea más importante de la vida. Ofrecemos enseñanza y recursos para construir una familia segura y amorosa basada en la fe.", "Cursos, grupos de conversación y material para padres en todas las etapas."], lanktext: "Más sobre recursos para padres →", resurs: "foraldrar" },
    { stil: "hemgrupper", titel: "Grupos en casa", stycken: ["En los grupos en casa profundizamos juntos en la Biblia. Mediante estudio, conversación y oración crecemos en entendimiento y fe.", "Material de estudio actual e información sobre el plan de enseñanza de los grupos en casa."], lanktext: "Material y enlaces →", resurs: "hemgrupper" },
  ],
  th: [
    { stil: "predikan", titel: "คำเทศนา", stycken: ["ทุกวันอาทิตย์ พระวจนะของพระเจ้าถูกถ่ายทอดผ่านคำเทศนา ผู้เทศน์ของเราแบ่งปันข้อความที่ทันสมัยและเปลี่ยนแปลงชีวิต", "ที่นี่คุณสามารถฟังคำเทศนาเก่าและติดตามชุดคำเทศนาปัจจุบันได้"], lanktext: "ฟังคำเทศนา →", resurs: "predikan" },
    { stil: "foraldrar", titel: "พ่อแม่", stycken: ["การเป็นพ่อแม่คือภารกิจที่สำคัญที่สุดในชีวิต เรามีการสอนและทรัพยากรที่ช่วยให้คุณสร้างครอบครัวที่ปลอดภัยและเปี่ยมด้วยความรักบนพื้นฐานของความเชื่อ", "หลักสูตร กลุ่มสนทนา และเนื้อหาสำหรับพ่อแม่ในทุกช่วงวัย"], lanktext: "อ่านเพิ่มเติมเกี่ยวกับทรัพยากรสำหรับพ่อแม่ →", resurs: "foraldrar" },
    { stil: "hemgrupper", titel: "กลุ่มในบ้าน", stycken: ["ในกลุ่มในบ้าน เราเจาะลึกพระคัมภีร์ด้วยกัน ผ่านการศึกษา การสนทนา และการอธิษฐาน เราเติบโตในความเข้าใจและความเชื่อ", "เนื้อหาการศึกษาปัจจุบันและข้อมูลเกี่ยวกับแผนการสอนของกลุ่มในบ้าน"], lanktext: "เนื้อหาและลิงก์ →", resurs: "hemgrupper" },
  ],
};

// ---------- Generera ----------
for (const [loc, iso] of LOCS) {
  // Sidor
  writeJson(`_data/pages/${loc}/hem.json`, {
    navnamn: namn(HOME, iso),
    hero: { rubrik: val(HOME, "heroTitle", iso), undertitel: val(HOME, "heroSubtitle", iso), knapptext: val(HOME, "heroButtonText", iso), bild: bild(HOME) },
    schema: { rubrik: val(HOME, "scheduleHeading", iso), undertitel: val(HOME, "scheduleSubheading", iso) },
    handelser: { rubrik: val(HOME, "eventsHeading", iso), undertitel: val(HOME, "eventsSubheading", iso) },
    cta: { rubrik: val(HOME, "ctaHeading", iso), text: val(HOME, "ctaText", iso), knapptext: val(HOME, "ctaButtonText", iso) },
  });
  writeJson(`_data/pages/${loc}/omoss.json`, {
    navnamn: namn(ABOUT, iso),
    hero: { rubrik: val(ABOUT, "heroTitle", iso), undertitel: val(ABOUT, "heroSubtitle", iso), bild: bild(ABOUT) },
    body: val(ABOUT, "mainContent", iso),
  });
  writeJson(`_data/pages/${loc}/verksamheter.json`, {
    navnamn: namn(ACTPAGE, iso),
    hero: { rubrik: val(ACTPAGE, "heroTitle", iso), undertitel: val(ACTPAGE, "heroSubtitle", iso), bild: bild(ACTPAGE) },
  });
  writeJson(`_data/pages/${loc}/kalender.json`, {
    navnamn: namn(CALPAGE, iso),
    hero: { rubrik: val(CALPAGE, "heroTitle", iso), undertitel: val(CALPAGE, "heroSubtitle", iso), bild: bild(CALPAGE) },
  });
  writeJson(`_data/pages/${loc}/undervisning.json`, {
    navnamn: namn(TEACHPAGE, iso),
    hero: { rubrik: val(TEACHPAGE, "heroTitle", iso), undertitel: val(TEACHPAGE, "heroSubtitle", iso), bild: bild(TEACHPAGE) },
    kort: TEACH_CARDS[loc],
  });
  writeJson(`_data/pages/${loc}/kontakt.json`, {
    navnamn: namn(CONTACT, iso),
    hero: { rubrik: val(CONTACT, "heroTitle", iso), undertitel: val(CONTACT, "heroSubtitle", iso), bild: bild(CONTACT) },
    adress: inv(CONTACT, "address"),
    telefon: inv(CONTACT, "phone"),
    epost: inv(CONTACT, "email"),
    pastor: inv(CONTACT, "pastorName"),
  });

  // Webbplatsinställningar
  writeJson(`_data/sajt/${loc}.json`, {
    logotopp: inv(SETTINGS, "logoTopText"),
    logobotten: inv(SETTINGS, "logoBottomText"),
    tagline: val(SETTINGS, "footerTagline", iso),
    adress: inv(SETTINGS, "footerAddress"),
    telefon: inv(SETTINGS, "footerPhone"),
    epost: inv(SETTINGS, "footerEmail"),
    copyright: val(SETTINGS, "copyrightSuffix", iso),
    formspree_id: "DITT_FORMSPREE_ID",
  });

  // Händelser (kalender)
  for (const ev of EVENTS) {
    const slug = slugify(ev.name);
    const datum = inv(ev, "eventDate") ? String(inv(ev, "eventDate")).slice(0, 10) : "";
    writeMd(`handelser/${loc}/${slug}.md`, {
      titel: val(ev, "title", iso),
      beskrivning: val(ev, "description", iso),
      typ: dropList(inv(ev, "eventType")),
      dag: dropList(inv(ev, "dayOfWeek")),
      tid: inv(ev, "time"),
      datum,
      ikon: inv(ev, "icon"),
      lank: inv(ev, "linkUrl"),
    });
  }

  // Verksamheter
  for (const act of ACTIVITIES) {
    const slug = slugify(act.names["sv-SE"] ?? act.name);
    const b = sortBlocks(blocks(act));
    writeMd(
      `verksamheter/${loc}/${slug}.md`,
      {
        titel: namn(act, iso),
        undertitel: val(act, "heroSubtitle", iso),
        ikon: inv(act, "icon"),
        kort: val(act, "shortDescription", iso),
        bild: bild(act),
        ordning: act.sortOrder,
        ...(b.missionLander.length ? { missionLander: b.missionLander } : {}),
        ...(b.bibelverser.length ? { bibelverser: b.bibelverser } : {}),
        ...(b.principer.length ? { principer: b.principer } : {}),
        ...(b.galleri.length ? { galleri: b.galleri } : {}),
        ...(b.resurskort.length ? { resurskort: b.resurskort } : {}),
      },
      val(act, "mainContent", iso)
    );
  }

  // Undervisningsresurser
  for (const res of RESOURCES) {
    const slug = slugify(res.names["sv-SE"] ?? res.name);
    const b = sortBlocks(blocks(res));
    writeMd(
      `undervisningsresurser/${loc}/${slug}.md`,
      {
        titel: namn(res, iso),
        rubrik: val(res, "heroTitle", iso),
        undertitel: val(res, "heroSubtitle", iso),
        bild: bild(res),
        ...(b.videor.length ? { videor: b.videor } : {}),
        ...(b.resurskort.length ? { resurskort: b.resurskort } : {}),
      },
      val(res, "mainContent", iso)
    );
  }

  // Katalogdatafiler per språk
  writeJson(`handelser/${loc}/${loc}.json`, { lang: loc });
  writeJson(`verksamheter/${loc}/${loc}.json`, { lang: loc, permalink: `/${PREFIX[loc]}verksamheter/{{ page.fileSlug }}/` });
  writeJson(`undervisningsresurser/${loc}/${loc}.json`, { lang: loc, permalink: `/${PREFIX[loc]}undervisning/{{ page.fileSlug }}/` });
}

// Gemensamma katalogdatafiler
writeJson(`handelser/handelser.json`, { tags: "handelse", permalink: false, eleventyExcludeFromCollections: false });
writeJson(`verksamheter/verksamheter.json`, { tags: "verksamhet", layout: "verksamhet.njk", aktiv: "verksamheter" });
writeJson(`undervisningsresurser/undervisningsresurser.json`, { tags: "undervisningsresurs", layout: "undervisningsresurs.njk", aktiv: "undervisning" });

console.log("Klart – innehållsfiler genererade för", LOCS.map(([l]) => l).join(", "));
