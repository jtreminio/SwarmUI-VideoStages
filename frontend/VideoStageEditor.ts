import {
    clipFieldId,
    escapeAttr,
    injectFieldData,
    overrideSliderSteps,
    refFieldId,
    snapDurationToFps,
    stageFieldId,
} from "./RenderUtils";
import { ToggleableGroupReuseGuard } from "./ToggleableGroupReuseGuard";
import {
    type Clip,
    type ImageSourceOption,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type RootDefaults,
    type Stage,
} from "./Types";
import { VideoStageUtils } from "./Utils";

const REF_FRAME_MIN = 1;
const CLIP_DURATION_MIN = 1;
const CLIP_DURATION_MAX = 9999;
const CLIP_DURATION_SLIDER_MAX = 60;
const CLIP_DURATION_SLIDER_STEP = 0.5;
const CLIP_DIMENSION_MIN = 256;
const CLIP_DIMENSION_MAX = 16384;
const CLIP_DIMENSION_SLIDER_MAX = 4096;
const CLIP_DIMENSION_STEP = 32;
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

export class VideoStageEditor {
    private editor: HTMLElement | null = null;
    private inactiveReuseGuard: ToggleableGroupReuseGuard;
    private genButtonWrapped = false;
    private genWrapInterval: ReturnType<typeof setInterval> | null = null;
    private clipsInputSyncInterval: ReturnType<typeof setInterval> | null =
        null;
    private clipsRefreshTimer: ReturnType<typeof setTimeout> | null = null;
    private lastKnownClipsJson = "";
    private suppressInactiveReseed = false;
    private observedDropdownIds = new Set<string>();
    private sourceDropdownObserver: MutationObserver | null = null;
    private base2EditListenerInstalled = false;
    private rootFramesChangeListenerInstalled = false;
    private refSourceFallbackListenerInstalled = false;
    private refUploadCache = new Map<string, CachedRefUpload>();

    public constructor() {
        this.inactiveReuseGuard = new ToggleableGroupReuseGuard({
            groupContentId: "input_group_content_videostages",
            getEnableToggle: () => this.getGroupToggle(),
            getGroupToggle: () => this.getGroupToggle(),
            clearInactiveState: () => this.clearClipsForInactiveReuse(),
            afterStateChange: () => {
                if (!this.editor) {
                    return;
                }
                this.scheduleClipsRefresh();
            },
        });
    }

    public init(): void {
        this.createEditor();
        this.startClipsInputSync();
        this.ensureClipsSeeded();
        this.wrapGenerateWithValidation();
        this.renderClips();
        this.installSourceDropdownObserver();
        this.installBase2EditStageChangeListener();
        this.installRootFramesChangeListener();
        this.installRefSourceFallbackListener();
    }

    public resetForInactiveReuse(): void {
        this.suppressInactiveReseed = true;
        this.inactiveReuseGuard.enforceInactiveState();
        this.inactiveReuseGuard.start();
    }

    public tryInstallInactiveReuseGuard(): boolean {
        return this.inactiveReuseGuard.tryInstallGroupToggleWrapper();
    }

    public startGenerateWrapRetry(intervalMs = 250): void {
        if (this.genWrapInterval) {
            return;
        }

        const tryWrap = () => {
            try {
                this.wrapGenerateWithValidation();
                if (
                    typeof mainGenHandler !== "undefined" &&
                    mainGenHandler &&
                    typeof mainGenHandler.doGenerate === "function" &&
                    mainGenHandler.doGenerate.__videoStagesWrapped
                ) {
                    if (this.genWrapInterval) {
                        clearInterval(this.genWrapInterval);
                        this.genWrapInterval = null;
                    }
                }
            } catch {}
        };

        tryWrap();
        this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }

    private createEditor(): void {
        let editor = document.getElementById("videostages_stage_editor");
        if (!editor) {
            editor = document.createElement("div");
            editor.id = "videostages_stage_editor";
            editor.className = "videostages-stage-editor keep_group_visible";
            document
                .getElementById("input_group_content_videostages")
                ?.appendChild(editor);
        }

        editor.style.width = "100%";
        editor.style.maxWidth = "100%";
        editor.style.minWidth = "0";
        editor.style.flex = "1 1 100%";
        editor.style.overflow = "visible";
        this.editor = editor;
    }

    private getClipsInput(): HTMLInputElement | null {
        return VideoStageUtils.getInputElement("input_videostages");
    }

    private getGroupToggle(): HTMLInputElement | null {
        return VideoStageUtils.getInputElement(
            "input_group_content_videostages_toggle",
        );
    }

    private getRootModelInput(): HTMLInputElement | null {
        return VideoStageUtils.getInputElement("input_model");
    }

    private parseBase2EditStageIndex(value: string): number | null {
        const match = `${value || ""}`
            .trim()
            .replace(/\s+/g, "")
            .match(/^edit(\d+)$/i);
        if (!match) {
            return null;
        }
        return parseInt(match[1], 10);
    }

    private getBase2EditStageRefs(): string[] {
        const snapshot = window.base2editStageRegistry?.getSnapshot?.();
        if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
            return [];
        }

