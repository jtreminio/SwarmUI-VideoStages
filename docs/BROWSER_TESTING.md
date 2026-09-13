# VideoStages browser guide

Use the global `playwright-cli` skill through `scripts/videostages-browser`.
The launcher loads nvm, selects the `videostages` session, and keeps its cache
under `test-results/`. In this environment, keep the launching shell alive with
`exec_command` (`cmd: "bash --noprofile --norc", `tty: true`) and send commands
with `write_stdin`.

## Editing boundary

Use a fresh browser profile. Do not click Generate, submit the global prompt,
or call generation APIs for editing checks. Additions can enable VideoStages;
that toggle does not queue a generation. Close the disposable profile when done.
Do not reset another user's saved document.

## Open the editor

```bash
scripts/videostages-browser open http://127.0.0.1:7801/Text2Image --browser=chromium
scripts/videostages-browser resize 1512 982
scripts/videostages-browser run-code "async page => { const tab=page.getByTestId('clip-editor-tab'); await tab.waitFor({state:'visible',timeout:30000}); await tab.click(); }"
scripts/videostages-browser snapshot '[data-testid=clip-editor]'
```

Wait for the tab rather than a fixed loading delay. Use a fresh snapshot after
changing a model or adding an item: production capability rules can change the
available controls. `+ Add clip` works directly in an empty editor.

## Stable locators

Open `[data-testid="clip-editor-tab"]`; its label gains a checkmark while enabled.
The root is `[data-testid="clip-editor"]`. Within it:

| Target | Locator |
| --- | --- |
| Collapsible map with pinned switch/output | `[data-testid="clip-strip"]` |
| Collapse/expand map | `[data-testid="toggle-map"]` |
| Output dimensions button | `[data-testid="output-settings"]` |
| Scrolling clip sequence | `[data-testid="clip-sequence"]` |
| Clip | `[data-testid="clip-card"][data-clip-id="…"]` |
| Prompt shortcut within a clip | `[data-testid="clip-prompt"]` |
| Horizontal section navigation | `[data-testid="section-navigation"]` |
| Section jump | `[data-testid="nav-stages"]` (or another section key) |
| Join options in main pane | `[data-testid="edit-join"]` |
| Delete clip | `[data-testid="clip-card"] [data-testid="delete-clip"]` |
| Clip action menu (move earlier/later, delete) | `[data-testid="clip-card"] [data-testid="clip-actions"]` |
| Scroll container | `[data-testid="clip-sections"]` |
| Section | `[data-testid="section-stages"]` (or another section key) |
| Repeated item | `[data-testid="editor-item"][data-item-id="…"]` |
| Current stage in sticky heading | `[data-testid="sticky-item-label"]` within Stages |
| Steps number within an item | `[data-testid="field-steps-number"]` |
| Add stage/keyframe | `[data-testid="add-stages"]`, `[data-testid="add-keyframes"]` |
| Delete within an item | `[data-testid="remove-item"]` |

Control selectors are scoped to their section/item. `data-field` names the
control; `data-vst-focus-key` also carries its clip/item identity for draft and
focus restoration. Native labels and roles remain usable. Refresh references
from snapshots; do not reuse stale generated DOM IDs.

## Check the continuous layout

All supported sections remain mounted in the main container. Navigation buttons
scroll that container, and manual scrolling updates the navigation highlight.
Headings stay visible until the following section arrives. When a stage title
scrolls behind its heading, its label joins the heading (for example, Stages · S0).
Other sections keep only their section title. The stage label clears when its
title returns. Newly added stages
and keyframes scroll into view below their section heading. The last section
can reach the top. Long sequences scroll vertically inside the clip map.
Collapsing the map keeps the current fields and unfinished drafts mounted.
The collapsed output button shows dimensions and FPS. Clip action menus use
SwarmUI's popup; Move earlier/later keeps the moved clip selected, and Delete
selects the clip to the left. Source join arrows stack
above and below their label in the collapsed map.
Join buttons open the selected boundary in the main container.
A reference-style Continue join adds its incoming video reference to the next
clip's References section. Edit its scale and soundtrack there. This requires a
backend model whose catalog supports reference-style Continue.

Normal LoRAs are rows inside each stage (`.vst-stage-loras`). Add with
`.vst-stage-lora-add`, remove with `.vst-stage-lora-remove`, and edit the
`.vst-stage-lora-strength` number. `.vst-stage-loras-copy` replaces the other
stages’ lists and strengths on the current clip. Check copying before blurring a
strength edit, independent edits afterward, and persistence after reload.
IC-LoRAs, Keyframes and References share a card of repeatable rows. Each row keeps
its own fields and delete action. Drop local files onto **+ Add keyframe** or
**+ Add reference** to create populated rows. Source video enables the Retake
section for models that support it; Start, Length and Strength edit its window.
The folder picker can open above the editor without clipping; filtering and Escape must not scroll the surrounding pane.

Upload fields share a 112×84 preview beside their filename and Upload/Select/Clear
controls. Click `.vst-media-thumbnail` to open `.vst-media-preview-modal`.
Images enlarge; audio/video keep their selected playback range. Close or Escape
returns focus to the thumbnail. Check keyframes, references, IC-LoRAs, source
video and audio, including replacement and Clear. Local upload payloads are
intentionally excluded from durable browser storage; use a server-backed file
when testing media persistence across page reloads.

Use SwarmUI's `#t2i-mid-split-bar` to compare default and taller panel heights.
Check the full app at 1512×982 so the available space is realistic.

```bash
scripts/videostages-browser screenshot --filename=test-results/clip-editor.png
scripts/videostages-browser console error
scripts/videostages-browser requests
scripts/videostages-browser close
```

The initial app load may return 401 responses from other extensions before the
session is initialized. Record the actual failing endpoint and distinguish
network entries from JavaScript exceptions.

## Automated check

With SwarmUI running:

```bash
source ~/.nvm/nvm.sh
npm run test:browser
```

The disposable browser edits prompts, steps, keyframes and a join, checks both
navigation methods, verifies the production form carriers, reorders clips through
their action menus, and reloads to check persistence. It also drops image references, opens
the preview viewer, authors a retake from a previous clip source, and checks
narrow layout. A MiniMax reference model exercises incoming Continue references
and their saved scale/soundtrack settings. Preview-era links restore the saved
clips into the selected production tab. The suite never generates media.
Artifacts from failures go to `test-results/playwright/`.

The browser suite requires LTX video models, a MiniMax ref2va model, and
Make Them Bonita installed on the backend.
It checks six clips, the pinned enable switch, card padding clicks, the disabled
background and horizontal Bonita fields. It does not install backend features.
