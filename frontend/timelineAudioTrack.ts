import {
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
    resolveAudioSourceValue,
} from "./audioSource";
import { clamp } from "./constants";
import { getClips, saveClips } from "./persistence";
import { readVideoStagesSection } from "./swarmInputs";
import type { Clip, UploadedAudio } from "./types";

const CLIP_SELECTOR = '.vst-audio-clip[data-vst-audio="clip"]';
const EDITING_CLASS = "vst-audio-editing";

export interface TimelineAudioTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

interface AudioDraft {
    audioSource: string;
    reuseAudio: boolean;
    clipLengthFromAudio: boolean;
    clipLengthFromControlNet: boolean;
    saveAudioTrack: boolean;
    uploadedAudio: UploadedAudio | null;
}

const parseClipIdx = (el: Element | null): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute("data-clip-idx");
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

const draftFromClip = (clip: Clip): AudioDraft => ({
    audioSource: `${clip.audioSource ?? AUDIO_SOURCE_NATIVE}`,
    reuseAudio: clip.reuseAudio === true,
    clipLengthFromAudio: clip.clipLengthFromAudio === true,
    clipLengthFromControlNet: clip.clipLengthFromControlNet === true,
    saveAudioTrack: clip.saveAudioTrack === true,
    uploadedAudio: clip.uploadedAudio ?? null,
});

