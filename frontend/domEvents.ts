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
    syncClipAudioLengthDisabled,
    syncClipAudioUploadFieldVisibility,
    syncClipControlNetLengthDisabled,
    syncClipDurationDisabled,
    syncRefUploadFieldVisibility,
    syncStageControlNetStrengthDisabled,
    syncStageUpscaleMethodDisabled,
} from "./fieldBinding";
import {
    buildDefaultClip,
    buildDefaultRef,
    buildDefaultStage,
    getReferenceFrameMax,
    normalizeControlNetLora,
    normalizeControlNetSource,
    normalizeWanClipStructuralRefs,
} from "./normalization";
import type { SaveStateOptions } from "./persistence";
import type { RefUploadCacheApi } from "./refUploadCache";
import { snapDurationToFps } from "./renderUtils";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import { isImageToVideoWorkflow, isVideoStagesEnabled } from "./swarmInputs";
import type { Clip, VideoStagesConfig } from "./types";
import { clipHasWanStage } from "./wanModel";

const changeFieldEventsHandled = new WeakMap<Event, void>();

type FieldChangeSourceEvent = Event & {
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
    ensureEditorRoot: (preferredRoot?: HTMLElement | null) => void;
    getEditor: () => HTMLElement | null;
    getClips: () => Clip[];
    saveClips: (clips: Clip[], options?: SaveStateOptions) => void;
    getState: () => VideoStagesConfig;
    saveState: (state: VideoStagesConfig, options?: SaveStateOptions) => void;
    scheduleClipsRefresh: () => void;
    refUploadCache: RefUploadCacheApi;
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

const saveClipsAndRefresh = (clips: Clip[], deps: DomEventsDeps): void => {
    deps.saveClips(clips, { notifyDomChange: true });
    deps.scheduleClipsRefresh();
};

const addClip = (clips: Clip[]): void => {
    clips.push(
        buildDefaultClip(
            getRootDefaults,
            getDefaultStageModel,
            isImageToVideoWorkflow(),
        ),
    );
};

type ClipActionContext = {
    clips: Clip[];
    clip: Clip;
    clipIdx: number;
    deps: DomEventsDeps;
};

const applyClipAction = (
    action: string,
    { clips, clip, clipIdx, deps }: ClipActionContext,
): boolean => {
    if (action === "delete") {
        clips.splice(clipIdx, 1);
        deps.refUploadCache.reindexAfterClipDelete(clipIdx);
        return true;
    }

    if (action === "skip") {
        clip.skipped = !clip.skipped;
        return true;
    }

    if (action === "add-stage") {
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
        return true;
    }

    if (action === "add-ref") {
        if (clipHasWanStage(clip) && clip.refs.length >= 2) {
            return false;
        }
        clip.refs.push(buildDefaultRef());
        for (const stage of clip.stages) {
            stage.refStrengths.push(
                isImageToVideoWorkflow()
                    ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH
                    : STAGE_REF_STRENGTH_DEFAULT,
            );
        }
        deps.refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
        normalizeWanClipStructuralRefs(clip);
        return true;
    }

    return false;
};

const applyRefAction = (
    action: string,
    elem: HTMLElement,
    { clip, clipIdx, deps }: ClipActionContext,
): boolean => {
    const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
    if (refIdx < 0 || refIdx >= clip.refs.length) {
        return false;
    }

    if (action === "delete") {
        clip.refs.splice(refIdx, 1);
        for (const stage of clip.stages) {
            if (refIdx < stage.refStrengths.length) {
                stage.refStrengths.splice(refIdx, 1);
            }
        }
        deps.refUploadCache.reindexAfterRefDelete(clipIdx, refIdx);
        normalizeWanClipStructuralRefs(clip);
    }

    if (action === "toggle-collapse") {
        const ref = clip.refs[refIdx];
        ref.expanded = !ref.expanded;
    }

    return true;
};

const applyStageAction = (
    action: string,
    elem: HTMLElement,
    { clip }: ClipActionContext,
): boolean => {
    const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
    if (stageIdx < 0 || stageIdx >= clip.stages.length) {
        return false;
    }

    if (action === "delete") {
        clip.stages.splice(stageIdx, 1);
    }

    const stage = clip.stages[stageIdx];
    if (action === "skip") {
        stage.skipped = !stage.skipped;
    }

    if (action === "toggle-collapse") {
        stage.expanded = !stage.expanded;
    }

    return true;
};

export const handleAction = (elem: HTMLElement, deps: DomEventsDeps): void => {
    const clips = deps.getClips();

    const clipAction = elem.dataset.clipAction;
    const stageAction = elem.dataset.stageAction;
    const refAction = elem.dataset.refAction;

    if (clipAction === "add-clip") {
        addClip(clips);
        saveClipsAndRefresh(clips, deps);
        return;
    }

    const clipIdx = parseInt(elem.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
        deps.scheduleClipsRefresh();
        return;
    }

    const clip = clips[clipIdx];
    const actionContext = { clips, clip, clipIdx, deps };
    if (clipAction && applyClipAction(clipAction, actionContext)) {
        saveClipsAndRefresh(clips, deps);
        return;
    }

    if (refAction) {
        if (!applyRefAction(refAction, elem, actionContext)) {
            deps.scheduleClipsRefresh();
            return;
        }
        saveClipsAndRefresh(clips, deps);
        return;
    }

    if (stageAction) {
        if (!applyStageAction(stageAction, elem, actionContext)) {
            deps.scheduleClipsRefresh();
            return;
        }
        saveClipsAndRefresh(clips, deps);
    }
};

type FieldChangeRefresh = "always" | "change-only";

type FieldChangeResult = {
    handled: boolean;
    applied: boolean;
    refreshClips?: FieldChangeRefresh;
    syncAudioUploadVisibility?: boolean;
    syncClipLengthControls?: boolean;
};

const FIELD_CHANGE_NOT_HANDLED: FieldChangeResult = {
    handled: false,
    applied: false,
};

const FIELD_CHANGE_IGNORED: FieldChangeResult = {
    handled: true,
    applied: false,
};

const fieldChangeApplied = (
    result: Omit<FieldChangeResult, "handled" | "applied"> = {},
): FieldChangeResult => ({
    handled: true,
    applied: true,
    ...result,
});

type DatasetFieldChangeContext = {
    elem: FieldTarget;
    clip: Clip;
    clipIdx: number;
    clipField: string | undefined;
    stageField: string | undefined;
    refField: string | undefined;
    fieldBindingDeps: ApplyRefFieldDeps;
};

type ClipFieldChangeContext = {
    elem: FieldTarget;
    clip: Clip;
    clipIdx: number;
    field: string;
    fieldBindingDeps: ApplyRefFieldDeps;
};

const setRelatedClipCheckbox = (
    elem: FieldTarget,
    field: string,
    checked: boolean,
    disabled?: boolean,
): void => {
    const checkbox = elem
        .closest(".vs-clip-card")
        ?.querySelector<HTMLInputElement>(`[data-clip-field="${field}"]`);
    if (!checkbox) {
        return;
    }
    checkbox.checked = checked;
    if (disabled !== undefined) {
        checkbox.disabled = disabled;
    }
};

const syncClipLengthControls = (elem: FieldTarget, clip: Clip): void => {
    const clipCard = elem.closest(".vs-clip-card");
    if (!(clipCard instanceof HTMLElement)) {
        return;
    }
    syncClipDurationDisabled(
        clipCard,
        clip.clipLengthFromAudio || clip.clipLengthFromControlNet,
    );
    syncClipAudioLengthDisabled(
        clipCard,
        !canUseClipLengthFromAudio(clip.audioSource) ||
            clip.clipLengthFromControlNet,
    );
    syncClipControlNetLengthDisabled(
        clipCard,
        clip.controlNetLora === "" || clip.clipLengthFromAudio,
    );
};

const applyClipDurationChange = ({
    elem,
    clip,
}: ClipFieldChangeContext): FieldChangeResult => {
    const value = parseFloat(elem.value);
    if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
        const rootDefaults = getRootDefaults();
        clip.duration = snapDurationToFps(value, rootDefaults.fps);
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        for (const ref of clip.refs) {
            ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
        }
    }

    return fieldChangeApplied({ refreshClips: "change-only" });
};

