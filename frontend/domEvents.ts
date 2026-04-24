import { AUDIO_SOURCE_NATIVE } from "./audioSource";
import {
    CLIP_AUDIO_UPLOAD_FIELD,
    CLIP_DURATION_MIN,
    clamp,
    normalizeUploadFileName,
    REF_FRAME_MIN,
    refUploadKey,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import {
    applyRefField,
    applyStageField,
    syncClipAudioUploadFieldVisibility,
    syncRefUploadFieldVisibility,
    syncStageUpscaleMethodDisabled,
} from "./fieldBinding";
import {
    buildDefaultClip,
    buildDefaultRef,
    buildDefaultStage,
    getReferenceFrameMax,
} from "./normalization";
import type { PersistenceCallbacks } from "./persistence";
import type { RefUploadCacheApi } from "./refUploadCache";
import { snapDurationToFps } from "./renderUtils";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import type { Clip, VideoStagesConfig } from "./types";

export type DomEventsDeps = {
    /** Re-resolve `#videostages_stage_editor` after SwarmUI rebuilds the param panel. */
    ensureEditorRoot: (preferredRoot?: HTMLElement | null) => void;
    getEditor: () => HTMLElement | null;
    getClips: () => Clip[];
    saveClips: (clips: Clip[]) => void;
    getState: () => VideoStagesConfig;
    saveState: (
        state: VideoStagesConfig,
        callbacks?: PersistenceCallbacks,
    ) => void;
    persistenceCallbacks?: PersistenceCallbacks;
    scheduleClipsRefresh: () => void;
    refUploadCache: RefUploadCacheApi;
};

type FieldTarget = HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
type StageFieldTarget = HTMLInputElement | HTMLSelectElement;

const isFieldTarget = (value: EventTarget | null): value is FieldTarget =>
    value instanceof HTMLInputElement ||
    value instanceof HTMLSelectElement ||
    value instanceof HTMLTextAreaElement;

const isStageFieldTarget = (value: FieldTarget): value is StageFieldTarget =>
    value instanceof HTMLInputElement || value instanceof HTMLSelectElement;

export const toggleClipExpanded = (
    clipIdx: number,
    deps: DomEventsDeps,
): void => {
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
    }
    clips[clipIdx].expanded = !clips[clipIdx].expanded;
    deps.saveClips(clips);
    deps.scheduleClipsRefresh();
};

export const handleRefUploadRemove = (
    elem: HTMLElement,
    deps: DomEventsDeps,
): void => {
    const uploadField = elem.closest(".vs-ref-upload-field");
    if (!(uploadField instanceof HTMLElement)) {
        return;
    }
    const fileInput = uploadField.querySelector<HTMLInputElement>(
        '.auto-file[data-ref-field="uploadFileName"]',
    );
    if (!fileInput) {
        return;
    }
    const clipIdx = parseInt(fileInput.dataset.clipIdx ?? "-1", 10);
    const refIdx = parseInt(fileInput.dataset.refIdx ?? "-1", 10);
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
    }
    if (refIdx < 0 || refIdx >= clips[clipIdx].refs.length) {
        return;
    }

    clips[clipIdx].refs[refIdx].uploadFileName = null;
    clips[clipIdx].refs[refIdx].uploadedImage = null;
    deps.refUploadCache.delete(refUploadKey(clipIdx, refIdx));
    deps.saveClips(clips);
};

export const handleClipAudioUploadRemove = (
    elem: HTMLElement,
    deps: DomEventsDeps,
): void => {
    const uploadField = elem.closest(".vs-clip-audio-upload-field");
    if (!(uploadField instanceof HTMLElement)) {
        return;
    }
    const fileInput = uploadField.querySelector<HTMLInputElement>(
        `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"]`,
    );
    if (!fileInput) {
        return;
    }
    const clipIdx = parseInt(fileInput.dataset.clipIdx ?? "-1", 10);
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
    }

    clips[clipIdx].uploadedAudio = null;
    deps.saveClips(clips);
};

