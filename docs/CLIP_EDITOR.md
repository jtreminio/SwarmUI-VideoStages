# Production clip editor

The VideoStages bottom tab uses `frontend/clipEditor/`. It retains the production
`VideoStages-Timeline-Tab` identifier so saved links keep working. Links to the
former preview tab resolve to this editor.

## Ownership

- `main.ts` connects SwarmUI's parameter lifecycle to `videoStagesApplication.ts`.
- `videoStagesApplication.ts` owns authoritative catalog startup, carrier
  synchronization, host rebuilds, and pending-edit flushes on page exit.
- `clipEditor/clipEditor.ts` owns selected clip/section, rendering, subscriptions,
  scroll/focus restoration, and editor disposal.
- `clipEditor/sections.ts` composes shared field builders and capability-aware
  item actions. Persisted unsupported settings remain available for repair.
- `clipEditor/components.ts` builds section/item/action markup and scopes draft
  and focus keys to their owners.
- `clipEditor/flowMap.ts` renders clip order, source relationships, actions, and
  output duration. `clipReorder.ts` persists moves and reconciles incoming media.
- `clipEditor/navigation.ts` handles section jumps and sticky stage context.
- `clipEditor/referenceDrops.ts` connects file drops to existing bulk-media
  authoring operations.
- `clipEditor/diagnostics.ts` presents authoring issues and links to affected clips.
- `clipEditor/tab.ts` owns tab mounting, link compatibility, and enabled status.

Shared field builders remain in `detailStrip/`; that directory name does not
mean those fields belong to the retired timeline renderer. `authoringSession.ts`
composes the focus session, draft queue, and domain operations. Edits pass through
the existing repository and carrier adapter. The editor introduces no separate
document schema, persistence store, or backend generation path.

## Layout

The clip strip scrolls independently of its enable/output controls. Section
navigation scrolls the continuous editor. Section headings remain visible until
the following section reaches them; only Stages adds current-item context.

Controls use SwarmUI's native field creators and theme. Shared sizing gives model
selectors room for names while numbers and sliders remain compact. Fields wrap
in document order; card padding and subsection separation remain consistent.
Normal LoRAs are repeatable rows inside each stage. Add below the list, remove beside
the strength, or copy the list and strengths to every other stage of the current clip.
Keyframes, references, and IC-LoRAs share section containers.
Reference row labels retain their prompt tags.

Media pickers show compact previews beside file controls. Opening a preview uses
the shared viewer and selected playback range. Trim editors retain their media
bounds. Editor disposal releases players and closes the active media or trim dialog.

## Verification

Use `__test_helpers__/editor.ts` for integration tests against the real editor.
`authoringFixtures.ts` owns catalog-backed fixture data and host carriers.
Test persisted edits, capability repair, selection, and host integration through
production controls. Retired timeline geometry is not a contract of this editor.

New or changed tests require compile-safe production mutations with actual
assertion failures, stable test totals, and restoration from immediate copies.
Keep mutation runs serialized when several agents share this checkout.

See [BROWSER_TESTING.md](BROWSER_TESTING.md) for fresh-profile browser checks.
Editing verification never submits generation.
