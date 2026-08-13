import { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
import { isAceStepFunAudioSource } from "./audioSource";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
import { applySkipSuffix } from "./clipSemantics";
import { MEDIA_SOURCE_UPLOAD } from "./generatedMediaSource";
import { getVideoStagesHostBridge } from "./host";
import type { InitVideoProbe } from "./mediaProbe";
import { initVideoFromProbe, probeInitVideo } from "./mediaProbe";
import { readProp } from "./normalizationShared";
import { serializeStateForStorage } from "./persistence/documentCodec";
import { getState } from "./persistence/repository";
import { isVideoStagesEnabled } from "./swarmInputs";
import type { AuthoringDocument, Clip } from "./types";
import { isRecord } from "./utils";

const REFINE_SOURCE_FILE_NAME = "refine-source";
const REFINE_STAGE_INDEX = 1;
const REFINE_OWNED_PARAMS = new Set(["images", "videostages"]);
const COMFY_BUTTON_ID = "video_stages_refine_to_comfy_button";
const COMFY_ROW_ID = "video_stages_refine_to_comfy_row";
const COMFY_MARK_CLASS = "video-stages-refine-comfy-mark";
const DONE_TIMEOUT_MS = 2500;

let comfyButtonObserver: MutationObserver | null = null;
let markTimer: ReturnType<typeof setTimeout> | null = null;

const resolvedPromptParameterOverrides = (
    originalPrompt: string,
    sourceParams: Record<string, unknown>,
): Record<string, unknown> => {
    const sourceKeys = new Map(
        Object.keys(sourceParams).map((key) => [cleanParamName(key), key]),
    );
    const overrides: Record<string, unknown> = {};
    for (const match of originalPrompt.matchAll(/<param\[([^\]]+)\]\s*:/gi)) {
        const authoredId = cleanParamName(match[1]) ?? "";
        const resolvedId = window.parameter_remaps?.[authoredId] ?? authoredId;
        if (REFINE_OWNED_PARAMS.has(resolvedId)) {
            continue;
        }
        const sourceKey = sourceKeys.get(resolvedId);
        if (!sourceKey) {
            continue;
        }
        overrides[resolvedId] = structuredClone(sourceParams[sourceKey]);
    }
    return overrides;
};

export const refineNeedsExtraStageMessage = (): string =>
    `Refine Video needs either Clip 0 to have Stage ${REFINE_STAGE_INDEX} defined ` +
    `(active or inactive), or another clip in the timeline. Add a stage or clip in the ` +
    `VideoStages panel, then click Refine Video again.`;

export const hasRefinementWorkToDo = (
    state: AuthoringDocument,
    enabled: boolean,
): boolean => {
    if (!enabled) {
        return false;
    }
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
        return false;
    }
    return clip0.stages.length > REFINE_STAGE_INDEX || state.clips.length > 1;
};

const installRefineSource = (
    clip: Clip,
    data: string,
    probe: InitVideoProbe | null,
): void => {
    clip.initVideo = initVideoFromProbe(
        probe,
        data,
        REFINE_SOURCE_FILE_NAME,
        clip.duration,
    );
    clip.duration = clip.initVideo.lengthSeconds;
    clip.audioSource = MEDIA_SOURCE_UPLOAD;
    clip.saveAudioTrack = false;
    clip.clipLengthFromAudio = false;
    clip.uploadedAudio = null;
    clip.uploadedAudioDurationSeconds = 0;
    clip.uploadedAudioStartSeconds = 0;
    clip.uploadedAudioLengthSeconds = 0;
};

/** Stage 0 becomes a passthrough; skipping it would truncate the refinement stages. */
export const applyRefineToClipZero = (
    clip: Clip,
    data: string,
    probe: InitVideoProbe | null,
): void => {
    installRefineSource(clip, data, probe);
    if (clip.stages.length > REFINE_STAGE_INDEX) {
        applySkipSuffix(clip.stages, REFINE_STAGE_INDEX, false);
    }
    if (clip.stages[0]) {
        clip.stages[0].control = 0;
    }
};

