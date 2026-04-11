"use strict";
// Keep toggleable parameter groups from being spuriously activated while
// "Reuse Parameters" is applying metadata that does not actually use them.
class ToggleableGroupReuseGuard {
    options;
    static guards = [];
    guardUntil = 0;
    guardTimer = null;
    constructor(options) {
        this.options = options;
        if (!ToggleableGroupReuseGuard.guards.includes(this)) {
            ToggleableGroupReuseGuard.guards.push(this);
        }
    }
    tryInstallGroupToggleWrapper() {
        if (typeof doToggleGroup != "function") {
            return false;
        }
        let wrappedExisting = doToggleGroup;
        if (wrappedExisting.__toggleableGroupReuseGuardWrapped) {
            return true;
        }
        let prior = doToggleGroup;
        let wrapped = ((id) => {
            let toggle = document.getElementById(`${id}_toggle`);
            let matchingGuards = ToggleableGroupReuseGuard.guards.filter((guard) => guard.matchesGroup(id));
            let shouldSuppress = !!toggle?.checked
                && matchingGuards.some((guard) => guard.shouldSuppressGroupActivation(id));
            if (shouldSuppress && toggle) {
                toggle.checked = false;
            }
            return prior(id);
        });
        wrapped.__toggleableGroupReuseGuardWrapped = true;
        doToggleGroup = wrapped;
        return true;
    }
    enforceInactiveState() {
        let changed = false;
        let groupToggle = this.getGroupToggle();
        if (groupToggle?.checked) {
            groupToggle.checked = false;
            if (typeof doToggleGroup == "function") {
                doToggleGroup(this.options.groupContentId);
            }
            changed = true;
        }
        let enableToggle = this.options.getEnableToggle();
        if (enableToggle?.checked) {
            enableToggle.checked = false;
            changed = true;
        }
        if (this.options.clearInactiveState?.()) {
            changed = true;
        }
        if (changed) {
            this.options.afterStateChange?.();
        }
    }
    start(durationMs = 1500) {
        this.stop();
        this.guardUntil = Date.now() + durationMs;
        let tick = () => {
            if (Date.now() >= this.guardUntil) {
                this.stop();
                return;
            }
            this.enforceInactiveState();
            this.guardTimer = setTimeout(tick, 25);
        };
        this.guardTimer = setTimeout(tick, 25);
    }
    stop() {
        if (this.guardTimer) {
            clearTimeout(this.guardTimer);
            this.guardTimer = null;
        }
        this.guardUntil = 0;
    }
    shouldSuppressGroupActivation(groupId) {
        if (groupId != this.options.groupContentId || Date.now() >= this.guardUntil) {
            return false;
        }
        return !this.options.getEnableToggle()?.checked;
    }
    matchesGroup(groupId) {
        return groupId == this.options.groupContentId;
    }
    getGroupToggle() {
        return this.options.getGroupToggle?.()
            ?? document.getElementById(`${this.options.groupContentId}_toggle`);
    }
}
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
/// <reference path="./ToggleableGroupReuseGuard.ts" />
class VideoStageEditor {
    editor;
    inactiveReuseGuard;
    genButtonWrapped = false;
    genWrapInterval = null;
    changeListenerElem = null;
    stageSyncTimer = null;
    sourceDropdownObserver = null;
    stageRefreshTimer = null;
    stageInputSyncInterval = null;
    lastKnownStagesJson = "";
    pendingStageRefreshSerialize = false;
    pendingStageRefreshNotify = false;
    lastShownValidationError = "";
    suppressInactiveReseed = false;
    constructor() {
        this.inactiveReuseGuard = new ToggleableGroupReuseGuard({
            groupContentId: "input_group_content_videostages",
            getEnableToggle: () => this.getEnableToggle(),
            getGroupToggle: () => this.getGroupToggle(),
            clearInactiveState: () => this.clearStagesForInactiveReuse(),
            afterStateChange: () => {
                if (!this.editor) {
                    return;
                }
                this.cancelPendingUiStageSync();
                this.scheduleStageRefresh();
            }
        });
    }
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
        this.installBase2EditStageChangeListener();
        this.installRootGuideImageReferenceListener();
    }
    resetForInactiveReuse() {
        this.suppressInactiveReseed = true;
        this.inactiveReuseGuard.enforceInactiveState();
        this.inactiveReuseGuard.start();
    }
    tryInstallInactiveReuseGuard() {
        return this.inactiveReuseGuard.tryInstallGroupToggleWrapper();
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
    getGroupToggle() {
        return VideoStageUtils.getInputElement("input_group_content_videostages_toggle");
    }
    getRootModelInput() {
        return VideoStageUtils.getInputElement("input_model");
    }
    getRootGuideImageReferenceInput() {
        return VideoStageUtils.getSelectElement("input_guideimagereference");
    }
    parseBase2EditStageIndex(value) {
        let match = `${value || ""}`.trim().replace(/\s+/g, "").match(/^edit(\d+)$/i);
        if (!match) {
            return null;
        }
        return parseInt(match[1], 10);
    }
    getBase2EditStageSnapshot() {
        let snapshot = window.base2editStageRegistry?.getSnapshot?.();
        if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
            return { enabled: false, stageCount: 0, refs: [] };
        }
        let refs = snapshot.refs
            .map((value) => {
            let stageIndex = this.parseBase2EditStageIndex(value);
            return stageIndex == null ? null : `edit${stageIndex}`;
        })
            .filter((value) => !!value);
        let uniqueRefs = [...new Set(refs)]
            .sort((left, right) => (this.parseBase2EditStageIndex(left) ?? 0) - (this.parseBase2EditStageIndex(right) ?? 0));
        return {
            enabled: true,
            stageCount: uniqueRefs.length,
            refs: uniqueRefs
        };
    }
    isAvailableBase2EditReference(value) {
        let stageIndex = this.parseBase2EditStageIndex(value);
        if (stageIndex == null) {
            return false;
        }
        return this.getBase2EditStageSnapshot().refs.includes(`edit${stageIndex}`);
    }
    installBase2EditStageChangeListener() {
        document.addEventListener("base2edit:stages-changed", () => {
            this.scheduleStageRefresh(true, true);
        });
    }
    installRootGuideImageReferenceListener() {
        this.getRootGuideImageReferenceInput()?.addEventListener("change", () => {
            this.refreshGuideReferenceValidation(this.getStages());
        });
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
        normalized.imageReference = this.canonicalizeStageImageReference(normalized.imageReference, stageIndex);
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
        this.suppressInactiveReseed = false;
        let serialized = JSON.stringify(stages);
        input.value = serialized;
        this.lastKnownStagesJson = serialized;
        triggerChangeFor(input);
    }
    clearStagesForInactiveReuse() {
        let input = this.getStagesInput();
        if (!input || input.value == "") {
            return false;
        }
        input.value = "";
        this.lastKnownStagesJson = "";
        return true;
    }
    shouldKeepStagesBlankWhileDisabled() {
        return this.suppressInactiveReseed && !this.isVideoStagesEnabled();
    }
    ensureStagesSeeded() {
        let stages = this.getStages();
        if (stages.length > 0) {
            return;
        }
        if (this.shouldKeepStagesBlankWhileDisabled()) {
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
        let rootGuideError = this.getRootGuideImageReferenceError(`${this.getRootGuideImageReferenceInput()?.value ?? "Default"}`);
        if (rootGuideError) {
            errors.push(rootGuideError);
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
            let imageReferenceError = this.getStageImageReferenceError(stage.imageReference, i);
            if (imageReferenceError) {
                errors.push(imageReferenceError);
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
            source.addEventListener("change", () => this.scheduleStageRefresh(true, true));
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
        this.pendingStageRefreshNotify = false;
    }
    scheduleStageSyncFromUi() {
        if (this.stageSyncTimer) {
            clearTimeout(this.stageSyncTimer);
        }
        this.stageSyncTimer = setTimeout(() => {
            this.stageSyncTimer = null;
            try {
                this.serializeStagesFromUi();
                this.refreshGuideReferenceValidation(this.getStages());
            }
            catch {
            }
        }, 125);
    }
    scheduleStageRefresh(serializeFromUi = false, notifyOnInvalid = false) {
        if (serializeFromUi) {
            this.pendingStageRefreshSerialize = true;
        }
        if (notifyOnInvalid) {
            this.pendingStageRefreshNotify = true;
        }
        if (this.stageRefreshTimer) {
            clearTimeout(this.stageRefreshTimer);
        }
        this.stageRefreshTimer = setTimeout(() => {
            this.stageRefreshTimer = null;
            let shouldSerialize = this.pendingStageRefreshSerialize;
            let shouldNotify = this.pendingStageRefreshNotify;
            this.pendingStageRefreshSerialize = false;
            this.pendingStageRefreshNotify = false;
            try {
                if (shouldSerialize) {
                    this.serializeStagesFromUi();
                }
            }
            catch {
            }
            let errors = [];
            try {
                errors = this.showStages();
            }
            catch {
            }
            if (errors.length > 0) {
                if (shouldNotify && this.lastShownValidationError != errors[0]) {
                    showError(errors[0]);
                }
                this.lastShownValidationError = errors[0];
            }
            else {
                this.lastShownValidationError = "";
            }
            this.lastKnownStagesJson = this.getStagesInput()?.value ?? "";
        }, 0);
    }
    showStages() {
        let stages = this.getStages();
        if (stages.length < 1) {
            stages = [this.buildDefaultStage(0, null)];
            if (!this.shouldKeepStagesBlankWhileDisabled()) {
                this.saveStages(stages);
            }
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
            this.applyImageReferenceOptionState(`${prefix}imagereference`, stage.imageReference, i);
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
        this.syncRootGuideImageReferenceOptions();
        return this.refreshGuideReferenceValidation(stages);
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
        let imageReferenceOptions = this.buildStageImageReferenceOptions(stageIndex, stage.imageReference);
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
    getDefaultStageImageReference(stageIndex) {
        return stageIndex > 0 ? "PreviousStage" : "Generated";
    }
    canonicalizeStageImageReference(value, stageIndex) {
        if (this.isRootTextToVideoModel()) {
            return "Generated";
        }
        let raw = `${value || ""}`.trim();
        if (!raw) {
            return this.getDefaultStageImageReference(stageIndex);
        }
        let compact = raw.replace(/\s+/g, "");
        if (compact == "Generated" || compact == "Base" || compact == "Refiner") {
            return compact;
        }
        if (stageIndex > 0 && compact.toLowerCase() == "previousstage") {
            return "PreviousStage";
        }
        let stageMatch = compact.match(/^Stage(\d+)$/i);
        if (stageMatch) {
            let explicitStage = parseInt(stageMatch[1], 10);
            return explicitStage < stageIndex
                ? `Stage${explicitStage}`
                : this.getDefaultStageImageReference(stageIndex);
        }
        let editStage = this.parseBase2EditStageIndex(compact);
        if (editStage != null) {
            return `edit${editStage}`;
        }
        return this.getDefaultStageImageReference(stageIndex);
    }
    canonicalizeRootGuideImageReference(value) {
        if (this.isRootTextToVideoModel()) {
            return "Default";
        }
        let raw = `${value || ""}`.trim();
        if (!raw) {
            return "Default";
        }
        let compact = raw.replace(/\s+/g, "");
        if (compact == "Default" || compact == "Base" || compact == "Refiner") {
            return compact;
        }
        let editStage = this.parseBase2EditStageIndex(compact);
        if (editStage != null) {
            return `edit${editStage}`;
        }
        return "Default";
    }
    describeImageReference(value) {
        if (value == "Generated") {
            return "Generated Output";
        }
        if (value == "Default") {
            return "Default";
        }
        if (value == "Base") {
            return "Base Output";
        }
        if (value == "Refiner") {
            return "Refiner Output";
        }
        if (value == "PreviousStage") {
            return "Previous Video Stage Output";
        }
        let stageMatch = value.match(/^Stage(\d+)$/);
        if (stageMatch) {
            return `Video Stage ${parseInt(stageMatch[1], 10)} Output`;
        }
        let editStage = this.parseBase2EditStageIndex(value);
        if (editStage != null) {
            return `Base2Edit Edit ${editStage} Output`;
        }
        return value;
    }
    buildStageImageReferenceOptions(stageIndex, currentValue) {
        if (this.isRootTextToVideoModel()) {
            return {
                values: ["Generated"],
                labels: ["Generated Output"],
                selected: "Generated",
                disabledValues: []
            };
        }
        let values = ["Generated", "Base", "Refiner"];
        let labels = ["Generated Output", "Base Output", "Refiner Output"];
        if (stageIndex > 0) {
            values.push("PreviousStage");
            labels.push("Previous Video Stage Output");
            for (let i = 0; i < stageIndex; i++) {
                values.push(`Stage${i}`);
                labels.push(`Video Stage ${i} Output`);
            }
        }
        for (let base2EditRef of this.getBase2EditStageSnapshot().refs) {
            values.push(base2EditRef);
            labels.push(this.describeImageReference(base2EditRef));
        }
        let selected = this.canonicalizeStageImageReference(currentValue, stageIndex);
        let disabledValues = [];
        if (selected && !values.includes(selected)) {
            let isMissingBase2EditRef = this.parseBase2EditStageIndex(selected) != null;
            values.unshift(selected);
            labels.unshift(isMissingBase2EditRef
                ? `Missing ${this.describeImageReference(selected)}`
                : this.describeImageReference(selected));
            if (isMissingBase2EditRef) {
                disabledValues.push(selected);
            }
        }
        return { values, labels, selected, disabledValues };
    }
    buildRootGuideImageReferenceOptions(currentValue) {
        if (this.isRootTextToVideoModel()) {
            return {
                values: ["Default"],
                labels: ["Default"],
                selected: "Default",
                disabledValues: []
            };
        }
        let values = ["Default", "Base", "Refiner"];
        let labels = ["Default", "Base Output", "Refiner Output"];
        for (let base2EditRef of this.getBase2EditStageSnapshot().refs) {
            values.push(base2EditRef);
            labels.push(this.describeImageReference(base2EditRef));
        }
        let selected = this.canonicalizeRootGuideImageReference(currentValue);
        let disabledValues = [];
        if (selected && !values.includes(selected)) {
            let isMissingBase2EditRef = this.parseBase2EditStageIndex(selected) != null;
            values.unshift(selected);
            labels.unshift(isMissingBase2EditRef
                ? `Missing ${this.describeImageReference(selected)}`
                : this.describeImageReference(selected));
            if (isMissingBase2EditRef) {
                disabledValues.push(selected);
            }
        }
        return { values, labels, selected, disabledValues };
    }
    getStageImageReferenceError(value, stageIndex) {
        if (this.canonicalizeStageImageReference(value, stageIndex) != value) {
            return `VideoStages: Stage ${stageIndex} has an invalid guide image reference "${value}".`;
        }
        if (this.parseBase2EditStageIndex(value) != null && !this.isAvailableBase2EditReference(value)) {
            return `VideoStages: Stage ${stageIndex} guide image reference "${value}" points to a missing Base2Edit stage.`;
        }
        return null;
    }
    getRootGuideImageReferenceError(value) {
        if (this.canonicalizeRootGuideImageReference(value) != value) {
            return `VideoStages: Root guide image reference "${value}" is invalid.`;
        }
        if (this.parseBase2EditStageIndex(value) != null && !this.isAvailableBase2EditReference(value)) {
            return `VideoStages: Root guide image reference "${value}" points to a missing Base2Edit stage.`;
        }
        return null;
    }
    applySelectOptions(select, options) {
        select.innerHTML = "";
        let disabledValues = new Set(options.disabledValues);
        for (let i = 0; i < options.values.length; i++) {
            let option = document.createElement("option");
            option.value = options.values[i];
            option.text = options.labels[i];
            option.disabled = disabledValues.has(option.value);
            option.selected = option.value == options.selected;
            select.appendChild(option);
        }
        select.value = options.selected;
    }
    applyImageReferenceOptionState(inputId, currentValue, stageIndex) {
        let select = VideoStageUtils.getSelectElement(inputId);
        if (!select) {
            return;
        }
        let disabledValues = new Set(this.buildStageImageReferenceOptions(stageIndex, currentValue).disabledValues);
        for (let option of Array.from(select.options)) {
            option.disabled = disabledValues.has(option.value);
        }
    }
    syncRootGuideImageReferenceOptions() {
        let select = this.getRootGuideImageReferenceInput();
        if (!select) {
            return;
        }
        this.applySelectOptions(select, this.buildRootGuideImageReferenceOptions(`${select.value || "Default"}`));
    }
    setInputValidationState(input, errorId, error) {
        if (!input) {
            return;
        }
        input.classList.remove("is-invalid");
        document.getElementById(errorId)?.remove();
        if (!error) {
            return;
        }
        input.classList.add("is-invalid");
        let parent = findParentOfClass(input, "auto-input");
        if (!parent) {
            return;
        }
        let errorElem = document.createElement("div");
        errorElem.id = errorId;
        errorElem.className = "text-danger";
        errorElem.style.marginTop = "4px";
        errorElem.innerText = error;
        parent.appendChild(errorElem);
    }
    refreshGuideReferenceValidation(stages) {
        let errors = [];
        let rootGuideError = this.getRootGuideImageReferenceError(`${this.getRootGuideImageReferenceInput()?.value ?? "Default"}`);
        this.setInputValidationState(this.getRootGuideImageReferenceInput(), "videostages_root_guideimagereference_error", rootGuideError);
        if (rootGuideError) {
            errors.push(rootGuideError);
        }
        for (let i = 0; i < stages.length; i++) {
            let error = this.getStageImageReferenceError(stages[i].imageReference, i);
            this.setInputValidationState(VideoStageUtils.getSelectElement(`videostages_stage_${i}_imagereference`), `videostages_stage_${i}_imagereference_error`, error);
            if (error) {
                errors.push(error);
            }
        }
        return errors;
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
            stage.imageReference = this.canonicalizeStageImageReference(stage.imageReference, i);
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
    visibleParamIds = [
        "rootwidth",
        "rootheight"
    ];
    constructor(stageEditor) {
        this.stageEditor = stageEditor;
        if (!this.stageEditor.tryInstallInactiveReuseGuard()) {
            let interval = setInterval(() => {
                if (this.stageEditor.tryInstallInactiveReuseGuard()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        if (!this.tryRegisterStageEditor()) {
            let interval = setInterval(() => {
                if (this.tryRegisterStageEditor()) {
                    clearInterval(interval);
                }
            }, 200);
        }
        if (!this.tryWrapReuseParameters()) {
            let interval = setInterval(() => {
                if (this.tryWrapReuseParameters()) {
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
    tryWrapReuseParameters() {
        if (typeof copy_current_image_params != "function") {
            return false;
        }
        let wrappedExisting = copy_current_image_params;
        if (wrappedExisting.__videoStagesWrapped) {
            return true;
        }
        let prior = copy_current_image_params;
        let wrapped = (() => {
            let metadataUsesVideoStages = this.currentImageUsesVideoStages();
            prior();
            if (metadataUsesVideoStages === false) {
                this.stageEditor.resetForInactiveReuse();
            }
        });
        wrapped.__videoStagesWrapped = true;
        copy_current_image_params = wrapped;
        return true;
    }
    currentImageUsesVideoStages() {
        if (!currentMetadataVal) {
            return null;
        }
        try {
            let metadataFull = JSON.parse(interpretMetadata(currentMetadataVal));
            let metadata = metadataFull?.sui_image_params;
            if (!metadata || typeof metadata != "object") {
                return null;
            }
            let enabled = metadata.enableadditionalvideostages ?? metadata.enablevideostages;
            if (`${enabled}` == "true") {
                return true;
            }
            if (this.visibleParamIds.some((id) => metadata[id] !== undefined && metadata[id] !== null && metadata[id] !== "")) {
                return true;
            }
            let guideReference = `${metadata.guideimagereference ?? ""}`.trim();
            if (guideReference && guideReference.toLowerCase() != "default") {
                return true;
            }
            // The hidden `videostages` JSON is always seeded locally, so it is not
            // trustworthy evidence that the reused image truly opted into VideoStages.
            return false;
        }
        catch (error) {
            console.log("VideoStages: failed to inspect reused image metadata", error);
            return null;
        }
    }
}
new VideoStages(new VideoStageEditor());
//# sourceMappingURL=video-stages.js.map