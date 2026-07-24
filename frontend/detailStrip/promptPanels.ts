import { clamp, PROMPT_WINDOW_MIN_DURATION } from "../constants";
import {
    buildAccordionSection,
    buildField,
    buildRepeatingEditor,
    buildTextarea,
} from "../detailWidgets";
import {
    applyPromptWindowBegin,
    applyPromptWindowEnd,
    promptWindowNeighborBounds,
} from "../promptWindowEdits";
import { setSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { gridCeil, gridFloor, roundToTenth } from "../utils";
import { disableCapabilityControls } from "./capabilityUi";
import type { DetailStripContext } from "./context";

type PromptSelection = Extract<
    TimelineSelection,
    { kind: "prompt-major" | "prompt-minor" }
>;

const buildMajorPromptSection = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    open: boolean,
): HTMLElement => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-prompt-body";
    const prompt = buildTextarea(
        clip.prompt ?? "",
        "Clip prompt (blank inherits the global prompt)…",
        "prompt-major",
        (value) => {
            ctx.debouncedCommit("prompt-major", (clips) => {
                const target = clips[clipIdx];
                if (target) {
                    target.prompt = value.trim();
                }
            });
        },
    );
    prompt.rows = 8;
    col.appendChild(buildField("Prompt", prompt));
    const built = buildAccordionSection({
        key: "major-prompt",
        label: "Major Prompt",
        content: col,
        open,
        flattenContent: true,
        className: "vst-detail-prompt-major",
    });
    const decision = ctx.capabilities().forClip(clip).decision("majorPrompt");
    if (!decision.supported) {
        disableCapabilityControls(built.section, decision);
        if (clip.prompt.trim()) {
            const clear = document.createElement("button");
            clear.type = "button";
            clear.className =
                "interrupt-button vst-btn-tiny vst-remove-unsupported-prompt";
            clear.textContent = "Remove unsupported clip prompt";
            clear.addEventListener("click", () => {
                ctx.commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.prompt = "";
                    }
                });
                ctx.render();
            });
            built.content.appendChild(clear);
        }
    }
    return built.section;
};