export const createTimelineAudioTrack = (): TimelineAudioTrack => {
    let boundBody: HTMLElement | null = null;
    let activeWrap: HTMLElement | null = null;
    let editingAnchor: HTMLElement | null = null;
    let outsideMouseHandler: ((event: MouseEvent) => void) | null = null;

    const isStale = (sourceJson: string): boolean =>
        readVideoStagesSection() !== sourceJson;

    const closeEditor = (): void => {
        if (outsideMouseHandler) {
            document.removeEventListener(
                "mousedown",
                outsideMouseHandler,
                true,
            );
            outsideMouseHandler = null;
        }
        if (editingAnchor) {
            editingAnchor.classList.remove(EDITING_CLASS);
            editingAnchor = null;
        }
        if (activeWrap) {
            activeWrap.remove();
            activeWrap = null;
        }
    };

    const commit = (clipIdx: number, draft: AudioDraft): void => {
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
        }
        const source = resolveAudioSourceValue(
            draft.audioSource,
            buildAudioSourceOptions(draft.audioSource, {
                controlNetEnabled: `${clip.controlNetLora ?? ""}`.trim() !== "",
            }),
        );
        const canLength = canUseClipLengthFromAudio(source);
        const isAce = isAceStepFunAudioSource(source);
        clip.audioSource = source;
        clip.reuseAudio = draft.reuseAudio;
        clip.clipLengthFromAudio = canLength && draft.clipLengthFromAudio;
        if (clip.clipLengthFromAudio) {
            clip.clipLengthFromControlNet = false;
        }
        clip.saveAudioTrack = isAce && draft.saveAudioTrack;
        clip.uploadedAudio =
            source === AUDIO_SOURCE_UPLOAD ? draft.uploadedAudio : null;
        saveClips(clips);
    };

    const buildField = (
        label: string,
        control: HTMLElement,
        hint?: string,
    ): HTMLElement => {
        const row = document.createElement("div");
        row.className = "vst-audio-field";
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(text, control);
        if (hint) {
            const small = document.createElement("small");
            small.className = "vst-audio-field-hint";
            small.textContent = hint;
            row.appendChild(small);
        }
        return row;
    };

    const buildCheckbox = (
        label: string,
        checked: boolean,
        onChange: (value: boolean) => void,
    ): { row: HTMLElement; input: HTMLInputElement } => {
        const row = document.createElement("label");
        row.className = "vst-audio-field vst-audio-field-check";
        const input = document.createElement("input");
        input.type = "checkbox";
        input.checked = checked;
        input.addEventListener("change", () => onChange(input.checked));
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(input, text);
        return { row, input };
    };

    const openEditor = (anchor: HTMLElement, clipIdx: number): void => {
        closeEditor();
        const clip = getClips()[clipIdx];
        if (!clip) {
            return;
        }
        const sourceJson = readVideoStagesSection();
        const draft = draftFromClip(clip);
        const controlNetEnabled = `${clip.controlNetLora ?? ""}`.trim() !== "";

        const host = boundBody ?? document.body;
        const hostRect = host.getBoundingClientRect();
        const viewportW =
            window.innerWidth || document.documentElement.clientWidth;
        const width = clamp(Math.round(hostRect.width - 32), 260, 420);
        const left = clamp(
            Math.round(hostRect.left + (hostRect.width - width) / 2),
            8,
            Math.max(8, viewportW - width - 8),
        );

        const wrap = document.createElement("div");
        wrap.className = "vst-prompt-inspector vst-audio-inspector";
        wrap.style.left = `${left}px`;
        wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
        wrap.style.width = `${width}px`;

        const head = document.createElement("div");
        head.className = "vst-prompt-inspector-head";
        head.textContent = `Clip ${clipIdx} · audio`;

        const select = document.createElement("select");
        select.className = "vst-audio-select";
        const rebuildOptions = (): void => {
            const options = buildAudioSourceOptions(draft.audioSource, {
                controlNetEnabled,
            });
            draft.audioSource = resolveAudioSourceValue(
                draft.audioSource,
                options,
            );
            select.innerHTML = "";
            for (const option of options) {
                const elem = document.createElement("option");
                elem.value = option.value;
                elem.textContent = option.label;
                elem.dataset.cleanname = option.label;
                elem.selected = option.value === draft.audioSource;
                select.appendChild(elem);
            }
        };
        rebuildOptions();
        const sourceField = buildField("Audio Source", select);

        const reuse = buildCheckbox(
            "Reuse Audio",
            draft.reuseAudio,
            (value) => {
                draft.reuseAudio = value;
            },
        );

        const lengthCheck = buildCheckbox(
            "Clip Length from Audio",
            draft.clipLengthFromAudio,
            (value) => {
                draft.clipLengthFromAudio = value;
            },
        );

        const saveCheck = buildCheckbox(
            "Save Audio Track",
            draft.saveAudioTrack,
            (value) => {
                draft.saveAudioTrack = value;
            },
        );

        const uploadRow = document.createElement("div");
        uploadRow.className = "vst-audio-field vst-audio-upload";
        const uploadLabel = document.createElement("span");
        uploadLabel.className = "vst-audio-field-label";
        uploadLabel.textContent = "Audio Upload";
        const fileInput = document.createElement("input");
        fileInput.type = "file";
        fileInput.accept = "audio/*";
        const fileName = document.createElement("span");
        fileName.className = "vst-audio-upload-name";
        const clearBtn = document.createElement("button");
        clearBtn.type = "button";
        clearBtn.className = "vst-audio-upload-clear";
        clearBtn.textContent = "Clear";
        const renderUploadName = (): void => {
            const name = draft.uploadedAudio?.fileName;
            fileName.textContent = name ? name : "No file chosen";
            clearBtn.hidden = !draft.uploadedAudio;
        };
        fileInput.addEventListener("change", () => {
            const file = fileInput.files?.[0];
            if (!file) {
                return;
            }
            const reader = new FileReader();
            reader.onload = () => {
                const data = `${reader.result ?? ""}`;
                if (!data) {
                    return;
                }
                draft.uploadedAudio = { data, fileName: file.name };
                renderUploadName();
            };
            reader.readAsDataURL(file);
        });
        clearBtn.addEventListener("click", () => {
            draft.uploadedAudio = null;
            fileInput.value = "";
            renderUploadName();
        });
        renderUploadName();
        uploadRow.append(uploadLabel, fileInput, fileName, clearBtn);

        const hint = document.createElement("div");
        hint.className = "vst-prompt-inspector-hint";
        hint.textContent = "Click away to apply · Esc to cancel";

        wrap.append(
            head,
            sourceField,
            reuse.row,
            lengthCheck.row,
            saveCheck.row,
            uploadRow,
            hint,
        );

        const syncVisibility = (): void => {
            const canLength = canUseClipLengthFromAudio(draft.audioSource);
            lengthCheck.input.disabled = !canLength;
            lengthCheck.row.classList.toggle("vst-audio-disabled", !canLength);
            if (!canLength) {
                lengthCheck.input.checked = false;
                draft.clipLengthFromAudio = false;
            }
            const isAce = isAceStepFunAudioSource(draft.audioSource);
            saveCheck.input.disabled = !isAce;
            saveCheck.row.classList.toggle("vst-audio-disabled", !isAce);
            if (!isAce) {
                saveCheck.input.checked = false;
                draft.saveAudioTrack = false;
            }
            uploadRow.hidden = draft.audioSource !== AUDIO_SOURCE_UPLOAD;
        };
        select.addEventListener("change", () => {
            draft.audioSource = select.value;
            syncVisibility();
        });
        syncVisibility();

        anchor.classList.add(EDITING_CLASS);
        editingAnchor = anchor;
        let done = false;
        const finish = (save: boolean): void => {
            if (done) {
                return;
            }
            done = true;
            closeEditor();
            if (save && !isStale(sourceJson)) {
                commit(clipIdx, draft);
            }
        };

        wrap.addEventListener("keydown", (event) => {
            if (event.key === "Escape") {
                event.preventDefault();
                finish(false);
            } else if (
                event.key === "Enter" &&
                !(event.target instanceof HTMLSelectElement)
            ) {
                event.preventDefault();
                finish(true);
            }
            event.stopPropagation();
        });
        const onOutside = (event: MouseEvent): void => {
            const target = event.target;
            if (!(target instanceof Element)) {
                return;
            }
            if (
                target.closest(".vst-audio-inspector") ||
                target.closest(".sui-popover")
            ) {
                return;
            }
            finish(true);
        };
        outsideMouseHandler = onOutside;
        document.addEventListener("mousedown", onOutside, true);

        document.body.appendChild(wrap);
        activeWrap = wrap;
        select.focus();
    };

    const onBodyClick = (event: Event): void => {
        if (!(event.target instanceof Element)) {
            return;
        }
        const seg = event.target.closest(CLIP_SELECTOR);
        if (!(seg instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseClipIdx(seg);
        if (clipIdx === null) {
            return;
        }
        openEditor(seg, clipIdx);
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " ") {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        const seg = ke.target.closest(CLIP_SELECTOR);
        if (!(seg instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseClipIdx(seg);
        if (clipIdx === null) {
            return;
        }
        ke.preventDefault();
        openEditor(seg, clipIdx);
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
    };

    const dispose = (): void => {
        closeEditor();
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody = null;
        }
    };

    return { attach, dispose };
};
