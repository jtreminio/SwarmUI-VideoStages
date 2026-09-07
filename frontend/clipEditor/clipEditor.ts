import {
    renderBlockingArchitectureCatalogStatus,
    renderRetainedArchitectureCatalogStatus,
} from "../architectureCatalogStatusView";
import {
    getArchitectureCatalogSnapshot,
    refreshAuthoritativeArchitectureCatalog,
    subscribeArchitectureCatalog,
} from "../architectures/catalog";
import { deriveAuthoringDiagnostics } from "../authoringDiagnostics";
import { addVideoStagesClip } from "../clipCreation";
import { reorderClip } from "../clipReorder";
import { executableBoundaryForLeftClip } from "../clipSemantics";
import {
    type AuthoringSession,
    createAuthoringSession,
} from "../detailStrip/authoringSession";
import { closeManagedModal } from "../detailStrip/modalManager";
import { releaseSidebarMediaPreviews } from "../detailStrip/sidebarMediaPreview";
import { getVideoStagesHostBridge } from "../host";
import { getState, getTimelineStore } from "../persistence/repository";
import { getSelection, setSelection, subscribeSelection } from "../selection";
import {
    getGroupToggle,
    isVideoStagesEnabled,
    setVideoStagesEnabled,
} from "../swarmInputs";
import type { TimelineSelection } from "../types";
import { renderEditorDiagnostics } from "./diagnostics";
import { flowMap } from "./flowMap";
import {
    jumpToSection,
    markNavigation,
    revealSelectedClip,
    revealSelectedItem,
    sectionAtTop,
    updateStickyItemLabels,
} from "./navigation";
import { clipSections } from "./sections";
import {
    EDITOR_BODY_ID,
    mountClipEditor,
    updateEditorTabIndicator,
} from "./tab";
import { type EditorNavigation, renderEditor } from "./view";

export function selectionSection(selection: TimelineSelection): string {
    switch (selection.kind) {
        case "clip":
            return "stages";
        case "ref":
            return "keyframes";
        case "boundary-ref":
        case "clip-ref":
            return "references";
        case "ic-lora":
            return "ic-loras";
        case "audio":
            return "audio";
        case "prompt-minor":
            return "relay-prompts";
        case "retake":
            return "retake";
        case "boundary":
            return "join";
        default:
            return "prompt";
    }
}