const applyClipAudioSourceChange = ({
    elem,
    clip,
}: ClipFieldChangeContext): FieldChangeResult => {
    clip.audioSource = elem.value || AUDIO_SOURCE_NATIVE;

    if (!isAceStepFunAudioSource(clip.audioSource)) {
        clip.saveAudioTrack = false;
        setRelatedClipCheckbox(elem, "saveAudioTrack", false);
    }
    if (!canUseClipLengthFromAudio(clip.audioSource)) {
        clip.clipLengthFromAudio = false;
        setRelatedClipCheckbox(elem, "clipLengthFromAudio", false);
    }

    return fieldChangeApplied({ syncAudioUploadVisibility: true });
};

const applyClipControlNetLoraChange = ({
    elem,
    clip,
}: ClipFieldChangeContext): FieldChangeResult => {
    clip.controlNetLora = normalizeControlNetLora(elem.value);
    if (clip.controlNetLora === "") {
        clip.clipLengthFromControlNet = false;
        setRelatedClipCheckbox(elem, "clipLengthFromControlNet", false, true);
    }

    return fieldChangeApplied({
        refreshClips: "always",
        syncClipLengthControls: true,
    });
};

const applyClipLengthFromAudioChange = ({
    elem,
    clip,
}: ClipFieldChangeContext): FieldChangeResult => {
    clip.clipLengthFromAudio =
        elem instanceof HTMLInputElement &&
        canUseClipLengthFromAudio(clip.audioSource) &&
        !clip.clipLengthFromControlNet
            ? !!elem.checked
            : false;
    if (elem instanceof HTMLInputElement && !clip.clipLengthFromAudio) {
        elem.checked = false;
    }
    if (clip.clipLengthFromAudio) {
        clip.clipLengthFromControlNet = false;
        setRelatedClipCheckbox(elem, "clipLengthFromControlNet", false, true);
    }

    return fieldChangeApplied({ syncClipLengthControls: true });
};

