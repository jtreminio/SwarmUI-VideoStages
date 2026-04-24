import { AUDIO_SOURCE_UPLOAD } from "../AudioSourceController";
import {
    type Clip,
    REF_SOURCE_BASE,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type RootDefaults,
    type Stage,
} from "../Types";
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
    const upscaleMethod = stageCard.querySelector(
        '[data-stage-field="upscaleMethod"]',
    ) as HTMLSelectElement | null;
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
    const uploadField = refCard.querySelector(
        ".vs-ref-upload-field",
    ) as HTMLElement | null;
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

    const uploadInput = uploadField.querySelector(
        '.auto-file[data-ref-field="uploadFileName"]',
    ) as HTMLInputElement | null;
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
    const uploadField = clipCard.querySelector(
        ".vs-clip-audio-upload-field",
    ) as HTMLElement | null;
    if (!uploadField) {
        return;
    }
    uploadField.style.display = source === AUDIO_SOURCE_UPLOAD ? "" : "none";
};

export const applyRefField = (
    clip: Clip,
    ref: RefImage,
    field: string,
    target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    deps: {
        getRootDefaults: () => RootDefaults;
        refUploadCache: RefUploadCacheApi;
        getClips: () => Clip[];
        saveClips: (clips: Clip[]) => void;
    },
): void => {
    const { getRootDefaults, refUploadCache, getClips, saveClips } = deps;
    const frameMax = (): number => getReferenceFrameMax(getRootDefaults, clip);

    if (field === "source") {
        ref.source = target.value || REF_SOURCE_BASE;
        if (ref.source !== REF_SOURCE_UPLOAD) {
            ref.uploadFileName = null;
            ref.uploadedImage = null;
        }
    } else if (field === "frame") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
            ref.frame = clamp(value, REF_FRAME_MIN, frameMax());
        }
    } else if (field === "fromEnd") {
        ref.fromEnd =
            target instanceof HTMLInputElement ? !!target.checked : false;
    } else if (field === "uploadFileName") {
        if (target instanceof HTMLInputElement && target.type === "file") {
            const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
            const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
            if (target.dataset.filedata) {
                ref.uploadedImage = {
                    data: target.dataset.filedata,
                    fileName: normalizeUploadFileName(
                        target.dataset.filename ??
                            target.files?.[0]?.name ??
                            null,
                    ),
                };
                ref.uploadFileName = ref.uploadedImage.fileName;
            } else if (target.files && target.files.length > 0) {
                const fileName = target.files[0]?.name ?? null;
                ref.uploadFileName = normalizeUploadFileName(fileName);
                if (ref.uploadFileName) {
                    refUploadCache.cacheSelection({
                        clipIdx,
                        refIdx,
                        fileInput: target,
                        getClips,
                        saveClips,
                    });
                } else {
                    ref.uploadedImage = null;
                    refUploadCache.delete(refUploadKey(clipIdx, refIdx));
                }
                return;
            } else {
                ref.uploadFileName = null;
                ref.uploadedImage = null;
                refUploadCache.delete(refUploadKey(clipIdx, refIdx));
            }
            return;
        }
        ref.uploadFileName = normalizeUploadFileName(target.value);
        if (!ref.uploadFileName) {
            ref.uploadedImage = null;
        }
    }
};

export const applyStageField = (
    stage: Stage,
    field: string,
    target: HTMLInputElement | HTMLSelectElement,
    getRootDefaults: () => RootDefaults,
): void => {
    const refStrengthIdx = parseStageRefStrengthIndex(field);
    if (refStrengthIdx != null) {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            stage.refStrengths[refStrengthIdx] =
                normalizeStageRefStrengthValue(value);
        }
    } else if (field === "model") {
        stage.model = target.value;
    } else if (field === "vae") {
        stage.vae = target.value;
    } else if (field === "sampler") {
        stage.sampler = target.value;
    } else if (field === "scheduler") {
        stage.scheduler = target.value;
    } else if (field === "upscaleMethod") {
        stage.upscaleMethod = target.value;
    } else if (field === "control") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.control = clamp(
                value,
                defaults.controlMin,
                defaults.controlMax,
            );
        }
    } else if (field === "upscale") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.upscale = clamp(
                value,
                defaults.upscaleMin,
                defaults.upscaleMax,
            );
        }
    } else if (field === "steps") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.steps = Math.round(
                clamp(value, defaults.stepsMin, defaults.stepsMax),
            );
        }
    } else if (field === "cfgScale") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
            const defaults = getRootDefaults();
            stage.cfgScale = clamp(
                value,
                defaults.cfgScaleMin,
                defaults.cfgScaleMax,
            );
        }
    }
};