        const refs = snapshot.refs
            .map((value) => {
                const stageIndex = this.parseBase2EditStageIndex(value);
                return stageIndex == null ? null : `edit${stageIndex}`;
            })
            .filter((value): value is string => !!value);
        return [...new Set(refs)].sort(
            (left, right) =>
                (this.parseBase2EditStageIndex(left) ?? 0) -
                (this.parseBase2EditStageIndex(right) ?? 0),
        );
    }

    private isAvailableBase2EditReference(value: string): boolean {
        const stageIndex = this.parseBase2EditStageIndex(value);
        if (stageIndex == null) {
            return false;
        }
        return this.getBase2EditStageRefs().includes(`edit${stageIndex}`);
    }

    private installBase2EditStageChangeListener(): void {
        if (this.base2EditListenerInstalled) {
            return;
        }
        this.base2EditListenerInstalled = true;
        document.addEventListener("base2edit:stages-changed", () => {
            this.scheduleClipsRefresh();
        });
    }

    private isRootTextToVideoModel(): boolean {
        const modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
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
    }

    private getDefaultStageModel(modelValues: string[]): string {
        if (this.isRootTextToVideoModel()) {
            const modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
            if (modelName) {
                return modelName;
            }
        }
        return modelValues[0] ?? "";
    }

    private getDropdownOptions(
        paramId: string,
        fallbackSelectId: string,
    ): { values: string[]; labels: string[] } {
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
    }

    private getRootDefaults(): RootDefaults {
        let model = VideoStageUtils.getSelectElement("input_videomodel");
        if (
            (!model || model.options.length === 0) &&
            this.isRootTextToVideoModel()
        ) {
            model = VideoStageUtils.getSelectElement("input_model");
        }
        const vae = VideoStageUtils.getSelectElement("input_vae");
        const sampler = this.getDropdownOptions("sampler", "input_sampler");
        const scheduler = this.getDropdownOptions(
            "scheduler",
            "input_scheduler",
        );
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
            VideoStageUtils.getInputElement("input_aspectratiowidth") ??
            VideoStageUtils.getInputElement("input_width");
        const heightInput =
            VideoStageUtils.getInputElement("input_aspectratioheight") ??
            VideoStageUtils.getInputElement("input_height");
        const fpsInput =
            VideoStageUtils.getInputElement("input_videofps") ??
            VideoStageUtils.getInputElement("input_videoframespersecond");
        const framesInput =
            VideoStageUtils.getInputElement("input_videoframes") ??
            VideoStageUtils.getInputElement("input_text2videoframes");

        const fps = Math.max(
            1,
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
            width: Math.max(
                CLIP_DIMENSION_MIN,
                Math.round(VideoStageUtils.toNumber(widthInput?.value, 1024)),
            ),
            height: Math.max(
                CLIP_DIMENSION_MIN,
                Math.round(VideoStageUtils.toNumber(heightInput?.value, 1024)),
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
            steps: Math.max(
                1,
                Math.round(VideoStageUtils.toNumber(steps?.value, 20)),
            ),
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
            cfgScale: VideoStageUtils.toNumber(cfgScale?.value, 7),
            cfgScaleMin: VideoStageUtils.toNumber(cfgScale?.min, 0),
            cfgScaleMax: Math.min(
                10,
                VideoStageUtils.toNumber(cfgScale?.max, 10),
            ),
            cfgScaleStep: VideoStageUtils.toNumber(cfgScale?.step, 0.5),
        };
    }

    private normalizeStageRefStrengthValue(value: unknown): number {
        return (
            Math.round(
                this.clamp(
                    VideoStageUtils.toNumber(
                        `${value ?? STAGE_REF_STRENGTH_DEFAULT}`,
                        STAGE_REF_STRENGTH_DEFAULT,
                    ),
                    STAGE_REF_STRENGTH_MIN,
                    STAGE_REF_STRENGTH_MAX,
                ) * 10,
            ) / 10
        );
    }

    private buildDefaultStageRefStrengths(refCount: number): number[] {
        const strengths: number[] = [];
        for (let i = 0; i < refCount; i++) {
            strengths.push(STAGE_REF_STRENGTH_DEFAULT);
        }
        return strengths;
    }

    private normalizeStageRefStrengths(
        rawStrengths: unknown,
        refCount: number,
    ): number[] {
        const strengths: number[] = [];
        const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
        for (let i = 0; i < refCount; i++) {
            strengths.push(this.normalizeStageRefStrengthValue(rawValues[i]));
        }
        return strengths;
    }

    private buildDefaultStage(
        previousStage: Stage | null,
        refCount: number,
    ): Stage {
        const defaults = this.getRootDefaults();
        return {
            expanded: true,
            skipped: false,
            control: previousStage ? previousStage.control : defaults.control,
            refStrengths: this.buildDefaultStageRefStrengths(refCount),
            upscale: previousStage ? previousStage.upscale : defaults.upscale,
            upscaleMethod: previousStage
                ? previousStage.upscaleMethod
                : defaults.upscaleMethodValues.includes("pixel-lanczos")
                  ? "pixel-lanczos"
                  : (defaults.upscaleMethodValues[0] ?? "pixel-lanczos"),
            model: previousStage
                ? previousStage.model
                : this.getDefaultStageModel(defaults.modelValues),
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
    }

    private buildDefaultRef(): RefImage {
        return {
            expanded: true,
            source: REF_SOURCE_BASE,
            uploadFileName: null,
            frame: REF_FRAME_MIN,
            fromEnd: false,
        };
    }

    private buildDefaultClip(index: number): Clip {
        const defaults = this.getRootDefaults();
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
            width: defaults.width,
            height: defaults.height,
            refs: [],
            stages: [this.buildDefaultStage(null, 0)],
        };
    }

    private refUploadKey(clipIdx: number, refIdx: number): string {
        return `${clipIdx}:${refIdx}`;
    }

    private parseRefUploadKey(
        key: string,
    ): { clipIdx: number; refIdx: number } | null {
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
    }

    private reindexRefUploadCacheAfterClipDelete(deletedClipIdx: number): void {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of this.refUploadCache.entries()) {
            const parsed = this.parseRefUploadKey(key);
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
            nextCache.set(this.refUploadKey(clipIdx, parsed.refIdx), cached);
        }
        this.refUploadCache = nextCache;
    }

    private reindexRefUploadCacheAfterRefDelete(
        clipIdx: number,
        deletedRefIdx: number,
    ): void {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of this.refUploadCache.entries()) {
            const parsed = this.parseRefUploadKey(key);
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
            nextCache.set(this.refUploadKey(clipIdx, refIdx), cached);
        }
        this.refUploadCache = nextCache;
    }

    private restoreRefUploadPreviews(): void {
        if (!this.editor) {
            return;
        }
        const uploadInputs = this.editor.querySelectorAll(
            '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
        );
        for (const input of uploadInputs) {
            if (!(input instanceof HTMLInputElement)) {
                continue;
            }
            const clipIdx = parseInt(input.dataset.clipIdx ?? "-1", 10);
            const refIdx = parseInt(input.dataset.refIdx ?? "-1", 10);
            const cached = this.refUploadCache.get(
                this.refUploadKey(clipIdx, refIdx),
            );
            if (!cached) {
                continue;
            }
            setMediaFileDirect(
                input,
                cached.src,
                "image",
                cached.name,
                cached.name,
            );
        }
    }

    private normalizeUploadFileName(
        value: string | null | undefined,
    ): string | null {
        const raw = `${value ?? ""}`.trim();
        if (!raw) {
            return null;
        }
        const slashIndex = Math.max(
            raw.lastIndexOf("/"),
            raw.lastIndexOf("\\"),
        );
        return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
    }

    private cacheRefUploadSelection(
        clipIdx: number,
        refIdx: number,
        fileInput: HTMLInputElement,
    ): void {
        const file = fileInput.files?.[0];
        const key = this.refUploadKey(clipIdx, refIdx);
        if (!file) {
            this.refUploadCache.delete(key);
            return;
        }

        const reader = new FileReader();
        reader.addEventListener("load", () => {
            if (typeof reader.result !== "string") {
                return;
            }
            this.refUploadCache.set(key, {
                src: reader.result,
                name: file.name,
            });
        });
        reader.readAsDataURL(file);
    }

    private getReferenceFrameMax(): number {
        return Math.max(REF_FRAME_MIN, this.getRootDefaults().frames);
    }

    private clamp(value: number, min: number, max: number): number {
        return Math.min(Math.max(value, min), max);
    }

    private normalizeStage(
        rawStage: Partial<Stage> & Record<string, unknown>,
        previousStage: Stage | null,
        refCount: number,
    ): Stage {
        const defaults = this.getRootDefaults();
        const fallback = this.buildDefaultStage(previousStage, refCount);
        const stage: Stage = {
            expanded:
                rawStage.expanded === undefined ? true : !!rawStage.expanded,
            skipped: !!rawStage.skipped,
            control: this.clamp(
                VideoStageUtils.toNumber(
                    `${rawStage.control ?? fallback.control}`,
                    fallback.control,
                ),
                defaults.controlMin,
                defaults.controlMax,
            ),
            refStrengths: this.normalizeStageRefStrengths(
                rawStage.refStrengths,
                refCount,
            ),
            upscale: this.clamp(
                VideoStageUtils.toNumber(
                    `${rawStage.upscale ?? fallback.upscale}`,
                    fallback.upscale,
                ),
                defaults.upscaleMin,
                defaults.upscaleMax,
            ),
            upscaleMethod:
                `${rawStage.upscaleMethod ?? fallback.upscaleMethod}` ||
                fallback.upscaleMethod,
            model: `${rawStage.model ?? fallback.model}` || fallback.model,
            vae: `${rawStage.vae ?? fallback.vae ?? ""}`,
            steps: Math.max(
                1,
                Math.round(
                    this.clamp(
                        VideoStageUtils.toNumber(
                            `${rawStage.steps ?? fallback.steps}`,
                            fallback.steps,
                        ),
                        defaults.stepsMin,
                        defaults.stepsMax,
                    ),
                ),
            ),
            cfgScale: this.clamp(
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
            stage.upscaleMethod = stage.upscaleMethod || fallback.upscaleMethod;
        }
        return stage;
    }

    private normalizeRef(
        rawRef: Partial<RefImage> & Record<string, unknown>,
        frameMax: number,
    ): RefImage {
        const fallback = this.buildDefaultRef();
        const source = `${rawRef.source ?? fallback.source}` || fallback.source;
        const ref: RefImage = {
            expanded: rawRef.expanded === undefined ? true : !!rawRef.expanded,
            source,
            uploadFileName:
                rawRef.uploadFileName == null || rawRef.uploadFileName === ""
                    ? null
                    : `${rawRef.uploadFileName}`,
            frame: Math.max(
                REF_FRAME_MIN,
                Math.round(
                    this.clamp(
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
    }

    private normalizeClip(
        rawClip: Partial<Clip> & Record<string, unknown>,
        index: number,
    ): Clip {
        const defaults = this.getRootDefaults();
        const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
        const refFrameMax = this.getReferenceFrameMax();
        const refs = refsRaw.map((rawRef) =>
            this.normalizeRef(
                (rawRef ?? {}) as Partial<RefImage> & Record<string, unknown>,
                refFrameMax,
            ),
        );

        const stages: Stage[] = [];
        const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
        for (let i = 0; i < stagesRaw.length; i++) {
            const previousStage = i > 0 ? stages[i - 1] : null;
            stages.push(
                this.normalizeStage(
                    (stagesRaw[i] ?? {}) as Partial<Stage> &
                        Record<string, unknown>,
                    previousStage,
                    refs.length,
                ),
            );
        }
        if (stages.length === 0) {
            stages.push(this.buildDefaultStage(null, refs.length));
        }

        const fps = Math.max(1, defaults.fps);
        const rawDuration = VideoStageUtils.toNumber(
            `${rawClip.duration}`,
            defaults.frames / fps,
        );

        return {
            name:
                typeof rawClip.name === "string" && rawClip.name.length > 0
                    ? rawClip.name
                    : `Clip ${index}`,
            expanded:
                rawClip.expanded === undefined ? true : !!rawClip.expanded,
            skipped: !!rawClip.skipped,
            duration: snapDurationToFps(
                Math.max(CLIP_DURATION_MIN, rawDuration),
                fps,
            ),
            width: Math.max(
                CLIP_DIMENSION_MIN,
                Math.round(
                    VideoStageUtils.toNumber(
                        `${rawClip.width}`,
                        defaults.width,
                    ),
                ),
            ),
            height: Math.max(
                CLIP_DIMENSION_MIN,
                Math.round(
                    VideoStageUtils.toNumber(
                        `${rawClip.height}`,
                        defaults.height,
                    ),
                ),
            ),
            refs,
            stages,
        };
    }

    private getClips(): Clip[] {
        const input = this.getClipsInput();
        if (!input?.value) {
            return [];
        }

        try {
            const parsed = JSON.parse(input.value);
            const clipsRaw = Array.isArray(parsed)
                ? parsed
                : Array.isArray((parsed as { clips?: unknown[] })?.clips)
                  ? (parsed as { clips: unknown[] }).clips
                  : [];

            const clips: Clip[] = [];
            for (let i = 0; i < clipsRaw.length; i++) {
                clips.push(
                    this.normalizeClip(
                        (clipsRaw[i] ?? {}) as Partial<Clip> &
                            Record<string, unknown>,
                        i,
                    ),
                );
            }
            return clips;
        } catch {
            return [];
        }
    }

    private serializeClipsForStorage(clips: Clip[]): unknown[] {
        return clips.map((clip) => ({
            name: clip.name,
            expanded: clip.expanded,
            skipped: clip.skipped,
            duration: clip.duration,
            width: clip.width,
            height: clip.height,
            refs: clip.refs.map((ref) => ({
                expanded: ref.expanded,
                source: ref.source,
                uploadFileName: ref.uploadFileName,
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
    }

    private saveClips(clips: Clip[]): void {
        const input = this.getClipsInput();
        if (!input) {
            return;
        }

        this.suppressInactiveReseed = false;
        const serialized = JSON.stringify(this.serializeClipsForStorage(clips));
        input.value = serialized;
        this.lastKnownClipsJson = serialized;
        triggerChangeFor(input);
    }

    private clearClipsForInactiveReuse(): boolean {
        const input = this.getClipsInput();
        if (!input || input.value === "") {
            return false;
        }

        input.value = "";
        this.lastKnownClipsJson = "";
        return true;
    }

    private shouldKeepClipsBlankWhileDisabled(): boolean {
        return this.suppressInactiveReseed && !this.isVideoStagesEnabled();
    }

    private ensureClipsSeeded(): void {
        const clips = this.getClips();
        if (clips.length > 0) {
            return;
        }
        if (this.shouldKeepClipsBlankWhileDisabled()) {
            return;
        }

        this.saveClips([this.buildDefaultClip(0)]);
    }

    private isVideoStagesEnabled(): boolean {
        const toggler = this.getGroupToggle();
        return toggler ? toggler.checked : false;
    }

    private hasRootVideoModel(): boolean {
        const videoModel = VideoStageUtils.getInputElement("input_videomodel");
        if (videoModel?.value) {
            return true;
        }
        return this.isRootTextToVideoModel();
    }

    private validateClips(clips: Clip[]): string[] {
        const errors: string[] = [];
        if (!this.hasRootVideoModel()) {
            errors.push("VideoStages requires a root Video Model.");
        }
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
                const sourceError = this.getRefSourceError(ref.source);
                if (sourceError) {
                    errors.push(`${refLabel} ${sourceError}`);
                }
            }
        }

        return errors;
    }

    private getRefSourceError(source: string): string | null {
        const compact = `${source || ""}`.trim().replace(/\s+/g, "");
        if (
            compact === REF_SOURCE_BASE ||
            compact === REF_SOURCE_REFINER ||
            compact === REF_SOURCE_UPLOAD
        ) {
            return null;
        }
        if (this.parseBase2EditStageIndex(compact) != null) {
            if (!this.isAvailableBase2EditReference(compact)) {
                return `references missing Base2Edit stage "${source}".`;
            }
            return null;
        }
        return `has unknown source "${source}".`;
    }

    private wrapGenerateWithValidation(): void {
        if (this.genButtonWrapped) {
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
            const clipsInput = this.getClipsInput();
            if (!clipsInput) {
                return original(...args);
            }
            if (!this.isVideoStagesEnabled()) {
                return original(...args);
            }

            const clips = this.getClips();
            const errors = this.validateClips(clips);
            if (errors.length > 0) {
                showError(errors[0]);
                return;
            }

            return original(...args);
        };
        mainGenHandler.doGenerate.__videoStagesWrapped = true;
        this.genButtonWrapped = true;
    }

    private startClipsInputSync(): void {
        if (this.clipsInputSyncInterval) {
            return;
        }

        this.lastKnownClipsJson = this.getClipsInput()?.value ?? "";
        this.clipsInputSyncInterval = setInterval(() => {
            const currentValue = this.getClipsInput()?.value ?? "";
            if (currentValue === this.lastKnownClipsJson) {
                return;
            }
            this.lastKnownClipsJson = currentValue;
            this.scheduleClipsRefresh();
        }, 150);
    }

    private installSourceDropdownObserver(): void {
        if (
            this.sourceDropdownObserver ||
            typeof MutationObserver === "undefined"
        ) {
            return;
        }

        const observer = new MutationObserver((mutations) => {
            if (!mutations.some((mutation) => mutation.type === "childList")) {
                return;
            }
            this.scheduleClipsRefresh();
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
            if (!source || this.observedDropdownIds.has(sourceId)) {
                continue;
            }
            this.observedDropdownIds.add(sourceId);
            observer.observe(source, { childList: true });
            source.addEventListener("change", () =>
                this.scheduleClipsRefresh(),
            );
            hasObservedSource = true;
        }

        if (!hasObservedSource) {
            observer.disconnect();
            return;
        }

        this.sourceDropdownObserver = observer;
    }

    private handleRootFramesCommittedChange(): void {
        const input = this.getClipsInput();
        if (!input) {
            return;
        }

        const clips = this.getClips();
        const serialized = JSON.stringify(this.serializeClipsForStorage(clips));
        if (serialized !== input.value) {
            this.saveClips(clips);
        }
        this.scheduleClipsRefresh();
    }

    private installRootFramesChangeListener(): void {
        if (this.rootFramesChangeListenerInstalled) {
            return;
        }
        this.rootFramesChangeListenerInstalled = true;
        document.addEventListener("change", (event) => {
            const target = event.target as HTMLElement | null;
            if (!(target instanceof HTMLInputElement)) {
                return;
            }
            if (
                target.id !== "input_videoframes" &&
                target.id !== "input_text2videoframes"
            ) {
                return;
            }

            // `change` fires when the user commits the root frame count
            // (typically blur/tab-away), which avoids per-keystroke rerenders
            // while still refreshing every ref slider's max promptly.
            this.handleRootFramesCommittedChange();
        });
    }

    private installRefSourceFallbackListener(): void {
        if (this.refSourceFallbackListenerInstalled) {
            return;
        }
        this.refSourceFallbackListenerInstalled = true;
        document.addEventListener(
            "change",
            (event) => {
                const target = event.target as Element | null;
                if (!(target instanceof HTMLSelectElement)) {
                    return;
                }
                if (target.dataset.refField !== "source") {
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

                // SwarmUI can rebuild the parameter panel after our init hook,
                // leaving the old editor element (and its delegated listeners)
                // detached. Mirror the ref-source change here so Upload still
                // reveals the native file picker on the live DOM.
                this.createEditor();
                this.handleFieldChange(target);
            },
            true,
        );
    }

    private scheduleClipsRefresh(): void {
        if (this.clipsRefreshTimer) {
            clearTimeout(this.clipsRefreshTimer);
        }
        this.clipsRefreshTimer = setTimeout(() => {
            this.clipsRefreshTimer = null;
            try {
                this.renderClips();
            } catch {}
        }, 0);
    }

    private buildRefSourceOptions(currentValue: string): ImageSourceOption[] {
        const options: ImageSourceOption[] = [
            { value: REF_SOURCE_BASE, label: "Base Output" },
            { value: REF_SOURCE_REFINER, label: "Refiner Output" },
            { value: REF_SOURCE_UPLOAD, label: "Upload" },
        ];
        for (const editRef of this.getBase2EditStageRefs()) {
            const editStage = this.parseBase2EditStageIndex(editRef);
            options.push({
                value: editRef,
                label: `Base2Edit Edit ${editStage} Output`,
            });
        }
        if (currentValue && !options.some((o) => o.value === currentValue)) {
            const isBase2Edit =
                this.parseBase2EditStageIndex(currentValue) != null;
            options.unshift({
                value: currentValue,
                label: isBase2Edit
                    ? `Missing Base2Edit ${currentValue}`
                    : currentValue,
                disabled: isBase2Edit,
            });
        }
        return options;
    }

    private renderClips(): string[] {
        if (!this.editor) {
            return [];
        }

        let clips = this.getClips();
        if (clips.length === 0) {
            if (!this.shouldKeepClipsBlankWhileDisabled()) {
                clips = [this.buildDefaultClip(0)];
                this.saveClips(clips);
            }
        }

        const focusSnapshot = this.captureFocus();
        this.editor.innerHTML = "";

        const stack = document.createElement("div");
        stack.className = "vs-clip-stack";
        stack.setAttribute("data-vs-clip-stack", "true");
        this.editor.appendChild(stack);

        if (clips.length === 0) {
            stack.insertAdjacentHTML(
                "beforeend",
                `<div class="vs-empty-card">No video clips. Click "+ Add Video Clip" below.</div>`,
            );
        } else {
            for (let i = 0; i < clips.length; i++) {
                stack.insertAdjacentHTML(
                    "beforeend",
                    this.renderClipCard(clips[i], i, clips.length),
                );
            }
        }

        const addClipButton = document.createElement("button");
        addClipButton.type = "button";
        addClipButton.className = "vs-add-btn vs-add-btn-clip";
        addClipButton.dataset.clipAction = "add-clip";
        addClipButton.innerText = "+ Add Video Clip";
        this.editor.appendChild(addClipButton);

        this.attachEventListeners();
        enableSlidersIn(this.editor);
        this.restoreRefUploadPreviews();
        this.restoreFocus(focusSnapshot);

        return this.validateClips(clips);
    }

    private renderClipCard(
        clip: Clip,
        clipIdx: number,
        totalClips: number,
    ): string {
        const stagesCount = clip.stages.length;
        const refsCount = clip.refs.length;
        const skipBtnTitle = clip.skipped ? "Re-enable clip" : "Skip clip";
        const skipBtnVariant = clip.skipped ? "vs-btn-skip-active" : "";
        // SwarmUI's native group: open uses ⮟ (U+2B9F), closed uses ⮞ (U+2B9E).
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
        const widthField = injectFieldData(
            makeSliderInput(
                "",
                clipFieldId(clipIdx, "width"),
                "width",
                "Width",
                "",
                String(clip.width),
                CLIP_DIMENSION_MIN,
                CLIP_DIMENSION_MAX,
                CLIP_DIMENSION_MIN,
                CLIP_DIMENSION_SLIDER_MAX,
                CLIP_DIMENSION_STEP,
                true,
                false,
                false,
            ),
            { "data-clip-field": "width", "data-clip-idx": String(clipIdx) },
        );
        const heightField = injectFieldData(
            makeSliderInput(
                "",
                clipFieldId(clipIdx, "height"),
                "height",
                "Height",
                "",
                String(clip.height),
                CLIP_DIMENSION_MIN,
                CLIP_DIMENSION_MAX,
                CLIP_DIMENSION_MIN,
                CLIP_DIMENSION_SLIDER_MAX,
                CLIP_DIMENSION_STEP,
                true,
                false,
                false,
            ),
            { "data-clip-field": "height", "data-clip-idx": String(clipIdx) },
        );

        const body = `
            <div class="input-group-content vs-clip-card-body" id="input_group_content_vsclip${clipIdx}" data-do_not_save="1"${contentStyle}>
                ${lengthField}
                ${widthField}
                ${heightField}

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Reference Images &middot; ${refsCount}</div>
                    </div>
                <div class="vs-card-list">${clip.refs.map((ref, refIdx) => this.renderRefRow(ref, clipIdx, refIdx)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-ref" data-clip-idx="${clipIdx}">+ Add Reference Image</button>
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Stages &middot; ${stagesCount}</div>
                    </div>
                    <div class="vs-card-list">${clip.stages.map((stage, stageIdx) => this.renderStageRow(clip, stage, clipIdx, stageIdx)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-stage" data-clip-idx="${clipIdx}">+ Add Video Stage</button>
                </div>
            </div>
        `;

        return `<div class="${groupClasses.join(" ")}" id="auto-group-vsclip${clipIdx}" data-clip-idx="${clipIdx}">${head}${body}</div>`;
    }

    private decorateAutoInputWrapper(
        html: string,
        className: string,
        hidden = false,
    ): string {
        return html.replace(
            /<div class="([^"]*)"([^>]*)>/,
            (_match, classes, attrs) =>
                `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`,
        );
    }

    private renderRefRow(
        ref: RefImage,
        clipIdx: number,
        refIdx: number,
    ): string {
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

        const sourceOptions = this.buildRefSourceOptions(ref.source);
        const frameCount = this.getReferenceFrameMax();
        const sourceError = this.getRefSourceError(ref.source);
        const errorHtml = sourceError
            ? `<div class="vs-field-error">${escapeAttr(sourceError)}</div>`
            : "";

        const sourceField = injectFieldData(
            this.buildNativeDropdown(
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

        const uploadField = this.decorateAutoInputWrapper(
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
                "Count from last frame",
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
    }

    private renderStageRow(
        clip: Clip,
        stage: Stage,
        clipIdx: number,
        stageIdx: number,
    ): string {
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

        const defaults = this.getRootDefaults();
        const stageSliderField = (
            field: string,
            label: string,
            value: number,
            min: number,
            max: number,
            step: number,
        ): string =>
            injectFieldData(
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
        const stageDropdownField = (
            field: string,
            label: string,
            values: string[],
            labels: string[],
            selected: string,
            disabled = false,
        ): string => {
            let html = injectFieldData(
                this.buildNativeDropdown(
                    stageFieldId(clipIdx, stageIdx, field),
                    field,
                    label,
                    this.dropdownOptions(values, labels, selected),
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
        );
        const upscaleMethodField = stageDropdownField(
            "upscaleMethod",
            "Upscale Method",
            defaults.upscaleMethodValues,
            defaults.upscaleMethodLabels,
            stage.upscaleMethod,
            stage.upscale === 1,
        );
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
    }

    /**
     * Aligns SwarmUI's `makeDropdownInput` with our value/label pairs and
     * preserves the selected value even when it is not in the canonical list
     * (e.g. an unknown model name carried over from a reused image).
     */
    private buildNativeDropdown(
        id: string,
        paramId: string,
        label: string,
        options: ImageSourceOption[],
        selected: string,
    ): string {
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
        // makeDropdownInput cannot disable individual options, so reapply any
        // explicit `disabled` flags from our option list after the fact.
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
    }

    private dropdownOptions(
        values: string[],
        labels: string[],
        selected: string,
    ): ImageSourceOption[] {
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
    }

    private attachEventListeners(): void {
        if (!this.editor) {
            return;
        }
        // Mark the actual DOM element so re-renders or full editor element
        // replacements (e.g. SwarmUI rebuilding the parameter panel) re-attach
        // listeners on the fresh element. A class-level boolean would
        // incorrectly skip re-attachment after the old editor is wiped.
        if (this.editor.dataset.vsListenersAttached === "1") {
            return;
        }
        this.editor.dataset.vsListenersAttached = "1";

        const editor = this.editor;

        editor.addEventListener("click", (event) => {
            const target = event.target as Element | null;
            const refUploadRemoveButton = target?.closest(
                ".vs-ref-upload-field .auto-input-remove-button",
            ) as HTMLElement | null;
            if (refUploadRemoveButton) {
                this.handleRefUploadRemove(refUploadRemoveButton);
                return;
            }
            const actionElem = target?.closest(
                "[data-clip-action], [data-stage-action], [data-ref-action]",
            ) as HTMLElement | null;
            if (actionElem) {
                // Skip / delete buttons live inside the clip's
                // `input-group-shrinkable` header. Stop propagation so the
                // document-level handler that toggles shrinkable groups does
                // not also fire and flip the clip open/closed state.
                event.preventDefault();
                event.stopPropagation();
                this.handleAction(actionElem);
                return;
            }

            // Native shrinkable header click for our clip groups: re-render
            // from our state instead of letting SwarmUI's document handler
            // mutate `style.display` directly (the next render would clobber
            // it). We still stop propagation to avoid the double-toggle.
            const clipHeader = target?.closest(
                ".vs-clip-card > .input-group-shrinkable",
            ) as HTMLElement | null;
            if (clipHeader) {
                event.stopPropagation();
                const group = clipHeader.closest(
                    ".vs-clip-card",
                ) as HTMLElement | null;
                const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
                this.toggleClipExpanded(clipIdx);
            }
        });

        editor.addEventListener("change", (event) => {
            this.handleFieldChange(event.target as HTMLElement | null);
        });
        editor.addEventListener("input", (event) => {
            const target = event.target as HTMLElement | null;
            if (
                target instanceof HTMLInputElement &&
                (target.type === "number" || target.type === "range")
            ) {
                this.handleFieldChange(target, true);
            }
        });
    }

    private getEditorActionTarget(elem: HTMLElement): HTMLElement | null {
        if (!this.editor?.contains(elem)) {
            return null;
        }
        return elem;
    }

    private toggleClipExpanded(clipIdx: number): void {
        const clips = this.getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        clips[clipIdx].expanded = !clips[clipIdx].expanded;
        this.saveClips(clips);
        this.scheduleClipsRefresh();
    }

    private handleAction(elem: HTMLElement): void {
        const target = this.getEditorActionTarget(elem);
        if (!target) {
            return;
        }
        const clips = this.getClips();

        const clipAction = target.dataset.clipAction;
        const stageAction = target.dataset.stageAction;
        const refAction = target.dataset.refAction;

        if (clipAction === "add-clip") {
            clips.push(this.buildDefaultClip(clips.length));
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }

        const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
        if (clipIdx < 0 || clipIdx >= clips.length) {
            this.scheduleClipsRefresh();
            return;
        }
        const clip = clips[clipIdx];

        if (clipAction === "delete") {
            if (clips.length <= 1) {
                return;
            }
            clips.splice(clipIdx, 1);
            this.reindexRefUploadCacheAfterClipDelete(clipIdx);
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }
        if (clipAction === "skip") {
            clip.skipped = !clip.skipped;
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }
        if (clipAction === "add-stage") {
            const previousStage =
                clip.stages.length > 0
                    ? clip.stages[clip.stages.length - 1]
                    : null;
            clip.stages.push(
                this.buildDefaultStage(previousStage, clip.refs.length),
            );
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }
        if (clipAction === "add-ref") {
            clip.refs.push(this.buildDefaultRef());
            for (const stage of clip.stages) {
                stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
            }
            this.refUploadCache.delete(
                this.refUploadKey(clipIdx, clip.refs.length - 1),
            );
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }

        if (refAction) {
            const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
            if (refIdx < 0 || refIdx >= clip.refs.length) {
                this.scheduleClipsRefresh();
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
                this.reindexRefUploadCacheAfterRefDelete(clipIdx, refIdx);
            } else if (refAction === "toggle-collapse") {
                ref.expanded = !ref.expanded;
            }
            this.saveClips(clips);
            this.scheduleClipsRefresh();
            return;
        }

        if (stageAction) {
            const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
            if (stageIdx < 0 || stageIdx >= clip.stages.length) {
                this.scheduleClipsRefresh();
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
            this.saveClips(clips);
            this.scheduleClipsRefresh();
        }
    }

    private handleRefUploadRemove(elem: HTMLElement): void {
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
        const clips = this.getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        if (refIdx < 0 || refIdx >= clips[clipIdx].refs.length) {
            return;
        }

        clips[clipIdx].refs[refIdx].uploadFileName = null;
        this.refUploadCache.delete(this.refUploadKey(clipIdx, refIdx));
        this.saveClips(clips);
    }

    private handleFieldChange(
        elem: HTMLElement | null,
        fromInputEvent = false,
    ): void {
        if (!elem || !this.editor?.contains(elem)) {
            return;
        }
        const target = elem as
            | HTMLInputElement
            | HTMLSelectElement
            | HTMLTextAreaElement;
        const clips = this.getClips();
        const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
        if (clipIdx < 0 || clipIdx >= clips.length) {
            return;
        }
        const clip = clips[clipIdx];
        const defaults = this.getRootDefaults();

        const clipField = target.dataset.clipField;
        const stageField = target.dataset.stageField;
        const refField = target.dataset.refField;

        if (clipField === "duration") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
                clip.duration = snapDurationToFps(value, defaults.fps);
            }
        } else if (clipField === "width") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value) && value >= CLIP_DIMENSION_MIN) {
                clip.width = Math.max(CLIP_DIMENSION_MIN, Math.round(value));
            }
        } else if (clipField === "height") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value) && value >= CLIP_DIMENSION_MIN) {
                clip.height = Math.max(CLIP_DIMENSION_MIN, Math.round(value));
            }
        } else if (refField) {
            const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
            if (refIdx < 0 || refIdx >= clip.refs.length) {
                return;
            }
            this.applyRefField(clip.refs[refIdx], refField, target);
            if (refField === "source") {
                this.syncRefUploadFieldVisibility(target, target.value);
            }
        } else if (stageField) {
            const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
            if (stageIdx < 0 || stageIdx >= clip.stages.length) {
                return;
            }
            this.applyStageField(
                clip.stages[stageIdx],
                stageField,
                target as HTMLInputElement | HTMLSelectElement,
            );
            if (stageField === "upscale") {
                this.syncStageUpscaleMethodDisabled(
                    target,
                    clip.stages[stageIdx].upscale,
                );
            }
        } else {
            return;
        }

        this.saveClips(clips);
        const isSliderDrag =
            fromInputEvent &&
            target instanceof HTMLInputElement &&
            target.type === "range";
        const needsRerender =
            !isSliderDrag &&
            (clipField === "duration" || stageField === "upscale");
        if (needsRerender) {
            this.scheduleClipsRefresh();
        }
    }

    private syncRefUploadFieldVisibility(
        target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
        source: string,
    ): void {
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
            this.refUploadCache.delete(this.refUploadKey(clipIdx, refIdx));
            clearMediaFileInput(uploadInput);
        }
    }

    private syncStageUpscaleMethodDisabled(
        target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
        upscale: number,
    ): void {
        const stageCard = target.closest("section[data-stage-idx]");
        if (!(stageCard instanceof HTMLElement)) {
            return;
        }
        const upscaleMethod = stageCard.querySelector(
            '[data-stage-field="upscaleMethod"]',
        ) as HTMLSelectElement | null;
        if (!upscaleMethod) {
            return;
        }
        upscaleMethod.disabled = upscale === 1;
    }

    private applyRefField(
        ref: RefImage,
        field: string,
        target: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement,
    ): void {
        if (field === "source") {
            ref.source = target.value || REF_SOURCE_BASE;
            if (ref.source !== REF_SOURCE_UPLOAD) {
                ref.uploadFileName = null;
            }
        } else if (field === "frame") {
            const value = parseInt(target.value, 10);
            if (Number.isFinite(value)) {
                ref.frame = this.clamp(
                    value,
                    REF_FRAME_MIN,
                    this.getReferenceFrameMax(),
                );
            }
        } else if (field === "fromEnd") {
            ref.fromEnd =
                target instanceof HTMLInputElement ? !!target.checked : false;
        } else if (field === "uploadFileName") {
            if (target instanceof HTMLInputElement && target.type === "file") {
                const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
                const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
                const fileName = target.files?.[0]?.name ?? null;
                ref.uploadFileName = this.normalizeUploadFileName(fileName);
                if (ref.uploadFileName) {
                    this.cacheRefUploadSelection(clipIdx, refIdx, target);
                } else {
                    this.refUploadCache.delete(
                        this.refUploadKey(clipIdx, refIdx),
                    );
                }
                return;
            }
            ref.uploadFileName = this.normalizeUploadFileName(target.value);
        }
    }

    private applyStageField(
        stage: Stage,
        field: string,
        target: HTMLInputElement | HTMLSelectElement,
    ): void {
        const refStrengthIdx = parseStageRefStrengthIndex(field);
        if (refStrengthIdx != null) {
            const value = parseFloat(target.value);
            if (Number.isFinite(value)) {
                stage.refStrengths[refStrengthIdx] =
                    this.normalizeStageRefStrengthValue(value);
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
                const defaults = this.getRootDefaults();
                stage.control = this.clamp(
                    value,
                    defaults.controlMin,
                    defaults.controlMax,
                );
            }
        } else if (field === "upscale") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value)) {
                const defaults = this.getRootDefaults();
                stage.upscale = this.clamp(
                    value,
                    defaults.upscaleMin,
                    defaults.upscaleMax,
                );
            }
        } else if (field === "steps") {
            const value = parseInt(target.value, 10);
            if (Number.isFinite(value)) {
                const defaults = this.getRootDefaults();
                stage.steps = Math.round(
                    this.clamp(value, defaults.stepsMin, defaults.stepsMax),
                );
            }
        } else if (field === "cfgScale") {
            const value = parseFloat(target.value);
            if (Number.isFinite(value)) {
                const defaults = this.getRootDefaults();
                stage.cfgScale = this.clamp(
                    value,
                    defaults.cfgScaleMin,
                    defaults.cfgScaleMax,
                );
            }
        }
    }

    private captureFocus(): {
        selector: string;
        start: number | null;
        end: number | null;
    } | null {
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
    }

    private restoreFocus(
        snapshot: {
            selector: string;
            start: number | null;
            end: number | null;
        } | null,
    ): void {
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
    }
}