const applyClipLengthFromControlNetChange = ({
    elem,
    clip,
}: ClipFieldChangeContext): FieldChangeResult => {
    clip.clipLengthFromControlNet =
        elem instanceof HTMLInputElement &&
        clip.controlNetLora !== "" &&
        !clip.clipLengthFromAudio
            ? !!elem.checked
            : false;
    if (elem instanceof HTMLInputElement && !clip.clipLengthFromControlNet) {
        elem.checked = false;
    }
    if (clip.clipLengthFromControlNet) {
        clip.clipLengthFromAudio = false;
        setRelatedClipCheckbox(elem, "clipLengthFromAudio", false, true);
    }

    return fieldChangeApplied({ syncClipLengthControls: true });
};

const applyClipAudioUploadChange = ({
    elem,
    clip,
    clipIdx,
    fieldBindingDeps,
}: ClipFieldChangeContext): FieldChangeResult => {
    if (!(elem instanceof HTMLInputElement) || elem.type !== "file") {
        return FIELD_CHANGE_IGNORED;
    }
    if (elem.dataset.filedata) {
        clip.uploadedAudio = {
            data: elem.dataset.filedata,
            fileName: normalizeUploadFileName(
                elem.dataset.filename ?? elem.files?.[0]?.name ?? null,
            ),
        };
        return fieldChangeApplied();
    }
    if (elem.files?.length) {
        cacheClipAudioSelection(clipIdx, elem, {
            getClips: fieldBindingDeps.getClips,
            saveClips: fieldBindingDeps.saveClips,
        });
        return FIELD_CHANGE_IGNORED;
    }

    clip.uploadedAudio = null;
    return fieldChangeApplied();
};

