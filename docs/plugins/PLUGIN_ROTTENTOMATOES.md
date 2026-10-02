# Chronicle.Plugin.RottenTomatoes — Rejected

**Status:** Rejected 2026-10-01. Do not implement.

## Why

Rotten Tomatoes has no usable API. The old `api.rottentomatoes.com/api/public/v1.0` partner API
is gone; RT data is available only through a commercial Fandango data licence.

The website itself can be read by a script: title pages embed their scores as JSON, and RT's
search runs on a public Algolia index. But RT's Terms of Use (`/policies/terms-of-use`) prohibit
"any automated method (including … robots, scripts, spiders, data extractors …) to collect data
from, access or search, the Services" without Fandango's express written authorization. There is
no personal-use exception for automated access.

Chronicle only uses data sources whose terms allow it, so this plugin was dropped.

## If RT scores are wanted later

Only through a source that is itself licensed to provide them, never by reading
rottentomatoes.com. Any such source plugs into the generic ratings model in
[`docs/plans/2026-10-01-ratings-tracking-design.md`](../plans/2026-10-01-ratings-tracking-design.md)
with no RT-specific code.
