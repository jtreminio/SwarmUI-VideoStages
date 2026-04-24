import {
    AUDIO_SOURCE_UPLOAD,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
} from "./audioSource";
import {
    clamp,
    normalizeUploadFileName,
    parseStageRefStrengthIndex,
    REF_FRAME_MIN,
    refUploadKey,
} from "./constants";
import {
    getReferenceFrameMax,
    normalizeStageRefStrengthValue,
} from "./normalization";
import type { RefUploadCacheApi } from "./refUploadCache";
import {
    type Clip,
    REF_SOURCE_BASE,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type RootDefaults,
    type Stage,
} from "./types";

type ApplyRefFieldDeps = {
    getRootDefaults: () => RootDefaults;
    refUploadCache: RefUploadCacheApi;
    getClips: () => Clip[];
    saveClips: (clips: Clip[]) => void;
};

const handleUploadFileName = (
    ref: RefImage,
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    deps: Pick<ApplyRefFieldDeps, "refUploadCache" | "getClips" | "saveClips">,
): void => {
    const { refUploadCache, getClips, saveClips } = deps;
    const clearUpload = (clipIdx: number, refIdx: number): void => {
        ref.uploadFileName = null;
        ref.uploadedImage = null;
        refUploadCache.delete(refUploadKey(clipIdx, refIdx));
    };

    if (!(target instanceof HTMLInputElement) || target.type !== "file") {
        ref.uploadFileName = normalizeUploadFileName(target.value);
        if (!ref.uploadFileName) {
            ref.uploadedImage = null;
        }
        return;
    }

    const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
    const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);

    if (target.dataset.filedata) {
        const fileName = normalizeUploadFileName(
            target.dataset.filename ?? target.files?.[0]?.name ?? null,
        );
        ref.uploadedImage = {
            data: target.dataset.filedata,
            fileName,
        };
        ref.uploadFileName = fileName;
        return;
    }

    const fileName = normalizeUploadFileName(target.files?.[0]?.name ?? null);
    if (!fileName) {
        clearUpload(clipIdx, refIdx);
        return;
    }

    ref.uploadFileName = fileName;
    ref.uploadedImage = null;
    refUploadCache.cacheSelection({
        clipIdx,
        refIdx,
        fileInput: target,
        getClips,
        saveClips,
    });
};

export const syncStageUpscaleMethodDisabled = (
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    upscale: number,
): void => {
    const stageCard = target.closest("section[data-stage-idx]");
    if (!(stageCard instanceof HTMLElement)) {
        return;
    }
    const stageIdx = parseInt(stageCard.dataset.stageIdx ?? "-1", 10);
    if (stageIdx === 0) {
        return;
    }
    const upscaleMethod = stageCard.querySelector<HTMLSelectElement>(
        '[data-stage-field="upscaleMethod"]',
    );
    if (!upscaleMethod) {
        return;
    }
    upscaleMethod.disabled = upscale === 1;
};

export const syncRefUploadFieldVisibility = (
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    source: string,
    refUploadCache: RefUploadCacheApi,
): void => {
    const refCard = target.closest(".vs-ref-card");
    if (!(refCard instanceof HTMLElement)) {
        return;
    }
    const uploadField = refCard.querySelector<HTMLElement>(
        ".vs-ref-upload-field",
    );
    if (!uploadField) {
        return;
    }
    uploadField.style.display = source === REF_SOURCE_UPLOAD ? "" : "none";
    const errorField = refCard.querySelector(".vs-field-error");
    if (errorField) {
        errorField.remove();
    }
    if (source === REF_SOURCE_UPLOAD) {
        return;
    }

    const uploadInput = uploadField.querySelector<HTMLInputElement>(
        '.auto-file[data-ref-field="uploadFileName"]',
    );
    if (uploadInput) {
        const clipIdx = parseInt(uploadInput.dataset.clipIdx ?? "-1", 10);
        const refIdx = parseInt(uploadInput.dataset.refIdx ?? "-1", 10);
        refUploadCache.delete(refUploadKey(clipIdx, refIdx));
        clearMediaFileInput(uploadInput);
    }
};

