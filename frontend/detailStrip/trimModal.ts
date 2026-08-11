import { mediaPreviewSrc } from "../constants";
import { getVideoStagesHostBridge } from "../host";
import {
    markInPoint,
    type SourceRange,
    setInPoint,
    setOutPoint,
    type TrimLimits,
    toInOut,
} from "../trimGeometry";
import { createManagedModal, type ManagedModal } from "./modalManager";
import { buildTrimBar, type TrimBarHandle } from "./trimBar";

export interface TrimModalSpec {
    mediaKind: "audio" | "video";
    title: string;
    fileName: string;
    dataUri: string | null;
    range: SourceRange;
    limits: TrimLimits;
    impactText(range: SourceRange): string;
    onApply(range: SourceRange): void;
}

const MODAL_CLASS = "vst-trim-modal";
const BACKDROP_CLASS = "vst-trim-modal-backdrop";
const TITLE_ID = "vst_trim_modal_title";
let currentModal: ManagedModal | null = null;

const seconds = (value: number): string => value.toFixed(1);

const button = (
    label: string,
    title: string,
    dataAttribute?: string,
): HTMLButtonElement => {
    const element = document.createElement("button");
    element.type = "button";
    element.className = "basic-button small-button";
    element.textContent = label;
    element.title = title;
    if (dataAttribute) {
        element.setAttribute(dataAttribute, "");
    }
    return element;
};

const numberField = (
    labelText: string,
    name: "in" | "out" | "duration",
    readOnly = false,
): { wrap: HTMLLabelElement; input: HTMLInputElement } => {
    const wrap = document.createElement("label");
    wrap.className = "vst-trim-modal-field";
    const label = document.createElement("span");
    label.textContent = labelText;
    const input = document.createElement("input");
    input.type = "number";
    input.step = "0.1";
    input.dataset.vstTrimField = name;
    input.readOnly = readOnly;
    wrap.append(label, input);
    return { wrap, input };
};

export const buildTrimLauncher = (
    summary: string,
    onOpen: () => void,
): HTMLElement => {
    const row = document.createElement("div");
    row.className = "vst-trim-launcher";
    const text = document.createElement("span");
    text.className = "vst-trim-launcher-summary vst-detail-field-hint";
    text.textContent = summary;
    const open = button("Edit…", "Open the range editor");
    open.setAttribute("data-vst-open-trim", "");
    open.addEventListener("click", onOpen);
    row.append(text, open);
    return row;
};

export const closeTrimModal = (): void => {
    currentModal?.close();
};