export const handleAction = (elem: HTMLElement, deps: DomEventsDeps): void => {
    const target = elem;
    const clips = deps.getClips();

    const clipAction = target.dataset.clipAction;
    const stageAction = target.dataset.stageAction;
    const refAction = target.dataset.refAction;

    if (clipAction === "add-clip") {
        clips.push(buildDefaultClip(getRootDefaults, getDefaultStageModel));
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }

    const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
        deps.scheduleClipsRefresh();
        return;
    }
    const clip = clips[clipIdx];

    if (clipAction === "delete") {
        if (clips.length <= 1) {
            return;
        }
        clips.splice(clipIdx, 1);
        deps.refUploadCache.reindexAfterClipDelete(clipIdx);
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }
    if (clipAction === "skip") {
        clip.skipped = !clip.skipped;
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }
    if (clipAction === "add-stage") {
        const previousStage =
            clip.stages.length > 0 ? clip.stages[clip.stages.length - 1] : null;
        clip.stages.push(
            buildDefaultStage(
                getRootDefaults,
                getDefaultStageModel,
                previousStage,
                clip.refs.length,
            ),
        );
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }
    if (clipAction === "add-ref") {
        clip.refs.push(buildDefaultRef());
        for (const stage of clip.stages) {
            stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
        }
        deps.refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }

    if (refAction) {
        const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
        if (refIdx < 0 || refIdx >= clip.refs.length) {
            deps.scheduleClipsRefresh();
            return;
        }
        const ref = clip.refs[refIdx];
        if (refAction === "delete") {
            clip.refs.splice(refIdx, 1);
            for (const stage of clip.stages) {
                if (refIdx < stage.refStrengths.length) {
                    stage.refStrengths.splice(refIdx, 1);
                }
            }
            deps.refUploadCache.reindexAfterRefDelete(clipIdx, refIdx);
        } else if (refAction === "toggle-collapse") {
            ref.expanded = !ref.expanded;
        }
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
        return;
    }

    if (stageAction) {
        const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            deps.scheduleClipsRefresh();
            return;
        }
        const stage = clip.stages[stageIdx];
        if (stageAction === "delete") {
            if (clip.stages.length <= 1) {
                return;
            }
            clip.stages.splice(stageIdx, 1);
        } else if (stageAction === "skip") {
            stage.skipped = !stage.skipped;
        } else if (stageAction === "toggle-collapse") {
            stage.expanded = !stage.expanded;
        }
        deps.saveClips(clips);
        deps.scheduleClipsRefresh();
    }
};

export const handleFieldChange = (
    elem: EventTarget | null,
    deps: DomEventsDeps,
    fromInputEvent = false,
): void => {
    if (!isFieldTarget(elem) || !deps.getEditor()?.contains(elem)) {
        return;
    }
    const state = deps.getState();
    const clips = state.clips;
    const defaults = getRootDefaults();

    const clipField = elem.dataset.clipField;
    const stageField = elem.dataset.stageField;
    const refField = elem.dataset.refField;

    const clipIdx = parseInt(elem.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
    }
    const clip = clips[clipIdx];

    const fieldBindingDeps = {
        getRootDefaults,
        refUploadCache: deps.refUploadCache,
        getClips: deps.getClips,
        saveClips: deps.saveClips,
    };

    if (clipField === "duration") {
        const value = parseFloat(elem.value);
        if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
            clip.duration = snapDurationToFps(value, defaults.fps);
            const frameMax = getReferenceFrameMax(getRootDefaults, clip);
            for (const ref of clip.refs) {
                ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
            }
        }
    } else if (clipField === "audioSource") {
        clip.audioSource = elem.value || AUDIO_SOURCE_NATIVE;
    } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
        if (!(elem instanceof HTMLInputElement) || elem.type !== "file") {
            return;
        }
        if (elem.dataset.filedata) {
            clip.uploadedAudio = {
                data: elem.dataset.filedata,
                fileName: normalizeUploadFileName(
                    elem.dataset.filename ?? elem.files?.[0]?.name ?? null,
                ),
            };
        } else if (elem.files?.length) {
            return;
        } else {
            clip.uploadedAudio = null;
        }
    } else if (refField) {
        const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
        if (refIdx < 0 || refIdx >= clip.refs.length) {
            return;
        }
        applyRefField(
            clip,
            clip.refs[refIdx],
            refField,
            elem,
            fieldBindingDeps,
        );
        if (refField === "source") {
            syncRefUploadFieldVisibility(elem, elem.value, deps.refUploadCache);
        }
    } else if (stageField) {
        const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return;
        }
        if (!isStageFieldTarget(elem)) {
            return;
        }
        const stage = clip.stages[stageIdx];
        const stageCard = elem.closest("section[data-stage-idx]");
        const methodSelect = stageCard?.querySelector<HTMLSelectElement>(
            '[data-stage-field="upscaleMethod"]',
        );
        const preservedUpscaleMethod =
            stageField === "upscale"
                ? (methodSelect?.value ?? stage.upscaleMethod)
                : null;
        applyStageField(stage, stageField, elem, getRootDefaults);
        if (stageField === "upscale") {
            if (preservedUpscaleMethod != null) {
                stage.upscaleMethod = preservedUpscaleMethod;
            }
            syncStageUpscaleMethodDisabled(elem, stage.upscale);
            if (methodSelect && preservedUpscaleMethod != null) {
                methodSelect.value = preservedUpscaleMethod;
            }
        }
    } else {
        return;
    }

    deps.saveState(state, deps.persistenceCallbacks);
    if (clipField === "audioSource") {
        syncClipAudioUploadFieldVisibility(elem, clip.audioSource);
    }
    const isSliderDrag =
        fromInputEvent &&
        elem instanceof HTMLInputElement &&
        elem.type === "range";
    const needsRerender =
        !isSliderDrag && clipField === "duration" && !fromInputEvent;
    if (needsRerender) {
        deps.scheduleClipsRefresh();
    }
};