const applyClipFieldChange = (
    ctx: ClipFieldChangeContext,
): FieldChangeResult => {
    const { elem, clip, field } = ctx;

    if (field === "duration") {
        return applyClipDurationChange(ctx);
    }

    if (field === "audioSource") {
        return applyClipAudioSourceChange(ctx);
    }

    if (field === "controlNetSource") {
        clip.controlNetSource = normalizeControlNetSource(elem.value);
        return fieldChangeApplied();
    }

    if (field === "controlNetLora") {
        return applyClipControlNetLoraChange(ctx);
    }

    if (field === "saveAudioTrack") {
        clip.saveAudioTrack =
            elem instanceof HTMLInputElement &&
            isAceStepFunAudioSource(clip.audioSource)
                ? !!elem.checked
                : false;
        if (elem instanceof HTMLInputElement && !clip.saveAudioTrack) {
            elem.checked = false;
        }
        return fieldChangeApplied();
    }

    if (field === "reuseAudio") {
        clip.reuseAudio = elem instanceof HTMLInputElement && !!elem.checked;
        return fieldChangeApplied();
    }

    if (field === "clipLengthFromAudio") {
        return applyClipLengthFromAudioChange(ctx);
    }

    if (field === "clipLengthFromControlNet") {
        return applyClipLengthFromControlNetChange(ctx);
    }

    if (field === CLIP_AUDIO_UPLOAD_FIELD) {
        return applyClipAudioUploadChange(ctx);
    }

    return FIELD_CHANGE_NOT_HANDLED;
};

const applyRefDatasetFieldChange = ({
    elem,
    clip,
    refField,
    fieldBindingDeps,
}: DatasetFieldChangeContext): FieldChangeResult => {
    if (!refField) {
        return FIELD_CHANGE_NOT_HANDLED;
    }

    const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
    if (refIdx < 0 || refIdx >= clip.refs.length) {
        return FIELD_CHANGE_IGNORED;
    }

    applyRefField(clip, clip.refs[refIdx], refField, elem, fieldBindingDeps);
    if (refField === "source") {
        syncRefUploadFieldVisibility(
            elem,
            elem.value,
            fieldBindingDeps.refUploadCache,
        );
    }

    return fieldChangeApplied();
};

const applyStageDatasetFieldChange = ({
    elem,
    clip,
    stageField,
}: DatasetFieldChangeContext): FieldChangeResult => {
    if (!stageField) {
        return FIELD_CHANGE_NOT_HANDLED;
    }

    const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
    if (stageIdx < 0 || stageIdx >= clip.stages.length) {
        return FIELD_CHANGE_IGNORED;
    }

    if (!isStageFieldTarget(elem)) {
        return FIELD_CHANGE_IGNORED;
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
    applyStageField(stage, stageField, elem, getRootDefaults, clip);

    if (stageField === "upscale") {
        if (preservedUpscaleMethod != null) {
            stage.upscaleMethod = preservedUpscaleMethod;
        }
        syncStageUpscaleMethodDisabled(elem, stage.upscale);
        if (methodSelect && preservedUpscaleMethod != null) {
            methodSelect.value = preservedUpscaleMethod;
        }
    }

    if (stageField === "model") {
        syncStageControlNetStrengthDisabled(elem, stage, clip);
        return fieldChangeApplied({ refreshClips: "always" });
    }

    return fieldChangeApplied();
};

const applyDatasetFieldChange = (
    ctx: DatasetFieldChangeContext,
): FieldChangeResult => {
    const { elem, clip, clipIdx, clipField, fieldBindingDeps } = ctx;

    if (clipField != null) {
        const clipResult = applyClipFieldChange({
            elem,
            clip,
            clipIdx,
            field: clipField,
            fieldBindingDeps,
        });
        if (clipResult.handled) {
            return clipResult;
        }
    }

    const refResult = applyRefDatasetFieldChange(ctx);
    if (refResult.handled) {
        return refResult;
    }

    return applyStageDatasetFieldChange(ctx);
};

const finishFieldChange = (
    result: FieldChangeResult,
    elem: FieldTarget,
    clip: Clip,
    deps: DomEventsDeps,
    fromInputEvent: boolean,
): void => {
    if (result.syncAudioUploadVisibility) {
        syncClipAudioUploadFieldVisibility(elem, clip.audioSource);
    }

    if (result.syncClipLengthControls) {
        syncClipLengthControls(elem, clip);
    }

    if (
        result.refreshClips === "always" ||
        (result.refreshClips === "change-only" && !fromInputEvent)
    ) {
        deps.scheduleClipsRefresh();
    }
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

    const fieldChangeResult = applyDatasetFieldChange({
        elem,
        clip,
        clipIdx,
        clipField,
        stageField,
        refField,
        fieldBindingDeps,
    });
    if (!fieldChangeResult.applied) {
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
    finishFieldChange(fieldChangeResult, elem, clip, deps, fromInputEvent);
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
