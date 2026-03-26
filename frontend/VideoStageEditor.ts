interface RootDefaults {
    modelValues: string[];
    modelLabels: string[];
    vaeValues: string[];
    vaeLabels: string[];
    samplerValues: string[];
    samplerLabels: string[];
    schedulerValues: string[];
    schedulerLabels: string[];
    upscaleMethodValues: string[];
    upscaleMethodLabels: string[];
    control: number;
    controlMin: number;
    controlMax: number;
    controlStep: number;
    upscale: number;
    upscaleMin: number;
    upscaleMax: number;
    upscaleStep: number;
    steps: number;
    stepsMin: number;
    stepsMax: number;
    stepsStep: number;
    cfgScale: number;
    cfgScaleMin: number;
    cfgScaleMax: number;
    cfgScaleStep: number;
}

interface Stage {
    control: number;
    upscale: number;
    upscaleMethod: string;
    model: string;
    vae: string;
    steps: number;
    cfgScale: number;
    sampler: string;
    scheduler: string;
    imageReference: string;
}

class VideoStageEditor
{
    private editor: HTMLElement;
    private genButtonWrapped = false;
    private genWrapInterval: ReturnType<typeof setInterval> | null = null;
    private changeListenerElem: HTMLElement | null = null;
    private stageSyncTimer: ReturnType<typeof setTimeout> | null = null;
    private sourceDropdownObserver: MutationObserver | null = null;
    private stageRefreshTimer: ReturnType<typeof setTimeout> | null = null;

    /**
     * Initializes the VideoStages editor after the parameter UI exists.
     */
    public init(): void
    {
        this.createEditor();
        this.ensureStagesSeeded();
        this.wrapGenerateWithValidation();
        this.showStages();
        this.installStageChangeListener();
        this.installSourceDropdownObserver();
    }

    /**
     * Keeps custom stage containers from inheriting flex min-width overflow from the parent group.
     */
    private applyFullWidthLayout(elem: HTMLElement): void
    {
        elem.style.width = "100%";
        elem.style.maxWidth = "100%";
        elem.style.minWidth = "0";
    }

    /**
     * Aligns the editor root with the current SwarmUI parameter-group layout rules.
     */
    private applyEditorLayout(editor: HTMLElement): void
    {
        this.applyFullWidthLayout(editor);
        editor.style.flex = "1 1 100%";
        editor.style.overflow = "visible";
    }

    /**
     * Retries generate-button wrapping until the main handler is ready.
     */
    public startGenerateWrapRetry(intervalMs = 250): void
    {
        if (this.genWrapInterval) {
            return;
        }

        let tryWrap = () => {
            try {
                this.wrapGenerateWithValidation();
                if (typeof mainGenHandler != "undefined"
                    && mainGenHandler
                    && typeof mainGenHandler.doGenerate == "function"
                    && mainGenHandler.doGenerate.__videoStagesWrapped
                ) {
                    clearInterval(this.genWrapInterval!);
                    this.genWrapInterval = null;
                }
            }
            catch {
            }
        };

        tryWrap();
        this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }

    /**
     * Creates the root editor container inside the VideoStages group.
     */
    private createEditor(): void
    {
        let editor = document.getElementById("videostages_stage_editor");
        if (!editor) {
            editor = document.createElement("div");
            editor.id = "videostages_stage_editor";
            editor.className = "videostages-stage-editor keep_group_visible";
            document.getElementById("input_group_content_videostages")!.appendChild(editor);
        }

        this.applyEditorLayout(editor);
        this.editor = editor;
    }

    private getStagesInput(): HTMLInputElement | null
    {
        return VideoStageUtils.getInputElement("input_videostages");
    }

    private getEnableToggle(): HTMLInputElement | null
    {
        return VideoStageUtils.getInputElement("input_enableadditionalvideostages")
            ?? VideoStageUtils.getInputElement("input_enablevideostages");
    }

    private getRootModelInput(): HTMLInputElement | null
    {
        return VideoStageUtils.getInputElement("input_model");
    }

