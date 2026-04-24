import {
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    resolveAudioSourceValue,
} from "./AudioSourceController";
import {
    clipFieldId,
    escapeAttr,
    framesForClip,
    injectFieldData,
    overrideSliderSteps,
    refFieldId,
    renderOptionList,
    snapDurationToFps,
    stageFieldId,
} from "./RenderUtils";
import {
    type Clip,
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type RootDefaults,
    type Stage,
    type UploadedAudio,
    type VideoStagesConfig,
} from "./Types";
import { VideoStageUtils } from "./Utils";

const REF_FRAME_MIN = 1;
const CLIP_DURATION_MIN = 1;
const CLIP_DURATION_MAX = 9999;
const CLIP_DURATION_SLIDER_MAX = 60;
const CLIP_DURATION_SLIDER_STEP = 0.5;
const ROOT_DIMENSION_MIN = 256;
const ROOT_FPS_MIN = 4;
const CLIP_AUDIO_UPLOAD_FIELD = "uploadedAudio";
const CLIP_AUDIO_UPLOAD_LABEL = "Audio Upload";
const CLIP_AUDIO_UPLOAD_DESCRIPTION =
    "Audio file to attach to this clip. Used when Audio Source is set to Upload.";
const STAGE_REF_STRENGTH_MIN = 0.1;
const STAGE_REF_STRENGTH_MAX = 1;
const STAGE_REF_STRENGTH_STEP = 0.1;
const STAGE_REF_STRENGTH_DEFAULT = 0.8;
const STAGE_REF_STRENGTH_FIELD_PREFIX = "refStrength_";

const stageRefStrengthField = (refIdx: number): string =>
    `${STAGE_REF_STRENGTH_FIELD_PREFIX}${refIdx}`;

const parseStageRefStrengthIndex = (field: string): number | null => {
    if (!field.startsWith(STAGE_REF_STRENGTH_FIELD_PREFIX)) {
        return null;
    }
    const refIdx = parseInt(
        field.slice(STAGE_REF_STRENGTH_FIELD_PREFIX.length),
        10,
    );
    if (!Number.isInteger(refIdx) || refIdx < 0) {
        return null;
    }
    return refIdx;
};

interface CachedRefUpload {
    src: string;
    name: string;
}

export type VideoStageEditor = {
    init(): void;
    startGenerateWrapRetry(intervalMs?: number): void;
};

