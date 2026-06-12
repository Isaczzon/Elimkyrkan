import { HtmlBasePlugin } from "@11ty/eleventy";

const MANADER = [
  "januari", "februari", "mars", "april", "maj", "juni",
  "juli", "augusti", "september", "oktober", "november", "december",
];

export default function (eleventyConfig) {
  eleventyConfig.addPassthroughCopy("images");
  eleventyConfig.addPassthroughCopy("docs");
  eleventyConfig.addPassthroughCopy({ "src/admin": "admin" });
  eleventyConfig.ignores.add("src/admin/**");
  eleventyConfig.addPassthroughCopy({ "src/css": "css" });
  eleventyConfig.addPassthroughCopy({ "src/js": "js" });

  // Gör att alla länkar fungerar även när sajten ligger under /Elimkyrkan/ på GitHub Pages
  eleventyConfig.addPlugin(HtmlBasePlugin);

  eleventyConfig.addFilter("svDatum", (value) => {
    const d = new Date(value);
    return `${d.getUTCDate()} ${MANADER[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
  });

  // Kommande händelser: dagens datum och framåt, tidigast först
  eleventyConfig.addCollection("kommande", (collectionApi) => {
    const idag = new Date();
    idag.setUTCHours(0, 0, 0, 0);
    return collectionApi
      .getFilteredByTag("kalender")
      .filter((e) => e.data.datum && new Date(e.data.datum) >= idag)
      .sort((a, b) => new Date(a.data.datum) - new Date(b.data.datum));
  });

  eleventyConfig.addCollection("verksamheterSorterade", (collectionApi) =>
    collectionApi
      .getFilteredByTag("verksamhet")
      .sort((a, b) => (a.data.ordning ?? 99) - (b.data.ordning ?? 99))
  );

  return {
    dir: {
      input: "src",
      includes: "_includes",
    },
    pathPrefix: process.env.PATH_PREFIX || "/",
  };
}
