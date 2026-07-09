"use strict";
(() => {
  // frontend/debugLog.ts
  var videoStagesDebugEnabled = () => typeof window !== "undefined" && !!window.__VIDEO_STAGES_DEBUG__;
  var videoStagesDebugLog = (area, message, ...details) => {
    if (!videoStagesDebugEnabled()) {
      return;
    }
    console.debug(`[VideoStages debug ${area}]`, message, ...details);
  };

  // frontend/audioSource.ts
  var AUDIO_SOURCE_NATIVE = "Native";
  var AUDIO_SOURCE_UPLOAD = "Upload";
  var AUDIO_SOURCE_CONTROLNET = "ControlNet";
  var ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
  var SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
  var CONTROLNET_SOURCE_SELECT_SELECTOR = '[data-clip-field="controlNetSource"]';
  var ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;
  var isAceStepFunAudioSource = (source) => ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());
  var isControlNetAudioSource = (source) => `${source ?? ""}`.trim() === AUDIO_SOURCE_CONTROLNET;
  var canUseClipLengthFromAudio = (source) => {
    const normalized = `${source ?? ""}`.trim();
    return normalized === AUDIO_SOURCE_UPLOAD || isAceStepFunAudioSource(normalized) || isControlNetAudioSource(normalized);
  };
  var getSourceSelects = () => Array.from(document.querySelectorAll(SOURCE_SELECT_SELECTOR)).filter(
    (elem) => elem instanceof HTMLSelectElement
  );
  var isSourceSelect = (target) => target instanceof HTMLSelectElement && target.matches(SOURCE_SELECT_SELECTOR);
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
  var getAceStepFunRefLabel = (ref) => {
    const audioRef = ACESTEPFUN_AUDIO_REF_PATTERN.exec(ref);
    if (audioRef) {
      return `AceStepFun Audio ${audioRef[1]}`;
    }
    return ref;
  };
  var buildAudioSourceOptions = (currentValue = "", context = {}) => {
    const options = [
      { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD }
    ];
    for (const ref of getAceStepFunRefs()) {
      options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
    }
    if (context.controlNetEnabled) {
      options.push({
        value: AUDIO_SOURCE_CONTROLNET,
        label: AUDIO_SOURCE_CONTROLNET
      });
    }
    const selected = `${currentValue || ""}`.trim();
    if (isAceStepFunAudioSource(selected) && !options.some((option) => option.value === selected)) {
      options.push({
        value: selected,
        label: getAceStepFunRefLabel(selected)
      });
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
  var detectControlNetEnabledForAudioSelect = (audioSelect) => {
    const clipIdx = audioSelect.dataset.clipIdx;
    if (!clipIdx) {
      return false;
    }
    for (const elem of document.querySelectorAll(
      CONTROLNET_SOURCE_SELECT_SELECTOR
    )) {
      if (elem instanceof HTMLSelectElement && elem.dataset.clipIdx === clipIdx) {
        return !elem.disabled;
      }
    }
    return false;
  };
  var audioSource = () => {
    const refreshOptions = (reason = "manual") => {
      const selects = getSourceSelects();
      videoStagesDebugLog("audioSource", "refreshOptions", {
        reason,
        selectCount: selects.length
      });
      if (selects.length === 0) {
        return;
      }
      for (const select of selects) {
        const options = buildAudioSourceOptions(select.value, {
          controlNetEnabled: detectControlNetEnabledForAudioSelect(select)
        });
        const desired = resolveAudioSourceValue(select.value, options);
        const newOptionsJson = JSON.stringify(
          options.map((o) => [o.value, o.label])
        );
        const currentOptionsJson = JSON.stringify(
          Array.from(select.options).map((o) => [
            o.value,
            o.textContent ?? ""
          ])
        );
        if (newOptionsJson === currentOptionsJson && select.value === desired) {
          continue;
        }
        videoStagesDebugLog("audioSource", "refreshOptions DOM rebuild", {
          reason,
          previousValue: select.value,
          desired
        });
        select.innerHTML = "";
        for (const option of options) {
          const elem = document.createElement("option");
          elem.value = option.value;
          elem.textContent = option.label;
          elem.dataset.cleanname = option.label;
          elem.selected = option.value === desired;
          select.appendChild(elem);
        }
        triggerChangeFor(select);
      }
    };
    const onDocumentDropdownInteraction = (event) => {
      if (isSourceSelect(event.target)) {
        refreshOptions("dropdown-interaction");
      }
    };
    const onAceStepFunTracksChanged = () => {
      refreshOptions("acestepfun:tracks-changed");
    };
    const runOnEachBuild = () => {
      try {
        refreshOptions("postParamBuildSteps");
      } catch (error) {
        console.warn("audioSource: param build sync failed", error);
      }
    };
    const scheduleInitialSync = () => {
      if (!Array.isArray(postParamBuildSteps)) {
        setTimeout(scheduleInitialSync, 200);
        return;
      }
      postParamBuildSteps.push(runOnEachBuild);
    };
    document.addEventListener("mousedown", onDocumentDropdownInteraction);
    document.addEventListener("focusin", onDocumentDropdownInteraction);
    document.addEventListener(ACESTEPFUN_EVENT, onAceStepFunTracksChanged);
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
        document.removeEventListener(
          ACESTEPFUN_EVENT,
          onAceStepFunTracksChanged
        );
      }
    };
  };

  // frontend/bottomTimelineTab.ts
  var TAB_ID = "VideoStages-Timeline-Tab";
  var TIMELINE_BODY_ID = "videostages-timeline-body";
  var registerTabWithLayout = (navLink) => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
      return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length > 0) {
      tab.contentElem.style.height = "100%";
      tab.contentElem.style.width = "100%";
      const parent = tab.contentElem.parentElement;
      if (parent && !genTabLayout.managedTabContainers.includes(parent)) {
        genTabLayout.managedTabContainers.push(parent);
      }
      tab.update();
      tab.navElem.addEventListener("click", () => {
        browserUtil.makeVisible(tab.contentElem);
      });
      genTabLayout.reapplyPositions();
    }
  };
  var injectTimelineTab = () => {
    const existing = document.getElementById(TIMELINE_BODY_ID);
    if (existing) {
      return existing;
    }
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content) {
      return null;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">Timeline</a>`;
    const toolsNav = nav.querySelector('a[href="#Tools-Tab"]');
    if (toolsNav?.parentElement) {
      nav.insertBefore(li, toolsNav.parentElement);
    } else {
      nav.appendChild(li);
    }
    const pane = document.createElement("div");
    pane.className = "tab-pane genpage-bottom-tab";
    pane.id = TAB_ID;
    pane.setAttribute("role", "tabpanel");
    const body = document.createElement("div");
    body.className = "vst-timeline";
    body.id = TIMELINE_BODY_ID;
    pane.appendChild(body);
    content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
      registerTabWithLayout(navLink);
    }
    return body;
  };

  // frontend/clipColor.ts
  var HUE_MIN = 0;
  var HUE_MAX = 359;
  var HUE_RANGE = 360;
  var BASE_HUE = 210;
  var UNASSIGNED_HUE = -1;
  var HUE_SATURATION = 65;
  var HUE_LIGHTNESS = 55;
  var isAssignedHue = (value) => typeof value === "number" && Number.isInteger(value) && value >= HUE_MIN && value <= HUE_MAX;
  var normalizeStoredHue = (value) => {
    if (value == null || value === "") {
      return UNASSIGNED_HUE;
    }
    const num = typeof value === "number" ? value : Number(value);
    if (!Number.isFinite(num)) {
      return UNASSIGNED_HUE;
    }
    return (Math.round(num) % HUE_RANGE + HUE_RANGE) % HUE_RANGE;
  };
  var hueDistance = (a, b) => {
    const d = Math.abs(a - b) % HUE_RANGE;
    return d > 180 ? HUE_RANGE - d : d;
  };
  var pickDistinctHue = (existing) => {
    const inUse = existing.filter(isAssignedHue);
    if (inUse.length === 0) {
      return BASE_HUE;
    }
    let best = 0;
    let bestScore = -1;
    for (let hue = HUE_MIN; hue <= HUE_MAX; hue++) {
      let minDist = 180;
      for (const used of inUse) {
        const d = hueDistance(hue, used);
        if (d < minDist) {
          minDist = d;
        }
      }
      if (minDist > bestScore) {
        bestScore = minDist;
        best = hue;
      }
    }
    return best;
  };
  var assignMissingHues = (clips) => {
    const assigned = [];
    for (const clip of clips) {
      if (isAssignedHue(clip.hue)) {
        assigned.push(clip.hue);
      }
    }
    for (const clip of clips) {
      if (isAssignedHue(clip.hue)) {
        continue;
      }
      const hue = pickDistinctHue(assigned);
      clip.hue = hue;
      assigned.push(hue);
    }
  };
  var clipHueCss = (hue) => {
    const resolved = isAssignedHue(hue) ? hue : BASE_HUE;
    return `hsl(${resolved} ${HUE_SATURATION}% ${HUE_LIGHTNESS}%)`;
  };

  // frontend/constants.ts
  var REF_FRAME_MIN = 1;
  var DEFAULT_CLIP_DURATION_SECONDS = 5;
  var CLIP_DURATION_MIN = 1;
  var PROMPT_WINDOW_MIN_DURATION = 0.25;
  var PROMPT_WINDOW_DEFAULT_DURATION = 1.5;
  var ROOT_DIMENSION_MIN = 256;
  var ROOT_DIMENSION_MAX = 4096;
  var ROOT_DIMENSION_STEP = 32;
  var ROOT_FPS_MIN = 1;
  var ROOT_FPS_MAX = 120;
  var CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3"
  ];
  var STAGE_REF_STRENGTH_MIN = 0;
  var STAGE_REF_STRENGTH_MAX = 1;
  var STAGE_REF_STRENGTH_STEP = 0.1;
  var STAGE_REF_STRENGTH_DEFAULT = 0.8;
  var IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;
  var STAGE_CONTROLNET_STRENGTH_MIN = 0;
  var STAGE_CONTROLNET_STRENGTH_MAX = 1;
  var STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
  var STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;
  var parseBase2EditStageIndex = (value) => {
    const match = `${value || ""}`.trim().replace(/\s+/g, "").match(/^edit(\d+)$/i);
    if (!match) {
      return null;
    }
    return parseInt(match[1], 10);
  };
  var normalizeUploadFileName = (value) => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
      return null;
    }
    const slashIndex = Math.max(raw.lastIndexOf("/"), raw.lastIndexOf("\\"));
    return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
  };
  var clamp = (value, min, max) => Math.min(Math.max(value, min), max);
  var mediaPreviewSrc = (value) => {
    if (`${value ?? ""}`.startsWith("data:")) {
      return value;
    }
    const prefix = typeof getImageOutPrefix === "function" ? getImageOutPrefix() : "";
    return `${prefix}/${value}`;
  };

  // frontend/renderUtils.ts
  var escapeAttr = (value) => String(value ?? "").replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  var FRAME_ALIGNMENT = 8;
  var framesForClip = (durationSeconds, fps) => Math.max(
    1,
    Math.ceil(
      Math.max(0, Math.ceil(durationSeconds * Math.max(1, fps))) / FRAME_ALIGNMENT
    ) * FRAME_ALIGNMENT + 1
  );
  var snapDurationToFps = (seconds, fps) => {
    if (!Number.isFinite(seconds) || seconds <= 0 || !Number.isFinite(fps) || fps <= 0) {
      return seconds;
    }
    const frames = Math.max(1, Math.ceil(seconds * fps));
    const aligned = frames / fps;
    return Math.max(0.1, Math.floor(aligned * 10) / 10);
  };

  // frontend/types.ts
  var REF_SOURCE_BASE = "Base";
  var REF_SOURCE_REFINER = "Refiner";
  var REF_SOURCE_UPLOAD = "Upload";

  // frontend/utils.ts
  var getElementByType = (id, ctor) => {
    const element = document.getElementById(id);
    return element instanceof ctor ? element : null;
  };
  var utils = {
    getInputElement: (id) => getElementByType(id, HTMLInputElement),
    getSelectElement: (id) => getElementByType(id, HTMLSelectElement),
    getSelectValues: (select) => select ? Array.from(select.options, (option) => option.value) : [],
    getSelectLabels: (select) => select ? Array.from(select.options, (option) => option.label) : [],
    toNumber: (value, fallback) => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    }
  };

  // frontend/normalization.ts
  var readProp = (raw, ...keys) => {
    for (const key of keys) {
      if (Object.hasOwn(raw, key)) {
        return raw[key];
      }
    }
    return void 0;
  };
  var normalizePromptWindow = (raw) => {
    const duration = utils.toNumber(
      `${readProp(raw, "duration", "Duration") ?? 0}`,
      0
    );
    if (!(duration > 0)) {
      return null;
    }
    const start = Math.max(
      0,
      utils.toNumber(`${readProp(raw, "start", "Start") ?? 0}`, 0)
    );
    return {
      prompt: `${readProp(raw, "prompt", "Prompt", "text", "Text") ?? ""}`,
      start,
      duration,
      skipped: !!readProp(raw, "skipped", "Skipped")
    };
  };
  var normalizePromptWindows = (rawClip) => {
    const rawList = readProp(rawClip, "promptWindows", "PromptWindows");
    if (!Array.isArray(rawList)) {
      return [];
    }
    return rawList.map((entry) => normalizePromptWindow(isRecord(entry) ? entry : {})).filter((window2) => window2 !== null).sort((a, b) => a.start - b.start);
  };
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.find(
    (value) => value.trim().toLowerCase().startsWith("latentmodel-")
  ) ?? upscaleMethodValues[0] ?? "";
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var normalizeExpanded = (raw) => raw.expanded === void 0 ? true : !!raw.expanded;
  var snapStrengthToStep = (value, fallback, min, max, step) => {
    const unitScale = 1 / step;
    return Math.round(
      clamp(utils.toNumber(`${value ?? fallback}`, fallback), min, max) * unitScale
    ) / unitScale;
  };
  var normalizeUploadedAudio = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
      return null;
    }
    return {
      data,
      fileName: normalizeUploadFileName(
        value.fileName == null ? null : `${value.fileName}`
      )
    };
  };
  var normalizeControlNetSource = (value) => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    for (const option of CONTROLNET_SOURCE_OPTIONS) {
      if (option.replace(/\s+/g, "").toLowerCase() === compact) {
        return option;
      }
    }
    return CONTROLNET_SOURCE_OPTIONS[0];
  };
  var normalizeOptionalModelName = (value) => {
    const raw = `${value ?? ""}`.trim();
    return raw || "";
  };
  var normalizeControlNetLora = (value) => {
    const raw = normalizeOptionalModelName(value);
    if (!raw) {
      return "";
    }
    const squeezed = raw.replace(/\s+/g, "").toLowerCase();
    if (squeezed === "(none)") {
      return "";
    }
    return raw;
  };
  var normalizeStageRefStrengthValue = (value) => snapStrengthToStep(
    value,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_STEP
  );
  var normalizeStageControlNetStrengthValue = (value) => snapStrengthToStep(
    value,
    STAGE_CONTROLNET_STRENGTH_DEFAULT,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_STEP
  );
  var buildDefaultStageRefStrengths = (refCount, defaultStrength = STAGE_REF_STRENGTH_DEFAULT) => Array.from({ length: refCount }, () => defaultStrength);
  var normalizeStageRefStrengths = (rawStrengths, refCount) => {
    const strengths = [];
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    for (let i = 0; i < refCount; i++) {
      strengths.push(normalizeStageRefStrengthValue(rawValues[i]));
    }
    return strengths;
  };
  var readRawStageProp = (raw, camel, pascal) => {
    if (Object.hasOwn(raw, camel)) {
      return raw[camel];
    }
    if (Object.hasOwn(raw, pascal)) {
      return raw[pascal];
    }
    return void 0;
  };
  var readRawStageString = (raw, camel, pascal) => {
    const v = readRawStageProp(raw, camel, pascal);
    if (v == null) {
      return void 0;
    }
    const s = `${v}`.trim();
    return s.length > 0 ? s : void 0;
  };
  var buildDefaultStage = (getRootDefaults2, getDefaultStageModel2, previousStage, refCount) => {
    const defaults = getRootDefaults2();
    return {
      expanded: true,
      skipped: false,
      control: previousStage ? previousStage.control : defaults.control,
      controlNetStrength: previousStage ? previousStage.controlNetStrength : STAGE_CONTROLNET_STRENGTH_DEFAULT,
      refStrengths: buildDefaultStageRefStrengths(refCount),
      upscale: previousStage ? previousStage.upscale : defaults.upscale,
      upscaleMethod: previousStage ? previousStage.upscaleMethod : resolveRootPreferredUpscaleMethod(defaults.upscaleMethodValues),
      model: previousStage ? previousStage.model : getDefaultStageModel2(defaults.modelValues),
      steps: previousStage ? previousStage.steps : defaults.steps,
      cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
      sampler: previousStage ? previousStage.sampler : defaults.samplerValues[0] ?? "euler",
      scheduler: previousStage ? previousStage.scheduler : defaults.schedulerValues[0] ?? "normal"
    };
  };
  var buildDefaultRef = (source = REF_SOURCE_REFINER) => ({
    expanded: true,
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false
  });
  var appendRefToClip = (clip, ref) => {
    clip.refs.push(ref);
    for (const stage of clip.stages) {
      stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
  };
  var removeRefAt = (clip, refIdx) => {
    if (refIdx < 0 || refIdx >= clip.refs.length) {
      return false;
    }
    clip.refs.splice(refIdx, 1);
    for (const stage of clip.stages) {
      if (refIdx < stage.refStrengths.length) {
        stage.refStrengths.splice(refIdx, 1);
      }
    }
    return true;
  };
  var buildDefaultClip = (getRootDefaults2, getDefaultStageModel2, includeDefaultRef = false) => {
    const defaults = getRootDefaults2();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    return {
      expanded: true,
      skipped: false,
      hue: UNASSIGNED_HUE,
      duration: snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
        defaults.fps
      ),
      audioSource: AUDIO_SOURCE_NATIVE,
      controlNetSource: CONTROLNET_SOURCE_OPTIONS[0],
      controlNetLora: "",
      saveAudioTrack: false,
      clipLengthFromAudio: false,
      clipLengthFromControlNet: false,
      reuseAudio: false,
      uploadedAudio: null,
      prompt: "",
      negativePrompt: "",
      promptWindows: [],
      refs,
      stages: [
        {
          ...buildDefaultStage(
            getRootDefaults2,
            getDefaultStageModel2,
            null,
            refs.length
          ),
          refStrengths: buildDefaultStageRefStrengths(
            refs.length,
            includeDefaultRef ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH : STAGE_REF_STRENGTH_DEFAULT
          )
        }
      ]
    };
  };
  var getReferenceFrameMax = (getRootDefaults2, clip) => {
    const defaults = getRootDefaults2();
    if (clip) {
      return Math.max(
        REF_FRAME_MIN,
        framesForClip(clip.duration, defaults.fps)
      );
    }
    return Math.max(REF_FRAME_MIN, defaults.frames);
  };
  var normalizeStage = (getRootDefaults2, getDefaultStageModel2, rawStage, previousStage, refCount, stageIndexInClip) => {
    const defaults = getRootDefaults2();
    const fallback = buildDefaultStage(
      getRootDefaults2,
      getDefaultStageModel2,
      previousStage,
      refCount
    );
    let firstStageUpscale;
    let control;
    if (stageIndexInClip === 0) {
      firstStageUpscale = {
        upscale: defaults.upscale,
        upscaleMethod: resolveRootPreferredUpscaleMethod(
          defaults.upscaleMethodValues
        )
      };
      control = clamp(
        defaults.control,
        defaults.controlMin,
        defaults.controlMax
      );
    } else {
      firstStageUpscale = {
        upscale: clamp(
          utils.toNumber(
            `${readRawStageProp(rawStage, "upscale", "Upscale") ?? fallback.upscale}`,
            fallback.upscale
          ),
          defaults.upscaleMin,
          defaults.upscaleMax
        ),
        upscaleMethod: `${readRawStageString(rawStage, "upscaleMethod", "UpscaleMethod") ?? fallback.upscaleMethod}` || fallback.upscaleMethod
      };
      control = clamp(
        utils.toNumber(
          `${readRawStageProp(rawStage, "control", "Control") ?? fallback.control}`,
          fallback.control
        ),
        defaults.controlMin,
        defaults.controlMax
      );
    }
    const stage = {
      expanded: normalizeExpanded(rawStage),
      skipped: !!rawStage.skipped,
      control,
      controlNetStrength: normalizeStageControlNetStrengthValue(
        readRawStageProp(
          rawStage,
          "controlNetStrength",
          "ControlNetStrength"
        ) ?? fallback.controlNetStrength
      ),
      refStrengths: normalizeStageRefStrengths(
        rawStage.refStrengths,
        refCount
      ),
      upscale: firstStageUpscale.upscale,
      upscaleMethod: firstStageUpscale.upscaleMethod,
      model: `${rawStage.model ?? fallback.model}` || fallback.model,
      steps: Math.max(
        1,
        Math.round(
          clamp(
            utils.toNumber(
              `${rawStage.steps ?? fallback.steps}`,
              fallback.steps
            ),
            defaults.stepsMin,
            defaults.stepsMax
          )
        )
      ),
      cfgScale: clamp(
        utils.toNumber(
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
      stage.upscaleMethod = stageIndexInClip === 0 ? defaults.upscaleMethodValues[0] ?? "" : stage.upscaleMethod || fallback.upscaleMethod;
    }
    return stage;
  };
  var normalizeRef = (rawRef, frameMax) => {
    const fallback = buildDefaultRef();
    const source = `${rawRef.source ?? fallback.source}` || fallback.source;
    const ref = {
      expanded: normalizeExpanded(rawRef),
      source,
      uploadFileName: rawRef.uploadFileName == null || rawRef.uploadFileName === "" ? null : `${rawRef.uploadFileName}`,
      uploadedImage: normalizeUploadedAudio(rawRef.uploadedImage),
      frame: Math.max(
        REF_FRAME_MIN,
        Math.round(
          clamp(
            utils.toNumber(
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
  };
  var normalizeClip = (rawClip, getRootDefaults2, getDefaultStageModel2) => {
    const defaults = getRootDefaults2();
    const rawAudioSource = `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`;
    const controlNetLora = normalizeControlNetLora(
      rawClip.controlNetLora ?? rawClip.ControlNetLora
    );
    const audioSourceOptions = buildAudioSourceOptions(rawAudioSource, {
      controlNetEnabled: controlNetLora !== ""
    });
    const fps = Math.max(1, defaults.fps);
    const rawDuration = utils.toNumber(
      `${rawClip.duration}`,
      defaults.frames / fps
    );
    const duration = snapDurationToFps(
      Math.max(CLIP_DURATION_MIN, rawDuration),
      fps
    );
    const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
    const refFrameMax = getReferenceFrameMax(getRootDefaults2, { duration });
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const refs = refsRaw.map(
      (rawRef) => normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax)
    );
    const stages = [];
    for (let i = 0; i < stagesRaw.length; i++) {
      const previousStage = i > 0 ? stages[i - 1] : null;
      stages.push(
        normalizeStage(
          getRootDefaults2,
          getDefaultStageModel2,
          isRecord(stagesRaw[i]) ? stagesRaw[i] : {},
          previousStage,
          refs.length,
          i
        )
      );
    }
    const audioSource2 = resolveAudioSourceValue(
      rawAudioSource,
      audioSourceOptions
    );
    const clipLengthFromAudio = canUseClipLengthFromAudio(audioSource2) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet = controlNetLora !== "" && !clipLengthFromAudio && !!(rawClip.clipLengthFromControlNet ?? rawClip.ClipLengthFromControlNet);
    const clip = {
      expanded: normalizeExpanded(rawClip),
      skipped: !!rawClip.skipped,
      hue: normalizeStoredHue(rawClip.hue),
      duration,
      audioSource: audioSource2,
      controlNetSource: normalizeControlNetSource(
        rawClip.controlNetSource ?? rawClip.ControlNetSource
      ),
      controlNetLora,
      saveAudioTrack: !!rawClip.saveAudioTrack,
      clipLengthFromAudio,
      clipLengthFromControlNet,
      reuseAudio: !!rawClip.reuseAudio,
      uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
      prompt: `${readProp(rawClip, "prompt", "Prompt") ?? ""}`,
      negativePrompt: `${readProp(rawClip, "negativePrompt", "NegativePrompt") ?? ""}`,
      promptWindows: normalizePromptWindows(rawClip),
      refs,
      stages
    };
    return clip;
  };

  // frontend/swarmInputs.ts
  var VIDEOSTAGES_OPENER = "<videostages>";
  var getPromptInput = () => {
    const el = document.getElementById("input_prompt");
    return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement ? el : null;
  };
  var readVideoStagesSection = () => {
    const value = getPromptInput()?.value ?? "";
    const at = value.indexOf(VIDEOSTAGES_OPENER);
    if (at < 0) {
      return "";
    }
    const rest = value.slice(at + VIDEOSTAGES_OPENER.length);
    const stop = rest.indexOf("<");
    return (stop < 0 ? rest : rest.slice(0, stop)).trim();
  };
  var writeVideoStagesSection = (json, notify = true) => {
    const el = getPromptInput();
    if (!el) {
      return;
    }
    const escaped = json.replace(/</g, "\\u003c").replace(/>/g, "\\u003e");
    const section = VIDEOSTAGES_OPENER + escaped;
    const prompt = el.value ?? "";
    const at = prompt.indexOf(VIDEOSTAGES_OPENER);
    if (at < 0) {
      const sep = prompt.length === 0 || prompt.endsWith("\n") ? "" : "\n";
      el.value = prompt + sep + section;
    } else {
      const afterOpener = at + VIDEOSTAGES_OPENER.length;
      const rest = prompt.slice(afterOpener);
      const stop = rest.indexOf("<");
      const spanEnd = stop < 0 ? prompt.length : afterOpener + stop;
      el.value = prompt.slice(0, at) + section + prompt.slice(spanEnd);
    }
    if (notify) {
      triggerChangeFor(el);
    }
  };
  var readGlobalPrompt = () => {
    const value = getPromptInput()?.value ?? "";
    const at = value.indexOf(VIDEOSTAGES_OPENER);
    if (at < 0) {
      return value.trim();
    }
    const afterOpener = at + VIDEOSTAGES_OPENER.length;
    const rest = value.slice(afterOpener);
    const stop = rest.indexOf("<");
    const spanEnd = stop < 0 ? value.length : afterOpener + stop;
    return (value.slice(0, at) + value.slice(spanEnd)).trim();
  };
  var getGroupToggle = () => utils.getInputElement("input_group_content_videostages_toggle");
  var getRootModelInput = () => utils.getInputElement("input_model");
  var getBase2EditStageRefs = () => {
    const snapshot = window.base2editStageRegistry?.getSnapshot?.();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
      return [];
    }
    const refs = snapshot.refs.map((value) => {
      const stageIndex = parseBase2EditStageIndex(value);
      return stageIndex == null ? null : `edit${stageIndex}`;
    }).filter((value) => !!value);
    return [...new Set(refs)].sort(
      (left, right) => (parseBase2EditStageIndex(left) ?? 0) - (parseBase2EditStageIndex(right) ?? 0)
    );
  };
  var isRootTextToVideoModel = () => {
    const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
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
  };
  var getDropdownOptions = (paramId, fallbackSelectId) => {
    if (typeof getParamById === "function") {
      const param = getParamById(paramId);
      if (param?.values && Array.isArray(param.values) && param.values.length > 0) {
        const labels = Array.isArray(param.value_names) && param.value_names.length === param.values.length ? [...param.value_names] : [...param.values];
        return { values: [...param.values], labels };
      }
    }
    const select = utils.getSelectElement(fallbackSelectId);
    return {
      values: utils.getSelectValues(select),
      labels: utils.getSelectLabels(select)
    };
  };
  var isVideoStagesEnabled = () => {
    const toggler = getGroupToggle();
    return toggler ? toggler.checked : false;
  };
  var setVideoStagesEnabled = (enabled) => {
    const toggler = getGroupToggle();
    if (!toggler || toggler.checked === enabled) {
      return;
    }
    toggler.checked = enabled;
    triggerChangeFor(toggler);
  };

  // frontend/rootDefaults.ts
  var trimDomValue = (el) => `${el?.value ?? ""}`.trim();
  var firstPresentInput = (...ids) => {
    for (let i = 0; i < ids.length; i++) {
      const el = utils.getInputElement(ids[i]);
      if (el) {
        return el;
      }
    }
    return null;
  };
  var getDefaultStageModel = (modelValues) => {
    if (isRootTextToVideoModel()) {
      const modelName = trimDomValue(getRootModelInput());
      if (modelName) {
        return modelName;
      }
    }
    const videoModel = trimDomValue(utils.getSelectElement("input_videomodel"));
    if (videoModel) {
      return videoModel;
    }
    return modelValues[0] ?? "";
  };
  var readInheritedDimsSignature = () => {
    const width = trimDomValue(
      firstPresentInput("input_width", "input_aspectratiowidth")
    );
    const height = trimDomValue(
      firstPresentInput("input_height", "input_aspectratioheight")
    );
    const fps = trimDomValue(
      firstPresentInput("input_videofps", "input_videoframespersecond")
    );
    return `${width}|${height}|${fps}`;
  };
  var getRootDefaults = () => {
    let model = utils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
      model = utils.getSelectElement("input_model");
    }
    const loras = getDropdownOptions("loras", "input_loras");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const upscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const upscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(
      "input_width",
      "input_aspectratiowidth"
    );
    const heightInput = firstPresentInput(
      "input_height",
      "input_aspectratioheight"
    );
    const fpsInput = firstPresentInput(
      "input_videofps",
      "input_videoframespersecond"
    );
    const framesInput = firstPresentInput(
      "input_videoframes",
      "input_text2videoframes"
    );
    const fps = Math.max(1, Math.round(utils.toNumber(fpsInput?.value, 24)));
    const frames = Math.max(
      1,
      Math.round(utils.toNumber(framesInput?.value, 24))
    );
    return {
      modelValues: utils.getSelectValues(model),
      modelLabels: utils.getSelectLabels(model),
      loraValues: loras.values,
      loraLabels: loras.labels,
      samplerValues: sampler.values,
      samplerLabels: sampler.labels,
      schedulerValues: scheduler.values,
      schedulerLabels: scheduler.labels,
      upscaleMethodValues,
      upscaleMethodLabels,
      width: Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(utils.toNumber(widthInput?.value, 1024))
      ),
      height: Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(utils.toNumber(heightInput?.value, 1024))
      ),
      fps,
      frames,
      control: 0.5,
      controlMin: 0.05,
      controlMax: 1,
      controlStep: 0.05,
      upscale: 1,
      upscaleMin: 0.25,
      upscaleMax: 4,
      upscaleStep: 0.25,
      steps: 8,
      stepsMin: Math.max(1, Math.round(utils.toNumber(steps?.min, 1))),
      stepsMax: Math.min(
        50,
        Math.max(1, Math.round(utils.toNumber(steps?.max, 200)))
      ),
      stepsStep: Math.max(1, Math.round(utils.toNumber(steps?.step, 1))),
      cfgScale: 1,
      cfgScaleMin: utils.toNumber(cfgScale?.min, 0),
      cfgScaleMax: Math.min(10, utils.toNumber(cfgScale?.max, 10)),
      cfgScaleStep: utils.toNumber(cfgScale?.step, 0.5)
    };
  };

  // frontend/persistence.ts
  var isRecord2 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var toIntOrNull = (value) => {
    if (value == null || value === "") {
      return null;
    }
    const num = utils.toNumber(`${value}`, Number.NaN);
    return Number.isFinite(num) ? Math.round(num) : null;
  };
  var resolveRootDims = (inherited, stored) => {
    const width = toIntOrNull(stored.width);
    const height = toIntOrNull(stored.height);
    const dimsExplicit = width !== null && width >= ROOT_DIMENSION_MIN && height !== null && height >= ROOT_DIMENSION_MIN;
    const fps = toIntOrNull(stored.fps);
    const fpsExplicit = fps !== null && fps >= ROOT_FPS_MIN;
    return {
      width: dimsExplicit ? width : inherited.width,
      height: dimsExplicit ? height : inherited.height,
      fps: fpsExplicit ? fps : inherited.fps,
      dimsExplicit,
      fpsExplicit
    };
  };
  var rootConfig = (dims, clips) => ({
    ...dims,
    clips
  });
  var serializeClipsForStorage = (clips) => clips.map(
    (clip) => ({
      expanded: clip.expanded,
      skipped: clip.skipped,
      hue: clip.hue,
      duration: clip.duration,
      audioSource: clip.audioSource,
      controlNetSource: clip.controlNetSource,
      controlNetLora: clip.controlNetLora,
      saveAudioTrack: clip.saveAudioTrack,
      clipLengthFromAudio: clip.clipLengthFromAudio,
      clipLengthFromControlNet: clip.clipLengthFromControlNet,
      reuseAudio: clip.reuseAudio,
      uploadedAudio: clip.uploadedAudio,
      prompt: clip.prompt,
      negativePrompt: clip.negativePrompt,
      promptWindows: clip.promptWindows.map((window2) => ({
        prompt: window2.prompt,
        start: window2.start,
        duration: window2.duration,
        skipped: window2.skipped
      })),
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
        controlNetStrength: stage.controlNetStrength,
        refStrengths: stage.refStrengths,
        upscale: stage.upscale,
        upscaleMethod: stage.upscaleMethod,
        model: stage.model,
        steps: stage.steps,
        cfgScale: stage.cfgScale,
        sampler: stage.sampler,
        scheduler: stage.scheduler
      }))
    })
  );
  var serializeStateForStorage = (state) => {
    const out = {};
    if (state.dimsExplicit) {
      out.width = Math.round(state.width);
      out.height = Math.round(state.height);
    }
    if (state.fpsExplicit) {
      out.fps = Math.round(state.fps);
    }
    out.clips = serializeClipsForStorage(state.clips);
    return JSON.stringify(out);
  };
  var lastSerializedState = "";
  var parseSerializedState = (serialized, inherited) => {
    try {
      const parsed = JSON.parse(serialized);
      let clipsRaw;
      let stored = {};
      if (Array.isArray(parsed)) {
        clipsRaw = parsed;
      } else if (isRecord2(parsed)) {
        clipsRaw = Array.isArray(parsed.clips) ? parsed.clips : [];
        stored = {
          width: parsed.width,
          height: parsed.height,
          fps: parsed.fps
        };
      } else {
        clipsRaw = [];
      }
      const clips = clipsRaw.map(
        (el) => normalizeClip(
          isRecord2(el) ? el : {},
          getRootDefaults,
          getDefaultStageModel
        )
      );
      assignMissingHues(clips);
      return rootConfig(resolveRootDims(inherited, stored), clips);
    } catch {
      return null;
    }
  };
  var getState = () => {
    const defaults = getRootDefaults();
    const inherited = {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
    const serialized = readVideoStagesSection() || lastSerializedState;
    if (!serialized) {
      return rootConfig(resolveRootDims(inherited, {}), []);
    }
    let parsedState = parseSerializedState(serialized, inherited);
    if (parsedState) {
      lastSerializedState = serialized;
      return parsedState;
    }
    if (serialized !== lastSerializedState && lastSerializedState) {
      parsedState = parseSerializedState(lastSerializedState, inherited);
      if (parsedState) {
        return parsedState;
      }
    }
    return rootConfig(resolveRootDims(inherited, {}), []);
  };
  var saveState = (state, callbacks, options) => {
    assignMissingHues(state.clips);
    const serialized = serializeStateForStorage(state);
    lastSerializedState = serialized;
    const willNotifyDom = options?.notifyDomChange !== false;
    writeVideoStagesSection(serialized, willNotifyDom);
    callbacks?.onAfterSerialize?.(serialized);
    videoStagesDebugLog("persistence", "saveState", {
      notifyDomChange: options?.notifyDomChange,
      willNotifyDom,
      jsonChars: serialized.length
    });
  };
  var getClips = () => getState().clips;
  var saveClips = (clips, callbacks, options) => {
    videoStagesDebugLog("persistence", "saveClips", {
      clipCount: clips.length
    });
    const state = getState();
    state.clips = clips;
    const notifyDomChange = options?.notifyDomChange !== void 0 ? options.notifyDomChange : isVideoStagesEnabled();
    saveState(state, callbacks, { ...options, notifyDomChange });
  };

  // frontend/timelineAudioTrack.ts
  var CLIP_SELECTOR = '.vst-audio-clip[data-vst-audio="clip"]';
  var EDITING_CLASS = "vst-audio-editing";
  var parseClipIdx = (el) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute("data-clip-idx");
    if (raw === null) {
      return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
  };
  var draftFromClip = (clip) => ({
    audioSource: `${clip.audioSource ?? AUDIO_SOURCE_NATIVE}`,
    reuseAudio: clip.reuseAudio === true,
    clipLengthFromAudio: clip.clipLengthFromAudio === true,
    clipLengthFromControlNet: clip.clipLengthFromControlNet === true,
    saveAudioTrack: clip.saveAudioTrack === true,
    uploadedAudio: clip.uploadedAudio ?? null
  });
  var createTimelineAudioTrack = () => {
    let boundBody = null;
    let activeWrap = null;
    let editingAnchor = null;
    let outsideMouseHandler = null;
    const isStale = (sourceJson) => readVideoStagesSection() !== sourceJson;
    const closeEditor = () => {
      if (outsideMouseHandler) {
        document.removeEventListener(
          "mousedown",
          outsideMouseHandler,
          true
        );
        outsideMouseHandler = null;
      }
      if (editingAnchor) {
        editingAnchor.classList.remove(EDITING_CLASS);
        editingAnchor = null;
      }
      if (activeWrap) {
        activeWrap.remove();
        activeWrap = null;
      }
    };
    const commit = (clipIdx, draft) => {
      const clips = getClips();
      const clip = clips[clipIdx];
      if (!clip) {
        return;
      }
      const source = resolveAudioSourceValue(
        draft.audioSource,
        buildAudioSourceOptions(draft.audioSource, {
          controlNetEnabled: `${clip.controlNetLora ?? ""}`.trim() !== ""
        })
      );
      const canLength = canUseClipLengthFromAudio(source);
      const isAce = isAceStepFunAudioSource(source);
      clip.audioSource = source;
      clip.reuseAudio = draft.reuseAudio;
      clip.clipLengthFromAudio = canLength && draft.clipLengthFromAudio;
      if (clip.clipLengthFromAudio) {
        clip.clipLengthFromControlNet = false;
      }
      clip.saveAudioTrack = isAce && draft.saveAudioTrack;
      clip.uploadedAudio = source === AUDIO_SOURCE_UPLOAD ? draft.uploadedAudio : null;
      saveClips(clips);
    };
    const buildField = (label, control, hint) => {
      const row = document.createElement("div");
      row.className = "vst-audio-field";
      const text = document.createElement("span");
      text.className = "vst-audio-field-label";
      text.textContent = label;
      row.append(text, control);
      if (hint) {
        const small = document.createElement("small");
        small.className = "vst-audio-field-hint";
        small.textContent = hint;
        row.appendChild(small);
      }
      return row;
    };
    const buildCheckbox = (label, checked, onChange) => {
      const row = document.createElement("label");
      row.className = "vst-audio-field vst-audio-field-check";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = checked;
      input.addEventListener("change", () => onChange(input.checked));
      const text = document.createElement("span");
      text.className = "vst-audio-field-label";
      text.textContent = label;
      row.append(input, text);
      return { row, input };
    };
    const openEditor = (anchor, clipIdx) => {
      closeEditor();
      const clip = getClips()[clipIdx];
      if (!clip) {
        return;
      }
      const sourceJson = readVideoStagesSection();
      const draft = draftFromClip(clip);
      const controlNetEnabled = `${clip.controlNetLora ?? ""}`.trim() !== "";
      const host = boundBody ?? document.body;
      const hostRect = host.getBoundingClientRect();
      const viewportW = window.innerWidth || document.documentElement.clientWidth;
      const width = clamp(Math.round(hostRect.width - 32), 260, 420);
      const left = clamp(
        Math.round(hostRect.left + (hostRect.width - width) / 2),
        8,
        Math.max(8, viewportW - width - 8)
      );
      const wrap = document.createElement("div");
      wrap.className = "vst-prompt-inspector vst-audio-inspector";
      wrap.style.left = `${left}px`;
      wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
      wrap.style.width = `${width}px`;
      const head = document.createElement("div");
      head.className = "vst-prompt-inspector-head";
      head.textContent = `Clip ${clipIdx} · audio`;
      const select = document.createElement("select");
      select.className = "vst-audio-select";
      const rebuildOptions = () => {
        const options = buildAudioSourceOptions(draft.audioSource, {
          controlNetEnabled
        });
        draft.audioSource = resolveAudioSourceValue(
          draft.audioSource,
          options
        );
        select.innerHTML = "";
        for (const option of options) {
          const elem = document.createElement("option");
          elem.value = option.value;
          elem.textContent = option.label;
          elem.dataset.cleanname = option.label;
          elem.selected = option.value === draft.audioSource;
          select.appendChild(elem);
        }
      };
      rebuildOptions();
      const sourceField = buildField("Audio Source", select);
      const reuse = buildCheckbox(
        "Reuse Audio",
        draft.reuseAudio,
        (value) => {
          draft.reuseAudio = value;
        }
      );
      const lengthCheck = buildCheckbox(
        "Clip Length from Audio",
        draft.clipLengthFromAudio,
        (value) => {
          draft.clipLengthFromAudio = value;
        }
      );
      const saveCheck = buildCheckbox(
        "Save Audio Track",
        draft.saveAudioTrack,
        (value) => {
          draft.saveAudioTrack = value;
        }
      );
      const uploadRow = document.createElement("div");
      uploadRow.className = "vst-audio-field vst-audio-upload";
      const uploadLabel = document.createElement("span");
      uploadLabel.className = "vst-audio-field-label";
      uploadLabel.textContent = "Audio Upload";
      const fileInput = document.createElement("input");
      fileInput.type = "file";
      fileInput.accept = "audio/*";
      const fileName = document.createElement("span");
      fileName.className = "vst-audio-upload-name";
      const clearBtn = document.createElement("button");
      clearBtn.type = "button";
      clearBtn.className = "vst-audio-upload-clear";
      clearBtn.textContent = "Clear";
      const renderUploadName = () => {
        const name = draft.uploadedAudio?.fileName;
        fileName.textContent = name ? name : "No file chosen";
        clearBtn.hidden = !draft.uploadedAudio;
      };
      fileInput.addEventListener("change", () => {
        const file = fileInput.files?.[0];
        if (!file) {
          return;
        }
        const reader = new FileReader();
        reader.onload = () => {
          const data = `${reader.result ?? ""}`;
          if (!data) {
            return;
          }
          draft.uploadedAudio = { data, fileName: file.name };
          renderUploadName();
        };
        reader.readAsDataURL(file);
      });
      clearBtn.addEventListener("click", () => {
        draft.uploadedAudio = null;
        fileInput.value = "";
        renderUploadName();
      });
      renderUploadName();
      uploadRow.append(uploadLabel, fileInput, fileName, clearBtn);
      const hint = document.createElement("div");
      hint.className = "vst-prompt-inspector-hint";
      hint.textContent = "Click away to apply · Esc to cancel";
      wrap.append(
        head,
        sourceField,
        reuse.row,
        lengthCheck.row,
        saveCheck.row,
        uploadRow,
        hint
      );
      const syncVisibility = () => {
        const canLength = canUseClipLengthFromAudio(draft.audioSource);
        lengthCheck.input.disabled = !canLength;
        lengthCheck.row.classList.toggle("vst-audio-disabled", !canLength);
        if (!canLength) {
          lengthCheck.input.checked = false;
          draft.clipLengthFromAudio = false;
        }
        const isAce = isAceStepFunAudioSource(draft.audioSource);
        saveCheck.input.disabled = !isAce;
        saveCheck.row.classList.toggle("vst-audio-disabled", !isAce);
        if (!isAce) {
          saveCheck.input.checked = false;
          draft.saveAudioTrack = false;
        }
        uploadRow.hidden = draft.audioSource !== AUDIO_SOURCE_UPLOAD;
      };
      select.addEventListener("change", () => {
        draft.audioSource = select.value;
        syncVisibility();
      });
      syncVisibility();
      anchor.classList.add(EDITING_CLASS);
      editingAnchor = anchor;
      let done = false;
      const finish = (save) => {
        if (done) {
          return;
        }
        done = true;
        closeEditor();
        if (save && !isStale(sourceJson)) {
          commit(clipIdx, draft);
        }
      };
      wrap.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          finish(false);
        } else if (event.key === "Enter" && !(event.target instanceof HTMLSelectElement)) {
          event.preventDefault();
          finish(true);
        }
        event.stopPropagation();
      });
      const onOutside = (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
          return;
        }
        if (target.closest(".vst-audio-inspector") || target.closest(".sui-popover")) {
          return;
        }
        finish(true);
      };
      outsideMouseHandler = onOutside;
      document.addEventListener("mousedown", onOutside, true);
      document.body.appendChild(wrap);
      activeWrap = wrap;
      select.focus();
    };
    const onBodyClick = (event) => {
      if (!(event.target instanceof Element)) {
        return;
      }
      const seg = event.target.closest(CLIP_SELECTOR);
      if (!(seg instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseClipIdx(seg);
      if (clipIdx === null) {
        return;
      }
      openEditor(seg, clipIdx);
    };
    const onBodyKeyDown = (event) => {
      const ke = event;
      if (ke.key !== "Enter" && ke.key !== " ") {
        return;
      }
      if (!(ke.target instanceof Element)) {
        return;
      }
      const seg = ke.target.closest(CLIP_SELECTOR);
      if (!(seg instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseClipIdx(seg);
      if (clipIdx === null) {
        return;
      }
      ke.preventDefault();
      openEditor(seg, clipIdx);
    };
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("click", onBodyClick);
      body.addEventListener("keydown", onBodyKeyDown);
    };
    const dispose = () => {
      closeEditor();
      if (boundBody) {
        boundBody.removeEventListener("click", onBodyClick);
        boundBody.removeEventListener("keydown", onBodyKeyDown);
        boundBody = null;
      }
    };
    return { attach, dispose };
  };

  // frontend/timelineHistory.ts
  var createTimelineHistory = (deps) => {
    const max = deps.maxDepth ?? 50;
    const undoStack = [];
    let redoStack = [];
    let last = deps.read();
    let suppress = false;
    const syncBaseline = () => {
      last = deps.read();
    };
    const capture = () => {
      if (suppress) {
        return;
      }
      const current = deps.read();
      if (current === last) {
        return;
      }
      if (last !== null) {
        undoStack.push(last);
        if (undoStack.length > max) {
          undoStack.shift();
        }
        redoStack = [];
      }
      last = current;
    };
    const restore = (from, to) => {
      if (from.length === 0) {
        return false;
      }
      const current = deps.read() ?? "";
      const target = from.pop();
      to.push(current);
      suppress = true;
      deps.write(target);
      suppress = false;
      last = target;
      return true;
    };
    return {
      syncBaseline,
      capture,
      undo: () => restore(undoStack, redoStack),
      redo: () => restore(redoStack, undoStack),
      canUndo: () => undoStack.length > 0,
      canRedo: () => redoStack.length > 0
    };
  };

  // frontend/timelineDetail.ts
  var DEFAULT_FPS = 24;
  var safeFps = (fps) => typeof fps === "number" && Number.isFinite(fps) && fps > 0 ? fps : DEFAULT_FPS;
  var keyframeTimeSeconds = (frame, fromEnd, clipDurationSeconds, fps) => {
    const duration = Math.max(0, clipDurationSeconds || 0);
    const offset = Math.max(0, frame || 0) / safeFps(fps);
    const raw = fromEnd ? duration - offset : offset;
    return Math.min(Math.max(raw, 0), duration);
  };
  var keyframeLeftPercent = (time, duration) => {
    const dur = Math.max(0, duration || 0);
    const fraction = dur > 0 ? (time || 0) / dur : 0;
    return Math.min(100, Math.max(0, fraction * 100));
  };
  var formatTimeLabel = (seconds, unit, fps) => {
    if (unit === "frames") {
      return `${Math.round((seconds || 0) * safeFps(fps))}f`;
    }
    const rounded = Math.round((seconds || 0) * 10) / 10;
    return Number.isInteger(rounded) ? `${rounded}s` : `${rounded.toFixed(1)}s`;
  };
  var RULER_MIN_TICK_SPACING_PX = 60;
  var RULER_STEP_LADDER_SECONDS = [
    0.5,
    1,
    2,
    5,
    10,
    15,
    30,
    60,
    120,
    300,
    600,
    900,
    1800,
    3600
  ];
  var chooseRulerStepSeconds = (pxPerSecond, minSpacingPx = RULER_MIN_TICK_SPACING_PX) => {
    const pps = pxPerSecond > 0 ? pxPerSecond : 1;
    for (const step of RULER_STEP_LADDER_SECONDS) {
      if (step * pps >= minSpacingPx) {
        return step;
      }
    }
    return RULER_STEP_LADDER_SECONDS[RULER_STEP_LADDER_SECONDS.length - 1];
  };
  var computeRulerTicks = (totalSeconds, pxPerSecond, minSpacingPx = RULER_MIN_TICK_SPACING_PX) => {
    const total = Math.max(0, totalSeconds || 0);
    if (total <= 0 || pxPerSecond <= 0) {
      return [{ x: 0, seconds: 0 }];
    }
    const step = chooseRulerStepSeconds(pxPerSecond, minSpacingPx);
    const ticks = [];
    const MAX_TICKS = 1e3;
    for (let i = 0; i < MAX_TICKS; i++) {
      const t = i * step;
      if (t > total + 1e-6) {
        break;
      }
      ticks.push({ x: t * pxPerSecond, seconds: t });
    }
    return ticks;
  };
  var formatRulerLabel = (seconds, unit, fps) => {
    if (unit === "frames") {
      return `${Math.round((seconds || 0) * safeFps(fps))}f`;
    }
    const s = Math.max(0, seconds || 0);
    if (s >= 60) {
      const totalWhole = Math.round(s);
      const mm = Math.floor(totalWhole / 60);
      const ss = totalWhole % 60;
      return `${mm}:${`${ss}`.padStart(2, "0")}`;
    }
    return formatTimeLabel(s, unit, fps);
  };
  var refSourceLabel = (source) => {
    const value = `${source ?? ""}`.trim();
    if (!value) {
      return REF_SOURCE_REFINER;
    }
    const editStage = parseBase2EditStageIndex(value);
    if (editStage != null) {
      return `Base2Edit Edit ${editStage}`;
    }
    return value;
  };
  var audioSourceBadge = (source) => {
    const value = `${source ?? ""}`.trim();
    if (!value || value === "Native") {
      return { label: "Native", title: "Audio source: Native" };
    }
    return { label: value, title: `Audio source: ${value}` };
  };

  // frontend/timelineEdit.ts
  var pxToDuration = (px, pxPerSecond, fps) => {
    if (!Number.isFinite(px) || !Number.isFinite(pxPerSecond) || pxPerSecond <= 0) {
      return CLIP_DURATION_MIN;
    }
    const seconds = Math.max(CLIP_DURATION_MIN, px / pxPerSecond);
    return Math.max(CLIP_DURATION_MIN, snapDurationToFps(seconds, fps));
  };
  var pxToFrame = (pointerXWithinRegion, regionWidthPx, durationSeconds, fps, fromEnd) => {
    const safeFps2 = Number.isFinite(fps) && fps > 0 ? fps : 1;
    const duration = Number.isFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
    const frameMax = Math.max(REF_FRAME_MIN, framesForClip(duration, safeFps2));
    if (!Number.isFinite(pointerXWithinRegion) || !Number.isFinite(regionWidthPx) || regionWidthPx <= 0) {
      return REF_FRAME_MIN;
    }
    const fraction = clamp(pointerXWithinRegion / regionWidthPx, 0, 1);
    const time = fraction * duration;
    const rawFrame = fromEnd ? (duration - time) * safeFps2 : time * safeFps2;
    return clamp(Math.round(rawFrame), REF_FRAME_MIN, frameMax);
  };
  var clampClipRefsToDuration = (clip, getRootDefaults2) => {
    const frameMax = getReferenceFrameMax(getRootDefaults2, clip);
    for (const ref of clip.refs) {
      ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
    }
  };
  var applyClipDurationResize = (clip, newDuration, getRootDefaults2) => {
    if (clip.duration === newDuration) {
      return false;
    }
    clip.duration = newDuration;
    clampClipRefsToDuration(clip, getRootDefaults2);
    return true;
  };

  // frontend/timelineReorder.ts
  var computeDropIndex = (pointerX, regions) => {
    for (let i = 0; i < regions.length; i++) {
      const region = regions[i];
      const midpoint = region.startPx + region.widthPx / 2;
      if (pointerX < midpoint) {
        return i;
      }
    }
    return regions.length;
  };
  var finalIndexAfterMove = (from, to) => to > from ? to - 1 : to;
  var moveItem = (array, from, to) => {
    const result = array.slice();
    if (!Number.isInteger(from) || from < 0 || from >= result.length) {
      return result;
    }
    const [item] = result.splice(from, 1);
    const insertAt = to > from ? to - 1 : to;
    const clamped = Math.max(0, Math.min(insertAt, result.length));
    result.splice(clamped, 0, item);
    return result;
  };
  var isNoOpMove = (from, to) => to === from || to === from + 1;

  // frontend/dimensionPresets.ts
  var DIMENSION_PRESET_KEYS = [
    "256x384",
    "384x512",
    "384x640",
    "512x768",
    "512x896",
    "512x1024",
    "768x1024",
    "384x256",
    "512x384",
    "640x384",
    "768x512",
    "896x512",
    "1024x512",
    "1024x768"
  ];
  var DIMENSION_PRESET_METADATA = {
    "256x384": [
      "384x576,1.5",
      "576x864,1.5,1.5",
      "*768x1152,1.5,2",
      "1152x1728,1.5,1.5,2"
    ],
    "384x512": [
      "576x768,1.5",
      "864x1152,1.5,1.5",
      "*1152x1536,1.5,2",
      "1728x2304,1.5,1.5,2"
    ],
    "384x640": [
      "576x960,1.5",
      "864x1440,1.5,1.5",
      "1152x1920,1.5,2",
      "1728x2880,1.5,1.5,2"
    ],
    "512x768": [
      "768x1152,1.5",
      "*1152x1728,1.5,1.5",
      "*1536x2304,1.5,2",
      "2304x3456,1.5,1.5,2"
    ],
    "512x896": ["*1536x2688,1.5,2"],
    "512x1024": ["*1152x2304,1.5,1.5", "*1536x3072,1.5,2"],
    "768x1024": ["*1728x2304,1.5,1.5", "*2304x3072,1.5,2"],
    "384x256": [
      "576x384,1.5",
      "864x576,1.5,1.5",
      "*1152x768,1.5,2",
      "1728x1152,1.5,1.5,2"
    ],
    "512x384": [
      "768x576,1.5",
      "1152x864,1.5,1.5",
      "*1536x1152,1.5,2",
      "2304x1728,1.5,1.5,2"
    ],
    "640x384": [
      "960x576,1.5",
      "1440x864,1.5,1.5",
      "1920x1152,1.5,2",
      "2880x1728,1.5,1.5,2"
    ],
    "768x512": [
      "1152x768,1.5",
      "*1728x1152,1.5,1.5",
      "*2304x1536,1.5,2",
      "3456x2304,1.5,1.5,2"
    ],
    "896x512": ["*2688x1536,1.5,2"],
    "1024x512": ["*2304x1152,1.5,1.5", "*3072x1536,1.5,2"],
    "1024x768": ["*2304x1728,1.5,1.5", "*3072x2304,1.5,2"]
  };
  var splitDimensionLabel = (label) => {
    const [w, h] = label.replace("*", "").split("x");
    return { width: Math.round(Number(w)), height: Math.round(Number(h)) };
  };
  var presetDimensions = (presetKey) => {
    if (!presetKey || !DIMENSION_PRESET_METADATA[presetKey]) {
      return null;
    }
    return splitDimensionLabel(presetKey);
  };
  var matchPresetKey = (width, height) => {
    const w = Math.round(width);
    const h = Math.round(height);
    for (const key of DIMENSION_PRESET_KEYS) {
      const dims = splitDimensionLabel(key);
      if (dims.width === w && dims.height === h) {
        return key;
      }
    }
    return null;
  };
  var parsePresetStops = (presetKey) => {
    const presetLines = DIMENSION_PRESET_METADATA[presetKey];
    if (!presetLines || presetLines.length === 0) {
      return [];
    }
    const out = [];
    for (let i = 0; i < presetLines.length; i++) {
      let line = presetLines[i].trim();
      let controlNetFriendly = false;
      if (line.startsWith("*")) {
        controlNetFriendly = true;
        line = line.slice(1);
      }
      const parts = line.split(",");
      const { width, height } = splitDimensionLabel(parts[0]);
      out.push({
        width,
        height,
        controlNetFriendly,
        steps: parts.slice(1)
      });
    }
    return out;
  };
  var upscaleBadgeElement = (stop) => {
    const badge = document.createElement("span");
    badge.className = "param_view_block tag-text tag-type-8";
    const resolution = `${stop.width}x${stop.height}`;
    const stepCount = stop.steps.length;
    const timesWord = stepCount === 1 ? "time" : "times";
    let altText = `The chosen resolution can be scaled to ${stepCount} ${timesWord} for a resolution of ${resolution}`;
    if (stop.controlNetFriendly) {
      altText += ". It is also ControlNet-friendly";
    }
    badge.title = altText;
    badge.setAttribute("aria-label", altText);
    const star = stop.controlNetFriendly ? `<span class="controlnet-friendly">*</span> ` : "";
    const stops = stop.steps.map((s) => `${s}x`).join(" ⇒ ");
    badge.innerHTML = `${star}${resolution}, ${stops}`;
    return badge;
  };
  var presetBadgeElements = (presetKey) => parsePresetStops(presetKey).map((stop) => upscaleBadgeElement(stop));

  // frontend/timelineView.ts
  var DEFAULT_PX_PER_SECOND = 44;
  var DEFAULT_MIN_WIDTH_PX = 8;
  var MIN_PX_PER_SECOND = 6;
  var MAX_PX_PER_SECOND = 400;
  var ZOOM_FACTOR = 1.25;
  var TRACK_HEADER_W_PX = 168;
  var waveBarHeights = (clipIdx, count) => {
    const n = Number.isFinite(count) ? Math.max(0, Math.floor(count)) : 0;
    const heights = [];
    for (let i = 0; i < n; i++) {
      const raw = Math.sin((clipIdx * 131 + i) * 12.9898) * 43758.5453;
      const fract = raw - Math.floor(raw);
      heights.push(Math.round((20 + fract * 80) * 10) / 10);
    }
    return heights;
  };
  var clampPxPerSecond = (value) => Number.isFinite(value) ? Math.min(MAX_PX_PER_SECOND, Math.max(MIN_PX_PER_SECOND, value)) : DEFAULT_PX_PER_SECOND;
  var zoomAnchorTime = (offsetX, scrollLeft, pxPerSecond, headerW = TRACK_HEADER_W_PX) => {
    if (pxPerSecond <= 0) {
      return 0;
    }
    const effOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, (effOffsetX + scrollLeft - headerW) / pxPerSecond);
  };
  var zoomAnchorScrollLeft = (time, pxPerSecond, offsetX, headerW = TRACK_HEADER_W_PX) => {
    const effOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, headerW + time * pxPerSecond - effOffsetX);
  };
  var computeFitPxPerSecond = (totalSeconds, containerWidthPx, padPx = 24) => {
    if (totalSeconds <= 0 || containerWidthPx <= padPx) {
      return DEFAULT_PX_PER_SECOND;
    }
    return clampPxPerSecond((containerWidthPx - padPx) / totalSeconds);
  };
  var computeRegionLayout = (clips, options) => {
    const pxPerSecond = options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND;
    const minWidthPx = options?.minWidthPx ?? DEFAULT_MIN_WIDTH_PX;
    const layouts = [];
    let cursorSeconds = 0;
    let cursorPx = 0;
    for (let index = 0; index < clips.length; index++) {
      const clip = clips[index];
      const durationSeconds = Math.max(0, clip.duration || 0);
      const widthPx = Math.max(minWidthPx, durationSeconds * pxPerSecond);
      layouts.push({
        index,
        startSeconds: cursorSeconds,
        durationSeconds,
        startPx: cursorPx,
        widthPx,
        stageCount: (clip.stages ?? []).length,
        keyframeCount: (clip.refs ?? []).length,
        skipped: clip.skipped === true
      });
      cursorSeconds += durationSeconds;
      cursorPx += durationSeconds * pxPerSecond;
    }
    return layouts;
  };
  var badgeHtml = (badge, extraClass = "") => `<span class="vst-badge${extraClass}" title="${escapeAttr(badge.title)}">${escapeAttr(badge.label)}</span>`;
  var renderRegionThumb = (clip) => {
    for (const ref of clip.refs ?? []) {
      const value = ref.uploadedImage?.data;
      if (value) {
        const src = mediaPreviewSrc(value);
        return `<div class="vst-region-thumb" style="background-image:url('${escapeAttr(src)}')" aria-hidden="true"></div>`;
      }
    }
    return "";
  };
  var renderKeyframes = (clip, clipIdx, durationSeconds, fps, unit) => {
    const refs = clip.refs ?? [];
    if (refs.length === 0) {
      return "";
    }
    const pips = refs.map((ref, refIdx) => {
      const time = keyframeTimeSeconds(
        ref.frame,
        ref.fromEnd === true,
        durationSeconds,
        fps
      );
      const left = keyframeLeftPercent(time, durationSeconds);
      const isEnd = ref.fromEnd === true;
      const isPrimary = (ref.frame ?? 0) === 1 && !isEnd;
      const source = refSourceLabel(ref.source ?? "");
      const title = `${source} · frame ${ref.frame ?? 0}${isEnd ? " (from end)" : ""}${isPrimary ? " (cover)" : ""} · ${formatTimeLabel(time, unit, fps)} · drag to move, shift-click to toggle from-end`;
      const kindClass = (isEnd ? " vst-key-end" : " vst-key-start") + (isPrimary ? " vst-key-primary" : "");
      const markAria = `Reference ${refIdx} marker (${source}${isEnd ? ", from end" : ""}) — drag to move, Enter toggles from end`;
      return `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeAttr(title)}" role="button" tabindex="0" aria-label="${escapeAttr(markAria)}"><span class="vst-key-dot" aria-hidden="true"></span></span>`;
    }).join("");
    return `<div class="vst-keys" title="Reference markers">${pips}</div>`;
  };
  var renderBadges = (clip) => {
    const badges = [
      badgeHtml(audioSourceBadge(clip.audioSource ?? ""), " vst-badge-audio")
    ];
    return `<div class="vst-badges">${badges.join("")}</div>`;
  };
  var lengthDerived = (clip) => clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true;
  var promptWindowGeom = (layout, window2, pxPerSecond) => {
    const clipDur = Math.max(0, layout.durationSeconds);
    const startSec = clamp(window2.start, 0, clipDur);
    const endSec = clamp(window2.start + window2.duration, startSec, clipDur);
    return {
      startSec,
      endSec,
      leftPx: startSec * pxPerSecond,
      widthPx: Math.max(2, (endSec - startSec) * pxPerSecond),
      active: !window2.skipped && `${window2.prompt ?? ""}`.trim() !== ""
    };
  };
  var PROMPT_PLACEHOLDER = "(no prompt)";
  var truncatePrompt = (text, max = 120) => text.length > max ? `${text.slice(0, max - 1)}…` : text;
  var renderPromptTrackRow = (clips, layouts, pxPerSecond, globalPrompt) => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const parts = [];
    for (let i = 0; i < layouts.length; i++) {
      const layout = layouts[i];
      const clip = clips[i];
      if (!clip) {
        continue;
      }
      const clipWidth = Math.max(1, layout.widthPx - 2);
      const windows = clip.promptWindows ?? [];
      const ownPrompt = `${clip.prompt ?? ""}`.trim();
      const inherited = ownPrompt === "";
      const major = inherited ? globalTrimmed : ownPrompt;
      const overlays = windows.map((w) => promptWindowGeom(layout, w, pxPerSecond)).filter((g) => g.active && g.endSec > g.startSec).map(
        (g) => `<div class="vst-major-off" style="left:${g.leftPx}px;width:${g.widthPx}px" aria-hidden="true"></div>`
      ).join("");
      const majorText = major === "" ? PROMPT_PLACEHOLDER : truncatePrompt(major);
      const majorClass = (major === "" ? " vst-major-empty" : "") + (inherited && major !== "" ? " vst-major-inherited" : "");
      const majorTitle = (major === "" ? PROMPT_PLACEHOLDER : major) + (inherited && major !== "" ? " — inherited from the global prompt; click to set a clip prompt" : " — click to edit");
      parts.push(
        `<div class="vst-major-seg${majorClass}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="${escapeAttr(majorTitle)}">` + overlays + `<span class="vst-major-text">${escapeAttr(majorText)}</span></div>`
      );
      const minorSegs = windows.map((w, j) => {
        const g = promptWindowGeom(layout, w, pxPerSecond);
        const skippedClass = w.skipped ? " vst-minor-skipped" : "";
        const text = `${w.prompt ?? ""}`.trim();
        const label = text === "" ? "(empty)" : truncatePrompt(text, 60);
        return `<div class="vst-minor-seg${skippedClass}" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${g.leftPx}px;width:${g.widthPx}px" title="${escapeAttr(`${text || "(empty minor prompt)"} · Shift+click to delete`)}"><span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span><span class="vst-minor-text">${escapeAttr(label)}</span><span class="vst-minor-actions"><button type="button" class="vst-minor-act" data-vst-minor-action="skip" title="${w.skipped ? "Enable this minor prompt" : "Skip this minor prompt"}" aria-label="${w.skipped ? "Enable minor prompt" : "Skip minor prompt"}">${w.skipped ? "○" : "◉"}</button></span><span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span></div>`;
      }).join("");
      parts.push(
        `<div class="vst-minor-lane" data-vst-prompt-add data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="Click empty space to add a minor prompt">${minorSegs}</div>`
      );
    }
    return `<div class="vst-track-row vst-track-prompt"><div class="vst-track-head"><div class="vst-track-icon vst-track-icon-prompt" aria-hidden="true">✎</div><div class="vst-track-label"><strong>Prompt</strong><small>major · relay</small></div></div><div class="vst-track-cell vst-prompt-cell">${parts.join("")}</div></div>`;
  };
  var audioFlagChips = (clip) => {
    const chips = [];
    if (clip.reuseAudio === true) {
      chips.push(
        `<span class="vst-audio-flag" title="Reuse the first stage's audio latent for later stages">↻</span>`
      );
    }
    if (clip.clipLengthFromAudio === true) {
      chips.push(
        `<span class="vst-audio-flag" title="Clip length follows the audio length">⇥</span>`
      );
    }
    if (clip.saveAudioTrack === true) {
      chips.push(
        `<span class="vst-audio-flag" title="Save a standalone MP3 for this clip's audio">MP3</span>`
      );
    }
    return chips.length === 0 ? "" : `<span class="vst-audio-flags" aria-hidden="true">${chips.join("")}</span>`;
  };
  var renderAudioTrackRow = (clips, layouts) => {
    const segments = layouts.map((l) => {
      const clip = clips[l.index];
      if (!clip) {
        return "";
      }
      const badge = audioSourceBadge(clip.audioSource ?? "");
      const native = badge.label === "Native";
      const nativeClass = native ? " vst-audio-native" : "";
      const width = Math.max(1, l.widthPx - 2);
      const upload = !native && clip.audioSource === "Upload" ? clip.uploadedAudio?.fileName : null;
      const labelText = upload ? `${badge.label} · ${upload}` : badge.label;
      const title = native ? "Audio: Native — click to choose an audio source" : `${badge.title} — click to edit`;
      const body = native ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>` : (() => {
        const barCount = Math.min(
          400,
          Math.max(8, Math.floor(width / 5.5))
        );
        const bars = waveBarHeights(l.index, barCount).map((h) => `<span style="height:${h}%"></span>`).join("");
        return `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>`;
      })();
      return `<div class="vst-audio-clip${nativeClass}" data-vst-audio="clip" data-clip-idx="${l.index}" role="button" tabindex="0" style="left:${l.startPx}px;width:${width}px" title="${escapeAttr(title)}" aria-label="Edit audio for clip ${l.index}"><span class="vst-audio-label">${escapeAttr(labelText)}</span>` + audioFlagChips(clip) + body + `</div>`;
    }).join("");
    return `<div class="vst-track-row vst-track-audio"><div class="vst-track-head"><div class="vst-track-icon vst-track-icon-audio" aria-hidden="true">♪</div><div class="vst-track-label"><strong>Audio</strong><small>A1 · per-clip</small></div></div><div class="vst-track-cell">${segments}</div></div>`;
  };
  var REF_EDGE_ALIGN_FRAMES = 3;
  var renderReferencesTrackRow = (clips, layouts, fps, unit) => {
    const lanes = layouts.map((l) => {
      const clip = clips[l.index];
      if (!clip) {
        return "";
      }
      const laneWidth = Math.max(1, l.widthPx - 2);
      const marks = (clip.refs ?? []).map((ref, refIdx) => {
        const isEnd = ref.fromEnd === true;
        const frame = Math.max(0, ref.frame ?? 0);
        const isPrimary = frame === 1 && !isEnd;
        const time = keyframeTimeSeconds(
          ref.frame,
          isEnd,
          l.durationSeconds,
          fps
        );
        const left = keyframeLeftPercent(time, l.durationSeconds);
        const source = refSourceLabel(ref.source ?? "");
        const image = ref.uploadedImage?.data;
        const thumbStyle = image ? ` style="background-image:url('${escapeAttr(mediaPreviewSrc(image))}')"` : "";
        const frameLabel = `R ${isEnd ? "-" : ""}${frame}`;
        const thumbClass = `vst-refs-thumb${image ? " vst-refs-has-image" : ""}`;
        const thumbInner = `<span class="vst-refs-ph">${escapeAttr(frameLabel)}</span>`;
        const alignClass = frame > REF_EDGE_ALIGN_FRAMES ? "" : isEnd ? " vst-refs-align-end" : " vst-refs-align-start";
        const kindClass = (isPrimary ? " vst-refs-primary" : "") + (isEnd ? " vst-refs-fromend" : "") + alignClass;
        const title = `${source}${isPrimary ? " · cover frame" : ""}${isEnd ? " · from end" : ""} · frame ${frame} · ${formatTimeLabel(time, unit, fps)} · click to edit, drag to move · Shift+click to delete`;
        const label = `Edit reference ${refIdx} (${source}${isEnd ? ", from end" : ""})`;
        return `<div class="vst-refs-mark${kindClass}" data-vst-ref="thumb" data-clip-idx="${l.index}" data-ref-idx="${refIdx}" style="left:${left}%" role="button" tabindex="0" title="${escapeAttr(title)}" aria-label="${escapeAttr(label)}"><span class="${thumbClass}"${thumbStyle}>${thumbInner}</span></div>`;
      }).join("");
      return `<div class="vst-refs-lane" data-vst-ref-add data-clip-idx="${l.index}" style="left:${l.startPx}px;width:${laneWidth}px" title="Click to add a reference image at this frame">${marks}</div>`;
    }).join("");
    return `<div class="vst-track-row vst-track-refs"><div class="vst-track-head"><div class="vst-track-icon vst-track-icon-refs" aria-hidden="true">⧉</div><div class="vst-track-label"><strong>References</strong><small>image refs</small></div></div><div class="vst-track-cell">${lanes}</div></div>`;
  };
  var renderTimeline = (body, clips, options) => {
    const fps = safeFps(options?.fps);
    const unit = options?.unit === "frames" ? "frames" : "seconds";
    const pxPerSecond = clampPxPerSecond(
      options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND
    );
    body.dataset.vstPps = String(pxPerSecond);
    const layouts = computeRegionLayout(clips, { pxPerSecond });
    const totalSeconds = layouts.reduce((sum, l) => sum + l.durationSeconds, 0);
    const totalPx = layouts.reduce(
      (max, l) => Math.max(max, l.startPx + l.widthPx),
      0
    );
    const toggleLabel = unit === "frames" ? "Show seconds" : "Show frames";
    const clipWord = `clip${clips.length === 1 ? "" : "s"}`;
    const totalLabel = escapeAttr(formatTimeLabel(totalSeconds, unit, fps));
    const zoomPct = Math.round(pxPerSecond / DEFAULT_PX_PER_SECOND * 100);
    const rawSelected = options?.selectedIndex;
    const selectedIndex = typeof rawSelected === "number" && Number.isInteger(rawSelected) && rawSelected >= 0 && rawSelected < clips.length ? rawSelected : null;
    const selHidden = selectedIndex === null ? " hidden" : "";
    const readout = `<span class="vst-readout" data-vst-readout><span title="Sequence total">${totalLabel} total</span><span class="vst-dot" data-vst-readout-sel-dot${selHidden}>·</span><span class="vst-readout-sel" data-vst-readout-sel title="Selected clip"${selHidden}>${selectedIndex !== null ? `clip ${selectedIndex}` : ""}</span></span>`;
    const chipWidth = Math.max(0, Math.round(options?.width ?? 0));
    const chipHeight = Math.max(0, Math.round(options?.height ?? 0));
    const chipFps = fps;
    const chipDimsExplicit = options?.dimsExplicit === true;
    const chipFpsExplicit = options?.fpsExplicit === true;
    const chipPresetKey = chipDimsExplicit && chipWidth > 0 && chipHeight > 0 ? matchPresetKey(chipWidth, chipHeight) : null;
    const dimsSource = chipDimsExplicit ? chipPresetKey ? `${chipPresetKey} preset` : "custom" : "inherited from image resolution";
    const fpsSource = chipFpsExplicit ? "custom" : "inherited from Video FPS";
    const settingsTip = `Resolution: ${dimsSource}; FPS: ${fpsSource}. Click to edit.`;
    const settingsChip = `<button type="button" class="vst-settings-chip" data-vst-settings title="${escapeAttr(settingsTip)}" aria-label="${escapeAttr(settingsTip)}"><span class="vst-settings-dims">${chipWidth}×${chipHeight}</span><span class="vst-settings-chip-sep" aria-hidden="true">·</span><span class="vst-settings-fps">${chipFps} fps</span></button>`;
    const enabled = options?.enabled !== false;
    const enableToggle = `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual."><input type="checkbox" class="vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}><span class="vst-enable-label">Enable</span></label>`;
    const header = `<div class="vst-topbar${enabled ? "" : " vst-topbar-disabled"}"><div class="vst-topbar-main"><span class="vst-title">Timeline</span>` + enableToggle + `<span class="vst-sub"><span class="vst-stat-num">${clips.length}</span> ${clipWord}</span>` + settingsChip + `</div><div class="vst-topbar-tools"><button type="button" class="vst-toggle vst-add-clip" data-vst-add-clip title="Add a new clip to the end of the sequence">+ Clip</button><span class="vst-tool-sep" aria-hidden="true"></span><div class="vst-zoom" role="group" aria-label="Timeline zoom (Ctrl+wheel over the track)"><button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-out title="Zoom out (show more time)" aria-label="Zoom out">−</button><span class="vst-zoom-pct" data-vst-zoom-pct title="Zoom level (100% = default)">${zoomPct}%</span><input type="range" class="vst-zoom-slider" data-vst-zoom-slider min="${MIN_PX_PER_SECOND}" max="${MAX_PX_PER_SECOND}" step="1" value="${Math.round(pxPerSecond)}" aria-label="Zoom (pixels per second)" title="Zoom (applies on release)"><button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-in title="Zoom in (show less time, more detail)" aria-label="Zoom in">+</button><button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-fit title="Fit the whole sequence to the view" aria-label="Zoom to fit">Fit</button></div><span class="vst-tool-sep" aria-hidden="true"></span><button type="button" class="vst-toggle vst-toggle-unit" data-vst-unit-toggle title="Toggle ruler units between seconds and frames (in-memory only)">${toggleLabel}</button><button type="button" class="vst-toggle vst-hist-btn" data-vst-undo title="Undo (Ctrl+Z)" aria-label="Undo">↶</button><button type="button" class="vst-toggle vst-hist-btn" data-vst-redo title="Redo (Ctrl+Shift+Z or Ctrl+Y)" aria-label="Redo">↷</button></div>` + readout + `</div>`;
    const wireTopbar = () => {
      const wire = (selector, handler) => {
        if (!handler) {
          return;
        }
        const btn = body.querySelector(selector);
        if (btn) {
          btn.addEventListener("click", () => handler());
        }
      };
      const enableInput = body.querySelector("[data-vst-enable]");
      if (enableInput && options?.onToggleEnabled) {
        enableInput.addEventListener("change", () => {
          options.onToggleEnabled?.(enableInput.checked);
        });
      }
      const settingsBtn = body.querySelector(
        "[data-vst-settings]"
      );
      if (settingsBtn && options?.onOpenSettings) {
        settingsBtn.addEventListener("click", () => {
          options.onOpenSettings?.(settingsBtn);
        });
      }
      wire("[data-vst-unit-toggle]", options?.onToggleUnit);
      wire("[data-vst-zoom-in]", options?.onZoomIn);
      wire("[data-vst-zoom-out]", options?.onZoomOut);
      wire("[data-vst-zoom-fit]", options?.onZoomFit);
      wire("[data-vst-undo]", options?.onUndo);
      wire("[data-vst-redo]", options?.onRedo);
      const slider = body.querySelector(
        "[data-vst-zoom-slider]"
      );
      if (slider) {
        slider.addEventListener("input", () => {
          const pct = body.querySelector(
            "[data-vst-zoom-pct]"
          );
          if (pct) {
            const value = Number.parseFloat(slider.value);
            pct.textContent = `${Math.round(value / DEFAULT_PX_PER_SECOND * 100)}%`;
          }
        });
        if (options?.onZoomSlider) {
          slider.addEventListener("change", () => {
            options.onZoomSlider?.(Number.parseFloat(slider.value));
          });
        }
      }
      if (options?.onAddClip) {
        for (const btn of body.querySelectorAll("[data-vst-add-clip]")) {
          btn.addEventListener("click", () => options.onAddClip?.());
        }
      }
    };
    const wireScroll = () => {
      const onZoomWheel = options?.onZoomWheel;
      if (!onZoomWheel) {
        return;
      }
      const scroll = body.querySelector(".vst-scroll");
      scroll?.addEventListener(
        "wheel",
        (event) => {
          if (!event.ctrlKey && !event.metaKey) {
            return;
          }
          event.preventDefault();
          const factor = event.deltaY < 0 ? ZOOM_FACTOR : 1 / ZOOM_FACTOR;
          onZoomWheel(factor, event.clientX);
        },
        { passive: false }
      );
    };
    if (clips.length === 0) {
      body.innerHTML = `${header}<div class="vst-empty"><div class="vst-empty-icon" aria-hidden="true">🎬</div><div class="vst-empty-title">No clips yet.</div><div class="vst-empty-hint">Add one here — or in the VideoStages panel on the left — to start building your sequence.</div><button type="button" class="vst-toggle vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button></div>`;
      wireTopbar();
      return;
    }
    const lastLayout = layouts[layouts.length - 1];
    const endPx = lastLayout.startPx + lastLayout.widthPx;
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond).map(
      (t) => `<span class="vst-tick vst-tick-grid" style="left:${t.x}px"><span class="vst-tick-label">${escapeAttr(formatRulerLabel(t.seconds, unit, fps))}</span></span>`
    );
    const minorStep = chooseRulerStepSeconds(pxPerSecond) / 5;
    const minorTicks = [];
    const MAX_MINOR_TICKS = 5e3;
    for (let i = 1; i <= MAX_MINOR_TICKS; i++) {
      const t = i * minorStep;
      if (t > totalSeconds + 1e-6) {
        break;
      }
      if (i % 5 === 0) {
        continue;
      }
      minorTicks.push(
        `<span class="vst-tick vst-tick-minor" style="left:${t * pxPerSecond}px" aria-hidden="true"></span>`
      );
    }
    const seamTicks = layouts.slice(1).map(
      (l) => `<span class="vst-tick vst-tick-seam" style="left:${l.startPx}px" aria-hidden="true"></span>`
    );
    const endTick = `<span class="vst-tick vst-tick-end" style="left:${endPx}px"><span class="vst-tick-label">${escapeAttr(formatRulerLabel(totalSeconds, unit, fps))}</span></span>`;
    const ticks = [
      ...minorTicks,
      ...gridTicks,
      ...seamTicks,
      endTick
    ];
    const regions = layouts.map((l) => {
      const clip = clips[l.index];
      const skipClass = l.skipped ? " vst-region-skipped" : "";
      const tinyClass = l.widthPx <= 12 ? " vst-region-tiny" : "";
      const skipChip = l.skipped ? `<span class="vst-chip vst-chip-skip">skipped</span>` : "";
      const dur = escapeAttr(
        formatTimeLabel(l.durationSeconds, unit, fps)
      );
      const skipTitle = l.skipped ? "Unskip clip" : "Skip clip";
      const skipGlyph = l.skipped ? "⟲" : "⊘";
      const controls = `<div class="vst-region-controls"><button type="button" class="vst-region-btn${l.skipped ? " vst-region-btn-active" : ""}" data-vst-region-action="skip" title="${skipTitle}" aria-label="${skipTitle}">${skipGlyph}</button></div>`;
      const rightGrip = lengthDerived(clip) ? "" : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
      const hue = clipHueCss(clip.hue);
      const skippedStages = (clip.stages ?? []).filter(
        (stage) => stage?.skipped
      ).length;
      const stagesTitle = skippedStages > 0 ? `Stages: ${l.stageCount} (${skippedStages} skipped)` : "Stages";
      const renderWidth = Math.max(1, l.widthPx - 2);
      return `<div class="vst-region${skipClass}${tinyClass}" style="left:${l.startPx}px;width:${renderWidth}px;--clip-hue:${hue}" data-clip-idx="${l.index}" title="Clip ${l.index} · ${dur} · Shift+click to delete">` + renderRegionThumb(clip) + renderKeyframes(clip, l.index, l.durationSeconds, fps, unit) + `<div class="vst-region-head"><span class="vst-region-name">Clip ${l.index}</span><span class="vst-chip" title="${escapeAttr(stagesTitle)}">▤ ${l.stageCount}</span><span class="vst-chip" title="Keyframes">◆ ${l.keyframeCount}</span>` + skipChip + `<span class="vst-region-dur">${dur}</span></div>` + renderBadges(clip) + controls + rightGrip + `</div>`;
    }).join("");
    const audioRow = renderAudioTrackRow(clips, layouts);
    const referencesRow = renderReferencesTrackRow(clips, layouts, fps, unit);
    const videoHead = `<div class="vst-track-head"><div class="vst-track-icon vst-track-icon-video" aria-hidden="true">▶</div><div class="vst-track-label"><strong>Video</strong><small>V1 · ${clips.length} ${clipWord}</small></div></div>`;
    const promptRow = renderPromptTrackRow(
      clips,
      layouts,
      pxPerSecond,
      `${options?.globalPrompt ?? ""}`
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML = `${header}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px"><div class="vst-ruler-row"><div class="vst-corner">Timeline</div><div class="vst-ruler">${ticks.join("")}</div></div>` + promptRow + `<div class="vst-track-row vst-track-video">${videoHead}<div class="vst-track-cell">${regions}</div></div>` + referencesRow + audioRow + `</div></div>`;
    wireTopbar();
    wireScroll();
  };

  // frontend/timelineLinking.ts
  var REGION_SELECTOR = ".vst-region[data-clip-idx]";
  var REGION_ACTION_SELECTOR = "[data-vst-region-action]";
  var REGION_RESIZE_SELECTOR = ".vst-region-resize";
  var CLIP_SHIFT_SELECTOR = ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";
  var KEY_SELECTOR = ".vst-key[data-ref-idx]";
  var REGION_SELECTED_CLASS = "vst-region-selected";
  var DRAGGING_CLASS = "vst-dragging";
  var RESIZING_CLASS = "vst-resizing";
  var KEYFRAMING_CLASS = "vst-keyframing";
  var DROP_INDICATOR_CLASS = "vst-drop-indicator";
  var DRAG_THRESHOLD_PX = 5;
  var MIN_RESIZE_WIDTH_PX = 24;
  var REGION_DRAGGING_CLASS = "vst-region-dragging";
  var livePxPerSecond = (body) => {
    const pps = Number.parseFloat(body.dataset.vstPps ?? "");
    return Number.isFinite(pps) && pps > 0 ? pps : DEFAULT_PX_PER_SECOND;
  };
  var currentFps = () => {
    try {
      const fps = getRootDefaults().fps;
      return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
      return 24;
    }
  };
  var resolveSelectedIndex = (selectedIndex, clipCount) => {
    if (selectedIndex === null || !Number.isInteger(selectedIndex) || selectedIndex < 0 || selectedIndex >= clipCount) {
      return null;
    }
    return selectedIndex;
  };
  var parseClipIdx2 = (el) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute("data-clip-idx");
    if (raw === null) {
      return null;
    }
    const idx = Number.parseInt(raw, 10);
    return Number.isInteger(idx) && idx >= 0 ? idx : null;
  };
  var shiftClipsAfter = (body, idx, deltaPx) => {
    for (const el of body.querySelectorAll(CLIP_SHIFT_SELECTOR)) {
      const elIdx = parseClipIdx2(el);
      if (elIdx !== null && elIdx > idx) {
        el.style.transform = deltaPx !== 0 ? `translateX(${deltaPx}px)` : "";
      }
    }
  };
  var clearClipShifts = (body) => {
    for (const el of body.querySelectorAll(CLIP_SHIFT_SELECTOR)) {
      el.style.transform = "";
    }
  };
  var parseRefIdx = (el) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute("data-ref-idx");
    if (raw === null) {
      return null;
    }
    const idx = Number.parseInt(raw, 10);
    return Number.isInteger(idx) && idx >= 0 ? idx : null;
  };
  var createTimelineLinking = () => {
    let attachedBody = null;
    let selectedIndex = null;
    let dragState = null;
    let suppressClick = false;
    let dropIndicator = null;
    let resizeState = null;
    let keyframeState = null;
    const findRegion = (body, idx) => body.querySelector(`.vst-region[data-clip-idx="${idx}"]`);
    const markSelection = (body) => {
      for (const region of body.querySelectorAll(
        `.${REGION_SELECTED_CLASS}`
      )) {
        region.classList.remove(REGION_SELECTED_CLASS);
      }
      if (selectedIndex === null) {
        return;
      }
      findRegion(body, selectedIndex)?.classList.add(REGION_SELECTED_CLASS);
    };
    const onRegionClick = (body, event) => {
      if (suppressClick) {
        suppressClick = false;
        return;
      }
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }
      const actionButton = target.closest(REGION_ACTION_SELECTOR);
      if (actionButton) {
        event.stopPropagation();
        const actionRegion = actionButton.closest(REGION_SELECTOR);
        const actionIdx = parseClipIdx2(actionRegion);
        if (actionIdx === null) {
          return;
        }
        const action = actionButton.getAttribute("data-vst-region-action");
        if (action === "skip") {
          applySkip(actionIdx);
        }
        return;
      }
      const region = target.closest(REGION_SELECTOR);
      const idx = parseClipIdx2(region);
      if (idx === null) {
        return;
      }
      if (event.shiftKey) {
        applyDelete(idx);
        return;
      }
      selectedIndex = idx;
      markSelection(body);
    };
    const readRegions = (body) => {
      const els = Array.from(
        body.querySelectorAll(REGION_SELECTOR)
      );
      const rects = els.map((el) => {
        const r = el.getBoundingClientRect();
        return { startPx: r.left, widthPx: r.width };
      });
      return { els, rects };
    };
    const removeDropIndicator = () => {
      dropIndicator?.remove();
      dropIndicator = null;
    };
    const showDropIndicator = (els, gap) => {
      if (els.length === 0) {
        return;
      }
      const track = els[0].parentElement;
      if (!track) {
        return;
      }
      if (!dropIndicator) {
        dropIndicator = document.createElement("div");
        dropIndicator.className = DROP_INDICATOR_CLASS;
      }
      if (dropIndicator.parentElement !== track) {
        track.appendChild(dropIndicator);
      }
      const left = gap < els.length ? els[gap].offsetLeft : els[els.length - 1].offsetLeft + els[els.length - 1].offsetWidth;
      dropIndicator.style.left = `${left}px`;
    };
    const endDrag = (body) => {
      if (dragState) {
        findRegion(body, dragState.sourceIdx)?.classList.remove(
          REGION_DRAGGING_CLASS
        );
      }
      dragState = null;
      removeDropIndicator();
      body.classList.remove(DRAGGING_CLASS);
    };
    const endResize = (body) => {
      if (resizeState) {
        resizeState.el.style.width = `${resizeState.originalWidthPx}px`;
      }
      clearClipShifts(body);
      resizeState = null;
      body.classList.remove(RESIZING_CLASS);
    };
    const applySkip = (idx) => {
      const clips = getClips();
      if (idx < 0 || idx >= clips.length) {
        return;
      }
      clips[idx].skipped = !clips[idx].skipped;
      saveClips(clips);
    };
    const applyDelete = (idx) => {
      const clips = getClips();
      if (idx < 0 || idx >= clips.length) {
        return;
      }
      clips.splice(idx, 1);
      if (selectedIndex !== null) {
        if (selectedIndex === idx) {
          selectedIndex = null;
        } else if (selectedIndex > idx) {
          selectedIndex -= 1;
        }
      }
      saveClips(clips);
    };
    const applyToggleKeyframeFromEnd = (clipIdx, refIdx, sourceJson) => {
      if (readVideoStagesSection() !== sourceJson) {
        return;
      }
      const clips = getClips();
      const clip = clips[clipIdx];
      const ref = clip?.refs?.[refIdx];
      if (!ref) {
        return;
      }
      ref.fromEnd = !ref.fromEnd;
      ref.frame = clamp(
        ref.frame,
        REF_FRAME_MIN,
        getReferenceFrameMax(getRootDefaults, clip)
      );
      saveClips(clips);
    };
    const endKeyframe = (body) => {
      if (keyframeState) {
        keyframeState.el.style.left = keyframeState.originalLeft;
      }
      keyframeState = null;
      body.classList.remove(KEYFRAMING_CLASS);
    };
    const onBodyMouseDown = (event) => {
      suppressClick = false;
      const me = event;
      if (me.button !== 0) {
        return;
      }
      if (!(me.target instanceof Element)) {
        return;
      }
      const pip = me.target.closest(KEY_SELECTOR);
      if (pip instanceof HTMLElement) {
        const pipRegion = pip.closest(REGION_SELECTOR);
        const clipIdx = parseClipIdx2(pipRegion);
        const refIdx = parseRefIdx(pip);
        if (clipIdx === null || refIdx === null || !(pipRegion instanceof HTMLElement)) {
          return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        const ref = clip?.refs?.[refIdx];
        if (!ref) {
          return;
        }
        keyframeState = {
          clipIdx,
          refIdx,
          el: pip,
          regionEl: pipRegion,
          startX: me.clientX,
          originalLeft: pip.style.left,
          active: false,
          durationSeconds: clip.duration,
          fps: currentFps(),
          fromEnd: ref.fromEnd === true,
          shiftKey: me.shiftKey,
          sourceJson: readVideoStagesSection()
        };
        me.preventDefault();
        return;
      }
      if (me.target.closest(REGION_ACTION_SELECTOR)) {
        return;
      }
      if (me.shiftKey) {
        me.preventDefault();
        return;
      }
      const resizeGrip = me.target.closest(REGION_RESIZE_SELECTOR);
      if (resizeGrip) {
        const region = resizeGrip.closest(REGION_SELECTOR);
        const idx2 = parseClipIdx2(region);
        if (idx2 === null || !(region instanceof HTMLElement)) {
          return;
        }
        const rect = region.getBoundingClientRect();
        resizeState = {
          idx: idx2,
          el: region,
          startX: me.clientX,
          startLeftPx: rect.left,
          originalWidthPx: rect.width,
          active: false,
          sourceJson: readVideoStagesSection()
        };
        me.preventDefault();
        return;
      }
      const target = me.target.closest(REGION_SELECTOR);
      const idx = parseClipIdx2(target);
      if (idx === null) {
        return;
      }
      dragState = {
        sourceIdx: idx,
        startX: me.clientX,
        startY: me.clientY,
        active: false,
        sourceJson: readVideoStagesSection()
      };
    };
    const onDocMouseMove = (body, event) => {
      if (keyframeState) {
        const kme = event;
        if (!keyframeState.active) {
          if (Math.abs(kme.clientX - keyframeState.startX) < DRAG_THRESHOLD_PX) {
            return;
          }
          keyframeState.active = true;
          body.classList.add(KEYFRAMING_CLASS);
        }
        const rect = keyframeState.regionEl.getBoundingClientRect();
        const frame = pxToFrame(
          kme.clientX - rect.left,
          rect.width,
          keyframeState.durationSeconds,
          keyframeState.fps,
          keyframeState.fromEnd
        );
        const time = keyframeTimeSeconds(
          frame,
          keyframeState.fromEnd,
          keyframeState.durationSeconds,
          keyframeState.fps
        );
        keyframeState.el.style.left = `${keyframeLeftPercent(
          time,
          keyframeState.durationSeconds
        )}%`;
        return;
      }
      if (resizeState) {
        const rme = event;
        if (!resizeState.active) {
          if (Math.abs(rme.clientX - resizeState.startX) < DRAG_THRESHOLD_PX) {
            return;
          }
          resizeState.active = true;
        }
        const width = Math.max(
          MIN_RESIZE_WIDTH_PX,
          rme.clientX - resizeState.startLeftPx
        );
        body.classList.add(RESIZING_CLASS);
        resizeState.el.style.width = `${width}px`;
        shiftClipsAfter(
          body,
          resizeState.idx,
          width - resizeState.originalWidthPx
        );
        return;
      }
      if (!dragState) {
        return;
      }
      const me = event;
      if (!dragState.active) {
        const dx = me.clientX - dragState.startX;
        const dy = me.clientY - dragState.startY;
        if (Math.hypot(dx, dy) < DRAG_THRESHOLD_PX) {
          return;
        }
        dragState.active = true;
        body.classList.add(DRAGGING_CLASS);
        findRegion(body, dragState.sourceIdx)?.classList.add(
          REGION_DRAGGING_CLASS
        );
      }
      const { els, rects } = readRegions(body);
      showDropIndicator(els, computeDropIndex(me.clientX, rects));
    };
    const onDocMouseUp = (body, event) => {
      if (keyframeState) {
        const ks = keyframeState;
        const kme = event;
        const rect = ks.regionEl.getBoundingClientRect();
        endKeyframe(body);
        suppressClick = true;
        if (!ks.active) {
          if (ks.shiftKey) {
            applyToggleKeyframeFromEnd(
              ks.clipIdx,
              ks.refIdx,
              ks.sourceJson
            );
          }
          return;
        }
        if (readVideoStagesSection() !== ks.sourceJson) {
          return;
        }
        const newFrame = pxToFrame(
          kme.clientX - rect.left,
          rect.width,
          ks.durationSeconds,
          ks.fps,
          ks.fromEnd
        );
        const clips2 = getClips();
        const ref = clips2[ks.clipIdx]?.refs?.[ks.refIdx];
        if (!ref || ref.frame === newFrame) {
          return;
        }
        ref.frame = newFrame;
        saveClips(clips2);
        return;
      }
      if (resizeState) {
        const rs = resizeState;
        const me2 = event;
        endResize(body);
        if (!rs.active) {
          return;
        }
        const width = me2.clientX - rs.startLeftPx;
        suppressClick = true;
        if (readVideoStagesSection() !== rs.sourceJson) {
          return;
        }
        const clips2 = getClips();
        if (rs.idx < 0 || rs.idx >= clips2.length) {
          return;
        }
        if (clips2[rs.idx].clipLengthFromAudio || clips2[rs.idx].clipLengthFromControlNet) {
          return;
        }
        const newDuration = pxToDuration(
          width,
          livePxPerSecond(body),
          currentFps()
        );
        if (applyClipDurationResize(
          clips2[rs.idx],
          newDuration,
          getRootDefaults
        )) {
          selectedIndex = rs.idx;
          saveClips(clips2);
        }
        return;
      }
      const state = dragState;
      if (!state) {
        return;
      }
      endDrag(body);
      if (!state.active) {
        return;
      }
      suppressClick = true;
      const me = event;
      const { rects } = readRegions(body);
      const gap = computeDropIndex(me.clientX, rects);
      const from = state.sourceIdx;
      if (isNoOpMove(from, gap)) {
        selectedIndex = from;
        markSelection(body);
        return;
      }
      if (readVideoStagesSection() !== state.sourceJson) {
        return;
      }
      const clips = getClips();
      if (from < 0 || from >= clips.length) {
        return;
      }
      selectedIndex = finalIndexAfterMove(from, gap);
      saveClips(moveItem(clips, from, gap));
    };
    const onDocKeyDown = (body, event) => {
      if (event.key !== "Escape") {
        return;
      }
      if (keyframeState) {
        suppressClick = true;
        endKeyframe(body);
      }
      if (resizeState) {
        if (resizeState.active) {
          suppressClick = true;
        }
        endResize(body);
      }
      if (dragState) {
        if (dragState.active) {
          suppressClick = true;
        }
        endDrag(body);
      }
    };
    const onBodyKeyDown = (event) => {
      const ke = event;
      if (ke.key !== "Enter" && ke.key !== " ") {
        return;
      }
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }
      const pipEl = target.closest(KEY_SELECTOR);
      if (!(pipEl instanceof HTMLElement)) {
        return;
      }
      ke.preventDefault();
      const pipRegion = pipEl.closest(REGION_SELECTOR);
      const clipIdx = parseClipIdx2(pipRegion);
      const refIdx = parseRefIdx(pipEl);
      if (clipIdx === null || refIdx === null) {
        return;
      }
      applyToggleKeyframeFromEnd(clipIdx, refIdx, readVideoStagesSection());
    };
    let bodyClickHandler = null;
    let bodyDownHandler = null;
    let bodyKeyDownHandler = null;
    let docMoveHandler = null;
    let docUpHandler = null;
    let docKeyHandler = null;
    const attach = (body) => {
      if (attachedBody === body) {
        return;
      }
      if (attachedBody) {
        dispose();
      }
      bodyClickHandler = (e) => onRegionClick(body, e);
      bodyDownHandler = (e) => onBodyMouseDown(e);
      bodyKeyDownHandler = (e) => onBodyKeyDown(e);
      docMoveHandler = (e) => onDocMouseMove(body, e);
      docUpHandler = (e) => onDocMouseUp(body, e);
      docKeyHandler = (e) => onDocKeyDown(body, e);
      body.addEventListener("click", bodyClickHandler);
      body.addEventListener("mousedown", bodyDownHandler);
      body.addEventListener("keydown", bodyKeyDownHandler);
      document.addEventListener("mousemove", docMoveHandler);
      document.addEventListener("mouseup", docUpHandler);
      document.addEventListener("keydown", docKeyHandler);
      attachedBody = body;
    };
    const reapplySelection = (body, clipCount) => {
      selectedIndex = resolveSelectedIndex(selectedIndex, clipCount);
      markSelection(body);
    };
    const dispose = () => {
      if (attachedBody) {
        if (bodyClickHandler) {
          attachedBody.removeEventListener("click", bodyClickHandler);
        }
        if (bodyDownHandler) {
          attachedBody.removeEventListener("mousedown", bodyDownHandler);
        }
        if (bodyKeyDownHandler) {
          attachedBody.removeEventListener("keydown", bodyKeyDownHandler);
        }
        endDrag(attachedBody);
        endResize(attachedBody);
        endKeyframe(attachedBody);
      }
      if (docMoveHandler) {
        document.removeEventListener("mousemove", docMoveHandler);
      }
      if (docUpHandler) {
        document.removeEventListener("mouseup", docUpHandler);
      }
      if (docKeyHandler) {
        document.removeEventListener("keydown", docKeyHandler);
      }
      bodyClickHandler = null;
      bodyDownHandler = null;
      bodyKeyDownHandler = null;
      docMoveHandler = null;
      docUpHandler = null;
      docKeyHandler = null;
      attachedBody = null;
      dragState = null;
      resizeState = null;
      keyframeState = null;
      suppressClick = false;
    };
    const getSelectedIndex = () => selectedIndex;
    return { attach, reapplySelection, getSelectedIndex, dispose };
  };

  // frontend/timelinePromptTrack.ts
  var MAJOR_SELECTOR = ".vst-major-seg[data-clip-idx]";
  var MINOR_SELECTOR = ".vst-minor-seg[data-clip-idx]";
  var MINOR_EDGE_SELECTOR = "[data-vst-minor-edge]";
  var MINOR_ACTION_SELECTOR = "[data-vst-minor-action]";
  var LANE_SELECTOR = ".vst-minor-lane[data-clip-idx]";
  var DRAG_THRESHOLD_PX2 = 4;
  var DRAGGING_CLASS2 = "vst-prompt-dragging";
  var GHOST_CLASS = "vst-minor-ghost";
  var parseIntAttr = (el, name) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
      return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
  };
  var clipDurationOf = (clip) => clip ? Math.max(0, clip.duration || 0) : 0;
  var roundSeconds = (seconds) => Math.round(seconds * 10) / 10;
  var otherSpans = (windows, excludeIdx, clipDuration) => windows.map((w, k) => ({
    k,
    start: clamp(w.start, 0, clipDuration),
    end: clamp(w.start + w.duration, 0, clipDuration)
  })).filter((s) => s.k !== excludeIdx && s.end > s.start).sort((a, b) => a.start - b.start).map((s) => ({ start: s.start, end: s.end }));
  var freeIntervalAt = (spans, clipDuration, point) => {
    const p = clamp(point, 0, clipDuration);
    let lo = 0;
    let hi = clipDuration;
    for (const span of spans) {
      if (span.end <= p) {
        if (span.end > lo) {
          lo = span.end;
        }
      } else if (span.start >= p) {
        hi = span.start;
        break;
      } else {
        return [p, p];
      }
    }
    return [lo, hi];
  };
  var createTimelinePromptTrack = () => {
    let moveState = null;
    let resizeState = null;
    let createState = null;
    let suppressClick = false;
    let activeEditorWrap = null;
    let editingAnchor = null;
    let outsideMouseHandler = null;
    let boundBody = null;
    const closeEditor = () => {
      if (outsideMouseHandler) {
        document.removeEventListener(
          "mousedown",
          outsideMouseHandler,
          true
        );
        outsideMouseHandler = null;
      }
      if (editingAnchor) {
        editingAnchor.classList.remove("vst-prompt-editing");
        editingAnchor = null;
      }
      if (activeEditorWrap) {
        activeEditorWrap.remove();
        activeEditorWrap = null;
      }
    };
    const openEditor = (anchor, label, initial, placeholder, commit, onDelete = null) => {
      closeEditor();
      const sourceJson = readVideoStagesSection();
      const hostRect = (boundBody ?? document.body).getBoundingClientRect();
      const viewportW = window.innerWidth || document.documentElement.clientWidth;
      const width = clamp(Math.round(hostRect.width - 32), 280, 560);
      const left = clamp(
        Math.round(hostRect.left + (hostRect.width - width) / 2),
        8,
        Math.max(8, viewportW - width - 8)
      );
      const wrap = document.createElement("div");
      wrap.className = "vst-prompt-inspector";
      wrap.style.left = `${left}px`;
      wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
      wrap.style.width = `${width}px`;
      const head = document.createElement("div");
      head.className = "vst-prompt-inspector-head";
      head.textContent = label;
      const editor = document.createElement("textarea");
      editor.className = "vst-prompt-editor";
      editor.value = initial;
      editor.placeholder = placeholder;
      const deleteBtn = onDelete ? document.createElement("button") : null;
      if (deleteBtn) {
        deleteBtn.type = "button";
        deleteBtn.className = "vst-refs-delete";
        deleteBtn.textContent = "Delete prompt window";
      }
      const hint = document.createElement("div");
      hint.className = "vst-prompt-inspector-hint";
      hint.textContent = "Enter to save · Shift+Enter for a new line · Esc to cancel";
      if (deleteBtn) {
        wrap.append(head, editor, deleteBtn, hint);
      } else {
        wrap.append(head, editor, hint);
      }
      anchor.classList.add("vst-prompt-editing");
      editingAnchor = anchor;
      let done = false;
      const finish = (save) => {
        if (done) {
          return;
        }
        done = true;
        const value = editor.value;
        closeEditor();
        if (save && !isStale(sourceJson)) {
          commit(value);
        }
      };
      if (deleteBtn && onDelete) {
        deleteBtn.addEventListener("click", (event) => {
          event.preventDefault();
          if (done) {
            return;
          }
          done = true;
          closeEditor();
          if (!isStale(sourceJson)) {
            onDelete();
          }
        });
      }
      editor.addEventListener("keydown", (event) => {
        if (event.key === "Enter" && !event.shiftKey) {
          event.preventDefault();
          finish(true);
        } else if (event.key === "Escape") {
          event.preventDefault();
          finish(false);
        }
        event.stopPropagation();
      });
      const onOutside = (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
          return;
        }
        if (target.closest(".vst-prompt-inspector") || target.closest(".sui-popover")) {
          return;
        }
        finish(true);
      };
      outsideMouseHandler = onOutside;
      document.addEventListener("mousedown", onOutside, true);
      document.body.appendChild(wrap);
      activeEditorWrap = wrap;
      if (typeof textPromptAddKeydownHandler === "function") {
        textPromptAddKeydownHandler(editor);
      }
      editor.focus();
      editor.select();
    };
    const isStale = (sourceJson) => readVideoStagesSection() !== sourceJson;
    const commitMajorPrompt = (clipIdx, text) => {
      const clips = getClips();
      const clip = clips[clipIdx];
      if (!clip) {
        return;
      }
      clip.prompt = text.trim();
      saveClips(clips);
    };
    const commitMinorPrompt = (clipIdx, windowIdx, text) => {
      const clips = getClips();
      const window2 = clips[clipIdx]?.promptWindows?.[windowIdx];
      if (!window2) {
        return;
      }
      window2.prompt = text.trim();
      saveClips(clips);
    };
    const applyMinorAction = (clipIdx, windowIdx, action) => {
      const clips = getClips();
      const clip = clips[clipIdx];
      const window2 = clip?.promptWindows?.[windowIdx];
      if (!clip || !window2) {
        return;
      }
      if (action === "delete") {
        clip.promptWindows.splice(windowIdx, 1);
      } else if (action === "skip") {
        window2.skipped = !window2.skipped;
      } else {
        return;
      }
      saveClips(clips);
    };
    const commitMove = (state, dxPx, pps) => {
      if (isStale(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      const window2 = clip?.promptWindows?.[state.windowIdx];
      if (!clip || !window2) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const desiredStart = state.startStart + dxPx / pps;
      const dur = Math.min(state.duration, clipDur);
      const maxStart = Math.max(state.boundLo, state.boundHi - dur);
      window2.start = roundSeconds(
        clamp(desiredStart, state.boundLo, maxStart)
      );
      window2.duration = roundSeconds(
        Math.max(
          PROMPT_WINDOW_MIN_DURATION,
          Math.min(dur, state.boundHi - window2.start)
        )
      );
      saveClips(clips);
    };
    const commitResize = (state, dxPx, pps) => {
      if (isStale(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      const window2 = clip?.promptWindows?.[state.windowIdx];
      if (!clip || !window2) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const spans = otherSpans(clip.promptWindows, state.windowIdx, clipDur);
      const deltaSec = dxPx / pps;
      if (state.edge === "right") {
        const [, hi] = freeIntervalAt(spans, clipDur, state.startStart);
        const end = clamp(
          state.startStart + state.startDuration + deltaSec,
          state.startStart + PROMPT_WINDOW_MIN_DURATION,
          hi
        );
        window2.start = roundSeconds(state.startStart);
        window2.duration = roundSeconds(end - state.startStart);
      } else {
        const end = state.startStart + state.startDuration;
        const [lo] = freeIntervalAt(
          spans,
          clipDur,
          Math.max(0, end - 1e-3)
        );
        const start = clamp(
          state.startStart + deltaSec,
          lo,
          end - PROMPT_WINDOW_MIN_DURATION
        );
        window2.start = roundSeconds(start);
        window2.duration = roundSeconds(end - start);
      }
      saveClips(clips);
    };
    const commitCreate = (state, endSec) => {
      if (isStale(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      if (!clip) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const spans = otherSpans(clip.promptWindows, -1, clipDur);
      const [lo, hi] = freeIntervalAt(spans, clipDur, state.startSec);
      const gap = hi - lo;
      if (gap < PROMPT_WINDOW_MIN_DURATION) {
        return;
      }
      let start;
      let duration;
      if (endSec === null) {
        duration = Math.min(PROMPT_WINDOW_DEFAULT_DURATION, gap);
        start = clamp(state.startSec, lo, hi - duration);
      } else {
        const a = clamp(Math.min(state.startSec, endSec), lo, hi);
        const b = clamp(Math.max(state.startSec, endSec), lo, hi);
        start = a;
        duration = Math.max(PROMPT_WINDOW_MIN_DURATION, b - a);
        if (start + duration > hi) {
          duration = hi - start;
        }
      }
      if (duration < PROMPT_WINDOW_MIN_DURATION) {
        return;
      }
      const window2 = {
        prompt: "",
        start: roundSeconds(start),
        duration: roundSeconds(duration),
        skipped: false
      };
      clip.promptWindows.push(window2);
      clip.promptWindows.sort((x, y) => x.start - y.start);
      saveClips(clips);
    };
    const laneTimeAt = (state, clientX, pps) => clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);
    const clearGesture = (body) => {
      if (createState?.ghost) {
        createState.ghost.remove();
      }
      moveState = null;
      resizeState = null;
      createState = null;
      body.classList.remove(DRAGGING_CLASS2);
    };
    const onBodyMouseDown = (event) => {
      suppressClick = false;
      const me = event;
      if (me.button !== 0 || !(me.target instanceof Element)) {
        return;
      }
      if (me.target.closest(MINOR_ACTION_SELECTOR)) {
        return;
      }
      if (me.shiftKey && me.target.closest(MINOR_SELECTOR)) {
        me.preventDefault();
        return;
      }
      const edgeEl = me.target.closest(MINOR_EDGE_SELECTOR);
      if (edgeEl) {
        const seg2 = edgeEl.closest(MINOR_SELECTOR);
        const clipIdx = parseIntAttr(seg2, "data-clip-idx");
        const windowIdx = parseIntAttr(seg2, "data-window-idx");
        const edge = edgeEl.getAttribute("data-vst-minor-edge") === "left" ? "left" : "right";
        if (clipIdx === null || windowIdx === null || !(seg2 instanceof HTMLElement)) {
          return;
        }
        const window2 = getClips()[clipIdx]?.promptWindows?.[windowIdx];
        if (!window2) {
          return;
        }
        resizeState = {
          clipIdx,
          windowIdx,
          edge,
          el: seg2,
          startX: me.clientX,
          startStart: window2.start,
          startDuration: window2.duration,
          clipDuration: clipDurationOf(getClips()[clipIdx]),
          originalLeft: seg2.style.left,
          originalWidth: seg2.style.width,
          active: false,
          sourceJson: readVideoStagesSection()
        };
        me.preventDefault();
        return;
      }
      const seg = me.target.closest(MINOR_SELECTOR);
      if (seg instanceof HTMLElement) {
        const clipIdx = parseIntAttr(seg, "data-clip-idx");
        const windowIdx = parseIntAttr(seg, "data-window-idx");
        if (clipIdx === null || windowIdx === null) {
          return;
        }
        const clip = getClips()[clipIdx];
        const window2 = clip?.promptWindows?.[windowIdx];
        if (!clip || !window2) {
          return;
        }
        const clipDuration = clipDurationOf(clip);
        const [boundLo, boundHi] = freeIntervalAt(
          otherSpans(clip.promptWindows, windowIdx, clipDuration),
          clipDuration,
          window2.start
        );
        moveState = {
          clipIdx,
          windowIdx,
          el: seg,
          startX: me.clientX,
          startStart: window2.start,
          duration: window2.duration,
          clipDuration,
          boundLo,
          boundHi,
          originalLeft: seg.style.left,
          active: false,
          sourceJson: readVideoStagesSection()
        };
        me.preventDefault();
        return;
      }
      const lane = me.target.closest(LANE_SELECTOR);
      if (lane instanceof HTMLElement) {
        const clipIdx = parseIntAttr(lane, "data-clip-idx");
        if (clipIdx === null) {
          return;
        }
        const rect = lane.getBoundingClientRect();
        const pps = livePxPerSecond(boundBody ?? lane);
        const clipDuration = clipDurationOf(getClips()[clipIdx]);
        const startSec = clamp(
          (me.clientX - rect.left) / pps,
          0,
          clipDuration
        );
        createState = {
          clipIdx,
          lane,
          laneLeft: rect.left,
          startSec,
          startX: me.clientX,
          clipDuration,
          ghost: null,
          active: false,
          sourceJson: readVideoStagesSection()
        };
        me.preventDefault();
      }
    };
    const onDocMouseMove = (body, event) => {
      const me = event;
      const pps = livePxPerSecond(body);
      if (resizeState) {
        const dx = me.clientX - resizeState.startX;
        if (!resizeState.active && Math.abs(dx) < DRAG_THRESHOLD_PX2) {
          return;
        }
        resizeState.active = true;
        body.classList.add(DRAGGING_CLASS2);
        const clipDur = resizeState.clipDuration;
        const deltaSec = dx / pps;
        if (resizeState.edge === "right") {
          const end = clamp(
            resizeState.startStart + resizeState.startDuration + deltaSec,
            resizeState.startStart + PROMPT_WINDOW_MIN_DURATION,
            clipDur
          );
          resizeState.el.style.width = `${Math.max(2, (end - resizeState.startStart) * pps)}px`;
        } else {
          const end = resizeState.startStart + resizeState.startDuration;
          const start = clamp(
            resizeState.startStart + deltaSec,
            0,
            end - PROMPT_WINDOW_MIN_DURATION
          );
          resizeState.el.style.left = `${start * pps}px`;
          resizeState.el.style.width = `${Math.max(2, (end - start) * pps)}px`;
        }
        return;
      }
      if (moveState) {
        const dx = me.clientX - moveState.startX;
        if (!moveState.active && Math.abs(dx) < DRAG_THRESHOLD_PX2) {
          return;
        }
        moveState.active = true;
        body.classList.add(DRAGGING_CLASS2);
        const dur = Math.min(moveState.duration, moveState.clipDuration);
        const maxStart = Math.max(
          moveState.boundLo,
          moveState.boundHi - dur
        );
        const start = clamp(
          moveState.startStart + dx / pps,
          moveState.boundLo,
          maxStart
        );
        moveState.el.style.left = `${start * pps}px`;
        return;
      }
      if (createState) {
        const dx = me.clientX - createState.startX;
        if (!createState.active && Math.abs(dx) < DRAG_THRESHOLD_PX2) {
          return;
        }
        createState.active = true;
        body.classList.add(DRAGGING_CLASS2);
        const nowSec = laneTimeAt(createState, me.clientX, pps);
        const a = Math.min(createState.startSec, nowSec);
        const b = Math.max(createState.startSec, nowSec);
        if (!createState.ghost) {
          const ghost = document.createElement("div");
          ghost.className = GHOST_CLASS;
          createState.lane.appendChild(ghost);
          createState.ghost = ghost;
        }
        createState.ghost.style.left = `${a * pps}px`;
        createState.ghost.style.width = `${Math.max(2, (b - a) * pps)}px`;
      }
    };
    const onDocMouseUp = (body, event) => {
      const me = event;
      const pps = livePxPerSecond(body);
      if (resizeState) {
        const state = resizeState;
        resizeState = null;
        body.classList.remove(DRAGGING_CLASS2);
        if (state.active) {
          suppressClick = true;
          commitResize(state, me.clientX - state.startX, pps);
        } else {
          state.el.style.left = state.originalLeft;
          state.el.style.width = state.originalWidth;
        }
        return;
      }
      if (moveState) {
        const state = moveState;
        moveState = null;
        body.classList.remove(DRAGGING_CLASS2);
        if (state.active) {
          suppressClick = true;
          commitMove(state, me.clientX - state.startX, pps);
        } else {
          state.el.style.left = state.originalLeft;
        }
        return;
      }
      if (createState) {
        const state = createState;
        createState = null;
        body.classList.remove(DRAGGING_CLASS2);
        if (state.ghost) {
          state.ghost.remove();
        }
        suppressClick = true;
        if (state.active) {
          commitCreate(state, laneTimeAt(state, me.clientX, pps));
        } else {
          commitCreate(state, null);
        }
      }
    };
    const onBodyClick = (event) => {
      if (suppressClick) {
        suppressClick = false;
        return;
      }
      if (!(event.target instanceof Element)) {
        return;
      }
      const actionEl = event.target.closest(MINOR_ACTION_SELECTOR);
      if (actionEl) {
        const seg = actionEl.closest(MINOR_SELECTOR);
        const clipIdx = parseIntAttr(seg, "data-clip-idx");
        const windowIdx = parseIntAttr(seg, "data-window-idx");
        const action = actionEl.getAttribute("data-vst-minor-action") ?? "";
        if (clipIdx !== null && windowIdx !== null) {
          applyMinorAction(clipIdx, windowIdx, action);
        }
        return;
      }
      const minor = event.target.closest(MINOR_SELECTOR);
      if (minor instanceof HTMLElement) {
        const clipIdx = parseIntAttr(minor, "data-clip-idx");
        const windowIdx = parseIntAttr(minor, "data-window-idx");
        if (clipIdx === null || windowIdx === null) {
          return;
        }
        if (event.shiftKey) {
          applyMinorAction(clipIdx, windowIdx, "delete");
          return;
        }
        const window2 = getClips()[clipIdx]?.promptWindows?.[windowIdx];
        if (!window2) {
          return;
        }
        openEditor(
          minor,
          `Clip ${clipIdx} · relay window ${windowIdx + 1}`,
          window2.prompt,
          "Minor prompt for this window…",
          (value) => commitMinorPrompt(clipIdx, windowIdx, value),
          () => applyMinorAction(clipIdx, windowIdx, "delete")
        );
        return;
      }
      const major = event.target.closest(MAJOR_SELECTOR);
      if (major instanceof HTMLElement) {
        const clipIdx = parseIntAttr(major, "data-clip-idx");
        if (clipIdx === null) {
          return;
        }
        const clip = getClips()[clipIdx];
        if (!clip) {
          return;
        }
        openEditor(
          major,
          `Clip ${clipIdx} · major prompt`,
          clip.prompt,
          "Clip prompt (blank inherits the global prompt)…",
          (value) => commitMajorPrompt(clipIdx, value)
        );
      }
    };
    const onDocKeyDown = (body, event) => {
      if (event.key !== "Escape") {
        return;
      }
      if (resizeState) {
        resizeState.el.style.left = resizeState.originalLeft;
        resizeState.el.style.width = resizeState.originalWidth;
      } else if (moveState) {
        moveState.el.style.left = moveState.originalLeft;
      } else if (!createState) {
        return;
      }
      clearGesture(body);
    };
    let moveHandler = null;
    let upHandler = null;
    let keyHandler = null;
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("mousedown", onBodyMouseDown);
      body.addEventListener("click", onBodyClick);
      moveHandler = (event) => onDocMouseMove(body, event);
      upHandler = (event) => onDocMouseUp(body, event);
      keyHandler = (event) => onDocKeyDown(body, event);
      document.addEventListener("mousemove", moveHandler);
      document.addEventListener("mouseup", upHandler);
      document.addEventListener("keydown", keyHandler);
    };
    const dispose = () => {
      closeEditor();
      if (boundBody) {
        boundBody.removeEventListener("mousedown", onBodyMouseDown);
        boundBody.removeEventListener("click", onBodyClick);
      }
      if (moveHandler) {
        document.removeEventListener("mousemove", moveHandler);
        moveHandler = null;
      }
      if (upHandler) {
        document.removeEventListener("mouseup", upHandler);
        upHandler = null;
      }
      if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
      }
      moveState = null;
      resizeState = null;
      createState = null;
      boundBody = null;
    };
    return { attach, dispose };
  };

  // frontend/imageSource.ts
  var buildImageSourceOptions = (currentValue = "") => {
    const options = [
      { value: REF_SOURCE_BASE, label: "Base Output" },
      { value: REF_SOURCE_REFINER, label: "Refiner Output" },
      { value: REF_SOURCE_UPLOAD, label: "Upload" }
    ];
    for (const editRef of getBase2EditStageRefs()) {
      const editStage = parseBase2EditStageIndex(editRef);
      options.push({
        value: editRef,
        label: `Base2Edit Edit ${editStage} Output`
      });
    }
    const selected = `${currentValue || ""}`.trim();
    if (selected && !options.some((option) => option.value === selected)) {
      const isBase2Edit = parseBase2EditStageIndex(selected) != null;
      options.unshift({
        value: selected,
        label: isBase2Edit ? `Missing Base2Edit ${selected}` : selected,
        disabled: isBase2Edit
      });
    }
    return options;
  };
  var resolveImageSourceValue = (currentValue, options) => {
    const desired = `${currentValue || ""}`;
    if (options.some((option) => option.value === desired)) {
      return desired;
    }
    return REF_SOURCE_REFINER;
  };

  // frontend/timelineReferencesTrack.ts
  var THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
  var LANE_SELECTOR2 = ".vst-refs-lane[data-vst-ref-add]";
  var EDITING_CLASS2 = "vst-refs-editing";
  var DRAGGING_CLASS3 = "vst-refs-dragging";
  var DRAG_THRESHOLD_PX3 = 5;
  var currentFps2 = () => {
    try {
      const fps = getRootDefaults().fps;
      return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
      return 24;
    }
  };
  var parseIntAttr2 = (el, name) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
      return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
  };
  var draftFromRef = (clip, refIdx) => {
    const ref = clip.refs?.[refIdx];
    if (!ref) {
      return null;
    }
    return {
      source: `${ref.source ?? ""}`,
      frame: ref.frame,
      fromEnd: ref.fromEnd === true,
      uploadFileName: ref.uploadFileName ?? null,
      uploadedImage: ref.uploadedImage ?? null
    };
  };
  var createTimelineReferencesTrack = () => {
    let boundBody = null;
    let activeWrap = null;
    let editingAnchor = null;
    let outsideMouseHandler = null;
    let suppressClick = false;
    let refDrag = null;
    const findArrow = (clipIdx, refIdx) => boundBody?.querySelector(
      `.vst-region[data-clip-idx="${clipIdx}"] .vst-key[data-ref-idx="${refIdx}"]`
    ) ?? null;
    const positionRefMarker = (mark, arrow, frame, fromEnd, durationSeconds, fps) => {
      const time = keyframeTimeSeconds(frame, fromEnd, durationSeconds, fps);
      const leftPct = `${keyframeLeftPercent(time, durationSeconds)}%`;
      mark.style.left = leftPct;
      if (arrow) {
        arrow.style.left = leftPct;
      }
      const ph = mark.querySelector(".vst-refs-ph");
      if (ph) {
        ph.textContent = `R ${fromEnd ? "-" : ""}${frame}`;
      }
    };
    const isStale = (sourceJson) => readVideoStagesSection() !== sourceJson;
    const closeEditor = () => {
      if (outsideMouseHandler) {
        document.removeEventListener(
          "mousedown",
          outsideMouseHandler,
          true
        );
        outsideMouseHandler = null;
      }
      if (editingAnchor) {
        editingAnchor.classList.remove(EDITING_CLASS2);
        editingAnchor = null;
      }
      if (activeWrap) {
        activeWrap.remove();
        activeWrap = null;
      }
    };
    const commit = (clipIdx, refIdx, draft) => {
      const clips = getClips();
      const clip = clips[clipIdx];
      const ref = clip?.refs?.[refIdx];
      if (!clip || !ref) {
        return;
      }
      const source = resolveImageSourceValue(
        draft.source,
        buildImageSourceOptions(draft.source)
      );
      const frameMax = getReferenceFrameMax(getRootDefaults, clip);
      ref.source = source;
      ref.frame = clamp(Math.round(draft.frame), REF_FRAME_MIN, frameMax);
      ref.fromEnd = draft.fromEnd;
      if (source === REF_SOURCE_UPLOAD) {
        ref.uploadedImage = draft.uploadedImage;
        ref.uploadFileName = draft.uploadedImage?.fileName ?? draft.uploadFileName;
      } else {
        ref.uploadedImage = null;
        ref.uploadFileName = null;
      }
      saveClips(clips);
    };
    const addRefAtFrame = (clipIdx, frame, sourceJson) => {
      if (isStale(sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[clipIdx];
      if (!clip) {
        return;
      }
      const frameMax = getReferenceFrameMax(getRootDefaults, clip);
      const ref = buildDefaultRef();
      ref.frame = clamp(Math.round(frame), REF_FRAME_MIN, frameMax);
      appendRefToClip(clip, ref);
      saveClips(clips);
    };
    const deleteRef = (clipIdx, refIdx, sourceJson) => {
      if (isStale(sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[clipIdx];
      if (!clip || !removeRefAt(clip, refIdx)) {
        return;
      }
      saveClips(clips);
    };
    const buildField = (label, control) => {
      const row = document.createElement("div");
      row.className = "vst-audio-field";
      const text = document.createElement("span");
      text.className = "vst-audio-field-label";
      text.textContent = label;
      row.append(text, control);
      return row;
    };
    const buildCheckbox = (label, checked, onChange) => {
      const row = document.createElement("label");
      row.className = "vst-audio-field vst-audio-field-check";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = checked;
      input.addEventListener("change", () => onChange(input.checked));
      const text = document.createElement("span");
      text.className = "vst-audio-field-label";
      text.textContent = label;
      row.append(input, text);
      return { row, input };
    };
    const openEditor = (anchor, clipIdx, refIdx) => {
      closeEditor();
      const clip = getClips()[clipIdx];
      if (!clip) {
        return;
      }
      const draft = draftFromRef(clip, refIdx);
      if (!draft) {
        return;
      }
      const sourceJson = readVideoStagesSection();
      const frameMax = getReferenceFrameMax(getRootDefaults, clip);
      const fps = currentFps2();
      const anchorOriginalLeft = anchor.style.left;
      const arrowOriginalLeft = findArrow(clipIdx, refIdx)?.style.left ?? "";
      const originalLabel = anchor.querySelector(".vst-refs-ph")?.textContent ?? "";
      const previewDraftPosition = () => {
        positionRefMarker(
          anchor,
          findArrow(clipIdx, refIdx),
          draft.frame,
          draft.fromEnd,
          clip.duration,
          fps
        );
      };
      const restorePreview = () => {
        anchor.style.left = anchorOriginalLeft;
        const arrow = findArrow(clipIdx, refIdx);
        if (arrow) {
          arrow.style.left = arrowOriginalLeft;
        }
        const ph = anchor.querySelector(".vst-refs-ph");
        if (ph) {
          ph.textContent = originalLabel;
        }
      };
      const host = boundBody ?? document.body;
      const hostRect = host.getBoundingClientRect();
      const viewportW = window.innerWidth || document.documentElement.clientWidth;
      const width = clamp(Math.round(hostRect.width - 32), 260, 420);
      const left = clamp(
        Math.round(hostRect.left + (hostRect.width - width) / 2),
        8,
        Math.max(8, viewportW - width - 8)
      );
      const wrap = document.createElement("div");
      wrap.className = "vst-prompt-inspector vst-refs-inspector";
      wrap.style.left = `${left}px`;
      wrap.style.top = `${Math.round(Math.max(8, hostRect.top + 46))}px`;
      wrap.style.width = `${width}px`;
      const head = document.createElement("div");
      head.className = "vst-prompt-inspector-head";
      head.textContent = `Clip ${clipIdx} · reference ${refIdx}`;
      const select = document.createElement("select");
      select.className = "vst-audio-select";
      const rebuildOptions = () => {
        const options = buildImageSourceOptions(draft.source);
        draft.source = resolveImageSourceValue(draft.source, options);
        select.innerHTML = "";
        for (const option of options) {
          const elem = document.createElement("option");
          elem.value = option.value;
          elem.textContent = option.label;
          elem.dataset.cleanname = option.label;
          elem.disabled = option.disabled === true;
          elem.selected = option.value === draft.source;
          select.appendChild(elem);
        }
      };
      rebuildOptions();
      const sourceField = buildField("Image Source", select);
      const preview = document.createElement("div");
      preview.className = "vst-refs-thumb-preview";
      const frameInput = document.createElement("input");
      frameInput.type = "number";
      frameInput.className = "vst-refs-num";
      frameInput.min = `${REF_FRAME_MIN}`;
      frameInput.max = `${frameMax}`;
      frameInput.step = "1";
      frameInput.value = `${draft.frame}`;
      const applyFrameInput = (normalize) => {
        const parsed = Number.parseInt(frameInput.value, 10);
        draft.frame = clamp(
          Number.isFinite(parsed) ? parsed : draft.frame,
          REF_FRAME_MIN,
          frameMax
        );
        if (normalize) {
          frameInput.value = `${draft.frame}`;
        }
        previewDraftPosition();
      };
      frameInput.addEventListener("input", () => applyFrameInput(false));
      frameInput.addEventListener("change", () => applyFrameInput(true));
      const frameField = buildField(
        `Attach at Frame (1–${frameMax})`,
        frameInput
      );
      const fromEnd = buildCheckbox(
        "Count from clip end",
        draft.fromEnd,
        (value) => {
          draft.fromEnd = value;
          previewDraftPosition();
        }
      );
      const uploadRow = document.createElement("div");
      uploadRow.className = "vst-audio-field vst-audio-upload";
      const uploadLabel = document.createElement("span");
      uploadLabel.className = "vst-audio-field-label";
      uploadLabel.textContent = "Image Upload";
      const fileInput = document.createElement("input");
      fileInput.type = "file";
      fileInput.accept = "image/*";
      const fileName = document.createElement("span");
      fileName.className = "vst-audio-upload-name";
      const clearBtn = document.createElement("button");
      clearBtn.type = "button";
      clearBtn.className = "vst-audio-upload-clear";
      clearBtn.textContent = "Clear";
      const renderUploadState = () => {
        const name = draft.uploadedImage?.fileName;
        fileName.textContent = name ? name : "No file chosen";
        clearBtn.hidden = !draft.uploadedImage;
        const data = draft.uploadedImage?.data;
        if (data) {
          preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
          preview.classList.add("vst-refs-thumb-preview-set");
        } else {
          preview.style.backgroundImage = "";
          preview.classList.remove("vst-refs-thumb-preview-set");
        }
      };
      fileInput.addEventListener("change", () => {
        const file = fileInput.files?.[0];
        if (!file) {
          return;
        }
        const reader = new FileReader();
        reader.onload = () => {
          const data = `${reader.result ?? ""}`;
          if (!data) {
            return;
          }
          draft.uploadedImage = { data, fileName: file.name };
          renderUploadState();
        };
        reader.readAsDataURL(file);
      });
      clearBtn.addEventListener("click", () => {
        draft.uploadedImage = null;
        draft.uploadFileName = null;
        fileInput.value = "";
        renderUploadState();
      });
      uploadRow.append(uploadLabel, fileInput, fileName, clearBtn);
      const deleteBtn = document.createElement("button");
      deleteBtn.type = "button";
      deleteBtn.className = "vst-refs-delete";
      deleteBtn.textContent = "Delete reference";
      const hint = document.createElement("div");
      hint.className = "vst-prompt-inspector-hint";
      hint.textContent = "Click away to apply · Esc to cancel";
      wrap.append(
        head,
        sourceField,
        preview,
        frameField,
        fromEnd.row,
        uploadRow,
        deleteBtn,
        hint
      );
      const syncVisibility = () => {
        const isUpload = draft.source === REF_SOURCE_UPLOAD;
        uploadRow.hidden = !isUpload;
        preview.hidden = !isUpload;
        renderUploadState();
      };
      select.addEventListener("change", () => {
        draft.source = select.value;
        syncVisibility();
      });
      syncVisibility();
      anchor.classList.add(EDITING_CLASS2);
      editingAnchor = anchor;
      let done = false;
      const finish = (save) => {
        if (done) {
          return;
        }
        done = true;
        const committed = save && !isStale(sourceJson);
        closeEditor();
        if (committed) {
          commit(clipIdx, refIdx, draft);
        } else {
          restorePreview();
        }
      };
      deleteBtn.addEventListener("click", (event) => {
        event.preventDefault();
        if (done) {
          return;
        }
        done = true;
        closeEditor();
        deleteRef(clipIdx, refIdx, sourceJson);
      });
      wrap.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          finish(false);
        } else if (event.key === "Enter" && !(event.target instanceof HTMLSelectElement)) {
          event.preventDefault();
          finish(true);
        }
        event.stopPropagation();
      });
      const onOutside = (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
          return;
        }
        if (target.closest(".vst-refs-inspector") || target.closest(".sui-popover")) {
          return;
        }
        finish(true);
      };
      outsideMouseHandler = onOutside;
      document.addEventListener("mousedown", onOutside, true);
      document.body.appendChild(wrap);
      activeWrap = wrap;
      select.focus();
    };
    const endRefDrag = (restore) => {
      if (refDrag && restore) {
        refDrag.mark.style.left = refDrag.originalLeft;
        if (refDrag.arrow) {
          refDrag.arrow.style.left = refDrag.arrowOriginalLeft;
        }
        const ph = refDrag.mark.querySelector(".vst-refs-ph");
        if (ph) {
          ph.textContent = refDrag.originalLabel;
        }
      }
      refDrag = null;
      (boundBody ?? document.body).classList.remove(DRAGGING_CLASS3);
    };
    const onBodyMouseDown = (event) => {
      const me = event;
      if (me.button !== 0 || !(me.target instanceof Element)) {
        return;
      }
      const mark = me.target.closest(THUMB_SELECTOR);
      if (!(mark instanceof HTMLElement)) {
        return;
      }
      if (me.shiftKey) {
        me.preventDefault();
        return;
      }
      const lane = mark.closest(LANE_SELECTOR2);
      const clipIdx = parseIntAttr2(mark, "data-clip-idx");
      const refIdx = parseIntAttr2(mark, "data-ref-idx");
      if (!(lane instanceof HTMLElement) || clipIdx === null || refIdx === null) {
        return;
      }
      const clip = getClips()[clipIdx];
      const ref = clip?.refs?.[refIdx];
      if (!clip || !ref) {
        return;
      }
      const arrow = findArrow(clipIdx, refIdx);
      refDrag = {
        clipIdx,
        refIdx,
        mark,
        arrow,
        lane,
        startX: me.clientX,
        originalLeft: mark.style.left,
        arrowOriginalLeft: arrow?.style.left ?? "",
        originalLabel: mark.querySelector(".vst-refs-ph")?.textContent ?? "",
        durationSeconds: clip.duration,
        fps: currentFps2(),
        fromEnd: ref.fromEnd === true,
        active: false,
        sourceJson: readVideoStagesSection()
      };
      me.preventDefault();
    };
    const dragFrameAt = (clientX) => {
      if (!refDrag) {
        return REF_FRAME_MIN;
      }
      const rect = refDrag.lane.getBoundingClientRect();
      return pxToFrame(
        clientX - rect.left,
        rect.width,
        refDrag.durationSeconds,
        refDrag.fps,
        refDrag.fromEnd
      );
    };
    const onDocMouseMove = (event) => {
      if (!refDrag) {
        return;
      }
      const me = event;
      if (!refDrag.active) {
        if (Math.abs(me.clientX - refDrag.startX) < DRAG_THRESHOLD_PX3) {
          return;
        }
        refDrag.active = true;
        closeEditor();
        (boundBody ?? document.body).classList.add(DRAGGING_CLASS3);
      }
      positionRefMarker(
        refDrag.mark,
        refDrag.arrow,
        dragFrameAt(me.clientX),
        refDrag.fromEnd,
        refDrag.durationSeconds,
        refDrag.fps
      );
    };
    const onDocMouseUp = (event) => {
      if (!refDrag) {
        return;
      }
      const drag = refDrag;
      const newFrame = dragFrameAt(event.clientX);
      if (!drag.active) {
        endRefDrag(true);
        return;
      }
      suppressClick = true;
      const clips = getClips();
      const ref = clips[drag.clipIdx]?.refs?.[drag.refIdx];
      if (isStale(drag.sourceJson) || !ref || ref.frame === newFrame) {
        endRefDrag(true);
        return;
      }
      endRefDrag(false);
      ref.frame = newFrame;
      saveClips(clips);
    };
    const onDocKeyDown = (event) => {
      if (event.key !== "Escape" || !refDrag) {
        return;
      }
      if (refDrag.active) {
        suppressClick = true;
      }
      endRefDrag(true);
    };
    const onBodyClick = (event) => {
      if (suppressClick) {
        suppressClick = false;
        return;
      }
      if (!(event.target instanceof Element)) {
        return;
      }
      const thumb = event.target.closest(THUMB_SELECTOR);
      if (thumb instanceof HTMLElement) {
        const clipIdx2 = parseIntAttr2(thumb, "data-clip-idx");
        const refIdx = parseIntAttr2(thumb, "data-ref-idx");
        if (clipIdx2 !== null && refIdx !== null) {
          if (event.shiftKey) {
            deleteRef(clipIdx2, refIdx, readVideoStagesSection());
          } else {
            openEditor(thumb, clipIdx2, refIdx);
          }
        }
        return;
      }
      const lane = event.target.closest(LANE_SELECTOR2);
      if (!(lane instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseIntAttr2(lane, "data-clip-idx");
      if (clipIdx === null) {
        return;
      }
      const clip = getClips()[clipIdx];
      if (!clip) {
        return;
      }
      const rect = lane.getBoundingClientRect();
      const frame = pxToFrame(
        event.clientX - rect.left,
        rect.width,
        clip.duration,
        currentFps2(),
        false
      );
      addRefAtFrame(clipIdx, frame, readVideoStagesSection());
    };
    const onBodyKeyDown = (event) => {
      const ke = event;
      if (ke.key !== "Enter" && ke.key !== " ") {
        return;
      }
      if (!(ke.target instanceof Element)) {
        return;
      }
      const thumb = ke.target.closest(THUMB_SELECTOR);
      if (!(thumb instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseIntAttr2(thumb, "data-clip-idx");
      const refIdx = parseIntAttr2(thumb, "data-ref-idx");
      if (clipIdx === null || refIdx === null) {
        return;
      }
      ke.preventDefault();
      openEditor(thumb, clipIdx, refIdx);
    };
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("click", onBodyClick);
      body.addEventListener("keydown", onBodyKeyDown);
      body.addEventListener("mousedown", onBodyMouseDown);
      document.addEventListener("mousemove", onDocMouseMove);
      document.addEventListener("mouseup", onDocMouseUp);
      document.addEventListener("keydown", onDocKeyDown);
    };
    const dispose = () => {
      closeEditor();
      endRefDrag(false);
      if (boundBody) {
        boundBody.removeEventListener("click", onBodyClick);
        boundBody.removeEventListener("keydown", onBodyKeyDown);
        boundBody.removeEventListener("mousedown", onBodyMouseDown);
        boundBody = null;
      }
      document.removeEventListener("mousemove", onDocMouseMove);
      document.removeEventListener("mouseup", onDocMouseUp);
      document.removeEventListener("keydown", onDocKeyDown);
      suppressClick = false;
    };
    return { attach, dispose };
  };

  // frontend/timelineSettings.ts
  var INHERIT_MODE = "inherit";
  var CUSTOM_MODE = "custom";
  var INSPECTOR_SELECTOR = ".vst-settings-inspector";
  var clampDimension = (value) => clamp(
    Math.round(value) || ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MAX
  );
  var clampFps = (value) => clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);
  var inheritedDims = () => {
    const defaults = getRootDefaults();
    return {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
  };
  var createTimelineSettings = (refresh) => {
    let activeWrap = null;
    let editingAnchor = null;
    let outsideMouseHandler = null;
    const close = () => {
      if (outsideMouseHandler) {
        document.removeEventListener(
          "mousedown",
          outsideMouseHandler,
          true
        );
        outsideMouseHandler = null;
      }
      if (editingAnchor) {
        editingAnchor.classList.remove("vst-settings-editing");
        editingAnchor = null;
      }
      if (activeWrap) {
        activeWrap.remove();
        activeWrap = null;
      }
    };
    const buildField = (label, control) => {
      const row = document.createElement("div");
      row.className = "vst-audio-field vst-settings-field";
      const text = document.createElement("span");
      text.className = "vst-audio-field-label";
      text.textContent = label;
      row.append(text, control);
      return row;
    };
    const open = (anchor) => {
      close();
      const state = getState();
      let mode = !state.dimsExplicit ? INHERIT_MODE : DIMENSION_PRESET_KEYS.find((key) => {
        const dims = presetDimensions(key);
        return dims && dims.width === Math.round(state.width) && dims.height === Math.round(state.height);
      }) ?? CUSTOM_MODE;
      let customWidth = clampDimension(state.width);
      let customHeight = clampDimension(state.height);
      let fpsExplicit = state.fpsExplicit;
      let fpsValue = clampFps(state.fps);
      const displayedDims = () => {
        if (mode === CUSTOM_MODE) {
          return { width: customWidth, height: customHeight };
        }
        if (mode === INHERIT_MODE) {
          const core2 = inheritedDims();
          return { width: core2.width, height: core2.height };
        }
        return presetDimensions(mode) ?? {
          width: customWidth,
          height: customHeight
        };
      };
      const commit = () => {
        const next = getState();
        if (mode === INHERIT_MODE) {
          next.dimsExplicit = false;
        } else if (mode === CUSTOM_MODE) {
          next.dimsExplicit = true;
          next.width = clampDimension(customWidth);
          next.height = clampDimension(customHeight);
        } else {
          const dims = presetDimensions(mode);
          if (dims) {
            next.dimsExplicit = true;
            next.width = dims.width;
            next.height = dims.height;
          }
        }
        next.fpsExplicit = fpsExplicit;
        if (fpsExplicit) {
          next.fps = clampFps(fpsValue);
        }
        saveState(next, void 0, {
          notifyDomChange: isVideoStagesEnabled()
        });
        refresh();
      };
      const rect = anchor.getBoundingClientRect();
      const viewportW = window.innerWidth || document.documentElement.clientWidth;
      const width = 300;
      const left = clamp(
        Math.round(rect.left),
        8,
        Math.max(8, viewportW - width - 8)
      );
      const wrap = document.createElement("div");
      wrap.className = "vst-prompt-inspector vst-settings-inspector";
      wrap.style.left = `${left}px`;
      wrap.style.width = `${width}px`;
      const head = document.createElement("div");
      head.className = "vst-prompt-inspector-head";
      head.textContent = "Timeline settings";
      const resSelect = document.createElement("select");
      resSelect.className = "vst-audio-select";
      const core = inheritedDims();
      const inheritOption = document.createElement("option");
      inheritOption.value = INHERIT_MODE;
      inheritOption.textContent = `Use image resolution (${core.width}×${core.height})`;
      resSelect.appendChild(inheritOption);
      for (const key of DIMENSION_PRESET_KEYS) {
        const option = document.createElement("option");
        option.value = key;
        option.textContent = key.replace("x", " × ");
        resSelect.appendChild(option);
      }
      const customOption = document.createElement("option");
      customOption.value = CUSTOM_MODE;
      customOption.textContent = "Custom";
      resSelect.appendChild(customOption);
      resSelect.value = mode;
      const resField = buildField("Resolution", resSelect);
      const widthInput = document.createElement("input");
      widthInput.type = "number";
      widthInput.className = "vst-refs-num vst-settings-num";
      widthInput.min = `${ROOT_DIMENSION_MIN}`;
      widthInput.max = `${ROOT_DIMENSION_MAX}`;
      widthInput.step = `${ROOT_DIMENSION_STEP}`;
      const widthField = buildField("Width", widthInput);
      const heightInput = document.createElement("input");
      heightInput.type = "number";
      heightInput.className = "vst-refs-num vst-settings-num";
      heightInput.min = `${ROOT_DIMENSION_MIN}`;
      heightInput.max = `${ROOT_DIMENSION_MAX}`;
      heightInput.step = `${ROOT_DIMENSION_STEP}`;
      const heightField = buildField("Height", heightInput);
      const badges = document.createElement("div");
      badges.className = "vst-settings-badges";
      const syncResolutionUi = () => {
        const isCustom = mode === CUSTOM_MODE;
        const dims = displayedDims();
        widthInput.value = `${dims.width}`;
        heightInput.value = `${dims.height}`;
        widthInput.disabled = !isCustom;
        heightInput.disabled = !isCustom;
        widthField.classList.toggle("vst-audio-disabled", !isCustom);
        heightField.classList.toggle("vst-audio-disabled", !isCustom);
        badges.replaceChildren();
        if (mode !== CUSTOM_MODE && mode !== INHERIT_MODE) {
          const els = presetBadgeElements(mode);
          if (els.length > 0) {
            badges.append(...els);
          }
        }
        badges.hidden = badges.childElementCount === 0;
      };
      resSelect.addEventListener("change", () => {
        const previousDims = displayedDims();
        mode = resSelect.value;
        if (mode === CUSTOM_MODE) {
          customWidth = clampDimension(previousDims.width);
          customHeight = clampDimension(previousDims.height);
        }
        syncResolutionUi();
        commit();
      });
      widthInput.addEventListener("input", () => {
        customWidth = clampDimension(Number(widthInput.value));
        commit();
      });
      heightInput.addEventListener("input", () => {
        customHeight = clampDimension(Number(heightInput.value));
        commit();
      });
      const fpsRow = document.createElement("label");
      fpsRow.className = "vst-audio-field vst-audio-field-check";
      const fpsCheck = document.createElement("input");
      fpsCheck.type = "checkbox";
      fpsCheck.checked = fpsExplicit;
      const fpsCheckLabel = document.createElement("span");
      fpsCheckLabel.className = "vst-audio-field-label";
      fpsCheckLabel.textContent = "Custom FPS";
      fpsRow.append(fpsCheck, fpsCheckLabel);
      const fpsInput = document.createElement("input");
      fpsInput.type = "number";
      fpsInput.className = "vst-refs-num vst-settings-num";
      fpsInput.min = `${ROOT_FPS_MIN}`;
      fpsInput.max = `${ROOT_FPS_MAX}`;
      fpsInput.step = "1";
      const fpsField = buildField("FPS", fpsInput);
      const syncFpsUi = () => {
        fpsInput.value = `${fpsExplicit ? fpsValue : inheritedDims().fps}`;
        fpsInput.disabled = !fpsExplicit;
        fpsField.classList.toggle("vst-audio-disabled", !fpsExplicit);
      };
      fpsCheck.addEventListener("change", () => {
        fpsExplicit = fpsCheck.checked;
        if (fpsExplicit) {
          fpsValue = clampFps(fpsValue);
        }
        syncFpsUi();
        commit();
      });
      fpsInput.addEventListener("input", () => {
        fpsValue = clampFps(Number(fpsInput.value));
        commit();
      });
      const hint = document.createElement("div");
      hint.className = "vst-prompt-inspector-hint";
      hint.textContent = "Changes apply immediately · Esc to close";
      wrap.append(
        head,
        resField,
        widthField,
        heightField,
        badges,
        fpsRow,
        fpsField,
        hint
      );
      syncResolutionUi();
      syncFpsUi();
      anchor.classList.add("vst-settings-editing");
      editingAnchor = anchor;
      wrap.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          close();
        }
        event.stopPropagation();
      });
      const onOutside = (event) => {
        const target = event.target;
        if (!(target instanceof Element)) {
          return;
        }
        if (target.closest(INSPECTOR_SELECTOR) || target.closest("[data-vst-settings]") || target.closest(".sui-popover")) {
          return;
        }
        close();
      };
      outsideMouseHandler = onOutside;
      document.addEventListener("mousedown", onOutside, true);
      document.body.appendChild(wrap);
      const viewportH = window.innerHeight || document.documentElement.clientHeight;
      const height = wrap.offsetHeight;
      let top = Math.round(rect.bottom + 6);
      if (top + height > viewportH - 8) {
        top = Math.round(rect.top - 6 - height);
      }
      wrap.style.top = `${clamp(top, 8, Math.max(8, viewportH - height - 8))}px`;
      activeWrap = wrap;
      resSelect.focus();
    };
    const dispose = () => {
      close();
    };
    return { open, close, dispose };
  };

  // frontend/videoStagesTimeline.ts
  var safeStateFps = (fps) => typeof fps === "number" && fps > 0 ? fps : 24;
  var INPUT_SYNC_INTERVAL_MS = 200;
  var videoStagesTimeline = () => {
    let boundInput = null;
    let boundToggle = null;
    let inputSyncInterval = null;
    let lastSeenValue = null;
    let lastDimsSignature = null;
    let unit = "seconds";
    let pxPerSecond = DEFAULT_PX_PER_SECOND;
    const linking = createTimelineLinking();
    const promptTrack = createTimelinePromptTrack();
    const audioTrack = createTimelineAudioTrack();
    const referencesTrack = createTimelineReferencesTrack();
    const settings = createTimelineSettings(() => refresh());
    const history = createTimelineHistory({
      read: () => readVideoStagesSection(),
      write: (value) => writeVideoStagesSection(value)
    });
    const VIEW_STATE_KEY = "videostages.timeline.viewState";
    const loadViewState = () => {
      try {
        const raw = localStorage.getItem(VIEW_STATE_KEY);
        if (!raw) {
          return;
        }
        const parsed = JSON.parse(raw);
        if (typeof parsed.pxPerSecond === "number") {
          pxPerSecond = clampPxPerSecond(parsed.pxPerSecond);
        }
        if (parsed.unit === "frames" || parsed.unit === "seconds") {
          unit = parsed.unit;
        }
      } catch {
      }
    };
    const saveViewState = () => {
      try {
        localStorage.setItem(
          VIEW_STATE_KEY,
          JSON.stringify({ pxPerSecond, unit })
        );
      } catch {
      }
    };
    const toggleUnit = () => {
      unit = unit === "seconds" ? "frames" : "seconds";
      saveViewState();
      refresh();
    };
    const timelineBody = () => document.getElementById(TIMELINE_BODY_ID);
    const scrollEl = () => timelineBody()?.querySelector(".vst-scroll") ?? null;
    const setZoom = (value) => {
      pxPerSecond = clampPxPerSecond(value);
      saveViewState();
      refresh();
    };
    const zoomIn = () => setZoom(pxPerSecond * ZOOM_FACTOR);
    const zoomOut = () => setZoom(pxPerSecond / ZOOM_FACTOR);
    const zoomFit = () => {
      const totalSeconds = getClips().reduce(
        (sum, clip) => sum + Math.max(0, clip.duration || 0),
        0
      );
      const width = scrollEl()?.clientWidth ?? timelineBody()?.clientWidth ?? 0;
      setZoom(
        computeFitPxPerSecond(totalSeconds, width, TRACK_HEADER_W_PX + 24)
      );
    };
    const zoomWheel = (factor, clientX) => {
      const scroll = scrollEl();
      if (!scroll || pxPerSecond <= 0) {
        setZoom(pxPerSecond * factor);
        return;
      }
      const offsetX = clientX - scroll.getBoundingClientRect().left;
      const timeAtPointer = zoomAnchorTime(
        offsetX,
        scroll.scrollLeft,
        pxPerSecond
      );
      setZoom(pxPerSecond * factor);
      const fresh = scrollEl();
      if (fresh) {
        fresh.scrollLeft = zoomAnchorScrollLeft(
          timeAtPointer,
          pxPerSecond,
          offsetX
        );
      }
    };
    const onBodyClickSyncReadout = () => {
      void Promise.resolve().then(() => {
        const body = timelineBody();
        if (!body) {
          return;
        }
        const sel = linking.getSelectedIndex();
        const selEl = body.querySelector(
          "[data-vst-readout-sel]"
        );
        const dotEl = body.querySelector(
          "[data-vst-readout-sel-dot]"
        );
        if (!selEl || !dotEl) {
          return;
        }
        selEl.hidden = sel === null;
        dotEl.hidden = sel === null;
        selEl.textContent = sel === null ? "" : `clip ${sel}`;
      });
    };
    const addClip = () => {
      const clips = getClips();
      clips.push(buildDefaultClip(getRootDefaults, getDefaultStageModel));
      saveClips(clips);
    };
    const refresh = () => {
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (!body) {
        return;
      }
      lastSeenValue = readVideoStagesSection();
      lastDimsSignature = readInheritedDimsSignature();
      history.capture();
      try {
        const state = getState();
        const clips = state.clips;
        renderTimeline(body, clips, {
          fps: safeStateFps(state.fps),
          width: state.width,
          height: state.height,
          dimsExplicit: state.dimsExplicit,
          fpsExplicit: state.fpsExplicit,
          unit,
          pxPerSecond,
          selectedIndex: linking.getSelectedIndex(),
          enabled: isVideoStagesEnabled(),
          onToggleEnabled: setVideoStagesEnabled,
          onOpenSettings: (anchor) => settings.open(anchor),
          onToggleUnit: toggleUnit,
          onAddClip: addClip,
          onZoomIn: zoomIn,
          onZoomOut: zoomOut,
          onZoomFit: zoomFit,
          onZoomSlider: setZoom,
          onZoomWheel: zoomWheel,
          onUndo: () => history.undo(),
          onRedo: () => history.redo(),
          globalPrompt: readGlobalPrompt()
        });
        linking.reapplySelection(body, clips.length);
      } catch (error) {
        console.warn("VideoStages: timeline render failed", error);
      }
    };
    const onInputChanged = () => {
      if (readVideoStagesSection() !== lastSeenValue) {
        refresh();
      }
    };
    const bindInputListener = () => {
      const input = getPromptInput();
      if (!input || input === boundInput) {
        return;
      }
      if (boundInput) {
        boundInput.removeEventListener("input", onInputChanged);
        boundInput.removeEventListener("change", onInputChanged);
      }
      input.addEventListener("input", onInputChanged);
      input.addEventListener("change", onInputChanged);
      boundInput = input;
    };
    const onEnabledToggled = () => {
      refresh();
    };
    const bindToggleListener = () => {
      const toggle = getGroupToggle();
      if (!toggle || toggle === boundToggle) {
        return;
      }
      if (boundToggle) {
        boundToggle.removeEventListener("change", onEnabledToggled);
      }
      toggle.addEventListener("change", onEnabledToggled);
      boundToggle = toggle;
    };
    const startInputSync = () => {
      if (inputSyncInterval) {
        return;
      }
      lastSeenValue = readVideoStagesSection();
      lastDimsSignature = readInheritedDimsSignature();
      inputSyncInterval = setInterval(() => {
        if (readVideoStagesSection() !== lastSeenValue || readInheritedDimsSignature() !== lastDimsSignature) {
          refresh();
        }
      }, INPUT_SYNC_INTERVAL_MS);
    };
    const onKeydown = (event) => {
      if (!(event.ctrlKey || event.metaKey)) {
        return;
      }
      const key = event.key.toLowerCase();
      const isUndo = key === "z" && !event.shiftKey;
      const isRedo = key === "z" && event.shiftKey || key === "y";
      if (!isUndo && !isRedo) {
        return;
      }
      const active = document.activeElement;
      const inTextField = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active instanceof HTMLElement && active.isContentEditable;
      if (inTextField || !isVideoStagesEnabled()) {
        return;
      }
      if (isUndo ? history.undo() : history.redo()) {
        event.preventDefault();
      }
    };
    const init = () => {
      loadViewState();
      injectTimelineTab();
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (body) {
        linking.attach(body);
        promptTrack.attach(body);
        audioTrack.attach(body);
        referencesTrack.attach(body);
        body.removeEventListener("click", onBodyClickSyncReadout);
        body.addEventListener("click", onBodyClickSyncReadout);
      }
      bindInputListener();
      bindToggleListener();
      history.syncBaseline();
      document.removeEventListener("keydown", onKeydown);
      document.addEventListener("keydown", onKeydown);
      startInputSync();
      refresh();
    };
    const dispose = () => {
      if (inputSyncInterval) {
        clearInterval(inputSyncInterval);
        inputSyncInterval = null;
      }
      if (boundInput) {
        boundInput.removeEventListener("input", onInputChanged);
        boundInput.removeEventListener("change", onInputChanged);
        boundInput = null;
      }
      if (boundToggle) {
        boundToggle.removeEventListener("change", onEnabledToggled);
        boundToggle = null;
      }
      linking.dispose();
      promptTrack.dispose();
      audioTrack.dispose();
      referencesTrack.dispose();
      settings.dispose();
      const body = document.getElementById(TIMELINE_BODY_ID);
      body?.removeEventListener("click", onBodyClickSyncReadout);
      document.removeEventListener("keydown", onKeydown);
    };
    return { init, refresh, dispose };
  };

  // frontend/main.ts
  var timeline = videoStagesTimeline();
  var registerVideoStagesPromptPrefix = () => {
    if (typeof promptTabComplete === "undefined") {
      return;
    }
    promptTabComplete.registerPrefix(
      "videostages",
      "Configure all VideoStages settings as one JSON prompt section.",
      () => [
        '\nUse "<videostages>{ ...JSON... }" to configure clips, stages, refs, audio, prompts and loras in one JSON blob.',
        '\nExample: <videostages>{"clips":[{"prompt":"a red fox","stages":[{"model":"...","steps":30}]}]}',
        '\nPer-clip "prompt" and per-clip / per-stage "loras" fold into this JSON — there is no more <videoclip> section.'
      ],
      true
    );
  };
  var initTimeline = () => {
    try {
      timeline.init();
    } catch (error) {
      console.warn("VideoStages: failed to init timeline", error);
    }
  };
  var scheduleTimelineInit = () => {
    if (!Array.isArray(postParamBuildSteps)) {
      setTimeout(scheduleTimelineInit, 200);
      return;
    }
    postParamBuildSteps.push(initTimeline);
  };
  scheduleTimelineInit();
  registerVideoStagesPromptPrefix();
  audioSource();
  injectTimelineTab();
})();
//# sourceMappingURL=video-stages.js.map