    private isRootTextToVideoModel(): boolean
    {
        let modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
        if (!modelName) {
            return false;
        }

        if (typeof modelsHelpers != "undefined"
            && modelsHelpers
            && typeof modelsHelpers.getDataFor == "function") {
            let modelData = modelsHelpers.getDataFor("Stable-Diffusion", modelName);
            if (modelData?.modelClass?.compatClass?.isText2Video) {
                return true;
            }
        }

        if (typeof currentModelHelper != "undefined"
            && currentModelHelper
            && currentModelHelper.curCompatClass
            && typeof modelsHelpers != "undefined"
            && modelsHelpers
            && modelsHelpers.compatClasses) {
            let compatClass = modelsHelpers.compatClasses[currentModelHelper.curCompatClass];
            return !!compatClass?.isText2Video;
        }

        return false;
    }

    private getDefaultStageModel(modelValues: string[]): string
    {
        if (this.isRootTextToVideoModel()) {
            let modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
            if (modelName) {
                return modelName;
            }
        }

        return this.firstValue(modelValues, "");
    }

    private getRootDefaults(): RootDefaults
    {
        let model = VideoStageUtils.getSelectElement("input_videomodel");
        if ((!model || model.options.length == 0) && this.isRootTextToVideoModel()) {
            model = VideoStageUtils.getSelectElement("input_model");
        }
        let vae = VideoStageUtils.getSelectElement("input_vae");
        let sampler = this.getDropdownOptions("sampler", "input_sampler");
        let scheduler = this.getDropdownOptions("scheduler", "input_scheduler");
        let upscaleMethod = VideoStageUtils.getSelectElement("input_refinerupscalemethod");
        let steps = VideoStageUtils.getInputElement("input_videosteps") ?? VideoStageUtils.getInputElement("input_steps");
        let cfgScale = VideoStageUtils.getInputElement("input_videocfg") ?? VideoStageUtils.getInputElement("input_cfgscale");
        let allUpscaleMethodValues = VideoStageUtils.getSelectValues(upscaleMethod);
        let allUpscaleMethodLabels = VideoStageUtils.getSelectLabels(upscaleMethod);
        let upscaleMethodValues = allUpscaleMethodValues.filter((value) =>
            value.startsWith("pixel-")
            || value.startsWith("model-")
            || value.startsWith("latent-")
            || value.startsWith("latentmodel-"));
        let upscaleMethodLabels = allUpscaleMethodLabels.filter((_, index) => {
            let value = allUpscaleMethodValues[index];
            return value.startsWith("pixel-")
                || value.startsWith("model-")
                || value.startsWith("latent-")
                || value.startsWith("latentmodel-");
        });

        let fallbackUpscaleMethods = [
            "pixel-lanczos",
            "pixel-bicubic",
            "pixel-area",
            "pixel-bilinear",
            "pixel-nearest-exact"
        ];

        return {
            modelValues: VideoStageUtils.getSelectValues(model),
            modelLabels: VideoStageUtils.getSelectLabels(model),
            vaeValues: VideoStageUtils.getSelectValues(vae),
            vaeLabels: VideoStageUtils.getSelectLabels(vae),
            samplerValues: sampler.values,
            samplerLabels: sampler.labels,
            schedulerValues: scheduler.values,
            schedulerLabels: scheduler.labels,
            upscaleMethodValues: upscaleMethodValues.length > 0 ? upscaleMethodValues : fallbackUpscaleMethods,
            upscaleMethodLabels: upscaleMethodLabels.length > 0 ? upscaleMethodLabels : fallbackUpscaleMethods,
            control: 1,
            controlMin: 0,
            controlMax: 1,
            controlStep: 0.05,
            upscale: 1,
            upscaleMin: 0.25,
            upscaleMax: 8,
            upscaleStep: 0.25,
            steps: Math.max(1, Math.round(VideoStageUtils.toNumber(steps?.value, 20))),
            stepsMin: Math.max(1, Math.round(VideoStageUtils.toNumber(steps?.min, 1))),
            stepsMax: Math.max(1, Math.round(VideoStageUtils.toNumber(steps?.max, 200))),
            stepsStep: Math.max(1, Math.round(VideoStageUtils.toNumber(steps?.step, 1))),
            cfgScale: VideoStageUtils.toNumber(cfgScale?.value, 7),
            cfgScaleMin: VideoStageUtils.toNumber(cfgScale?.min, 0),
            cfgScaleMax: VideoStageUtils.toNumber(cfgScale?.max, 100),
            cfgScaleStep: VideoStageUtils.toNumber(cfgScale?.step, 0.5),
        };
    }

