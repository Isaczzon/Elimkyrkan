# Elimkyrkan Mantorp – webbplats

Statisk webbplats byggd med [Eleventy](https://www.11ty.dev/), publicerad på **GitHub Pages** och redigerbar via **[Sveltia CMS](https://github.com/sveltia/sveltia-cms)** på `/admin/`.

Sajten är en portering av den tidigare Umbraco-versionen (sparad på grenen `Elim-Umbraco`) med samma design och funktioner: bilder, mörkt läge, fem språk (svenska, engelska, ryska, spanska, thai), interaktiv kalender, undervisningssidor med videor samt kontaktformulär.

## Så hänger det ihop

```
Redaktör → /admin/ (Sveltia CMS) → commit till GitHub → GitHub Actions bygger → GitHub Pages publicerar
```

- **Sidtexter** ligger i `src/_data/pages/{språk}/…` och `src/_data/sajt/{språk}.json`
- **Kalenderhändelser** ligger i `src/handelser/{språk}/…` – en fil per händelse. Återkommande pass (varje vecka, jämna/udda veckor, var tredje vecka) visas i veckoschemat och rullas ut i kalendern; månadshändelser med datum döljs automatiskt när datumet passerat.
- **Verksamheter** ligger i `src/verksamheter/{språk}/…` – en sida per verksamhet, med stöd för missionsländer, bibelverser, principlistor, bildgallerier och resurskort.
- **Undervisningsresurser** (Predikan, Föräldrar, Hemgrupper) ligger i `src/undervisningsresurser/{språk}/…` med YouTube-videor och resurskort.
- **Översättningar av fasta UI-texter** (knappar, etiketter, formulär) ligger i `src/_data/t.json`.
- **Mallar/design**: `src/_includes/`, `src/css/site.css` (inkl. mörkt läge), `src/js/site.js`.
- Bilder som laddas upp via CMS:et hamnar i `images/uploads/`.
- Sajten byggs om vid varje ändring **och varje natt kl 03:00 UTC**, så att veckoschema och kalender alltid är aktuella.

I CMS:et redigeras alla fem språken sida vid sida – Sveltia visar en flik per språk för varje fält som är översättningsbart.

## Köra lokalt

```bash
npm install
npm start        # http://localhost:8080
```

## Engångsinstallation (görs en gång av administratören)

### 1. Aktivera GitHub Pages

GitHub → repo **Settings → Pages → Source: GitHub Actions**. Nästa push bygger och publicerar sajten till `https://isaczzon.github.io/Elimkyrkan/`.

> **Eget domännamn?** Lägg till domänen under Settings → Pages och ändra `PATH_PREFIX` i `.github/workflows/deploy.yml` till `/`.

### 2. Formspree (kontaktformuläret)

1. Skapa ett gratis konto på [formspree.io](https://formspree.io) och skapa ett formulär (peka det mot `info@elimmantorp.se`).
2. Kopiera formulärets ID (t.ex. `xqkrwabc`) och skriv in det under **Inställningar** i CMS:et (eller direkt i `src/_data/sajt/*.json`).

Gratisnivån ger 50 meddelanden/månad. Formuläret har honeypot + enkel mattefråga som skräppostskydd, och Formspree filtrerar dessutom på sin sida.

### 3. Inloggning till CMS:et (OAuth-proxy)

Sveltia loggar in redaktörer via GitHub. GitHub Pages kan inte hantera OAuth-handskakningen, så en liten gratis Cloudflare Worker behövs (engångsuppgift, ~10 minuter):

1. Följ instruktionerna på [sveltia/sveltia-cms-auth](https://github.com/sveltia/sveltia-cms-auth) – det finns en "Deploy to Cloudflare Workers"-knapp.
2. Skapa en GitHub OAuth-app (Settings → Developer settings → OAuth Apps) enligt samma instruktion.
3. Skriv in Workerns URL som `base_url` i `src/admin/config.yml`.

### 4. Bjud in redaktörer

Gå till [Settings → Collaborators](https://github.com/Isaczzon/Elimkyrkan/settings/access), klicka **Add people** och ange redaktörens **e-postadress** (välj Write-behörighet). GitHub skickar en inbjudan via mejl – har personen inget GitHub-konto guidar inbjudningslänken dem genom att skapa ett. Därefter:

1. Gå till `https://isaczzon.github.io/Elimkyrkan/admin/`
2. Klicka **Sign in with GitHub**
3. Redigera och klicka **Spara** – ändringen syns på sajten efter någon minut.

## Innehållstyper i CMS:et

| Meny | Vad det styr |
|---|---|
| **Sidor** | Texterna på Hem, Om oss, Verksamheter, Kalender, Undervisning, Kontakt |
| **Kalenderhändelser** | Veckoschemat, "Vad som händer" och kalendersidan |
| **Verksamheter** | En sida per verksamhet (Barn, Hemgrupper, Mission, Loppis …) |
| **Undervisningsresurser** | Predikan/Föräldrar/Hemgrupper med videor och material |
| **Inställningar** | Logotyp, sidfot, adress, Formspree-ID |

## Grenar

- `main` – den här Eleventy/Sveltia-versionen (publiceras till Pages)
- `Elim-Umbraco` – den tidigare Umbraco-versionen, sparad för referens. Allt innehåll (alla fem språk) migrerades därifrån med `scripts/dump-umbraco.mjs` + `scripts/transform-dump.mjs`.