const buildRelayPromptSection = (
    ctx: DetailStripContext,
    clip: Clip,
    clipIdx: number,
    selectedWindowIdx: number | null,
    open: boolean,
): HTMLElement => {
    const windows = clip.promptWindows ?? [];
    const decision = ctx.capabilities().forClip(clip).decision("promptRelay");
    const activeWindowIdx =
        windows.length === 0
            ? null
            : clamp(selectedWindowIdx ?? 0, 0, windows.length - 1);
    const buildSection = (editor?: HTMLElement): HTMLElement =>
        buildRepeatingEditor({
            key: "relay-prompts",
            label: "Relay Prompts",
            sectionClass: "vst-detail-relay-section",
            open,
            items: windows.map((window, index) => ({
                label: `R${index + 1}`,
                focusKey: `relay-tab-${index}`,
                title: `Relay prompt ${roundToTenth(window.start)}–${roundToTenth(window.start + window.duration)} seconds`,
                active: index === activeWindowIdx,
                className: "vst-relay-tab",
                onSelect: () =>
                    setSelection({
                        kind: "prompt-minor",
                        clipIdx,
                        windowIdx: index,
                    }),
                onShiftDelete: () => ctx.deleteWindowEntry(clipIdx, index),
            })),
            add: {
                title: decision.supported
                    ? "Add a relay prompt in the first available time window"
                    : decision.reason,
                label: "+ Add Relay Prompt",
                className: "vst-detail-add-relay",
                disabled: !decision.supported,
                onClick: () => ctx.addPromptWindow(clipIdx),
            },
            remove: {
                title:
                    activeWindowIdx === null
                        ? "No relay prompt to delete"
                        : `Delete relay prompt ${activeWindowIdx + 1}`,
                className: "vst-detail-delete-relay",
            },
            editor,
        }).section;
    if (activeWindowIdx === null) {
        return buildSection();
    }

    const window = windows[activeWindowIdx];
    const clipDuration = Math.max(
        PROMPT_WINDOW_MIN_DURATION,
        clip.duration || 0,
    );
    const editorSection = document.createElement("div");
    editorSection.className =
        "vst-detail-col vst-detail-prompt-body vst-detail-minor-window";
    editorSection.setAttribute("data-vst-minor-window", `${activeWindowIdx}`);
    const bounds = promptWindowNeighborBounds(clip, activeWindowIdx);
    const beginInput = ctx.buildClampedNumber({
        key: `minor-${activeWindowIdx}-begin`,
        value: roundToTenth(window.start),
        min: bounds?.beginMin ?? 0,
        max: gridFloor(Math.max(0, clipDuration - PROMPT_WINDOW_MIN_DURATION)),
        step: 0.1,
        readBack: (clips) => {
            const target = clips[clipIdx]?.promptWindows?.[activeWindowIdx];
            return target ? roundToTenth(target.start) : null;
        },
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            if (target) {
                applyPromptWindowBegin(target, activeWindowIdx, value);
            }
        },
    });
    editorSection.appendChild(
        buildField(
            "Begin (s)",
            beginInput,
            undefined,
            "When this relay prompt starts within the clip.",
        ),
    );
    const endInput = ctx.buildClampedNumber({
        key: `minor-${activeWindowIdx}-end`,
        value: roundToTenth(window.start + window.duration),
        min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
        max: bounds?.endMax ?? clipDuration,
        step: 0.1,
        readBack: (clips) => {
            const target = clips[clipIdx]?.promptWindows?.[activeWindowIdx];
            return target ? roundToTenth(target.start + target.duration) : null;
        },
        mutate: (clips, value) => {
            const target = clips[clipIdx];
            if (target) {
                applyPromptWindowEnd(target, activeWindowIdx, value);
            }
        },
    });
    editorSection.appendChild(
        buildField(
            "End (s)",
            endInput,
            undefined,
            "When this relay prompt ends. It cannot cross a neighbouring relay.",
        ),
    );
    const editor = buildTextarea(
        window.prompt ?? "",
        "Relay prompt for this window…",
        `minor-${activeWindowIdx}`,
        (value) => {
            ctx.debouncedCommit(`minor-${activeWindowIdx}`, (clips) => {
                const target = clips[clipIdx]?.promptWindows?.[activeWindowIdx];
                if (target) {
                    target.prompt = value.trim();
                }
            });
        },
    );
    editor.addEventListener("focus", () => {
        setSelection({
            kind: "prompt-minor",
            clipIdx,
            windowIdx: activeWindowIdx,
        });
    });
    editor.rows = 4;
    editorSection.appendChild(buildField("Prompt", editor));
    if (!decision.supported) {
        disableCapabilityControls(editorSection, decision);
    }
    return buildSection(editorSection);
};

/** Major and relay prompts are one clip-prompt sidebar with child relays. */
export const buildPromptBody = (
    ctx: DetailStripContext,
    selection: PromptSelection,
    clips: Clip[],
): HTMLElement => {
    const clipIdx = selection.clipIdx;
    const clip = clips[clipIdx];
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-prompts-body";
    body.appendChild(
        buildMajorPromptSection(
            ctx,
            clip,
            clipIdx,
            selection.kind === "prompt-major",
        ),
    );
    body.appendChild(
        buildRelayPromptSection(
            ctx,
            clip,
            clipIdx,
            selection.kind === "prompt-minor" ? selection.windowIdx : null,
            selection.kind === "prompt-minor",
        ),
    );
    return body;
};

export const buildPromptMajorBody = (
    ctx: DetailStripContext,
    selection: Extract<TimelineSelection, { kind: "prompt-major" }>,
    clips: Clip[],
): HTMLElement => buildPromptBody(ctx, selection, clips);

export const buildPromptMinorBody = (
    ctx: DetailStripContext,
    selection: Extract<TimelineSelection, { kind: "prompt-minor" }>,
    clips: Clip[],
): HTMLElement => buildPromptBody(ctx, selection, clips);