    private buildDefaultStage(_stageIndex: number, previousStage: Stage | null): Stage
    {
        let defaults = this.getRootDefaults();
        return {
            control: previousStage ? previousStage.control : defaults.control,
            upscale: previousStage ? previousStage.upscale : defaults.upscale,
            upscaleMethod: previousStage
                ? previousStage.upscaleMethod
                : (defaults.upscaleMethodValues.includes("pixel-lanczos")
                    ? "pixel-lanczos"
                    : this.firstValue(defaults.upscaleMethodValues, "pixel-lanczos")),
            model: previousStage ? previousStage.model : this.getDefaultStageModel(defaults.modelValues),
            vae: previousStage ? previousStage.vae : this.firstValue(defaults.vaeValues, ""),
            steps: previousStage ? previousStage.steps : defaults.steps,
            cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
            sampler: previousStage ? previousStage.sampler : this.firstValue(defaults.samplerValues, "euler"),
            scheduler: previousStage ? previousStage.scheduler : this.firstValue(defaults.schedulerValues, "normal"),
            imageReference: previousStage ? previousStage.imageReference : "Generated"
        };
    }

    private getDropdownOptions(paramId: string, fallbackSelectId: string): { values: string[]; labels: string[] }
    {
        if (typeof getParamById == "function") {
            let param = getParamById(paramId);
            if (param?.values && Array.isArray(param.values) && param.values.length > 0) {
                let labels = Array.isArray(param.value_names) && param.value_names.length == param.values.length
                    ? [...param.value_names]
                    : [...param.values];
                return { values: [...param.values], labels: labels };
            }
        }

        let select = VideoStageUtils.getSelectElement(fallbackSelectId);
        return {
            values: VideoStageUtils.getSelectValues(select),
            labels: VideoStageUtils.getSelectLabels(select)
        };
    }

    private normalizeStage(rawStage: Partial<Stage>, stageIndex: number, previousStage: Stage | null): Stage
    {
        let fallback = this.buildDefaultStage(stageIndex, previousStage);
        let normalized: Stage = {
            control: this.clamp(VideoStageUtils.toNumber(`${rawStage.control ?? fallback.control}`, fallback.control), 0, 1),
            upscale: Math.max(0.25, VideoStageUtils.toNumber(`${rawStage.upscale ?? fallback.upscale}`, fallback.upscale)),
            upscaleMethod: `${(rawStage.upscaleMethod ?? fallback.upscaleMethod) || ""}`,
            model: `${(rawStage.model ?? fallback.model) || ""}`,
            vae: `${(rawStage.vae ?? fallback.vae) || ""}`,
            steps: Math.max(1, Math.round(VideoStageUtils.toNumber(`${rawStage.steps ?? fallback.steps}`, fallback.steps))),
            cfgScale: VideoStageUtils.toNumber(`${rawStage.cfgScale ?? fallback.cfgScale}`, fallback.cfgScale),
            sampler: `${(rawStage.sampler ?? fallback.sampler) || ""}`,
            scheduler: `${(rawStage.scheduler ?? fallback.scheduler) || ""}`,
            imageReference: `${(rawStage.imageReference ?? fallback.imageReference) || ""}`,
        };

        if (!normalized.upscaleMethod) {
            normalized.upscaleMethod = fallback.upscaleMethod;
        }
        if (!normalized.model) {
            normalized.model = fallback.model;
        }
        if (!normalized.vae) {
            normalized.vae = fallback.vae;
        }
        if (!normalized.sampler) {
            normalized.sampler = fallback.sampler;
        }
        if (!normalized.scheduler) {
            normalized.scheduler = fallback.scheduler;
        }

        normalized.imageReference = this.normalizeImageReference(normalized.imageReference, stageIndex);
        return normalized;
    }

    private getStages(): Stage[]
    {
        let input = this.getStagesInput();
        if (!input || !input.value) {
            return [];
        }

        try {
            let parsed = JSON.parse(input.value);
            if (!Array.isArray(parsed)) {
                return [];
            }

            let stages: Stage[] = [];
            for (let i = 0; i < parsed.length; i++) {
                let previousStage = i > 0 ? stages[i - 1] : null;
                stages.push(this.normalizeStage(parsed[i] ?? {}, i, previousStage));
            }
            return stages;
        }
        catch {
            return [];
        }
    }