export const VideoStageEditor = (): VideoStageEditor => {
    let editor: HTMLElement | null = null;
    let genButtonWrapped = false;
    let genWrapInterval: ReturnType<typeof setInterval> | null = null;
    let clipsInputSyncInterval: ReturnType<typeof setInterval> | null = null;
    let clipsRefreshTimer: ReturnType<typeof setTimeout> | null = null;
    let lastKnownClipsJson = "";
    const observedDropdownIds = new Set<string>();
    let sourceDropdownObserver: MutationObserver | null = null;
    let base2EditListenerInstalled = false;
    let rootVideoTimingChangeListenerInstalled = false;
    let refSourceFallbackListenerInstalled = false;
    let refUploadCache = new Map<string, CachedRefUpload>();

    const init = (): void => {
        createEditor();
        startClipsInputSync();
        ensureClipsSeeded();
        wrapGenerateWithValidation();
        renderClips();
        installSourceDropdownObserver();
        installBase2EditStageChangeListener();
        installRootVideoTimingChangeListener();
        installRefSourceFallbackListener();
    };

    const startGenerateWrapRetry = (intervalMs = 250): void => {
        if (genWrapInterval) {
            return;
        }

        const tryWrap = () => {
            try {
                wrapGenerateWithValidation();
                if (
                    typeof mainGenHandler !== "undefined" &&
                    mainGenHandler &&
                    typeof mainGenHandler.doGenerate === "function" &&
                    mainGenHandler.doGenerate.__videoStagesWrapped
                ) {
                    if (genWrapInterval) {
                        clearInterval(genWrapInterval);
                        genWrapInterval = null;
                    }
                }
            } catch {}
        };

        tryWrap();
        genWrapInterval = setInterval(tryWrap, intervalMs);
    };

    const createEditor = (): void => {
        let el = document.getElementById("videostages_stage_editor");
        if (!el) {
            el = document.createElement("div");
            el.id = "videostages_stage_editor";
            el.className = "videostages-stage-editor keep_group_visible";
            document
                .getElementById("input_group_content_videostages")
                ?.appendChild(el);
        }

        el.style.width = "100%";
        el.style.maxWidth = "100%";
        el.style.minWidth = "0";
        el.style.flex = "1 1 100%";
        el.style.overflow = "visible";
        editor = el;
    };

    const getClipsInput = (): HTMLInputElement | null => {
        return VideoStageUtils.getInputElement("input_videostages");
    };

    const getRootDimensionParamInput = (
        field: "width" | "height",
    ): HTMLInputElement | null => {
        return VideoStageUtils.getInputElement(
            field === "width" ? "input_vswidth" : "input_vsheight",
        );
    };

    const getRootFpsParamInput = (): HTMLInputElement | null => {
        return VideoStageUtils.getInputElement("input_vsfps");
    };

    const getCoreDimensionInput = (
        field: "width" | "height",
    ): HTMLInputElement | null => {
        const primaryId = field === "width" ? "input_width" : "input_height";
        const fallbackId =
            field === "width"
                ? "input_aspectratiowidth"
                : "input_aspectratioheight";
        return (
            VideoStageUtils.getInputElement(primaryId) ??
            VideoStageUtils.getInputElement(fallbackId)
        );
    };

    const getRegisteredRootDimension = (
        field: "width" | "height",
    ): number | null => {
        const input = getRootDimensionParamInput(field);
        if (!input) {
            return null;
        }
        const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
        return value >= ROOT_DIMENSION_MIN ? value : null;
    };

    const getRegisteredRootFps = (): number | null => {
        const input = getRootFpsParamInput();
        if (!input) {
            return null;
        }
        const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
        return value >= ROOT_FPS_MIN ? value : null;
    };

    const getCoreDimension = (field: "width" | "height"): number | null => {
        const input = getCoreDimensionInput(field);
        if (!input) {
            return null;
        }
        const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
        return value >= ROOT_DIMENSION_MIN ? value : null;
    };

    /**
     * Seeds the registered RootWidth/RootHeight sliders with the user's
     * currently-selected core Width/Height while our slider is still at the
     * sentinel default (anything below {@link ROOT_DIMENSION_MIN}). Once the
     * user moves our slider to a real value the sentinel guard prevents any
     * further automatic overrides, so manual changes stick.
     */
    const seedRegisteredDimensionsFromCore = (): void => {
        for (const field of ["width", "height"] as const) {
            const ourInput = getRootDimensionParamInput(field);
            if (!ourInput) {
                continue;
            }
            const ourValue = Math.round(
                VideoStageUtils.toNumber(ourInput.value, 0),
            );
            if (ourValue >= ROOT_DIMENSION_MIN) {
                continue;
            }
            const coreValue = getCoreDimension(field);
            if (coreValue === null) {
                continue;
            }
            ourInput.value = `${coreValue}`;
            triggerChangeFor(ourInput);
        }
    };

    const getEffectiveRootDimension = (
        field: "width" | "height",
        persistedValue: unknown,
        fallback: number,
    ): number => {
        return (
            getRegisteredRootDimension(field) ??
            getCoreDimension(field) ??
            normalizeRootDimension(persistedValue, fallback)
        );
    };

    const normalizeUploadedAudio = (value: unknown): UploadedAudio | null => {
        if (!value || typeof value !== "object") {
            return null;
        }
        const raw = value as { data?: unknown; fileName?: unknown };
        const data = `${raw.data ?? ""}`.trim();
        if (!data) {
            return null;
        }
        return {
            data,
            fileName: normalizeUploadFileName(
                raw.fileName == null ? null : `${raw.fileName}`,
            ),
        };
    };

    const getGroupToggle = (): HTMLInputElement | null => {
        return VideoStageUtils.getInputElement(
            "input_group_content_videostages_toggle",
        );
    };

    const getRootModelInput = (): HTMLInputElement | null => {
        return VideoStageUtils.getInputElement("input_model");
    };

    const parseBase2EditStageIndex = (value: string): number | null => {
        const match = `${value || ""}`
            .trim()
            .replace(/\s+/g, "")
            .match(/^edit(\d+)$/i);
        if (!match) {
            return null;
        }
        return parseInt(match[1], 10);
    };

    const getBase2EditStageRefs = (): string[] => {
        const snapshot = window.base2editStageRegistry?.getSnapshot?.();
        if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
            return [];
        }

        const refs = snapshot.refs
            .map((value) => {
                const stageIndex = parseBase2EditStageIndex(value);
                return stageIndex == null ? null : `edit${stageIndex}`;
            })
            .filter((value): value is string => !!value);
        return [...new Set(refs)].sort(
            (left, right) =>
                (parseBase2EditStageIndex(left) ?? 0) -
                (parseBase2EditStageIndex(right) ?? 0),
        );
    };

    const isAvailableBase2EditReference = (value: string): boolean => {
        const stageIndex = parseBase2EditStageIndex(value);
        if (stageIndex == null) {
            return false;
        }
        return getBase2EditStageRefs().includes(`edit${stageIndex}`);
    };

    const installBase2EditStageChangeListener = (): void => {
        if (base2EditListenerInstalled) {
            return;
        }
        base2EditListenerInstalled = true;
        document.addEventListener("base2edit:stages-changed", () => {
            scheduleClipsRefresh();
        });
    };

    const isRootTextToVideoModel = (): boolean => {
        const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
        if (!modelName) {
            return false;
        }

        if (
            typeof modelsHelpers !== "undefined" &&
            modelsHelpers &&
            typeof modelsHelpers.getDataFor === "function"
        ) {
            const modelData = modelsHelpers.getDataFor(
                "Stable-Diffusion",
                modelName,
            );
            if (modelData?.modelClass?.compatClass?.isText2Video) {
                return true;
            }
        }

        if (
            typeof currentModelHelper !== "undefined" &&
            currentModelHelper &&
            currentModelHelper.curCompatClass &&
            typeof modelsHelpers !== "undefined" &&
            modelsHelpers?.compatClasses
        ) {
            const compatClass =
                modelsHelpers.compatClasses[currentModelHelper.curCompatClass];
            return !!compatClass?.isText2Video;
        }

        return false;
    };

    const getDefaultStageModel = (modelValues: string[]): string => {
        if (isRootTextToVideoModel()) {
            const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
            if (modelName) {
                return modelName;
            }
        }
        return modelValues[0] ?? "";
    };

    const getDropdownOptions = (
        paramId: string,
        fallbackSelectId: string,
    ): { values: string[]; labels: string[] } => {
        if (typeof getParamById === "function") {
            const param = getParamById(paramId);
            if (
                param?.values &&
                Array.isArray(param.values) &&
                param.values.length > 0
            ) {
                const labels =
                    Array.isArray(param.value_names) &&
                    param.value_names.length === param.values.length
                        ? [...param.value_names]
                        : [...param.values];
                return { values: [...param.values], labels: labels };
            }
        }

        const select = VideoStageUtils.getSelectElement(fallbackSelectId);
        return {
            values: VideoStageUtils.getSelectValues(select),
            labels: VideoStageUtils.getSelectLabels(select),
        };
    };

    const getRootDefaults = (): RootDefaults => {
        let model = VideoStageUtils.getSelectElement("input_videomodel");
        if (
            (!model || model.options.length === 0) &&
            isRootTextToVideoModel()
        ) {
            model = VideoStageUtils.getSelectElement("input_model");
        }
        const vae = VideoStageUtils.getSelectElement("input_vae");
        const sampler = getDropdownOptions("sampler", "input_sampler");
        const scheduler = getDropdownOptions("scheduler", "input_scheduler");
        const upscaleMethod = VideoStageUtils.getSelectElement(
            "input_refinerupscalemethod",
        );
        const allUpscaleMethodValues =
            VideoStageUtils.getSelectValues(upscaleMethod);
        const allUpscaleMethodLabels =
            VideoStageUtils.getSelectLabels(upscaleMethod);
        const isStageMethod = (value: string): boolean =>
            value.startsWith("pixel-") ||
            value.startsWith("model-") ||
            value.startsWith("latent-") ||
            value.startsWith("latentmodel-");
        const upscaleMethodValues =
            allUpscaleMethodValues.filter(isStageMethod);
        const upscaleMethodLabels = allUpscaleMethodLabels.filter((_, index) =>
            isStageMethod(allUpscaleMethodValues[index]),
        );

        const fallbackUpscaleMethods = [
            "pixel-lanczos",
            "pixel-bicubic",
            "pixel-area",
            "pixel-bilinear",
            "pixel-nearest-exact",
        ];

        const steps =
            VideoStageUtils.getInputElement("input_videosteps") ??
            VideoStageUtils.getInputElement("input_steps");
        const cfgScale =
            VideoStageUtils.getInputElement("input_videocfg") ??
            VideoStageUtils.getInputElement("input_cfgscale");
        const widthInput =
            VideoStageUtils.getInputElement("input_width") ??
            VideoStageUtils.getInputElement("input_aspectratiowidth");
        const heightInput =
            VideoStageUtils.getInputElement("input_height") ??
            VideoStageUtils.getInputElement("input_aspectratioheight");
        const fpsInput =
            VideoStageUtils.getInputElement("input_videofps") ??
            VideoStageUtils.getInputElement("input_videoframespersecond");
        const framesInput =
            VideoStageUtils.getInputElement("input_videoframes") ??
            VideoStageUtils.getInputElement("input_text2videoframes");

        const fps = Math.max(
            1,
            getRegisteredRootFps() ??
                Math.round(VideoStageUtils.toNumber(fpsInput?.value, 24)),
        );
        const frames = Math.max(
            1,
            Math.round(VideoStageUtils.toNumber(framesInput?.value, 24)),
        );

        return {
            modelValues: VideoStageUtils.getSelectValues(model),
            modelLabels: VideoStageUtils.getSelectLabels(model),
            vaeValues: VideoStageUtils.getSelectValues(vae),
            vaeLabels: VideoStageUtils.getSelectLabels(vae),
            samplerValues: sampler.values,
            samplerLabels: sampler.labels,
            schedulerValues: scheduler.values,
            schedulerLabels: scheduler.labels,
            upscaleMethodValues:
                upscaleMethodValues.length > 0
                    ? upscaleMethodValues
                    : fallbackUpscaleMethods,
            upscaleMethodLabels:
                upscaleMethodLabels.length > 0
                    ? upscaleMethodLabels
                    : fallbackUpscaleMethods,
            width:
                getRegisteredRootDimension("width") ??
                Math.max(
                    ROOT_DIMENSION_MIN,
                    Math.round(
                        VideoStageUtils.toNumber(widthInput?.value, 1024),
                    ),
                ),
            height:
                getRegisteredRootDimension("height") ??
                Math.max(
                    ROOT_DIMENSION_MIN,
                    Math.round(
                        VideoStageUtils.toNumber(heightInput?.value, 1024),
                    ),
                ),
            fps,
            frames,
            control: 1,
            controlMin: 0.05,
            controlMax: 1,
            controlStep: 0.05,
            upscale: 1,
            upscaleMin: 0.25,
            upscaleMax: 4,
            upscaleStep: 0.25,
            steps: 8,
            stepsMin: Math.max(
                1,
                Math.round(VideoStageUtils.toNumber(steps?.min, 1)),
            ),
            stepsMax: Math.min(
                50,
                Math.max(
                    1,
                    Math.round(VideoStageUtils.toNumber(steps?.max, 200)),
                ),
            ),
            stepsStep: Math.max(
                1,
                Math.round(VideoStageUtils.toNumber(steps?.step, 1)),
            ),
            cfgScale: 1,
            cfgScaleMin: VideoStageUtils.toNumber(cfgScale?.min, 0),
            cfgScaleMax: Math.min(
                10,
                VideoStageUtils.toNumber(cfgScale?.max, 10),
            ),
            cfgScaleStep: VideoStageUtils.toNumber(cfgScale?.step, 0.5),
        };
    };

    const normalizeStageRefStrengthValue = (value: unknown): number => {
        return (
            Math.round(
                clamp(
                    VideoStageUtils.toNumber(
                        `${value ?? STAGE_REF_STRENGTH_DEFAULT}`,
                        STAGE_REF_STRENGTH_DEFAULT,
                    ),
                    STAGE_REF_STRENGTH_MIN,
                    STAGE_REF_STRENGTH_MAX,
                ) * 10,
            ) / 10
        );
    };

    const buildDefaultStageRefStrengths = (refCount: number): number[] => {
        const strengths: number[] = [];
        for (let i = 0; i < refCount; i++) {
            strengths.push(STAGE_REF_STRENGTH_DEFAULT);
        }
        return strengths;
    };

    const normalizeStageRefStrengths = (
        rawStrengths: unknown,
        refCount: number,
    ): number[] => {
        const strengths: number[] = [];
        const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
        for (let i = 0; i < refCount; i++) {
            strengths.push(normalizeStageRefStrengthValue(rawValues[i]));
        }
        return strengths;
    };

    const buildDefaultStage = (
        previousStage: Stage | null,
        refCount: number,
    ): Stage => {
        const defaults = getRootDefaults();
        return {
            expanded: true,
            skipped: false,
            control: previousStage ? previousStage.control : defaults.control,
            refStrengths: buildDefaultStageRefStrengths(refCount),
            upscale: previousStage ? previousStage.upscale : defaults.upscale,
            upscaleMethod: previousStage
                ? previousStage.upscaleMethod
                : defaults.upscaleMethodValues.includes("pixel-lanczos")
                  ? "pixel-lanczos"
                  : (defaults.upscaleMethodValues[0] ?? "pixel-lanczos"),
            model: previousStage
                ? previousStage.model
                : getDefaultStageModel(defaults.modelValues),
            vae: previousStage
                ? previousStage.vae
                : (defaults.vaeValues[0] ?? ""),
            steps: previousStage ? previousStage.steps : defaults.steps,
            cfgScale: previousStage
                ? previousStage.cfgScale
                : defaults.cfgScale,
            sampler: previousStage
                ? previousStage.sampler
                : (defaults.samplerValues[0] ?? "euler"),
            scheduler: previousStage
                ? previousStage.scheduler
                : (defaults.schedulerValues[0] ?? "normal"),
        };
    };

    const buildDefaultRef = (): RefImage => {
        return {
            expanded: true,
            source: REF_SOURCE_BASE,
            uploadFileName: null,
            uploadedImage: null,
            frame: REF_FRAME_MIN,
            fromEnd: false,
        };
    };

    const buildDefaultClip = (index: number): Clip => {
        const defaults = getRootDefaults();
        return {
            name: `Clip ${index}`,
            expanded: true,
            skipped: false,
            duration: snapDurationToFps(
                Math.max(
                    CLIP_DURATION_MIN,
                    defaults.frames / Math.max(1, defaults.fps),
                ),
                defaults.fps,
            ),
            audioSource: AUDIO_SOURCE_NATIVE,
            uploadedAudio: null,
            refs: [],
            stages: [buildDefaultStage(null, 0)],
        };
    };

    const normalizeRootDimension = (
        value: unknown,
        fallback: number,
    ): number => {
        return Math.max(
            ROOT_DIMENSION_MIN,
            Math.round(
                VideoStageUtils.toNumber(`${value ?? fallback}`, fallback),
            ),
        );
    };

    const normalizeRootFps = (value: unknown, fallback: number): number => {
        return Math.max(
            ROOT_FPS_MIN,
            Math.round(
                VideoStageUtils.toNumber(`${value ?? fallback}`, fallback),
            ),
        );
    };

    const refUploadKey = (clipIdx: number, refIdx: number): string => {
        return `${clipIdx}:${refIdx}`;
    };

    const parseRefUploadKey = (
        key: string,
    ): { clipIdx: number; refIdx: number } | null => {
        const parts = key.split(":");
        if (parts.length !== 2) {
            return null;
        }
        const clipIdx = parseInt(parts[0], 10);
        const refIdx = parseInt(parts[1], 10);
        if (!Number.isInteger(clipIdx) || !Number.isInteger(refIdx)) {
            return null;
        }
        return { clipIdx, refIdx };
    };

    const reindexRefUploadCacheAfterClipDelete = (
        deletedClipIdx: number,
    ): void => {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of refUploadCache.entries()) {
            const parsed = parseRefUploadKey(key);
            if (!parsed) {
                continue;
            }
            if (parsed.clipIdx === deletedClipIdx) {
                continue;
            }
            const clipIdx =
                parsed.clipIdx > deletedClipIdx
                    ? parsed.clipIdx - 1
                    : parsed.clipIdx;
            nextCache.set(refUploadKey(clipIdx, parsed.refIdx), cached);
        }
        refUploadCache = nextCache;
    };

    const reindexRefUploadCacheAfterRefDelete = (
        clipIdx: number,
        deletedRefIdx: number,
    ): void => {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of refUploadCache.entries()) {
            const parsed = parseRefUploadKey(key);
            if (!parsed) {
                continue;
            }
            if (parsed.clipIdx !== clipIdx) {
                nextCache.set(key, cached);
                continue;
            }
            if (parsed.refIdx === deletedRefIdx) {
                continue;
            }
            const refIdx =
                parsed.refIdx > deletedRefIdx
                    ? parsed.refIdx - 1
                    : parsed.refIdx;
            nextCache.set(refUploadKey(clipIdx, refIdx), cached);
        }
        refUploadCache = nextCache;
    };

    const restoreRefUploadPreviews = (): void => {
        if (!editor) {
            return;
        }
        const clips = getClips();
        const uploadInputs = editor.querySelectorAll(
            '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
        );
        for (const input of uploadInputs) {
            if (!(input instanceof HTMLInputElement)) {
                continue;
            }
            const clipIdx = parseInt(input.dataset.clipIdx ?? "-1", 10);
            const refIdx = parseInt(input.dataset.refIdx ?? "-1", 10);
            const persisted =
                clipIdx >= 0 && clipIdx < clips.length
                    ? clips[clipIdx].refs[refIdx]?.uploadedImage
                    : null;
            const cached = refUploadCache.get(refUploadKey(clipIdx, refIdx));
            const src = persisted?.data ?? cached?.src;
            const name = persisted?.fileName ?? cached?.name;
            if (!src) {
                continue;
            }
            setMediaFileDirect(
                input,
                src,
                "image",
                name ?? "Upload Image",
                name ?? undefined,
            );
        }
    };

    const normalizeUploadFileName = (
        value: string | null | undefined,
    ): string | null => {
        const raw = `${value ?? ""}`.trim();
        if (!raw) {
            return null;
        }
        const slashIndex = Math.max(
            raw.lastIndexOf("/"),
            raw.lastIndexOf("\\"),
        );
        return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
    };

    const cacheRefUploadSelection = (
        clipIdx: number,
        refIdx: number,
        fileInput: HTMLInputElement,
    ): void => {
        const file = fileInput.files?.[0];
        const key = refUploadKey(clipIdx, refIdx);
        if (!file) {
            refUploadCache.delete(key);
            return;
        }

        const reader = new FileReader();
        reader.addEventListener("load", () => {
            if (typeof reader.result !== "string") {
                return;
            }
            refUploadCache.set(key, {
                src: reader.result,
                name: file.name,
            });
            const clips = getClips();
            if (clipIdx < 0 || clipIdx >= clips.length) {
                return;
            }
            const ref = clips[clipIdx].refs[refIdx];
            if (!ref) {
                return;
            }
            ref.uploadedImage = {
                data: reader.result,
                fileName: normalizeUploadFileName(file.name),
            };
            saveClips(clips);
        });
        reader.readAsDataURL(file);
    };

    const getReferenceFrameMax = (clip?: Pick<Clip, "duration">): number => {
        const defaults = getRootDefaults();
        if (clip) {
            return Math.max(
                REF_FRAME_MIN,
                framesForClip(clip.duration, defaults.fps),
            );
        }
        return Math.max(REF_FRAME_MIN, defaults.frames);
    };

    const clamp = (value: number, min: number, max: number): number => {
        return Math.min(Math.max(value, min), max);
    };

    /**
     * Stage JSON may use camelCase (editor saves) or PascalCase (C# / metadata).
     */
    const readRawStageProp = (
        raw: Record<string, unknown>,
        camel: string,
        pascal: string,
    ): unknown => {
        if (Object.hasOwn(raw, camel)) {
            return raw[camel];
        }
        if (Object.hasOwn(raw, pascal)) {
            return raw[pascal];
        }
        return undefined;
    };

    const readRawStageString = (
        raw: Record<string, unknown>,
        camel: string,
        pascal: string,
    ): string | undefined => {
        const v = readRawStageProp(raw, camel, pascal);
        if (v == null) {
            return undefined;
        }
        const s = `${v}`.trim();
        return s.length > 0 ? s : undefined;
    };

    const normalizeStage = (
        rawStage: Partial<Stage> & Record<string, unknown>,
        previousStage: Stage | null,
        refCount: number,
        stageIndexInClip: number,
    ): Stage => {
        const defaults = getRootDefaults();
        const fallback = buildDefaultStage(previousStage, refCount);
        const rawRecord = rawStage as Record<string, unknown>;
        const firstStageUpscale =
            stageIndexInClip === 0
                ? {
                      upscale: defaults.upscale,
                      upscaleMethod: defaults.upscaleMethodValues.includes(
                          "pixel-lanczos",
                      )
                          ? "pixel-lanczos"
                          : (defaults.upscaleMethodValues[0] ??
                            "pixel-lanczos"),
                  }
                : {
                      upscale: clamp(
                          VideoStageUtils.toNumber(
                              `${readRawStageProp(rawRecord, "upscale", "Upscale") ?? fallback.upscale}`,
                              fallback.upscale,
                          ),
                          defaults.upscaleMin,
                          defaults.upscaleMax,
                      ),
                      upscaleMethod:
                          `${readRawStageString(rawRecord, "upscaleMethod", "UpscaleMethod") ?? fallback.upscaleMethod}` ||
                          fallback.upscaleMethod,
                  };
        const stage: Stage = {
            expanded:
                rawStage.expanded === undefined ? true : !!rawStage.expanded,
            skipped: !!rawStage.skipped,
            control: clamp(
                VideoStageUtils.toNumber(
                    `${rawStage.control ?? fallback.control}`,
                    fallback.control,
                ),
                defaults.controlMin,
                defaults.controlMax,
            ),
            refStrengths: normalizeStageRefStrengths(
                rawStage.refStrengths,
                refCount,
            ),
            upscale: firstStageUpscale.upscale,
            upscaleMethod: firstStageUpscale.upscaleMethod,
            model: `${rawStage.model ?? fallback.model}` || fallback.model,
            vae: `${rawStage.vae ?? fallback.vae ?? ""}`,
            steps: Math.max(
                1,
                Math.round(
                    clamp(
                        VideoStageUtils.toNumber(
                            `${rawStage.steps ?? fallback.steps}`,
                            fallback.steps,
                        ),
                        defaults.stepsMin,
                        defaults.stepsMax,
                    ),
                ),
            ),
            cfgScale: clamp(
                VideoStageUtils.toNumber(
                    `${rawStage.cfgScale ?? fallback.cfgScale}`,
                    fallback.cfgScale,
                ),
                defaults.cfgScaleMin,
                defaults.cfgScaleMax,
            ),
            sampler:
                `${rawStage.sampler ?? fallback.sampler}` || fallback.sampler,
            scheduler:
                `${rawStage.scheduler ?? fallback.scheduler}` ||
                fallback.scheduler,
        };

        if (
            !defaults.upscaleMethodValues.includes(stage.upscaleMethod) &&
            defaults.upscaleMethodValues.length > 0
        ) {
            stage.upscaleMethod =
                stageIndexInClip === 0
                    ? (defaults.upscaleMethodValues[0] ?? "pixel-lanczos")
                    : stage.upscaleMethod || fallback.upscaleMethod;
        }
        return stage;
    };

    const normalizeRef = (
        rawRef: Partial<RefImage> & Record<string, unknown>,
        frameMax: number,
    ): RefImage => {
        const fallback = buildDefaultRef();
        const source = `${rawRef.source ?? fallback.source}` || fallback.source;
        const ref: RefImage = {
            expanded: rawRef.expanded === undefined ? true : !!rawRef.expanded,
            source,
            uploadFileName:
                rawRef.uploadFileName == null || rawRef.uploadFileName === ""
                    ? null
                    : `${rawRef.uploadFileName}`,
            uploadedImage: normalizeUploadedAudio(rawRef.uploadedImage),
            frame: Math.max(
                REF_FRAME_MIN,
                Math.round(
                    clamp(
                        VideoStageUtils.toNumber(
                            `${rawRef.frame ?? fallback.frame}`,
                            fallback.frame,
                        ),
                        REF_FRAME_MIN,
                        frameMax,
                    ),
                ),
            ),
            fromEnd: !!rawRef.fromEnd,
        };
        return ref;
    };

    const normalizeClip = (
        rawClip: Partial<Clip> & Record<string, unknown>,
        index: number,
    ): Clip => {
        const defaults = getRootDefaults();
        const audioSourceOptions = buildAudioSourceOptions();
        const fps = Math.max(1, defaults.fps);
        const rawDuration = VideoStageUtils.toNumber(
            `${rawClip.duration}`,
            defaults.frames / fps,
        );
        const duration = snapDurationToFps(
            Math.max(CLIP_DURATION_MIN, rawDuration),
            fps,
        );
        const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
        const refFrameMax = getReferenceFrameMax({ duration });
        const refs = refsRaw.map((rawRef) =>
            normalizeRef(
                (rawRef ?? {}) as Partial<RefImage> & Record<string, unknown>,
                refFrameMax,
            ),
        );

        const stages: Stage[] = [];
        const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
        for (let i = 0; i < stagesRaw.length; i++) {
            const previousStage = i > 0 ? stages[i - 1] : null;
            stages.push(
                normalizeStage(
                    (stagesRaw[i] ?? {}) as Partial<Stage> &
                        Record<string, unknown>,
                    previousStage,
                    refs.length,
                    i,
                ),
            );
        }
        if (stages.length === 0) {
            stages.push(buildDefaultStage(null, refs.length));
        }

        return {
            name:
                typeof rawClip.name === "string" && rawClip.name.length > 0
                    ? rawClip.name
                    : `Clip ${index}`,
            expanded:
                rawClip.expanded === undefined ? true : !!rawClip.expanded,
            skipped: !!rawClip.skipped,
            duration,
            audioSource: resolveAudioSourceValue(
                `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`,
                audioSourceOptions,
            ),
            uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
            refs,
            stages,
        };
    };

    const getState = (): VideoStagesConfig => {
        const defaults = getRootDefaults();
        const input = getClipsInput();
        if (!input?.value) {
            return {
                width: defaults.width,
                height: defaults.height,
                fps: defaults.fps,
                clips: [],
            };
        }

        try {
            const parsed = JSON.parse(input.value);
            const parsedConfig =
                parsed && !Array.isArray(parsed) && typeof parsed === "object"
                    ? (parsed as {
                          width?: unknown;
                          height?: unknown;
                          fps?: unknown;
                          clips?: unknown[];
                      })
                    : null;
            const clipsRaw = Array.isArray(parsed)
                ? parsed
                : Array.isArray(parsedConfig?.clips)
                  ? parsedConfig.clips
                  : [];
            const firstClip =
                clipsRaw.length > 0 &&
                clipsRaw[0] &&
                typeof clipsRaw[0] === "object"
                    ? (clipsRaw[0] as { width?: unknown; height?: unknown })
                    : null;

            const clips: Clip[] = [];
            for (let i = 0; i < clipsRaw.length; i++) {
                clips.push(
                    normalizeClip(
                        (clipsRaw[i] ?? {}) as Partial<Clip> &
                            Record<string, unknown>,
                        i,
                    ),
                );
            }
            return {
                width: getEffectiveRootDimension(
                    "width",
                    parsedConfig?.width ?? firstClip?.width,
                    defaults.width,
                ),
                height: getEffectiveRootDimension(
                    "height",
                    parsedConfig?.height ?? firstClip?.height,
                    defaults.height,
                ),
                fps:
                    getRegisteredRootFps() ??
                    normalizeRootFps(parsedConfig?.fps, defaults.fps),
                clips,
            };
        } catch {
            return {
                width: defaults.width,
                height: defaults.height,
                fps: defaults.fps,
                clips: [],
            };
        }
    };

    const serializeClipsForStorage = (clips: Clip[]): unknown[] => {
        return clips.map((clip) => ({
            name: clip.name,
            expanded: clip.expanded,
            skipped: clip.skipped,
            duration: clip.duration,
            audioSource: clip.audioSource,
            uploadedAudio: clip.uploadedAudio,
            refs: clip.refs.map((ref) => ({
                expanded: ref.expanded,
                source: ref.source,
                uploadFileName: ref.uploadFileName,
                uploadedImage: ref.uploadedImage,
                frame: ref.frame,
                fromEnd: ref.fromEnd,
            })),
            stages: clip.stages.map((stage) => ({
                expanded: stage.expanded,
                skipped: stage.skipped,
                control: stage.control,
                refStrengths: stage.refStrengths,
                upscale: stage.upscale,
                upscaleMethod: stage.upscaleMethod,
                model: stage.model,
                vae: stage.vae,
                steps: stage.steps,
                cfgScale: stage.cfgScale,
                sampler: stage.sampler,
                scheduler: stage.scheduler,
            })),
        }));
    };

    const saveState = (state: VideoStagesConfig): void => {
        const input = getClipsInput();
        if (!input) {
            return;
        }

        const serialized = JSON.stringify({
            width: state.width,
            height: state.height,
            fps: state.fps,
            clips: serializeClipsForStorage(state.clips),
        });
        input.value = serialized;
        lastKnownClipsJson = serialized;
        triggerChangeFor(input);
    };

    const getClips = (): Clip[] => {
        return getState().clips;
    };

    const saveClips = (clips: Clip[]): void => {
        const state = getState();
        state.clips = clips;
        saveState(state);
    };

    const ensureClipsSeeded = (): void => {
        const state = getState();
        if (state.clips.length > 0) {
            return;
        }

        state.clips = [buildDefaultClip(0)];
        saveState(state);
    };

    const isVideoStagesEnabled = (): boolean => {
        const toggler = getGroupToggle();
        return toggler ? toggler.checked : false;
    };

    const validateClips = (clips: Clip[]): string[] => {
        const errors: string[] = [];
        if (clips.length === 0) {
            errors.push("VideoStages requires at least one clip.");
            return errors;
        }

        for (let i = 0; i < clips.length; i++) {
            const clip = clips[i];
            if (clip.skipped) {
                continue;
            }
            const clipLabel = `VideoStages: ${clip.name || `Clip ${i}`}`;
            if (clip.stages.length === 0) {
                errors.push(`${clipLabel} requires at least one stage.`);
                continue;
            }

            for (let j = 0; j < clip.stages.length; j++) {
                const stage = clip.stages[j];
                if (stage.skipped) {
                    continue;
                }
                const stageLabel = `${clipLabel}: Stage ${j}`;
                if (!stage.model) {
                    errors.push(`${stageLabel} is missing a video model.`);
                }
                if (!stage.sampler) {
                    errors.push(`${stageLabel} is missing a sampler.`);
                }
                if (!stage.scheduler) {
                    errors.push(`${stageLabel} is missing a scheduler.`);
                }
            }

            for (let j = 0; j < clip.refs.length; j++) {
                const ref = clip.refs[j];
                const refLabel = `${clipLabel}: Reference ${j}`;
                const sourceError = getRefSourceError(ref.source);
                if (sourceError) {
                    errors.push(`${refLabel} ${sourceError}`);
                }
            }
        }

        return errors;
    };

    const getRefSourceError = (source: string): string | null => {
        const compact = `${source || ""}`.trim().replace(/\s+/g, "");
        if (
            compact === REF_SOURCE_BASE ||
            compact === REF_SOURCE_REFINER ||
            compact === REF_SOURCE_UPLOAD
        ) {
            return null;
        }
        if (parseBase2EditStageIndex(compact) != null) {
            if (!isAvailableBase2EditReference(compact)) {
                return `references missing Base2Edit stage "${source}".`;
            }
            return null;
        }
        return `has unknown source "${source}".`;
    };

    const wrapGenerateWithValidation = (): void => {
        if (genButtonWrapped) {
            return;
        }
        if (
            typeof mainGenHandler === "undefined" ||
            !mainGenHandler ||
            typeof mainGenHandler.doGenerate !== "function"
        ) {
            return;
        }

        const original = mainGenHandler.doGenerate.bind(mainGenHandler);
        mainGenHandler.doGenerate = (...args: unknown[]) => {
            const clipsInput = getClipsInput();
            if (!clipsInput) {
                return original(...args);
            }
            if (!isVideoStagesEnabled()) {
                return original(...args);
            }

            const clips = getClips();
            const errors = validateClips(clips);
            if (errors.length > 0) {
                showError(errors[0]);
                return;
            }

            return original(...args);
        };
        mainGenHandler.doGenerate.__videoStagesWrapped = true;
        genButtonWrapped = true;
    };

    const startClipsInputSync = (): void => {
        if (clipsInputSyncInterval) {
            return;
        }

        lastKnownClipsJson = getClipsInput()?.value ?? "";
        clipsInputSyncInterval = setInterval(() => {
            const currentValue = getClipsInput()?.value ?? "";
            if (currentValue === lastKnownClipsJson) {
                return;
            }
            lastKnownClipsJson = currentValue;
            scheduleClipsRefresh();
        }, 150);
    };

    const installSourceDropdownObserver = (): void => {
        if (sourceDropdownObserver || typeof MutationObserver === "undefined") {
            return;
        }

        const observer = new MutationObserver((mutations) => {
            if (!mutations.some((mutation) => mutation.type === "childList")) {
                return;
            }
            scheduleClipsRefresh();
        });

        const observableIds = [
            "input_videomodel",
            "input_model",
            "input_vae",
            "input_sampler",
            "input_scheduler",
            "input_refinerupscalemethod",
        ];

        let hasObservedSource = false;
        for (const sourceId of observableIds) {
            const source = VideoStageUtils.getSelectElement(sourceId);
            if (!source || observedDropdownIds.has(sourceId)) {
                continue;
            }
            observedDropdownIds.add(sourceId);
            observer.observe(source, { childList: true });
            source.addEventListener("change", () => scheduleClipsRefresh());
            hasObservedSource = true;
        }

        if (!hasObservedSource) {
            observer.disconnect();
            return;
        }

        sourceDropdownObserver = observer;
    };

    const handleRootVideoTimingCommittedChange = (): void => {
        const input = getClipsInput();
        if (!input) {
            return;
        }

        const state = getState();
        const serialized = JSON.stringify({
            width: state.width,
            height: state.height,
            fps: state.fps,
            clips: serializeClipsForStorage(state.clips),
        });
        if (serialized !== input.value) {
            saveState(state);
        }
        scheduleClipsRefresh();
    };

    const installRootVideoTimingChangeListener = (): void => {
        if (rootVideoTimingChangeListenerInstalled) {
            return;
        }
        rootVideoTimingChangeListenerInstalled = true;
        document.addEventListener("change", (event) => {
            const target = event.target as HTMLElement | null;
            if (!(target instanceof HTMLInputElement)) {
                return;
            }
            if (
                target.id !== "input_videoframes" &&
                target.id !== "input_text2videoframes" &&
                target.id !== "input_videofps" &&
                target.id !== "input_videoframespersecond" &&
                target.id !== "input_vsfps"
            ) {
                return;
            }

            // Root timing: use change (not input) to limit rerenders while syncing ref maxes.
            handleRootVideoTimingCommittedChange();
        });
    };

    const installRefSourceFallbackListener = (): void => {
        if (refSourceFallbackListenerInstalled) {
            return;
        }
        refSourceFallbackListenerInstalled = true;
        document.addEventListener(
            "change",
            (event) => {
                const target = event.target as Element | null;
                if (!(target instanceof HTMLSelectElement)) {
                    return;
                }
                const isRefSourceChange = target.dataset.refField === "source";
                const isClipAudioSourceChange =
                    target.dataset.clipField === "audioSource";
                if (!isRefSourceChange && !isClipAudioSourceChange) {
                    return;
                }
                const liveEditor = document.getElementById(
                    "videostages_stage_editor",
                );
                if (!(liveEditor instanceof HTMLElement)) {
                    return;
                }
                if (!liveEditor.contains(target)) {
                    return;
                }

                // Panel rebuilds can detach the editor; recreate so listeners match live DOM.
                createEditor();
                handleFieldChange(target);
            },
            true,
        );
    };

    const scheduleClipsRefresh = (): void => {
        if (clipsRefreshTimer) {
            clearTimeout(clipsRefreshTimer);
        }
        clipsRefreshTimer = setTimeout(() => {
            clipsRefreshTimer = null;
            try {
                renderClips();
            } catch {}
        }, 0);
    };

    const buildRefSourceOptions = (
        currentValue: string,
    ): ImageSourceOption[] => {
        const options: ImageSourceOption[] = [
            { value: REF_SOURCE_BASE, label: "Base Output" },
            { value: REF_SOURCE_REFINER, label: "Refiner Output" },
            { value: REF_SOURCE_UPLOAD, label: "Upload" },
        ];
        for (const editRef of getBase2EditStageRefs()) {
            const editStage = parseBase2EditStageIndex(editRef);
            options.push({
                value: editRef,
                label: `Base2Edit Edit ${editStage} Output`,
            });
        }
        if (currentValue && !options.some((o) => o.value === currentValue)) {
            const isBase2Edit = parseBase2EditStageIndex(currentValue) != null;
            options.unshift({
                value: currentValue,
                label: isBase2Edit
                    ? `Missing Base2Edit ${currentValue}`
                    : currentValue,
                disabled: isBase2Edit,
            });
        }
        return options;
    };

    const renderClips = (): string[] => {
        if (!editor) {
            return [];
        }

        seedRegisteredDimensionsFromCore();

        const state = getState();
        let clips = state.clips;
        if (clips.length === 0) {
            state.clips = [buildDefaultClip(0)];
            clips = state.clips;
            saveState(state);
        }

        const focusSnapshot = captureFocus();
        editor.innerHTML = "";

        const stack = document.createElement("div");
        stack.className = "vs-clip-stack";
        stack.setAttribute("data-vs-clip-stack", "true");
        editor.appendChild(stack);

        if (clips.length === 0) {
            stack.insertAdjacentHTML(
                "beforeend",
                `<div class="vs-empty-card">No video clips. Click "+ Add Video Clip" below.</div>`,
            );
        } else {
            for (let i = 0; i < clips.length; i++) {
                stack.insertAdjacentHTML(
                    "beforeend",
                    renderClipCard(clips[i], i, clips.length),
                );
            }
        }

        const addClipButton = document.createElement("button");
        addClipButton.type = "button";
        addClipButton.className = "vs-add-btn vs-add-btn-clip";
        addClipButton.dataset.clipAction = "add-clip";
        addClipButton.innerText = "+ Add Video Clip";
        editor.appendChild(addClipButton);

        attachEventListeners();
        enableSlidersIn(editor);
        restoreClipAudioUploadPreviews(clips);
        restoreRefUploadPreviews();
        restoreFocus(focusSnapshot);

        return validateClips(clips);
    };

    const renderClipCard = (
        clip: Clip,
        clipIdx: number,
        totalClips: number,
    ): string => {
        const stagesCount = clip.stages.length;
        const refsCount = clip.refs.length;
        const skipBtnTitle = clip.skipped ? "Re-enable clip" : "Skip clip";
        const skipBtnVariant = clip.skipped ? "vs-btn-skip-active" : "";
        // Match SwarmUI shrinkable glyphs (open U+2B9F, closed U+2B9E).
        const collapseGlyph = clip.expanded ? "&#x2B9F;" : "&#x2B9E;";

        const groupClasses = ["input-group", "vs-clip-card"];
        groupClasses.push(
            clip.expanded ? "input-group-open" : "input-group-closed",
        );
        if (clip.skipped) {
            groupClasses.push("vs-skipped");
        }
        const contentStyle = clip.expanded ? "" : ' style="display: none;"';

        const head = `<span id="input_group_vsclip${clipIdx}" class="input-group-header input-group-shrinkable"><span class="header-label-wrap"><span class="auto-symbol">${collapseGlyph}</span><span class="header-label">${escapeAttr(clip.name)}</span><span class="header-label-spacer"></span><span class="vs-clip-card-actions"><button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-clip-action="skip" data-clip-idx="${clipIdx}" title="${skipBtnTitle}">&#x23ED;&#xFE0E;</button><button type="button" class="interrupt-button vs-btn-tiny" data-clip-action="delete" data-clip-idx="${clipIdx}" title="Remove clip" ${totalClips === 1 ? "disabled" : ""}>&times;</button></span></span></span>`;

        const lengthField = injectFieldData(
            overrideSliderSteps(
                makeSliderInput(
                    "",
                    clipFieldId(clipIdx, "duration"),
                    "duration",
                    "Length (seconds)",
                    "",
                    clip.duration.toFixed(1),
                    CLIP_DURATION_MIN,
                    CLIP_DURATION_MAX,
                    CLIP_DURATION_MIN,
                    CLIP_DURATION_SLIDER_MAX,
                    CLIP_DURATION_SLIDER_STEP,
                    false,
                    false,
                    false,
                ),
                {
                    numberStep: "any",
                    rangeStep: CLIP_DURATION_SLIDER_STEP,
                },
            ),
            { "data-clip-field": "duration", "data-clip-idx": String(clipIdx) },
        );
        const audioSourceOptions = buildAudioSourceOptions();
        const audioSource = resolveAudioSourceValue(
            clip.audioSource,
            audioSourceOptions,
        );
        const audioSourceField = injectFieldData(
            buildNativeDropdown(
                clipFieldId(clipIdx, "audioSource"),
                "audioSource",
                "Audio Source",
                audioSourceOptions,
                audioSource,
            ),
            {
                "data-clip-field": "audioSource",
                "data-clip-idx": String(clipIdx),
            },
        );
        const audioUploadField = renderClipAudioUploadField(
            clip,
            clipIdx,
            audioSource,
        );

        const body = `
            <div class="input-group-content vs-clip-card-body" id="input_group_content_vsclip${clipIdx}" data-do_not_save="1"${contentStyle}>
                ${lengthField}
                ${audioSourceField}
                ${audioUploadField}

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Reference Images &middot; ${refsCount}</div>
                    </div>
                <div class="vs-card-list">${clip.refs.map((ref, refIdx) => renderRefRow(ref, clip, clipIdx, refIdx)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-ref" data-clip-idx="${clipIdx}">+ Add Reference Image</button>
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Stages &middot; ${stagesCount}</div>
                    </div>
                    <div class="vs-card-list">${clip.stages.map((stage, stageIdx) => renderStageRow(clip, stage, clipIdx, stageIdx)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-stage" data-clip-idx="${clipIdx}">+ Add Video Stage</button>
                </div>
            </div>
        `;

        return `<div class="${groupClasses.join(" ")}" id="auto-group-vsclip${clipIdx}" data-clip-idx="${clipIdx}">${head}${body}</div>`;
    };

    const decorateAutoInputWrapper = (
        html: string,
        className: string,
        hidden = false,
    ): string => {
        return html.replace(
            /<div class="([^"]*)"([^>]*)>/,
            (_match, classes, attrs) =>
                `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`,
        );
    };

    const renderClipAudioUploadField = (
        clip: Clip,
        clipIdx: number,
        audioSource: string,
    ): string => {
        const id = clipFieldId(clipIdx, CLIP_AUDIO_UPLOAD_FIELD);
        return decorateAutoInputWrapper(
            injectFieldData(
                makeAudioInput(
                    "",
                    id,
                    CLIP_AUDIO_UPLOAD_FIELD,
                    CLIP_AUDIO_UPLOAD_LABEL,
                    CLIP_AUDIO_UPLOAD_DESCRIPTION,
                    false,
                    true,
                    true,
                    true,
                ),
                {
                    "data-clip-field": CLIP_AUDIO_UPLOAD_FIELD,
                    "data-clip-idx": String(clipIdx),
                    "data-has-uploaded-audio": clip.uploadedAudio?.data
                        ? "true"
                        : "false",
                },
            ),
            "vs-clip-audio-upload-field",
            audioSource !== AUDIO_SOURCE_UPLOAD,
        );
    };

    const restoreClipAudioUploadPreviews = (clips: Clip[]): void => {
        if (!editor) {
            return;
        }
        for (let clipIdx = 0; clipIdx < clips.length; clipIdx++) {
            const upload = clips[clipIdx].uploadedAudio;
            if (!upload?.data) {
                continue;
            }
            const input = editor.querySelector(
                `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"][data-clip-idx="${clipIdx}"]`,
            ) as HTMLInputElement | null;
            if (!input) {
                continue;
            }
            if (
                input.dataset.filedata === upload.data &&
                normalizeUploadFileName(input.dataset.filename) ===
                    upload.fileName
            ) {
                continue;
            }
            setMediaFileDirect(
                input,
                upload.data,
                "audio",
                upload.fileName ?? CLIP_AUDIO_UPLOAD_LABEL,
                upload.fileName ?? undefined,
            );
        }
    };

    const syncClipAudioUploadFieldVisibility = (
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
        uploadField.style.display =
            source === AUDIO_SOURCE_UPLOAD ? "" : "none";
    };

    const renderRefRow = (
        ref: RefImage,
        clip: Clip,
        clipIdx: number,
        refIdx: number,
    ): string => {
        const collapseTitle = ref.expanded ? "Collapse" : "Expand";
        const collapseGlyph = ref.expanded ? "&#x2B9F;" : "&#x2B9E;";
        const head = `
            <div class="vs-card-head">
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse" data-ref-action="toggle-collapse" data-ref-idx="${refIdx}" data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Ref Image ${refIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="interrupt-button vs-btn-tiny" data-ref-action="delete" data-ref-idx="${refIdx}" data-clip-idx="${clipIdx}" title="Remove reference">&times;</button>
                </div>
            </div>
        `;
        if (!ref.expanded) {
            return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">${head}</section>`;
        }

        const sourceOptions = buildRefSourceOptions(ref.source);
        const frameCount = getReferenceFrameMax(clip);
        const sourceError = getRefSourceError(ref.source);
        const errorHtml = sourceError
            ? `<div class="vs-field-error">${escapeAttr(sourceError)}</div>`
            : "";

        const sourceField = injectFieldData(
            buildNativeDropdown(
                refFieldId(clipIdx, refIdx, "source"),
                "source",
                "Image Source",
                sourceOptions,
                ref.source,
            ),
            {
                "data-ref-field": "source",
                "data-ref-idx": String(refIdx),
                "data-clip-idx": String(clipIdx),
            },
        );

        const uploadField = decorateAutoInputWrapper(
            injectFieldData(
                makeImageInput(
                    "",
                    refFieldId(clipIdx, refIdx, "uploadFileName"),
                    "uploadFileName",
                    "Upload Image",
                    "",
                    false,
                    false,
                    true,
                    false,
                ),
                {
                    "data-ref-field": "uploadFileName",
                    "data-ref-idx": String(refIdx),
                    "data-clip-idx": String(clipIdx),
                },
            ),
            "vs-ref-upload-field",
            ref.source !== REF_SOURCE_UPLOAD,
        );

        const frameField = injectFieldData(
            makeSliderInput(
                "",
                refFieldId(clipIdx, refIdx, "frame"),
                "frame",
                `Frame (max ${frameCount})`,
                "",
                String(ref.frame),
                REF_FRAME_MIN,
                frameCount,
                REF_FRAME_MIN,
                frameCount,
                1,
                false,
                false,
                false,
            ),
            {
                "data-ref-field": "frame",
                "data-ref-idx": String(refIdx),
                "data-clip-idx": String(clipIdx),
            },
        );

        const fromEndField = injectFieldData(
            makeCheckboxInput(
                "",
                refFieldId(clipIdx, refIdx, "fromEnd"),
                "fromEnd",
                "Count in reverse from end",
                "",
                ref.fromEnd,
                false,
                false,
                false,
            ),
            {
                "data-ref-field": "fromEnd",
                "data-ref-idx": String(refIdx),
                "data-clip-idx": String(clipIdx),
            },
        );

        return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">
            ${head}
            <div class="vs-card-body input-group-content">
                ${sourceField}
                ${uploadField}
                ${frameField}
                ${fromEndField}
                ${errorHtml}
            </div>
        </section>`;
    };

    const renderStageRow = (
        clip: Clip,
        stage: Stage,
        clipIdx: number,
        stageIdx: number,
    ): string => {
        const cardClasses = ["vs-card", "input-group"];
        if (stage.skipped) {
            cardClasses.push("vs-skipped");
        }
        const collapseTitle = stage.expanded ? "Collapse" : "Expand";
        const collapseGlyph = stage.expanded ? "&#x2B9F;" : "&#x2B9E;";
        const skipTitle = stage.skipped ? "Re-enable stage" : "Skip stage";
        const skipBtnVariant = stage.skipped ? "vs-btn-skip-active" : "";
        const head = `
            <div class="vs-card-head">
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse" data-stage-action="toggle-collapse" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Stage ${stageIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-stage-action="skip" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${skipTitle}">&#x23ED;&#xFE0E;</button>
                    <button type="button" class="interrupt-button vs-btn-tiny" data-stage-action="delete" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="Remove stage" ${clip.stages.length === 1 ? "disabled" : ""}>&times;</button>
                </div>
            </div>
        `;
        if (!stage.expanded) {
            return `<section class="${cardClasses.join(" ")}" data-stage-idx="${stageIdx}">${head}</section>`;
        }

        const defaults = getRootDefaults();
        const stageSliderField = (
            field: string,
            label: string,
            value: number,
            min: number,
            max: number,
            step: number,
            disabled = false,
        ): string => {
            let html = injectFieldData(
                makeSliderInput(
                    "",
                    stageFieldId(clipIdx, stageIdx, field),
                    field,
                    label,
                    "",
                    String(value),
                    min,
                    max,
                    min,
                    max,
                    step,
                    false,
                    false,
                    false,
                ),
                {
                    "data-stage-field": field,
                    "data-stage-idx": String(stageIdx),
                    "data-clip-idx": String(clipIdx),
                },
            );
            if (disabled) {
                html = html.replace(
                    /<input class="auto-slider-number nogrow"/g,
                    '<input class="auto-slider-number nogrow" disabled',
                );
                html = html.replace(
                    /<input class="auto-slider-range nogrow"/g,
                    '<input class="auto-slider-range nogrow" disabled',
                );
            }
            return html;
        };
        const stageDropdownField = (
            field: string,
            label: string,
            values: string[],
            labels: string[],
            selected: string,
            disabled = false,
        ): string => {
            let html = injectFieldData(
                buildNativeDropdown(
                    stageFieldId(clipIdx, stageIdx, field),
                    field,
                    label,
                    dropdownOptions(values, labels, selected),
                    selected,
                ),
                {
                    "data-stage-field": field,
                    "data-stage-idx": String(stageIdx),
                    "data-clip-idx": String(clipIdx),
                },
            );
            if (disabled) {
                html = html.replace(/<select /, "<select disabled ");
            }
            return html;
        };

        const modelField = stageDropdownField(
            "model",
            "Model",
            defaults.modelValues,
            defaults.modelLabels,
            stage.model,
        );
        const controlField = stageSliderField(
            "control",
            "Control",
            stage.control,
            defaults.controlMin,
            defaults.controlMax,
            defaults.controlStep,
        );
        const stepsField = stageSliderField(
            "steps",
            "Steps",
            stage.steps,
            defaults.stepsMin,
            defaults.stepsMax,
            defaults.stepsStep,
        );
        const cfgScaleField = stageSliderField(
            "cfgScale",
            "CFG Scale",
            stage.cfgScale,
            defaults.cfgScaleMin,
            defaults.cfgScaleMax,
            defaults.cfgScaleStep,
        );
        const upscaleField = stageSliderField(
            "upscale",
            "Upscale",
            stage.upscale,
            defaults.upscaleMin,
            defaults.upscaleMax,
            defaults.upscaleStep,
            stageIdx === 0,
        );
        const upscaleMethodField = (() => {
            const selectedMethod = `${stage.upscaleMethod ?? ""}`;
            let html = injectFieldData(
                buildNativeDropdownStrict(
                    stageFieldId(clipIdx, stageIdx, "upscaleMethod"),
                    "upscaleMethod",
                    "Upscale Method",
                    dropdownOptions(
                        defaults.upscaleMethodValues,
                        defaults.upscaleMethodLabels,
                        selectedMethod,
                    ),
                    selectedMethod,
                ),
                {
                    "data-stage-field": "upscaleMethod",
                    "data-stage-idx": String(stageIdx),
                    "data-clip-idx": String(clipIdx),
                },
            );
            if (stageIdx === 0 || stage.upscale === 1) {
                html = html.replace(/<select /, "<select disabled ");
            }
            return html;
        })();
        const samplerField = stageDropdownField(
            "sampler",
            "Sampler",
            defaults.samplerValues,
            defaults.samplerLabels,
            stage.sampler,
        );
        const schedulerField = stageDropdownField(
            "scheduler",
            "Scheduler",
            defaults.schedulerValues,
            defaults.schedulerLabels,
            stage.scheduler,
        );
        const vaeField = stageDropdownField(
            "vae",
            "VAE",
            defaults.vaeValues,
            defaults.vaeLabels,
            stage.vae,
        );
        const refStrengthFields = clip.refs
            .map((_ref, refIdx) =>
                stageSliderField(
                    stageRefStrengthField(refIdx),
                    `Reference Image ${refIdx} Strength`,
                    stage.refStrengths[refIdx] ?? STAGE_REF_STRENGTH_DEFAULT,
                    STAGE_REF_STRENGTH_MIN,
                    STAGE_REF_STRENGTH_MAX,
                    STAGE_REF_STRENGTH_STEP,
                ),
            )
            .join("");

        return `<section class="${cardClasses.join(" ")}" data-stage-idx="${stageIdx}">
            ${head}
            <div class="vs-card-body input-group-content">
                ${modelField}
                ${controlField}
                ${stepsField}
                ${cfgScaleField}
                ${upscaleField}
                ${upscaleMethodField}
                ${samplerField}
                ${schedulerField}
                ${vaeField}
                ${refStrengthFields}
            </div>
        </section>`;
    };

    /**
     * Native select like {@link buildNativeDropdown}, but with strict option matching.
     * `makeDropdownInput` uses loose equality and can select the wrong option after re-render.
     */
    const buildNativeDropdownStrict = (
        id: string,
        paramId: string,
        label: string,
        options: ImageSourceOption[],
        selected: string,
    ): string => {
        const escapedLabel = escapeAttr(label);
        const selectedStr = `${selected ?? ""}`;
        const optionHtml = renderOptionList(options, selectedStr);
        const baseHtml = `
    <div class="auto-input auto-dropdown-box auto-input-flex">
        <label>
            <span class="auto-input-name">${escapedLabel}</span>
        </label>
        <select class="auto-dropdown" id="${escapeAttr(id)}" data-name="${escapedLabel}" data-param_id="${escapeAttr(paramId)}" autocomplete="off" onchange="autoSelectWidth(this)">
${optionHtml}
        </select>
    </div>`;
        return options.reduce((acc, option) => {
            if (!option.disabled) {
                return acc;
            }
            const optionValue = escapeAttr(option.value);
            return acc.replace(
                new RegExp(`(<option [^>]*value="${optionValue}")`),
                "$1 disabled",
            );
        }, baseHtml);
    };

    const buildNativeDropdown = (
        id: string,
        paramId: string,
        label: string,
        options: ImageSourceOption[],
        selected: string,
    ): string => {
        const values = options.map((option) => option.value);
        const labels = options.map((option) => option.label);
        const html = makeDropdownInput(
            "",
            id,
            paramId,
            label,
            "",
            values,
            selected,
            false,
            false,
            labels,
            false,
        );
        // makeDropdownInput omits per-option disabled; patch from our option list.
        return options.reduce((acc, option) => {
            if (!option.disabled) {
                return acc;
            }
            const optionValue = escapeAttr(option.value);
            return acc.replace(
                new RegExp(`(<option [^>]*value="${optionValue}")`),
                "$1 disabled",
            );
        }, html);
    };

    const dropdownOptions = (
        values: string[],
        labels: string[],
        selected: string,
    ): ImageSourceOption[] => {
        const finalValues = [...values];
        const finalLabels = [...labels];
        if (selected && !finalValues.includes(selected)) {
            finalValues.unshift(selected);
            finalLabels.unshift(selected);
        }
        return finalValues.map((value, idx) => ({
            value,
            label: finalLabels[idx] ?? value,
        }));
    };

    const attachEventListeners = (): void => {
        if (!editor) {
            return;
        }
        // data-* on the element so panel rebuilds re-run setup (a class flag would not).
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
                handleRefUploadRemove(refUploadRemoveButton);
                return;
            }
            const clipUploadRemoveButton = target?.closest(
                ".vs-clip-audio-upload-field .auto-input-remove-button",
            ) as HTMLElement | null;
            if (clipUploadRemoveButton) {
                handleClipAudioUploadRemove(clipUploadRemoveButton);
                return;
            }
            const actionElem = target?.closest(
                "[data-clip-action], [data-stage-action], [data-ref-action]",
            ) as HTMLElement | null;
            if (actionElem) {
                // Actions sit in the shrinkable header; do not let it toggle open/closed.
                event.preventDefault();
                event.stopPropagation();
                handleAction(actionElem);
                return;
            }

            // Own expand/collapse so SwarmUI does not set display before our re-render.
            const clipHeader = target?.closest(
                ".vs-clip-card > .input-group-shrinkable",
            ) as HTMLElement | null;
            if (clipHeader) {
                event.stopPropagation();
                const group = clipHeader.closest(
                    ".vs-clip-card",
                ) as HTMLElement | null;
                const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
                toggleClipExpanded(clipIdx);
            }
        });

        editor.addEventListener("change", (event) => {
            handleFieldChange(event.target as HTMLElement | null);
        });
        editor.addEventListener("input", (event) => {
            const target = event.target as HTMLElement | null;
            if (
                target instanceof HTMLInputElement &&
                (target.type === "number" || target.type === "range")
            ) {
                handleFieldChange(target, true);
            }
        });
    };

    const getEditorActionTarget = (elem: HTMLElement): HTMLElement | null => {
        if (!editor?.contains(elem)) {
            return null;
        }
        return elem;
    };

    const toggleClipExpanded = (clipIdx: number): void => {
        const clips = getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        clips[clipIdx].expanded = !clips[clipIdx].expanded;
        saveClips(clips);
        scheduleClipsRefresh();
    };

    const handleAction = (elem: HTMLElement): void => {
        const target = getEditorActionTarget(elem);
        if (!target) {
            return;
        }
        const clips = getClips();

        const clipAction = target.dataset.clipAction;
        const stageAction = target.dataset.stageAction;
        const refAction = target.dataset.refAction;

        if (clipAction === "add-clip") {
            clips.push(buildDefaultClip(clips.length));
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }

        const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
        if (clipIdx < 0 || clipIdx >= clips.length) {
            scheduleClipsRefresh();
            return;
        }
        const clip = clips[clipIdx];

        if (clipAction === "delete") {
            if (clips.length <= 1) {
                return;
            }
            clips.splice(clipIdx, 1);
            reindexRefUploadCacheAfterClipDelete(clipIdx);
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }
        if (clipAction === "skip") {
            clip.skipped = !clip.skipped;
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }
        if (clipAction === "add-stage") {
            const previousStage =
                clip.stages.length > 0
                    ? clip.stages[clip.stages.length - 1]
                    : null;
            clip.stages.push(
                buildDefaultStage(previousStage, clip.refs.length),
            );
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }
        if (clipAction === "add-ref") {
            clip.refs.push(buildDefaultRef());
            for (const stage of clip.stages) {
                stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
            }
            refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }

        if (refAction) {
            const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
            if (refIdx < 0 || refIdx >= clip.refs.length) {
                scheduleClipsRefresh();
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
                reindexRefUploadCacheAfterRefDelete(clipIdx, refIdx);
            } else if (refAction === "toggle-collapse") {
                ref.expanded = !ref.expanded;
            }
            saveClips(clips);
            scheduleClipsRefresh();
            return;
        }

        if (stageAction) {
            const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
            if (stageIdx < 0 || stageIdx >= clip.stages.length) {
                scheduleClipsRefresh();
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
            saveClips(clips);
            scheduleClipsRefresh();
        }
    };

    const handleRefUploadRemove = (elem: HTMLElement): void => {
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
        const clips = getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        if (refIdx < 0 || refIdx >= clips[clipIdx].refs.length) {
            return;
        }

        clips[clipIdx].refs[refIdx].uploadFileName = null;
        clips[clipIdx].refs[refIdx].uploadedImage = null;
        refUploadCache.delete(refUploadKey(clipIdx, refIdx));
        saveClips(clips);
    };

    const handleClipAudioUploadRemove = (elem: HTMLElement): void => {
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
        const clips = getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }

        clips[clipIdx].uploadedAudio = null;
        saveClips(clips);
    };

    const handleFieldChange = (
        elem: HTMLElement | null,
        fromInputEvent = false,
    ): void => {
        if (!elem || !editor?.contains(elem)) {
            return;
        }
        const target = elem as
            | HTMLInputElement
            | HTMLSelectElement
            | HTMLTextAreaElement;
        const state = getState();
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

        if (clipField === "duration") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
                clip.duration = snapDurationToFps(value, defaults.fps);
                const frameMax = getReferenceFrameMax(clip);
                for (const ref of clip.refs) {
                    ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
                }
            }
        } else if (clipField === "audioSource") {
            clip.audioSource = target.value || AUDIO_SOURCE_NATIVE;
        } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
            if (
                !(target instanceof HTMLInputElement) ||
                target.type !== "file"
            ) {
                return;
            }
            if (target.dataset.filedata) {
                clip.uploadedAudio = {
                    data: target.dataset.filedata,
                    fileName: normalizeUploadFileName(
                        target.dataset.filename ??
                            target.files?.[0]?.name ??
                            null,
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
            applyRefField(clip, clip.refs[refIdx], refField, target);
            if (refField === "source") {
                syncRefUploadFieldVisibility(target, target.value);
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

        saveState(state);
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
            scheduleClipsRefresh();
        }
    };

    const syncStageUpscaleMethodDisabled = (
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

    const syncRefUploadFieldVisibility = (
        target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
        source: string,
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

    const applyRefField = (
        clip: Clip,
        ref: RefImage,
        field: string,
        target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    ): void => {
        if (field === "source") {
            ref.source = target.value || REF_SOURCE_BASE;
            if (ref.source !== REF_SOURCE_UPLOAD) {
                ref.uploadFileName = null;
                ref.uploadedImage = null;
            }
        } else if (field === "frame") {
            const value = parseInt(target.value, 10);
            if (Number.isFinite(value)) {
                ref.frame = clamp(
                    value,
                    REF_FRAME_MIN,
                    getReferenceFrameMax(clip),
                );
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
                        cacheRefUploadSelection(clipIdx, refIdx, target);
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

    const applyStageField = (
        stage: Stage,
        field: string,
        target: HTMLInputElement | HTMLSelectElement,
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

    const captureFocus = (): {
        selector: string;
        start: number | null;
        end: number | null;
    } | null => {
        const el = document.activeElement;
        if (
            !el ||
            el === document.body ||
            (el.tagName !== "INPUT" && el.tagName !== "SELECT")
        ) {
            return null;
        }
        const dataset = (el as HTMLElement).dataset;
        let selector: string | null = null;
        if (dataset.clipField && dataset.clipIdx) {
            selector = `[data-clip-field="${dataset.clipField}"][data-clip-idx="${dataset.clipIdx}"]`;
        } else if (dataset.stageField && dataset.stageIdx && dataset.clipIdx) {
            selector = `[data-stage-field="${dataset.stageField}"][data-stage-idx="${dataset.stageIdx}"][data-clip-idx="${dataset.clipIdx}"]`;
        } else if (dataset.refField && dataset.refIdx && dataset.clipIdx) {
            selector = `[data-ref-field="${dataset.refField}"][data-ref-idx="${dataset.refIdx}"][data-clip-idx="${dataset.clipIdx}"]`;
        }
        if (!selector) {
            return null;
        }
        let start: number | null = null;
        let end: number | null = null;
        try {
            const inputEl = el as HTMLInputElement;
            start = inputEl.selectionStart;
            end = inputEl.selectionEnd;
        } catch {}
        return { selector, start, end };
    };

    const restoreFocus = (
        snapshot: {
            selector: string;
            start: number | null;
            end: number | null;
        } | null,
    ): void => {
        if (!snapshot) {
            return;
        }
        const el = document.querySelector(snapshot.selector) as
            | HTMLInputElement
            | HTMLSelectElement
            | null;
        if (!el) {
            return;
        }
        el.focus();
        if (
            el instanceof HTMLInputElement &&
            snapshot.start != null &&
            snapshot.end != null
        ) {
            try {
                el.setSelectionRange(snapshot.start, snapshot.end);
            } catch {}
        }
    };

    return {
        init,
        startGenerateWrapRetry,
    };
};
