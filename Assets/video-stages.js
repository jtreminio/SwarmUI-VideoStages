"use strict";
(() => {
  // frontend/Utils.ts
  var VideoStageUtils = {
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
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    }
  };

  // frontend/AudioSourceController.ts
  var AUDIO_SOURCE_NATIVE = "Native";
  var AUDIO_SOURCE_UPLOAD = "Upload";
  var AUDIO_SOURCE_SWARM = "Swarm Audio";
  var TEXT2AUDIO_TOGGLE_ID = "input_group_content_texttoaudio_toggle";
  var ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
  var SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
  var getSourceSelects = () => Array.from(document.querySelectorAll(SOURCE_SELECT_SELECTOR)).filter(
    (elem) => elem instanceof HTMLSelectElement
  );
  var isSourceSelect = (target) => target instanceof HTMLSelectElement && target.matches('[data-clip-field="audioSource"]');
  var isTextToAudioEnabled = () => {
    const toggle = VideoStageUtils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
    return !!toggle?.checked;
  };
  var getAceStepFunRefs = () => {
    const snapshot = window.acestepfunTrackRegistry?.getSnapshot?.();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
      return [];
    }
    const seen = /* @__PURE__ */ new Set();
    const refs = [];
    for (const raw of snapshot.refs) {
      const ref = `${raw || ""}`.trim();
      if (!ref || seen.has(ref)) {
        continue;
      }
      seen.add(ref);
      refs.push(ref);
    }
    return refs;
  };
  var buildAudioSourceOptions = () => {
    const options = [
      { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD }
    ];
    if (isTextToAudioEnabled()) {
      options.push({
        value: AUDIO_SOURCE_SWARM,
        label: AUDIO_SOURCE_SWARM
      });
    }
    for (const ref of getAceStepFunRefs()) {
      options.push({ value: ref, label: ref });
    }
    return options;
  };
  var resolveAudioSourceValue = (currentValue, options) => {
    const desired = `${currentValue || ""}`;
    if (options.some((option) => option.value === desired)) {
      return desired;
    }
    return AUDIO_SOURCE_NATIVE;
  };
  var AudioSourceController = () => {
    const refreshOptions = () => {
      const selects = getSourceSelects();
      if (selects.length === 0) {
        return;
      }
      const options = buildAudioSourceOptions();
      for (const select of selects) {
        const desired = resolveAudioSourceValue(select.value, options);
        const newValuesJson = JSON.stringify(options.map((o) => o.value));
        const currentValuesJson = JSON.stringify(
          Array.from(select.options).map((o) => o.value)
        );
        if (newValuesJson === currentValuesJson && select.value === desired) {
          continue;
        }
        select.innerHTML = "";
        for (const option of options) {
          const elem = document.createElement("option");
          elem.value = option.value;
          elem.text = option.label;
          elem.selected = option.value === desired;
          select.appendChild(elem);
        }
        select.value = desired;
        triggerChangeFor(select);
      }
    };
    const onDocumentDropdownInteraction = (event) => {
      if (isSourceSelect(event.target)) {
        refreshOptions();
      }
    };
    let lastBoundText2AudioToggle = null;
    const bindText2AudioToggle = () => {
      const toggle = VideoStageUtils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
      if (!toggle || toggle === lastBoundText2AudioToggle) {
        return;
      }
      toggle.addEventListener("change", refreshOptions);
      lastBoundText2AudioToggle = toggle;
    };
    const runOnEachBuild = () => {
      try {
        bindText2AudioToggle();
        refreshOptions();
      } catch (error) {
        console.log(
          "AudioSourceController: param build sync failed",
          error
        );
      }
    };
    const scheduleInitialSync = () => {
      if (typeof postParamBuildSteps !== "undefined" && Array.isArray(postParamBuildSteps)) {
        postParamBuildSteps.push(runOnEachBuild);
        return;
      }
      setTimeout(scheduleInitialSync, 200);
    };
    document.addEventListener("mousedown", onDocumentDropdownInteraction);
    document.addEventListener("focusin", onDocumentDropdownInteraction);
    document.addEventListener(ACESTEPFUN_EVENT, refreshOptions);
    scheduleInitialSync();
    return {
      buildOptions: buildAudioSourceOptions,
      resolveSelectedValue: resolveAudioSourceValue,
      refreshOptions,
      runOnEachBuild,
      dispose: () => {
        document.removeEventListener(
          "mousedown",
          onDocumentDropdownInteraction
        );
        document.removeEventListener(
          "focusin",
          onDocumentDropdownInteraction
        );
        document.removeEventListener(ACESTEPFUN_EVENT, refreshOptions);
        if (lastBoundText2AudioToggle) {
          lastBoundText2AudioToggle.removeEventListener(
            "change",
            refreshOptions
          );
          lastBoundText2AudioToggle = null;
        }
      }
    };
  };

  // frontend/RenderUtils.ts
  var escapeAttr = (value) => String(value ?? "").replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  var renderOptionList = (options, selected) => options.map((option) => {
    const value = escapeAttr(option.value);
    const label = escapeAttr(option.label);
    const isSelected = option.value === selected ? " selected" : "";
    const isDisabled = option.disabled ? " disabled" : "";
    return `<option value="${value}"${isSelected}${isDisabled}>${label}</option>`;
  }).join("");
  var FRAME_ALIGNMENT = 8;
  var framesForClip = (durationSeconds, fps) => Math.max(
    1,
    Math.ceil(
      Math.max(0, Math.ceil(durationSeconds * Math.max(1, fps))) / FRAME_ALIGNMENT
    ) * FRAME_ALIGNMENT + 1
  );
  var clipFieldId = (clipIdx, field) => `vsclip${clipIdx}_${field}`;
  var refFieldId = (clipIdx, refIdx, field) => `vsclip${clipIdx}_ref${refIdx}_${field}`;
  var stageFieldId = (clipIdx, stageIdx, field) => `vsclip${clipIdx}_stage${stageIdx}_${field}`;
  var injectFieldData = (html, dataAttrs) => {
    const dataAttrString = Object.entries(dataAttrs).map(([key, value]) => `${key}="${escapeAttr(value)}"`).join(" ");
    return html.replace(
      /<(input|select|textarea)\s+class="([^"]*)"/g,
      (_match, tag, classes) => `<${tag} class="${classes} nogrow" ${dataAttrString}`
    );
  };
  var overrideSliderSteps = (html, config) => {
    let updated = html;
    if (config.numberStep !== void 0) {
      updated = updated.replace(
        /(<input\b[^>]*type="number"[^>]*\sstep=")[^"]*(")/g,
        (_match, prefix, suffix) => `${prefix}${String(config.numberStep)}${suffix}`
      );
    }
    if (config.rangeStep !== void 0) {
      updated = updated.replace(
        /(<input\b[^>]*type="range"[^>]*\sstep=")[^"]*(")/g,
        (_match, prefix, suffix) => `${prefix}${String(config.rangeStep)}${suffix}`
      );
    }
    return updated;
  };
  var snapDurationToFps = (seconds, fps) => {
    if (!Number.isFinite(seconds) || seconds <= 0 || !Number.isFinite(fps) || fps <= 0) {
      return seconds;
    }
    const frames = Math.max(1, Math.ceil(seconds * fps));
    const aligned = frames / fps;
    return Math.max(0.1, Math.floor(aligned * 10) / 10);
  };

  // frontend/ToggleableGroupReuseGuard.ts
  var ToggleableGroupReuseGuard = class _ToggleableGroupReuseGuard {
    constructor(options) {
      this.options = options;
      if (!_ToggleableGroupReuseGuard.guards.includes(this)) {
        _ToggleableGroupReuseGuard.guards.push(this);
      }
    }
    options;
    static guards = [];
    guardUntil = 0;
    guardTimer = null;
    tryInstallGroupToggleWrapper() {
      if (typeof doToggleGroup !== "function") {
        return false;
      }
      const wrappedExisting = doToggleGroup;
      if (wrappedExisting.__toggleableGroupReuseGuardWrapped) {
        return true;
      }
      const prior = doToggleGroup;
      const wrapped = ((id) => {
        const toggle = document.getElementById(
          `${id}_toggle`
        );
        const matchingGuards = _ToggleableGroupReuseGuard.guards.filter(
          (guard) => guard.matchesGroup(id)
        );
        const shouldSuppress = !!toggle?.checked && matchingGuards.some(
          (guard) => guard.shouldSuppressGroupActivation(id)
        );
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
      const groupToggle = this.getGroupToggle();
      if (groupToggle?.checked) {
        groupToggle.checked = false;
        if (typeof doToggleGroup === "function") {
          doToggleGroup(this.options.groupContentId);
        }
        changed = true;
      }
      const enableToggle = this.options.getEnableToggle();
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
      const tick = () => {
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
      if (groupId !== this.options.groupContentId || Date.now() >= this.guardUntil) {
        return false;
      }
      return !this.options.getEnableToggle()?.checked;
    }
    matchesGroup(groupId) {
      return groupId === this.options.groupContentId;
    }
    getGroupToggle() {
      return this.options.getGroupToggle?.() ?? document.getElementById(
        `${this.options.groupContentId}_toggle`
      );
    }
  };

  // frontend/Types.ts
  var REF_SOURCE_BASE = "Base";
  var REF_SOURCE_REFINER = "Refiner";
  var REF_SOURCE_UPLOAD = "Upload";

  // frontend/VideoStageEditor.ts
  var REF_FRAME_MIN = 1;
  var CLIP_DURATION_MIN = 1;
  var CLIP_DURATION_MAX = 9999;
  var CLIP_DURATION_SLIDER_MAX = 60;
  var CLIP_DURATION_SLIDER_STEP = 0.5;
  var ROOT_DIMENSION_MIN = 256;
  var ROOT_FPS_MIN = 4;
  var CLIP_AUDIO_UPLOAD_FIELD = "uploadedAudio";
  var CLIP_AUDIO_UPLOAD_LABEL = "Audio Upload";
  var CLIP_AUDIO_UPLOAD_DESCRIPTION = "Audio file to attach to this clip. Used when Audio Source is set to Upload.";
  var STAGE_REF_STRENGTH_MIN = 0.1;
  var STAGE_REF_STRENGTH_MAX = 1;
  var STAGE_REF_STRENGTH_STEP = 0.1;
  var STAGE_REF_STRENGTH_DEFAULT = 0.8;
  var STAGE_REF_STRENGTH_FIELD_PREFIX = "refStrength_";
  var stageRefStrengthField = (refIdx) => `${STAGE_REF_STRENGTH_FIELD_PREFIX}${refIdx}`;
  var parseStageRefStrengthIndex = (field) => {
    if (!field.startsWith(STAGE_REF_STRENGTH_FIELD_PREFIX)) {
      return null;
    }
    const refIdx = parseInt(
      field.slice(STAGE_REF_STRENGTH_FIELD_PREFIX.length),
      10
    );
    if (!Number.isInteger(refIdx) || refIdx < 0) {
      return null;
    }
    return refIdx;
  };
  var VideoStageEditor = class {
    editor = null;
    inactiveReuseGuard;
    genButtonWrapped = false;
    genWrapInterval = null;
    clipsInputSyncInterval = null;
    clipsRefreshTimer = null;
    lastKnownClipsJson = "";
    suppressInactiveReseed = false;
    observedDropdownIds = /* @__PURE__ */ new Set();
    sourceDropdownObserver = null;
    base2EditListenerInstalled = false;
    rootVideoTimingChangeListenerInstalled = false;
    refSourceFallbackListenerInstalled = false;
    refUploadCache = /* @__PURE__ */ new Map();
    constructor() {
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
        }
      });
    }
    init() {
      this.createEditor();
      this.startClipsInputSync();
      this.ensureClipsSeeded();
      this.persistLegacyRootUploadMigration();
      this.wrapGenerateWithValidation();
      this.renderClips();
      this.installSourceDropdownObserver();
      this.installBase2EditStageChangeListener();
      this.installRootVideoTimingChangeListener();
      this.installRefSourceFallbackListener();
    }
    /**
     * If the persisted JSON still carries a legacy root-level `uploadedAudio`
     * field, persist the migrated state once on load so subsequent reads see
     * the per-clip layout and the obsolete root field is dropped.
     */
    persistLegacyRootUploadMigration() {
      const input = this.getClipsInput();
      if (!input?.value) {
        return;
      }
      let parsed;
      try {
        parsed = JSON.parse(input.value);
      } catch {
        return;
      }
      if (!parsed || Array.isArray(parsed) || typeof parsed !== "object" || !("uploadedAudio" in parsed)) {
        return;
      }
      this.saveState(this.getState());
    }
    resetForInactiveReuse() {
      this.suppressInactiveReseed = true;
      this.inactiveReuseGuard.enforceInactiveState();
      this.inactiveReuseGuard.start();
    }
    tryInstallInactiveReuseGuard() {
      return this.inactiveReuseGuard.tryInstallGroupToggleWrapper();
    }
    startGenerateWrapRetry(intervalMs = 250) {
      if (this.genWrapInterval) {
        return;
      }
      const tryWrap = () => {
        try {
          this.wrapGenerateWithValidation();
          if (typeof mainGenHandler !== "undefined" && mainGenHandler && typeof mainGenHandler.doGenerate === "function" && mainGenHandler.doGenerate.__videoStagesWrapped) {
            if (this.genWrapInterval) {
              clearInterval(this.genWrapInterval);
              this.genWrapInterval = null;
            }
          }
        } catch {
        }
      };
      tryWrap();
      this.genWrapInterval = setInterval(tryWrap, intervalMs);
    }
    createEditor() {
      let editor = document.getElementById("videostages_stage_editor");
      if (!editor) {
        editor = document.createElement("div");
        editor.id = "videostages_stage_editor";
        editor.className = "videostages-stage-editor keep_group_visible";
        document.getElementById("input_group_content_videostages")?.appendChild(editor);
      }
      editor.style.width = "100%";
      editor.style.maxWidth = "100%";
      editor.style.minWidth = "0";
      editor.style.flex = "1 1 100%";
      editor.style.overflow = "visible";
      this.editor = editor;
    }
    getClipsInput() {
      return VideoStageUtils.getInputElement("input_videostages");
    }
    getRootDimensionParamInput(field) {
      return VideoStageUtils.getInputElement(
        field === "width" ? "input_vswidth" : "input_vsheight"
      );
    }
    getRootFpsParamInput() {
      return VideoStageUtils.getInputElement("input_vsfps");
    }
    getCoreDimensionInput(field) {
      const primaryId = field === "width" ? "input_width" : "input_height";
      const fallbackId = field === "width" ? "input_aspectratiowidth" : "input_aspectratioheight";
      return VideoStageUtils.getInputElement(primaryId) ?? VideoStageUtils.getInputElement(fallbackId);
    }
    getRegisteredRootDimension(field) {
      const input = this.getRootDimensionParamInput(field);
      if (!input) {
        return null;
      }
      const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
      return value >= ROOT_DIMENSION_MIN ? value : null;
    }
    getRegisteredRootFps() {
      const input = this.getRootFpsParamInput();
      if (!input) {
        return null;
      }
      const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
      return value >= ROOT_FPS_MIN ? value : null;
    }
    getCoreDimension(field) {
      const input = this.getCoreDimensionInput(field);
      if (!input) {
        return null;
      }
      const value = Math.round(VideoStageUtils.toNumber(input.value, 0));
      return value >= ROOT_DIMENSION_MIN ? value : null;
    }
    /**
     * Seeds the registered RootWidth/RootHeight sliders with the user's
     * currently-selected core Width/Height while our slider is still at the
     * sentinel default (anything below {@link ROOT_DIMENSION_MIN}). Once the
     * user moves our slider to a real value the sentinel guard prevents any
     * further automatic overrides, so manual changes stick.
     */
    seedRegisteredDimensionsFromCore() {
      for (const field of ["width", "height"]) {
        const ourInput = this.getRootDimensionParamInput(field);
        if (!ourInput) {
          continue;
        }
        const ourValue = Math.round(
          VideoStageUtils.toNumber(ourInput.value, 0)
        );
        if (ourValue >= ROOT_DIMENSION_MIN) {
          continue;
        }
        const coreValue = this.getCoreDimension(field);
        if (coreValue === null) {
          continue;
        }
        ourInput.value = `${coreValue}`;
        triggerChangeFor(ourInput);
      }
    }
    getEffectiveRootDimension(field, persistedValue, fallback) {
      return this.getRegisteredRootDimension(field) ?? this.getCoreDimension(field) ?? this.normalizeRootDimension(persistedValue, fallback);
    }
    normalizeUploadedAudio(value) {
      if (!value || typeof value !== "object") {
        return null;
      }
      const raw = value;
      const data = `${raw.data ?? ""}`.trim();
      if (!data) {
        return null;
      }
      return {
        data,
        fileName: this.normalizeUploadFileName(
          raw.fileName == null ? null : `${raw.fileName}`
        )
      };
    }
    getGroupToggle() {
      return VideoStageUtils.getInputElement(
        "input_group_content_videostages_toggle"
      );
    }
    getRootModelInput() {
      return VideoStageUtils.getInputElement("input_model");
    }
    parseBase2EditStageIndex(value) {
      const match = `${value || ""}`.trim().replace(/\s+/g, "").match(/^edit(\d+)$/i);
      if (!match) {
        return null;
      }
      return parseInt(match[1], 10);
    }
    getBase2EditStageRefs() {
      const snapshot = window.base2editStageRegistry?.getSnapshot?.();
      if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
        return [];
      }
      const refs = snapshot.refs.map((value) => {
        const stageIndex = this.parseBase2EditStageIndex(value);
        return stageIndex == null ? null : `edit${stageIndex}`;
      }).filter((value) => !!value);
      return [...new Set(refs)].sort(
        (left, right) => (this.parseBase2EditStageIndex(left) ?? 0) - (this.parseBase2EditStageIndex(right) ?? 0)
      );
    }
    isAvailableBase2EditReference(value) {
      const stageIndex = this.parseBase2EditStageIndex(value);
      if (stageIndex == null) {
        return false;
      }
      return this.getBase2EditStageRefs().includes(`edit${stageIndex}`);
    }
    installBase2EditStageChangeListener() {
      if (this.base2EditListenerInstalled) {
        return;
      }
      this.base2EditListenerInstalled = true;
      document.addEventListener("base2edit:stages-changed", () => {
        this.scheduleClipsRefresh();
      });
    }
    isRootTextToVideoModel() {
      const modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
      if (!modelName) {
        return false;
      }
      if (typeof modelsHelpers !== "undefined" && modelsHelpers && typeof modelsHelpers.getDataFor === "function") {
        const modelData = modelsHelpers.getDataFor(
          "Stable-Diffusion",
          modelName
        );
        if (modelData?.modelClass?.compatClass?.isText2Video) {
          return true;
        }
      }
      if (typeof currentModelHelper !== "undefined" && currentModelHelper && currentModelHelper.curCompatClass && typeof modelsHelpers !== "undefined" && modelsHelpers?.compatClasses) {
        const compatClass = modelsHelpers.compatClasses[currentModelHelper.curCompatClass];
        return !!compatClass?.isText2Video;
      }
      return false;
    }
    getDefaultStageModel(modelValues) {
      if (this.isRootTextToVideoModel()) {
        const modelName = `${this.getRootModelInput()?.value ?? ""}`.trim();
        if (modelName) {
          return modelName;
        }
      }
      return modelValues[0] ?? "";
    }
    getDropdownOptions(paramId, fallbackSelectId) {
      if (typeof getParamById === "function") {
        const param = getParamById(paramId);
        if (param?.values && Array.isArray(param.values) && param.values.length > 0) {
          const labels = Array.isArray(param.value_names) && param.value_names.length === param.values.length ? [...param.value_names] : [...param.values];
          return { values: [...param.values], labels };
        }
      }
      const select = VideoStageUtils.getSelectElement(fallbackSelectId);
      return {
        values: VideoStageUtils.getSelectValues(select),
        labels: VideoStageUtils.getSelectLabels(select)
      };
    }
    getRootDefaults() {
      let model = VideoStageUtils.getSelectElement("input_videomodel");
      if ((!model || model.options.length === 0) && this.isRootTextToVideoModel()) {
        model = VideoStageUtils.getSelectElement("input_model");
      }
      const vae = VideoStageUtils.getSelectElement("input_vae");
      const sampler = this.getDropdownOptions("sampler", "input_sampler");
      const scheduler = this.getDropdownOptions(
        "scheduler",
        "input_scheduler"
      );
      const upscaleMethod = VideoStageUtils.getSelectElement(
        "input_refinerupscalemethod"
      );
      const allUpscaleMethodValues = VideoStageUtils.getSelectValues(upscaleMethod);
      const allUpscaleMethodLabels = VideoStageUtils.getSelectLabels(upscaleMethod);
      const isStageMethod = (value) => value.startsWith("pixel-") || value.startsWith("model-") || value.startsWith("latent-") || value.startsWith("latentmodel-");
      const upscaleMethodValues = allUpscaleMethodValues.filter(isStageMethod);
      const upscaleMethodLabels = allUpscaleMethodLabels.filter(
        (_, index) => isStageMethod(allUpscaleMethodValues[index])
      );
      const fallbackUpscaleMethods = [
        "pixel-lanczos",
        "pixel-bicubic",
        "pixel-area",
        "pixel-bilinear",
        "pixel-nearest-exact"
      ];
      const steps = VideoStageUtils.getInputElement("input_videosteps") ?? VideoStageUtils.getInputElement("input_steps");
      const cfgScale = VideoStageUtils.getInputElement("input_videocfg") ?? VideoStageUtils.getInputElement("input_cfgscale");
      const widthInput = VideoStageUtils.getInputElement("input_width") ?? VideoStageUtils.getInputElement("input_aspectratiowidth");
      const heightInput = VideoStageUtils.getInputElement("input_height") ?? VideoStageUtils.getInputElement("input_aspectratioheight");
      const fpsInput = VideoStageUtils.getInputElement("input_videofps") ?? VideoStageUtils.getInputElement("input_videoframespersecond");
      const framesInput = VideoStageUtils.getInputElement("input_videoframes") ?? VideoStageUtils.getInputElement("input_text2videoframes");
      const fps = Math.max(
        1,
        this.getRegisteredRootFps() ?? Math.round(VideoStageUtils.toNumber(fpsInput?.value, 24))
      );
      const frames = Math.max(
        1,
        Math.round(VideoStageUtils.toNumber(framesInput?.value, 24))
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
        upscaleMethodValues: upscaleMethodValues.length > 0 ? upscaleMethodValues : fallbackUpscaleMethods,
        upscaleMethodLabels: upscaleMethodLabels.length > 0 ? upscaleMethodLabels : fallbackUpscaleMethods,
        width: this.getRegisteredRootDimension("width") ?? Math.max(
          ROOT_DIMENSION_MIN,
          Math.round(
            VideoStageUtils.toNumber(widthInput?.value, 1024)
          )
        ),
        height: this.getRegisteredRootDimension("height") ?? Math.max(
          ROOT_DIMENSION_MIN,
          Math.round(
            VideoStageUtils.toNumber(heightInput?.value, 1024)
          )
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
          Math.round(VideoStageUtils.toNumber(steps?.min, 1))
        ),
        stepsMax: Math.min(
          50,
          Math.max(
            1,
            Math.round(VideoStageUtils.toNumber(steps?.max, 200))
          )
        ),
        stepsStep: Math.max(
          1,
          Math.round(VideoStageUtils.toNumber(steps?.step, 1))
        ),
        cfgScale: 1,
        cfgScaleMin: VideoStageUtils.toNumber(cfgScale?.min, 0),
        cfgScaleMax: Math.min(
          10,
          VideoStageUtils.toNumber(cfgScale?.max, 10)
        ),
        cfgScaleStep: VideoStageUtils.toNumber(cfgScale?.step, 0.5)
      };
    }
    normalizeStageRefStrengthValue(value) {
      return Math.round(
        this.clamp(
          VideoStageUtils.toNumber(
            `${value ?? STAGE_REF_STRENGTH_DEFAULT}`,
            STAGE_REF_STRENGTH_DEFAULT
          ),
          STAGE_REF_STRENGTH_MIN,
          STAGE_REF_STRENGTH_MAX
        ) * 10
      ) / 10;
    }
    buildDefaultStageRefStrengths(refCount) {
      const strengths = [];
      for (let i = 0; i < refCount; i++) {
        strengths.push(STAGE_REF_STRENGTH_DEFAULT);
      }
      return strengths;
    }
    normalizeStageRefStrengths(rawStrengths, refCount) {
      const strengths = [];
      const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
      for (let i = 0; i < refCount; i++) {
        strengths.push(this.normalizeStageRefStrengthValue(rawValues[i]));
      }
      return strengths;
    }
    buildDefaultStage(previousStage, refCount) {
      const defaults = this.getRootDefaults();
      return {
        expanded: true,
        skipped: false,
        control: previousStage ? previousStage.control : defaults.control,
        refStrengths: this.buildDefaultStageRefStrengths(refCount),
        upscale: previousStage ? previousStage.upscale : defaults.upscale,
        upscaleMethod: previousStage ? previousStage.upscaleMethod : defaults.upscaleMethodValues.includes("pixel-lanczos") ? "pixel-lanczos" : defaults.upscaleMethodValues[0] ?? "pixel-lanczos",
        model: previousStage ? previousStage.model : this.getDefaultStageModel(defaults.modelValues),
        vae: previousStage ? previousStage.vae : defaults.vaeValues[0] ?? "",
        steps: previousStage ? previousStage.steps : defaults.steps,
        cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
        sampler: previousStage ? previousStage.sampler : defaults.samplerValues[0] ?? "euler",
        scheduler: previousStage ? previousStage.scheduler : defaults.schedulerValues[0] ?? "normal"
      };
    }
    buildDefaultRef() {
      return {
        expanded: true,
        source: REF_SOURCE_BASE,
        uploadFileName: null,
        uploadedImage: null,
        frame: REF_FRAME_MIN,
        fromEnd: false
      };
    }
    buildDefaultClip(index) {
      const defaults = this.getRootDefaults();
      return {
        name: `Clip ${index}`,
        expanded: true,
        skipped: false,
        duration: snapDurationToFps(
          Math.max(
            CLIP_DURATION_MIN,
            defaults.frames / Math.max(1, defaults.fps)
          ),
          defaults.fps
        ),
        audioSource: AUDIO_SOURCE_NATIVE,
        uploadedAudio: null,
        refs: [],
        stages: [this.buildDefaultStage(null, 0)]
      };
    }
    normalizeRootDimension(value, fallback) {
      return Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(
          VideoStageUtils.toNumber(`${value ?? fallback}`, fallback)
        )
      );
    }
    normalizeRootFps(value, fallback) {
      return Math.max(
        ROOT_FPS_MIN,
        Math.round(
          VideoStageUtils.toNumber(`${value ?? fallback}`, fallback)
        )
      );
    }
    refUploadKey(clipIdx, refIdx) {
      return `${clipIdx}:${refIdx}`;
    }
    parseRefUploadKey(key) {
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
    reindexRefUploadCacheAfterClipDelete(deletedClipIdx) {
      const nextCache = /* @__PURE__ */ new Map();
      for (const [key, cached] of this.refUploadCache.entries()) {
        const parsed = this.parseRefUploadKey(key);
        if (!parsed) {
          continue;
        }
        if (parsed.clipIdx === deletedClipIdx) {
          continue;
        }
        const clipIdx = parsed.clipIdx > deletedClipIdx ? parsed.clipIdx - 1 : parsed.clipIdx;
        nextCache.set(this.refUploadKey(clipIdx, parsed.refIdx), cached);
      }
      this.refUploadCache = nextCache;
    }
    reindexRefUploadCacheAfterRefDelete(clipIdx, deletedRefIdx) {
      const nextCache = /* @__PURE__ */ new Map();
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
        const refIdx = parsed.refIdx > deletedRefIdx ? parsed.refIdx - 1 : parsed.refIdx;
        nextCache.set(this.refUploadKey(clipIdx, refIdx), cached);
      }
      this.refUploadCache = nextCache;
    }
    restoreRefUploadPreviews() {
      if (!this.editor) {
        return;
      }
      const clips = this.getClips();
      const uploadInputs = this.editor.querySelectorAll(
        '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]'
      );
      for (const input of uploadInputs) {
        if (!(input instanceof HTMLInputElement)) {
          continue;
        }
        const clipIdx = parseInt(input.dataset.clipIdx ?? "-1", 10);
        const refIdx = parseInt(input.dataset.refIdx ?? "-1", 10);
        const persisted = clipIdx >= 0 && clipIdx < clips.length ? clips[clipIdx].refs[refIdx]?.uploadedImage : null;
        const cached = this.refUploadCache.get(
          this.refUploadKey(clipIdx, refIdx)
        );
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
          name ?? void 0
        );
      }
    }
    normalizeUploadFileName(value) {
      const raw = `${value ?? ""}`.trim();
      if (!raw) {
        return null;
      }
      const slashIndex = Math.max(
        raw.lastIndexOf("/"),
        raw.lastIndexOf("\\")
      );
      return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
    }
    cacheRefUploadSelection(clipIdx, refIdx, fileInput) {
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
          name: file.name
        });
        const clips = this.getClips();
        if (clipIdx < 0 || clipIdx >= clips.length) {
          return;
        }
        const ref = clips[clipIdx].refs[refIdx];
        if (!ref) {
          return;
        }
        ref.uploadedImage = {
          data: reader.result,
          fileName: this.normalizeUploadFileName(file.name)
        };
        this.saveClips(clips);
      });
      reader.readAsDataURL(file);
    }
    getReferenceFrameMax(clip) {
      const defaults = this.getRootDefaults();
      if (clip) {
        return Math.max(
          REF_FRAME_MIN,
          framesForClip(clip.duration, defaults.fps)
        );
      }
      return Math.max(REF_FRAME_MIN, defaults.frames);
    }
    clamp(value, min, max) {
      return Math.min(Math.max(value, min), max);
    }
    /**
     * Stage JSON may use camelCase (editor saves) or PascalCase (C# / metadata).
     */
    readRawStageProp(raw, camel, pascal) {
      if (Object.hasOwn(raw, camel)) {
        return raw[camel];
      }
      if (Object.hasOwn(raw, pascal)) {
        return raw[pascal];
      }
      return void 0;
    }
    readRawStageString(raw, camel, pascal) {
      const v = this.readRawStageProp(raw, camel, pascal);
      if (v == null) {
        return void 0;
      }
      const s = `${v}`.trim();
      return s.length > 0 ? s : void 0;
    }
    normalizeStage(rawStage, previousStage, refCount, stageIndexInClip) {
      const defaults = this.getRootDefaults();
      const fallback = this.buildDefaultStage(previousStage, refCount);
      const rawRecord = rawStage;
      const firstStageUpscale = stageIndexInClip === 0 ? {
        upscale: defaults.upscale,
        upscaleMethod: defaults.upscaleMethodValues.includes(
          "pixel-lanczos"
        ) ? "pixel-lanczos" : defaults.upscaleMethodValues[0] ?? "pixel-lanczos"
      } : {
        upscale: this.clamp(
          VideoStageUtils.toNumber(
            `${this.readRawStageProp(rawRecord, "upscale", "Upscale") ?? fallback.upscale}`,
            fallback.upscale
          ),
          defaults.upscaleMin,
          defaults.upscaleMax
        ),
        upscaleMethod: `${this.readRawStageString(rawRecord, "upscaleMethod", "UpscaleMethod") ?? fallback.upscaleMethod}` || fallback.upscaleMethod
      };
      const stage = {
        expanded: rawStage.expanded === void 0 ? true : !!rawStage.expanded,
        skipped: !!rawStage.skipped,
        control: this.clamp(
          VideoStageUtils.toNumber(
            `${rawStage.control ?? fallback.control}`,
            fallback.control
          ),
          defaults.controlMin,
          defaults.controlMax
        ),
        refStrengths: this.normalizeStageRefStrengths(
          rawStage.refStrengths,
          refCount
        ),
        upscale: firstStageUpscale.upscale,
        upscaleMethod: firstStageUpscale.upscaleMethod,
        model: `${rawStage.model ?? fallback.model}` || fallback.model,
        vae: `${rawStage.vae ?? fallback.vae ?? ""}`,
        steps: Math.max(
          1,
          Math.round(
            this.clamp(
              VideoStageUtils.toNumber(
                `${rawStage.steps ?? fallback.steps}`,
                fallback.steps
              ),
              defaults.stepsMin,
              defaults.stepsMax
            )
          )
        ),
        cfgScale: this.clamp(
          VideoStageUtils.toNumber(
            `${rawStage.cfgScale ?? fallback.cfgScale}`,
            fallback.cfgScale
          ),
          defaults.cfgScaleMin,
          defaults.cfgScaleMax
        ),
        sampler: `${rawStage.sampler ?? fallback.sampler}` || fallback.sampler,
        scheduler: `${rawStage.scheduler ?? fallback.scheduler}` || fallback.scheduler
      };
      if (!defaults.upscaleMethodValues.includes(stage.upscaleMethod) && defaults.upscaleMethodValues.length > 0) {
        stage.upscaleMethod = stageIndexInClip === 0 ? defaults.upscaleMethodValues[0] ?? "pixel-lanczos" : stage.upscaleMethod || fallback.upscaleMethod;
      }
      return stage;
    }
    normalizeRef(rawRef, frameMax) {
      const fallback = this.buildDefaultRef();
      const source = `${rawRef.source ?? fallback.source}` || fallback.source;
      const ref = {
        expanded: rawRef.expanded === void 0 ? true : !!rawRef.expanded,
        source,
        uploadFileName: rawRef.uploadFileName == null || rawRef.uploadFileName === "" ? null : `${rawRef.uploadFileName}`,
        uploadedImage: this.normalizeUploadedAudio(rawRef.uploadedImage),
        frame: Math.max(
          REF_FRAME_MIN,
          Math.round(
            this.clamp(
              VideoStageUtils.toNumber(
                `${rawRef.frame ?? fallback.frame}`,
                fallback.frame
              ),
              REF_FRAME_MIN,
              frameMax
            )
          )
        ),
        fromEnd: !!rawRef.fromEnd
      };
      return ref;
    }
    normalizeClip(rawClip, index) {
      const defaults = this.getRootDefaults();
      const audioSourceOptions = buildAudioSourceOptions();
      const fps = Math.max(1, defaults.fps);
      const rawDuration = VideoStageUtils.toNumber(
        `${rawClip.duration}`,
        defaults.frames / fps
      );
      const duration = snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, rawDuration),
        fps
      );
      const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
      const refFrameMax = this.getReferenceFrameMax({ duration });
      const refs = refsRaw.map(
        (rawRef) => this.normalizeRef(
          rawRef ?? {},
          refFrameMax
        )
      );
      const stages = [];
      const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
      for (let i = 0; i < stagesRaw.length; i++) {
        const previousStage = i > 0 ? stages[i - 1] : null;
        stages.push(
          this.normalizeStage(
            stagesRaw[i] ?? {},
            previousStage,
            refs.length,
            i
          )
        );
      }
      if (stages.length === 0) {
        stages.push(this.buildDefaultStage(null, refs.length));
      }
      return {
        name: typeof rawClip.name === "string" && rawClip.name.length > 0 ? rawClip.name : `Clip ${index}`,
        expanded: rawClip.expanded === void 0 ? true : !!rawClip.expanded,
        skipped: !!rawClip.skipped,
        duration,
        audioSource: resolveAudioSourceValue(
          `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`,
          audioSourceOptions
        ),
        uploadedAudio: this.normalizeUploadedAudio(rawClip.uploadedAudio),
        refs,
        stages
      };
    }
    getState() {
      const defaults = this.getRootDefaults();
      const input = this.getClipsInput();
      if (!input?.value) {
        return {
          width: defaults.width,
          height: defaults.height,
          fps: defaults.fps,
          clips: []
        };
      }
      try {
        const parsed = JSON.parse(input.value);
        const parsedConfig = parsed && !Array.isArray(parsed) && typeof parsed === "object" ? parsed : null;
        const clipsRaw = Array.isArray(parsed) ? parsed : Array.isArray(parsedConfig?.clips) ? parsedConfig.clips : [];
        const firstClip = clipsRaw.length > 0 && clipsRaw[0] && typeof clipsRaw[0] === "object" ? clipsRaw[0] : null;
        const clips = [];
        for (let i = 0; i < clipsRaw.length; i++) {
          clips.push(
            this.normalizeClip(
              clipsRaw[i] ?? {},
              i
            )
          );
        }
        this.migrateLegacyRootUploadedAudio(
          clips,
          this.normalizeUploadedAudio(parsedConfig?.uploadedAudio)
        );
        return {
          width: this.getEffectiveRootDimension(
            "width",
            parsedConfig?.width ?? firstClip?.width,
            defaults.width
          ),
          height: this.getEffectiveRootDimension(
            "height",
            parsedConfig?.height ?? firstClip?.height,
            defaults.height
          ),
          fps: this.getRegisteredRootFps() ?? this.normalizeRootFps(parsedConfig?.fps, defaults.fps),
          clips
        };
      } catch {
        return {
          width: defaults.width,
          height: defaults.height,
          fps: defaults.fps,
          clips: []
        };
      }
    }
    /**
     * Older saved JSON stored a single shared upload at the top level of the
     * config. New saves persist the upload directly inside each Upload-mode
     * clip, so on load we migrate any legacy root-level upload into every
     * Upload-mode clip that does not already carry its own audio. The next
     * save then drops the obsolete root-level field.
     */
    migrateLegacyRootUploadedAudio(clips, legacyRootUpload) {
      if (!legacyRootUpload) {
        return;
      }
      for (const clip of clips) {
        if (clip.uploadedAudio) {
          continue;
        }
        if (`${clip.audioSource || ""}` !== AUDIO_SOURCE_UPLOAD) {
          continue;
        }
        clip.uploadedAudio = {
          data: legacyRootUpload.data,
          fileName: legacyRootUpload.fileName
        };
      }
    }
    serializeClipsForStorage(clips) {
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
          fromEnd: ref.fromEnd
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
          scheduler: stage.scheduler
        }))
      }));
    }
    saveState(state) {
      const input = this.getClipsInput();
      if (!input) {
        return;
      }
      this.suppressInactiveReseed = false;
      const serialized = JSON.stringify({
        width: state.width,
        height: state.height,
        fps: state.fps,
        clips: this.serializeClipsForStorage(state.clips)
      });
      input.value = serialized;
      this.lastKnownClipsJson = serialized;
      triggerChangeFor(input);
    }
    getClips() {
      return this.getState().clips;
    }
    saveClips(clips) {
      const state = this.getState();
      state.clips = clips;
      this.saveState(state);
    }
    clearClipsForInactiveReuse() {
      const input = this.getClipsInput();
      let cleared = false;
      if (input && input.value !== "") {
        input.value = "";
        this.lastKnownClipsJson = "";
        cleared = true;
      }
      const widthInput = this.getRootDimensionParamInput("width");
      if (widthInput && widthInput.value !== "0") {
        widthInput.value = "0";
        triggerChangeFor(widthInput);
        cleared = true;
      }
      const heightInput = this.getRootDimensionParamInput("height");
      if (heightInput && heightInput.value !== "0") {
        heightInput.value = "0";
        triggerChangeFor(heightInput);
        cleared = true;
      }
      const fpsInput = this.getRootFpsParamInput();
      if (fpsInput && fpsInput.value !== "24") {
        fpsInput.value = "24";
        triggerChangeFor(fpsInput);
        cleared = true;
      }
      return cleared;
    }
    shouldKeepClipsBlankWhileDisabled() {
      return this.suppressInactiveReseed && !this.isVideoStagesEnabled();
    }
    ensureClipsSeeded() {
      const state = this.getState();
      if (state.clips.length > 0) {
        return;
      }
      if (this.shouldKeepClipsBlankWhileDisabled()) {
        return;
      }
      state.clips = [this.buildDefaultClip(0)];
      this.saveState(state);
    }
    isVideoStagesEnabled() {
      const toggler = this.getGroupToggle();
      return toggler ? toggler.checked : false;
    }
    validateClips(clips) {
      const errors = [];
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
    getRefSourceError(source) {
      const compact = `${source || ""}`.trim().replace(/\s+/g, "");
      if (compact === REF_SOURCE_BASE || compact === REF_SOURCE_REFINER || compact === REF_SOURCE_UPLOAD) {
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
    wrapGenerateWithValidation() {
      if (this.genButtonWrapped) {
        return;
      }
      if (typeof mainGenHandler === "undefined" || !mainGenHandler || typeof mainGenHandler.doGenerate !== "function") {
        return;
      }
      const original = mainGenHandler.doGenerate.bind(mainGenHandler);
      mainGenHandler.doGenerate = (...args) => {
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
    startClipsInputSync() {
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
    installSourceDropdownObserver() {
      if (this.sourceDropdownObserver || typeof MutationObserver === "undefined") {
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
        "input_refinerupscalemethod"
      ];
      let hasObservedSource = false;
      for (const sourceId of observableIds) {
        const source = VideoStageUtils.getSelectElement(sourceId);
        if (!source || this.observedDropdownIds.has(sourceId)) {
          continue;
        }
        this.observedDropdownIds.add(sourceId);
        observer.observe(source, { childList: true });
        source.addEventListener(
          "change",
          () => this.scheduleClipsRefresh()
        );
        hasObservedSource = true;
      }
      if (!hasObservedSource) {
        observer.disconnect();
        return;
      }
      this.sourceDropdownObserver = observer;
    }
    handleRootVideoTimingCommittedChange() {
      const input = this.getClipsInput();
      if (!input) {
        return;
      }
      const state = this.getState();
      const serialized = JSON.stringify({
        width: state.width,
        height: state.height,
        fps: state.fps,
        clips: this.serializeClipsForStorage(state.clips)
      });
      if (serialized !== input.value) {
        this.saveState(state);
      }
      this.scheduleClipsRefresh();
    }
    installRootVideoTimingChangeListener() {
      if (this.rootVideoTimingChangeListenerInstalled) {
        return;
      }
      this.rootVideoTimingChangeListenerInstalled = true;
      document.addEventListener("change", (event) => {
        const target = event.target;
        if (!(target instanceof HTMLInputElement)) {
          return;
        }
        if (target.id !== "input_videoframes" && target.id !== "input_text2videoframes" && target.id !== "input_videofps" && target.id !== "input_videoframespersecond" && target.id !== "input_vsfps") {
          return;
        }
        this.handleRootVideoTimingCommittedChange();
      });
    }
    installRefSourceFallbackListener() {
      if (this.refSourceFallbackListenerInstalled) {
        return;
      }
      this.refSourceFallbackListenerInstalled = true;
      document.addEventListener(
        "change",
        (event) => {
          const target = event.target;
          if (!(target instanceof HTMLSelectElement)) {
            return;
          }
          const isRefSourceChange = target.dataset.refField === "source";
          const isClipAudioSourceChange = target.dataset.clipField === "audioSource";
          if (!isRefSourceChange && !isClipAudioSourceChange) {
            return;
          }
          const liveEditor = document.getElementById(
            "videostages_stage_editor"
          );
          if (!(liveEditor instanceof HTMLElement)) {
            return;
          }
          if (!liveEditor.contains(target)) {
            return;
          }
          this.createEditor();
          this.handleFieldChange(target);
        },
        true
      );
    }
    scheduleClipsRefresh() {
      if (this.clipsRefreshTimer) {
        clearTimeout(this.clipsRefreshTimer);
      }
      this.clipsRefreshTimer = setTimeout(() => {
        this.clipsRefreshTimer = null;
        try {
          this.renderClips();
        } catch {
        }
      }, 0);
    }
    buildRefSourceOptions(currentValue) {
      const options = [
        { value: REF_SOURCE_BASE, label: "Base Output" },
        { value: REF_SOURCE_REFINER, label: "Refiner Output" },
        { value: REF_SOURCE_UPLOAD, label: "Upload" }
      ];
      for (const editRef of this.getBase2EditStageRefs()) {
        const editStage = this.parseBase2EditStageIndex(editRef);
        options.push({
          value: editRef,
          label: `Base2Edit Edit ${editStage} Output`
        });
      }
      if (currentValue && !options.some((o) => o.value === currentValue)) {
        const isBase2Edit = this.parseBase2EditStageIndex(currentValue) != null;
        options.unshift({
          value: currentValue,
          label: isBase2Edit ? `Missing Base2Edit ${currentValue}` : currentValue,
          disabled: isBase2Edit
        });
      }
      return options;
    }
    renderClips() {
      if (!this.editor) {
        return [];
      }
      this.seedRegisteredDimensionsFromCore();
      const state = this.getState();
      let clips = state.clips;
      if (clips.length === 0) {
        if (!this.shouldKeepClipsBlankWhileDisabled()) {
          state.clips = [this.buildDefaultClip(0)];
          clips = state.clips;
          this.saveState(state);
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
          `<div class="vs-empty-card">No video clips. Click "+ Add Video Clip" below.</div>`
        );
      } else {
        for (let i = 0; i < clips.length; i++) {
          stack.insertAdjacentHTML(
            "beforeend",
            this.renderClipCard(clips[i], i, clips.length)
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
      this.restoreClipAudioUploadPreviews(clips);
      this.restoreRefUploadPreviews();
      this.restoreFocus(focusSnapshot);
      return this.validateClips(clips);
    }
    renderClipCard(clip, clipIdx, totalClips) {
      const stagesCount = clip.stages.length;
      const refsCount = clip.refs.length;
      const skipBtnTitle = clip.skipped ? "Re-enable clip" : "Skip clip";
      const skipBtnVariant = clip.skipped ? "vs-btn-skip-active" : "";
      const collapseGlyph = clip.expanded ? "&#x2B9F;" : "&#x2B9E;";
      const groupClasses = ["input-group", "vs-clip-card"];
      groupClasses.push(
        clip.expanded ? "input-group-open" : "input-group-closed"
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
            false
          ),
          {
            numberStep: "any",
            rangeStep: CLIP_DURATION_SLIDER_STEP
          }
        ),
        { "data-clip-field": "duration", "data-clip-idx": String(clipIdx) }
      );
      const audioSourceOptions = buildAudioSourceOptions();
      const audioSource = resolveAudioSourceValue(
        clip.audioSource,
        audioSourceOptions
      );
      const audioSourceField = injectFieldData(
        this.buildNativeDropdown(
          clipFieldId(clipIdx, "audioSource"),
          "audioSource",
          "Audio Source",
          audioSourceOptions,
          audioSource
        ),
        {
          "data-clip-field": "audioSource",
          "data-clip-idx": String(clipIdx)
        }
      );
      const audioUploadField = this.renderClipAudioUploadField(
        clip,
        clipIdx,
        audioSource
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
                <div class="vs-card-list">${clip.refs.map((ref, refIdx) => this.renderRefRow(ref, clip, clipIdx, refIdx)).join("")}</div>
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
    decorateAutoInputWrapper(html, className, hidden = false) {
      return html.replace(
        /<div class="([^"]*)"([^>]*)>/,
        (_match, classes, attrs) => `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`
      );
    }
    renderClipAudioUploadField(clip, clipIdx, audioSource) {
      const id = clipFieldId(clipIdx, CLIP_AUDIO_UPLOAD_FIELD);
      return this.decorateAutoInputWrapper(
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
            true
          ),
          {
            "data-clip-field": CLIP_AUDIO_UPLOAD_FIELD,
            "data-clip-idx": String(clipIdx),
            "data-has-uploaded-audio": clip.uploadedAudio?.data ? "true" : "false"
          }
        ),
        "vs-clip-audio-upload-field",
        audioSource !== AUDIO_SOURCE_UPLOAD
      );
    }
    restoreClipAudioUploadPreviews(clips) {
      if (!this.editor) {
        return;
      }
      for (let clipIdx = 0; clipIdx < clips.length; clipIdx++) {
        const upload = clips[clipIdx].uploadedAudio;
        if (!upload?.data) {
          continue;
        }
        const input = this.editor.querySelector(
          `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"][data-clip-idx="${clipIdx}"]`
        );
        if (!input) {
          continue;
        }
        if (input.dataset.filedata === upload.data && this.normalizeUploadFileName(input.dataset.filename) === upload.fileName) {
          continue;
        }
        setMediaFileDirect(
          input,
          upload.data,
          "audio",
          upload.fileName ?? CLIP_AUDIO_UPLOAD_LABEL,
          upload.fileName ?? void 0
        );
      }
    }
    syncClipAudioUploadFieldVisibility(target, source) {
      const clipCard = target.closest(".vs-clip-card");
      if (!(clipCard instanceof HTMLElement)) {
        return;
      }
      const uploadField = clipCard.querySelector(
        ".vs-clip-audio-upload-field"
      );
      if (!uploadField) {
        return;
      }
      uploadField.style.display = source === AUDIO_SOURCE_UPLOAD ? "" : "none";
    }
    renderRefRow(ref, clip, clipIdx, refIdx) {
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
      const frameCount = this.getReferenceFrameMax(clip);
      const sourceError = this.getRefSourceError(ref.source);
      const errorHtml = sourceError ? `<div class="vs-field-error">${escapeAttr(sourceError)}</div>` : "";
      const sourceField = injectFieldData(
        this.buildNativeDropdown(
          refFieldId(clipIdx, refIdx, "source"),
          "source",
          "Image Source",
          sourceOptions,
          ref.source
        ),
        {
          "data-ref-field": "source",
          "data-ref-idx": String(refIdx),
          "data-clip-idx": String(clipIdx)
        }
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
            false
          ),
          {
            "data-ref-field": "uploadFileName",
            "data-ref-idx": String(refIdx),
            "data-clip-idx": String(clipIdx)
          }
        ),
        "vs-ref-upload-field",
        ref.source !== REF_SOURCE_UPLOAD
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
          false
        ),
        {
          "data-ref-field": "frame",
          "data-ref-idx": String(refIdx),
          "data-clip-idx": String(clipIdx)
        }
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
          false
        ),
        {
          "data-ref-field": "fromEnd",
          "data-ref-idx": String(refIdx),
          "data-clip-idx": String(clipIdx)
        }
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
    renderStageRow(clip, stage, clipIdx, stageIdx) {
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
      const stageSliderField = (field, label, value, min, max, step, disabled = false) => {
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
            false
          ),
          {
            "data-stage-field": field,
            "data-stage-idx": String(stageIdx),
            "data-clip-idx": String(clipIdx)
          }
        );
        if (disabled) {
          html = html.replace(
            /<input class="auto-slider-number nogrow"/g,
            '<input class="auto-slider-number nogrow" disabled'
          );
          html = html.replace(
            /<input class="auto-slider-range nogrow"/g,
            '<input class="auto-slider-range nogrow" disabled'
          );
        }
        return html;
      };
      const stageDropdownField = (field, label, values, labels, selected, disabled = false) => {
        let html = injectFieldData(
          this.buildNativeDropdown(
            stageFieldId(clipIdx, stageIdx, field),
            field,
            label,
            this.dropdownOptions(values, labels, selected),
            selected
          ),
          {
            "data-stage-field": field,
            "data-stage-idx": String(stageIdx),
            "data-clip-idx": String(clipIdx)
          }
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
        stage.model
      );
      const controlField = stageSliderField(
        "control",
        "Control",
        stage.control,
        defaults.controlMin,
        defaults.controlMax,
        defaults.controlStep
      );
      const stepsField = stageSliderField(
        "steps",
        "Steps",
        stage.steps,
        defaults.stepsMin,
        defaults.stepsMax,
        defaults.stepsStep
      );
      const cfgScaleField = stageSliderField(
        "cfgScale",
        "CFG Scale",
        stage.cfgScale,
        defaults.cfgScaleMin,
        defaults.cfgScaleMax,
        defaults.cfgScaleStep
      );
      const upscaleField = stageSliderField(
        "upscale",
        "Upscale",
        stage.upscale,
        defaults.upscaleMin,
        defaults.upscaleMax,
        defaults.upscaleStep,
        stageIdx === 0
      );
      const upscaleMethodField = (() => {
        const selectedMethod = `${stage.upscaleMethod ?? ""}`;
        let html = injectFieldData(
          this.buildNativeDropdownStrict(
            stageFieldId(clipIdx, stageIdx, "upscaleMethod"),
            "upscaleMethod",
            "Upscale Method",
            this.dropdownOptions(
              defaults.upscaleMethodValues,
              defaults.upscaleMethodLabels,
              selectedMethod
            ),
            selectedMethod
          ),
          {
            "data-stage-field": "upscaleMethod",
            "data-stage-idx": String(stageIdx),
            "data-clip-idx": String(clipIdx)
          }
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
        stage.sampler
      );
      const schedulerField = stageDropdownField(
        "scheduler",
        "Scheduler",
        defaults.schedulerValues,
        defaults.schedulerLabels,
        stage.scheduler
      );
      const vaeField = stageDropdownField(
        "vae",
        "VAE",
        defaults.vaeValues,
        defaults.vaeLabels,
        stage.vae
      );
      const refStrengthFields = clip.refs.map(
        (_ref, refIdx) => stageSliderField(
          stageRefStrengthField(refIdx),
          `Reference Image ${refIdx} Strength`,
          stage.refStrengths[refIdx] ?? STAGE_REF_STRENGTH_DEFAULT,
          STAGE_REF_STRENGTH_MIN,
          STAGE_REF_STRENGTH_MAX,
          STAGE_REF_STRENGTH_STEP
        )
      ).join("");
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
    /**
     * Same layout as {@link buildNativeDropdown} but uses strict option matching.
     * SwarmUI's `makeDropdownInput` uses `==`, which can mark the wrong option as
     * selected for some value shapes and makes the browser fall back to the first
     * option (typically pixel-lanczos) after re-renders.
     */
    buildNativeDropdownStrict(id, paramId, label, options, selected) {
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
          "$1 disabled"
        );
      }, baseHtml);
    }
    buildNativeDropdown(id, paramId, label, options, selected) {
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
        false
      );
      return options.reduce((acc, option) => {
        if (!option.disabled) {
          return acc;
        }
        const optionValue = escapeAttr(option.value);
        return acc.replace(
          new RegExp(`(<option [^>]*value="${optionValue}")`),
          "$1 disabled"
        );
      }, html);
    }
    dropdownOptions(values, labels, selected) {
      const finalValues = [...values];
      const finalLabels = [...labels];
      if (selected && !finalValues.includes(selected)) {
        finalValues.unshift(selected);
        finalLabels.unshift(selected);
      }
      return finalValues.map((value, idx) => ({
        value,
        label: finalLabels[idx] ?? value
      }));
    }
    attachEventListeners() {
      if (!this.editor) {
        return;
      }
      if (this.editor.dataset.vsListenersAttached === "1") {
        return;
      }
      this.editor.dataset.vsListenersAttached = "1";
      const editor = this.editor;
      editor.addEventListener("click", (event) => {
        const target = event.target;
        const refUploadRemoveButton = target?.closest(
          ".vs-ref-upload-field .auto-input-remove-button"
        );
        if (refUploadRemoveButton) {
          this.handleRefUploadRemove(refUploadRemoveButton);
          return;
        }
        const clipUploadRemoveButton = target?.closest(
          ".vs-clip-audio-upload-field .auto-input-remove-button"
        );
        if (clipUploadRemoveButton) {
          this.handleClipAudioUploadRemove(clipUploadRemoveButton);
          return;
        }
        const actionElem = target?.closest(
          "[data-clip-action], [data-stage-action], [data-ref-action]"
        );
        if (actionElem) {
          event.preventDefault();
          event.stopPropagation();
          this.handleAction(actionElem);
          return;
        }
        const clipHeader = target?.closest(
          ".vs-clip-card > .input-group-shrinkable"
        );
        if (clipHeader) {
          event.stopPropagation();
          const group = clipHeader.closest(
            ".vs-clip-card"
          );
          const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
          this.toggleClipExpanded(clipIdx);
        }
      });
      editor.addEventListener("change", (event) => {
        this.handleFieldChange(event.target);
      });
      editor.addEventListener("input", (event) => {
        const target = event.target;
        if (target instanceof HTMLInputElement && (target.type === "number" || target.type === "range")) {
          this.handleFieldChange(target, true);
        }
      });
    }
    getEditorActionTarget(elem) {
      if (!this.editor?.contains(elem)) {
        return null;
      }
      return elem;
    }
    toggleClipExpanded(clipIdx) {
      const clips = this.getClips();
      if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
      }
      clips[clipIdx].expanded = !clips[clipIdx].expanded;
      this.saveClips(clips);
      this.scheduleClipsRefresh();
    }
    handleAction(elem) {
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
        const previousStage = clip.stages.length > 0 ? clip.stages[clip.stages.length - 1] : null;
        clip.stages.push(
          this.buildDefaultStage(previousStage, clip.refs.length)
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
          this.refUploadKey(clipIdx, clip.refs.length - 1)
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
    handleRefUploadRemove(elem) {
      const uploadField = elem.closest(".vs-ref-upload-field");
      if (!(uploadField instanceof HTMLElement)) {
        return;
      }
      const fileInput = uploadField.querySelector(
        '.auto-file[data-ref-field="uploadFileName"]'
      );
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
      clips[clipIdx].refs[refIdx].uploadedImage = null;
      this.refUploadCache.delete(this.refUploadKey(clipIdx, refIdx));
      this.saveClips(clips);
    }
    handleClipAudioUploadRemove(elem) {
      const uploadField = elem.closest(".vs-clip-audio-upload-field");
      if (!(uploadField instanceof HTMLElement)) {
        return;
      }
      const fileInput = uploadField.querySelector(
        `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"]`
      );
      if (!fileInput) {
        return;
      }
      const clipIdx = parseInt(fileInput.dataset.clipIdx ?? "-1", 10);
      const clips = this.getClips();
      if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
      }
      clips[clipIdx].uploadedAudio = null;
      this.saveClips(clips);
    }
    handleFieldChange(elem, fromInputEvent = false) {
      if (!elem || !this.editor?.contains(elem)) {
        return;
      }
      const target = elem;
      const state = this.getState();
      const clips = state.clips;
      const defaults = this.getRootDefaults();
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
          const frameMax = this.getReferenceFrameMax(clip);
          for (const ref of clip.refs) {
            ref.frame = this.clamp(ref.frame, REF_FRAME_MIN, frameMax);
          }
        }
      } else if (clipField === "audioSource") {
        clip.audioSource = target.value || AUDIO_SOURCE_NATIVE;
      } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
        if (!(target instanceof HTMLInputElement) || target.type !== "file") {
          return;
        }
        if (target.dataset.filedata) {
          clip.uploadedAudio = {
            data: target.dataset.filedata,
            fileName: this.normalizeUploadFileName(
              target.dataset.filename ?? target.files?.[0]?.name ?? null
            )
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
        this.applyRefField(clip, clip.refs[refIdx], refField, target);
        if (refField === "source") {
          this.syncRefUploadFieldVisibility(target, target.value);
        }
      } else if (stageField) {
        const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
          return;
        }
        const stage = clip.stages[stageIdx];
        const stageCard = target.closest("section[data-stage-idx]");
        const methodSelect = stageCard?.querySelector(
          '[data-stage-field="upscaleMethod"]'
        );
        const preservedUpscaleMethod = stageField === "upscale" ? methodSelect?.value ?? stage.upscaleMethod : null;
        this.applyStageField(
          stage,
          stageField,
          target
        );
        if (stageField === "upscale") {
          if (preservedUpscaleMethod != null) {
            stage.upscaleMethod = preservedUpscaleMethod;
          }
          this.syncStageUpscaleMethodDisabled(target, stage.upscale);
          if (methodSelect && preservedUpscaleMethod != null) {
            methodSelect.value = preservedUpscaleMethod;
          }
        }
      } else {
        return;
      }
      this.saveState(state);
      if (clipField === "audioSource") {
        this.syncClipAudioUploadFieldVisibility(target, clip.audioSource);
      }
      const isSliderDrag = fromInputEvent && target instanceof HTMLInputElement && target.type === "range";
      const needsRerender = !isSliderDrag && clipField === "duration" && !fromInputEvent;
      if (needsRerender) {
        this.scheduleClipsRefresh();
      }
    }
    syncStageUpscaleMethodDisabled(target, upscale) {
      const stageCard = target.closest("section[data-stage-idx]");
      if (!(stageCard instanceof HTMLElement)) {
        return;
      }
      const stageIdx = parseInt(stageCard.dataset.stageIdx ?? "-1", 10);
      if (stageIdx === 0) {
        return;
      }
      const upscaleMethod = stageCard.querySelector(
        '[data-stage-field="upscaleMethod"]'
      );
      if (!upscaleMethod) {
        return;
      }
      upscaleMethod.disabled = upscale === 1;
    }
    syncRefUploadFieldVisibility(target, source) {
      const refCard = target.closest(".vs-ref-card");
      if (!(refCard instanceof HTMLElement)) {
        return;
      }
      const uploadField = refCard.querySelector(
        ".vs-ref-upload-field"
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
      const uploadInput = uploadField.querySelector(
        '.auto-file[data-ref-field="uploadFileName"]'
      );
      if (uploadInput) {
        const clipIdx = parseInt(uploadInput.dataset.clipIdx ?? "-1", 10);
        const refIdx = parseInt(uploadInput.dataset.refIdx ?? "-1", 10);
        this.refUploadCache.delete(this.refUploadKey(clipIdx, refIdx));
        clearMediaFileInput(uploadInput);
      }
    }
    applyRefField(clip, ref, field, target) {
      if (field === "source") {
        ref.source = target.value || REF_SOURCE_BASE;
        if (ref.source !== REF_SOURCE_UPLOAD) {
          ref.uploadFileName = null;
          ref.uploadedImage = null;
        }
      } else if (field === "frame") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
          ref.frame = this.clamp(
            value,
            REF_FRAME_MIN,
            this.getReferenceFrameMax(clip)
          );
        }
      } else if (field === "fromEnd") {
        ref.fromEnd = target instanceof HTMLInputElement ? !!target.checked : false;
      } else if (field === "uploadFileName") {
        if (target instanceof HTMLInputElement && target.type === "file") {
          const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
          const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
          if (target.dataset.filedata) {
            ref.uploadedImage = {
              data: target.dataset.filedata,
              fileName: this.normalizeUploadFileName(
                target.dataset.filename ?? target.files?.[0]?.name ?? null
              )
            };
            ref.uploadFileName = ref.uploadedImage.fileName;
          } else if (target.files && target.files.length > 0) {
            const fileName = target.files[0]?.name ?? null;
            ref.uploadFileName = this.normalizeUploadFileName(fileName);
            if (ref.uploadFileName) {
              this.cacheRefUploadSelection(clipIdx, refIdx, target);
            } else {
              ref.uploadedImage = null;
              this.refUploadCache.delete(
                this.refUploadKey(clipIdx, refIdx)
              );
            }
            return;
          } else {
            ref.uploadFileName = null;
            ref.uploadedImage = null;
            this.refUploadCache.delete(
              this.refUploadKey(clipIdx, refIdx)
            );
          }
          return;
        }
        ref.uploadFileName = this.normalizeUploadFileName(target.value);
        if (!ref.uploadFileName) {
          ref.uploadedImage = null;
        }
      }
    }
    applyStageField(stage, field, target) {
      const refStrengthIdx = parseStageRefStrengthIndex(field);
      if (refStrengthIdx != null) {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
          stage.refStrengths[refStrengthIdx] = this.normalizeStageRefStrengthValue(value);
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
            defaults.controlMax
          );
        }
      } else if (field === "upscale") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
          const defaults = this.getRootDefaults();
          stage.upscale = this.clamp(
            value,
            defaults.upscaleMin,
            defaults.upscaleMax
          );
        }
      } else if (field === "steps") {
        const value = parseInt(target.value, 10);
        if (Number.isFinite(value)) {
          const defaults = this.getRootDefaults();
          stage.steps = Math.round(
            this.clamp(value, defaults.stepsMin, defaults.stepsMax)
          );
        }
      } else if (field === "cfgScale") {
        const value = parseFloat(target.value);
        if (Number.isFinite(value)) {
          const defaults = this.getRootDefaults();
          stage.cfgScale = this.clamp(
            value,
            defaults.cfgScaleMin,
            defaults.cfgScaleMax
          );
        }
      }
    }
    captureFocus() {
      const el = document.activeElement;
      if (!el || el === document.body || el.tagName !== "INPUT" && el.tagName !== "SELECT") {
        return null;
      }
      const dataset = el.dataset;
      let selector = null;
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
      let start = null;
      let end = null;
      try {
        const inputEl = el;
        start = inputEl.selectionStart;
        end = inputEl.selectionEnd;
      } catch {
      }
      return { selector, start, end };
    }
    restoreFocus(snapshot) {
      if (!snapshot) {
        return;
      }
      const el = document.querySelector(snapshot.selector);
      if (!el) {
        return;
      }
      el.focus();
      if (el instanceof HTMLInputElement && snapshot.start != null && snapshot.end != null) {
        try {
          el.setSelectionRange(snapshot.start, snapshot.end);
        } catch {
        }
      }
    }
  };

  // frontend/VideoStages.ts
  var VideoStages = class {
    stageEditor;
    constructor(stageEditor) {
      this.stageEditor = stageEditor;
      if (!this.stageEditor.tryInstallInactiveReuseGuard()) {
        const interval = setInterval(() => {
          if (this.stageEditor.tryInstallInactiveReuseGuard()) {
            clearInterval(interval);
          }
        }, 200);
      }
      if (!this.tryRegisterStageEditor()) {
        const interval = setInterval(() => {
          if (this.tryRegisterStageEditor()) {
            clearInterval(interval);
          }
        }, 200);
      }
      if (!this.tryWrapReuseParameters()) {
        const interval = setInterval(() => {
          if (this.tryWrapReuseParameters()) {
            clearInterval(interval);
          }
        }, 200);
      }
      this.stageEditor.startGenerateWrapRetry();
    }
    tryRegisterStageEditor() {
      if (typeof postParamBuildSteps === "undefined" || !Array.isArray(postParamBuildSteps)) {
        return false;
      }
      postParamBuildSteps.push(() => {
        try {
          this.stageEditor.init();
        } catch (error) {
          console.log("VideoStages: failed to build stage editor", error);
        }
      });
      return true;
    }
    tryWrapReuseParameters() {
      if (typeof copy_current_image_params !== "function") {
        return false;
      }
      const wrappedExisting = copy_current_image_params;
      if (wrappedExisting.__videoStagesWrapped) {
        return true;
      }
      const prior = copy_current_image_params;
      const wrapped = (() => {
        const metadataUsesVideoStages = this.currentImageUsesVideoStages();
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
        const metadataFull = JSON.parse(
          interpretMetadata(currentMetadataVal)
        );
        const metadata = metadataFull?.sui_image_params;
        if (!metadata || typeof metadata !== "object") {
          return null;
        }
        return metadata.videostages !== void 0 || metadata.vsaudiosource !== void 0 || metadata.vsaudioupload !== void 0;
      } catch (error) {
        console.log(
          "VideoStages: failed to inspect reused image metadata",
          error
        );
        return null;
      }
    }
  };

  // frontend/main.ts
  new VideoStages(new VideoStageEditor());
  AudioSourceController();
})();
//# sourceMappingURL=video-stages.js.map
