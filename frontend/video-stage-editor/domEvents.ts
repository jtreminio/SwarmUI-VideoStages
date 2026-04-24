import { AUDIO_SOURCE_NATIVE } from "../AudioSourceController";
import { snapDurationToFps } from "../RenderUtils";
import type { Clip } from "../Types";
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
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";

export type DomEventsDeps = {
    getEditor: () => HTMLElement | null;
    getClips: () => Clip[];
    saveClips: (clips: Clip[]) => void;
    getState: () => import("../Types").VideoStagesConfig;
    saveState: (
        state: import("../Types").VideoStagesConfig,
        callbacks?: PersistenceCallbacks,
    ) => void;
    persistenceCallbacks?: PersistenceCallbacks;
    scheduleClipsRefresh: () => void;
    refUploadCache: RefUploadCacheApi;
};

export const getEditorActionTarget = (
    getEditor: () => HTMLElement | null,
    elem: HTMLElement,
): HTMLElement | null => {
    const editor = getEditor();
    if (!editor?.contains(elem)) {
        return null;
    }
    return elem;
};

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
    const fileInput = uploadField.querySelector(
        '.auto-file[data-ref-field="uploadFileName"]',
    ) as HTMLInputElement | null;
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
    const fileInput = uploadField.querySelector(
        `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"]`,
    ) as HTMLInputElement | null;
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
    const target = getEditorActionTarget(deps.getEditor, elem);
    if (!target) {
        return;
    }
    const clips = deps.getClips();

    const clipAction = target.dataset.clipAction;
    const stageAction = target.dataset.stageAction;
    const refAction = target.dataset.refAction;

    if (clipAction === "add-clip") {
        clips.push(
            buildDefaultClip(
                clips.length,
                getRootDefaults,
                getDefaultStageModel,
            ),
        );
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
    elem: HTMLElement | null,
    deps: DomEventsDeps,
    fromInputEvent = false,
): void => {
    if (!elem || !deps.getEditor()?.contains(elem)) {
        return;
    }
    const target = elem as
        | HTMLInputElement
        | HTMLSelectElement
        | HTMLTextAreaElement;
    const state = deps.getState();
    const clips = state.clips;
    const defaults = getRootDefaults();

    const clipField = target.dataset.clipField;
    const stageField = target.dataset.stageField;
    const refField = target.dataset.refField;

    const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
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
        const value = parseFloat(target.value);
        if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
            clip.duration = snapDurationToFps(value, defaults.fps);
            const frameMax = getReferenceFrameMax(getRootDefaults, clip);
            for (const ref of clip.refs) {
                ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
            }
        }
    } else if (clipField === "audioSource") {
        clip.audioSource = target.value || AUDIO_SOURCE_NATIVE;
    } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
        if (!(target instanceof HTMLInputElement) || target.type !== "file") {
            return;
        }
        if (target.dataset.filedata) {
            clip.uploadedAudio = {
                data: target.dataset.filedata,
                fileName: normalizeUploadFileName(
                    target.dataset.filename ?? target.files?.[0]?.name ?? null,
                ),
            };
        } else if (target.files && target.files.length > 0) {
            return;
        } else {
            clip.uploadedAudio = null;
        }
    } else if (refField) {
        const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
        if (refIdx < 0 || refIdx >= clip.refs.length) {
            return;
        }
        applyRefField(
            clip,
            clip.refs[refIdx],
            refField,
            target,
            fieldBindingDeps,
        );
        if (refField === "source") {
            syncRefUploadFieldVisibility(
                target,
                target.value,
                deps.refUploadCache,
            );
        }
    } else if (stageField) {
        const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return;
        }
        const stage = clip.stages[stageIdx];
        const stageCard = target.closest("section[data-stage-idx]");
        const methodSelect = stageCard?.querySelector(
            '[data-stage-field="upscaleMethod"]',
        ) as HTMLSelectElement | null;
        const preservedUpscaleMethod =
            stageField === "upscale"
                ? (methodSelect?.value ?? stage.upscaleMethod)
                : null;
        applyStageField(
            stage,
            stageField,
            target as HTMLInputElement | HTMLSelectElement,
            getRootDefaults,
        );
        if (stageField === "upscale") {
            if (preservedUpscaleMethod != null) {
                stage.upscaleMethod = preservedUpscaleMethod;
            }
            syncStageUpscaleMethodDisabled(target, stage.upscale);
            if (methodSelect && preservedUpscaleMethod != null) {
                methodSelect.value = preservedUpscaleMethod;
            }
        }
    } else {
        return;
    }

    deps.saveState(state, deps.persistenceCallbacks);
    if (clipField === "audioSource") {
        syncClipAudioUploadFieldVisibility(target, clip.audioSource);
    }
    const isSliderDrag =
        fromInputEvent &&
        target instanceof HTMLInputElement &&
        target.type === "range";
    const needsRerender =
        !isSliderDrag && clipField === "duration" && !fromInputEvent;
    if (needsRerender) {
        deps.scheduleClipsRefresh();
    }
};

export const attachEventListeners = (deps: DomEventsDeps): void => {
    const editor = deps.getEditor();
    if (!editor) {
        return;
    }
    if (editor.dataset.vsListenersAttached === "1") {
        return;
    }
    editor.dataset.vsListenersAttached = "1";

    editor.addEventListener("click", (event: MouseEvent) => {
        const target = event.target as Element | null;
        const refUploadRemoveButton = target?.closest(
            ".vs-ref-upload-field .auto-input-remove-button",
        ) as HTMLElement | null;
        if (refUploadRemoveButton) {
            handleRefUploadRemove(refUploadRemoveButton, deps);
            return;
        }
        const clipUploadRemoveButton = target?.closest(
            ".vs-clip-audio-upload-field .auto-input-remove-button",
        ) as HTMLElement | null;
        if (clipUploadRemoveButton) {
            handleClipAudioUploadRemove(clipUploadRemoveButton, deps);
            return;
        }
        const actionElem = target?.closest(
            "[data-clip-action], [data-stage-action], [data-ref-action]",
        ) as HTMLElement | null;
        if (actionElem) {
            event.preventDefault();
            event.stopPropagation();
            handleAction(actionElem, deps);
            return;
        }

        const clipHeader = target?.closest(
            ".vs-clip-card > .input-group-shrinkable",
        ) as HTMLElement | null;
        if (clipHeader) {
            event.stopPropagation();
            const group = clipHeader.closest(
                ".vs-clip-card",
            ) as HTMLElement | null;
            const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
            toggleClipExpanded(clipIdx, deps);
        }
    });

    editor.addEventListener("change", (event) => {
        handleFieldChange(event.target as HTMLElement | null, deps);
    });
    editor.addEventListener("input", (event) => {
        const target = event.target as HTMLElement | null;
        if (
            target instanceof HTMLInputElement &&
            (target.type === "number" || target.type === "range")
        ) {
            handleFieldChange(target, deps, true);
        }
    });
};