let latestDomEventDeps: DomEventsDeps | null = null;
let stageEditorDocumentClickBound = false;
const stageEditorsWithFieldListeners = new WeakSet<HTMLElement>();

const getClickTargetElement = (event: MouseEvent): Element | null => {
    if (event.target instanceof Element) {
        return event.target;
    }
    if (event.target instanceof Node) {
        return event.target.parentElement;
    }
    const path = event.composedPath();
    for (const entry of path) {
        if (entry instanceof Element) {
            return entry;
        }
        if (entry instanceof Node && entry.parentElement) {
            return entry.parentElement;
        }
    }
    return null;
};

const handleStageEditorDocumentClick = (event: MouseEvent): void => {
    const deps = latestDomEventDeps;
    if (!deps) {
        return;
    }
    const target = getClickTargetElement(event);
    if (!target) {
        return;
    }
    const host = target.closest("#videostages_stage_editor");
    if (!(host instanceof HTMLElement) || !host.isConnected) {
        return;
    }
    deps.ensureEditorRoot(host);

    const refUploadRemoveButton = target.closest<HTMLElement>(
        ".vs-ref-upload-field .auto-input-remove-button",
    );
    if (refUploadRemoveButton) {
        handleRefUploadRemove(refUploadRemoveButton, deps);
        return;
    }
    const clipUploadRemoveButton = target.closest<HTMLElement>(
        ".vs-clip-audio-upload-field .auto-input-remove-button",
    );
    if (clipUploadRemoveButton) {
        handleClipAudioUploadRemove(clipUploadRemoveButton, deps);
        return;
    }
    const actionElem = target.closest<HTMLElement>(
        "[data-clip-action], [data-stage-action], [data-ref-action]",
    );
    if (actionElem) {
        event.preventDefault();
        event.stopPropagation();
        handleAction(actionElem, deps);
        return;
    }

    const clipHeader = target.closest<HTMLElement>(
        ".vs-clip-card > .input-group-shrinkable",
    );
    if (clipHeader) {
        event.stopPropagation();
        const group = clipHeader.closest<HTMLElement>(".vs-clip-card");
        const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
        toggleClipExpanded(clipIdx, deps);
    }
};

export const attachEventListeners = (deps: DomEventsDeps): void => {
    latestDomEventDeps = deps;
    deps.ensureEditorRoot();
    const editor = deps.getEditor();
    if (!editor) {
        return;
    }
    if (!stageEditorDocumentClickBound) {
        stageEditorDocumentClickBound = true;
        document.addEventListener(
            "click",
            handleStageEditorDocumentClick,
            true,
        );
    }

    if (stageEditorsWithFieldListeners.has(editor)) {
        return;
    }
    stageEditorsWithFieldListeners.add(editor);

    editor.addEventListener("change", (event: Event) => {
        handleFieldChange(event.target, deps);
    });
    editor.addEventListener(
        "change",
        (event: Event) => {
            const inputTarget = event.target;
            if (!isFieldTarget(inputTarget)) {
                return;
            }
            if (
                !(inputTarget instanceof HTMLInputElement) ||
                inputTarget.type !== "range" ||
                event.bubbles
            ) {
                return;
            }
            handleFieldChange(inputTarget, deps, true);
        },
        true,
    );
    editor.addEventListener("input", (event: Event) => {
        const inputTarget = event.target;
        if (!isFieldTarget(inputTarget)) {
            return;
        }
        if (
            inputTarget instanceof HTMLInputElement &&
            (inputTarget.type === "number" || inputTarget.type === "range")
        ) {
            handleFieldChange(inputTarget, deps, true);
        }
    });
};