export const openTrimModal = (spec: TrimModalSpec): void => {
    closeTrimModal();

    const isAudio = spec.mediaKind === "audio";
    let draft = { ...spec.range };
    let bar: TrimBarHandle | null = null;
    let stopAt: number | null = null;

    const heading = document.createElement("div");
    const title = document.createElement("h5");
    title.className = "modal-title";
    title.id = TITLE_ID;
    title.textContent = spec.title;
    const fileName = document.createElement("small");
    fileName.className = "vst-trim-modal-file";
    fileName.textContent = spec.fileName;
    heading.append(title, fileName);
    const close = button("×", "Close without applying");
    close.setAttribute("aria-label", close.title);

    const playerWrap = document.createElement("div");
    playerWrap.className = "vst-trim-modal-player-wrap";
    if (isAudio) {
        playerWrap.classList.add("vst-trim-modal-player-wrap-audio");
    }
    const player: HTMLMediaElement = isAudio
        ? document.createElement("audio")
        : getVideoStagesHostBridge().createInitVideoElement();
    player.className = "vst-trim-modal-player";
    player.controls = true;
    player.preload = "auto";
    if (!isAudio) {
        player.setAttribute("playsinline", "");
    }
    if (spec.dataUri) {
        player.src = mediaPreviewSrc(spec.dataUri);
    }
    player.currentTime = draft.startSeconds;
    const mediaError = document.createElement("div");
    mediaError.className = "vst-trim-modal-media-error";
    mediaError.textContent = `This browser cannot preview the selected ${spec.mediaKind}.`;
    mediaError.hidden = spec.dataUri !== null;
    player.hidden = spec.dataUri === null;
    playerWrap.append(player, mediaError);

    const transport = document.createElement("div");
    transport.className = "vst-trim-modal-transport";
    const timecode = document.createElement("output");
    timecode.className = "vst-trim-modal-timecode";
    const stepLabel = isAudio ? "0.1 s" : "Frame";
    const previousFrame = button(
        `◀ ${stepLabel}`,
        isAudio ? "Step backward 0.1 seconds" : "Step backward one frame",
    );
    const preview = button("▶ Preview range", "Play the selected range");
    const nextFrame = button(
        `${stepLabel} ▶`,
        isAudio ? "Step forward 0.1 seconds" : "Step forward one frame",
    );
    const markIn = button("Mark In", "Set In at the playhead (I)");
    const markOut = button("Mark Out", "Set Out at the playhead (O)");
    transport.append(
        timecode,
        previousFrame,
        preview,
        nextFrame,
        markIn,
        markOut,
    );

    const fields = document.createElement("div");
    fields.className = "vst-trim-modal-fields";
    const inField = numberField("In (s)", "in");
    const outField = numberField("Out (s)", "out");
    const durationField = numberField("Duration (s)", "duration", true);
    inField.input.min = "0";
    inField.input.max = `${spec.limits.limitSeconds}`;
    outField.input.min = `${spec.limits.minLengthSeconds}`;
    outField.input.max = `${spec.limits.limitSeconds}`;
    fields.append(inField.wrap, outField.wrap, durationField.wrap);

    const impact = document.createElement("small");
    impact.className = "vst-trim-modal-impact";

    const renderDraft = (syncBar = true): void => {
        const range = toInOut(draft);
        inField.input.value = seconds(range.inSeconds);
        outField.input.value = seconds(range.outSeconds);
        durationField.input.value = seconds(draft.lengthSeconds);
        impact.textContent = spec.impactText(draft);
        if (syncBar) {
            bar?.sync(draft);
        }
    };
    const setDraft = (next: SourceRange, syncBar = true): void => {
        draft = next;
        renderDraft(syncBar);
    };

    bar = buildTrimBar({
        range: draft,
        limits: spec.limits,
        onChange: (next) => {
            const before = toInOut(draft);
            const after = toInOut(next);
            setDraft(next, false);
            if (
                after.inSeconds !== before.inSeconds &&
                after.outSeconds === before.outSeconds
            ) {
                seek(after.inSeconds);
            } else if (
                after.outSeconds !== before.outSeconds &&
                after.inSeconds === before.inSeconds
            ) {
                seek(after.outSeconds);
            }
        },
    });
    const playhead = document.createElement("span");
    playhead.className = "vst-trim-playhead";
    playhead.setAttribute("aria-hidden", "true");
    bar.element
        .querySelector<HTMLElement>(".vst-trim-track")
        ?.appendChild(playhead);

    const paintPlayhead = (): void => {
        const at = Math.min(
            spec.limits.limitSeconds,
            Math.max(0, player.currentTime || 0),
        );
        timecode.textContent = `${seconds(at)} / ${seconds(spec.limits.limitSeconds)} s`;
        const percent =
            spec.limits.limitSeconds > 0
                ? (at / spec.limits.limitSeconds) * 100
                : 0;
        playhead.style.left = `${percent}%`;
        if (stopAt !== null && at >= stopAt) {
            player.pause();
            stopAt = null;
            player.currentTime = draft.startSeconds;
            paintPlayhead();
        }
    };
    const seek = (value: number): void => {
        player.currentTime = Math.min(
            spec.limits.limitSeconds,
            Math.max(0, value),
        );
        paintPlayhead();
    };
    const playRange = (): void => {
        const range = toInOut(draft);
        stopAt = range.outSeconds;
        seek(range.inSeconds);
        player.play()?.catch(() => {
            stopAt = null;
        });
    };

    const readNumber = (
        input: HTMLInputElement,
        apply: (value: number) => SourceRange,
    ): void => {
        const value = Number(input.value);
        if (Number.isFinite(value)) {
            setDraft(apply(value));
        }
    };
    inField.input.addEventListener("input", () =>
        readNumber(inField.input, (value) =>
            setInPoint(draft, value, spec.limits),
        ),
    );
    outField.input.addEventListener("input", () =>
        readNumber(outField.input, (value) =>
            setOutPoint(draft, value, spec.limits),
        ),
    );
    const reset = button(
        `Use full ${spec.mediaKind}`,
        "Reset In and Out to the whole source",
        "data-vst-trim-reset",
    );
    reset.addEventListener("click", () =>
        setDraft({
            startSeconds: 0,
            lengthSeconds: spec.limits.limitSeconds,
        }),
    );
    previousFrame.addEventListener("click", () =>
        seek(
            player.currentTime -
                (spec.limits.fps > 0 ? 1 / spec.limits.fps : 0.1),
        ),
    );
    nextFrame.addEventListener("click", () =>
        seek(
            player.currentTime +
                (spec.limits.fps > 0 ? 1 / spec.limits.fps : 0.1),
        ),
    );
    preview.addEventListener("click", playRange);
    markIn.addEventListener("click", () =>
        setDraft(markInPoint(draft, player.currentTime, spec.limits)),
    );
    markOut.addEventListener("click", () =>
        setDraft(setOutPoint(draft, player.currentTime, spec.limits)),
    );

    const footer = document.createElement("div");
    footer.className = "modal-footer vst-trim-modal-footer";
    const cancel = button("Cancel", "Close without applying");
    const apply = button("Apply", "Apply this range", "data-vst-trim-apply");
    apply.classList.add("vst-trim-modal-apply");
    footer.append(reset, cancel, apply);

    let managed: ManagedModal;
    managed = createManagedModal({
        modalClass: MODAL_CLASS,
        backdropClass: BACKDROP_CLASS,
        labelledBy: TITLE_ID,
        onKeyDown: (event) => {
            if (
                event.target instanceof HTMLInputElement ||
                event.target instanceof HTMLTextAreaElement ||
                event.target instanceof HTMLSelectElement
            ) {
                return;
            }
            const key = event.key.toLowerCase();
            if (key === "i") {
                event.preventDefault();
                setDraft(markInPoint(draft, player.currentTime, spec.limits));
            } else if (key === "o") {
                event.preventDefault();
                setDraft(setOutPoint(draft, player.currentTime, spec.limits));
            } else if (event.key === " ") {
                event.preventDefault();
                if (player.paused) {
                    playRange();
                } else {
                    player.pause();
                    stopAt = null;
                }
            }
        },
        onClose: () => {
            player.removeEventListener("timeupdate", paintPlayhead);
            player.removeEventListener("seeked", paintPlayhead);
            player.pause();
            player.removeAttribute("src");
            player.load();
            if (currentModal === managed) {
                currentModal = null;
            }
        },
    });
    currentModal = managed;
    managed.header.append(heading, close);
    managed.body.append(playerWrap, transport, bar.element, fields, impact);
    managed.content.appendChild(footer);

    const dismiss = (): void => managed.close();
    const applyAndClose = (): void => {
        const applied = { ...draft };
        dismiss();
        spec.onApply(applied);
    };
    close.addEventListener("click", dismiss);
    cancel.addEventListener("click", dismiss);
    apply.addEventListener("click", applyAndClose);
    player.addEventListener("loadedmetadata", () => seek(draft.startSeconds));
    player.addEventListener("timeupdate", paintPlayhead);
    player.addEventListener("seeked", paintPlayhead);
    player.addEventListener("error", () => {
        player.hidden = true;
        mediaError.hidden = false;
    });
    renderDraft();
    paintPlayhead();
    managed.open(player);
};
