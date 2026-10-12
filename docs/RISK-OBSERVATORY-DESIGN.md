# Risk Observatory visual redesign

## Direction

The reference video shows a split login layout with a left purple marketing panel and right form, followed by a fixed left sidebar and a light card dashboard. Sentinel deliberately avoids that composition.

- **Login:** full-bleed locally served observatory artwork; brand telemetry along the top; headline and signal legend over the image; horizontal authorization rail anchored at the bottom.
- **Console:** horizontally oriented instrument dock (not a vertical sidebar); full-width image-backed risk-atlas hero; transparent/glass panels for live metrics, risk trend, model health, scoring, review queue, and model registry.
- **Artwork:** `web/public/sentinel-observatory.svg` and `web/public/risk-atlas.svg` are locally shipped custom vector scenes. They are used as actual CSS background images, with translucent overlays only where needed for legibility. No remote image request is needed at runtime.
- **Responsive behavior:** the auth rail stacks at tablet/mobile widths; the nav dock wraps into a horizontally scrollable row; metric panels reflow for narrow screens.

The layout is bespoke to this repository. No one can reliably guarantee that no similar visual idea has ever been used elsewhere.

## Changed files

- `web/src/App.tsx`: reworked the login screen markup while preserving the existing API authentication handler.
- `web/src/styles.css`: added the Risk Observatory visual system and responsive rules.
- `web/public/sentinel-observatory.svg`: full-bleed primary background image.
- `web/public/risk-atlas.svg`: secondary image used on login rail, navigation dock, dashboard hero and glass telemetry panels.
- `web/index.html`: updated browser title and theme color.
