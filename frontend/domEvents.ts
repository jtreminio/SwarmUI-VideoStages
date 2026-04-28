import {
    AUDIO_SOURCE_NATIVE,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
} from "./audioSource";
import {
    CLIP_AUDIO_UPLOAD_FIELD,
    CLIP_DURATION_MIN,
    clamp,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    normalizeUploadFileName,
    REF_FRAME_MIN,
    refUploadKey,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import { videoStagesDebugLog } from "./debugLog";
import {
    type ApplyRefFieldDeps,
    applyRefField,
    applyStageField,
    syncClipAudioUploadFieldVisibility,
    syncClipDurationDisabled,
    syncRefUploadFieldVisibility,
    syncStageUpscaleMethodDisabled,
} from "./fieldBinding";
import {
    buildDefaultClip,
    buildDefaultRef,
    buildDefaultStage,
    getReferenceFrameMax,
    normalizeControlNetSource,
    normalizeOptionalModelName,
} from "./normalization";
import type { SaveStateOptions } from "./persistence";
import type { RefUploadCacheApi } from "./refUploadCache";
import { snapDurationToFps } from "./renderUtils";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import { isImageToVideoWorkflow, isVideoStagesEnabled } from "./swarmInputs";
import type { Clip, VideoStagesConfig } from "./types";

const changeFieldEventsHandled = new WeakMap<Event, void>();

type FieldChangeSourceEvent = Event & {
    /** Tests only: jsdom cannot set `isTrusted`; opt in to host notify when the group toggle is off. */
    __videoStagesSimulateUserFieldChange?: boolean;
};

const resolveHostNotifyForHandleFieldChange = (
    deps: DomEventsDeps,
    sourceEvent: Event | null | undefined,
): boolean => {
    if (deps.shouldSuppressClipsHostNotify?.() === true) {
        return false;
    }
    if (isVideoStagesEnabled()) {
        return true;
    }
    const ev = sourceEvent as FieldChangeSourceEvent | null | undefined;
    if (ev?.__videoStagesSimulateUserFieldChange === true) {
        return true;
    }
    return ev?.isTrusted === true;
};

export type DomEventsDeps = {
    /** Re-resolve `#videostages_stage_editor` after SwarmUI rebuilds the param panel. */
    ensureEditorRoot: (preferredRoot?: HTMLElement | null) => void;
    getEditor: () => HTMLElement | null;
    getClips: () => Clip[];
    saveClips: (clips: Clip[], options?: SaveStateOptions) => void;
    getState: () => VideoStagesConfig;
    saveState: (state: VideoStagesConfig, options?: SaveStateOptions) => void;
    scheduleClipsRefresh: () => void;
    refUploadCache: RefUploadCacheApi;
    /** While true, `handleFieldChange` must not call `triggerChangeFor` on the clips input (e.g. during `renderClips`). */
    shouldSuppressClipsHostNotify?: () => boolean;
};

type FieldTarget = HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
type StageFieldTarget = HTMLInputElement | HTMLSelectElement;

const isFieldTarget = (value: EventTarget | null): value is FieldTarget =>
    value instanceof HTMLInputElement ||
    value instanceof HTMLSelectElement ||
    value instanceof HTMLTextAreaElement;

const isStageFieldTarget = (value: FieldTarget): value is StageFieldTarget =>
    value instanceof HTMLInputElement || value instanceof HTMLSelectElement;

const isSliderNumericInput = (
    value: EventTarget | null,
): value is HTMLInputElement =>
    value instanceof HTMLInputElement &&
    (value.type === "number" || value.type === "range");

const isDurationInput = (
    value: EventTarget | null,
): value is HTMLInputElement =>
    isSliderNumericInput(value) && value.dataset.clipField === "duration";

const isClipAudioUploadInput = (
    value: EventTarget | null,
): value is HTMLInputElement =>
    value instanceof HTMLInputElement &&
    value.type === "file" &&
    value.dataset.clipField === CLIP_AUDIO_UPLOAD_FIELD;

const cacheClipAudioSelection = (
    clipIdx: number,
    fileInput: HTMLInputElement,
    deps: Pick<DomEventsDeps, "getClips" | "saveClips">,
): void => {
    const file = fileInput.files?.[0];
    if (!file) {
        return;
    }

    const reader = new FileReader();
    reader.addEventListener("load", () => {
        if (typeof reader.result !== "string") {
            return;
        }
        const clips = deps.getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        clips[clipIdx].uploadedAudio = {
            data: reader.result,
            fileName: normalizeUploadFileName(file.name),
        };
        deps.saveClips(clips);
    });
    reader.readAsDataURL(file);
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
    deps.saveClips(clips, { notifyDomChange: true });
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
    deps.saveClips(clips, { notifyDomChange: true });
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
    deps.saveClips(clips, { notifyDomChange: true });
};

export const handleAction = (elem: HTMLElement, deps: DomEventsDeps): void => {
    const target = elem;
    const clips = deps.getClips();

    const clipAction = target.dataset.clipAction;
    const stageAction = target.dataset.stageAction;
    const refAction = target.dataset.refAction;

    if (clipAction === "add-clip") {
        clips.push(
            buildDefaultClip(
                getRootDefaults,
                getDefaultStageModel,
                isImageToVideoWorkflow(),
            ),
        );
        deps.saveClips(clips, { notifyDomChange: true });
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
        clips.splice(clipIdx, 1);
        deps.refUploadCache.reindexAfterClipDelete(clipIdx);
        deps.saveClips(clips, { notifyDomChange: true });
        deps.scheduleClipsRefresh();
        return;
    }
    if (clipAction === "skip") {
        clip.skipped = !clip.skipped;
        deps.saveClips(clips, { notifyDomChange: true });
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
        deps.saveClips(clips, { notifyDomChange: true });
        deps.scheduleClipsRefresh();
        return;
    }
    if (clipAction === "add-ref") {
        clip.refs.push(buildDefaultRef());
        for (const stage of clip.stages) {
            stage.refStrengths.push(
                isImageToVideoWorkflow()
                    ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH
                    : STAGE_REF_STRENGTH_DEFAULT,
            );
        }
        deps.refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
        deps.saveClips(clips, { notifyDomChange: true });
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
        deps.saveClips(clips, { notifyDomChange: true });
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
            clip.stages.splice(stageIdx, 1);
        } else if (stageAction === "skip") {
            stage.skipped = !stage.skipped;
        } else if (stageAction === "toggle-collapse") {
            stage.expanded = !stage.expanded;
        }
        deps.saveClips(clips, { notifyDomChange: true });
        deps.scheduleClipsRefresh();
    }
};