    private saveStages(stages: Stage[]): void
    {
        let input = this.getStagesInput();
        if (!input) {
            return;
        }

        input.value = JSON.stringify(stages);
        triggerChangeFor(input);
    }

    private ensureStagesSeeded(): void
    {
        let stages = this.getStages();
        if (stages.length > 0) {
            return;
        }

        this.saveStages([this.buildDefaultStage(0, null)]);
    }

    private isVideoStagesEnabled(): boolean
    {
        let toggler = this.getEnableToggle();
        return toggler ? toggler.checked : false;
    }

    private hasRootVideoModel(): boolean
    {
        let videoModel = VideoStageUtils.getInputElement("input_videomodel");
        if (videoModel?.value) {
            return true;
        }

        return this.isRootTextToVideoModel();
    }

    private validateStages(stages: Stage[]): string[]
    {
        let errors: string[] = [];
        if (!this.hasRootVideoModel()) {
            errors.push("VideoStages requires a root Video Model.");
        }
        if (stages.length < 1) {
            errors.push("VideoStages requires at least one stage.");
            return errors;
        }

        for (let i = 0; i < stages.length; i++) {
            let stage = stages[i];
            let label = `VideoStages: Stage ${i}`;
            if (!stage.model) {
                errors.push(`${label} is missing a video model.`);
            }
            if (!stage.sampler) {
                errors.push(`${label} is missing a sampler.`);
            }
            if (!stage.scheduler) {
                errors.push(`${label} is missing a scheduler.`);
            }
            if (!this.isValidImageReference(stage.imageReference, i)) {
                errors.push(`${label} has an invalid guide image reference "${stage.imageReference}".`);
            }
        }

        return errors;
    }

    private wrapGenerateWithValidation(): void
    {
        if (this.genButtonWrapped) {
            return;
        }

        let original = mainGenHandler.doGenerate.bind(mainGenHandler);
        let stageEditor = this;
        mainGenHandler.doGenerate = function(...args: unknown[]) {
            let stagesInput = stageEditor.getStagesInput();
            if (!stagesInput) {
                return original(...args);
            }

            if (!stageEditor.isVideoStagesEnabled()) {
                return original(...args);
            }

            stageEditor.serializeStagesFromUi();
            let stages = stageEditor.getStages();
            let errors = stageEditor.validateStages(stages);
            if (errors.length > 0) {
                showError(errors[0]);
                return;
            }

            return original(...args);
        };
        mainGenHandler.doGenerate.__videoStagesWrapped = true;
        this.genButtonWrapped = true;
    }

    /**
     * Reads the current UI values back into the hidden JSON field.
     */
    private serializeStagesFromUi(): void
    {
        let existingStages = this.getStages();
        let nextStages: Stage[] = [];

        for (let i = 0; i < existingStages.length; i++) {
            let previousStage = i > 0 ? nextStages[i - 1] : null;
            let prefix = `videostages_stage_${i}_`;
            nextStages.push(this.readStageFromUi(prefix, i, previousStage, existingStages[i]));
        }

        if (nextStages.length < 1) {
            nextStages.push(this.buildDefaultStage(0, null));
        }
        this.saveStages(nextStages);
    }

    private installStageChangeListener(): void
    {
        if (this.changeListenerElem == this.editor) {
            return;
        }

        let handler = (event: Event) => {
            try {
                let target = event.target as Element | null;
                if (!target || !target.closest("[data-videostages-stage-id]")) {
                    return;
                }

                this.scheduleStageSyncFromUi();
            }
            catch {
            }
        };

        this.editor.addEventListener("input", handler, true);
        this.editor.addEventListener("change", handler, true);
        this.changeListenerElem = this.editor;
    }

