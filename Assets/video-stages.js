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
    spec.isPot ?? false,
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
    createInitVideoElement: () => document.createElement("video"),
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

  // frontend/clipSemantics.ts
  var activePrefix = (items) => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    return firstSkipped < 0 ? [...items] : items.slice(0, firstSkipped);
  };
  var applySkipSuffix = (items, fromIndex, skipped) => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    const start = skipped ? fromIndex : Math.max(0, firstSkipped);
    for (let index = start; index < items.length; index++) {
      items[index].skipped = skipped;
    }
  };
  var sealSkipSuffix = (items) => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    if (firstSkipped >= 0) {
      applySkipSuffix(items, firstSkipped, true);
    }
  };
  var activeStageCount = (clip) => {
    const firstSkipped = clip.stages.findIndex(
      (stage) => stage.skipped === true
    );
    return firstSkipped < 0 ? clip.stages.length : firstSkipped;
  };
  var isExecutableClip = (clip) => !clip.skipped && (clip.initVideo !== null || activeStageCount(clip) > 0);
  var executableClipIndexes = (clips) => activePrefix(clips).flatMap(
    (clip, index) => isExecutableClip(clip) ? [index] : []
  );
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

  // frontend/architectures/catalogQueries.ts
  var architectureDescriptor = (catalog, architectureId) => (architectureId ? catalog?.architectures.find((entry) => entry.id === architectureId) : null) ?? null;
  var modelCatalogEntry = (catalog, model) => (model ? catalog?.entries.find((entry) => entry.value === model) : null) ?? null;
  var supportedArchitectureCatalog = (catalog) => ({
    architectures: structuredClone(catalog.architectures),
    source: catalog.source,
    entries: catalog.entries.filter((entry) => entry.architectureId !== null)
  });
  var architectureForModel = (catalog, model) => modelCatalogEntry(catalog, model)?.architectureId ?? null;
  var modelProfileForModel = (catalog, model) => modelCatalogEntry(catalog, model)?.modelProfileId ?? null;
  var buildArchitectureRetargetPlan = (catalog, model) => {
    const entry = modelCatalogEntry(catalog, model);
    const architectureId = entry?.architectureId ?? null;
    const profileId = entry?.modelProfileId ?? null;
    return entry && architectureId && profileId ? { architectureId, modelProfileId: profileId, model } : null;
  };
  var entryModesForModel = (catalog, model) => modelCatalogEntry(catalog, model)?.entryModes ?? [];

  // frontend/architectures/none/identity.ts
  var NONE_ARCHITECTURE_ID = "none";

  // frontend/architectures/clipIdentity.ts
  var modelIdentityFromCatalog = (catalog, model) => {
    if (!catalog) return null;
    const entry = modelCatalogEntry(catalog, model);
    if (!entry?.architectureId || !entry.modelProfileId || !entry.compatibilityClassId) {
      return null;
    }
    return {
      architectureId: entry.architectureId,
      modelProfileId: entry.modelProfileId,
      compatibilityClassId: entry.compatibilityClassId
    };
  };
  var resolvedClipArchitectureId = (clip, catalog) => {
    if (clip.initVideo !== null && activeStageCount(clip) === 0) {
      return NONE_ARCHITECTURE_ID;
    }
    const stageZeroModel = clip.stages[0]?.model;
    return stageZeroModel ? modelIdentityFromCatalog(catalog, stageZeroModel)?.architectureId ?? null : null;
  };
  var deriveClipArchitectureIdentity = (clip, catalog) => {
    if (!catalog) return null;
    const identities = clip.stages.map((stage) => ({
      stage,
      identity: modelIdentityFromCatalog(catalog, stage.model)
    }));
    if (identities.some(({ identity }) => !identity)) {
      return null;
    }
    const authored = identities[0]?.identity ?? null;
    if (authored && identities.some(
      ({ identity }) => identity?.architectureId !== authored.architectureId || identity.compatibilityClassId !== authored.compatibilityClassId
    )) {
      return null;
    }
    const authoredIdentity = {
      authoredArchitectureId: authored?.architectureId ?? null,
      authoredModelProfileId: authored?.modelProfileId ?? null
    };
    if (clip.initVideo !== null && activeStageCount(clip) === 0) {
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
    if (clip.architectureHint === NONE_ARCHITECTURE_ID && clip.modelProfileId === NONE_ARCHITECTURE_ID) {
      return {
        architectureId: NONE_ARCHITECTURE_ID,
        modelProfileId: NONE_ARCHITECTURE_ID,
        ...authoredIdentity
      };
    }
    const validEmptyIdentity = catalog.architectures.some(
      (architecture) => architecture.id === clip.architectureHint
    );
    return validEmptyIdentity ? {
      architectureId: clip.architectureHint,
      modelProfileId: clip.modelProfileId,
      ...authoredIdentity
    } : null;
  };
  var reconcileClipArchitectureIdentity = (clip, catalog) => {
    const identity = deriveClipArchitectureIdentity(clip, catalog);
    if (!identity) return false;
    clip.architectureHint = identity.architectureId;
    clip.modelProfileId = identity.modelProfileId;
    return true;
  };

  // frontend/architectures/generatedFeatures.ts
  var ARCHITECTURE_FEATURE_LABELS = {
    promptRelay: "Prompt relay",
    frameReferences: "Frame references",
    clipReferences: "Clip references",
    referenceFraming: "Reference framing",
    retake: "Retake",
    audioBoundaryCarry: "Boundary audio carry",
    latentUpscale: "Latent interpolation upscaling",
    latentModelUpscale: "Latent-model upscaling",
    audioReuse: "Captured stage audio reuse",
    audioDerivedDuration: "Audio-derived clip duration",
    icLora: "IC-LoRA"
  };
  var ENTRY_MODES = [
    "text-to-video",
    "image-to-video",
    "init-video"
  ];
  var BOUNDARY_MODES = ["cut", "continue", "crossfade"];
  var RULE_SUPPORTS = [
    "supported",
    "unsupported",
    "conditional"
  ];
  var CONTINUE_MODES = ["overlap", "reference"];
  var FRAME_REFERENCE_POSITIONS = ["first", "last", "any"];
  var AUDIO_SOURCE_KINDS = [
    "Disabled",
    "Native",
    "Upload",
    "ControlNet",
    "AceStepFun"
  ];

  // frontend/generatedMediaSource.ts
  var MEDIA_SOURCE_UPLOAD = "Upload";
  var MEDIA_SOURCE_NATIVE = "Native";
  var MEDIA_SOURCE_INCOMING = "Incoming";
  var MEDIA_SOURCE_INCOMING_LEGACY = "Stage Input";
  var MEDIA_SOURCE_CONTROLNET = "ControlNet";
  var MEDIA_SOURCE_ACE_STEP_FUN = "AceStepFun";
  var MEDIA_SOURCE_BASE = "Base";
  var MEDIA_SOURCE_REFINER = "Refiner";
  var CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3"
  ];
  var MEDIA_SOURCE_ACE_STEP_FUN_PREFIX = "audio";
  var MEDIA_SOURCE_BASE_2_EDIT_PREFIX = "edit";

  // frontend/mediaSourceSyntax.ts
  var compactMediaSource = (value) => `${value ?? ""}`.trim().replaceAll(" ", "");
  var equalsMediaSource = (left, right) => left.toLowerCase() === right.toLowerCase();
  var INT_MAX = 2147483647;
  var parseIndexedMediaSource = (value, prefix) => {
    const text2 = compactMediaSource(value);
    if (!text2.toLowerCase().startsWith(prefix.toLowerCase())) {
      return null;
    }
    const rest = text2.slice(prefix.length);
    if (!/^\d+$/.test(rest)) {
      return null;
    }
    const index = Number(rest);
    return index <= INT_MAX ? index : null;
  };
  var parseAceStepFunIndex = (value) => parseIndexedMediaSource(value, MEDIA_SOURCE_ACE_STEP_FUN_PREFIX);
  var parseBase2EditStageIndex = (value) => parseIndexedMediaSource(value, MEDIA_SOURCE_BASE_2_EDIT_PREFIX);
  var canonicalControlNetSource = (value) => {
    const oneBased = parseIndexedMediaSource(value, MEDIA_SOURCE_CONTROLNET);
    return oneBased !== null && oneBased >= 1 && oneBased <= CONTROLNET_SOURCE_OPTIONS.length ? CONTROLNET_SOURCE_OPTIONS[oneBased - 1] : null;
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
  var [
    AUDIO_SOURCE_DISABLED_KIND,
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_CONTROLNET,
    AUDIO_SOURCE_ACE_STEP_FUN
  ] = AUDIO_SOURCE_KINDS;
  var isAceStepFunAudioSource = (source) => parseAceStepFunIndex(source) !== null;
  var LITERAL_AUDIO_SOURCES = [
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_CONTROLNET
  ];
  var canonicalAudioSource = (source) => {
    const normalized = `${source ?? ""}`.trim();
    if (!normalized) {
      return AUDIO_SOURCE_NATIVE;
    }
    return LITERAL_AUDIO_SOURCES.find(
      (kind) => equalsMediaSource(kind, normalized)
    ) ?? normalized;
  };
  var audioSourceKind = (source) => {
    const canonical = canonicalAudioSource(source);
    return isAceStepFunAudioSource(canonical) ? AUDIO_SOURCE_ACE_STEP_FUN : canonical;
  };
  var isAllowedAudioSource = (allowedKinds, source) => {
    const kind = audioSourceKind(source);
    return allowedKinds.includes(kind) || kind === AUDIO_SOURCE_NATIVE && allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND);
  };
  var defaultAuthoringAudioSource = (allowedKinds) => allowedKinds.includes(AUDIO_SOURCE_NATIVE) || allowedKinds.includes(AUDIO_SOURCE_DISABLED_KIND) ? AUDIO_SOURCE_NATIVE : allowedKinds[0] ?? AUDIO_SOURCE_NATIVE;
  var canUseClipLengthFromAudio = (source) => {
    const kind = audioSourceKind(source);
    return kind === AUDIO_SOURCE_UPLOAD || kind === AUDIO_SOURCE_CONTROLNET || kind === AUDIO_SOURCE_ACE_STEP_FUN;
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
    const index = parseAceStepFunIndex(ref);
    return index === null ? ref : `AceStepFun Audio ${index}`;
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
  var buildAudioTrackSourceOptions = (currentValue = "") => {
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

  // frontend/renderUtils.ts
  var escapeHtml = (value) => String(value ?? "").replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  var NEUTRAL_FRAME_GRID = {
    frameGrid: 1,
    frameGridOrigin: 1
  };
  var framesForClip = (durationSeconds, fps, spec) => {
    const rawFrameGrid = spec.frameGrid;
    const frameGrid = Number.isInteger(rawFrameGrid) && rawFrameGrid > 0 ? rawFrameGrid : 1;
    const rawOrigin = spec.frameGridOrigin;
    const gridOrigin = Number.isInteger(rawOrigin) && rawOrigin >= 1 && rawOrigin <= frameGrid ? rawOrigin : 1;
    const intervals = Math.max(
      0,
      Math.ceil(durationSeconds * Math.max(1, fps))
    );
    const beyondOrigin = Math.max(0, intervals + 1 - gridOrigin);
    return gridOrigin + Math.ceil(beyondOrigin / frameGrid) * frameGrid;
  };
  var snapDurationToFps = (seconds, fps) => {
    if (!Number.isFinite(seconds) || seconds <= 0 || !Number.isFinite(fps) || fps <= 0) {
      return seconds;
    }
    const frames = Math.max(1, Math.ceil(seconds * fps));
    const aligned = frames / fps;
    return Math.max(0.1, Math.floor(aligned * 10) / 10);
  };

  // frontend/architectures/modelCapabilities.ts
  var intersect = (left, right) => left.filter((value) => right.includes(value));
  var effectiveModelCapabilities = (model, architecture) => model?.capabilities ?? architecture.capabilities;
  var effectiveClipCapabilities = (clip, architecture, modelForName) => {
    const stages = clip.stages.slice(0, activeStageCount(clip));
    if (stages.length === 0) {
      return structuredClone(architecture.capabilities);
    }
    const models = stages.map((stage) => modelForName(stage.model));
    if (models.some(
      (model) => !model?.architectureId || model.architectureId !== architecture.id
    )) {
      return null;
    }
    return models.reduce((effective, model) => {
      const capabilities = effectiveModelCapabilities(model, architecture);
      return {
        features: intersect(effective.features, capabilities.features),
        entryModes: intersect(
          effective.entryModes,
          capabilities.entryModes
        ),
        audioSourceKinds: intersect(
          effective.audioSourceKinds,
          capabilities.audioSourceKinds
        )
      };
    }, structuredClone(architecture.capabilities));
  };

  // frontend/generatedPlanDiagnostics.ts
  var PLAN_DIAGNOSTIC_RETAKE_SOURCE_REQUIRED = "retake-source-required";

  // frontend/architectures/policy/featureValues.ts
  var RETAKE_SOURCE_RULE = {
    code: PLAN_DIAGNOSTIC_RETAKE_SOURCE_REQUIRED,
    reason: "Retake requires an init-video clip."
  };
  var supportsClipAudio = (audioSourceKinds) => audioSourceKinds.some(
    (kind) => kind !== AUDIO_SOURCE_DISABLED_KIND && kind !== AUDIO_SOURCE_NATIVE
  );
  var architectureReason = (label, feature) => `${ARCHITECTURE_FEATURE_LABELS[feature]} is not supported by ${label}.`;
  var noArchitectureReason = (feature) => `${ARCHITECTURE_FEATURE_LABELS[feature]} requires a generated clip with a known architecture.`;
  var upscaleModeForMethod = (method) => {
    const normalized = method.trim().toLowerCase();
    const hasMethodName = (prefix) => normalized.startsWith(prefix) && normalized.slice(prefix.length).trim().length > 0;
    if (hasMethodName("latentmodel-")) return "latent-model";
    if (hasMethodName("latent-")) return "latent";
    if (hasMethodName("pixel-")) return "pixel";
    if (hasMethodName("model-")) return "model";
    return "unsupported";
  };
  var architectureFeatureSupport = (feature, capabilities) => capabilities.features.includes(feature);

  // frontend/architectures/temporalGrid.ts
  var MAX_FRAME_GRID = 2147483647;
  var greatestCommonDivisor = (left, right) => {
    while (right !== 0) {
      [left, right] = [right, left % right];
    }
    return left;
  };
  var resolveCompatibleFrameGrid = (specs) => {
    let compatible = 1;
    let origin = null;
    for (const raw of specs) {
      const grid = Number(raw.frameGrid);
      const gridOrigin = Number(raw.frameGridOrigin);
      if (!Number.isInteger(grid) || grid < 1 || grid > MAX_FRAME_GRID || !Number.isInteger(gridOrigin) || gridOrigin < 1 || gridOrigin > grid || origin !== null && origin !== gridOrigin) {
        return { status: "conflict" };
      }
      origin = gridOrigin;
      const next = compatible / greatestCommonDivisor(compatible, grid) * grid;
      if (!Number.isSafeInteger(next) || next > MAX_FRAME_GRID) {
        return { status: "conflict" };
      }
      compatible = next;
    }
    return {
      status: "resolved",
      frameGrid: compatible,
      frameGridOrigin: origin ?? 1
    };
  };
  var resolveFrameGridForModelLookup = (models, frameGridForModel) => {
    if (models.length === 0) {
      return { status: "not-applicable" };
    }
    const specs = models.map(frameGridForModel);
    return specs.some((spec) => spec === null) ? { status: "unknown" } : resolveCompatibleFrameGrid(specs);
  };
  var effectiveGridModels = (clip, modelForName, architectureForId) => {
    const stages = clip.stages.slice(0, activeStageCount(clip));
    if (stages.length === 0) {
      return [];
    }
    const firstModel = modelForName(stages[0].model);
    const clipDescriptor = firstModel?.architectureId ? architectureForId(firstModel.architectureId) : void 0;
    const clipCapabilities = clipDescriptor ? effectiveClipCapabilities(clip, clipDescriptor, modelForName) : null;
    const retakeCanExecute = clip.retake !== null && clip.retake !== void 0 && clip.initVideo != null && (!clipDescriptor || !clipCapabilities || architectureFeatureSupport("retake", clipCapabilities));
    return stages.filter((stage, stageIndex) => {
      if (stageIndex === 0 && clip.initVideo == null) {
        return true;
      }
      if (stage.control > 0 || stageIndex === stages.length - 1 && retakeCanExecute) {
        return true;
      }
      const upscaleMode = upscaleModeForMethod(stage.upscaleMethod ?? "");
      if ((stage.upscale ?? 1) === 1 || upscaleMode !== "latent" && upscaleMode !== "latent-model") {
        return false;
      }
      return clipCapabilities === null || architectureFeatureSupport(
        upscaleMode === "latent" ? "latentUpscale" : "latentModelUpscale",
        clipCapabilities
      );
    }).map((stage) => stage.model);
  };
  var clipHasGenerationStageForLookup = (clip, modelForName, architectureForId) => effectiveGridModels(clip, modelForName, architectureForId).length > 0;
  var resolveClipFrameGridForLookup = (clip, modelForName, architectureForId) => {
    const activeStages = clip.stages.slice(0, activeStageCount(clip));
    if (activeStages.length === 0) {
      return { status: "not-applicable" };
    }
    const resolvedAuthoredModels = clip.stages.map(
      (stage) => modelForName(stage.model)
    );
    if (resolvedAuthoredModels.some(
      (model) => !model?.architectureId || !model.modelProfileId || !model.compatibilityClassId
    )) {
      return { status: "unknown" };
    }
    const firstModel = resolvedAuthoredModels[0];
    const descriptor = architectureForId(firstModel.architectureId);
    if (!descriptor || resolvedAuthoredModels.some(
      (model) => model?.architectureId !== firstModel.architectureId || model.compatibilityClassId !== firstModel.compatibilityClassId
    ) || activeStages.some(
      (stage) => (stage.upscale ?? 1) !== 1 && upscaleModeForMethod(stage.upscaleMethod ?? "") === "unsupported"
    )) {
      return { status: "unknown" };
    }
    const capabilities = effectiveClipCapabilities(
      clip,
      descriptor,
      modelForName
    );
    if (!capabilities) {
      return { status: "unknown" };
    }
    if (clip.clipLengthFromAudio === true && canUseClipLengthFromAudio(clip.audioSource ?? "") && isAllowedAudioSource(
      capabilities.audioSourceKinds,
      clip.audioSource ?? ""
    ) && architectureFeatureSupport("audioDerivedDuration", capabilities) || clip.clipLengthFromControlNet === true && architectureFeatureSupport("icLora", capabilities)) {
      return { status: "not-applicable" };
    }
    const models = effectiveGridModels(clip, modelForName, architectureForId);
    return resolveFrameGridForModelLookup(models, (model) => {
      const entry = modelForName(model);
      return entry?.frameGrid == null || entry.frameGridOrigin == null ? null : {
        frameGrid: entry.frameGrid,
        frameGridOrigin: entry.frameGridOrigin
      };
    });
  };
  var resolveClipFrameGrid = (clip, catalog) => {
    if (!catalog) {
      const activeCount = activeStageCount(clip);
      return activeCount === 0 ? { status: "not-applicable" } : { status: "unknown" };
    }
    return resolveClipFrameGridForLookup(
      clip,
      (model) => modelCatalogEntry(catalog, model) ?? void 0,
      (architectureId) => architectureDescriptor(catalog, architectureId) ?? void 0
    );
  };
  var resolvedClipFrameGrid = (clip, catalog) => {
    const resolution = resolveClipFrameGrid(clip, catalog);
    return resolution.status === "resolved" ? {
      frameGrid: resolution.frameGrid,
      frameGridOrigin: resolution.frameGridOrigin
    } : NEUTRAL_FRAME_GRID;
  };

  // frontend/architectures/catalogWire.ts
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var isTrimmedNonEmpty = (value) => typeof value === "string" && value.length > 0 && value === value.trim();
  var isUniqueStringArray = (value) => Array.isArray(value) && value.every((entry) => isTrimmedNonEmpty(entry)) && new Set(value).size === value.length;
  var isEntryModeArray = (value) => isUniqueStringArray(value) && value.every((entry) => ENTRY_MODES.includes(entry));
  var isAudioSourceKindArray = (value) => isUniqueStringArray(value) && value.every(
    (entry) => AUDIO_SOURCE_KINDS.includes(entry)
  );
  var isFrameReferencePositionArray = (value) => isUniqueStringArray(value) && value.every(
    (entry) => FRAME_REFERENCE_POSITIONS.includes(entry)
  );
  var hasExactKeys = (value, expected) => Object.keys(value).length === expected.length && expected.every((key) => Object.hasOwn(value, key));
  var isCapabilitySupport = (value) => typeof value === "string" && RULE_SUPPORTS.includes(value);
  var isRuleDecision = (value) => isRecord(value) && hasExactKeys(value, ["support", "code", "reason", "constraints"]) && isCapabilitySupport(value.support) && isTrimmedNonEmpty(value.code) && isTrimmedNonEmpty(value.reason) && (value.constraints === null || isRecord(value.constraints) && value.support !== "unsupported");
  var isBoundaryRule = (value) => {
    if (!isRuleDecision(value)) {
      return false;
    }
    if (value.support !== "conditional") {
      return value.constraints === null;
    }
    if (!isRecord(value.constraints)) {
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
    if (!hasExactKeys(constraints, [
      "sameArchitecture",
      "frameStep",
      "minFrames",
      "maxFrames",
      "defaultFrames",
      "continuityExtraFrames",
      "continueMode",
      "targetRequiresGeneratedEntry",
      "targetRequiresStage",
      "targetDisallowsInitialReference"
    ]) || constraints.sameArchitecture !== true || !CONTINUE_MODES.includes(
      constraints.continueMode
    ) || typeof constraints.targetRequiresGeneratedEntry !== "boolean" || typeof constraints.targetRequiresStage !== "boolean" || typeof constraints.targetDisallowsInitialReference !== "boolean" || !integers.every(Number.isInteger)) {
      return false;
    }
    const frameStep = constraints.frameStep;
    const minFrames = constraints.minFrames;
    const maxFrames = constraints.maxFrames;
    const defaultFrames = constraints.defaultFrames;
    const continuityExtraFrames = constraints.continuityExtraFrames;
    return frameStep > 0 && minFrames >= 0 && maxFrames >= minFrames && defaultFrames >= minFrames && defaultFrames <= maxFrames && continuityExtraFrames >= 0 && (defaultFrames - minFrames) % frameStep === 0;
  };
  var isCapabilities = (value) => {
    if (!isRecord(value) || !hasExactKeys(value, ["features", "entryModes", "audioSourceKinds"])) {
      return false;
    }
    return isUniqueStringArray(value.features) && isAudioSourceKindArray(value.audioSourceKinds) && isEntryModeArray(value.entryModes);
  };
  var hasCompleteBoundaryRules = (value) => {
    if (!isRecord(value)) {
      return false;
    }
    const keys = Object.keys(value);
    return keys.length === BOUNDARY_MODES.length && BOUNDARY_MODES.every((mode) => isBoundaryRule(value[mode]));
  };
  var parseVideoArchitectureCatalog = (value) => {
    if (!isRecord(value) || !hasExactKeys(value, ["schemaVersion", "architectures", "models"]) || value.schemaVersion !== 2 || !Array.isArray(value.architectures) || !Array.isArray(value.models)) {
      return null;
    }
    const architectures = [];
    const architectureIds = /* @__PURE__ */ new Set();
    for (const raw of value.architectures) {
      if (!isRecord(raw) || !hasExactKeys(raw, [
        "id",
        "label",
        "capabilities",
        "boundaryRules"
      ]) || !isTrimmedNonEmpty(raw.id) || !isTrimmedNonEmpty(raw.label) || !isCapabilities(raw.capabilities) || !hasCompleteBoundaryRules(raw.boundaryRules)) {
        return null;
      }
      const boundaryCodes = Object.values(raw.boundaryRules).map(
        (rule) => rule.code
      );
      if (architectureIds.has(raw.id) || new Set(boundaryCodes).size !== boundaryCodes.length) {
        return null;
      }
      architectureIds.add(raw.id);
      architectures.push({
        id: raw.id,
        label: raw.label,
        capabilities: structuredClone(raw.capabilities),
        boundaryRules: structuredClone(raw.boundaryRules)
      });
    }
    if (architectures.length === 0) {
      return null;
    }
    const modelNames = /* @__PURE__ */ new Set();
    const models = [];
    for (const raw of value.models) {
      if (!isRecord(raw) || !hasExactKeys(raw, [
        "modelName",
        "architectureId",
        "modelProfileId",
        "modelClassId",
        "compatibilityClassId",
        "frameGrid",
        "frameGridOrigin",
        "capabilities",
        "enhancements"
      ]) || !isTrimmedNonEmpty(raw.modelName) || !isTrimmedNonEmpty(raw.architectureId) || !architectureIds.has(raw.architectureId) || !isTrimmedNonEmpty(raw.modelProfileId) || !isTrimmedNonEmpty(raw.modelClassId) || !isTrimmedNonEmpty(raw.compatibilityClassId) || !Number.isSafeInteger(raw.frameGrid) || Number(raw.frameGrid) < 1 || Number(raw.frameGrid) > MAX_FRAME_GRID || !Number.isSafeInteger(raw.frameGridOrigin) || Number(raw.frameGridOrigin) < 1 || Number(raw.frameGridOrigin) > Number(raw.frameGrid) || !isCapabilities(raw.capabilities) || !isRecord(raw.enhancements) || !hasExactKeys(raw.enhancements, ["referencePositions"]) || !isFrameReferencePositionArray(raw.enhancements.referencePositions)) {
        return null;
      }
      if (modelNames.has(raw.modelName)) {
        return null;
      }
      modelNames.add(raw.modelName);
      models.push({
        modelName: raw.modelName,
        architectureId: raw.architectureId,
        modelProfileId: raw.modelProfileId,
        modelClassId: raw.modelClassId,
        compatibilityClassId: raw.compatibilityClassId,
        frameGrid: Number(raw.frameGrid),
        frameGridOrigin: Number(raw.frameGridOrigin),
        capabilities: structuredClone(raw.capabilities),
        enhancements: {
          referencePositions: [...raw.enhancements.referencePositions]
        }
      });
    }
    return { schemaVersion: 2, architectures, models };
  };

  // frontend/architectures/catalogRepository.ts
  var ARCHITECTURE_CATALOG_API = "VideoStagesGetArchitectureCatalog";
  var authoritativeCatalog = null;
  var snapshotStatus = "loading";
  var snapshotError = null;
  var requestGeneration = 0;
  var activeRequest = null;
  var pendingRefresh = null;
  var subscribers = /* @__PURE__ */ new Set();
  var cloneCatalog = (catalog) => structuredClone(catalog);
  var errorMessage = (error) => error instanceof Error ? error.message : `${error}`;
  var getArchitectureCatalogSnapshot = () => ({
    status: snapshotStatus,
    catalog: authoritativeCatalog ? cloneCatalog(authoritativeCatalog) : null,
    error: snapshotError
  });
  var notifySubscribers = () => {
    for (const subscriber of subscribers) {
      subscriber(getArchitectureCatalogSnapshot());
    }
  };
  var subscribeArchitectureCatalog = (subscriber) => {
    subscribers.add(subscriber);
    return () => {
      subscribers.delete(subscriber);
    };
  };
  var requestAuthoritativeCatalog = () => {
    if (activeRequest) {
      return activeRequest;
    }
    const generation = ++requestGeneration;
    const owned = () => requestGeneration === generation;
    snapshotStatus = authoritativeCatalog ? "refreshing" : "loading";
    snapshotError = null;
    const request = Promise.resolve().then(
      () => getVideoStagesHostBridge().requestJson(ARCHITECTURE_CATALOG_API)
    ).then((response) => {
      const parsed = parseVideoArchitectureCatalog(response);
      if (!parsed) {
        throw new Error(
          "The architecture catalog response was malformed."
        );
      }
      if (!owned()) {
        return null;
      }
      authoritativeCatalog = parsed;
      snapshotStatus = "ready";
      snapshotError = null;
      notifySubscribers();
      return cloneCatalog(parsed);
    }).catch((error) => {
      if (!owned()) {
        return null;
      }
      snapshotStatus = authoritativeCatalog ? "stale" : "unavailable";
      snapshotError = errorMessage(error);
      notifySubscribers();
      console.warn(
        "VideoStages: authoritative architecture catalog unavailable",
        error
      );
      return null;
    }).finally(() => {
      if (owned()) {
        activeRequest = null;
      }
    });
    activeRequest = request;
    notifySubscribers();
    return request;
  };
  var loadAuthoritativeArchitectureCatalog = () => {
    if (activeRequest) {
      return activeRequest;
    }
    if (authoritativeCatalog) {
      return Promise.resolve(cloneCatalog(authoritativeCatalog));
    }
    return requestAuthoritativeCatalog();
  };
  var refreshAuthoritativeArchitectureCatalog = () => {
    if (!activeRequest) {
      return requestAuthoritativeCatalog();
    }
    if (pendingRefresh) {
      return pendingRefresh;
    }
    const generation = requestGeneration;
    const refresh = activeRequest.then(() => {
      if (pendingRefresh === refresh) {
        pendingRefresh = null;
      }
      return requestGeneration === generation ? requestAuthoritativeCatalog() : null;
    });
    pendingRefresh = refresh;
    return refresh;
  };
  var buildArchitectureModelCatalog = (values, labels, catalog = authoritativeCatalog) => {
    const backend = catalog;
    const hostLabels = /* @__PURE__ */ new Map();
    const modelNames = [];
    const seen = /* @__PURE__ */ new Set();
    values.forEach((value, index) => {
      if (!seen.has(value)) {
        seen.add(value);
        modelNames.push(value);
      }
      hostLabels.set(value, labels[index] ?? value);
    });
    for (const model of backend?.models ?? []) {
      if (!seen.has(model.modelName)) {
        seen.add(model.modelName);
        modelNames.push(model.modelName);
      }
    }
    const backendModels = new Map(
      backend?.models.map((model) => [model.modelName, model]) ?? []
    );
    return {
      architectures: backend ? structuredClone(backend.architectures) : [],
      source: backend ? "backend" : "unavailable",
      entries: modelNames.map((value) => {
        const backendModel = backendModels.get(value);
        return {
          value,
          label: hostLabels.get(value) ?? value,
          architectureId: backendModel?.architectureId ?? null,
          modelProfileId: backendModel?.modelProfileId ?? null,
          modelClassId: backendModel?.modelClassId ?? null,
          compatibilityClassId: backendModel?.compatibilityClassId ?? null,
          frameGrid: backendModel?.frameGrid ?? null,
          frameGridOrigin: backendModel?.frameGridOrigin ?? null,
          ...backendModel?.capabilities === void 0 ? {} : {
            capabilities: structuredClone(
              backendModel.capabilities
            )
          },
          ...backendModel?.enhancements === void 0 ? {} : {
            enhancements: structuredClone(
              backendModel.enhancements
            )
          },
          entryModes: [...backendModel?.capabilities.entryModes ?? []]
        };
      })
    };
  };

  // frontend/architectures/boundaryConstraints.ts
  var GENERIC_BOUNDARY_CONSTRAINTS = {
    frameStep: 1,
    minFrames: 1,
    maxFrames: Number.MAX_SAFE_INTEGER,
    defaultFrames: 1,
    continuityExtraFrames: 0,
    continueMode: "overlap"
  };
  var asContinueMode = (value) => CONTINUE_MODES.find((mode) => mode === value) ?? GENERIC_BOUNDARY_CONSTRAINTS.continueMode;
  var finitePositive = (value, fallback, allowZero = false) => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && (allowZero ? numeric >= 0 : numeric > 0) ? numeric : fallback;
  };
  var boundaryWindowConstraints = (rule) => {
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
      ),
      continueMode: asContinueMode(raw?.continueMode)
    };
  };
  var normalizeBoundaryWindow = (value, constraints) => {
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
  var boundaryWindowChoices = (constraints) => {
    const choices = [];
    for (let value = constraints.minFrames; value <= constraints.maxFrames; value += constraints.frameStep) {
      choices.push(value);
      if (choices.length >= 100) break;
    }
    return choices;
  };

  // frontend/architectures/policy/boundaryPolicy.ts
  var forceCrossArchitectureCutsForConversion = (clips, catalog) => {
    for (const boundary of executableBoundaries(clips)) {
      const left = clips[boundary.leftIdx];
      const right = clips[boundary.rightIdx];
      const leftArchitectureId = resolvedClipArchitectureId(left, catalog);
      const rightArchitectureId = resolvedClipArchitectureId(right, catalog);
      if (leftArchitectureId !== null && rightArchitectureId !== null && leftArchitectureId !== rightArchitectureId) {
        left.boundaryOut = "cut";
      }
    }
  };
  var createBoundaryCapabilityViews = (architectureById, forClip) => {
    const byTimeline = /* @__PURE__ */ new WeakMap();
    const forBoundary = (left, right, leftClipIdx = -1, rightClipIdx = null) => {
      const leftView = forClip(left);
      const rightView = right === null ? null : forClip(right);
      const leftDescriptor = architectureById.get(leftView.architectureId);
      const crossArchitecture = rightView !== null && leftView.architectureId !== rightView.architectureId;
      const hasInitialReference = right?.frameRefs.some(
        (reference) => reference.fromEnd !== true && Math.max(1, Math.round(reference.frame)) === 1
      ) ?? false;
      const rightHasGenerationStage = rightView?.hasGenerationStage === true;
      const supportsMode = (mode) => {
        const rule = leftDescriptor?.boundaryRules[mode];
        if (!rule || rule.support === "unsupported") {
          return mode === "cut" && !leftDescriptor;
        }
        const constraints = rule.constraints;
        if (constraints?.sameArchitecture === true && crossArchitecture) {
          return false;
        }
        if (constraints?.targetRequiresGeneratedEntry === true && right?.initVideo !== null) {
          return false;
        }
        if (constraints?.targetRequiresStage === true && right !== null && !rightHasGenerationStage) {
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
        windowConstraints: (mode) => boundaryWindowConstraints(
          leftDescriptor?.boundaryRules[mode] ?? null
        ),
        effective: (requested) => supportsMode(requested) ? requested : "cut"
      };
    };
    const forBoundaryIndex = (clips, leftClipIdx) => {
      let timelineViews = byTimeline.get(clips);
      if (!timelineViews) {
        timelineViews = /* @__PURE__ */ new Map();
        byTimeline.set(clips, timelineViews);
      }
      const cached = timelineViews.get(leftClipIdx);
      if (cached) {
        return cached;
      }
      const left = clips[leftClipIdx];
      if (!left) {
        throw new Error(`Missing left clip at index ${leftClipIdx}.`);
      }
      const rightClipIdx = executableBoundaryForLeftClip(clips, leftClipIdx)?.rightIdx ?? null;
      const view = forBoundary(
        left,
        rightClipIdx === null ? null : clips[rightClipIdx],
        leftClipIdx,
        rightClipIdx
      );
      timelineViews.set(leftClipIdx, view);
      return view;
    };
    return {
      forBoundary,
      forBoundaryIndex
    };
  };

  // frontend/architectures/policy/clipStageViews.ts
  var UNRESOLVED_ARCHITECTURE_ID = "unsupported";
  var createClipStageCapabilityViews = (architectureById, modelByName) => {
    const clipViews = /* @__PURE__ */ new WeakMap();
    const stageViews = /* @__PURE__ */ new WeakMap();
    const effectiveClipIdentity = (clip) => {
      const sourceOnly = activeStageCount(clip) === 0 && clip.initVideo !== null;
      const resolvedModel = sourceOnly ? void 0 : modelByName.get(clip.stages[0]?.model ?? "");
      const architectureId = sourceOnly ? NONE_ARCHITECTURE_ID : resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID;
      return {
        architectureId,
        descriptor: architectureById.get(architectureId)
      };
    };
    const forClip = (clip) => {
      const cached = clipViews.get(clip);
      if (cached) {
        return cached;
      }
      const identity = effectiveClipIdentity(clip);
      const { architectureId, descriptor } = identity;
      const capabilities = descriptor ? effectiveClipCapabilities(
        clip,
        descriptor,
        (model) => modelByName.get(model)
      ) : null;
      const label = descriptor?.label ?? (architectureId === NONE_ARCHITECTURE_ID ? "source-only clips" : `unknown architecture '${architectureId}'`);
      const decision = (feature) => {
        if (!descriptor || !capabilities) {
          return {
            supported: false,
            reason: noArchitectureReason(feature),
            code: ""
          };
        }
        const featureSupported = architectureFeatureSupport(
          feature,
          capabilities
        );
        const needsRetakeSource = feature === "retake" && featureSupported && clip.initVideo === null;
        return {
          supported: featureSupported && !needsRetakeSource,
          reason: needsRetakeSource ? RETAKE_SOURCE_RULE.reason : featureSupported ? "" : architectureReason(label, feature),
          code: needsRetakeSource ? RETAKE_SOURCE_RULE.code : ""
        };
      };
      const frameGridResolution = resolveClipFrameGridForLookup(
        clip,
        (model) => modelByName.get(model),
        (architectureId2) => architectureById.get(architectureId2)
      );
      const hasGenerationStage = clipHasGenerationStageForLookup(
        clip,
        (model) => modelByName.get(model),
        (architectureId2) => architectureById.get(architectureId2)
      );
      const view = {
        architectureId,
        architectureLabel: label,
        known: descriptor !== void 0,
        frameGrid: frameGridResolution.status === "resolved" ? {
          frameGrid: frameGridResolution.frameGrid,
          frameGridOrigin: frameGridResolution.frameGridOrigin
        } : NEUTRAL_FRAME_GRID,
        frameGridResolution,
        hasGenerationStage,
        audioSourceKinds: capabilities?.audioSourceKinds ?? [],
        clipAudio: {
          supported: supportsClipAudio(
            capabilities?.audioSourceKinds ?? []
          ),
          reason: supportsClipAudio(capabilities?.audioSourceKinds ?? []) ? "" : `Clip audio is not supported by ${label}.`,
          code: ""
        },
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
      clipViews.set(clip, view);
      return view;
    };
    const forStage = (clip, stage) => {
      let viewsForClip = stageViews.get(clip);
      if (!viewsForClip) {
        viewsForClip = /* @__PURE__ */ new WeakMap();
        stageViews.set(clip, viewsForClip);
      }
      const cached = viewsForClip.get(stage);
      if (cached) {
        return cached;
      }
      const view = forClip(clip);
      const sourceOnly = view.architectureId === NONE_ARCHITECTURE_ID && activeStageCount(clip) === 0 && clip.initVideo !== null;
      const resolvedModel = sourceOnly ? void 0 : modelByName.get(stage.model);
      const architectureId = sourceOnly ? NONE_ARCHITECTURE_ID : resolvedModel?.architectureId ?? UNRESOLVED_ARCHITECTURE_ID;
      const descriptor = architectureById.get(architectureId);
      const decision = (feature) => {
        if (feature === "sampler" || feature === "scheduler") {
          const supported = descriptor !== void 0 && resolvedModel !== void 0 && resolvedModel.entryModes.length > 0;
          return {
            supported,
            reason: supported ? "" : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection requires a resolved generating video model.`,
            code: ""
          };
        }
        return { supported: true, reason: "", code: "" };
      };
      const stageView = {
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
      viewsForClip.set(stage, stageView);
      return stageView;
    };
    return { forClip, forStage };
  };

  // frontend/architectures/policy.ts
  var createCapabilityViewResolver = (catalog) => {
    const architectureById = new Map(
      catalog.architectures.map((entry) => [entry.id, entry])
    );
    const modelByName = new Map(
      catalog.entries.map((entry) => [entry.value, entry])
    );
    const clipStage = createClipStageCapabilityViews(
      architectureById,
      modelByName
    );
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
  var AUDIO_SPAN_MIN_LENGTH = 0.1;
  var AUDIO_SPAN_DEFAULT_LENGTH = 2;
  var AUDIO_SPAN_STEP = 0.1;
  var AUDIO_SPAN_VOLUME_MIN = 1e-5;
  var AUDIO_SPAN_VOLUME_MAX = 1e5;
  var AUDIO_SPAN_VOLUME_SLIDER_MIN = 0.1;
  var AUDIO_SPAN_VOLUME_SLIDER_MAX = 4;
  var AUDIO_SPAN_VOLUME_SLIDER_STEP = 0.1;
  var AUDIO_SPAN_VOLUME_DEFAULT = 1;
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

  // frontend/dimensionSnap.ts
  var AREA_DRIFT_WEIGHT = 0.15;
  var SCORE_EPSILON = 1e-12;
  var clampToSnapRange = (value, multiple) => Math.min(ROOT_DIMENSION_MAX, Math.max(multiple, value));
  var snapDimensions = (width, height, multiple = ROOT_DIMENSION_STEP) => {
    const requestedWidth = Math.max(1, Number.isFinite(width) ? width : 1);
    const requestedHeight = Math.max(1, Number.isFinite(height) ? height : 1);
    const grid = Math.min(
      ROOT_DIMENSION_MAX,
      Math.max(
        ROOT_DIMENSION_STEP,
        Math.round(Number.isFinite(multiple) ? multiple : 0)
      )
    );
    const floorWidth = clampToSnapRange(
      Math.floor(requestedWidth / grid) * grid,
      grid
    );
    const ceilWidth = clampToSnapRange(
      Math.ceil(requestedWidth / grid) * grid,
      grid
    );
    const floorHeight = clampToSnapRange(
      Math.floor(requestedHeight / grid) * grid,
      grid
    );
    const ceilHeight = clampToSnapRange(
      Math.ceil(requestedHeight / grid) * grid,
      grid
    );
    const candidates = [
      { width: floorWidth, height: floorHeight },
      { width: ceilWidth, height: floorHeight },
      { width: floorWidth, height: ceilHeight },
      { width: ceilWidth, height: ceilHeight }
    ];
    const targetAspect = requestedWidth / requestedHeight;
    const targetArea = requestedWidth * requestedHeight;
    const score = (candidate) => Math.abs(
      Math.log(candidate.width / candidate.height) - Math.log(targetAspect)
    ) + AREA_DRIFT_WEIGHT * Math.abs(
      Math.log(candidate.width * candidate.height / targetArea)
    );
    let best = candidates[0];
    let bestScore = score(best);
    for (const candidate of candidates.slice(1)) {
      const candidateScore = score(candidate);
      const candidateArea = candidate.width * candidate.height;
      const bestArea = best.width * best.height;
      if (candidateScore < bestScore - SCORE_EPSILON || Math.abs(candidateScore - bestScore) <= SCORE_EPSILON && candidateArea > bestArea) {
        best = candidate;
        bestScore = candidateScore;
      }
    }
    return best;
  };

  // frontend/dimensionPresets.ts
  var ASPECT_RATIOS = [
    {
      id: "1:1",
      label: "1:1 (Square)",
      reference: { width: 512, height: 512 }
    },
    {
      id: "4:3",
      label: "4:3 (Old PC)",
      reference: { width: 576, height: 448 }
    },
    {
      id: "3:2",
      label: "3:2 (Semi-wide)",
      reference: { width: 608, height: 416 }
    },
    {
      id: "8:5",
      label: "8:5",
      reference: { width: 608, height: 384 }
    },
    {
      id: "16:9",
      label: "16:9 (Standard Widescreen)",
      reference: { width: 672, height: 384 }
    },
    {
      id: "21:9",
      label: "21:9 (Ultra-Widescreen)",
      reference: { width: 768, height: 320 }
    },
    { id: "3:4", label: "3:4", reference: null },
    {
      id: "2:3",
      label: "2:3 (Semi-tall)",
      reference: { width: 416, height: 608 }
    },
    {
      id: "5:8",
      label: "5:8",
      reference: { width: 384, height: 608 }
    },
    {
      id: "9:16",
      label: "9:16 (Tall)",
      reference: { width: 384, height: 672 }
    },
    {
      id: "9:21",
      label: "9:21 (Ultra-Tall)",
      reference: { width: 320, height: 768 }
    }
  ];
  var roundHalfToEven = (value) => {
    const floor = Math.floor(value);
    const fraction = value - floor;
    if (Math.abs(fraction - 0.5) <= Number.EPSILON * Math.abs(value) * 2) {
      return floor % 2 === 0 ? floor : floor + 1;
    }
    return Math.round(value);
  };
  var dimensionsFor = (ratioId, sideLength) => {
    const ratio = ASPECT_RATIOS.find((candidate) => candidate.id === ratioId);
    if (!ratio?.reference) {
      return null;
    }
    const side = Math.max(1, Number.isFinite(sideLength) ? sideLength : 1);
    return {
      width: roundHalfToEven(ratio.reference.width * side / 512 / 16) * 16,
      height: roundHalfToEven(ratio.reference.height * side / 512 / 16) * 16
    };
  };
  var declaredRatioMatches = (ratioId, width, height) => {
    const [numerator, denominator] = ratioId.split(":").map(Number);
    return Number.isFinite(numerator) && Number.isFinite(denominator) && Math.abs(width * denominator - height * numerator) < 0.5;
  };
  var sideLengthForDimensions = (ratioId, width, height) => {
    const ratio = ASPECT_RATIOS.find((candidate) => candidate.id === ratioId);
    if (!ratio?.reference) {
      return Math.min(
        ROOT_DIMENSION_MAX,
        Math.max(
          ROOT_DIMENSION_MIN,
          Math.round(Math.sqrt(width * height) / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP
        )
      );
    }
    const scale = Math.sqrt(
      Math.max(1, width) * Math.max(1, height) / (ratio.reference.width * ratio.reference.height)
    );
    return Math.min(
      ROOT_DIMENSION_MAX,
      Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(scale * 512 / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP
      )
    );
  };
  var matchAspectRatio = (width, height, multiple = ROOT_DIMENSION_STEP) => {
    const roundedWidth = Math.round(width);
    const roundedHeight = Math.round(height);
    const exact = ASPECT_RATIOS.find(
      (ratio) => declaredRatioMatches(ratio.id, roundedWidth, roundedHeight)
    );
    if (exact) {
      return exact.id;
    }
    for (const ratio of ASPECT_RATIOS) {
      if (!ratio.reference) {
        continue;
      }
      const estimated = sideLengthForDimensions(
        ratio.id,
        roundedWidth,
        roundedHeight
      );
      for (const offset of [-64, -32, 0, 32, 64]) {
        const raw = dimensionsFor(ratio.id, estimated + offset);
        if (!raw) {
          continue;
        }
        const snapped = snapDimensions(raw.width, raw.height, multiple);
        if (snapped.width === roundedWidth && snapped.height === roundedHeight) {
          return ratio.id;
        }
      }
    }
    return null;
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
  var getRootModelInput = () => getVideoStagesHostBridge().getSelect("input_model");
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
  var isRootTextToVideoModel = (modelCatalog) => {
    const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
    if (!modelName) {
      return false;
    }
    const catalog = modelCatalog ?? buildArchitectureModelCatalog([modelName], [modelName]);
    return entryModesForModel(catalog, modelName).includes("text-to-video");
  };
  var getRootGeneratedEntryMode = (modelCatalog) => !`${getRootModelInput()?.value ?? ""}`.trim() || isRootTextToVideoModel(modelCatalog) ? "text-to-video" : "image-to-video";
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

  // frontend/utils.ts
  var isRecord2 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
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

  // frontend/rootDefaults.ts
  var trimDomValue = (el) => `${el?.value ?? ""}`.trim();
  var WIDTH_INPUT_IDS = ["input_width", "input_aspectratiowidth"];
  var HEIGHT_INPUT_IDS = ["input_height", "input_aspectratioheight"];
  var ASPECT_RATIO_INPUT_ID = "input_aspectratio";
  var SIDE_LENGTH_INPUT_ID = "input_sidelength";
  var SIDE_LENGTH_TOGGLE_ID = "input_sidelength_toggle";
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
  var getDefaultStageModel = (defaults, architectureId) => {
    const { modelValues, modelCatalog } = defaults;
    const supports = (modelName) => {
      const resolved = architectureForModel(modelCatalog, modelName);
      return resolved !== null && (architectureId === void 0 || resolved === architectureId);
    };
    if (isRootTextToVideoModel(modelCatalog)) {
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
    const aspectRatio = trimDomValue(
      getVideoStagesHostBridge().getSelect(ASPECT_RATIO_INPUT_ID)
    );
    const sideLength = trimDomValue(
      getVideoStagesHostBridge().getInput(SIDE_LENGTH_INPUT_ID)
    );
    const sideLengthToggle = getVideoStagesHostBridge().getInput(
      SIDE_LENGTH_TOGGLE_ID
    );
    const fps = trimDomValue(rootVideoFpsInput());
    return `${width}|${height}|${aspectRatio}|${sideLength}|${sideLengthToggle?.checked ?? ""}|${fps}`;
  };
  var getRootDefaults = (architectureCatalog) => {
    let model = getVideoStagesHostBridge().getSelect("input_videomodel");
    let modelOptions = getVideoStagesHostBridge().getSelectOptions(model);
    const buildModelCatalog = () => architectureCatalog === void 0 ? buildArchitectureModelCatalog(
      modelOptions.values,
      modelOptions.labels
    ) : buildArchitectureModelCatalog(
      modelOptions.values,
      modelOptions.labels,
      architectureCatalog
    );
    let modelCatalog = buildModelCatalog();
    if ((!model || model.options.length === 0) && isRootTextToVideoModel(modelCatalog)) {
      model = getRootModelInput();
      modelOptions = getVideoStagesHostBridge().getSelectOptions(model);
      modelCatalog = buildModelCatalog();
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
    modelCatalog = supportedArchitectureCatalog(modelCatalog);
    const models = {
      values: modelCatalog.entries.map((entry) => entry.value),
      labels: modelCatalog.entries.map((entry) => entry.label)
    };
    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(...WIDTH_INPUT_IDS);
    const heightInput = firstPresentInput(...HEIGHT_INPUT_IDS);
    const aspectRatioInput = getVideoStagesHostBridge().getSelect(
      ASPECT_RATIO_INPUT_ID
    );
    const sideLengthInput = getVideoStagesHostBridge().getInput(SIDE_LENGTH_INPUT_ID);
    const sideLengthToggle = getVideoStagesHostBridge().getInput(
      SIDE_LENGTH_TOGGLE_ID
    );
    const fpsInput = rootVideoFpsInput();
    const framesInput = firstPresentInput(
      "input_videoframes",
      "input_text2videoframes"
    );
    const fps = Math.max(1, Math.round(toNumber(fpsInput?.value, 24)));
    const frames = Math.max(1, Math.round(toNumber(framesInput?.value, 24)));
    const hostWidth = Math.max(
      ROOT_DIMENSION_MIN,
      Math.round(toNumber(widthInput?.value, 1024))
    );
    const hostHeight = Math.max(
      ROOT_DIMENSION_MIN,
      Math.round(toNumber(heightInput?.value, 1024))
    );
    const aspectRatio = trimDomValue(aspectRatioInput);
    const sideLengthEnabled = sideLengthInput !== null && (sideLengthToggle === null || sideLengthToggle.checked);
    const sideLength = sideLengthEnabled ? Math.max(
      ROOT_DIMENSION_MIN,
      Math.round(toNumber(sideLengthInput.value, 1024))
    ) : null;
    const aspectDimensions = aspectRatio && aspectRatio !== "Custom" && sideLength !== null ? dimensionsFor(aspectRatio, sideLength) : null;
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
      width: aspectDimensions?.width ?? hostWidth,
      height: aspectDimensions?.height ?? hostHeight,
      aspectRatio: aspectRatio || void 0,
      sideLength,
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
      stepsMax: Math.max(1, Math.round(toNumber(steps?.max, 200))),
      stepsStep: Math.max(1, Math.round(toNumber(steps?.step, 1))),
      cfgScale: 1,
      cfgScaleMin: toNumber(cfgScale?.min, 0),
      cfgScaleMax: toNumber(cfgScale?.max, 10),
      cfgScaleStep: toNumber(cfgScale?.step, 0.5)
    };
  };

  // frontend/authoringSnapshot.ts
  var captureAuthoringTransactionSnapshot = () => {
    const catalogStatus = getArchitectureCatalogSnapshot();
    const defaults = getRootDefaults(catalogStatus.catalog);
    return {
      catalogStatus,
      defaults,
      capabilities: createCapabilityViewResolver(defaults.modelCatalog),
      generatedEntryMode: getRootGeneratedEntryMode(
        defaults.modelCatalog
      )
    };
  };

  // frontend/initVideoProbe.ts
  var initVideoFromProbe = (probe, data, fileName, clipDuration) => {
    const durationSeconds = roundToTenth(probe?.durationSeconds ?? 0);
    return {
      data,
      fileName,
      fps: probe?.fps ?? 0,
      durationSeconds,
      startSeconds: 0,
      lengthSeconds: durationSeconds > 0 ? durationSeconds : clipDuration
    };
  };
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
  var withProbedMedia = (src, timeoutMs, empty, onMetadata) => new Promise((resolve) => {
    const video = getVideoStagesHostBridge().createInitVideoElement();
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
    const timer = setTimeout(() => finish2(empty), timeoutMs);
    video.addEventListener("error", () => finish2(empty));
    video.addEventListener("loadedmetadata", () => {
      const durationSeconds = Number.isFinite(video.duration) ? video.duration : 0;
      if (!(durationSeconds > 0)) {
        finish2(empty);
        return;
      }
      onMetadata(video, durationSeconds, finish2);
    });
    video.src = src;
  });
  var probeInitVideo = (src, timeoutMs = 8e3) => withProbedMedia(
    src,
    timeoutMs,
    null,
    (video, durationSeconds, finish2) => {
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
    }
  );
  var probeMediaDurationSeconds = (src, timeoutMs = 8e3) => withProbedMedia(
    src,
    timeoutMs,
    0,
    (_video, duration, finish2) => finish2(duration)
  );

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
  var snapValueToStep = (value, fallback, min, max, step) => {
    const unitScale = 1 / step;
    return Math.round(clampedNumber(value, fallback, min, max) * unitScale) / unitScale;
  };

  // frontend/types.ts
  var CURRENT_AUTHORING_SCHEMA_VERSION = 7;

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
      for (let refIndex = 0; refIndex < clip.frameRefs.length; refIndex++) {
        entries.push({
          entity: clip.frameRefs[refIndex],
          kind: "ref",
          repairPath: `${clipIndex}_${refIndex}`
        });
      }
      for (let referenceIndex = 0; referenceIndex < clip.references.length; referenceIndex++) {
        entries.push({
          entity: clip.references[referenceIndex],
          kind: "clip_reference",
          repairPath: `${clipIndex}_${referenceIndex}`
        });
      }
      for (let icLoraIndex = 0; icLoraIndex < clip.icLoras.length; icLoraIndex++) {
        entries.push({
          entity: clip.icLoras[icLoraIndex],
          kind: "ic_lora",
          repairPath: `${clipIndex}_${icLoraIndex}`
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
  var ownedIds = (entity) => {
    const nested = "stages" in entity ? [
      ...entity.stages,
      ...entity.frameRefs,
      ...entity.references ?? [],
      ...entity.icLoras ?? [],
      ...entity.promptWindows,
      ...entity.retake ? [entity.retake] : []
    ] : "spans" in entity ? entity.spans : [];
    return [entity.id, ...nested.map((item) => item.id)];
  };
  var collectAuthoringEntityIds = (state) => [
    ...state.clips.flatMap(ownedIds),
    ...(state.audioTracks ?? []).flatMap(ownedIds)
  ].filter((id) => !!id);

  // frontend/generatedReferenceScale.ts
  var REFERENCE_SCALE_FULL = 1;
  var REFERENCE_SCALES = [1, 0.5, 0.25];

  // frontend/clipReferenceAuthoring.ts
  var CLIP_REFERENCE_KIND_INFO = {
    image: {
      label: "Image",
      tag: "Picture",
      accept: "image/*",
      browserTypes: ["image"]
    },
    video: {
      label: "Video",
      tag: "Video",
      accept: "video/*",
      browserTypes: ["video"]
    },
    audio: {
      label: "Audio",
      tag: "Audio",
      accept: "audio/*",
      browserTypes: ["audio"]
    }
  };
  var CLIP_REFERENCE_KINDS = ["image", "video", "audio"];
  var CLIP_REFERENCE_SCALE_LABELS = {
    1: "Full",
    0.5: "Half",
    0.25: "Quarter"
  };
  var CLIP_REFERENCE_SCALES = REFERENCE_SCALES.map((value) => ({
    value,
    label: CLIP_REFERENCE_SCALE_LABELS[value]
  }));
  var normalizeClipReferenceScale = (value) => {
    const numeric = Number(value);
    return REFERENCE_SCALES.some((scale) => scale === numeric) ? numeric : REFERENCE_SCALE_FULL;
  };
  var normalizeClipReferenceKind = (value) => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "video" || raw === "audio" ? raw : "image";
  };
  var buildDefaultClipReference = (kind = "image") => ({
    kind,
    source: MEDIA_SOURCE_UPLOAD,
    uploadedMedia: null,
    includeSoundtrack: false,
    mediaDurationSeconds: 0,
    drivesClipLength: false,
    mediaScale: REFERENCE_SCALE_FULL
  });
  var clipReferenceCanDriveLength = (reference) => reference.kind === "video" || reference.kind === "audio";
  var clipReferenceTags = (references, precedingReferences = []) => {
    const allReferences = [...precedingReferences, ...references];
    const used = {
      image: 0,
      video: 0,
      audio: allReferences.filter(
        (reference) => reference.kind === "video" && reference.includeSoundtrack === true
      ).length
    };
    for (const reference of precedingReferences) {
      used[reference.kind] += 1;
    }
    return references.map((reference) => {
      used[reference.kind] += 1;
      return `<${CLIP_REFERENCE_KIND_INFO[reference.kind].tag} ${used[reference.kind]}>`;
    });
  };
  var clipLengthReferenceIndex = (references) => references.findIndex(
    (reference) => reference.drivesClipLength === true && clipReferenceCanDriveLength(reference) && reference.mediaDurationSeconds > 0
  );

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
    return rawList.map((entry) => normalizePromptWindow(isRecord2(entry) ? entry : {})).filter((window2) => window2 !== null).sort((a, b) => a.start - b.start);
  };
  var normalizeRetake = (value, clipDuration) => {
    if (!isRecord2(value)) {
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
  var normalizeInitVideo = (value) => {
    if (!isRecord2(value)) {
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
  var normalizeClipReferences = (value, clipLengthClaimedElsewhere = false) => {
    if (!Array.isArray(value)) {
      return [];
    }
    let lengthClaimed = clipLengthClaimedElsewhere;
    return value.map((entry) => {
      const raw = isRecord2(entry) ? entry : {};
      const kind = normalizeClipReferenceKind(raw.kind);
      const source = trimmedText(raw.source) || MEDIA_SOURCE_UPLOAD;
      const mediaDurationSeconds = roundToTenth(
        nonNegativeNumber(raw.mediaDurationSeconds)
      );
      const reference = {
        id: normalizeOptionalEntityId(raw.id),
        kind,
        source,
        uploadedMedia: normalizeUploadedMedia(raw.uploadedMedia),
        includeSoundtrack: kind === "video" && raw.includeSoundtrack === true,
        mediaDurationSeconds,
        // A claim with no length behind it cannot move the clip, so it is
        // dropped rather than left holding the one slot forever.
        drivesClipLength: !lengthClaimed && raw.drivesClipLength === true && clipReferenceCanDriveLength({ kind }) && mediaDurationSeconds > 0,
        mediaScale: kind === "video" ? normalizeClipReferenceScale(raw.mediaScale) : REFERENCE_SCALE_FULL
      };
      lengthClaimed = lengthClaimed || reference.drivesClipLength;
      return reference;
    });
  };
  var clipReferenceDurationSeconds = (references) => {
    const index = clipLengthReferenceIndex(references);
    return index < 0 ? null : references[index].mediaDurationSeconds;
  };
  var normalizeUploadedMedia = (value) => {
    if (!isRecord2(value)) {
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
      case MEDIA_SOURCE_UPLOAD.toLowerCase():
        return MEDIA_SOURCE_UPLOAD;
      case MEDIA_SOURCE_ACE_STEP_FUN.toLowerCase():
        return MEDIA_SOURCE_ACE_STEP_FUN;
      case MEDIA_SOURCE_NATIVE.toLowerCase():
        return MEDIA_SOURCE_NATIVE;
      case MEDIA_SOURCE_CONTROLNET.toLowerCase():
        return MEDIA_SOURCE_CONTROLNET;
      default:
        return "Unrecognized";
    }
  };
  var normalizeAudioTrackSpan = (value) => {
    if (!isRecord2(value)) {
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
      if (!isRecord2(rawTrack)) {
        continue;
      }
      const rawSource = rawTrack.source;
      const source = isRecord2(rawSource) ? rawSource : {};
      const rawSpans = rawTrack.spans;
      const volume = rawTrack.volume === void 0 ? void 0 : clampedNumber(
        rawTrack.volume,
        AUDIO_SPAN_VOLUME_DEFAULT,
        AUDIO_SPAN_VOLUME_MIN,
        AUDIO_SPAN_VOLUME_MAX
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

  // frontend/architectures/ltx2/generatedIcLora.ts
  var IC_LORA_AUTO = "[AUTO]";
  var IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";

  // frontend/architectures/ltx2/icLoraPresets.ts
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
  var IC_LORA_DEFAULT_PRESET_ID = "union-control";
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
      dimensionDownscaleFactor: 2,
      note: "Structural control from depth/canny/normal signals; pick the control type to render. Dims snap to multiples of 64."
    },
    {
      id: "motion-track-control",
      displayName: "Motion Track Control",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors`,
      dimensionDownscaleFactor: 2,
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
      id: "pixel-spatial-upscaler-x2",
      displayName: "Pixel Spatial Upscaler ×2",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors`,
      dimensionDownscaleFactor: 2,
      note: "Apply on a refine stage with Upscale ×2 and source Incoming media. Dims snap to multiples of 64."
    },
    {
      id: "pixel-spatial-upscaler-x4",
      displayName: "Pixel Spatial Upscaler ×4",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors`,
      dimensionDownscaleFactor: 4,
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
  var icLoraDisplayName = (entry) => {
    if (entry.preset === IC_LORA_PRESET_CUSTOM_ID) {
      return entry.lora;
    }
    return findIcLoraPreset(entry.preset)?.displayName ?? entry.preset;
  };
  var icLoraDriveMediaContract = (preset) => preset?.driveMedia ?? DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
  var icLoraWeightsStem = (preset) => preset.weightsUrl.slice(preset.weightsUrl.lastIndexOf("/") + 1).replace(/\.safetensors$/i, "");
  var icLoraAutoModelName = (preset) => `${IC_LORA_AUTO_FOLDER}/${icLoraWeightsStem(preset).replaceAll(".", "_")}`;
  var icLoraLegacyAutoModelName = (preset) => `${IC_LORA_AUTO_FOLDER}/${icLoraWeightsStem(preset)}`;
  var icLoraRepoUrl = (preset) => preset.weightsUrl.split("/resolve/")[0];
  var icLoraTriggerHint = (preset) => {
    if (!preset?.triggerPhrase) {
      return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
  };

  // frontend/architectures/ltx2/dimensionPolicy.ts
  var normalizeModelName = (value) => {
    const normalized = `${value ?? ""}`.trim().replaceAll("\\", "/");
    const basename = normalized.slice(normalized.lastIndexOf("/") + 1);
    return basename.replace(/\.safetensors$/i, "").toLowerCase();
  };
  var presetFactors = /* @__PURE__ */ new Map();
  var curatedModelFactors = /* @__PURE__ */ new Map();
  for (const preset of IC_LORA_PRESETS) {
    const factor = preset.dimensionDownscaleFactor;
    if (factor) {
      presetFactors.set(preset.id, factor);
      for (const name of [
        icLoraAutoModelName(preset),
        icLoraLegacyAutoModelName(preset)
      ]) {
        curatedModelFactors.set(normalizeModelName(name), factor);
      }
    }
  }
  var icLoraDimensionFactor = (entry) => {
    const presetFactor = presetFactors.get(
      `${entry.preset ?? ""}`.trim().toLowerCase()
    );
    if (presetFactor) {
      return presetFactor;
    }
    return curatedModelFactors.get(normalizeModelName(entry.lora)) ?? 1;
  };
  var ltx2DimensionFactor = (clip) => Math.max(1, ...clip.icLoras.map((entry) => icLoraDimensionFactor(entry)));
  var ltx2DimensionMultiple = (clip) => ROOT_DIMENSION_STEP * ltx2DimensionFactor(clip);

  // frontend/architectures/ltx2/icLoraDriveAvailability.ts
  var canUseIncomingIcLoraDrive = (entry, clip, clipIdx, clips, generatedEntryMode) => {
    const executable = executableClipIndexes(clips);
    if (entry.driveData === "none" || !executable.includes(clipIdx)) {
      return false;
    }
    const acceptedKinds = entry.driveMediaKinds;
    const activeStageIndexes = clip.stages.slice(0, activeStageCount(clip)).map((_stage, rawIndex) => rawIndex);
    const targetedStages = entry.stage >= 0 ? activeStageIndexes.includes(entry.stage) ? [entry.stage] : [] : activeStageIndexes;
    const hasPreviousClipOutput = executable.some((index) => index < clipIdx);
    return targetedStages.length > 0 && targetedStages.every((targetStage) => {
      const activeStageIndex = activeStageIndexes.indexOf(targetStage);
      const incomingKind = activeStageIndex > 0 || clip.initVideo ? "video" : hasPreviousClipOutput ? "video" : generatedEntryMode === "image-to-video" ? "image" : null;
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
      if (entry.driveSource === MEDIA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
        entry,
        clip,
        clipIdx,
        clips,
        generatedEntryMode
      )) {
        entry.driveSource = MEDIA_SOURCE_UPLOAD;
        changed = true;
      }
    }
    return changed;
  };

  // frontend/icLoraAuthoring.ts
  var STAGE_CONTROLNET_STRENGTH_MIN = 0;
  var STAGE_CONTROLNET_STRENGTH_MAX = 1;
  var STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
  var STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;
  var IC_LORA_STAGE_ALL = -1;
  var IC_LORA_STRENGTH_MIN = 0;
  var IC_LORA_STRENGTH_MAX = 5;
  var IC_LORA_STRENGTH_STEP = 0.05;
  var IC_LORA_STRENGTH_DEFAULT = 1;
  var IC_LORA_ATTENTION_MIN = 0;
  var IC_LORA_ATTENTION_MAX = 1;
  var IC_LORA_ATTENTION_STEP = 0.05;
  var IC_LORA_ATTENTION_DEFAULT = 1;

  // frontend/architectures/ltx2/icLoraNormalization.ts
  var defaultIcLora = (overrides = {}) => ({
    lora: "",
    preset: IC_LORA_PRESET_CUSTOM_ID,
    driveSource: MEDIA_SOURCE_UPLOAD,
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
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
  var INCOMING_LEGACY_COMPACT = compactMediaSource(
    MEDIA_SOURCE_INCOMING_LEGACY
  );
  var normalizeIcLoraDriveSource = (value) => {
    const authored = `${value ?? ""}`.trim();
    const compact = compactMediaSource(authored);
    if (!compact || equalsMediaSource(compact, MEDIA_SOURCE_UPLOAD)) {
      return MEDIA_SOURCE_UPLOAD;
    }
    if (equalsMediaSource(compact, MEDIA_SOURCE_INCOMING) || equalsMediaSource(compact, INCOMING_LEGACY_COMPACT)) {
      return MEDIA_SOURCE_INCOMING;
    }
    return canonicalControlNetSource(authored) ?? authored;
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
  var normalizeIcLora = (raw, stageCount = 0, _initVideoClip = false) => {
    if (!isRecord2(raw)) {
      return null;
    }
    const lora = normalizeControlNetLora(raw.lora);
    if (!lora) {
      return null;
    }
    const preset = `${raw.preset ?? ""}`.trim();
    const repairsLegacyCustomAuto = lora === IC_LORA_AUTO && (!preset || preset === IC_LORA_PRESET_CUSTOM_ID);
    const normalizedPreset = repairsLegacyCustomAuto ? IC_LORA_DEFAULT_PRESET_ID : preset || IC_LORA_PRESET_CUSTOM_ID;
    const repairedPreset = repairsLegacyCustomAuto ? findIcLoraPreset(normalizedPreset) : null;
    const driveData = normalizeIcLoraDriveData(raw.driveData);
    const driveMediaKinds = normalizeIcLoraDriveMediaKinds(
      raw.driveMediaKinds,
      driveData
    );
    const normalizedDriveMedia = normalizeUploadedMedia(raw.driveMedia);
    let driveSource = normalizeIcLoraDriveSource(raw.driveSource);
    const driveMedia = driveSource === MEDIA_SOURCE_UPLOAD && driveData !== "none" && normalizedDriveMedia && driveMediaKinds.some(
      (kind) => normalizedDriveMedia.data.startsWith(`data:${kind}/`)
    ) ? normalizedDriveMedia : null;
    const stage = normalizeIcLoraStage(raw.stage, stageCount);
    if (driveData === "none") {
      driveSource = MEDIA_SOURCE_UPLOAD;
    }
    return {
      id: normalizeOptionalEntityId(raw.id),
      lora,
      preset: normalizedPreset,
      driveSource,
      driveData,
      driveMediaKinds,
      stage,
      strength: repairsLegacyCustomAuto ? repairedPreset?.strength ?? IC_LORA_STRENGTH_DEFAULT : snapValueToStep(
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
      controlType: driveData !== "visual" ? "none" : repairsLegacyCustomAuto ? repairedPreset?.controlType ?? "none" : normalizeIcLoraControlType(raw.controlType),
      driveMedia
    };
  };
  var normalizeIcLoras = (rawClip, stageCount = 0, initVideoClip = false) => {
    if (!Array.isArray(rawClip.icLoras)) {
      return [];
    }
    return rawClip.icLoras.map((entry) => normalizeIcLora(entry, stageCount, initVideoClip)).filter((entry) => entry !== null);
  };
  var canonicalizeIcLoraFields = (entry) => {
    if (entry.driveData === "none") {
      entry.driveSource = MEDIA_SOURCE_UPLOAD;
      entry.driveMedia = null;
    }
    entry.driveMediaKinds = normalizeIcLoraDriveMediaKinds(
      entry.driveMediaKinds,
      entry.driveData
    );
  };
  var hasSlotSourcedIcLora = (icLoras) => icLoras.some(
    (entry) => canonicalControlNetSource(entry.driveSource) !== null
  );

  // frontend/architectures/ltx2/identity.ts
  var LTX2_ARCHITECTURE_ID = "ltx2";

  // frontend/architectures/behaviorRegistry.ts
  var isLtx2 = (architectureId) => architectureId === LTX2_ARCHITECTURE_ID;
  var architectureDimensionMultiple = (clip, architectureId) => {
    const requested = isLtx2(architectureId) ? ltx2DimensionMultiple(clip) : ROOT_DIMENSION_STEP;
    if (!Number.isFinite(requested)) {
      return ROOT_DIMENSION_STEP;
    }
    return Math.max(
      ROOT_DIMENSION_STEP,
      Math.ceil(requested / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP
    );
  };
  var normalizeArchitectureIcLoras = (architectureId, rawClip, stageCount, initVideoClip, options = {}) => {
    if (isLtx2(architectureId)) {
      return normalizeIcLoras(
        rawClip,
        stageCount,
        initVideoClip
      );
    }
    return options.preserveDormantLtx === true && Array.isArray(rawClip.icLoras) && rawClip.icLoras.length > 0 ? normalizeIcLoras(
      rawClip,
      stageCount,
      initVideoClip
    ) : [];
  };
  var canonicalizeArchitectureIcLoraFields = (architectureId, entry) => {
    if (isLtx2(architectureId)) {
      canonicalizeIcLoraFields(entry);
    }
  };
  var reconcileArchitectureIncomingIcLoraDrives = (clips, generatedEntryMode, catalog) => {
    let changed = false;
    clips.forEach((clip, clipIdx) => {
      const architectureId = resolvedClipArchitectureId(clip, catalog) ?? "";
      changed = isLtx2(architectureId) && reconcileIncomingIcLoraDrives(
        clips,
        clipIdx,
        generatedEntryMode
      ) || changed;
    });
    return changed;
  };
  var reconcileClipArchitectureIncomingIcLoraDrives = (clips, clipIdx, generatedEntryMode, catalog) => {
    const clip = clips[clipIdx];
    if (!clip) return false;
    const architectureId = resolvedClipArchitectureId(clip, catalog) ?? "";
    return isLtx2(architectureId) && reconcileIncomingIcLoraDrives(clips, clipIdx, generatedEntryMode);
  };
  var hasArchitectureSlotSourcedIcLora = (architectureId, entries) => isLtx2(architectureId) && hasSlotSourcedIcLora(entries);
  var architectureIcLoraDisplayName = (architectureId, entry) => isLtx2(architectureId) ? icLoraDisplayName(entry) : entry.lora;

  // frontend/architectures/identity.ts
  var normalizeClipArchitecture = (rawArchitecture, stageZeroModel, catalog) => {
    const fromCatalog = catalog && stageZeroModel ? architectureForModel(catalog, stageZeroModel) : null;
    if (fromCatalog) {
      return fromCatalog;
    }
    return `${rawArchitecture ?? ""}`.trim() || "unsupported";
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

  // frontend/normalizationStage.ts
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.find(
    (value) => upscaleModeForMethod(value) === "latent-model"
  ) ?? upscaleMethodValues[0] ?? "";
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
      if (!isRecord2(entry)) {
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
  var buildDefaultStage = (defaults, defaultStageModel, previousStage, refCount, initialLoraWeights = [], initialIcLoraStrengths = []) => {
    const model = previousStage ? previousStage.model : defaultStageModel;
    return {
      skipped: false,
      control: previousStage ? previousStage.control : defaults.control,
      controlNetStrength: previousStage ? previousStage.controlNetStrength : STAGE_CONTROLNET_STRENGTH_DEFAULT,
      icLoraStrengths: previousStage ? [...previousStage.icLoraStrengths] : initialIcLoraStrengths.map(normalizeStageControlNetStrengthValue),
      loraWeights: previousStage ? [...previousStage.loraWeights] : [...initialLoraWeights],
      frameRefStrengths: buildDefaultStageRefStrengths(refCount),
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
  var buildDefaultRef = (source = MEDIA_SOURCE_REFINER) => ({
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false
  });
  var appendRefToClip = (clip, ref) => {
    clip.frameRefs.push(ref);
    for (const stage of clip.stages) {
      stage.frameRefStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
  };
  var removeRefAt = (clip, refIdx) => {
    if (refIdx < 0 || refIdx >= clip.frameRefs.length) {
      return false;
    }
    clip.frameRefs.splice(refIdx, 1);
    for (const stage of clip.stages) {
      if (refIdx < stage.frameRefStrengths.length) {
        stage.frameRefStrengths.splice(refIdx, 1);
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
  var getReferenceFrameMax = (defaults, clip, effectiveFps) => {
    const fps = typeof effectiveFps === "number" && Number.isFinite(effectiveFps) && effectiveFps > 0 ? effectiveFps : defaults.fps;
    if (clip) {
      const frameGrid = clip.stages ? resolvedClipFrameGrid(
        { ...clip, stages: clip.stages },
        defaults.modelCatalog
      ) : NEUTRAL_FRAME_GRID;
      return Math.max(
        REF_FRAME_MIN,
        framesForClip(clip.duration, fps, frameGrid)
      );
    }
    return Math.max(REF_FRAME_MIN, defaults.frames);
  };
  var getKnownReferenceFrameMax = (defaults, clip, effectiveFps) => {
    const resolution = resolveClipFrameGrid(clip, defaults.modelCatalog);
    if (resolution.status !== "resolved") {
      return null;
    }
    const fps = typeof effectiveFps === "number" && Number.isFinite(effectiveFps) && effectiveFps > 0 ? effectiveFps : defaults.fps;
    return Math.max(
      REF_FRAME_MIN,
      framesForClip(clip.duration, fps, {
        frameGrid: resolution.frameGrid,
        frameGridOrigin: resolution.frameGridOrigin
      })
    );
  };
  var normalizeStage = (defaults, defaultStageModel, rawStage, previousStage, refCount, stageIndexInClip, initVideoClip = false, clipLoras = [], clipLoraDefaultWeights = []) => {
    const fallback = buildDefaultStage(
      defaults,
      defaultStageModel,
      previousStage,
      refCount,
      clipLoraDefaultWeights
    );
    const forcedFirstStage = stageIndexInClip === 0 && !initVideoClip;
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
      frameRefStrengths: normalizeStageRefStrengths(
        rawStage.frameRefStrengths,
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
    stage.modelProfileId = modelProfileForModel(defaults.modelCatalog, stage.model) || trimmedText(readRawStageProp(rawStage, "modelProfileId")) || fallback.modelProfileId;
    if (!defaults.upscaleMethodValues.includes(stage.upscaleMethod) && defaults.upscaleMethodValues.length > 0) {
      stage.upscaleMethod = forcedFirstStage ? defaults.upscaleMethodValues[0] ?? "" : stage.upscaleMethod || fallback.upscaleMethod;
    }
    return stage;
  };
  var normalizeRef = (rawRef, frameMax) => {
    const fallback = buildDefaultRef();
    const source = textOr(rawRef.source, fallback.source);
    const authoredFrame = Math.max(
      REF_FRAME_MIN,
      Math.round(numberOr(rawRef.frame, fallback.frame))
    );
    return {
      id: normalizeOptionalEntityId(rawRef.id),
      source,
      uploadFileName: textOr(rawRef.uploadFileName, "") || null,
      uploadedImage: normalizeUploadedMedia(rawRef.uploadedImage),
      frame: frameMax === null ? authoredFrame : clamp(authoredFrame, REF_FRAME_MIN, frameMax),
      fromEnd: !!rawRef.fromEnd
    };
  };

  // frontend/normalizationClip.ts
  var normalizeBoundaryOut = (value) => {
    const raw = trimmedText(value).toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
  };
  var normalizeContinueOverlap = (value, constraints = boundaryWindowConstraints(null)) => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && numeric > 0 ? numeric : normalizeBoundaryWindow(value, constraints);
  };
  var normalizeReferenceFraming = (value) => value === "stretch" || value === "fit" || value === "fit-green" ? value : "crop";
  var buildDefaultClip = (defaults, defaultStageModel, includeDefaultRef = false, previousClip = null) => {
    const frameRefs = includeDefaultRef ? [buildDefaultRef()] : [];
    const loras = previousClip?.loras.map((entry) => ({ ...entry })) ?? [];
    const initialLoraWeights = loras.map(
      (entry, index) => previousClip?.stages[0]?.loraWeights[index] ?? defaults.loraDefaultWeights[defaults.loraValues.indexOf(entry.name)] ?? 1
    );
    const firstStage = {
      ...buildDefaultStage(
        defaults,
        defaultStageModel,
        previousClip?.stages[0] ?? null,
        frameRefs.length,
        initialLoraWeights
      ),
      frameRefStrengths: buildDefaultStageRefStrengths(
        frameRefs.length,
        includeDefaultRef ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH : STAGE_REF_STRENGTH_DEFAULT
      )
    };
    const architecture = (previousClip?.architectureHint !== NONE_ARCHITECTURE_ID ? previousClip?.architectureHint : null) ?? architectureForModel(defaults.modelCatalog, firstStage.model) ?? "unsupported";
    const continueRule = architectureDescriptor(
      defaults.modelCatalog,
      architecture
    )?.boundaryRules.continue;
    return {
      architectureHint: architecture,
      modelProfileId: (previousClip?.architectureHint !== NONE_ARCHITECTURE_ID ? previousClip?.modelProfileId : null) ?? modelProfileForModel(defaults.modelCatalog, firstStage.model) ?? firstStage.modelProfileId,
      skipped: previousClip?.skipped === true,
      hue: UNASSIGNED_HUE,
      boundaryOut: "cut",
      boundaryOutCarryAudio: false,
      boundaryOutReferenceScale: REFERENCE_SCALE_FULL,
      boundaryOutReferenceIncludeSoundtrack: true,
      boundaryOutOverlap: boundaryWindowConstraints(continueRule).defaultFrames,
      duration: previousClip ? previousClip.duration : snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
        defaults.fps
      ),
      refFraming: "crop",
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
      initVideo: null,
      references: [],
      frameRefs,
      stages: [firstStage]
    };
  };
  var normalizeClip = (rawClip, defaults, defaultStageModel, effectiveFps) => {
    const rawAudioSource = text(rawClip.audioSource, AUDIO_SOURCE_NATIVE);
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const initVideo = normalizeInitVideo(rawClip.initVideo);
    const fps = Math.max(
      1,
      typeof effectiveFps === "number" && Number.isFinite(effectiveFps) && effectiveFps > 0 ? effectiveFps : defaults.fps
    );
    const clipLengthFromControlNet = !!rawClip.clipLengthFromControlNet;
    const clipLengthFromAudio = !clipLengthFromControlNet && !!rawClip.clipLengthFromAudio;
    const references = normalizeClipReferences(
      rawClip.references,
      clipLengthFromControlNet || clipLengthFromAudio
    );
    const rawDuration = initVideo?.lengthSeconds ?? clipReferenceDurationSeconds(references) ?? numberOr(rawClip.duration, defaults.frames / fps);
    const duration = snapDurationToFps(
      Math.max(CLIP_DURATION_MIN, rawDuration),
      fps
    );
    const refsRaw = Array.isArray(rawClip.frameRefs) ? rawClip.frameRefs : [];
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
      if (!isRecord2(rawStage)) {
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
          defaults,
          defaultStageModel,
          isRecord2(stagesRaw[i]) ? stagesRaw[i] : {},
          previousStage,
          refsRaw.length,
          i,
          initVideo !== null,
          loras,
          loraDefaultWeights
        )
      );
    }
    sealSkipSuffix(stages);
    const retake = normalizeRetake(rawClip.retake, duration);
    const audioSource = canonicalAudioSource(rawAudioSource);
    const refFrameMax = getKnownReferenceFrameMax(
      defaults,
      {
        duration,
        stages,
        initVideo,
        retake,
        audioSource,
        clipLengthFromAudio,
        clipLengthFromControlNet
      },
      fps
    );
    const frameRefs = refsRaw.map(
      (rawRef) => normalizeRef(isRecord2(rawRef) ? rawRef : {}, refFrameMax)
    );
    const stageZero = stages[0] ?? null;
    const persistedArchitecture = trimmedText(rawClip.architectureHint);
    const persistedProfile = trimmedText(rawClip.modelProfileId);
    const isSourceOnly = initVideo !== null && stages.every((stage) => stage.skipped);
    const architecture = isSourceOnly ? persistedArchitecture || "none" : normalizeClipArchitecture(
      persistedArchitecture,
      stageZero?.model ?? null,
      defaults.modelCatalog
    );
    const resolvedArchitecture = isSourceOnly ? NONE_ARCHITECTURE_ID : architectureForModel(
      defaults.modelCatalog,
      stageZero?.model ?? ""
    ) ?? "unsupported";
    const modelProfileId = isSourceOnly ? persistedProfile || (architecture === NONE_ARCHITECTURE_ID ? NONE_ARCHITECTURE_ID : "unsupported") : modelProfileForModel(defaults.modelCatalog, stageZero?.model ?? "") || persistedProfile || stageZero?.modelProfileId || "unsupported";
    const icLoras = normalizeArchitectureIcLoras(
      resolvedArchitecture,
      rawClip,
      stagesRaw.length,
      initVideo !== null,
      { preserveDormantLtx: true }
    );
    const icLoraDefaultStrengths = icLoras.map(
      (entry) => defaultLoraWeight(defaults, entry.lora)
    );
    for (let index = 0; index < stages.length; index++) {
      const stage = stages[index];
      const rawStage = isRecord2(stagesRaw[index]) ? stagesRaw[index] : {};
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
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = architectureDescriptor(
      defaults.modelCatalog,
      resolvedArchitecture
    )?.boundaryRules[boundaryOut];
    return {
      id: normalizeOptionalEntityId(rawClip.id),
      architectureHint: architecture,
      modelProfileId,
      skipped: !!rawClip.skipped,
      hue: normalizeStoredHue(rawClip.hue),
      boundaryOut,
      boundaryOutCarryAudio: !!rawClip.boundaryOutCarryAudio,
      boundaryOutReferenceScale: normalizeClipReferenceScale(
        rawClip.boundaryOutReferenceScale
      ),
      boundaryOutReferenceIncludeSoundtrack: rawClip.boundaryOutReferenceIncludeSoundtrack !== false,
      boundaryOutOverlap: normalizeContinueOverlap(
        rawClip.boundaryOutOverlap,
        boundaryWindowConstraints(boundaryRule)
      ),
      duration,
      refFraming: normalizeReferenceFraming(rawClip.refFraming),
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
      retake,
      initVideo,
      references,
      frameRefs,
      stages
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
        architectureHint: clip.architectureHint,
        modelProfileId: clip.modelProfileId,
        skipped: clip.skipped,
        boundaryOut: clip.boundaryOut,
        boundaryOutCarryAudio: clip.boundaryOutCarryAudio,
        boundaryOutReferenceScale: clip.boundaryOutReferenceScale,
        boundaryOutReferenceIncludeSoundtrack: clip.boundaryOutReferenceIncludeSoundtrack,
        boundaryOutOverlap: clip.boundaryOutOverlap,
        duration: clip.duration,
        refFraming: clip.refFraming,
        audioSource: clip.audioSource,
        loras: clip.loras.map((entry) => ({
          name: entry.name
        })),
        icLoras: clip.icLoras.map((entry) => ({
          id: entry.id,
          lora: entry.lora,
          preset: entry.preset,
          driveSource: entry.driveSource,
          driveData: entry.driveData,
          driveMediaKinds: entry.driveMediaKinds,
          stage: entry.stage,
          strength: entry.strength,
          attentionStrength: entry.attentionStrength,
          controlType: entry.controlType,
          driveMedia: entry.driveMedia
        })),
        saveAudioTrack: clip.saveAudioTrack,
        clipLengthFromAudio: clip.clipLengthFromAudio,
        clipLengthFromControlNet: clip.clipLengthFromControlNet,
        reuseAudio: clip.reuseAudio,
        uploadedAudio: clip.uploadedAudio,
        initVideo: clip.initVideo ? {
          data: clip.initVideo.data,
          fileName: clip.initVideo.fileName,
          fps: clip.initVideo.fps,
          durationSeconds: clip.initVideo.durationSeconds,
          startSeconds: clip.initVideo.startSeconds,
          lengthSeconds: clip.initVideo.lengthSeconds
        } : null,
        retake: clip.retake ? {
          id: clip.retake.id,
          startSeconds: clip.retake.startSeconds,
          lengthSeconds: clip.retake.lengthSeconds,
          strength: clip.retake.strength
        } : null,
        references: clip.references.map((reference) => ({
          id: reference.id,
          kind: reference.kind,
          source: reference.source,
          uploadedMedia: reference.uploadedMedia,
          includeSoundtrack: reference.includeSoundtrack,
          mediaDurationSeconds: reference.mediaDurationSeconds,
          drivesClipLength: reference.drivesClipLength,
          mediaScale: reference.mediaScale
        })),
        frameRefs: clip.frameRefs.map((ref) => ({
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
          frameRefStrengths: stage.frameRefStrengths,
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
      if (clip.initVideo && isTransientBrowserMedia({ data: clip.initVideo.data })) {
        clip.initVideo = null;
      }
      for (const ref of clip.frameRefs) {
        if (isTransientBrowserMedia(ref.uploadedImage)) {
          ref.uploadedImage = null;
        }
      }
      for (const reference of clip.references) {
        if (isTransientBrowserMedia(reference.uploadedMedia)) {
          reference.uploadedMedia = null;
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
    return Array.isArray(value) && value.every(isRecord2);
  };
  var hasValidStoredCollections = (parsed) => {
    if (!Array.isArray(parsed.clips) || !parsed.clips.every(isRecord2)) {
      return false;
    }
    if (!hasArrayOfRecords(parsed, "audioTracks") || !hasArrayOfRecords(parsed, "clips")) {
      return false;
    }
    for (const clip of parsed.clips) {
      if (!hasArrayOfRecords(clip, "stages") || !hasArrayOfRecords(clip, "frameRefs") || !hasArrayOfRecords(clip, "references") || !hasArrayOfRecords(clip, "icLoras") || Object.hasOwn(clip, "loras") && !hasArrayOfRecords(clip, "loras")) {
        return false;
      }
      const stages = Array.isArray(clip.stages) ? clip.stages : [];
      for (const stage of stages) {
        if (Object.hasOwn(stage, "loras") && !hasArrayOfRecords(stage, "loras") || Object.hasOwn(stage, "loraWeights") && (!Array.isArray(stage.loraWeights) || !stage.loraWeights.every(
          (weight) => typeof weight === "number" && Number.isFinite(weight)
        )) || Object.hasOwn(stage, "icLoraStrengths") && (!Array.isArray(stage.icLoraStrengths) || !stage.icLoraStrengths.every(
          (strength) => typeof strength === "number" && Number.isFinite(strength)
        )) || Object.hasOwn(stage, "frameRefStrengths") && (!Array.isArray(stage.frameRefStrengths) || !stage.frameRefStrengths.every(
          (strength) => typeof strength === "number" && Number.isFinite(strength)
        ))) {
          return false;
        }
      }
    }
    const tracks = Array.isArray(parsed.audioTracks) ? parsed.audioTracks : [];
    return tracks.every(
      (track) => hasArrayOfRecords(track, "spans") && (!Object.hasOwn(track, "source") || isRecord2(track.source))
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
  var DIVERGENT_PROJECTION_NOTICE = "VideoStages: the saved timeline has audio spans whose clip anchors disagree with their timeline seconds. The seconds were used and the anchors will be rewritten on the next save — re-check those spans.";
  var noticedDivergentProjection = null;
  var SPAN_PROJECTION_TOLERANCE = 1e-6;
  var numberAt = (owner, key) => typeof owner[key] === "number" && Number.isFinite(owner[key]) ? owner[key] : null;
  var storedSpanProjection = (span) => {
    const raw = span.projection;
    if (!isRecord2(raw)) {
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
      const spans = isRecord2(track) && Array.isArray(track.spans) ? track.spans : [];
      for (const span of spans) {
        if (!isRecord2(span)) {
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
  var FRAME_REFS_LEGACY_SCHEMA_VERSION = 6;
  var renameKey = (target, oldKey, newKey) => {
    if (!(oldKey in target)) {
      return;
    }
    if (!(newKey in target)) {
      target[newKey] = target[oldKey];
    }
    delete target[oldKey];
  };
  var migrateStoredDocument = (parsed) => {
    if (parsed.schemaVersion === CURRENT_AUTHORING_SCHEMA_VERSION) {
      return parsed;
    }
    if (parsed.schemaVersion !== FRAME_REFS_LEGACY_SCHEMA_VERSION || !Array.isArray(parsed.clips)) {
      return null;
    }
    const migrated = structuredClone(parsed);
    migrated.schemaVersion = CURRENT_AUTHORING_SCHEMA_VERSION;
    for (const rawClip of migrated.clips) {
      if (!isRecord2(rawClip)) {
        continue;
      }
      renameKey(rawClip, "refs", "frameRefs");
      if (!Array.isArray(rawClip.stages)) {
        continue;
      }
      for (const rawStage of rawClip.stages) {
        if (isRecord2(rawStage)) {
          renameKey(rawStage, "refStrengths", "frameRefStrengths");
        }
      }
    }
    return migrated;
  };
  var decodeStoredDocument = (serialized, inherited, defaults, defaultStageModel) => {
    try {
      const parsed = JSON.parse(serialized);
      if (!isRecord2(parsed)) {
        return null;
      }
      const current = migrateStoredDocument(parsed);
      if (!current) {
        noticeOutdatedSchema(serialized);
        return null;
      }
      if (!hasValidStoredCollections(current)) {
        return null;
      }
      if (hasDivergentSpanProjection(current)) {
        noticeDivergentProjection(serialized);
      }
      const dims = resolveRootDims(inherited, {
        width: current.width,
        height: current.height
      });
      const clips = current.clips.map(
        (entry) => normalizeClip(entry, defaults, defaultStageModel, dims.fps)
      );
      sealSkipSuffix(clips);
      return {
        dims,
        clips,
        audioTracks: normalizeAudioTracks(current.audioTracks)
      };
    } catch {
      return null;
    }
  };
  var hasCanonicalStoredId = (value, seen) => {
    if (!isRecord2(value) || typeof value.id !== "string" || value.id.length === 0 || value.id.trim() !== value.id || seen.has(value.id)) {
      return false;
    }
    seen.add(value.id);
    return true;
  };
  var storedDocumentNeedsCanonicalIdRepair = (serialized) => {
    try {
      const parsed = JSON.parse(serialized);
      if (!isRecord2(parsed) || parsed.schemaVersion !== CURRENT_AUTHORING_SCHEMA_VERSION || !Array.isArray(parsed.clips) || !Array.isArray(parsed.audioTracks)) {
        return true;
      }
      const seenIds = /* @__PURE__ */ new Set();
      for (const rawClip of parsed.clips) {
        if (!hasCanonicalStoredId(rawClip, seenIds)) return true;
        for (const key of ["stages", "frameRefs", "references"]) {
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

  // frontend/architectures/conversion/plan.ts
  var resolveArchitectureRetarget = (requested, catalog) => {
    if (!catalog) {
      return null;
    }
    const model = modelCatalogEntry(catalog, requested.model);
    if (!model?.architectureId || !model.modelProfileId || model.architectureId !== requested.architectureId) {
      return null;
    }
    const descriptor = architectureDescriptor(catalog, model.architectureId);
    if (!descriptor) {
      return null;
    }
    return {
      architectureId: descriptor.id,
      modelProfileId: model.modelProfileId,
      model: model.value
    };
  };
  var planArchitectureConversion = (source, requested, catalog) => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
      return null;
    }
    const clip = structuredClone(source);
    clip.architectureHint = target.architectureId;
    clip.modelProfileId = target.modelProfileId;
    for (const stage of clip.stages) {
      stage.model = target.model;
      stage.modelProfileId = target.modelProfileId;
    }
    return clip;
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
      "boundaryOutReferenceScale",
      "boundaryOutReferenceIncludeSoundtrack",
      "boundaryOutOverlap",
      "duration",
      "refFraming",
      "audioSource",
      "loras",
      "icLoras",
      "saveAudioTrack",
      "clipLengthFromAudio",
      "clipLengthFromControlNet",
      "reuseAudio",
      "uploadedAudio",
      "prompt",
      "initVideo"
    ],
    reservedKeys: [
      "id",
      "architectureHint",
      "modelProfileId",
      "promptWindows",
      "retake",
      "references",
      "frameRefs",
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
      "frameRefStrengths",
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
    collection: (clip) => clip.frameRefs
  });
  var CLIP_REFERENCE_ENTITY = defineList()({
    prefix: "clip-reference",
    owner: "clip",
    entityField: "reference",
    idField: "referenceId",
    beforeIdField: "beforeReferenceId",
    patchKeys: [
      "kind",
      "source",
      "uploadedMedia",
      "includeSoundtrack",
      "mediaDurationSeconds",
      "drivesClipLength",
      "mediaScale"
    ],
    reservedKeys: ["id"],
    collection: (clip) => clip.references
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
    clipReference: CLIP_REFERENCE_ENTITY,
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
    ...document2.clips.flatMap(ownedIds),
    ...document2.audioTracks.flatMap(ownedIds)
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
  var diffStages = (before, after, phases, context) => diffList(
    LIST_ENTITIES.stage,
    after.id,
    before,
    after,
    phases,
    (previous, next) => {
      if (previous.model !== next.model || previous.modelProfileId !== next.modelProfileId) {
        const targetEntry = modelCatalogEntry(
          context.architectureCatalog,
          next.model
        );
        if (!targetEntry?.architectureId || !targetEntry.modelProfileId) {
          throw new DocumentDiffError("architecture-invariant");
        }
        phases.patches.push({
          type: "stage.retarget-model",
          clipId: after.id,
          stageId: next.id,
          target: {
            architectureId: targetEntry.architectureId,
            modelProfileId: targetEntry.modelProfileId,
            model: targetEntry.value
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
  var diffClipChildren = (before, after, phases, context) => {
    diffStages(before, after, phases, context);
    diffList(LIST_ENTITIES.ref, after.id, before, after, phases);
    diffList(LIST_ENTITIES.clipReference, after.id, before, after, phases);
    diffList(LIST_ENTITIES.promptWindow, after.id, before, after, phases);
    diffRetake(before, after, phases);
  };
  var clipDiffBase = (previous, next, phases, context) => {
    const changesEffectiveIdentity = previous.architectureHint !== next.architectureHint || previous.modelProfileId !== next.modelProfileId;
    const previousIdentity = deriveClipArchitectureIdentity(
      previous,
      context.architectureCatalog
    );
    const nextIdentity = deriveClipArchitectureIdentity(
      next,
      context.architectureCatalog
    );
    const previousStageZeroIdentity = modelIdentityFromCatalog(
      context.architectureCatalog,
      previous.stages[0]?.model ?? ""
    );
    const nextStageZeroIdentity = modelIdentityFromCatalog(
      context.architectureCatalog,
      next.stages[0]?.model ?? ""
    );
    const repairsUnresolvedStageZero = previous.stages[0]?.model !== next.stages[0]?.model && previousStageZeroIdentity === null && nextStageZeroIdentity !== null;
    if (changesEffectiveIdentity) {
      if (!nextIdentity || nextIdentity.architectureId !== next.architectureHint || nextIdentity.modelProfileId !== next.modelProfileId) {
        throw new DocumentDiffError("architecture-invariant");
      }
    }
    const changesAuthoredArchitecture = repairsUnresolvedStageZero || previousIdentity?.authoredArchitectureId !== null && previousIdentity?.authoredArchitectureId !== void 0 && nextIdentity?.authoredArchitectureId !== null && nextIdentity?.authoredArchitectureId !== void 0 && previousIdentity.authoredArchitectureId !== nextIdentity.authoredArchitectureId;
    const nextIsSourceOnlyClip = next.stages.length === 0 && next.initVideo !== null && nextIdentity?.authoredArchitectureId == null && nextIdentity?.architectureId === NONE_ARCHITECTURE_ID;
    if (changesEffectiveIdentity && !changesAuthoredArchitecture && !nextIsSourceOnlyClip && (previousIdentity?.authoredArchitectureId == null || nextIdentity?.authoredArchitectureId == null || previousIdentity.authoredArchitectureId !== nextIdentity.authoredArchitectureId)) {
      throw new DocumentDiffError("architecture-invariant");
    }
    if (!changesAuthoredArchitecture) {
      return previous;
    }
    const catalog = context.architectureCatalog;
    const targetStage = next.stages[0];
    const targetEntry = modelCatalogEntry(catalog, targetStage?.model);
    const targetDescriptor = architectureDescriptor(
      catalog,
      targetEntry?.architectureId
    );
    if (!catalog || !targetStage || !targetEntry?.architectureId || !targetEntry.modelProfileId || !targetDescriptor || targetEntry.architectureId !== nextIdentity?.authoredArchitectureId) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const target = {
      architectureId: targetEntry.architectureId,
      modelProfileId: targetEntry.modelProfileId,
      model: targetEntry.value
    };
    const conversionSource = clone(previous);
    if (!deepEqual(previous.initVideo, next.initVideo)) {
      phases.preConversions.push({
        type: "clip.patch",
        clipId: next.id,
        patch: { initVideo: clone(next.initVideo) }
      });
      conversionSource.initVideo = clone(next.initVideo);
    }
    const nextStagesById = new Map(
      next.stages.map((stage) => [stage.id, stage])
    );
    const nextStageIds = new Set(nextStagesById.keys());
    for (const stage of conversionSource.stages) {
      if (!nextStageIds.has(stage.id)) {
        phases.preConversions.push({
          type: "stage.remove",
          clipId: next.id,
          stageId: stage.id
        });
      }
    }
    conversionSource.stages = conversionSource.stages.filter(
      (stage) => nextStageIds.has(stage.id)
    );
    for (const stage of conversionSource.stages) {
      const nextStage = nextStagesById.get(stage.id);
      if (nextStage && stage.skipped !== nextStage.skipped) {
        phases.preConversions.push({
          type: "stage.patch",
          clipId: next.id,
          stageId: stage.id,
          patch: { skipped: nextStage.skipped }
        });
        stage.skipped = nextStage.skipped;
      }
    }
    const baselinePlan = planArchitectureConversion(
      conversionSource,
      target,
      catalog
    );
    if (!baselinePlan) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const cleanedRequested = clone(next);
    if (!reconcileClipArchitectureIdentity(cleanedRequested, catalog) || !deepEqual(cleanedRequested, next)) {
      throw new DocumentDiffError("architecture-invariant");
    }
    const convertedBase = baselinePlan;
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
      diffClipChildren(diffBase, next, phases, context);
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
      preConversions: [],
      conversions: [],
      removes: [],
      adds: [],
      moves: [],
      patches: []
    };
    const rootPatch = changedPatch(before, after, ROOT_PATCH_KEYS);
    diffClips(before, after, phases, context);
    if (phases.conversions.length > 0) {
      const reconciledAfter = clone(after);
      for (const conversion of phases.conversions) {
        if (conversion.type !== "clip.convert-architecture") continue;
        const clipIdx = reconciledAfter.clips.findIndex(
          (clip) => clip.id === conversion.clipId
        );
        reconcileClipArchitectureIncomingIcLoraDrives(
          reconciledAfter.clips,
          clipIdx,
          context.generatedEntryMode ?? "text-to-video",
          context.architectureCatalog
        );
      }
      if (!deepEqual(reconciledAfter, after)) {
        throw new DocumentDiffError("architecture-invariant");
      }
      const forcedFinalClips = clone(after.clips);
      forceCrossArchitectureCutsForConversion(
        forcedFinalClips,
        context.architectureCatalog
      );
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
      const convertedClipIds = new Set(
        phases.conversions.flatMap(
          (conversion) => conversion.type === "clip.convert-architecture" ? [conversion.clipId] : []
        )
      );
      for (const clip of after.clips) {
        if (!convertedClipIds.has(clip.id)) continue;
        phases.patches.push({
          type: "clip.patch",
          clipId: clip.id,
          patch: { icLoras: clone(clip.icLoras) }
        });
      }
    }
    diffAudioTracks(before, after, phases);
    return {
      type: "batch",
      commands: [
        ...hasPatch(rootPatch) ? [{ type: "root.patch", patch: rootPatch }] : [],
        ...phases.preConversions,
        ...phases.conversions,
        ...phases.removes,
        ...phases.adds,
        ...phases.moves,
        ...phases.patches
      ]
    };
  };

  // frontend/documentDimensionSnap.ts
  var greatestCommonDivisor2 = (left, right) => {
    let a = Math.abs(Math.round(left));
    let b = Math.abs(Math.round(right));
    while (b !== 0) {
      [a, b] = [b, a % b];
    }
    return a || 1;
  };
  var leastCommonMultiple = (left, right) => Math.abs(left * right) / greatestCommonDivisor2(left, right);
  var activeDocumentDimensionMultiple = (clips, catalog) => clips.reduce(
    (multiple, clip) => leastCommonMultiple(
      multiple,
      architectureDimensionMultiple(
        clip,
        resolvedClipArchitectureId(clip, catalog) ?? ""
      )
    ),
    ROOT_DIMENSION_STEP
  );
  var snapExplicitDocumentDimensions = (state, catalog) => {
    const before = {
      width: Math.round(state.width),
      height: Math.round(state.height)
    };
    const multiple = activeDocumentDimensionMultiple(state.clips, catalog);
    const after = state.dimsExplicit ? snapDimensions(before.width, before.height, multiple) : before;
    const changed = after.width !== before.width || after.height !== before.height;
    if (changed) {
      state.width = after.width;
      state.height = after.height;
    }
    return { changed, before, after, multiple };
  };

  // frontend/documentCommands/helpers.ts
  var clone2 = (value) => structuredClone(value);
  var findClip = (document2, clipId) => document2.clips.find((clip) => clip.id === clipId) ?? null;
  var findTrack = (document2, trackId) => document2.audioTracks.find((track) => track.id === trackId) ?? null;
  var invalidNewEntity = (document2, entity) => {
    const ids = ownedIds(entity);
    const invalidId = ids.some(
      (id) => typeof id !== "string" || id.trim().length === 0 || id.trim() !== id
    );
    if (invalidId) return failure(document2, "invalid-id");
    if (new Set(ids).size !== ids.length) {
      return failure(document2, "duplicate-id");
    }
    const existing = new Set(
      collectAuthoringEntityIds(document2)
    );
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
      case "clip.toggle-skip": {
        const clipIndex = document2.clips.findIndex(
          (clip2) => clip2.id === command.clipId
        );
        const clip = document2.clips[clipIndex];
        if (!clip) {
          return failure(document2, "missing-target");
        }
        if (clipIndex === 0 && clip.skipped !== true) {
          return failure(document2, "invalid-operation");
        }
        applySkipSuffix(document2.clips, clipIndex, !clip.skipped);
        reconcileArchitectureIncomingIcLoraDrives(
          document2.clips,
          context.generatedEntryMode ?? "text-to-video",
          context.architectureCatalog
        );
        return success(document2);
      }
      case "clip.move":
        return list(document2, "clip", "move", command, context);
      case "clip.patch": {
        if (hasOwn(command.patch, "architectureHint") || hasOwn(command.patch, "modelProfileId")) {
          return failure(document2, "architecture-invariant");
        }
        const clip = findClip(document2, command.clipId);
        if (!clip) {
          return failure(document2, "missing-target");
        }
        const candidate = clone2(clip);
        Object.assign(candidate, clone2(command.patch), { id: clip.id });
        if (hasOwn(command.patch, "initVideo") && !reconcileClipArchitectureIdentity(
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
        const converted = conversion;
        if (!reconcileClipArchitectureIdentity(
          converted,
          context.architectureCatalog
        )) {
          return failure(document2, "invalid-architecture-conversion");
        }
        document2.clips[clipIndex] = converted;
        forceCrossArchitectureCutsForConversion(
          document2.clips,
          context.architectureCatalog
        );
        reconcileClipArchitectureIncomingIcLoraDrives(
          document2.clips,
          clipIndex,
          context.generatedEntryMode ?? "text-to-video",
          context.architectureCatalog
        );
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
        const ownerArchitectureId = modelIdentityFromCatalog(
          context.architectureCatalog,
          clip.stages[0]?.model ?? ""
        )?.architectureId;
        if (target.architectureId !== ownerArchitectureId) {
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
      case "stage.toggle-skip": {
        const clip = findClip(document2, command.clipId);
        const stageIndex = clip?.stages.findIndex(
          (stage2) => stage2.id === command.stageId
        ) ?? -1;
        const stage = clip?.stages[stageIndex];
        if (!clip || !stage) {
          return failure(document2, "missing-target");
        }
        if (stageIndex === 0 && stage.skipped !== true) {
          return failure(document2, "invalid-operation");
        }
        applySkipSuffix(clip.stages, stageIndex, !stage.skipped);
        if (!reconcileClipArchitectureIdentity(
          clip,
          context.architectureCatalog
        )) {
          return failure(clone2(source), "architecture-invariant");
        }
        reconcileArchitectureIncomingIcLoraDrives(
          document2.clips,
          context.generatedEntryMode ?? "text-to-video",
          context.architectureCatalog
        );
        return success(document2);
      }
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
      case "clip-reference.add":
        return list(document2, "clipReference", "add", command, context);
      case "clip-reference.remove":
        return list(document2, "clipReference", "remove", command, context);
      case "clip-reference.move":
        return list(document2, "clipReference", "move", command, context);
      case "clip-reference.patch":
        return list(document2, "clipReference", "patch", command, context);
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
    const subscribers2 = /* @__PURE__ */ new Set();
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
      for (const cb of [...subscribers2]) {
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
    const dispatch = (command, origin, notifyDomChange, expectedRevision, hint, context) => {
      const source = structuredClone(revalidate());
      if (expectedRevision !== void 0 && expectedRevision !== documentRevision) {
        return {
          applied: false,
          failure: "stale-revision",
          revision: documentRevision
        };
      }
      ensureAuthoringDocumentIdentity(source);
      const reduced = reduceDocumentCommand(source, command, context);
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
        subscribers2.add(cb);
        return () => {
          subscribers2.delete(cb);
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
        subscribers2.clear();
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
      if (!isRecord2(rawWindow) || typeof rawWindow.id !== "string" || !rawWindow.id.trim() || typeof rawWindow.prompt !== "string" || typeof rawWindow.start !== "number" || typeof rawWindow.duration !== "number") {
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
    const storedClips = isRecord2(parsed) && Array.isArray(parsed.clips) ? parsed.clips : [];
    const storedById = /* @__PURE__ */ new Map();
    for (const stored of storedClips) {
      if (isRecord2(stored) && typeof stored.id === "string" && stored.id.trim()) {
        storedById.set(stored.id.trim(), stored);
      }
    }
    for (let i = 0; i < clips.length; i++) {
      const clipId = clips[i].id;
      const positional = storedClips[i];
      const stored = (clipId ? storedById.get(clipId) : void 0) ?? (isRecord2(positional) && !positional.id ? positional : void 0);
      if (!isRecord2(stored)) {
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
    if (!isRecord2(value) || typeof value.prompt !== "string") {
      return null;
    }
    if (!Array.isArray(value.windows)) {
      return null;
    }
    const windows = [];
    for (const window2 of value.windows) {
      if (!isRecord2(window2) || typeof window2.prompt !== "string" || !isFiniteNumber(window2.start) || !isFiniteNumber(window2.duration)) {
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
      if (!isRecord2(parsed) || parsed.version !== DURABLE_AUTHORING_VERSION || typeof parsed.document !== "string" || !Array.isArray(parsed.prompts)) {
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
  var inheritedDims = (defaults) => ({
    width: defaults.width,
    height: defaults.height,
    fps: defaults.fps
  });
  var parse = (serialized) => {
    const defaults = getRootDefaults();
    const decoded = decodeStoredDocument(
      serialized,
      inheritedDims(defaults),
      defaults,
      getDefaultStageModel(defaults)
    );
    if (!decoded) return null;
    overlayPromptAndUiState(decoded.clips);
    return createRootConfig(decoded.dims, decoded.clips, decoded.audioTracks);
  };
  var parseEmpty = () => {
    const clips = [];
    overlayPromptAndUiState(clips);
    return createRootConfig(
      resolveRootDims(inheritedDims(getRootDefaults()), {}),
      clips
    );
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
    const defaults = getRootDefaults();
    const decoded = decodeStoredDocument(
      snapshot.document,
      inheritedDims(defaults),
      defaults,
      getDefaultStageModel(defaults)
    );
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
  var commandContextFor = (transaction) => ({
    architectureCatalog: transaction.defaults.modelCatalog,
    generatedEntryMode: transaction.generatedEntryMode
  });
  var store = createTimelineStore(timelineCarrierAdapter);
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
    const transaction = captureAuthoringTransactionSnapshot();
    const commandContext = commandContextFor(transaction);
    const requested = structuredClone(requestedInput);
    ensureAuthoringDocumentIdentity(requested);
    assignMissingHues(requested.clips);
    const dimensionSnap = snapExplicitDocumentDimensions(
      requested,
      transaction.defaults.modelCatalog
    );
    const before = structuredClone(snapshot.state);
    ensureAuthoringDocumentIdentity(before);
    assignMissingHues(before.clips);
    const diffCommand = (() => {
      try {
        return diffDocuments(before, requested, commandContext);
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
      options?.valueOnly ? "value-only" : void 0,
      commandContext
    );
    if (!result.applied) {
      throwSaveFailure("dispatch", result.failure ?? "unknown failure");
    }
    if (dimensionSnap.changed) {
      const gridReason = dimensionSnap.multiple > ROOT_DIMENSION_STEP ? ` Active architecture features require multiples of ${dimensionSnap.multiple}.` : ` VideoStages dimensions use multiples of ${ROOT_DIMENSION_STEP}.`;
      getVideoStagesHostBridge().showError(
        `VideoStages adjusted the timeline resolution from ${dimensionSnap.before.width}×${dimensionSnap.before.height} to ${dimensionSnap.after.width}×${dimensionSnap.after.height}.${gridReason}`
      );
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
      options?.valueOnly ? "value-only" : void 0,
      commandContextFor(captureAuthoringTransactionSnapshot())
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
  var REFINE_SOURCE_FILE_NAME = "refine-source";
  var refineNeedsExtraStageMessage = (skipCount) => `Refine Video needs Clip 0 to have at least one active stage after Stage ${skipCount - 1} (for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;
  var countActiveStagesInMetadataClip0 = (videostagesJson) => {
    const parsed = safeJsonParse(videostagesJson, null);
    if (!isRecord2(parsed)) {
      return 0;
    }
    const clips = readProp(parsed, "clips");
    if (!Array.isArray(clips) || clips.length === 0) {
      return 0;
    }
    const clip0 = clips[0];
    if (!isRecord2(clip0) || readProp(clip0, "skipped") === true) {
      return 0;
    }
    const stages = readProp(clip0, "stages");
    if (!Array.isArray(stages)) {
      return 0;
    }
    const firstSkipped = stages.findIndex(
      (stage) => isRecord2(stage) && readProp(stage, "skipped") === true
    );
    return firstSkipped < 0 ? stages.length : firstSkipped;
  };
  var hasRefinementWorkToDo = (state, enabled, skipCount) => {
    if (!enabled) {
      return false;
    }
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
      return false;
    }
    return activeStageCount(clip0) > skipCount;
  };
  var applyRefineToClipZero = (clip, data, probe, skipCount) => {
    clip.initVideo = initVideoFromProbe(
      probe,
      data,
      REFINE_SOURCE_FILE_NAME,
      clip.duration
    );
    let activeIndex = 0;
    for (const stage of clip.stages) {
      if (stage.skipped) {
        break;
      }
      if (activeIndex < skipCount) {
        stage.control = 0;
      }
      activeIndex++;
    }
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
          const params = isRecord2(parsedMetadata) ? readProp(parsedMetadata, "sui_image_params") : null;
          const sourceVideostages = isRecord2(params) ? readProp(params, "videostages") : void 0;
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
          const probe = await probeInitVideo(videoDataUrl);
          const state = getState();
          const clipZero = state.clips[0];
          if (!clipZero) {
            host.showError(refineNeedsExtraStageMessage(skipCount));
            return;
          }
          const clips = [...state.clips];
          clips[0] = structuredClone(clipZero);
          applyRefineToClipZero(clips[0], videoDataUrl, probe, skipCount);
          reconcileClipArchitectureIdentity(
            clips[0],
            captureAuthoringTransactionSnapshot().capabilities.catalog
          );
          const inputOverrides = {
            videostages: serializeStateForStorage({ ...state, clips }),
            images: 1
          };
          const seed = isRecord2(params) ? readProp(params, "seed") : void 0;
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

  // frontend/architectureCatalogStatusView.ts
  var detailDock = (body) => body.parentElement?.querySelector(":scope > .vst-detail") ?? null;
  var setDockAvailable = (body, available) => {
    const dock = detailDock(body);
    if (dock) {
      dock.hidden = !available;
    }
  };
  var retryButton = (onRetry) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "basic-button vst-catalog-retry";
    button.textContent = "Retry";
    button.addEventListener("click", onRetry);
    return button;
  };
  var renderBlockingArchitectureCatalogStatus = (body, snapshot, onRetry) => {
    if (snapshot.catalog) {
      setDockAvailable(body, true);
      return false;
    }
    setDockAvailable(body, false);
    body.replaceChildren();
    const status = document.createElement("section");
    status.className = "vst-catalog-status";
    status.setAttribute("role", "status");
    status.dataset.catalogStatus = snapshot.status;
    const title = document.createElement("strong");
    title.className = "vst-catalog-status-title";
    if (snapshot.status === "loading") {
      title.textContent = "Loading video model capabilities…";
      status.setAttribute("aria-busy", "true");
      status.appendChild(title);
      body.appendChild(status);
      return true;
    }
    title.textContent = "Video model capabilities are unavailable";
    const explanation = document.createElement("p");
    explanation.textContent = "VideoStages cannot safely author clips until the backend catalog is available.";
    status.append(title, explanation, retryButton(onRetry));
    body.appendChild(status);
    return true;
  };
  var renderRetainedArchitectureCatalogStatus = (body, snapshot, onRetry) => {
    body.querySelector(":scope > .vst-catalog-notice")?.remove();
    if (snapshot.status !== "refreshing" && snapshot.status !== "stale") {
      return;
    }
    const notice = document.createElement("div");
    notice.className = `vst-catalog-notice vst-catalog-notice-${snapshot.status}`;
    notice.dataset.catalogStatus = snapshot.status;
    notice.setAttribute(
      "role",
      snapshot.status === "stale" ? "alert" : "status"
    );
    const text2 = document.createElement("span");
    text2.textContent = snapshot.status === "refreshing" ? "Refreshing video model capabilities…" : "Using the last known video model capabilities; refresh failed.";
    notice.appendChild(text2);
    if (snapshot.status === "stale") {
      notice.appendChild(retryButton(onRetry));
    }
    body.prepend(notice);
  };

  // frontend/architectures/diagnostics.ts
  var issue = (code, message, clipIdx, severity = "error") => ({ severity, code, message, clipIdx });
  var persistedCapabilityIssues = (clip, clipIdx, architectureId, capabilities) => {
    const diagnostics = [];
    const supports = (feature) => architectureFeatureSupport(feature, capabilities);
    const unsupported = (active, key, label, severity) => {
      if (active) {
        const effectiveSeverity = severity ?? "warning";
        diagnostics.push(
          issue(
            `architecture.unsupported.${key}`,
            effectiveSeverity === "warning" ? `Clip ${clipIdx} has ${label} saved, but its architecture cannot use it. Generation will ignore it and keep the authored setting.` : `Clip ${clipIdx} has ${label} persisted, but its architecture does not support it. Remove it or explicitly convert the clip.`,
            clipIdx,
            effectiveSeverity
          )
        );
      }
    };
    const unsupportedFeature = (active, feature) => unsupported(
      active,
      feature.replace(/[A-Z]/g, (upper) => `-${upper.toLowerCase()}`),
      ARCHITECTURE_FEATURE_LABELS[feature]
    );
    unsupportedFeature(
      !supports("frameReferences") && clip.frameRefs.length > 0,
      "frameReferences"
    );
    unsupportedFeature(
      !supports("clipReferences") && clip.references.length > 0,
      "clipReferences"
    );
    unsupportedFeature(
      !supports("referenceFraming") && clip.refFraming !== "crop",
      "referenceFraming"
    );
    unsupportedFeature(
      !supports("icLora") && clip.icLoras.length > 0,
      "icLora"
    );
    unsupportedFeature(!supports("retake") && clip.retake !== null, "retake");
    unsupportedFeature(
      !supports("promptRelay") && clip.promptWindows.length > 0,
      "promptRelay"
    );
    const activeUpscaleModes = clip.stages.filter((stage) => stage.upscale !== 1).map((stage) => upscaleModeForMethod(stage.upscaleMethod));
    if (activeUpscaleModes.includes("unsupported")) {
      diagnostics.push(
        issue(
          "architecture.unsupported.upscale",
          `Clip ${clipIdx} has stage upscaling persisted, but its upscale method is not a known method. Remove it or choose a known method.`,
          clipIdx
        )
      );
    }
    unsupportedFeature(
      !supports("latentUpscale") && activeUpscaleModes.includes("latent"),
      "latentUpscale"
    );
    unsupportedFeature(
      !supports("latentModelUpscale") && activeUpscaleModes.includes("latent-model"),
      "latentModelUpscale"
    );
    const sourceKind = audioSourceKind(clip.audioSource);
    const clipAudioCapabilitySupported = supportsClipAudio(
      capabilities.audioSourceKinds
    );
    const standaloneAudioSupported = capabilities.audioSourceKinds.includes(AUDIO_SOURCE_NATIVE);
    const selectedAudioSourceSupported = isAllowedAudioSource(
      capabilities.audioSourceKinds,
      clip.audioSource
    );
    unsupportedFeature(
      !supports("audioReuse") && clip.reuseAudio,
      "audioReuse"
    );
    unsupportedFeature(
      !supports("audioDerivedDuration") && clip.clipLengthFromAudio,
      "audioDerivedDuration"
    );
    const supportsControlSignalDerivedDuration = supports("icLora");
    unsupported(
      !supportsControlSignalDerivedDuration && clip.clipLengthFromControlNet,
      "control-signal-derived-duration",
      "Control-signal-derived clip duration"
    );
    unsupported(
      !selectedAudioSourceSupported && (sourceKind !== AUDIO_SOURCE_NATIVE || clip.uploadedAudio !== null),
      "audio-source",
      `Audio source '${sourceKind}'`,
      clipAudioCapabilitySupported ? "error" : void 0
    );
    unsupported(
      clip.saveAudioTrack && !standaloneAudioSupported,
      "audio-output",
      "Standalone audio output"
    );
    if (clip.clipLengthFromAudio && supports("audioDerivedDuration") && selectedAudioSourceSupported && !canUseClipLengthFromAudio(clip.audioSource)) {
      diagnostics.push(
        issue(
          "architecture.unusable.clip-length-from-audio",
          `Clip length from audio is persisted on Clip ${clipIdx}, but audio source '${sourceKind}' cannot supply a length. Turn it off or pick a source that can.`,
          clipIdx
        )
      );
    }
    if (clip.clipLengthFromControlNet && supportsControlSignalDerivedDuration && !hasArchitectureSlotSourcedIcLora(architectureId, clip.icLoras)) {
      diagnostics.push(
        issue(
          "architecture.unusable.clip-length-from-control-net",
          `Clip length from the control signal is persisted on Clip ${clipIdx}, but no IC-LoRA supplies one. Turn it off or add a slot-init-video IC-LoRA.`,
          clipIdx
        )
      );
    }
    return diagnostics;
  };
  var effectiveArchitectureIdForClip = (clip, catalog) => resolvedClipArchitectureId(clip, catalog) ?? "unsupported";
  var deriveArchitectureDiagnostics = (clips, resolver) => {
    const catalog = resolver.catalog;
    const diagnostics = [];
    const architectureById = new Map(
      catalog.architectures.map((entry) => [entry.id, entry])
    );
    const modelByName = new Map(
      catalog.entries.map((entry) => [entry.value, entry])
    );
    const executableClipIndexSet = new Set(executableClipIndexes(clips));
    clips.forEach((clip, clipIdx) => {
      const temporalGrid = resolver.forClip(clip).frameGridResolution;
      if (executableClipIndexSet.has(clipIdx) && temporalGrid.status === "conflict") {
        diagnostics.push(
          issue(
            "architecture.temporal-grid-conflict",
            `Clip ${clipIdx}'s active model handlers have no representable compatible temporal grid. Use stage models with compatible temporal requirements.`,
            clipIdx
          )
        );
      }
      const sourceOnly = activeStageCount(clip) === 0 && clip.initVideo !== null;
      const resolvedFirstModel = !sourceOnly && clip.stages.length > 0 ? modelByName.get(clip.stages[0].model) : void 0;
      const effectiveArchitectureId = effectiveArchitectureIdForClip(
        clip,
        catalog
      );
      const effectiveModelProfileId = resolvedFirstModel?.modelProfileId ?? clip.modelProfileId;
      const effectiveCompatibilityClassId = resolvedFirstModel?.compatibilityClassId ?? null;
      if (resolvedFirstModel?.architectureId && resolvedFirstModel.architectureId !== clip.architectureHint) {
        diagnostics.push(
          issue(
            "architecture.stale-identity-hint",
            `Clip ${clipIdx} caches architecture hint '${clip.architectureHint}', but model '${clip.stages[0].model}' resolves to '${resolvedFirstModel.architectureId}'. Generation uses the resolved architecture and preserves the authored hint.`,
            clipIdx,
            "warning"
          )
        );
      }
      if (resolvedFirstModel?.modelProfileId && resolvedFirstModel.modelProfileId !== clip.modelProfileId) {
        diagnostics.push(
          issue(
            "architecture.stale-profile-hint",
            `Clip ${clipIdx} caches model profile '${clip.modelProfileId}', but model '${clip.stages[0].model}' resolves to '${resolvedFirstModel.modelProfileId}'. Generation uses the resolved profile and preserves the authored hint.`,
            clipIdx,
            "warning"
          )
        );
      }
      if (sourceOnly) {
        if (clip.architectureHint !== NONE_ARCHITECTURE_ID || clip.modelProfileId !== NONE_ARCHITECTURE_ID) {
          diagnostics.push(
            issue(
              "architecture.source-only-requires-none",
              `Source-only Clip ${clipIdx} must use architecture and profile 'none'.`,
              clipIdx
            )
          );
        }
      }
      const architecture = sourceOnly ? architectureById.get("none") : architectureById.get(effectiveArchitectureId);
      if (architecture) {
        const capabilities = effectiveClipCapabilities(
          clip,
          architecture,
          (model) => modelByName.get(model)
        );
        if (capabilities) {
          diagnostics.push(
            ...persistedCapabilityIssues(
              clip,
              clipIdx,
              effectiveArchitectureId,
              capabilities
            )
          );
        }
      }
      let dormantArchitecture = null;
      let dormantCompatibilityClass = null;
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
          dormantCompatibilityClass = resolved.compatibilityClassId;
        }
        const mixedDormant = sourceOnly && dormantArchitecture !== null && (resolved.architectureId !== dormantArchitecture || resolved.compatibilityClassId !== dormantCompatibilityClass);
        if (mixedDormant || !sourceOnly && (resolved.architectureId !== effectiveArchitectureId || effectiveCompatibilityClassId !== null && resolved.compatibilityClassId !== effectiveCompatibilityClassId)) {
          diagnostics.push(
            issue(
              "architecture.mixed-stage",
              sourceOnly ? `Source-only Clip ${clipIdx} has dormant stages from incompatible model families; Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to architecture '${resolved.architectureId}' and compatibility class '${resolved.compatibilityClassId}'.` : `Clip ${clipIdx} uses architecture '${effectiveArchitectureId}' and compatibility class '${effectiveCompatibilityClassId}', but Stage ${stageIdx}${stage.skipped ? " (skipped)" : ""} resolves to '${resolved.architectureId}' and '${resolved.compatibilityClassId}'.`,
              clipIdx
            )
          );
        }
        if (stage.modelProfileId !== resolved.modelProfileId || !sourceOnly && stageIdx === 0 && effectiveModelProfileId !== resolved.modelProfileId) {
          diagnostics.push(
            issue(
              "architecture.profile-mismatch",
              `Clip ${clipIdx} Stage ${stageIdx} caches a profile identity that does not match model '${stage.model}'. Generation uses the resolved profile and preserves the authored hint.`,
              clipIdx,
              "warning"
            )
          );
        }
      });
    });
    for (const seam of executableBoundaries(clips)) {
      const left = { clip: clips[seam.leftIdx], clipIdx: seam.leftIdx };
      const right = { clip: clips[seam.rightIdx], clipIdx: seam.rightIdx };
      if (effectiveArchitectureIdForClip(left.clip, catalog) !== effectiveArchitectureIdForClip(right.clip, catalog) && left.clip.boundaryOut !== "cut") {
        diagnostics.push(
          issue(
            "architecture.cross-boundary-cut-only",
            `Clip ${left.clipIdx} → ${right.clipIdx} crosses architectures; '${left.clip.boundaryOut}' remains authored, but generation safely uses a cut.`,
            left.clipIdx,
            "warning"
          )
        );
        continue;
      }
      const boundary = resolver.forBoundary(
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
            `Clip ${left.clipIdx} cannot execute a '${left.clip.boundaryOut}' boundary into Clip ${right.clipIdx}.${reason} Generation safely uses a cut while preserving the authored boundary.`,
            left.clipIdx,
            "warning"
          )
        );
      }
    }
    return diagnostics;
  };

  // frontend/authoringDiagnostics.ts
  var diagnostic = (severity, code, message, clipIdx) => ({ severity, code, message, clipIdx });
  var deriveAuthoringDiagnostics = (clips, capabilities) => {
    const diagnostics = [];
    const authoredPrefix = activePrefix(clips);
    const executable = executableClipIndexes(clips).map((clipIdx) => ({
      clip: clips[clipIdx],
      clipIdx
    }));
    diagnostics.push(
      ...deriveArchitectureDiagnostics(authoredPrefix, capabilities)
    );
    for (const { clip, clipIdx } of executable) {
      const retake = capabilities.forClip(clip).decision("retake");
      if (clip.retake !== null && retake.code) {
        diagnostics.push(
          diagnostic("error", retake.code, retake.reason, clipIdx)
        );
      }
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
      case "boundary-ref":
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
        return { list: clip.frameRefs, index: selection.refIdx };
      case "clip-ref":
        return { list: clip.references, index: selection.referenceIdx };
      case "ic-lora":
        return { list: clip.icLoras, index: selection.entryIdx };
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
      case "boundary-ref":
        return { kind: "boundary-ref", leftClipIdx: clipIdx };
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
      case "clip-ref":
        return { kind: "clip-ref", clipIdx, referenceIdx: itemIdx };
      case "ic-lora":
        return { kind: "ic-lora", clipIdx, entryIdx: itemIdx };
      case "prompt-minor":
        return { kind: "prompt-minor", clipIdx, windowIdx: itemIdx };
      default:
        return withClipIndex(selection, clipIdx);
    }
  };
  var itemFallback = (selection, clipIdx) => selection.kind === "clip" || selection.kind === "ic-lora" ? { kind: "clip", clipIdx, stageIdx: 0 } : { kind: "none" };
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
      case "boundary-ref":
        return a.leftClipIdx === b.leftClipIdx;
      case "audio-track":
        return a.trackIdx === b.trackIdx;
      case "clip":
        return a.clipIdx === b.clipIdx && a.stageIdx === b.stageIdx;
      case "ref":
        return a.clipIdx === b.clipIdx && a.refIdx === b.refIdx;
      case "clip-ref":
        return a.clipIdx === b.clipIdx && a.referenceIdx === b.referenceIdx;
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
  var clipIdxOf = (sel) => sel.kind === "none" || sel.kind === "boundary" || sel.kind === "boundary-ref" || sel.kind === "audio-track" ? null : sel.clipIdx;
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
  var timelineClipEdges = (clips, timing) => {
    if (timing) {
      const boundaryAfter = new Map(
        timing.boundaries.map((boundary) => [boundary.leftIdx, boundary])
      );
      const boundaryBefore = new Map(
        timing.boundaries.map((boundary) => [boundary.rightIdx, boundary])
      );
      const edges2 = [0];
      let cursor2 = 0;
      for (const clipIdx of timing.executableClipIndexes) {
        const duration = (timing.clipFrames[clipIdx] ?? 0) / timing.fps;
        const incoming = boundaryBefore.get(clipIdx);
        const outgoing = boundaryAfter.get(clipIdx);
        const trimBefore = incoming?.effectiveMode === "crossfade" ? incoming.overlapSeconds / 2 : 0;
        const trimAfter = outgoing?.effectiveMode === "crossfade" ? outgoing.overlapSeconds / 2 : outgoing?.timelineReductionSeconds ?? 0;
        const editEnd = cursor2 + Math.max(0, duration - trimBefore - trimAfter);
        edges2.push(cursor2, editEnd);
        if (outgoing && outgoing.overlapSeconds > 0) {
          edges2.push(
            outgoing.effectiveMode === "continue" ? editEnd - outgoing.overlapSeconds : editEnd - outgoing.overlapSeconds / 2,
            outgoing.effectiveMode === "continue" ? editEnd : editEnd + outgoing.overlapSeconds / 2
          );
        }
        cursor2 = editEnd;
      }
      edges2.push(timing.outputSeconds);
      return edges2.sort((left, right) => left - right).filter(
        (edge, index, sorted) => index === 0 || Math.abs(edge - sorted[index - 1]) > 1e-9
      );
    }
    const edges = [0];
    let cursor = 0;
    for (const clip of clips) {
      cursor += Math.max(0, clip.duration || 0);
      edges.push(cursor);
    }
    return edges;
  };

  // frontend/boundaryPlan.ts
  var boundaryPlanForClips = (clips, fps, resolveConstraints = (clip, _index, mode) => {
    const generic = boundaryWindowConstraints(null);
    const persisted = Math.trunc(Number(clip.boundaryOutOverlap));
    return {
      ...generic,
      defaultFrames: mode === "cut" || !Number.isFinite(persisted) || persisted <= 0 ? generic.defaultFrames : persisted
    };
  }, resolveFrameGrid = () => NEUTRAL_FRAME_GRID) => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const zeroBoundaries = () => new Array(boundaryCount).fill(0);
    if (count < 2) {
      return {
        overlaps: zeroBoundaries(),
        continuityWindows: zeroBoundaries(),
        fallback: false
      };
    }
    const modes = [];
    for (let i = 0; i < count - 1; i++) {
      const b = clips[i].boundaryOut ?? "cut";
      modes[i] = b;
    }
    const frames = clips.map(
      (clip, index) => framesForClip(clip.duration, fps, resolveFrameGrid(clip, index))
    );
    const constraints = clips.map(
      (clip, index) => resolveConstraints(clip, index, clip.boundaryOut ?? "cut")
    );
    const prefs = clips.map(
      (clip, index) => normalizeBoundaryWindow(clip.boundaryOutOverlap, constraints[index])
    );
    const hasRequestedBoundary = modes.some((mode) => mode !== "cut");
    const active = (index) => modes[index] === "crossfade" || modes[index] === "continue" && constraints[index].continueMode === "overlap";
    const trim = (index) => modes[index] === "continue" ? constraints[index].continueMode === "overlap" ? prefs[index] + constraints[index].continuityExtraFrames : 0 : modes[index] === "crossfade" ? prefs[index] : 0;
    const hasBudgetedOverlap = modes.some((_mode, index) => active(index));
    const continuityWindows = () => modes.slice(0, boundaryCount).map(
      (mode, index) => mode === "continue" ? constraints[index].continueMode === "reference" ? prefs[index] : trim(index) : 0
    );
    if (!hasBudgetedOverlap) {
      return {
        overlaps: zeroBoundaries(),
        continuityWindows: continuityWindows(),
        fallback: false
      };
    }
    while (true) {
      let overBudgetClip = -1;
      for (let i = 0; i < count; i++) {
        const left = i > 0 ? trim(i - 1) : 0;
        const right = i < boundaryCount ? trim(i) : 0;
        const incomingHandle = i > 0 && modes[i - 1] === "continue" && constraints[i - 1].continueMode === "overlap" ? prefs[i - 1] : 0;
        if (left + right > frames[i] + incomingHandle - 1) {
          overBudgetClip = i;
          break;
        }
      }
      if (overBudgetClip < 0) break;
      const candidate = overBudgetClip < boundaryCount && active(overBudgetClip) ? overBudgetClip : overBudgetClip > 0 && active(overBudgetClip - 1) ? overBudgetClip - 1 : -1;
      if (candidate < 0) {
        for (let i = 0; i < boundaryCount; i++) {
          if (active(i)) modes[i] = "cut";
        }
        return {
          overlaps: zeroBoundaries(),
          continuityWindows: continuityWindows(),
          fallback: !modes.some((mode) => mode !== "cut")
        };
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
      continuityWindows: continuityWindows(),
      fallback: hasRequestedBoundary && !modes.some((mode) => mode !== "cut")
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
  var formatTimeLabel = (seconds, unit, fps) => {
    if (unit === "frames") {
      return `${Math.round((seconds || 0) * safeFps(fps))}f`;
    }
    const rounded = Math.round((seconds || 0) * 10) / 10;
    return Number.isInteger(rounded) ? `${rounded}s` : `${rounded.toFixed(1)}s`;
  };
  var formatSecondsTenth = (seconds) => `${(Math.round((Number.isFinite(seconds) ? seconds : 0) * 10) / 10).toFixed(1)}s`;
  var formatOverlapSeconds = (frames, fps) => formatSecondsTenth(frames / Math.max(1, fps));
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
      return MEDIA_SOURCE_REFINER;
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

  // frontend/timelineTiming.ts
  var resolveTimelineTiming = (clips, rawFps, capabilities) => {
    const fps = safeFps(rawFps);
    const indexes = executableClipIndexes(clips);
    const executable = new Set(indexes);
    const compacted = indexes.map((clipIdx, position) => {
      const clip = clips[clipIdx];
      const requested = clip.boundaryOut ?? "cut";
      let effective = position < indexes.length - 1 ? capabilities?.forBoundaryIndex(clips, clipIdx).effective(requested) ?? requested : "cut";
      const target = clips[indexes[position + 1]];
      if (effective === "continue" && (target?.clipLengthFromAudio === true || target?.clipLengthFromControlNet === true)) {
        effective = "cut";
      }
      return { ...clip, boundaryOut: effective };
    });
    const plan = capabilities ? boundaryPlanForClips(
      compacted,
      fps,
      (_left, position, mode) => capabilities.forBoundaryIndex(clips, indexes[position]).windowConstraints(mode),
      (clip) => capabilities.forClip(clip).frameGrid
    ) : boundaryPlanForClips(compacted, fps);
    const seams = executableBoundaries(clips);
    const boundaries = seams.map((seam) => {
      const requestedMode = clips[seam.leftIdx].boundaryOut ?? "cut";
      const policyEffective = compacted[seam.position].boundaryOut ?? "cut";
      const overlapFrames = Math.max(0, plan.overlaps[seam.position] ?? 0);
      const continuityWindowFrames = Math.max(
        0,
        plan.continuityWindows[seam.position] ?? 0
      );
      const effectiveMode = overlapFrames > 0 || continuityWindowFrames > 0 ? policyEffective : "cut";
      const continuityExtraFrames = effectiveMode === "continue" ? capabilities?.forBoundaryIndex(clips, seam.leftIdx).windowConstraints(effectiveMode).continuityExtraFrames ?? 1 : 0;
      const handleFrames = effectiveMode === "continue" ? Math.max(0, overlapFrames - continuityExtraFrames) : 0;
      const timelineReductionFrames = Math.max(
        0,
        overlapFrames - handleFrames
      );
      return {
        ...seam,
        requestedMode,
        effectiveMode,
        continuityWindowFrames,
        continuityWindowSeconds: continuityWindowFrames / fps,
        overlapFrames,
        overlapSeconds: overlapFrames / fps,
        handleFrames,
        handleSeconds: handleFrames / fps,
        timelineReductionFrames,
        timelineReductionSeconds: timelineReductionFrames / fps
      };
    });
    const clipFrames = clips.map(
      (clip, clipIdx) => executable.has(clipIdx) ? framesForClip(
        clip.duration,
        fps,
        capabilities?.forClip(clip).frameGrid ?? NEUTRAL_FRAME_GRID
      ) : 0
    );
    const generatedFrames = indexes.reduce((sum, clipIdx) => sum + clipFrames[clipIdx], 0) + boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const joinFrames = boundaries.reduce(
      (sum, boundary) => sum + boundary.overlapFrames,
      0
    );
    const outputFrames = Math.max(0, generatedFrames - joinFrames);
    return {
      fps,
      executableClipIndexes: indexes,
      clipFrames,
      boundaries,
      authoredSeconds: indexes.reduce(
        (sum, clipIdx) => sum + Math.max(0, clips[clipIdx].duration || 0),
        0
      ),
      generatedFrames,
      joinFrames,
      joinSeconds: joinFrames / fps,
      outputFrames,
      outputSeconds: outputFrames / fps,
      outputGeometryAvailable: indexes.length === clips.length
    };
  };
  var boundaryImpactForLeftClip = (timing, leftClipIdx) => timing.boundaries.find((boundary) => boundary.leftIdx === leftClipIdx) ?? null;
  var incomingReferenceContinueForClip = (clips, rawFps, capabilities, rightClipIdx) => resolveTimelineTiming(clips, rawFps, capabilities).boundaries.find(
    (boundary) => boundary.rightIdx === rightClipIdx && boundary.effectiveMode === "continue" && capabilities.forBoundaryIndex(clips, boundary.leftIdx).windowConstraints("continue").continueMode === "reference"
  ) ?? null;
  var timelineDisplaySeconds = (clips, timing) => timing.outputGeometryAvailable ? timing.outputSeconds : clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);

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
  var audioSpanWaveBarHeights = (trackIdx, count) => waveBarHeights(trackIdx * 4099 + 1, count);
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
    const timing = options?.timing;
    const useOutputGeometry = timing?.outputGeometryAvailable === true;
    const boundaryAfter = new Map(
      timing?.boundaries.map((boundary) => [boundary.leftIdx, boundary]) ?? []
    );
    const boundaryBefore = new Map(
      timing?.boundaries.map((boundary) => [boundary.rightIdx, boundary]) ?? []
    );
    const layouts = [];
    let cursorSeconds = 0;
    let cursorPx = 0;
    for (let index = 0; index < clips.length; index++) {
      const clip = clips[index];
      const durationSeconds = Math.max(0, clip.duration || 0);
      const frameCount = timing?.clipFrames[index] ?? 0;
      const incomingBoundary = boundaryBefore.get(index);
      const generationFrameCount = frameCount + (incomingBoundary?.handleFrames ?? 0);
      const generatedDurationSeconds = generationFrameCount > 0 ? generationFrameCount / (timing?.fps ?? 1) : durationSeconds;
      const outgoingBoundary = boundaryAfter.get(index);
      const incomingJoinSeconds = useOutputGeometry ? incomingBoundary?.overlapSeconds ?? 0 : 0;
      const outgoingJoinSeconds = useOutputGeometry ? outgoingBoundary?.overlapSeconds ?? 0 : 0;
      const incomingHandleSeconds = useOutputGeometry ? incomingBoundary?.handleSeconds ?? 0 : 0;
      const layoutDurationSeconds = useOutputGeometry ? frameCount / (timing?.fps ?? 1) : durationSeconds;
      const trimBefore = incomingBoundary?.effectiveMode === "crossfade" ? incomingJoinSeconds / 2 : 0;
      const trimAfter = outgoingBoundary?.effectiveMode === "crossfade" ? outgoingJoinSeconds / 2 : outgoingBoundary?.timelineReductionSeconds ?? 0;
      const timelineReductionSeconds = trimBefore + trimAfter;
      const timelineDurationSeconds = Math.max(
        0,
        layoutDurationSeconds - timelineReductionSeconds
      );
      const rawWidthPx = timelineDurationSeconds * pxPerSecond;
      const widthPx = incomingJoinSeconds > 0 || outgoingJoinSeconds > 0 ? Math.max(1, rawWidthPx) : Math.max(DEFAULT_MIN_WIDTH_PX, rawWidthPx);
      layouts.push({
        index,
        startSeconds: cursorSeconds,
        durationSeconds,
        generatedDurationSeconds,
        timelineDurationSeconds,
        incomingJoinSeconds,
        outgoingJoinSeconds,
        incomingHandleSeconds,
        timelineReductionSeconds,
        frameCount,
        startPx: cursorPx,
        widthPx,
        stageCount: (clip.stages ?? []).length,
        keyframeCount: (clip.frameRefs ?? []).length,
        skipped: clip.skipped === true
      });
      cursorSeconds += timelineDurationSeconds;
      cursorPx += timelineDurationSeconds * pxPerSecond;
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
    const createFromButton = (target) => {
      const button = config.createButtonSelector ? target.closest(config.createButtonSelector) : null;
      if (!(button instanceof HTMLElement)) {
        return false;
      }
      const lane = config.scope.resolveLane(button);
      if (lane && !(config.canCreate && !config.canCreate(lane))) {
        commitCreate(
          {
            ownerIdx: lane.ownerIdx,
            duration: lane.duration,
            laneEl: button,
            laneLeft: 0,
            startSec: 0,
            ghost: null,
            sourceRevision: currentRevision()
          },
          null
        );
      }
      return true;
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
      if (createFromButton(event.target)) {
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
      if (createFromButton(ke.target)) {
        ke.preventDefault();
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

  // frontend/timelineAudioSpanTrack.ts
  var timelineTiming = (state, capabilities) => resolveTimelineTiming(state.clips, state.fps, capabilities?.());
  var timelineDuration = (state, capabilities) => timelineTiming(state, capabilities).outputSeconds;
  var pressSpanOf = (span) => span && span.timelineStartSeconds !== null && span.timelineLengthSeconds !== null ? {
    start: span.timelineStartSeconds,
    length: span.timelineLengthSeconds,
    trim: span.sourceStartSeconds
  } : null;
  var blankTrack = () => ({
    id: createEntityId("audio_track"),
    source: { kind: "Upload", reference: "", uploadedAudio: null },
    volume: AUDIO_SPAN_VOLUME_DEFAULT,
    spans: []
  });
  var audioTrackScope = (capabilities) => ({
    read: (ownerIdx) => {
      const state = getState();
      const owner = state.audioTracks?.[ownerIdx];
      return owner ? {
        owner,
        ownerIdx,
        duration: timelineDuration(state, capabilities)
      } : null;
    },
    // The blank lane carries no index: a create appends a new track.
    resolveLane: () => {
      const state = getState();
      return {
        owner: null,
        ownerIdx: state.audioTracks?.length ?? 0,
        duration: timelineDuration(state, capabilities)
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
        duration: timelineDuration(state, capabilities),
        removeOwner: () => {
          tracks.splice(ownerIdx, 1);
          return tracks.length;
        }
      });
      if (!applied) {
        return false;
      }
      saveState(state, { origin: "audio-span-track" });
      return true;
    }
  });
  var createTimelineAudioSpanTrack = (capabilities) => createWindowTrack({
    routeId: "timeline-audio-span",
    priority: 40,
    scope: audioTrackScope(capabilities),
    spanSelector: ".vst-audio-span[data-track-idx]",
    ownerIdxAttr: "data-track-idx",
    itemIdxAttr: null,
    edgeSelector: "[data-vst-audio-span-edge]",
    edgeAttr: "data-vst-audio-span-edge",
    laneSelector: ".vst-audio-track-lane[data-vst-audio-track-add]:not([data-clip-idx])",
    createButtonSelector: ".vst-head-tag-track[data-vst-audio-track-add]",
    draggingClass: "vst-audio-span-dragging",
    ghostClass: "vst-audio-span-ghost",
    unit: "pct",
    keyboardSelect: true,
    // The span sits on the audio row; its clicks must not bubble
    // into that row's select handler.
    isolateClicks: true,
    readSpan: ({ owner }) => pressSpanOf(owner.spans[0]),
    canCreate: ({ duration }) => duration >= AUDIO_SPAN_MIN_LENGTH,
    // Spans snap to the track immediately above before falling back
    // to the clip boundaries underneath them.
    snapTargets: (ownerIdx) => {
      const state = getState();
      const timing = timelineTiming(state, capabilities);
      const above = pressSpanOf(
        ownerIdx > 0 ? state.audioTracks?.[ownerIdx - 1]?.spans[0] : void 0
      );
      return {
        primary: above ? [above.start, above.start + above.length] : [],
        fallback: timelineClipEdges(state.clips, timing)
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
      AUDIO_SPAN_MIN_LENGTH,
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
        AUDIO_SPAN_MIN_LENGTH,
        AUDIO_SPAN_DEFAULT_LENGTH
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
    // A track exists only for its spans: deleting the last one
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
      step,
      isPot: opts?.isPot
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
    action.className = `${actionSpec.variant === "interrupt" ? "interrupt-button" : "basic-button"} vst-btn-tiny vst-detail-repeating-group-action ${actionSpec.className ?? ""}`.trim();
    action.textContent = actionSpec.label;
    action.title = actionSpec.title;
    action.setAttribute("aria-label", actionSpec.title);
    if (actionSpec.active !== void 0) {
      action.setAttribute("aria-pressed", `${actionSpec.active}`);
      action.classList.toggle("vst-btn-skip-active", actionSpec.active);
    }
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
    const headerActions = spec.headerActions ?? (spec.headerAction === void 0 ? [] : [spec.headerAction]);
    if (headerActions.length > 0) {
      const actions = document.createElement("span");
      actions.className = "vst-detail-repeating-group-actions";
      for (const action of headerActions) {
        appendSectionHeaderAction(actions, action);
      }
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
    const isValidIndex = (index) => index !== null && index !== void 0 && index >= 0 && index < spec.items.length;
    const rememberedIndex = rememberedRepeaterItems.get(spec.key);
    const validRememberedIndex = isValidIndex(rememberedIndex) ? rememberedIndex : null;
    if (rememberedIndex !== void 0 && validRememberedIndex === null) {
      rememberedRepeaterItems.delete(spec.key);
      forceOpenRepeaterKeys.delete(spec.key);
    }
    const forceOpen = forceOpenRepeaterKeys.has(spec.key) && validRememberedIndex !== null;
    const defaultActiveIndex = isValidIndex(spec.defaultActiveIndex) ? spec.defaultActiveIndex : null;
    const activeIndex = forceOpen ? validRememberedIndex : explicitActiveIndex >= 0 ? explicitActiveIndex : validRememberedIndex ?? defaultActiveIndex;
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
      const onDelete = item.onDelete;
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
      // An empty repeater has no child row that can reveal its action.
      // Keep its outer group open so the Add button is immediately
      // discoverable in every panel that uses this shared primitive.
      open: forceOpen || spec.items.length === 0 || spec.open,
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
      note.textContent = "This audio track has no timeline window.";
      fields.appendChild(note);
      return fields;
    }
    const total = Math.max(AUDIO_SPAN_MIN_LENGTH, timelineDuration2(state));
    const clamped = () => clampStartLength(
      span.timelineStartSeconds ?? 0,
      span.timelineLengthSeconds ?? AUDIO_SPAN_DEFAULT_LENGTH,
      total,
      AUDIO_SPAN_MIN_LENGTH
    );
    const aceReference = track.source.kind === "AceStepFun" ? track.source.reference : "";
    const sourceSelect = buildOptionSelect(
      buildAudioTrackSourceOptions(aceReference),
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
    const volume = track.volume ?? AUDIO_SPAN_VOLUME_DEFAULT;
    const volumeSlider = buildSlider(
      "Volume",
      volume,
      AUDIO_SPAN_VOLUME_MIN,
      AUDIO_SPAN_VOLUME_MAX,
      AUDIO_SPAN_VOLUME_SLIDER_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (next) => {
            next.volume = Math.min(
              AUDIO_SPAN_VOLUME_MAX,
              Math.max(AUDIO_SPAN_VOLUME_MIN, value)
            );
          },
          `audio-track-${trackId}-volume`
        );
      },
      {
        sliderMin: AUDIO_SPAN_VOLUME_SLIDER_MIN,
        sliderMax: AUDIO_SPAN_VOLUME_SLIDER_MAX,
        numberStep: "any"
      }
    );
    volumeSlider.querySelector("input.auto-slider-number")?.setAttribute("data-vst-focus-key", `audio-track-${trackId}-volume`);
    fields.appendChild(volumeSlider);
    const geometry = clamped();
    const startInput = buildNumber(
      geometry.start,
      0,
      Math.max(0, total - AUDIO_SPAN_MIN_LENGTH),
      AUDIO_SPAN_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (_next, nextSpan) => {
            const next = clampStartLength(
              value,
              nextSpan.timelineLengthSeconds ?? AUDIO_SPAN_DEFAULT_LENGTH,
              total,
              AUDIO_SPAN_MIN_LENGTH
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
      AUDIO_SPAN_STEP,
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
      AUDIO_SPAN_MIN_LENGTH,
      total,
      AUDIO_SPAN_STEP,
      (value) => {
        commitTrack(
          ctx,
          trackId,
          (_next, nextSpan) => {
            const next = clampStartLength(
              nextSpan.timelineStartSeconds ?? 0,
              value,
              total,
              AUDIO_SPAN_MIN_LENGTH
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
        "How long this track plays across the complete timeline."
      )
    );
    fields.dataset.vstTrackIndex = `${trackIndex}`;
    return fields;
  };
  var addAudioTrack = (ctx, state, clipWindow) => {
    const total = Math.max(AUDIO_SPAN_MIN_LENGTH, timelineDuration2(state));
    const start = Math.min(
      Math.max(0, clipWindow?.startSeconds ?? 0),
      Math.max(0, total - AUDIO_SPAN_MIN_LENGTH)
    );
    const availableLength = Math.max(
      AUDIO_SPAN_MIN_LENGTH,
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
        volume: AUDIO_SPAN_VOLUME_DEFAULT,
        spans: [
          {
            id: createEntityId("audio_span"),
            timelineStartSeconds: start,
            timelineLengthSeconds: Math.min(
              AUDIO_SPAN_DEFAULT_LENGTH,
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
      label: "Audio Tracks",
      sectionClass: "vst-audio-tracks-panel",
      open: selectedTrackIndex !== null,
      items: visibleTrackIndices.map((trackIndex) => ({
        label: `A${trackIndex + 1}`,
        focusKey: `audio-track-tab-${trackIndex}`,
        title: `Edit audio track A${trackIndex + 1}`,
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
        title: "Add an audio track spanning the timeline",
        label: "+ Add Audio Track",
        className: "vst-audio-track-add",
        onClick: () => {
          const trackIdx = addAudioTrack(ctx, state, options?.clipWindow);
          setSelection({ kind: "audio-track", trackIdx });
          ctx.render();
        }
      },
      remove: {
        title: activeTrackIndex === null ? "No audio track to delete" : `Delete audio track A${activeTrackIndex + 1}`,
        className: "vst-audio-track-delete"
      }
    });
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = tracks.length === 0 ? "No audio tracks." : visibleTrackIndices.length === 0 ? "No audio tracks in this clip." : "Audio tracks are cut per clip during generation; overlapping tracks mix together.";
    built.content.insertBefore(note, built.content.firstChild);
    return built.section;
  };
  var buildTimelineAudioTracksBody = (ctx, state, selection) => {
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
  var buildAudioBody = (ctx, sel, state) => {
    const clips = state.clips;
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const capabilityView = ctx.authoring().capabilities.forClip(clip);
    const audioCapabilityDecision = capabilityView.clipAudio;
    const reuseDecision = capabilityView.decision("audioReuse");
    const durationDecision = capabilityView.decision("audioDerivedDuration");
    const icLoraDecision = capabilityView.decision("icLora");
    const controlDurationDecision = icLoraDecision.supported ? icLoraDecision : {
      ...icLoraDecision,
      reason: `Control-signal-derived clip duration is not supported by ${capabilityView.architectureLabel}.`
    };
    const controlSignalEnabled = hasArchitectureSlotSourcedIcLora(
      capabilityView.architectureId,
      clip.icLoras
    );
    const controlDurationIssueDecision = clip.clipLengthFromControlNet && !controlDurationDecision.supported ? controlDurationDecision : clip.clipLengthFromControlNet && !controlSignalEnabled ? {
      ...controlDurationDecision,
      supported: false,
      reason: "No IC-LoRA supplies a ControlNet 1-3 drive source for clip duration."
    } : null;
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
      controlNetEnabled: capabilityView.audioSourceKinds.includes(
        AUDIO_SOURCE_CONTROLNET
      ),
      allowedKinds: capabilityView.audioSourceKinds
    });
    const source = options.find((option) => option.value === clip.audioSource)?.value ?? clip.audioSource ?? "";
    const selectedAudioSourceAllowed = isAllowedAudioSource(
      capabilityView.audioSourceKinds,
      source
    );
    const audioDecision = audioCapabilityDecision.supported && !selectedAudioSourceAllowed ? {
      ...audioCapabilityDecision,
      supported: false,
      reason: `Audio source '${source}' is not supported by ${capabilityView.architectureLabel}.`
    } : audioCapabilityDecision;
    const canLength = canUseClipLengthFromAudio(source);
    const canDeriveDuration = durationDecision.supported && selectedAudioSourceAllowed && canLength;
    const durationIssueDecision = selectedAudioSourceAllowed && !canDeriveDuration ? durationDecision.supported ? {
      ...durationDecision,
      supported: false,
      reason: `Audio source '${source}' cannot determine video duration.`
    } : durationDecision : null;
    const durationUnavailableReason = durationIssueDecision?.reason ?? (!audioDecision.supported ? audioDecision.reason : "");
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
    reuseRow.classList.add("vst-detail-audio-reuse");
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
      clip.clipLengthFromAudio === true,
      (value) => {
        commitAudio((c) => {
          c.clipLengthFromAudio = value;
        });
      },
      {
        disabled: !canDeriveDuration,
        help: "Set the clip's duration to match the length of its audio instead of a fixed value. Available only for sources with a known length." + (durationUnavailableReason ? ` ${durationUnavailableReason}` : "")
      }
    );
    lengthRow.classList.add("vst-detail-audio-derived-duration");
    base.appendChild(lengthRow);
    if (clip.clipLengthFromAudio && durationIssueDecision) {
      lengthRow.appendChild(buildCapabilityNotice(durationIssueDecision));
      lengthRow.appendChild(
        buildCapabilityRepairButton({
          label: "Remove unsupported audio-derived duration",
          className: "vst-detail-delete",
          onRepair: () => {
            ctx.commit((items) => {
              const target = items[clipIdx];
              if (target) {
                target.clipLengthFromAudio = false;
              }
            });
            ctx.render();
          }
        })
      );
    }
    if (clip.clipLengthFromControlNet) {
      const controlLengthStatus = document.createElement("div");
      controlLengthStatus.className = "vst-detail-control-signal-derived-duration";
      const status = document.createElement("p");
      status.className = "vst-detail-note";
      status.textContent = "Control-signal-derived clip duration is active.";
      controlLengthStatus.appendChild(status);
      if (controlDurationIssueDecision) {
        controlLengthStatus.appendChild(
          buildCapabilityNotice(controlDurationIssueDecision)
        );
      }
      controlLengthStatus.appendChild(
        buildCapabilityRepairButton({
          label: "Remove control-signal-derived duration",
          className: "vst-detail-delete vst-remove-control-signal-derived-duration",
          onRepair: () => {
            ctx.commit((items) => {
              const target = items[clipIdx];
              if (target) {
                target.clipLengthFromControlNet = false;
              }
            });
            ctx.render();
          }
        })
      );
      base.appendChild(controlLengthStatus);
    }
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
        keep: [
          ...CAPABILITY_REPAIR_SELECTORS,
          ".vst-detail-audio-reuse",
          ".vst-detail-audio-derived-duration",
          ".vst-detail-control-signal-derived-duration"
        ],
        repair: {
          label: "Remove unsupported clip audio",
          className: "vst-remove-unsupported-audio",
          onRepair: () => {
            ctx.structuralCommit((items) => {
              const target = items[clipIdx];
              if (!target) {
                return null;
              }
              target.audioSource = defaultAuthoringAudioSource(
                capabilityView.audioSourceKinds
              );
              target.uploadedAudio = null;
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

  // frontend/skipVocabulary.ts
  var skipGlyph = (skipped) => skipped ? "⟲" : "⏭︎";
  var skipTitle = (subject, skipped) => `${skipped ? "Re-enable" : "Skip"} ${subject}`;

  // frontend/timelineView/rendering.ts
  var laneVisible = (clip, feature, persisted, capabilities) => {
    const decision = capabilities?.forClip(clip).decision(feature);
    return decision === void 0 || decision.supported || decision.code !== "" || persisted;
  };
  var clipInnerWidth = (widthPx) => Math.max(1, widthPx - 2);
  var backgroundImageDataAttr = (source) => ` data-vst-background-image="${escapeHtml(source)}"`;
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
    return `<div class="${options.className}${options.extraClassName ? ` ${options.extraClassName}` : ""}" ${options.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeHtml(options.title)}" aria-label="${escapeHtml(options.ariaLabel)}"><span class="${options.className}-resize ${options.className}-resize-l" ${options.edgeAttr}="left" aria-hidden="true"></span>` + (options.decoration ?? "") + `<span class="${options.labelClass}">${escapeHtml(options.label)}</span><span class="${options.className}-resize ${options.className}-resize-r" ${options.edgeAttr}="right" aria-hidden="true"></span></div>`;
  };
  var headTag = (kind, label, options) => {
    const action = options?.action;
    const classes = `vst-head-tag vst-head-tag-${kind}` + (options?.active ? " vst-head-tag-active" : "") + (options?.muted ? " vst-head-tag-muted" : "") + (action ? " vst-head-tag-action" : "");
    const style = options?.style ? ` style="${options.style}"` : "";
    return `<div class="${classes}"${style} ${action ? `${action} role="button" tabindex="0"` : `aria-hidden="true"`}><span class="vst-head-tag-pill">${label}</span><span class="vst-head-tag-tick"></span></div>`;
  };
  var renderTrackHead = (iconClass, icon, title, tags) => `<div class="vst-track-head"><div class="vst-head-top"><div class="vst-track-icon ${iconClass}" aria-hidden="true">${icon}</div><div class="vst-track-label"><strong>${title}</strong></div></div>` + (tags ? `<div class="vst-head-tags">${tags}</div>` : "") + `</div>`;

  // frontend/timelineView/regionRenderer.ts
  var refFrame = (ref) => Math.max(0, ref.frame ?? 0);
  var hasRetake = (clip) => clip.retake != null;
  var retakeLaneVisible = (clip, capabilities) => laneVisible(clip, "retake", hasRetake(clip), capabilities);
  var renderRegionThumb = (clip) => {
    const withImage = (clip.frameRefs ?? []).filter(
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
  var renderRetakeOverlay = (clip, clipIdx, durationSeconds, editable) => {
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
      title: editable ? `${label} · drag to move/resize · Shift+click to delete` : `${label} · unsupported by this architecture · click to inspect or Shift+click to delete`,
      ariaLabel: editable ? label : `${label}. Unsupported persisted retake; available for inspection or removal.`,
      startSeconds: retake.startSeconds,
      lengthSeconds: retake.lengthSeconds,
      durationSeconds
    });
  };
  var renderKeyframes = (clip, clipIdx, durationSeconds, fps, unit) => {
    const frameRefs = clip.frameRefs ?? [];
    if (frameRefs.length === 0) {
      return "";
    }
    const markers = frameRefs.map((ref, refIdx) => {
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
      return `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeHtml(title)}" aria-hidden="true"><span class="vst-key-dot" aria-hidden="true"></span></span>`;
    }).join("");
    return `<div class="vst-keys" title="Frame reference markers">${markers}</div>`;
  };
  var renderBadges = (clip, clipIdx) => {
    const firstStage = (clip.stages ?? [])[0];
    if (!firstStage) {
      return `<div class="vst-badges"></div>`;
    }
    const model = firstStage.model ?? "";
    const title = `Clip model: ${`${model}`.trim() || "(default)"} — click to change (applies to Stage 0)`;
    const modelBadge = `<span class="vst-badge vst-badge-model" data-vst-model data-clip-idx="${clipIdx}" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(title)}">${escapeHtml(shortModelName(model))}</span>`;
    const icLoraCount = (clip.icLoras ?? []).length;
    const icLoraTitle = `${icLoraCount} IC-LoRA${icLoraCount === 1 ? "" : "s"} on this clip — edit in the clip panel`;
    const icLoraBadge = icLoraCount > 0 ? `<span class="vst-badge vst-badge-iclora" title="${escapeHtml(icLoraTitle)}" aria-label="${escapeHtml(icLoraTitle)}">IC×${icLoraCount}</span>` : "";
    return `<div class="vst-badges">${modelBadge}${icLoraBadge}</div>`;
  };
  var renderStageChips = (clip, clipIdx) => (clip.stages ?? []).map((stage, stageIdx) => {
    const skipped = stage?.skipped === true;
    const skippedClass = skipped ? " vst-stage-chip-skipped" : "";
    const title = `${stageChipTitle(stage, stageIdx)}${skipped ? " (skipped)" : ""} · click to edit${stageIdx === 0 ? "" : " · Shift+click to delete"}`;
    const label = `${skipped ? "⊘ " : ""}${stageChipLabel(stageIdx)}`;
    return `<span class="vst-chip vst-stage-chip${skippedClass}" data-vst-stage data-clip-idx="${clipIdx}" data-stage-idx="${stageIdx}" role="button" tabindex="0" title="${escapeHtml(title)}">${escapeHtml(label)}</span>`;
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
  var renderBoundarySeams = (clips, layouts, capabilities, timing, pxPerSecond = 1) => executableBoundaries(clips).flatMap((seam) => {
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
    const policyEffective = capability?.effective(value) ?? value;
    const impact = timing?.boundaries.find(
      (boundary) => boundary.leftIdx === seam.leftIdx
    );
    const effective = impact?.effectiveMode ?? policyEffective;
    const glyph = BOUNDARY_GLYPH[effective] ?? BOUNDARY_GLYPH.cut;
    const label = BOUNDARY_LABEL[value] ?? BOUNDARY_LABEL.cut;
    const effectiveLabel = BOUNDARY_LABEL[effective];
    const fallback = value === effective ? "" : ` Requested ${label}; effective ${effectiveLabel}.`;
    const shared = impact && impact.overlapFrames > 0 ? ` ${impact.overlapFrames} frames (${formatSecondsTenth(impact.overlapSeconds)}) shared.` : "";
    const duration = impact && impact.overlapFrames > 0 ? formatSecondsTenth(impact.overlapSeconds) : "";
    const sharedPixels = impact && impact.overlapFrames > 0 ? impact.overlapSeconds * pxPerSecond : 0;
    const density = sharedPixels >= 88 ? "full" : sharedPixels >= 44 ? "compact" : "icon";
    const title = `Boundary clip ${seam.leftIdx} → ${seam.rightIdx}: ${label}.${fallback}${shared} Click to edit.`;
    const ariaLabel = `Clip ${seam.leftIdx} outgoing boundary: ${label}.${fallback}${shared} Click to edit.`;
    const left = layout.startPx;
    return [
      `<button type="button" class="basic-button vst-boundary-chip vst-boundary-chip-${density} vst-boundary-${effective}${value === effective ? "" : " vst-boundary-fallback"}" data-vst-boundary-chip data-vst-boundary-density="${density}" data-vst-boundary-has-duration="${duration ? "true" : "false"}" data-left-clip-idx="${seam.leftIdx}" data-right-clip-idx="${seam.rightIdx}" data-boundary="${value}" data-effective-boundary="${effective}" style="left:${left}px" title="${escapeHtml(title)}" aria-label="${escapeHtml(ariaLabel)}"><span class="vst-boundary-glyph" aria-hidden="true">${escapeHtml(glyph)}</span>` + (duration ? `<span class="vst-boundary-kind">${escapeHtml(effectiveLabel)}</span><span class="vst-boundary-divider" aria-hidden="true"></span><span class="vst-boundary-duration">${escapeHtml(duration)}</span>` : "") + `</button>`
    ];
  }).join("");
  var renderBoundaryOverlapBands = (layouts, boundaries, pxPerSecond) => boundaries.filter((boundary) => boundary.overlapFrames > 0).map((boundary) => {
    const right = layouts[boundary.rightIdx];
    if (!right) {
      return "";
    }
    const width = boundary.overlapSeconds * pxPerSecond;
    const left = boundary.effectiveMode === "continue" ? right.startPx - width : right.startPx - width / 2;
    return `<div class="vst-boundary-overlap vst-boundary-overlap-${boundary.effectiveMode}" style="left:${left}px;width:${width}px" aria-hidden="true"></div>`;
  }).join("");
  var renderRegions = (clips, layouts, fps, unit, capabilities) => layouts.map((layout) => {
    const clip = clips[layout.index];
    const skippedClass = layout.skipped ? " vst-region-skipped" : "";
    const tinyClass = layout.widthPx <= 12 ? " vst-region-tiny" : "";
    const skippedChip = layout.skipped ? `<span class="vst-chip vst-chip-skip">skipped</span>` : "";
    const authoredDurationSeconds = layout.frameCount > 0 ? layout.frameCount / fps : layout.durationSeconds;
    const duration = escapeHtml(
      unit === "frames" && layout.frameCount > 0 ? `${layout.frameCount}f` : formatTimeLabel(authoredDurationSeconds, unit, fps)
    );
    const sharedAllocation = layout.timelineReductionSeconds;
    const timingTitle = layout.incomingHandleSeconds > 0 ? ` · ${formatSecondsTenth(layout.generatedDurationSeconds)} generated · ${formatSecondsTenth(layout.timelineDurationSeconds)} timeline · ${formatSecondsTenth(layout.incomingHandleSeconds)} handle` : sharedAllocation > 0 ? ` · ${formatSecondsTenth(layout.generatedDurationSeconds)} generated · ${formatSecondsTenth(layout.timelineDurationSeconds)} unique · ${formatSecondsTenth(sharedAllocation)} shared` : ` · ${duration}`;
    const skipLabel = skipTitle("clip", layout.skipped);
    const skipMark = skipGlyph(layout.skipped);
    const firstClip = layout.index === 0;
    const controls = firstClip ? "" : `<div class="vst-region-controls"><button type="button" class="vst-region-btn${layout.skipped ? " vst-region-btn-active" : ""}" data-vst-region-action="skip" title="${skipLabel}" aria-label="${skipLabel}">${skipMark}</button></div>`;
    const resizeGrip = lengthDerived(clip) ? "" : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
    const width = clipInnerWidth(layout.widthPx);
    const retakeDecision = capabilities?.forClip(clip).decision("retake");
    const retakeSupported = retakeDecision?.supported ?? true;
    const canAddRetake = retakeSupported && !clip.retake;
    const retakeLaneAttrs = canAddRetake ? " data-vst-retake-add" : retakeSupported ? " data-vst-retake-full" : ' data-vst-capability-disabled="retake"';
    const retakeLaneTitle = canAddRetake ? "Click empty space to add a retake window" : retakeSupported ? "This clip already has a retake window" : retakeDecision?.reason ?? "Retake is unavailable for this clip";
    return `<div class="vst-region${skippedClass}${tinyClass}" style="left:${layout.startPx}px;width:${width}px;--clip-hue:${clipHueCss(clip.hue)}" data-clip-idx="${layout.index}" data-vst-join-trim-seconds="${sharedAllocation}" title="Clip ${layout.index}${timingTitle} · Click to edit${firstClip ? "" : " · Shift+click to delete"}">` + renderRegionThumb(clip) + renderRetakeRegionShade(clip, layout.durationSeconds) + renderKeyframes(
      clip,
      layout.index,
      authoredDurationSeconds,
      fps,
      unit
    ) + `<div class="vst-region-head"><span class="vst-region-name">Clip ${layout.index}</span>` + renderStageChips(clip, layout.index) + `<span class="vst-chip" title="Keyframes">◆ ${layout.keyframeCount}</span>` + skippedChip + `<span class="vst-region-dur">${duration}</span></div>` + renderBadges(clip, layout.index) + controls + resizeGrip + `</div>` + (retakeLaneVisible(clip, capabilities) ? `<div class="vst-retake-lane${retakeSupported ? "" : " vst-capability-disabled"}"${retakeLaneAttrs}${retakeSupported || clip.retake ? "" : ' aria-disabled="true"'} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(retakeLaneTitle)}">` + renderRetakeOverlay(
      clip,
      layout.index,
      layout.durationSeconds,
      retakeSupported
    ) + `</div>` : "");
  }).join("");
  var renderVideoTrackRow = (clips, layouts, fps, unit, capabilities, timing, pxPerSecond = 1) => {
    const retakeTrack = clips.some(
      (clip) => retakeLaneVisible(clip, capabilities)
    );
    const head = renderTrackHead(
      "vst-track-icon-video",
      "▶",
      "Video",
      headTag("clip", "Clip", { active: true }) + (retakeTrack ? headTag("retake", "Retake", {
        active: clips.some(hasRetake)
      }) : "")
    );
    return `<div class="vst-track-row vst-track-video${retakeTrack ? "" : " vst-no-retake"}">${head}<div class="vst-track-cell">` + renderRegions(clips, layouts, fps, unit, capabilities) + renderBoundaryOverlapBands(
      layouts,
      timing?.outputGeometryAvailable === true ? timing.boundaries : [],
      pxPerSecond
    ) + renderBoundarySeams(clips, layouts, capabilities, timing, pxPerSecond) + `</div></div>`;
  };

  // frontend/detailStrip/boundaryPanel.ts
  var buildBoundaryBody = (ctx, sel, state) => {
    const clips = state.clips;
    const { leftClipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    const fields = document.createElement("div");
    fields.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const value = clip?.boundaryOut ?? "cut";
    const capabilities = ctx.authoring().capabilities;
    const capability = capabilities.forBoundaryIndex(clips, leftClipIdx);
    const seam = executableBoundaryForLeftClip(clips, leftClipIdx);
    const fps = Math.round(safeFps(state.fps));
    const overlapPolicy = capability.windowConstraints(value);
    const isReferenceContinue = value === "continue" && overlapPolicy.continueMode === "reference";
    const referenceTailLabel = clip?.boundaryOutReferenceIncludeSoundtrack === false ? "video tail" : "video and audio tail";
    const carryTargetHasStage = capability.rightClipIdx !== null && capabilities.forClip(clips[capability.rightClipIdx]).hasGenerationStage;
    const carryAudioSupported = clip !== void 0 && capabilities.forClip(clip).decision("audioBoundaryCarry").supported;
    const carryAudioActive = carryAudioSupported && !isReferenceContinue && clip?.boundaryOutCarryAudio === true && carryTargetHasStage;
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
        "Join",
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
          code: ""
        })
      );
    }
    if (value !== "cut" && capability.modes.includes(value)) {
      const overlapValue = clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames;
      const overlapSpecs = boundaryWindowChoices(
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
              c.boundaryOutOverlap = normalizeBoundaryWindow(
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
          isReferenceContinue ? "Reference window" : "Overlap",
          overlapSelect,
          void 0,
          isReferenceContinue ? `Requested duration of the previous clip's ${referenceTailLabel} used as reference context.` : "How many frames the clips share at the join. For Continue this is frozen context; for Crossfade it is the dissolve length."
        )
      );
      if (carryAudioSupported && !isReferenceContinue) {
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
              help: "Preserve this clip's audio tail at the start of the next clip's generation, then let its model generate the continuation. The next clip needs an active stage."
            }
          )
        );
      }
    }
    const timing = seam === null ? null : resolveTimelineTiming(clips, fps, capabilities);
    const impact = timing === null ? null : boundaryImpactForLeftClip(timing, leftClipIdx);
    const plannedWindow = impact === null ? 0 : value === "continue" ? impact.continuityWindowFrames : impact.overlapFrames;
    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    const effective = capability.effective(value);
    if (effective !== value) {
      info.classList.add("vst-boundary-warn");
      info.textContent = `${BOUNDARY_LABEL[value]} is preserved for repair, but this join executes as ${BOUNDARY_LABEL[effective].toLowerCase()}. ${capability.reason}`;
    } else if (value === "cut") {
      info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
      const window2 = plannedWindow;
      if (window2 <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = `This continue will fall back to a cut — a clip is too short for the requested ${isReferenceContinue ? "reference window" : "overlap"}.`;
      } else {
        const requested = (clip?.boundaryOutOverlap ?? overlapPolicy.defaultFrames) + (overlapPolicy.continueMode === "overlap" ? overlapPolicy.continuityExtraFrames : 0);
        let text2 = isReferenceContinue ? `Continue — requests up to ~${formatOverlapSeconds(window2, fps)} of this clip's ${referenceTailLabel} as reference context.` : `Continue — the next clip is generated with this clip's last ${window2} frame${window2 === 1 ? "" : "s"} (~${formatOverlapSeconds(window2, fps)}) as frozen context, and the merge collapses the duplicated frames.`;
        if (window2 < requested) {
          text2 += " The window was reduced to fit clip or model limits.";
        }
        if (carryAudioActive) {
          text2 += " Its audio tail becomes preserved opening context for the next clip's generated audio.";
        }
        info.textContent = text2;
      }
    } else {
      const overlapFrames = plannedWindow;
      if (overlapFrames <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
      } else {
        const requested = clip?.boundaryOutOverlap ?? capability.windowConstraints(value).defaultFrames;
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
    if (timing !== null && impact !== null) {
      const leftFrames = timing.clipFrames[impact.leftIdx] ?? 0;
      const rightFrames = timing.clipFrames[impact.rightIdx] ?? 0;
      const combinedFrames = Math.max(
        0,
        leftFrames + rightFrames + impact.handleFrames - impact.overlapFrames
      );
      const impactBlock = document.createElement("div");
      impactBlock.className = "vst-boundary-impact";
      const heading = document.createElement("div");
      heading.className = "vst-detail-crumb vst-detail-subsection-crumb vst-boundary-impact-title";
      heading.textContent = "Output impact";
      impactBlock.appendChild(heading);
      const rows = document.createElement("div");
      rows.className = "vst-boundary-impact-rows";
      const addRow = (label, frames, sign = "", strong = false) => {
        const row = document.createElement("div");
        row.className = `vst-boundary-impact-row${strong ? " vst-boundary-impact-total" : ""}`;
        const name = document.createElement("span");
        name.textContent = label;
        const value2 = document.createElement("span");
        value2.textContent = `${sign}${frames}f · ${sign}${formatSecondsTenth(frames / fps)}`;
        row.append(name, value2);
        rows.appendChild(row);
      };
      addRow(`Clip ${impact.leftIdx}`, leftFrames);
      addRow(`Clip ${impact.rightIdx}`, rightFrames, "+");
      if (impact.handleFrames > 0) {
        addRow("Incoming Continue handle", impact.handleFrames, "+");
      }
      addRow(
        `${BOUNDARY_LABEL[impact.effectiveMode]} shared`,
        impact.overlapFrames,
        impact.overlapFrames > 0 ? "−" : ""
      );
      addRow("Pair after this join", combinedFrames, "", true);
      impactBlock.appendChild(rows);
      if (value === "continue" && impact.overlapFrames > 0 && overlapPolicy.continuityExtraFrames > 0) {
        const note = document.createElement("div");
        note.className = "vst-boundary-impact-note";
        const selectedFrames = impact.handleFrames;
        note.textContent = `${selectedFrames}f selected + ${overlapPolicy.continuityExtraFrames} LTX continuation frame = ${impact.overlapFrames}f effective shared window.`;
        impactBlock.appendChild(note);
      }
      fields.appendChild(impactBlock);
    }
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
  var IC_LORA_AUTO_HINT_ATTR = "data-vst-iclora-auto";
  var statuses = /* @__PURE__ */ new Map();
  var clearIcLoraAutoFailure = (presetId) => {
    if (statuses.get(`${presetId ?? ""}`.trim())?.state === "error") {
      statuses.delete(`${presetId ?? ""}`.trim());
    }
  };
  var installedAutoWeights = (preset, installedLoras) => {
    const wanted = [
      icLoraAutoModelName(preset).toLowerCase(),
      icLoraLegacyAutoModelName(preset).toLowerCase()
    ];
    return installedLoras.find(
      (name) => wanted.includes(`${name}`.toLowerCase())
    ) ?? null;
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
    if (!preset || installedAutoWeights(preset, installedLoras) || statuses.has(preset.id)) {
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
    const installed = installedAutoWeights(preset, installedLoras);
    if (installed) {
      return `Using ${installed}.`;
    }
    const status = statuses.get(preset.id);
    return status ? statusTextFor(preset, status) : "Preparing preset weights download…";
  };

  // frontend/architectures/ltx2/icLoraPanel.ts
  var buildIcLorasSection = (context, clip, clipIdx, defaults, selectedEntryIdx = null, open = selectedEntryIdx !== null) => {
    const authoring = context.authoring();
    const clipCapabilities = authoring.capabilities.forClip(clip);
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
              const defaultPreset = findIcLoraPreset(
                IC_LORA_DEFAULT_PRESET_ID
              );
              if (!target || !defaultPreset || !authoring.capabilities.forClip(target).decision("icLora").supported) {
                return null;
              }
              const defaultContract = icLoraDriveMediaContract(defaultPreset);
              target.icLoras.push(
                defaultIcLora({
                  lora: IC_LORA_AUTO,
                  preset: defaultPreset.id,
                  strength: defaultPreset.strength,
                  controlType: defaultPreset.controlType,
                  driveData: defaultContract.driveData,
                  driveMediaKinds: [
                    ...defaultContract.acceptedKinds
                  ]
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
      {
        const fields = col;
        const preset = findIcLoraPreset(entry.preset);
        const driveMediaKinds = entry.driveMediaKinds;
        const audioDriveMedia = entry.driveData === "audio";
        const presetOptions = IC_LORA_PRESETS;
        const presetSpecs = [
          {
            value: IC_LORA_PRESET_CUSTOM_ID,
            label: "Custom",
            disabled: defaults.loraValues.length === 0 && entry.preset !== IC_LORA_PRESET_CUSTOM_ID
          },
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
              } else if (value === IC_LORA_PRESET_CUSTOM_ID) {
                const customLora = defaults.loraValues[0];
                if (customLora) {
                  target.lora = customLora;
                  const initialStrength = defaultLoraWeight(
                    defaults,
                    customLora
                  );
                  const targetClip2 = clips[clipIdx];
                  for (const stage of targetClip2?.stages ?? []) {
                    stage.icLoraStrengths[entryIdx2] = normalizeStageControlNetStrengthValue(
                      initialStrength
                    );
                  }
                }
              }
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
              if (targetClip && target.driveSource === MEDIA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                target,
                targetClip,
                clipIdx,
                clips,
                authoring.generatedEntryMode
              )) {
                target.driveSource = MEDIA_SOURCE_UPLOAD;
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
        if (entry.preset === IC_LORA_PRESET_CUSTOM_ID) {
          const loraSpecs = defaults.loraValues.map(
            (value, optionIdx) => ({
              value,
              label: defaults.loraLabels[optionIdx] ?? value
            })
          );
          if (entry.lora !== IC_LORA_AUTO) {
            preserveSelectedOption(
              loraSpecs,
              entry.lora,
              "start",
              (value) => ({
                value,
                label: `${value} (unsupported persisted value)`,
                disabled: true
              })
            );
          }
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
              context.render();
            }
          );
          fields.appendChild(
            buildField(
              "LoRA",
              loraSelect,
              void 0,
              "The in-context LoRA weights that turn the drive media into conditioning."
            )
          );
        }
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
              if (targetClip && target.driveSource === MEDIA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                target,
                targetClip,
                clipIdx,
                clips,
                authoring.generatedEntryMode
              )) {
                target.driveSource = MEDIA_SOURCE_UPLOAD;
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
                  target.driveSource = MEDIA_SOURCE_UPLOAD;
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
                if (targetClip && target.driveSource === MEDIA_SOURCE_INCOMING && !canUseIncomingIcLoraDrive(
                  target,
                  targetClip,
                  clipIdx,
                  clips,
                  authoring.generatedEntryMode
                )) {
                  target.driveSource = MEDIA_SOURCE_UPLOAD;
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
            authoring.generatedEntryMode
          );
          const sourceSelect = buildOptionSelect(
            [
              { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
              {
                value: MEDIA_SOURCE_INCOMING,
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
                  if (value !== MEDIA_SOURCE_UPLOAD) {
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
        if (entry.driveData !== "none" && entry.driveSource === MEDIA_SOURCE_UPLOAD) {
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
        } else if (entry.driveSource === MEDIA_SOURCE_INCOMING) {
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
        ensureIcLoraAutoWeights(entry, defaults.loraValues, context.render);
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
      }
      return col;
    };
    return buildSection(buildEditor);
  };

  // frontend/architectures/authoringPanels.ts
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
  var buildArchitectureIcLorasSection = (context, clip, clipIdx, defaults, selectedEntryIdx = null, open = selectedEntryIdx !== null) => {
    const architectureId = context.authoring().capabilities.forClip(clip).architectureId;
    return architectureId === LTX2_ARCHITECTURE_ID ? buildIcLorasSection(
      context,
      clip,
      clipIdx,
      defaults,
      selectedEntryIdx,
      open
    ) : persistedIcLoraRemovalPanel(
      context,
      clip,
      clipIdx,
      selectedEntryIdx,
      open
    );
  };

  // frontend/timelineEdit.ts
  var pxToDuration = (px, pxPerSecond, fps) => {
    if (!Number.isFinite(px) || !Number.isFinite(pxPerSecond) || pxPerSecond <= 0) {
      return CLIP_DURATION_MIN;
    }
    const seconds = Math.max(CLIP_DURATION_MIN, px / pxPerSecond);
    return Math.max(CLIP_DURATION_MIN, snapDurationToFps(seconds, fps));
  };
  var pxToFrame = (pointerXWithinRegion, regionWidthPx, durationSeconds, fps, fromEnd, frameGrid) => {
    const safeFps2 = Number.isFinite(fps) && fps > 0 ? fps : 1;
    const authoredDuration = Number.isFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
    const frameMax = Math.max(
      REF_FRAME_MIN,
      framesForClip(authoredDuration, safeFps2, frameGrid)
    );
    if (!Number.isFinite(pointerXWithinRegion) || !Number.isFinite(regionWidthPx) || regionWidthPx <= 0) {
      return REF_FRAME_MIN;
    }
    const fraction = clamp(pointerXWithinRegion / regionWidthPx, 0, 1);
    const effectiveDuration = frameMax / safeFps2;
    const time = fraction * effectiveDuration;
    const rawFrame = fromEnd ? (effectiveDuration - time) * safeFps2 : time * safeFps2;
    return clamp(Math.round(rawFrame), REF_FRAME_MIN, frameMax);
  };
  var clampClipRefsToDuration = (clip, defaults, effectiveFps) => {
    const frameMax = getKnownReferenceFrameMax(defaults, clip, effectiveFps);
    for (const ref of clip.frameRefs) {
      ref.frame = frameMax === null ? Math.max(REF_FRAME_MIN, Math.round(ref.frame)) : clamp(ref.frame, REF_FRAME_MIN, frameMax);
    }
  };
  var applyClipDurationResize = (clip, newDuration, defaults, effectiveFps) => {
    if (clip.duration === newDuration) {
      return false;
    }
    clip.duration = newDuration;
    clampClipRefsToDuration(clip, defaults, effectiveFps);
    return true;
  };

  // frontend/detailStrip/clipBasics.ts
  var DURATION_STEP = 0.1;
  var buildClipColumn = (context, clip, clipIdx, referenceFramingState) => {
    const column = document.createElement("div");
    column.className = "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";
    const initVideoClip = !!clip.initVideo;
    const lengthReferenceIdx = clipLengthReferenceIndex(clip.references);
    const lengthDerived2 = clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true || lengthReferenceIdx >= 0 || initVideoClip;
    const durationInput = buildNumber(
      clip.duration,
      CLIP_DURATION_MIN,
      CLIP_DURATION_MAX,
      DURATION_STEP,
      (value) => {
        context.debouncedCommit("duration", (clips) => {
          const target = clips[clipIdx];
          if (target && !lengthDerived2) {
            const defaults = context.authoring().defaults;
            applyClipDurationResize(target, value, defaults);
          }
        });
      }
    );
    durationInput.setAttribute("data-vst-focus-key", "duration");
    const durationField = buildField(
      "Duration (s)",
      durationInput,
      !lengthDerived2 ? void 0 : initVideoClip ? "(derived from the source video range)" : lengthReferenceIdx >= 0 ? "(derived from a reference's media length)" : "(derived from audio/ControlNet source)"
    );
    if (lengthDerived2) {
      durationInput.disabled = true;
      durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    if (referenceFramingState?.visible) {
      const framing = buildOptionSelect(
        [
          { value: "crop", label: "Crop" },
          { value: "stretch", label: "Stretch" },
          { value: "fit", label: "Fit (black padding)" },
          { value: "fit-green", label: "Fit (green padding)" }
        ],
        clip.refFraming,
        (value) => {
          context.commit((clips) => {
            const target = clips[clipIdx];
            if (target) {
              target.refFraming = value;
            }
          });
        }
      );
      framing.dataset.vstReferenceFraming = "true";
      const field = buildField(
        "Reference resize",
        framing,
        void 0,
        "Fit (green padding) preserves aspect ratio and pads with #66FF00 so outpainting IC-LoRAs treat the padded area as empty."
      );
      if (!referenceFramingState.enabled) {
        applyPersistedCapabilityRepair(field, referenceFramingState, {
          repair: {
            label: "Reset reference resize",
            className: "vst-reset-unsupported-reference-framing",
            onRepair: () => {
              context.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                  target.refFraming = "crop";
                }
              });
            }
          }
        });
      }
      column.appendChild(field);
    }
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
    const applyStageWeights = (target, loraIdx, weight) => {
      for (const stage of target.stages) {
        stage.loraWeights[loraIdx] = weight;
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
            applyStageWeights(target, loraIdx, initialWeight);
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
              applyStageWeights(
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

  // frontend/clipMediaProbeGuard.ts
  var INIT_VIDEO_PROBE_SLOT = "init-video";
  var nextOperationId = 0;
  var currentOperations = /* @__PURE__ */ new Map();
  var findClipByStableId = (clips, clipId) => clips.find((clip) => clip.id === clipId);
  var beginClipMediaProbe = (clipId, slot, revisionAtStart) => {
    const operationId = ++nextOperationId;
    const key = `${clipId}
${slot}`;
    currentOperations.set(key, operationId);
    const release = () => {
      if (currentOperations.get(key) === operationId) {
        currentOperations.delete(key);
      }
    };
    return {
      clipId,
      claim: (currentRevision2) => {
        const current = currentRevision2 === revisionAtStart && currentOperations.get(key) === operationId;
        release();
        return current;
      },
      cancel: release
    };
  };
  var runClipMediaProbe = ({
    clipId,
    slot,
    probe,
    apply,
    onApplied
  }) => {
    const store2 = getTimelineStore();
    const operation = beginClipMediaProbe(
      clipId,
      slot,
      store2.getSnapshot().revision
    );
    void probe().then((result) => {
      if (!operation.claim(store2.revision())) {
        return;
      }
      const state = store2.getState();
      const clip = findClipByStableId(state.clips, operation.clipId);
      if (!clip) {
        return;
      }
      apply(clip, result, state);
      saveClips(state.clips, { origin: "detail-strip" });
      onApplied?.();
    }, operation.cancel);
  };

  // frontend/imageSource.ts
  var buildImageSourceOptions = (currentValue = "", includeControlNet = false) => {
    const options = [
      { value: MEDIA_SOURCE_BASE, label: "Base Output" },
      { value: MEDIA_SOURCE_REFINER, label: "Refiner Output" },
      { value: MEDIA_SOURCE_UPLOAD, label: "Upload" }
    ];
    for (const editRef of getBase2EditStageRefs()) {
      const editStage = parseBase2EditStageIndex(editRef);
      options.push({
        value: editRef,
        label: `Base2Edit Edit ${editStage} Output`
      });
    }
    if (includeControlNet) {
      for (const source of CONTROLNET_SOURCE_OPTIONS) {
        options.push({ value: source, label: source });
      }
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
  var resolveImageSourceValue = (currentValue, options) => resolveSelectValue(currentValue, options, MEDIA_SOURCE_REFINER);

  // frontend/clipReferenceSource.ts
  var buildClipReferenceSourceOptions = (kind, currentValue = "") => {
    if (kind === "image") {
      return buildImageSourceOptions(currentValue, true);
    }
    const options = [
      { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
      ...CONTROLNET_SOURCE_OPTIONS.map((source) => ({
        value: source,
        label: source
      }))
    ];
    if (kind === "audio") {
      options.push(
        ...buildAudioTrackSourceOptions(currentValue).filter(
          (option) => option.value !== MEDIA_SOURCE_UPLOAD
        )
      );
    }
    preserveSelectedOption(options, currentValue, "start", (value) => ({
      value,
      label: `${value} (unsupported persisted value)`
    }));
    return options;
  };
  var resolveClipReferenceSourceValue = (currentValue, options) => resolveSelectValue(currentValue, options, MEDIA_SOURCE_UPLOAD);
  var clipReferenceSourceSupportsKind = (kind, source) => {
    const value = `${source ?? ""}`.trim();
    if (value === MEDIA_SOURCE_UPLOAD || canonicalControlNetSource(value)) {
      return true;
    }
    if (kind === "audio") {
      return isAceStepFunAudioSource(value);
    }
    return kind === "image" && (value === MEDIA_SOURCE_BASE || value === MEDIA_SOURCE_REFINER || parseBase2EditStageIndex(value) !== null);
  };

  // frontend/detailStrip/clipReferencePanel.ts
  var claimClipLength = (clip, referenceIdx, defaults, fps) => {
    clip.references.forEach((reference, index) => {
      reference.drivesClipLength = index === referenceIdx;
    });
    if (referenceIdx < 0) {
      return;
    }
    clip.clipLengthFromAudio = false;
    clip.clipLengthFromControlNet = false;
    const seconds = clip.references[referenceIdx]?.mediaDurationSeconds ?? 0;
    if (seconds > 0) {
      applyClipDurationResize(
        clip,
        Math.max(CLIP_DURATION_MIN, seconds),
        defaults,
        fps
      );
    }
  };
  var applyPickedReferenceMedia = (ctx, clipId, referenceId, data, fileName) => runClipMediaProbe({
    clipId,
    slot: referenceId,
    probe: () => probeMediaDurationSeconds(data),
    apply: (clip, seconds, state) => {
      const index = clip.references.findIndex(
        (reference2) => reference2.id === referenceId
      );
      const reference = clip.references[index];
      if (!reference) {
        return;
      }
      reference.uploadedMedia = { data, fileName };
      reference.mediaDurationSeconds = roundToTenth(seconds);
      if (reference.drivesClipLength) {
        claimClipLength(
          clip,
          index,
          ctx.authoring().defaults,
          state.fps
        );
      }
    },
    onApplied: () => ctx.render()
  });
  var buildClipReferenceSection = (ctx, clipIdx, selectedIdx, clips, fps, open = selectedIdx !== null, incomingSelected = false) => {
    const clip = clips[clipIdx];
    const references = clip.references;
    const { capabilities, defaults } = ctx.authoring();
    const decision = capabilities.forClip(clip).decision("clipReferences");
    const incomingBoundary = incomingReferenceContinueForClip(
      clips,
      fps,
      capabilities,
      clipIdx
    );
    const incomingReference = {
      kind: "video",
      includeSoundtrack: incomingBoundary ? clips[incomingBoundary.leftIdx].boundaryOutReferenceIncludeSoundtrack : false
    };
    const tags = clipReferenceTags(
      references,
      incomingBoundary ? [incomingReference] : []
    );
    const itemOffset = incomingBoundary ? 1 : 0;
    const activeIdx = references.length === 0 || selectedIdx === null ? null : clamp(selectedIdx, 0, references.length - 1);
    const buildIncomingEditor = () => {
      if (!incomingBoundary) {
        return void 0;
      }
      const sourceClip = clips[incomingBoundary.leftIdx];
      const patchIncoming = (mutate) => {
        ctx.commit((cs) => {
          const source = cs[incomingBoundary.leftIdx];
          if (source) {
            mutate(source);
          }
        });
      };
      const fields = document.createElement("div");
      fields.className = "vst-detail-col vst-detail-instance-fields vst-detail-clip-ref-editor vst-detail-join-ref-editor";
      fields.appendChild(
        buildField(
          "Reference scale",
          buildOptionSelect(
            CLIP_REFERENCE_SCALES.map((scale) => ({
              value: `${scale.value}`,
              label: scale.label
            })),
            `${sourceClip.boundaryOutReferenceScale}`,
            (value) => {
              patchIncoming((source) => {
                source.boundaryOutReferenceScale = Number(value);
              });
            }
          )
        )
      );
      fields.appendChild(
        buildCheckbox(
          "Include soundtrack",
          sourceClip.boundaryOutReferenceIncludeSoundtrack,
          (value) => {
            patchIncoming((source) => {
              source.boundaryOutReferenceIncludeSoundtrack = value;
            });
            ctx.render();
          }
        )
      );
      return fields;
    };
    const buildSection = (editorForItem) => buildRepeatingEditor({
      key: "clip-references",
      label: "References",
      sectionClass: "vst-detail-clip-ref-section",
      open,
      items: [
        ...incomingBoundary ? [
          {
            label: `<Video 1> (from Join with Clip ${incomingBoundary.leftIdx})`,
            title: `Edit the reference supplied by the Continue join from Clip ${incomingBoundary.leftIdx}`,
            focusKey: `clip-reference-join-${incomingBoundary.leftIdx}`,
            active: incomingSelected,
            className: "vst-clip-ref-tab vst-clip-ref-join-tab",
            onSelect: () => setSelection({
              kind: "boundary-ref",
              leftClipIdx: incomingBoundary.leftIdx
            })
          }
        ] : [],
        ...references.map((reference, index) => ({
          label: tags[index],
          focusKey: `clip-reference-tab-${index}`,
          title: `Edit ${CLIP_REFERENCE_KIND_INFO[reference.kind].label} reference ${tags[index]}`,
          active: index === activeIdx,
          className: "vst-clip-ref-tab",
          onSelect: () => setSelection({
            kind: "clip-ref",
            clipIdx,
            referenceIdx: index
          }),
          onDelete: () => ctx.deleteClipReference(clipIdx, index)
        }))
      ],
      add: {
        title: decision.supported ? "Add a reference the prompt can name by tag" : decision.reason,
        label: "+ Add Reference",
        className: "vst-detail-add-clip-ref",
        disabled: !decision.supported,
        onClick: () => ctx.addClipReference(clipIdx)
      },
      remove: {
        title: activeIdx === null ? "No reference to delete" : `Delete reference ${tags[activeIdx]}`,
        className: "vst-detail-delete-clip-ref"
      },
      editorForItem: incomingBoundary === null && editorForItem === void 0 ? void 0 : (itemIndex) => {
        if (itemIndex < itemOffset) {
          return buildIncomingEditor();
        }
        return editorForItem?.(itemIndex - itemOffset);
      }
    }).section;
    if (activeIdx === null) {
      return buildSection();
    }
    const buildEditor = (editorIdx) => {
      const reference = references[editorIdx];
      if (!reference) {
        return void 0;
      }
      const patch = (mutate) => {
        ctx.commit((cs) => {
          const target = cs[clipIdx]?.references[editorIdx];
          if (target) {
            mutate(target);
          }
        });
      };
      const fields = document.createElement("div");
      fields.className = "vst-detail-col vst-detail-instance-fields vst-detail-clip-ref-editor";
      fields.setAttribute("data-vst-clip-ref-index", `${editorIdx}`);
      fields.appendChild(
        buildField(
          "Kind",
          buildOptionSelect(
            CLIP_REFERENCE_KINDS.map((kind) => ({
              value: kind,
              label: CLIP_REFERENCE_KIND_INFO[kind].label
            })),
            reference.kind,
            (value) => {
              patch((target) => {
                target.kind = value;
                target.uploadedMedia = null;
                target.includeSoundtrack = false;
                target.mediaDurationSeconds = 0;
                target.drivesClipLength = false;
                if (!clipReferenceSourceSupportsKind(
                  target.kind,
                  target.source
                )) {
                  target.source = MEDIA_SOURCE_UPLOAD;
                }
              });
              ctx.render();
            }
          ),
          void 0,
          `What this reference is. The prompt names it as ${tags[editorIdx]} — a reference the prompt never mentions still costs sampling time.`
        )
      );
      const options = buildClipReferenceSourceOptions(
        reference.kind,
        reference.source ?? ""
      );
      const source = resolveClipReferenceSourceValue(
        reference.source ?? "",
        options
      );
      fields.appendChild(
        buildField(
          "Source",
          buildOptionSelect(options, source, (value) => {
            patch((target) => {
              const resolved = resolveClipReferenceSourceValue(
                value,
                buildClipReferenceSourceOptions(target.kind, value)
              );
              target.source = resolved;
              if (resolved !== MEDIA_SOURCE_UPLOAD) {
                target.uploadedMedia = null;
                target.mediaDurationSeconds = 0;
                target.drivesClipLength = false;
              }
            });
            ctx.render();
          }),
          void 0,
          "Where this reference comes from — an upload, a ControlNet input, or another source available for this media kind."
        )
      );
      if (source === MEDIA_SOURCE_UPLOAD) {
        const data = reference.uploadedMedia?.data;
        if (reference.kind === "image" && data) {
          const preview = document.createElement("div");
          preview.className = "vst-refs-thumb-preview vst-refs-thumb-preview-set";
          preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
          fields.appendChild(preview);
        }
        const media = CLIP_REFERENCE_KIND_INFO[reference.kind];
        fields.appendChild(
          buildMediaPickRow(
            `${media.label} File`,
            media.accept,
            [...media.browserTypes],
            reference.uploadedMedia?.fileName,
            (pickedData, fileName) => {
              if (clipReferenceCanDriveLength(reference) && clip.id && reference.id) {
                applyPickedReferenceMedia(
                  ctx,
                  clip.id,
                  reference.id,
                  pickedData,
                  fileName
                );
                return;
              }
              patch((target) => {
                target.uploadedMedia = {
                  data: pickedData,
                  fileName
                };
              });
              ctx.render();
            },
            () => {
              patch((target) => {
                target.uploadedMedia = null;
                target.mediaDurationSeconds = 0;
                target.drivesClipLength = false;
              });
              ctx.render();
            }
          )
        );
      }
      if (clipReferenceCanDriveLength(reference)) {
        const seconds = reference.mediaDurationSeconds;
        const hint = document.createElement("small");
        hint.className = "vst-detail-field-hint";
        hint.textContent = `Detected: ${seconds > 0 ? `${seconds.toFixed(1)} s` : "unknown length"}`;
        fields.appendChild(hint);
        fields.appendChild(
          buildCheckbox(
            `Clip Length from ${CLIP_REFERENCE_KIND_INFO[reference.kind].label}`,
            reference.drivesClipLength === true,
            (value) => {
              ctx.commit((cs) => {
                const target = cs[clipIdx];
                if (target) {
                  claimClipLength(
                    target,
                    value ? editorIdx : -1,
                    defaults,
                    fps
                  );
                }
              });
              ctx.render();
            },
            {
              // Never disable a ticked box: a re-pick that probes to
              // nothing would otherwise trap an unhonourable claim.
              disabled: !(seconds > 0) && reference.drivesClipLength !== true,
              help: seconds > 0 ? "Set this clip's length to the reference's own length. Only one source can own the clip length, so this clears any other reference and the clip-level length options." : "This reference has no detected length to lend the clip. Pick a file the browser can read."
            }
          )
        );
      }
      if (reference.kind === "video") {
        fields.appendChild(
          buildField(
            "Reference scale",
            buildOptionSelect(
              CLIP_REFERENCE_SCALES.map((scale) => ({
                value: `${scale.value}`,
                label: scale.label
              })),
              `${reference.mediaScale}`,
              (value) => {
                patch((target) => {
                  target.mediaScale = Number(value);
                });
              }
            ),
            void 0,
            "Downsample this video before it is referenced. Its tokens are re-encoded on every sampling step, so half or quarter resolution is markedly faster and costs detail the model may not need from a motion or style reference."
          )
        );
        fields.appendChild(
          buildCheckbox(
            "Include soundtrack",
            reference.includeSoundtrack === true,
            (value) => {
              patch((target) => {
                target.includeSoundtrack = value;
              });
              ctx.render();
            },
            {
              help: `Also reference this video's own audio. It is presented as its own Audio reference just before ${tags[editorIdx]}, ahead of every standalone audio reference — the tags shown here already account for it.`
            }
          )
        );
      }
      return fields;
    };
    return buildSection(buildEditor);
  };

  // frontend/detailStrip/initVideoPanel.ts
  var DURATION_STEP2 = 0.1;
  var applyPickedInitVideo = (context, clipId, data, fileName) => runClipMediaProbe({
    clipId,
    slot: INIT_VIDEO_PROBE_SLOT,
    probe: () => probeInitVideo(data),
    apply: (target, probe, state) => {
      const { capabilities, defaults } = context.authoring();
      target.initVideo = initVideoFromProbe(
        probe,
        data,
        fileName,
        target.duration
      );
      reconcileClipArchitectureIdentity(target, capabilities.catalog);
      applyClipDurationResize(
        target,
        Math.max(CLIP_DURATION_MIN, target.initVideo.lengthSeconds),
        defaults,
        state.fps
      );
    },
    onApplied: () => context.render()
  });
  var buildInitVideoSection = (context, clip, clipIdx, open = false) => {
    const { wrap, col } = buildStackSection(
      "init-video",
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
    const source = clip.initVideo;
    const removeSource = () => {
      context.structuralCommit((clips) => {
        const target = clips[clipIdx];
        if (!target?.initVideo) {
          return null;
        }
        const transaction = context.authoring();
        target.initVideo = null;
        reconcileClipArchitectureIdentity(
          target,
          transaction.capabilities.catalog
        );
        reconcileArchitectureIncomingIcLoraDrives(
          clips,
          transaction.generatedEntryMode,
          transaction.capabilities.catalog
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
            applyPickedInitVideo(context, clip.id, data, fileName);
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
      const defaults = context.authoring().defaults;
      applyClipDurationResize(
        target,
        Math.max(
          CLIP_DURATION_MIN,
          target.initVideo?.lengthSeconds ?? target.duration
        ),
        defaults,
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
      readBack: (clips) => clips[clipIdx]?.initVideo?.startSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        const targetSource = target?.initVideo;
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
      readBack: (clips) => clips[clipIdx]?.initVideo?.lengthSeconds ?? null,
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        const targetSource = target?.initVideo;
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

  // frontend/architectures/referenceEndpoints.ts
  var referenceEndpointPolicy = (clip, catalog) => {
    const activeStages = clip.stages.slice(0, activeStageCount(clip));
    const firstPositions = modelCatalogEntry(catalog, activeStages[0]?.model)?.enhancements?.referencePositions ?? [];
    let terminalGenerating = activeStages.at(-1);
    while (terminalGenerating && terminalGenerating.control <= 0) {
      activeStages.pop();
      terminalGenerating = activeStages.at(-1);
    }
    const lastPositions = modelCatalogEntry(catalog, terminalGenerating?.model)?.enhancements?.referencePositions ?? [];
    if (firstPositions.includes("any")) {
      return {
        positions: ["any"],
        available: true,
        bounded: false,
        supportsFirst: true,
        supportsLast: true
      };
    }
    const supportsFirst = firstPositions.includes("first");
    const supportsLast = lastPositions.includes("last");
    const positions = [
      ...supportsFirst ? ["first"] : [],
      ...supportsLast ? ["last"] : []
    ];
    return {
      positions,
      available: positions.length > 0,
      bounded: positions.length > 0,
      supportsFirst,
      supportsLast
    };
  };
  var boundedReferencePositionHelp = (policy) => {
    if (!policy.available) {
      return "This clip does not accept frame-reference endpoints.";
    }
    if (!policy.bounded) {
      return void 0;
    }
    if (policy.supportsFirst && policy.supportsLast) {
      return "This clip accepts an image only at the first or final frame.";
    }
    if (policy.supportsFirst) {
      return "This clip accepts an image only at the first frame.";
    }
    return "This clip accepts an image only at the final frame.";
  };
  var boundedReferenceToggleHelp = (policy) => {
    if (!policy.available) {
      return "This clip does not publish a supported frame-reference endpoint.";
    }
    if (!policy.bounded) {
      return void 0;
    }
    if (policy.supportsFirst && policy.supportsLast) {
      return "Off means first frame; on means final frame.";
    }
    return policy.supportsFirst ? "This clip supports only the first frame. Turn this off to repair an older final-frame value." : "This clip supports only the final frame. Turn this on to repair an older first-frame value.";
  };

  // frontend/detailStrip/refPanel.ts
  var buildRefSection = (ctx, clipIdx, selectedRefIdx, clips, fps, open = selectedRefIdx !== null) => {
    const clip = clips[clipIdx];
    const { capabilities, defaults } = ctx.authoring();
    const decision = capabilities.forClip(clip).decision("frameReferences");
    const endpointPolicy = referenceEndpointPolicy(clip, defaults.modelCatalog);
    const hasSupportedEndpoint = endpointPolicy.available;
    const activeRefIdx = clip.frameRefs.length === 0 ? null : clamp(selectedRefIdx ?? 0, 0, clip.frameRefs.length - 1);
    const buildSection = (editorForItem) => buildRepeatingEditor({
      key: "references",
      label: "Frame References",
      sectionClass: "vst-detail-ref-section",
      open,
      items: clip.frameRefs.map((_, refIdx) => ({
        label: `Ref${refIdx}`,
        focusKey: `reference-tab-${refIdx}`,
        title: `Edit frame reference ${refIdx}`,
        active: refIdx === activeRefIdx,
        className: "vst-ref-tab",
        onSelect: () => setSelection({ kind: "ref", clipIdx, refIdx }),
        onDelete: () => ctx.deleteRefEntry(clipIdx, refIdx)
      })),
      add: {
        title: !decision.supported ? decision.reason : hasSupportedEndpoint ? "Add a frame reference" : "The selected models do not publish a supported frame-reference endpoint.",
        label: "+ Add Frame Reference",
        className: "vst-detail-add-ref",
        disabled: !decision.supported || !hasSupportedEndpoint,
        onClick: () => ctx.addRefEntry(clipIdx)
      },
      remove: {
        title: activeRefIdx === null ? "No frame reference to delete" : `Delete frame reference ${activeRefIdx}`,
        className: "vst-detail-delete-ref"
      },
      editorForItem
    }).section;
    if (activeRefIdx === null) {
      return buildSection();
    }
    const buildEditor = (editorRefIdx) => {
      const ref = clip.frameRefs[editorRefIdx];
      if (!ref) {
        return void 0;
      }
      const options = buildImageSourceOptions(ref.source ?? "");
      const source = resolveImageSourceValue(ref.source ?? "", options);
      const isUpload = source === MEDIA_SOURCE_UPLOAD;
      const fields = document.createElement("div");
      fields.className = "vst-detail-col vst-detail-instance-fields vst-detail-ref-row vst-detail-ref-editor";
      fields.setAttribute("data-vst-ref-index", `${editorRefIdx}`);
      const select2 = buildOptionSelect(options, source, (value) => {
        ctx.commit((cs) => {
          const target = cs[clipIdx]?.frameRefs[editorRefIdx];
          if (!target) {
            return;
          }
          const resolved = resolveImageSourceValue(
            value,
            buildImageSourceOptions(value)
          );
          target.source = resolved;
          if (resolved !== MEDIA_SOURCE_UPLOAD) {
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
      const frameMax = getReferenceFrameMax(defaults, clip, fps);
      const boundedPositions = endpointPolicy.bounded;
      const supportsFirst = endpointPolicy.supportsFirst;
      const supportsLast = endpointPolicy.supportsLast;
      const currentPositionSupported = ref.fromEnd ? supportsLast : supportsFirst;
      const editableFrameMax = endpointPolicy.available && !endpointPolicy.bounded ? frameMax : REF_FRAME_MIN;
      const frameInput = buildNumber(
        ref.frame,
        REF_FRAME_MIN,
        editableFrameMax,
        1,
        (value) => {
          ctx.debouncedCommit(`ref-${editorRefIdx}-frame`, (cs) => {
            const target = cs[clipIdx]?.frameRefs[editorRefIdx];
            if (target) {
              target.frame = clamp(
                Math.round(value),
                REF_FRAME_MIN,
                editableFrameMax
              );
            }
          });
        }
      );
      frameInput.setAttribute(
        "data-vst-focus-key",
        `ref-${editorRefIdx}-frame`
      );
      frameInput.disabled = !hasSupportedEndpoint;
      fields.appendChild(
        buildField(
          "Attach at Frame",
          frameInput,
          void 0,
          boundedReferencePositionHelp(endpointPolicy) ?? (boundedPositions ? "This clip accepts an image only at a bounded endpoint." : "The frame within the clip where this reference is anchored. Frame 1 is the first frame; the image influences the clip most strongly around here.")
        )
      );
      fields.appendChild(
        buildCheckbox(
          "Count from clip end",
          ref.fromEnd === true,
          (value) => {
            ctx.commit((cs) => {
              const target = cs[clipIdx]?.frameRefs[editorRefIdx];
              if (target) {
                target.fromEnd = value;
              }
            });
          },
          {
            disabled: !hasSupportedEndpoint || boundedPositions && currentPositionSupported && !(supportsFirst && supportsLast),
            help: boundedReferenceToggleHelp(endpointPolicy) ?? (boundedPositions ? "Choose the supported clip endpoint." : "Count the attach frame backwards from the last frame instead of forward from the first — so it stays anchored to the end even if the clip length changes.")
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
                const target = cs[clipIdx]?.frameRefs[editorRefIdx];
                if (target) {
                  target.uploadedImage = { data, fileName };
                  target.uploadFileName = fileName;
                }
              });
              ctx.render();
            },
            () => {
              ctx.commit((cs) => {
                const target = cs[clipIdx]?.frameRefs[editorRefIdx];
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
      return fields;
    };
    return buildSection(buildEditor);
  };

  // frontend/detailStrip/retakePanel.ts
  var buildRetakeSection = (context, clip, clipIdx, open = false) => {
    const retake = clip.retake;
    const decision = context.authoring().capabilities.forClip(clip).decision("retake");
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
    const rootModel = modelCatalogEntry(
      defaults.modelCatalog,
      clip.stages[0]?.model
    );
    const ownerArchitectureId = resolvedClipArchitectureId(
      clip,
      defaults.modelCatalog
    );
    const modelOptions = defaults.modelCatalog.entries.flatMap(
      (entry) => {
        const model = modelCatalogEntry(defaults.modelCatalog, entry.value);
        const target = buildArchitectureRetargetPlan(
          defaults.modelCatalog,
          entry.value
        );
        if (!target || !model) return [];
        const leavesAuthoredStagesCompatible = clip.stages.every(
          (candidate, candidateIndex) => {
            if (candidateIndex === stageIdx) return true;
            const candidateModel = modelCatalogEntry(
              defaults.modelCatalog,
              candidate.model
            );
            return candidateModel?.architectureId === model.architectureId && candidateModel.compatibilityClassId !== null && candidateModel.compatibilityClassId === model.compatibilityClassId;
          }
        );
        const requiresWholeClipConversion = stageIdx === 0 && (ownerArchitectureId === null || target.architectureId !== ownerArchitectureId);
        const preservesClipLock = stageIdx === 0 ? requiresWholeClipConversion || leavesAuthoredStagesCompatible : model.architectureId === rootModel?.architectureId && model.compatibilityClassId !== null && model.compatibilityClassId === rootModel?.compatibilityClassId;
        return preservesClipLock ? [{ value: entry.value, label: entry.label }] : [];
      }
    );
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
        const selectedModel = modelCatalogEntry(
          defaults.modelCatalog,
          value
        );
        const plan = buildArchitectureRetargetPlan(
          defaults.modelCatalog,
          value
        );
        if (!plan || !selectedModel) {
          modelSelect.value = stage.model;
          return;
        }
        if (stageIdx === 0 && (ownerArchitectureId === null || plan.architectureId !== ownerArchitectureId)) {
          const conversion = planArchitectureConversion(
            clip,
            plan,
            defaults.modelCatalog
          );
          if (!conversion) {
            modelSelect.value = stage.model;
            return;
          }
          context.structuralCommit((clips) => {
            const clipId = clips[clipIdx]?.id;
            if (!clipId) {
              modelSelect.value = stage.model;
              return null;
            }
            return {
              command: {
                type: "clip.convert-architecture",
                clipId,
                target: plan
              },
              selection: "render"
            };
          });
          return;
        }
        context.structuralCommit((clips) => {
          const clipId = clips[clipIdx]?.id;
          const stageId = clips[clipIdx]?.stages[stageIdx]?.id;
          if (!clipId || !stageId) {
            modelSelect.value = stage.model;
            return null;
          }
          return {
            command: {
              type: "stage.retarget-model",
              clipId,
              stageId,
              target: plan
            },
            selection: "render"
          };
        });
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
    debouncedCommit
  }) => {
    if (clip.frameRefs.length > 0) {
      const refDecision = context.authoring().capabilities.forClip(clip).decision("frameReferences");
      appendSectionHeader(fields, "Frame Reference Strengths");
      const setRefHover = (refIdx, on) => {
        context.getBoundBody()?.querySelector(
          `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`
        )?.classList.toggle("vst-ref-hover", on);
      };
      clip.frameRefs.forEach((ref, refIdx) => {
        const current = refIdx < stage.frameRefStrengths.length ? stage.frameRefStrengths[refIdx] : STAGE_REF_STRENGTH_MAX;
        const refSlider = buildSlider(
          `Frame Ref R${refIdx}`,
          current,
          STAGE_REF_STRENGTH_MIN,
          STAGE_REF_STRENGTH_MAX,
          STAGE_REF_STRENGTH_STEP,
          (value) => {
            debouncedCommit(`refstrength-${refIdx}`, (target) => {
              if (refIdx < target.frameRefStrengths.length) {
                target.frameRefStrengths[refIdx] = value;
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
      fields.appendChild(group);
    }
    const applicableIcLoras = clip.icLoras.map((entry, entryIdx) => ({ entry, entryIdx })).filter(({ entry }) => entry.stage < 0 || entry.stage === stageIdx);
    if (applicableIcLoras.length === 0) return;
    appendSectionHeader(fields, "IC-LoRA Guide Strengths");
    const capabilityView = context.authoring().capabilities.forClip(clip);
    const icDecision = capabilityView.decision("icLora");
    const icGroup = document.createDocumentFragment();
    applicableIcLoras.forEach(({ entry, entryIdx }) => {
      const displayName = architectureIcLoraDisplayName(
        capabilityView.architectureId,
        entry
      );
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
      const row = buildField(shortModelName2(displayName), guideStrength);
      row.classList.add("vst-stage-iclora-strength-row");
      row.title = displayName;
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
  var latentUpscaleFeature = (method) => {
    const mode = upscaleModeForMethod(method);
    return mode === "latent" ? "latentUpscale" : mode === "latent-model" ? "latentModelUpscale" : null;
  };
  var appendStageUpscaleSection = (bindings, isRefine) => {
    if (!isRefine) return;
    const { context, clip, stage, defaults, fields, slider, commit } = bindings;
    const capabilities = context.authoring().capabilities.forClip(clip);
    const supportedMethods = defaults.upscaleMethodValues.flatMap(
      (value, index) => {
        const feature = latentUpscaleFeature(value);
        return feature === null || capabilities.decision(feature).supported ? [
          {
            value,
            label: defaults.upscaleMethodLabels[index] ?? value
          }
        ] : [];
      }
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
  };

  // frontend/detailStrip/stagePanel.ts
  var buildStageParamsColumn = (context, clip, clipIdx, stageIdx, stage, defaults) => {
    const column = document.createElement("div");
    column.className = "vst-detail-fields vst-detail-params";
    const initVideoStage0 = stageIdx === 0 && !!clip.initVideo && stage.skipped !== true;
    const isRefine = stageIdx >= 1 || initVideoStage0;
    const stageCapabilities = context.authoring().capabilities.forStage(clip, stage);
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
    if (initVideoStage0) {
      const note = document.createElement("p");
      note.className = "vst-detail-note vst-stage-passthrough-note";
      note.textContent = "This stage starts from the source footage — Control sets how much is re-generated (0 passes it through).";
      column.insertBefore(note, column.firstChild);
    }
    return column;
  };

  // frontend/detailStrip/stageRail.ts
  var buildStageRail = (context, clip, clipIdx, stageIdx, editorForStage, open = true) => {
    const addTitle = clip.stages.length === 0 ? "Add the first stage and choose its architecture" : "Add a refine stage";
    return buildRepeatingEditor({
      key: "stages",
      label: "Stages",
      sectionClass: "vst-detail-stage-groups",
      open,
      items: clip.stages.map((stage, index) => {
        const firstStage = index === 0;
        return {
          label: `Stage ${stageChipLabel(index)}`,
          focusKey: `stage-group-${index}`,
          title: stageChipTitle(stage, index),
          active: index === stageIdx,
          className: `vst-stage-tab${stage.skipped ? " vst-stage-tab-skipped" : ""}`,
          onSelect: () => context.selectStage(clipIdx, index),
          onDelete: firstStage ? void 0 : () => context.deleteStage(clipIdx, index),
          deleteTitle: firstStage ? void 0 : `Delete stage ${stageChipLabel(index)}`,
          headerAction: firstStage ? void 0 : {
            label: skipGlyph(stage.skipped === true),
            title: skipTitle(
              `stage ${stageChipLabel(index)}`,
              stage.skipped === true
            ),
            className: "vst-detail-skip-stage",
            active: stage.skipped,
            onClick: () => context.toggleStageSkip(clipIdx, index)
          }
        };
      }),
      editorForItem: editorForStage,
      add: {
        title: addTitle,
        label: "+ Add Video Stage",
        className: "vst-detail-add-stage",
        onClick: () => context.addStage(clipIdx)
      },
      remove: {
        title: "Delete stage",
        className: "vst-detail-delete-stage"
      }
    }).section;
  };

  // frontend/detailStrip/clipPanel.ts
  var buildClipBody = (context, selection, state) => {
    const clips = state.clips;
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    let clipIdx;
    if (selection.kind === "boundary-ref") {
      const incomingBoundary = executableBoundaryForLeftClip(
        clips,
        selection.leftClipIdx
      );
      if (!incomingBoundary) {
        throw new Error(
          "boundary reference selection has no executable seam"
        );
      }
      clipIdx = incomingBoundary.rightIdx;
    } else {
      clipIdx = selection.clipIdx;
    }
    const stageIdx = selection.kind === "clip" ? selection.stageIdx : 0;
    const clip = clips[clipIdx];
    body.classList.toggle("vst-detail-clip-skipped", clip.skipped === true);
    const { capabilities, defaults } = context.authoring();
    const capabilityView = capabilities.forClip(clip);
    const referenceFramingState = capabilityView.authoringState(
      "referenceFraming",
      clip.refFraming !== "crop"
    );
    body.appendChild(
      buildStaticSection({
        key: "clip",
        label: "Clip",
        className: "vst-detail-clip-section",
        content: buildClipColumn(
          context,
          clip,
          clipIdx,
          referenceFramingState
        ),
        flattenContent: true,
        headerActions: clipIdx === 0 ? [] : [
          buildClipSkipAction(context, clip, clipIdx),
          {
            label: "×",
            title: `Delete clip ${clipIdx}`,
            className: "vst-detail-delete vst-detail-repeating-group-delete vst-detail-delete-clip",
            variant: "interrupt",
            onClick: () => context.deleteClip?.(clipIdx)
          }
        ]
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
      const state2 = capabilityView.authoringState(feature, persisted);
      if (!state2.visible) {
        return;
      }
      const section = content();
      if (!state2.enabled) {
        applyPersistedCapabilityRepair(section, state2);
      }
      body.appendChild(section);
    };
    appendCapabilitySection(
      "clipReferences",
      clip.references.length > 0,
      () => buildClipReferenceSection(
        context,
        clipIdx,
        selection.kind === "clip-ref" ? selection.referenceIdx : null,
        clips,
        state.fps,
        selection.kind === "clip-ref" || selection.kind === "boundary-ref",
        selection.kind === "boundary-ref"
      )
    );
    appendCapabilitySection(
      "frameReferences",
      clip.frameRefs.length > 0,
      () => buildRefSection(
        context,
        clipIdx,
        selection.kind === "ref" ? selection.refIdx : null,
        clips,
        state.fps,
        selection.kind === "ref"
      )
    );
    body.appendChild(
      buildClipLorasSection(context, clip, clipIdx, stageIdx, defaults)
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
    body.appendChild(buildInitVideoSection(context, clip, clipIdx, false));
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
    return built.section;
  };
  var buildRelayPromptSection = (ctx, clip, clipIdx, selectedWindowIdx, open) => {
    const windows = clip.promptWindows ?? [];
    const decision = ctx.authoring().capabilities.forClip(clip).decision("promptRelay");
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
        onDelete: () => ctx.deleteWindowEntry(clipIdx, index)
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
  var clampSideLength = (value) => clamp(
    Math.round((Math.round(value) || 1024) / ROOT_DIMENSION_STEP) * ROOT_DIMENSION_STEP,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MAX
  );
  var setSliderDisabled = (slider, disabled) => {
    slider.querySelectorAll("input").forEach((input2) => {
      input2.disabled = disabled;
    });
    const box = slider.querySelector(".auto-slider-box");
    if (box) {
      box.dataset.disabled = `${disabled}`;
    }
  };
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
  var buildSettingsBody = (ctx, state, _selection = {
    kind: "none"
  }) => {
    const defaults = ctx.authoring().defaults;
    const core = {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
    const multiple = activeDocumentDimensionMultiple(
      state.clips,
      defaults.modelCatalog
    );
    const defaultMode = !state.dimsExplicit ? SETTINGS_INHERIT : matchAspectRatio(state.width, state.height, multiple) ?? SETTINGS_CUSTOM;
    const mode = ctx.getSettingsMode() ?? defaultMode;
    const isCustom = mode === SETTINGS_CUSTOM;
    const isInherited = mode === SETTINGS_INHERIT;
    const fallbackSideLength = defaults.sideLength ?? (defaults.aspectRatio ? sideLengthForDimensions(
      defaults.aspectRatio,
      core.width,
      core.height
    ) : 1024);
    const selectedSideLength = !isInherited && !isCustom ? sideLengthForDimensions(mode, state.width, state.height) : clampSideLength(fallbackSideLength);
    const rawDimensions = isInherited || isCustom ? {
      width: clampDimension(isInherited ? core.width : state.width),
      height: clampDimension(
        isInherited ? core.height : state.height
      )
    } : dimensionsFor(mode, selectedSideLength) ?? {
      width: clampDimension(state.width),
      height: clampDimension(state.height)
    };
    const effectiveDimensions = snapDimensions(
      rawDimensions.width,
      rawDimensions.height,
      multiple
    );
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-settings";
    const ratioSpecs = [
      {
        value: SETTINGS_INHERIT,
        label: `Use image resolution (${core.width}×${core.height})`
      },
      ...ASPECT_RATIOS.map((ratio) => ({
        value: ratio.id,
        label: ratio.label
      })),
      { value: SETTINGS_CUSTOM, label: "Custom" }
    ];
    const ratioSelect = buildOptionSelect(ratioSpecs, mode, (value) => {
      ctx.setSettingsMode(value);
      ctx.commitState((next) => {
        if (value === SETTINGS_INHERIT) {
          next.dimsExplicit = false;
        } else if (value === SETTINGS_CUSTOM) {
          next.dimsExplicit = true;
          next.width = effectiveDimensions.width;
          next.height = effectiveDimensions.height;
        } else {
          const raw = dimensionsFor(value, fallbackSideLength) ?? {
            width: effectiveDimensions.width,
            height: effectiveDimensions.height
          };
          const snapped = snapDimensions(raw.width, raw.height, multiple);
          next.dimsExplicit = true;
          next.width = snapped.width;
          next.height = snapped.height;
        }
      });
      ctx.render();
    });
    body.appendChild(buildField("Aspect Ratio", ratioSelect));
    if (isCustom) {
      const widthSlider = tagFocus(
        buildSlider(
          "Width",
          rawDimensions.width,
          ROOT_DIMENSION_MIN,
          ROOT_DIMENSION_MAX,
          ROOT_DIMENSION_STEP,
          (value) => {
            ctx.debouncedCommitState("settings-width", (next) => {
              const snapped = snapDimensions(
                clampDimension(value),
                clampDimension(next.height),
                multiple
              );
              next.dimsExplicit = true;
              next.width = snapped.width;
              next.height = snapped.height;
            });
          }
        ),
        "settings-width"
      );
      const heightSlider = tagFocus(
        buildSlider(
          "Height",
          rawDimensions.height,
          ROOT_DIMENSION_MIN,
          ROOT_DIMENSION_MAX,
          ROOT_DIMENSION_STEP,
          (value) => {
            ctx.debouncedCommitState("settings-height", (next) => {
              const snapped = snapDimensions(
                clampDimension(next.width),
                clampDimension(value),
                multiple
              );
              next.dimsExplicit = true;
              next.width = snapped.width;
              next.height = snapped.height;
            });
          }
        ),
        "settings-height"
      );
      body.append(widthSlider, heightSlider);
    } else if (!isInherited) {
      const ratioHasReference = dimensionsFor(mode, selectedSideLength) !== null;
      let calculatedDimensions = null;
      const sideLengthSlider = tagFocus(
        buildSlider(
          "Side Length",
          selectedSideLength,
          ROOT_DIMENSION_MIN,
          ROOT_DIMENSION_MAX,
          ROOT_DIMENSION_STEP,
          (value) => {
            if (isInherited) {
              return;
            }
            const raw = dimensionsFor(mode, clampSideLength(value));
            if (!raw) {
              return;
            }
            const snapped = snapDimensions(
              raw.width,
              raw.height,
              multiple
            );
            if (calculatedDimensions) {
              calculatedDimensions.textContent = `${snapped.width} × ${snapped.height}`;
            }
            ctx.debouncedCommitState("settings-side-length", (next) => {
              next.dimsExplicit = true;
              next.width = snapped.width;
              next.height = snapped.height;
            });
          },
          {
            hint: !ratioHasReference ? "(the host has no 3:4 reference; current dimensions are retained)" : void 0,
            isPot: true
          }
        ),
        "settings-side-length"
      );
      calculatedDimensions = document.createElement("span");
      calculatedDimensions.className = "vst-settings-calculated-dims";
      calculatedDimensions.textContent = `${effectiveDimensions.width} × ${effectiveDimensions.height}`;
      sideLengthSlider.querySelector("label")?.appendChild(calculatedDimensions);
      setSliderDisabled(sideLengthSlider, !ratioHasReference);
      body.appendChild(sideLengthSlider);
    }
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
  var clampDetailSelection = (selection, clips, audioTracks = [], fps, capabilities) => {
    if (selection.kind === "none") {
      return selection;
    }
    if (selection.kind === "boundary") {
      return selection.leftClipIdx >= 0 && selection.leftClipIdx <= clips.length - 2 ? selection : { kind: "none" };
    }
    if (selection.kind === "boundary-ref") {
      const seam = executableBoundaryForLeftClip(
        clips,
        selection.leftClipIdx
      );
      return seam && fps !== void 0 && capabilities !== void 0 ? incomingReferenceContinueForClip(
        clips,
        fps,
        capabilities,
        seam.rightIdx
      ) ? selection : { kind: "none" } : { kind: "none" };
    }
    if (selection.kind === "audio-track") {
      return audioTracks[selection.trackIdx] ? selection : { kind: "none" };
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
      return selection.refIdx >= 0 && selection.refIdx < clip.frameRefs.length ? selection : { kind: "none" };
    }
    if (selection.kind === "clip-ref") {
      return selection.referenceIdx >= 0 && selection.referenceIdx < clip.references.length ? selection : { kind: "clip", clipIdx: selection.clipIdx, stageIdx: 0 };
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
  var detailBreadcrumb = (selection, clips, fps, capabilities) => {
    switch (selection.kind) {
      case "clip":
        return clips[selection.clipIdx]?.stages.length === 0 ? `Clip ${selection.clipIdx} · Source only` : `Clip ${selection.clipIdx} · ${stageChipLabel(selection.stageIdx)}`;
      case "ref":
        return `Ref${selection.refIdx} · Clip ${selection.clipIdx}`;
      case "clip-ref": {
        const incomingBoundary = fps === void 0 || capabilities === void 0 ? null : incomingReferenceContinueForClip(
          clips,
          fps,
          capabilities,
          selection.clipIdx
        );
        const tag = clipReferenceTags(
          clips[selection.clipIdx]?.references ?? [],
          incomingBoundary ? [
            {
              kind: "video",
              includeSoundtrack: clips[incomingBoundary.leftIdx].boundaryOutReferenceIncludeSoundtrack
            }
          ] : []
        )[selection.referenceIdx];
        return `${tag ?? "Reference"} · Clip ${selection.clipIdx}`;
      }
      case "boundary-ref": {
        const seam = executableBoundaryForLeftClip(
          clips,
          selection.leftClipIdx
        );
        return seam ? `<Video 1> (from Join with Clip ${selection.leftClipIdx}) · Clip ${seam.rightIdx}` : "Reference";
      }
      case "ic-lora":
        return `IC-LoRA ${selection.entryIdx} · Clip ${selection.clipIdx}`;
      case "audio":
        return `Audio · Clip ${selection.clipIdx}`;
      case "audio-track":
        return `Audio track A${selection.trackIdx + 1}`;
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
  var buildDetailHeader = (selection, state, capabilities) => {
    const header = document.createElement("div");
    header.className = "vst-detail-head";
    const breadcrumb = document.createElement("span");
    breadcrumb.className = "vst-detail-crumb";
    breadcrumb.textContent = detailBreadcrumb(
      selection,
      state.clips,
      state.fps,
      capabilities
    );
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
  var buildDetailPanelBody = (context, selection, state) => {
    const clips = state.clips;
    switch (selection.kind) {
      case "clip":
        return buildClipBody(context, selection, state);
      case "ref":
        return buildClipBody(context, selection, state);
      case "clip-ref":
        return buildClipBody(context, selection, state);
      case "boundary-ref":
        return buildClipBody(context, selection, state);
      case "ic-lora":
        return buildClipBody(context, selection, state);
      case "audio":
        return buildAudioBody(context, selection, state);
      case "audio-track":
        return buildTimelineAudioTracksBody(context, state, selection);
      case "prompt-major":
        return buildPromptMajorBody(context, selection, clips);
      case "prompt-minor":
        return buildPromptMinorBody(context, selection, clips);
      case "retake":
        return buildClipBody(context, selection, state);
      case "boundary":
        return buildBoundaryBody(context, selection, state);
      default:
        return buildSettingsBody(context, state, { kind: "none" });
    }
  };

  // frontend/detailStrip/renderShell.ts
  var DETAIL_CLASS = "vst-detail";
  var revealRepeaterKey = (selection) => {
    switch (selection.kind) {
      case "ref":
        return "references";
      case "boundary-ref":
        return "clip-references";
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
      buildDetailHeader(
        options.selection,
        options.state,
        options.context.authoring().capabilities
      )
    );
    const body = buildDetailPanelBody(
      options.context,
      options.selection,
      options.state
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

  // frontend/referenceAuthoring.ts
  var REFERENCE_FRAME_STEP_FRACTION = 0.1;
  var absoluteReferenceFrame = (ref, frameMax) => {
    const authoredFrame = clamp(Math.round(ref.frame), REF_FRAME_MIN, frameMax);
    return ref.fromEnd ? frameMax - authoredFrame + REF_FRAME_MIN : authoredFrame;
  };
  var nextAvailableReferenceFrame = (frameRefs, rawFrameMax) => {
    const frameMax = Number.isFinite(rawFrameMax) && rawFrameMax >= REF_FRAME_MIN ? Math.floor(rawFrameMax) : REF_FRAME_MIN;
    const occupied = new Set(
      frameRefs.map((ref) => absoluteReferenceFrame(ref, frameMax))
    );
    const step = Math.max(
      1,
      Math.round(frameMax * REFERENCE_FRAME_STEP_FRACTION)
    );
    const preferred = /* @__PURE__ */ new Set();
    for (let index = 0; ; index++) {
      const candidate = Math.min(frameMax, REF_FRAME_MIN + index * step);
      preferred.add(candidate);
      if (candidate >= frameMax) {
        break;
      }
    }
    for (const candidate of preferred) {
      if (!occupied.has(candidate)) {
        return candidate;
      }
    }
    for (let candidate = REF_FRAME_MIN; candidate <= frameMax; candidate++) {
      if (!occupied.has(candidate)) {
        return candidate;
      }
    }
    return null;
  };
  var nextAllowedReferencePosition = (frameRefs, rawFrameMax, allowed) => {
    if (allowed.includes("any")) {
      const frame = nextAvailableReferenceFrame(frameRefs, rawFrameMax);
      return frame === null ? null : { frame, fromEnd: false };
    }
    if (allowed.includes("first") && !frameRefs.some((ref) => ref.frame === REF_FRAME_MIN && !ref.fromEnd)) {
      return { frame: REF_FRAME_MIN, fromEnd: false };
    }
    if (allowed.includes("last") && !frameRefs.some((ref) => ref.frame === REF_FRAME_MIN && ref.fromEnd)) {
      return { frame: REF_FRAME_MIN, fromEnd: true };
    }
    return null;
  };

  // frontend/detailStrip/selectionDomainOperations.ts
  var refStrengthPatches = (clip, next) => clip.stages.flatMap(
    (stage) => stage.id ? [
      {
        type: "stage.patch",
        clipId: clip.id,
        stageId: stage.id,
        patch: {
          frameRefStrengths: next(stage.frameRefStrengths)
        }
      }
    ] : []
  );
  var createDetailSelectionDomainOperations = (structuralCommit, captureAuthoringTransaction) => {
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
          const ref = clip?.frameRefs[refIdx];
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
            remaining: clip.frameRefs.length - 1
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
        const { capabilities, defaults } = captureAuthoringTransaction();
        if (!clip?.id || !capabilities.forClip(clip).decision("frameReferences").supported) {
          return null;
        }
        const position = nextAllowedReferencePosition(
          clip.frameRefs,
          getReferenceFrameMax(defaults, clip),
          referenceEndpointPolicy(clip, defaults.modelCatalog).positions
        );
        if (position === null) {
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
                  frame: position.frame,
                  fromEnd: position.fromEnd,
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
            refIdx: clip.frameRefs.length
          }
        };
      });
    };
    const addClipReference = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        const { capabilities } = captureAuthoringTransaction();
        if (!clip?.id || !capabilities.forClip(clip).decision("clipReferences").supported) {
          return null;
        }
        return {
          command: {
            type: "clip-reference.add",
            clipId: clip.id,
            reference: {
              ...buildDefaultClipReference(),
              id: createEntityId("clip_reference")
            }
          },
          selection: {
            kind: "clip-ref",
            clipIdx,
            referenceIdx: clip.references.length
          }
        };
      });
    };
    const deleteClipReference = (clipIdx, referenceIdx) => {
      commitRemoval(
        (clips) => {
          const clip = clips[clipIdx];
          const reference = clip?.references[referenceIdx];
          if (!clip?.id || !reference?.id) {
            return null;
          }
          return {
            command: {
              type: "clip-reference.remove",
              clipId: clip.id,
              referenceId: reference.id
            },
            remaining: clip.references.length - 1
          };
        },
        referenceIdx,
        (index) => ({ kind: "clip-ref", clipIdx, referenceIdx: index }),
        { kind: "clip", clipIdx, stageIdx: 0 }
      );
    };
    const addPromptWindow = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          const { capabilities } = captureAuthoringTransaction();
          if (!clip?.id || !capabilities.forClip(clip).decision("promptRelay").supported) {
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
          const { capabilities } = captureAuthoringTransaction();
          if (!clip?.id || clip.retake || !capabilities.forClip(clip).decision("retake").supported) {
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
          const keepRetakeSelected = captureAuthoringTransaction().capabilities.forClip(clip).decision("retake").supported;
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
    const deleteClip = (clipIdx) => {
      structuralCommit(
        (clips) => {
          if (clipIdx <= 0 || clipIdx >= clips.length) {
            return null;
          }
          const transaction = captureAuthoringTransaction();
          clips.splice(clipIdx, 1);
          reconcileArchitectureIncomingIcLoraDrives(
            clips,
            transaction.generatedEntryMode,
            transaction.capabilities.catalog
          );
          return selectionAfterRemoval(
            clipIdx,
            clips.length,
            (index) => ({
              kind: "clip",
              clipIdx: index,
              stageIdx: 0
            }),
            { kind: "none" }
          );
        },
        { rebuildAfterSelect: true }
      );
    };
    const addStage = (clipIdx) => {
      structuralCommit(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip) {
            return null;
          }
          const { capabilities, defaults } = captureAuthoringTransaction();
          const last = clip.stages[clip.stages.length - 1] ?? null;
          const clipArchitectureId = capabilities.forClip(clip).architectureId;
          const lockedArchitecture = clipArchitectureId === NONE_ARCHITECTURE_ID || clipArchitectureId === "unsupported" ? void 0 : clipArchitectureId;
          const stage = buildDefaultStage(
            defaults,
            getDefaultStageModel(defaults, lockedArchitecture),
            last,
            clip.frameRefs.length,
            clip.loras.map(
              (entry) => defaultLoraWeight(defaults, entry.name)
            ),
            clip.icLoras.map(
              (entry) => defaultLoraWeight(defaults, entry.lora)
            )
          );
          stage.skipped = last?.skipped === true;
          if (clipArchitectureId === NONE_ARCHITECTURE_ID && clip.stages.length === 0) {
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
          if (!clip || clip.stages.length === 0 || stageIdx <= 0 || stageIdx >= clip.stages.length) {
            return null;
          }
          const transaction = captureAuthoringTransaction();
          clip.stages.splice(stageIdx, 1);
          for (const entry of clip.icLoras) {
            if (entry.stage === stageIdx) {
              entry.stage = IC_LORA_STAGE_ALL;
            } else if (entry.stage > stageIdx) {
              entry.stage -= 1;
            }
            canonicalizeArchitectureIcLoraFields(
              transaction.capabilities.forClip(clip).architectureId,
              entry
            );
          }
          reconcileClipArchitectureIdentity(
            clip,
            transaction.capabilities.catalog
          );
          reconcileArchitectureIncomingIcLoraDrives(
            clips,
            transaction.generatedEntryMode,
            transaction.capabilities.catalog
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
    const toggleClipSkip = (clipIdx) => {
      structuralCommit((clips) => {
        const clipId = clips[clipIdx]?.id;
        return clipId ? {
          command: { type: "clip.toggle-skip", clipId },
          selection: "render"
        } : null;
      });
    };
    const toggleStageSkip = (clipIdx, stageIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        const stageId = clip?.stages[stageIdx]?.id;
        return clip?.id && stageId ? {
          command: {
            type: "stage.toggle-skip",
            clipId: clip.id,
            stageId
          },
          selection: "render"
        } : null;
      });
    };
    return {
      addRefEntry,
      deleteRefEntry,
      addClipReference,
      deleteClipReference,
      addPromptWindow,
      deleteWindowEntry,
      createRetake,
      removeRetake,
      deleteClip,
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
  var createDetailSelectionOperations = (structuralCommit, captureAuthoringTransaction) => {
    const domain = createDetailSelectionDomainOperations(
      structuralCommit,
      captureAuthoringTransaction
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
    let renderEnabled = true;
    let activeSnapshot = null;
    let draftQueue;
    let renderImplementation = () => {
    };
    const render = (meta, snapshot = captureAuthoringTransactionSnapshot()) => {
      renderEnabled = true;
      renderImplementation(meta, snapshot);
    };
    const authoring = () => activeSnapshot ?? captureAuthoringTransactionSnapshot();
    let renderedSelection = null;
    const focus = createDetailFocusSession({
      getDock: () => dockEl,
      isRendering: () => rendering,
      flushPending: () => draftQueue?.flush()
    });
    const syncValueDerivedUi = (selection) => {
      if (!selection || !dockEl) {
        return;
      }
      const state = getState();
      const breadcrumb = dockEl.querySelector(".vst-detail-crumb");
      if (breadcrumb) {
        breadcrumb.textContent = detailBreadcrumb(
          selection,
          state.clips,
          state.fps,
          authoring().capabilities
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
      captureAuthoringTransactionSnapshot
    );
    const context = {
      commit: draftQueue.commit,
      commitState: draftQueue.commitState,
      debouncedCommit: draftQueue.debouncedCommit,
      debouncedCommitState: draftQueue.debouncedCommitState,
      buildClampedNumber: draftQueue.buildClampedNumber,
      structuralCommit: draftQueue.structuralCommit,
      render,
      authoring,
      addRefEntry: selectionOperations.addRefEntry,
      deleteRefEntry: selectionOperations.deleteRefEntry,
      addClipReference: selectionOperations.addClipReference,
      deleteClipReference: selectionOperations.deleteClipReference,
      addPromptWindow: selectionOperations.addPromptWindow,
      deleteWindowEntry: selectionOperations.deleteWindowEntry,
      createRetake: selectionOperations.createRetake,
      removeRetake: selectionOperations.removeRetake,
      deleteClip: selectionOperations.deleteClip,
      addStage: selectionOperations.addStage,
      deleteStage: selectionOperations.deleteStage,
      selectStage: selectionOperations.selectStage,
      toggleClipSkip: selectionOperations.toggleClipSkip,
      toggleStageSkip: selectionOperations.toggleStageSkip,
      getBoundBody: () => boundBody,
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
    renderImplementation = (meta, snapshot) => {
      if (!dockEl) {
        return;
      }
      activeSnapshot = snapshot;
      try {
        if (meta?.origin === "detail-strip" && meta.hint === "value-only" && renderedSelection && isSameSelection(getSelection(), renderedSelection)) {
          draftQueue.markCurrentSource();
          syncValueDerivedUi(renderedSelection);
          return;
        }
        draftQueue.flush();
        rendering = true;
        draftQueue.markCurrentSource();
        const detail = ensureDetail();
        const state = getState();
        const clips = state.clips;
        const rawSelection = getSelection();
        const selection = clampDetailSelection(
          rawSelection,
          clips,
          state.audioTracks,
          state.fps,
          snapshot.capabilities
        );
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
          state,
          selection,
          revealSelection
        });
        renderedSelection = selection;
      } finally {
        rendering = false;
        activeSnapshot = null;
      }
    };
    const onSelectionChanged = () => {
      if (suppressSelectionRender || !renderEnabled) {
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
      renderEnabled = false;
    };
    const attach = (body, dock, renderImmediately = true) => {
      renderEnabled = renderImmediately;
      if (boundBody === body && dockEl === dock) {
        return;
      }
      dispose();
      boundBody = body;
      dockEl = dock;
      renderEnabled = renderImmediately;
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
      if (renderImmediately) {
        render();
      }
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
        selector = `.vst-audio-span[data-track-idx="${sel.trackIdx}"]`;
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
      case "boundary-ref":
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
      const snapshot = getTimelineStore().getSnapshot();
      const clip = snapshot.state.clips[idx];
      if (!clip?.id || idx === 0 && clip.skipped !== true) {
        return;
      }
      dispatchDocumentCommand(
        { type: "clip.toggle-skip", clipId: clip.id },
        {
          origin: "linking",
          expectedRevision: snapshot.revision
        }
      );
    };
    const applyDelete = (idx) => {
      const clips = getClips();
      if (idx <= 0 || idx >= clips.length) {
        return;
      }
      clips.splice(idx, 1);
      reconcileArchitectureIncomingIcLoraDrives(
        clips,
        getRootGeneratedEntryMode(),
        getRootDefaults().modelCatalog
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
              if (state.idx < 0 || state.idx >= clips.length || clip.clipLengthFromAudio || clip.clipLengthFromControlNet || clip.initVideo) {
                return null;
              }
              const fps = documentFps(getState());
              const pxPerSecond = livePxPerSecond(body);
              const joinTrimSeconds = Math.max(
                0,
                Number(state.el.dataset.vstJoinTrimSeconds ?? 0) || 0
              );
              const newDuration = pxToDuration(
                width + joinTrimSeconds * pxPerSecond,
                pxPerSecond,
                fps
              );
              if (!applyClipDurationResize(
                clip,
                newDuration,
                getRootDefaults(),
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
                getRootGeneratedEntryMode(),
                getRootDefaults().modelCatalog
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
      setSelection({ kind: "prompt-major", clipIdx });
    }
  });

  // frontend/timelineReferencesTrack.ts
  var THUMB_SELECTOR = '.vst-refs-mark[data-vst-ref="thumb"]';
  var LANE_SELECTOR = ".vst-refs-lane[data-vst-ref-add]";
  var DRAGGING_CLASS2 = "vst-refs-dragging";
  var DRAG_THRESHOLD_PX3 = 5;
  var createTimelineReferencesTrack = (getAuthoring) => {
    let boundBody = null;
    let unregister = null;
    const canEditReferences = (clip, authoring = getAuthoring()) => authoring.capabilities.forClip(clip).decision("frameReferences").supported;
    const referenceEndpoints = (clip, authoring = getAuthoring()) => referenceEndpointPolicy(clip, authoring.defaults.modelCatalog);
    const resolveDragPolicy = (clip, fps, authoring) => ({
      supported: canEditReferences(clip, authoring),
      endpoints: referenceEndpoints(clip, authoring),
      frameGrid: resolvedClipFrameGrid(clip, authoring.defaults.modelCatalog),
      frameMax: getReferenceFrameMax(authoring.defaults, clip, fps)
    });
    const sameDragPolicy = (left, right) => left.supported === right.supported && left.frameGrid.frameGrid === right.frameGrid.frameGrid && left.frameGrid.frameGridOrigin === right.frameGrid.frameGridOrigin && left.frameMax === right.frameMax && left.endpoints.positions.length === right.endpoints.positions.length && left.endpoints.positions.every(
      (position, index) => position === right.endpoints.positions[index]
    );
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
    const addRefAtFrame = (clipIdx, frame, sourceRevision, authoring = getAuthoring()) => {
      const fps = documentFps(getState());
      let newRefIdx = -1;
      const saved = commitClipMutation(
        sourceRevision,
        "references-track",
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip || !canEditReferences(clip, authoring)) {
            return null;
          }
          const frameMax = getReferenceFrameMax(
            authoring.defaults,
            clip,
            fps
          );
          const endpoints = referenceEndpoints(clip, authoring);
          if (!endpoints.available) {
            return null;
          }
          const ref = buildDefaultRef();
          if (!endpoints.bounded) {
            ref.frame = clamp(
              Math.round(frame),
              REF_FRAME_MIN,
              frameMax
            );
          } else {
            const position = nextAllowedReferencePosition(
              clip.frameRefs,
              frameMax,
              endpoints.positions
            );
            if (!position) {
              return null;
            }
            ref.frame = position.frame;
            ref.fromEnd = position.fromEnd;
          }
          appendRefToClip(clip, ref);
          newRefIdx = clip.frameRefs.length - 1;
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
    const dragPositionAt = (state, clientX) => {
      const { bounded, supportsFirst, supportsLast } = state.policy.endpoints;
      if (bounded) {
        const rect2 = state.lane.getBoundingClientRect();
        const prefersLast = clientX - rect2.left >= rect2.width / 2;
        return {
          frame: REF_FRAME_MIN,
          fromEnd: supportsLast && (!supportsFirst || prefersLast)
        };
      }
      const rect = state.lane.getBoundingClientRect();
      const frame = pxToFrame(
        clientX - rect.left,
        rect.width,
        state.durationSeconds,
        state.fps,
        state.fromEnd,
        state.policy.frameGrid
      );
      if (!getTimelineAuthoringSettings().snap || rect.width <= 0) {
        return { frame, fromEnd: state.fromEnd };
      }
      const thresholdFrames = Math.max(
        1,
        SNAP_THRESHOLD_PX / rect.width * state.policy.frameMax
      );
      return {
        frame: Math.round(
          snapPoint(
            frame,
            [],
            [REF_FRAME_MIN, state.policy.frameMax],
            thresholdFrames
          )
        ),
        fromEnd: state.fromEnd
      };
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
        const position = dragPositionAt(state, ctx.event.clientX);
        positionRefMarker(
          state.mark,
          state.arrow,
          position.frame,
          position.fromEnd,
          state.generatedDurationSeconds,
          state.fps
        );
      },
      onCommit: (ctx) => {
        body.classList.remove(DRAGGING_CLASS2);
        if (!state.mark.isConnected || !state.lane.isConnected) {
          restoreDragPreview(state);
          return;
        }
        const position = dragPositionAt(state, ctx.event.clientX);
        const saved = commitClipMutation(
          state.sourceRevision,
          "references-track",
          (clips) => {
            const clip = clips[state.clipIdx];
            const ref = clip?.frameRefs?.[state.refIdx];
            const livePolicy = clip ? resolveDragPolicy(clip, state.fps, getAuthoring()) : null;
            if (!ref || !livePolicy) {
              return null;
            }
            if (!livePolicy.supported || !sameDragPolicy(state.policy, livePolicy) || ref.frame === position.frame && ref.fromEnd === position.fromEnd) {
              return null;
            }
            ref.frame = position.frame;
            ref.fromEnd = position.fromEnd;
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
      const documentSnapshot = getTimelineStore().getSnapshot();
      const clip = documentSnapshot.state.clips[clipIdx];
      const ref = clip?.frameRefs?.[refIdx];
      if (!clip || !ref) {
        return null;
      }
      const fps = documentFps(documentSnapshot.state);
      const policy = resolveDragPolicy(clip, fps, getAuthoring());
      if (!policy.supported) {
        me.preventDefault();
        return claimOnly();
      }
      const arrow = findArrow(clipIdx, refIdx);
      if (!policy.endpoints.available) {
        me.preventDefault();
        return claimOnly();
      }
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
        generatedDurationSeconds: policy.frameMax / fps,
        fps,
        policy,
        fromEnd: ref.fromEnd === true,
        sourceRevision: documentSnapshot.revision
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
      const authoring = getAuthoring();
      if (!canEditReferences(clip, authoring)) {
        return;
      }
      const rect = lane.getBoundingClientRect();
      const frame = pxToFrame(
        event.clientX - rect.left,
        rect.width,
        clip.duration,
        documentFps(getState()),
        false,
        resolvedClipFrameGrid(clip, authoring.defaults.modelCatalog)
      );
      addRefAtFrame(clipIdx, frame, currentRevision(), authoring);
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

  // frontend/timelineSelectionTracks.ts
  var RULES = [
    {
      selector: '.vst-audio-clip[data-vst-audio="clip"]',
      select: (element) => {
        const clipIdx = parseIntAttr(element, "data-clip-idx");
        return clipIdx === null ? null : { kind: "audio", clipIdx };
      }
    },
    {
      selector: "[data-vst-boundary-chip]",
      select: (element) => {
        const leftClipIdx = parseIntAttr(element, "data-left-clip-idx");
        return leftClipIdx === null ? null : { kind: "boundary", leftClipIdx };
      }
    }
  ];
  var createTimelineSelectionTracks = () => {
    let boundBody = null;
    const selector = RULES.map((rule) => rule.selector).join(", ");
    const activate = (target) => {
      const element = target.closest(selector);
      if (!(element instanceof HTMLElement)) {
        return;
      }
      const selection = RULES.find(
        (rule) => element.matches(rule.selector)
      )?.select(element);
      if (selection) {
        setSelection(selection);
      }
    };
    const onClick = (event) => {
      if (event.target instanceof Element) {
        activate(event.target);
      }
    };
    const onKeyDown = (event) => {
      if (!isActivateKey(event) || !(event.target instanceof Element) || !event.target.closest(selector)) {
        return;
      }
      event.preventDefault();
      activate(event.target);
    };
    const dispose = () => {
      boundBody?.removeEventListener("click", onClick);
      boundBody?.removeEventListener("keydown", onKeyDown);
      boundBody = null;
    };
    const attach = (body) => {
      if (boundBody === body) {
        return;
      }
      dispose();
      boundBody = body;
      body.addEventListener("click", onClick);
      body.addEventListener("keydown", onKeyDown);
    };
    return { attach, dispose };
  };

  // frontend/timelineView/toolbar.ts
  var renderDiagnosticPanel = (diagnostics = []) => {
    const content = diagnostics.map(
      (item) => `<div class="vst-diagnostic vst-diagnostic-${item.severity}" data-vst-diagnostic="${escapeHtml(item.code)}">${item.clipIdx === void 0 ? "" : `<strong>Clip ${item.clipIdx}:</strong> `}${escapeHtml(item.message)}</div>`
    ).join("");
    return content ? `<div class="vst-diagnostics" role="status">${content}</div>` : "";
  };
  var renderTimelineHeader = (clipCount, totalSeconds, fps, unit, pxPerSecond, options, timing) => {
    const toggleLabel = unit === "frames" ? "Show seconds" : "Show frames";
    const clipWord = `clip${clipCount === 1 ? "" : "s"}`;
    const totalLabel = unit === "frames" ? `${timing?.outputFrames ?? Math.round(totalSeconds * fps)}f` : formatSecondsTenth(totalSeconds);
    const zoomPct = Math.round(pxPerSecond / DEFAULT_PX_PER_SECOND * 100);
    const rawSelected = options?.selectedIndex;
    const selectedIndex = typeof rawSelected === "number" && Number.isInteger(rawSelected) && rawSelected >= 0 && rawSelected < clipCount ? rawSelected : null;
    const selectedHidden = selectedIndex === null ? " hidden" : "";
    const joinFrames = timing?.joinFrames ?? 0;
    const joinSeconds = timing?.joinSeconds ?? 0;
    const handleSeconds = timing?.boundaries.reduce(
      (sum, boundary) => sum + boundary.handleSeconds,
      0
    ) ?? 0;
    const joinFrameLabel = `${joinFrames > 0 ? "−" : ""}${joinFrames}f`;
    const joinSecondsLabel = `${joinSeconds > 0 ? "−" : ""}${formatSecondsTenth(joinSeconds)}`;
    const authoredLabel = formatSecondsTenth(
      timing?.authoredSeconds ?? totalSeconds
    );
    const secondary = unit === "frames" ? `${timing?.generatedFrames ?? 0}f generated · ${joinFrameLabel} shared` : handleSeconds > 0 ? `${authoredLabel} authored · +${formatSecondsTenth(handleSeconds)} handle · ${joinSecondsLabel} shared` : `${authoredLabel} authored · ${joinSecondsLabel} joins`;
    const readout = `<span class="vst-readout" data-vst-readout><span class="vst-readout-output" title="Published sequence length">${escapeHtml(totalLabel)} output</span><span class="vst-readout-detail" title="Authored length and resolved shared joins">${escapeHtml(secondary)}</span><span class="vst-dot" data-vst-readout-sel-dot${selectedHidden}>·</span><span class="vst-readout-sel" data-vst-readout-sel title="Selected clip"${selectedHidden}>${selectedIndex !== null ? `clip ${selectedIndex}` : ""}</span></span>`;
    const width = Math.max(0, Math.round(options?.width ?? 0));
    const height = Math.max(0, Math.round(options?.height ?? 0));
    const dimsExplicit = options?.dimsExplicit === true;
    const ratioId = dimsExplicit && width > 0 && height > 0 ? matchAspectRatio(width, height) : null;
    const dimsSource = dimsExplicit ? ratioId ? `${ratioId} aspect ratio` : "custom" : "inherited from image resolution";
    const fpsSource = "synced with Video FPS";
    const settingsTip = `Resolution: ${dimsSource}; FPS: ${fpsSource}. Click to edit.`;
    const settingsChip = `<button type="button" class="basic-button small-button vst-settings-chip" data-vst-settings title="${escapeHtml(settingsTip)}" aria-label="${escapeHtml(settingsTip)}"><span class="vst-settings-dims">${width}×${height}</span><span class="vst-settings-chip-sep" aria-hidden="true">·</span><span class="vst-settings-fps">${fps} fps</span></button>`;
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
  var hasRelayWindows = (clip) => (clip.promptWindows?.length ?? 0) > 0;
  var renderPromptTrackRow = (clips, layouts, pxPerSecond, globalPrompt, capabilities) => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const relayLane = (clip) => laneVisible(clip, "promptRelay", hasRelayWindows(clip), capabilities);
    const relayTrack = clips.some(relayLane);
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
      parts.push(
        `<div class="vst-major-seg${majorClass}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(majorTitle)}">` + overlays + `<span class="vst-major-text">${escapeHtml(majorText)}</span></div>`
      );
      const minorSegments = windows.map((window2, windowIdx) => {
        const geometry = promptWindowGeom(layout, window2, pxPerSecond);
        const text2 = `${window2.prompt ?? ""}`.trim();
        const label = text2 === "" ? "(empty)" : truncate(text2, 60);
        const title = relaySupported ? `${text2 || "(empty minor prompt)"} · Shift+click to delete` : "Persisted relay prompt is unsupported by this architecture; click to inspect or Shift+click to delete";
        return `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${windowIdx}" style="left:${geometry.leftPx}px;width:${geometry.widthPx}px" title="${escapeHtml(title)}"><span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span><span class="vst-minor-text">${escapeHtml(label)}</span><span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span></div>`;
      }).join("");
      if (relayLane(clip)) {
        parts.push(
          `<div class="vst-minor-lane${relaySupported ? "" : " vst-capability-disabled"}"${relaySupported ? " data-vst-prompt-add" : ""} data-clip-idx="${i}" style="left:${layout.startPx}px;width:${width}px" title="${relaySupported ? "Click empty space to add a minor prompt" : "Relay prompts are unsupported; existing windows can be inspected or removed"}">${minorSegments}</div>`
        );
      }
    }
    return `<div class="vst-track-row vst-track-prompt${relayTrack ? "" : " vst-no-relay"}">` + renderTrackHead(
      "vst-track-icon-prompt",
      "✎",
      "Prompt",
      headTag("major", "Major", { active: true }) + (relayTrack ? headTag("relay", "Relay", {
        active: clips.some(hasRelayWindows)
      }) : "")
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
  var persistedClipAudio = (clip) => clip.audioSource !== "Native" || clip.uploadedAudio !== null || clip.reuseAudio === true || clip.clipLengthFromAudio === true || clip.saveAudioTrack === true;
  var clipAudioLaneVisible = (clip, capabilities) => {
    const view = capabilities?.forClip(clip);
    return view === void 0 || view.clipAudio.supported || persistedClipAudio(clip);
  };
  var audioTrackTag = (trackIdx) => `A${trackIdx + 1}`;
  var audioTrackName = (track, trackIdx) => track.source.reference || track.source.uploadedAudio?.fileName || `Audio track ${audioTrackTag(trackIdx)}`;
  var renderTimelineAudioSpanBlock = (track, trackIdx, totalSeconds) => {
    const span = track.spans[0];
    if (!span || span.timelineStartSeconds === null || span.timelineLengthSeconds === null) {
      return "";
    }
    const { startSeconds: start, endSeconds: end } = spanGeometry(
      span.timelineStartSeconds,
      span.timelineLengthSeconds,
      totalSeconds
    );
    const labelText = audioTrackName(track, trackIdx);
    const rangeLabel = `${roundToTenth(start)}–${roundToTenth(end)} s`;
    const waveform = audioSpanWaveBarHeights(trackIdx, 40).map((height) => `<span style="height:${height}%"></span>`).join("");
    return renderWindowSpan({
      className: "vst-audio-span",
      extraClassName: `vst-audio-span-tone-${trackIdx % 5}`,
      dataAttrs: `data-vst-audio-span data-track-idx="${trackIdx}"`,
      edgeAttr: "data-vst-audio-span-edge",
      labelClass: "vst-audio-label",
      label: labelText,
      title: `${labelText} · ${rangeLabel} · drag to move/resize · Shift+click to delete`,
      ariaLabel: `Edit audio track ${audioTrackTag(trackIdx)}`,
      startSeconds: start,
      lengthSeconds: end - start,
      durationSeconds: totalSeconds,
      decoration: `<span class="vst-audio-span-wave" aria-hidden="true">${waveform}</span>`
    });
  };
  var renderTimelineAudioSpanLanes = (tracks, totalSeconds, totalWidthPx) => {
    const place = (laneIdx) => `left:0;width:${totalWidthPx}px;--vst-audio-lane-idx:${laneIdx}`;
    const lanes = tracks.map(
      (track, trackIdx) => `<div class="vst-audio-track-lane" data-track-idx="${trackIdx}" style="${place(trackIdx)}">` + renderTimelineAudioSpanBlock(track, trackIdx, totalSeconds) + `</div>`
    );
    lanes.push(
      `<div class="vst-audio-track-lane vst-audio-track-lane-blank" data-vst-audio-track-add style="${place(tracks.length)}" title="Click or drag to add an audio track spanning the timeline"></div>`
    );
    return lanes.join("");
  };
  var renderAudioTrackRow = (clips, layouts, capabilities, audioTracks = [], pxPerSecond = 1, timelineTotalSeconds) => {
    const clipLane = (clip) => clipAudioLaneVisible(clip, capabilities);
    const clipRow = clips.some(clipLane);
    const clipBlocks = layouts.map((layout) => {
      const clip = clips[layout.index];
      if (!clip || !clipLane(clip)) {
        return "";
      }
      const badge = audioSourceBadge(clip.audioSource ?? "");
      const clipCapabilities = capabilities?.forClip(clip);
      const clipAudioSupported = clipCapabilities?.clipAudio.supported ?? true;
      const persistedAudio = persistedClipAudio(clip);
      const audioOperable = clipAudioSupported || persistedAudio;
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
      const renderedTitle = clipAudioSupported ? title : persistedAudio ? "Clip audio is unsupported; click persisted audio to inspect or remove it" : "Clip audio is unsupported by this architecture";
      const ariaLabel = clipAudioSupported ? `Edit audio for clip ${layout.index}` : persistedAudio ? `Inspect unsupported persisted audio for clip ${layout.index}` : `Audio unavailable for clip ${layout.index}`;
      return `<div class="vst-audio-clip${kindClass}${clipAudioSupported ? "" : " vst-capability-disabled"}"${audioOperable ? ' data-vst-audio="clip" role="button" tabindex="0"' : ' aria-disabled="true"'} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(renderedTitle)}" aria-label="${ariaLabel}"><span class="vst-audio-label">${escapeHtml(labelText)}</span>` + audioFlagChips(clip) + body + `</div>`;
    }).join("");
    const totalSeconds = timelineTotalSeconds ?? layouts.reduce(
      (max, layout) => Math.max(
        max,
        layout.startSeconds + layout.timelineDurationSeconds
      ),
      0
    );
    const totalWidthPx = totalSeconds * pxPerSecond;
    const overlayLanes = renderTimelineAudioSpanLanes(
      audioTracks,
      totalSeconds,
      totalWidthPx
    );
    const laneCount = Math.max(1, audioTracks.length + 1);
    const laneTags = clipRow ? [headTag("src", "A0", { active: true })] : [];
    for (let i = 0; i < laneCount; i++) {
      const blank = i === laneCount - 1;
      laneTags.push(
        headTag("track", blank ? "+ Audio" : audioTrackTag(i), {
          active: !blank,
          muted: blank,
          style: `--vst-audio-lane-idx:${i}`,
          action: blank ? `data-vst-audio-track-add title="Add an audio track spanning the timeline" aria-label="Add an audio track"` : void 0
        })
      );
    }
    const soloLane = !clipRow && audioTracks.length === 0;
    return `<div class="vst-track-row vst-track-audio${clipRow ? "" : " vst-no-clip-audio"}${soloLane ? " vst-audio-solo-lane" : ""}" style="--vst-audio-lane-count:${laneCount}">` + renderTrackHead(
      "vst-track-icon-audio",
      "♪",
      "Audio",
      laneTags.join("")
    ) + `<div class="vst-track-cell vst-audio-cell">${clipBlocks}${overlayLanes}</div></div>`;
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
      const marks = (clip.frameRefs ?? []).map((ref, refIdx) => {
        const isEnd = ref.fromEnd === true;
        const frame = Math.max(0, ref.frame ?? 0);
        const isPrimary = frame === 1 && !isEnd;
        const time = keyframeTimeSeconds(
          ref.frame,
          isEnd,
          layout.frameCount > 0 ? layout.frameCount / fps : layout.durationSeconds,
          fps
        );
        const left = keyframeLeftPercent(
          time,
          layout.frameCount > 0 ? layout.frameCount / fps : layout.durationSeconds
        );
        const source = refSourceLabel(ref.source ?? "");
        const image = ref.uploadedImage?.data;
        const thumbnailData = image ? backgroundImageDataAttr(mediaPreviewSrc(image)) : "";
        const frameLabel = `R ${isEnd ? "-" : ""}${frame}`;
        const thumbnailClass = `vst-refs-thumb${image ? " vst-refs-has-image" : ""}`;
        const alignClass = frame > REF_EDGE_ALIGN_FRAMES ? "" : isEnd ? " vst-refs-align-end" : " vst-refs-align-start";
        const kindClass = (isPrimary ? " vst-refs-primary" : "") + (isEnd ? " vst-refs-fromend" : "") + alignClass;
        const title = refsSupported ? `${source}${isPrimary ? " · cover frame" : ""}${isEnd ? " · from end" : ""} · frame ${frame} · ${formatTimeLabel(time, unit, fps)} · click to edit, drag to move · Shift+click to delete` : `Persisted reference ${refIdx} is unsupported by this architecture · click to inspect or Shift+click to delete`;
        const label = refsSupported ? `Edit reference ${refIdx} (${source}${isEnd ? ", from end" : ""})` : `Inspect unsupported persisted reference ${refIdx} for removal`;
        return `<div class="vst-refs-mark${kindClass}" data-vst-ref="thumb" data-clip-idx="${layout.index}" data-ref-idx="${refIdx}" style="left:${left}%" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(label)}"><span class="${thumbnailClass}"${thumbnailData}><span class="vst-refs-ph">${escapeHtml(frameLabel)}</span></span></div>`;
      }).join("");
      return `<div class="vst-refs-lane${refsSupported ? "" : " vst-capability-disabled"}"${refsSupported ? " data-vst-ref-add" : clip.frameRefs.length === 0 ? ' aria-disabled="true"' : ""} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${refsSupported ? "Click to add a frame reference here" : "Frame references are unsupported; existing references can be inspected or removed"}">${marks}</div>`;
    }).join("");
    return `<div class="vst-track-row vst-track-refs">` + renderTrackHead("vst-track-icon-refs", "⧉", "Frame References", "") + `<div class="vst-track-cell">${lanes}</div></div>`;
  };

  // frontend/timelineView.ts
  var renderRulerTicks = (layouts, totalSeconds, endPx, pxPerSecond, fps, unit, timing) => {
    const endLabel = formatRulerLabel(totalSeconds, unit, fps);
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond).filter(
      (tick) => Math.abs(tick.seconds - totalSeconds) > 1e-6 && !(formatRulerLabel(tick.seconds, unit, fps) === endLabel && Math.abs(tick.x - endPx) < 40)
    ).map(
      (tick) => `<span class="vst-tick vst-tick-grid" style="left:${tick.x}px"><span class="vst-tick-label">${escapeHtml(formatRulerLabel(tick.seconds, unit, fps))}</span></span>`
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
    const seamTicks = timing.boundaries.map((boundary) => {
      const editPoint = layouts[boundary.rightIdx]?.startPx ?? 0;
      return `<span class="vst-tick vst-tick-seam" style="left:${editPoint}px" aria-hidden="true"></span>`;
    });
    const endTick = `<span class="vst-tick vst-tick-end" style="left:${endPx}px"><span class="vst-tick-label">${escapeHtml(endLabel)}</span></span>`;
    const outputTick = timing.outputSeconds < totalSeconds - 1e-6 ? `<span class="vst-tick vst-tick-output" style="left:${timing.outputSeconds * pxPerSecond}px"><span class="vst-tick-label">${escapeHtml(formatRulerLabel(timing.outputSeconds, unit, fps))} output</span></span>` : "";
    return [
      ...minorTicks,
      ...gridTicks,
      ...seamTicks,
      outputTick,
      endTick
    ].join("");
  };
  var renderTimeline = (body, clips, options) => {
    const fps = safeFps(options?.fps);
    const unit = options?.unit === "frames" ? "frames" : "seconds";
    const pxPerSecond = clampPxPerSecond(
      options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND
    );
    body.dataset.vstPps = String(pxPerSecond);
    body.dataset.vstFps = String(fps);
    const timing = resolveTimelineTiming(clips, fps, options?.capabilities);
    const layouts = computeRegionLayout(clips, { pxPerSecond, timing });
    const totalSeconds = timelineDisplaySeconds(clips, timing);
    const totalPx = layouts.reduce(
      (max, layout) => Math.max(max, layout.startPx + layout.widthPx),
      0
    );
    const header = renderTimelineHeader(
      clips.length,
      timing.outputSeconds,
      fps,
      unit,
      pxPerSecond,
      options,
      timing
    );
    const diagnostics = renderDiagnosticPanel(options?.diagnostics);
    if (clips.length === 0) {
      body.innerHTML = `${header}${diagnostics}<div class="vst-empty"><div class="vst-empty-icon" aria-hidden="true">🎬</div><div class="vst-empty-title">No clips yet.</div><div class="vst-empty-hint">Use the button below to start building your sequence.</div><button type="button" class="basic-button btn-primary vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button></div>`;
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
      options?.capabilities,
      timing,
      pxPerSecond
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
      pxPerSecond,
      timing.outputSeconds
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML = `${header}${diagnostics}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px"><div class="vst-ruler-row"><div class="vst-corner">Timeline</div><div class="vst-ruler">${renderRulerTicks(layouts, totalSeconds, totalSeconds * pxPerSecond, pxPerSecond, fps, unit, timing)}</div></div>` + promptRow + videoRow + referencesRow + renderedAudioRow + `</div></div>`;
    applyBackgroundImages(body);
    wireTimelineToolbar(body, options);
    wireTimelineZoomWheel(body, options);
  };

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
    let catalogUnsub = null;
    const timelineBody = () => document.getElementById(TIMELINE_BODY_ID);
    const scrollEl = () => timelineBody()?.querySelector(".vst-scroll") ?? null;
    const capabilities = () => captureAuthoringTransactionSnapshot().capabilities;
    const viewport = createTimelineViewport({
      refresh: () => refresh(),
      totalSeconds: () => {
        const state = getState();
        const timing = resolveTimelineTiming(
          state.clips,
          safeFps(state.fps),
          capabilities()
        );
        return timelineDisplaySeconds(state.clips, timing);
      },
      timelineBody,
      scrollElement: scrollEl
    });
    const detailStrip = createTimelineDetailStrip();
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack(capabilities);
    const promptTrack = createTimelinePromptTrack(capabilities);
    const audioSpanTrack = createTimelineAudioSpanTrack(capabilities);
    const selectionTracks = createTimelineSelectionTracks();
    const referencesTrack = createTimelineReferencesTrack(
      captureAuthoringTransactionSnapshot
    );
    let addClipInFlight = false;
    let historyNeedsRebase = true;
    const hasAuthoritativeCatalog = () => getArchitectureCatalogSnapshot().catalog !== null;
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
    const rebaseHistoryIfReady = () => {
      if (!hasAuthoritativeCatalog()) {
        historyNeedsRebase = true;
        return;
      }
      if (historyNeedsRebase) {
        history.rebase();
        historyNeedsRebase = false;
      }
    };
    const hostLifecycle = createTimelineHostLifecycle({
      refresh: () => refresh(),
      refreshCatalog: () => {
        requestArchitectureCatalog(true);
      },
      syncFromCarrier: () => {
        if (!hasAuthoritativeCatalog()) {
          return;
        }
        rebaseHistoryIfReady();
        getTimelineStore().syncFromCarrier();
      },
      flushPending: () => {
        if (hasAuthoritativeCatalog()) {
          detailStrip.flushPending();
        }
      },
      undo: () => hasAuthoritativeCatalog() && history.undo(),
      redo: () => hasAuthoritativeCatalog() && history.redo()
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
        const { defaults } = captureAuthoringTransactionSnapshot();
        const defaultModel = getDefaultStageModel(defaults);
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
          prev.boundaryOutReferenceScale = prevJoin.boundaryOutReferenceScale;
          prev.boundaryOutReferenceIncludeSoundtrack = prevJoin.boundaryOutReferenceIncludeSoundtrack;
          prev.boundaryOutOverlap = prevJoin.boundaryOutOverlap;
        }
        clips.push(buildDefaultClip(defaults, defaultModel, false, prev));
        saveClips(clips, { origin: "timeline" });
        setSelection({
          kind: "clip",
          clipIdx: clips.length - 1,
          stageIdx: 0
        });
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
      const transaction = captureAuthoringTransactionSnapshot();
      const catalogSnapshot = transaction.catalogStatus;
      if (renderBlockingArchitectureCatalogStatus(
        body,
        catalogSnapshot,
        () => requestArchitectureCatalog(true)
      )) {
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
          diagnostics: deriveAuthoringDiagnostics(
            clips,
            transaction.capabilities
          ),
          capabilities: transaction.capabilities
        });
        renderRetainedArchitectureCatalogStatus(
          body,
          catalogSnapshot,
          () => requestArchitectureCatalog(true)
        );
        viewport.restoreScroll(previousScroll);
        linking.reapplySelection(body, clips.length);
        detailStrip.render(meta, transaction);
        applySelectionHighlight(body);
      } catch (error) {
        console.warn("VideoStages: timeline render failed", error);
      }
    };
    const refresh = () => renderAll();
    const requestArchitectureCatalog = (forceRefresh = false) => {
      const currentCatalog = getArchitectureCatalogSnapshot();
      if (!forceRefresh && currentCatalog.catalog && currentCatalog.status !== "refreshing") {
        renderAll();
        return;
      }
      if (forceRefresh) {
        refreshAuthoritativeArchitectureCatalog();
      } else {
        loadAuthoritativeArchitectureCatalog();
      }
    };
    const init = () => {
      historyNeedsRebase = true;
      viewport.load();
      injectTimelineTab();
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (body) {
        retakeTrack.attach(body, gestures);
        audioSpanTrack.attach(body, gestures);
        linking.attach(body, gestures);
        promptTrack.attach(body, gestures);
        selectionTracks.attach(body);
        referencesTrack.attach(body, gestures);
        detailStrip.attach(body, ensureDock(body), false);
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
      rebaseHistoryIfReady();
      hostLifecycle.bind();
      catalogUnsub?.();
      catalogUnsub = subscribeArchitectureCatalog((snapshot) => {
        if (snapshot.status === "ready" && snapshot.catalog) {
          getTimelineStore().invalidate();
          historyNeedsRebase = true;
          rebaseHistoryIfReady();
        }
        renderAll();
      });
      requestArchitectureCatalog();
    };
    const dispose = () => {
      catalogUnsub?.();
      catalogUnsub = null;
      hostLifecycle.dispose();
      retakeTrack.dispose();
      audioSpanTrack.dispose();
      linking.dispose();
      promptTrack.dispose();
      gestures.dispose();
      selectionTracks.dispose();
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
