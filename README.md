# Elimkyrkan Mantorp – webbplats

Statisk webbplats byggd med [Eleventy](https://www.11ty.dev/), publicerad på **GitHub Pages** och redigerbar via **[Sveltia CMS](https://github.com/sveltia/sveltia-cms)** på `/admin/`.

## Så hänger det ihop

```
Redaktör → /admin/ (Sveltia CMS) → commit till GitHub → GitHub Actions bygger → GitHub Pages publicerar
```

- **Innehåll** ligger i `src/_data/` (sidtexter, veckoschema, kontaktuppgifter) samt `src/kalender/` och `src/verksamheter/` (en fil per händelse/verksamhet).
- **Mallar/design** ligger i `src/_includes/`, `src/css/` och `src/js/`.
- **Bilder** som laddas upp via CMS:et hamnar i `images/uploads/`.
- Sajten byggs om automatiskt vid varje ändring **och varje natt kl 03:00 UTC**, så att passerade kalenderhändelser försvinner av sig själva.

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
2. Kopiera formulärets ID (t.ex. `xqkrwabc`) och skriv in det i `src/_data/site.json` under `formspree_id` (går även att göra i CMS:et under **Inställningar**).

Gratisnivån ger 50 meddelanden/månad, vilket räcker gott för en församlingssida.

### 3. Inloggning till CMS:et (OAuth-proxy)

Sveltia loggar in redaktörer via GitHub. GitHub Pages kan inte hantera OAuth-handskakningen, så en liten gratis Cloudflare Worker behövs (engångsuppgift, ~10 minuter):

1. Följ instruktionerna på [sveltia/sveltia-cms-auth](https://github.com/sveltia/sveltia-cms-auth) – det finns en "Deploy to Cloudflare Workers"-knapp.
2. Skapa en GitHub OAuth-app (Settings → Developer settings → OAuth Apps) enligt samma instruktion.
3. Skriv in Workerns URL som `base_url` i `src/admin/config.yml`.

### 4. Bjud in redaktörer

Varje redaktör behöver ett (gratis) GitHub-konto och inbjudan som **collaborator** med write-behörighet till detta repo (Settings → Collaborators). Därefter:

1. Gå till `https://isaczzon.github.io/Elimkyrkan/admin/`
2. Klicka **Sign in with GitHub**
3. Redigera och klicka **Spara** – ändringen syns på sajten efter någon minut.

## Innehållstyper i CMS:et

| Meny | Vad det styr |
|---|---|
| **Sidor** | Texterna på Hem, Om oss, Verksamheter, Undervisning, Kontakt |
| **Veckoschema** | Korten under "Veckoschema" på startsidan |
| **Kalender** | Kommande händelser (döljs automatiskt efter sitt datum) |
| **Verksamheter** | En sida per verksamhet (Barn, Hemgrupper, Loppis …) |
| **Inställningar** | Adress, telefon, e-post, Formspree-ID, kartlänk |

## Grenar

- `main` – den här Eleventy/Sveltia-versionen (publiceras till Pages)
- `Elim-Umbraco` – den tidigare Umbraco-prototypen, sparad för referens
- Den ursprungliga statiska prototypen finns kvar som `index - Feedback.html` och i git-historiken.
