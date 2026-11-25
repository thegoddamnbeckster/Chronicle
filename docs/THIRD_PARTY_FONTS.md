# Bundled fonts

The score badges on an item's detail page can name one of four typefaces (`font_family` in a plugin manifest's `rating_sources`; see
`PLUGIN_DEVELOPMENT_GUIDE.md`, "Reporting scores"). They stand in for the proprietary faces the source sites use. All four are
open-licence, come from the `@fontsource` npm packages (Latin subset, weights 400, 600 and 700, woff2) and are bundled into the web build,
so the browser never fetches a font from anywhere else.

| Face | Package (version) | Licence | Copyright |
|------|-------------------|---------|-----------|
| Roboto | `@fontsource/roboto` 5.3.0 | SIL Open Font License 1.1 | 2011 The Roboto Project Authors (github.com/googlefonts/roboto-classic) |
| Source Sans 3 | `@fontsource/source-sans-3` 5.3.1 | SIL Open Font License 1.1 | Google Inc. (as named in the package's LICENSE file, which carries no fuller notice) |
| Libre Franklin | `@fontsource/libre-franklin` 5.3.0 | SIL Open Font License 1.1 | 2020 The Libre Franklin Project Authors (github.com/googlefonts/Libre-Franklin) |
| Nunito Sans | `@fontsource/nunito-sans` 5.3.0 | SIL Open Font License 1.1 | 2016 The Nunito Sans Project Authors (github.com/Fonthausen/NunitoSans) |

The full licence text ships in each package's `LICENSE` file (`src/Chronicle.Web/node_modules/@fontsource/<face>/LICENSE`).
The OFL permits bundling and redistribution with software; the fonts are not sold on their own and keep their names.

The list of allowed faces is fixed in `ItemScores` (`Fonts`) and imported in `ScoresPanel.tsx`; a manifest cannot add a face or supply a
font file or address. A face added later needs its licence checked and recorded here first.
