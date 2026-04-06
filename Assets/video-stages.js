"use strict";
const VideoStageUtils = {
    getInputElement: (id) => {
        return document.getElementById(id);
    },
    getSelectElement: (id) => {
        return document.getElementById(id);
    },
    getSelectValues: (select) => {
        if (!select) {
            return [];
        }
        return Array.from(select.options).map((option) => option.value);
    },
    getSelectLabels: (select) => {
        if (!select) {
            return [];
        }
        return Array.from(select.options).map((option) => option.label);
    },
    toNumber: (value, fallback) => {
        let parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }
};
class VideoStageEditor {
    editor;
    genButtonWrapped = false;
    genWrapInterval = null;
    changeListenerElem = null;
    stageSyncTimer = null;
    sourceDropdownObserver = null;
    stageRefreshTimer = null;
    stageInputSyncInterval = null;
    lastKnownStagesJson = "";
    pendingStageRefreshSerialize = false;
    /**
     * Initializes the VideoStages editor after the parameter UI exists.
     */
    init() {
        this.createEditor();
        this.startStagesInputSync();
        this.ensureStagesSeeded();
        this.wrapGenerateWithValidation();
        this.showStages();
        this.installStageChangeListener();
        this.installSourceDropdownObserver();
    }
    /**
     * Keeps custom stage containers from inheriting flex min-width overflow from the parent group.
     */
    applyFullWidthLayout(elem) {
        elem.style.width = "100%";
        elem.style.maxWidth = "100%";
        elem.style.minWidth = "0";
    }
    /**
     * Aligns the editor root with the current SwarmUI parameter-group layout rules.
     */
    applyEditorLayout(editor) {
        this.applyFullWidthLayout(editor);
        editor.style.flex = "1 1 100%";
        editor.style.overflow = "visible";
    }
    /**
     * Retries generate-button wrapping until the main handler is ready.
     */
    startGenerateWrapRetry(intervalMs = 250) {
        if (this.genWrapInterval) {
            return;
        }
        let tryWrap = () => {
            try {
                this.wrapGenerateWithValidation();
                if (typeof mainGenHandler != "undefined"
                    && mainGenHandler
                    && typeof mainGenHandler.doGenerate == "function"
                    && mainGenHandler.doGenerate.__videoStagesWrapped) {
                    clearInterval(this.genWrapInterval);
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
    createEditor() {
        let editor = document.getElementById("videostages_stage_editor");
        if (!editor) {
            editor = document.createElement("div");
            editor.id = "videostages_stage_editor";
            editor.className = "videostages-stage-editor keep_group_visible";
            document.getElementById("input_group_content_videostages").appendChild(editor);
        }
        this.applyEditorLayout(editor);
        this.editor = editor;
    }
    getStagesInput() {
        return VideoStageUtils.getInputElement("input_videostages");
    }
    getEnableToggle() {
        return VideoStageUtils.getInputElement("input_enableadditionalvideostages")
            ?? VideoStageUtils.getInputElement("input_enablevideostages");
    }
    getRootModelInput() {
        return VideoStageUtils.getInputElement("input_model");
    }
    isRootTextToVideoModel() {
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
    getDefaultStageModel(modelValues) {
        if (this.isRootTextToVideoModel()) {
            let modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
            if (modelName) {
                return modelName;
            }
        }
        return this.firstValue(modelValues, "");
    }
    getRootDefaults() {
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
        let upscaleMethodValues = allUpscaleMethodValues.filter((value) => value.startsWith("pixel-")
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
    buildDefaultStage(_stageIndex, previousStage) {
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
    getDropdownOptions(paramId, fallbackSelectId) {
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
    normalizeStage(rawStage, stageIndex, previousStage) {
        let fallback = this.buildDefaultStage(stageIndex, previousStage);
        let normalized = {
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
    getStages() {
        let input = this.getStagesInput();
        if (!input || !input.value) {
            return [];
        }
        try {
            let parsed = JSON.parse(input.value);
            if (!Array.isArray(parsed)) {
                return [];
            }
            let stages = [];
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
    saveStages(stages) {
        let input = this.getStagesInput();
        if (!input) {
            return;
        }
        let serialized = JSON.stringify(stages);
        input.value = serialized;
        this.lastKnownStagesJson = serialized;
        triggerChangeFor(input);
    }
    ensureStagesSeeded() {
        let stages = this.getStages();
        if (stages.length > 0) {
            return;
        }
        this.saveStages([this.buildDefaultStage(0, null)]);
    }
    isVideoStagesEnabled() {
        let toggler = this.getEnableToggle();
        return toggler ? toggler.checked : false;
    }
    hasRootVideoModel() {
        let videoModel = VideoStageUtils.getInputElement("input_videomodel");
        if (videoModel?.value) {
            return true;
        }
        return this.isRootTextToVideoModel();
    }
    validateStages(stages) {
        let errors = [];
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
    wrapGenerateWithValidation() {
        if (this.genButtonWrapped) {
            return;
        }
        let original = mainGenHandler.doGenerate.bind(mainGenHandler);
        let stageEditor = this;
        mainGenHandler.doGenerate = function (...args) {
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
    serializeStagesFromUi() {
        let existingStages = this.getStages();
        let nextStages = [];
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
    installStageChangeListener() {
        if (this.changeListenerElem == this.editor) {
            return;
        }
        let handler = (event) => {
            try {
                let target = event.target;
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
    installSourceDropdownObserver() {
        if (this.sourceDropdownObserver || typeof MutationObserver == "undefined") {
            return;
        }
        let observer = new MutationObserver((mutations) => {
            if (!mutations.some((mutation) => mutation.type == "childList")) {
                return;
            }
            this.scheduleStageRefresh(true);
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
    /**
     * The hidden JSON input can be populated after the custom editor first renders.
     * Poll it so the visible stage list always catches up to the real source of truth.
     */
    startStagesInputSync() {
        if (this.stageInputSyncInterval) {
            return;
        }
        this.lastKnownStagesJson = this.getStagesInput()?.value ?? "";
        this.stageInputSyncInterval = setInterval(() => {
            let currentValue = this.getStagesInput()?.value ?? "";
            if (currentValue == this.lastKnownStagesJson) {
                return;
            }
            this.lastKnownStagesJson = currentValue;
            this.cancelPendingUiStageSync();
            this.scheduleStageRefresh();
        }, 150);
    }
    cancelPendingUiStageSync() {
        if (this.stageSyncTimer) {
            clearTimeout(this.stageSyncTimer);
            this.stageSyncTimer = null;
        }
        if (this.stageRefreshTimer) {
            clearTimeout(this.stageRefreshTimer);
            this.stageRefreshTimer = null;
        }
        this.pendingStageRefreshSerialize = false;
    }
    scheduleStageSyncFromUi() {
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
    scheduleStageRefresh(serializeFromUi = false) {
        if (serializeFromUi) {
            this.pendingStageRefreshSerialize = true;
        }
        if (this.stageRefreshTimer) {
            clearTimeout(this.stageRefreshTimer);
        }
        this.stageRefreshTimer = setTimeout(() => {
            this.stageRefreshTimer = null;
            let shouldSerialize = this.pendingStageRefreshSerialize;
            this.pendingStageRefreshSerialize = false;
            try {
                if (shouldSerialize) {
                    this.serializeStagesFromUi();
                }
            }
            catch {
            }
            try {
                this.showStages();
            }
            catch {
            }
            this.lastKnownStagesJson = this.getStagesInput()?.value ?? "";
        }, 0);
    }
    showStages() {
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
    addRemoveButtonListener(list) {
        list.addEventListener("click", (event) => {
            let target = event.target;
            let button = target?.closest('button[data-videostages-action="remove-stage"]');
            if (!button) {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            this.serializeStagesFromUi();
            let stageWrap = button.closest("[data-videostages-stage-id]");
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
    buildFieldsForStage(stage, prefix, stageIndex) {
        let defaults = this.getRootDefaults();
        let imageReferenceOptions = this.buildImageReferenceOptions(stageIndex, stage.imageReference);
        let modelOptions = this.withCurrentOption(defaults.modelValues, defaults.modelLabels, stage.model, stage.model);
        let vaeOptions = this.withCurrentOption(defaults.vaeValues, defaults.vaeLabels, stage.vae, stage.vae);
        let samplerOptions = this.withCurrentOption(defaults.samplerValues, defaults.samplerLabels, stage.sampler, stage.sampler);
        let schedulerOptions = this.withCurrentOption(defaults.schedulerValues, defaults.schedulerLabels, stage.scheduler, stage.scheduler);
        let upscaleMethodOptions = this.withCurrentOption(defaults.upscaleMethodValues, defaults.upscaleMethodLabels, stage.upscaleMethod, stage.upscaleMethod);
        let parts = [];
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
    readStageFromUi(prefix, stageIndex, previousStage, fallbackStage) {
        let stage = {
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
    buildImageReferenceOptions(_stageIndex, currentValue) {
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
    normalizeImageReference(value, _stageIndex) {
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
    isValidImageReference(value, stageIndex) {
        return this.normalizeImageReference(value, stageIndex) == value;
    }
    /**
     * When a middle stage is removed, later explicit stage references need to collapse with the list.
     */
    rebaseStageReferences(stages, removedStageIndex) {
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
    withCurrentOption(values, labels, currentValue, currentLabel) {
        let nextValues = [...values];
        let nextLabels = [...labels];
        if (currentValue && !nextValues.includes(currentValue)) {
            nextValues.unshift(currentValue);
            nextLabels.unshift(currentLabel || currentValue);
        }
        return { values: nextValues, labels: nextLabels };
    }
    clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }
    firstValue(values, fallback) {
        return values.length > 0 ? values[0] : fallback;
    }
}
/// <reference path="./VideoStageEditor.ts" />
class VideoStages {
    stageEditor;
    constructor(stageEditor) {
        this.stageEditor = stageEditor;
        if (!this.tryRegisterStageEditor()) {
            let interval = setInterval(() => {
                if (this.tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        this.stageEditor.startGenerateWrapRetry();
    }
    tryRegisterStageEditor() {
        if (typeof postParamBuildSteps == "undefined" || !Array.isArray(postParamBuildSteps)) {
            return false;
        }
        postParamBuildSteps.push(() => {
            try {
                this.stageEditor.init();
            }
            catch (error) {
                console.log("VideoStages: failed to build stage editor", error);
            }
        });
        return true;
    }
}
new VideoStages(new VideoStageEditor());
//# sourceMappingURL=video-stages.js.map