export const syncClipAudioUploadFieldVisibility = (
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    source: string,
): void => {
    const clipCard = target.closest(".vs-clip-card");
    if (!(clipCard instanceof HTMLElement)) {
        return;
    }
    const uploadField = clipCard.querySelector<HTMLElement>(
        ".vs-clip-audio-upload-field",
    );
    if (!uploadField) {
        return;
    }
    uploadField.style.display = source === AUDIO_SOURCE_UPLOAD ? "" : "none";

    const saveAudioTrackField = clipCard.querySelector<HTMLElement>(
        ".vs-clip-save-audio-track-field",
    );
    if (saveAudioTrackField) {
        saveAudioTrackField.style.display = isAceStepFunAudioSource(source)
            ? ""
            : "none";
    }

    const canUseAudioLength = canUseClipLengthFromAudio(source);
    const lengthFromAudioField = clipCard.querySelector<HTMLElement>(
        ".vs-clip-length-from-audio-field",
    );
    if (lengthFromAudioField) {
        lengthFromAudioField.style.display = canUseAudioLength ? "" : "none";
    }

    const lengthFromAudio = clipCard.querySelector<HTMLInputElement>(
        '[data-clip-field="clipLengthFromAudio"]',
    );
    if (lengthFromAudio && !canUseAudioLength) {
        lengthFromAudio.checked = false;
    }
    syncClipDurationDisabled(
        clipCard,
        canUseAudioLength && !!lengthFromAudio?.checked,
    );
};

export const syncClipDurationDisabled = (
    clipCard: HTMLElement,
    disabled: boolean,
): void => {
    const durationInputs = clipCard.querySelectorAll<HTMLInputElement>(
        '.vs-clip-duration-field [data-clip-field="duration"]',
    );
    for (const durationInput of durationInputs) {
        durationInput.disabled = disabled;
    }
};

export const applyRefField = (
    clip: Clip,
    ref: RefImage,
    field: string,
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    deps: ApplyRefFieldDeps,
): void => {
    const { getRootDefaults, refUploadCache, getClips, saveClips } = deps;
    const frameMax = (): number => getReferenceFrameMax(getRootDefaults, clip);

    if (field === "source") {
        ref.source = target.value || REF_SOURCE_BASE;
        if (ref.source !== REF_SOURCE_UPLOAD) {
            ref.uploadFileName = null;
            ref.uploadedImage = null;
        }

        return;
    }

    if (field === "frame") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
            ref.frame = clamp(value, REF_FRAME_MIN, frameMax());
        }

        return;
    }

    if (field === "fromEnd") {
        ref.fromEnd =
            target instanceof HTMLInputElement ? !!target.checked : false;

        return;
    }
    if (field === "uploadFileName") {
        handleUploadFileName(ref, target, {
            refUploadCache,
            getClips,
            saveClips,
        });
        return;
    }
};

export const applyStageField = (
    stage: Stage,
    field: string,
    target: HTMLInputElement | HTMLSelectElement,
    getRootDefaults: () => RootDefaults,
): void => {
    const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
    const refStrengthIdx = parseStageRefStrengthIndex(field);
    if (refStrengthIdx != null) {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            stage.refStrengths[refStrengthIdx] =
                normalizeStageRefStrengthValue(value);
        }
        return;
    }

    if (field === "model") {
        stage.model = target.value;
        return;
    }

    if (field === "vae") {
        stage.vae = target.value;
        return;
    }

    if (field === "sampler") {
        stage.sampler = target.value;
        return;
    }

    if (field === "scheduler") {
        stage.scheduler = target.value;
        return;
    }

    if (field === "upscaleMethod") {
        stage.upscaleMethod = target.value;
        return;
    }

    if (field === "control") {
        if (stageIdx === 0) {
            const defaults = getRootDefaults();
            stage.control = clamp(
                defaults.control,
                defaults.controlMin,
                defaults.controlMax,
            );
            return;
        }
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.control = clamp(
                value,
                defaults.controlMin,
                defaults.controlMax,
            );
        }
        return;
    }

    if (field === "upscale") {
        if (stageIdx === 0) {
            stage.upscale = getRootDefaults().upscale;
            return;
        }
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.upscale = clamp(
                value,
                defaults.upscaleMin,
                defaults.upscaleMax,
            );
        }
        return;
    }

    if (field === "steps") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.steps = Math.round(
                clamp(value, defaults.stepsMin, defaults.stepsMax),
            );
        }
        return;
    }

    if (field === "cfgScale") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.cfgScale = clamp(
                value,
                defaults.cfgScaleMin,
                defaults.cfgScaleMax,
            );
        }
        return;
    }
};
