# Custom CLI tab regression — 2026-10-24

The Other CLI tab failed to load because its editor queried AgentConnectionStore
with `custom`, a provider intentionally excluded from registered chat-switching
profiles. The resulting argument error restored the previous tab. Skip that
lookup for custom editors, retaining their existing draft and activation rules.

The previous endurance run checked liveness and persisted settings, but did not
assert that every requested tab remained selected. The runner now asserts final
selection in wide, overflow and random navigation, and explicitly checks the
custom editor's initial fields, confirmed executable edit, independent draft,
tab round trip, compact layout and restoration after restart. The new opening
assertion failed against the pre-fix binary and passed with the fix.

- Japanese/dark: all nine scenario groups and 90 random switches passed;
  saved active settings and the loopback chat connection were retained.
  Evidence: `desktop/artifacts/2d24a4e7-0efa-4e63-8bd0-1097ed485def/`.
- English/light: the same nine scenarios and 90 random switches passed, including
  compact custom editor and post-restart draft assertions. Both themes' compact
  screenshots were reviewed; native tabs and fields remain visible and scrollable.
  Evidence: `desktop/artifacts/eb85c4ca-4646-49c8-bc93-f4f2032bd6d6/`.
- Portable publish, localization consistency and diff review passed. The change
  preserves the restriction on registering custom CLIs as chat-switch profiles.
- Tests use isolated settings, an offline executable and loopback Mattermost;
  they do not execute user CLIs or send external messages.

---

# Settings navigation endurance regression — 2026-10-23

The reported crash was reproduced in the native app at a 500 px window width,
selecting Custom from the agent tabs' More menu with persisted CLI verification.
Windows recorded a native access violation in Microsoft.UI.Xaml.Controls.dll;
the managed unhandled-exception handler was not invoked.

Selection callbacks now defer and coalesce scope changes until the native
NavigationView selection event returns. They no longer disable/reselect item
containers inside that event. Responsive layout only changes navigation mode
when crossing the breakpoint. A failed save preserves a newer queued selection
instead of dropping it and leaving the selected tab inconsistent with its editor.

Validation evidence (isolated profiles only):
- 900 agent scope switches across widths 500/1250/1440/1000/500, with real cached
  CLI verification: no crash; saved active settings hash unchanged.
  Evidence: `desktop/artifacts/scope-switch-repro-7bb52f2e0591477785c0f427b27d8ef8/`.
- Japanese/dark: eight existing scenario groups plus 50 seeded random cycles,
  450 scope switches, 100 outer-page selections and 50 resizes passed. Saved
  active settings and the single loopback chat connection were retained.
  Evidence: `desktop/artifacts/1fb580a4-87e8-477e-b303-2b8de775e118/`.
- Final build, English/light: the same eight scenario groups and 50 random
  cycles (450 scope switches) passed, retaining saved settings and connection.
  Minimum-width screenshots in both themes were reviewed: outline icons and
  native overflow controls remained visible, with vertical scrolling available.
  Evidence: `desktop/artifacts/d62ab774-9dda-45f2-b999-283d2ab1dcba/`.
- Portable publish, localization consistency and bundled third-party notices
  checks passed. Review found and fixed the pending-selection loss on failed
  transitions; no remaining release-blocking issue was identified in this diff.
- Reproducible random runner: `desktop/SettingsScopesUiSmoke.ps1 -StressCycles 50`.
  Uses seed 20261023, an offline CLI and loopback Mattermost; no real AI requests
  or external chat messages. A finite stress run does not prove absence of every
  race or a memory leak.

---

# Outline icons and responsive settings tabs — 2026-10-23

Target: design option 2's top tabs for constrained windows; retain option 1's
side list when the content width is at least 1000 effective pixels. This is a
responsive adaptation: the concept is wide and edits Claude, while the final
medium capture selects the offline Codex fixture. No pixel-perfect claim.

Source and implementation were opened together in
`desktop/artifacts/c9e83ba0-9dec-444a-ba90-c6999acf5dc3/design2-responsive-comparison.png`.

Findings resolved:
- The original 20 px glyphs were clipped by a 16 px box. The new icons use
  original Fluent System Icons regular SVG paths in a 20 px canvas/Viewbox.
- Distinct, generic outline symbols identify the providers; no third-party
  product logos are bundled. The pinned Microsoft MIT license and attribution
  remain accessible from the Information page; inventory and aggregate notice
  were updated and validated against the published payload.
- Medium and small windows use native top NavigationView tabs. Its More menu
  exposes overflow choices, including all six agent scopes at minimum width.
- Added detail padding in both orientations. Editor busy guards preserve access
  to the CLI cancel button; the endurance fix above supersedes disabling tabs.
- WinUI rejected shared PathIcon geometry in the first isolated run. Each icon
  now creates its own path through a DataTemplate; restart tests pass.

Fidelity: native typography, theme-aware foregrounds and surfaces, 20 px vector
outline icons, aligned inputs, and a native selected-tab underline. Existing
production copy and real settings controls are retained. The separate activation
action and CLI catalog feedback remain visible; browsing never activates a profile.

Final native smoke: eight scenario groups pass in Japanese/dark and English/light
at 1440, 1000 and 500 window widths. Tests include selecting every compact scope,
CLI checks, cancellation rollback, save failure, activation, restart/cache restore
and keeping the loopback chat connection alive. Screenshots were visually reviewed.
- Dark: `desktop/artifacts/c9e83ba0-9dec-444a-ba90-c6999acf5dc3/`.
- Light: `desktop/artifacts/932f0509-81e9-4f69-9e94-7c401013f170/`.
- Portable publish, localization consistency and third-party notice validation pass.

No remaining P0/P1/P2 visual issues against the requested responsive direction.

final result: passed

---

# Previous WinUI settings scope review — 2026-10-23

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
