# Editor fields and updates

Use the production builders in `frontend/detailWidgets.ts`: `buildField`,
`buildSlider`, `buildCheckbox`, and `buildMediaPickRow`. They emit SwarmUI
controls with a shared `.vst-form-field` container and `data-field-kind`.
Checkboxes use `buildField` for their labels, help, and control association.
Sliders retain the host's number/range wiring.

The editor styles that contract, not individual field names. Standard controls
occupy one grid cell; textareas span the row. `auto-fill` reserves unused cells
so a section with fewer fields does not enlarge its controls. Inner field groups
use `.vst-detail-fields`; section roots receive `.vst-editor-fields` when mounted.
Keep controls within those grids and use `.vst-detail-span-full` for headings,
notes, and nested sections.

Use the existing `DetailStripContext` to write changes:

- `commit` / `commitState` for immediate values.
- `debouncedCommit` for typed or dragged values, with a stable field key.
- `structuralCommit` when an edit changes available controls or document structure.

The authoring session owns draft flushing and focus restoration. The editor
subscribes to the production document store, so updates from either editor,
imports, and saved data go through the same rendering path. Do not update strip
labels or colors from individual field handlers. While a text or numeric field
is being edited, value-only updates preserve its DOM and refresh the overview.

The strip describes media type separately from consumption: `data-media-kind`
identifies audio/video, while `data-source-usage` identifies a consuming join.
A standalone audio-only clip therefore has audio styling without claiming it is
consumed. Source relationships still come from `clipRelationship`.

Browser regressions in `browser-tests/video-stages.spec.ts` measure control
widths across sections at 1512px and 1920px, including after keyframes are added.