const buildRefineVideoPayload = async (
    src: string,
): Promise<Record<string, unknown> | null> => {
    const host = getVideoStagesHostBridge();
    let parsedMetadata: unknown = null;
    const currentMetadata = host.getCurrentMediaMetadata();
    if (currentMetadata) {
        try {
            const readable = host.interpretMediaMetadata(currentMetadata);
            parsedMetadata = readable ? JSON.parse(readable) : null;
        } catch (error) {
            console.warn(
                "VideoStages: failed to parse source video metadata",
                error,
            );
        }
    }

    const params = isRecord(parsedMetadata)
        ? readProp(parsedMetadata, "sui_image_params")
        : null;
    const initialState = getState();

    if (!hasRefinementWorkToDo(initialState, isVideoStagesEnabled())) {
        host.showError(refineNeedsExtraStageMessage());
        return null;
    }

    const videoDataUrl = await host.toDataUrl(src);
    const probe = await probeInitVideo(videoDataUrl);
    const state = getState();
    const clipZero = state.clips[0];
    if (!clipZero || !hasRefinementWorkToDo(state, isVideoStagesEnabled())) {
        host.showError(refineNeedsExtraStageMessage());
        return null;
    }
    const bakesAceStepFunAudio = isAceStepFunAudioSource(clipZero.audioSource);
    const refinesWithinClipZero = clipZero.stages.length > REFINE_STAGE_INDEX;
    const clips = state.clips.slice(refinesWithinClipZero ? 0 : 1);
    clips[0] = structuredClone(clips[0]);
    if (refinesWithinClipZero) {
        applyRefineToClipZero(clips[0], videoDataUrl, probe);
    } else {
        installRefineSource(clips[0], videoDataUrl, probe);
    }
    reconcileClipArchitectureIdentity(
        clips[0],
        captureAuthoringTransactionSnapshot().capabilities.catalog,
    );
    const inputOverrides: Record<string, unknown> = {
        videostages: serializeStateForStorage({ ...state, clips }),
        images: 1,
    };

    const sourceExtraMetadata = isRecord(parsedMetadata)
        ? readProp(parsedMetadata, "sui_extra_data")
        : undefined;
    const originalPrompt = isRecord(sourceExtraMetadata)
        ? readProp(sourceExtraMetadata, "original_prompt")
        : undefined;
    if (isRecord(params) && typeof originalPrompt === "string") {
        Object.assign(
            inputOverrides,
            resolvedPromptParameterOverrides(originalPrompt, params),
        );
    }
    if (bakesAceStepFunAudio) {
        const aceStepFunModelParam = cleanParamName("AceStepFun Model");
        if (aceStepFunModelParam) {
            inputOverrides[aceStepFunModelParam] = null;
        }
    }

    const prompt = isRecord(params) ? readProp(params, "prompt") : undefined;
    if (typeof prompt === "string") {
        inputOverrides.prompt = prompt;
    }

    const negativePrompt = isRecord(params)
        ? readProp(params, "negativeprompt")
        : undefined;
    if (typeof negativePrompt === "string") {
        inputOverrides.negativeprompt = negativePrompt;
    }

    const seed = isRecord(params) ? readProp(params, "seed") : undefined;
    if (typeof seed === "number") {
        inputOverrides.seed = seed;
    }

    if (isRecord(sourceExtraMetadata)) {
        inputOverrides.extra_metadata = structuredClone(sourceExtraMetadata);
    }

    return inputOverrides;
};

const dispatchRefineVideo = async (
    src: string,
    destination: (
        host: ReturnType<typeof getVideoStagesHostBridge>,
        payload: Record<string, unknown>,
    ) => void | Promise<void>,
): Promise<boolean> => {
    const host = getVideoStagesHostBridge();
    try {
        const payload = await buildRefineVideoPayload(src);
        if (!payload) {
            return false;
        }
        await destination(host, payload);
        return true;
    } catch (error) {
        console.error("VideoStages: failed to prepare Refine Video", error);
        host.showError(
            error instanceof Error
                ? error.message
                : "Failed to prepare Refine Video.",
        );
        return false;
    }
};

