"use strict";
(() => {
  // frontend/host/swarmUiAdapters.ts
  var findHostBottomTabLink = (tabId) => document.querySelector(`a[href="#${tabId}"]`);
  var getHostBottomTabMount = () => {
    const nav = document.getElementById("bottombartabcollection");
    const content = document.getElementById("t2i_bottom_bar_content");
    if (!nav || !content) {
      return null;
    }
    return {
      nav,
      content,
      toolsTabItem: nav.querySelector('a[href="#Tools-Tab"]')?.parentElement ?? null
    };
  };
  var registerBottomTabWithHost = (navLink) => {
    if (typeof genTabLayout === "undefined" || !genTabLayout) {
      return;
    }
    const tab = new MovableGenTab(navLink, genTabLayout);
    genTabLayout.managedTabs.push(tab);
    if (genTabLayout.managedTabContainers.length === 0) {
      return;
    }
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
  };
  var showHostPopover = (id, event) => {
    if (typeof doPopover === "function") {
      doPopover(id, event);
    }
  };
  var renderHostSlider = (spec) => makeSliderInput(
    null,
    spec.id,
    "",
    spec.label,
    "",
    spec.value,
    spec.min,
    spec.max,
    spec.viewMin ?? spec.min,
    spec.viewMax ?? spec.max,
    spec.step,
    false,
    false,
    false
  );
  var enhanceHostPromptEditor = (editor) => {
    if (typeof textPromptAddKeydownHandler === "function") {
      textPromptAddKeydownHandler(editor);
    }
  };
  var hasHostInputBrowser = () => typeof inputBrowserHelper !== "undefined" && Boolean(inputBrowserHelper);
  var openHostInputBrowser = (inputId, browserTypes) => {
    if (typeof inputBrowserHelper !== "undefined" && inputBrowserHelper) {
      inputBrowserHelper.openInputBrowser(inputId, browserTypes);
    }
  };
  var refreshHostParameters = () => {
    if (typeof refreshParameterValues === "function") {
      refreshParameterValues(true);
    }
  };
  var canRequestHostWs = () => typeof makeWSRequest === "function";
  var requestHostWs = (route, payload, onMessage, onError) => {
    if (typeof makeWSRequest !== "function") {
      return;
    }
    makeWSRequest(route, payload, onMessage, 0, onError);
  };

  // frontend/bottomTimelineTab.ts
  var TAB_ID = "VideoStages-Timeline-Tab";
  var TIMELINE_BODY_ID = "videostages-timeline-body";
  var updateTimelineTabIndicator = (enabled) => {
    const navLink = findHostBottomTabLink(TAB_ID);
    if (!navLink) {
      return;
    }
    const mark = navLink.querySelector(".vst-tab-check");
    if (enabled && !mark) {
      const check = document.createElement("span");
      check.className = "vst-tab-check";
      check.setAttribute("aria-hidden", "true");
      check.textContent = "✓";
      navLink.appendChild(check);
      navLink.title = "Video Stages is enabled";
    } else if (!enabled && mark) {
      mark.remove();
      navLink.removeAttribute("title");
    }
  };
  var injectTimelineTab = () => {
    const existing = document.getElementById(TIMELINE_BODY_ID);
    if (existing) {
      return existing;
    }
    const mount = getHostBottomTabMount();
    if (!mount) {
      return null;
    }
    const li = document.createElement("li");
    li.className = "nav-item";
    li.setAttribute("role", "presentation");
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">VideoStages</a>`;
    if (mount.toolsTabItem) {
      mount.nav.insertBefore(li, mount.toolsTabItem);
    } else {
      mount.nav.appendChild(li);
    }
    const pane = document.createElement("div");
    pane.className = "tab-pane genpage-bottom-tab";
    pane.id = TAB_ID;
    pane.setAttribute("role", "tabpanel");
    const shell = document.createElement("div");
    shell.className = "vst-timeline";
    const body = document.createElement("div");
    body.className = "vst-right";
    body.id = TIMELINE_BODY_ID;
    shell.appendChild(body);
    pane.appendChild(shell);
    mount.content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
      registerBottomTabWithHost(navLink);
    }
    return body;
  };

  // frontend/host/defaultVideoStagesHostBridge.ts
  var textInput = (id) => {
    const element = document.getElementById(id);
    return element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement ? element : null;
  };
  var input = (id) => {
    const element = document.getElementById(id);
    return element instanceof HTMLInputElement ? element : null;
  };
  var select = (id) => {
    const element = document.getElementById(id);
    return element instanceof HTMLSelectElement ? element : null;
  };
  var selectOptions = (element) => ({
    values: element ? Array.from(element.options, (option) => option.value) : [],
    labels: element ? Array.from(element.options, (option) => option.label) : []
  });
  var registrySnapshot = (registry) => registry?.getSnapshot?.() ?? null;
  var withSuppressedPromptCompletion = (fn) => {
    const completion = typeof promptTabComplete !== "undefined" ? promptTabComplete : null;
    if (!completion) {
      fn();
      return;
    }
    const previous = completion.blockInput;
    completion.blockInput = true;
    try {
      fn();
    } finally {
      completion.blockInput = previous;
    }
  };
  var createDefaultVideoStagesHostBridge = () => ({
    hasElement: (id) => document.getElementById(id) !== null,
    getTextInput: textInput,
    getInput: input,
    getRootVideoFpsInput: () => input("input_videofps") ?? input("input_videoframespersecond"),
    getSelect: select,
    getSelectOptions: selectOptions,
    getParamOptions: (paramId) => {
      if (typeof getParamById !== "function") {
        return null;
      }
      const param = getParamById(paramId);
      if (!Array.isArray(param?.values) || param.values.length === 0) {
        return null;
      }
      const labels = Array.isArray(param.value_names) && param.value_names.length === param.values.length ? [...param.value_names] : [...param.values];
      return { values: [...param.values], labels };
    },
    notifyChanged: (element, suppressPromptCompletion = false) => {
      const notify2 = () => triggerChangeFor(element);
      if (suppressPromptCompletion) {
        withSuppressedPromptCompletion(notify2);
      } else {
        notify2();
      }
    },
    getBase2EditRegistry: () => registrySnapshot(window.base2editStageRegistry),
    getAceStepFunRegistry: () => registrySnapshot(window.acestepfunTrackRegistry),
    getModelCompatId: (modelName) => {
      if (typeof modelsHelpers === "undefined" || !modelsHelpers || typeof modelsHelpers.getDataFor !== "function") {
        return null;
      }
      const id = modelsHelpers.getDataFor("Stable-Diffusion", modelName)?.modelClass?.compatClass?.id;
      return typeof id === "string" && id.trim() ? id : null;
    },
    getModelClassId: (modelName) => {
      if (typeof modelsHelpers === "undefined" || !modelsHelpers || typeof modelsHelpers.getDataFor !== "function") {
        return null;
      }
      const id = modelsHelpers.getDataFor("Stable-Diffusion", modelName)?.modelClass?.id;
      return typeof id === "string" && id.trim() ? id : null;
    },
    getLoraDefaultWeight: (modelName) => {
      const browserModels = typeof sdLoraBrowser !== "undefined" ? sdLoraBrowser?.models : void 0;
      const browserModel = browserModels?.[modelName] ?? browserModels?.[`${modelName}.safetensors`];
      const browserRaw = browserModel?.data?.lora_default_weight;
      const helperRaw = typeof modelsHelpers !== "undefined" && modelsHelpers && typeof modelsHelpers.getDataFor === "function" ? modelsHelpers.getDataFor("LoRA", modelName)?.lora_default_weight : void 0;
      const preferenceRaw = typeof loraHelper !== "undefined" ? loraHelper?.loraWeightPref?.[modelName] : void 0;
      const finiteWeight = (raw) => {
        const value = typeof raw === "number" ? raw : typeof raw === "string" && raw.trim() ? Number(raw.trim()) : Number.NaN;
        return Number.isFinite(value) ? value : null;
      };
      return finiteWeight(browserRaw) ?? finiteWeight(helperRaw) ?? finiteWeight(preferenceRaw);
    },
    getCurrentModelCompatId: () => {
      if (typeof currentModelHelper === "undefined" || !currentModelHelper?.curCompatClass || typeof modelsHelpers === "undefined" || !modelsHelpers?.compatClasses) {
        return null;
      }
      const key = currentModelHelper.curCompatClass;
      const compat = modelsHelpers.compatClasses[key];
      const id = compat?.id ?? key;
      return typeof id === "string" && id.trim() ? id : null;
    },
    requestJson: (url, data = {}) => new Promise((resolve, reject) => {
      if (typeof genericRequest !== "function") {
        reject(new Error("Swarm genericRequest is unavailable."));
        return;
      }
      genericRequest(
        url,
        data,
        (response) => resolve(response),
        0,
        (error) => reject(error)
      );
    }),
    registerPromptPrefix: (prefix, description, examples, isMulti) => {
      if (typeof promptTabComplete === "undefined") {
        return;
      }
      promptTabComplete.registerPrefix(
        prefix,
        description,
        examples,
        isMulti
      );
    },
    addPostParamBuildStep: (step) => {
      if (typeof postParamBuildSteps === "undefined" || !Array.isArray(postParamBuildSteps)) {
        return false;
      }
      postParamBuildSteps.push(step);
      return true;
    },
    addParamRefreshHook: (hook) => {
      if (typeof refreshParamsExtra === "undefined" || !Array.isArray(refreshParamsExtra)) {
        return null;
      }
      refreshParamsExtra.push(hook);
      return () => {
        if (typeof refreshParamsExtra === "undefined" || !Array.isArray(refreshParamsExtra)) {
          return;
        }
        const index = refreshParamsExtra.indexOf(hook);
        if (index >= 0) {
          refreshParamsExtra.splice(index, 1);
        }
      };
    },
    getMediaOutputPrefix: () => typeof getImageOutPrefix === "function" ? getImageOutPrefix() : "",
    createSourceVideoElement: () => document.createElement("video"),
    enableSliders: (element) => {
      if (typeof enableSlidersIn === "function") {
        enableSlidersIn(element);
      }
    },
    registerRefineVideoButton: (onSelect, description) => {
      if (typeof registerMediaButton !== "function") {
        return;
      }
      registerMediaButton(
        "Refine Video",
        onSelect,
        description,
        ["video"],
        true
      );
    },
    getCurrentMediaMetadata: () => typeof currentMetadataVal === "string" ? currentMetadataVal : null,
    interpretMediaMetadata: (metadata) => typeof interpretMetadata === "function" ? interpretMetadata(metadata) : metadata,
    showError: (message) => {
      if (typeof showError === "function") {
        showError(message);
      }
    },
    toDataUrl: (src) => new Promise((resolve) => {
      if (typeof toDataURL !== "function") {
        resolve(src);
        return;
      }
      toDataURL(src, resolve);
    }),
    generate: (inputOverrides) => {
      if (typeof mainGenHandler !== "undefined" && typeof mainGenHandler?.doGenerate === "function") {
        mainGenHandler.doGenerate(inputOverrides, {});
      }
    }
  });

  // frontend/host/index.ts
  var bridge = createDefaultVideoStagesHostBridge();
  var getVideoStagesHostBridge = () => bridge;

  // frontend/constants.ts
  var REF_FRAME_MIN = 1;
  var DEFAULT_CLIP_DURATION_SECONDS = 5;
  var CLIP_DURATION_MIN = 1;
  var CLIP_DURATION_MAX = 9999;
  var PROMPT_WINDOW_MIN_DURATION = 0.25;
  var PROMPT_WINDOW_DEFAULT_DURATION = 3;
  var RETAKE_MIN_DURATION = 0.1;
  var RETAKE_DEFAULT_DURATION = 3;
  var RETAKE_DURATION_STEP = 0.1;
  var RETAKE_STRENGTH_MIN = 0;
  var RETAKE_STRENGTH_MAX = 1;
  var RETAKE_STRENGTH_STEP = 0.05;
  var RETAKE_STRENGTH_DEFAULT = 1;
  var AUDIO_SEGMENT_MIN_LENGTH = 0.1;
  var AUDIO_SEGMENT_DEFAULT_LENGTH = 2;
  var AUDIO_SEGMENT_STEP = 0.1;
  var AUDIO_SEGMENT_VOLUME_MIN = 1e-5;
  var AUDIO_SEGMENT_VOLUME_MAX = 1e5;
  var AUDIO_SEGMENT_VOLUME_SLIDER_MIN = 0.1;
  var AUDIO_SEGMENT_VOLUME_SLIDER_MAX = 4;
  var AUDIO_SEGMENT_VOLUME_SLIDER_STEP = 0.1;
  var AUDIO_SEGMENT_VOLUME_DEFAULT = 1;
  var ROOT_DIMENSION_MIN = 256;
  var ROOT_DIMENSION_MAX = 4096;
  var ROOT_DIMENSION_STEP = 32;
  var ROOT_FPS_MIN = 1;
  var ROOT_FPS_MAX = 120;
  var STAGE_REF_STRENGTH_MIN = 0;
  var STAGE_REF_STRENGTH_MAX = 1;
  var STAGE_REF_STRENGTH_STEP = 0.1;
  var STAGE_REF_STRENGTH_DEFAULT = 0.8;
  var IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;
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
    const prefix = getVideoStagesHostBridge().getMediaOutputPrefix();
    return `${prefix}/${value}`;
  };

  // frontend/utils.ts
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var toNumber = (value, fallback) => {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  };
  var roundToTenth = (seconds) => Math.round(seconds * 10) / 10;
  var gridCeil = (seconds) => Math.ceil(seconds * 10) / 10;
  var gridFloor = (seconds) => Math.floor(seconds * 10) / 10;
  var safeJsonParse = (raw, fallback) => {
    if (raw == null) {
      return fallback;
    }
    try {
      return JSON.parse(raw);
    } catch {
      return fallback;
    }
  };

  // frontend/normalizationShared.ts
  var text = (value, fallback = "") => `${value ?? fallback}`;
  var trimmedText = (value, fallback = "") => text(value, fallback).trim();
  var textOr = (value, fallback) => text(value, fallback) || fallback;
  var numberOr = (value, fallback) => toNumber(text(value, fallback), fallback);
  var clampedNumber = (value, fallback, min, max) => clamp(numberOr(value, fallback), min, max);
  var nonNegativeNumber = (value) => Math.max(0, numberOr(value, 0));
  var optionalNonNegativeNumber = (value) => {
    if (value == null || trimmedText(value) === "") {
      return null;
    }
    const number = toNumber(text(value), Number.NaN);
    return Number.isFinite(number) && number >= 0 ? number : null;
  };
  var optionalPositiveNumber = (value) => {
    const number = optionalNonNegativeNumber(value);
    return number !== null && number > 0 ? number : null;
  };
  var readProp = (raw, ...keys) => {
    for (const key of keys) {
      if (Object.hasOwn(raw, key)) {
        return raw[key];
      }
    }
    return void 0;
  };
  var normalizeOptionalEntityId = (value) => typeof value === "string" ? value.trim() || void 0 : void 0;
  var snapToStep = (value, step) => step > 0 ? Math.round(value / step) * step : value;
  var clampWindowInDuration = (startRaw, lengthRaw, clipDuration, minLength) => {
    if (!(lengthRaw > 0)) {
      return null;
    }
    const maxStart = Math.max(0, clipDuration - minLength);
    const startSeconds = clamp(startRaw, 0, maxStart);
    const lengthSeconds = clamp(
      lengthRaw,
      minLength,
      Math.max(minLength, clipDuration - startSeconds)
    );
    if (!(lengthSeconds > 0)) {
      return null;
    }
    return { startSeconds, lengthSeconds };
  };
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.find(
    (value) => value.trim().toLowerCase().startsWith("latentmodel-")
  ) ?? upscaleMethodValues[0] ?? "";
  var snapValueToStep = (value, fallback, min, max, step) => {
    const unitScale = 1 / step;
    return Math.round(clampedNumber(value, fallback, min, max) * unitScale) / unitScale;
  };

  // frontend/normalizationMedia.ts
  var normalizePromptWindow = (raw) => {
    const duration = numberOr(raw.duration, 0);
    if (!(duration > 0)) {
      return null;
    }
    const start = nonNegativeNumber(raw.start);
    return {
      id: normalizeOptionalEntityId(raw.id),
      prompt: text(raw.prompt),
      start,
      duration
    };
  };
  var normalizePromptWindows = (rawClip) => {
    const rawList = rawClip.promptWindows;
    if (!Array.isArray(rawList)) {
      return [];
    }
    return rawList.map((entry) => normalizePromptWindow(isRecord(entry) ? entry : {})).filter((window2) => window2 !== null).sort((a, b) => a.start - b.start);
  };
  var normalizeRetake = (value, clipDuration) => {
    if (!isRecord(value)) {
      return null;
    }
    const startRaw = nonNegativeNumber(value.startSeconds);
    const lengthRaw = numberOr(value.lengthSeconds, 0);
    const window2 = clampWindowInDuration(
      startRaw,
      lengthRaw,
      clipDuration,
      RETAKE_MIN_DURATION
    );
    if (!window2) {
      return null;
    }
    const strengthRaw = value.strength;
    const strength = strengthRaw == null ? RETAKE_STRENGTH_DEFAULT : clampedNumber(
      strengthRaw,
      RETAKE_STRENGTH_DEFAULT,
      RETAKE_STRENGTH_MIN,
      RETAKE_STRENGTH_MAX
    );
    return {
      id: normalizeOptionalEntityId(value.id),
      startSeconds: roundToTenth(window2.startSeconds),
      lengthSeconds: roundToTenth(window2.lengthSeconds),
      strength
    };
  };
  var normalizeSourceVideo = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const data = trimmedText(value.data);
    if (!data) {
      return null;
    }
    const durationSeconds = nonNegativeNumber(value.durationSeconds);
    let startSeconds = nonNegativeNumber(value.startSeconds);
    let lengthSeconds = nonNegativeNumber(value.lengthSeconds);
    if (durationSeconds > 0) {
      startSeconds = Math.min(
        startSeconds,
        Math.max(0, durationSeconds - CLIP_DURATION_MIN)
      );
      if (!(lengthSeconds > 0)) {
        lengthSeconds = durationSeconds - startSeconds;
      }
      lengthSeconds = Math.min(lengthSeconds, durationSeconds - startSeconds);
    }
    if (!(lengthSeconds > 0)) {
      return null;
    }
    return {
      data,
      fileName: normalizeUploadFileName(
        value.fileName == null ? null : text(value.fileName)
      ),
      fps: nonNegativeNumber(value.fps),
      durationSeconds: roundToTenth(durationSeconds),
      startSeconds: roundToTenth(startSeconds),
      lengthSeconds: roundToTenth(lengthSeconds)
    };
  };
  var normalizeUploadedMedia = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const data = trimmedText(value.data);
    if (!data) {
      return null;
    }
    return {
      data,
      fileName: normalizeUploadFileName(
        value.fileName == null ? null : text(value.fileName)
      )
    };
  };

  // frontend/normalizationAudio.ts
  var normalizeAudioTrackSourceKind = (value) => {
    const compact = trimmedText(value).toLowerCase();
    switch (compact) {
      case "upload":
        return "Upload";
      case "acestepfun":
        return "AceStepFun";
      case "native":
        return "Native";
      case "controlnet":
        return "ControlNet";
      default:
        return "External";
    }
  };
  var normalizeAudioTrackSpan = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const sourceStart = optionalNonNegativeNumber(value.sourceStartSeconds) ?? 0;
    return {
      id: normalizeOptionalEntityId(value.id),
      timelineStartSeconds: optionalNonNegativeNumber(
        value.timelineStartSeconds
      ),
      timelineLengthSeconds: optionalPositiveNumber(
        value.timelineLengthSeconds
      ),
      sourceStartSeconds: sourceStart
    };
  };
  var splitSpansIntoLanes = (track) => {
    if (track.spans.length <= 1) {
      return [track];
    }
    return track.spans.map((span, spanIndex) => ({
      ...track,
      id: track.id === void 0 ? void 0 : `${track.id}:${spanIndex}`,
      source: { ...track.source },
      spans: [span]
    }));
  };
  var normalizeAudioTracks = (value) => {
    if (!Array.isArray(value)) {
      return [];
    }
    const tracks = [];
    for (const rawTrack of value) {
      if (!isRecord(rawTrack)) {
        continue;
      }
      const rawSource = rawTrack.source;
      const source = isRecord(rawSource) ? rawSource : {};
      const rawSpans = rawTrack.spans;
      const volume = rawTrack.volume === void 0 ? void 0 : clampedNumber(
        rawTrack.volume,
        AUDIO_SEGMENT_VOLUME_DEFAULT,
        AUDIO_SEGMENT_VOLUME_MIN,
        AUDIO_SEGMENT_VOLUME_MAX
      );
      tracks.push(
        ...splitSpansIntoLanes({
          id: normalizeOptionalEntityId(rawTrack.id),
          source: {
            kind: normalizeAudioTrackSourceKind(source.kind),
            reference: trimmedText(source.reference),
            uploadedAudio: normalizeUploadedMedia(source.uploadedAudio)
          },
          spans: Array.isArray(rawSpans) ? rawSpans.map(normalizeAudioTrackSpan).filter(
            (span) => span !== null
          ) : [],
          ...volume === void 0 ? {} : { volume }
        })
      );
    }
    return tracks;
  };

  // frontend/clipSemantics.ts
  var activeStageCount = (clip) => clip.stages.filter((stage) => !stage.skipped).length;
  var isExecutableClip = (clip) => !clip.skipped && (clip.sourceVideo !== null || activeStageCount(clip) > 0);
  var executableClipIndexes = (clips) => clips.flatMap((clip, index) => isExecutableClip(clip) ? [index] : []);
  var executableBoundaries = (clips) => {
    const indexes = executableClipIndexes(clips);
    const boundaries = [];
    for (let position = 0; position < indexes.length - 1; position++) {
      const leftIdx = indexes[position];
      const rightIdx = indexes[position + 1];
      boundaries.push({
        position,
        leftIdx,
        rightIdx,
        leftId: clips[leftIdx].id,
        rightId: clips[rightIdx].id
      });
    }
    return boundaries;
  };
  var executableBoundaryForLeftClip = (clips, leftClipIdx) => executableBoundaries(clips).find(
    (boundary) => boundary.leftIdx === leftClipIdx
  ) ?? null;

  // frontend/icLoraAuthoring.ts
  var STAGE_CONTROLNET_STRENGTH_MIN = 0;
  var STAGE_CONTROLNET_STRENGTH_MAX = 1;
  var STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
  var STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;
  var IC_LORA_SOURCE_UPLOAD = "Upload";
  var IC_LORA_SOURCE_INCOMING = "Incoming";
  var IC_LORA_STAGE_ALL = -1;
  var IC_LORA_STRENGTH_MIN = 0;
  var IC_LORA_STRENGTH_MAX = 2;
  var IC_LORA_STRENGTH_STEP = 0.05;
  var IC_LORA_STRENGTH_DEFAULT = 1;
  var IC_LORA_ATTENTION_MIN = 0;
  var IC_LORA_ATTENTION_MAX = 1;
  var IC_LORA_ATTENTION_STEP = 0.05;
  var IC_LORA_ATTENTION_DEFAULT = 1;

  // frontend/architectures/ltx2/icLoraDriveAvailability.ts
  var canUseIncomingIcLoraDrive = (entry, clip, clipIdx, clips, generatedEntryMode) => {
    if (entry.driveData === "none" || !isExecutableClip(clip)) {
      return false;
    }
    const acceptedKinds = entry.driveMediaKinds;
    const activeStageIndexes = clip.stages.flatMap(
      (stage, rawIndex) => stage.skipped ? [] : [rawIndex]
    );
    const targetedStages = entry.stage >= 0 ? activeStageIndexes.includes(entry.stage) ? [entry.stage] : [] : activeStageIndexes;
    const hasPreviousClipOutput = clips.slice(0, clipIdx).some(isExecutableClip);
    return targetedStages.length > 0 && targetedStages.every((targetStage) => {
      const activeStageIndex = activeStageIndexes.indexOf(targetStage);
      const incomingKind = activeStageIndex > 0 || clip.sourceVideo ? "video" : hasPreviousClipOutput ? "video" : generatedEntryMode === "image-to-video" ? "image" : null;
      return incomingKind !== null && acceptedKinds.includes(incomingKind);
    });
  };
  var reconcileIncomingIcLoraDrives = (clips, clipIdx, generatedEntryMode) => {
    const clip = clips[clipIdx];
    if (!clip) {
      return false;
    }
    let changed = false;
    for (const entry of clip.icLoras) {
      if (entry.driveSource === IC_LORA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
        entry,
        clip,
        clipIdx,
        clips,
        generatedEntryMode
      )) {
        entry.driveSource = IC_LORA_SOURCE_UPLOAD;
        changed = true;
      }
    }
    return changed;
  };

  // frontend/architectures/ltx2/icLoraPresets.ts
  var IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";
  var DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT = {
    acceptedKinds: ["image", "video"],
    driveData: "visual"
  };
  var LIPDUB_DRIVE_MEDIA_CONTRACT = {
    acceptedKinds: ["audio", "video"],
    driveData: "audio"
  };
  var icLoraDriveMediaContractForData = (driveData) => {
    if (driveData === "audio") {
      return LIPDUB_DRIVE_MEDIA_CONTRACT;
    }
    if (driveData === "visual") {
      return DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
    }
    return { acceptedKinds: [], driveData: "none" };
  };
  var IC_LORA_PRESET_CUSTOM_ID = "custom";
  var HF = "https://huggingface.co";
  var IC_LORA_PRESETS = [
    {
      id: "union-control",
      displayName: "Union Control",
      triggerPhrase: "",
      strength: 1,
      controlType: "depth",
      allowedControlTypes: ["none", "canny", "depth", "normal"],
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control/resolve/main/ltx-2.3-22b-ic-lora-union-control-ref0.5.safetensors`,
      note: "Structural control from depth/canny/normal signals; pick the control type to render. Dims snap to multiples of 64."
    },
    {
      id: "motion-track-control",
      displayName: "Motion Track Control",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors`,
      note: "Feed an LTXVDrawTracks-rendered track video (e.g. saved from the official workflow) — hand-made dot videos don't match the training format. Dims snap to multiples of 64."
    },
    {
      id: "in-outpainting",
      displayName: "In/Outpainting",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting/resolve/main/ltx-2.3-22b-ic-lora-in-outpainting-0.9.safetensors`,
      note: "Feed a pre-masked clip: masked region must be hard #66FF00 green, slightly dilated, losslessly encoded. Kept regions are still re-generated, not composited back."
    },
    {
      id: "ingredients",
      displayName: "Ingredients",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.3-22b-ic-lora-ingredients-0.9.safetensors`,
      note: "Feed the reference sheet as drive media (a still image works). Prompt pattern: '### Reference Sheet Description' per cell, then '### Target Description'."
    },
    {
      id: "lipdub",
      displayName: "LipDub",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-LipDub/resolve/main/ltx-2.3-22b-ic-lora-lipdub-0.9.safetensors`,
      note: "Generates new speech + lips from the prompt's words. The drive source supplies the speaker sample: audio is used directly, and video sources contribute only their audio while their frames are ignored.",
      driveMedia: LIPDUB_DRIVE_MEDIA_CONTRACT
    },
    {
      id: "hdr",
      displayName: "HDR",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      // The repo also ships an auxiliary hdr-scene-emb file; only the LoRA itself is fetched.
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-HDR/resolve/main/ltx-2.3-22b-ic-lora-hdr-0.9.safetensors`,
      hdr: true,
      note: "HDR generation; feed the SDR clip as the drive video. Output is auto-tonemapped to SDR (LogC3 decompressed). Suggested prompt: 'HDR footage'."
    },
    {
      id: "pixel-spatial-upscaler-x2",
      displayName: "Pixel Spatial Upscaler ×2",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors`,
      note: "Apply on a refine stage with Upscale ×2 and source Incoming media. Dims snap to multiples of 64."
    },
    {
      id: "pixel-spatial-upscaler-x4",
      displayName: "Pixel Spatial Upscaler ×4",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors`,
      note: "Apply on a refine stage with Upscale ×4 and source Incoming media. Dims snap to multiples of 128."
    },
    {
      id: "deblur",
      displayName: "Deblur",
      triggerPhrase: "DEBLUR",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Deblur/resolve/main/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors`,
      note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened."
    },
    {
      id: "decompression",
      displayName: "Decompression",
      triggerPhrase: "ENHANCE QUALITY",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Decompression/resolve/main/ltx-2.3-22b-ic-lora-decompression-0.9.safetensors`,
      note: "Removes compression artifacts; feed a low-bitrate clip directly."
    },
    {
      id: "water-simulation",
      displayName: "Water Simulation",
      triggerPhrase: "ADD WATER",
      strength: 1.2,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.3-22b-ic-lora-water-simulation-0.9.safetensors`,
      note: "Sweet spot ~1.2 (1.0 subtle; ≥1.5 warps faces). Feed a dry clip."
    },
    {
      id: "instant-shave",
      displayName: "Instant Shave",
      triggerPhrase: "REMOVEBEARD",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave/resolve/main/ltx-2.3-22b-ic-lora-instant-shave-0.9.safetensors`,
      note: "Feed a bearded clip directly. Lower toward 0.8 if artifacts appear."
    },
    {
      id: "colorization",
      displayName: "Colorization",
      triggerPhrase: "COLORIZE",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Colorization/resolve/main/ltx-2.3-22b-ic-lora-colorization-0.9.safetensors`,
      note: "Feed the grayscale clip; describe the restored colors after the COLORIZE trigger."
    },
    {
      id: "cross-eyed",
      displayName: "Cross-Eyed",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Cross-Eyed/resolve/main/ltx-2.3-22b-ic-lora-cross-eyed-0.9.safetensors`,
      note: "Turns straight eyes inward (convergent strabismus) in close-up portrait clips; describe the effect in the prompt."
    },
    {
      id: "day-to-night",
      displayName: "Day to Night",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.3-22b-ic-lora-day-to-night-0.9.safetensors`,
      note: "Relights a daytime clip to night. Prompt the night look and add 'Only the lighting changes from day to night'. Best at ~4s clips."
    },
    {
      id: "restyle",
      displayName: "ReStyle",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Cseti/LTX2.3-22B_ReStyle_IC-LoRA/resolve/main/852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors`,
      note: "Style transfer over an existing clip; see README for style prompts."
    },
    {
      id: "cameraman",
      displayName: "Cameraman",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2/resolve/main/LTX2.3-22B_IC-LoRA-Cameraman_v2_14000.safetensors`,
      note: "Camera-motion control driven by the reference video's movement."
    },
    {
      id: "crossview-prompt",
      displayName: "CrossView Prompt",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt/resolve/main/LTX2.3-22B_IC-LoRA-CrossView-Prompt_v0.9_13700.safetensors`,
      note: "Re-renders the scene from a prompted new camera viewpoint."
    },
    {
      id: "outpaint",
      displayName: "Outpaint",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint/resolve/main/ltx-2.3-22b-ic-lora-outpaint.safetensors`,
      note: "Extends the frame beyond the source video's borders."
    },
    {
      id: "refocus",
      displayName: "ReFocus",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus/resolve/main/ltx-2.3-22b-ic-lora-refocus.safetensors`,
      note: "Fixes lens blur / refocuses; feed the blurred clip directly."
    },
    {
      id: "vr360-outpaint",
      displayName: "VR 360 Outpaint",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA/resolve/main/360vroutpaint_v2_step09000.safetensors`,
      note: "Outpaints to an equirectangular 360° panorama."
    }
  ];
  var findIcLoraPreset = (id) => {
    const wanted = `${id ?? ""}`.trim();
    if (!wanted || wanted === IC_LORA_PRESET_CUSTOM_ID) {
      return null;
    }
    return IC_LORA_PRESETS.find((preset) => preset.id === wanted) ?? null;
  };
  var icLoraDriveMediaContract = (preset) => preset?.driveMedia ?? DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
  var icLoraAutoModelName = (preset) => {
    const file = preset.weightsUrl.slice(
      preset.weightsUrl.lastIndexOf("/") + 1
    );
    return `${IC_LORA_AUTO_FOLDER}/${file.replace(/\.safetensors$/i, "")}`;
  };
  var icLoraRepoUrl = (preset) => preset.weightsUrl.split("/resolve/")[0];
  var icLoraTriggerHint = (preset) => {
    if (!preset?.triggerPhrase) {
      return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
  };

  // frontend/architectures/ltx2/icLoraNormalization.ts
  var CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3"
  ];
  var normalizeControlNetSource = (value) => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    for (const option of CONTROLNET_SOURCE_OPTIONS) {
      if (option.replace(/\s+/g, "").toLowerCase() === compact) {
        return option;
      }
    }
    return CONTROLNET_SOURCE_OPTIONS[0];
  };
  var defaultIcLora = (overrides = {}) => ({
    lora: "",
    preset: IC_LORA_PRESET_CUSTOM_ID,
    driveSource: IC_LORA_SOURCE_UPLOAD,
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
    hdr: false,
    driveMedia: null,
    ...overrides
  });
  var normalizeControlNetLora = (value) => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
      return "";
    }
    const squeezed = raw.replace(/\s+/g, "").toLowerCase();
    if (squeezed === "(none)") {
      return "";
    }
    return raw;
  };
  var normalizeIcLoraControlType = (value) => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "canny" || raw === "depth" || raw === "normal" ? raw : "none";
  };
  var normalizeIcLoraDriveSource = (value) => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact || compact === "upload") {
      return IC_LORA_SOURCE_UPLOAD;
    }
    if (compact === "incoming" || compact === "stageinput") {
      return IC_LORA_SOURCE_INCOMING;
    }
    return normalizeControlNetSource(value);
  };
  var normalizeIcLoraDriveData = (value) => {
    const compact = `${value ?? ""}`.trim().toLowerCase();
    return compact === "none" || compact === "audio" ? compact : "visual";
  };
  var mediaKindsForData = (driveData) => icLoraDriveMediaContractForData(driveData).acceptedKinds;
  var normalizeIcLoraDriveMediaKinds = (value, driveData) => {
    const allowed = mediaKindsForData(driveData);
    if (!Array.isArray(value)) {
      return [...allowed];
    }
    const kinds = [];
    for (const rawKind of value) {
      const kind = `${rawKind ?? ""}`.trim().toLowerCase();
      if ((kind === "image" || kind === "video" || kind === "audio") && allowed.includes(kind) && !kinds.includes(kind)) {
        kinds.push(kind);
      }
    }
    return kinds;
  };
  var normalizeIcLoraStage = (value, stageCount) => {
    if (value == null || `${value}`.trim() === "") {
      return IC_LORA_STAGE_ALL;
    }
    const stage = Math.trunc(Number(value));
    if (!Number.isFinite(stage) || stage < 0) {
      return IC_LORA_STAGE_ALL;
    }
    return stageCount > 0 && stage >= stageCount ? IC_LORA_STAGE_ALL : stage;
  };
  var normalizeIcLora = (raw, stageCount = 0, _sourcedClip = false) => {
    if (!isRecord(raw)) {
      return null;
    }
    const lora = normalizeControlNetLora(raw.lora);
    if (!lora) {
      return null;
    }
    const preset = `${raw.preset ?? ""}`.trim();
    const normalizedPreset = preset || IC_LORA_PRESET_CUSTOM_ID;
    const driveData = normalizeIcLoraDriveData(raw.driveData);
    const driveMediaKinds = normalizeIcLoraDriveMediaKinds(
      raw.driveMediaKinds,
      driveData
    );
    const normalizedDriveMedia = normalizeUploadedMedia(raw.driveMedia);
    let driveSource = normalizeIcLoraDriveSource(raw.driveSource);
    const driveMedia = driveSource === IC_LORA_SOURCE_UPLOAD && driveData !== "none" && normalizedDriveMedia && driveMediaKinds.some(
      (kind) => normalizedDriveMedia.data.startsWith(`data:${kind}/`)
    ) ? normalizedDriveMedia : null;
    const stage = normalizeIcLoraStage(raw.stage, stageCount);
    if (driveData === "none") {
      driveSource = IC_LORA_SOURCE_UPLOAD;
    }
    return {
      lora,
      preset: normalizedPreset,
      driveSource,
      driveData,
      driveMediaKinds,
      stage,
      strength: snapValueToStep(
        raw.strength,
        IC_LORA_STRENGTH_DEFAULT,
        IC_LORA_STRENGTH_MIN,
        IC_LORA_STRENGTH_MAX,
        IC_LORA_STRENGTH_STEP
      ),
      attentionStrength: snapValueToStep(
        raw.attentionStrength,
        IC_LORA_ATTENTION_DEFAULT,
        IC_LORA_ATTENTION_MIN,
        IC_LORA_ATTENTION_MAX,
        IC_LORA_ATTENTION_STEP
      ),
      controlType: driveData !== "visual" ? "none" : normalizeIcLoraControlType(raw.controlType),
      // Documents authored before the flag existed carry only the preset id; the preset table
      // (not a name match) seeds the intent, so those documents keep working.
      hdr: typeof raw.hdr === "boolean" ? raw.hdr : findIcLoraPreset(normalizedPreset)?.hdr ?? false,
      driveMedia
    };
  };
  var normalizeIcLoras = (rawClip, stageCount = 0, sourcedClip = false) => {
    if (!Array.isArray(rawClip.icLoras)) {
      return [];
    }
    return rawClip.icLoras.map((entry) => normalizeIcLora(entry, stageCount, sourcedClip)).filter((entry) => entry !== null);
  };
  var canonicalizeIcLoraFields = (entry) => {
    if (entry.driveData === "none") {
      entry.driveSource = IC_LORA_SOURCE_UPLOAD;
      entry.driveMedia = null;
    }
    entry.driveMediaKinds = normalizeIcLoraDriveMediaKinds(
      entry.driveMediaKinds,
      entry.driveData
    );
  };
  var isHdrFeature = (entry) => entry.hdr === true;
  var hasSlotSourcedIcLora = (icLoras) => icLoras.some(
    (entry) => entry.driveSource !== IC_LORA_SOURCE_UPLOAD && entry.driveSource !== IC_LORA_SOURCE_INCOMING
  );

  // frontend/architectures/ltx2/behavior.ts
  var ltx2Behavior = {
    normalizeIcLoras,
    canonicalizeIcLoraFields,
    reconcileIncomingIcLoraDrives,
    hasSlotSourcedIcLora,
    isHdrFeature
  };

  // frontend/architectures/ltx2/definition.ts
  var LTX2_ARCHITECTURE_ID = "ltx2";
  var LTX23_MODEL_NAME = /(^|[/\\_. -])ltx(?:[/\\_. -]*v?2)?[/\\_. -]*3($|[/\\_. -])/i;
  var ltx2Architecture = {
    id: LTX2_ARCHITECTURE_ID,
    label: "LTX Video 2.3",
    defaultProfileId: "ltx-2.3",
    capabilities: {
      architecture: [
        "generated-entry",
        "sourced-entry",
        "multi-stage",
        "native-audio",
        "decoded-output"
      ],
      clip: [
        "source-video",
        "prompts",
        "prompt-relay",
        "references",
        "retake",
        "audio-sources",
        "audio-segments"
      ],
      stage: [
        "image-input",
        "video-input",
        "pixel-upscale",
        "model-upscale",
        "latent-upscale",
        "latent-model-upscale",
        "lora",
        "ic-lora",
        "hdr",
        "frame-references"
      ],
      output: ["video", "attached-audio", "standalone-audio"],
      upscaleModes: ["pixel", "model", "latent", "latent-model"],
      entryModes: [
        "text-to-video",
        "image-to-video",
        "source-video",
        "refine-video"
      ],
      audioSourceKinds: ["Native", "Upload", "ControlNet", "AceStepFun"]
    },
    profiles: [
      {
        id: "ltx-2.3",
        label: "LTX Video 2.3",
        capabilities: [
          "sampler-selection",
          "scheduler-selection",
          "dimension-rules",
          "frame-rules",
          "normal-lora"
        ],
        rules: []
      }
    ],
    boundaryRules: {
      cut: {
        support: "supported",
        code: "ltx2.boundary.cut",
        reason: "Decoded LTX clips can be joined with a hard cut.",
        scope: "boundary",
        entityId: null,
        constraints: null
      },
      continue: {
        support: "conditional",
        code: "ltx2.boundary.continue",
        reason: "Continue requires adjacent LTX clips and a compatible generated target.",
        scope: "boundary",
        entityId: null,
        constraints: {
          sameArchitecture: true,
          targetRequiresGeneratedEntry: true,
          targetRequiresStage: true,
          targetDisallowsInitialReference: true,
          frameStep: 8,
          minFrames: 8,
          maxFrames: 48,
          defaultFrames: 8,
          continuityExtraFrames: 1
        }
      },
      crossfade: {
        support: "conditional",
        code: "ltx2.boundary.crossfade",
        reason: "Crossfade currently uses the LTX-owned decoded transition path.",
        scope: "boundary",
        entityId: null,
        constraints: {
          sameArchitecture: true,
          targetRequiresGeneratedEntry: false,
          targetRequiresStage: false,
          targetDisallowsInitialReference: false,
          frameStep: 8,
          minFrames: 8,
          maxFrames: 48,
          defaultFrames: 8,
          continuityExtraFrames: 0
        }
      }
    },
    rules: [
      {
        support: "conditional",
        code: "audio.reuse.requires_three_stages",
        reason: "Audio reuse needs at least three active stages: generate, capture, then reuse.",
        scope: "clip",
        entityId: null,
        constraints: {
          minimumActiveStages: 3,
          failureSeverity: "warning",
          failureEffect: "disable-feature"
        }
      },
      {
        support: "conditional",
        code: "prompt-relay-dynamic-length-unsupported",
        reason: "Prompt relay requires a fixed frame count and cannot be combined with audio-owned or ControlNet-owned clip length.",
        scope: "clip",
        entityId: null,
        constraints: { requiresFixedFrameCount: true }
      },
      {
        support: "conditional",
        code: "retake-frame-references-unsupported",
        reason: "Retake and frame references are mutually exclusive because guide merging would overwrite the retake mask.",
        scope: "stage",
        entityId: null,
        constraints: {
          mutuallyExclusive: ["retake", "frameReferences"]
        }
      },
      {
        support: "conditional",
        code: "retake-source-required",
        reason: "Retake requires a sourced clip or a global Refine Video source.",
        scope: "clip",
        entityId: null,
        constraints: {
          requiresAnyEntryMode: ["source-video", "refine-video"]
        }
      },
      {
        support: "conditional",
        code: "mixed-hdr-timeline-unsupported",
        reason: "HDR IC-LoRA activation must be uniform across the complete timeline.",
        scope: "architecture",
        entityId: null,
        constraints: {
          uniformTimelineFeature: "hdr",
          minimumTimelineClips: 2
        }
      }
    ],
    resolveModelProfile: (model) => {
      const classId = `${model.modelClassId ?? ""}`.trim().toLowerCase();
      if (classId === "lightricks-ltx-video-2-3") {
        return "ltx-2.3";
      }
      return LTX23_MODEL_NAME.test(model.value) ? "ltx-2.3" : null;
    }
  };

  // frontend/architectures/none/definition.ts
  var NONE_ARCHITECTURE_ID = "none";
  var noneArchitecture = {
    id: NONE_ARCHITECTURE_ID,
    label: "Decoded source only",
    defaultProfileId: NONE_ARCHITECTURE_ID,
    capabilities: {
      architecture: ["sourced-entry", "decoded-output"],
      clip: ["source-video", "audio-sources", "audio-segments"],
      stage: [],
      output: ["video", "attached-audio"],
      upscaleModes: [],
      entryModes: ["source-video"],
      audioSourceKinds: ["Disabled", "Upload"]
    },
    profiles: [
      {
        id: NONE_ARCHITECTURE_ID,
        label: "Decoded source only",
        capabilities: [],
        rules: []
      }
    ],
    boundaryRules: {
      cut: {
        support: "supported",
        code: "none.boundary.cut",
        reason: "Decoded sourced clips can be joined with a hard cut.",
        scope: "boundary",
        entityId: null,
        constraints: null
      },
      continue: {
        support: "unsupported",
        code: "none.boundary.continue.unsupported",
        reason: "A sourced-only clip has no architecture stage that can consume continuity.",
        scope: "boundary",
        entityId: null,
        constraints: null
      },
      crossfade: {
        support: "unsupported",
        code: "none.boundary.crossfade.unsupported",
        reason: "Architecture-neutral sourced clips currently support cut joins only.",
        scope: "boundary",
        entityId: null,
        constraints: null
      }
    },
    rules: [],
    resolveModelProfile: () => null
  };

  // frontend/architectures/modules.ts
  var VIDEO_ARCHITECTURE_MODULES = [
    { definition: ltx2Architecture, behavior: ltx2Behavior },
    { definition: noneArchitecture, behavior: null }
  ];

  // frontend/architectures/behaviorRegistry.ts
  var behaviors = new Map(
    VIDEO_ARCHITECTURE_MODULES.flatMap(
      (module) => module.behavior ? [[module.definition.id, module.behavior]] : []
    )
  );
  var architectureBehavior = (architectureId) => behaviors.get(architectureId) ?? null;
  var normalizeArchitectureIcLoras = (architectureId, rawClip, stageCount, sourcedClip, allowPersistedLtxFallback = false) => {
    const behavior = architectureBehavior(architectureId);
    if (behavior) {
      return behavior.normalizeIcLoras(rawClip, stageCount, sourcedClip);
    }
    return allowPersistedLtxFallback && Array.isArray(rawClip.icLoras) && rawClip.icLoras.length > 0 ? ltx2Behavior.normalizeIcLoras(rawClip, stageCount, sourcedClip) : [];
  };
  var canonicalizeArchitectureIcLoraFields = (architectureId, entry) => {
    architectureBehavior(architectureId)?.canonicalizeIcLoraFields(entry);
  };
  var reconcileArchitectureIncomingIcLoraDrives = (clips, generatedEntryMode) => {
    let changed = false;
    clips.forEach((clip, clipIdx) => {
      changed = architectureBehavior(
        clip.architecture
      )?.reconcileIncomingIcLoraDrives(
        clips,
        clipIdx,
        generatedEntryMode
      ) || changed;
    });
    return changed;
  };
  var hasArchitectureSlotSourcedIcLora = (architectureId, entries) => architectureBehavior(architectureId)?.hasSlotSourcedIcLora(entries) ?? false;
  var isArchitectureHdrFeature = (architectureId, entry) => architectureBehavior(architectureId)?.isHdrFeature(entry) ?? false;
  var clipHasActiveHdr = (clip) => clip.icLoras.some(
    (entry) => isArchitectureHdrFeature(clip.architecture, entry) && clip.stages.some(
      (stage, rawStageIdx) => stage.skipped !== true && (entry.stage < 0 || entry.stage === rawStageIdx)
    )
  );

  // frontend/architectures/boundaryConstraints.ts
  var GENERIC_BOUNDARY_CONSTRAINTS = {
    frameStep: 1,
    minFrames: 1,
    maxFrames: Number.MAX_SAFE_INTEGER,
    defaultFrames: 1,
    continuityExtraFrames: 0
  };
  var finitePositive = (value, fallback, allowZero = false) => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && (allowZero ? numeric >= 0 : numeric > 0) ? numeric : fallback;
  };
  var boundaryOverlapConstraints = (rule) => {
    const raw = rule?.constraints;
    const frameStep = finitePositive(
      raw?.frameStep,
      GENERIC_BOUNDARY_CONSTRAINTS.frameStep
    );
    const minFrames = finitePositive(
      raw?.minFrames,
      GENERIC_BOUNDARY_CONSTRAINTS.minFrames
    );
    const maxFrames = Math.max(
      minFrames,
      finitePositive(raw?.maxFrames, GENERIC_BOUNDARY_CONSTRAINTS.maxFrames)
    );
    const authoredDefault = finitePositive(raw?.defaultFrames, minFrames);
    return {
      frameStep,
      minFrames,
      maxFrames,
      defaultFrames: Math.max(
        minFrames,
        Math.min(maxFrames, authoredDefault)
      ),
      continuityExtraFrames: finitePositive(
        raw?.continuityExtraFrames,
        GENERIC_BOUNDARY_CONSTRAINTS.continuityExtraFrames,
        true
      )
    };
  };
  var normalizeBoundaryOverlap = (value, constraints) => {
    const numeric = Math.trunc(Number(value));
    if (!Number.isFinite(numeric) || numeric <= 0) {
      return constraints.defaultFrames;
    }
    const snapped = numeric < constraints.minFrames ? constraints.minFrames : constraints.minFrames + Math.floor(
      (numeric - constraints.minFrames) / constraints.frameStep
    ) * constraints.frameStep;
    return Math.max(
      constraints.minFrames,
      Math.min(constraints.maxFrames, snapped)
    );
  };
  var boundaryOverlapChoices = (constraints) => {
    const choices = [];
    for (let value = constraints.minFrames; value <= constraints.maxFrames; value += constraints.frameStep) {
      choices.push(value);
      if (choices.length >= 100) break;
    }
    return choices;
  };

  // frontend/architectures/registry.ts
  var createArchitectureRegistry = (initial = []) => {
    const byId = /* @__PURE__ */ new Map();
    for (const definition of initial) {
      if (byId.has(definition.id)) {
        throw new Error(`Duplicate video architecture '${definition.id}'.`);
      }
      byId.set(definition.id, definition);
    }
    return {
      definitions: () => [...byId.values()],
      get: (id) => byId.get(id) ?? null,
      resolveModel: (model) => {
        for (const definition of byId.values()) {
          const profileId = definition.resolveModelProfile(model);
          if (profileId) {
            return { definition, profileId };
          }
        }
        return null;
      }
    };
  };
  var videoArchitectureRegistry = createArchitectureRegistry(
    VIDEO_ARCHITECTURE_MODULES.map((module) => module.definition)
  );

  // frontend/architectures/catalogQueries.ts
  var architectureDescriptor = (catalog, architectureId) => (architectureId ? catalog?.architectures.find((entry) => entry.id === architectureId) : null) ?? null;
  var modelCatalogEntry = (catalog, model) => (model ? catalog?.entries.find((entry) => entry.value === model) : null) ?? null;
  var architectureCatalogView = (catalog, architectureId, registry = videoArchitectureRegistry) => {
    const definition = registry.get(architectureId);
    const catalogArchitecture = architectureDescriptor(catalog, architectureId);
    const entries = catalog.entries.filter(
      (entry) => entry.architectureId === architectureId
    );
    return {
      architectureId,
      architectureLabel: catalogArchitecture?.label ?? definition?.label ?? architectureId,
      values: entries.map((entry) => entry.value),
      labels: entries.map((entry) => entry.label)
    };
  };
  var supportedArchitectureCatalog = (catalog) => ({
    architectures: structuredClone(catalog.architectures),
    source: catalog.source,
    entries: catalog.entries.filter((entry) => entry.architectureId !== null)
  });
  var architectureForModel = (catalog, model) => modelCatalogEntry(catalog, model)?.architectureId ?? null;
  var modelProfileForModel = (catalog, model) => modelCatalogEntry(catalog, model)?.modelProfileId ?? null;
  var buildArchitectureRetargetPlan = (catalog, model, registry = videoArchitectureRegistry) => {
    const entry = modelCatalogEntry(catalog, model);
    const architectureId = entry?.architectureId ?? null;
    const descriptor = architectureDescriptor(catalog, architectureId);
    const fallback = architectureId ? registry.get(architectureId) : null;
    const profileId = entry?.modelProfileId ?? null;
    const capabilities = descriptor?.capabilities ?? fallback?.capabilities;
    return architectureId && profileId && capabilities ? {
      architectureId,
      modelProfileId: profileId,
      model,
      capabilities: structuredClone(capabilities)
    } : null;
  };

  // frontend/architectures/catalogWire.ts
  var BOUNDARY_MODES = ["cut", "continue", "crossfade"];
  var isRecord2 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var isTrimmedNonEmpty = (value) => typeof value === "string" && value.length > 0 && value === value.trim();
  var isUniqueStringArray = (value) => Array.isArray(value) && value.every((entry) => isTrimmedNonEmpty(entry)) && new Set(value).size === value.length;
  var isRuleDecision = (value, allowedScopes) => {
    if (!isRecord2(value) || !["supported", "unsupported", "conditional"].includes(
      `${value.support}`
    ) || !isTrimmedNonEmpty(value.code) || !isTrimmedNonEmpty(value.reason) || ![
      "architecture",
      "model-profile",
      "clip",
      "stage",
      "boundary",
      "output"
    ].includes(`${value.scope}`) || value.entityId !== null && !isTrimmedNonEmpty(value.entityId) || value.constraints !== null && !isRecord2(value.constraints)) {
      return false;
    }
    const scope = value.scope;
    if (allowedScopes && !allowedScopes.includes(scope)) {
      return false;
    }
    if (value.support === "conditional" && !isRecord2(value.constraints)) {
      return false;
    }
    if (value.support === "unsupported" && value.constraints !== null) {
      return false;
    }
    return true;
  };
  var isBoundaryRule = (value) => {
    if (!isRuleDecision(value, ["boundary"]) || value.entityId !== null) {
      return false;
    }
    if (value.support !== "conditional") {
      return value.constraints === null;
    }
    if (!isRecord2(value.constraints)) {
      return false;
    }
    const constraints = value.constraints;
    const integers = [
      constraints.frameStep,
      constraints.minFrames,
      constraints.maxFrames,
      constraints.defaultFrames,
      constraints.continuityExtraFrames
    ];
    if (constraints.sameArchitecture !== true || typeof constraints.targetRequiresGeneratedEntry !== "boolean" || typeof constraints.targetRequiresStage !== "boolean" || typeof constraints.targetDisallowsInitialReference !== "boolean" || !integers.every(Number.isInteger)) {
      return false;
    }
    const frameStep = constraints.frameStep;
    const minFrames = constraints.minFrames;
    const maxFrames = constraints.maxFrames;
    const defaultFrames = constraints.defaultFrames;
    const continuityExtraFrames = constraints.continuityExtraFrames;
    return frameStep > 0 && minFrames >= 0 && maxFrames >= minFrames && defaultFrames >= minFrames && defaultFrames <= maxFrames && continuityExtraFrames >= 0 && (defaultFrames - minFrames) % frameStep === 0;
  };
  var isRuleArray = (value, allowedScopes) => Array.isArray(value) && value.every((rule) => isRuleDecision(rule, allowedScopes)) && new Set(value.map((rule) => rule.code)).size === value.length;
  var isProfile = (value) => isRecord2(value) && isTrimmedNonEmpty(value.id) && isTrimmedNonEmpty(value.label) && isUniqueStringArray(value.capabilities) && isRuleArray(value.rules, ["model-profile"]);
  var isCapabilities = (value) => {
    if (!isRecord2(value)) {
      return false;
    }
    return [
      value.architecture,
      value.clip,
      value.stage,
      value.output,
      value.upscaleModes,
      value.entryModes,
      value.audioSourceKinds
    ].every(isUniqueStringArray);
  };
  var hasCompleteBoundaryRules = (value) => {
    if (!isRecord2(value)) {
      return false;
    }
    const keys = Object.keys(value);
    return keys.length === BOUNDARY_MODES.length && BOUNDARY_MODES.every((mode) => isBoundaryRule(value[mode]));
  };
  var parseVideoArchitectureCatalog = (value) => {
    if (!isRecord2(value) || !Array.isArray(value.architectures) || !Array.isArray(value.models)) {
      return null;
    }
    const architectures = [];
    const architectureIds = /* @__PURE__ */ new Set();
    for (const raw of value.architectures) {
      if (!isRecord2(raw) || !isTrimmedNonEmpty(raw.id) || !isTrimmedNonEmpty(raw.label) || !isTrimmedNonEmpty(raw.defaultProfileId) || !isCapabilities(raw.capabilities) || !Array.isArray(raw.profiles) || !raw.profiles.every(isProfile) || !hasCompleteBoundaryRules(raw.boundaryRules) || !isRuleArray(raw.rules, ["architecture", "clip", "stage", "output"])) {
        return null;
      }
      const profileIds = raw.profiles.map((profile) => profile.id);
      const allCodes = [
        ...Object.values(raw.boundaryRules).map((rule) => rule.code),
        ...raw.rules.map((rule) => rule.code),
        ...raw.profiles.flatMap(
          (profile) => profile.rules.map((rule) => rule.code)
        )
      ];
      if (architectureIds.has(raw.id) || new Set(profileIds).size !== profileIds.length || !profileIds.includes(raw.defaultProfileId) || new Set(allCodes).size !== allCodes.length) {
        return null;
      }
      architectureIds.add(raw.id);
      architectures.push({
        id: raw.id,
        label: raw.label,
        defaultProfileId: raw.defaultProfileId,
        capabilities: structuredClone(raw.capabilities),
        profiles: structuredClone(raw.profiles),
        boundaryRules: structuredClone(raw.boundaryRules),
        rules: structuredClone(raw.rules)
      });
    }
    if (architectures.length === 0) {
      return null;
    }
    const modelNames = /* @__PURE__ */ new Set();
    const models = [];
    for (const raw of value.models) {
      if (!isRecord2(raw) || !isTrimmedNonEmpty(raw.modelName) || !isTrimmedNonEmpty(raw.architectureId) || !architectureIds.has(raw.architectureId) || !isTrimmedNonEmpty(raw.modelProfileId) || raw.compatId !== void 0 && raw.compatId !== null && typeof raw.compatId !== "string") {
        return null;
      }
      const descriptor = architectures.find(
        (architecture) => architecture.id === raw.architectureId
      );
      if (modelNames.has(raw.modelName) || !descriptor?.profiles.some(
        (profile) => profile.id === raw.modelProfileId
      )) {
        return null;
      }
      modelNames.add(raw.modelName);
      models.push({
        modelName: raw.modelName,
        architectureId: raw.architectureId,
        modelProfileId: raw.modelProfileId,
        compatId: raw.compatId ?? null
      });
    }
    return { architectures, models };
  };

  // frontend/architectures/catalogRepository.ts
  var ARCHITECTURE_CATALOG_API = "VideoStagesGetArchitectureCatalog";
  var authoritativeCatalog = null;
  var catalogRequest = null;
  var loadAuthoritativeArchitectureCatalog = () => {
    if (authoritativeCatalog) {
      return Promise.resolve(structuredClone(authoritativeCatalog));
    }
    if (catalogRequest) {
      return catalogRequest;
    }
    catalogRequest = getVideoStagesHostBridge().requestJson(ARCHITECTURE_CATALOG_API).then((response) => {
      const parsed = parseVideoArchitectureCatalog(response);
      if (parsed) {
        authoritativeCatalog = parsed;
      }
      return parsed ? structuredClone(parsed) : null;
    }).catch((error) => {
      console.warn(
        "VideoStages: architecture catalog unavailable; using registered frontend bootstrap",
        error
      );
      return null;
    }).finally(() => {
      catalogRequest = null;
    });
    return catalogRequest;
  };
  var invalidateArchitectureCatalog = () => {
    authoritativeCatalog = null;
    catalogRequest = null;
  };
  var bootstrapArchitectures = (registry) => registry.definitions().map(
    ({
      id,
      label,
      defaultProfileId,
      capabilities,
      profiles,
      boundaryRules,
      rules
    }) => ({
      id,
      label,
      defaultProfileId,
      capabilities: structuredClone(capabilities),
      profiles: structuredClone(profiles),
      boundaryRules: structuredClone(boundaryRules),
      rules: structuredClone(rules)
    })
  );
  var buildArchitectureModelCatalog = (values, labels, registry = videoArchitectureRegistry) => {
    const backend = authoritativeCatalog;
    const modelNames = [...values];
    if (backend) {
      const seen = new Set(modelNames);
      for (const model of backend.models) {
        if (!seen.has(model.modelName)) {
          seen.add(model.modelName);
          modelNames.push(model.modelName);
        }
      }
    }
    return {
      architectures: backend?.architectures ?? bootstrapArchitectures(registry),
      source: backend ? "backend" : "bootstrap",
      entries: modelNames.map((value, index) => {
        const backendModel = backend?.models.find(
          (model) => model.modelName === value
        );
        const descriptor = {
          value,
          label: labels[index] ?? value,
          compatId: backendModel?.compatId ?? getVideoStagesHostBridge().getModelCompatId(value),
          modelClassId: getVideoStagesHostBridge().getModelClassId(value)
        };
        const bootstrap = backend ? null : registry.resolveModel(descriptor);
        return {
          ...descriptor,
          architectureId: backendModel?.architectureId ?? bootstrap?.definition.id ?? null,
          modelProfileId: backendModel?.modelProfileId ?? bootstrap?.profileId ?? null
        };
      })
    };
  };

  // frontend/architectures/identity.ts
  var normalizeClipArchitecture = (rawArchitecture, stageZeroModel, catalog) => {
    const persisted = `${rawArchitecture ?? ""}`.trim();
    if (persisted) {
      return persisted;
    }
    const fromCatalog = catalog && stageZeroModel ? architectureForModel(catalog, stageZeroModel) : null;
    if (fromCatalog) {
      return fromCatalog;
    }
    return videoArchitectureRegistry.definitions()[0]?.id ?? "unsupported";
  };

  // frontend/selectOption.ts
  var preserveSelectedOption = (options, selectedValue, position, build) => {
    const value = `${selectedValue || ""}`.trim();
    if (!value || options.some((option2) => option2.value === value)) {
      return;
    }
    const option = build(value);
    if (!option) {
      return;
    }
    if (position === "start") {
      options.unshift(option);
    } else {
      options.push(option);
    }
  };
  var resolveSelectValue = (currentValue, options, fallback) => {
    const desired = `${currentValue || ""}`;
    return options.some((option) => option.value === desired) ? desired : fallback;
  };

  // frontend/audioSource.ts
  var AUDIO_SOURCE_NATIVE = "Native";
  var AUDIO_SOURCE_UPLOAD = "Upload";
  var AUDIO_SOURCE_CONTROLNET = "ControlNet";
  var AUDIO_SOURCE_DISABLED_KIND = "Disabled";
  var ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;
  var isAceStepFunAudioSource = (source) => ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());
  var audioSourceKind = (source) => {
    const normalized = `${source ?? ""}`.trim() || AUDIO_SOURCE_NATIVE;
    return isAceStepFunAudioSource(normalized) ? "AceStepFun" : normalized;
  };
  var isAllowedAudioSource = (allowedKinds, source) => {
    const kind = audioSourceKind(source);
    return allowedKinds.includes(kind) || kind === AUDIO_SOURCE_NATIVE && allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND);
  };
  var defaultAuthoringAudioSource = (allowedKinds) => allowedKinds.includes(AUDIO_SOURCE_NATIVE) || allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND) ? AUDIO_SOURCE_NATIVE : allowedKinds[0] ?? AUDIO_SOURCE_NATIVE;
  var isControlNetAudioSource = (source) => `${source ?? ""}`.trim() === AUDIO_SOURCE_CONTROLNET;
  var canUseClipLengthFromAudio = (source) => {
    const normalized = `${source ?? ""}`.trim();
    return normalized === AUDIO_SOURCE_UPLOAD || isAceStepFunAudioSource(normalized) || isControlNetAudioSource(normalized);
  };
  var getAceStepFunRefs = () => {
    const snapshot = getVideoStagesHostBridge().getAceStepFunRegistry();
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
  var appendAceStepFunRefs = (options) => {
    for (const ref of getAceStepFunRefs()) {
      options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
    }
  };
  var appendMissingSelectedRef = (options, currentValue) => preserveSelectedOption(
    options,
    currentValue,
    "end",
    (value) => isAceStepFunAudioSource(value) ? { value, label: getAceStepFunRefLabel(value) } : null
  );
  var buildSegmentAudioSourceOptions = (currentValue = "") => {
    const options = [
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD }
    ];
    appendAceStepFunRefs(options);
    appendMissingSelectedRef(options, currentValue);
    return options;
  };
  var buildAudioSourceOptions = (currentValue = "", context = {}) => {
    const options = [
      { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD }
    ];
    appendAceStepFunRefs(options);
    if (context.controlNetEnabled) {
      options.push({
        value: AUDIO_SOURCE_CONTROLNET,
        label: AUDIO_SOURCE_CONTROLNET
      });
    }
    if (context.allowedKinds) {
      const allowed = new Set(context.allowedKinds);
      const filtered = options.filter((option) => {
        const kind = audioSourceKind(option.value);
        return allowed.has(kind) || kind === AUDIO_SOURCE_NATIVE && allowed.has(AUDIO_SOURCE_DISABLED_KIND);
      });
      options.length = 0;
      options.push(...filtered);
    }
    appendMissingSelectedRef(options, currentValue);
    preserveSelectedOption(options, currentValue, "start", (value) => ({
      value,
      label: `${value} (unsupported persisted value)`
    }));
    return options;
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

  // frontend/loraAuthoring.ts
  var LORA_WEIGHT_DEFAULT = 1;
  var LORA_WEIGHT_STEP = 0.05;
  var defaultLoraWeight = (defaults, modelName) => {
    const index = defaults.loraValues.indexOf(modelName);
    const value = index >= 0 ? defaults.loraDefaultWeights[index] : null;
    return typeof value === "number" && Number.isFinite(value) ? value : LORA_WEIGHT_DEFAULT;
  };
  var appendLoraToClip = (clip, name, initialWeight) => {
    clip.loras.push({ name });
    for (const stage of clip.stages) {
      stage.loraWeights.push(initialWeight);
    }
  };
  var replaceLoraModelAt = (clip, index, name, initialWeight) => {
    const entry = clip.loras[index];
    if (!entry) {
      return false;
    }
    entry.name = name;
    for (const stage of clip.stages) {
      stage.loraWeights[index] = initialWeight;
    }
    return true;
  };
  var removeLoraAt = (clip, index) => {
    if (index < 0 || index >= clip.loras.length) {
      return false;
    }
    clip.loras.splice(index, 1);
    for (const stage of clip.stages) {
      if (index < stage.loraWeights.length) {
        stage.loraWeights.splice(index, 1);
      }
    }
    return true;
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
  var CURRENT_AUTHORING_SCHEMA_VERSION = 5;
  var REF_SOURCE_BASE = "Base";
  var REF_SOURCE_REFINER = "Refiner";
  var REF_SOURCE_UPLOAD = "Upload";

  // frontend/normalizationStage.ts
  var normalizeStageRefStrengthValue = (value) => snapValueToStep(
    value,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_STEP
  );
  var normalizeStageControlNetStrengthValue = (value) => snapValueToStep(
    value,
    STAGE_CONTROLNET_STRENGTH_DEFAULT,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_STEP
  );
  var normalizeStageIcLoraStrengths = (rawStrengths, icLoraCount, fallbackStrength = 1, perLoraFallbacks = []) => {
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    return Array.from(
      { length: icLoraCount },
      (_, index) => normalizeStageControlNetStrengthValue(
        rawValues[index] ?? perLoraFallbacks[index] ?? fallbackStrength
      )
    );
  };
  var buildDefaultStageRefStrengths = (refCount, defaultStrength = STAGE_REF_STRENGTH_DEFAULT) => Array.from({ length: refCount }, () => defaultStrength);
  var normalizeStageRefStrengths = (rawStrengths, refCount) => {
    const strengths = [];
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    for (let i = 0; i < refCount; i++) {
      strengths.push(normalizeStageRefStrengthValue(rawValues[i]));
    }
    return strengths;
  };
  var readRawStageProp = (raw, key) => readProp(raw, key);
  var readRawStageString = (raw, key) => {
    const value = readRawStageProp(raw, key);
    if (value == null) {
      return void 0;
    }
    return trimmedText(value) || void 0;
  };
  var normalizeStageLoras = (raw) => {
    if (!Array.isArray(raw)) {
      return [];
    }
    const out = [];
    for (const entry of raw) {
      if (!isRecord(entry)) {
        continue;
      }
      const name = trimmedText(readRawStageProp(entry, "name"));
      if (!name) {
        continue;
      }
      out.push({
        name,
        weight: numberOr(readRawStageProp(entry, "weight"), 1)
      });
    }
    return out;
  };
  var buildDefaultStage = (getRootDefaults2, getDefaultStageModel2, previousStage, refCount, initialLoraWeights = [], initialIcLoraStrengths = []) => {
    const defaults = getRootDefaults2();
    const model = previousStage ? previousStage.model : getDefaultStageModel2(defaults.modelValues);
    return {
      skipped: false,
      control: previousStage ? previousStage.control : defaults.control,
      controlNetStrength: previousStage ? previousStage.controlNetStrength : STAGE_CONTROLNET_STRENGTH_DEFAULT,
      icLoraStrengths: previousStage ? [...previousStage.icLoraStrengths] : initialIcLoraStrengths.map(normalizeStageControlNetStrengthValue),
      loraWeights: previousStage ? [...previousStage.loraWeights] : [...initialLoraWeights],
      refStrengths: buildDefaultStageRefStrengths(refCount),
      upscale: previousStage ? previousStage.upscale : defaults.upscale,
      upscaleMethod: previousStage ? previousStage.upscaleMethod : resolveRootPreferredUpscaleMethod(defaults.upscaleMethodValues),
      model,
      modelProfileId: previousStage?.modelProfileId ?? modelProfileForModel(defaults.modelCatalog, model) ?? "unsupported",
      steps: previousStage ? previousStage.steps : defaults.steps,
      cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
      sampler: previousStage ? previousStage.sampler : defaults.samplerValues[0] ?? "euler",
      scheduler: previousStage ? previousStage.scheduler : defaults.schedulerValues[0] ?? "normal"
    };
  };
  var buildDefaultRef = (source = REF_SOURCE_REFINER) => ({
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
  var appendIcLoraStrengthToClip = (clip, initialStrength = 1) => {
    for (const stage of clip.stages) {
      stage.icLoraStrengths.push(
        normalizeStageControlNetStrengthValue(initialStrength)
      );
    }
  };
  var removeIcLoraStrengthAt = (clip, entryIdx) => {
    for (const stage of clip.stages) {
      if (entryIdx < stage.icLoraStrengths.length) {
        stage.icLoraStrengths.splice(entryIdx, 1);
      }
    }
  };
  var getReferenceFrameMax = (getRootDefaults2, clip, effectiveFps) => {
    const defaults = getRootDefaults2();
    const fps = typeof effectiveFps === "number" && Number.isFinite(effectiveFps) && effectiveFps > 0 ? effectiveFps : defaults.fps;
    if (clip) {
      return Math.max(REF_FRAME_MIN, framesForClip(clip.duration, fps));
    }
    return Math.max(REF_FRAME_MIN, defaults.frames);
  };
  var normalizeStage = (getRootDefaults2, getDefaultStageModel2, rawStage, previousStage, refCount, stageIndexInClip, sourcedClip = false, clipLoras = [], clipLoraDefaultWeights = []) => {
    const defaults = getRootDefaults2();
    const fallback = buildDefaultStage(
      getRootDefaults2,
      getDefaultStageModel2,
      previousStage,
      refCount,
      clipLoraDefaultWeights
    );
    const forcedFirstStage = stageIndexInClip === 0 && !sourcedClip;
    let firstStageUpscale;
    let control;
    if (forcedFirstStage) {
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
        upscale: snapToStep(
          clampedNumber(
            readRawStageProp(rawStage, "upscale"),
            fallback.upscale,
            defaults.upscaleMin,
            defaults.upscaleMax
          ),
          defaults.upscaleStep
        ),
        upscaleMethod: textOr(
          readRawStageString(rawStage, "upscaleMethod"),
          fallback.upscaleMethod
        )
      };
      control = clampedNumber(
        readRawStageProp(rawStage, "control"),
        fallback.control,
        defaults.controlMin,
        defaults.controlMax
      );
    }
    const rawLoraWeights = readRawStageProp(rawStage, "loraWeights");
    const legacyLoras = normalizeStageLoras(
      readRawStageProp(rawStage, "loras")
    );
    const legacyWeights = new Map(
      legacyLoras.map((entry) => [entry.name, entry.weight])
    );
    const hasLegacyLoras = Array.isArray(readRawStageProp(rawStage, "loras"));
    const loraWeights = clipLoras.map((entry, index) => {
      if (Array.isArray(rawLoraWeights)) {
        return numberOr(
          rawLoraWeights[index],
          clipLoraDefaultWeights[index] ?? 1
        );
      }
      const legacyWeight = legacyWeights.get(entry.name);
      if (legacyWeight !== void 0) {
        return legacyWeight;
      }
      if (hasLegacyLoras) {
        return clipLoraDefaultWeights[index] ?? 0;
      }
      return fallback.loraWeights[index] ?? clipLoraDefaultWeights[index] ?? 1;
    });
    const stage = {
      id: normalizeOptionalEntityId(rawStage.id),
      skipped: !!rawStage.skipped,
      control,
      controlNetStrength: normalizeStageControlNetStrengthValue(
        readRawStageProp(rawStage, "controlNetStrength") ?? fallback.controlNetStrength
      ),
      icLoraStrengths: Array.isArray(rawStage.icLoraStrengths) ? rawStage.icLoraStrengths.map(
        normalizeStageControlNetStrengthValue
      ) : [...fallback.icLoraStrengths],
      loraWeights,
      refStrengths: normalizeStageRefStrengths(
        rawStage.refStrengths,
        refCount
      ),
      upscale: firstStageUpscale.upscale,
      upscaleMethod: firstStageUpscale.upscaleMethod,
      model: textOr(rawStage.model, fallback.model),
      modelProfileId: "unsupported",
      steps: Math.max(
        1,
        Math.round(
          clampedNumber(
            rawStage.steps,
            fallback.steps,
            defaults.stepsMin,
            defaults.stepsMax
          )
        )
      ),
      cfgScale: clampedNumber(
        rawStage.cfgScale,
        fallback.cfgScale,
        defaults.cfgScaleMin,
        defaults.cfgScaleMax
      ),
      sampler: textOr(rawStage.sampler, fallback.sampler),
      scheduler: textOr(rawStage.scheduler, fallback.scheduler)
    };
    stage.modelProfileId = trimmedText(readRawStageProp(rawStage, "modelProfileId")) || modelProfileForModel(defaults.modelCatalog, stage.model) || fallback.modelProfileId;
    if (!defaults.upscaleMethodValues.includes(stage.upscaleMethod) && defaults.upscaleMethodValues.length > 0) {
      stage.upscaleMethod = forcedFirstStage ? defaults.upscaleMethodValues[0] ?? "" : stage.upscaleMethod || fallback.upscaleMethod;
    }
    return stage;
  };
  var normalizeRef = (rawRef, frameMax) => {
    const fallback = buildDefaultRef();
    const source = textOr(rawRef.source, fallback.source);
    return {
      id: normalizeOptionalEntityId(rawRef.id),
      source,
      uploadFileName: textOr(rawRef.uploadFileName, "") || null,
      uploadedImage: normalizeUploadedMedia(rawRef.uploadedImage),
      frame: Math.max(
        REF_FRAME_MIN,
        Math.round(
          clampedNumber(
            rawRef.frame,
            fallback.frame,
            REF_FRAME_MIN,
            frameMax
          )
        )
      ),
      fromEnd: !!rawRef.fromEnd
    };
  };

  // frontend/normalizationClip.ts
  var normalizeBoundaryOut = (value) => {
    const raw = trimmedText(value).toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
  };
  var normalizeContinueOverlap = (value, constraints = boundaryOverlapConstraints(null)) => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && numeric > 0 ? numeric : normalizeBoundaryOverlap(value, constraints);
  };
  var buildDefaultClip = (getRootDefaults2, getDefaultStageModel2, includeDefaultRef = false, previousClip = null) => {
    const defaults = getRootDefaults2();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    const loras = previousClip?.loras.map((entry) => ({ ...entry })) ?? [];
    const initialLoraWeights = loras.map(
      (entry, index) => previousClip?.stages[0]?.loraWeights[index] ?? defaults.loraDefaultWeights[defaults.loraValues.indexOf(entry.name)] ?? 1
    );
    const firstStage = {
      ...buildDefaultStage(
        getRootDefaults2,
        getDefaultStageModel2,
        previousClip?.stages[0] ?? null,
        refs.length,
        initialLoraWeights
      ),
      refStrengths: buildDefaultStageRefStrengths(
        refs.length,
        includeDefaultRef ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH : STAGE_REF_STRENGTH_DEFAULT
      )
    };
    const architecture = (previousClip?.architecture !== NONE_ARCHITECTURE_ID ? previousClip?.architecture : null) ?? architectureForModel(defaults.modelCatalog, firstStage.model) ?? "unsupported";
    const continueRule = architectureDescriptor(
      defaults.modelCatalog,
      architecture
    )?.boundaryRules.continue;
    return {
      architecture,
      modelProfileId: (previousClip?.architecture !== NONE_ARCHITECTURE_ID ? previousClip?.modelProfileId : null) ?? modelProfileForModel(defaults.modelCatalog, firstStage.model) ?? firstStage.modelProfileId,
      skipped: false,
      hue: UNASSIGNED_HUE,
      boundaryOut: "cut",
      boundaryOutCarryAudio: false,
      boundaryOutOverlap: boundaryOverlapConstraints(continueRule).defaultFrames,
      duration: previousClip ? previousClip.duration : snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
        defaults.fps
      ),
      audioSource: AUDIO_SOURCE_NATIVE,
      loras,
      icLoras: [],
      saveAudioTrack: false,
      clipLengthFromAudio: false,
      clipLengthFromControlNet: false,
      reuseAudio: false,
      uploadedAudio: null,
      prompt: "",
      promptWindows: [],
      retake: null,
      sourceVideo: null,
      refs,
      stages: [firstStage]
    };
  };
  var normalizeClip = (rawClip, getRootDefaults2, getDefaultStageModel2, effectiveFps) => {
    const defaults = getRootDefaults2();
    const rawAudioSource = text(rawClip.audioSource, AUDIO_SOURCE_NATIVE);
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const sourceVideo = normalizeSourceVideo(rawClip.sourceVideo);
    const fps = Math.max(
      1,
      typeof effectiveFps === "number" && Number.isFinite(effectiveFps) && effectiveFps > 0 ? effectiveFps : defaults.fps
    );
    const rawDuration = sourceVideo?.lengthSeconds ?? numberOr(rawClip.duration, defaults.frames / fps);
    const duration = snapDurationToFps(
      Math.max(CLIP_DURATION_MIN, rawDuration),
      fps
    );
    const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
    const refFrameMax = getReferenceFrameMax(
      getRootDefaults2,
      { duration },
      fps
    );
    const refs = refsRaw.map(
      (rawRef) => normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax)
    );
    const clipScopedLoras = normalizeStageLoras(rawClip.loras);
    const loraNames = [];
    const loraDefaultWeightByName = /* @__PURE__ */ new Map();
    const appendLoraName = (name, defaultWeight) => {
      if (loraDefaultWeightByName.has(name)) {
        return;
      }
      loraNames.push(name);
      loraDefaultWeightByName.set(name, defaultWeight);
    };
    for (const entry of clipScopedLoras) {
      appendLoraName(entry.name, entry.weight);
    }
    for (const rawStage of stagesRaw) {
      if (!isRecord(rawStage)) {
        continue;
      }
      for (const entry of normalizeStageLoras(rawStage.loras)) {
        appendLoraName(entry.name, 0);
      }
    }
    const loras = loraNames.map((name) => ({ name }));
    const loraDefaultWeights = loraNames.map(
      (name) => loraDefaultWeightByName.get(name) ?? 1
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
          i,
          sourceVideo !== null,
          loras,
          loraDefaultWeights
        )
      );
    }
    const audioSource = rawAudioSource.trim() || AUDIO_SOURCE_NATIVE;
    const stageZero = stages[0] ?? null;
    const persistedArchitecture = trimmedText(rawClip.architecture);
    const persistedProfile = trimmedText(rawClip.modelProfileId);
    const isSourceOnly = sourceVideo !== null && stages.every((stage) => stage.skipped);
    const architecture = isSourceOnly ? persistedArchitecture || "none" : normalizeClipArchitecture(
      persistedArchitecture,
      stageZero?.model ?? null,
      defaults.modelCatalog
    );
    const modelProfileId = isSourceOnly ? persistedProfile || (architecture === NONE_ARCHITECTURE_ID ? NONE_ARCHITECTURE_ID : "unsupported") : persistedProfile || stageZero?.modelProfileId || "unsupported";
    const icLoras = normalizeArchitectureIcLoras(
      architecture,
      rawClip,
      stagesRaw.length,
      sourceVideo !== null,
      architectureDescriptor(defaults.modelCatalog, architecture) === null || architecture === NONE_ARCHITECTURE_ID
    );
    const icLoraDefaultStrengths = icLoras.map(
      (entry) => defaultLoraWeight(defaults, entry.lora)
    );
    for (let index = 0; index < stages.length; index++) {
      const stage = stages[index];
      const rawStage = isRecord(stagesRaw[index]) ? stagesRaw[index] : {};
      const hasLegacyControlNetStrength = Object.hasOwn(
        rawStage,
        "controlNetStrength"
      );
      const legacyFallback = hasLegacyControlNetStrength ? stage.controlNetStrength : 1;
      stage.icLoraStrengths = normalizeStageIcLoraStrengths(
        rawStage.icLoraStrengths,
        icLoras.length,
        legacyFallback,
        hasLegacyControlNetStrength ? [] : stages[index - 1]?.icLoraStrengths ?? icLoraDefaultStrengths
      );
    }
    const clipLengthFromAudio = !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet = !clipLengthFromAudio && !!rawClip.clipLengthFromControlNet;
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = architectureDescriptor(
      defaults.modelCatalog,
      architecture
    )?.boundaryRules[boundaryOut];
    return {
      id: normalizeOptionalEntityId(rawClip.id),
      architecture,
      modelProfileId,
      skipped: !!rawClip.skipped,
      hue: normalizeStoredHue(rawClip.hue),
      boundaryOut,
      boundaryOutCarryAudio: !!rawClip.boundaryOutCarryAudio,
      boundaryOutOverlap: normalizeContinueOverlap(
        rawClip.boundaryOutOverlap,
        boundaryOverlapConstraints(boundaryRule)
      ),
      duration,
      audioSource,
      loras,
      icLoras,
      saveAudioTrack: !!rawClip.saveAudioTrack,
      clipLengthFromAudio,
      clipLengthFromControlNet,
      reuseAudio: !!rawClip.reuseAudio,
      uploadedAudio: normalizeUploadedMedia(rawClip.uploadedAudio),
      prompt: text(rawClip.prompt),
      promptWindows: normalizePromptWindows(rawClip),
      retake: normalizeRetake(rawClip.retake, duration),
      sourceVideo,
      refs,
      stages
    };
  };

  // frontend/identity.ts
  var fallbackSequence = 0;
  var normalizedExistingId = (value) => {
    if (typeof value !== "string") {
      return null;
    }
    const id = value.trim();
    return id.length > 0 ? id : null;
  };
  var createEntityId = (kind) => {
    const randomUuid = globalThis.crypto?.randomUUID?.();
    if (randomUuid) {
      return `${kind}_${randomUuid}`;
    }
    fallbackSequence += 1;
    return `${kind}_${Date.now().toString(36)}_${fallbackSequence.toString(36)}`;
  };
  var assignUniqueId = (entry, reserved, used) => {
    const reservedId = reserved.get(entry.entity);
    if (reservedId) {
      entry.entity.id = reservedId;
      return reservedId;
    }
    const base = `${entry.kind}_legacy_${entry.repairPath}`;
    let id = base;
    let collision = 1;
    while (used.has(id)) {
      collision += 1;
      id = `${base}_${collision}`;
    }
    entry.entity.id = id;
    used.add(id);
    return id;
  };
  var clipIdentityEntries = (clips) => {
    const entries = [];
    for (let clipIndex = 0; clipIndex < clips.length; clipIndex++) {
      const clip = clips[clipIndex];
      entries.push({
        entity: clip,
        kind: "clip",
        repairPath: `${clipIndex}`
      });
      for (let stageIndex = 0; stageIndex < clip.stages.length; stageIndex++) {
        entries.push({
          entity: clip.stages[stageIndex],
          kind: "stage",
          repairPath: `${clipIndex}_${stageIndex}`
        });
      }
      for (let refIndex = 0; refIndex < clip.refs.length; refIndex++) {
        entries.push({
          entity: clip.refs[refIndex],
          kind: "ref",
          repairPath: `${clipIndex}_${refIndex}`
        });
      }
      for (let windowIndex = 0; windowIndex < clip.promptWindows.length; windowIndex++) {
        entries.push({
          entity: clip.promptWindows[windowIndex],
          kind: "prompt_window",
          repairPath: `${clipIndex}_${windowIndex}`
        });
      }
      if (clip.retake) {
        entries.push({
          entity: clip.retake,
          kind: "retake",
          repairPath: `${clipIndex}`
        });
      }
    }
    return entries;
  };
  var audioTrackIdentityEntries = (tracks) => {
    const entries = [];
    for (let trackIndex = 0; trackIndex < tracks.length; trackIndex++) {
      const track = tracks[trackIndex];
      entries.push({
        entity: track,
        kind: "audio_track",
        repairPath: `${trackIndex}`
      });
      for (let spanIndex = 0; spanIndex < track.spans.length; spanIndex++) {
        entries.push({
          entity: track.spans[spanIndex],
          kind: "audio_span",
          repairPath: `${trackIndex}_${spanIndex}`
        });
      }
    }
    return entries;
  };
  var assignEntryIdentities = (entries, used) => {
    const reserved = /* @__PURE__ */ new Map();
    for (const { entity } of entries) {
      const existing = normalizedExistingId(entity.id);
      if (existing && !used.has(existing)) {
        reserved.set(entity, existing);
        used.add(existing);
      }
    }
    for (const entry of entries) {
      assignUniqueId(entry, reserved, used);
    }
  };
  var ensureClipEntityIdentities = (clips, seen = /* @__PURE__ */ new Set()) => {
    assignEntryIdentities(clipIdentityEntries(clips), seen);
    return seen;
  };
  function ensureAuthoringDocumentIdentity(state) {
    state.schemaVersion = CURRENT_AUTHORING_SCHEMA_VERSION;
    state.audioTracks ??= [];
    assignEntryIdentities(
      [
        ...clipIdentityEntries(state.clips),
        ...audioTrackIdentityEntries(state.audioTracks)
      ],
      /* @__PURE__ */ new Set()
    );
  }
  var collectAuthoringEntityIds = (state) => {
    const ids = [];
    for (const clip of state.clips) {
      if (clip.id) ids.push(clip.id);
      for (const stage of clip.stages) if (stage.id) ids.push(stage.id);
      for (const ref of clip.refs) if (ref.id) ids.push(ref.id);
      for (const window2 of clip.promptWindows) {
        if (window2.id) ids.push(window2.id);
      }
      if (clip.retake?.id) ids.push(clip.retake.id);
    }
    for (const track of state.audioTracks ?? []) {
      if (track.id) ids.push(track.id);
      for (const span of track.spans) if (span.id) ids.push(span.id);
    }
    return ids;
  };

  // frontend/promptSegments.ts
  var BOUNDARY_RE = /<videoclip(?=[>[])|<videostages\[/gi;
  var TAG_RE = /^<videoclip(?:\[([^\]]*)\])?(?::([^>]*))?>$/i;
  var INDEX_RE = /^\d+$/;
  var FLOAT_RE = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;
  var preserved = {
    owned: null,
    clip: null,
    window: null
  };
  var parseWindowValue = (value) => {
    const trimmed = value.trim();
    if (trimmed.length === 0 || trimmed.includes(",")) {
      return null;
    }
    const dash = trimmed.indexOf("-");
    if (dash <= 0 || dash >= trimmed.length - 1) {
      return null;
    }
    const left = trimmed.slice(0, dash).trim();
    const right = trimmed.slice(dash + 1).trim();
    if (!FLOAT_RE.test(left) || !FLOAT_RE.test(right)) {
      return null;
    }
    const start = Math.max(0, Number.parseFloat(left));
    const end = Number.parseFloat(right);
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
      return null;
    }
    return { start, end };
  };
  var classify = (tagRaw) => {
    const match = TAG_RE.exec(tagRaw);
    if (!match) {
      return preserved;
    }
    const bracket = match[1];
    const value = match[2];
    if (bracket === void 0) {
      return preserved;
    }
    const tokens = bracket.split(",").map((token) => token.trim());
    if (tokens.length !== 1 || !INDEX_RE.test(tokens[0])) {
      return preserved;
    }
    const clip = Number.parseInt(tokens[0], 10);
    if (value === void 0) {
      return { owned: "section", clip, window: null };
    }
    const window2 = parseWindowValue(value);
    if (window2) {
      return { owned: "window", clip, window: window2 };
    }
    return preserved;
  };
  var tokenizePrompt = (prompt) => {
    const text2 = prompt ?? "";
    BOUNDARY_RE.lastIndex = 0;
    const starts = [];
    for (let match = BOUNDARY_RE.exec(text2); match !== null; match = BOUNDARY_RE.exec(text2)) {
      starts.push(match.index);
    }
    if (starts.length === 0) {
      return { leading: text2, tags: [] };
    }
    const leading = text2.slice(0, starts[0]);
    const tags = [];
    for (let i = 0; i < starts.length; i++) {
      const start = starts[i];
      const nextStart = i + 1 < starts.length ? starts[i + 1] : text2.length;
      const span = text2.slice(start, nextStart);
      const close = span.indexOf(">");
      const tagRaw = close < 0 ? span : span.slice(0, close + 1);
      const body = close < 0 ? "" : span.slice(close + 1);
      tags.push({ tagRaw, body, ...classify(tagRaw) });
    }
    return { leading, tags };
  };
  var formatSeconds = (value) => Number.parseFloat(value.toFixed(3)).toString();
  var authorClip = (index, clip) => {
    if (!clip) {
      return "";
    }
    const pieces = [];
    const mainPrompt = (clip.prompt ?? "").trim();
    if (mainPrompt) {
      pieces.push(`<videoclip[${index}]>${mainPrompt}`);
    }
    const windows = [...clip.windows ?? []].filter((w) => w.duration > 0).sort((a, b) => a.start - b.start);
    for (const win of windows) {
      const start = Math.max(0, win.start);
      const startText = formatSeconds(start);
      const endText = formatSeconds(start + win.duration);
      const winPrompt = (win.prompt ?? "").trim();
      pieces.push(
        `<videoclip[${index}]:${startText}-${endText}>${winPrompt}`
      );
    }
    return pieces.join("\n");
  };
  var serializeClipPrompts = (prompt, clips) => {
    const { leading, tags } = tokenizePrompt(prompt);
    const blocks = [];
    const leadTrimmed = leading.trimEnd();
    if (leadTrimmed) {
      blocks.push(leadTrimmed);
    }
    const emitted = /* @__PURE__ */ new Set();
    const emitClip = (index) => {
      if (emitted.has(index)) {
        return;
      }
      emitted.add(index);
      const block = authorClip(index, clips[index]);
      if (block) {
        blocks.push(block);
      }
    };
    for (const tag of tags) {
      if (tag.owned !== null) {
        const index = tag.clip ?? -1;
        if (index >= 0 && index < clips.length) {
          emitClip(index);
        }
        continue;
      }
      const raw = (tag.tagRaw + tag.body).trimEnd();
      if (raw) {
        blocks.push(raw);
      }
    }
    for (let index = 0; index < clips.length; index++) {
      emitClip(index);
    }
    return blocks.join("\n");
  };
  var parseClipPrompts = (prompt) => {
    const { tags } = tokenizePrompt(prompt);
    const sections = /* @__PURE__ */ new Map();
    const windows = /* @__PURE__ */ new Map();
    for (const tag of tags) {
      if (tag.owned === "section" && tag.clip !== null) {
        if (!sections.has(tag.clip)) {
          sections.set(tag.clip, tag.body.trim());
        }
      } else if (tag.owned === "window" && tag.clip !== null && tag.window) {
        const list2 = windows.get(tag.clip) ?? [];
        list2.push({
          start: Math.max(0, tag.window.start),
          duration: tag.window.end - tag.window.start,
          prompt: tag.body.trim()
        });
        windows.set(tag.clip, list2);
      }
    }
    for (const list2 of windows.values()) {
      list2.sort((a, b) => a.start - b.start);
    }
    return { sections, windows };
  };
  var extractGlobalPrompt = (prompt) => tokenizePrompt(prompt).leading.trim();

  // frontend/swarmInputs.ts
  var DATA_INPUT_ID = "input_videostages";
  var warnedMissingDataInput = false;
  var getPromptInput = () => getVideoStagesHostBridge().getTextInput("input_prompt");
  var getDataInput = () => {
    const el = getVideoStagesHostBridge().getTextInput(DATA_INPUT_ID);
    if (el) {
      return el;
    }
    if (!warnedMissingDataInput) {
      warnedMissingDataInput = true;
      console.warn(
        `VideoStages: Data param input not found (#${DATA_INPUT_ID}).`
      );
    }
    return null;
  };
  var readDataParam = () => getDataInput()?.value ?? "";
  var writeDataParam = (json) => {
    const el = getDataInput();
    if (!el) {
      return;
    }
    el.value = json;
  };
  var readStateToken = () => `${readDataParam()}\0${getPromptInput()?.value ?? ""}`;
  var writeClipPrompts = (clips) => {
    const el = getPromptInput();
    if (!el) {
      return;
    }
    el.value = serializeClipPrompts(el.value ?? "", clips);
  };
  var notifyCarrierChanged = () => {
    const dataEl = getDataInput();
    if (dataEl) {
      getVideoStagesHostBridge().notifyChanged(dataEl);
    }
    const promptEl = getPromptInput();
    if (promptEl) {
      getVideoStagesHostBridge().notifyChanged(promptEl, true);
    }
  };
  var readGlobalPrompt = () => extractGlobalPrompt(getPromptInput()?.value ?? "");
  var getGroupToggle = () => getVideoStagesHostBridge().getInput(
    "input_group_content_videostages_toggle"
  );
  var getRootModelInput = () => getVideoStagesHostBridge().getInput("input_model");
  var getBase2EditStageRefs = () => {
    const snapshot = getVideoStagesHostBridge().getBase2EditRegistry();
    if (!snapshot?.enabled || !Array.isArray(snapshot.refs)) {
      return [];
    }
    const refs = snapshot.refs.map((value) => {
      const stageIndex = parseBase2EditStageIndex(`${value ?? ""}`);
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
    const catalog = buildArchitectureModelCatalog([modelName], [modelName]);
    const architectureId = architectureForModel(catalog, modelName);
    const architecture = architectureDescriptor(catalog, architectureId);
    return architecture?.capabilities.entryModes.includes("text-to-video") ?? false;
  };
  var getRootGeneratedEntryMode = () => !`${getRootModelInput()?.value ?? ""}`.trim() || isRootTextToVideoModel() ? "text-to-video" : "image-to-video";
  var getDropdownOptions = (paramId, fallbackSelectId) => {
    const registered = getVideoStagesHostBridge().getParamOptions(paramId);
    if (registered) {
      return registered;
    }
    const bridge2 = getVideoStagesHostBridge();
    return bridge2.getSelectOptions(bridge2.getSelect(fallbackSelectId));
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
    getVideoStagesHostBridge().notifyChanged(toggler);
  };

  // frontend/rootDefaults.ts
  var trimDomValue = (el) => `${el?.value ?? ""}`.trim();
  var WIDTH_INPUT_IDS = ["input_width", "input_aspectratiowidth"];
  var HEIGHT_INPUT_IDS = ["input_height", "input_aspectratioheight"];
  var rootVideoFpsInput = () => getVideoStagesHostBridge().getRootVideoFpsInput();
  var firstPresentInput = (...ids) => {
    for (let i = 0; i < ids.length; i++) {
      const el = getVideoStagesHostBridge().getInput(ids[i]);
      if (el) {
        return el;
      }
    }
    return null;
  };
  var getDefaultStageModel = (modelValues, architectureId) => {
    const catalog = buildArchitectureModelCatalog(modelValues, modelValues);
    const supports = (modelName) => {
      const resolved = architectureForModel(catalog, modelName);
      return resolved !== null && (architectureId === void 0 || resolved === architectureId);
    };
    if (isRootTextToVideoModel()) {
      const modelName = trimDomValue(getRootModelInput());
      if (modelName && supports(modelName)) {
        return modelName;
      }
    }
    const videoModel = trimDomValue(
      getVideoStagesHostBridge().getSelect("input_videomodel")
    );
    if (videoModel && supports(videoModel)) {
      return videoModel;
    }
    return modelValues.find((modelName) => supports(modelName)) ?? modelValues[0] ?? "";
  };
  var readInheritedDimsSignature = () => {
    const width = trimDomValue(firstPresentInput(...WIDTH_INPUT_IDS));
    const height = trimDomValue(firstPresentInput(...HEIGHT_INPUT_IDS));
    const fps = trimDomValue(rootVideoFpsInput());
    return `${width}|${height}|${fps}`;
  };
  var getRootDefaults = () => {
    let model = getVideoStagesHostBridge().getSelect("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
      model = getVideoStagesHostBridge().getSelect("input_model");
    }
    const rawLoras = getDropdownOptions("loras", "input_loras");
    const loras = { values: [], labels: [] };
    rawLoras.values.forEach((value, i) => {
      if (`${value}`.replace(/\s+/g, "").toLowerCase() !== "(none)") {
        loras.values.push(value);
        loras.labels.push(rawLoras.labels[i] ?? value);
      }
    });
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = getVideoStagesHostBridge().getSelect(
      "input_refinerupscalemethod"
    );
    const upscaleMethodValues = getVideoStagesHostBridge().getSelectOptions(upscaleMethod).values;
    const upscaleMethodLabels = getVideoStagesHostBridge().getSelectOptions(upscaleMethod).labels;
    const modelCatalog = supportedArchitectureCatalog(
      buildArchitectureModelCatalog(
        getVideoStagesHostBridge().getSelectOptions(model).values,
        getVideoStagesHostBridge().getSelectOptions(model).labels
      )
    );
    const models = {
      values: modelCatalog.entries.map((entry) => entry.value),
      labels: modelCatalog.entries.map((entry) => entry.label)
    };
    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(...WIDTH_INPUT_IDS);
    const heightInput = firstPresentInput(...HEIGHT_INPUT_IDS);
    const fpsInput = rootVideoFpsInput();
    const framesInput = firstPresentInput(
      "input_videoframes",
      "input_text2videoframes"
    );
    const fps = Math.max(1, Math.round(toNumber(fpsInput?.value, 24)));
    const frames = Math.max(1, Math.round(toNumber(framesInput?.value, 24)));
    return {
      modelValues: models.values,
      modelLabels: models.labels,
      modelCatalog,
      loraValues: loras.values,
      loraLabels: loras.labels,
      loraDefaultWeights: loras.values.map(
        (value) => getVideoStagesHostBridge().getLoraDefaultWeight(value)
      ),
      samplerValues: sampler.values,
      samplerLabels: sampler.labels,
      schedulerValues: scheduler.values,
      schedulerLabels: scheduler.labels,
      upscaleMethodValues,
      upscaleMethodLabels,
      width: Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(toNumber(widthInput?.value, 1024))
      ),
      height: Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(toNumber(heightInput?.value, 1024))
      ),
      fps,
      frames,
      control: 0.5,
      controlMin: 0,
      controlMax: 1,
      controlStep: 0.05,
      upscale: 1,
      upscaleMin: 0.25,
      upscaleMax: 4,
      upscaleStep: 0.25,
      steps: 8,
      stepsMin: Math.max(1, Math.round(toNumber(steps?.min, 1))),
      stepsMax: Math.min(
        50,
        Math.max(1, Math.round(toNumber(steps?.max, 200)))
      ),
      stepsStep: Math.max(1, Math.round(toNumber(steps?.step, 1))),
      cfgScale: 1,
      cfgScaleMin: toNumber(cfgScale?.min, 0),
      cfgScaleMax: Math.min(10, toNumber(cfgScale?.max, 10)),
      cfgScaleStep: toNumber(cfgScale?.step, 0.5)
    };
  };

  // frontend/persistence/documentCodec.ts
  var toIntOrNull = (value) => {
    const num = optionalNonNegativeNumber(value);
    return num === null ? null : Math.round(num);
  };
  var resolveRootDims = (inherited, stored) => {
    const width = toIntOrNull(stored.width);
    const height = toIntOrNull(stored.height);
    const dimsExplicit = width !== null && width >= ROOT_DIMENSION_MIN && height !== null && height >= ROOT_DIMENSION_MIN;
    return {
      width: dimsExplicit ? width : inherited.width,
      height: dimsExplicit ? height : inherited.height,
      // fps is never stored: the timeline always follows the core Video FPS
      // param (the backend falls back to it too when the JSON has no fps).
      fps: inherited.fps,
      dimsExplicit
    };
  };
  var createRootConfig = (dims, clips, audioTracks = []) => {
    const config = {
      schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
      ...dims,
      clips,
      audioTracks
    };
    ensureAuthoringDocumentIdentity(config);
    return config;
  };
  var serializeClipsForStorage = (clips) => {
    ensureClipEntityIdentities(clips);
    return clips.map(
      (clip) => ({
        id: clip.id,
        architecture: clip.architecture,
        modelProfileId: clip.modelProfileId,
        skipped: clip.skipped,
        boundaryOut: clip.boundaryOut,
        boundaryOutCarryAudio: clip.boundaryOutCarryAudio,
        boundaryOutOverlap: clip.boundaryOutOverlap,
        duration: clip.duration,
        audioSource: clip.audioSource,
        loras: clip.loras.map((entry) => ({
          name: entry.name
        })),
        icLoras: clip.icLoras.map((entry) => ({
          lora: entry.lora,
          preset: entry.preset,
          driveSource: entry.driveSource,
          driveData: entry.driveData,
          driveMediaKinds: entry.driveMediaKinds,
          stage: entry.stage,
          strength: entry.strength,
          attentionStrength: entry.attentionStrength,
          controlType: entry.controlType,
          hdr: entry.hdr,
          driveMedia: entry.driveMedia
        })),
        saveAudioTrack: clip.saveAudioTrack,
        clipLengthFromAudio: clip.clipLengthFromAudio,
        clipLengthFromControlNet: clip.clipLengthFromControlNet,
        reuseAudio: clip.reuseAudio,
        uploadedAudio: clip.uploadedAudio,
        sourceVideo: clip.sourceVideo ? {
          data: clip.sourceVideo.data,
          fileName: clip.sourceVideo.fileName,
          fps: clip.sourceVideo.fps,
          durationSeconds: clip.sourceVideo.durationSeconds,
          startSeconds: clip.sourceVideo.startSeconds,
          lengthSeconds: clip.sourceVideo.lengthSeconds
        } : null,
        retake: clip.retake ? {
          id: clip.retake.id,
          startSeconds: clip.retake.startSeconds,
          lengthSeconds: clip.retake.lengthSeconds,
          strength: clip.retake.strength
        } : null,
        refs: clip.refs.map((ref) => ({
          id: ref.id,
          source: ref.source,
          uploadFileName: ref.uploadFileName,
          uploadedImage: ref.uploadedImage,
          frame: ref.frame,
          fromEnd: ref.fromEnd
        })),
        stages: clip.stages.map((stage) => ({
          id: stage.id,
          skipped: stage.skipped,
          control: stage.control,
          controlNetStrength: stage.controlNetStrength,
          icLoraStrengths: stage.icLoraStrengths,
          loraWeights: stage.loraWeights,
          refStrengths: stage.refStrengths,
          upscale: stage.upscale,
          upscaleMethod: stage.upscaleMethod,
          model: stage.model,
          modelProfileId: stage.modelProfileId,
          steps: stage.steps,
          cfgScale: stage.cfgScale,
          sampler: stage.sampler,
          scheduler: stage.scheduler
        }))
      })
    );
  };
  var timelinePointProjection = (clips, seconds, edge) => {
    if (!Number.isFinite(seconds) || seconds < 0 || clips.length === 0) {
      return null;
    }
    let cursor = 0;
    for (let index = 0; index < clips.length; index++) {
      const clip = clips[index];
      const duration = Math.max(0, clip.duration || 0);
      const clipEnd = cursor + duration;
      const isLast = index === clips.length - 1;
      const ownsPoint = edge === "start" ? seconds < clipEnd || isLast : seconds <= clipEnd || isLast;
      if (ownsPoint) {
        return {
          clipId: clip.id,
          offsetSeconds: Math.max(
            0,
            Math.min(duration, seconds - cursor)
          )
        };
      }
      cursor = clipEnd;
    }
    return null;
  };
  var timelineSpanProjection = (clips, span) => {
    if (span.timelineStartSeconds === null || span.timelineLengthSeconds === null) {
      return null;
    }
    const start = timelinePointProjection(
      clips,
      span.timelineStartSeconds,
      "start"
    );
    const end = timelinePointProjection(
      clips,
      span.timelineStartSeconds + span.timelineLengthSeconds,
      "end"
    );
    return start && end ? {
      firstClipId: start.clipId,
      lastClipId: end.clipId,
      clipStartOffsetSeconds: start.offsetSeconds,
      clipEndOffsetSeconds: end.offsetSeconds
    } : null;
  };
  var serializeStateForStorage = (state) => {
    ensureAuthoringDocumentIdentity(state);
    const canonical = state;
    const out = {
      schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION
    };
    if (state.dimsExplicit) {
      out.width = Math.round(state.width);
      out.height = Math.round(state.height);
    }
    out.clips = serializeClipsForStorage(state.clips);
    out.audioTracks = canonical.audioTracks.map((track) => ({
      id: track.id,
      ...track.volume === void 0 ? {} : { volume: track.volume },
      source: {
        kind: track.source.kind,
        reference: track.source.reference,
        uploadedAudio: track.source.uploadedAudio
      },
      spans: track.spans.map((span) => ({
        id: span.id,
        timelineStartSeconds: span.timelineStartSeconds,
        timelineLengthSeconds: span.timelineLengthSeconds,
        sourceStartSeconds: span.sourceStartSeconds,
        projection: timelineSpanProjection(canonical.clips, span)
      }))
    }));
    return JSON.stringify(out);
  };
  var isTransientBrowserMedia = (media) => {
    const data = media?.data.trim().toLowerCase() ?? "";
    return data.startsWith("data:") || data.startsWith("blob:");
  };
  var serializeStateForDurableStorage = (state) => {
    ensureAuthoringDocumentIdentity(state);
    const durable = structuredClone(state);
    for (const clip of durable.clips) {
      if (isTransientBrowserMedia(clip.uploadedAudio)) {
        clip.uploadedAudio = null;
      }
      if (clip.sourceVideo && isTransientBrowserMedia({ data: clip.sourceVideo.data })) {
        clip.sourceVideo = null;
      }
      for (const ref of clip.refs) {
        if (isTransientBrowserMedia(ref.uploadedImage)) {
          ref.uploadedImage = null;
        }
      }
      for (const icLora of clip.icLoras) {
        if (isTransientBrowserMedia(icLora.driveMedia)) {
          icLora.driveMedia = null;
        }
      }
    }
    for (const track of durable.audioTracks ?? []) {
      if (isTransientBrowserMedia(track.source.uploadedAudio)) {
        track.source.uploadedAudio = null;
      }
    }
    return serializeStateForStorage(durable);
  };
  var hasArrayOfRecords = (owner, key) => {
    if (!Object.hasOwn(owner, key)) {
      return true;
    }
    const value = owner[key];
    return Array.isArray(value) && value.every(isRecord);
  };
  var hasValidStoredCollections = (parsed) => {
    if (!Array.isArray(parsed.clips) || !parsed.clips.every(isRecord)) {
      return false;
    }
    if (!hasArrayOfRecords(parsed, "audioTracks") || !hasArrayOfRecords(parsed, "clips")) {
      return false;
    }
    for (const clip of parsed.clips) {
      if (!hasArrayOfRecords(clip, "stages") || !hasArrayOfRecords(clip, "refs") || !hasArrayOfRecords(clip, "icLoras") || Object.hasOwn(clip, "loras") && !hasArrayOfRecords(clip, "loras")) {
        return false;
      }
      const stages = Array.isArray(clip.stages) ? clip.stages : [];
      for (const stage of stages) {
        if (Object.hasOwn(stage, "loras") && !hasArrayOfRecords(stage, "loras") || Object.hasOwn(stage, "loraWeights") && (!Array.isArray(stage.loraWeights) || !stage.loraWeights.every(
          (weight) => typeof weight === "number" && Number.isFinite(weight)
        )) || Object.hasOwn(stage, "icLoraStrengths") && (!Array.isArray(stage.icLoraStrengths) || !stage.icLoraStrengths.every(
          (strength) => typeof strength === "number" && Number.isFinite(strength)
        )) || Object.hasOwn(stage, "refStrengths") && (!Array.isArray(stage.refStrengths) || !stage.refStrengths.every(
          (strength) => typeof strength === "number" && Number.isFinite(strength)
        ))) {
          return false;
        }
      }
    }
    const tracks = Array.isArray(parsed.audioTracks) ? parsed.audioTracks : [];
    return tracks.every(
      (track) => hasArrayOfRecords(track, "spans") && (!Object.hasOwn(track, "source") || isRecord(track.source))
    );
  };
  var OUTDATED_SCHEMA_NOTICE = "VideoStages: the saved timeline was created by an older version and could not be loaded.";
  var noticedOutdatedDocument = null;
  var noticeOutdatedSchema = (serialized) => {
    if (noticedOutdatedDocument === serialized) {
      return;
    }
    noticedOutdatedDocument = serialized;
    getVideoStagesHostBridge().showError(OUTDATED_SCHEMA_NOTICE);
  };
  var DIVERGENT_PROJECTION_NOTICE = "VideoStages: the saved timeline has audio spans whose clip anchors disagree with their timeline seconds. The seconds were used and the anchors will be rewritten on the next save — re-check those segments.";
  var noticedDivergentProjection = null;
  var SPAN_PROJECTION_TOLERANCE = 1e-6;
  var numberAt = (owner, key) => typeof owner[key] === "number" && Number.isFinite(owner[key]) ? owner[key] : null;
  var storedSpanProjection = (span) => {
    const raw = span.projection;
    if (!isRecord(raw)) {
      return null;
    }
    const first = raw.firstClipId;
    const last = raw.lastClipId;
    const startOffset = numberAt(raw, "clipStartOffsetSeconds");
    const endOffset = numberAt(raw, "clipEndOffsetSeconds");
    return typeof first === "string" && typeof last === "string" && startOffset !== null && endOffset !== null ? {
      firstClipId: first,
      lastClipId: last,
      clipStartOffsetSeconds: startOffset,
      clipEndOffsetSeconds: endOffset
    } : null;
  };
  var hasDivergentSpanProjection = (parsed) => {
    const clips = parsed.clips.map((clip) => ({
      id: typeof clip.id === "string" ? clip.id : "",
      duration: numberAt(clip, "duration") ?? 0
    }));
    const tracks = Array.isArray(parsed.audioTracks) ? parsed.audioTracks : [];
    for (const track of tracks) {
      const spans = isRecord(track) && Array.isArray(track.spans) ? track.spans : [];
      for (const span of spans) {
        if (!isRecord(span)) {
          continue;
        }
        const stored = storedSpanProjection(span);
        if (!stored) {
          continue;
        }
        const expected = timelineSpanProjection(clips, {
          timelineStartSeconds: numberAt(span, "timelineStartSeconds"),
          timelineLengthSeconds: numberAt(span, "timelineLengthSeconds")
        });
        if (!expected || expected.firstClipId !== stored.firstClipId || expected.lastClipId !== stored.lastClipId || Math.abs(
          expected.clipStartOffsetSeconds - stored.clipStartOffsetSeconds
        ) > SPAN_PROJECTION_TOLERANCE || Math.abs(
          expected.clipEndOffsetSeconds - stored.clipEndOffsetSeconds
        ) > SPAN_PROJECTION_TOLERANCE) {
          return true;
        }
      }
    }
    return false;
  };
  var noticeDivergentProjection = (serialized) => {
    if (noticedDivergentProjection === serialized) {
      return;
    }
    noticedDivergentProjection = serialized;
    getVideoStagesHostBridge().showError(DIVERGENT_PROJECTION_NOTICE);
  };
  var decodeStoredDocument = (serialized, inherited) => {
    try {
      const parsed = JSON.parse(serialized);
      if (!isRecord(parsed)) {
        return null;
      }
      if (parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION) {
        noticeOutdatedSchema(serialized);
        return null;
      }
      if (!hasValidStoredCollections(parsed)) {
        return null;
      }
      if (hasDivergentSpanProjection(parsed)) {
        noticeDivergentProjection(serialized);
      }
      const dims = resolveRootDims(inherited, {
        width: parsed.width,
        height: parsed.height
      });
      return {
        dims,
        clips: parsed.clips.map(
          (entry) => normalizeClip(
            entry,
            getRootDefaults,
            getDefaultStageModel,
            dims.fps
          )
        ),
        audioTracks: normalizeAudioTracks(parsed.audioTracks)
      };
    } catch {
      return null;
    }
  };
  var hasCanonicalStoredId = (value, seen) => {
    if (!isRecord(value) || typeof value.id !== "string" || value.id.length === 0 || value.id.trim() !== value.id || seen.has(value.id)) {
      return false;
    }
    seen.add(value.id);
    return true;
  };
  var storedDocumentNeedsCanonicalIdRepair = (serialized) => {
    try {
      const parsed = JSON.parse(serialized);
      if (!isRecord(parsed) || parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION || !Array.isArray(parsed.clips) || !Array.isArray(parsed.audioTracks)) {
        return true;
      }
      const seenIds = /* @__PURE__ */ new Set();
      for (const rawClip of parsed.clips) {
        if (!hasCanonicalStoredId(rawClip, seenIds)) return true;
        for (const key of ["stages", "refs"]) {
          const children = rawClip[key];
          if (!Array.isArray(children) || children.some(
            (child) => !hasCanonicalStoredId(child, seenIds)
          )) {
            return true;
          }
        }
        if (rawClip.retake !== null && !hasCanonicalStoredId(rawClip.retake, seenIds)) {
          return true;
        }
      }
      for (const rawTrack of parsed.audioTracks) {
        if (!hasCanonicalStoredId(rawTrack, seenIds) || !Array.isArray(rawTrack.spans) || rawTrack.spans.some(
          (span) => !hasCanonicalStoredId(span, seenIds)
        )) {
          return true;
        }
      }
      return false;
    } catch {
      return true;
    }
  };

  // frontend/debugLog.ts
  var videoStagesDebugEnabled = () => typeof window !== "undefined" && !!window.__VIDEO_STAGES_DEBUG__;
  var videoStagesDebugLog = (area, message, ...details) => {
    if (!videoStagesDebugEnabled()) {
      return;
    }
    console.debug(`[VideoStages debug ${area}]`, message, ...details);
  };

  // frontend/architectures/clipIdentity.ts
  var modelIdentityFromCatalog = (catalog, model) => {
    if (!catalog) return null;
    const entry = modelCatalogEntry(catalog, model);
    if (!entry?.architectureId || !entry.modelProfileId || !architectureDescriptor(catalog, entry.architectureId)?.profiles.some(
      (profile) => profile.id === entry.modelProfileId
    )) {
      return null;
    }
    return {
      architectureId: entry.architectureId,
      modelProfileId: entry.modelProfileId
    };
  };
  var deriveClipArchitectureIdentity = (clip, catalog) => {
    if (!catalog) return null;
    const identities = clip.stages.map((stage) => ({
      stage,
      identity: modelIdentityFromCatalog(catalog, stage.model)
    }));
    if (identities.some(
      ({ stage, identity }) => !identity || stage.modelProfileId !== identity.modelProfileId
    )) {
      return null;
    }
    const authored = identities[0]?.identity ?? null;
    if (authored && identities.some(
      ({ identity }) => identity?.architectureId !== authored.architectureId
    )) {
      return null;
    }
    const descriptor = authored ? architectureDescriptor(catalog, authored.architectureId) : null;
    if (clip.stages.length > 1 && !descriptor?.capabilities.architecture.includes("multi-stage")) {
      return null;
    }
    const authoredIdentity = {
      authoredArchitectureId: authored?.architectureId ?? null,
      authoredModelProfileId: authored?.modelProfileId ?? null
    };
    if (clip.sourceVideo !== null && clip.stages.every((stage) => stage.skipped)) {
      return {
        architectureId: NONE_ARCHITECTURE_ID,
        modelProfileId: NONE_ARCHITECTURE_ID,
        ...authoredIdentity
      };
    }
    if (authored) {
      return {
        architectureId: authored.architectureId,
        modelProfileId: authored.modelProfileId,
        ...authoredIdentity
      };
    }
    if (clip.architecture === NONE_ARCHITECTURE_ID && clip.modelProfileId === NONE_ARCHITECTURE_ID) {
      return {
        architectureId: NONE_ARCHITECTURE_ID,
        modelProfileId: NONE_ARCHITECTURE_ID,
        ...authoredIdentity
      };
    }
    const validEmptyIdentity = catalog.architectures.find((architecture) => architecture.id === clip.architecture)?.profiles.some((profile) => profile.id === clip.modelProfileId) ?? false;
    return validEmptyIdentity ? {
      architectureId: clip.architecture,
      modelProfileId: clip.modelProfileId,
      ...authoredIdentity
    } : null;
  };
  var reconcileClipArchitectureIdentity = (clip, catalog) => {
    const identity = deriveClipArchitectureIdentity(clip, catalog);
    if (!identity) return false;
    clip.architecture = identity.architectureId;
    clip.modelProfileId = identity.modelProfileId;
    return true;
  };

  // frontend/architectures/conditionalRules.ts
  var CONDITIONAL_RULE_CODES = {
    audioReuseRequiresStages: "audio.reuse.requires_three_stages",
    promptRelayRequiresFixedLength: "prompt-relay-dynamic-length-unsupported",
    retakeExcludesReferences: "retake-frame-references-unsupported",
    retakeRequiresSource: "retake-source-required",
    uniformTimelineHdr: "mixed-hdr-timeline-unsupported"
  };
  var conditionalRule = (rules, code) => rules.find((rule) => rule.code === code) ?? null;
  var finiteConstraint = (rule, key, fallback) => {
    const value = Number(rule.constraints?.[key]);
    return Number.isFinite(value) ? value : fallback;
  };
  var DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES = 3;
  var audioReuseMinimumActiveStages = (rule) => rule?.code === CONDITIONAL_RULE_CODES.audioReuseRequiresStages ? finiteConstraint(
    rule,
    "minimumActiveStages",
    DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES
  ) : DEFAULT_AUDIO_REUSE_MINIMUM_ACTIVE_STAGES;
  var evaluateConditionalRule = (rule, context) => {
    const clip = context.clip;
    switch (rule.code) {
      case CONDITIONAL_RULE_CODES.audioReuseRequiresStages:
        return clip !== void 0 && activeStageCount(clip) < audioReuseMinimumActiveStages(rule);
      case CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength:
        return clip !== void 0 && (clip.clipLengthFromAudio || clip.clipLengthFromControlNet);
      case CONDITIONAL_RULE_CODES.retakeExcludesReferences:
        return clip !== void 0 && clip.refs.length > 0 && (clip.sourceVideo !== null || context.globalRefineMode === true);
      case CONDITIONAL_RULE_CODES.retakeRequiresSource:
        return clip !== void 0 && clip.sourceVideo === null && context.globalRefineMode !== true;
      case CONDITIONAL_RULE_CODES.uniformTimelineHdr: {
        const clips = context.timelineClips;
        const hasActiveHdr = context.hasActiveHdr;
        if (!clips || !hasActiveHdr) return false;
        if (clips.length < finiteConstraint(rule, "minimumTimelineClips", 2)) {
          return false;
        }
        const hdr = clips.map(hasActiveHdr);
        return hdr.some(Boolean) && hdr.some((value) => !value);
      }
      default:
        return false;
    }
  };

  // frontend/architectures/policy/featureValues.ts
  var FEATURE_LABEL = {
    multiStage: "Multiple stages",
    sourceVideo: "Source video",
    frameReferences: "Frame references",
    retake: "Retakes",
    majorPrompt: "Major prompts",
    promptRelay: "Relay prompts",
    clipAudio: "Clip audio",
    audioReuse: "Captured stage audio reuse",
    stageLoras: "LoRAs",
    icLora: "IC-LoRA",
    hdr: "HDR",
    upscale: "Stage upscaling"
  };
  var architectureReason = (label, feature) => `${FEATURE_LABEL[feature]} is not supported by ${label}.`;
  var noArchitectureReason = (feature) => `${FEATURE_LABEL[feature]} requires a generated clip with a known architecture.`;
  var upscaleModeForMethod = (method) => {
    const normalized = method.trim().toLowerCase();
    if (normalized.startsWith("latentmodel-")) return "latent-model";
    if (normalized.startsWith("latent-")) return "latent";
    if (normalized.startsWith("pixel-")) return "pixel";
    return "model";
  };

  // frontend/architectures/policy/clipStageViews.ts
  var FEATURE_RULE_CODES = {
    promptRelay: [CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength],
    audioReuse: [CONDITIONAL_RULE_CODES.audioReuseRequiresStages],
    retake: [
      CONDITIONAL_RULE_CODES.retakeRequiresSource,
      CONDITIONAL_RULE_CODES.retakeExcludesReferences
    ],
    hdr: [CONDITIONAL_RULE_CODES.uniformTimelineHdr]
  };
  var conditionalRuleFor = (clip, feature, descriptor, scope) => {
    const codes = FEATURE_RULE_CODES[feature];
    if (!codes) return void 0;
    for (const code of codes) {
      const rule = conditionalRule(descriptor.rules, code);
      if (rule && evaluateConditionalRule(rule, {
        clip,
        globalRefineMode: scope.globalRefineMode,
        timelineClips: scope.timelineClips,
        hasActiveHdr: clipHasActiveHdr
      })) {
        return rule;
      }
    }
    return void 0;
  };
  var architectureFeatureSupport = (feature, scope) => {
    const capability = scope.capabilities;
    switch (feature) {
      case "multiStage":
        return capability.architecture.includes("multi-stage");
      case "sourceVideo":
        return capability.clip.includes("source-video");
      case "frameReferences":
        return capability.clip.includes("references") && capability.stage.includes("frame-references");
      case "retake":
        return capability.clip.includes("retake");
      case "majorPrompt":
        return capability.clip.includes("prompts");
      case "promptRelay":
        return capability.clip.includes("prompt-relay");
      case "clipAudio":
      case "audioReuse":
        return capability.clip.includes("audio-sources") && (scope.audioSource === void 0 || isAllowedAudioSource(
          capability.audioSourceKinds,
          scope.audioSource
        ));
      case "stageLoras":
        return capability.stage.includes("lora") && (scope.profileCapabilities === void 0 || scope.profileCapabilities.includes("normal-lora"));
      case "icLora":
        return capability.stage.includes("ic-lora");
      case "hdr":
        return capability.stage.includes("hdr");
      case "upscale":
        return scope.upscaleMethod === void 0 ? capability.upscaleModes.length > 0 : capability.upscaleModes.includes(
          upscaleModeForMethod(scope.upscaleMethod)
        );
    }
  };
  var scopedFeatureSupport = (feature, descriptor, profileId) => architectureFeatureSupport(feature, {
    capabilities: descriptor.capabilities,
    profileCapabilities: descriptor.profiles.find(
      (entry) => entry.id === profileId
    )?.capabilities
  });
  var createClipStageCapabilityViews = (architectureById, scope = {}) => {
    const forClip = (clip) => {
      const descriptor = architectureById.get(clip.architecture);
      const label = descriptor?.label ?? (clip.architecture === NONE_ARCHITECTURE_ID ? "source-only clips" : `unknown architecture '${clip.architecture}'`);
      const decision = (feature) => {
        if (!descriptor) {
          return {
            supported: false,
            reason: noArchitectureReason(feature),
            rule: null
          };
        }
        const conditionalRule2 = conditionalRuleFor(
          clip,
          feature,
          descriptor,
          scope
        );
        const supported = scopedFeatureSupport(
          feature,
          descriptor,
          clip.modelProfileId
        ) && !conditionalRule2;
        return {
          supported,
          reason: supported ? "" : conditionalRule2?.reason ?? architectureReason(label, feature),
          rule: conditionalRule2 ?? null
        };
      };
      return {
        architectureId: clip.architecture,
        architectureLabel: label,
        known: descriptor !== void 0,
        audioSourceKinds: descriptor?.capabilities.audioSourceKinds ?? [],
        decision,
        authoringState: (feature, persisted) => {
          const result = decision(feature);
          return {
            ...result,
            visible: result.supported || persisted,
            enabled: result.supported
          };
        }
      };
    };
    const forStage = (clip, stage) => {
      const view = forClip(clip);
      const descriptor = architectureById.get(clip.architecture);
      const profile = descriptor?.profiles.find(
        (entry) => entry.id === stage.modelProfileId
      );
      const decision = (feature) => {
        if (feature === "stageLoras" && descriptor) {
          const supported = descriptor.capabilities.stage.includes("lora") && profile?.capabilities.includes("normal-lora") === true;
          return {
            supported,
            reason: supported ? "" : `LoRAs require normal-LoRA support in ${descriptor.label}.`,
            rule: null
          };
        }
        if (feature === "sampler" || feature === "scheduler") {
          const required = feature === "sampler" ? "sampler-selection" : "scheduler-selection";
          const supported = profile?.capabilities.includes(required) === true;
          return {
            supported,
            reason: supported ? "" : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection is not supported by this model profile.`,
            rule: null
          };
        }
        return view.decision(feature);
      };
      return {
        upscaleModes: descriptor?.capabilities.upscaleModes ?? [],
        decision,
        authoringState: (feature, persisted) => {
          const result = decision(feature);
          return {
            ...result,
            visible: result.supported || persisted,
            enabled: result.supported
          };
        }
      };
    };
    return { forClip, forStage };
  };

  // frontend/architectures/conversion/plan.ts
  var countLabel = (count, singular) => `${count} ${singular}${count === 1 ? "" : "s"}`;
  var ownId = (value) => typeof value === "object" && value !== null && "id" in value && typeof value.id === "string" ? value.id : null;
  var collectIds = (values) => values.map(ownId).filter((id) => id !== null);
  var resolveArchitectureRetarget = (requested, catalog) => {
    if (!catalog) {
      return null;
    }
    const model = modelCatalogEntry(catalog, requested.model);
    if (!model?.architectureId || !model.modelProfileId || model.architectureId !== requested.architectureId || model.modelProfileId !== requested.modelProfileId) {
      return null;
    }
    const descriptor = architectureDescriptor(catalog, model.architectureId);
    const profile = descriptor?.profiles.find(
      (entry) => entry.id === model.modelProfileId
    );
    if (!descriptor || !profile) {
      return null;
    }
    return {
      architectureId: descriptor.id,
      modelProfileId: profile.id,
      model: model.value,
      capabilities: structuredClone(descriptor.capabilities),
      profileCapabilities: [...profile.capabilities]
    };
  };
  var planArchitectureConversion = (source, requested, catalog) => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
      return null;
    }
    const clip = structuredClone(source);
    const removals = [];
    const removedEntityIds = [];
    const supports = (feature, value) => architectureFeatureSupport(feature, {
      capabilities: target.capabilities,
      profileCapabilities: target.profileCapabilities,
      ...value
    });
    const supportsMultipleStages = supports("multiStage");
    const supportsReferences = supports("frameReferences");
    const supportsNormalLoras = supports("stageLoras");
    clip.architecture = target.architectureId;
    clip.modelProfileId = target.modelProfileId;
    if (!supportsMultipleStages && clip.stages.length > 1) {
      const removedStages = clip.stages.slice(1);
      removals.push(countLabel(removedStages.length, "later authored stage"));
      removedEntityIds.push(...collectIds(removedStages));
      clip.stages = clip.stages.slice(0, 1);
    }
    if (!supportsReferences && clip.refs.length > 0) {
      removals.push(countLabel(clip.refs.length, "frame reference"));
      removedEntityIds.push(...collectIds(clip.refs));
      clip.refs = [];
    }
    const removedClipLoras = !supportsNormalLoras ? clip.loras.length : 0;
    if (removedClipLoras > 0) {
      clip.loras = [];
    }
    let removedUpscaleSettings = 0;
    for (const stage of clip.stages) {
      stage.model = target.model;
      stage.modelProfileId = target.modelProfileId;
      if (!supportsReferences) {
        stage.refStrengths = [];
      }
      if (!supportsNormalLoras) {
        stage.loraWeights = [];
      }
      if (stage.upscale !== 1 && !supports("upscale", { upscaleMethod: stage.upscaleMethod })) {
        removedUpscaleSettings++;
        stage.upscale = 1;
      }
    }
    if (removedClipLoras > 0) {
      removals.push(countLabel(removedClipLoras, "clip LoRA"));
    }
    if (removedUpscaleSettings > 0) {
      removals.push("stage upscale settings");
    }
    const removeIcLoras = (doomed) => {
      const removed = clip.icLoras.filter(doomed);
      if (removed.length === 0) {
        return 0;
      }
      for (let index = clip.icLoras.length - 1; index >= 0; index--) {
        if (doomed(clip.icLoras[index])) {
          removeIcLoraStrengthAt(clip, index);
        }
      }
      removedEntityIds.push(...collectIds(removed));
      clip.icLoras = clip.icLoras.filter((entry) => !doomed(entry));
      return removed.length;
    };
    if (!supports("icLora") && clip.icLoras.length > 0) {
      removals.push(
        countLabel(
          removeIcLoras(() => true),
          "IC-LoRA"
        )
      );
      clip.clipLengthFromControlNet = false;
    } else if (supports("icLora")) {
      if (!supports("hdr")) {
        const hdrCount = removeIcLoras(
          (entry) => isArchitectureHdrFeature(source.architecture, entry)
        );
        if (hdrCount > 0) {
          removals.push(countLabel(hdrCount, "HDR IC-LoRA"));
        }
      }
      let repairedTargets = false;
      for (const entry of clip.icLoras) {
        if (entry.stage >= clip.stages.length) {
          entry.stage = IC_LORA_STAGE_ALL;
          repairedTargets = true;
        }
        if (entry.driveData === "none" && entry.driveSource !== IC_LORA_SOURCE_UPLOAD) {
          entry.driveSource = IC_LORA_SOURCE_UPLOAD;
          entry.driveMedia = null;
        }
      }
      if (repairedTargets) {
        removals.push("IC-LoRA targets on removed stages");
      }
    }
    if (!supports("retake") && clip.retake !== null) {
      removals.push("retake");
      const id = ownId(clip.retake);
      if (id) removedEntityIds.push(id);
      clip.retake = null;
    }
    if (!supports("majorPrompt") && clip.prompt.trim()) {
      removals.push("major prompt");
      clip.prompt = "";
    }
    if (!supports("promptRelay") && clip.promptWindows.length > 0) {
      removals.push(countLabel(clip.promptWindows.length, "relay prompt"));
      removedEntityIds.push(...collectIds(clip.promptWindows));
      clip.promptWindows = [];
    }
    if (!supports("sourceVideo") && clip.sourceVideo) {
      removals.push("source video");
      clip.sourceVideo = null;
    }
    if (!supports("clipAudio", { audioSource: clip.audioSource })) {
      const hasAudioSettings = clip.audioSource !== "Native" || clip.uploadedAudio !== null || clip.saveAudioTrack || clip.clipLengthFromAudio || clip.reuseAudio || clip.clipLengthFromControlNet;
      if (hasAudioSettings) {
        removals.push("clip audio source settings");
      }
      clip.audioSource = defaultAuthoringAudioSource(
        target.capabilities.audioSourceKinds
      );
      clip.uploadedAudio = null;
      clip.saveAudioTrack = false;
      clip.clipLengthFromAudio = false;
      clip.reuseAudio = false;
      clip.clipLengthFromControlNet = false;
    }
    return {
      clip,
      removals,
      removedEntityIds,
      selectionAffected: removedEntityIds.length > 0
    };
  };

  // frontend/architectures/policy/boundaryPolicy.ts
  var forceCrossArchitectureCutsForConversion = (clips) => {
    for (const boundary of executableBoundaries(clips)) {
      const left = clips[boundary.leftIdx];
      if (left.architecture !== clips[boundary.rightIdx].architecture) {
        left.boundaryOut = "cut";
      }
    }
  };
  var createBoundaryCapabilityViews = (architectureById, forClip) => {
    const forBoundary = (left, right, leftClipIdx = -1, rightClipIdx = null) => {
      const leftView = forClip(left);
      const leftDescriptor = architectureById.get(left.architecture);
      const crossArchitecture = right !== null && left.architecture !== right.architecture;
      const hasInitialReference = right?.refs.some(
        (reference) => reference.fromEnd !== true && Math.max(1, Math.round(reference.frame)) === 1
      ) ?? false;
      const rightHasActiveStage = right?.stages.some((stage) => !stage.skipped) ?? false;
      const supportsMode = (mode) => {
        const rule = leftDescriptor?.boundaryRules[mode];
        if (!rule || rule.support === "unsupported") {
          return mode === "cut" && !leftDescriptor;
        }
        const constraints = rule.constraints;
        if (constraints?.sameArchitecture === true && crossArchitecture) {
          return false;
        }
        if (constraints?.targetRequiresGeneratedEntry === true && right?.sourceVideo !== null) {
          return false;
        }
        if (constraints?.targetRequiresStage === true && right !== null && !rightHasActiveStage) {
          return false;
        }
        if (constraints?.targetDisallowsInitialReference === true && hasInitialReference) {
          return false;
        }
        return true;
      };
      const ruleModes = ["cut", "continue", "crossfade"].filter(
        supportsMode
      );
      const modes = ruleModes.length > 0 ? ruleModes : ["cut"];
      const requestedRule = leftDescriptor?.boundaryRules[left.boundaryOut] ?? null;
      const reason = crossArchitecture ? "Executable clips from different architectures can only use a cut." : modes.length === 1 && modes[0] === "cut" ? requestedRule?.reason ?? `${leftView.architectureLabel} only supports cut boundaries.` : "";
      return {
        leftClipIdx,
        rightClipIdx,
        modes,
        crossArchitecture,
        reason,
        overlapConstraints: (mode) => boundaryOverlapConstraints(
          leftDescriptor?.boundaryRules[mode] ?? null
        ),
        effective: (requested) => supportsMode(requested) ? requested : "cut"
      };
    };
    const forBoundaryIndex = (clips, leftClipIdx) => {
      const left = clips[leftClipIdx];
      if (!left) {
        throw new Error(`Missing left clip at index ${leftClipIdx}.`);
      }
      const rightClipIdx = executableBoundaryForLeftClip(clips, leftClipIdx)?.rightIdx ?? null;
      return forBoundary(
        left,
        rightClipIdx === null ? null : clips[rightClipIdx],
        leftClipIdx,
        rightClipIdx
      );
    };
    return {
      forBoundary,
      forBoundaryIndex
    };
  };

  // frontend/documentCommands/listEntities.ts
  var OWNER_ID_FIELD = {
    document: null,
    clip: "clipId",
    track: "trackId"
  };
  var defineList = () => (descriptor) => descriptor;
  var definePatchKeys = () => (keys) => keys.patchKeys;
  var ROOT_PATCH_KEYS = definePatchKeys()({
    patchKeys: ["schemaVersion", "width", "height", "fps", "dimsExplicit"],
    reservedKeys: ["clips", "audioTracks"]
  });
  var RETAKE_PATCH_KEYS = definePatchKeys()({
    patchKeys: ["startSeconds", "lengthSeconds", "strength"],
    reservedKeys: ["id"]
  });
  var CLIP_ENTITY = defineList()({
    prefix: "clip",
    owner: "document",
    entityField: "clip",
    idField: "clipId",
    beforeIdField: "beforeClipId",
    patchKeys: [
      "skipped",
      "hue",
      "boundaryOut",
      "boundaryOutCarryAudio",
      "boundaryOutOverlap",
      "duration",
      "audioSource",
      "loras",
      "icLoras",
      "saveAudioTrack",
      "clipLengthFromAudio",
      "clipLengthFromControlNet",
      "reuseAudio",
      "uploadedAudio",
      "prompt",
      "sourceVideo"
    ],
    reservedKeys: [
      "id",
      "architecture",
      "modelProfileId",
      "promptWindows",
      "retake",
      "refs",
      "stages"
    ],
    collection: (document2) => document2.clips
  });
  var STAGE_ENTITY = defineList()({
    prefix: "stage",
    owner: "clip",
    entityField: "stage",
    idField: "stageId",
    beforeIdField: "beforeStageId",
    patchKeys: [
      "skipped",
      "control",
      "controlNetStrength",
      "icLoraStrengths",
      "loraWeights",
      "refStrengths",
      "upscale",
      "upscaleMethod",
      "steps",
      "cfgScale",
      "sampler",
      "scheduler"
    ],
    reservedKeys: ["id", "model", "modelProfileId"],
    reconcilesClipIdentity: true,
    collection: (clip) => clip.stages
  });
  var REF_ENTITY = defineList()({
    prefix: "ref",
    owner: "clip",
    entityField: "ref",
    idField: "refId",
    beforeIdField: "beforeRefId",
    patchKeys: [
      "source",
      "uploadFileName",
      "uploadedImage",
      "frame",
      "fromEnd"
    ],
    reservedKeys: ["id"],
    collection: (clip) => clip.refs
  });
  var PROMPT_WINDOW_ENTITY = defineList()(
    {
      prefix: "prompt-window",
      owner: "clip",
      entityField: "window",
      idField: "windowId",
      beforeIdField: "beforeWindowId",
      patchKeys: ["prompt", "start", "duration"],
      reservedKeys: ["id"],
      collection: (clip) => clip.promptWindows
    }
  );
  var AUDIO_TRACK_ENTITY = defineList()({
    prefix: "audio-track",
    owner: "document",
    entityField: "track",
    idField: "trackId",
    beforeIdField: "beforeTrackId",
    patchKeys: ["source", "volume"],
    reservedKeys: ["id", "spans"],
    collection: (document2) => document2.audioTracks
  });
  var AUDIO_SPAN_ENTITY = defineList()({
    prefix: "audio-span",
    owner: "track",
    entityField: "span",
    idField: "spanId",
    beforeIdField: "beforeSpanId",
    patchKeys: [
      "timelineStartSeconds",
      "timelineLengthSeconds",
      "sourceStartSeconds"
    ],
    reservedKeys: ["id"],
    collection: (track) => track.spans
  });
  var LIST_ENTITIES = {
    clip: CLIP_ENTITY,
    stage: STAGE_ENTITY,
    ref: REF_ENTITY,
    promptWindow: PROMPT_WINDOW_ENTITY,
    audioTrack: AUDIO_TRACK_ENTITY,
    audioSpan: AUDIO_SPAN_ENTITY
  };

  // frontend/documentDiff.ts
  var DocumentDiffError = class extends Error {
    failure;
    constructor(failure2) {
      super(`Cannot diff authoring documents: ${failure2}`);
      this.name = "DocumentDiffError";
      this.failure = failure2;
    }
  };
  var clone = (value) => structuredClone(value);
  var isRecord3 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var deepEqual = (left, right) => {
    if (Object.is(left, right)) {
      return true;
    }
    if (Array.isArray(left) || Array.isArray(right)) {
      return Array.isArray(left) && Array.isArray(right) && left.length === right.length && left.every((value, index) => deepEqual(value, right[index]));
    }
    if (!isRecord3(left) || !isRecord3(right)) {
      return false;
    }
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    return leftKeys.length === rightKeys.length && leftKeys.every(
      (key, index) => key === rightKeys[index] && deepEqual(left[key], right[key])
    );
  };
  var changedPatch = (before, after, keys) => {
    const patch = {};
    for (const key of keys) {
      if (!deepEqual(before[key], after[key])) {
        patch[key] = clone(after[key]);
      }
    }
    return patch;
  };
  var hasPatch = (patch) => Object.keys(patch).length > 0;
  var allEntityIds = (document2) => [
    ...document2.clips.flatMap((clip) => [
      clip.id,
      ...clip.stages.map((stage) => stage.id),
      ...clip.refs.map((ref) => ref.id),
      ...clip.promptWindows.map((window2) => window2.id),
      ...clip.retake ? [clip.retake.id] : []
    ]),
    ...document2.audioTracks.flatMap((track) => [
      track.id,
      ...track.spans.map((span) => span.id)
    ])
  ];
  var validateDocumentIds = (document2) => {
    const ids = allEntityIds(document2);
    if (ids.some(
      (id) => typeof id !== "string" || id.trim().length === 0 || id.trim() !== id
    )) {
      throw new DocumentDiffError("invalid-id");
    }
    if (new Set(ids).size !== ids.length) {
      throw new DocumentDiffError("duplicate-id");
    }
  };
  var insertBeforeId = (ids, id, beforeId) => {
    if (beforeId === null) {
      ids.push(id);
      return;
    }
    const index = ids.indexOf(beforeId);
    ids.splice(index, 0, id);
  };
  var moveBeforeId = (ids, id, beforeId) => {
    ids.splice(ids.indexOf(id), 1);
    insertBeforeId(ids, id, beforeId);
  };
  var listCommand = (descriptor, ownerId, suffix, fields) => {
    const ownerField = OWNER_ID_FIELD[descriptor.owner];
    return {
      type: `${descriptor.prefix}.${suffix}`,
      ...ownerField === null ? {} : { [ownerField]: ownerId },
      ...fields
    };
  };
  var emitPatch = (descriptor, ownerId, previous, next, phases) => {
    const patch = changedPatch(previous, next, descriptor.patchKeys);
    if (hasPatch(patch)) {
      phases.patches.push(
        listCommand(descriptor, ownerId, "patch", {
          [descriptor.idField]: next.id,
          patch
        })
      );
    }
  };
  var diffList = (descriptor, ownerId, beforeOwner, afterOwner, phases, patchEntity = (previous, next) => emitPatch(descriptor, ownerId, previous, next, phases)) => {
    const before = descriptor.collection(beforeOwner);
    const after = descriptor.collection(afterOwner);
    const beforeById = new Map(before.map((entity) => [entity.id, entity]));
    const afterIds = new Set(after.map((entity) => entity.id));
    const currentIds = before.map((entity) => entity.id);
    for (const entity of before) {
      if (!afterIds.has(entity.id)) {
        phases.removes.push(
          listCommand(descriptor, ownerId, "remove", {
            [descriptor.idField]: entity.id
          })
        );
        currentIds.splice(currentIds.indexOf(entity.id), 1);
      }
    }
    for (let index = after.length - 1; index >= 0; index--) {
      const entity = after[index];
      if (beforeById.has(entity.id)) {
        continue;
      }
      const beforeId = after[index + 1]?.id ?? null;
      phases.adds.push(
        listCommand(descriptor, ownerId, "add", {
          [descriptor.entityField]: clone(entity),
          [descriptor.beforeIdField]: beforeId
        })
      );
      insertBeforeId(currentIds, entity.id, beforeId);
    }
    for (let index = 0; index < after.length; index++) {
      const targetId = after[index].id;
      if (currentIds[index] === targetId) {
        continue;
      }
      const beforeId = currentIds[index] ?? null;
      phases.moves.push(
        listCommand(descriptor, ownerId, "move", {
          [descriptor.idField]: targetId,
          [descriptor.beforeIdField]: beforeId
        })
      );
      moveBeforeId(currentIds, targetId, beforeId);
    }
    for (const entity of after) {
      const previous = beforeById.get(entity.id);
      if (previous) {
        patchEntity(previous, entity);
      }
    }
  };
  var diffStages = (before, after, phases) => diffList(
    LIST_ENTITIES.stage,
    after.id,
    before,
    after,
    phases,
    (previous, next) => {
      if (previous.model !== next.model || previous.modelProfileId !== next.modelProfileId) {
        phases.patches.push({
          type: "stage.retarget-model",
          clipId: after.id,
          stageId: next.id,
          target: {
            architectureId: after.architecture,
            modelProfileId: next.modelProfileId,
            model: next.model
          }
        });
      }
      emitPatch(LIST_ENTITIES.stage, after.id, previous, next, phases);
    }
  );
  var diffRetake = (before, after, phases) => {
    if (before.retake?.id !== after.retake?.id) {
      if (before.retake) {
        phases.removes.push({
          type: "retake.remove",
          clipId: before.id,
          retakeId: before.retake.id
        });
      }
      if (after.retake) {
        phases.adds.push({
          type: "retake.add",
          clipId: after.id,
          retake: clone(after.retake)
        });
      }
      return;
    }
    if (!before.retake || !after.retake) {
      return;
    }
    const patch = changedPatch(before.retake, after.retake, RETAKE_PATCH_KEYS);
    if (hasPatch(patch)) {
      phases.patches.push({
        type: "retake.patch",
        clipId: after.id,
        retakeId: after.retake.id,
        patch
      });
    }
  };
  var diffClipChildren = (before, after, phases) => {
    diffStages(before, after, phases);
    diffList(LIST_ENTITIES.ref, after.id, before, after, phases);
    diffList(LIST_ENTITIES.promptWindow, after.id, before, after, phases);
    diffRetake(before, after, phases);
  };
  var clipDiffBase = (previous, next, phases, context) => {
    const changesEffectiveIdentity = previous.architecture !== next.architecture || previous.modelProfileId !== next.modelProfileId;
    const previousIdentity = deriveClipArchitectureIdentity(
      previous,
      context.architectureCatalog
    );
    const nextIdentity = deriveClipArchitectureIdentity(
      next,
      context.architectureCatalog
    );
    if (changesEffectiveIdentity) {
      if (!nextIdentity || nextIdentity.architectureId !== next.architecture || nextIdentity.modelProfileId !== next.modelProfileId) {
        throw new DocumentDiffError("architecture-invariant");
      }
    }
    const changesAuthoredArchitecture = previousIdentity?.authoredArchitectureId !== null && previousIdentity?.authoredArchitectureId !== void 0 && nextIdentity?.authoredArchitectureId !== null && nextIdentity?.authoredArchitectureId !== void 0 && previousIdentity.authoredArchitectureId !== nextIdentity.authoredArchitectureId;
    const nextIsSourceOnlyClip = next.stages.length === 0 && next.sourceVideo !== null && nextIdentity?.authoredArchitectureId == null && nextIdentity?.architectureId === NONE_ARCHITECTURE_ID;
    if (changesEffectiveIdentity && !changesAuthoredArchitecture && !nextIsSourceOnlyClip && (previousIdentity?.authoredArchitectureId == null || nextIdentity?.authoredArchitectureId == null || previousIdentity.authoredArchitectureId !== nextIdentity.authoredArchitectureId)) {
      throw new DocumentDiffError("architecture-invariant");
    }
    if (!changesAuthoredArchitecture) {
      return previous;
    }
    const catalog = context.architectureCatalog;
    const sourceArchitectureId = previousIdentity?.authoredArchitectureId;
    const targetStage = next.stages[0];
    const targetEntry = modelCatalogEntry(catalog, targetStage?.model);
    const targetDescriptor = architectureDescriptor(
      catalog,
      targetEntry?.architectureId
    );
    if (!catalog || !sourceArchitectureId || !targetStage || !targetEntry?.architectureId || !targetEntry.modelProfileId || !targetDescriptor || targetEntry.architectureId !== nextIdentity?.authoredArchitectureId || targetEntry.modelProfileId !== targetStage.modelProfileId) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const target = {
      architectureId: targetEntry.architectureId,
      modelProfileId: targetEntry.modelProfileId,
      model: targetEntry.value,
      capabilities: clone(targetDescriptor.capabilities)
    };
    const requestedForCleanup = clone(next);
    requestedForCleanup.architecture = sourceArchitectureId;
    const requestedPlan = planArchitectureConversion(
      requestedForCleanup,
      target,
      catalog
    );
    const baselinePlan = planArchitectureConversion(previous, target, catalog);
    if (!requestedPlan || !baselinePlan) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const cleanedRequested = requestedPlan.clip;
    if (cleanedRequested.stages.length === next.stages.length) {
      for (let index = 0; index < next.stages.length; index++) {
        cleanedRequested.stages[index].model = next.stages[index].model;
        cleanedRequested.stages[index].modelProfileId = next.stages[index].modelProfileId;
      }
    }
    if (!reconcileClipArchitectureIdentity(cleanedRequested, catalog) || !deepEqual(cleanedRequested, next)) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const convertedBase = baselinePlan.clip;
    if (!reconcileClipArchitectureIdentity(convertedBase, catalog)) {
      throw new DocumentDiffError("architecture-invariant");
    }
    phases.conversions.push({
      type: "clip.convert-architecture",
      clipId: next.id,
      target
    });
    return convertedBase;
  };
  var diffClips = (before, after, phases, context) => diffList(
    LIST_ENTITIES.clip,
    null,
    before,
    after,
    phases,
    (previous, next) => {
      const diffBase = clipDiffBase(previous, next, phases, context);
      emitPatch(LIST_ENTITIES.clip, null, diffBase, next, phases);
      diffClipChildren(diffBase, next, phases);
    }
  );
  var diffAudioTracks = (before, after, phases) => diffList(
    LIST_ENTITIES.audioTrack,
    null,
    before,
    after,
    phases,
    (previous, next) => {
      emitPatch(LIST_ENTITIES.audioTrack, null, previous, next, phases);
      diffList(LIST_ENTITIES.audioSpan, next.id, previous, next, phases);
    }
  );
  var diffDocuments = (before, after, context = { architectureCatalog: null }) => {
    validateDocumentIds(before);
    validateDocumentIds(after);
    const phases = {
      conversions: [],
      removes: [],
      adds: [],
      moves: [],
      patches: []
    };
    const rootPatch = changedPatch(before, after, ROOT_PATCH_KEYS);
    diffClips(before, after, phases, context);
    if (phases.conversions.length > 0) {
      const forcedFinalClips = clone(after.clips);
      forceCrossArchitectureCutsForConversion(forcedFinalClips);
      if (forcedFinalClips.some(
        (clip, index) => clip.boundaryOut !== after.clips[index]?.boundaryOut
      )) {
        throw new DocumentDiffError("architecture-invariant");
      }
      for (const clip of after.clips) {
        phases.patches.push({
          type: "clip.patch",
          clipId: clip.id,
          patch: { boundaryOut: clip.boundaryOut }
        });
      }
    }
    diffAudioTracks(before, after, phases);
    return {
      type: "batch",
      commands: [
        ...hasPatch(rootPatch) ? [{ type: "root.patch", patch: rootPatch }] : [],
        ...phases.conversions,
        ...phases.removes,
        ...phases.adds,
        ...phases.moves,
        ...phases.patches
      ]
    };
  };

  // frontend/documentCommands/helpers.ts
  var clone2 = (value) => structuredClone(value);
  var findClip = (document2, clipId) => document2.clips.find((clip) => clip.id === clipId) ?? null;
  var findTrack = (document2, trackId) => document2.audioTracks.find((track) => track.id === trackId) ?? null;
  var candidateIds = (entity) => {
    if ("stages" in entity && "refs" in entity) {
      return [
        entity.id,
        ...entity.stages.map((stage) => stage.id),
        ...entity.refs.map((ref) => ref.id),
        ...entity.promptWindows.map((window2) => window2.id),
        ...entity.retake ? [entity.retake.id] : []
      ];
    }
    if ("spans" in entity) {
      return [entity.id, ...entity.spans.map((span) => span.id)];
    }
    return [entity.id];
  };
  var invalidNewEntity = (document2, entity) => {
    const ids = candidateIds(entity);
    const invalidId = ids.some(
      (id) => typeof id !== "string" || id.trim().length === 0 || id.trim() !== id
    );
    if (invalidId) return failure(document2, "invalid-id");
    if (new Set(ids).size !== ids.length) {
      return failure(document2, "duplicate-id");
    }
    const existing = new Set(collectAuthoringEntityIds(document2));
    return ids.some((id) => existing.has(id)) ? failure(document2, "duplicate-id") : null;
  };
  var addBefore = (items, item, beforeId) => {
    if (beforeId == null) {
      items.push(item);
      return true;
    }
    const beforeIndex = items.findIndex(
      (candidate) => candidate.id === beforeId
    );
    if (beforeIndex < 0) return false;
    items.splice(beforeIndex, 0, item);
    return true;
  };
  var removeById = (items, id) => {
    const index = items.findIndex((item) => item.id === id);
    if (index < 0) return false;
    items.splice(index, 1);
    return true;
  };
  var moveBefore = (items, id, beforeId) => {
    const fromIndex = items.findIndex((item2) => item2.id === id);
    if (fromIndex < 0) return false;
    if (beforeId !== null && !items.some((item2) => item2.id === beforeId)) {
      return false;
    }
    if (id === beforeId) return true;
    const [item] = items.splice(fromIndex, 1);
    if (beforeId === null) {
      items.push(item);
      return true;
    }
    const toIndex = items.findIndex((candidate) => candidate.id === beforeId);
    items.splice(toIndex, 0, item);
    return true;
  };
  var patchById = (items, id, patch) => {
    const entity = items.find((item) => item.id === id);
    if (!entity) return false;
    Object.assign(entity, clone2(patch), { id });
    return true;
  };
  var success = (document2) => ({ document: document2, applied: true });
  var failure = (document2, reason) => ({
    document: document2,
    applied: false,
    failure: reason
  });
  var hasOwn = (value, key) => Object.hasOwn(value, key);

  // frontend/documentCommands/listReducer.ts
  var applyListCommand = (document2, descriptor, operation, command, context) => {
    const fields = command;
    const ownerField = OWNER_ID_FIELD[descriptor.owner];
    const ownerId = ownerField === null ? "" : fields[ownerField];
    const owner = descriptor.owner === "document" ? document2 : descriptor.owner === "clip" ? findClip(document2, ownerId) : findTrack(document2, ownerId);
    if (!owner) {
      return failure(document2, "missing-target");
    }
    const target = descriptor.reconcilesClipIdentity ? clone2(owner) : owner;
    const items = descriptor.collection(
      target
    );
    const id = fields[descriptor.idField];
    const beforeId = fields[descriptor.beforeIdField] ?? null;
    let mutated;
    switch (operation) {
      case "add": {
        const entity = fields[descriptor.entityField];
        const invalid = invalidNewEntity(
          document2,
          entity
        );
        if (invalid) {
          return invalid;
        }
        mutated = addBefore(items, clone2(entity), beforeId);
        break;
      }
      case "remove":
        mutated = removeById(items, id);
        break;
      case "move":
        mutated = moveBefore(items, id, beforeId);
        break;
      case "patch": {
        const patch = fields.patch;
        if (descriptor.reservedKeys.some(
          (key) => key !== "id" && hasOwn(patch, key)
        )) {
          return failure(document2, "architecture-invariant");
        }
        mutated = patchById(items, id, patch);
        break;
      }
    }
    if (!mutated) {
      return failure(document2, "missing-target");
    }
    if (descriptor.reconcilesClipIdentity) {
      const candidate = target;
      if (!reconcileClipArchitectureIdentity(
        candidate,
        context.architectureCatalog
      )) {
        return failure(document2, "architecture-invariant");
      }
      document2.clips[document2.clips.indexOf(owner)] = candidate;
    }
    return success(document2);
  };

  // frontend/documentCommands.ts
  var list = (document2, entity, operation, command, context) => applyListCommand(
    document2,
    LIST_ENTITIES[entity],
    operation,
    command,
    context
  );
  var assertNever = (command) => {
    throw new Error(`Unhandled document command: ${JSON.stringify(command)}`);
  };
  var reduceDocumentCommand = (source, command, context = { architectureCatalog: null }) => {
    const document2 = clone2(source);
    switch (command.type) {
      case "batch": {
        let current = document2;
        for (const child of command.commands) {
          const result = reduceDocumentCommand(current, child, context);
          if (!result.applied) {
            return failure(
              clone2(source),
              result.failure
            );
          }
          current = result.document;
        }
        return success(current);
      }
      case "root.patch": {
        Object.assign(document2, clone2(command.patch));
        return success(document2);
      }
      case "clip.add": {
        const invalid = invalidNewEntity(document2, command.clip);
        if (invalid) return invalid;
        const addedClip = clone2(command.clip);
        if (!reconcileClipArchitectureIdentity(
          addedClip,
          context.architectureCatalog
        )) {
          return failure(document2, "architecture-invariant");
        }
        if (!addBefore(document2.clips, addedClip, command.beforeClipId)) {
          return failure(clone2(source), "missing-target");
        }
        return success(document2);
      }
      case "clip.remove":
        return list(document2, "clip", "remove", command, context);
      case "clip.move":
        return list(document2, "clip", "move", command, context);
      case "clip.patch": {
        if (hasOwn(command.patch, "architecture") || hasOwn(command.patch, "modelProfileId")) {
          return failure(document2, "architecture-invariant");
        }
        const clip = findClip(document2, command.clipId);
        if (!clip) {
          return failure(document2, "missing-target");
        }
        const candidate = clone2(clip);
        Object.assign(candidate, clone2(command.patch), { id: clip.id });
        if (hasOwn(command.patch, "sourceVideo") && !reconcileClipArchitectureIdentity(
          candidate,
          context.architectureCatalog
        )) {
          return failure(document2, "architecture-invariant");
        }
        document2.clips[document2.clips.indexOf(clip)] = candidate;
        return success(document2);
      }
      case "clip.convert-architecture": {
        const clipIndex = document2.clips.findIndex(
          (clip2) => clip2.id === command.clipId
        );
        const clip = document2.clips[clipIndex];
        const target = command.target;
        if (!clip) {
          return failure(document2, "missing-target");
        }
        const conversion = planArchitectureConversion(
          clip,
          target,
          context.architectureCatalog
        );
        if (!conversion) {
          return failure(document2, "invalid-architecture-conversion");
        }
        const converted = conversion.clip;
        if (!reconcileClipArchitectureIdentity(
          converted,
          context.architectureCatalog
        )) {
          return failure(document2, "invalid-architecture-conversion");
        }
        document2.clips[clipIndex] = converted;
        forceCrossArchitectureCutsForConversion(document2.clips);
        return success(document2);
      }
      case "stage.add":
        return list(document2, "stage", "add", command, context);
      case "stage.retarget-model": {
        const clip = findClip(document2, command.clipId);
        const stage = clip?.stages.find(
          (candidate2) => candidate2.id === command.stageId
        );
        if (!clip || !stage) {
          return failure(document2, "missing-target");
        }
        const target = resolveArchitectureRetarget(
          command.target,
          context.architectureCatalog
        );
        if (!target) {
          return failure(document2, "architecture-invariant");
        }
        const candidate = clone2(clip);
        const candidateStage = candidate.stages.find(
          (entry) => entry.id === command.stageId
        );
        if (!candidateStage) {
          return failure(document2, "missing-target");
        }
        candidateStage.model = target.model;
        candidateStage.modelProfileId = target.modelProfileId;
        if (!reconcileClipArchitectureIdentity(
          candidate,
          context.architectureCatalog
        )) {
          return failure(document2, "architecture-invariant");
        }
        document2.clips[document2.clips.indexOf(clip)] = candidate;
        return success(document2);
      }
      case "stage.remove":
        return list(document2, "stage", "remove", command, context);
      case "stage.move":
        return list(document2, "stage", "move", command, context);
      case "stage.patch":
        return list(document2, "stage", "patch", command, context);
      case "ref.add":
        return list(document2, "ref", "add", command, context);
      case "ref.remove":
        return list(document2, "ref", "remove", command, context);
      case "ref.move":
        return list(document2, "ref", "move", command, context);
      case "ref.patch":
        return list(document2, "ref", "patch", command, context);
      case "prompt-window.add":
        return list(document2, "promptWindow", "add", command, context);
      case "prompt-window.remove":
        return list(document2, "promptWindow", "remove", command, context);
      case "prompt-window.move":
        return list(document2, "promptWindow", "move", command, context);
      case "prompt-window.patch":
        return list(document2, "promptWindow", "patch", command, context);
      case "retake.add": {
        const clip = findClip(document2, command.clipId);
        if (!clip) return failure(document2, "missing-target");
        if (clip.retake) {
          return failure(document2, "retake-already-exists");
        }
        const invalid = invalidNewEntity(document2, command.retake);
        if (invalid) return invalid;
        clip.retake = clone2(command.retake);
        return success(document2);
      }
      case "retake.remove": {
        const clip = findClip(document2, command.clipId);
        if (!clip || clip.retake?.id !== command.retakeId) {
          return failure(document2, "missing-target");
        }
        clip.retake = null;
        return success(document2);
      }
      case "retake.patch": {
        const clip = findClip(document2, command.clipId);
        if (!clip || clip.retake?.id !== command.retakeId) {
          return failure(document2, "missing-target");
        }
        Object.assign(clip.retake, clone2(command.patch), {
          id: command.retakeId
        });
        return success(document2);
      }
      case "audio-track.add":
        return list(document2, "audioTrack", "add", command, context);
      case "audio-track.remove":
        return list(document2, "audioTrack", "remove", command, context);
      case "audio-track.move":
        return list(document2, "audioTrack", "move", command, context);
      case "audio-track.patch":
        return list(document2, "audioTrack", "patch", command, context);
      case "audio-span.add":
        return list(document2, "audioSpan", "add", command, context);
      case "audio-span.remove":
        return list(document2, "audioSpan", "remove", command, context);
      case "audio-span.move":
        return list(document2, "audioSpan", "move", command, context);
      case "audio-span.patch":
        return list(document2, "audioSpan", "patch", command, context);
      default:
        return assertNever(command);
    }
  };

  // frontend/store.ts
  var createTimelineStore = (deps) => {
    let canonical = null;
    let cachedToken = null;
    let syncedToken = null;
    let lastGoodSerialized = "";
    let documentRevision = 0;
    const subscribers = /* @__PURE__ */ new Set();
    const parseCurrent = () => {
      const serialized = deps.readDataParam() || lastGoodSerialized;
      if (!serialized) {
        return deps.parseEmpty();
      }
      const parsed = deps.parse(serialized);
      if (parsed) {
        lastGoodSerialized = serialized;
        return parsed;
      }
      if (serialized !== lastGoodSerialized && lastGoodSerialized) {
        const fallback = deps.parse(lastGoodSerialized);
        if (fallback) {
          return fallback;
        }
      }
      return deps.parseEmpty();
    };
    const revalidate = () => {
      const token = deps.readToken();
      if (canonical && token === cachedToken) {
        return canonical;
      }
      canonical = parseCurrent();
      cachedToken = token;
      documentRevision++;
      if (syncedToken === null) {
        syncedToken = token;
      }
      return canonical;
    };
    const notify2 = (meta) => {
      const state = canonical;
      if (!state) {
        return;
      }
      const snapshot = structuredClone(state);
      for (const cb of [...subscribers]) {
        try {
          cb(snapshot, meta);
        } catch (error) {
          videoStagesDebugLog("store", "subscriber notification failed", {
            error,
            origin: meta.origin
          });
        }
      }
    };
    const commit = (state, origin, notifyDomChange, hint) => {
      let serialized;
      try {
        serialized = deps.serialize(state);
      } catch {
        return null;
      }
      if (!deps.parse(serialized)) {
        return null;
      }
      deps.writeQuiet(state, serialized);
      lastGoodSerialized = serialized;
      canonical = deps.parse(serialized);
      if (!canonical) {
        throw new Error(
          "VideoStages carrier adapter rejected a preflighted commit."
        );
      }
      cachedToken = deps.readToken();
      syncedToken = cachedToken;
      documentRevision++;
      if (notifyDomChange) {
        deps.notifyHost();
      }
      notify2({ origin, hint });
      return serialized;
    };
    const dispatch = (command, origin, notifyDomChange, expectedRevision, hint) => {
      const source = structuredClone(revalidate());
      if (expectedRevision !== void 0 && expectedRevision !== documentRevision) {
        return {
          applied: false,
          failure: "stale-revision",
          revision: documentRevision
        };
      }
      ensureAuthoringDocumentIdentity(source);
      const reduced = reduceDocumentCommand(source, command, {
        architectureCatalog: deps.architectureCatalog?.() ?? null
      });
      if (!reduced.applied) {
        return {
          applied: false,
          failure: reduced.failure,
          revision: documentRevision
        };
      }
      if (command.type === "batch" && command.commands.length === 0) {
        return {
          applied: true,
          revision: documentRevision
        };
      }
      const serialized = commit(
        reduced.document,
        origin,
        notifyDomChange,
        hint
      );
      if (serialized === null) {
        return {
          applied: false,
          failure: "invalid-serialized-state",
          revision: documentRevision
        };
      }
      return {
        applied: true,
        revision: documentRevision
      };
    };
    const syncFromCarrier = () => {
      const token = deps.readToken();
      if (canonical && syncedToken !== null && token === syncedToken) {
        return false;
      }
      revalidate();
      syncedToken = cachedToken;
      if (canonical) {
        deps.writeDurable?.(canonical);
      }
      notify2({ origin: "external" });
      return true;
    };
    return {
      getState: () => structuredClone(revalidate()),
      getSnapshot: () => ({
        state: structuredClone(revalidate()),
        revision: documentRevision
      }),
      revision: () => {
        revalidate();
        return documentRevision;
      },
      dispatch,
      syncFromCarrier,
      subscribe: (cb) => {
        subscribers.add(cb);
        return () => {
          subscribers.delete(cb);
        };
      },
      invalidate: () => {
        cachedToken = null;
      },
      resetForTests: () => {
        canonical = null;
        cachedToken = null;
        syncedToken = null;
        lastGoodSerialized = "";
        documentRevision = 0;
        subscribers.clear();
      }
    };
  };

  // frontend/uiState.ts
  var UI_STATE_KEY = "videostages_ui_state";
  var UI_STATE_SCHEMA_VERSION = 2;
  var serializeUiState = (clips) => {
    ensureClipEntityIdentities(clips);
    const state = {
      schemaVersion: UI_STATE_SCHEMA_VERSION,
      clips: clips.map((clip) => ({
        id: clip.id,
        hue: typeof clip.hue === "number" ? clip.hue : null,
        promptWindows: clip.promptWindows.map((window2) => ({
          id: window2.id,
          prompt: window2.prompt,
          start: window2.start,
          duration: window2.duration
        }))
      }))
    };
    return JSON.stringify(state);
  };
  var promptWindowKey = (window2) => JSON.stringify([window2.prompt, window2.start, window2.duration]);
  var restorePromptWindowIds = (stored, clip) => {
    const rawWindows = Array.isArray(stored.promptWindows) ? stored.promptWindows : [];
    const idsByWindow = /* @__PURE__ */ new Map();
    for (const rawWindow of rawWindows) {
      if (!isRecord(rawWindow) || typeof rawWindow.id !== "string" || !rawWindow.id.trim() || typeof rawWindow.prompt !== "string" || typeof rawWindow.start !== "number" || typeof rawWindow.duration !== "number") {
        continue;
      }
      const key = promptWindowKey({
        prompt: rawWindow.prompt,
        start: rawWindow.start,
        duration: rawWindow.duration
      });
      const ids = idsByWindow.get(key) ?? [];
      ids.push(rawWindow.id.trim());
      idsByWindow.set(key, ids);
    }
    for (const window2 of clip.promptWindows) {
      const ids = idsByWindow.get(promptWindowKey(window2));
      const id = ids?.shift();
      if (id) {
        window2.id = id;
      }
    }
  };
  var applyUiStateFrom = (raw, clips) => {
    if (!raw) {
      return;
    }
    const parsed = safeJsonParse(raw, null);
    const storedClips = isRecord(parsed) && Array.isArray(parsed.clips) ? parsed.clips : [];
    const storedById = /* @__PURE__ */ new Map();
    for (const stored of storedClips) {
      if (isRecord(stored) && typeof stored.id === "string" && stored.id.trim()) {
        storedById.set(stored.id.trim(), stored);
      }
    }
    for (let i = 0; i < clips.length; i++) {
      const clipId = clips[i].id;
      const positional = storedClips[i];
      const stored = (clipId ? storedById.get(clipId) : void 0) ?? (isRecord(positional) && !positional.id ? positional : void 0);
      if (!isRecord(stored)) {
        continue;
      }
      if (typeof stored.hue === "number" && Number.isFinite(stored.hue)) {
        clips[i].hue = stored.hue;
      }
      restorePromptWindowIds(stored, clips[i]);
    }
  };
  var applyUiState = (clips) => {
    try {
      applyUiStateFrom(localStorage.getItem(UI_STATE_KEY), clips);
    } catch {
    }
  };
  var saveUiState = (clips) => {
    try {
      localStorage.setItem(UI_STATE_KEY, serializeUiState(clips));
    } catch {
    }
  };

  // frontend/persistence/durableAuthoringState.ts
  var DURABLE_AUTHORING_KEY = "videostages_authoring_state_v1";
  var DURABLE_AUTHORING_VERSION = 1;
  var isFiniteNumber = (value) => typeof value === "number" && Number.isFinite(value);
  var readPrompt = (value) => {
    if (!isRecord(value) || typeof value.prompt !== "string") {
      return null;
    }
    if (!Array.isArray(value.windows)) {
      return null;
    }
    const windows = [];
    for (const window2 of value.windows) {
      if (!isRecord(window2) || typeof window2.prompt !== "string" || !isFiniteNumber(window2.start) || !isFiniteNumber(window2.duration)) {
        return null;
      }
      windows.push({
        prompt: window2.prompt,
        start: window2.start,
        duration: window2.duration
      });
    }
    return { prompt: value.prompt, windows };
  };
  var loadDurableAuthoringState = () => {
    try {
      const raw = localStorage.getItem(DURABLE_AUTHORING_KEY);
      if (!raw) {
        return null;
      }
      const parsed = JSON.parse(raw);
      if (!isRecord(parsed) || parsed.version !== DURABLE_AUTHORING_VERSION || typeof parsed.document !== "string" || !Array.isArray(parsed.prompts)) {
        return null;
      }
      const prompts = [];
      for (const prompt of parsed.prompts) {
        const normalized = readPrompt(prompt);
        if (!normalized) {
          return null;
        }
        prompts.push(normalized);
      }
      return { document: parsed.document, prompts };
    } catch (error) {
      videoStagesDebugLog(
        "persistence",
        "durable authoring state read failed",
        { error }
      );
      return null;
    }
  };
  var saveDurableAuthoringState = (state) => {
    try {
      const snapshot = {
        document: serializeStateForDurableStorage(state),
        prompts: state.clips.map((clip) => ({
          prompt: clip.prompt,
          windows: clip.promptWindows.map((window2) => ({
            prompt: window2.prompt,
            start: window2.start,
            duration: window2.duration
          }))
        }))
      };
      const stored = {
        version: DURABLE_AUTHORING_VERSION,
        ...snapshot
      };
      localStorage.setItem(DURABLE_AUTHORING_KEY, JSON.stringify(stored));
      return snapshot;
    } catch (error) {
      console.warn(
        "VideoStages: durable authoring state could not be saved.",
        error
      );
      videoStagesDebugLog(
        "persistence",
        "durable authoring state write failed",
        { error }
      );
      return null;
    }
  };
  var clearDurableAuthoringState = () => {
    try {
      localStorage.removeItem(DURABLE_AUTHORING_KEY);
    } catch {
    }
  };

  // frontend/persistence/carrierAdapter.ts
  var BOOT_CARRIER_PROTECTION_MS = 2e3;
  var hydrationComplete = false;
  var hydratedDataInput = null;
  var hydratedPromptInput = null;
  var hydratedSnapshot = null;
  var hydratedPromptCarrierValue = null;
  var pendingHydratedPrompts = null;
  var protectOverriddenBootCarrier = false;
  var bootCarrierProtectionDeadline = 0;
  var overlayPromptAndUiState = (clips) => {
    ensureClipEntityIdentities(clips);
    const { sections, windows } = parseClipPrompts(
      getPromptInput()?.value ?? ""
    );
    for (let index = 0; index < clips.length; index++) {
      clips[index].prompt = sections.get(index) ?? "";
      clips[index].promptWindows = (windows.get(index) ?? []).map(
        (window2) => ({
          prompt: window2.prompt,
          start: window2.start,
          duration: window2.duration
        })
      );
    }
    applyUiState(clips);
    ensureClipEntityIdentities(clips);
    assignMissingHues(clips);
  };
  var inheritedDims = () => {
    const defaults = getRootDefaults();
    return {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
  };
  var parse = (serialized) => {
    const decoded = decodeStoredDocument(serialized, inheritedDims());
    if (!decoded) return null;
    overlayPromptAndUiState(decoded.clips);
    return createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks);
  };
  var parseEmpty = () => {
    const clips = [];
    overlayPromptAndUiState(clips);
    return createRootConfig(resolveRootDims(inheritedDims(), {}), clips);
  };
  var applyPendingHydratedPrompts = () => {
    if (!pendingHydratedPrompts || !getPromptInput()) {
      return;
    }
    writeClipPrompts(pendingHydratedPrompts);
    hydratedPromptCarrierValue = getPromptInput()?.value ?? null;
    pendingHydratedPrompts = null;
  };
  var restoreDurableSnapshot = (snapshot) => {
    const decoded = decodeStoredDocument(snapshot.document, inheritedDims());
    if (!decoded || snapshot.prompts.length !== decoded.clips.length) {
      return false;
    }
    writeDataParam(snapshot.document);
    hydratedSnapshot = snapshot;
    pendingHydratedPrompts = snapshot.prompts;
    applyPendingHydratedPrompts();
    return true;
  };
  var writeDurable = (state) => {
    const saved = saveDurableAuthoringState(state);
    if (saved) {
      hydratedSnapshot = saved;
    }
  };
  var releaseBootCarrierProtection = () => {
    protectOverriddenBootCarrier = false;
    bootCarrierProtectionDeadline = 0;
  };
  var ensureHydratedCarrier = () => {
    const dataInput = getDataInput();
    if (!dataInput) {
      return;
    }
    if (hydrationComplete) {
      const promptInput = getPromptInput();
      if (dataInput !== hydratedDataInput) {
        hydratedDataInput = dataInput;
      }
      if (promptInput !== hydratedPromptInput) {
        hydratedPromptInput = promptInput;
      }
      const protectingBootCarrier = protectOverriddenBootCarrier && Date.now() <= bootCarrierProtectionDeadline;
      if (!protectingBootCarrier) {
        protectOverriddenBootCarrier = false;
      }
      if (protectingBootCarrier && hydratedSnapshot && dataInput.value !== hydratedSnapshot.document) {
        restoreDurableSnapshot(hydratedSnapshot);
      }
      if (protectingBootCarrier && hydratedSnapshot && promptInput && promptInput.value !== hydratedPromptCarrierValue) {
        pendingHydratedPrompts = hydratedSnapshot.prompts;
      }
      applyPendingHydratedPrompts();
      return;
    }
    hydrationComplete = true;
    hydratedDataInput = dataInput;
    hydratedPromptInput = getPromptInput();
    const durable = loadDurableAuthoringState();
    if (durable && restoreDurableSnapshot(durable)) {
      protectOverriddenBootCarrier = true;
      bootCarrierProtectionDeadline = Date.now() + BOOT_CARRIER_PROTECTION_MS;
      return;
    }
    if (durable) {
      clearDurableAuthoringState();
    }
    const existing = readDataParam();
    if (!existing) {
      return;
    }
    const state = parse(existing);
    if (state) {
      writeDurable(state);
    }
  };
  var readDataParam2 = () => {
    ensureHydratedCarrier();
    return readDataParam();
  };
  var writeQuiet = (state, serialized) => {
    releaseBootCarrierProtection();
    ensureAuthoringDocumentIdentity(state);
    assignMissingHues(state.clips);
    writeDataParam(serialized);
    writeClipPrompts(
      state.clips.map((clip) => ({
        prompt: clip.prompt,
        windows: clip.promptWindows
      }))
    );
    saveUiState(state.clips);
    writeDurable(state);
  };
  var timelineCarrierAdapter = {
    readToken: () => {
      ensureHydratedCarrier();
      return `${readStateToken()}\0${readInheritedDimsSignature()}`;
    },
    readDataParam: readDataParam2,
    parse,
    parseEmpty,
    serialize: serializeStateForStorage,
    writeQuiet,
    writeDurable,
    notifyHost: notifyCarrierChanged
  };
  var dataCarrierNeedsCanonicalIdRepair = () => storedDocumentNeedsCanonicalIdRepair(readDataParam2());

  // frontend/persistence/repository.ts
  var store = createTimelineStore({
    architectureCatalog: () => getRootDefaults().modelCatalog,
    ...timelineCarrierAdapter
  });
  var getTimelineStore = () => store;
  var getState = () => store.getState();
  var throwSaveFailure = (phase, error) => {
    const detail = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
    console.error(`[VideoStages persistence] saveState ${phase} failed`, error);
    videoStagesDebugLog("persistence", `saveState ${phase} failed`, {
      detail
    });
    throw new Error(`VideoStages saveState ${phase} failed: ${detail}`, {
      cause: error
    });
  };
  var saveRequestedState = (requestedInput, options, snapshot = store.getSnapshot()) => {
    const requested = structuredClone(requestedInput);
    ensureAuthoringDocumentIdentity(requested);
    assignMissingHues(requested.clips);
    const before = structuredClone(snapshot.state);
    ensureAuthoringDocumentIdentity(before);
    assignMissingHues(before.clips);
    const diffCommand = (() => {
      try {
        return diffDocuments(before, requested, {
          architectureCatalog: getRootDefaults().modelCatalog
        });
      } catch (error) {
        return throwSaveFailure("diff", error);
      }
    })();
    const command = diffCommand.commands.length === 0 && dataCarrierNeedsCanonicalIdRepair() ? {
      type: "batch",
      commands: [
        {
          type: "root.patch",
          patch: { schemaVersion: requested.schemaVersion }
        }
      ]
    } : diffCommand;
    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
      command,
      options?.origin ?? "timeline",
      willNotifyDom,
      options?.expectedRevision ?? snapshot.revision,
      options?.valueOnly ? "value-only" : void 0
    );
    if (!result.applied) {
      throwSaveFailure("dispatch", result.failure ?? "unknown failure");
    }
    videoStagesDebugLog("persistence", "saveState", {
      notifyDomChange: options?.notifyDomChange,
      willNotifyDom,
      commandCount: command.commands.length,
      revision: result.revision
    });
  };
  var saveState = (state, options) => saveRequestedState(state, options);
  var dispatchDocumentCommand = (command, options) => {
    const willNotifyDom = options?.notifyDomChange !== false;
    const result = store.dispatch(
      command,
      options?.origin ?? "timeline",
      willNotifyDom,
      options?.expectedRevision,
      options?.valueOnly ? "value-only" : void 0
    );
    videoStagesDebugLog("persistence", "dispatchDocumentCommand", {
      command: command.type,
      applied: result.applied,
      failure: result.failure,
      revision: result.revision,
      willNotifyDom
    });
    return result;
  };
  var getClips = () => getState().clips;
  var saveClips = (clips, options) => {
    videoStagesDebugLog("persistence", "saveClips", {
      clipCount: clips.length
    });
    const snapshot = store.getSnapshot();
    const state = structuredClone(snapshot.state);
    state.clips = structuredClone(clips);
    const notifyDomChange = options?.notifyDomChange !== void 0 ? options.notifyDomChange : isVideoStagesEnabled();
    saveRequestedState(state, { ...options, notifyDomChange }, snapshot);
  };

  // frontend/refineVideoButton.ts
  var refineNeedsExtraStageMessage = (skipCount) => `Refine Video needs Clip 0 to have at least one active stage after Stage ${skipCount - 1} (for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;
  var countActiveStagesInMetadataClip0 = (videostagesJson) => {
    const parsed = safeJsonParse(videostagesJson, null);
    if (!isRecord(parsed)) {
      return 0;
    }
    const clips = readProp(parsed, "clips");
    if (!Array.isArray(clips) || clips.length === 0) {
      return 0;
    }
    const clip0 = clips[0];
    if (!isRecord(clip0) || readProp(clip0, "skipped") === true) {
      return 0;
    }
    const stages = readProp(clip0, "stages");
    if (!Array.isArray(stages)) {
      return 0;
    }
    return stages.filter(
      (stage) => !(isRecord(stage) && readProp(stage, "skipped") === true)
    ).length;
  };
  var hasRefinementWorkToDo = (state, enabled, skipCount) => {
    if (!enabled) {
      return false;
    }
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
      return false;
    }
    const activeStages = clip0.stages.filter((stage) => !stage.skipped);
    return activeStages.length > skipCount;
  };
  var refineVideoButton = () => {
    const description = "Re-runs VideoStages using this video as the source for Clip 0 (skips the first N stage samplers, where N is read from the source video's metadata). Requires an extra stage beyond those.";
    getVideoStagesHostBridge().registerRefineVideoButton(
      (src) => {
        const host = getVideoStagesHostBridge();
        const run = async () => {
          let parsedMetadata = null;
          const currentMetadata = host.getCurrentMediaMetadata();
          if (currentMetadata) {
            try {
              const readable = host.interpretMediaMetadata(currentMetadata);
              parsedMetadata = readable ? JSON.parse(readable) : null;
            } catch (error) {
              console.warn(
                "VideoStages: failed to parse source video metadata",
                error
              );
            }
          }
          const params = isRecord(parsedMetadata) ? readProp(parsedMetadata, "sui_image_params") : null;
          const sourceVideostages = isRecord(params) ? readProp(params, "videostages") : void 0;
          const skipCount = Math.max(
            1,
            typeof sourceVideostages === "string" ? countActiveStagesInMetadataClip0(sourceVideostages) : 0
          );
          if (!hasRefinementWorkToDo(
            getState(),
            isVideoStagesEnabled(),
            skipCount
          )) {
            host.showError(refineNeedsExtraStageMessage(skipCount));
            return;
          }
          const videoDataUrl = await host.toDataUrl(src);
          const inputOverrides = {
            videostagesrefinesourcevideo: videoDataUrl,
            videostagesrefineskipstages: skipCount,
            images: 1
          };
          const seed = isRecord(params) ? readProp(params, "seed") : void 0;
          if (typeof seed === "number") {
            inputOverrides.seed = seed;
          }
          host.generate(inputOverrides);
        };
        void run();
      },
      description
    );
  };

  // frontend/architectures/policy/identity.ts
  var reconcileSourcedClipIdentity = (clip, catalog) => {
    reconcileClipArchitectureIdentity(clip, catalog);
  };

  // frontend/architectures/policy.ts
  var createCapabilityViewResolver = (catalog, scope = {}) => {
    const architectureById = new Map(
      catalog.architectures.map((entry) => [entry.id, entry])
    );
    const clipStage = createClipStageCapabilityViews(architectureById, scope);
    const boundaries = createBoundaryCapabilityViews(
      architectureById,
      clipStage.forClip
    );
    return {
      catalog,
      ...clipStage,
      ...boundaries,
      executableClipIndexes
    };
  };

  // frontend/architectures/currentPolicy.ts
  var currentCapabilityViewResolver = () => createCapabilityViewResolver(getRootDefaults().modelCatalog);

  // frontend/architectures/conversion/entryModePolicy.ts
  var architectureSupportsClipStart = (capabilities, clip, generatedEntryMode) => {
    const modes = capabilities.entryModes;
    if (clip.sourceVideo !== null) {
      return modes.includes("source-video") || modes.includes("refine-video");
    }
    const hasInitialReference = clip.refs.some(
      (reference) => reference.fromEnd !== true && Math.max(1, Math.round(reference.frame)) === 1
    );
    return hasInitialReference ? modes.includes("image-to-video") : modes.includes(generatedEntryMode);
  };

  // frontend/architectures/diagnostics.ts
  var issue = (code, message, clipIdx) => ({ severity: "error", code, message, clipIdx });
  var persistedCapabilityIssues = (clip, clipIdx, capabilities) => {
    const diagnostics = [];
    const supports = (feature, value) => architectureFeatureSupport(feature, { capabilities, ...value });
    const unsupported = (active, key, label) => {
      if (active) {
        diagnostics.push(
          issue(
            `architecture.unsupported.${key}`,
            `${label} is persisted on Clip ${clipIdx}, but its architecture does not support it. Remove it or explicitly convert the clip.`,
            clipIdx
          )
        );
      }
    };
    unsupported(
      !supports("multiStage") && activeStageCount(clip) > 1,
      "multi-stage",
      "Multiple active stages"
    );
    unsupported(
      !supports("frameReferences") && clip.refs.length > 0,
      "frame-references",
      "Frame references"
    );
    unsupported(
      !supports("icLora") && clip.icLoras.length > 0,
      "ic-lora",
      "IC-LoRA"
    );
    unsupported(
      !supports("hdr") && clip.icLoras.some(
        (entry) => isArchitectureHdrFeature(clip.architecture, entry)
      ),
      "hdr",
      "HDR"
    );
    unsupported(
      !supports("retake") && clip.retake !== null,
      "retake",
      "Retake"
    );
    unsupported(
      !supports("majorPrompt") && clip.prompt.trim().length > 0,
      "major-prompt",
      "Major prompt"
    );
    unsupported(
      !supports("sourceVideo") && clip.sourceVideo !== null,
      "source-video",
      "Source video"
    );
    unsupported(
      !supports("promptRelay") && clip.promptWindows.length > 0,
      "prompt-relay",
      "Prompt relay"
    );
    unsupported(
      !supports("stageLoras") && clip.stages.some(
        (stage) => clip.loras.some(
          (_, index) => (stage.loraWeights[index] ?? 1) !== 0
        )
      ),
      "stage-loras",
      "LoRAs"
    );
    unsupported(
      clip.stages.some(
        (stage) => stage.upscale !== 1 && !supports("upscale", { upscaleMethod: stage.upscaleMethod })
      ),
      "upscale",
      "Stage upscaling"
    );
    const sourceKind = audioSourceKind(clip.audioSource);
    unsupported(
      !supports("clipAudio", { audioSource: clip.audioSource }) && (sourceKind !== "Native" || clip.uploadedAudio !== null || clip.saveAudioTrack || clip.reuseAudio || clip.clipLengthFromAudio || clip.clipLengthFromControlNet),
      "audio-source",
      `Audio source '${sourceKind}'`
    );
    if (clip.clipLengthFromAudio && !canUseClipLengthFromAudio(clip.audioSource)) {
      diagnostics.push(
        issue(
          "architecture.unusable.clip-length-from-audio",
          `Clip length from audio is persisted on Clip ${clipIdx}, but audio source '${sourceKind}' cannot supply a length. Turn it off or pick a source that can.`,
          clipIdx
        )
      );
    }
    if (clip.clipLengthFromControlNet && !hasArchitectureSlotSourcedIcLora(clip.architecture, clip.icLoras)) {
      diagnostics.push(
        issue(
          "architecture.unusable.clip-length-from-control-net",
          `Clip length from the control signal is persisted on Clip ${clipIdx}, but no IC-LoRA supplies one. Turn it off or add a slot-sourced IC-LoRA.`,
          clipIdx
        )
      );
    }
    return diagnostics;
  };
  var deriveArchitectureDiagnostics = (clips, catalog, generatedEntryMode = "text-to-video") => {
    const diagnostics = [];
    const architectureById = new Map(
      catalog.architectures.map((entry) => [entry.id, entry])
    );
    const boundaries = createBoundaryCapabilityViews(
      architectureById,
      createClipStageCapabilityViews(architectureById).forClip
    );
    const modelByName = new Map(
      catalog.entries.map((entry) => [entry.value, entry])
    );
    clips.forEach((clip, clipIdx) => {
      const sourceOnly = activeStageCount(clip) === 0 && clip.sourceVideo !== null;
      if (sourceOnly) {
        if (clip.architecture !== NONE_ARCHITECTURE_ID || clip.modelProfileId !== NONE_ARCHITECTURE_ID) {
          diagnostics.push(
            issue(
              "architecture.source-only-requires-none",
              `Source-only Clip ${clipIdx} must use architecture and profile 'none'.`,
              clipIdx
            )
          );
        }
      }
      const architecture = sourceOnly ? architectureById.get("none") : architectureById.get(clip.architecture);
      if (!architecture && !sourceOnly) {
        diagnostics.push(
          issue(
            "architecture.unknown",
            `Clip ${clipIdx} uses unknown architecture '${clip.architecture}'. Its persisted settings were preserved, but generation is blocked.`,
            clipIdx
          )
        );
      } else if (architecture) {
        diagnostics.push(
          ...persistedCapabilityIssues(
            clip,
            clipIdx,
            architecture.capabilities
          )
        );
        if (!sourceOnly && activeStageCount(clip) > 0 && !architectureSupportsClipStart(
          architecture.capabilities,
          clip,
          generatedEntryMode
        )) {
          diagnostics.push(
            issue(
              "architecture.entry-mode-unsupported",
              `Clip ${clipIdx} cannot start from the current ${generatedEntryMode} host entry with architecture '${architecture.id}'.`,
              clipIdx
            )
          );
        }
      }
      let dormantArchitecture = null;
      clip.stages.forEach((stage, stageIdx) => {
        const resolved = modelByName.get(stage.model);
        if (!resolved?.architectureId || !resolved.modelProfileId) {
          diagnostics.push(
            issue(
              "architecture.model-unknown",
              `Clip ${clipIdx} Stage ${stageIdx} model '${stage.model}' is not in the architecture catalog.`,
              clipIdx
            )
          );
          return;
        }
        if (sourceOnly && dormantArchitecture === null) {
          dormantArchitecture = resolved.architectureId;
        }
        const mixedDormant = sourceOnly && dormantArchitecture !== null && resolved.architectureId !== dormantArchitecture;
        if (mixedDormant || !sourceOnly && resolved.architectureId !== clip.architecture) {
          diagnostics.push(
            issue(
              "architecture.mixed-stage",
              sourceOnly ? `Source-only Clip ${clipIdx} has dormant stages from multiple architectures; Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}'.` : `Clip ${clipIdx} is locked to '${clip.architecture}', but Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}'.`,
              clipIdx
            )
          );
        }
        if (stage.modelProfileId !== resolved.modelProfileId || !sourceOnly && stageIdx === 0 && clip.modelProfileId !== resolved.modelProfileId) {
          diagnostics.push(
            issue(
              "architecture.profile-mismatch",
              `Clip ${clipIdx} Stage ${stageIdx} profile identity does not match model '${stage.model}'.`,
              clipIdx
            )
          );
        }
        const resolvedProfile = architectureById.get(resolved.architectureId)?.profiles.find(
          (profile) => profile.id === resolved.modelProfileId
        );
        if (clip.loras.some(
          (_, index) => (stage.loraWeights[index] ?? 1) !== 0
        ) && resolvedProfile && !resolvedProfile.capabilities.includes("normal-lora")) {
          diagnostics.push(
            issue(
              "architecture.unsupported.stage-loras-profile",
              `Clip ${clipIdx} Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} has normal LoRAs, but model profile '${resolvedProfile.id}' does not support them.`,
              clipIdx
            )
          );
        }
      });
    });
    for (const seam of executableBoundaries(clips)) {
      const left = { clip: clips[seam.leftIdx], clipIdx: seam.leftIdx };
      const right = { clip: clips[seam.rightIdx], clipIdx: seam.rightIdx };
      if (left.clip.architecture !== right.clip.architecture && left.clip.boundaryOut !== "cut") {
        diagnostics.push(
          issue(
            "architecture.cross-boundary-cut-only",
            `Clip ${left.clipIdx} → ${right.clipIdx} crosses architectures; '${left.clip.boundaryOut}' is preserved for repair, but only cut can execute.`,
            left.clipIdx
          )
        );
        continue;
      }
      const boundary = boundaries.forBoundary(
        left.clip,
        right.clip,
        left.clipIdx,
        right.clipIdx
      );
      if (boundary.effective(left.clip.boundaryOut) !== left.clip.boundaryOut) {
        const reason = boundary.reason ? ` ${boundary.reason}` : ` Its requested value is preserved for repair, but only '${boundary.effective(left.clip.boundaryOut)}' can execute.`;
        diagnostics.push(
          issue(
            "architecture.boundary-unsupported",
            `Clip ${left.clipIdx} cannot execute a '${left.clip.boundaryOut}' boundary into Clip ${right.clipIdx}.${reason}`,
            left.clipIdx
          )
        );
      }
    }
    return diagnostics;
  };

  // frontend/authoringDiagnostics.ts
  var diagnostic = (severity, code, message, clipIdx) => ({ severity, code, message, clipIdx });
  var deriveAuthoringDiagnostics = (clips, context = {}) => {
    const diagnostics = [];
    if (context.catalog) {
      diagnostics.push(
        ...deriveArchitectureDiagnostics(
          clips,
          context.catalog,
          context.generatedEntryMode
        )
      );
    }
    const executable = clips.map((clip, clipIdx) => ({ clip, clipIdx })).filter(({ clip }) => isExecutableClip(clip));
    for (const { clip, clipIdx } of executable) {
      const descriptor = architectureDescriptor(
        context.catalog,
        clip.architecture
      );
      const rule = (code) => descriptor ? conditionalRule(descriptor.rules, code) : null;
      const reuseRule = rule(CONDITIONAL_RULE_CODES.audioReuseRequiresStages);
      if (reuseRule && clip.reuseAudio && evaluateConditionalRule(reuseRule, { clip })) {
        diagnostics.push(
          diagnostic(
            "warning",
            reuseRule.code,
            reuseRule.reason,
            clipIdx
          )
        );
      }
      const relayRule = rule(
        CONDITIONAL_RULE_CODES.promptRelayRequiresFixedLength
      );
      if (relayRule && clip.promptWindows.length > 0 && evaluateConditionalRule(relayRule, { clip })) {
        diagnostics.push(
          diagnostic("error", relayRule.code, relayRule.reason, clipIdx)
        );
      }
      const retakeReferenceRule = rule(
        CONDITIONAL_RULE_CODES.retakeExcludesReferences
      );
      const retakeSourceRule = rule(
        CONDITIONAL_RULE_CODES.retakeRequiresSource
      );
      if (retakeSourceRule && clip.retake && evaluateConditionalRule(retakeSourceRule, {
        clip,
        globalRefineMode: context.globalRefineMode
      })) {
        diagnostics.push(
          diagnostic(
            "warning",
            retakeSourceRule.code,
            retakeSourceRule.reason,
            clipIdx
          )
        );
      }
      if (retakeReferenceRule && clip.retake && evaluateConditionalRule(retakeReferenceRule, {
        clip,
        globalRefineMode: context.globalRefineMode
      })) {
        diagnostics.push(
          diagnostic(
            "error",
            retakeReferenceRule.code,
            retakeReferenceRule.reason,
            clipIdx
          )
        );
      }
    }
    const hdrRule = executable.map(
      ({ clip }) => context.catalog?.architectures.find((entry) => entry.id === clip.architecture)?.rules.find(
        (rule) => rule.code === CONDITIONAL_RULE_CODES.uniformTimelineHdr
      )
    ).find((rule) => rule !== void 0);
    if (hdrRule && evaluateConditionalRule(hdrRule, {
      timelineClips: executable.map(({ clip }) => clip),
      hasActiveHdr: clipHasActiveHdr
    })) {
      diagnostics.push(diagnostic("error", hdrRule.code, hdrRule.reason));
    }
    return diagnostics;
  };

  // frontend/gestureRouter.ts
  var DEFAULT_THRESHOLD_PX = 5;
  var claimOnly = () => ({
    threshold: Number.POSITIVE_INFINITY,
    onMove: () => {
    },
    onCommit: () => {
    }
  });
  var createGestureRouter = () => {
    let body = null;
    const routes = [];
    let live = null;
    let swallowNextClick = false;
    const ctxFor = (me) => ({
      event: me,
      dx: me.clientX - (live?.startX ?? 0),
      dy: me.clientY - (live?.startY ?? 0)
    });
    const onMouseDown = (event) => {
      swallowNextClick = false;
      const me = event;
      if (me.cancelBubble) {
        return;
      }
      if (me.button !== 0 || !(me.target instanceof Element) || !body) {
        return;
      }
      if (live) {
        return;
      }
      const ordered = [...routes].sort((a, b) => b.priority - a.priority);
      for (const route of ordered) {
        const session = route.onPress(me, body);
        if (session) {
          live = {
            session,
            startX: me.clientX,
            startY: me.clientY,
            active: (session.threshold ?? DEFAULT_THRESHOLD_PX) <= 0
          };
          me.stopPropagation();
          return;
        }
      }
    };
    const onDocMouseMove = (event) => {
      if (!live) {
        return;
      }
      const ctx = ctxFor(event);
      if (!live.active) {
        const threshold = live.session.threshold ?? DEFAULT_THRESHOLD_PX;
        const dist = live.session.axis === "xy" ? Math.hypot(ctx.dx, ctx.dy) : Math.abs(ctx.dx);
        if (dist < threshold) {
          return;
        }
        live.active = true;
      }
      live.session.onMove(ctx);
    };
    const onDocMouseUp = (event) => {
      if (!live) {
        return;
      }
      const ctx = ctxFor(event);
      const { session, active } = live;
      live = null;
      if (active) {
        swallowNextClick = true;
        session.onCommit(ctx);
        return;
      }
      if (session.suppressTapClick) {
        swallowNextClick = true;
      }
      session.onTap?.(ctx);
    };
    const onDocKeyDown = (event) => {
      if (event.key !== "Escape" || !live) {
        return;
      }
      const { session, active } = live;
      live = null;
      if (session.suppressEscapeClick && active) {
        swallowNextClick = true;
      }
      session.onCancel?.();
    };
    const onBodyClickCapture = (event) => {
      if (!swallowNextClick) {
        return;
      }
      swallowNextClick = false;
      event.stopPropagation();
      event.preventDefault();
    };
    const removeListeners = () => {
      if (body) {
        body.removeEventListener("mousedown", onMouseDown, true);
        body.removeEventListener("click", onBodyClickCapture, true);
      }
      document.removeEventListener("mousemove", onDocMouseMove);
      document.removeEventListener("mouseup", onDocMouseUp);
      document.removeEventListener("keydown", onDocKeyDown);
    };
    const cancelLive = () => {
      if (live) {
        const { session } = live;
        live = null;
        session.onCancel?.();
      }
    };
    return {
      attach: (nextBody) => {
        if (body === nextBody) {
          return;
        }
        cancelLive();
        removeListeners();
        body = nextBody;
        swallowNextClick = false;
        nextBody.addEventListener("mousedown", onMouseDown, true);
        nextBody.addEventListener("click", onBodyClickCapture, true);
        document.addEventListener("mousemove", onDocMouseMove);
        document.addEventListener("mouseup", onDocMouseUp);
        document.addEventListener("keydown", onDocKeyDown);
      },
      register: (route) => {
        routes.push(route);
        return () => {
          const at = routes.indexOf(route);
          if (at !== -1) {
            routes.splice(at, 1);
          }
        };
      },
      dispose: () => {
        cancelLive();
        removeListeners();
        body = null;
        swallowNextClick = false;
      }
    };
  };

  // frontend/selectionIdentity.ts
  var selectionAfterRemoval = (index, remaining, neighbour, fallback) => remaining > 0 ? neighbour(Math.min(index, remaining - 1)) : fallback;
  var idAt = (list2, index) => list2?.[index]?.id ?? void 0;
  var clipOwnerIndex = (selection) => {
    switch (selection.kind) {
      case "none":
      case "audio-track":
        return null;
      case "boundary":
        return selection.leftClipIdx;
      default:
        return selection.clipIdx;
    }
  };
  var clipItemList = (selection, clip) => {
    if (!clip) {
      return null;
    }
    switch (selection.kind) {
      case "clip":
        return { list: clip.stages, index: selection.stageIdx };
      case "ref":
        return { list: clip.refs, index: selection.refIdx };
      case "prompt-minor":
        return {
          list: clip.promptWindows ?? [],
          index: selection.windowIdx
        };
      default:
        return null;
    }
  };
  var anchorSelection = (selection, stateOverride) => {
    if (selection.kind === "none") {
      return { selection };
    }
    const state = stateOverride ?? getState();
    if (selection.kind === "audio-track") {
      return {
        selection,
        ownerId: idAt(state.audioTracks, selection.trackIdx)
      };
    }
    const ownerIdx = clipOwnerIndex(selection);
    if (ownerIdx === null) {
      return { selection };
    }
    const clip = state.clips[ownerIdx];
    const item = clipItemList(selection, clip);
    return {
      selection,
      ownerId: clip?.id ?? void 0,
      itemId: item ? idAt(item.list, item.index) : void 0
    };
  };
  var resolveIndex = (list2, id, hint) => {
    if (id === void 0) {
      return hint;
    }
    const found = list2.findIndex((entry) => entry.id === id);
    return found >= 0 ? found : null;
  };
  var withClipIndex = (selection, clipIdx) => {
    switch (selection.kind) {
      case "none":
      case "audio-track":
        return selection;
      case "boundary":
        return { kind: "boundary", leftClipIdx: clipIdx };
      default:
        return { ...selection, clipIdx };
    }
  };
  var withItemIndex = (selection, clipIdx, itemIdx) => {
    switch (selection.kind) {
      case "clip":
        return { kind: "clip", clipIdx, stageIdx: itemIdx };
      case "ref":
        return { kind: "ref", clipIdx, refIdx: itemIdx };
      case "prompt-minor":
        return { kind: "prompt-minor", clipIdx, windowIdx: itemIdx };
      default:
        return withClipIndex(selection, clipIdx);
    }
  };
  var itemFallback = (selection, clipIdx) => selection.kind === "clip" ? { kind: "clip", clipIdx, stageIdx: 0 } : { kind: "none" };
  var resolveSelection = (anchor2, stateOverride) => {
    const selection = anchor2.selection;
    if (anchor2.ownerId === void 0 && anchor2.itemId === void 0) {
      return selection;
    }
    const state = stateOverride ?? getState();
    if (selection.kind === "audio-track") {
      const tracks = state.audioTracks ?? [];
      const trackIdx = resolveIndex(
        tracks,
        anchor2.ownerId,
        selection.trackIdx
      );
      return trackIdx === null ? selectionAfterRemoval(
        selection.trackIdx,
        tracks.length,
        (index) => ({ kind: "audio-track", trackIdx: index }),
        { kind: "none" }
      ) : { kind: "audio-track", trackIdx };
    }
    const ownerHint = clipOwnerIndex(selection);
    if (ownerHint === null) {
      return selection;
    }
    const clipIdx = resolveIndex(state.clips, anchor2.ownerId, ownerHint);
    if (clipIdx === null) {
      return selection.kind === "clip" ? selectionAfterRemoval(
        ownerHint,
        state.clips.length,
        (index) => ({ kind: "clip", clipIdx: index, stageIdx: 0 }),
        { kind: "none" }
      ) : { kind: "none" };
    }
    const clip = state.clips[clipIdx];
    const item = clipItemList(selection, clip);
    if (!item) {
      return withClipIndex(selection, clipIdx);
    }
    const itemIdx = resolveIndex(item.list, anchor2.itemId, item.index);
    return itemIdx === null ? selectionAfterRemoval(
      item.index,
      item.list.length,
      (index) => withItemIndex(selection, clipIdx, index),
      itemFallback(selection, clipIdx)
    ) : withItemIndex(selection, clipIdx, itemIdx);
  };
  var sameAnchor = (a, b, state) => a.ownerId === b.ownerId && a.itemId === b.itemId && sameSelectionShape(resolveSelection(a, state), resolveSelection(b, state));
  var sameSelectionShape = (a, b) => {
    if (a.kind !== b.kind) {
      return false;
    }
    switch (a.kind) {
      case "none":
        return true;
      case "boundary":
        return a.leftClipIdx === b.leftClipIdx;
      case "audio-track":
        return a.trackIdx === b.trackIdx;
      case "clip":
        return a.clipIdx === b.clipIdx && a.stageIdx === b.stageIdx;
      case "ref":
        return a.clipIdx === b.clipIdx && a.refIdx === b.refIdx;
      case "ic-lora":
        return a.clipIdx === b.clipIdx && a.entryIdx === b.entryIdx;
      case "prompt-minor":
        return a.clipIdx === b.clipIdx && a.windowIdx === b.windowIdx;
      default:
        return a.clipIdx === b.clipIdx;
    }
  };
  var isSameSelection = sameSelectionShape;

  // frontend/selection.ts
  var NO_SELECTION = { selection: { kind: "none" } };
  var anchor = NO_SELECTION;
  var selectionSubscribers = /* @__PURE__ */ new Set();
  var clipIdxOf = (sel) => sel.kind === "none" || sel.kind === "boundary" || sel.kind === "audio-track" ? null : sel.clipIdx;
  var getSelection = () => resolveSelection(anchor);
  var getSelectedClipIndex = () => clipIdxOf(getSelection());
  var notify = () => {
    const current = getSelection();
    for (const cb of [...selectionSubscribers]) {
      try {
        cb(current);
      } catch {
      }
    }
  };
  var setSelection = (next) => {
    const nextAnchor = anchorSelection(next);
    if (sameAnchor(anchor, nextAnchor)) {
      return;
    }
    anchor = nextAnchor;
    notify();
  };
  var activateSelection = (next) => {
    anchor = anchorSelection(next);
    notify();
  };
  var subscribeSelection = (cb) => {
    selectionSubscribers.add(cb);
    return () => {
      selectionSubscribers.delete(cb);
    };
  };

  // frontend/timelineSnap.ts
  var SNAP_THRESHOLD_PX = 8;
  var nearestTarget = (value, targets, threshold) => {
    let nearest = null;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (const target of targets) {
      const distance = Math.abs(value - target);
      if (distance <= threshold && distance < nearestDistance) {
        nearest = target;
        nearestDistance = distance;
      }
    }
    return nearest;
  };
  var snapPoint = (value, primaryTargets, fallbackTargets, threshold) => nearestTarget(value, primaryTargets, threshold) ?? nearestTarget(value, fallbackTargets, threshold) ?? value;
  var snapMovedStart = (start, length, primaryTargets, fallbackTargets, threshold) => {
    const primaryStart = nearestTarget(start, primaryTargets, threshold);
    const primaryEnd = nearestTarget(start + length, primaryTargets, threshold);
    if (primaryStart !== null || primaryEnd !== null) {
      if (primaryStart === null) {
        return primaryEnd - length;
      }
      if (primaryEnd === null) {
        return primaryStart;
      }
      return Math.abs(primaryStart - start) <= Math.abs(primaryEnd - (start + length)) ? primaryStart : primaryEnd - length;
    }
    const fallbackStart = nearestTarget(start, fallbackTargets, threshold);
    const fallbackEnd = nearestTarget(
      start + length,
      fallbackTargets,
      threshold
    );
    if (fallbackStart === null && fallbackEnd === null) {
      return start;
    }
    if (fallbackStart === null) {
      return fallbackEnd - length;
    }
    if (fallbackEnd === null) {
      return fallbackStart;
    }
    return Math.abs(fallbackStart - start) <= Math.abs(fallbackEnd - (start + length)) ? fallbackStart : fallbackEnd - length;
  };
  var timelineClipEdges = (clips) => {
    const edges = [0];
    let cursor = 0;
    for (const clip of clips) {
      cursor += Math.max(0, clip.duration || 0);
      edges.push(cursor);
    }
    return edges;
  };

  // frontend/timelineAuthoringSettings.ts
  var SETTINGS_KEY = "videostages.timeline.authoringSettings";
  var DEFAULT_SETTINGS = {
    snap: true,
    autoCollapse: true
  };
  var getTimelineAuthoringSettings = () => {
    try {
      const raw = localStorage.getItem(SETTINGS_KEY);
      if (!raw) {
        return { ...DEFAULT_SETTINGS };
      }
      const parsed = JSON.parse(raw);
      return {
        snap: typeof parsed.snap === "boolean" ? parsed.snap : DEFAULT_SETTINGS.snap,
        autoCollapse: typeof parsed.autoCollapse === "boolean" ? parsed.autoCollapse : DEFAULT_SETTINGS.autoCollapse
      };
    } catch {
      return { ...DEFAULT_SETTINGS };
    }
  };
  var setTimelineAuthoringSetting = (key, value) => {
    const next = {
      ...getTimelineAuthoringSettings(),
      [key]: value
    };
    try {
      localStorage.setItem(SETTINGS_KEY, JSON.stringify(next));
    } catch {
    }
  };

  // frontend/timelineView/layout.ts
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
  var audioSegmentWaveBarHeights = (clipIdx, segmentIdx, count) => waveBarHeights(clipIdx * 4099 + segmentIdx + 1, count);
  var clampPxPerSecond = (value) => Number.isFinite(value) ? Math.min(MAX_PX_PER_SECOND, Math.max(MIN_PX_PER_SECOND, value)) : DEFAULT_PX_PER_SECOND;
  var zoomAnchorTime = (offsetX, scrollLeft, pxPerSecond, headerW = TRACK_HEADER_W_PX) => {
    if (pxPerSecond <= 0) {
      return 0;
    }
    const effectiveOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, (effectiveOffsetX + scrollLeft - headerW) / pxPerSecond);
  };
  var zoomAnchorScrollLeft = (time, pxPerSecond, offsetX, headerW = TRACK_HEADER_W_PX) => {
    const effectiveOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, headerW + time * pxPerSecond - effectiveOffsetX);
  };
  var computeFitPxPerSecond = (totalSeconds, containerWidthPx, padPx = 24) => {
    if (totalSeconds <= 0 || containerWidthPx <= padPx) {
      return DEFAULT_PX_PER_SECOND;
    }
    return clampPxPerSecond((containerWidthPx - padPx) / totalSeconds);
  };
  var computeRegionLayout = (clips, options) => {
    const pxPerSecond = options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND;
    const layouts = [];
    let cursorSeconds = 0;
    let cursorPx = 0;
    for (let index = 0; index < clips.length; index++) {
      const clip = clips[index];
      const durationSeconds = Math.max(0, clip.duration || 0);
      const widthPx = Math.max(
        DEFAULT_MIN_WIDTH_PX,
        durationSeconds * pxPerSecond
      );
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

  // frontend/trackDomUtils.ts
  var livePxPerSecond = (body) => {
    const pps = Number.parseFloat(body.dataset.vstPps ?? "");
    return Number.isFinite(pps) && pps > 0 ? pps : DEFAULT_PX_PER_SECOND;
  };
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
  var spanGeometry = (startSeconds, lengthSeconds, durationSeconds, options = {}) => {
    const duration = Math.max(0, durationSeconds || 0);
    const rawStart = startSeconds || 0;
    const start = clamp(rawStart, 0, duration);
    const end = clamp(rawStart + (lengthSeconds || 0), start, duration);
    const percent = (options.unit ?? "percent") === "percent";
    const scale = percent ? duration > 0 ? 100 / duration : 0 : options.pxPerSecond ?? 0;
    let left = start * scale;
    let width = (end - start) * scale;
    if (options.clampOutput ?? percent) {
      const bound = percent ? 100 : Number.MAX_SAFE_INTEGER;
      left = clamp(left, 0, bound);
      width = clamp(width, 0, bound - left);
    }
    if (options.minWidth !== void 0) {
      width = Math.max(options.minWidth, width);
    }
    return {
      startSeconds: start,
      endSeconds: end,
      left,
      width,
      empty: end <= start
    };
  };
  var keyframeLeftPercent = (time, duration) => spanGeometry(time, 0, duration).left;
  var currentRevision = () => getTimelineStore().revision();
  var isStaleRevision = (sourceRevision) => currentRevision() !== sourceRevision;
  var commitClipMutation = (sourceRevision, origin, mutate) => {
    if (isStaleRevision(sourceRevision)) {
      return false;
    }
    const next = mutate(getClips());
    if (!next) {
      return false;
    }
    saveClips(next, { origin });
    return true;
  };
  var isActivateKey = (ke) => ke.key === "Enter" || ke.key === " " || ke.key === "Spacebar";
  var bindClickSelectableTrack = (body, selector, activate) => {
    const fromTarget = (target) => {
      const el = target.closest(selector);
      if (el instanceof HTMLElement) {
        activate(el);
      }
    };
    const onClick = (event) => {
      if (event.target instanceof Element) {
        fromTarget(event.target);
      }
    };
    const onKeyDown = (event) => {
      const ke = event;
      if (!isActivateKey(ke)) {
        return;
      }
      if (!(ke.target instanceof Element) || !ke.target.closest(selector)) {
        return;
      }
      ke.preventDefault();
      fromTarget(ke.target);
    };
    body.addEventListener("click", onClick);
    body.addEventListener("keydown", onKeyDown);
    return () => {
      body.removeEventListener("click", onClick);
      body.removeEventListener("keydown", onKeyDown);
    };
  };

  // frontend/windowTrack.ts
  var DRAG_THRESHOLD_PX = 4;
  var createDefaultOrDraggedSpan = (startSec, endSec, lo, hi, minLen, defaultLen) => {
    const gap = hi - lo;
    if (gap < minLen) {
      return null;
    }
    let start;
    let length;
    if (endSec === null) {
      length = Math.min(defaultLen, gap);
      start = clamp(startSec, lo, hi - length);
    } else {
      const a = clamp(Math.min(startSec, endSec), lo, hi);
      const b = clamp(Math.max(startSec, endSec), lo, hi);
      start = a;
      length = Math.max(minLen, b - a);
      if (start + length > hi) {
        length = hi - start;
      }
    }
    return length < minLen ? null : { start, length };
  };
  var resizeSpanEdge = (edge, press, deltaSec, minLen, lo, hi) => {
    if (edge === "right") {
      const end2 = clamp(
        press.start + press.length + deltaSec,
        press.start + minLen,
        hi
      );
      return { start: press.start, length: end2 - press.start };
    }
    const end = press.start + press.length;
    const start = clamp(press.start + deltaSec, lo, end - minLen);
    return { start, length: end - start };
  };
  var clipWindowTrackScope = (origin) => {
    const laneFor = (clip, ownerIdx) => clip ? { owner: clip, ownerIdx, duration: clipDurationOf(clip) } : null;
    return {
      read: (ownerIdx) => laneFor(getClips()[ownerIdx], ownerIdx),
      resolveLane: (lane) => {
        const ownerIdx = parseIntAttr(lane, "data-clip-idx");
        return ownerIdx === null ? null : laneFor(getClips()[ownerIdx], ownerIdx);
      },
      write: (ownerIdx, _create, mutate) => {
        const clips = getClips();
        const lane = laneFor(clips[ownerIdx], ownerIdx);
        if (!lane || !mutate(lane)) {
          return false;
        }
        saveClips(clips, { origin });
        return true;
      }
    };
  };
  var createWindowTrack = (config) => {
    let boundBody = null;
    let unregister = null;
    const ownerAttr = config.ownerIdxAttr ?? "data-clip-idx";
    const snapTargets = (ownerIdx, duration) => config.snapTargets?.(ownerIdx, duration) ?? {
      primary: [],
      fallback: [0, duration]
    };
    const spanStyle = (start, length, laneDuration, pps) => {
      const pct = config.unit === "pct";
      const geometry = spanGeometry(start, length, laneDuration, {
        unit: pct ? "percent" : "px",
        pxPerSecond: pps,
        minWidth: pct ? void 0 : 2
      });
      const suffix = pct ? "%" : "px";
      return {
        left: `${geometry.left}${suffix}`,
        width: `${geometry.width}${suffix}`
      };
    };
    const moveTarget = (lane, itemIdx, press, desiredStart, pps) => {
      const raw = config.moveTargetStart(lane, itemIdx, press, desiredStart);
      if (!getTimelineAuthoringSettings().snap) {
        return raw;
      }
      const targets = snapTargets(lane.ownerIdx, lane.duration);
      const snapped = snapMovedStart(
        raw,
        press.length,
        targets.primary,
        targets.fallback,
        SNAP_THRESHOLD_PX / pps
      );
      return config.moveTargetStart(lane, itemIdx, press, snapped);
    };
    const resizeTarget = (lane, itemIdx, edge, press, deltaSec, pps) => {
      const raw = config.resizeTarget(lane, itemIdx, edge, press, deltaSec);
      if (!getTimelineAuthoringSettings().snap) {
        return raw;
      }
      const targets = snapTargets(lane.ownerIdx, lane.duration);
      const rawEdge = edge === "left" ? raw.start : raw.start + raw.length;
      const snappedEdge = snapPoint(
        rawEdge,
        targets.primary,
        targets.fallback,
        SNAP_THRESHOLD_PX / pps
      );
      if (snappedEdge === rawEdge) {
        return raw;
      }
      const pressEdge = edge === "left" ? press.start : press.start + press.length;
      return config.resizeTarget(
        lane,
        itemIdx,
        edge,
        press,
        snappedEdge - pressEdge
      );
    };
    const commitEdit = (state, write) => {
      if (isStaleRevision(state.sourceRevision)) {
        return;
      }
      const saved = config.scope.write(state.lane.ownerIdx, false, (txn) => {
        if (config.canEdit && !config.canEdit(txn) || !config.readSpan(txn, state.itemIdx)) {
          return false;
        }
        write(txn);
        return true;
      });
      if (saved) {
        setSelection(
          config.selectionFor(state.lane.ownerIdx, state.itemIdx)
        );
      }
    };
    const commitMove = (state, dxPx, pps) => {
      commitEdit(state, (txn) => {
        config.writeMove(
          txn,
          state.itemIdx,
          state.press,
          moveTarget(
            txn,
            state.itemIdx,
            state.press,
            state.press.start + dxPx / pps,
            pps
          )
        );
      });
    };
    const commitResize = (state, dxPx, pps) => {
      commitEdit(state, (txn) => {
        config.writeResize(
          txn,
          state.itemIdx,
          state.edge,
          state.press,
          resizeTarget(
            txn,
            state.itemIdx,
            state.edge,
            state.press,
            dxPx / pps,
            pps
          )
        );
      });
    };
    const commitCreate = (state, endSec) => {
      if (isStaleRevision(state.sourceRevision)) {
        return;
      }
      const created = {
        selection: null
      };
      const saved = config.scope.write(state.ownerIdx, true, (txn) => {
        if (config.canCreate && !config.canCreate(txn)) {
          return false;
        }
        created.selection = config.createSpan(txn, state.startSec, endSec);
        return created.selection !== null;
      });
      if (saved && created.selection) {
        setSelection(created.selection);
      }
    };
    const timeAtX = (lane, laneLeft, clientX, pps) => {
      const raw = clamp((clientX - laneLeft) / pps, 0, lane.duration);
      if (!getTimelineAuthoringSettings().snap) {
        return raw;
      }
      const targets = snapTargets(lane.ownerIdx, lane.duration);
      return snapPoint(
        raw,
        targets.primary,
        targets.fallback,
        SNAP_THRESHOLD_PX / pps
      );
    };
    const laneTimeAt = (state, clientX, pps) => timeAtX(state, state.laneLeft, clientX, pps);
    const moveSession = (body, state) => {
      const restore = () => {
        state.el.style.left = state.originalLeft;
      };
      return {
        threshold: DRAG_THRESHOLD_PX,
        onMove: (ctx) => {
          body.classList.add(config.draggingClass);
          const pps = livePxPerSecond(body);
          const start = moveTarget(
            state.lane,
            state.itemIdx,
            state.press,
            state.press.start + ctx.dx / pps,
            pps
          );
          state.el.style.left = spanStyle(
            start,
            state.press.length,
            state.lane.duration,
            pps
          ).left;
        },
        onCommit: (ctx) => {
          body.classList.remove(config.draggingClass);
          commitMove(state, ctx.dx, livePxPerSecond(body));
        },
        onTap: restore,
        onCancel: () => {
          restore();
          body.classList.remove(config.draggingClass);
        }
      };
    };
    const resizeSession = (body, state) => {
      const restore = () => {
        state.el.style.left = state.originalLeft;
        state.el.style.width = state.originalWidth;
      };
      return {
        threshold: DRAG_THRESHOLD_PX,
        onMove: (ctx) => {
          body.classList.add(config.draggingClass);
          const pps = livePxPerSecond(body);
          const geom = resizeTarget(
            state.lane,
            state.itemIdx,
            state.edge,
            state.press,
            ctx.dx / pps,
            pps
          );
          const style = spanStyle(
            geom.start,
            geom.length,
            state.lane.duration,
            pps
          );
          if (state.edge === "left") {
            state.el.style.left = style.left;
          }
          state.el.style.width = style.width;
        },
        onCommit: (ctx) => {
          body.classList.remove(config.draggingClass);
          commitResize(state, ctx.dx, livePxPerSecond(body));
        },
        onTap: restore,
        onCancel: () => {
          restore();
          body.classList.remove(config.draggingClass);
        }
      };
    };
    const createSession = (body, state) => {
      const removeGhost = () => {
        state.ghost?.remove();
        state.ghost = null;
      };
      return {
        threshold: DRAG_THRESHOLD_PX,
        // A plain lane tap creates a default-length span at the pressed
        // time, so the concluding click is always consumed.
        suppressTapClick: true,
        onMove: (ctx) => {
          body.classList.add(config.draggingClass);
          const pps = livePxPerSecond(body);
          const nowSec = laneTimeAt(state, ctx.event.clientX, pps);
          const a = Math.min(state.startSec, nowSec);
          const b = Math.max(state.startSec, nowSec);
          if (!state.ghost) {
            const ghost = document.createElement("div");
            ghost.className = config.ghostClass;
            state.laneEl.appendChild(ghost);
            state.ghost = ghost;
          }
          const ghostStyle = spanStyle(a, b - a, state.duration, pps);
          state.ghost.style.left = ghostStyle.left;
          state.ghost.style.width = ghostStyle.width;
        },
        onCommit: (ctx) => {
          body.classList.remove(config.draggingClass);
          removeGhost();
          commitCreate(
            state,
            laneTimeAt(state, ctx.event.clientX, livePxPerSecond(body))
          );
        },
        onTap: () => {
          removeGhost();
          commitCreate(state, null);
        },
        onCancel: () => {
          removeGhost();
          body.classList.remove(config.draggingClass);
        }
      };
    };
    const itemIdxOf = (span) => config.itemIdxAttr ? parseIntAttr(span, config.itemIdxAttr) : 0;
    const spanAt = (span) => {
      const ownerIdx = parseIntAttr(span, ownerAttr);
      const itemIdx = itemIdxOf(span);
      if (ownerIdx === null || itemIdx === null) {
        return null;
      }
      const lane = config.scope.read(ownerIdx);
      const press = lane ? config.readSpan(lane, itemIdx) : null;
      return lane && press ? { lane, itemIdx, press } : null;
    };
    const onPress = (me, body) => {
      if (!(me.target instanceof Element)) {
        return null;
      }
      const span = me.target.closest(config.spanSelector);
      if (span instanceof HTMLElement) {
        if (me.shiftKey) {
          me.preventDefault();
          return claimOnly();
        }
        const found = spanAt(span);
        if (!found) {
          return null;
        }
        if (config.canEdit && !config.canEdit(found.lane)) {
          me.preventDefault();
          return claimOnly();
        }
        const base = {
          lane: found.lane,
          itemIdx: found.itemIdx,
          el: span,
          press: found.press,
          originalLeft: span.style.left,
          sourceRevision: currentRevision()
        };
        me.preventDefault();
        const edgeEl = me.target.closest(config.edgeSelector);
        if (edgeEl) {
          return resizeSession(body, {
            ...base,
            edge: edgeEl.getAttribute(config.edgeAttr) === "left" ? "left" : "right",
            originalWidth: span.style.width
          });
        }
        return moveSession(body, base);
      }
      const laneEl = me.target.closest(config.laneSelector);
      if (laneEl instanceof HTMLElement) {
        const lane = config.scope.resolveLane(laneEl);
        if (!lane || config.canCreate && !config.canCreate(lane)) {
          return null;
        }
        const rect = laneEl.getBoundingClientRect();
        me.preventDefault();
        return createSession(body, {
          ownerIdx: lane.ownerIdx,
          duration: lane.duration,
          laneEl,
          laneLeft: rect.left,
          startSec: timeAtX(
            lane,
            rect.left,
            me.clientX,
            livePxPerSecond(body)
          ),
          ghost: null,
          sourceRevision: currentRevision()
        });
      }
      return null;
    };
    const activate = (selection) => {
      if (config.revealOnActivate) {
        activateSelection(selection);
      } else {
        setSelection(selection);
      }
    };
    const onBodyClick = (event) => {
      if (!(event.target instanceof Element)) {
        return;
      }
      const span = event.target.closest(config.spanSelector);
      if (!(span instanceof HTMLElement)) {
        config.onClickFallthrough?.(event, event.target);
        return;
      }
      if (config.isolateClicks) {
        event.stopImmediatePropagation();
      }
      const found = spanAt(span);
      if (!found) {
        return;
      }
      if (event.shiftKey) {
        const removed = {
          selection: null
        };
        const saved = config.scope.write(
          found.lane.ownerIdx,
          false,
          (txn) => {
            removed.selection = config.deleteItem(txn, found.itemIdx);
            return removed.selection !== null;
          }
        );
        if (saved && removed.selection) {
          setSelection(removed.selection);
        }
        return;
      }
      activate(config.selectionFor(found.lane.ownerIdx, found.itemIdx));
    };
    const onBodyKeyDown = (event) => {
      const ke = event;
      if (!isActivateKey(ke)) {
        return;
      }
      if (!(ke.target instanceof Element)) {
        return;
      }
      const span = ke.target.closest(config.spanSelector);
      if (!(span instanceof HTMLElement)) {
        return;
      }
      ke.preventDefault();
      if (config.isolateClicks) {
        ke.stopImmediatePropagation();
      }
      const found = spanAt(span);
      if (!found) {
        return;
      }
      activate(config.selectionFor(found.lane.ownerIdx, found.itemIdx));
    };
    const attach = (body, router) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("click", onBodyClick);
      if (config.keyboardSelect) {
        body.addEventListener("keydown", onBodyKeyDown);
      }
      unregister = router.register({
        id: config.routeId,
        priority: config.priority,
        onPress: (me) => onPress(me, body)
      });
    };
    const dispose = () => {
      if (boundBody) {
        boundBody.removeEventListener("click", onBodyClick);
        boundBody.removeEventListener("keydown", onBodyKeyDown);
        boundBody = null;
      }
      unregister?.();
      unregister = null;
    };
    return { attach, dispose };
  };

  // frontend/timelineAudioSegmentTrack.ts
  var timelineDuration = (state) => state.clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
  var pressSpanOf = (span) => span && span.timelineStartSeconds !== null && span.timelineLengthSeconds !== null ? {
    start: span.timelineStartSeconds,
    length: span.timelineLengthSeconds,
    trim: span.sourceStartSeconds
  } : null;
  var blankTrack = () => ({
    id: createEntityId("audio_track"),
    source: { kind: "Upload", reference: "", uploadedAudio: null },
    volume: AUDIO_SEGMENT_VOLUME_DEFAULT,
    spans: []
  });
  var audioTrackScope = () => ({
    read: (ownerIdx) => {
      const state = getState();
      const owner = state.audioTracks?.[ownerIdx];
      return owner ? { owner, ownerIdx, duration: timelineDuration(state) } : null;
    },
    // The blank lane carries no index: a create appends a new track.
    resolveLane: () => {
      const state = getState();
      return {
        owner: null,
        ownerIdx: state.audioTracks?.length ?? 0,
        duration: timelineDuration(state)
      };
    },
    write: (ownerIdx, create, mutate) => {
      const state = getState();
      state.audioTracks ??= [];
      const tracks = state.audioTracks;
      if (create && ownerIdx === tracks.length) {
        tracks.push(blankTrack());
      }
      const owner = tracks[ownerIdx];
      if (!owner) {
        return false;
      }
      const applied = mutate({
        owner,
        ownerIdx,
        duration: timelineDuration(state),
        removeOwner: () => {
          tracks.splice(ownerIdx, 1);
          return tracks.length;
        }
      });
      if (!applied) {
        return false;
      }
      saveState(state, { origin: "audio-segment-track" });
      return true;
    }
  });
  var createTimelineAudioSegmentTrack = () => createWindowTrack({
    routeId: "timeline-audio-segment",
    priority: 40,
    scope: audioTrackScope(),
    spanSelector: ".vst-audio-seg[data-track-idx]",
    ownerIdxAttr: "data-track-idx",
    itemIdxAttr: null,
    edgeSelector: "[data-vst-audio-seg-edge]",
    edgeAttr: "data-vst-audio-seg-edge",
    laneSelector: ".vst-audio-seg-lane[data-vst-audio-seg-add]:not([data-clip-idx])",
    draggingClass: "vst-audio-seg-dragging",
    ghostClass: "vst-audio-seg-ghost",
    unit: "pct",
    keyboardSelect: true,
    // The segment sits on the audio row; its clicks must not bubble
    // into that row's select handler.
    isolateClicks: true,
    readSpan: ({ owner }) => pressSpanOf(owner.spans[0]),
    canCreate: ({ duration }) => duration >= AUDIO_SEGMENT_MIN_LENGTH,
    // Segments snap to the track immediately above before falling back
    // to the clip boundaries underneath them.
    snapTargets: (ownerIdx) => {
      const state = getState();
      const above = pressSpanOf(
        ownerIdx > 0 ? state.audioTracks?.[ownerIdx - 1]?.spans[0] : void 0
      );
      return {
        primary: above ? [above.start, above.start + above.length] : [],
        fallback: timelineClipEdges(state.clips)
      };
    },
    moveTargetStart: ({ duration }, _itemIdx, press, desiredStart) => Math.min(
      Math.max(desiredStart, 0),
      Math.max(0, duration - press.length)
    ),
    writeMove: ({ owner }, _itemIdx, _press, start) => {
      const span = owner.spans[0];
      if (span) {
        span.timelineStartSeconds = roundToTenth(start);
      }
    },
    // A left resize may run back to the start of the untrimmed source,
    // so its wall sits `trim` seconds before the pressed start.
    resizeTarget: ({ duration }, _itemIdx, edge, press, delta) => resizeSpanEdge(
      edge,
      press,
      delta,
      AUDIO_SEGMENT_MIN_LENGTH,
      Math.max(0, press.start - press.trim),
      duration
    ),
    writeResize: ({ owner }, _itemIdx, edge, press, geom) => {
      const span = owner.spans[0];
      if (!span) {
        return;
      }
      span.timelineStartSeconds = roundToTenth(geom.start);
      span.timelineLengthSeconds = roundToTenth(geom.length);
      if (edge === "left") {
        span.sourceStartSeconds = roundToTenth(
          press.trim + (geom.start - press.start)
        );
      }
    },
    createSpan: ({ owner, ownerIdx, duration }, startSec, endSec) => {
      const geom = createDefaultOrDraggedSpan(
        startSec,
        endSec,
        0,
        duration,
        AUDIO_SEGMENT_MIN_LENGTH,
        AUDIO_SEGMENT_DEFAULT_LENGTH
      );
      if (!geom) {
        return null;
      }
      owner.spans.push({
        id: createEntityId("audio_span"),
        timelineStartSeconds: roundToTenth(geom.start),
        timelineLengthSeconds: roundToTenth(geom.length),
        sourceStartSeconds: 0
      });
      return { kind: "audio-track", trackIdx: ownerIdx };
    },
    // A track exists only for its segments: deleting the last one
    // deletes the track, and the selection moves to a sibling track.
    deleteItem: ({ owner, ownerIdx, removeOwner }, itemIdx) => {
      if (!owner.spans[itemIdx]) {
        return null;
      }
      owner.spans.splice(itemIdx, 1);
      if (owner.spans.length > 0) {
        return { kind: "audio-track", trackIdx: ownerIdx };
      }
      return selectionAfterRemoval(
        ownerIdx,
        removeOwner?.() ?? 0,
        (index) => ({ kind: "audio-track", trackIdx: index }),
        { kind: "none" }
      );
    },
    selectionFor: (trackIdx) => ({ kind: "audio-track", trackIdx })
  });

  // frontend/timelineAudioTrack.ts
  var CLIP_SELECTOR = '.vst-audio-clip[data-vst-audio="clip"]';
  var createTimelineAudioTrack = () => {
    let boundBody = null;
    let unbind = null;
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      unbind = bindClickSelectableTrack(body, CLIP_SELECTOR, (el) => {
        const clipIdx = parseIntAttr(el, "data-clip-idx");
        if (clipIdx !== null) {
          setSelection({ kind: "audio", clipIdx });
        }
      });
    };
    const dispose = () => {
      unbind?.();
      unbind = null;
      boundBody = null;
    };
    return { attach, dispose };
  };

  // frontend/timelineBoundaryTrack.ts
  var CHIP_SELECTOR = "[data-vst-boundary-chip]";
  var parseLeftClipIdx = (el) => {
    if (!el) {
      return null;
    }
    const raw = el.getAttribute("data-left-clip-idx");
    if (raw === null) {
      return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
  };
  var createTimelineBoundaryTrack = () => {
    let boundBody = null;
    let unbind = null;
    const activateFromTarget = (target) => {
      const chip = target.closest(CHIP_SELECTOR);
      if (!(chip instanceof HTMLElement)) {
        return;
      }
      const leftClipIdx = parseLeftClipIdx(chip);
      if (leftClipIdx === null) {
        return;
      }
      setSelection({ kind: "boundary", leftClipIdx });
    };
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      unbind = bindClickSelectableTrack(
        body,
        CHIP_SELECTOR,
        (el) => activateFromTarget(el)
      );
    };
    const dispose = () => {
      unbind?.();
      unbind = null;
      boundBody = null;
    };
    return { attach, dispose };
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
  var formatTimeLabel = (seconds, unit, fps) => {
    if (unit === "frames") {
      return `${Math.round((seconds || 0) * safeFps(fps))}f`;
    }
    const rounded = Math.round((seconds || 0) * 10) / 10;
    return Number.isInteger(rounded) ? `${rounded}s` : `${rounded.toFixed(1)}s`;
  };
  var formatOverlapSeconds = (frames, fps) => `${(frames / Math.max(1, fps)).toFixed(2)}s`;
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
  var truncate = (value, max = 80) => {
    const text2 = `${value ?? ""}`;
    return text2.length <= max ? text2 : `${text2.slice(0, Math.max(0, max - 1))}…`;
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
  var shortModelName = (model) => {
    const raw = `${model ?? ""}`.trim();
    if (!raw) {
      return "(default)";
    }
    const segment = raw.split(/[\\/]/).pop() ?? raw;
    return segment.replace(/\.(safetensors|ckpt|pt|pth|gguf|sft|bin)$/i, "");
  };
  var stageChipLabel = (index) => `S${index}`;
  var stageChipTitle = (stage, index) => {
    const parts = [
      `Stage ${index}${index === 0 ? " (full gen)" : " (refine)"}`,
      `model: ${shortModelName(stage?.model ?? "")}`,
      `steps: ${stage?.steps ?? "?"}`,
      `cfg: ${stage?.cfgScale ?? "?"}`,
      `control: ${stage?.control ?? "?"}`
    ];
    if (stage?.skipped) {
      parts.push("skipped");
    }
    return parts.join(" · ");
  };

  // frontend/detailWidgets.ts
  var sliderSeq = 0;
  var helpSeq = 0;
  var checkboxSeq = 0;
  var fieldSeq = 0;
  var slugify = (value) => value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "") || "field";
  var appendHelp = (labelEl, row, fieldName, helpText) => {
    const key = `vst_${slugify(fieldName)}_${++helpSeq}`;
    const btn = document.createElement("span");
    btn.className = "auto-input-qbutton info-popover-button";
    btn.textContent = "?";
    btn.addEventListener("click", (event) => {
      showHostPopover(key, event);
    });
    labelEl.insertBefore(btn, labelEl.firstChild);
    const pop = document.createElement("div");
    pop.className = "sui-popover sui-info-popover";
    pop.id = `popover_${key}`;
    const name = document.createElement("b");
    name.textContent = fieldName;
    pop.append(
      name,
      document.createElement("br"),
      document.createTextNode(helpText)
    );
    row.appendChild(pop);
  };
  var wireNumericInput = (input2, fallback, min, max, onChange) => {
    const apply = (normalize) => {
      const parsed = Number.parseFloat(input2.value);
      const next = clamp(
        Number.isFinite(parsed) ? parsed : fallback,
        min,
        max
      );
      onChange(next);
      if (normalize) {
        input2.value = `${next}`;
      }
    };
    input2.addEventListener("input", () => apply(false));
    input2.addEventListener("change", () => apply(true));
  };
  var wireUnboundedNumericInput = (input2, fallback, onChange) => {
    const apply = (normalize) => {
      const parsed = Number.parseFloat(input2.value);
      const next = Number.isFinite(parsed) ? parsed : fallback;
      onChange(next);
      if (normalize) {
        input2.value = `${next}`;
      }
    };
    input2.removeAttribute("min");
    input2.removeAttribute("max");
    input2.addEventListener("input", () => apply(false));
    input2.addEventListener("change", () => apply(true));
  };
  var boxClassFor = (control) => {
    if (control.classList.contains("auto-dropdown")) {
      return "auto-dropdown-box";
    }
    if (control.classList.contains("auto-number")) {
      return "auto-number-box";
    }
    if (control.classList.contains("auto-text")) {
      return "auto-text-box";
    }
    return null;
  };
  var buildFieldLabel = (label) => {
    const labelEl = document.createElement("label");
    const text2 = document.createElement("span");
    text2.className = "auto-input-name vst-detail-field-label";
    text2.textContent = label;
    labelEl.appendChild(text2);
    return labelEl;
  };
  var buildField = (label, control, hint, help) => {
    const row = document.createElement("div");
    row.className = "auto-input vst-detail-field";
    row.classList.add(
      control.classList.contains("auto-text-block") ? "auto-input-flex-wide" : "auto-input-flex"
    );
    const boxClass = boxClassFor(control);
    if (boxClass) {
      row.classList.add(boxClass);
    }
    const labelEl = buildFieldLabel(label);
    if (help) {
      appendHelp(labelEl, row, label, help);
    }
    if (control instanceof HTMLInputElement || control instanceof HTMLSelectElement || control instanceof HTMLTextAreaElement) {
      if (!control.id) {
        control.id = `vst_field_${slugify(label)}_${++fieldSeq}`;
      }
      labelEl.htmlFor = control.id;
    }
    row.append(labelEl, control);
    if (hint) {
      const small = document.createElement("small");
      small.className = "auto-input-description vst-detail-field-hint";
      small.textContent = hint;
      row.appendChild(small);
    }
    return row;
  };
  var buildOptionSelect = (specs, selected, onChange) => {
    const select2 = document.createElement("select");
    select2.className = "auto-dropdown vst-audio-select";
    for (const spec of specs) {
      const opt = document.createElement("option");
      opt.value = spec.value;
      opt.textContent = spec.label;
      opt.dataset.cleanname = spec.label;
      opt.disabled = spec.disabled === true;
      opt.selected = spec.value === selected;
      select2.appendChild(opt);
    }
    select2.addEventListener("change", () => onChange(select2.value));
    return select2;
  };
  var buildNumber = (value, min, max, step, onChange) => {
    const input2 = document.createElement("input");
    input2.type = "number";
    input2.className = "auto-number vst-refs-num";
    input2.min = `${min}`;
    input2.max = `${max}`;
    input2.step = `${step}`;
    input2.value = `${value}`;
    wireNumericInput(input2, value, min, max, onChange);
    return input2;
  };
  var buildUnboundedNumber = (value, step, onChange) => {
    const input2 = document.createElement("input");
    input2.type = "number";
    input2.className = "auto-number vst-refs-num";
    input2.step = `${step}`;
    input2.value = `${value}`;
    wireUnboundedNumericInput(input2, value, onChange);
    return input2;
  };
  var buildSlider = (label, value, min, max, step, onChange, opts) => {
    const holder = document.createElement("div");
    holder.className = "vst-stage-slider auto-input-flex-wide";
    const id = `vst_stage_slider_${++sliderSeq}`;
    holder.innerHTML = renderHostSlider({
      id,
      label,
      value,
      min,
      max,
      viewMin: opts?.sliderMin,
      viewMax: opts?.sliderMax,
      step
    });
    const number = holder.querySelector(
      "input.auto-slider-number"
    );
    if (number) {
      number.step = `${opts?.numberStep ?? step}`;
      if (opts?.allowNumberOutOfRange) {
        wireUnboundedNumericInput(number, value, onChange);
      } else {
        wireNumericInput(number, value, min, max, onChange);
      }
    }
    if (opts?.title) {
      holder.title = opts.title;
    }
    if (opts?.help) {
      const labelEl = holder.querySelector("label");
      if (labelEl) {
        appendHelp(labelEl, holder, label, opts.help);
      }
    }
    if (opts?.hint) {
      const small = document.createElement("small");
      small.className = "vst-detail-field-hint";
      small.textContent = opts.hint;
      holder.appendChild(small);
    }
    return holder;
  };
  var buildCheckbox = (label, checked, onChange, opts) => {
    const row = document.createElement("div");
    row.className = "auto-input auto-checkbox-box auto-input-flex vst-detail-field vst-detail-field-check";
    row.dataset.disabled = `${opts?.disabled === true}`;
    const input2 = document.createElement("input");
    input2.type = "checkbox";
    input2.className = "auto-checkbox";
    input2.id = `vst_checkbox_${slugify(label)}_${++checkboxSeq}`;
    input2.dataset.name = label;
    input2.checked = checked;
    input2.addEventListener("change", () => onChange(input2.checked));
    const labelEl = buildFieldLabel(label);
    row.append(labelEl, input2);
    if (opts?.help) {
      appendHelp(labelEl, row, label, opts.help);
    }
    if (opts?.disabled) {
      row.classList.add("vst-audio-disabled");
      input2.disabled = true;
    }
    return row;
  };
  var buildTextarea = (value, placeholder, focusKey, onInput) => {
    const editor = document.createElement("textarea");
    editor.className = "auto-text auto-text-block vst-prompt-editor vst-detail-prompt";
    editor.value = value;
    editor.placeholder = placeholder;
    editor.setAttribute("data-vst-focus-key", focusKey);
    editor.addEventListener("input", () => onInput(editor.value));
    enhanceHostPromptEditor(editor);
    return editor;
  };
  var readFileAsDataUri = (file, onFile) => {
    const reader = new FileReader();
    reader.onload = () => {
      const data = `${reader.result ?? ""}`;
      if (data) {
        onFile(data, file.name);
      }
    };
    reader.readAsDataURL(file);
  };
  var mediaPickCounter = 0;
  var buildMediaPickRow = (label, accept, browserTypes, name, onFile, onClear) => {
    const row = document.createElement("div");
    row.className = "auto-input auto-file-box vst-detail-field vst-audio-upload";
    const controls = document.createElement("label");
    controls.className = "auto-file-input-label";
    const pickLabel = document.createElement("span");
    pickLabel.className = "auto-input-name vst-detail-field-label";
    pickLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.className = "auto-file";
    fileInput.accept = accept;
    fileInput.id = `vst-media-pick-${++mediaPickCounter}`;
    const uploadBtn = document.createElement("button");
    uploadBtn.type = "button";
    uploadBtn.className = "basic-button auto-file-input-button vst-media-pick-upload";
    uploadBtn.textContent = "Upload";
    uploadBtn.addEventListener("click", () => fileInput.click());
    controls.append(pickLabel, uploadBtn);
    const fileDrop = document.createElement("label");
    fileDrop.className = "auto-file-label";
    fileDrop.htmlFor = fileInput.id;
    const fileDisplay = document.createElement("div");
    fileDisplay.className = "auto-file-input";
    const fileName = document.createElement("span");
    fileName.className = "auto-file-input-filename vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    fileDisplay.appendChild(fileName);
    fileDrop.append(fileInput, fileDisplay);
    const preview = document.createElement("div");
    preview.className = "auto-input-preview";
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "basic-button auto-file-input-button vst-audio-upload-clear";
    clearBtn.textContent = "Clear";
    clearBtn.hidden = !name;
    fileInput.addEventListener("change", () => {
      const file = fileInput.files?.[0];
      if (file) {
        readFileAsDataUri(file, onFile);
        return;
      }
      const picked = fileInput.dataset.filedata ?? "";
      if (!picked) {
        return;
      }
      const pickedName = fileInput.dataset.filename ?? "server file";
      if (picked.startsWith("data:")) {
        onFile(picked, pickedName);
        return;
      }
      void getVideoStagesHostBridge().toDataUrl(picked).then((data) => onFile(data, pickedName));
    });
    clearBtn.addEventListener("click", () => onClear());
    if (hasHostInputBrowser()) {
      const selectBtn = document.createElement("button");
      selectBtn.type = "button";
      selectBtn.className = "basic-button auto-file-input-button vst-media-pick-select";
      selectBtn.textContent = "Select";
      selectBtn.addEventListener(
        "click",
        () => openHostInputBrowser(fileInput.id, browserTypes)
      );
      controls.appendChild(selectBtn);
    }
    controls.appendChild(clearBtn);
    row.append(controls, fileDrop, preview);
    return row;
  };
  var setAccordionOpen = (section, open) => {
    const header = section.querySelector(
      ":scope > .input-group-header"
    );
    const content = section.querySelector(
      ":scope > .input-group-content"
    );
    const symbol = header?.querySelector(".auto-symbol");
    section.classList.toggle("input-group-open", open);
    section.classList.toggle("input-group-closed", !open);
    header?.setAttribute("aria-expanded", `${open}`);
    if (section.classList.contains("vst-detail-repeating-group")) {
      header?.setAttribute("aria-pressed", `${open}`);
    }
    if (content) {
      content.style.removeProperty("display");
      content.hidden = !open;
      if (section.classList.contains("vst-detail-repeating-group") && content.childNodes.length > 0) {
        content.classList.toggle(
          "vst-detail-repeating-editor-active",
          open
        );
      }
    }
    if (symbol) {
      symbol.textContent = open ? "⮟" : "⮞";
    }
  };
  var closeSiblingAccordionSections = (section) => {
    const parent = section.parentElement;
    if (!parent) {
      return;
    }
    for (const sibling of parent.children) {
      if (sibling instanceof HTMLElement && sibling !== section && sibling.classList.contains("vst-detail-section")) {
        setAccordionOpen(sibling, false);
      }
    }
  };
  var appendSectionContent = (target, source, flatten) => {
    if (!flatten || source instanceof DocumentFragment) {
      target.appendChild(source);
      return;
    }
    for (const { name, value } of Array.from(source.attributes)) {
      if (name.startsWith("data-")) {
        target.setAttribute(name, value);
      }
    }
    target.classList.add(...Array.from(source.classList));
    target.append(...Array.from(source.childNodes));
  };
  var appendSectionHeaderAction = (target, actionSpec) => {
    const action = document.createElement("button");
    action.type = "button";
    action.className = `basic-button vst-btn-tiny vst-detail-repeating-group-action ${actionSpec.className ?? ""}`.trim();
    action.textContent = actionSpec.label;
    action.title = actionSpec.title;
    action.setAttribute("aria-label", actionSpec.title);
    action.setAttribute("aria-pressed", `${actionSpec.active === true}`);
    action.classList.toggle("vst-btn-skip-active", actionSpec.active === true);
    action.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      actionSpec.onClick();
    });
    target.appendChild(action);
  };
  var rememberedAccordionSections = /* @__PURE__ */ new Set();
  var resetRememberedAccordionSections = () => {
    rememberedAccordionSections.clear();
    rememberedRepeaterItems.clear();
    rememberedRepeaterOpenItems.clear();
    forceOpenRepeaterKeys.clear();
  };
  var buildStaticSection = (spec) => {
    const section = document.createElement("div");
    section.className = `input-group input-group-open vst-detail-section vst-detail-static-section ${spec.className ?? ""}`.trim();
    section.dataset.vstStaticKey = spec.key;
    const header = document.createElement("span");
    header.className = "input-group-header input-group-noshrink vst-detail-section-header";
    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(heading, spacer);
    if (spec.headerAction) {
      const actions = document.createElement("span");
      actions.className = "vst-detail-repeating-group-actions";
      appendSectionHeaderAction(actions, spec.headerAction);
      labelWrap.appendChild(actions);
    }
    header.appendChild(labelWrap);
    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
    appendSectionContent(content, spec.content, spec.flattenContent === true);
    section.append(header, content);
    return { section, heading, content };
  };
  var buildAccordionSection = (spec) => {
    const autoCollapse = getTimelineAuthoringSettings().autoCollapse;
    const open = spec.open === true || !autoCollapse && rememberedAccordionSections.has(spec.key);
    if (!autoCollapse && open) {
      rememberedAccordionSections.add(spec.key);
    }
    const section = document.createElement("div");
    section.className = `input-group vst-detail-section ${open ? "input-group-open" : "input-group-closed"} ${spec.className ?? ""}`.trim();
    section.dataset.vstAccordionKey = spec.key;
    const header = document.createElement("span");
    header.className = "input-group-header input-group-shrinkable vst-detail-section-header";
    header.tabIndex = 0;
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", `${open}`);
    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    const symbol = document.createElement("span");
    symbol.className = "auto-symbol";
    symbol.textContent = open ? "⮟" : "⮞";
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(symbol, heading, spacer);
    if (spec.counter !== void 0) {
      const counter = document.createElement("span");
      counter.className = "header-label-counter";
      counter.textContent = `${spec.counter}`;
      labelWrap.appendChild(counter);
    }
    header.appendChild(labelWrap);
    const content = document.createElement("div");
    content.className = "input-group-content vst-detail-section-content";
    content.hidden = !open;
    appendSectionContent(content, spec.content, spec.flattenContent === true);
    const toggle = (event) => {
      event.preventDefault();
      event.stopPropagation();
      const opening = content.hidden === true;
      const collapseSiblings = getTimelineAuthoringSettings().autoCollapse;
      if (opening && collapseSiblings) {
        closeSiblingAccordionSections(section);
      } else if (opening) {
        for (const sibling of Array.from(
          section.parentElement?.children ?? []
        )) {
          if (sibling instanceof HTMLElement && sibling !== section && sibling.classList.contains("input-group-open")) {
            const key = sibling.dataset.vstAccordionKey;
            if (key) {
              rememberedAccordionSections.add(key);
            }
          }
        }
      }
      setAccordionOpen(section, opening);
      if (opening && !collapseSiblings) {
        rememberedAccordionSections.add(spec.key);
      } else {
        rememberedAccordionSections.delete(spec.key);
      }
    };
    header.addEventListener("click", toggle);
    header.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        toggle(event);
      }
    });
    section.append(header, content);
    return { section, heading, content };
  };
  var rememberedRepeaterItems = /* @__PURE__ */ new Map();
  var rememberedRepeaterOpenItems = /* @__PURE__ */ new Map();
  var forceOpenRepeaterKeys = /* @__PURE__ */ new Set();
  var buildRepeatingEditor = (spec) => {
    const explicitActiveIndex = spec.items.findIndex(
      (item) => item.active === true
    );
    const rememberedIndex = rememberedRepeaterItems.get(spec.key);
    const validRememberedIndex = rememberedIndex !== void 0 && rememberedIndex >= 0 && rememberedIndex < spec.items.length ? rememberedIndex : null;
    if (rememberedIndex !== void 0 && validRememberedIndex === null) {
      rememberedRepeaterItems.delete(spec.key);
      forceOpenRepeaterKeys.delete(spec.key);
    }
    const forceOpen = forceOpenRepeaterKeys.has(spec.key) && validRememberedIndex !== null;
    const activeIndex = forceOpen ? validRememberedIndex : explicitActiveIndex >= 0 ? explicitActiveIndex : validRememberedIndex ?? spec.defaultActiveIndex ?? null;
    if (explicitActiveIndex >= 0 && !forceOpen) {
      rememberedRepeaterItems.set(spec.key, explicitActiveIndex);
    } else if (activeIndex !== null) {
      rememberedRepeaterItems.set(spec.key, activeIndex);
    }
    if (forceOpen) {
      forceOpenRepeaterKeys.delete(spec.key);
    }
    const autoCollapse = getTimelineAuthoringSettings().autoCollapse;
    const openItems = autoCollapse ? /* @__PURE__ */ new Set() : new Set(rememberedRepeaterOpenItems.get(spec.key) ?? []);
    for (const index of openItems) {
      if (index < 0 || index >= spec.items.length) {
        openItems.delete(index);
      }
    }
    if (activeIndex !== null) {
      openItems.add(activeIndex);
    }
    rememberedRepeaterOpenItems.set(spec.key, openItems);
    const children = document.createDocumentFragment();
    spec.items.forEach((item, index) => {
      const active = index === activeIndex;
      const open = openItems.has(index);
      const group = document.createElement("div");
      group.className = `input-group vst-detail-repeating-group ${open ? "input-group-open" : "input-group-closed"} ${item.groupClassName ?? ""}`.trim();
      const header = document.createElement("span");
      header.className = `input-group-header input-group-shrinkable vst-detail-repeating-group-header ${item.className ?? ""}`.trim();
      header.tabIndex = 0;
      header.setAttribute("role", "button");
      header.setAttribute("aria-expanded", `${open}`);
      header.setAttribute("aria-pressed", `${active}`);
      if (item.focusKey) {
        header.dataset.vstFocusKey = item.focusKey;
      }
      if (item.title) {
        header.title = item.title;
      }
      const labelWrap = document.createElement("span");
      labelWrap.className = "header-label-wrap";
      const symbol = document.createElement("span");
      symbol.className = "auto-symbol";
      symbol.textContent = open ? "⮟" : "⮞";
      const label = document.createElement("span");
      label.className = "header-label";
      label.textContent = item.label;
      const spacer = document.createElement("span");
      spacer.className = "header-label-spacer";
      const actions = document.createElement("span");
      actions.className = "vst-detail-repeating-group-actions";
      if (item.headerAction) {
        appendSectionHeaderAction(actions, item.headerAction);
      }
      const onDelete = item.onDelete ?? item.onShiftDelete;
      if (onDelete) {
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = `interrupt-button vst-btn-tiny vst-detail-delete vst-detail-repeating-group-delete ${spec.remove.className}`.trim();
        remove.textContent = "×";
        remove.title = item.deleteTitle ?? (active ? spec.remove.title : `Delete ${item.label}`);
        remove.setAttribute("aria-label", remove.title);
        remove.disabled = item.deleteDisabled === true;
        remove.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();
          onDelete();
        });
        actions.appendChild(remove);
      }
      labelWrap.append(symbol, label, spacer, actions);
      header.appendChild(labelWrap);
      const content = document.createElement("div");
      content.className = "input-group-content vst-detail-repeating-group-content";
      const editor = open ? item.editor ?? spec.editorForItem?.(index) ?? (active ? spec.editor : void 0) : void 0;
      if (editor) {
        appendSectionContent(content, editor, true);
      }
      content.hidden = !open;
      content.classList.toggle(
        "vst-detail-repeating-editor-active",
        open && editor !== void 0
      );
      const activateOrToggle = (event) => {
        event.preventDefault();
        event.stopPropagation();
        if (!active && item.onSelect) {
          if (getTimelineAuthoringSettings().autoCollapse) {
            rememberedRepeaterOpenItems.set(spec.key, /* @__PURE__ */ new Set([index]));
          } else {
            const remembered = rememberedRepeaterOpenItems.get(spec.key) ?? /* @__PURE__ */ new Set();
            for (const sibling of Array.from(
              group.parentElement?.children ?? []
            )) {
              if (sibling instanceof HTMLElement && sibling.classList.contains(
                "vst-detail-repeating-group"
              ) && sibling.classList.contains("input-group-open")) {
                const siblingIndex = Number(
                  sibling.dataset.vstRepeaterItem
                );
                if (Number.isInteger(siblingIndex)) {
                  remembered.add(siblingIndex);
                }
              }
            }
            remembered.add(index);
            rememberedRepeaterOpenItems.set(spec.key, remembered);
          }
          rememberedRepeaterItems.set(spec.key, index);
          item.onSelect();
          return;
        }
        const opening = content.hidden === true;
        const collapseItems = getTimelineAuthoringSettings().autoCollapse;
        if (opening && collapseItems) {
          for (const sibling of Array.from(
            group.parentElement?.children ?? []
          )) {
            if (sibling instanceof HTMLElement && sibling !== group && sibling.classList.contains("vst-detail-repeating-group")) {
              setAccordionOpen(sibling, false);
            }
          }
        }
        setAccordionOpen(group, opening);
        if (opening) {
          rememberedRepeaterItems.set(spec.key, index);
          if (collapseItems) {
            rememberedRepeaterOpenItems.set(spec.key, /* @__PURE__ */ new Set([index]));
          } else {
            const remembered = rememberedRepeaterOpenItems.get(spec.key) ?? /* @__PURE__ */ new Set();
            remembered.add(index);
            rememberedRepeaterOpenItems.set(spec.key, remembered);
          }
        } else if (rememberedRepeaterItems.get(spec.key) === index) {
          rememberedRepeaterItems.delete(spec.key);
          rememberedRepeaterOpenItems.get(spec.key)?.delete(index);
        } else {
          rememberedRepeaterOpenItems.get(spec.key)?.delete(index);
        }
      };
      header.addEventListener("click", activateOrToggle);
      header.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          activateOrToggle(event);
        }
      });
      group.append(header, content);
      children.appendChild(group);
      group.dataset.vstRepeaterItem = `${index}`;
    });
    const add = document.createElement("button");
    add.type = "button";
    add.className = `basic-button small-button vst-detail-repeating-add ${spec.add.className}`.trim();
    add.textContent = spec.add.label ?? "+ Add";
    add.title = spec.add.title;
    add.setAttribute("aria-label", spec.add.title);
    add.disabled = spec.add.disabled === true;
    add.addEventListener("click", (event) => {
      event.preventDefault();
      const nextIndex = spec.items.length;
      if (getTimelineAuthoringSettings().autoCollapse) {
        rememberedRepeaterOpenItems.set(spec.key, /* @__PURE__ */ new Set([nextIndex]));
      } else {
        const remembered = rememberedRepeaterOpenItems.get(spec.key) ?? /* @__PURE__ */ new Set();
        for (const sibling of Array.from(
          add.parentElement?.children ?? []
        )) {
          if (sibling instanceof HTMLElement && sibling.classList.contains("vst-detail-repeating-group") && sibling.classList.contains("input-group-open")) {
            const siblingIndex = Number(
              sibling.dataset.vstRepeaterItem
            );
            if (Number.isInteger(siblingIndex)) {
              remembered.add(siblingIndex);
            }
          }
        }
        remembered.add(nextIndex);
        rememberedRepeaterOpenItems.set(spec.key, remembered);
      }
      rememberedRepeaterItems.set(spec.key, spec.items.length);
      forceOpenRepeaterKeys.add(spec.key);
      spec.add.onClick();
    });
    children.appendChild(add);
    const built = buildAccordionSection({
      key: spec.key,
      label: spec.label,
      content: children,
      counter: spec.items.length,
      open: forceOpen || spec.open,
      className: `vst-detail-repeating-editor ${spec.sectionClass ?? ""}`.trim()
    });
    built.section.dataset.vstRepeaterKey = spec.key;
    return {
      section: built.section,
      heading: built.heading,
      content: built.content,
      editor: spec.editor ?? null
    };
  };
  var clampStartLength = (start, length, clipDur, minLength) => {
    const s = clamp(start, 0, Math.max(0, clipDur - minLength));
    const l = clamp(length, minLength, Math.max(minLength, clipDur - s));
    return { start: s, length: l };
  };
  var wrapForm = (key, label, content) => {
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    body.appendChild(
      buildAccordionSection({
        key,
        label,
        content,
        open: true,
        flattenContent: true
      }).section
    );
    return body;
  };
  var tagFocus = (field, key) => {
    const control = field.querySelector("input.auto-slider-number") ?? field.querySelector("input, select") ?? (field.matches("input, select") ? field : null);
    control?.setAttribute("data-vst-focus-key", key);
    return field;
  };
  var buildStackSection = (key, label, colClass, open = false) => {
    const col = document.createElement("div");
    col.className = `vst-detail-col ${colClass}`;
    const built = buildAccordionSection({
      key,
      label,
      content: col,
      open,
      flattenContent: true
    });
    return { wrap: built.section, col: built.content };
  };

  // frontend/detailStrip/draftQueue.ts
  var asCommand = (outcome) => typeof outcome === "object" && outcome !== null && "command" in outcome ? outcome : null;
  var DEBOUNCE_MS = 200;
  var createDetailDraftQueue = (options) => {
    let sourceRevision = -1;
    let pendingTimer = null;
    let flushing = false;
    const pending = /* @__PURE__ */ new Map();
    const markCurrentSource = () => {
      sourceRevision = getTimelineStore().revision();
    };
    const isStale = () => getTimelineStore().revision() !== sourceRevision;
    const writeBackClamped = (entries, clips) => {
      const dock = options.getDock();
      if (!dock || !clips) {
        return;
      }
      for (const [key, entry] of entries) {
        if (!entry.readBack) {
          continue;
        }
        const input2 = dock.querySelector(
          `input[data-vst-focus-key="${key}"]`
        );
        const display = entry.readBack(clips);
        if (input2 && display != null && input2.value !== `${display}`) {
          input2.value = `${display}`;
        }
      }
    };
    const flush = () => {
      if (pendingTimer) {
        clearTimeout(pendingTimer);
        pendingTimer = null;
      }
      if (flushing || pending.size === 0) {
        return;
      }
      const entryList = [...pending.entries()];
      const entries = entryList.map(([, entry]) => entry);
      pending.clear();
      options.focus.capture();
      if (isStale()) {
        return;
      }
      const clipMutations = entries.filter((entry) => entry.kind === "clips").map((entry) => entry.mutate);
      const stateMutations = entries.filter((entry) => entry.kind === "state").map((entry) => entry.mutate);
      flushing = true;
      let flushedClips = null;
      try {
        if (clipMutations.length > 0) {
          const clips = getClips();
          for (const mutate of clipMutations) {
            mutate(clips);
          }
          saveClips(clips, {
            origin: "detail-strip",
            valueOnly: true
          });
          flushedClips = clips;
        }
        if (stateMutations.length > 0) {
          const state = getState();
          for (const mutate of stateMutations) {
            mutate(state);
          }
          saveState(state, {
            notifyDomChange: isVideoStagesEnabled(),
            origin: "detail-strip",
            valueOnly: true
          });
        }
        markCurrentSource();
      } finally {
        flushing = false;
      }
      writeBackClamped(entryList, flushedClips);
      options.syncValueDerivedUi(options.getRenderedSelection());
    };
    const schedule = (key, entry) => {
      if (options.isRendering()) {
        return;
      }
      pending.set(key, entry);
      if (pendingTimer) {
        clearTimeout(pendingTimer);
        pendingTimer = null;
      }
      if (options.focus.isTypingInDock() || options.focus.isSliderGesture()) {
        return;
      }
      pendingTimer = setTimeout(() => {
        pendingTimer = null;
        flush();
      }, DEBOUNCE_MS);
    };
    const commit = (mutate) => {
      flush();
      options.focus.capture();
      if (isStale()) {
        options.render();
        return;
      }
      const clips = getClips();
      mutate(clips);
      saveClips(clips, {
        origin: "detail-strip",
        valueOnly: true
      });
      markCurrentSource();
      options.syncValueDerivedUi(options.getRenderedSelection());
    };
    const commitState = (mutate) => {
      flush();
      options.focus.capture();
      if (isStale()) {
        options.render();
        return;
      }
      const state = getState();
      mutate(state);
      saveState(state, {
        notifyDomChange: isVideoStagesEnabled(),
        origin: "detail-strip",
        valueOnly: true
      });
      markCurrentSource();
      options.syncValueDerivedUi(options.getRenderedSelection());
    };
    const structuralCommit = (apply, structuralOptions) => {
      flush();
      if (isStale()) {
        options.render();
        return;
      }
      const snapshot = getTimelineStore().getSnapshot();
      const clips = getClips();
      const outcome = apply(clips);
      if (outcome === null) {
        return;
      }
      const commanded = asCommand(outcome);
      if (commanded) {
        const result = dispatchDocumentCommand(commanded.command, {
          origin: "detail-strip",
          expectedRevision: snapshot.revision
        });
        if (!result.applied) {
          options.render();
          return;
        }
      } else {
        saveClips(clips, { origin: "detail-strip" });
      }
      markCurrentSource();
      const selection = commanded ? commanded.selection : outcome;
      if (selection === null) {
        return;
      }
      if (selection === "render") {
        options.render();
        return;
      }
      if (structuralOptions?.rebuildAfterSelect) {
        options.setSelectionSilently(selection);
        options.render();
        return;
      }
      setSelection(selection);
    };
    return {
      markCurrentSource,
      flush,
      dispose: () => {
        flush();
        if (pendingTimer) {
          clearTimeout(pendingTimer);
          pendingTimer = null;
        }
        pending.clear();
      },
      commit,
      commitState,
      debouncedCommit: (key, mutate) => schedule(key, { kind: "clips", mutate }),
      debouncedCommitState: (key, mutate) => schedule(key, { kind: "state", mutate }),
      buildClampedNumber: (clampedOptions) => {
        const input2 = buildNumber(
          clampedOptions.value,
          clampedOptions.min,
          clampedOptions.max,
          clampedOptions.step,
          (value) => {
            schedule(clampedOptions.key, {
              kind: "clips",
              mutate: (clips) => clampedOptions.mutate(clips, value),
              readBack: clampedOptions.readBack
            });
          }
        );
        input2.setAttribute("data-vst-focus-key", clampedOptions.key);
        return input2;
      },
      structuralCommit
    };
  };

  // frontend/detailStrip/focusSession.ts
  var createDetailFocusSession = (options) => {
    let sliderDragActive = false;
    let focusLeftDock = false;
    let pendingFocus = null;
    const isTypingInDock = () => {
      const dock = options.getDock();
      const active = document.activeElement;
      if (!dock || !(active instanceof HTMLElement) || !dock.contains(active)) {
        return false;
      }
      return active instanceof HTMLTextAreaElement || active instanceof HTMLInputElement && (active.type === "text" || active.type === "number");
    };
    const isSliderGesture = () => {
      if (sliderDragActive) {
        return true;
      }
      const dock = options.getDock();
      const active = document.activeElement;
      if (!dock || !(active instanceof HTMLInputElement) || !dock.contains(active)) {
        return false;
      }
      return active.type === "range" || active.classList.contains("auto-slider-number");
    };
    const capture = () => {
      const dock = options.getDock();
      if (focusLeftDock) {
        pendingFocus = null;
        return;
      }
      const active = document.activeElement;
      if (!dock || !(active instanceof HTMLElement) || !dock.contains(active)) {
        pendingFocus = null;
        return;
      }
      const holder = active.closest("[data-vst-focus-key]");
      if (!holder || !dock.contains(holder)) {
        pendingFocus = null;
        return;
      }
      let start = null;
      let end = null;
      if (active instanceof HTMLInputElement && (active.type === "number" || active.type === "text") || active instanceof HTMLTextAreaElement) {
        try {
          start = active.selectionStart;
          end = active.selectionEnd;
        } catch {
        }
      }
      pendingFocus = {
        key: holder.getAttribute("data-vst-focus-key") ?? "",
        start,
        end
      };
    };
    const restore = (detail) => {
      const focus = pendingFocus;
      pendingFocus = null;
      if (!focus?.key) {
        return;
      }
      const holder = detail.querySelector(
        `[data-vst-focus-key="${focus.key}"]`
      );
      if (!holder) {
        return;
      }
      holder.focus();
      if ((holder instanceof HTMLInputElement || holder instanceof HTMLTextAreaElement) && focus.start != null) {
        try {
          holder.setSelectionRange(focus.start, focus.end ?? focus.start);
        } catch {
        }
      }
    };
    const focusKeyForSelection = (selection) => {
      if (selection.kind === "prompt-major") {
        return "prompt-major";
      }
      if (selection.kind === "prompt-minor") {
        return `minor-${selection.windowIdx}`;
      }
      return null;
    };
    const autoFocusSelection = (detail, selection) => {
      if (focusLeftDock) {
        return;
      }
      const active = document.activeElement;
      if (active instanceof HTMLElement && detail.contains(active)) {
        return;
      }
      const focusKey = focusKeyForSelection(selection);
      if (!focusKey) {
        return;
      }
      const editor = detail.querySelector(
        `textarea[data-vst-focus-key="${focusKey}"]`
      );
      if (!editor) {
        return;
      }
      editor.focus();
      const length = editor.value.length;
      try {
        editor.setSelectionRange(length, length);
      } catch {
      }
      if (typeof editor.scrollIntoView === "function") {
        editor.scrollIntoView({ block: "nearest" });
      }
    };
    const onDockFocusOut = (event) => {
      if (options.isRendering()) {
        return;
      }
      const next = event.relatedTarget;
      const dock = options.getDock();
      if (next instanceof Node && dock?.contains(next)) {
        return;
      }
      focusLeftDock = true;
      pendingFocus = null;
      options.flushPending();
    };
    const onDockFocusIn = () => {
      focusLeftDock = false;
    };
    const onDockChange = (event) => {
      const target = event.target;
      if (target instanceof HTMLInputElement && target.type === "number" && document.activeElement === target) {
        options.flushPending();
      }
    };
    const onDocumentPointerDown = (event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }
      if (!options.getDock()?.contains(target)) {
        options.flushPending();
        return;
      }
      if (target.closest('input[type="range"]')) {
        sliderDragActive = true;
      }
    };
    const onDocumentPointerUp = () => {
      if (!sliderDragActive) {
        return;
      }
      sliderDragActive = false;
      options.flushPending();
    };
    return {
      isTypingInDock,
      isSliderGesture,
      capture,
      restore,
      autoFocusSelection,
      beginSelectionSession: () => {
        pendingFocus = null;
        focusLeftDock = false;
      },
      reset: () => {
        sliderDragActive = false;
        focusLeftDock = false;
        pendingFocus = null;
      },
      onDockFocusOut,
      onDockFocusIn,
      onDockChange,
      onDocumentPointerDown,
      onDocumentPointerUp
    };
  };

  // frontend/documentQueries.ts
  var documentFps = (document2) => safeFps(document2.fps);
  var clipTimelineWindow = (clips, clipIdx) => {
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return null;
    }
    const startSeconds = clips.slice(0, clipIdx).reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
    return {
      startSeconds,
      endSeconds: startSeconds + Math.max(0, clips[clipIdx].duration || 0)
    };
  };
  var audioTrackIndicesForClipWindow = (state, clipIdx) => {
    const clipWindow = clipTimelineWindow(state.clips, clipIdx);
    if (!clipWindow || clipWindow.endSeconds <= clipWindow.startSeconds) {
      return [];
    }
    return (state.audioTracks ?? []).flatMap((track, trackIdx) => {
      const intersects = track.spans.some((span) => {
        const start = span.timelineStartSeconds;
        const length = span.timelineLengthSeconds;
        if (typeof start !== "number" || !Number.isFinite(start) || typeof length !== "number" || !Number.isFinite(length) || length <= 0) {
          return false;
        }
        const end = start + length;
        return start < clipWindow.endSeconds && end > clipWindow.startSeconds;
      });
      return intersects ? [trackIdx] : [];
    });
  };

  // frontend/detailStrip/audioTracksPanel.ts
  var timelineDuration2 = (state) => state.clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
  var primarySpan = (track) => track.spans[0] ?? null;
  var commitTrack = (ctx, trackId, mutate, debounceKey) => {
    const apply = (state) => {
      const track = state.audioTracks?.find((entry) => entry.id === trackId);
      const span = track ? primarySpan(track) : null;
      if (track && span) {
        mutate(track, span);
      }
    };
    if (debounceKey) {
      ctx.debouncedCommitState(debounceKey, apply);
    } else {
      ctx.commitState(apply);
    }
  };
  var buildTrackEditor = (ctx, state, track, trackIndex) => {
    const trackId = track.id;
    const span = primarySpan(track);
    const fields = document.createElement("div");
    fields.className = "vst-detail-col vst-detail-instance-fields vst-audio-track";
    fields.dataset.vstAudioTrackId = trackId;
    if (!span) {
      const note = document.createElement("p");
      note.className = "vst-detail-note";
      note.textContent = "This audio segment has no timeline window.";
      fields.appendChild(note);
      return fields;
    }
    const total = Math.max(AUDIO_SEGMENT_MIN_LENGTH, timelineDuration2(state));
    const clamped = () => clampStartLength(
      span.timelineStartSeconds ?? 0,
      span.timelineLengthSeconds ?? AUDIO_SEGMENT_DEFAULT_LENGTH,
      total,
      AUDIO_SEGMENT_MIN_LENGTH
    );
    const aceReference = track.source.kind === "AceStepFun" ? track.source.reference : "";
    const sourceSelect = buildOptionSelect(
      buildSegmentAudioSourceOptions(aceReference),
      aceReference || AUDIO_SOURCE_UPLOAD,
      (value) => {
        commitTrack(ctx, trackId, (next) => {
          if (isAceStepFunAudioSource(value)) {
            next.source.kind = "AceStepFun";
            next.source.reference = value;
            next.source.uploadedAudio = null;
          } else {
            next.source.kind = "Upload";
            next.source.reference = next.source.uploadedAudio?.fileName ?? "";
          }
        });
        ctx.render();
      }
    );
    fields.appendChild(
      buildField(
        "Source",
        sourceSelect,
        void 0,
        "Where this timeline-wide overlay comes from. It is cut at clip boundaries during generation and mixed additively."
      )
    );
    if (!aceReference) {
      fields.appendChild(
        buildMediaPickRow(
          "Audio Upload",
          "audio/*",
          ["audio"],
          track.source.uploadedAudio?.fileName,
          (data, fileName) => {
            commitTrack(ctx, trackId, (next) => {
              next.source.kind = "Upload";
              next.source.reference = fileName ?? "";
              next.source.uploadedAudio = { data, fileName };
            });
            ctx.render();
          },
          () => {
            commitTrack(ctx, trackId, (next) => {
              next.source.reference = "";
              next.source.uploadedAudio = null;
            });
            ctx.render();
          }
        )
      );
    }
    const volume = track.volume ?? AUDIO_SEGMENT_VOLUME_DEFAULT;
    const volumeSlider = buildSlider(
      "Volume",
      volume,
      AUDIO_SEGMENT_VOLUME_MIN,
      AUDIO_SEGMENT_VOLUME_MAX,
      AUDIO_SEGMENT_VOLUME_SLIDER_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (next) => {
            next.volume = Math.min(
              AUDIO_SEGMENT_VOLUME_MAX,
              Math.max(AUDIO_SEGMENT_VOLUME_MIN, value)
            );
          },
          `audio-track-${trackId}-volume`
        );
      },
      {
        sliderMin: AUDIO_SEGMENT_VOLUME_SLIDER_MIN,
        sliderMax: AUDIO_SEGMENT_VOLUME_SLIDER_MAX,
        numberStep: "any"
      }
    );
    volumeSlider.querySelector("input.auto-slider-number")?.setAttribute("data-vst-focus-key", `audio-track-${trackId}-volume`);
    fields.appendChild(volumeSlider);
    const geometry = clamped();
    const startInput = buildNumber(
      geometry.start,
      0,
      Math.max(0, total - AUDIO_SEGMENT_MIN_LENGTH),
      AUDIO_SEGMENT_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (_next, nextSpan) => {
            const next = clampStartLength(
              value,
              nextSpan.timelineLengthSeconds ?? AUDIO_SEGMENT_DEFAULT_LENGTH,
              total,
              AUDIO_SEGMENT_MIN_LENGTH
            );
            nextSpan.timelineStartSeconds = next.start;
            nextSpan.timelineLengthSeconds = next.length;
          },
          `audio-track-${trackId}-start`
        );
      }
    );
    startInput.setAttribute(
      "data-vst-focus-key",
      `audio-track-${trackId}-start`
    );
    fields.appendChild(
      buildField(
        "Timeline start (s)",
        startInput,
        void 0,
        "Seconds from the beginning of the complete multi-clip timeline."
      )
    );
    const trimInput = buildNumber(
      span.sourceStartSeconds,
      0,
      Number.MAX_SAFE_INTEGER,
      AUDIO_SEGMENT_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (_next, nextSpan) => {
            nextSpan.sourceStartSeconds = Math.max(0, value);
          },
          `audio-track-${trackId}-trim`
        );
      }
    );
    trimInput.setAttribute("data-vst-focus-key", `audio-track-${trackId}-trim`);
    fields.appendChild(
      buildField(
        "Trim start (s)",
        trimInput,
        void 0,
        "Skip this many seconds from the source before playback begins."
      )
    );
    const lengthInput = buildNumber(
      geometry.length,
      AUDIO_SEGMENT_MIN_LENGTH,
      total,
      AUDIO_SEGMENT_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (_next, nextSpan) => {
            const next = clampStartLength(
              nextSpan.timelineStartSeconds ?? 0,
              value,
              total,
              AUDIO_SEGMENT_MIN_LENGTH
            );
            nextSpan.timelineStartSeconds = next.start;
            nextSpan.timelineLengthSeconds = next.length;
          },
          `audio-track-${trackId}-length`
        );
      }
    );
    lengthInput.setAttribute(
      "data-vst-focus-key",
      `audio-track-${trackId}-length`
    );
    fields.appendChild(
      buildField(
        "Length (s)",
        lengthInput,
        void 0,
        "How long this segment plays across the complete timeline."
      )
    );
    fields.dataset.vstTrackIndex = `${trackIndex}`;
    return fields;
  };
  var addAudioTrack = (ctx, state, clipWindow) => {
    const total = Math.max(AUDIO_SEGMENT_MIN_LENGTH, timelineDuration2(state));
    const start = Math.min(
      Math.max(0, clipWindow?.startSeconds ?? 0),
      Math.max(0, total - AUDIO_SEGMENT_MIN_LENGTH)
    );
    const availableLength = Math.max(
      AUDIO_SEGMENT_MIN_LENGTH,
      Math.min(
        total - start,
        clipWindow ? clipWindow.endSeconds - clipWindow.startSeconds : total
      )
    );
    const nextIndex = state.audioTracks?.length ?? 0;
    ctx.commitState((next) => {
      next.audioTracks ??= [];
      next.audioTracks.push({
        id: createEntityId("audio_track"),
        source: {
          kind: "Upload",
          reference: "",
          uploadedAudio: null
        },
        volume: AUDIO_SEGMENT_VOLUME_DEFAULT,
        spans: [
          {
            id: createEntityId("audio_span"),
            timelineStartSeconds: start,
            timelineLengthSeconds: Math.min(
              AUDIO_SEGMENT_DEFAULT_LENGTH,
              availableLength
            ),
            sourceStartSeconds: 0
          }
        ]
      });
    });
    return nextIndex;
  };
  var buildAudioTracksPanel = (ctx, state, selection = {
    kind: "none"
  }, options) => {
    const tracks = state.audioTracks ?? [];
    const visibleTrackIndices = options?.trackIndices ? options.trackIndices.filter(
      (trackIndex) => trackIndex >= 0 && trackIndex < tracks.length
    ) : tracks.map((_, trackIndex) => trackIndex);
    const selectedTrackIndex = selection.kind === "audio-track" ? selection.trackIdx : null;
    const activeTrackIndex = selectedTrackIndex !== null && visibleTrackIndices.includes(selectedTrackIndex) ? selectedTrackIndex : visibleTrackIndices[0] ?? null;
    const built = buildRepeatingEditor({
      key: "audio-tracks",
      label: "Audio Segments",
      sectionClass: "vst-audio-tracks-panel",
      open: selectedTrackIndex !== null,
      items: visibleTrackIndices.map((trackIndex) => ({
        label: `S${trackIndex}`,
        focusKey: `audio-track-tab-${trackIndex}`,
        title: `Edit audio segment ${trackIndex}`,
        active: trackIndex === activeTrackIndex,
        className: "vst-audio-track-tab",
        onSelect: () => setSelection({ kind: "audio-track", trackIdx: trackIndex }),
        onDelete: () => {
          ctx.commitState((next) => {
            next.audioTracks?.splice(trackIndex, 1);
          });
          setSelection(
            selectionAfterRemoval(
              trackIndex,
              tracks.length - 1,
              (index) => ({ kind: "audio-track", trackIdx: index }),
              { kind: "none" }
            )
          );
          ctx.render();
        }
      })),
      editorForItem: (itemIndex) => {
        const trackIndex = visibleTrackIndices[itemIndex];
        const track = tracks[trackIndex];
        return track ? buildTrackEditor(ctx, state, track, trackIndex) : void 0;
      },
      add: {
        title: "Add a timeline-wide audio segment",
        label: "+ Add Audio Segment",
        className: "vst-audio-track-add",
        onClick: () => {
          const trackIdx = addAudioTrack(ctx, state, options?.clipWindow);
          setSelection({ kind: "audio-track", trackIdx });
          ctx.render();
        }
      },
      remove: {
        title: activeTrackIndex === null ? "No audio segment to delete" : `Delete audio segment ${activeTrackIndex}`,
        className: "vst-audio-track-delete"
      }
    });
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = tracks.length === 0 ? "No overlay segments." : visibleTrackIndices.length === 0 ? "No overlay segments in this clip." : "Timeline-wide overlays are cut per clip during generation; overlapping segments mix together.";
    built.content.insertBefore(note, built.content.firstChild);
    return built.section;
  };
  var buildTimelineAudioSegmentsBody = (ctx, state, selection) => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-audio-body";
    body.appendChild(buildAudioTracksPanel(ctx, state, selection));
    return body;
  };

  // frontend/detailStrip/capabilityUi.ts
  var buildCapabilityNotice = (decision) => {
    const notice = document.createElement("p");
    notice.className = "vst-detail-note vst-capability-notice";
    notice.textContent = decision.reason;
    notice.dataset.vstCapabilityUnsupported = "true";
    return notice;
  };
  var CAPABILITY_REPAIR_SELECTORS = [
    ".vst-detail-delete"
  ];
  var buildCapabilityRepairButton = (action) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `interrupt-button vst-btn-tiny vst-capability-repair ${action.className ?? ""}`.trim();
    button.textContent = action.label;
    button.title = action.title ?? action.label;
    button.setAttribute("aria-label", button.title);
    button.addEventListener("click", action.onRepair);
    return button;
  };
  var disableCapabilityControls = (root, decision, removableSelectors = []) => {
    const removable = new Set(
      removableSelectors.flatMap(
        (selector) => Array.from(root.querySelectorAll(selector))
      )
    );
    for (const control of root.querySelectorAll("input, select, textarea, button")) {
      if (removable.has(control) || [...removable].some((element) => element.contains(control))) {
        continue;
      }
      control.disabled = true;
      control.title = decision.reason;
    }
    if (root instanceof DocumentFragment) {
      for (const child of root.children) {
        child.classList.add("vst-capability-readonly");
      }
    } else {
      root.classList.add("vst-capability-readonly");
    }
    root.prepend(buildCapabilityNotice(decision));
  };
  var applyPersistedCapabilityRepair = (root, decision, options = {}) => {
    disableCapabilityControls(
      root,
      decision,
      options.keep ?? CAPABILITY_REPAIR_SELECTORS
    );
    if (options.repair) {
      root.appendChild(buildCapabilityRepairButton(options.repair));
    }
  };

  // frontend/detailStrip/audioPanel.ts
  var buildAudioBody = (ctx, sel, clips) => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const capabilityView = ctx.capabilities().forClip(clip);
    const audioDecision = capabilityView.decision("clipAudio");
    const reuseDecision = capabilityView.decision("audioReuse");
    const controlNetEnabled = hasArchitectureSlotSourcedIcLora(
      clip.architecture,
      clip.icLoras
    );
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
      controlNetEnabled,
      allowedKinds: capabilityView.audioSourceKinds
    });
    const source = options.find((option) => option.value === clip.audioSource)?.value ?? clip.audioSource ?? "";
    const canLength = canUseClipLengthFromAudio(source);
    const isAce = isAceStepFunAudioSource(source);
    const commitAudio = (mutate) => {
      ctx.commit((cs) => {
        const target = cs[clipIdx];
        if (!target) {
          return;
        }
        mutate(target);
        const nextSource = target.audioSource;
        target.clipLengthFromAudio = canUseClipLengthFromAudio(nextSource) && target.clipLengthFromAudio;
        if (target.clipLengthFromAudio) {
          target.clipLengthFromControlNet = false;
        }
        target.saveAudioTrack = isAceStepFunAudioSource(nextSource) && target.saveAudioTrack;
        target.uploadedAudio = nextSource === AUDIO_SOURCE_UPLOAD ? target.uploadedAudio : null;
      });
    };
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-audio-body";
    const base = document.createElement("div");
    base.className = "vst-detail-col vst-detail-audio";
    const select2 = buildOptionSelect(
      options.map((o) => ({ value: o.value, label: o.label })),
      source,
      (value) => {
        commitAudio((c) => {
          c.audioSource = value;
        });
        ctx.render();
      }
    );
    base.appendChild(
      buildField(
        "Audio Source",
        select2,
        void 0,
        "Where this clip's audio comes from: generated from the prompt, an uploaded file, or a connected generated-audio source."
      )
    );
    const reuseRow = buildCheckbox(
      "Reuse Captured Stage Audio",
      clip.reuseAudio === true,
      (value) => {
        commitAudio((c) => {
          c.reuseAudio = value;
        });
      },
      {
        disabled: !reuseDecision.supported,
        help: "Capture this clip's audio after its second active stage and reuse that captured audio from the third active stage onward. Requires at least three active stages." + (reuseDecision.reason ? ` ${reuseDecision.reason}` : "")
      }
    );
    base.appendChild(reuseRow);
    if (clip.reuseAudio && !reuseDecision.supported) {
      reuseRow.appendChild(buildCapabilityNotice(reuseDecision));
      reuseRow.appendChild(
        buildCapabilityRepairButton({
          label: "Remove unsupported reuse",
          className: "vst-detail-delete",
          onRepair: () => {
            commitAudio((target) => {
              target.reuseAudio = false;
            });
            ctx.render();
          }
        })
      );
    }
    const lengthRow = buildCheckbox(
      "Clip Length from Audio",
      clip.clipLengthFromAudio === true && canLength,
      (value) => {
        commitAudio((c) => {
          c.clipLengthFromAudio = value;
        });
      },
      {
        disabled: !canLength,
        help: "Set the clip's duration to match the length of its audio instead of a fixed value. Available only for sources with a known length."
      }
    );
    base.appendChild(lengthRow);
    const saveRow = buildCheckbox(
      "Save Audio Track",
      clip.saveAudioTrack === true && isAce,
      (value) => {
        commitAudio((c) => {
          c.saveAudioTrack = value;
        });
      },
      {
        disabled: !isAce,
        help: "Export the generated audio as a separate track alongside the video. Only available for generated (AceStep) audio."
      }
    );
    base.appendChild(saveRow);
    if (source === AUDIO_SOURCE_UPLOAD) {
      base.appendChild(
        buildMediaPickRow(
          "Audio Upload",
          "audio/*",
          ["audio"],
          clip.uploadedAudio?.fileName,
          (data, fileName) => {
            commitAudio((c) => {
              c.uploadedAudio = { data, fileName };
            });
            ctx.render();
          },
          () => {
            commitAudio((c) => {
              c.uploadedAudio = null;
            });
            ctx.render();
          }
        )
      );
    }
    if (!audioDecision.supported) {
      applyPersistedCapabilityRepair(base, audioDecision, {
        repair: {
          label: "Remove unsupported clip audio",
          className: "vst-remove-unsupported-audio",
          onRepair: () => {
            ctx.structuralCommit((items) => {
              const target = items[clipIdx];
              if (!target) {
                return null;
              }
              target.audioSource = "Native";
              target.uploadedAudio = null;
              target.reuseAudio = false;
              target.clipLengthFromAudio = false;
              target.saveAudioTrack = false;
              return "render";
            });
          }
        }
      });
    }
    body.appendChild(
      buildAccordionSection({
        key: "base-audio",
        label: "Base Audio",
        content: base,
        open: true,
        flattenContent: true
      }).section
    );
    const state = getState();
    body.appendChild(
      buildAudioTracksPanel(
        ctx,
        state,
        { kind: "none" },
        {
          trackIndices: audioTrackIndicesForClipWindow(state, clipIdx),
          clipWindow: clipTimelineWindow(state.clips, clipIdx) ?? void 0
        }
      )
    );
    return body;
  };

  // frontend/boundaryPlan.ts
  var crossfadePlanForClips = (clips, fps, resolveConstraints = (clip, _index, mode) => {
    const generic = boundaryOverlapConstraints(null);
    const persisted = Math.trunc(Number(clip.boundaryOutOverlap));
    return {
      ...generic,
      defaultFrames: mode === "cut" || !Number.isFinite(persisted) || persisted <= 0 ? generic.defaultFrames : persisted
    };
  }) => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const noOverlap = () => new Array(boundaryCount).fill(0);
    if (count < 2) {
      return { overlaps: noOverlap(), fallback: false };
    }
    let requested = 0;
    const modes = [];
    for (let i = 0; i < count - 1; i++) {
      const b = clips[i].boundaryOut ?? "cut";
      modes[i] = b;
      if (b === "crossfade" || b === "continue") {
        requested++;
      }
    }
    if (requested === 0) {
      return { overlaps: noOverlap(), fallback: false };
    }
    const frames = clips.map((c) => framesForClip(c.duration, fps));
    const constraints = clips.map(
      (clip, index) => resolveConstraints(clip, index, clip.boundaryOut ?? "cut")
    );
    const prefs = clips.map(
      (clip, index) => normalizeBoundaryOverlap(clip.boundaryOutOverlap, constraints[index])
    );
    const active = (index) => modes[index] === "continue" || modes[index] === "crossfade";
    const trim = (index) => modes[index] === "continue" ? prefs[index] + constraints[index].continuityExtraFrames : modes[index] === "crossfade" ? prefs[index] : 0;
    while (true) {
      let overBudgetClip = -1;
      for (let i = 0; i < count; i++) {
        const left = i > 0 ? trim(i - 1) : 0;
        const right = i < boundaryCount ? trim(i) : 0;
        if (left + right > frames[i] - 1) {
          overBudgetClip = i;
          break;
        }
      }
      if (overBudgetClip < 0) break;
      const candidate = overBudgetClip < boundaryCount && active(overBudgetClip) ? overBudgetClip : overBudgetClip > 0 && active(overBudgetClip - 1) ? overBudgetClip - 1 : -1;
      if (candidate < 0) {
        return { overlaps: noOverlap(), fallback: true };
      }
      const reduced = prefs[candidate] - constraints[candidate].frameStep;
      if (reduced < constraints[candidate].minFrames) {
        modes[candidate] = "cut";
        prefs[candidate] = 0;
      } else {
        prefs[candidate] = reduced;
      }
    }
    const overlaps = modes.slice(0, boundaryCount).map((_mode, index) => trim(index));
    return {
      overlaps,
      fallback: requested > 0 && !modes.some((_mode, index) => active(index))
    };
  };

  // frontend/skipVocabulary.ts
  var skipGlyph = (skipped) => skipped ? "⟲" : "⏭︎";
  var skipTitle = (subject, skipped) => `${skipped ? "Re-enable" : "Skip"} ${subject}`;

  // frontend/timelineView/rendering.ts
  var clipInnerWidth = (widthPx) => Math.max(1, widthPx - 2);
  var backgroundImageDataAttr = (source) => ` data-vst-background-image="${escapeAttr(source)}"`;
  var applyBackgroundImages = (root) => {
    for (const element of root.querySelectorAll(
      "[data-vst-background-image]"
    )) {
      const source = element.dataset.vstBackgroundImage;
      if (source) {
        element.style.backgroundImage = `url(${JSON.stringify(source)})`;
      }
      element.removeAttribute("data-vst-background-image");
    }
  };
  var renderWindowSpan = (options) => {
    const { left, width, empty } = spanGeometry(
      options.startSeconds,
      options.lengthSeconds,
      options.durationSeconds
    );
    if (empty) {
      return "";
    }
    return `<div class="${options.className}${options.extraClassName ? ` ${options.extraClassName}` : ""}" ${options.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeAttr(options.title)}" aria-label="${escapeAttr(options.ariaLabel)}"><span class="${options.className}-resize ${options.className}-resize-l" ${options.edgeAttr}="left" aria-hidden="true"></span>` + (options.decoration ?? "") + `<span class="${options.labelClass}">${escapeAttr(options.label)}</span><span class="${options.className}-resize ${options.className}-resize-r" ${options.edgeAttr}="right" aria-hidden="true"></span></div>`;
  };
  var headTag = (kind, label, options) => {
    const classes = `vst-head-tag vst-head-tag-${kind}` + (options?.active ? " vst-head-tag-active" : "") + (options?.muted ? " vst-head-tag-muted" : "");
    const style = options?.style ? ` style="${options.style}"` : "";
    return `<div class="${classes}"${style} aria-hidden="true"><span class="vst-head-tag-pill">${label}</span><span class="vst-head-tag-tick"></span></div>`;
  };
  var renderTrackHead = (iconClass, icon, title, tags) => `<div class="vst-track-head"><div class="vst-head-top"><div class="vst-track-icon ${iconClass}" aria-hidden="true">${icon}</div><div class="vst-track-label"><strong>${title}</strong></div></div>` + (tags ? `<div class="vst-head-tags">${tags}</div>` : "") + `</div>`;

  // frontend/timelineView/regionRenderer.ts
  var refFrame = (ref) => Math.max(0, ref.frame ?? 0);
  var renderRegionThumb = (clip) => {
    const withImage = (clip.refs ?? []).filter(
      (ref) => !!ref.uploadedImage?.data
    );
    if (withImage.length === 0) {
      return "";
    }
    const startPool = withImage.filter((ref) => ref.fromEnd !== true);
    const startRef = (startPool.length > 0 ? startPool : withImage).reduce(
      (best, ref) => refFrame(ref) < refFrame(best) ? ref : best
    );
    let endRef = withImage.find((ref) => ref.fromEnd === true) ?? null;
    if (!endRef) {
      const highest = withImage.reduce(
        (best, ref) => refFrame(ref) > refFrame(best) ? ref : best
      );
      if (highest !== startRef) {
        endRef = highest;
      }
    }
    const cell = (ref, side) => {
      const source = mediaPreviewSrc(ref.uploadedImage?.data ?? "");
      return `<div class="vst-region-thumb-cell vst-region-thumb-${side}"${backgroundImageDataAttr(source)}></div>`;
    };
    const cells = cell(startRef, "start") + (endRef ? cell(endRef, "end") : "");
    return `<div class="vst-region-thumb" data-cells="${endRef ? 2 : 1}" aria-hidden="true">${cells}</div>`;
  };
  var renderRetakeRegionShade = (clip, durationSeconds) => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
      return "";
    }
    const { left, width, empty } = spanGeometry(
      retake.startSeconds,
      retake.lengthSeconds,
      durationSeconds
    );
    if (empty) {
      return "";
    }
    return `<div class="vst-region-off" style="left:${left}%;width:${width}%" aria-hidden="true"></div>`;
  };
  var renderRetakeOverlay = (clip, clipIdx, durationSeconds) => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
      return "";
    }
    const { startSeconds: start, endSeconds: end } = spanGeometry(
      retake.startSeconds,
      retake.lengthSeconds,
      durationSeconds
    );
    const label = `RETAKE ${roundToTenth(start)}–${roundToTenth(end)} s`;
    return renderWindowSpan({
      className: "vst-retake",
      dataAttrs: `data-vst-retake data-clip-idx="${clipIdx}"`,
      edgeAttr: "data-vst-retake-edge",
      labelClass: "vst-retake-label",
      label,
      title: `${label} · drag to move/resize · Shift+click to delete`,
      ariaLabel: label,
      startSeconds: retake.startSeconds,
      lengthSeconds: retake.lengthSeconds,
      durationSeconds
    });
  };
  var renderKeyframes = (clip, clipIdx, durationSeconds, fps, unit) => {
    const refs = clip.refs ?? [];
    if (refs.length === 0) {
      return "";
    }
    const markers = refs.map((ref, refIdx) => {
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
      const title = `${source} · frame ${ref.frame ?? 0}${isEnd ? " (from end)" : ""}${isPrimary ? " (cover)" : ""} · ${formatTimeLabel(time, unit, fps)}`;
      const kindClass = (isEnd ? " vst-key-end" : " vst-key-start") + (isPrimary ? " vst-key-primary" : "");
      return `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeAttr(title)}" aria-hidden="true"><span class="vst-key-dot" aria-hidden="true"></span></span>`;
    }).join("");
    return `<div class="vst-keys" title="Reference markers">${markers}</div>`;
  };
  var renderBadges = (clip, clipIdx) => {
    const firstStage = (clip.stages ?? [])[0];
    if (!firstStage) {
      return `<div class="vst-badges"></div>`;
    }
    const model = firstStage.model ?? "";
    const title = `Clip model: ${`${model}`.trim() || "(default)"} — click to change (applies to Stage 0)`;
    const modelBadge = `<span class="vst-badge vst-badge-model" data-vst-model data-clip-idx="${clipIdx}" role="button" tabindex="0" title="${escapeAttr(title)}" aria-label="${escapeAttr(title)}">${escapeAttr(shortModelName(model))}</span>`;
    const icLoraCount = (clip.icLoras ?? []).length;
    const icLoraTitle = `${icLoraCount} IC-LoRA${icLoraCount === 1 ? "" : "s"} on this clip — edit in the clip panel`;
    const icLoraBadge = icLoraCount > 0 ? `<span class="vst-badge vst-badge-iclora" title="${escapeAttr(icLoraTitle)}" aria-label="${escapeAttr(icLoraTitle)}">IC×${icLoraCount}</span>` : "";
    return `<div class="vst-badges">${modelBadge}${icLoraBadge}</div>`;
  };
  var renderStageChips = (clip, clipIdx) => (clip.stages ?? []).map((stage, stageIdx) => {
    const skipped = stage?.skipped === true;
    const skippedClass = skipped ? " vst-stage-chip-skipped" : "";
    const title = `${stageChipTitle(stage, stageIdx)}${skipped ? " (skipped)" : ""} · click to edit · Shift+click to delete`;
    const label = `${skipped ? "⊘ " : ""}${stageChipLabel(stageIdx)}`;
    return `<span class="vst-chip vst-stage-chip${skippedClass}" data-vst-stage data-clip-idx="${clipIdx}" data-stage-idx="${stageIdx}" role="button" tabindex="0" title="${escapeAttr(title)}">${escapeAttr(label)}</span>`;
  }).join("");
  var lengthDerived = (clip) => clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true;
  var BOUNDARY_GLYPH = {
    cut: "│",
    continue: "→",
    crossfade: "⤬"
  };
  var BOUNDARY_LABEL = {
    cut: "Cut",
    continue: "Continue",
    crossfade: "Crossfade"
  };
  var renderBoundarySeams = (clips, layouts, capabilities) => executableBoundaries(clips).flatMap((seam) => {
    const layout = layouts[seam.rightIdx];
    if (!layout) {
      return [];
    }
    const clip = clips[seam.leftIdx];
    const value = clip.boundaryOut ?? "cut";
    const capability = capabilities?.forBoundaryIndex(
      clips,
      seam.leftIdx
    );
    const effective = capability?.effective(value) ?? value;
    const glyph = BOUNDARY_GLYPH[effective] ?? BOUNDARY_GLYPH.cut;
    const label = BOUNDARY_LABEL[value] ?? BOUNDARY_LABEL.cut;
    const effectiveLabel = BOUNDARY_LABEL[effective];
    const fallback = value === effective ? "" : ` Requested ${label}; effective ${effectiveLabel}.`;
    const title = `Boundary clip ${seam.leftIdx} → ${seam.rightIdx}: ${label}.${fallback} Click to edit.`;
    const ariaLabel = `Clip ${seam.leftIdx} outgoing boundary: ${label}.${fallback} Click to edit.`;
    return [
      `<button type="button" class="basic-button vst-boundary-chip vst-boundary-${effective}${value === effective ? "" : " vst-boundary-fallback"}" data-vst-boundary-chip data-left-clip-idx="${seam.leftIdx}" data-right-clip-idx="${seam.rightIdx}" data-boundary="${value}" data-effective-boundary="${effective}" style="left:${layout.startPx}px" title="${escapeAttr(title)}" aria-label="${escapeAttr(ariaLabel)}"><span class="vst-boundary-glyph" aria-hidden="true">${escapeAttr(glyph)}</span></button>`
    ];
  }).join("");
  var renderRegions = (clips, layouts, fps, unit, capabilities) => layouts.map((layout) => {
    const clip = clips[layout.index];
    const skippedClass = layout.skipped ? " vst-region-skipped" : "";
    const tinyClass = layout.widthPx <= 12 ? " vst-region-tiny" : "";
    const skippedChip = layout.skipped ? `<span class="vst-chip vst-chip-skip">skipped</span>` : "";
    const duration = escapeAttr(
      formatTimeLabel(layout.durationSeconds, unit, fps)
    );
    const skipLabel = skipTitle("clip", layout.skipped);
    const skipMark = skipGlyph(layout.skipped);
    const controls = `<div class="vst-region-controls"><button type="button" class="vst-region-btn${layout.skipped ? " vst-region-btn-active" : ""}" data-vst-region-action="skip" title="${skipLabel}" aria-label="${skipLabel}">${skipMark}</button></div>`;
    const resizeGrip = lengthDerived(clip) ? "" : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
    const width = clipInnerWidth(layout.widthPx);
    const retakeSupported = capabilities?.forClip(clip).decision("retake").supported ?? true;
    const canAddRetake = retakeSupported && !clip.retake;
    const retakeLaneAttrs = canAddRetake ? " data-vst-retake-add" : retakeSupported ? " data-vst-retake-full" : ' data-vst-capability-disabled="retake"';
    const retakeLaneTitle = canAddRetake ? "Click empty space to add a retake window" : retakeSupported ? "This clip already has a retake window" : "Retakes are not supported by this clip architecture";
    return `<div class="vst-region${skippedClass}${tinyClass}" style="left:${layout.startPx}px;width:${width}px;--clip-hue:${clipHueCss(clip.hue)}" data-clip-idx="${layout.index}" title="Clip ${layout.index} · ${duration} · Click to edit · Shift+click to delete">` + renderRegionThumb(clip) + renderRetakeRegionShade(clip, layout.durationSeconds) + renderKeyframes(
      clip,
      layout.index,
      layout.durationSeconds,
      fps,
      unit
    ) + `<div class="vst-region-head"><span class="vst-region-name">Clip ${layout.index}</span>` + renderStageChips(clip, layout.index) + `<span class="vst-chip" title="Keyframes">◆ ${layout.keyframeCount}</span>` + skippedChip + `<span class="vst-region-dur">${duration}</span></div>` + renderBadges(clip, layout.index) + controls + resizeGrip + `</div><div class="vst-retake-lane${retakeSupported ? "" : " vst-capability-disabled"}"${retakeLaneAttrs} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${retakeLaneTitle}">` + renderRetakeOverlay(
      clip,
      layout.index,
      layout.durationSeconds
    ) + `</div>`;
  }).join("");
  var renderVideoTrackRow = (clips, layouts, fps, unit, capabilities) => {
    const head = renderTrackHead(
      "vst-track-icon-video",
      "▶",
      "Video",
      headTag("clip", "Clip", { active: true }) + headTag("retake", "Retake", {
        active: clips.some((clip) => clip.retake != null)
      })
    );
    return `<div class="vst-track-row vst-track-video">${head}<div class="vst-track-cell">` + renderRegions(clips, layouts, fps, unit, capabilities) + renderBoundarySeams(clips, layouts, capabilities) + `</div></div>`;
  };

  // frontend/dimensionPresets.ts
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
  var DIMENSION_PRESET_KEYS = Object.keys(
    DIMENSION_PRESET_METADATA
  );
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

  // frontend/timelineView/toolbar.ts
  var renderDiagnosticPanel = (diagnostics = []) => {
    const content = diagnostics.map(
      (item) => `<div class="vst-diagnostic vst-diagnostic-${item.severity}" data-vst-diagnostic="${escapeAttr(item.code)}">${item.clipIdx === void 0 ? "" : `<strong>Clip ${item.clipIdx}:</strong> `}${escapeAttr(item.message)}</div>`
    ).join("");
    return content ? `<div class="vst-diagnostics" role="status">${content}</div>` : "";
  };
  var renderTimelineHeader = (clipCount, totalSeconds, fps, unit, pxPerSecond, options) => {
    const toggleLabel = unit === "frames" ? "Show seconds" : "Show frames";
    const clipWord = `clip${clipCount === 1 ? "" : "s"}`;
    const totalLabel = escapeAttr(formatTimeLabel(totalSeconds, unit, fps));
    const zoomPct = Math.round(pxPerSecond / DEFAULT_PX_PER_SECOND * 100);
    const rawSelected = options?.selectedIndex;
    const selectedIndex = typeof rawSelected === "number" && Number.isInteger(rawSelected) && rawSelected >= 0 && rawSelected < clipCount ? rawSelected : null;
    const selectedHidden = selectedIndex === null ? " hidden" : "";
    const readout = `<span class="vst-readout" data-vst-readout><span title="Sequence total">${totalLabel} total</span><span class="vst-dot" data-vst-readout-sel-dot${selectedHidden}>·</span><span class="vst-readout-sel" data-vst-readout-sel title="Selected clip"${selectedHidden}>${selectedIndex !== null ? `clip ${selectedIndex}` : ""}</span></span>`;
    const width = Math.max(0, Math.round(options?.width ?? 0));
    const height = Math.max(0, Math.round(options?.height ?? 0));
    const dimsExplicit = options?.dimsExplicit === true;
    const presetKey = dimsExplicit && width > 0 && height > 0 ? matchPresetKey(width, height) : null;
    const dimsSource = dimsExplicit ? presetKey ? `${presetKey} preset` : "custom" : "inherited from image resolution";
    const fpsSource = "synced with Video FPS";
    const settingsTip = `Resolution: ${dimsSource}; FPS: ${fpsSource}. Click to edit.`;
    const settingsChip = `<button type="button" class="basic-button small-button vst-settings-chip" data-vst-settings title="${escapeAttr(settingsTip)}" aria-label="${escapeAttr(settingsTip)}"><span class="vst-settings-dims">${width}×${height}</span><span class="vst-settings-chip-sep" aria-hidden="true">·</span><span class="vst-settings-fps">${fps} fps</span></button>`;
    const enabled = options?.enabled !== false;
    const enableToggle = `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual."><span class="toggle-switch"><input type="checkbox" class="auto-slider-toggle vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}><div class="auto-slider-toggle-content"></div></span><span class="vst-enable-label">Enable</span></label>`;
    return `<div class="vst-topbar${enabled ? "" : " vst-topbar-disabled"}"><div class="vst-topbar-main"><span class="vst-title">Timeline</span>` + enableToggle + `<span class="vst-sub"><span class="vst-stat-num">${clipCount}</span> ${clipWord}</span>` + settingsChip + `</div><div class="vst-topbar-tools"><button type="button" class="basic-button small-button btn-primary vst-add-clip" data-vst-add-clip title="Add a new clip to the end of the sequence">+ Clip</button><span class="vst-tool-sep" aria-hidden="true"></span><div class="vst-zoom" role="group" aria-label="Timeline zoom (Ctrl+wheel over the track)"><button type="button" class="basic-button small-button" data-vst-zoom-out title="Zoom out (show more time)" aria-label="Zoom out">−</button><span class="vst-zoom-pct" data-vst-zoom-pct title="Zoom level (100% = default)">${zoomPct}%</span><input type="range" class="vst-zoom-slider" data-vst-zoom-slider min="${MIN_PX_PER_SECOND}" max="${MAX_PX_PER_SECOND}" step="1" value="${Math.round(pxPerSecond)}" aria-label="Zoom (pixels per second)" title="Zoom (applies on release)"><button type="button" class="basic-button small-button" data-vst-zoom-in title="Zoom in (show less time, more detail)" aria-label="Zoom in">+</button><button type="button" class="basic-button small-button" data-vst-zoom-fit title="Fit the whole sequence to the view" aria-label="Zoom to fit">Fit</button></div><span class="vst-tool-sep" aria-hidden="true"></span><button type="button" class="basic-button small-button vst-toggle-unit" data-vst-unit-toggle title="Toggle ruler units between seconds and frames (in-memory only)">${toggleLabel}</button><button type="button" class="basic-button small-button vst-hist-btn" data-vst-undo title="Undo (Ctrl+Z)" aria-label="Undo">↶</button><button type="button" class="basic-button small-button vst-hist-btn" data-vst-redo title="Redo (Ctrl+Shift+Z or Ctrl+Y)" aria-label="Redo">↷</button></div>` + readout + `</div>`;
  };
  var wireTimelineToolbar = (body, options) => {
    const wire = (selector, handler) => {
      if (!handler) {
        return;
      }
      body.querySelector(selector)?.addEventListener(
        "click",
        () => handler()
      );
    };
    const enableInput = body.querySelector("[data-vst-enable]");
    if (enableInput && options?.onToggleEnabled) {
      enableInput.addEventListener("change", () => {
        options.onToggleEnabled?.(enableInput.checked);
      });
    }
    const settingsButton = body.querySelector(
      "[data-vst-settings]"
    );
    if (settingsButton && options?.onOpenSettings) {
      settingsButton.addEventListener("click", () => {
        options.onOpenSettings?.(settingsButton);
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
        const percent = body.querySelector(
          "[data-vst-zoom-pct]"
        );
        if (percent) {
          const value = Number.parseFloat(slider.value);
          percent.textContent = `${Math.round(value / DEFAULT_PX_PER_SECOND * 100)}%`;
        }
      });
      if (options?.onZoomSlider) {
        slider.addEventListener("change", () => {
          options.onZoomSlider?.(Number.parseFloat(slider.value));
        });
      }
    }
    if (options?.onAddClip) {
      for (const button of body.querySelectorAll("[data-vst-add-clip]")) {
        button.addEventListener("click", () => options.onAddClip?.());
      }
    }
  };
  var wireTimelineZoomWheel = (body, options) => {
    const onZoomWheel = options?.onZoomWheel;
    if (!onZoomWheel) {
      return;
    }
    body.querySelector(".vst-scroll")?.addEventListener(
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

  // frontend/timelineView/trackRows.ts
  var promptWindowGeom = (layout, window2, pxPerSecond) => {
    const geometry = spanGeometry(
      window2.start,
      window2.duration,
      layout.durationSeconds,
      { unit: "px", pxPerSecond, minWidth: 2 }
    );
    return {
      startSec: geometry.startSeconds,
      endSec: geometry.endSeconds,
      leftPx: geometry.left,
      widthPx: geometry.width,
      active: `${window2.prompt ?? ""}`.trim() !== ""
    };
  };
  var PROMPT_PLACEHOLDER = "(no prompt)";
  var renderPromptTrackRow = (clips, layouts, pxPerSecond, globalPrompt, capabilities) => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const parts = [];
    for (let i = 0; i < layouts.length; i++) {
      const layout = layouts[i];
      const clip = clips[i];
      if (!clip) {
        continue;
      }
      const width = clipInnerWidth(layout.widthPx);
      const windows = clip.promptWindows ?? [];
      const clipCapabilities = capabilities?.forClip(clip);
      const majorSupported = clipCapabilities?.decision("majorPrompt").supported ?? true;
      const relaySupported = clipCapabilities?.decision("promptRelay").supported ?? true;
      const ownPrompt = `${clip.prompt ?? ""}`.trim();
      const inherited = ownPrompt === "";
      const major = inherited ? globalTrimmed : ownPrompt;
      const overlays = windows.map((window2) => promptWindowGeom(layout, window2, pxPerSecond)).filter(
        (geometry) => geometry.active && geometry.endSec > geometry.startSec
      ).map(
        (geometry) => `<div class="vst-major-off" style="left:${geometry.leftPx}px;width:${geometry.widthPx}px" aria-hidden="true"></div>`
      ).join("");
      const majorText = major === "" ? PROMPT_PLACEHOLDER : truncate(major, 120);
      const majorClass = (major === "" ? " vst-major-empty" : "") + (inherited && major !== "" ? " vst-major-inherited" : "");
      const majorTitle = (major === "" ? PROMPT_PLACEHOLDER : major) + (inherited && major !== "" ? " — inherited from the global prompt; click to set a clip prompt" : " — click to edit");
      if (majorSupported || ownPrompt !== "") {
        parts.push(
          `<div class="vst-major-seg${majorClass}${majorSupported ? "" : " vst-capability-disabled"}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${escapeAttr(majorSupported ? majorTitle : `${majorTitle} — unsupported by this architecture`)}">` + overlays + `<span class="vst-major-text">${escapeAttr(majorText)}</span></div>`
        );
      }
      const minorSegments = windows.map((window2, windowIdx) => {
        const geometry = promptWindowGeom(layout, window2, pxPerSecond);
        const text2 = `${window2.prompt ?? ""}`.trim();
        const label = text2 === "" ? "(empty)" : truncate(text2, 60);
        return `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${windowIdx}" style="left:${geometry.leftPx}px;width:${geometry.widthPx}px" title="${escapeAttr(`${text2 || "(empty minor prompt)"} · Shift+click to delete`)}"><span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span><span class="vst-minor-text">${escapeAttr(label)}</span><span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span></div>`;
      }).join("");
      if (relaySupported || windows.length > 0) {
        parts.push(
          `<div class="vst-minor-lane${relaySupported ? "" : " vst-capability-disabled"}"${relaySupported ? " data-vst-prompt-add" : ""} data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${relaySupported ? "Click empty space to add a minor prompt" : "Relay prompts are unsupported; existing windows can be removed"}">${minorSegments}</div>`
        );
      }
    }
    return `<div class="vst-track-row vst-track-prompt">` + renderTrackHead(
      "vst-track-icon-prompt",
      "✎",
      "Prompt",
      headTag("major", "Major", { active: true }) + headTag("relay", "Relay", {
        active: clips.some(
          (clip) => (clip.promptWindows?.length ?? 0) > 0
        )
      })
    ) + `<div class="vst-track-cell vst-prompt-cell">${parts.join("")}</div></div>`;
  };
  var audioFlagChips = (clip) => {
    const chips = [];
    if (clip.reuseAudio === true) {
      chips.push(
        `<span class="vst-audio-flag" title="Capture audio after the second active stage and reuse it from the third active stage onward">↻</span>`
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
  var renderTimelineAudioSegmentBlock = (track, trackIdx, totalSeconds) => {
    const span = track.spans[0];
    if (!span || span.timelineStartSeconds === null || span.timelineLengthSeconds === null) {
      return "";
    }
    const { startSeconds: start, endSeconds: end } = spanGeometry(
      span.timelineStartSeconds,
      span.timelineLengthSeconds,
      totalSeconds
    );
    const labelText = track.source.reference || track.source.uploadedAudio?.fileName || "audio segment";
    const rangeLabel = `${roundToTenth(start)}–${roundToTenth(end)} s`;
    const waveform = audioSegmentWaveBarHeights(trackIdx, trackIdx, 40).map((height) => `<span style="height:${height}%"></span>`).join("");
    return renderWindowSpan({
      className: "vst-audio-seg",
      extraClassName: `vst-audio-seg-tone-${trackIdx % 5}`,
      dataAttrs: `data-vst-audio-seg data-track-idx="${trackIdx}"`,
      edgeAttr: "data-vst-audio-seg-edge",
      labelClass: "vst-audio-label",
      label: labelText,
      title: `${labelText} · ${rangeLabel} · drag to move/resize · Shift+click to delete`,
      ariaLabel: `Edit timeline audio segment ${trackIdx}`,
      startSeconds: start,
      lengthSeconds: end - start,
      durationSeconds: totalSeconds,
      decoration: `<span class="vst-audio-seg-wave" aria-hidden="true">${waveform}</span>`
    });
  };
  var renderTimelineAudioSegmentLanes = (tracks, totalSeconds, totalWidthPx) => {
    const place = (laneIdx) => `left:0;width:${totalWidthPx}px;--vst-audio-lane-idx:${laneIdx}`;
    const lanes = tracks.map(
      (track, trackIdx) => `<div class="vst-audio-seg-lane" data-track-idx="${trackIdx}" style="${place(trackIdx)}">` + renderTimelineAudioSegmentBlock(track, trackIdx, totalSeconds) + `</div>`
    );
    lanes.push(
      `<div class="vst-audio-seg-lane vst-audio-seg-lane-blank" data-vst-audio-seg-add style="${place(tracks.length)}" title="Click or drag to add a timeline-wide audio segment"></div>`
    );
    return lanes.join("");
  };
  var renderAudioTrackRow = (clips, layouts, capabilities, audioTracks = [], pxPerSecond = 1) => {
    const baseSegments = layouts.map((layout) => {
      const clip = clips[layout.index];
      if (!clip) {
        return "";
      }
      const badge = audioSourceBadge(clip.audioSource ?? "");
      const clipCapabilities = capabilities?.forClip(clip);
      const clipAudioSupported = clipCapabilities?.decision("clipAudio").supported ?? true;
      const persistedAudio = clip.audioSource !== "Native" || clip.uploadedAudio !== null || clip.reuseAudio || clip.clipLengthFromAudio || clip.saveAudioTrack;
      const native = badge.label === "Native";
      const width = clipInnerWidth(layout.widthPx);
      const kindClass = native ? " vst-audio-native vst-audio-kind-native" : isAceStepFunAudioSource(clip.audioSource ?? "") ? " vst-audio-kind-ace" : " vst-audio-kind-upload";
      const upload = !native && clip.audioSource === "Upload" ? clip.uploadedAudio?.fileName : null;
      const labelText = upload ? `${badge.label} · ${upload}` : badge.label;
      const title = native ? "Audio: Native — click to choose an audio source" : `${badge.title} — click to edit`;
      const barCount = Math.min(
        400,
        Math.max(8, Math.floor(width / 5.5))
      );
      const bars = waveBarHeights(layout.index, barCount).map((height) => `<span style="height:${height}%"></span>`).join("");
      const hint = native ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>` : "";
      const body = `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>${hint}`;
      return `<div class="vst-audio-clip${kindClass}${clipAudioSupported ? "" : " vst-capability-disabled"}"${clipAudioSupported || persistedAudio ? ' data-vst-audio="clip"' : ""} data-clip-idx="${layout.index}" role="button" tabindex="0" style="left:${layout.startPx}px;width:${width}px" title="${escapeAttr(clipAudioSupported ? title : "Clip audio is unsupported; click persisted audio to remove it")}" aria-label="Edit audio for clip ${layout.index}"><span class="vst-audio-label">${escapeAttr(labelText)}</span>` + audioFlagChips(clip) + body + `</div>`;
    }).join("");
    const totalSeconds = layouts.reduce(
      (sum, layout) => sum + layout.durationSeconds,
      0
    );
    const totalWidthPx = totalSeconds * pxPerSecond;
    const overlaySegments = renderTimelineAudioSegmentLanes(
      audioTracks,
      totalSeconds,
      totalWidthPx
    );
    const laneCount = Math.max(1, audioTracks.length + 1);
    const laneTags = [headTag("src", "Clip", { active: true })];
    for (let i = 0; i < laneCount; i++) {
      const blank = i === laneCount - 1;
      laneTags.push(
        headTag("seg", blank ? "+" : `S${i}`, {
          active: !blank,
          muted: blank,
          style: `--vst-audio-lane-idx:${i}`
        })
      );
    }
    return `<div class="vst-track-row vst-track-audio" style="--vst-audio-lane-count:${laneCount}">` + renderTrackHead(
      "vst-track-icon-audio",
      "♪",
      "Audio",
      laneTags.join("")
    ) + `<div class="vst-track-cell vst-audio-cell">${baseSegments}${overlaySegments}</div></div>`;
  };
  var REF_EDGE_ALIGN_FRAMES = 3;
  var renderReferencesTrackRow = (clips, layouts, fps, unit, capabilities) => {
    const lanes = layouts.map((layout) => {
      const clip = clips[layout.index];
      if (!clip) {
        return "";
      }
      const width = clipInnerWidth(layout.widthPx);
      const refsSupported = capabilities?.forClip(clip).decision("frameReferences").supported ?? true;
      const marks = (clip.refs ?? []).map((ref, refIdx) => {
        const isEnd = ref.fromEnd === true;
        const frame = Math.max(0, ref.frame ?? 0);
        const isPrimary = frame === 1 && !isEnd;
        const time = keyframeTimeSeconds(
          ref.frame,
          isEnd,
          layout.durationSeconds,
          fps
        );
        const left = keyframeLeftPercent(
          time,
          layout.durationSeconds
        );
        const source = refSourceLabel(ref.source ?? "");
        const image = ref.uploadedImage?.data;
        const thumbnailData = image ? backgroundImageDataAttr(mediaPreviewSrc(image)) : "";
        const frameLabel = `R ${isEnd ? "-" : ""}${frame}`;
        const thumbnailClass = `vst-refs-thumb${image ? " vst-refs-has-image" : ""}`;
        const alignClass = frame > REF_EDGE_ALIGN_FRAMES ? "" : isEnd ? " vst-refs-align-end" : " vst-refs-align-start";
        const kindClass = (isPrimary ? " vst-refs-primary" : "") + (isEnd ? " vst-refs-fromend" : "") + alignClass;
        const title = `${source}${isPrimary ? " · cover frame" : ""}${isEnd ? " · from end" : ""} · frame ${frame} · ${formatTimeLabel(time, unit, fps)} · click to edit, drag to move · Shift+click to delete`;
        const label = `Edit reference ${refIdx} (${source}${isEnd ? ", from end" : ""})`;
        return `<div class="vst-refs-mark${kindClass}" data-vst-ref="thumb" data-clip-idx="${layout.index}" data-ref-idx="${refIdx}" style="left:${left}%" role="button" tabindex="0" title="${escapeAttr(title)}" aria-label="${escapeAttr(label)}"><span class="${thumbnailClass}"${thumbnailData}><span class="vst-refs-ph">${escapeAttr(frameLabel)}</span></span></div>`;
      }).join("");
      return `<div class="vst-refs-lane${refsSupported ? "" : " vst-capability-disabled"}"${refsSupported ? " data-vst-ref-add" : ""} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${refsSupported ? "Click to add a reference image at this frame" : "Frame references are unsupported; existing references can be removed"}">${marks}</div>`;
    }).join("");
    return `<div class="vst-track-row vst-track-refs">` + renderTrackHead("vst-track-icon-refs", "⧉", "References", "") + `<div class="vst-track-cell">${lanes}</div></div>`;
  };

  // frontend/timelineView.ts
  var renderRulerTicks = (layouts, totalSeconds, pxPerSecond, fps, unit) => {
    const lastLayout = layouts[layouts.length - 1];
    const endPx = lastLayout.startPx + lastLayout.widthPx;
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond).map(
      (tick) => `<span class="vst-tick vst-tick-grid" style="left:${tick.x}px"><span class="vst-tick-label">${escapeAttr(formatRulerLabel(tick.seconds, unit, fps))}</span></span>`
    );
    const minorStep = chooseRulerStepSeconds(pxPerSecond) / 5;
    const minorTicks = [];
    const maxMinorTicks = 5e3;
    for (let i = 1; i <= maxMinorTicks; i++) {
      const seconds = i * minorStep;
      if (seconds > totalSeconds + 1e-6) {
        break;
      }
      if (i % 5 === 0) {
        continue;
      }
      minorTicks.push(
        `<span class="vst-tick vst-tick-minor" style="left:${seconds * pxPerSecond}px" aria-hidden="true"></span>`
      );
    }
    const seamTicks = layouts.slice(1).map(
      (layout) => `<span class="vst-tick vst-tick-seam" style="left:${layout.startPx}px" aria-hidden="true"></span>`
    );
    const endTick = `<span class="vst-tick vst-tick-end" style="left:${endPx}px"><span class="vst-tick-label">${escapeAttr(formatRulerLabel(totalSeconds, unit, fps))}</span></span>`;
    return [...minorTicks, ...gridTicks, ...seamTicks, endTick].join("");
  };
  var renderTimeline = (body, clips, options) => {
    const fps = safeFps(options?.fps);
    const unit = options?.unit === "frames" ? "frames" : "seconds";
    const pxPerSecond = clampPxPerSecond(
      options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND
    );
    body.dataset.vstPps = String(pxPerSecond);
    body.dataset.vstFps = String(fps);
    const layouts = computeRegionLayout(clips, { pxPerSecond });
    const totalSeconds = layouts.reduce(
      (sum, layout) => sum + layout.durationSeconds,
      0
    );
    const totalPx = layouts.reduce(
      (max, layout) => Math.max(max, layout.startPx + layout.widthPx),
      0
    );
    const header = renderTimelineHeader(
      clips.length,
      totalSeconds,
      fps,
      unit,
      pxPerSecond,
      options
    );
    const diagnostics = renderDiagnosticPanel(options?.diagnostics);
    if (clips.length === 0) {
      body.innerHTML = `${header}${diagnostics}<div class="vst-empty"><div class="vst-empty-icon" aria-hidden="true">🎬</div><div class="vst-empty-title">No clips yet.</div><div class="vst-empty-hint">Add one here — or in the VideoStages panel on the left — to start building your sequence.</div><button type="button" class="basic-button btn-primary vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button></div>`;
      wireTimelineToolbar(body, options);
      return;
    }
    const promptRow = renderPromptTrackRow(
      clips,
      layouts,
      pxPerSecond,
      `${options?.globalPrompt ?? ""}`,
      options?.capabilities
    );
    const videoRow = renderVideoTrackRow(
      clips,
      layouts,
      fps,
      unit,
      options?.capabilities
    );
    const referencesRow = renderReferencesTrackRow(
      clips,
      layouts,
      fps,
      unit,
      options?.capabilities
    );
    const renderedAudioRow = renderAudioTrackRow(
      clips,
      layouts,
      options?.capabilities,
      options?.audioTracks,
      pxPerSecond
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML = `${header}${diagnostics}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px"><div class="vst-ruler-row"><div class="vst-corner">Timeline</div><div class="vst-ruler">${renderRulerTicks(layouts, totalSeconds, pxPerSecond, fps, unit)}</div></div>` + promptRow + videoRow + referencesRow + renderedAudioRow + `</div></div>`;
    applyBackgroundImages(body);
    wireTimelineToolbar(body, options);
    wireTimelineZoomWheel(body, options);
  };

  // frontend/detailStrip/boundaryPanel.ts
  var buildBoundaryBody = (ctx, sel, clips) => {
    const { leftClipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    const fields = document.createElement("div");
    fields.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const value = clip?.boundaryOut ?? "cut";
    const capability = ctx.capabilities().forBoundaryIndex(clips, leftClipIdx);
    const seam = executableBoundaryForLeftClip(clips, leftClipIdx);
    const state = getState();
    const fps = Math.round(safeFps(state.fps));
    const carryTargetHasStage = capability.rightClipIdx !== null && clips[capability.rightClipIdx]?.stages.some(
      (stage) => !stage.skipped
    ) === true;
    const carryAudioActive = clip?.boundaryOutCarryAudio === true && carryTargetHasStage;
    const joinSpecs = capability.modes.map((mode) => ({
      value: mode,
      label: `${BOUNDARY_LABEL[mode]} ${BOUNDARY_GLYPH[mode]}`
    }));
    if (!capability.modes.includes(value)) {
      joinSpecs.unshift({
        value,
        label: `${BOUNDARY_LABEL[value]} ${BOUNDARY_GLYPH[value]} (unsupported persisted value)`,
        disabled: true
      });
    }
    const select2 = buildOptionSelect(joinSpecs, value, (next) => {
      ctx.commit((cs) => {
        const c = cs[leftClipIdx];
        if (c) {
          c.boundaryOut = next ?? "cut";
        }
      });
      ctx.render();
    });
    fields.appendChild(
      buildField(
        `Join · Clip ${leftClipIdx} → ${seam === null ? "end" : seam.rightIdx}`,
        select2,
        void 0,
        "How this clip joins the next one. Cut: hard concatenation. Continue: the next clip is generated from this clip's last frames so motion carries through. Crossfade: the overlap is dissolved pixel-by-pixel."
      )
    );
    if (!capability.modes.includes(value) && capability.reason) {
      fields.appendChild(
        buildCapabilityNotice({
          supported: false,
          reason: capability.reason,
          rule: null
        })
      );
    }
    const overlapPolicy = capability.overlapConstraints(value);
    if (value !== "cut" && capability.modes.includes(value)) {
      const overlapValue = clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames;
      const overlapSpecs = boundaryOverlapChoices(
        overlapPolicy
      ).map((frames) => ({
        value: `${frames}`,
        label: `${frames} frames (~${formatOverlapSeconds(frames, fps)})`
      }));
      if (overlapValue > 0 && !overlapSpecs.some((option) => option.value === `${overlapValue}`)) {
        overlapSpecs.unshift({
          value: `${overlapValue}`,
          label: `${overlapValue} frames (unsupported persisted value)`,
          disabled: true
        });
      }
      const overlapSelect = buildOptionSelect(
        overlapSpecs,
        `${overlapValue}`,
        (next) => {
          ctx.commit((cs) => {
            const c = cs[leftClipIdx];
            if (c) {
              c.boundaryOutOverlap = normalizeBoundaryOverlap(
                next,
                overlapPolicy
              );
            }
          });
          ctx.render();
        }
      );
      fields.appendChild(
        buildField(
          "Overlap",
          overlapSelect,
          void 0,
          "How many frames the two clips share at the join. For continue this is the frozen context handed to the next clip; for crossfade it is the length of the dissolve. A clip too short for the overlap falls back to a cut."
        )
      );
      fields.appendChild(
        buildCheckbox(
          "Continue outgoing audio into next clip",
          clip?.boundaryOutCarryAudio === true,
          (enabled) => {
            ctx.commit((cs) => {
              const c = cs[leftClipIdx];
              if (c) {
                c.boundaryOutCarryAudio = enabled;
              }
            });
            ctx.render();
          },
          {
            disabled: !carryTargetHasStage,
            help: "Preserve this clip's audio tail at the start of the next clip's LTX generation, then let LTX generate its continuation. The next clip needs an active stage."
          }
        )
      );
    }
    const executable = executableClipIndexes(clips);
    const plannedWindow = () => {
      if (seam === null) {
        return 0;
      }
      const plan = crossfadePlanForClips(
        executable.map((clipIdx) => clips[clipIdx]),
        fps,
        (_left, position, mode) => ctx.capabilities().forBoundaryIndex(clips, executable[position]).overlapConstraints(mode)
      );
      return plan.fallback ? 0 : plan.overlaps[seam.position] ?? 0;
    };
    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    const effective = capability.effective(value);
    if (effective !== value) {
      info.classList.add("vst-boundary-warn");
      info.textContent = `${BOUNDARY_LABEL[value]} is preserved for repair, but this join executes as ${BOUNDARY_LABEL[effective].toLowerCase()}. ${capability.reason}`;
    } else if (value === "cut") {
      info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
      const window2 = plannedWindow();
      if (window2 <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = "This continue will fall back to a cut — a clip is too short for the overlap.";
      } else {
        const requested = (clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames) + overlapPolicy.continuityExtraFrames;
        let text2 = `Continue — the next clip is generated with this clip's last ${window2} frame${window2 === 1 ? "" : "s"} (~${formatOverlapSeconds(window2, fps)}) as frozen context, and the merge collapses the duplicated frames.`;
        if (window2 < requested) {
          text2 += " The window was reduced because a clip is too short.";
        }
        if (carryAudioActive) {
          text2 += " Its audio tail becomes preserved opening context for the next clip's generated audio.";
        }
        info.textContent = text2;
      }
    } else {
      const overlapFrames = plannedWindow();
      if (overlapFrames <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
      } else {
        const requested = clip?.boundaryOutOverlap ?? capability.overlapConstraints(value).defaultFrames;
        let text2 = `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} (~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
        if (overlapFrames < requested) {
          text2 += " The window was reduced because a clip is too short.";
        }
        if (carryAudioActive) {
          text2 += " Its audio tail becomes preserved opening context for the next clip's generated audio.";
        }
        info.textContent = text2;
      }
    }
    fields.appendChild(info);
    body.appendChild(
      buildStaticSection({
        key: "boundary",
        label: "Boundary",
        className: "vst-detail-boundary-section",
        content: fields,
        flattenContent: true
      }).section
    );
    return body;
  };

  // frontend/architectures/ltx2/icLoraAutoDownload.ts
  var IC_LORA_AUTO = "[AUTO]";
  var IC_LORA_AUTO_HINT_ATTR = "data-vst-iclora-auto";
  var statuses = /* @__PURE__ */ new Map();
  var clearIcLoraAutoFailure = (presetId) => {
    if (statuses.get(`${presetId ?? ""}`.trim())?.state === "error") {
      statuses.delete(`${presetId ?? ""}`.trim());
    }
  };
  var hasAutoWeights = (preset, installedLoras) => {
    const wanted = icLoraAutoModelName(preset).toLowerCase();
    return installedLoras.some((name) => `${name}`.toLowerCase() === wanted);
  };
  var statusTextFor = (preset, status) => {
    switch (status.state) {
      case "downloading":
        return `Downloading ${preset.displayName} weights… ${Math.round(status.percent * 100)}%`;
      case "done":
        return `Downloaded to ${icLoraAutoModelName(preset)}.`;
      case "error":
        return `Download failed: ${status.message} Reselect the preset to retry.`;
    }
  };
  var setStatus = (preset, status) => {
    statuses.set(preset.id, status);
    const text2 = statusTextFor(preset, status);
    document.querySelectorAll(`[${IC_LORA_AUTO_HINT_ATTR}="${preset.id}"]`).forEach((el) => {
      el.textContent = text2;
    });
  };
  var finish = (preset, onSettled) => {
    setStatus(preset, { state: "done" });
    refreshHostParameters();
    onSettled();
  };
  var ensureIcLoraAutoWeights = (entry, installedLoras, onSettled) => {
    if (entry.lora !== IC_LORA_AUTO) {
      return;
    }
    const preset = findIcLoraPreset(entry.preset);
    if (!preset || hasAutoWeights(preset, installedLoras) || statuses.has(preset.id)) {
      return;
    }
    if (!canRequestHostWs()) {
      statuses.set(preset.id, {
        state: "error",
        message: "Model downloader is unavailable."
      });
      return;
    }
    statuses.set(preset.id, { state: "downloading", percent: 0 });
    requestHostWs(
      "VideoStagesDownloadIcLoraWS",
      { presetId: preset.id },
      (data) => {
        if (typeof data?.current_percent === "number") {
          setStatus(preset, {
            state: "downloading",
            percent: data.current_percent
          });
        } else if (data?.success) {
          finish(preset, onSettled);
        }
      },
      (error) => {
        if (`${error}` === "Model at that save path already exists.") {
          finish(preset, onSettled);
          return;
        }
        setStatus(preset, { state: "error", message: `${error}` });
        onSettled();
      }
    );
  };
  var icLoraAutoHint = (entry, installedLoras) => {
    if (entry.lora !== IC_LORA_AUTO) {
      return "";
    }
    const preset = findIcLoraPreset(entry.preset);
    if (!preset) {
      return "[AUTO] needs a preset — pick one to download its weights.";
    }
    if (hasAutoWeights(preset, installedLoras)) {
      return `Using ${icLoraAutoModelName(preset)}.`;
    }
    const status = statuses.get(preset.id);
    return status ? statusTextFor(preset, status) : "Preparing preset weights download…";
  };

  // frontend/architectures/ltx2/icLoraPanel.ts
  var buildIcLorasSection = (context, clip, clipIdx, defaults, selectedEntryIdx = null, open = selectedEntryIdx !== null) => {
    const clipCapabilities = context.capabilities().forClip(clip);
    const icLoraDecision = clipCapabilities.decision("icLora");
    const entryIdx = clip.icLoras.length === 0 ? null : Math.max(
      0,
      Math.min(selectedEntryIdx ?? 0, clip.icLoras.length - 1)
    );
    const buildSection = (editorForItem) => {
      const built = buildRepeatingEditor({
        key: "ic-loras",
        label: "IC-LoRAs",
        sectionClass: "vst-detail-iclora-section",
        open,
        items: clip.icLoras.map((_, index) => ({
          label: `IC${index}`,
          focusKey: `ic-lora-tab-${index}`,
          title: `Edit IC-LoRA ${index}`,
          active: index === entryIdx,
          className: "vst-iclora-tab",
          onSelect: () => setSelection({
            kind: "ic-lora",
            clipIdx,
            entryIdx: index
          }),
          onDelete: () => {
            context.structuralCommit(
              (clips) => {
                const target = clips[clipIdx];
                if (!target?.icLoras[index]) {
                  return null;
                }
                target.icLoras.splice(index, 1);
                removeIcLoraStrengthAt(target, index);
                return target.icLoras.length > 0 ? {
                  kind: "ic-lora",
                  clipIdx,
                  entryIdx: Math.min(
                    index,
                    target.icLoras.length - 1
                  )
                } : { kind: "clip", clipIdx, stageIdx: 0 };
              },
              { rebuildAfterSelect: true }
            );
          }
        })),
        add: {
          title: icLoraDecision.supported ? "Add an IC-LoRA" : icLoraDecision.reason,
          label: "+ Add IC-LoRA",
          className: "vst-detail-add-iclora",
          disabled: !icLoraDecision.supported,
          onClick: () => {
            context.structuralCommit((clips) => {
              const target = clips[clipIdx];
              if (!target || !context.capabilities().forClip(target).decision("icLora").supported) {
                return null;
              }
              target.icLoras.push(
                defaultIcLora({
                  lora: IC_LORA_AUTO
                })
              );
              appendIcLoraStrengthToClip(
                target,
                defaultLoraWeight(defaults, IC_LORA_AUTO)
              );
              return {
                kind: "ic-lora",
                clipIdx,
                entryIdx: target.icLoras.length - 1
              };
            });
          }
        },
        remove: {
          title: entryIdx === null ? "No IC-LoRA to delete" : `Delete IC-LoRA ${entryIdx}`,
          className: "vst-detail-delete-iclora"
        },
        editorForItem
      });
      appendHelp(
        built.heading,
        built.section,
        "IC-LoRAs",
        "In-context LoRAs use uploaded or incoming media for pose, depth, motion, style, audio, or other preset-specific conditioning. Add one per guide you want to apply."
      );
      return built.section;
    };
    if (entryIdx === null) {
      return buildSection();
    }
    const buildEditor = (editorEntryIdx) => {
      const entry = clip.icLoras[editorEntryIdx];
      if (!entry) {
        return void 0;
      }
      const entryIdx2 = editorEntryIdx;
      const col = document.createElement("div");
      col.className = "vst-detail-col vst-detail-instance-fields vst-detail-iclora vst-detail-iclora-col";
      col.setAttribute("data-vst-iclora-idx", `${entryIdx2}`);
      const entryAt = (clips, index) => clips[clipIdx]?.icLoras[index];
      const hdrDecision = clipCapabilities.decision("hdr");
      {
        const fields = col;
        const persistedHdr = isHdrFeature(entry);
        const preset = findIcLoraPreset(entry.preset);
        const driveMediaKinds = entry.driveMediaKinds;
        const audioDriveMedia = entry.driveData === "audio";
        const presetOptions = IC_LORA_PRESETS.filter(
          (preset2) => hdrDecision.supported || preset2.hdr !== true
        );
        const presetSpecs = [
          { value: IC_LORA_PRESET_CUSTOM_ID, label: "Custom" },
          ...presetOptions.map((preset2) => ({
            value: preset2.id,
            label: preset2.displayName
          }))
        ];
        preserveSelectedOption(
          presetSpecs,
          entry.preset,
          "start",
          (value) => ({
            value,
            label: `${value} (unsupported persisted value)`,
            disabled: true
          })
        );
        const presetSelect = buildOptionSelect(
          presetSpecs,
          entry.preset,
          (value) => {
            context.commit((clips) => {
              const target = entryAt(clips, entryIdx2);
              if (!target) {
                return;
              }
              target.preset = value;
              const preset2 = findIcLoraPreset(value);
              if (preset2) {
                target.lora = IC_LORA_AUTO;
                target.strength = preset2.strength;
                target.controlType = preset2.controlType;
              }
              target.hdr = preset2?.hdr === true;
              const nextContract = icLoraDriveMediaContract(preset2);
              target.driveData = nextContract.driveData;
              target.driveMediaKinds = [
                ...nextContract.acceptedKinds
              ];
              if (nextContract.driveData !== "visual") {
                target.controlType = "none";
              }
              const driveData = target.driveMedia?.data ?? "";
              if (driveData && !target.driveMediaKinds.some(
                (kind) => driveData.startsWith(`data:${kind}/`)
              )) {
                target.driveMedia = null;
              }
              const targetClip = clips[clipIdx];
              if (targetClip && target.driveSource === IC_LORA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                target,
                targetClip,
                clipIdx,
                clips,
                context.generatedEntryMode()
              )) {
                target.driveSource = IC_LORA_SOURCE_UPLOAD;
              }
            });
            clearIcLoraAutoFailure(value);
            context.render();
          }
        );
        fields.appendChild(
          buildField(
            "Preset",
            presetSelect,
            void 0,
            "A curated IC-LoRA setup — picking one fills in the LoRA, strength, and control type for a known effect (pose, depth, style, etc.). Choose Custom to set everything yourself."
          )
        );
        const loraSpecs = [
          { value: IC_LORA_AUTO, label: IC_LORA_AUTO },
          ...defaults.loraValues.map((value, optionIdx) => ({
            value,
            label: defaults.loraLabels[optionIdx] ?? value
          }))
        ];
        preserveSelectedOption(loraSpecs, entry.lora, "start", (value) => ({
          value,
          label: `${value} (unsupported persisted value)`,
          disabled: true
        }));
        const loraSelect = buildOptionSelect(
          loraSpecs,
          entry.lora,
          (value) => {
            context.commit((clips) => {
              const target = entryAt(clips, entryIdx2);
              if (target) {
                target.lora = value;
                const initialStrength = defaultLoraWeight(
                  defaults,
                  value
                );
                const targetClip = clips[clipIdx];
                for (const stage of targetClip?.stages ?? []) {
                  stage.icLoraStrengths[entryIdx2] = normalizeStageControlNetStrengthValue(
                    initialStrength
                  );
                }
              }
            });
            if (value === IC_LORA_AUTO) {
              clearIcLoraAutoFailure(entry.preset);
            }
            context.render();
          }
        );
        fields.appendChild(
          buildField(
            "LoRA",
            loraSelect,
            void 0,
            "The in-context LoRA weights that turn the drive media into conditioning. [AUTO] downloads the preset's recommended weights when they are not installed."
          )
        );
        const strength = context.buildClampedNumber({
          key: `iclora-${entryIdx2}-strength`,
          value: entry.strength,
          min: IC_LORA_STRENGTH_MIN,
          max: IC_LORA_STRENGTH_MAX,
          step: IC_LORA_STRENGTH_STEP,
          readBack: (clips) => entryAt(clips, entryIdx2)?.strength ?? null,
          mutate: (clips, value) => {
            const target = entryAt(clips, entryIdx2);
            if (target) {
              target.strength = value;
            }
          }
        });
        fields.appendChild(
          buildField(
            "Strength",
            strength,
            void 0,
            "How strongly this IC-LoRA steers generation. Higher follows the drive media more closely; too high can overpower the prompt."
          )
        );
        if (entry.driveData === "visual") {
          const attention = context.buildClampedNumber({
            key: `iclora-${entryIdx2}-attention`,
            value: entry.attentionStrength,
            min: IC_LORA_ATTENTION_MIN,
            max: IC_LORA_ATTENTION_MAX,
            step: IC_LORA_ATTENTION_STEP,
            readBack: (clips) => entryAt(clips, entryIdx2)?.attentionStrength ?? null,
            mutate: (clips, value) => {
              const target = entryAt(clips, entryIdx2);
              if (target) {
                target.attentionStrength = value;
              }
            }
          });
          fields.appendChild(
            buildField(
              "Attention",
              attention,
              void 0,
              "Scales how much the IC-LoRA influences the model's attention layers. A finer control than Strength; leave at the default unless a preset tunes it."
            )
          );
        }
        if (entry.driveData === "visual" && (!preset || (preset.allowedControlTypes?.length ?? 0) > 1)) {
          const allowedControlTypes = preset?.allowedControlTypes ?? ["none", "canny", "depth", "normal"];
          const controlSelect = buildOptionSelect(
            [
              { value: "none", label: "None (raw video)" },
              { value: "canny", label: "Canny edges" },
              { value: "depth", label: "Depth map" },
              { value: "normal", label: "Normal map" }
            ].filter(
              (option) => allowedControlTypes.includes(
                option.value
              )
            ),
            entry.controlType,
            (value) => {
              context.commit((clips) => {
                const target = entryAt(clips, entryIdx2);
                if (target) {
                  target.controlType = value;
                }
              });
            }
          );
          fields.appendChild(
            buildField(
              "Control",
              controlSelect,
              void 0,
              "Preprocesses visual drive media into a control signal before conditioning: Canny edges, a depth map, or a normal map. None feeds the raw video straight in."
            )
          );
        }
        const applySelect = buildOptionSelect(
          [
            { value: `${IC_LORA_STAGE_ALL}`, label: "All stages" },
            ...clip.stages.map((_, stageIdx) => ({
              value: `${stageIdx}`,
              label: `Stage ${stageIdx}`
            }))
          ],
          `${entry.stage}`,
          (value) => {
            context.commit((clips) => {
              const target = entryAt(clips, entryIdx2);
              if (!target) {
                return;
              }
              const stage = Number(value);
              target.stage = Number.isInteger(stage) && stage >= 0 ? stage : IC_LORA_STAGE_ALL;
              canonicalizeIcLoraFields(target);
              const targetClip = clips[clipIdx];
              if (targetClip && target.driveSource === IC_LORA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                target,
                targetClip,
                clipIdx,
                clips,
                context.generatedEntryMode()
              )) {
                target.driveSource = IC_LORA_SOURCE_UPLOAD;
              }
            });
            context.render();
          }
        );
        fields.appendChild(
          buildField(
            "Apply on",
            applySelect,
            void 0,
            "Which stage this IC-LoRA conditions — a single stage, or All stages of the clip."
          )
        );
        if (!preset) {
          const dataSelect = buildOptionSelect(
            [
              { value: "visual", label: "Visual frames" },
              { value: "audio", label: "Audio" },
              { value: "none", label: "None (model only)" }
            ],
            entry.driveData,
            (value) => {
              context.commit((clips) => {
                const target = entryAt(clips, entryIdx2);
                if (!target) {
                  return;
                }
                target.driveData = value;
                target.driveMediaKinds = [
                  ...icLoraDriveMediaContractForData(
                    target.driveData
                  ).acceptedKinds
                ];
                if (target.driveData !== "visual") {
                  target.controlType = "none";
                }
                if (target.driveData === "none") {
                  target.driveSource = IC_LORA_SOURCE_UPLOAD;
                  target.driveMedia = null;
                  return;
                }
                const data = target.driveMedia?.data ?? "";
                if (data && !target.driveMediaKinds.some(
                  (kind) => data.startsWith(`data:${kind}/`)
                )) {
                  target.driveMedia = null;
                }
                const targetClip = clips[clipIdx];
                if (targetClip && target.driveSource === IC_LORA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                  target,
                  targetClip,
                  clipIdx,
                  clips,
                  context.generatedEntryMode()
                )) {
                  target.driveSource = IC_LORA_SOURCE_UPLOAD;
                }
              });
              context.render();
            }
          );
          fields.appendChild(
            buildField(
              "Drive data",
              dataSelect,
              void 0,
              "Which stream this custom IC-LoRA extracts from its drive source. Visual frames create an IC-LoRA guide; Audio creates speaker/audio reference tokens; None applies only the model patch."
            )
          );
        }
        if (entry.driveData !== "none") {
          const currentClips = getClips();
          const incomingAvailable = canUseIncomingIcLoraDrive(
            entry,
            clip,
            clipIdx,
            currentClips,
            context.generatedEntryMode()
          );
          const sourceSelect = buildOptionSelect(
            [
              { value: IC_LORA_SOURCE_UPLOAD, label: "Upload" },
              {
                value: IC_LORA_SOURCE_INCOMING,
                label: incomingAvailable ? "Incoming media" : "Incoming media (unavailable)",
                disabled: !incomingAvailable
              }
            ],
            entry.driveSource,
            (value) => {
              context.commit((clips) => {
                const target = entryAt(clips, entryIdx2);
                if (target) {
                  target.driveSource = value;
                  if (value !== IC_LORA_SOURCE_UPLOAD) {
                    target.driveMedia = null;
                  }
                }
              });
              context.render();
            }
          );
          fields.appendChild(
            buildField(
              "Source",
              sourceSelect,
              void 0,
              "Where the selected drive data comes from: Upload your own media, or use compatible media already entering this generation point."
            )
          );
        }
        if (entry.driveData !== "none" && entry.driveSource === IC_LORA_SOURCE_UPLOAD) {
          const acceptedKinds = driveMediaKinds;
          fields.appendChild(
            buildMediaPickRow(
              "Drive Media",
              acceptedKinds.map((kind) => `${kind}/*`).join(","),
              [...acceptedKinds],
              entry.driveMedia?.fileName,
              (data, fileName) => {
                context.commit((clips) => {
                  const target = entryAt(clips, entryIdx2);
                  if (target) {
                    target.driveMedia = { data, fileName };
                  }
                });
                context.render();
              },
              () => {
                context.commit((clips) => {
                  const target = entryAt(clips, entryIdx2);
                  if (target) {
                    target.driveMedia = null;
                  }
                });
                context.render();
              }
            )
          );
          if (audioDriveMedia) {
            const hint = document.createElement("small");
            hint.className = "vst-detail-field-hint";
            hint.textContent = "Only this media's audio is used as the reference sample. For a video upload, its frames are ignored; the clip's normal text, image, or video entry path supplies visuals.";
            fields.appendChild(hint);
          }
        } else if (entry.driveSource === IC_LORA_SOURCE_INCOMING) {
          const hint = document.createElement("small");
          hint.className = "vst-detail-field-hint";
          hint.textContent = entry.stage >= 0 ? `Uses ${entry.driveData} from stage ${entry.stage}'s incoming media.` : `Uses ${entry.driveData} from each stage's incoming media.`;
          fields.appendChild(hint);
        } else if (entry.driveData !== "none") {
          const slot = document.createElement("small");
          slot.className = "vst-detail-field-hint";
          slot.textContent = `Driven by ${entry.driveSource} (legacy source)`;
          fields.appendChild(slot);
        }
        const hintText = [preset?.note ?? "", icLoraTriggerHint(preset)].filter(Boolean).join(" ");
        if (hintText || preset) {
          const hint = document.createElement("small");
          hint.className = "vst-detail-field-hint";
          hint.textContent = hintText ? `${hintText} ` : "";
          if (preset) {
            const link = document.createElement("a");
            link.href = icLoraRepoUrl(preset);
            link.target = "_blank";
            link.rel = "noopener";
            link.textContent = "repo";
            hint.appendChild(link);
          }
          fields.appendChild(hint);
        }
        if (!persistedHdr || hdrDecision.supported) {
          ensureIcLoraAutoWeights(
            entry,
            defaults.loraValues,
            context.render
          );
        }
        const autoText = icLoraAutoHint(entry, defaults.loraValues);
        if (autoText) {
          const autoHint = document.createElement("small");
          autoHint.className = "vst-detail-field-hint";
          if (preset) {
            autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
          }
          autoHint.textContent = autoText;
          fields.appendChild(autoHint);
        }
        if (persistedHdr && !hdrDecision.supported) {
          disableCapabilityControls(fields, hdrDecision);
        }
      }
      return col;
    };
    return buildSection(buildEditor);
  };

  // frontend/architectures/authoringPanels.ts
  var panels = /* @__PURE__ */ new Map([
    [LTX2_ARCHITECTURE_ID, { buildIcLorasSection }]
  ]);
  var persistedIcLoraRemovalPanel = (context, clip, clipIdx, selectedEntryIdx, open) => {
    const entryIdx = clip.icLoras.length === 0 ? null : Math.max(
      0,
      Math.min(selectedEntryIdx ?? 0, clip.icLoras.length - 1)
    );
    const buildEditor = (index) => {
      const entry = clip.icLoras[index];
      if (!entry) {
        return void 0;
      }
      const content = document.createElement("div");
      content.className = "vst-detail-col vst-detail-iclora-col";
      const note = document.createElement("p");
      note.className = "vst-detail-note";
      note.textContent = "This architecture has no IC-LoRA editor. Existing entries remain available for removal.";
      content.appendChild(note);
      const label = document.createElement("span");
      label.textContent = entry.lora || `IC-LoRA ${index}`;
      content.appendChild(label);
      return content;
    };
    return buildRepeatingEditor({
      key: "ic-loras",
      label: "Persisted IC-LoRAs",
      sectionClass: "vst-detail-iclora-section",
      open,
      items: clip.icLoras.map((_, index) => ({
        label: `IC${index}`,
        focusKey: `ic-lora-tab-${index}`,
        title: `Inspect persisted IC-LoRA ${index}`,
        active: index === entryIdx,
        onSelect: () => setSelection({
          kind: "ic-lora",
          clipIdx,
          entryIdx: index
        }),
        onDelete: () => {
          context.structuralCommit(
            (clips) => {
              const target = clips[clipIdx];
              if (!target?.icLoras[index]) return null;
              target.icLoras.splice(index, 1);
              removeIcLoraStrengthAt(target, index);
              return target.icLoras.length > 0 ? {
                kind: "ic-lora",
                clipIdx,
                entryIdx: Math.min(
                  index,
                  target.icLoras.length - 1
                )
              } : { kind: "clip", clipIdx, stageIdx: 0 };
            },
            { rebuildAfterSelect: true }
          );
        }
      })),
      add: {
        title: "This architecture has no IC-LoRA editor",
        label: "+ Add IC-LoRA",
        className: "vst-detail-add-iclora",
        disabled: true,
        onClick: () => {
        }
      },
      remove: {
        title: entryIdx === null ? "No IC-LoRA to delete" : `Delete persisted IC-LoRA ${entryIdx}`,
        className: "vst-detail-delete-iclora"
      },
      editorForItem: buildEditor
    }).section;
  };
  var buildArchitectureIcLorasSection = (context, clip, clipIdx, defaults, selectedEntryIdx = null, open = selectedEntryIdx !== null) => panels.get(clip.architecture)?.buildIcLorasSection(
    context,
    clip,
    clipIdx,
    defaults,
    selectedEntryIdx,
    open
  ) ?? persistedIcLoraRemovalPanel(context, clip, clipIdx, selectedEntryIdx, open);

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
  var clampClipRefsToDuration = (clip, getRootDefaults2, effectiveFps) => {
    const frameMax = getReferenceFrameMax(getRootDefaults2, clip, effectiveFps);
    for (const ref of clip.refs) {
      ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
    }
  };
  var applyClipDurationResize = (clip, newDuration, getRootDefaults2, effectiveFps) => {
    if (clip.duration === newDuration) {
      return false;
    }
    clip.duration = newDuration;
    clampClipRefsToDuration(clip, getRootDefaults2, effectiveFps);
    return true;
  };

  // frontend/detailStrip/clipBasics.ts
  var DURATION_STEP = 0.1;
  var buildClipColumn = (context, clip, clipIdx) => {
    const column = document.createElement("div");
    column.className = "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";
    const sourced = !!clip.sourceVideo;
    const lengthDerived2 = clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true || sourced;
    const durationInput = buildNumber(
      clip.duration,
      CLIP_DURATION_MIN,
      CLIP_DURATION_MAX,
      DURATION_STEP,
      (value) => {
        context.debouncedCommit("duration", (clips) => {
          const target = clips[clipIdx];
          if (target && !lengthDerived2) {
            applyClipDurationResize(target, value, getRootDefaults);
          }
        });
      }
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationField = buildField(
      "Duration (s)",
      durationInput,
      lengthDerived2 ? sourced ? "(derived from the source video range)" : "(derived from audio/ControlNet source)" : void 0
    );
    if (lengthDerived2) {
      durationInput.disabled = true;
      durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    return column;
  };
  var buildClipSkipAction = (context, clip, clipIdx) => ({
    label: skipGlyph(clip.skipped === true),
    title: skipTitle("clip", clip.skipped === true),
    className: "vst-detail-skip-clip",
    active: clip.skipped === true,
    onClick: () => context.toggleClipSkip(clipIdx)
  });

  // frontend/detailStrip/clipLorasPanel.ts
  var buildClipLorasSection = (context, clip, clipIdx, stageIdx, defaults) => {
    const selectedNames = new Set(clip.loras.map((entry) => entry.name));
    const nextAvailableName = defaults.loraValues.find(
      (value) => !selectedNames.has(value)
    );
    const applySupportedStageWeights = (target, loraIdx, supportedWeight) => {
      const capabilities = context.capabilities();
      for (const stage of target.stages) {
        if (!capabilities.forStage(target, stage).decision("stageLoras").supported) {
          stage.loraWeights[loraIdx] = 0;
        } else {
          stage.loraWeights[loraIdx] = supportedWeight;
        }
      }
    };
    const items = clip.loras.map((lora, loraIdx) => {
      const editor = document.createElement("div");
      const options = defaults.loraValues.filter(
        (value) => value === lora.name || !clip.loras.some(
          (entry, index) => index !== loraIdx && entry.name === value
        )
      ).map((value) => {
        const optionIdx = defaults.loraValues.indexOf(value);
        return {
          value,
          label: defaults.loraLabels[optionIdx] ?? value
        };
      });
      preserveSelectedOption(options, lora.name, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
        disabled: true
      }));
      const select2 = buildOptionSelect(options, lora.name, (value) => {
        context.commit((clips) => {
          const target = clips[clipIdx];
          if (target) {
            const initialWeight = defaultLoraWeight(defaults, value);
            replaceLoraModelAt(target, loraIdx, value, initialWeight);
            applySupportedStageWeights(target, loraIdx, initialWeight);
          }
        });
        context.render();
      });
      select2.setAttribute(
        "data-vst-focus-key",
        `clip-${clipIdx}-lora-${loraIdx}-model`
      );
      editor.appendChild(buildField("Model", select2));
      return {
        label: `L${loraIdx}`,
        groupClassName: "vst-clip-lora-entry",
        editor,
        onDelete: () => {
          context.structuralCommit(
            (clips) => {
              const target = clips[clipIdx];
              if (!target || !removeLoraAt(target, loraIdx)) {
                return null;
              }
              return { kind: "clip", clipIdx, stageIdx };
            },
            { rebuildAfterSelect: true }
          );
        },
        deleteTitle: `Delete LoRA ${loraIdx}`
      };
    });
    const built = buildRepeatingEditor({
      key: `clip-${clipIdx}-loras`,
      label: "LoRAs",
      sectionClass: "vst-detail-loras-section",
      open: items.length > 0,
      defaultActiveIndex: items.length > 0 ? 0 : null,
      items,
      add: {
        title: nextAvailableName ? "Add a LoRA to this clip" : "All available LoRAs are already on this clip",
        label: "+ Add LoRA",
        className: "vst-detail-add-lora",
        disabled: !nextAvailableName,
        onClick: () => {
          context.structuralCommit(
            (clips) => {
              const target = clips[clipIdx];
              const name = nextAvailableName;
              if (!target || !name) {
                return null;
              }
              const initialWeight = defaultLoraWeight(defaults, name);
              appendLoraToClip(target, name, initialWeight);
              applySupportedStageWeights(
                target,
                target.loras.length - 1,
                initialWeight
              );
              return { kind: "clip", clipIdx, stageIdx };
            },
            { rebuildAfterSelect: true }
          );
        }
      },
      remove: {
        title: "Delete LoRA",
        className: "vst-detail-delete-lora"
      }
    });
    appendHelp(
      built.heading,
      built.section,
      "LoRAs",
      "Choose the normal LoRA models once for this clip. Each stage sets its own weight below its reference strengths."
    );
    return built.section;
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
    preserveSelectedOption(options, currentValue, "start", (value) => {
      const isBase2Edit = parseBase2EditStageIndex(value) != null;
      return {
        value,
        label: isBase2Edit ? `Missing Base2Edit ${value}` : value,
        disabled: isBase2Edit
      };
    });
    return options;
  };
  var resolveImageSourceValue = (currentValue, options) => resolveSelectValue(currentValue, options, REF_SOURCE_REFINER);

  // frontend/detailStrip/refPanel.ts
  var buildRefSection = (ctx, clipIdx, selectedRefIdx, clips, open = selectedRefIdx !== null) => {
    const clip = clips[clipIdx];
    const decision = ctx.capabilities().forClip(clip).decision("frameReferences");
    const activeRefIdx = clip.refs.length === 0 ? null : clamp(selectedRefIdx ?? 0, 0, clip.refs.length - 1);
    const buildSection = (editorForItem) => buildRepeatingEditor({
      key: "references",
      label: "Reference Images",
      sectionClass: "vst-detail-ref-section",
      open,
      items: clip.refs.map((_, refIdx) => ({
        label: `Ref${refIdx}`,
        focusKey: `reference-tab-${refIdx}`,
        title: `Edit reference image ${refIdx}`,
        active: refIdx === activeRefIdx,
        className: "vst-ref-tab",
        onSelect: () => setSelection({ kind: "ref", clipIdx, refIdx }),
        onShiftDelete: () => ctx.deleteRefEntry(clipIdx, refIdx)
      })),
      add: {
        title: decision.supported ? "Add a reference image" : decision.reason,
        label: "+ Add Reference Image",
        className: "vst-detail-add-ref",
        disabled: !decision.supported,
        onClick: () => ctx.addRefEntry(clipIdx)
      },
      remove: {
        title: activeRefIdx === null ? "No reference image to delete" : `Delete reference image ${activeRefIdx}`,
        className: "vst-detail-delete-ref"
      },
      editorForItem
    }).section;
    if (activeRefIdx === null) {
      return buildSection();
    }
    const buildEditor = (editorRefIdx) => {
      const ref = clip.refs[editorRefIdx];
      if (!ref) {
        return void 0;
      }
      const options = buildImageSourceOptions(ref.source ?? "");
      const source = resolveImageSourceValue(ref.source ?? "", options);
      const isUpload = source === REF_SOURCE_UPLOAD;
      const fields = document.createElement("div");
      fields.className = "vst-detail-col vst-detail-instance-fields vst-detail-ref-row vst-detail-ref-editor";
      fields.setAttribute("data-vst-ref-index", `${editorRefIdx}`);
      const select2 = buildOptionSelect(options, source, (value) => {
        ctx.commit((cs) => {
          const target = cs[clipIdx]?.refs[editorRefIdx];
          if (!target) {
            return;
          }
          const resolved = resolveImageSourceValue(
            value,
            buildImageSourceOptions(value)
          );
          target.source = resolved;
          if (resolved !== REF_SOURCE_UPLOAD) {
            target.uploadedImage = null;
            target.uploadFileName = null;
          }
        });
        ctx.render();
      });
      fields.appendChild(
        buildField(
          "Image Source",
          select2,
          void 0,
          "Where this reference image comes from — an upload, or another clip's rendered frame. The image guides how the clip looks at its attach frame."
        )
      );
      if (isUpload) {
        const preview = document.createElement("div");
        preview.className = "vst-refs-thumb-preview";
        const data = ref.uploadedImage?.data;
        if (data) {
          preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
          preview.classList.add("vst-refs-thumb-preview-set");
        }
        fields.appendChild(preview);
      }
      const frameMax = getReferenceFrameMax(
        getRootDefaults,
        clip,
        getState().fps
      );
      const frameInput = buildNumber(
        ref.frame,
        REF_FRAME_MIN,
        frameMax,
        1,
        (value) => {
          ctx.debouncedCommit(`ref-${editorRefIdx}-frame`, (cs) => {
            const target = cs[clipIdx]?.refs[editorRefIdx];
            if (target) {
              target.frame = clamp(
                Math.round(value),
                REF_FRAME_MIN,
                frameMax
              );
            }
          });
        }
      );
      frameInput.setAttribute(
        "data-vst-focus-key",
        `ref-${editorRefIdx}-frame`
      );
      fields.appendChild(
        buildField(
          "Attach at Frame",
          frameInput,
          void 0,
          "The frame within the clip where this reference is anchored. Frame 1 is the first frame; the image influences the clip most strongly around here."
        )
      );
      fields.appendChild(
        buildCheckbox(
          "Count from clip end",
          ref.fromEnd === true,
          (value) => {
            ctx.commit((cs) => {
              const target = cs[clipIdx]?.refs[editorRefIdx];
              if (target) {
                target.fromEnd = value;
              }
            });
          },
          {
            help: "Count the attach frame backwards from the last frame instead of forward from the first — so it stays anchored to the end even if the clip length changes."
          }
        )
      );
      if (isUpload) {
        fields.appendChild(
          buildMediaPickRow(
            "Image Upload",
            "image/*",
            ["image"],
            ref.uploadedImage?.fileName,
            (data, fileName) => {
              ctx.commit((cs) => {
                const target = cs[clipIdx]?.refs[editorRefIdx];
                if (target) {
                  target.uploadedImage = { data, fileName };
                  target.uploadFileName = fileName;
                }
              });
              ctx.render();
            },
            () => {
              ctx.commit((cs) => {
                const target = cs[clipIdx]?.refs[editorRefIdx];
                if (target) {
                  target.uploadedImage = null;
                  target.uploadFileName = null;
                }
              });
              ctx.render();
            }
          )
        );
      }
      if (!decision.supported) {
        applyPersistedCapabilityRepair(fields, decision);
      }
      return fields;
    };
    return buildSection(buildEditor);
  };

  // frontend/detailStrip/retakePanel.ts
  var buildRetakeSection = (context, clip, clipIdx, open = false) => {
    const retake = clip.retake;
    const decision = context.capabilities().forClip(clip).decision("retake");
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-retake-col";
    const buildSection = () => {
      const built = buildAccordionSection({
        key: "retake",
        label: "Retake",
        className: "vst-detail-retake-section",
        open,
        content: col,
        flattenContent: true
      });
      if (retake) {
        const actions = document.createElement("span");
        actions.className = "vst-detail-repeating-group-actions";
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "interrupt-button vst-btn-tiny vst-detail-delete vst-detail-delete-retake";
        remove.textContent = "×";
        remove.title = "Delete the retake window";
        remove.setAttribute("aria-label", remove.title);
        remove.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();
          context.removeRetake(clipIdx);
        });
        actions.appendChild(remove);
        built.heading.parentElement?.appendChild(actions);
      }
      appendHelp(
        built.heading,
        built.section,
        "Retake",
        "Regenerate just a time window of a base video, leaving the rest untouched — handy for fixing one bad stretch without redoing the whole clip."
      );
      return built.section;
    };
    if (!retake) {
      const hint = document.createElement("small");
      hint.className = "vst-detail-field-hint";
      hint.textContent = "Regenerates a sub-range when refining a base video.";
      col.appendChild(hint);
      const add = document.createElement("button");
      add.type = "button";
      add.className = "basic-button small-button vst-detail-repeating-add vst-detail-add-retake";
      add.textContent = "+ Add Retake";
      add.title = decision.supported ? "Add a retake window" : decision.reason;
      add.setAttribute("aria-label", add.title);
      add.disabled = !decision.supported;
      add.addEventListener("click", (event) => {
        event.preventDefault();
        context.createRetake(clipIdx);
      });
      col.appendChild(add);
      return buildSection();
    }
    const clipDuration = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);
    const clampRetake = (start, length) => clampStartLength(start, length, clipDuration, RETAKE_MIN_DURATION);
    const startInput = context.buildClampedNumber({
      key: "retake-start",
      value: retake.startSeconds,
      min: 0,
      max: Math.max(0, clipDuration - RETAKE_MIN_DURATION),
      step: RETAKE_DURATION_STEP,
      readBack: (clips) => clips[clipIdx]?.retake?.startSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx]?.retake;
        if (target) {
          const next = clampRetake(value, target.lengthSeconds);
          target.startSeconds = next.start;
          target.lengthSeconds = next.length;
        }
      }
    });
    col.appendChild(
      buildField(
        "Start (s)",
        startInput,
        void 0,
        "Where the retake window begins inside the clip. Only this sub-range is regenerated."
      )
    );
    const lengthInput = context.buildClampedNumber({
      key: "retake-length",
      value: retake.lengthSeconds,
      min: RETAKE_MIN_DURATION,
      max: clipDuration,
      step: RETAKE_DURATION_STEP,
      readBack: (clips) => clips[clipIdx]?.retake?.lengthSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx]?.retake;
        if (target) {
          const next = clampRetake(target.startSeconds, value);
          target.startSeconds = next.start;
          target.lengthSeconds = next.length;
        }
      }
    });
    col.appendChild(
      buildField(
        "Length (s)",
        lengthInput,
        void 0,
        "How long the retake window is, starting at Start. Frames outside the window are kept as-is."
      )
    );
    col.appendChild(
      buildSlider(
        "Strength",
        retake.strength,
        RETAKE_STRENGTH_MIN,
        RETAKE_STRENGTH_MAX,
        RETAKE_STRENGTH_STEP,
        (value) => {
          context.debouncedCommit("retake-strength", (clips) => {
            const target = clips[clipIdx]?.retake;
            if (target) {
              target.strength = clamp(
                value,
                RETAKE_STRENGTH_MIN,
                RETAKE_STRENGTH_MAX
              );
            }
          });
        },
        {
          help: "How much of the window is regenerated. Higher changes the footage more; lower keeps it closer to the original."
        }
      )
    );
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "Applies when refining a base video; audio inside the window regenerates with the frames.";
    col.appendChild(note);
    return buildSection();
  };

  // frontend/sourceVideoProbe.ts
  var FPS_SAMPLE_FRAMES = 12;
  var MIN_FRAME_DELTA_SECONDS = 5e-4;
  var MAX_PLAUSIBLE_FPS = 240;
  var estimateFpsFromMediaTimes = (mediaTimes) => {
    const deltas = [];
    for (let i = 1; i < mediaTimes.length; i++) {
      const delta = mediaTimes[i] - mediaTimes[i - 1];
      if (delta > MIN_FRAME_DELTA_SECONDS) {
        deltas.push(delta);
      }
    }
    if (deltas.length < 4) {
      return null;
    }
    deltas.sort((a, b) => a - b);
    const median = deltas[Math.floor(deltas.length / 2)];
    const fps = Math.round(1 / median);
    return fps >= 1 && fps <= MAX_PLAUSIBLE_FPS ? fps : null;
  };
  var probeSourceVideo = (src, timeoutMs = 8e3) => new Promise((resolve) => {
    const video = getVideoStagesHostBridge().createSourceVideoElement();
    video.muted = true;
    video.preload = "auto";
    let settled = false;
    const finish2 = (result) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      video.pause();
      video.removeAttribute("src");
      video.load();
      resolve(result);
    };
    const timer = setTimeout(() => finish2(null), timeoutMs);
    video.addEventListener("error", () => finish2(null));
    video.addEventListener("loadedmetadata", () => {
      const durationSeconds = Number.isFinite(video.duration) ? video.duration : 0;
      if (!(durationSeconds > 0)) {
        finish2(null);
        return;
      }
      const requestFrame = video.requestVideoFrameCallback?.bind(video);
      if (!requestFrame) {
        finish2({ durationSeconds, fps: null });
        return;
      }
      const mediaTimes = [];
      const step = (_now, metadata) => {
        mediaTimes.push(metadata.mediaTime);
        if (mediaTimes.length >= FPS_SAMPLE_FRAMES || metadata.mediaTime >= durationSeconds) {
          finish2({
            durationSeconds,
            fps: estimateFpsFromMediaTimes(mediaTimes)
          });
          return;
        }
        requestFrame(step);
      };
      requestFrame(step);
      video.play()?.catch(() => finish2({ durationSeconds, fps: null }));
    });
    video.src = src;
  });

  // frontend/sourceVideoProbeGuard.ts
  var nextOperationId = 0;
  var currentOperations = /* @__PURE__ */ new Map();
  var findClipByStableId = (clips, clipId) => clips.find((clip) => clip.id === clipId);
  var beginSourceVideoProbeOperation = (clipId, revisionAtStart) => {
    const operationId = ++nextOperationId;
    currentOperations.set(clipId, operationId);
    const release = () => {
      if (currentOperations.get(clipId) === operationId) {
        currentOperations.delete(clipId);
      }
    };
    return {
      clipId,
      claim: (currentRevision2) => {
        const current = currentRevision2 === revisionAtStart && currentOperations.get(clipId) === operationId;
        release();
        return current;
      },
      cancel: release
    };
  };

  // frontend/detailStrip/sourceVideoPanel.ts
  var DURATION_STEP2 = 0.1;
  var applyPickedSourceVideo = (context, clipId, data, fileName) => {
    const store2 = getTimelineStore();
    const { revision } = store2.getSnapshot();
    const operation = beginSourceVideoProbeOperation(clipId, revision);
    void probeSourceVideo(data).then((probe) => {
      if (!operation.claim(store2.revision())) {
        return;
      }
      const state = store2.getState();
      const clips = state.clips;
      const target = findClipByStableId(clips, operation.clipId);
      if (!target || !context.capabilities().forClip(target).decision("sourceVideo").supported) {
        return;
      }
      const durationSeconds = roundToTenth(probe?.durationSeconds ?? 0);
      const lengthSeconds = durationSeconds > 0 ? durationSeconds : target.duration;
      target.sourceVideo = {
        data,
        fileName,
        fps: probe?.fps ?? 0,
        durationSeconds,
        startSeconds: 0,
        lengthSeconds
      };
      reconcileSourcedClipIdentity(target, context.capabilities().catalog);
      applyClipDurationResize(
        target,
        Math.max(CLIP_DURATION_MIN, lengthSeconds),
        getRootDefaults,
        state.fps
      );
      saveClips(clips, { origin: "detail-strip" });
      context.render();
    }, operation.cancel);
  };
  var buildSourceVideoSection = (context, clip, clipIdx, open = false) => {
    const { wrap, col } = buildStackSection(
      "source-video",
      "Source Video",
      "vst-detail-source-col",
      open
    );
    const sectionLabel = wrap.querySelector(
      ":scope > .input-group-header .header-label"
    );
    if (sectionLabel) {
      appendHelp(
        sectionLabel,
        wrap,
        "Source Video",
        "Start this clip from an existing video instead of generating it. Stages then refine/upscale the footage and a retake can regenerate part of it."
      );
    }
    const source = clip.sourceVideo;
    const removeSource = () => {
      context.structuralCommit((clips) => {
        const target = clips[clipIdx];
        if (!target?.sourceVideo) {
          return null;
        }
        target.sourceVideo = null;
        reconcileSourcedClipIdentity(
          target,
          context.capabilities().catalog
        );
        reconcileArchitectureIncomingIcLoraDrives(
          clips,
          context.generatedEntryMode()
        );
        return "render";
      });
    };
    const hint = document.createElement("small");
    hint.className = "vst-detail-field-hint";
    hint.textContent = "Use an existing video file as this clip instead of generating it.";
    col.appendChild(hint);
    col.appendChild(
      buildMediaPickRow(
        "Video file",
        "video/*",
        ["video"],
        source?.fileName ?? null,
        (data, fileName) => {
          if (clip.id) {
            applyPickedSourceVideo(context, clip.id, data, fileName);
          }
        },
        removeSource
      )
    );
    if (!source) {
      return wrap;
    }
    const info = document.createElement("small");
    info.className = "vst-detail-field-hint";
    info.textContent = `Detected: ${source.fps > 0 ? `${source.fps} fps` : "unknown fps"} · ${source.durationSeconds > 0 ? `${source.durationSeconds.toFixed(1)} s` : "unknown length"}`;
    col.appendChild(info);
    const fileLimit = source.durationSeconds > 0 ? source.durationSeconds : source.startSeconds + source.lengthSeconds;
    const syncClipDuration = (target) => {
      applyClipDurationResize(
        target,
        Math.max(
          CLIP_DURATION_MIN,
          target.sourceVideo?.lengthSeconds ?? target.duration
        ),
        getRootDefaults,
        getTimelineStore().getState().fps
      );
    };
    const clampSource = (start, length) => clampStartLength(start, length, fileLimit, CLIP_DURATION_MIN);
    const startInput = context.buildClampedNumber({
      key: "source-start",
      value: source.startSeconds,
      min: 0,
      max: Math.max(0, fileLimit - CLIP_DURATION_MIN),
      step: DURATION_STEP2,
      readBack: (clips) => clips[clipIdx]?.sourceVideo?.startSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        const targetSource = target?.sourceVideo;
        if (target && targetSource) {
          const next = clampSource(value, targetSource.lengthSeconds);
          targetSource.startSeconds = next.start;
          targetSource.lengthSeconds = next.length;
          syncClipDuration(target);
        }
      }
    });
    col.appendChild(
      buildField(
        "Start (s)",
        startInput,
        void 0,
        "Where inside the source file this clip's footage begins. Trims the front of the file."
      )
    );
    const lengthInput = context.buildClampedNumber({
      key: "source-length",
      value: source.lengthSeconds,
      min: CLIP_DURATION_MIN,
      max: fileLimit,
      step: DURATION_STEP2,
      readBack: (clips) => clips[clipIdx]?.sourceVideo?.lengthSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        const targetSource = target?.sourceVideo;
        if (target && targetSource) {
          const next = clampSource(targetSource.startSeconds, value);
          targetSource.startSeconds = next.start;
          targetSource.lengthSeconds = next.length;
          syncClipDuration(target);
        }
      }
    });
    col.appendChild(
      buildField(
        "Length (s)",
        lengthInput,
        void 0,
        "How many seconds of the source file this clip uses, starting at Start. This also becomes the clip's duration."
      )
    );
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "This range (conformed to the timeline fps and size) is the clip's starting point: the first stage refines it using its Control value, later stages refine or upscale it, and a retake regenerates part of it.";
    col.appendChild(note);
    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "interrupt-button vst-btn-tiny vst-detail-delete vst-detail-rail-btn";
    removeButton.textContent = "Remove source video";
    removeButton.addEventListener("click", (event) => {
      event.preventDefault();
      removeSource();
    });
    col.appendChild(removeButton);
    return wrap;
  };

  // frontend/architectures/conversion/presentation.ts
  var architectureConversionMessage = (fromLabel, toLabel, removals) => {
    const impact = removals.length === 0 ? "Architecture-owned stage settings will be retargeted." : `This removes: ${removals.join(", ")}.`;
    return `Convert this clip from ${fromLabel} to ${toLabel}?

${impact}

The conversion is one undoable change.`;
  };
  var confirmArchitectureConversion = (message, apply, confirm = (value) => window.confirm(value)) => confirm(message) && apply();

  // frontend/detailStrip/stagePanel/modelSection.ts
  var appendStageModelSection = ({
    context,
    clip,
    clipIdx,
    stageIdx,
    stage,
    defaults,
    fields
  }) => {
    const modelView = stageIdx === 0 ? {
      values: defaults.modelCatalog.entries.flatMap((entry) => {
        const architecture = architectureDescriptor(
          defaults.modelCatalog,
          entry.architectureId
        );
        return architecture && architectureSupportsClipStart(
          architecture.capabilities,
          clip,
          context.generatedEntryMode()
        ) ? [entry.value] : [];
      }),
      labels: defaults.modelCatalog.entries.flatMap(
        (entry) => {
          const architecture = architectureDescriptor(
            defaults.modelCatalog,
            entry.architectureId
          );
          return architecture && architectureSupportsClipStart(
            architecture.capabilities,
            clip,
            context.generatedEntryMode()
          ) ? [entry.label] : [];
        }
      )
    } : architectureCatalogView(defaults.modelCatalog, clip.architecture);
    const modelOptions = modelView.values.map((value, index) => ({
      value,
      label: modelView.labels[index] ?? value
    }));
    if (stage.model && !modelOptions.some((option) => option.value === stage.model)) {
      modelOptions.unshift({
        value: stage.model,
        label: `${stage.model} (unsupported persisted value)`,
        disabled: true
      });
    }
    const modelSelect = buildOptionSelect(
      modelOptions,
      `${stage.model ?? ""}`,
      (value) => {
        const plan = buildArchitectureRetargetPlan(
          defaults.modelCatalog,
          value
        );
        if (!plan) {
          modelSelect.value = stage.model;
          return;
        }
        if (stageIdx === 0 && plan.architectureId !== clip.architecture) {
          const conversion = planArchitectureConversion(
            clip,
            plan,
            defaults.modelCatalog
          );
          if (!conversion) {
            modelSelect.value = stage.model;
            return;
          }
          const fromLabel = architectureDescriptor(
            defaults.modelCatalog,
            clip.architecture
          )?.label ?? clip.architecture;
          const toLabel = architectureDescriptor(
            defaults.modelCatalog,
            plan.architectureId
          )?.label ?? plan.architectureId;
          const confirmedAndApplied = confirmArchitectureConversion(
            architectureConversionMessage(
              fromLabel,
              toLabel,
              conversion.removals
            ),
            () => {
              const snapshot2 = getTimelineStore().getSnapshot();
              const clipId2 = snapshot2.state.clips[clipIdx]?.id;
              if (!clipId2) return false;
              const result2 = dispatchDocumentCommand(
                {
                  type: "clip.convert-architecture",
                  clipId: clipId2,
                  target: plan
                },
                {
                  expectedRevision: snapshot2.revision,
                  origin: "detail-strip"
                }
              );
              if (result2.applied) context.render();
              return result2.applied;
            }
          );
          if (!confirmedAndApplied) modelSelect.value = stage.model;
          return;
        }
        const snapshot = getTimelineStore().getSnapshot();
        const clipId = snapshot.state.clips[clipIdx]?.id;
        const stageId = snapshot.state.clips[clipIdx]?.stages[stageIdx]?.id;
        if (!clipId || !stageId) {
          modelSelect.value = stage.model;
          return;
        }
        const result = dispatchDocumentCommand(
          {
            type: "stage.retarget-model",
            clipId,
            stageId,
            target: plan
          },
          {
            expectedRevision: snapshot.revision,
            origin: "detail-strip"
          }
        );
        if (!result.applied) {
          modelSelect.value = stage.model;
          return;
        }
        context.render();
      }
    );
    const modelField = buildField("Model", modelSelect);
    modelField.classList.add("vst-detail-span-2");
    fields.appendChild(modelField);
  };

  // frontend/detailStrip/stagePanel/referenceGuideSection.ts
  var appendSectionHeader = (fields, label) => {
    const header = document.createElement("div");
    header.className = "vst-detail-sec vst-detail-span-full vst-detail-crumb vst-detail-subsection-crumb";
    header.setAttribute("role", "heading");
    header.setAttribute("aria-level", "4");
    header.textContent = label;
    fields.appendChild(header);
  };
  var shortModelName2 = (modelName) => {
    const parts = modelName.split(/[\\/]/);
    return parts[parts.length - 1] || modelName;
  };
  var appendStageReferenceGuideSection = ({
    context,
    clip,
    clipIdx,
    stage,
    stageIdx,
    fields,
    stageCapabilities,
    debouncedCommit
  }) => {
    if (clip.refs.length > 0) {
      const refDecision = context.capabilities().forClip(clip).decision("frameReferences");
      appendSectionHeader(fields, "Reference Strengths");
      const setRefHover = (refIdx, on) => {
        context.getBoundBody()?.querySelector(
          `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`
        )?.classList.toggle("vst-ref-hover", on);
      };
      clip.refs.forEach((ref, refIdx) => {
        const current = refIdx < stage.refStrengths.length ? stage.refStrengths[refIdx] : STAGE_REF_STRENGTH_MAX;
        const refSlider = buildSlider(
          `Reference R${refIdx}`,
          current,
          STAGE_REF_STRENGTH_MIN,
          STAGE_REF_STRENGTH_MAX,
          STAGE_REF_STRENGTH_STEP,
          (value) => {
            debouncedCommit(`refstrength-${refIdx}`, (target) => {
              if (refIdx < target.refStrengths.length) {
                target.refStrengths[refIdx] = value;
              }
            });
          },
          {
            title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`
          }
        );
        refSlider.classList.add("vst-stage-ref-slider");
        tagFocus(refSlider, `ref-${refIdx}`);
        refSlider.addEventListener(
          "mouseenter",
          () => setRefHover(refIdx, true)
        );
        refSlider.addEventListener(
          "mouseleave",
          () => setRefHover(refIdx, false)
        );
        fields.appendChild(refSlider);
        if (!refDecision.supported) {
          disableCapabilityControls(refSlider, refDecision);
        }
      });
      if (!refDecision.supported) {
        fields.appendChild(buildCapabilityNotice(refDecision));
      }
    }
    if (clip.loras.length > 0) {
      appendSectionHeader(fields, "LoRA Weights");
      const loraState = stageCapabilities.authoringState(
        "stageLoras",
        clip.loras.length > 0
      );
      const group = document.createDocumentFragment();
      clip.loras.forEach((entry, entryIdx) => {
        const weight = tagFocus(
          buildUnboundedNumber(
            stage.loraWeights[entryIdx] ?? 1,
            LORA_WEIGHT_STEP,
            (value) => {
              debouncedCommit(`lora-weight-${entryIdx}`, (target) => {
                target.loraWeights[entryIdx] = value;
              });
            }
          ),
          `lora-weight-${entryIdx}`
        );
        weight.classList.add("lora-weight-input", "vst-stage-lora-weight");
        const row = buildField(shortModelName2(entry.name), weight);
        row.classList.add("vst-stage-lora-weight-row");
        row.title = entry.name;
        group.appendChild(row);
      });
      if (!loraState.enabled) {
        disableCapabilityControls(group, loraState);
      }
      fields.appendChild(group);
    }
    const applicableIcLoras = clip.icLoras.map((entry, entryIdx) => ({ entry, entryIdx })).filter(({ entry }) => entry.stage < 0 || entry.stage === stageIdx);
    if (applicableIcLoras.length === 0) return;
    appendSectionHeader(fields, "IC-LoRA Guide Strengths");
    const icDecision = context.capabilities().forClip(clip).decision("icLora");
    const icGroup = document.createDocumentFragment();
    applicableIcLoras.forEach(({ entry, entryIdx }) => {
      const guideStrength = tagFocus(
        buildNumber(
          stage.icLoraStrengths[entryIdx] ?? 1,
          STAGE_CONTROLNET_STRENGTH_MIN,
          STAGE_CONTROLNET_STRENGTH_MAX,
          STAGE_CONTROLNET_STRENGTH_STEP,
          (value) => {
            debouncedCommit(
              `ic-lora-strength-${entryIdx}`,
              (target) => {
                target.icLoraStrengths[entryIdx] = value;
              }
            );
          }
        ),
        `ic-lora-strength-${entryIdx}`
      );
      guideStrength.classList.add(
        "lora-weight-input",
        "vst-stage-iclora-strength"
      );
      const row = buildField(shortModelName2(entry.lora), guideStrength);
      row.classList.add("vst-stage-iclora-strength-row");
      row.title = entry.lora;
      icGroup.appendChild(row);
    });
    if (!icDecision.supported) {
      disableCapabilityControls(icGroup, icDecision);
    }
    fields.appendChild(icGroup);
  };

  // frontend/detailStrip/stagePanel/samplingSection.ts
  var appendStageDenoisingSection = (bindings, isRefine) => {
    const { stage, defaults, fields, slider } = bindings;
    fields.appendChild(
      slider(
        "Steps",
        "steps",
        stage.steps,
        defaults.stepsMin,
        defaults.stepsMax,
        defaults.stepsStep,
        (target, value) => {
          target.steps = Math.round(value);
        }
      )
    );
    fields.appendChild(
      slider(
        "CFG Scale",
        "cfg",
        stage.cfgScale,
        defaults.cfgScaleMin,
        defaults.cfgScaleMax,
        defaults.cfgScaleStep,
        (target, value) => {
          target.cfgScale = value;
        }
      )
    );
    if (isRefine) {
      fields.appendChild(
        slider(
          "Control",
          "control",
          stage.control,
          defaults.controlMin,
          defaults.controlMax,
          defaults.controlStep,
          (target, value) => {
            target.control = value;
          },
          {
            title: "Regen strength — higher = more of the stage is re-generated",
            help: "Regeneration strength for this refine stage. Higher re-generates more of the incoming frames (starting step = floor(Steps × (1 − Control))); at 0 the frames pass through untouched."
          }
        )
      );
    }
  };
  var appendStageSamplerSchedulerSection = ({
    stage,
    defaults,
    fields,
    stageCapabilities,
    commit
  }) => {
    const persistedSelect = (values, labels, selected, assign) => {
      const options = values.map((value, index) => ({
        value,
        label: labels[index] ?? value
      }));
      preserveSelectedOption(options, selected, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
        disabled: true
      }));
      return buildOptionSelect(options, selected, assign);
    };
    const samplerField = buildField(
      "Sampler",
      persistedSelect(
        defaults.samplerValues,
        defaults.samplerLabels,
        `${stage.sampler ?? ""}`,
        (value) => commit((target) => {
          target.sampler = value;
        })
      )
    );
    const samplerState = stageCapabilities.authoringState("sampler", true);
    if (!samplerState.enabled) {
      disableCapabilityControls(samplerField, samplerState);
    }
    fields.appendChild(samplerField);
    const schedulerField = buildField(
      "Scheduler",
      persistedSelect(
        defaults.schedulerValues,
        defaults.schedulerLabels,
        `${stage.scheduler ?? ""}`,
        (value) => commit((target) => {
          target.scheduler = value;
        })
      )
    );
    const schedulerState = stageCapabilities.authoringState("scheduler", true);
    if (!schedulerState.enabled) {
      disableCapabilityControls(schedulerField, schedulerState);
    }
    fields.appendChild(schedulerField);
  };

  // frontend/detailStrip/stagePanel/upscaleSection.ts
  var UPSCALE_EPSILON = 1e-6;
  var appendStageUpscaleSection = (bindings, isRefine) => {
    if (!isRefine) return;
    const { stage, defaults, fields, stageCapabilities, slider, commit } = bindings;
    const upscaleState = stageCapabilities.authoringState(
      "upscale",
      stage.upscale !== 1
    );
    if (!upscaleState.visible) return;
    const supportedMethods = defaults.upscaleMethodValues.flatMap(
      (value, index) => stageCapabilities.upscaleModes.includes(upscaleModeForMethod(value)) ? [
        {
          value,
          label: defaults.upscaleMethodLabels[index] ?? value
        }
      ] : []
    );
    if (stage.upscaleMethod && !supportedMethods.some((option) => option.value === stage.upscaleMethod)) {
      supportedMethods.unshift({
        value: stage.upscaleMethod,
        label: `${stage.upscaleMethod} (unsupported persisted value)`,
        disabled: true
      });
    }
    const methodSelect = buildOptionSelect(
      supportedMethods,
      `${stage.upscaleMethod ?? ""}`,
      (value) => {
        commit((target) => {
          target.upscaleMethod = value;
        });
      }
    );
    const methodField = buildField(
      "Upscale Method",
      methodSelect,
      void 0,
      "How frames are enlarged before this stage refines them. Only applies when Upscale is above 1×."
    );
    methodField.classList.add("vst-detail-span-2");
    const syncMethod = (upscale) => {
      const disabled = Math.abs(upscale - 1) < UPSCALE_EPSILON;
      methodSelect.disabled = disabled;
      methodField.classList.toggle("vst-field-disabled", disabled);
      methodField.title = disabled ? "Set Upscale above 1× to choose a method" : "";
    };
    const upscaleSlider = slider(
      "Upscale",
      "upscale",
      stage.upscale,
      defaults.upscaleMin,
      defaults.upscaleMax,
      defaults.upscaleStep,
      (target, value) => {
        target.upscale = value;
      },
      {
        onValue: syncMethod,
        help: "Resolution multiplier applied to the incoming frames before this stage refines them. 1× keeps the size; above 1× enlarges using the Upscale Method."
      }
    );
    fields.append(upscaleSlider, methodField);
    syncMethod(stage.upscale);
    if (!upscaleState.enabled) {
      applyPersistedCapabilityRepair(upscaleSlider, upscaleState, {
        // A persisted unsupported upscale must be repairable from here:
        // 1× is the removal of the value, so it is this section's remove.
        repair: Math.abs(stage.upscale - 1) < UPSCALE_EPSILON ? void 0 : {
          label: "Reset upscale to 1×",
          className: "vst-reset-unsupported-upscale",
          onRepair: () => {
            commit((target) => {
              target.upscale = 1;
            });
            bindings.context.render();
          }
        }
      });
      applyPersistedCapabilityRepair(methodField, upscaleState);
    }
  };

  // frontend/detailStrip/stagePanel.ts
  var buildStageParamsColumn = (context, clip, clipIdx, stageIdx, stage, defaults) => {
    const column = document.createElement("div");
    column.className = "vst-detail-fields vst-detail-params";
    const sourcedStage0 = stageIdx === 0 && !!clip.sourceVideo && stage.skipped !== true;
    const isRefine = stageIdx >= 1 || sourcedStage0;
    const stageCapabilities = context.capabilities().forStage(clip, stage);
    const commit = (mutate) => {
      context.commit((clips) => {
        const target = clips[clipIdx]?.stages[stageIdx];
        if (target) mutate(target);
      });
    };
    const debouncedCommit = (key, mutate) => {
      context.debouncedCommit(key, (clips) => {
        const target = clips[clipIdx]?.stages[stageIdx];
        if (target) mutate(target);
      });
    };
    const slider = (label, focusKey, value, min, max, step, assign, options) => tagFocus(
      buildSlider(
        label,
        value,
        min,
        max,
        step,
        (nextValue) => {
          options?.onValue?.(nextValue);
          debouncedCommit(
            focusKey,
            (target) => assign(target, nextValue)
          );
        },
        options?.title || options?.help ? { title: options.title, help: options.help } : void 0
      ),
      focusKey
    );
    const fields = column;
    fields.classList.toggle("vst-stage-fields-muted", stage.skipped === true);
    const bindings = {
      context,
      clip,
      clipIdx,
      stageIdx,
      stage,
      defaults,
      fields,
      stageCapabilities,
      commit,
      debouncedCommit,
      slider
    };
    appendStageModelSection(bindings);
    appendStageDenoisingSection(bindings, isRefine);
    appendStageUpscaleSection(bindings, isRefine);
    appendStageSamplerSchedulerSection(bindings);
    appendStageReferenceGuideSection(bindings);
    if (sourcedStage0) {
      const note = document.createElement("p");
      note.className = "vst-detail-note vst-stage-passthrough-note";
      note.textContent = "This stage starts from the source footage — Control sets how much is re-generated (0 passes it through).";
      column.insertBefore(note, column.firstChild);
    }
    return column;
  };

  // frontend/detailStrip/stageRail.ts
  var buildStageRail = (context, clip, clipIdx, stageIdx, editorForStage, open = true) => {
    const canAdd = clip.stages.length === 0 || context.capabilities().forClip(clip).decision("multiStage").supported;
    const addTitle = canAdd ? clip.stages.length === 0 ? "Add the first stage and choose its architecture" : "Add a refine stage" : context.capabilities().forClip(clip).decision("multiStage").reason;
    const cannotDelete = clip.stages.length === 0 || clip.stages.length === 1 && clip.sourceVideo === null;
    return buildRepeatingEditor({
      key: "stages",
      label: "Stages",
      sectionClass: "vst-detail-stage-groups",
      open,
      items: clip.stages.map((stage, index) => ({
        label: `Stage ${stageChipLabel(index)}`,
        focusKey: `stage-group-${index}`,
        title: stageChipTitle(stage, index),
        active: index === stageIdx,
        className: `vst-stage-tab${stage.skipped ? " vst-stage-tab-skipped" : ""}`,
        onSelect: () => context.selectStage(clipIdx, index),
        onDelete: () => context.deleteStage(clipIdx, index),
        deleteDisabled: cannotDelete,
        deleteTitle: cannotDelete ? clip.stages.length === 0 ? "This source-only clip has no generation stage" : "Add a source video before removing the only generation stage" : `Delete stage ${stageChipLabel(index)}`,
        headerAction: {
          label: skipGlyph(stage.skipped === true),
          title: skipTitle(
            `stage ${stageChipLabel(index)}`,
            stage.skipped === true
          ),
          className: "vst-detail-skip-stage",
          active: stage.skipped,
          onClick: () => context.toggleStageSkip(clipIdx, index)
        }
      })),
      editorForItem: editorForStage,
      add: {
        title: addTitle,
        label: "+ Add Video Stage",
        className: "vst-detail-add-stage",
        disabled: !canAdd,
        onClick: () => context.addStage(clipIdx)
      },
      remove: {
        title: "Delete stage",
        className: "vst-detail-delete-stage"
      }
    }).section;
  };

  // frontend/detailStrip/clipPanel.ts
  var buildClipBody = (context, selection, clips) => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    const { clipIdx } = selection;
    const stageIdx = selection.kind === "clip" ? selection.stageIdx : 0;
    const clip = clips[clipIdx];
    body.classList.toggle("vst-detail-clip-skipped", clip.skipped === true);
    const defaults = getRootDefaults();
    const capabilityView = context.capabilities().forClip(clip);
    body.appendChild(
      buildStaticSection({
        key: "clip",
        label: "Clip",
        className: "vst-detail-clip-section",
        content: buildClipColumn(context, clip, clipIdx),
        flattenContent: true,
        headerAction: buildClipSkipAction(context, clip, clipIdx)
      }).section
    );
    const stages = buildStageRail(
      context,
      clip,
      clipIdx,
      stageIdx,
      (editorStageIdx) => {
        const editorStage = clip.stages[editorStageIdx];
        return editorStage ? buildStageParamsColumn(
          context,
          clip,
          clipIdx,
          editorStageIdx,
          editorStage,
          defaults
        ) : void 0;
      },
      selection.kind === "clip"
    );
    if (!clip.stages[stageIdx]) {
      const note = document.createElement("p");
      note.className = "vst-detail-note vst-source-only-note";
      note.textContent = "Source-only clip. Add a stage to choose an architecture and refine this footage.";
      stages.appendChild(note);
    }
    body.appendChild(stages);
    const appendCapabilitySection = (feature, persisted, content) => {
      const state = capabilityView.authoringState(feature, persisted);
      if (!state.visible) {
        return;
      }
      const section = content();
      if (!state.enabled) {
        applyPersistedCapabilityRepair(section, state);
      }
      body.appendChild(section);
    };
    appendCapabilitySection(
      "frameReferences",
      clip.refs.length > 0,
      () => buildRefSection(
        context,
        clipIdx,
        selection.kind === "ref" ? selection.refIdx : null,
        clips,
        selection.kind === "ref"
      )
    );
    appendCapabilitySection(
      "stageLoras",
      clip.loras.length > 0,
      () => buildClipLorasSection(context, clip, clipIdx, stageIdx, defaults)
    );
    appendCapabilitySection(
      "icLora",
      clip.icLoras.length > 0,
      () => buildArchitectureIcLorasSection(
        context,
        clip,
        clipIdx,
        defaults,
        selection.kind === "ic-lora" ? selection.entryIdx : null,
        selection.kind === "ic-lora"
      )
    );
    appendCapabilitySection(
      "sourceVideo",
      clip.sourceVideo !== null,
      () => buildSourceVideoSection(context, clip, clipIdx, false)
    );
    appendCapabilitySection(
      "retake",
      clip.retake !== null,
      () => buildRetakeSection(context, clip, clipIdx, selection.kind === "retake")
    );
    return body;
  };

  // frontend/intervals.ts
  var freeIntervalAt = (spans, total, point) => {
    const p = Math.min(Math.max(point, 0), total);
    let lo = 0;
    let hi = total;
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

  // frontend/promptWindowEdits.ts
  var otherSpans = (windows, excludeIdx, clipDuration) => windows.map((w, k) => ({
    k,
    start: clamp(w.start, 0, clipDuration),
    end: clamp(w.start + w.duration, 0, clipDuration)
  })).filter((s) => s.k !== excludeIdx && s.end > s.start).sort((a, b) => a.start - b.start).map((s) => ({ start: s.start, end: s.end }));
  var applyPromptWindowBegin = (clip, windowIdx, desiredBegin) => {
    const window2 = clip.promptWindows?.[windowIdx];
    if (!window2) {
      return;
    }
    const clipDur = clipDurationOf(clip);
    const end = window2.start + window2.duration;
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [lo] = freeIntervalAt(spans, clipDur, Math.max(0, end - 1e-3));
    const start = clamp(desiredBegin, lo, end - PROMPT_WINDOW_MIN_DURATION);
    window2.start = roundToTenth(start);
    window2.duration = roundToTenth(end - start);
  };
  var applyPromptWindowEnd = (clip, windowIdx, desiredEnd) => {
    const window2 = clip.promptWindows?.[windowIdx];
    if (!window2) {
      return;
    }
    const clipDur = clipDurationOf(clip);
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [, hi] = freeIntervalAt(spans, clipDur, window2.start);
    const end = clamp(
      desiredEnd,
      window2.start + PROMPT_WINDOW_MIN_DURATION,
      hi
    );
    window2.start = roundToTenth(window2.start);
    window2.duration = roundToTenth(end - window2.start);
  };
  var promptWindowNeighborBounds = (clip, windowIdx) => {
    const window2 = clip.promptWindows?.[windowIdx];
    if (!window2) {
      return null;
    }
    const clipDur = clipDurationOf(clip);
    const end = window2.start + window2.duration;
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [lo] = freeIntervalAt(spans, clipDur, Math.max(0, end - 1e-3));
    const [, hi] = freeIntervalAt(spans, clipDur, window2.start);
    return { beginMin: roundToTenth(lo), endMax: roundToTenth(hi) };
  };

  // frontend/detailStrip/promptPanels.ts
  var buildMajorPromptSection = (ctx, clip, clipIdx, open) => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-prompt-body";
    const prompt = buildTextarea(
      clip.prompt ?? "",
      "Clip prompt (blank inherits the global prompt)…",
      "prompt-major",
      (value) => {
        ctx.debouncedCommit("prompt-major", (clips) => {
          const target = clips[clipIdx];
          if (target) {
            target.prompt = value.trim();
          }
        });
      }
    );
    prompt.rows = 8;
    col.appendChild(buildField("Prompt", prompt));
    const built = buildAccordionSection({
      key: "major-prompt",
      label: "Major Prompt",
      content: col,
      open,
      flattenContent: true,
      className: "vst-detail-prompt-major"
    });
    const decision = ctx.capabilities().forClip(clip).decision("majorPrompt");
    if (!decision.supported) {
      applyPersistedCapabilityRepair(built.section, decision);
      if (clip.prompt.trim()) {
        built.content.appendChild(
          buildCapabilityRepairButton({
            label: "Remove unsupported clip prompt",
            className: "vst-remove-unsupported-prompt",
            onRepair: () => {
              ctx.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                  target.prompt = "";
                }
              });
              ctx.render();
            }
          })
        );
      }
    }
    return built.section;
  };
  var buildRelayPromptSection = (ctx, clip, clipIdx, selectedWindowIdx, open) => {
    const windows = clip.promptWindows ?? [];
    const decision = ctx.capabilities().forClip(clip).decision("promptRelay");
    const activeWindowIdx = windows.length === 0 ? null : clamp(selectedWindowIdx ?? 0, 0, windows.length - 1);
    const buildSection = (editorForItem) => buildRepeatingEditor({
      key: "relay-prompts",
      label: "Relay Prompts",
      sectionClass: "vst-detail-relay-section",
      open,
      items: windows.map((window2, index) => ({
        label: `R${index}`,
        focusKey: `relay-tab-${index}`,
        title: `Relay prompt ${roundToTenth(window2.start)}–${roundToTenth(window2.start + window2.duration)} seconds`,
        active: index === activeWindowIdx,
        className: "vst-relay-tab",
        onSelect: () => setSelection({
          kind: "prompt-minor",
          clipIdx,
          windowIdx: index
        }),
        onShiftDelete: () => ctx.deleteWindowEntry(clipIdx, index)
      })),
      add: {
        title: decision.supported ? "Add a relay prompt in the first available time window" : decision.reason,
        label: "+ Add Relay Prompt",
        className: "vst-detail-add-relay",
        disabled: !decision.supported,
        onClick: () => ctx.addPromptWindow(clipIdx)
      },
      remove: {
        title: activeWindowIdx === null ? "No relay prompt to delete" : `Delete relay prompt ${activeWindowIdx}`,
        className: "vst-detail-delete-relay"
      },
      editorForItem
    }).section;
    if (activeWindowIdx === null) {
      return buildSection();
    }
    const buildEditor = (editorWindowIdx) => {
      const window2 = windows[editorWindowIdx];
      if (!window2) {
        return void 0;
      }
      const clipDuration = Math.max(
        PROMPT_WINDOW_MIN_DURATION,
        clip.duration || 0
      );
      const editorSection = document.createElement("div");
      editorSection.className = "vst-detail-col vst-detail-prompt-body vst-detail-minor-window";
      editorSection.setAttribute(
        "data-vst-minor-window",
        `${editorWindowIdx}`
      );
      const bounds = promptWindowNeighborBounds(clip, editorWindowIdx);
      const beginInput = ctx.buildClampedNumber({
        key: `minor-${editorWindowIdx}-begin`,
        value: roundToTenth(window2.start),
        min: bounds?.beginMin ?? 0,
        max: gridFloor(
          Math.max(0, clipDuration - PROMPT_WINDOW_MIN_DURATION)
        ),
        step: 0.1,
        readBack: (clips) => {
          const target = clips[clipIdx]?.promptWindows?.[editorWindowIdx];
          return target ? roundToTenth(target.start) : null;
        },
        mutate: (clips, value) => {
          const target = clips[clipIdx];
          if (target) {
            applyPromptWindowBegin(target, editorWindowIdx, value);
          }
        }
      });
      editorSection.appendChild(
        buildField(
          "Begin (s)",
          beginInput,
          void 0,
          "When this relay prompt starts within the clip."
        )
      );
      const endInput = ctx.buildClampedNumber({
        key: `minor-${editorWindowIdx}-end`,
        value: roundToTenth(window2.start + window2.duration),
        min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
        max: bounds?.endMax ?? clipDuration,
        step: 0.1,
        readBack: (clips) => {
          const target = clips[clipIdx]?.promptWindows?.[editorWindowIdx];
          return target ? roundToTenth(target.start + target.duration) : null;
        },
        mutate: (clips, value) => {
          const target = clips[clipIdx];
          if (target) {
            applyPromptWindowEnd(target, editorWindowIdx, value);
          }
        }
      });
      editorSection.appendChild(
        buildField(
          "End (s)",
          endInput,
          void 0,
          "When this relay prompt ends. It cannot cross a neighbouring relay."
        )
      );
      const editor = buildTextarea(
        window2.prompt ?? "",
        "Relay prompt for this window…",
        `minor-${editorWindowIdx}`,
        (value) => {
          ctx.debouncedCommit(`minor-${editorWindowIdx}`, (clips) => {
            const target = clips[clipIdx]?.promptWindows?.[editorWindowIdx];
            if (target) {
              target.prompt = value.trim();
            }
          });
        }
      );
      editor.addEventListener("focus", () => {
        setSelection({
          kind: "prompt-minor",
          clipIdx,
          windowIdx: editorWindowIdx
        });
      });
      editor.rows = 4;
      editorSection.appendChild(buildField("Prompt", editor));
      if (!decision.supported) {
        applyPersistedCapabilityRepair(editorSection, decision);
      }
      return editorSection;
    };
    return buildSection(buildEditor);
  };
  var buildPromptBody = (ctx, selection, clips) => {
    const clipIdx = selection.clipIdx;
    const clip = clips[clipIdx];
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-prompts-body";
    body.appendChild(
      buildMajorPromptSection(
        ctx,
        clip,
        clipIdx,
        selection.kind === "prompt-major"
      )
    );
    body.appendChild(
      buildRelayPromptSection(
        ctx,
        clip,
        clipIdx,
        selection.kind === "prompt-minor" ? selection.windowIdx : null,
        selection.kind === "prompt-minor"
      )
    );
    return body;
  };
  var buildPromptMajorBody = (ctx, selection, clips) => buildPromptBody(ctx, selection, clips);
  var buildPromptMinorBody = (ctx, selection, clips) => buildPromptBody(ctx, selection, clips);

  // frontend/detailStrip/settingsModal.ts
  var MODAL_CLASS = "vst-timeline-settings-modal";
  var BACKDROP_CLASS = "vst-timeline-settings-backdrop";
  var currentCleanup = null;
  var closeTimelineAuthoringSettingsModal = () => {
    currentCleanup?.();
    currentCleanup = null;
    document.querySelector(`.${MODAL_CLASS}`)?.remove();
    document.querySelector(`.${BACKDROP_CLASS}`)?.remove();
  };
  var openTimelineAuthoringSettingsModal = () => {
    closeTimelineAuthoringSettingsModal();
    const settings = getTimelineAuthoringSettings();
    const backdrop = document.createElement("div");
    backdrop.className = `modal-backdrop fade show ${BACKDROP_CLASS}`;
    const modal = document.createElement("div");
    modal.className = `modal fade show ${MODAL_CLASS}`;
    modal.style.display = "block";
    modal.tabIndex = -1;
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.setAttribute("aria-labelledby", "vst_timeline_settings_title");
    const dialog = document.createElement("div");
    dialog.className = "modal-dialog modal-dialog-centered";
    dialog.setAttribute("role", "document");
    const content = document.createElement("div");
    content.className = "modal-content";
    const header = document.createElement("div");
    header.className = "modal-header";
    const title = document.createElement("h5");
    title.className = "modal-title";
    title.id = "vst_timeline_settings_title";
    title.textContent = "Timeline Settings";
    const close = document.createElement("button");
    close.type = "button";
    close.className = "basic-button small-button";
    close.textContent = "×";
    close.title = "Close timeline settings";
    close.setAttribute("aria-label", close.title);
    header.append(title, close);
    const body = document.createElement("div");
    body.className = "modal-body";
    body.append(
      buildCheckbox(
        "Snap",
        settings.snap,
        (value) => setTimelineAuthoringSetting("snap", value)
      ),
      buildCheckbox("Auto-collapse", settings.autoCollapse, (value) => {
        setTimelineAuthoringSetting("autoCollapse", value);
        if (value) {
          resetRememberedAccordionSections();
        }
      })
    );
    content.append(header, body);
    dialog.appendChild(content);
    modal.appendChild(dialog);
    const dismiss = () => {
      currentCleanup?.();
    };
    const onKeyDown = (event) => {
      if (event.key === "Escape") {
        dismiss();
      }
    };
    const cleanup = () => {
      document.removeEventListener("keydown", onKeyDown);
      modal.remove();
      backdrop.remove();
      if (currentCleanup === cleanup) {
        currentCleanup = null;
      }
    };
    currentCleanup = cleanup;
    close.addEventListener("click", dismiss);
    backdrop.addEventListener("click", dismiss);
    modal.addEventListener("mousedown", (event) => {
      if (event.target === modal) {
        dismiss();
      }
    });
    document.addEventListener("keydown", onKeyDown);
    document.body.append(backdrop, modal);
    close.focus();
  };

  // frontend/detailStrip/settingsPanel.ts
  var SETTINGS_INHERIT = "inherit";
  var SETTINGS_CUSTOM = "custom";
  var clampDimension = (value) => clamp(
    Math.round(value) || ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MAX
  );
  var clampFps = (value) => clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);
  var FPS_WRITE_DEBOUNCE_MS = 300;
  var fpsWriteTimer = null;
  var scheduleCoreFpsWrite = (value) => {
    if (fpsWriteTimer !== null) {
      clearTimeout(fpsWriteTimer);
    }
    fpsWriteTimer = setTimeout(() => {
      fpsWriteTimer = null;
      const bridge2 = getVideoStagesHostBridge();
      const core = bridge2.getRootVideoFpsInput();
      if (!core || core.value === `${value}`) {
        return;
      }
      core.value = `${value}`;
      bridge2.notifyChanged(core);
    }, FPS_WRITE_DEBOUNCE_MS);
  };
  var buildSettingsBody = (ctx, _selection = {
    kind: "none"
  }) => {
    const state = getState();
    const defaults = getRootDefaults();
    const core = {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
    const defaultMode = !state.dimsExplicit ? SETTINGS_INHERIT : matchPresetKey(state.width, state.height) ?? SETTINGS_CUSTOM;
    const mode = ctx.getSettingsMode() ?? defaultMode;
    const isCustom = mode === SETTINGS_CUSTOM;
    const displayed = mode === SETTINGS_CUSTOM ? {
      width: clampDimension(state.width),
      height: clampDimension(state.height)
    } : mode === SETTINGS_INHERIT ? { width: core.width, height: core.height } : presetDimensions(mode) ?? {
      width: clampDimension(state.width),
      height: clampDimension(state.height)
    };
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-settings";
    const resSpecs = [
      {
        value: SETTINGS_INHERIT,
        label: `Use image resolution (${core.width}×${core.height})`
      },
      ...DIMENSION_PRESET_KEYS.map((key) => ({
        value: key,
        label: key.replace("x", " × ")
      })),
      { value: SETTINGS_CUSTOM, label: "Custom" }
    ];
    const resSelect = buildOptionSelect(resSpecs, mode, (value) => {
      ctx.setSettingsMode(value);
      ctx.commitState((next) => {
        if (value === SETTINGS_INHERIT) {
          next.dimsExplicit = false;
        } else if (value === SETTINGS_CUSTOM) {
          next.dimsExplicit = true;
          next.width = clampDimension(displayed.width);
          next.height = clampDimension(displayed.height);
        } else {
          const dims = presetDimensions(value);
          if (dims) {
            next.dimsExplicit = true;
            next.width = dims.width;
            next.height = dims.height;
          }
        }
      });
      ctx.render();
    });
    body.appendChild(buildField("Resolution", resSelect));
    if (isCustom) {
      const widthInput = buildNumber(
        displayed.width,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
        ROOT_DIMENSION_STEP,
        (value) => {
          ctx.debouncedCommitState("settings-width", (next) => {
            next.dimsExplicit = true;
            next.width = clampDimension(value);
          });
        }
      );
      widthInput.setAttribute("data-vst-focus-key", "settings-width");
      const heightInput = buildNumber(
        displayed.height,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
        ROOT_DIMENSION_STEP,
        (value) => {
          ctx.debouncedCommitState("settings-height", (next) => {
            next.dimsExplicit = true;
            next.height = clampDimension(value);
          });
        }
      );
      heightInput.setAttribute("data-vst-focus-key", "settings-height");
      const dimsPair = document.createElement("div");
      dimsPair.className = "vst-settings-dims";
      const dimsSep = document.createElement("span");
      dimsSep.className = "vst-settings-dims-sep";
      dimsSep.textContent = "×";
      dimsPair.append(widthInput, dimsSep, heightInput);
      body.appendChild(buildField("Dimensions", dimsPair));
    }
    const badges = document.createElement("div");
    badges.className = "vst-settings-badges";
    if (mode !== SETTINGS_CUSTOM && mode !== SETTINGS_INHERIT) {
      const els = presetBadgeElements(mode);
      if (els.length > 0) {
        badges.append(...els);
      }
    }
    badges.hidden = badges.childElementCount === 0;
    body.appendChild(badges);
    const hasCoreFps = getVideoStagesHostBridge().getRootVideoFpsInput() !== null;
    const fpsInput = buildNumber(
      clampFps(state.fps),
      ROOT_FPS_MIN,
      ROOT_FPS_MAX,
      1,
      (value) => {
        scheduleCoreFpsWrite(clampFps(value));
      }
    );
    fpsInput.disabled = !hasCoreFps;
    fpsInput.setAttribute("data-vst-focus-key", "settings-fps");
    body.appendChild(
      buildField(
        "FPS",
        fpsInput,
        hasCoreFps ? void 0 : "(no core Video FPS parameter found)",
        "Frames per second for the whole timeline. This is the same value as the core Video FPS parameter — editing either updates both."
      )
    );
    return wrapForm("timeline-settings", "Timeline", body);
  };

  // frontend/detailStrip/panelRouter.ts
  var clampDetailSelection = (selection, clips) => {
    if (selection.kind === "none") {
      return selection;
    }
    if (selection.kind === "boundary") {
      return selection.leftClipIdx >= 0 && selection.leftClipIdx <= clips.length - 2 ? selection : { kind: "none" };
    }
    if (selection.kind === "audio-track") {
      const tracks = getState().audioTracks ?? [];
      return tracks[selection.trackIdx] ? selection : { kind: "none" };
    }
    if (selection.clipIdx < 0 || selection.clipIdx >= clips.length) {
      return { kind: "none" };
    }
    const clip = clips[selection.clipIdx];
    if (selection.kind === "clip") {
      if (clip.stages.length === 0) {
        return { kind: "clip", clipIdx: selection.clipIdx, stageIdx: 0 };
      }
      const stageIdx = clamp(selection.stageIdx, 0, clip.stages.length - 1);
      return stageIdx === selection.stageIdx ? selection : { kind: "clip", clipIdx: selection.clipIdx, stageIdx };
    }
    if (selection.kind === "ref") {
      return selection.refIdx >= 0 && selection.refIdx < clip.refs.length ? selection : { kind: "none" };
    }
    if (selection.kind === "ic-lora") {
      return selection.entryIdx >= 0 && selection.entryIdx < clip.icLoras.length ? selection : { kind: "clip", clipIdx: selection.clipIdx, stageIdx: 0 };
    }
    if (selection.kind === "prompt-minor") {
      const windows = clip.promptWindows ?? [];
      return selection.windowIdx >= 0 && selection.windowIdx < windows.length ? selection : { kind: "none" };
    }
    if (selection.kind === "retake") {
      return selection;
    }
    return selection;
  };
  var detailBreadcrumb = (selection, clips) => {
    switch (selection.kind) {
      case "clip":
        return clips[selection.clipIdx]?.stages.length === 0 ? `Clip ${selection.clipIdx} · Source only` : `Clip ${selection.clipIdx} · ${stageChipLabel(selection.stageIdx)}`;
      case "ref":
        return `Ref${selection.refIdx} · Clip ${selection.clipIdx}`;
      case "ic-lora":
        return `IC-LoRA ${selection.entryIdx} · Clip ${selection.clipIdx}`;
      case "audio":
        return `Audio · Clip ${selection.clipIdx}`;
      case "audio-track":
        return `Audio segment S${selection.trackIdx}`;
      case "boundary": {
        const seam = executableBoundaryForLeftClip(
          clips,
          selection.leftClipIdx
        );
        return `Boundary · Clip ${selection.leftClipIdx} → ${seam === null ? "end" : seam.rightIdx}`;
      }
      case "prompt-major":
        return `Prompts · Clip ${selection.clipIdx}`;
      case "prompt-minor": {
        const window2 = clips[selection.clipIdx]?.promptWindows?.[selection.windowIdx];
        if (!window2) {
          return `Relay · Clip ${selection.clipIdx}`;
        }
        const start = roundToTenth(window2.start);
        const end = roundToTenth(window2.start + window2.duration);
        return `Relay ${start}–${end}s · Clip ${selection.clipIdx}`;
      }
      case "retake": {
        const retake = clips[selection.clipIdx]?.retake;
        if (!retake) {
          return `Retake · Clip ${selection.clipIdx}`;
        }
        const start = roundToTenth(retake.startSeconds);
        const end = roundToTenth(
          retake.startSeconds + retake.lengthSeconds
        );
        return `Retake · Clip ${selection.clipIdx} · ${start}–${end} s`;
      }
      default:
        return "Timeline settings";
    }
  };
  var buildDetailHeader = (selection, clips) => {
    const header = document.createElement("div");
    header.className = "vst-detail-head";
    const breadcrumb = document.createElement("span");
    breadcrumb.className = "vst-detail-crumb";
    breadcrumb.textContent = detailBreadcrumb(selection, clips);
    const settings = document.createElement("button");
    settings.type = "button";
    settings.className = "basic-button small-button vst-detail-settings-button";
    settings.textContent = "⚙";
    settings.title = "Timeline settings";
    settings.setAttribute("aria-label", settings.title);
    settings.addEventListener("click", openTimelineAuthoringSettingsModal);
    header.append(breadcrumb, settings);
    return header;
  };
  var buildDetailPanelBody = (context, selection, clips) => {
    switch (selection.kind) {
      case "clip":
        return buildClipBody(context, selection, clips);
      case "ref":
        return buildClipBody(context, selection, clips);
      case "ic-lora":
        return buildClipBody(context, selection, clips);
      case "audio":
        return buildAudioBody(context, selection, clips);
      case "audio-track":
        return buildTimelineAudioSegmentsBody(
          context,
          getState(),
          selection
        );
      case "prompt-major":
        return buildPromptMajorBody(context, selection, clips);
      case "prompt-minor":
        return buildPromptMinorBody(context, selection, clips);
      case "retake":
        return buildClipBody(context, selection, clips);
      case "boundary":
        return buildBoundaryBody(context, selection, clips);
      default:
        return buildSettingsBody(context, { kind: "none" });
    }
  };

  // frontend/detailStrip/renderShell.ts
  var DETAIL_CLASS = "vst-detail";
  var revealRepeaterKey = (selection) => {
    switch (selection.kind) {
      case "ref":
        return "references";
      case "audio-track":
        return "audio-tracks";
      case "prompt-minor":
        return "relay-prompts";
      case "ic-lora":
        return "ic-loras";
      default:
        return null;
    }
  };
  var renderDetailShell = (options) => {
    const previousBody = options.detail.querySelector(".vst-detail-body");
    const savedScroll = previousBody?.scrollTop ?? 0;
    options.focus.capture();
    options.detail.className = DETAIL_CLASS;
    options.detail.innerHTML = "";
    options.detail.appendChild(
      buildDetailHeader(options.selection, options.clips)
    );
    const body = buildDetailPanelBody(
      options.context,
      options.selection,
      options.clips
    );
    options.detail.appendChild(body);
    getVideoStagesHostBridge().enableSliders(body);
    options.focus.restore(options.detail);
    const newBody = options.detail.querySelector(".vst-detail-body");
    if (newBody && savedScroll > 0) {
      newBody.scrollTop = savedScroll;
    }
    if (options.revealSelection) {
      const key = revealRepeaterKey(options.selection);
      const target = options.selection.kind === "retake" ? options.detail.querySelector(
        '[data-vst-accordion-key="retake"]'
      ) : key ? options.detail.querySelector(
        `[data-vst-repeater-key="${key}"]`
      ) : null;
      if (target && typeof target.scrollIntoView === "function") {
        target.scrollIntoView({ block: "nearest" });
      }
    }
    options.focus.autoFocusSelection(options.detail, options.selection);
  };

  // frontend/detailStrip/selectionDomainOperations.ts
  var refStrengthPatches = (clip, next) => clip.stages.flatMap(
    (stage) => stage.id ? [
      {
        type: "stage.patch",
        clipId: clip.id,
        stageId: stage.id,
        patch: { refStrengths: next(stage.refStrengths) }
      }
    ] : []
  );
  var applyClipSkip = (clips, clipIdx, generatedEntryMode) => {
    const clip = clips[clipIdx];
    if (!clip) {
      return false;
    }
    clip.skipped = !clip.skipped;
    reconcileArchitectureIncomingIcLoraDrives(clips, generatedEntryMode);
    return true;
  };
  var applyStageSkip = (clips, clipIdx, stageIdx, catalog, generatedEntryMode) => {
    const clip = clips[clipIdx];
    const stage = clip?.stages[stageIdx];
    if (!clip || !stage) {
      return false;
    }
    stage.skipped = !stage.skipped;
    reconcileSourcedClipIdentity(clip, catalog);
    reconcileArchitectureIncomingIcLoraDrives(clips, generatedEntryMode);
    return true;
  };
  var createDetailSelectionDomainOperations = (structuralCommit, getCapabilities, getGeneratedEntryMode = () => "text-to-video") => {
    const commitRemoval = (build, index, neighbour, fallback) => structuralCommit(
      (clips) => {
        const removal = build(clips);
        return removal === null ? null : {
          command: removal.command,
          selection: selectionAfterRemoval(
            index,
            removal.remaining,
            neighbour,
            fallback
          )
        };
      },
      // Deleting the last inactive item, or the first of several items,
      // can leave the selected numeric index unchanged. A normal
      // setSelection then emits no event, so force the repeater DOM to
      // rebuild around the surviving entities.
      { rebuildAfterSelect: true }
    );
    const deleteRefEntry = (clipIdx, refIdx) => {
      commitRemoval(
        (clips) => {
          const clip = clips[clipIdx];
          const ref = clip?.refs[refIdx];
          if (!clip?.id || !ref?.id) {
            return null;
          }
          return {
            command: {
              type: "batch",
              commands: [
                {
                  type: "ref.remove",
                  clipId: clip.id,
                  refId: ref.id
                },
                ...refStrengthPatches(
                  clip,
                  (strengths) => strengths.filter(
                    (_, index) => index !== refIdx
                  )
                )
              ]
            },
            remaining: clip.refs.length - 1
          };
        },
        refIdx,
        (index) => ({ kind: "ref", clipIdx, refIdx: index }),
        { kind: "clip", clipIdx, stageIdx: 0 }
      );
    };
    const addRefEntry = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        if (!clip?.id || !getCapabilities().forClip(clip).decision("frameReferences").supported) {
          return null;
        }
        return {
          command: {
            type: "batch",
            commands: [
              {
                type: "ref.add",
                clipId: clip.id,
                ref: {
                  ...buildDefaultRef(),
                  id: createEntityId("ref")
                }
              },
              ...refStrengthPatches(clip, (strengths) => [
                ...strengths,
                STAGE_REF_STRENGTH_DEFAULT
              ])
            ]
          },
          selection: {
            kind: "ref",
            clipIdx,
            refIdx: clip.refs.length
          }
        };
      });
    };
    const addPromptWindow = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip?.id || !getCapabilities().forClip(clip).decision("promptRelay").supported) {
            return null;
          }
          const clipDuration = Math.max(0, clip.duration || 0);
          const windows = clip.promptWindows ?? [];
          let start = 0;
          let end = clipDuration;
          for (const window3 of windows) {
            const windowStart = clamp(window3.start, 0, clipDuration);
            if (windowStart - start >= PROMPT_WINDOW_MIN_DURATION) {
              end = windowStart;
              break;
            }
            start = Math.max(
              start,
              clamp(window3.start + window3.duration, 0, clipDuration)
            );
          }
          if (end === clipDuration) {
            const next = windows.find(
              (window3) => window3.start >= start + PROMPT_WINDOW_MIN_DURATION
            );
            if (next) {
              end = clamp(next.start, start, clipDuration);
            }
          }
          if (end - start < PROMPT_WINDOW_MIN_DURATION) {
            return null;
          }
          const window2 = {
            id: createEntityId("prompt_window"),
            prompt: "",
            start: roundToTenth(start),
            duration: roundToTenth(
              Math.min(PROMPT_WINDOW_DEFAULT_DURATION, end - start)
            )
          };
          const insertAt = windows.findIndex(
            (candidate) => candidate.start > window2.start
          );
          const beforeWindowId = insertAt < 0 ? null : windows[insertAt].id ?? null;
          return {
            command: {
              type: "prompt-window.add",
              clipId: clip.id,
              window: window2,
              beforeWindowId
            },
            selection: {
              kind: "prompt-minor",
              clipIdx,
              windowIdx: insertAt < 0 ? windows.length : insertAt
            }
          };
        },
        // A newly sorted leading window can reuse the currently selected
        // numeric index; rebuild even when setSelection would be a no-op.
        { rebuildAfterSelect: true }
      );
    };
    const deleteWindowEntry = (clipIdx, windowIdx) => {
      commitRemoval(
        (clips) => {
          const clip = clips[clipIdx];
          const window2 = clip?.promptWindows?.[windowIdx];
          if (!clip?.id || !window2?.id) {
            return null;
          }
          return {
            command: {
              type: "prompt-window.remove",
              clipId: clip.id,
              windowId: window2.id
            },
            remaining: clip.promptWindows.length - 1
          };
        },
        windowIdx,
        (index) => ({
          kind: "prompt-minor",
          clipIdx,
          windowIdx: index
        }),
        { kind: "prompt-major", clipIdx }
      );
    };
    const createRetake = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip?.id || clip.retake || !getCapabilities().forClip(clip).decision("retake").supported) {
            return null;
          }
          const clipDuration = Math.max(0, clip.duration || 0);
          return {
            command: {
              type: "retake.add",
              clipId: clip.id,
              retake: {
                id: createEntityId("retake"),
                startSeconds: 0,
                lengthSeconds: Math.max(
                  RETAKE_MIN_DURATION,
                  Math.min(
                    RETAKE_DEFAULT_DURATION,
                    clipDuration || RETAKE_DEFAULT_DURATION
                  )
                ),
                strength: RETAKE_STRENGTH_DEFAULT
              }
            },
            selection: { kind: "retake", clipIdx }
          };
        },
        { rebuildAfterSelect: true }
      );
    };
    const removeRetake = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip?.id || !clip.retake?.id) {
            return null;
          }
          const keepRetakeSelected = getCapabilities().forClip(clip).decision("retake").supported;
          return {
            command: {
              type: "retake.remove",
              clipId: clip.id,
              retakeId: clip.retake.id
            },
            selection: keepRetakeSelected ? { kind: "retake", clipIdx } : { kind: "clip", clipIdx, stageIdx: 0 }
          };
        },
        { rebuildAfterSelect: true }
      );
    };
    const selectStage = (clipIdx, stageIdx) => {
      setSelection({ kind: "clip", clipIdx, stageIdx });
    };
    const addStage = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip) {
            return null;
          }
          if (clip.stages.length > 0 && !getCapabilities().forClip(clip).decision("multiStage").supported) {
            return null;
          }
          const last = clip.stages[clip.stages.length - 1] ?? null;
          const defaults = getRootDefaults();
          const lockedArchitecture = clip.architecture === NONE_ARCHITECTURE_ID ? void 0 : clip.architecture;
          const stage = buildDefaultStage(
            getRootDefaults,
            (values) => getDefaultStageModel(values, lockedArchitecture),
            last,
            clip.refs.length,
            clip.loras.map(
              (entry) => defaultLoraWeight(defaults, entry.name)
            ),
            clip.icLoras.map(
              (entry) => defaultLoraWeight(defaults, entry.lora)
            )
          );
          if (clip.architecture === NONE_ARCHITECTURE_ID && clip.stages.length === 0) {
            const target = buildArchitectureRetargetPlan(
              defaults.modelCatalog,
              stage.model
            );
            if (!target || !clip.id) {
              return null;
            }
            return {
              command: {
                type: "batch",
                commands: [
                  {
                    type: "clip.convert-architecture",
                    clipId: clip.id,
                    target
                  },
                  {
                    type: "stage.add",
                    clipId: clip.id,
                    stage: {
                      ...stage,
                      id: createEntityId("stage"),
                      modelProfileId: target.modelProfileId
                    }
                  }
                ]
              },
              selection: { kind: "clip", clipIdx, stageIdx: 0 }
            };
          }
          clip.stages.push(stage);
          if (!reconcileClipArchitectureIdentity(
            clip,
            defaults.modelCatalog
          )) {
            return null;
          }
          return {
            kind: "clip",
            clipIdx,
            stageIdx: clip.stages.length - 1
          };
        },
        { rebuildAfterSelect: true }
      );
    };
    const deleteStage = (clipIdx, stageIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip || clip.stages.length === 0 || clip.stages.length === 1 && clip.sourceVideo === null || stageIdx < 0 || stageIdx >= clip.stages.length) {
            return null;
          }
          clip.stages.splice(stageIdx, 1);
          for (const entry of clip.icLoras) {
            if (entry.stage === stageIdx) {
              entry.stage = IC_LORA_STAGE_ALL;
            } else if (entry.stage > stageIdx) {
              entry.stage -= 1;
            }
            canonicalizeArchitectureIcLoraFields(
              clip.architecture,
              entry
            );
          }
          reconcileSourcedClipIdentity(clip, getCapabilities().catalog);
          reconcileArchitectureIncomingIcLoraDrives(
            clips,
            getGeneratedEntryMode()
          );
          return {
            kind: "clip",
            clipIdx,
            stageIdx: clip.stages.length === 0 ? 0 : clamp(stageIdx, 0, clip.stages.length - 1)
          };
        },
        { rebuildAfterSelect: true }
      );
    };
    const commitSkip = (clips, mutate, skipCommand) => {
      const beforeDrives = clips.map((clip) => JSON.stringify(clip.icLoras));
      if (!mutate(clips)) {
        return null;
      }
      const skip = skipCommand(clips);
      if (!skip) {
        return null;
      }
      return {
        command: {
          type: "batch",
          commands: [
            skip,
            ...clips.flatMap(
              (clip, index) => clip.id && JSON.stringify(clip.icLoras) !== beforeDrives[index] ? [
                {
                  type: "clip.patch",
                  clipId: clip.id,
                  patch: { icLoras: clip.icLoras }
                }
              ] : []
            )
          ]
        },
        selection: "render"
      };
    };
    const toggleClipSkip = (clipIdx) => {
      structuralCommit(
        (clips) => commitSkip(
          clips,
          (working) => applyClipSkip(working, clipIdx, getGeneratedEntryMode()),
          (working) => {
            const clip = working[clipIdx];
            return clip.id ? {
              type: "clip.patch",
              clipId: clip.id,
              patch: { skipped: clip.skipped }
            } : null;
          }
        )
      );
    };
    const toggleStageSkip = (clipIdx, stageIdx) => {
      structuralCommit(
        (clips) => commitSkip(
          clips,
          (working) => applyStageSkip(
            working,
            clipIdx,
            stageIdx,
            getCapabilities().catalog,
            getGeneratedEntryMode()
          ),
          (working) => {
            const clip = working[clipIdx];
            const stage = clip.stages[stageIdx];
            return clip.id && stage.id ? {
              type: "stage.patch",
              clipId: clip.id,
              stageId: stage.id,
              patch: { skipped: stage.skipped }
            } : null;
          }
        )
      );
    };
    return {
      addRefEntry,
      deleteRefEntry,
      addPromptWindow,
      deleteWindowEntry,
      createRetake,
      removeRetake,
      addStage,
      deleteStage,
      selectStage,
      toggleClipSkip,
      toggleStageSkip
    };
  };

  // frontend/detailStrip/selectionOperations.ts
  var STAGE_SELECTOR = "[data-vst-stage]";
  var MODEL_SELECTOR = "[data-vst-model]";
  var INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;
  var createDetailSelectionOperations = (structuralCommit, getCapabilities, getGeneratedEntryMode = () => "text-to-video") => {
    const domain = createDetailSelectionDomainOperations(
      structuralCommit,
      getCapabilities,
      getGeneratedEntryMode
    );
    const handleActivation = (target, shiftKey) => {
      const stageChip = target.closest(STAGE_SELECTOR);
      if (stageChip instanceof HTMLElement) {
        const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
        const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
        if (clipIdx === null || stageIdx === null) return;
        if (shiftKey) {
          domain.deleteStage(clipIdx, stageIdx);
        } else {
          domain.selectStage(clipIdx, stageIdx);
        }
        return;
      }
      const modelBadge = target.closest(MODEL_SELECTOR);
      if (modelBadge instanceof HTMLElement) {
        const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
        if (clipIdx !== null) domain.selectStage(clipIdx, 0);
      }
    };
    return {
      ...domain,
      onMouseDownCapture: (event) => {
        if (event.target instanceof Element && event.target.closest(INTERACTIVE_SELECTOR)) {
          event.stopPropagation();
        }
      },
      onClickCapture: (event) => {
        if (!(event.target instanceof Element) || !event.target.closest(INTERACTIVE_SELECTOR)) {
          return;
        }
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
      },
      onKeyDownCapture: (event) => {
        if (event.key !== "Enter" && event.key !== " ") return;
        if (!(event.target instanceof Element) || !event.target.closest(INTERACTIVE_SELECTOR)) {
          return;
        }
        event.preventDefault();
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
      },
      onStripKeyDown: (event) => {
        if (event.key !== "Escape" || event.target instanceof Element && event.target.closest(".sui-popover")) {
          return;
        }
        event.preventDefault();
        event.stopPropagation();
        setSelection({ kind: "none" });
      }
    };
  };

  // frontend/timelineDetailStrip.ts
  var DETAIL_CLASS2 = "vst-detail";
  var createTimelineDetailStrip = () => {
    let boundBody = null;
    let dockEl = null;
    let unsubscribe = null;
    let rendering = false;
    let suppressSelectionRender = false;
    let settingsMode = null;
    let revealSelectionOnNextRender = false;
    let draftQueue;
    let renderImplementation = () => {
    };
    const render = (meta) => renderImplementation(meta);
    let renderedSelection = null;
    const focus = createDetailFocusSession({
      getDock: () => dockEl,
      isRendering: () => rendering,
      flushPending: () => draftQueue?.flush()
    });
    const syncValueDerivedUi = (selection) => {
      if (!selection || !dockEl || !renderedSelection) {
        return;
      }
      const breadcrumb = dockEl.querySelector(".vst-detail-crumb");
      if (breadcrumb) {
        breadcrumb.textContent = detailBreadcrumb(
          renderedSelection,
          getClips()
        );
      }
    };
    draftQueue = createDetailDraftQueue({
      focus,
      getDock: () => dockEl,
      isRendering: () => rendering,
      getRenderedSelection: () => renderedSelection,
      syncValueDerivedUi,
      render,
      setSelectionSilently: (selection) => {
        suppressSelectionRender = true;
        setSelection(selection);
        suppressSelectionRender = false;
      }
    });
    const selectionOperations = createDetailSelectionOperations(
      draftQueue.structuralCommit,
      () => createCapabilityViewResolver(getRootDefaults().modelCatalog),
      getRootGeneratedEntryMode
    );
    const context = {
      commit: draftQueue.commit,
      commitState: draftQueue.commitState,
      debouncedCommit: draftQueue.debouncedCommit,
      debouncedCommitState: draftQueue.debouncedCommitState,
      buildClampedNumber: draftQueue.buildClampedNumber,
      structuralCommit: draftQueue.structuralCommit,
      render,
      capabilities: () => createCapabilityViewResolver(getRootDefaults().modelCatalog),
      generatedEntryMode: getRootGeneratedEntryMode,
      addRefEntry: selectionOperations.addRefEntry,
      deleteRefEntry: selectionOperations.deleteRefEntry,
      addPromptWindow: selectionOperations.addPromptWindow,
      deleteWindowEntry: selectionOperations.deleteWindowEntry,
      createRetake: selectionOperations.createRetake,
      removeRetake: selectionOperations.removeRetake,
      addStage: selectionOperations.addStage,
      deleteStage: selectionOperations.deleteStage,
      selectStage: selectionOperations.selectStage,
      toggleClipSkip: selectionOperations.toggleClipSkip,
      toggleStageSkip: selectionOperations.toggleStageSkip,
      getBoundBody: () => boundBody,
      getDockEl: () => dockEl,
      getSettingsMode: () => settingsMode,
      setSettingsMode: (mode) => {
        settingsMode = mode;
      }
    };
    const ensureDetail = () => {
      if (!dockEl) {
        throw new Error("detail strip not attached");
      }
      return dockEl;
    };
    renderImplementation = (meta) => {
      if (!dockEl) {
        return;
      }
      if (meta?.origin === "detail-strip" && meta.hint === "value-only" && renderedSelection && isSameSelection(getSelection(), renderedSelection)) {
        draftQueue.markCurrentSource();
        syncValueDerivedUi(renderedSelection);
        return;
      }
      draftQueue.flush();
      rendering = true;
      try {
        draftQueue.markCurrentSource();
        const detail = ensureDetail();
        const clips = getClips();
        const rawSelection = getSelection();
        const selection = clampDetailSelection(rawSelection, clips);
        if (!isSameSelection(rawSelection, selection)) {
          setSelection(selection);
          return;
        }
        const revealSelection = revealSelectionOnNextRender;
        revealSelectionOnNextRender = false;
        renderDetailShell({
          detail,
          context,
          focus,
          clips,
          selection,
          revealSelection
        });
        renderedSelection = selection;
      } finally {
        rendering = false;
      }
    };
    const onSelectionChanged = () => {
      if (suppressSelectionRender) {
        return;
      }
      focus.beginSelectionSession();
      settingsMode = null;
      const active = document.activeElement;
      revealSelectionOnNextRender = !(active instanceof HTMLElement && dockEl?.contains(active));
      render();
    };
    const dispose = () => {
      draftQueue.dispose();
      closeTimelineAuthoringSettingsModal();
      focus.reset();
      document.removeEventListener(
        "pointerdown",
        focus.onDocumentPointerDown,
        true
      );
      document.removeEventListener(
        "pointerup",
        focus.onDocumentPointerUp,
        true
      );
      document.removeEventListener(
        "pointercancel",
        focus.onDocumentPointerUp,
        true
      );
      unsubscribe?.();
      unsubscribe = null;
      if (boundBody) {
        boundBody.removeEventListener(
          "mousedown",
          selectionOperations.onMouseDownCapture,
          true
        );
        boundBody.removeEventListener(
          "click",
          selectionOperations.onClickCapture,
          true
        );
        boundBody.removeEventListener(
          "keydown",
          selectionOperations.onKeyDownCapture,
          true
        );
        boundBody = null;
      }
      if (dockEl) {
        dockEl.removeEventListener(
          "keydown",
          selectionOperations.onStripKeyDown
        );
        dockEl.removeEventListener("focusout", focus.onDockFocusOut);
        dockEl.removeEventListener("focusin", focus.onDockFocusIn);
        dockEl.removeEventListener("change", focus.onDockChange);
        dockEl.className = DETAIL_CLASS2;
        dockEl.innerHTML = "";
        dockEl = null;
      }
      renderedSelection = null;
    };
    const attach = (body, dock) => {
      if (boundBody === body && dockEl === dock) {
        return;
      }
      dispose();
      boundBody = body;
      dockEl = dock;
      body.addEventListener(
        "mousedown",
        selectionOperations.onMouseDownCapture,
        true
      );
      body.addEventListener(
        "click",
        selectionOperations.onClickCapture,
        true
      );
      body.addEventListener(
        "keydown",
        selectionOperations.onKeyDownCapture,
        true
      );
      dock.addEventListener("keydown", selectionOperations.onStripKeyDown);
      dock.addEventListener("focusout", focus.onDockFocusOut);
      dock.addEventListener("focusin", focus.onDockFocusIn);
      dock.addEventListener("change", focus.onDockChange);
      document.addEventListener(
        "pointerdown",
        focus.onDocumentPointerDown,
        true
      );
      document.addEventListener("pointerup", focus.onDocumentPointerUp, true);
      document.addEventListener(
        "pointercancel",
        focus.onDocumentPointerUp,
        true
      );
      unsubscribe = subscribeSelection(onSelectionChanged);
      render();
    };
    return {
      attach,
      render,
      flushPending: () => draftQueue.flush(),
      dispose
    };
  };

  // frontend/timelineHistory.ts
  var createTimelineHistory = (deps) => {
    const max = deps.maxDepth ?? 50;
    const undoStack = [];
    let redoStack = [];
    let last = null;
    let suppress = false;
    const rebase = () => {
      const current = deps.read();
      if (current === last) {
        return;
      }
      last = current;
      undoStack.length = 0;
      redoStack = [];
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
      const target = from[from.length - 1];
      suppress = true;
      try {
        deps.write(target);
      } catch (error) {
        from.pop();
        throw error;
      } finally {
        suppress = false;
      }
      from.pop();
      to.push(current);
      last = target;
      return true;
    };
    return {
      rebase,
      capture,
      undo: () => restore(undoStack, redoStack),
      redo: () => restore(redoStack, undoStack),
      canUndo: () => undoStack.length > 0,
      canRedo: () => redoStack.length > 0
    };
  };

  // frontend/timelineHostLifecycle.ts
  var INPUT_SYNC_INTERVAL_MS = 200;
  var createTimelineHostLifecycle = (options) => {
    let boundInput = null;
    let boundToggle = null;
    let inputSyncInterval = null;
    let paramRefreshCleanup = null;
    const onInputChanged = () => options.syncFromCarrier();
    const onEnabledToggled = () => options.refresh();
    const onPageExit = () => options.flushPending();
    const bindInput = () => {
      const input2 = getPromptInput();
      if (!input2 || input2 === boundInput) return;
      boundInput?.removeEventListener("input", onInputChanged);
      boundInput?.removeEventListener("change", onInputChanged);
      input2.addEventListener("input", onInputChanged);
      input2.addEventListener("change", onInputChanged);
      boundInput = input2;
    };
    const bindToggle = () => {
      const toggle = getGroupToggle();
      if (!toggle || toggle === boundToggle) return;
      boundToggle?.removeEventListener("change", onEnabledToggled);
      toggle.addEventListener("change", onEnabledToggled);
      boundToggle = toggle;
    };
    const onKeydown = (event) => {
      if (!(event.ctrlKey || event.metaKey)) return;
      const key = event.key.toLowerCase();
      const isUndo = key === "z" && !event.shiftKey;
      const isRedo = key === "z" && event.shiftKey || key === "y";
      if (!isUndo && !isRedo) return;
      const active = document.activeElement;
      const inTextField = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active instanceof HTMLElement && active.isContentEditable;
      if (inTextField || !isVideoStagesEnabled()) return;
      if (isUndo ? options.undo() : options.redo()) event.preventDefault();
    };
    const bind = () => {
      bindInput();
      bindToggle();
      document.removeEventListener("keydown", onKeydown);
      document.addEventListener("keydown", onKeydown);
      window.removeEventListener("pagehide", onPageExit);
      window.addEventListener("pagehide", onPageExit);
      window.removeEventListener("beforeunload", onPageExit);
      window.addEventListener("beforeunload", onPageExit);
      if (!inputSyncInterval) {
        inputSyncInterval = setInterval(
          options.syncFromCarrier,
          INPUT_SYNC_INTERVAL_MS
        );
      }
      if (!paramRefreshCleanup) {
        paramRefreshCleanup = getVideoStagesHostBridge().addParamRefreshHook(() => {
          options.refreshCatalog();
          setTimeout(options.refresh, 0);
        });
      }
    };
    const dispose = () => {
      if (inputSyncInterval) {
        clearInterval(inputSyncInterval);
        inputSyncInterval = null;
      }
      boundInput?.removeEventListener("input", onInputChanged);
      boundInput?.removeEventListener("change", onInputChanged);
      boundInput = null;
      boundToggle?.removeEventListener("change", onEnabledToggled);
      boundToggle = null;
      paramRefreshCleanup?.();
      paramRefreshCleanup = null;
      document.removeEventListener("keydown", onKeydown);
      window.removeEventListener("pagehide", onPageExit);
      window.removeEventListener("beforeunload", onPageExit);
    };
    return { bind, dispose };
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

  // frontend/timelineSelectionView.ts
  var SELECTED = "vst-selected";
  var REGION_SELECTED = "vst-region-selected";
  var applySelectionHighlight = (body) => {
    const sel = getSelection();
    for (const el of body.querySelectorAll(`.${SELECTED}`)) {
      el.classList.remove(SELECTED);
    }
    for (const el of body.querySelectorAll(`.${REGION_SELECTED}`)) {
      el.classList.remove(REGION_SELECTED);
    }
    if (sel.kind === "clip" || sel.kind === "ic-lora") {
      body.querySelector(
        `.vst-region[data-clip-idx="${sel.clipIdx}"]`
      )?.classList.add(REGION_SELECTED);
      return;
    }
    let selector = null;
    switch (sel.kind) {
      case "ref":
        selector = `.vst-refs-mark[data-clip-idx="${sel.clipIdx}"][data-ref-idx="${sel.refIdx}"]`;
        break;
      case "audio":
        selector = `.vst-audio-clip[data-clip-idx="${sel.clipIdx}"]`;
        break;
      case "audio-track":
        selector = `.vst-audio-seg[data-track-idx="${sel.trackIdx}"]`;
        break;
      case "prompt-major":
        selector = `.vst-major-seg[data-clip-idx="${sel.clipIdx}"]`;
        break;
      case "prompt-minor":
        selector = `.vst-minor-seg[data-clip-idx="${sel.clipIdx}"][data-window-idx="${sel.windowIdx}"]`;
        break;
      case "retake":
        selector = `.vst-retake[data-clip-idx="${sel.clipIdx}"]`;
        break;
      case "boundary":
        selector = `.vst-boundary-chip[data-left-clip-idx="${sel.leftClipIdx}"]`;
        break;
      default:
        selector = null;
    }
    if (selector) {
      body.querySelector(selector)?.classList.add(SELECTED);
    }
  };

  // frontend/timelineLinking.ts
  var REGION_SELECTOR = ".vst-region[data-clip-idx]";
  var REGION_ACTION_SELECTOR = "[data-vst-region-action]";
  var REGION_RESIZE_SELECTOR = ".vst-region-resize";
  var CLIP_SHIFT_SELECTOR = ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";
  var DRAGGING_CLASS = "vst-dragging";
  var RESIZING_CLASS = "vst-resizing";
  var DROP_INDICATOR_CLASS = "vst-drop-indicator";
  var DRAG_THRESHOLD_PX2 = 5;
  var MIN_RESIZE_WIDTH_PX = 24;
  var REGION_DRAGGING_CLASS = "vst-region-dragging";
  var resolveSelectedIndex = (selectedIndex, clipCount) => {
    if (selectedIndex === null || !Number.isInteger(selectedIndex) || selectedIndex < 0 || selectedIndex >= clipCount) {
      return null;
    }
    return selectedIndex;
  };
  var shiftClipsAfter = (body, idx, deltaPx) => {
    for (const el of body.querySelectorAll(CLIP_SHIFT_SELECTOR)) {
      const elIdx = parseIntAttr(el, "data-clip-idx");
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
  var createTimelineLinking = () => {
    let attachedBody = null;
    const selectedClip = () => getSelectedClipIndex();
    const stageForClip = (clipIdx) => {
      const sel = getSelection();
      return sel.kind === "clip" && sel.clipIdx === clipIdx ? sel.stageIdx : 0;
    };
    const selectClip = (clipIdx, stageIdx) => {
      setSelection({ kind: "clip", clipIdx, stageIdx });
    };
    let dropIndicator = null;
    const findRegion = (body, idx) => body.querySelector(`.vst-region[data-clip-idx="${idx}"]`);
    const onRegionClick = (body, event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }
      const actionButton = target.closest(REGION_ACTION_SELECTOR);
      if (actionButton) {
        event.stopPropagation();
        const actionRegion = actionButton.closest(REGION_SELECTOR);
        const actionIdx = parseIntAttr(actionRegion, "data-clip-idx");
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
      const idx = parseIntAttr(region, "data-clip-idx");
      if (idx === null) {
        return;
      }
      if (event.shiftKey) {
        applyDelete(idx);
        return;
      }
      selectClip(idx, 0);
      applySelectionHighlight(body);
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
    const applySkip = (idx) => {
      const clips = getClips();
      if (applyClipSkip(clips, idx, getRootGeneratedEntryMode())) {
        saveClips(clips, { origin: "linking" });
      }
    };
    const applyDelete = (idx) => {
      const clips = getClips();
      if (idx < 0 || idx >= clips.length) {
        return;
      }
      clips.splice(idx, 1);
      reconcileArchitectureIncomingIcLoraDrives(
        clips,
        getRootGeneratedEntryMode()
      );
      saveClips(clips, { origin: "linking" });
    };
    const resizeSession = (body, state) => {
      const restore = () => {
        state.el.style.width = `${state.originalWidthPx}px`;
        clearClipShifts(body);
        body.classList.remove(RESIZING_CLASS);
      };
      return {
        threshold: DRAG_THRESHOLD_PX2,
        suppressEscapeClick: true,
        onMove: (ctx) => {
          const width = Math.max(
            MIN_RESIZE_WIDTH_PX,
            ctx.event.clientX - state.startLeftPx
          );
          body.classList.add(RESIZING_CLASS);
          state.el.style.width = `${width}px`;
          shiftClipsAfter(body, state.idx, width - state.originalWidthPx);
        },
        onCommit: (ctx) => {
          const width = ctx.event.clientX - state.startLeftPx;
          const committed = commitClipMutation(
            state.sourceRevision,
            "linking",
            (clips) => {
              const clip = clips[state.idx];
              if (state.idx < 0 || state.idx >= clips.length || clip.clipLengthFromAudio || clip.clipLengthFromControlNet || clip.sourceVideo) {
                return null;
              }
              const fps = documentFps(getState());
              const newDuration = pxToDuration(
                width,
                livePxPerSecond(body),
                fps
              );
              if (!applyClipDurationResize(
                clip,
                newDuration,
                getRootDefaults,
                fps
              )) {
                return null;
              }
              selectClip(state.idx, stageForClip(state.idx));
              return clips;
            }
          );
          if (committed) {
            body.classList.remove(RESIZING_CLASS);
          } else {
            restore();
          }
        },
        onTap: restore,
        onCancel: restore
      };
    };
    const dragSession = (body, state) => {
      const cleanup = () => {
        findRegion(body, state.sourceIdx)?.classList.remove(
          REGION_DRAGGING_CLASS
        );
        removeDropIndicator();
        body.classList.remove(DRAGGING_CLASS);
      };
      return {
        threshold: DRAG_THRESHOLD_PX2,
        axis: "xy",
        suppressEscapeClick: true,
        onMove: (ctx) => {
          body.classList.add(DRAGGING_CLASS);
          findRegion(body, state.sourceIdx)?.classList.add(
            REGION_DRAGGING_CLASS
          );
          const { els, rects } = readRegions(body);
          showDropIndicator(
            els,
            computeDropIndex(ctx.event.clientX, rects)
          );
        },
        onCommit: (ctx) => {
          cleanup();
          const { rects } = readRegions(body);
          const gap = computeDropIndex(ctx.event.clientX, rects);
          const from = state.sourceIdx;
          if (isNoOpMove(from, gap)) {
            selectClip(from, stageForClip(from));
            applySelectionHighlight(body);
            return;
          }
          const stageIdx = stageForClip(from);
          const moved = commitClipMutation(
            state.sourceRevision,
            "linking",
            (clips) => {
              if (from < 0 || from >= clips.length) {
                return null;
              }
              const reordered = moveItem(clips, from, gap);
              reconcileArchitectureIncomingIcLoraDrives(
                reordered,
                getRootGeneratedEntryMode()
              );
              return reordered;
            }
          );
          if (moved) {
            selectClip(finalIndexAfterMove(from, gap), stageIdx);
          }
        },
        onCancel: cleanup
      };
    };
    const onPress = (me, body) => {
      if (!(me.target instanceof Element)) {
        return null;
      }
      if (me.target.closest(REGION_ACTION_SELECTOR)) {
        return null;
      }
      if (me.shiftKey) {
        me.preventDefault();
        return null;
      }
      const resizeGrip = me.target.closest(REGION_RESIZE_SELECTOR);
      if (resizeGrip) {
        const region = resizeGrip.closest(REGION_SELECTOR);
        const idx2 = parseIntAttr(region, "data-clip-idx");
        if (idx2 === null || !(region instanceof HTMLElement)) {
          return null;
        }
        const rect = region.getBoundingClientRect();
        me.preventDefault();
        return resizeSession(body, {
          idx: idx2,
          el: region,
          startLeftPx: rect.left,
          originalWidthPx: rect.width,
          sourceRevision: currentRevision()
        });
      }
      const target = me.target.closest(REGION_SELECTOR);
      const idx = parseIntAttr(target, "data-clip-idx");
      if (idx === null) {
        return null;
      }
      return dragSession(body, {
        sourceIdx: idx,
        sourceRevision: currentRevision()
      });
    };
    let bodyClickHandler = null;
    let unregister = null;
    const attach = (body, router) => {
      if (attachedBody === body) {
        return;
      }
      if (attachedBody) {
        dispose();
      }
      bodyClickHandler = (e) => onRegionClick(body, e);
      body.addEventListener("click", bodyClickHandler);
      unregister = router.register({
        id: "linking",
        priority: 10,
        onPress
      });
      attachedBody = body;
    };
    const reapplySelection = (body, clipCount) => {
      const idx = selectedClip();
      if (idx !== null && resolveSelectedIndex(idx, clipCount) === null) {
        setSelection({ kind: "none" });
      }
      applySelectionHighlight(body);
    };
    const dispose = () => {
      if (attachedBody) {
        if (bodyClickHandler) {
          attachedBody.removeEventListener("click", bodyClickHandler);
        }
      }
      removeDropIndicator();
      unregister?.();
      unregister = null;
      bodyClickHandler = null;
      attachedBody = null;
    };
    const getSelectedIndex = () => selectedClip();
    return { attach, reapplySelection, getSelectedIndex, dispose };
  };

  // frontend/timelinePromptTrack.ts
  var MAJOR_SELECTOR = ".vst-major-seg[data-clip-idx]";
  var wallsFor = (clip, windowIdx, press) => {
    const clipDur = clipDurationOf(clip);
    return freeIntervalAt(
      otherSpans(clip.promptWindows ?? [], windowIdx, clipDur),
      clipDur,
      press.start
    );
  };
  var createTimelinePromptTrack = (getCapabilities) => createWindowTrack({
    routeId: "prompt-track",
    priority: 20,
    scope: clipWindowTrackScope("prompt-track"),
    spanSelector: ".vst-minor-seg[data-clip-idx]",
    itemIdxAttr: "data-window-idx",
    edgeSelector: "[data-vst-minor-edge]",
    edgeAttr: "data-vst-minor-edge",
    laneSelector: ".vst-minor-lane[data-clip-idx]",
    draggingClass: "vst-prompt-dragging",
    ghostClass: "vst-minor-ghost",
    unit: "px",
    keyboardSelect: false,
    isolateClicks: false,
    readSpan: ({ owner }, windowIdx) => {
      const window2 = owner.promptWindows?.[windowIdx];
      return window2 ? { start: window2.start, length: window2.duration, trim: 0 } : null;
    },
    canEdit: ({ owner }) => getCapabilities?.().forClip(owner).decision("promptRelay").supported ?? true,
    canCreate: ({ owner }) => owner !== null && (getCapabilities?.().forClip(owner).decision("promptRelay").supported ?? true),
    moveTargetStart: ({ owner: clip }, windowIdx, press, desiredStart) => {
      const clipDur = clipDurationOf(clip);
      const [lo, hi] = wallsFor(clip, windowIdx, press);
      const dur = Math.min(press.length, clipDur);
      return clamp(desiredStart, lo, Math.max(lo, hi - dur));
    },
    writeMove: ({ owner: clip }, windowIdx, press, start) => {
      const window2 = clip.promptWindows?.[windowIdx];
      if (!window2) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const [, hi] = wallsFor(clip, windowIdx, press);
      const dur = Math.min(press.length, clipDur);
      window2.start = roundToTenth(start);
      window2.duration = roundToTenth(
        Math.max(
          PROMPT_WINDOW_MIN_DURATION,
          Math.min(dur, hi - window2.start)
        )
      );
    },
    resizeTarget: ({ owner: clip }, windowIdx, edge, press, deltaSec) => {
      const clipDur = clipDurationOf(clip);
      const spans = otherSpans(
        clip.promptWindows ?? [],
        windowIdx,
        clipDur
      );
      const [, hi] = freeIntervalAt(spans, clipDur, press.start);
      const end = press.start + press.length;
      const [lo] = freeIntervalAt(
        spans,
        clipDur,
        Math.max(0, end - 1e-3)
      );
      return resizeSpanEdge(
        edge,
        press,
        deltaSec,
        PROMPT_WINDOW_MIN_DURATION,
        lo,
        hi
      );
    },
    writeResize: ({ owner: clip }, windowIdx, _edge, _press, geom) => {
      const window2 = clip.promptWindows?.[windowIdx];
      if (!window2) {
        return;
      }
      window2.start = roundToTenth(geom.start);
      window2.duration = roundToTenth(geom.length);
    },
    createSpan: ({ owner: clip, ownerIdx: clipIdx }, startSec, endSec) => {
      const clipDur = clipDurationOf(clip);
      const spans = otherSpans(clip.promptWindows ?? [], -1, clipDur);
      const [lo, hi] = freeIntervalAt(spans, clipDur, startSec);
      const geom = createDefaultOrDraggedSpan(
        startSec,
        endSec,
        lo,
        hi,
        PROMPT_WINDOW_MIN_DURATION,
        PROMPT_WINDOW_DEFAULT_DURATION
      );
      if (!geom) {
        return null;
      }
      const window2 = {
        prompt: "",
        start: roundToTenth(geom.start),
        duration: roundToTenth(geom.length)
      };
      clip.promptWindows.push(window2);
      clip.promptWindows.sort((x, y) => x.start - y.start);
      const newIdx = clip.promptWindows.indexOf(window2);
      return newIdx >= 0 ? { kind: "prompt-minor", clipIdx, windowIdx: newIdx } : null;
    },
    deleteItem: ({ owner: clip, ownerIdx: clipIdx }, windowIdx) => {
      if (!clip.promptWindows?.[windowIdx]) {
        return null;
      }
      clip.promptWindows.splice(windowIdx, 1);
      return selectionAfterRemoval(
        windowIdx,
        clip.promptWindows.length,
        (index) => ({
          kind: "prompt-minor",
          clipIdx,
          windowIdx: index
        }),
        { kind: "prompt-major", clipIdx }
      );
    },
    selectionFor: (clipIdx, windowIdx) => ({
      kind: "prompt-minor",
      clipIdx,
      windowIdx
    }),
    // Clicks that land on the MAJOR (whole-clip prompt) row select it.
    onClickFallthrough: (_event, target) => {
      const major = target.closest(MAJOR_SELECTOR);
      if (!(major instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseIntAttr(major, "data-clip-idx");
      if (clipIdx === null || !getClips()[clipIdx]) {
        return;
      }
      if (getCapabilities && !getCapabilities().forClip(getClips()[clipIdx]).decision("majorPrompt").supported && !getClips()[clipIdx].prompt.trim()) {
        return;
      }
      setSelection({ kind: "prompt-major", clipIdx });
    }
  });

  // frontend/timelineReferencesTrack.ts
  var THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
  var LANE_SELECTOR = ".vst-refs-lane[data-vst-ref-add]";
  var DRAGGING_CLASS2 = "vst-refs-dragging";
  var DRAG_THRESHOLD_PX3 = 5;
  var createTimelineReferencesTrack = (getCapabilities) => {
    let boundBody = null;
    let unregister = null;
    const canEditReferences = (clip) => getCapabilities?.().forClip(clip).decision("frameReferences").supported ?? true;
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
    const addRefAtFrame = (clipIdx, frame, sourceRevision) => {
      const fps = documentFps(getState());
      let newRefIdx = -1;
      const saved = commitClipMutation(
        sourceRevision,
        "references-track",
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip || !canEditReferences(clip)) {
            return null;
          }
          const frameMax = getReferenceFrameMax(
            getRootDefaults,
            clip,
            fps
          );
          const ref = buildDefaultRef();
          ref.frame = clamp(Math.round(frame), REF_FRAME_MIN, frameMax);
          appendRefToClip(clip, ref);
          newRefIdx = clip.refs.length - 1;
          return clips;
        }
      );
      if (saved) {
        setSelection({ kind: "ref", clipIdx, refIdx: newRefIdx });
      }
    };
    const deleteRef = (clipIdx, refIdx, sourceRevision) => {
      commitClipMutation(sourceRevision, "references-track", (clips) => {
        const clip = clips[clipIdx];
        return clip && removeRefAt(clip, refIdx) ? clips : null;
      });
    };
    const dragFrameAt = (state, clientX) => {
      const rect = state.lane.getBoundingClientRect();
      const frame = pxToFrame(
        clientX - rect.left,
        rect.width,
        state.durationSeconds,
        state.fps,
        state.fromEnd
      );
      if (!getTimelineAuthoringSettings().snap || rect.width <= 0) {
        return frame;
      }
      const thresholdFrames = Math.max(
        1,
        SNAP_THRESHOLD_PX / rect.width * state.frameMax
      );
      return Math.round(
        snapPoint(
          frame,
          [],
          [REF_FRAME_MIN, state.frameMax],
          thresholdFrames
        )
      );
    };
    const restoreDragPreview = (state) => {
      state.mark.style.left = state.originalLeft;
      if (state.arrow) {
        state.arrow.style.left = state.arrowOriginalLeft;
      }
      const ph = state.mark.querySelector(".vst-refs-ph");
      if (ph) {
        ph.textContent = state.originalLabel;
      }
    };
    const dragSession = (body, state) => ({
      threshold: DRAG_THRESHOLD_PX3,
      suppressEscapeClick: true,
      onMove: (ctx) => {
        body.classList.add(DRAGGING_CLASS2);
        positionRefMarker(
          state.mark,
          state.arrow,
          dragFrameAt(state, ctx.event.clientX),
          state.fromEnd,
          state.durationSeconds,
          state.fps
        );
      },
      onCommit: (ctx) => {
        body.classList.remove(DRAGGING_CLASS2);
        const newFrame = dragFrameAt(state, ctx.event.clientX);
        const saved = commitClipMutation(
          state.sourceRevision,
          "references-track",
          (clips) => {
            const ref = clips[state.clipIdx]?.refs?.[state.refIdx];
            if (!ref || ref.frame === newFrame) {
              return null;
            }
            ref.frame = newFrame;
            return clips;
          }
        );
        if (!saved) {
          restoreDragPreview(state);
        }
      },
      onTap: () => restoreDragPreview(state),
      onCancel: () => {
        restoreDragPreview(state);
        body.classList.remove(DRAGGING_CLASS2);
      }
    });
    const onPress = (me, body) => {
      if (!(me.target instanceof Element)) {
        return null;
      }
      const mark = me.target.closest(THUMB_SELECTOR);
      if (!(mark instanceof HTMLElement)) {
        return null;
      }
      if (me.shiftKey) {
        me.preventDefault();
        return claimOnly();
      }
      const lane = mark.closest(LANE_SELECTOR);
      const clipIdx = parseIntAttr(mark, "data-clip-idx");
      const refIdx = parseIntAttr(mark, "data-ref-idx");
      if (!(lane instanceof HTMLElement) || clipIdx === null || refIdx === null) {
        return null;
      }
      const clip = getClips()[clipIdx];
      const ref = clip?.refs?.[refIdx];
      if (!clip || !ref) {
        return null;
      }
      if (!canEditReferences(clip)) {
        me.preventDefault();
        return claimOnly();
      }
      const arrow = findArrow(clipIdx, refIdx);
      const fps = documentFps(getState());
      me.preventDefault();
      return dragSession(body, {
        clipIdx,
        refIdx,
        mark,
        arrow,
        lane,
        originalLeft: mark.style.left,
        arrowOriginalLeft: arrow?.style.left ?? "",
        originalLabel: mark.querySelector(".vst-refs-ph")?.textContent ?? "",
        durationSeconds: clip.duration,
        fps,
        frameMax: getReferenceFrameMax(getRootDefaults, clip, fps),
        fromEnd: ref.fromEnd === true,
        sourceRevision: currentRevision()
      });
    };
    const selectRef = (clipIdx, refIdx) => {
      activateSelection({ kind: "ref", clipIdx, refIdx });
    };
    const onBodyClick = (event) => {
      if (!(event.target instanceof Element)) {
        return;
      }
      const thumb = event.target.closest(THUMB_SELECTOR);
      if (thumb instanceof HTMLElement) {
        const clipIdx2 = parseIntAttr(thumb, "data-clip-idx");
        const refIdx = parseIntAttr(thumb, "data-ref-idx");
        if (clipIdx2 !== null && refIdx !== null) {
          if (event.shiftKey) {
            deleteRef(clipIdx2, refIdx, currentRevision());
          } else {
            selectRef(clipIdx2, refIdx);
          }
        }
        return;
      }
      const lane = event.target.closest(LANE_SELECTOR);
      if (!(lane instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseIntAttr(lane, "data-clip-idx");
      if (clipIdx === null) {
        return;
      }
      const clip = getClips()[clipIdx];
      if (!clip) {
        return;
      }
      if (!canEditReferences(clip)) {
        return;
      }
      const rect = lane.getBoundingClientRect();
      const frame = pxToFrame(
        event.clientX - rect.left,
        rect.width,
        clip.duration,
        documentFps(getState()),
        false
      );
      addRefAtFrame(clipIdx, frame, currentRevision());
    };
    const onBodyKeyDown = (event) => {
      const ke = event;
      if (!isActivateKey(ke)) {
        return;
      }
      if (!(ke.target instanceof Element)) {
        return;
      }
      const thumb = ke.target.closest(THUMB_SELECTOR);
      if (!(thumb instanceof HTMLElement)) {
        return;
      }
      const clipIdx = parseIntAttr(thumb, "data-clip-idx");
      const refIdx = parseIntAttr(thumb, "data-ref-idx");
      if (clipIdx === null || refIdx === null) {
        return;
      }
      ke.preventDefault();
      selectRef(clipIdx, refIdx);
    };
    const attach = (body, router) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("click", onBodyClick);
      body.addEventListener("keydown", onBodyKeyDown);
      unregister = router.register({
        id: "references",
        priority: 30,
        onPress: (me) => onPress(me, body)
      });
    };
    const dispose = () => {
      if (boundBody) {
        boundBody.removeEventListener("click", onBodyClick);
        boundBody.removeEventListener("keydown", onBodyKeyDown);
        boundBody = null;
      }
      unregister?.();
      unregister = null;
    };
    return { attach, dispose };
  };

  // frontend/timelineRetakeTrack.ts
  var createTimelineRetakeTrack = (getCapabilities) => createWindowTrack({
    routeId: "retake",
    priority: 50,
    scope: clipWindowTrackScope("retake-track"),
    spanSelector: ".vst-retake[data-clip-idx]",
    itemIdxAttr: null,
    edgeSelector: "[data-vst-retake-edge]",
    edgeAttr: "data-vst-retake-edge",
    laneSelector: ".vst-retake-lane[data-vst-retake-add]",
    draggingClass: "vst-retake-dragging",
    ghostClass: "vst-retake-ghost",
    unit: "pct",
    keyboardSelect: true,
    revealOnActivate: true,
    // The retake sits inside the clip region; its clicks must not bubble
    // into the region's clip-select handler.
    isolateClicks: true,
    readSpan: ({ owner }) => owner.retake ? {
      start: owner.retake.startSeconds,
      length: owner.retake.lengthSeconds,
      trim: 0
    } : null,
    canEdit: ({ owner }) => getCapabilities?.().forClip(owner).decision("retake").supported ?? true,
    canCreate: ({ owner }) => owner !== null && !owner.retake && (getCapabilities?.().forClip(owner).decision("retake").supported ?? true),
    moveTargetStart: ({ duration }, _itemIdx, press, desiredStart) => {
      const length = Math.min(press.length, duration);
      return clamp(desiredStart, 0, Math.max(0, duration - length));
    },
    writeMove: ({ owner, duration }, _itemIdx, press, start) => {
      if (!owner.retake) {
        return;
      }
      const length = Math.min(press.length, duration);
      owner.retake.startSeconds = roundToTenth(start);
      owner.retake.lengthSeconds = roundToTenth(
        Math.min(length, duration - owner.retake.startSeconds)
      );
    },
    resizeTarget: ({ duration }, _itemIdx, edge, press, deltaSec) => resizeSpanEdge(
      edge,
      press,
      deltaSec,
      RETAKE_MIN_DURATION,
      0,
      duration
    ),
    writeResize: ({ owner }, _itemIdx, _edge, _press, geom) => {
      if (!owner.retake) {
        return;
      }
      owner.retake.startSeconds = roundToTenth(geom.start);
      owner.retake.lengthSeconds = roundToTenth(geom.length);
    },
    // A plain tap places a default-length window at the pressed time; a
    // drag sizes it.
    createSpan: ({ owner, ownerIdx, duration }, startSec, endSec) => {
      if (owner.retake) {
        return null;
      }
      const geom = createDefaultOrDraggedSpan(
        startSec,
        endSec,
        0,
        duration,
        RETAKE_MIN_DURATION,
        RETAKE_DEFAULT_DURATION
      );
      if (!geom) {
        return null;
      }
      owner.retake = {
        startSeconds: roundToTenth(geom.start),
        lengthSeconds: roundToTenth(geom.length),
        strength: RETAKE_STRENGTH_DEFAULT
      };
      return { kind: "retake", clipIdx: ownerIdx };
    },
    // The clip owns at most one retake, so its removal always falls back
    // to the clip itself.
    deleteItem: ({ owner, ownerIdx }) => {
      if (!owner.retake) {
        return null;
      }
      owner.retake = null;
      return { kind: "clip", clipIdx: ownerIdx, stageIdx: 0 };
    },
    selectionFor: (clipIdx) => ({ kind: "retake", clipIdx })
  });

  // frontend/timelineViewState.ts
  var VIEW_STATE_KEY = "videostages.timeline.viewState";
  var loadViewState = () => {
    try {
      const raw = localStorage.getItem(VIEW_STATE_KEY);
      if (!raw) {
        return null;
      }
      const parsed = JSON.parse(raw);
      const state = {};
      if (typeof parsed.pxPerSecond === "number") {
        state.pxPerSecond = parsed.pxPerSecond;
      }
      if (parsed.unit === "frames" || parsed.unit === "seconds") {
        state.unit = parsed.unit;
      }
      return state;
    } catch {
      return null;
    }
  };
  var saveViewState = (state) => {
    try {
      localStorage.setItem(VIEW_STATE_KEY, JSON.stringify(state));
    } catch {
    }
  };

  // frontend/timelineViewport.ts
  var createTimelineViewport = (options) => {
    let currentUnit = "seconds";
    let currentPxPerSecond = DEFAULT_PX_PER_SECOND;
    let lastRenderedPxPerSecond = 0;
    const save = () => {
      saveViewState({
        pxPerSecond: currentPxPerSecond,
        unit: currentUnit
      });
    };
    const load = () => {
      const stored = loadViewState();
      if (!stored) return;
      if (stored.pxPerSecond !== void 0) {
        currentPxPerSecond = clampPxPerSecond(stored.pxPerSecond);
      }
      if (stored.unit) currentUnit = stored.unit;
    };
    const setZoom = (value) => {
      currentPxPerSecond = clampPxPerSecond(value);
      save();
      options.refresh();
    };
    const zoomWheel = (factor, clientX) => {
      const scroll = options.scrollElement();
      if (!scroll || currentPxPerSecond <= 0) {
        setZoom(currentPxPerSecond * factor);
        return;
      }
      const offsetX = clientX - scroll.getBoundingClientRect().left;
      const timeAtPointer = zoomAnchorTime(
        offsetX,
        scroll.scrollLeft,
        currentPxPerSecond
      );
      setZoom(currentPxPerSecond * factor);
      const fresh = options.scrollElement();
      if (fresh) {
        fresh.scrollLeft = zoomAnchorScrollLeft(
          timeAtPointer,
          currentPxPerSecond,
          offsetX
        );
      }
    };
    const restoreScroll = (previous) => {
      const fresh = options.scrollElement();
      if (fresh && previous.left > 0) {
        const target = lastRenderedPxPerSecond > 0 && lastRenderedPxPerSecond !== currentPxPerSecond ? zoomAnchorScrollLeft(
          zoomAnchorTime(
            TRACK_HEADER_W_PX,
            previous.left,
            lastRenderedPxPerSecond
          ),
          currentPxPerSecond,
          TRACK_HEADER_W_PX
        ) : previous.left;
        fresh.scrollLeft = target;
      }
      if (fresh && previous.top > 0) {
        fresh.scrollTop = previous.top;
      }
      lastRenderedPxPerSecond = currentPxPerSecond;
    };
    return {
      load,
      unit: () => currentUnit,
      pxPerSecond: () => currentPxPerSecond,
      toggleUnit: () => {
        currentUnit = currentUnit === "seconds" ? "frames" : "seconds";
        save();
        options.refresh();
      },
      zoomIn: () => setZoom(currentPxPerSecond * ZOOM_FACTOR),
      zoomOut: () => setZoom(currentPxPerSecond / ZOOM_FACTOR),
      zoomFit: () => {
        const width = options.scrollElement()?.clientWidth ?? options.timelineBody()?.clientWidth ?? 0;
        setZoom(
          computeFitPxPerSecond(
            options.totalSeconds(),
            width,
            TRACK_HEADER_W_PX + 24
          )
        );
      },
      setZoom,
      zoomWheel,
      restoreScroll
    };
  };

  // frontend/videoStagesTimeline.ts
  var videoStagesTimeline = () => {
    let storeUnsub = null;
    let selectionUnsub = null;
    const timelineBody = () => document.getElementById(TIMELINE_BODY_ID);
    const scrollEl = () => timelineBody()?.querySelector(".vst-scroll") ?? null;
    const viewport = createTimelineViewport({
      refresh: () => refresh(),
      totalSeconds: () => getClips().reduce(
        (sum, clip) => sum + Math.max(0, clip.duration || 0),
        0
      ),
      timelineBody,
      scrollElement: scrollEl
    });
    const detailStrip = createTimelineDetailStrip();
    const capabilities = currentCapabilityViewResolver;
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack(capabilities);
    const promptTrack = createTimelinePromptTrack(capabilities);
    const audioTrack = createTimelineAudioTrack();
    const audioSegmentTrack = createTimelineAudioSegmentTrack();
    const boundaryTrack = createTimelineBoundaryTrack();
    const referencesTrack = createTimelineReferencesTrack(capabilities);
    let addClipInFlight = false;
    const openSettings = () => {
      setSelection({ kind: "none" });
      detailStrip.render();
    };
    const history = createTimelineHistory({
      // The canonical model contains everything VideoStages authors across
      // Data, clip-prompt, and UI-state carriers, including hue and
      // prompt-window IDs.
      read: () => JSON.stringify(getState()),
      write: (value) => {
        const state = JSON.parse(value);
        const expectedRevision = getTimelineStore().revision();
        saveState(state, {
          expectedRevision,
          notifyDomChange: isVideoStagesEnabled(),
          origin: "history"
        });
      }
    });
    const hostLifecycle = createTimelineHostLifecycle({
      refresh: () => refresh(),
      refreshCatalog: () => {
        invalidateArchitectureCatalog();
        void adoptArchitectureCatalog();
      },
      syncFromCarrier: () => getTimelineStore().syncFromCarrier(),
      flushPending: () => detailStrip.flushPending(),
      undo: () => history.undo(),
      redo: () => history.redo()
    });
    const ensureDock = (body) => {
      const shell = body.parentElement;
      if (!shell) {
        throw new Error("timeline body has no shell parent");
      }
      let dock = shell.querySelector(":scope > .vst-detail");
      if (!dock) {
        dock = document.createElement("div");
        dock.className = "vst-detail";
        shell.insertBefore(dock, body);
      }
      return dock;
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
    const addClipAfterCatalog = async () => {
      try {
        await loadAuthoritativeArchitectureCatalog();
        const defaults = getRootDefaults();
        const defaultModel = getDefaultStageModel(defaults.modelValues);
        if (!defaultModel || architectureForModel(defaults.modelCatalog, defaultModel) === null) {
          getVideoStagesHostBridge().showError(
            "VideoStages cannot add a clip because no supported video model is available."
          );
          return;
        }
        const clips = getClips();
        const prev = clips[clips.length - 1] ?? null;
        if (prev && clips.length >= 2) {
          const prevJoin = clips[clips.length - 2];
          prev.boundaryOut = prevJoin.boundaryOut;
          prev.boundaryOutCarryAudio = prevJoin.boundaryOutCarryAudio;
          prev.boundaryOutOverlap = prevJoin.boundaryOutOverlap;
        }
        clips.push(
          buildDefaultClip(
            () => defaults,
            getDefaultStageModel,
            false,
            prev
          )
        );
        saveClips(clips, { origin: "timeline" });
      } catch (error) {
        console.warn("VideoStages: failed to add clip", error);
        getVideoStagesHostBridge().showError(
          "VideoStages could not add the clip. See the browser console for details."
        );
      } finally {
        addClipInFlight = false;
      }
    };
    const addClip = () => {
      if (addClipInFlight) {
        return;
      }
      addClipInFlight = true;
      void addClipAfterCatalog();
    };
    const renderAll = (meta) => {
      const enabled = isVideoStagesEnabled();
      updateTimelineTabIndicator(enabled);
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (!body) {
        return;
      }
      const previousScrollElement = scrollEl();
      const previousScroll = meta?.origin === "external" ? { left: 0, top: 0 } : {
        left: previousScrollElement?.scrollLeft ?? 0,
        top: previousScrollElement?.scrollTop ?? 0
      };
      try {
        const state = getState();
        const clips = state.clips;
        const globalPrompt = readGlobalPrompt();
        const architectureCatalog = getRootDefaults().modelCatalog;
        renderTimeline(body, clips, {
          fps: safeFps(state.fps),
          width: state.width,
          height: state.height,
          dimsExplicit: state.dimsExplicit,
          unit: viewport.unit(),
          pxPerSecond: viewport.pxPerSecond(),
          selectedIndex: linking.getSelectedIndex(),
          enabled,
          onToggleEnabled: setVideoStagesEnabled,
          onOpenSettings: () => openSettings(),
          onToggleUnit: viewport.toggleUnit,
          onAddClip: addClip,
          onZoomIn: viewport.zoomIn,
          onZoomOut: viewport.zoomOut,
          onZoomFit: viewport.zoomFit,
          onZoomSlider: viewport.setZoom,
          onZoomWheel: viewport.zoomWheel,
          onUndo: () => history.undo(),
          onRedo: () => history.redo(),
          globalPrompt,
          audioTracks: state.audioTracks,
          diagnostics: deriveAuthoringDiagnostics(clips, {
            catalog: architectureCatalog,
            generatedEntryMode: getRootGeneratedEntryMode()
          }),
          capabilities: capabilities()
        });
        viewport.restoreScroll(previousScroll);
        linking.reapplySelection(body, clips.length);
        detailStrip.render(meta);
        applySelectionHighlight(body);
      } catch (error) {
        console.warn("VideoStages: timeline render failed", error);
      }
    };
    const refresh = () => renderAll();
    const adoptArchitectureCatalog = () => loadAuthoritativeArchitectureCatalog().then((catalog) => {
      if (!catalog) {
        return;
      }
      getTimelineStore().invalidate();
      refresh();
    });
    const init = () => {
      viewport.load();
      injectTimelineTab();
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (body) {
        retakeTrack.attach(body, gestures);
        audioSegmentTrack.attach(body, gestures);
        linking.attach(body, gestures);
        promptTrack.attach(body, gestures);
        audioTrack.attach(body);
        boundaryTrack.attach(body);
        referencesTrack.attach(body, gestures);
        detailStrip.attach(body, ensureDock(body));
        gestures.attach(body);
        body.removeEventListener("click", onBodyClickSyncReadout);
        body.addEventListener("click", onBodyClickSyncReadout);
      }
      selectionUnsub?.();
      selectionUnsub = subscribeSelection(() => {
        const el = document.getElementById(TIMELINE_BODY_ID);
        if (el) {
          applySelectionHighlight(el);
        }
      });
      const store2 = getTimelineStore();
      store2.invalidate();
      storeUnsub?.();
      storeUnsub = store2.subscribe((_state, meta) => {
        history.capture();
        renderAll(meta);
      });
      history.rebase();
      hostLifecycle.bind();
      refresh();
      void adoptArchitectureCatalog();
    };
    const dispose = () => {
      hostLifecycle.dispose();
      retakeTrack.dispose();
      audioSegmentTrack.dispose();
      linking.dispose();
      promptTrack.dispose();
      gestures.dispose();
      audioTrack.dispose();
      boundaryTrack.dispose();
      referencesTrack.dispose();
      detailStrip.dispose();
      selectionUnsub?.();
      selectionUnsub = null;
      storeUnsub?.();
      storeUnsub = null;
      const body = document.getElementById(TIMELINE_BODY_ID);
      body?.removeEventListener("click", onBodyClickSyncReadout);
    };
    return { init, refresh, dispose };
  };

  // frontend/main.ts
  var timeline = videoStagesTimeline();
  var dataInputWatchdog = null;
  var warnIfDataInputNeverAppears = () => {
    if (dataInputWatchdog) {
      return;
    }
    dataInputWatchdog = setTimeout(() => {
      if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
        console.warn(
          `VideoStages: Data param input (#${DATA_INPUT_ID}) never appeared — is the VideoStages backend extension loaded?`
        );
      }
    }, 1e4);
  };
  var registerVideoStagesPromptPrefix = () => {
    getVideoStagesHostBridge().registerPromptPrefix(
      "videoclip",
      "Per-clip prompt sections and prompt windows for the VideoStages timeline.",
      () => [
        "\n<videoclip[0]>the first clip's prompt text — everything until the next <videoclip...> tag.",
        "\n<videoclip[0]:1.5-4>a prompt window on the first clip from 1.5s to 4s.",
        "\nThe timeline owns these; structured config (stages, refs, audio) rides in the hidden Data param."
      ],
      true
    );
  };
  var DATA_INPUT_RETRY_MS = 250;
  var dataInputRetryTimer = null;
  var initTimeline = () => {
    if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
      warnIfDataInputNeverAppears();
      if (!dataInputRetryTimer) {
        dataInputRetryTimer = setTimeout(() => {
          dataInputRetryTimer = null;
          initTimeline();
        }, DATA_INPUT_RETRY_MS);
      }
      return;
    }
    try {
      timeline.init();
    } catch (error) {
      console.warn("VideoStages: failed to init timeline", error);
    }
  };
  getVideoStagesHostBridge().addPostParamBuildStep(initTimeline);
  registerVideoStagesPromptPrefix();
  refineVideoButton();
  injectTimelineTab();
})();
//# sourceMappingURL=video-stages.js.map