type DatasetFieldChangeContext = {
    elem: FieldTarget;
    clip: Clip;
    clipIdx: number;
    clipField: string | undefined;
    stageField: string | undefined;
    refField: string | undefined;
    fieldBindingDeps: ApplyRefFieldDeps;
};

const applyDatasetFieldChange = (ctx: DatasetFieldChangeContext): boolean => {
    const {
        elem,
        clip,
        clipIdx,
        clipField,
        stageField,
        refField,
        fieldBindingDeps,
    } = ctx;

    if (clipField === "duration") {
        const value = parseFloat(elem.value);
        if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
            const rootDefaults = getRootDefaults();
            clip.duration = snapDurationToFps(value, rootDefaults.fps);
            const frameMax = getReferenceFrameMax(getRootDefaults, clip);
            for (const ref of clip.refs) {
                ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
            }
        }
    } else if (clipField === "audioSource") {
        clip.audioSource = elem.value || AUDIO_SOURCE_NATIVE;
        if (!isAceStepFunAudioSource(clip.audioSource)) {
            clip.saveAudioTrack = false;
            const saveAudioTrack = elem
                .closest(".vs-clip-card")
                ?.querySelector<HTMLInputElement>(
                    '[data-clip-field="saveAudioTrack"]',
                );
            if (saveAudioTrack) {
                saveAudioTrack.checked = false;
            }
        }
        if (!canUseClipLengthFromAudio(clip.audioSource)) {
            clip.clipLengthFromAudio = false;
            const clipLengthFromAudio = elem
                .closest(".vs-clip-card")
                ?.querySelector<HTMLInputElement>(
                    '[data-clip-field="clipLengthFromAudio"]',
                );
            if (clipLengthFromAudio) {
                clipLengthFromAudio.checked = false;
            }
        }
    } else if (clipField === "controlNetSource") {
        clip.controlNetSource = normalizeControlNetSource(elem.value);
    } else if (clipField === "controlNetLora") {
        clip.controlNetLora = normalizeOptionalModelName(elem.value);
    } else if (clipField === "saveAudioTrack") {
        clip.saveAudioTrack =
            elem instanceof HTMLInputElement &&
            isAceStepFunAudioSource(clip.audioSource)
                ? !!elem.checked
                : false;
        if (elem instanceof HTMLInputElement && !clip.saveAudioTrack) {
            elem.checked = false;
        }
    } else if (clipField === "reuseAudio") {
        clip.reuseAudio = elem instanceof HTMLInputElement && !!elem.checked;
    } else if (clipField === "clipLengthFromAudio") {
        clip.clipLengthFromAudio =
            elem instanceof HTMLInputElement &&
            canUseClipLengthFromAudio(clip.audioSource)
                ? !!elem.checked
                : false;
        if (elem instanceof HTMLInputElement && !clip.clipLengthFromAudio) {
            elem.checked = false;
        }
    } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
        if (!(elem instanceof HTMLInputElement) || elem.type !== "file") {
            return false;
        }
        if (elem.dataset.filedata) {
            clip.uploadedAudio = {
                data: elem.dataset.filedata,
                fileName: normalizeUploadFileName(
                    elem.dataset.filename ?? elem.files?.[0]?.name ?? null,
                ),
            };
        } else if (elem.files?.length) {
            cacheClipAudioSelection(clipIdx, elem, {
                getClips: fieldBindingDeps.getClips,
                saveClips: fieldBindingDeps.saveClips,
            });
            return false;
        } else {
            clip.uploadedAudio = null;
        }
    } else if (refField) {
        const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
        if (refIdx < 0 || refIdx >= clip.refs.length) {
            return false;
        }
        applyRefField(
            clip,
            clip.refs[refIdx],
            refField,
            elem,
            fieldBindingDeps,
        );
        if (refField === "source") {
            syncRefUploadFieldVisibility(
                elem,
                elem.value,
                fieldBindingDeps.refUploadCache,
            );
        }
    } else if (stageField) {
        const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return false;
        }
        if (!isStageFieldTarget(elem)) {
            return false;
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
        return false;
    }

    return true;
};