const setComfyButtonMark = (mark: string, timeoutMs = 0): void => {
    const button = document.getElementById(COMFY_BUTTON_ID);
    if (!button) {
        return;
    }
    if (markTimer !== null) {
        clearTimeout(markTimer);
        markTimer = null;
    }
    let marker = button.querySelector<HTMLElement>(`.${COMFY_MARK_CLASS}`);
    if (!mark) {
        marker?.remove();
        return;
    }
    if (!marker) {
        marker = document.createElement("span");
        marker.className = COMFY_MARK_CLASS;
        marker.style.marginLeft = "0.3rem";
        marker.style.fontWeight = "bold";
        button.appendChild(marker);
    }
    marker.textContent = mark;
    if (timeoutMs > 0) {
        const shown = marker;
        markTimer = setTimeout(() => {
            shown.remove();
            markTimer = null;
        }, timeoutMs);
    }
};

const refineCurrentVideoToComfy = (): void => {
    const host = getVideoStagesHostBridge();
    const src = host.getCurrentVideoSource();
    if (!src) {
        host.showError("Select a video on the Generate tab first.");
        return;
    }
    setComfyButtonMark("…");
    void dispatchRefineVideo(src, (target, payload) =>
        target.sendToComfyUiAndSave(payload),
    ).then((success) => {
        setComfyButtonMark(success ? "✓" : "", success ? DONE_TIMEOUT_MS : 0);
    });
};

export const injectRefineVideoToComfyButton = (rootDoc: Document): boolean => {
    if (rootDoc.getElementById(COMFY_BUTTON_ID)) {
        return true;
    }
    const whatTheDuckButton = rootDoc.getElementById(
        "wtd_comfy_save_workflow_button",
    );
    const whatTheDuckRow =
        whatTheDuckButton?.closest(".wtd-comfy-save-row") ??
        whatTheDuckButton?.parentElement;
    if (!whatTheDuckRow) {
        return false;
    }

    const row = rootDoc.createElement("div");
    row.id = COMFY_ROW_ID;
    row.className = "comfy-second-button-row";
    row.style.clear = "both";
    row.style.overflow = "hidden";

    const button = rootDoc.createElement("button");
    button.type = "button";
    button.id = COMFY_BUTTON_ID;
    button.className = "basic-button comfy-small-button comfy-left-button";
    button.title =
        "Use the selected Generate-tab video for the next VideoStages refinement, " +
        "open the workflow in ComfyUI, and save the payload and workflow to the server.";
    button.textContent = "Refine Video to ComfyUI";
    button.addEventListener("click", refineCurrentVideoToComfy);
    row.appendChild(button);
    whatTheDuckRow.insertAdjacentElement("afterend", row);
    return true;
};

const initRefineVideoToComfyButton = (): void => {
    if (injectRefineVideoToComfyButton(document) || comfyButtonObserver) {
        return;
    }
    if (!document.body) {
        document.addEventListener(
            "DOMContentLoaded",
            initRefineVideoToComfyButton,
            {
                once: true,
            },
        );
        return;
    }
    comfyButtonObserver = new MutationObserver(() => {
        if (injectRefineVideoToComfyButton(document)) {
            comfyButtonObserver?.disconnect();
            comfyButtonObserver = null;
        }
    });
    comfyButtonObserver.observe(document.body, {
        childList: true,
        subtree: true,
    });
};

export const refineVideoButton = (): void => {
    const host = getVideoStagesHostBridge();
    const description =
        "Uses this video as the source for the next refinement stage or clip.";
    host.registerRefineVideoButton(
        (src) =>
            void dispatchRefineVideo(src, (target, payload) =>
                target.generate(payload),
            ),
        description,
    );
    initRefineVideoToComfyButton();
};
