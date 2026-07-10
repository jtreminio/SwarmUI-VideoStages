import { clamp, mediaPreviewSrc, REF_FRAME_MIN } from "./constants";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "./imageSource";
import {
    appendRefToClip,
    buildDefaultRef,
    getReferenceFrameMax,
    removeRefAt,
} from "./normalization";
import { getClips, saveClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { readStateToken } from "./swarmInputs";
import { keyframeLeftPercent, keyframeTimeSeconds } from "./timelineDetail";
import { pxToFrame } from "./timelineEdit";
import { type Clip, REF_SOURCE_UPLOAD, type UploadedAudio } from "./types";

const THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
const LANE_SELECTOR = ".vst-refs-lane[data-vst-ref-add]";
const EDITING_CLASS = "vst-refs-editing";
const DRAGGING_CLASS = "vst-refs-dragging";
const DRAG_THRESHOLD_PX = 5;

export interface TimelineReferencesTrack {
    attach(body: HTMLElement): void;
    dispose(): void;
}

interface RefDraft {
    source: string;
    frame: number;
    fromEnd: boolean;
    uploadFileName: string | null;
    uploadedImage: UploadedAudio | null;
}

const currentFps = (): number => {
    try {
        const fps = getRootDefaults().fps;
        return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
        return 24;
    }
};

const parseIntAttr = (el: Element | null, name: string): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

const draftFromRef = (clip: Clip, refIdx: number): RefDraft | null => {
    const ref = clip.refs?.[refIdx];
    if (!ref) {
        return null;
    }
    return {
        source: `${ref.source ?? ""}`,
        frame: ref.frame,
        fromEnd: ref.fromEnd === true,
        uploadFileName: ref.uploadFileName ?? null,
        uploadedImage: ref.uploadedImage ?? null,
    };
};

export const createTimelineReferencesTrack = (): TimelineReferencesTrack => {
    let boundBody: HTMLElement | null = null;
    let activeWrap: HTMLElement | null = null;
    let editingAnchor: HTMLElement | null = null;
    let outsideMouseHandler: ((event: MouseEvent) => void) | null = null;
    let suppressClick = false;
    let refDrag: {
        clipIdx: number;
        refIdx: number;
        mark: HTMLElement;
        arrow: HTMLElement | null;
        lane: HTMLElement;
        startX: number;
        originalLeft: string;
        arrowOriginalLeft: string;
        originalLabel: string;
        durationSeconds: number;
        fps: number;
        fromEnd: boolean;
        active: boolean;
        sourceJson: string;
    } | null = null;

    const findArrow = (clipIdx: number, refIdx: number): HTMLElement | null =>
        boundBody?.querySelector<HTMLElement>(
            `.vst-region[data-clip-idx="${clipIdx}"] .vst-key[data-ref-idx="${refIdx}"]`,
        ) ?? null;

    const positionRefMarker = (
        mark: HTMLElement,
        arrow: HTMLElement | null,
        frame: number,
        fromEnd: boolean,
        durationSeconds: number,
        fps: number,
    ): void => {
        const time = keyframeTimeSeconds(frame, fromEnd, durationSeconds, fps);
        const leftPct = `${keyframeLeftPercent(time, durationSeconds)}%`;
        mark.style.left = leftPct;
        if (arrow) {
            arrow.style.left = leftPct;
        }
        const ph = mark.querySelector<HTMLElement>(".vst-refs-ph");
        if (ph) {
            ph.textContent = `R ${fromEnd ? "-" : ""}${frame}`;
        }
    };

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

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

    const commit = (clipIdx: number, refIdx: number, draft: RefDraft): void => {
        const clips = getClips();
        const clip = clips[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!clip || !ref) {
            return;
        }
        const source = resolveImageSourceValue(
            draft.source,
            buildImageSourceOptions(draft.source),
        );
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        ref.source = source;
        ref.frame = clamp(Math.round(draft.frame), REF_FRAME_MIN, frameMax);
        ref.fromEnd = draft.fromEnd;
        if (source === REF_SOURCE_UPLOAD) {
            ref.uploadedImage = draft.uploadedImage;
            ref.uploadFileName =
                draft.uploadedImage?.fileName ?? draft.uploadFileName;
        } else {
            ref.uploadedImage = null;
            ref.uploadFileName = null;
        }
        saveClips(clips);
    };

    const addRefAtFrame = (
        clipIdx: number,
        frame: number,
        sourceJson: string,
    ): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
        }
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        const ref = buildDefaultRef();
        ref.frame = clamp(Math.round(frame), REF_FRAME_MIN, frameMax);
        appendRefToClip(clip, ref);
        saveClips(clips);
    };

    const deleteRef = (
        clipIdx: number,
        refIdx: number,
        sourceJson: string,
    ): void => {
        if (isStale(sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || !removeRefAt(clip, refIdx)) {
            return;
        }
        saveClips(clips);
    };

    const buildField = (label: string, control: HTMLElement): HTMLElement => {
        const row = document.createElement("div");
        row.className = "vst-audio-field";
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(text, control);
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

    const openEditor = (
        anchor: HTMLElement,
        clipIdx: number,
        refIdx: number,
    ): void => {
        closeEditor();
        const clip = getClips()[clipIdx];
        if (!clip) {
            return;
        }
        const draft = draftFromRef(clip, refIdx);
        if (!draft) {
            return;
        }
        const sourceJson = readStateToken();
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);

        const fps = currentFps();
        const anchorOriginalLeft = anchor.style.left;
        const arrowOriginalLeft = findArrow(clipIdx, refIdx)?.style.left ?? "";
        const originalLabel =
            anchor.querySelector<HTMLElement>(".vst-refs-ph")?.textContent ??
            "";
        const previewDraftPosition = (): void => {
            positionRefMarker(
                anchor,
                findArrow(clipIdx, refIdx),
                draft.frame,
                draft.fromEnd,
                clip.duration,
                fps,
            );
        };
        const restorePreview = (): void => {
            anchor.style.left = anchorOriginalLeft;
            const arrow = findArrow(clipIdx, refIdx);
            if (arrow) {
                arrow.style.left = arrowOriginalLeft;
            }
            const ph = anchor.querySelector<HTMLElement>(".vst-refs-ph");
            if (ph) {
                ph.textContent = originalLabel;
            }
        };

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
        wrap.className = "vst-prompt-inspector vst-refs-inspector";
        wrap.style.left = `${left}px`;
        wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
        wrap.style.width = `${width}px`;

        const head = document.createElement("div");
        head.className = "vst-prompt-inspector-head";
        head.textContent = `Clip ${clipIdx} · reference ${refIdx}`;

        const select = document.createElement("select");
        select.className = "vst-audio-select";
        const rebuildOptions = (): void => {
            const options = buildImageSourceOptions(draft.source);
            draft.source = resolveImageSourceValue(draft.source, options);
            select.innerHTML = "";
            for (const option of options) {
                const elem = document.createElement("option");
                elem.value = option.value;
                elem.textContent = option.label;
                elem.dataset.cleanname = option.label;
                elem.disabled = option.disabled === true;
                elem.selected = option.value === draft.source;
                select.appendChild(elem);
            }
        };
        rebuildOptions();
        const sourceField = buildField("Image Source", select);

        const preview = document.createElement("div");
        preview.className = "vst-refs-thumb-preview";

        const frameInput = document.createElement("input");
        frameInput.type = "number";
        frameInput.className = "vst-refs-num";
        frameInput.min = `${REF_FRAME_MIN}`;
        frameInput.max = `${frameMax}`;
        frameInput.step = "1";
        frameInput.value = `${draft.frame}`;
        const applyFrameInput = (normalize: boolean): void => {
            const parsed = Number.parseInt(frameInput.value, 10);
            draft.frame = clamp(
                Number.isFinite(parsed) ? parsed : draft.frame,
                REF_FRAME_MIN,
                frameMax,
            );
            if (normalize) {
                frameInput.value = `${draft.frame}`;
            }
            previewDraftPosition();
        };
        frameInput.addEventListener("input", () => applyFrameInput(false));
        frameInput.addEventListener("change", () => applyFrameInput(true));
        const frameField = buildField(
            `Attach at Frame (1–${frameMax})`,
            frameInput,
        );

        const fromEnd = buildCheckbox(
            "Count from clip end",
            draft.fromEnd,
            (value) => {
                draft.fromEnd = value;
                previewDraftPosition();
            },
        );

        const uploadRow = document.createElement("div");
        uploadRow.className = "vst-audio-field vst-audio-upload";
        const uploadLabel = document.createElement("span");
        uploadLabel.className = "vst-audio-field-label";
        uploadLabel.textContent = "Image Upload";
        const fileInput = document.createElement("input");
        fileInput.type = "file";
        fileInput.accept = "image/*";
        const fileName = document.createElement("span");
        fileName.className = "vst-audio-upload-name";
        const clearBtn = document.createElement("button");
        clearBtn.type = "button";
        clearBtn.className = "vst-audio-upload-clear";
        clearBtn.textContent = "Clear";
        const renderUploadState = (): void => {
            const name = draft.uploadedImage?.fileName;
            fileName.textContent = name ? name : "No file chosen";
            clearBtn.hidden = !draft.uploadedImage;
            const data = draft.uploadedImage?.data;
            if (data) {
                preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
                preview.classList.add("vst-refs-thumb-preview-set");
            } else {
                preview.style.backgroundImage = "";
                preview.classList.remove("vst-refs-thumb-preview-set");
            }
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
                draft.uploadedImage = { data, fileName: file.name };
                renderUploadState();
            };
            reader.readAsDataURL(file);
        });
        clearBtn.addEventListener("click", () => {
            draft.uploadedImage = null;
            draft.uploadFileName = null;
            fileInput.value = "";
            renderUploadState();
        });
        uploadRow.append(uploadLabel, fileInput, fileName, clearBtn);

        const deleteBtn = document.createElement("button");
        deleteBtn.type = "button";
        deleteBtn.className = "vst-refs-delete";
        deleteBtn.textContent = "Delete reference";

        const hint = document.createElement("div");
        hint.className = "vst-prompt-inspector-hint";
        hint.textContent = "Click away to apply · Esc to cancel";

        wrap.append(
            head,
            sourceField,
            preview,
            frameField,
            fromEnd.row,
            uploadRow,
            deleteBtn,
            hint,
        );

        const syncVisibility = (): void => {
            const isUpload = draft.source === REF_SOURCE_UPLOAD;
            uploadRow.hidden = !isUpload;
            preview.hidden = !isUpload;
            renderUploadState();
        };
        select.addEventListener("change", () => {
            draft.source = select.value;
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
            const committed = save && !isStale(sourceJson);
            closeEditor();
            if (committed) {
                commit(clipIdx, refIdx, draft);
            } else {
                restorePreview();
            }
        };

        deleteBtn.addEventListener("click", (event) => {
            event.preventDefault();
            if (done) {
                return;
            }
            done = true;
            closeEditor();
            deleteRef(clipIdx, refIdx, sourceJson);
        });

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
                target.closest(".vst-refs-inspector") ||
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

    const endRefDrag = (restore: boolean): void => {
        if (refDrag && restore) {
            refDrag.mark.style.left = refDrag.originalLeft;
            if (refDrag.arrow) {
                refDrag.arrow.style.left = refDrag.arrowOriginalLeft;
            }
            const ph = refDrag.mark.querySelector<HTMLElement>(".vst-refs-ph");
            if (ph) {
                ph.textContent = refDrag.originalLabel;
            }
        }
        refDrag = null;
        (boundBody ?? document.body).classList.remove(DRAGGING_CLASS);
    };

    const onBodyMouseDown = (event: Event): void => {
        const me = event as MouseEvent;
        if (me.button !== 0 || !(me.target instanceof Element)) {
            return;
        }
        const mark = me.target.closest(THUMB_SELECTOR);
        if (!(mark instanceof HTMLElement)) {
            return;
        }
        if (me.shiftKey) {
            me.preventDefault();
            return;
        }
        const lane = mark.closest(LANE_SELECTOR);
        const clipIdx = parseIntAttr(mark, "data-clip-idx");
        const refIdx = parseIntAttr(mark, "data-ref-idx");
        if (
            !(lane instanceof HTMLElement) ||
            clipIdx === null ||
            refIdx === null
        ) {
            return;
        }
        const clip = getClips()[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!clip || !ref) {
            return;
        }
        const arrow = findArrow(clipIdx, refIdx);
        refDrag = {
            clipIdx,
            refIdx,
            mark,
            arrow,
            lane,
            startX: me.clientX,
            originalLeft: mark.style.left,
            arrowOriginalLeft: arrow?.style.left ?? "",
            originalLabel:
                mark.querySelector<HTMLElement>(".vst-refs-ph")?.textContent ??
                "",
            durationSeconds: clip.duration,
            fps: currentFps(),
            fromEnd: ref.fromEnd === true,
            active: false,
            sourceJson: readStateToken(),
        };
        me.preventDefault();
    };

    const dragFrameAt = (clientX: number): number => {
        if (!refDrag) {
            return REF_FRAME_MIN;
        }
        const rect = refDrag.lane.getBoundingClientRect();
        return pxToFrame(
            clientX - rect.left,
            rect.width,
            refDrag.durationSeconds,
            refDrag.fps,
            refDrag.fromEnd,
        );
    };

    const onDocMouseMove = (event: Event): void => {
        if (!refDrag) {
            return;
        }
        const me = event as MouseEvent;
        if (!refDrag.active) {
            if (Math.abs(me.clientX - refDrag.startX) < DRAG_THRESHOLD_PX) {
                return;
            }
            refDrag.active = true;
            closeEditor();
            (boundBody ?? document.body).classList.add(DRAGGING_CLASS);
        }
        positionRefMarker(
            refDrag.mark,
            refDrag.arrow,
            dragFrameAt(me.clientX),
            refDrag.fromEnd,
            refDrag.durationSeconds,
            refDrag.fps,
        );
    };

    const onDocMouseUp = (event: Event): void => {
        if (!refDrag) {
            return;
        }
        const drag = refDrag;
        const newFrame = dragFrameAt((event as MouseEvent).clientX);
        if (!drag.active) {
            endRefDrag(true);
            return;
        }
        suppressClick = true;
        const clips = getClips();
        const ref = clips[drag.clipIdx]?.refs?.[drag.refIdx];
        if (isStale(drag.sourceJson) || !ref || ref.frame === newFrame) {
            endRefDrag(true);
            return;
        }
        endRefDrag(false);
        ref.frame = newFrame;
        saveClips(clips);
    };

    const onDocKeyDown = (event: Event): void => {
        if ((event as KeyboardEvent).key !== "Escape" || !refDrag) {
            return;
        }
        if (refDrag.active) {
            suppressClick = true;
        }
        endRefDrag(true);
    };

    const onBodyClick = (event: Event): void => {
        if (suppressClick) {
            suppressClick = false;
            return;
        }
        if (!(event.target instanceof Element)) {
            return;
        }
        const thumb = event.target.closest(THUMB_SELECTOR);
        if (thumb instanceof HTMLElement) {
            const clipIdx = parseIntAttr(thumb, "data-clip-idx");
            const refIdx = parseIntAttr(thumb, "data-ref-idx");
            if (clipIdx !== null && refIdx !== null) {
                if ((event as MouseEvent).shiftKey) {
                    deleteRef(clipIdx, refIdx, readStateToken());
                } else {
                    openEditor(thumb, clipIdx, refIdx);
                }
            }
            return;
        }
        const lane = event.target.closest(LANE_SELECTOR);
        if (!(lane instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseIntAttr(lane, "data-clip-idx");
        if (clipIdx === null) {
            return;
        }
        const clip = getClips()[clipIdx];
        if (!clip) {
            return;
        }
        const rect = lane.getBoundingClientRect();
        const frame = pxToFrame(
            (event as MouseEvent).clientX - rect.left,
            rect.width,
            clip.duration,
            currentFps(),
            false,
        );
        addRefAtFrame(clipIdx, frame, readStateToken());
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " ") {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        const thumb = ke.target.closest(THUMB_SELECTOR);
        if (!(thumb instanceof HTMLElement)) {
            return;
        }
        const clipIdx = parseIntAttr(thumb, "data-clip-idx");
        const refIdx = parseIntAttr(thumb, "data-ref-idx");
        if (clipIdx === null || refIdx === null) {
            return;
        }
        ke.preventDefault();
        openEditor(thumb, clipIdx, refIdx);
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
        body.addEventListener("mousedown", onBodyMouseDown);
        document.addEventListener("mousemove", onDocMouseMove);
        document.addEventListener("mouseup", onDocMouseUp);
        document.addEventListener("keydown", onDocKeyDown);
    };

    const dispose = (): void => {
        closeEditor();
        endRefDrag(false);
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody.removeEventListener("mousedown", onBodyMouseDown);
            boundBody = null;
        }
        document.removeEventListener("mousemove", onDocMouseMove);
        document.removeEventListener("mouseup", onDocMouseUp);
        document.removeEventListener("keydown", onDocKeyDown);
        suppressClick = false;
    };

    return { attach, dispose };
};