    private installSourceDropdownObserver(): void
    {
        if (this.sourceDropdownObserver || typeof MutationObserver == "undefined") {
            return;
        }

        let observer = new MutationObserver((mutations) => {
            if (!mutations.some((mutation) => mutation.type == "childList")) {
                return;
            }

            this.scheduleStageRefresh();
        });

        let hasObservedSource = false;
        for (let sourceId of ["input_videomodel", "input_model", "input_vae", "input_sampler", "input_scheduler", "input_refinerupscalemethod"]) {
            let source = VideoStageUtils.getSelectElement(sourceId);
            if (!source) {
                continue;
            }

            observer.observe(source, { childList: true });
            hasObservedSource = true;
        }

        if (!hasObservedSource) {
            observer.disconnect();
            return;
        }

        this.sourceDropdownObserver = observer;
    }

    private scheduleStageSyncFromUi(): void
    {
        if (this.stageSyncTimer) {
            clearTimeout(this.stageSyncTimer);
        }

        this.stageSyncTimer = setTimeout(() => {
            this.stageSyncTimer = null;
            try {
                this.serializeStagesFromUi();
            }
            catch {
            }
        }, 125);
    }

    private scheduleStageRefresh(): void
    {
        if (this.stageRefreshTimer) {
            clearTimeout(this.stageRefreshTimer);
        }

        this.stageRefreshTimer = setTimeout(() => {
            this.stageRefreshTimer = null;
            try {
                this.serializeStagesFromUi();
            }
            catch {
            }

            try {
                this.showStages();
            }
            catch {
            }
        }, 0);
    }

    private showStages(): void
    {
        let stages = this.getStages();
        if (stages.length < 1) {
            stages = [this.buildDefaultStage(0, null)];
            this.saveStages(stages);
        }

        let list = document.createElement("div");
        list.className = "videostages-stage-list";
        this.applyFullWidthLayout(list);
        this.editor.innerHTML = "";
        this.editor.appendChild(list);

        for (let i = 0; i < stages.length; i++) {
            let stage = stages[i];
            let wrap = document.createElement("div");
            wrap.className = "input-group input-group-open videostages-stage-wrap";
            wrap.classList.add("border", "rounded", "p-2", "mb-2");
            wrap.dataset.videostagesStageId = `${i}`;
            this.applyFullWidthLayout(wrap);

            let header = document.createElement("span");
            header.className = "input-group-header input-group-noshrink";
            header.innerHTML =
                `<span class="header-label-wrap">`
                + `<span class="header-label">Video Stage ${i}</span>`
                + `<span class="header-label-spacer"></span>`
                + `<button class="interrupt-button" title="Remove stage" data-videostages-action="remove-stage">×</button>`
                + `</span>`;
            wrap.appendChild(header);

            let content = document.createElement("div");
            content.className = "input-group-content videostages-stage-content";
            this.applyFullWidthLayout(content);
            wrap.appendChild(content);
            list.appendChild(wrap);

            let prefix = `videostages_stage_${i}_`;
            let parts = this.buildFieldsForStage(stage, prefix, i);
            content.insertAdjacentHTML("beforeend", parts.map((part) => part.html).join(""));
            for (let part of parts) {
                try {
                    part.runnable();
                }
                catch {
                }
            }
        }

        this.addRemoveButtonListener(list);

        let addButton = document.createElement("button");
        addButton.className = "basic-button";
        addButton.innerText = "+ Add Video Stage";
        addButton.addEventListener("click", (event) => {
            event.preventDefault();
            this.serializeStagesFromUi();
            let current = this.getStages();
            let previousStage = current.length > 0 ? current[current.length - 1] : null;
            current.push(this.buildDefaultStage(current.length, previousStage));
            this.saveStages(current);
            this.showStages();
        });
        this.editor.appendChild(addButton);
    }

    private addRemoveButtonListener(list: HTMLElement): void
    {
        list.addEventListener("click", (event) => {
            let target = event.target as Element | null;
            let button = target?.closest('button[data-videostages-action="remove-stage"]');
            if (!button) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            this.serializeStagesFromUi();

            let stageWrap = button.closest("[data-videostages-stage-id]") as HTMLElement | null;
            let stageIndex = parseInt(stageWrap?.dataset.videostagesStageId ?? "-1", 10);
            if (stageIndex < 0) {
                return;
            }

            let stages = this.getStages();
            stages.splice(stageIndex, 1);
            if (stages.length < 1) {
                stages.push(this.buildDefaultStage(0, null));
            }
            this.rebaseStageReferences(stages, stageIndex);
            this.saveStages(stages);
            this.showStages();
        });
    }