export function createClipEditor() {
    let session: AuthoringSession | null = null;
    let unsubscribers: (() => void)[] = [];
    let clipId: string | null = null;
    let section = "prompt";
    let rendering = false;
    let selecting = false;
    let adding = false;
    let collapsed = false;
    let layoutObserver: ResizeObserver | null = null;
    let enabledToggle: HTMLInputElement | null = null;
    const root = () => document.getElementById(EDITOR_BODY_ID);
    const pane = () => root()?.querySelector<HTMLElement>(".vst-editor-scroll");
    const selectedIndex = () =>
        Math.max(
            0,
            getState().clips.findIndex((clip) => clip.id === clipId),
        );
    const silentlySelect = (selection: TimelineSelection): void => {
        if ("clipIdx" in selection)
            clipId = getState().clips[selection.clipIdx]?.id || null;
        selecting = true;
        try {
            setSelection(selection);
        } finally {
            selecting = false;
        }
    };
    const mark = (): void => {
        const rail = root()?.querySelector<HTMLElement>(
            ".vst-editor-navigation",
        );
        if (rail) markNavigation(rail, section);
    };
    const jump = (key: string): void => {
        session?.flush();
        const scroll = pane();
        if (!scroll) return;
        jumpToSection(scroll, key);
        section = key;
        mark();
    };
    const selectClip: EditorNavigation["selectClip"] = (
        index,
        destination = section,
        focusPrompt = false,
    ) => {
        session?.flush();
        const clip = getState().clips[index];
        if (!clip) return;
        const changed = clip.id !== clipId;
        clipId = clip.id || null;
        section = destination;
        silentlySelect({ kind: "clip", clipIdx: index, stageIdx: 0 });
        if (changed) render(destination);
        else jump(destination);
        if (focusPrompt)
            root()
                ?.querySelector<HTMLTextAreaElement>(
                    '[data-section="prompt"] textarea',
                )
                ?.focus({ preventScroll: true });
    };
    const navigation: EditorNavigation = {
        selectClip,
        moveClip: (from, to) => {
            session?.flush();
            const selected = reorderClip(from, to);
            if (selected !== null) selectClip(selected);
        },
        deleteClip: (index) => {
            session?.flush();
            session?.context.deleteClip?.(index);
        },
        toggleMap: () => {
            collapsed = !collapsed;
            const body = root();
            body?.classList.toggle("vst-editor-map-collapsed", collapsed);
            const button = body?.querySelector<HTMLButtonElement>(
                '[data-testid="toggle-map"]',
            );
            if (button) {
                button.textContent = collapsed ? "»" : "«";
                button.setAttribute("aria-expanded", String(!collapsed));
                button.setAttribute(
                    "aria-label",
                    collapsed ? "Expand clip map" : "Collapse clip map",
                );
            }
            if (body) revealSelectedClip(body);
        },
        jump,
        addClip: () => {
            session?.flush();
            adding = true;
            addVideoStagesClip();
        },
        setEnabled: (value) => {
            setVideoStagesEnabled(value);
            render();
        },
    };
    const updateDiagnostics = (): void => {
        const body = root();
        if (body && session)
            renderEditorDiagnostics(
                body,
                deriveAuthoringDiagnostics(
                    getState().clips,
                    session.context.authoring().capabilities,
                ),
                selectClip,
            );
    };
    function render(destination?: string): void {
        const body = root();
        if (!body || !session || rendering) return;
        updateEditorTabIndicator(isVideoStagesEnabled());
        const catalog = getArchitectureCatalogSnapshot();
        if (
            renderBlockingArchitectureCatalogStatus(
                body,
                catalog,
                refreshAuthoritativeArchitectureCatalog,
            )
        )
            return;
        rendering = true;
        try {
            session.beforeRender();
            layoutObserver?.disconnect();
            const state = getState();
            const index = Math.max(
                0,
                state.clips.findIndex((clip) => clip.id === clipId),
            );
            const scrollTop = pane()?.scrollTop || 0;
            const mapTop =
                body.querySelector<HTMLElement>(".vst-editor-sequence")
                    ?.scrollTop || 0;
            const navLeft =
                body.querySelector<HTMLElement>(".vst-editor-navigation")
                    ?.scrollLeft || 0;
            clipId = state.clips[index]?.id || null;
            releaseSidebarMediaPreviews(body);
            const sections = state.clips[index]
                ? clipSections(state, index, session.context)
                : [];
            renderEditor(body, state, index, sections, navigation, collapsed);
            updateDiagnostics();
            renderRetainedArchitectureCatalogStatus(
                body,
                catalog,
                refreshAuthoritativeArchitectureCatalog,
            );
            const scroll = pane();
            if (scroll) {
                scroll.scrollTop = scrollTop;
                if (destination) jumpToSection(scroll, destination);
                scroll.addEventListener(
                    "scroll",
                    () => {
                        section = sectionAtTop(scroll) || "prompt";
                        updateStickyItemLabels(scroll);
                        mark();
                    },
                    { passive: true },
                );
            }
            const sequence = body.querySelector<HTMLElement>(
                ".vst-editor-sequence",
            );
            if (sequence) sequence.scrollTop = mapTop;
            const nav = body.querySelector<HTMLElement>(
                ".vst-editor-navigation",
            );
            if (nav) nav.scrollLeft = navLeft;
            if (destination) revealSelectedClip(body);
            if (!sections.some((entry) => entry.key === section)) {
                section = "prompt";
                if (scroll) scroll.scrollTop = 0;
            }
            mark();
            getVideoStagesHostBridge().enableSliders(body);
            if (scroll) {
                updateStickyItemLabels(scroll);
                layoutObserver?.observe(scroll);
                for (const item of scroll.querySelectorAll("[data-item-id]"))
                    layoutObserver?.observe(item);
            }
        } finally {
            session.afterRender();
            rendering = false;
        }
    }
    const focusItem = (event: FocusEvent): void => {
        const item = (event.target as HTMLElement).closest<HTMLElement>(
            "[data-selection]",
        );
        if (item?.dataset.selection) {
            silentlySelect(
                JSON.parse(item.dataset.selection) as TimelineSelection,
            );
            const target = event.target as HTMLElement;
            if (target.isConnected && document.activeElement !== target)
                target.focus({ preventScroll: true });
        }
    };
    const dispose = (): void => {
        closeManagedModal();
        layoutObserver?.disconnect();
        layoutObserver = null;
        session?.dispose();
        session = null;
        for (const unsubscribe of unsubscribers.splice(0)) unsubscribe();
        enabledToggle?.removeEventListener("change", onEnabled);
        enabledToggle = null;
        const body = root();
        body?.removeEventListener("focusin", focusItem);
        if (body) releaseSidebarMediaPreviews(body);
    };
    const onEnabled = (): void => render();
    const init = (): void => {
        dispose();
        const body = mountClipEditor();
        if (!body) return;
        if (typeof ResizeObserver !== "undefined")
            layoutObserver = new ResizeObserver(() => {
                const scroll = pane();
                if (scroll) updateStickyItemLabels(scroll);
            });
        session = createAuthoringSession({
            render,
            isRendering: () => rendering,
            getRoot: root,
            setSelectionSilently: silentlySelect,
        });
        body.addEventListener("focusin", focusItem);
        enabledToggle = getGroupToggle();
        enabledToggle?.addEventListener("change", onEnabled);
        unsubscribers = [
            getTimelineStore().subscribe((state, meta) => {
                if (rendering) return;
                const active = document.activeElement;
                const editing =
                    active instanceof HTMLTextAreaElement ||
                    (active instanceof HTMLInputElement &&
                        ["text", "number", "range"].includes(active.type));
                if (
                    meta.hint === "value-only" &&
                    editing &&
                    body.contains(active)
                ) {
                    const strip =
                        body.querySelector<HTMLElement>(".vst-editor-strip");
                    const top =
                        strip?.querySelector<HTMLElement>(
                            ".vst-editor-sequence",
                        )?.scrollTop || 0;
                    strip?.replaceWith(
                        flowMap(state, selectedIndex(), navigation, collapsed),
                    );
                    const updated = body.querySelector<HTMLElement>(
                        ".vst-editor-sequence",
                    );
                    if (updated) updated.scrollTop = top;
                    updateDiagnostics();
                } else render();
            }),
            subscribeSelection((selection) => {
                if (
                    selecting ||
                    rendering ||
                    selection.kind === "none" ||
                    selection.kind === "audio-track"
                )
                    return;
                const index =
                    selection.kind === "boundary" ||
                    selection.kind === "boundary-ref"
                        ? executableBoundaryForLeftClip(
                              getState().clips,
                              selection.leftClipIdx,
                          )?.rightIdx
                        : selection.clipIdx;
                if (index === undefined) return;
                clipId = getState().clips[index]?.id || null;
                section = adding ? "prompt" : selectionSection(selection);
                render(section);
                const scroll = pane();
                if (scroll) revealSelectedItem(scroll, selection);
                if (adding) {
                    adding = false;
                    const strip = body.querySelector<HTMLElement>(
                        ".vst-editor-sequence",
                    );
                    if (strip) revealSelectedClip(body);
                    body.querySelector<HTMLTextAreaElement>(
                        '[data-section="prompt"] textarea',
                    )?.focus({ preventScroll: true });
                }
            }),
            subscribeArchitectureCatalog(() => render()),
        ];
        const selection = getSelection();
        if (getArchitectureCatalogSnapshot().catalog && "clipIdx" in selection)
            clipId = getState().clips[selection.clipIdx]?.id || null;
        render();
    };
    return {
        init,
        refresh: () => render(),
        flushPending: () => session?.flush(),
        dispose,
    };
}