export const handleFieldChange = (
    elem: EventTarget | null,
    deps: DomEventsDeps,
    fromInputEvent = false,
    sourceEvent: Event | null | undefined = undefined,
): void => {
    if (!isFieldTarget(elem) || !deps.getEditor()?.contains(elem)) {
        return;
    }
    if (
        sourceEvent instanceof Event &&
        sourceEvent.type === "change" &&
        changeFieldEventsHandled.has(sourceEvent)
    ) {
        return;
    }
    const state = deps.getState();
    const clips = state.clips;

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

    if (
        !applyDatasetFieldChange({
            elem,
            clip,
            clipIdx,
            clipField,
            stageField,
            refField,
            fieldBindingDeps,
        })
    ) {
        return;
    }

    videoStagesDebugLog("domEvents", "handleFieldChange → saveState", {
        clipIdx,
        clipField: clipField ?? null,
        stageField: stageField ?? null,
        refField: refField ?? null,
        tag: elem instanceof HTMLElement ? elem.tagName : null,
        fromInputEvent,
    });
    const notifyDomChange = resolveHostNotifyForHandleFieldChange(
        deps,
        sourceEvent,
    );
    deps.saveState(state, { notifyDomChange });
    if (sourceEvent instanceof Event && sourceEvent.type === "change") {
        changeFieldEventsHandled.set(sourceEvent);
    }
    if (clipField === "audioSource") {
        syncClipAudioUploadFieldVisibility(elem, clip.audioSource);
    }
    if (clipField === "clipLengthFromAudio") {
        const clipCard = elem.closest(".vs-clip-card");
        if (clipCard instanceof HTMLElement) {
            syncClipDurationDisabled(clipCard, clip.clipLengthFromAudio);
        }
    }
    if (clipField === "duration" && !fromInputEvent) {
        deps.scheduleClipsRefresh();
    }
};

let latestDomEventDeps: DomEventsDeps | null = null;
let stageEditorDocumentClickBound = false;
const stageEditorsWithFieldListeners = new WeakSet<HTMLElement>();
const stageEditorsWithUploadObservers = new WeakSet<HTMLElement>();

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

const observeMediaFileDatasetChanges = (
    editor: HTMLElement,
    deps: DomEventsDeps,
): void => {
    if (
        stageEditorsWithUploadObservers.has(editor) ||
        typeof MutationObserver === "undefined"
    ) {
        return;
    }
    stageEditorsWithUploadObservers.add(editor);

    new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            if (!isClipAudioUploadInput(mutation.target)) {
                continue;
            }
            if (!editor.contains(mutation.target)) {
                continue;
            }
            handleFieldChange(mutation.target, deps, false, undefined);
        }
    }).observe(editor, {
        subtree: true,
        attributes: true,
        attributeFilter: ["data-filedata", "data-filename"],
    });
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
    observeMediaFileDatasetChanges(editor, deps);

    if (stageEditorsWithFieldListeners.has(editor)) {
        return;
    }
    stageEditorsWithFieldListeners.add(editor);

    editor.addEventListener("change", (event: Event) => {
        handleFieldChange(event.target, deps, false, event);
    });
    editor.addEventListener(
        "change",
        (event: Event) => {
            const inputTarget = event.target;
            if (!isFieldTarget(inputTarget)) {
                return;
            }
            if (event.bubbles) {
                return;
            }
            handleFieldChange(inputTarget, deps, true, event);
        },
        true,
    );
    editor.addEventListener("input", (event: Event) => {
        const inputTarget = event.target;
        if (!isFieldTarget(inputTarget)) {
            return;
        }
        if (isSliderNumericInput(inputTarget)) {
            handleFieldChange(inputTarget, deps, true, event);
        }
    });
    editor.addEventListener("focusout", (event: FocusEvent) => {
        const inputTarget = event.target;
        if (!isDurationInput(inputTarget)) {
            return;
        }
        if (inputTarget.value === inputTarget.defaultValue) {
            return;
        }
        deps.scheduleClipsRefresh();
    });
};