    private buildFieldsForStage(stage: Stage, prefix: string, stageIndex: number): Array<{ html: string; runnable: () => void }>
    {
        let defaults = this.getRootDefaults();
        let imageReferenceOptions = this.buildImageReferenceOptions(stageIndex, stage.imageReference);
        let modelOptions = this.withCurrentOption(defaults.modelValues, defaults.modelLabels, stage.model, stage.model);
        let vaeOptions = this.withCurrentOption(defaults.vaeValues, defaults.vaeLabels, stage.vae, stage.vae);
        let samplerOptions = this.withCurrentOption(defaults.samplerValues, defaults.samplerLabels, stage.sampler, stage.sampler);
        let schedulerOptions = this.withCurrentOption(defaults.schedulerValues, defaults.schedulerLabels, stage.scheduler, stage.scheduler);
        let upscaleMethodOptions = this.withCurrentOption(defaults.upscaleMethodValues, defaults.upscaleMethodLabels, stage.upscaleMethod, stage.upscaleMethod);
        let parts: Array<{ html: string; runnable: () => void }> = [];

        parts.push(getHtmlForParam({
            id: "control",
            name: "Control",
            description: "Controls how much of the previous video stage is preserved. Values below 1 only apply when the input reference is a video.",
            type: "decimal",
            default: `${stage.control}`,
            min: defaults.controlMin,
            max: defaults.controlMax,
            step: defaults.controlStep,
            view_min: defaults.controlMin,
            view_max: defaults.controlMax,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "imagereference",
            name: "Guide Image Reference",
            description: "Which earlier output should provide the guide image for this stage. This changes conditioning only and does not replace the live video branch being refined.",
            type: "dropdown",
            values: imageReferenceOptions.values,
            value_names: imageReferenceOptions.labels,
            default: imageReferenceOptions.selected,
            toggleable: false,
            view_type: "normal",
            feature_flag: null,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "upscale",
            name: "Upscale",
            description: "Optional upscale applied before this video stage runs. 1 disables stage upscaling.",
            type: "decimal",
            default: `${stage.upscale}`,
            min: defaults.upscaleMin,
            max: defaults.upscaleMax,
            step: defaults.upscaleStep,
            view_min: 0.25,
            view_max: 4,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "upscalemethod",
            name: "Upscale Method",
            description: "How to upscale this stage input when Upscale is enabled.",
            type: "dropdown",
            values: upscaleMethodOptions.values,
            value_names: upscaleMethodOptions.labels,
            default: stage.upscaleMethod,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "model",
            name: "Model",
            description: "The image-to-video model to use for this stage.",
            type: "dropdown",
            values: modelOptions.values,
            value_names: modelOptions.labels,
            default: stage.model,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "vae",
            name: "VAE",
            description: "VAE override to use for this stage. Leave on the default selection to inherit the normal request VAE.",
            type: "dropdown",
            values: vaeOptions.values,
            value_names: vaeOptions.labels,
            default: stage.vae,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "steps",
            name: "Steps",
            description: "Number of diffusion steps for this stage.",
            type: "integer",
            default: `${stage.steps}`,
            min: defaults.stepsMin,
            max: defaults.stepsMax,
            step: defaults.stepsStep,
            view_min: 1,
            view_max: 100,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "cfgscale",
            name: "CFG Scale",
            description: "CFG scale for this stage.",
            type: "decimal",
            default: `${stage.cfgScale}`,
            min: defaults.cfgScaleMin,
            max: defaults.cfgScaleMax,
            step: defaults.cfgScaleStep,
            view_min: 1,
            view_max: 20,
            view_type: "slider",
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "sampler",
            name: "Sampler",
            description: "Sampler to use for this stage.",
            type: "dropdown",
            values: samplerOptions.values,
            value_names: samplerOptions.labels,
            default: stage.sampler,
            toggleable: false,
        }, prefix));
        parts.push(getHtmlForParam({
            id: "scheduler",
            name: "Scheduler",
            description: "Scheduler to use for this stage.",
            type: "dropdown",
            values: schedulerOptions.values,
            value_names: schedulerOptions.labels,
            default: stage.scheduler,
            toggleable: false,
        }, prefix));

        return parts;
    }

