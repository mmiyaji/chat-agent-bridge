# WinUI settings scope review — 2026-10-23

Selected target: design option 1, `exec-9b837a0f-ed60-47fc-b6eb-1cbd7e4c718d.png`.
Implementation: native Agent settings and Connection settings, on an isolated profile.

The source and final Japanese/dark capture were opened together in
`desktop/artifacts/b0b94895-e48b-498b-91cb-960eb7c8f7d1/design-comparison.png`.
The comparison uses the same desktop proportions; actual fixture paths, version,
model catalog and existing product copy replace the concept's fictional values.

## Findings and fixes

- Removed repeated provider headings and old jump selectors. Both pages now have a
  local scope list and one detail area; smaller windows use a labelled scope picker.
- Replaced the model control's competing editable text/selection updates with a
  text field and explicit catalog selection. Catalog progress/errors remain visible.
- Made form fields stretch, moved labels above inputs at narrow widths, wrapped
  pending-change help and truncated long workspace/version summaries with tooltips.
- Kept per-agent editing separate from activation. CLI check and Use are explicit;
  browsing scopes does not change the active provider or reconnect chat.
- Checked cancellation rollback, failed-save resolution, restart restoration and
  shared-workspace changes. Old CLI verification cannot activate a changed context.

## Fidelity surfaces

- Typography: native WinUI text styles and Japanese/English resources. Native font
  metrics and the shorter production help differ acceptably from the concept.
- Layout: preserves outer navigation, local scope list, workspace summary, aligned
  fields and advanced expander. Native spacing is deliberately more compact.
- Colors: theme brushes provide light/dark surfaces, native selection and accent
  actions. OS backdrop tint varies from the generated image.
- Assets: existing app branding and standard WinUI vector icons; no new raster art.
- Content: shared versus provider-specific scope is explicit. Synthetic loopback
  connection and offline CLI data are test evidence, not real-service results.

## Validation

- Portable self-contained publish: passed.
- Related Core tests: 93 passed; localization consistency check: passed.
- Native smoke: eight scenario groups passed in Japanese/dark and English/light,
  at wide and minimum window sizes. Screenshots were visually reviewed.
- Dark evidence: `desktop/artifacts/b0b94895-e48b-498b-91cb-960eb7c8f7d1/`.
- Light evidence: `desktop/artifacts/a6022e82-a7ba-4c55-9717-cd1bedd9ba64/`.
- Reproducible runner: `desktop/SettingsScopesUiSmoke.ps1` (isolated settings,
  loopback Mattermost and offline test CLI; no real Slack/Mattermost API testing).

No remaining actionable P0/P1/P2 visual differences against the chosen direction.
This accepts the design direction, not pixel-perfect reproduction of the mock.

final result: passed

---

# Previous web workbench UI review (historical)

Selected target: third displayed design, `exec-47203530-964d-4ed0-b8a3-759c426446f1.png`.
Implementation: `/admin`, conversation selected, `.tmp-workbench-preview.png`.

Compared both images in the same review input. The source is 1487 × 1058; capture used a 1440 × 1024 CSS viewport. Screenshot presentation is scaled by the browser capture surface; pixel-perfect typography comparison is limited. The preview uses isolated E2E fixture data (one legacy approval session, two execution records, disconnected transports), rather than the five fictional conversations and native question in the concept. No sample connection state is presented as live.

## Findings and fixes

- Initial detail text inherited a 12px notice style. Raised history text to 14px with 1.8 line height, then recaptured.
- Initial conversation column was narrower than the source. Changed the desktop split to 32% / remaining width, then recaptured.
- Initial preview fixture lacked connection/settings defaults and displayed API errors. Supplied a recording connection manager and isolated default settings, then verified the page without the API error banner.

## Fidelity surfaces

- Typography: system Japanese UI font, restrained headings, readable history. Exact font weight and screenshot rasterization differ from the generated concept; P3 polish.
- Layout: horizontal navigation, conversation list on left, details on right, full-width bottom status bar. Extra status filter and overview navigation preserve existing product functions. Mobile stacks the panes.
- Colors: neutral charcoal background, muted blue selected conversation, amber pending request, thin separators. No gradients or decorative imagery.
- Assets: the selected concept contains no required raster assets. Existing decorative navigation glyphs and logo tile are hidden.
- Content: real API metadata, execution records and approval reasons replace fictional progress events. Mock timeline granularity and affected-file lists are not fabricated.

Interactions validated: navigation, settings save, connection error, combined search/filter and refresh, native input state, history selection/close, escaped output and missing-session error. Eight browser scenarios pass. Type checking, executable build and standalone executable smoke test pass.

This accepts the concept's visual direction with the documented production-data differences; it does not assert pixel-perfect reproduction.

final result: passed