    private readStageFromUi(prefix: string, stageIndex: number, previousStage: Stage | null, fallbackStage: Stage): Stage
    {
        let stage: Partial<Stage> = {
            control: VideoStageUtils.toNumber(VideoStageUtils.getInputElement(`${prefix}control`)?.value, fallbackStage.control),
            imageReference: `${VideoStageUtils.getInputElement(`${prefix}imagereference`)?.value ?? fallbackStage.imageReference}`,
            upscale: VideoStageUtils.toNumber(VideoStageUtils.getInputElement(`${prefix}upscale`)?.value, fallbackStage.upscale),
            upscaleMethod: `${VideoStageUtils.getInputElement(`${prefix}upscalemethod`)?.value ?? fallbackStage.upscaleMethod}`,
            model: `${VideoStageUtils.getInputElement(`${prefix}model`)?.value ?? fallbackStage.model}`,
            vae: `${VideoStageUtils.getInputElement(`${prefix}vae`)?.value ?? fallbackStage.vae}`,
            steps: Math.round(VideoStageUtils.toNumber(VideoStageUtils.getInputElement(`${prefix}steps`)?.value, fallbackStage.steps)),
            cfgScale: VideoStageUtils.toNumber(VideoStageUtils.getInputElement(`${prefix}cfgscale`)?.value, fallbackStage.cfgScale),
            sampler: `${VideoStageUtils.getInputElement(`${prefix}sampler`)?.value ?? fallbackStage.sampler}`,
            scheduler: `${VideoStageUtils.getInputElement(`${prefix}scheduler`)?.value ?? fallbackStage.scheduler}`,
        };
        return this.normalizeStage(stage, stageIndex, previousStage);
    }

    private buildImageReferenceOptions(_stageIndex: number, currentValue: string): { values: string[]; labels: string[]; selected: string }
    {
        if (this.isRootTextToVideoModel()) {
            return {
                values: ["Generated"],
                labels: ["Generated Output"],
                selected: "Generated"
            };
        }

        let values = ["Generated", "Base", "Refiner"];
        let labels = ["Generated Output", "Base Output", "Refiner Output"];
        let selected = this.normalizeImageReference(currentValue, 0);
        return { values, labels, selected };
    }

    private normalizeImageReference(value: string, _stageIndex: number): string
    {
        if (this.isRootTextToVideoModel()) {
            return "Generated";
        }

        let raw = `${value || ""}`.trim();
        if (!raw) {
            return "Generated";
        }

        let compact = raw.replace(/\s+/g, "");
        if (compact == "Generated" || compact == "Base" || compact == "Refiner") {
            return compact;
        }

        return "Generated";
    }

    private isValidImageReference(value: string, stageIndex: number): boolean
    {
        return this.normalizeImageReference(value, stageIndex) == value;
    }

    /**
     * When a middle stage is removed, later explicit stage references need to collapse with the list.
     */
    private rebaseStageReferences(stages: Stage[], removedStageIndex: number): void
    {
        for (let i = 0; i < stages.length; i++) {
            let stage = stages[i];
            let currentValue = stage.imageReference;
            let match = currentValue.match(/^Stage(\d+)$/);
            if (match) {
                let referencedStage = parseInt(match[1], 10);
                if (referencedStage == removedStageIndex) {
                    stage.imageReference = "Generated";
                }
                else if (referencedStage > removedStageIndex) {
                    stage.imageReference = "Generated";
                }
            }

            stage.imageReference = this.normalizeImageReference(stage.imageReference, i);
        }
    }

    private withCurrentOption(values: string[], labels: string[], currentValue: string, currentLabel: string): { values: string[]; labels: string[] }
    {
        let nextValues = [...values];
        let nextLabels = [...labels];
        if (currentValue && !nextValues.includes(currentValue)) {
            nextValues.unshift(currentValue);
            nextLabels.unshift(currentLabel || currentValue);
        }
        return { values: nextValues, labels: nextLabels };
    }

    private clamp(value: number, min: number, max: number): number
    {
        return Math.min(Math.max(value, min), max);
    }

    private firstValue(values: string[], fallback: string): string
    {
        return values.length > 0 ? values[0] : fallback;
    }
}
