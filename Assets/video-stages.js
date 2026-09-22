"use strict";
(() => {
  // frontend/architectureCatalogStatusView.ts
  var detailDock = (body) => body.parentElement?.querySelector(":scope > .vst-detail") ?? null;
  var setDockAvailable = (body, available) => {
    const dock = detailDock(body);
    if (dock) {
      dock.hidden = !available;
    }
  };
  var retryButton = (onRetry) => {
    const button2 = document.createElement("button");
    button2.type = "button";
    button2.className = "basic-button vst-catalog-retry";
    button2.textContent = "Retry";
    button2.addEventListener("click", onRetry);
    return button2;
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

  // frontend/utils.ts
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var toNumber = (value, fallback) => {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : fallback;
  };
  var roundToTenth = (seconds2) => Math.round(seconds2 * 10) / 10;
  var gridCeil = (seconds2) => Math.ceil(seconds2 * 10) / 10;
  var gridFloor = (seconds2) => Math.floor(seconds2 * 10) / 10;
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

  // frontend/authoringMedia.ts
  var visitAuthoringMedia = (root, visitor) => {
    const visit = (value, replace) => {
      if (Array.isArray(value)) {
        value.forEach((entry, index) => {
          visit(entry, (next) => {
            value[index] = next;
          });
        });
        return;
      }
      if (!isRecord(value)) {
        return;
      }
      let current = value;
      if (typeof value.data === "string") {
        visitor(value, (next) => {
          current = next;
          replace(next);
        });
      }
      if (!isRecord(current)) {
        return;
      }
      const record = current;
      for (const key of Object.keys(record)) {
        visit(record[key], (next) => {
          record[key] = next;
        });
      }
    };
    visit(root, () => {
    });
  };

  // frontend/browserMediaStore.ts
  var HANDLE_PREFIX = "vst-browser-media:";
  var assets = /* @__PURE__ */ new Map();
  var handlesByData = /* @__PURE__ */ new Map();
  var nextHandle = 0;
  var createHandle = () => {
    const randomId = globalThis.crypto?.randomUUID?.();
    return `${HANDLE_PREFIX}${randomId ?? ++nextHandle}`;
  };
  var isBrowserMediaHandle = (value) => value.startsWith(HANDLE_PREFIX);
  var containsBrowserMediaHandle = (value) => value.includes(HANDLE_PREFIX);
  var storeBrowserMedia = (data) => {
    if (!data.startsWith("data:")) {
      return data;
    }
    const existing = handlesByData.get(data);
    if (existing) {
      return existing;
    }
    const handle = createHandle();
    assets.set(handle, data);
    handlesByData.set(data, handle);
    return handle;
  };
  var resolveBrowserMedia = (value) => assets.get(value) ?? value;
  var transformSerializedMedia = (serialized, transform) => {
    let parsed;
    try {
      parsed = JSON.parse(serialized);
    } catch {
      return { serialized, changed: false, missing: [] };
    }
    let changed = false;
    const missing = [];
    visitAuthoringMedia(parsed, (media) => {
      if (!media.data) {
        return;
      }
      const fileName = typeof media.fileName === "string" ? media.fileName : null;
      const result = transform(media.data, fileName);
      if (result.missing) {
        missing.push(fileName ?? "unnamed media");
      }
      if (result.data !== media.data) {
        media.data = result.data;
        changed = true;
      }
    });
    return {
      serialized: changed ? JSON.stringify(parsed) : serialized,
      changed,
      missing
    };
  };
  var externalizeSerializedBrowserMedia = (serialized) => transformSerializedMedia(serialized, (data) => ({
    data: storeBrowserMedia(data)
  }));
  var materializeSerializedBrowserMedia = (serialized) => transformSerializedMedia(serialized, (data) => {
    if (!isBrowserMediaHandle(data)) {
      return { data };
    }
    const materialized = assets.get(data);
    return materialized ? { data: materialized } : { data: "", missing: true };
  });

  // frontend/generationMedia.ts
  var materializeGenerationMedia = (input2) => {
    const serialized = input2.videostages;
    if (typeof serialized !== "string" || !containsBrowserMediaHandle(serialized)) {
      return;
    }
    const materialized = materializeSerializedBrowserMedia(serialized);
    if (materialized.missing.length > 0) {
      throw new Error(
        `Reselect browser-local VideoStages media: ${materialized.missing.join(", ")}.`
      );
    }
    input2.videostages = materialized.serialized;
  };
  var materializeGenerationMediaValue = (input2) => {
    if (isRecord(input2)) {
      materializeGenerationMedia(input2);
    }
  };

  // frontend/host/defaultVideoStagesHostBridge.ts
  var textInput = (id) => {
    const element2 = document.getElementById(id);
    return element2 instanceof HTMLInputElement || element2 instanceof HTMLTextAreaElement ? element2 : null;
  };
  var input = (id) => {
    const element2 = document.getElementById(id);
    return element2 instanceof HTMLInputElement ? element2 : null;
  };
  var select = (id) => {
    const element2 = document.getElementById(id);
    return element2 instanceof HTMLSelectElement ? element2 : null;
  };
  var selectOptions = (element2) => ({
    values: element2 ? Array.from(element2.options, (option) => option.value) : [],
    labels: element2 ? Array.from(element2.options, (option) => option.label) : []
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
  var requestJson = (url, data = {}) => new Promise((resolve, reject) => {
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
  });
  var pageMetadataForSource = (src) => {
    for (const element2 of document.querySelectorAll(
      "[data-src][data-metadata]"
    )) {
      const metadata = element2.dataset.metadata;
      if (element2.dataset.src === src && metadata && metadata !== "{}") {
        return metadata;
      }
    }
    return null;
  };
  var currentMetadataForSource = (src) => {
    const media = document.getElementById("current_image_img");
    return media instanceof HTMLElement && media.dataset.src === src && typeof currentMetadataVal === "string" ? currentMetadataVal : null;
  };
  var historyMetadataForSource = async (src) => {
    if (/^(?:blob|data):/i.test(src) || typeof getImageFullSrc !== "function") {
      return null;
    }
    const path = getImageFullSrc(src);
    const separator = path.lastIndexOf("/");
    const folder = separator < 0 ? "" : path.slice(0, separator);
    const fileName = separator < 0 ? path : path.slice(separator + 1);
    if (!fileName) {
      return null;
    }
    const response = await requestJson("ListImages", {
      path: folder,
      depth: 0,
      sortBy: "Name",
      sortReverse: false
    });
    if (!isRecord(response) || !Array.isArray(response.files)) {
      return null;
    }
    const file = response.files.find(
      (entry) => isRecord(entry) && (entry.src === fileName || entry.name === fileName)
    );
    return isRecord(file) && typeof file.metadata === "string" ? file.metadata : null;
  };
  var metadataForSource = async (src) => {
    try {
      const historyMetadata = await historyMetadataForSource(src);
      if (historyMetadata) {
        return historyMetadata;
      }
    } catch {
    }
    return pageMetadataForSource(src) ?? currentMetadataForSource(src);
  };
  var collectGenerationInput = (inputOverrides) => {
    if (typeof mainGenHandler === "undefined" || typeof mainGenHandler?.getGenInput !== "function") {
      throw new Error("Swarm generation input collection is unavailable.");
    }
    const { extra_metadata: requestedExtraMetadata, ...generationOverrides } = inputOverrides;
    const actualInput = mainGenHandler.getGenInput(generationOverrides, {});
    materializeGenerationMediaValue(actualInput);
    if (isRecord(requestedExtraMetadata)) {
      actualInput.extra_metadata = {
        ...isRecord(actualInput.extra_metadata) ? actualInput.extra_metadata : {},
        ...structuredClone(requestedExtraMetadata)
      };
    }
    return actualInput;
  };
  var clipboardLine = (response) => {
    const payload = response.payloadLocalPath || response.payloadPath || "";
    const workflow = response.workflowLocalPath || response.workflowPath || "";
    return `Payload: ${payload}, Generated Workflow: ${workflow}`;
  };
  var loadWorkflowInComfyUi = async (workflowJson) => {
    const tab = document.getElementById("maintab_comfyworkflow");
    if (!(tab instanceof HTMLElement)) {
      throw new Error("The ComfyUI tab is unavailable.");
    }
    tab.click();
    const workflow = JSON.parse(workflowJson);
    const deadline = Date.now() + 3e4;
    while (Date.now() < deadline) {
      const frame = document.getElementById("comfy_workflow_frame");
      const comfyWindow = frame instanceof HTMLIFrameElement ? frame.contentWindow : null;
      if (comfyWindow?.app && comfyWindow.LiteGraph) {
        comfyWindow.app.loadApiJson(
          comfyWindow.LiteGraph.cloneObject(workflow)
        );
        return;
      }
      await new Promise((resolve) => window.setTimeout(resolve, 100));
    }
    throw new Error("Timed out waiting for the ComfyUI tab to load.");
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
    hasBackendFeature: (feature) => typeof currentBackendFeatureSet !== "undefined" && Array.isArray(currentBackendFeatureSet) && currentBackendFeatureSet.includes(feature),
    notifyChanged: (element2, suppressPromptCompletion = false) => {
      const notify2 = () => triggerChangeFor(element2);
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
    requestJson,
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
    enableSliders: (element2) => {
      if (typeof enableSlidersIn === "function") {
        enableSlidersIn(element2);
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
    getCurrentVideoSource: () => {
      const media = document.getElementById("current_image_img");
      return media instanceof HTMLVideoElement && media.dataset.src ? media.dataset.src : null;
    },
    getMediaMetadata: metadataForSource,
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
        const {
          extra_metadata: requestedExtraMetadata,
          ...generationOverrides
        } = inputOverrides;
        if (!isRecord(requestedExtraMetadata)) {
          mainGenHandler.doGenerate(
            generationOverrides,
            {},
            materializeGenerationMediaValue
          );
          return;
        }
        mainGenHandler.doGenerate(
          generationOverrides,
          {},
          (actualInput) => {
            materializeGenerationMediaValue(actualInput);
            if (!isRecord(actualInput)) {
              return;
            }
            const collectedExtraMetadata = actualInput.extra_metadata;
            actualInput.extra_metadata = {
              ...isRecord(collectedExtraMetadata) ? collectedExtraMetadata : {},
              ...structuredClone(requestedExtraMetadata)
            };
          }
        );
      }
    },
    sendToComfyUiAndSave: async (inputOverrides) => {
      const actualInput = collectGenerationInput(inputOverrides);
      const response = await requestJson(
        "ComfyGetGeneratedWorkflow",
        actualInput
      );
      if (!isRecord(response) || typeof response.workflow !== "string") {
        const message = isRecord(response) && typeof response.error === "string" ? response.error : "SwarmUI returned no generated ComfyUI workflow.";
        throw new Error(message);
      }
      try {
        await loadWorkflowInComfyUi(response.workflow);
      } catch (error) {
        if (typeof showError === "function") {
          showError(
            `Failed to load workflow into the ComfyUI tab: ${error}`
          );
        }
      }
      const saved = await requestJson("WhatTheDuckSaveComfyWorkflow", {
        payload: JSON.stringify(actualInput),
        workflow: response.workflow
      });
      if (!isRecord(saved) || saved.success !== true) {
        const message = isRecord(saved) && typeof saved.error === "string" ? saved.error : "Failed to save the refine workflow.";
        throw new Error(message);
      }
      if (typeof copyText === "function") {
        copyText(clipboardLine(saved));
      }
    }
  });

  // frontend/host/index.ts
  var bridge = createDefaultVideoStagesHostBridge();
  var getVideoStagesHostBridge = () => bridge;

  // frontend/architectures/generatedFeatures.ts
  var ARCHITECTURE_FEATURE_LABELS = {
    stageAttention: "Stage attention",
    promptRelay: "Prompt relay",
    frameReferences: "Keyframes",
    stageReferenceStrengths: "Stage keyframe strengths",
    clipReferences: "Clip references",
    characterRefine: "Character refinement",
    characterRefineAppearanceReferences: "Appearance references",
    characterRefineIdentityReferences: "Identity references",
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
    "AceStepFun",
    "Previous Clip"
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
  var MEDIA_SOURCE_PREVIOUS_CLIP = "Previous Clip";
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
    AUDIO_SOURCE_ACE_STEP_FUN,
    AUDIO_SOURCE_PREVIOUS_CLIP
  ] = AUDIO_SOURCE_KINDS;
  var isAceStepFunAudioSource = (source) => parseAceStepFunIndex(source) !== null;
  var LITERAL_AUDIO_SOURCES = [
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_UPLOAD,
    AUDIO_SOURCE_CONTROLNET,
    AUDIO_SOURCE_PREVIOUS_CLIP
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
    return kind === AUDIO_SOURCE_UPLOAD || kind === AUDIO_SOURCE_PREVIOUS_CLIP || kind === AUDIO_SOURCE_CONTROLNET || kind === AUDIO_SOURCE_ACE_STEP_FUN;
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
    if (context.previousClipEnabled) {
      options.push({
        value: AUDIO_SOURCE_PREVIOUS_CLIP,
        label: context.previousClipLabel ?? "Previous Clip Output"
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

  // frontend/clipSemantics.ts
  var clipDurationOf = (clip) => clip ? Math.max(0, clip.duration || 0) : 0;
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

  // frontend/renderUtils.ts
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
  var snapDurationToFps = (seconds2, fps) => {
    if (!Number.isFinite(seconds2) || seconds2 <= 0 || !Number.isFinite(fps) || fps <= 0) {
      return seconds2;
    }
    const frames = Math.max(1, Math.ceil(seconds2 * fps));
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

  // frontend/generatedUpscaleModes.ts
  var UPSCALE_MODE_UNSUPPORTED = "unsupported";
  var UPSCALE_METHOD_PREFIXES = [
    ["latentmodel-", "latent-model"],
    ["latent-", "latent"],
    ["pixel-", "pixel"],
    ["model-", "model"]
  ];

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
    for (const [prefix, mode] of UPSCALE_METHOD_PREFIXES) {
      if (normalized.startsWith(prefix) && normalized.slice(prefix.length).trim().length > 0) {
        return mode;
      }
    }
    return UPSCALE_MODE_UNSUPPORTED;
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
  var stageProducesDecodedVideo = (stage, supports) => {
    if (stage.control > 0) {
      return true;
    }
    const upscaleMode = upscaleModeForMethod(stage.upscaleMethod ?? "");
    if ((stage.upscale ?? 1) === 1 || upscaleMode !== "latent" && upscaleMode !== "latent-model") {
      return false;
    }
    return supports(
      upscaleMode === "latent" ? "latentUpscale" : "latentModelUpscale"
    );
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
      if (stageProducesDecodedVideo(
        stage,
        (feature) => clipCapabilities === null ? true : architectureFeatureSupport(feature, clipCapabilities)
      ) || stageIndex === stages.length - 1 && retakeCanExecute) {
        return true;
      }
      return false;
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
  var isRecord2 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
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
  var isRuleDecision = (value) => isRecord2(value) && hasExactKeys(value, ["support", "code", "reason", "constraints"]) && isCapabilitySupport(value.support) && isTrimmedNonEmpty(value.code) && isTrimmedNonEmpty(value.reason) && (value.constraints === null || isRecord2(value.constraints) && value.support !== "unsupported");
  var isBoundaryRule = (value) => {
    if (!isRuleDecision(value)) {
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
    if (!isRecord2(value) || !hasExactKeys(value, ["features", "entryModes", "audioSourceKinds"])) {
      return false;
    }
    return isUniqueStringArray(value.features) && isAudioSourceKindArray(value.audioSourceKinds) && isEntryModeArray(value.entryModes);
  };
  var hasCompleteBoundaryRules = (value) => {
    if (!isRecord2(value)) {
      return false;
    }
    const keys = Object.keys(value);
    return keys.length === BOUNDARY_MODES.length && BOUNDARY_MODES.every((mode) => isBoundaryRule(value[mode]));
  };
  var parseVideoArchitectureCatalog = (value) => {
    if (!isRecord2(value) || !hasExactKeys(value, ["schemaVersion", "architectures", "models"]) || value.schemaVersion !== 2 || !Array.isArray(value.architectures) || !Array.isArray(value.models)) {
      return null;
    }
    const architectures = [];
    const architectureIds = /* @__PURE__ */ new Set();
    for (const raw of value.architectures) {
      if (!isRecord2(raw) || !hasExactKeys(raw, [
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
      if (!isRecord2(raw) || !hasExactKeys(raw, [
        "modelName",
        "architectureId",
        "modelProfileId",
        "modelClassId",
        "compatibilityClassId",
        "frameGrid",
        "frameGridOrigin",
        "capabilities",
        "enhancements"
      ]) || !isTrimmedNonEmpty(raw.modelName) || !isTrimmedNonEmpty(raw.architectureId) || !architectureIds.has(raw.architectureId) || !isTrimmedNonEmpty(raw.modelProfileId) || !isTrimmedNonEmpty(raw.modelClassId) || !isTrimmedNonEmpty(raw.compatibilityClassId) || !Number.isSafeInteger(raw.frameGrid) || Number(raw.frameGrid) < 1 || Number(raw.frameGrid) > MAX_FRAME_GRID || !Number.isSafeInteger(raw.frameGridOrigin) || Number(raw.frameGridOrigin) < 1 || Number(raw.frameGridOrigin) > Number(raw.frameGrid) || !isCapabilities(raw.capabilities) || !isRecord2(raw.enhancements) || !hasExactKeys(raw.enhancements, ["referencePositions"]) || !isFrameReferencePositionArray(raw.enhancements.referencePositions)) {
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

  // frontend/constants.ts
  var DEFAULT_GENERATED_ENTRY_MODE = "text-to-video";
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
  var AUDIO_SPAN_VOLUME_MIN = 1e-5;
  var AUDIO_SPAN_VOLUME_MAX = 1e5;
  var AUDIO_SPAN_VOLUME_DEFAULT = 1;
  var ROOT_DIMENSION_MIN = 32;
  var ROOT_DIMENSION_SLIDER_MIN = 256;
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
    const resolved = resolveBrowserMedia(`${value ?? ""}`);
    if (resolved.startsWith("data:") || resolved.startsWith("blob:")) {
      return resolved;
    }
    const prefix = getVideoStagesHostBridge().getMediaOutputPrefix();
    return `${prefix}/${resolved}`;
  };

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

  // frontend/architectures/ltx2/generatedIcLora.ts
  var IC_LORA_AUTO = "[AUTO]";
  var IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";
  var IC_LORA_WEIGHTS = [
    {
      id: "ingredients",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.5-22b-ic-lora-ingredients-0.9.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "detailer",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2-19b-IC-LoRA-Detailer/resolve/main/ltx-2-19b-ic-lora-detailer.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "pixel-spatial-upscaler-2.5-x2",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.5-22b-ic-lora-pixel-spatial-upscaler-x2-1.0.safetensors",
      dimensionDownscaleFactor: 2
    },
    {
      id: "deblur",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Deblur/resolve/main/ltx-2.5-22b-ic-lora-deblur-0.9.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "decompression",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Decompression/resolve/main/ltx-2.5-22b-ic-lora-decompression-0.9.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "water-simulation",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.5-22b-ic-lora-water-simulation-0.9.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "colorization",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Colorization/resolve/main/ltx-2.5-22b-ic-lora-colorization-0.9.safetensors",
      dimensionDownscaleFactor: 1
    },
    {
      id: "day-to-night",
      weightsUrl: "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.5-22b-ic-lora-day-to-night-0.9.safetensors",
      dimensionDownscaleFactor: 1
    }
  ];

  // frontend/architectures/ltx2/icLoraPresets.ts
  var DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT = {
    acceptedKinds: ["image", "video"],
    driveData: "visual"
  };
  var AUDIO_DRIVE_MEDIA_CONTRACT = {
    acceptedKinds: ["audio", "video"],
    driveData: "audio"
  };
  var icLoraDriveMediaContractForData = (driveData) => {
    if (driveData === "audio") {
      return AUDIO_DRIVE_MEDIA_CONTRACT;
    }
    if (driveData === "visual") {
      return DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
    }
    return { acceptedKinds: [], driveData: "none" };
  };
  var IC_LORA_PRESET_CUSTOM_ID = "custom";
  var IC_LORA_DEFAULT_PRESET_ID = "pixel-spatial-upscaler-2.5-x2";
  var IC_LORA_PRESENTATION = {
    ingredients: {
      displayName: "Ingredients",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      note: "Feed the reference sheet as drive media (a still image works). Prompt pattern: '### Reference Sheet Description' per cell, then '### Target Description'."
    },
    detailer: {
      displayName: "Detailer",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      note: "Enhances fine details and textures. Apply to a low-control refine stage. Trained on LTX-2 19B.",
      driveMedia: icLoraDriveMediaContractForData("none")
    },
    "pixel-spatial-upscaler-2.5-x2": {
      displayName: "Pixel Spatial Upscaler 2.5 ×2",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      note: "Apply on a refine stage with Upscale ×2 and source Incoming media. Dims snap to multiples of 64."
    },
    deblur: {
      displayName: "Deblur",
      triggerPhrase: "DEBLUR",
      strength: 1,
      controlType: "none",
      note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened."
    },
    decompression: {
      displayName: "Decompression",
      triggerPhrase: "ENHANCE QUALITY",
      strength: 1,
      controlType: "none",
      note: "Removes compression artifacts; feed a low-bitrate clip directly."
    },
    "water-simulation": {
      displayName: "Water Simulation",
      triggerPhrase: "ADD WATER",
      strength: 1,
      controlType: "none",
      note: "Feed a dry clip. The weights are pre-scaled for strength 1.0."
    },
    colorization: {
      displayName: "Colorization",
      triggerPhrase: "COLORIZE",
      strength: 1,
      controlType: "none",
      note: "Feed the grayscale clip; describe the restored colors after the COLORIZE trigger."
    },
    "day-to-night": {
      displayName: "Day to Night",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      note: "Relights a daytime clip to night. Prompt the night look and add 'Only the lighting changes from day to night'. Best at ~4s clips."
    }
  };
  var IC_LORA_PRESETS = IC_LORA_WEIGHTS.map(
    (weights) => ({ ...IC_LORA_PRESENTATION[weights.id], ...weights })
  );
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

  // frontend/generatedReferenceScale.ts
  var REFERENCE_SCALE_FULL = 1;
  var REFERENCE_SCALES = [1, 0.5, 0.25];

  // frontend/clipReferenceAuthoring.ts
  var clipReferenceAuthoring = (capabilities) => {
    const general = capabilities.decision("clipReferences");
    if (general.supported) {
      return { mode: "all", decision: general, feature: "clipReferences" };
    }
    const identity = capabilities.decision("characterRefineIdentityReferences");
    return identity.supported ? {
      mode: "identity-images",
      decision: identity,
      feature: "characterRefineIdentityReferences"
    } : { mode: "none", decision: general, feature: "clipReferences" };
  };
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
    startSeconds: 0,
    lengthSeconds: 0,
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
  var normalizeInitVideo = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const source = trimmedText(value.source) || MEDIA_SOURCE_UPLOAD;
    const previousClip = source === MEDIA_SOURCE_PREVIOUS_CLIP;
    const data = trimmedText(value.data);
    if (!previousClip && !data) {
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
      source: previousClip ? MEDIA_SOURCE_PREVIOUS_CLIP : MEDIA_SOURCE_UPLOAD,
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
      const raw = isRecord(entry) ? entry : {};
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
        mediaScale: kind === "video" ? normalizeClipReferenceScale(raw.mediaScale) : REFERENCE_SCALE_FULL,
        ...normalizeClipReferenceTrim(raw, kind, mediaDurationSeconds)
      };
      lengthClaimed = lengthClaimed || reference.drivesClipLength;
      return reference;
    });
  };
  var normalizeClipReferenceTrim = (raw, kind, mediaDurationSeconds) => {
    if (kind === "image") {
      return { startSeconds: 0, lengthSeconds: 0 };
    }
    return normalizeTimedMediaRange(
      raw.startSeconds,
      raw.lengthSeconds,
      mediaDurationSeconds
    );
  };
  var normalizeTimedMediaRange = (startValue, lengthValue, mediaDurationSeconds) => {
    if (mediaDurationSeconds <= 0) {
      return { startSeconds: 0, lengthSeconds: 0 };
    }
    const startSeconds = Math.min(
      roundToTenth(nonNegativeNumber(startValue)),
      mediaDurationSeconds
    );
    const lengthSeconds = Math.min(
      roundToTenth(nonNegativeNumber(lengthValue)),
      mediaDurationSeconds - startSeconds
    );
    return lengthSeconds > 0 ? { startSeconds, lengthSeconds } : { startSeconds: 0, lengthSeconds: 0 };
  };
  var clipReferenceUsedRange = (reference) => reference.lengthSeconds > 0 ? {
    startSeconds: reference.startSeconds,
    lengthSeconds: reference.lengthSeconds
  } : {
    startSeconds: 0,
    lengthSeconds: reference.mediaDurationSeconds
  };
  var clipReferenceUsedSeconds = (reference) => clipReferenceUsedRange(reference).lengthSeconds;
  var clipReferenceDurationSeconds = (references) => {
    const index = clipLengthReferenceIndex(references);
    return index < 0 ? null : clipReferenceUsedSeconds(references[index]);
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
  var mediaKindByExtension = {
    apng: "image",
    avif: "image",
    bmp: "image",
    gif: "image",
    jpeg: "image",
    jpg: "image",
    png: "image",
    svg: "image",
    webp: "image",
    flac: "audio",
    m4a: "audio",
    mp3: "audio",
    ogg: "audio",
    wav: "audio",
    weba: "audio",
    avi: "video",
    m4v: "video",
    mkv: "video",
    mov: "video",
    mp4: "video",
    webm: "video"
  };
  var isAcceptedIcLoraDriveMedia = (value, fileName, acceptedKinds) => {
    const resolved = resolveBrowserMedia(value);
    const dataKind = /^data:(image|video|audio)\//i.exec(resolved)?.[1];
    const named = fileName || resolved.split(/[?#]/, 1)[0] || "";
    const extension = named.slice(named.lastIndexOf(".") + 1).toLowerCase();
    const kind = dataKind ?? mediaKindByExtension[extension] ?? null;
    return kind !== null && acceptedKinds.includes(kind);
  };
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
    if (!isRecord(raw)) {
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
    const driveMedia = driveSource === MEDIA_SOURCE_UPLOAD && driveData !== "none" && normalizedDriveMedia && isAcceptedIcLoraDriveMedia(
      normalizedDriveMedia.data,
      normalizedDriveMedia.fileName,
      driveMediaKinds
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
    const identityImagesSupported = supports("characterRefineIdentityReferences") && clip.stages.some(
      (stage) => stage.characterRefine?.verifyIdentity === true
    );
    unsupportedFeature(
      !supports("clipReferences") && clip.references.some(
        (reference) => !identityImagesSupported || reference.kind !== "image"
      ),
      "clipReferences"
    );
    const characterRefineConfigured = clip.stages.some(
      (stage) => stage.characterRefine !== null
    );
    unsupportedFeature(
      !supports("characterRefine") && characterRefineConfigured,
      "characterRefine"
    );
    unsupportedFeature(
      supports("characterRefine") && !supports("characterRefineAppearanceReferences") && clip.stages.some(
        (stage) => (stage.characterRefine?.pictureReferenceIds.length ?? 0) > 0
      ),
      "characterRefineAppearanceReferences"
    );
    unsupportedFeature(
      supports("characterRefine") && !supports("characterRefineIdentityReferences") && clip.stages.some(
        (stage) => stage.characterRefine?.verifyIdentity === true
      ),
      "characterRefineIdentityReferences"
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
        const supported = descriptor !== void 0 && resolvedModel !== void 0 && resolvedModel.entryModes.length > 0;
        return {
          supported,
          reason: supported ? "" : `${feature === "sampler" ? "Sampler" : "Scheduler"} selection requires a resolved generating video model.`,
          code: ""
        };
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
    {
      id: "3:4",
      label: "3:4",
      reference: { width: 448, height: 576 }
    },
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

  // frontend/loraFolderFilter.ts
  var ROOT_LORA_FOLDER = "";
  var topLevelPathSegment = (value, rootWhenUnnested) => {
    const normalized = `${value}`.trim().replaceAll("\\", "/");
    const separator = normalized.indexOf("/");
    if (separator < 0) {
      return rootWhenUnnested ? ROOT_LORA_FOLDER : normalized;
    }
    return normalized.slice(0, separator);
  };
  var topLevelLoraFolder = (modelName) => topLevelPathSegment(modelName, true);
  var isLoraNoneOption = (value) => `${value}`.replace(/\s+/g, "").toLowerCase() === "(none)";
  var availableLoraFolders = (modelNames) => {
    const folders = /* @__PURE__ */ new Set();
    for (const modelName of modelNames) {
      if (!isLoraNoneOption(modelName)) {
        folders.add(topLevelLoraFolder(modelName));
      }
    }
    return [...folders].sort((left, right) => {
      if (left === ROOT_LORA_FOLDER) return -1;
      if (right === ROOT_LORA_FOLDER) return 1;
      return left.localeCompare(right);
    });
  };
  var normalizeLoraFolders = (value) => {
    if (!Array.isArray(value)) {
      return null;
    }
    const folders = [];
    for (const raw of value) {
      if (typeof raw !== "string") {
        continue;
      }
      const folder = topLevelPathSegment(raw, false);
      if (!folders.includes(folder)) {
        folders.push(folder);
      }
    }
    return folders;
  };
  var isLoraFolderIncluded = (modelName, includedFolders) => includedFolders === null || includedFolders.has(topLevelLoraFolder(modelName));

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
  var getRootGeneratedEntryMode = (modelCatalog) => {
    if (!`${getRootModelInput()?.value ?? ""}`.trim()) {
      return DEFAULT_GENERATED_ENTRY_MODE;
    }
    return isRootTextToVideoModel(modelCatalog) ? "text-to-video" : "image-to-video";
  };
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

  // frontend/timelineAuthoringSettings.ts
  var SETTINGS_KEY = "videostages.timeline.authoringSettings";
  var TIMELINE_AUTHORING_SETTINGS_CHANGED = "videostages:timeline-authoring-settings-changed";
  var DEFAULT_SETTINGS = {
    snap: true,
    autoCollapse: true,
    dimensionSnap: "disabled",
    loraFolders: null
  };
  var dimensionSnapSetting = (value) => value === 32 || value === 64 ? value : "disabled";
  var getTimelineAuthoringSettings = () => {
    try {
      const raw = localStorage.getItem(SETTINGS_KEY);
      if (!raw) {
        return { ...DEFAULT_SETTINGS };
      }
      const parsed = JSON.parse(raw);
      return {
        snap: typeof parsed.snap === "boolean" ? parsed.snap : DEFAULT_SETTINGS.snap,
        autoCollapse: typeof parsed.autoCollapse === "boolean" ? parsed.autoCollapse : DEFAULT_SETTINGS.autoCollapse,
        dimensionSnap: dimensionSnapSetting(parsed.dimensionSnap),
        loraFolders: normalizeLoraFolders(parsed.loraFolders)
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
    const savedLoraFolders = getTimelineAuthoringSettings().loraFolders;
    const includedLoraFolders = savedLoraFolders === null ? null : new Set(savedLoraFolders);
    const loras = { values: [], labels: [] };
    rawLoras.values.forEach((value, i) => {
      if (!isLoraNoneOption(value) && isLoraFolderIncluded(value, includedLoraFolders)) {
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

  // frontend/generatedMiniMaxTextEncoder.ts
  var H3_TEXT_ENCODER_FEATURE = "clipproj";
  var H3_TEXT_ENCODERS = ["default", "8b", "4b"];
  var H3_TEXT_ENCODER_DEFAULT = "default";

  // frontend/h3TextEncoder.ts
  var normalizeH3TextEncoder = (value) => H3_TEXT_ENCODERS.includes(value) ? value : H3_TEXT_ENCODER_DEFAULT;

  // frontend/loraAuthoring.ts
  var LORA_WEIGHT_DEFAULT = 1;
  var LORA_WEIGHT_STEP = 0.05;
  var defaultLoraWeight = (defaults, modelName) => {
    const index = defaults.loraValues.indexOf(modelName);
    const value = index >= 0 ? defaults.loraDefaultWeights[index] : null;
    return typeof value === "number" && Number.isFinite(value) ? value : LORA_WEIGHT_DEFAULT;
  };

  // frontend/generatedCharacterRefine.ts
  var CHARACTER_REFINE_INSTALL_FEATURE = "bonita";
  var CHARACTER_REFINE_REQUIRED_FEATURES = [
    "bonita.sam3-finder",
    "bonita.yolo-finder",
    "bonita.find-and-crop",
    "bonita.apply-repair"
  ];
  var CHARACTER_REFINE_REPOSITORY_URL = "https://github.com/jtreminio/ComfyUI-Bonita";
  var CHARACTER_REFINE_FINDER_DEFAULT = "sam3";
  var CHARACTER_REFINE_SUBJECT_DEFAULT = "person";
  var CHARACTER_REFINE_PART_DEFAULT = "face";
  var CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT = 0.25;
  var CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT = 0.1;
  var CHARACTER_REFINE_DETECTION_THRESHOLD_MIN = 0;
  var CHARACTER_REFINE_DETECTION_THRESHOLD_MAX = 1;
  var CHARACTER_REFINE_DETECTION_THRESHOLD_STEP = 0.05;
  var CHARACTER_REFINE_CROP_WIDTH_DEFAULT = 512;
  var CHARACTER_REFINE_CROP_HEIGHT_DEFAULT = 512;
  var CHARACTER_REFINE_CROP_DIMENSION_MIN = 64;
  var CHARACTER_REFINE_CROP_DIMENSION_MAX = 4096;
  var CHARACTER_REFINE_CROP_DIMENSION_STEP = 32;

  // frontend/h3AttentionWindow.ts
  var H3_ATTENTION_WINDOW_FEATURE = "h3_window_attention";
  var H3_ATTENTION_WINDOW_MIN_SECONDS = 0;
  var H3_ATTENTION_WINDOW_MAX_SECONDS = 20;
  var H3_ATTENTION_WINDOW_STEP_SECONDS = 0.5;
  var normalizeH3AttentionWindowSeconds = (value) => {
    const numeric = Number(value);
    if (!Number.isFinite(numeric)) {
      return H3_ATTENTION_WINDOW_MIN_SECONDS;
    }
    return clamp(
      Math.round(numeric / H3_ATTENTION_WINDOW_STEP_SECONDS) * H3_ATTENTION_WINDOW_STEP_SECONDS,
      H3_ATTENTION_WINDOW_MIN_SECONDS,
      H3_ATTENTION_WINDOW_MAX_SECONDS
    );
  };

  // frontend/normalizationStage.ts
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.find((value) => value === "pixel-lanczos") ?? upscaleMethodValues[0] ?? "";
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
  var buildDefaultStage = (defaults, defaultStageModel, previousStage, refCount, initialIcLoraStrengths = []) => {
    const model = previousStage ? previousStage.model : defaultStageModel;
    return {
      h3AttentionWindowSeconds: previousStage?.h3AttentionWindowSeconds ?? 0,
      modelAttentionBackend: previousStage?.modelAttentionBackend,
      modelAttentionBackendEnabled: previousStage?.modelAttentionBackendEnabled,
      useSparseAttention: previousStage?.useSparseAttention,
      useSparseAttentionEnabled: previousStage?.useSparseAttentionEnabled,
      loras: [],
      skipped: false,
      control: previousStage ? previousStage.control : defaults.control,
      controlNetStrength: previousStage ? previousStage.controlNetStrength : STAGE_CONTROLNET_STRENGTH_DEFAULT,
      icLoraStrengths: previousStage ? [...previousStage.icLoraStrengths] : initialIcLoraStrengths.map(normalizeStageControlNetStrengthValue),
      frameRefStrengths: buildDefaultStageRefStrengths(refCount),
      upscale: previousStage ? previousStage.upscale : defaults.upscale,
      upscaleMethod: previousStage ? previousStage.upscaleMethod : resolveRootPreferredUpscaleMethod(defaults.upscaleMethodValues),
      model,
      modelProfileId: previousStage?.modelProfileId ?? modelProfileForModel(defaults.modelCatalog, model) ?? "unsupported",
      steps: previousStage ? previousStage.steps : defaults.steps,
      cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
      sampler: previousStage ? previousStage.sampler : defaults.samplerValues[0] ?? "euler",
      scheduler: previousStage ? previousStage.scheduler : defaults.schedulerValues[0] ?? "normal",
      characterRefine: null
    };
  };
  var defaultCharacterRefine = (pictureReferenceIds) => ({
    pictureReferenceIds: [...pictureReferenceIds],
    verifyIdentity: pictureReferenceIds.length > 0,
    finder: CHARACTER_REFINE_FINDER_DEFAULT,
    subject: CHARACTER_REFINE_SUBJECT_DEFAULT,
    part: CHARACTER_REFINE_PART_DEFAULT,
    subjectConfidence: CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT,
    partConfidence: CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT,
    cropWidth: CHARACTER_REFINE_CROP_WIDTH_DEFAULT,
    cropHeight: CHARACTER_REFINE_CROP_HEIGHT_DEFAULT
  });
  var referenceIds = (value) => Array.isArray(value) ? [...new Set(value.map(trimmedText).filter(Boolean))] : [];
  var normalizeCharacterRefine = (value) => {
    if (!isRecord(value)) return null;
    const legacyIdentity = trimmedText(value.identityReferenceId);
    const pictures = Array.isArray(value.pictureReferenceIds) ? referenceIds(value.pictureReferenceIds) : referenceIds([legacyIdentity, ...referenceIds(value.referenceIds)]);
    return {
      pictureReferenceIds: pictures,
      verifyIdentity: typeof value.verifyIdentity === "boolean" ? value.verifyIdentity : Boolean(legacyIdentity),
      finder: value.finder === "yolo" ? "yolo" : CHARACTER_REFINE_FINDER_DEFAULT,
      subject: trimmedText(value.subject) || trimmedText(value.coarseTarget) || CHARACTER_REFINE_SUBJECT_DEFAULT,
      part: trimmedText(value.part) || trimmedText(value.fineTarget) || CHARACTER_REFINE_PART_DEFAULT,
      subjectConfidence: clampedNumber(
        value.subjectConfidence ?? value.trackDetectionThreshold,
        CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MAX
      ),
      partConfidence: clampedNumber(
        value.partConfidence ?? value.targetDetectionThreshold,
        CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MAX
      ),
      cropWidth: snapValueToStep(
        value.cropWidth,
        CHARACTER_REFINE_CROP_WIDTH_DEFAULT,
        CHARACTER_REFINE_CROP_DIMENSION_MIN,
        CHARACTER_REFINE_CROP_DIMENSION_MAX,
        CHARACTER_REFINE_CROP_DIMENSION_STEP
      ),
      cropHeight: snapValueToStep(
        value.cropHeight,
        CHARACTER_REFINE_CROP_HEIGHT_DEFAULT,
        CHARACTER_REFINE_CROP_DIMENSION_MIN,
        CHARACTER_REFINE_CROP_DIMENSION_MAX,
        CHARACTER_REFINE_CROP_DIMENSION_STEP
      )
    };
  };
  var buildDefaultRef = (source = MEDIA_SOURCE_REFINER) => ({
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false
  });
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
  var normalizeStage = (defaults, defaultStageModel, rawStage, previousStage, refCount, stageIndexInClip, initVideoClip = false) => {
    const fallback = buildDefaultStage(
      defaults,
      defaultStageModel,
      previousStage,
      refCount
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
    const stage = {
      h3AttentionWindowSeconds: normalizeH3AttentionWindowSeconds(
        rawStage.h3AttentionWindowSeconds
      ),
      modelAttentionBackend: readRawStageString(
        rawStage,
        "modelAttentionBackend"
      ),
      modelAttentionBackendEnabled: typeof rawStage.modelAttentionBackendEnabled === "boolean" ? rawStage.modelAttentionBackendEnabled : void 0,
      useSparseAttention: readRawStageString(rawStage, "useSparseAttention"),
      useSparseAttentionEnabled: typeof rawStage.useSparseAttentionEnabled === "boolean" ? rawStage.useSparseAttentionEnabled : void 0,
      loras: normalizeStageLoras(rawStage.loras),
      id: normalizeOptionalEntityId(rawStage.id),
      skipped: !!rawStage.skipped,
      control,
      controlNetStrength: normalizeStageControlNetStrengthValue(
        readRawStageProp(rawStage, "controlNetStrength") ?? fallback.controlNetStrength
      ),
      icLoraStrengths: Array.isArray(rawStage.icLoraStrengths) ? rawStage.icLoraStrengths.map(
        normalizeStageControlNetStrengthValue
      ) : [...fallback.icLoraStrengths],
      frameRefStrengths: normalizeStageRefStrengths(
        rawStage.keyframeStrengths,
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
      scheduler: textOr(rawStage.scheduler, fallback.scheduler),
      characterRefine: normalizeCharacterRefine(rawStage.characterRefine)
    };
    if (stage.characterRefine) {
      stage.upscale = 1;
    }
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
    const firstStage = {
      ...buildDefaultStage(
        defaults,
        defaultStageModel,
        previousClip?.stages[0] ?? null,
        frameRefs.length
      ),
      loras: previousClip?.stages[0]?.loras.map((entry) => ({ ...entry })) ?? [],
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
      useSeedVr: false,
      audioOnly: false,
      refFraming: "crop",
      h3TextEncoder: previousClip?.h3TextEncoder ?? "default",
      audioSource: previousClip?.audioOnly === true ? AUDIO_SOURCE_PREVIOUS_CLIP : AUDIO_SOURCE_NATIVE,
      icLoras: [],
      saveAudioTrack: false,
      clipLengthFromAudio: false,
      clipLengthFromControlNet: false,
      reuseAudio: false,
      uploadedAudio: null,
      uploadedAudioDurationSeconds: 0,
      uploadedAudioStartSeconds: 0,
      uploadedAudioLengthSeconds: 0,
      prompt: "",
      promptWindows: [],
      retake: null,
      initVideo: null,
      references: [],
      frameRefs,
      stages: [firstStage]
    };
  };
  var applyAudioOnlyLeadBase = (clips) => {
    if (clips[0]?.audioOnly === true && clips[1]) {
      clips[1].audioSource = AUDIO_SOURCE_PREVIOUS_CLIP;
    }
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
    const refsRaw = Array.isArray(rawClip.keyframes) ? rawClip.keyframes : [];
    const stages = [];
    for (let i = 0; i < stagesRaw.length; i++) {
      const previousStage = i > 0 ? stages[i - 1] : null;
      stages.push(
        normalizeStage(
          defaults,
          defaultStageModel,
          {
            ...isRecord(stagesRaw[i]) ? stagesRaw[i] : {},
            loras: isRecord(stagesRaw[i]) && Array.isArray(stagesRaw[i].loras) ? stagesRaw[i].loras : rawClip.loras
          },
          previousStage,
          refsRaw.length,
          i,
          initVideo !== null
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
      (rawRef) => normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax)
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
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = architectureDescriptor(
      defaults.modelCatalog,
      resolvedArchitecture
    )?.boundaryRules[boundaryOut];
    const uploadedAudio = normalizeUploadedMedia(rawClip.uploadedAudio);
    const uploadedAudioDurationSeconds = uploadedAudio ? roundToTenth(nonNegativeNumber(rawClip.uploadedAudioDurationSeconds)) : 0;
    const uploadedAudioRange = uploadedAudio ? normalizeTimedMediaRange(
      rawClip.uploadedAudioStartSeconds,
      rawClip.uploadedAudioLengthSeconds,
      uploadedAudioDurationSeconds
    ) : { startSeconds: 0, lengthSeconds: 0 };
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
      useSeedVr: !!rawClip.useSeedVr,
      audioOnly: !!rawClip.audioOnly,
      refFraming: normalizeReferenceFraming(rawClip.refFraming),
      h3TextEncoder: normalizeH3TextEncoder(rawClip.h3TextEncoder),
      audioSource,
      icLoras,
      saveAudioTrack: !!rawClip.saveAudioTrack,
      clipLengthFromAudio,
      clipLengthFromControlNet,
      reuseAudio: !!rawClip.reuseAudio,
      uploadedAudio,
      uploadedAudioDurationSeconds,
      uploadedAudioStartSeconds: uploadedAudioRange.startSeconds,
      uploadedAudioLengthSeconds: uploadedAudioRange.lengthSeconds,
      prompt: text(rawClip.prompt),
      promptWindows: normalizePromptWindows(rawClip),
      retake,
      initVideo,
      references,
      frameRefs,
      stages
    };
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
  var discardUnsupportedFeatures = (clip, capabilities) => {
    const supports = (feature) => architectureFeatureSupport(feature, capabilities);
    if (!supports("frameReferences")) {
      clip.frameRefs = [];
    }
    if (!supports("promptRelay")) {
      clip.promptWindows = [];
    }
    if (!supports("retake")) {
      clip.retake = null;
    }
    if (!supports("referenceFraming")) {
      clip.refFraming = "crop";
    }
    if (!supports("icLora")) {
      clip.icLoras = [];
      clip.clipLengthFromControlNet = false;
    }
    if (!supports("audioReuse")) {
      clip.reuseAudio = false;
    }
    if (!supports("audioDerivedDuration")) {
      clip.clipLengthFromAudio = false;
    }
    if (!supports("audioBoundaryCarry")) {
      clip.boundaryOutCarryAudio = false;
    }
    if (!isAllowedAudioSource(capabilities.audioSourceKinds, clip.audioSource)) {
      clip.audioSource = defaultAuthoringAudioSource(
        capabilities.audioSourceKinds
      );
      clip.uploadedAudio = null;
      clip.uploadedAudioDurationSeconds = 0;
      clip.uploadedAudioStartSeconds = 0;
      clip.uploadedAudioLengthSeconds = 0;
      clip.saveAudioTrack = false;
    }
    if (!capabilities.entryModes.includes("init-video")) {
      clip.initVideo = null;
    }
    const keepCharacterReferences = supports("characterRefineAppearanceReferences") || supports("characterRefineIdentityReferences");
    if (!supports("clipReferences") && !keepCharacterReferences) {
      clip.references = [];
    }
    for (const stage of clip.stages) {
      if (!supports("frameReferences") || !supports("stageReferenceStrengths")) {
        stage.frameRefStrengths = [];
      }
      if (!supports("icLora")) {
        stage.icLoraStrengths = [];
      }
      if (!supports("characterRefine")) {
        stage.characterRefine = null;
      } else if (stage.characterRefine) {
        if (!supports("characterRefineAppearanceReferences")) {
          stage.characterRefine.pictureReferenceIds = [];
        }
        if (!supports("characterRefineIdentityReferences")) {
          stage.characterRefine.verifyIdentity = false;
        }
      }
      const upscaleMode = upscaleModeForMethod(stage.upscaleMethod);
      if (upscaleMode === "unsupported" || upscaleMode === "latent" && !supports("latentUpscale") || upscaleMode === "latent-model" && !supports("latentModelUpscale")) {
        stage.upscale = 1;
      }
    }
  };
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
    const capabilities = modelCatalogEntry(catalog, target.model)?.capabilities ?? architectureDescriptor(catalog, target.architectureId)?.capabilities;
    if (!capabilities) {
      return null;
    }
    discardUnsupportedFeatures(clip, capabilities);
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
      "useSeedVr",
      "audioOnly",
      "refFraming",
      "h3TextEncoder",
      "audioSource",
      "icLoras",
      "saveAudioTrack",
      "clipLengthFromAudio",
      "clipLengthFromControlNet",
      "reuseAudio",
      "uploadedAudio",
      "uploadedAudioDurationSeconds",
      "uploadedAudioStartSeconds",
      "uploadedAudioLengthSeconds",
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
      "h3AttentionWindowSeconds",
      "modelAttentionBackend",
      "modelAttentionBackendEnabled",
      "useSparseAttention",
      "useSparseAttentionEnabled",
      "loras",
      "skipped",
      "control",
      "controlNetStrength",
      "icLoraStrengths",
      "frameRefStrengths",
      "upscale",
      "upscaleMethod",
      "steps",
      "cfgScale",
      "sampler",
      "scheduler",
      "characterRefine"
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
      "mediaScale",
      "startSeconds",
      "lengthSeconds"
    ],
    reservedKeys: ["id"],
    reconcilesCharacterRefineReferences: true,
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
    patchKeys: ["source", "volume", "disabled"],
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

  // frontend/types.ts
  var CURRENT_AUTHORING_SCHEMA_VERSION = 11;

  // frontend/identity.ts
  var fallbackSequence = 0;
  var normalizedExistingId = (value) => {
    if (typeof value !== "string") {
      return null;
    }
    const id = value.trim();
    return id.length > 0 && !id.includes("_legacy_") ? id : null;
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
    const base = `${entry.kind}_${entry.repairPath}`;
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
          kind: "keyframe",
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
          context.generatedEntryMode ?? DEFAULT_GENERATED_ENTRY_MODE,
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
  var buildClipReferenceSourceOptions = (kind, currentValue = "", allowPreviousClip = false) => {
    if (kind === "image") {
      return buildImageSourceOptions(currentValue, true);
    }
    const options = [
      { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
      ...kind === "video" && allowPreviousClip ? [
        {
          value: MEDIA_SOURCE_PREVIOUS_CLIP,
          label: "Previous Clip"
        }
      ] : [],
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
    if (kind === "video" && value === MEDIA_SOURCE_PREVIOUS_CLIP) {
      return true;
    }
    return kind === "image" && (value === MEDIA_SOURCE_BASE || value === MEDIA_SOURCE_REFINER || parseBase2EditStageIndex(value) !== null);
  };
  var clipReferenceIsUsable = (reference) => clipReferenceSourceSupportsKind(reference.kind, reference.source) && (reference.source.trim() !== MEDIA_SOURCE_UPLOAD || Boolean(reference.uploadedMedia?.data.trim()));

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
    const affectedId = operation === "add" ? fields[descriptor.entityField].id : id;
    const wasUsableImage = descriptor.reconcilesCharacterRefineReferences && owner.references.some(
      (reference) => reference.id === affectedId && reference.kind === "image" && clipReferenceIsUsable(reference)
    );
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
    if (descriptor.reconcilesCharacterRefineReferences) {
      const supportsAppearanceReferences = context.architectureCatalog !== null && createCapabilityViewResolver(context.architectureCatalog).forClip(owner).decision("characterRefineAppearanceReferences").supported;
      const reference = owner.references.find(
        (candidate) => candidate.id === affectedId
      );
      const usableImage = operation !== "remove" && reference !== void 0 && reference.kind === "image" && clipReferenceIsUsable(reference);
      for (const stage of owner.stages) {
        if (!stage.characterRefine) continue;
        if (supportsAppearanceReferences && usableImage && !wasUsableImage) {
          if (!stage.characterRefine.pictureReferenceIds.includes(
            affectedId
          )) {
            stage.characterRefine.pictureReferenceIds.push(affectedId);
          }
        } else if (!usableImage) {
          stage.characterRefine.pictureReferenceIds = stage.characterRefine.pictureReferenceIds.filter(
            (referenceId) => referenceId !== affectedId
          );
        }
        const hasUsableImage = owner.references.some(
          (candidate) => candidate.kind === "image" && clipReferenceIsUsable(candidate)
        );
        if (!hasUsableImage) {
          stage.characterRefine.verifyIdentity = false;
        }
      }
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
          context.generatedEntryMode ?? DEFAULT_GENERATED_ENTRY_MODE,
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
          context.generatedEntryMode ?? DEFAULT_GENERATED_ENTRY_MODE,
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
          context.generatedEntryMode ?? DEFAULT_GENERATED_ENTRY_MODE,
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
            uploadedAudio: normalizeUploadedMedia(source.uploadedAudio),
            mediaDurationSeconds: optionalPositiveNumber(source.mediaDurationSeconds) ?? 0
          },
          spans: Array.isArray(rawSpans) ? rawSpans.map(normalizeAudioTrackSpan).filter(
            (span) => span !== null
          ) : [],
          ...rawTrack.disabled === true ? { disabled: true } : {},
          ...volume === void 0 ? {} : { volume }
        })
      );
    }
    return tracks;
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
      // The timeline follows core's Video FPS param.
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
        useSeedVr: clip.useSeedVr,
        audioOnly: clip.audioOnly,
        refFraming: clip.refFraming,
        h3TextEncoder: clip.h3TextEncoder,
        audioSource: clip.audioSource,
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
        uploadedAudioDurationSeconds: clip.uploadedAudioDurationSeconds,
        uploadedAudioStartSeconds: clip.uploadedAudioStartSeconds,
        uploadedAudioLengthSeconds: clip.uploadedAudioLengthSeconds,
        initVideo: clip.initVideo ? {
          source: clip.initVideo.source ?? MEDIA_SOURCE_UPLOAD,
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
          mediaScale: reference.mediaScale,
          startSeconds: reference.startSeconds,
          lengthSeconds: reference.lengthSeconds
        })),
        keyframes: clip.frameRefs.map((ref) => ({
          id: ref.id,
          source: ref.source,
          uploadFileName: ref.uploadFileName,
          uploadedImage: ref.uploadedImage,
          frame: ref.frame,
          fromEnd: ref.fromEnd
        })),
        stages: clip.stages.map((stage) => ({
          loras: stage.loras.map((entry) => ({
            name: entry.name,
            weight: entry.weight
          })),
          id: stage.id,
          skipped: stage.skipped,
          control: stage.control,
          controlNetStrength: stage.controlNetStrength,
          icLoraStrengths: stage.icLoraStrengths,
          keyframeStrengths: stage.frameRefStrengths,
          upscale: stage.upscale,
          upscaleMethod: stage.upscaleMethod,
          model: stage.model,
          modelProfileId: stage.modelProfileId,
          steps: stage.steps,
          cfgScale: stage.cfgScale,
          sampler: stage.sampler,
          scheduler: stage.scheduler,
          h3AttentionWindowSeconds: stage.h3AttentionWindowSeconds,
          modelAttentionBackend: stage.modelAttentionBackend,
          modelAttentionBackendEnabled: stage.modelAttentionBackendEnabled,
          useSparseAttention: stage.useSparseAttention,
          useSparseAttentionEnabled: stage.useSparseAttentionEnabled,
          ...stage.characterRefine ? { characterRefine: stage.characterRefine } : {}
        }))
      })
    );
  };
  var timelinePointProjection = (clips, seconds2, edge) => {
    if (!Number.isFinite(seconds2) || seconds2 < 0 || clips.length === 0) {
      return null;
    }
    let cursor = 0;
    for (let index = 0; index < clips.length; index++) {
      const clip = clips[index];
      const duration = Math.max(0, clip.duration || 0);
      const clipEnd = cursor + duration;
      const isLast = index === clips.length - 1;
      const ownsPoint = edge === "start" ? seconds2 < clipEnd || isLast : seconds2 <= clipEnd || isLast;
      if (ownsPoint) {
        return {
          clipId: clip.id,
          offsetSeconds: Math.max(
            0,
            Math.min(duration, seconds2 - cursor)
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
      disabled: track.disabled === true,
      ...track.volume === void 0 ? {} : { volume: track.volume },
      source: {
        kind: track.source.kind,
        reference: track.source.reference,
        uploadedAudio: track.source.uploadedAudio,
        mediaDurationSeconds: track.source.mediaDurationSeconds
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
    return data.startsWith("data:") || data.startsWith("blob:") || isBrowserMediaHandle(data);
  };
  var serializeStateForDurableStorage = (state) => {
    ensureAuthoringDocumentIdentity(state);
    const durable = structuredClone(state);
    for (const clip of durable.clips) {
      if (isTransientBrowserMedia(clip.uploadedAudio)) {
        clip.uploadedAudioDurationSeconds = 0;
        clip.uploadedAudioStartSeconds = 0;
        clip.uploadedAudioLengthSeconds = 0;
      }
    }
    visitAuthoringMedia(durable, (media, replace) => {
      if (media.source !== MEDIA_SOURCE_PREVIOUS_CLIP && isTransientBrowserMedia(media)) {
        replace(null);
      }
    });
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
      if (!hasArrayOfRecords(clip, "stages") || !hasArrayOfRecords(clip, "keyframes") || !hasArrayOfRecords(clip, "references") || !hasArrayOfRecords(clip, "icLoras") || Object.hasOwn(clip, "loras") && !hasArrayOfRecords(clip, "loras")) {
        return false;
      }
      const stages = Array.isArray(clip.stages) ? clip.stages : [];
      const loras = Array.isArray(clip.loras) ? clip.loras : [];
      if (loras.some(
        (lora) => !isRecord(lora) || typeof lora.weight !== "number" || !Number.isFinite(lora.weight)
      )) {
        return false;
      }
      for (const stage of stages) {
        if (Object.hasOwn(stage, "loras") && (!hasArrayOfRecords(stage, "loras") || !stage.loras.every(
          (lora) => typeof lora.weight === "number" && Number.isFinite(lora.weight)
        )) || Object.hasOwn(stage, "loraWeights") || Object.hasOwn(stage, "icLoraStrengths") && (!Array.isArray(stage.icLoraStrengths) || !stage.icLoraStrengths.every(
          (strength) => typeof strength === "number" && Number.isFinite(strength)
        )) || Object.hasOwn(stage, "keyframeStrengths") && (!Array.isArray(stage.keyframeStrengths) || !stage.keyframeStrengths.every(
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
  var DIVERGENT_PROJECTION_NOTICE = "VideoStages: the saved timeline has audio spans whose clip anchors disagree with their timeline seconds. The seconds were used and the anchors will be rewritten on the next save — re-check those spans.";
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
  var FRAME_REFS_SCHEMA_VERSION = 7;
  var STAGE_LORA_SCHEMA_VERSION = 8;
  var CLIP_LORA_SCHEMA_VERSION = 9;
  var CLIP_ATTENTION_WINDOW_SCHEMA_VERSION = 10;
  var renameKey = (target, oldKey, newKey) => {
    if (!(oldKey in target)) {
      return;
    }
    if (!(newKey in target)) {
      target[newKey] = target[oldKey];
    }
    delete target[oldKey];
  };
  var finiteNumberArray = (value) => Array.isArray(value) && value.every((entry) => typeof entry === "number" && Number.isFinite(entry));
  var migrateStageLorasToClip = (document2) => {
    if (!Array.isArray(document2.clips)) {
      return null;
    }
    for (const rawClip of document2.clips) {
      if (!isRecord(rawClip)) {
        continue;
      }
      if (!hasArrayOfRecords(rawClip, "loras")) {
        return null;
      }
      const clipLoras = Array.isArray(rawClip.loras) ? rawClip.loras : [];
      const stages = Array.isArray(rawClip.stages) ? rawClip.stages : [];
      const firstStage = isRecord(stages[0]) ? stages[0] : null;
      const firstStageWeights = firstStage?.loraWeights;
      if (firstStage && Object.hasOwn(firstStage, "loraWeights") && !finiteNumberArray(firstStageWeights)) {
        return null;
      }
      const names = /* @__PURE__ */ new Set();
      clipLoras.forEach((entry, index) => {
        if (!isRecord(entry)) {
          return;
        }
        const name = typeof entry.name === "string" ? entry.name.trim() : "";
        if (name) {
          names.add(name);
        }
        const weight = entry.weight;
        entry.weight = typeof weight === "number" && Number.isFinite(weight) ? weight : (finiteNumberArray(firstStageWeights) ? firstStageWeights[index] : void 0) ?? 1;
      });
      for (const rawStage of stages) {
        if (!isRecord(rawStage)) {
          continue;
        }
        if (Object.hasOwn(rawStage, "loras") && !hasArrayOfRecords(rawStage, "loras") || Object.hasOwn(rawStage, "loraWeights") && !finiteNumberArray(rawStage.loraWeights)) {
          return null;
        }
        const stageLoras = Array.isArray(rawStage.loras) ? rawStage.loras : [];
        for (const entry of stageLoras) {
          if (!isRecord(entry)) {
            continue;
          }
          const name = typeof entry.name === "string" ? entry.name.trim() : "";
          if (!name || names.has(name)) {
            continue;
          }
          const weight = entry.weight;
          clipLoras.push({
            name,
            weight: typeof weight === "number" && Number.isFinite(weight) ? weight : 1
          });
          names.add(name);
        }
        delete rawStage.loras;
        delete rawStage.loraWeights;
      }
      rawClip.loras = clipLoras;
    }
    document2.schemaVersion = CLIP_LORA_SCHEMA_VERSION;
    return document2;
  };
  var migrateStoredDocument = (parsed) => {
    if (parsed.schemaVersion === CURRENT_AUTHORING_SCHEMA_VERSION) {
      return parsed;
    }
    if (parsed.schemaVersion !== FRAME_REFS_SCHEMA_VERSION && parsed.schemaVersion !== STAGE_LORA_SCHEMA_VERSION && parsed.schemaVersion !== CLIP_LORA_SCHEMA_VERSION && parsed.schemaVersion !== CLIP_ATTENTION_WINDOW_SCHEMA_VERSION) {
      return null;
    }
    const migrated = structuredClone(parsed);
    if (migrated.schemaVersion === FRAME_REFS_SCHEMA_VERSION) {
      if (!Array.isArray(migrated.clips)) {
        return null;
      }
      for (const rawClip of migrated.clips) {
        if (!isRecord(rawClip)) {
          continue;
        }
        renameKey(rawClip, "frameRefs", "keyframes");
        if (!Array.isArray(rawClip.stages)) {
          continue;
        }
        for (const rawStage of rawClip.stages) {
          if (isRecord(rawStage)) {
            renameKey(
              rawStage,
              "frameRefStrengths",
              "keyframeStrengths"
            );
          }
        }
      }
      migrated.schemaVersion = STAGE_LORA_SCHEMA_VERSION;
    }
    if (migrated.schemaVersion === STAGE_LORA_SCHEMA_VERSION && !migrateStageLorasToClip(migrated))
      return null;
    if (!hasValidStoredCollections(migrated)) return null;
    for (const clip of migrated.clips) {
      for (const stage of Array.isArray(clip.stages) ? clip.stages : []) {
        if (migrated.schemaVersion === CLIP_LORA_SCHEMA_VERSION)
          stage.loras = structuredClone(clip.loras ?? []);
        stage.h3AttentionWindowSeconds = clip.h3AttentionWindowSeconds ?? 0;
      }
      delete clip.loras;
      delete clip.h3AttentionWindowSeconds;
    }
    migrated.schemaVersion = CURRENT_AUTHORING_SCHEMA_VERSION;
    return migrated;
  };
  var decodeStoredDocument = (serialized, inherited, defaults, defaultStageModel) => {
    try {
      const parsed = JSON.parse(serialized);
      if (!isRecord(parsed)) {
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
      applyAudioOnlyLeadBase(clips);
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
    if (!isRecord(value) || typeof value.id !== "string" || value.id.length === 0 || value.id.trim() !== value.id || seen.has(value.id) || value.id.includes("_legacy_")) {
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
        for (const key of ["stages", "keyframes", "references"]) {
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
  var hydratedSnapshot = null;
  var hydratedPromptCarrierValue = null;
  var pendingHydratedPrompts = null;
  var protectOverriddenBootCarrier = false;
  var bootCarrierProtectionDeadline = 0;
  var externalizedCarrierValue = null;
  var withoutFrontendAudioTracks = (serialized) => {
    try {
      const parsed = JSON.parse(serialized);
      if (typeof parsed !== "object" || parsed === null) {
        return serialized;
      }
      const record = parsed;
      const audioTracks = record.audioTracks;
      if (!Array.isArray(audioTracks) || audioTracks.length === 0) {
        return serialized;
      }
      record.audioTracks = [];
      return JSON.stringify(parsed);
    } catch {
      return serialized;
    }
  };
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
  var externalizeHostDataParam = () => {
    const serialized = readDataParam();
    if (!serialized) {
      return serialized;
    }
    if (serialized === externalizedCarrierValue) {
      return serialized;
    }
    const externalized = externalizeSerializedBrowserMedia(serialized);
    const frontendState = withoutFrontendAudioTracks(externalized.serialized);
    if (frontendState !== serialized) {
      writeDataParam(frontendState);
    }
    externalizedCarrierValue = frontendState;
    return frontendState;
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
    const existing = externalizeHostDataParam();
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
    return externalizeHostDataParam();
  };
  var writeQuiet = (state, serialized) => {
    releaseBootCarrierProtection();
    ensureAuthoringDocumentIdentity(state);
    assignMissingHues(state.clips);
    externalizedCarrierValue = serialized;
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
  var serializeCarrierState = (state) => externalizeSerializedBrowserMedia(serializeStateForStorage(state)).serialized;
  var timelineCarrierAdapter = {
    readToken: () => {
      ensureHydratedCarrier();
      externalizeHostDataParam();
      return `${readStateToken()}\0${readInheritedDimsSignature()}`;
    },
    readDataParam: readDataParam2,
    parse,
    parseEmpty,
    serialize: serializeCarrierState,
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
    requested.audioTracks = [];
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

  // frontend/selection.ts
  var NO_SELECTION = { selection: { kind: "none" } };
  var anchor = NO_SELECTION;
  var selectionSubscribers = /* @__PURE__ */ new Set();
  var getSelection = () => resolveSelection(anchor);
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
  var subscribeSelection = (cb) => {
    selectionSubscribers.add(cb);
    return () => {
      selectionSubscribers.delete(cb);
    };
  };

  // frontend/clipCreation.ts
  var addClipInFlight = false;
  var addClipAfterCatalog = async () => {
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
      const state = getState();
      const clips = state.clips;
      const previousClip = clips[clips.length - 1] ?? null;
      if (previousClip && clips.length >= 2) {
        const previousJoin = clips[clips.length - 2];
        previousClip.boundaryOut = previousJoin.boundaryOut;
        previousClip.boundaryOutCarryAudio = previousJoin.boundaryOutCarryAudio;
        previousClip.boundaryOutReferenceScale = previousJoin.boundaryOutReferenceScale;
        previousClip.boundaryOutReferenceIncludeSoundtrack = previousJoin.boundaryOutReferenceIncludeSoundtrack;
        previousClip.boundaryOutOverlap = previousJoin.boundaryOutOverlap;
      }
      clips.push(
        buildDefaultClip(defaults, defaultModel, false, previousClip)
      );
      saveState(state, { origin: "timeline" });
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
  var addVideoStagesClip = () => {
    if (addClipInFlight) {
      return;
    }
    addClipInFlight = true;
    void addClipAfterCatalog();
  };

  // frontend/clipReorder.ts
  var reorderClip = (from, to) => {
    const { state, revision } = getTimelineStore().getSnapshot();
    if (!Number.isInteger(from) || !Number.isInteger(to) || from < 0 || from >= state.clips.length || to < 0 || to >= state.clips.length)
      return null;
    if (from === to) return to;
    const [clip] = state.clips.splice(from, 1);
    state.clips.splice(to, 0, clip);
    const { defaults, generatedEntryMode } = captureAuthoringTransactionSnapshot();
    reconcileArchitectureIncomingIcLoraDrives(
      state.clips,
      generatedEntryMode,
      defaults.modelCatalog
    );
    saveState(state, { expectedRevision: revision, origin: "linking" });
    return to;
  };

  // frontend/trimGeometry.ts
  var clampStartLength = (start, length, duration, minLength) => {
    const clampedStart = clamp(start, 0, Math.max(0, duration - minLength));
    const clampedLength = clamp(
      length,
      minLength,
      Math.max(minLength, duration - clampedStart)
    );
    return { start: clampedStart, length: clampedLength };
  };
  var sourceLimitSeconds = (source) => source.durationSeconds > 0 ? source.durationSeconds : source.startSeconds + source.lengthSeconds;
  var toInOut = ({
    startSeconds,
    lengthSeconds
  }) => ({
    inSeconds: startSeconds,
    outSeconds: roundToTenth(startSeconds + lengthSeconds)
  });
  var fromInOut = ({ inSeconds, outSeconds }, { limitSeconds, minLengthSeconds, fps }) => {
    const start = roundToTenth(inSeconds);
    const length = snapDurationToFps(roundToTenth(outSeconds) - start, fps);
    const clamped = clampStartLength(
      start,
      length,
      limitSeconds,
      minLengthSeconds
    );
    return { startSeconds: clamped.start, lengthSeconds: clamped.length };
  };
  var setInPoint = (current, inSeconds, limits) => {
    const { outSeconds } = toInOut(current);
    return fromInOut(
      {
        inSeconds: Math.max(
          0,
          Math.min(inSeconds, outSeconds - limits.minLengthSeconds)
        ),
        outSeconds
      },
      limits
    );
  };
  var setOutPoint = (current, outSeconds, limits) => {
    const { inSeconds } = toInOut(current);
    return fromInOut(
      {
        inSeconds,
        outSeconds: Math.min(
          limits.limitSeconds,
          Math.max(outSeconds, inSeconds + limits.minLengthSeconds)
        )
      },
      limits
    );
  };
  var slideRange = (current, inSeconds, limits) => {
    const length = current.lengthSeconds;
    const start = Math.max(
      0,
      Math.min(
        roundToTenth(inSeconds),
        Math.max(0, limits.limitSeconds - length)
      )
    );
    return fromInOut({ inSeconds: start, outSeconds: start + length }, limits);
  };
  var markInPoint = (current, inSeconds, limits) => inSeconds > toInOut(current).outSeconds ? slideRange(current, inSeconds, limits) : setInPoint(current, inSeconds, limits);

  // frontend/detailStrip/modalManager.ts
  var currentModal = null;
  var closeManagedModal = () => currentModal?.close();
  var createManagedModal = (spec) => {
    const backdrop = document.createElement("div");
    backdrop.className = `modal-backdrop fade show ${spec.backdropClass}`;
    const modal = document.createElement("div");
    modal.className = `modal fade show ${spec.modalClass}`;
    modal.style.display = "block";
    modal.tabIndex = -1;
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.setAttribute("aria-labelledby", spec.labelledBy);
    const dialog = document.createElement("div");
    dialog.className = "modal-dialog modal-dialog-centered";
    dialog.setAttribute("role", "document");
    const content = document.createElement("div");
    content.className = "modal-content";
    const header = document.createElement("div");
    header.className = "modal-header";
    const body = document.createElement("div");
    body.className = "modal-body";
    content.append(header, body);
    dialog.appendChild(content);
    modal.appendChild(dialog);
    let open = false;
    const onKeyDown = (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        managed.close();
        return;
      }
      spec.onKeyDown?.(event);
    };
    const managed = {
      modal,
      content,
      header,
      body,
      open: (initialFocus) => {
        closeManagedModal();
        open = true;
        currentModal = managed;
        document.addEventListener("keydown", onKeyDown);
        document.body.append(backdrop, modal);
        initialFocus?.focus();
      },
      close: () => {
        if (!open) {
          return;
        }
        open = false;
        document.removeEventListener("keydown", onKeyDown);
        if (currentModal === managed) {
          currentModal = null;
        }
        try {
          spec.onClose?.();
        } finally {
          modal.remove();
          backdrop.remove();
        }
      }
    };
    backdrop.addEventListener("click", managed.close);
    modal.addEventListener("mousedown", (event) => {
      if (event.target === modal) {
        managed.close();
      }
    });
    return managed;
  };

  // frontend/detailStrip/sidebarMediaPreview.ts
  var makeMediaPreviewExpandable = (preview, src, name, playback) => {
    preview.querySelector(".auto-input-remove-button")?.remove();
    const media = preview.querySelector("img, video, audio");
    if (!media) return;
    const kind = playback?.kind ?? media.tagName.toLowerCase();
    const button2 = document.createElement("button");
    button2.type = "button";
    button2.className = "basic-button vst-media-thumbnail";
    button2.dataset.testid = "media-preview";
    button2.dataset.mediaKind = kind;
    button2.setAttribute("aria-label", `Preview ${name}`);
    const hint = document.createElement("span");
    hint.className = "vst-media-thumbnail-hint";
    hint.textContent = kind === "audio" ? "▶ Audio" : kind === "video" ? "▶ Video" : "↗";
    hint.setAttribute("aria-hidden", "true");
    media.addEventListener("error", () => {
      hint.textContent = "Preview unavailable";
      button2.setAttribute("aria-label", `Preview unavailable: ${name}`);
    });
    if (media instanceof HTMLMediaElement) {
      media.controls = false;
      media.muted = true;
      if (kind === "audio") media.hidden = true;
    }
    button2.append(media, hint);
    preview.replaceChildren(button2);
    button2.addEventListener("click", () => {
      const modal = createManagedModal({
        modalClass: "vst-media-preview-modal",
        backdropClass: "vst-media-preview-backdrop",
        labelledBy: "vst-media-preview-title",
        onClose: () => {
          releaseSidebarMediaPreviews(modal.body);
          if (button2.isConnected) button2.focus();
        },
        onKeyDown: (event) => {
          if (event.key !== "Tab") return;
          const controls = Array.from(
            modal.modal.querySelectorAll(
              "button, video[controls], audio[controls]"
            )
          );
          const first = controls[0];
          const last = controls.at(-1);
          if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last?.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first?.focus();
          }
        }
      });
      const title = document.createElement("h5");
      title.id = "vst-media-preview-title";
      title.className = "modal-title";
      title.textContent = name;
      const close = document.createElement("button");
      close.type = "button";
      close.className = "basic-button";
      close.textContent = "Close";
      close.addEventListener("click", modal.close);
      modal.header.append(title, close);
      if (kind === "audio" || kind === "video") {
        modal.body.append(
          buildSidebarMediaPreview(
            kind,
            src,
            playback?.range ?? { startSeconds: 0, lengthSeconds: 0 }
          )
        );
      } else {
        const image = document.createElement("img");
        image.alt = name;
        image.src = mediaPreviewSrc(src);
        modal.body.append(image);
      }
      modal.open(close);
    });
  };
  var buildSidebarMediaPreview = (mediaKind, dataUri, range) => {
    const section = document.createElement("div");
    section.className = "vst-sidebar-media-preview-section";
    const label = document.createElement("span");
    label.className = "vst-sidebar-media-preview-label";
    label.textContent = "Preview";
    const player = mediaKind === "video" ? getVideoStagesHostBridge().createInitVideoElement() : document.createElement("audio");
    player.className = `vst-sidebar-media-preview vst-sidebar-${mediaKind}-preview`;
    player.controls = true;
    player.preload = "metadata";
    player.setAttribute("playsinline", "");
    const mediaError = document.createElement("div");
    mediaError.className = "vst-sidebar-media-preview-error";
    mediaError.textContent = `Cannot preview this ${mediaKind}.`;
    mediaError.hidden = true;
    const bounds = () => range.lengthSeconds > 0 ? toInOut(range) : null;
    const resetToIn = () => {
      const selected = bounds();
      if (selected && player.currentTime !== selected.inSeconds) {
        player.currentTime = selected.inSeconds;
      }
    };
    const keepSeekInRange = () => {
      const selected = bounds();
      if (selected && (player.currentTime < selected.inSeconds || player.currentTime >= selected.outSeconds)) {
        resetToIn();
      }
    };
    const stopAtOut = () => {
      const selected = bounds();
      if (!selected) {
        return;
      }
      if (player.currentTime >= selected.outSeconds) {
        player.pause();
        resetToIn();
      } else if (player.currentTime < selected.inSeconds) {
        resetToIn();
      }
    };
    player.addEventListener("loadedmetadata", resetToIn);
    player.addEventListener("play", keepSeekInRange);
    player.addEventListener(
      mediaKind === "audio" ? "seeked" : "seeking",
      keepSeekInRange
    );
    player.addEventListener("timeupdate", stopAtOut);
    player.addEventListener("ended", stopAtOut);
    player.addEventListener("error", () => {
      player.hidden = true;
      mediaError.hidden = false;
    });
    player.src = mediaPreviewSrc(dataUri);
    section.append(label, player, mediaError);
    return section;
  };
  var releaseSidebarMediaPreviews = (root) => {
    for (const player of root.querySelectorAll(
      ".vst-sidebar-media-preview"
    )) {
      player.pause();
      player.removeAttribute("src");
      player.load();
    }
  };

  // frontend/fileDataUri.ts
  var fileAsDataUri = (file) => new Promise((resolve) => {
    const reader = new FileReader();
    reader.onerror = () => resolve(null);
    reader.onload = () => {
      const data = `${reader.result ?? ""}`;
      resolve(data || null);
    };
    reader.readAsDataURL(file);
  });

  // frontend/fileDrop.ts
  var hasDroppedFiles = (transfer) => transfer !== null && (transfer.files.length > 0 || Array.from(transfer.items ?? []).some((item) => item.kind === "file") || Array.from(transfer.types ?? []).includes("Files"));
  var readDirectoryBatch = (reader) => new Promise((resolve) => reader.readEntries(resolve, () => resolve([])));
  var filesFromEntry = async (entry) => {
    if (entry.isFile && entry.file) {
      return new Promise(
        (resolve) => entry.file?.(
          (file) => resolve([file]),
          () => resolve([])
        )
      );
    }
    const reader = entry.isDirectory ? entry.createReader?.() : void 0;
    if (!reader) {
      return [];
    }
    const files = [];
    for (; ; ) {
      const batch = await readDirectoryBatch(reader);
      if (batch.length === 0) {
        return files;
      }
      const nested = await Promise.all(batch.map(filesFromEntry));
      files.push(...nested.flat());
    }
  };
  var collectDroppedFiles = async (transfer) => {
    const items = Array.from(transfer.items ?? []);
    if (items.length === 0) {
      return Array.from(transfer.files);
    }
    const nested = await Promise.all(
      items.map((item) => {
        const entry = item.webkitGetAsEntry?.();
        if (entry) {
          return filesFromEntry(entry);
        }
        const file = item.getAsFile();
        return Promise.resolve(file ? [file] : []);
      })
    );
    const files = nested.flat();
    return files.length > 0 ? files : Array.from(transfer.files);
  };

  // frontend/host/swarmUiAdapters.ts
  var restoreHostMediaPreview = (input2, src, name) => {
    if (typeof setMediaFileDirect !== "function") return;
    setMediaFileDirect(input2, src, getMediaType(src), name);
    const player = input2.closest(".auto-input")?.querySelector("video, audio");
    if (player instanceof HTMLMediaElement) {
      player.classList.add("vst-sidebar-media-preview");
      player.autoplay = false;
      player.controls = true;
      player.preload = "metadata";
    }
  };
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
  var showHostActionMenu = (anchor2, actions) => {
    if (typeof AdvancedPopover !== "function") return;
    const rect = anchor2.getBoundingClientRect();
    new AdvancedPopover(
      "videostages-actions",
      actions,
      false,
      rect.left,
      rect.bottom,
      document.body,
      null,
      rect.height
    );
  };
  var installHostFeature = (featureId, buttonContainerId) => {
    if (typeof installFeatureById !== "function") {
      return false;
    }
    installFeatureById(featureId, buttonContainerId);
    return true;
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
  var generationInputProjector = null;
  var installedGenerationWrapper = null;
  var installedInputCollector = null;
  var generationProjectorRetry = null;
  var projectGenerationInput = (actualInput, beforeProjection) => {
    try {
      beforeProjection?.();
      if (isRecord(actualInput)) {
        generationInputProjector?.(actualInput);
      }
    } catch (error) {
      if (typeof showError === "function") {
        showError(`${error}`);
      }
      throw error;
    }
  };
  var installHostInputCollectorProjector = () => {
    const host = globalThis;
    if (typeof host.getGenInput !== "function" || host.getGenInput === installedInputCollector) {
      return;
    }
    const original = host.getGenInput;
    const wrapper = function(...args) {
      const actualInput = original.apply(this, args);
      projectGenerationInput(actualInput);
      return actualInput;
    };
    host.getGenInput = wrapper;
    installedInputCollector = wrapper;
  };
  var retryGenerationProjector = () => {
    if (generationProjectorRetry !== null) {
      return;
    }
    generationProjectorRetry = setTimeout(() => {
      generationProjectorRetry = null;
      if (generationInputProjector) {
        installHostGenerationInputProjector(generationInputProjector);
      }
    }, 250);
  };
  var installHostGenerationInputProjector = (projector) => {
    generationInputProjector = projector;
    installHostInputCollectorProjector();
    if (typeof mainGenHandler === "undefined" || typeof mainGenHandler?.doGenerate !== "function") {
      retryGenerationProjector();
      return false;
    }
    if (generationProjectorRetry !== null) {
      clearTimeout(generationProjectorRetry);
      generationProjectorRetry = null;
    }
    if (mainGenHandler.doGenerate === installedGenerationWrapper) {
      return true;
    }
    const original = mainGenHandler.doGenerate;
    const wrapper = function(...args) {
      const existingPostCollect = args[2];
      args[2] = (actualInput) => projectGenerationInput(
        actualInput,
        typeof existingPostCollect === "function" ? () => existingPostCollect(actualInput) : void 0
      );
      original.apply(this, args);
    };
    mainGenHandler.doGenerate = wrapper;
    installedGenerationWrapper = wrapper;
    return true;
  };
  var hasHostInputBrowser = () => typeof inputBrowserHelper !== "undefined" && Boolean(inputBrowserHelper);
  var isHostReusableMediaPath = (value) => typeof isValidMediaPath === "function" && isValidMediaPath(value);
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

  // frontend/toggleSwitch.ts
  var toggleSequence = 0;
  function buildToggleSwitch(options) {
    const wrap = document.createElement("span");
    wrap.className = "form-check form-switch toggle-switch display-inline-block group-toggler-switch";
    const input2 = document.createElement("input");
    input2.type = "checkbox";
    input2.id = `vst_toggle_${++toggleSequence}`;
    input2.className = "auto-slider-toggle form-check-input";
    input2.checked = options.checked;
    input2.disabled = options.disabled === true;
    input2.title = options.title;
    input2.setAttribute("aria-label", options.title);
    input2.addEventListener("click", (event) => event.stopPropagation());
    input2.addEventListener("change", (event) => {
      event.stopPropagation();
      options.onChange(input2.checked);
    });
    const track = document.createElement("div");
    track.className = "auto-slider-toggle-content";
    wrap.append(input2, track);
    return wrap;
  }

  // frontend/detailWidgets.ts
  var sliderSeq = 0;
  var helpSeq = 0;
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
    if (control.classList.contains("auto-checkbox")) {
      return "auto-checkbox-box";
    }
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
    row.className = "auto-input vst-detail-field vst-form-field";
    row.dataset.fieldKind = control instanceof HTMLInputElement ? control.type : control.tagName.toLowerCase();
    row.classList.add(
      control.classList.contains("auto-text-block") ? "auto-input-flex-wide" : "auto-input-flex"
    );
    const boxClass = boxClassFor(control);
    if (boxClass) {
      row.classList.add(boxClass);
    }
    const labelEl = buildFieldLabel(label);
    if (control instanceof HTMLInputElement || control instanceof HTMLSelectElement || control instanceof HTMLTextAreaElement) {
      if (!control.id) {
        control.id = `vst_field_${slugify(label)}_${++fieldSeq}`;
      }
      labelEl.htmlFor = control.id;
    }
    row.append(labelEl, control);
    if (help) {
      appendHelp(labelEl, row, label, help);
    }
    if (hint) {
      const small = document.createElement("small");
      small.className = "auto-input-description vst-detail-field-hint";
      small.textContent = hint;
      row.appendChild(small);
    }
    return row;
  };
  var setFieldDisabled = (field, disabled, reason = "") => {
    field.querySelectorAll("input, select, textarea, button").forEach((control) => {
      control.disabled = disabled;
    });
    field.classList.toggle("vst-field-disabled", disabled);
    field.title = disabled ? reason : "";
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
    if (onChange) {
      select2.addEventListener("change", () => onChange(select2.value));
    }
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
    holder.className = "vst-stage-slider auto-input-flex-wide vst-detail-field vst-form-field";
    holder.dataset.fieldKind = "slider";
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
    holder.querySelector(".auto-input-name")?.classList.add("vst-detail-field-label");
    const number = holder.querySelector(
      "input.auto-slider-number"
    );
    if (number) {
      const labelEl = holder.querySelector("label");
      if (labelEl) {
        labelEl.htmlFor = number.id;
        labelEl.addEventListener("click", (event) => {
          if (event.target.closest(
            "input, .info-popover-button"
          ))
            return;
          event.preventDefault();
          if (!number.disabled) {
            number.focus();
            number.select();
          }
        });
      }
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
    const input2 = document.createElement("input");
    input2.type = "checkbox";
    input2.className = "auto-checkbox";
    input2.dataset.name = label;
    input2.checked = checked;
    input2.addEventListener("change", () => onChange(input2.checked));
    const row = buildField(label, input2, void 0, opts?.help);
    row.classList.add("vst-detail-field-check");
    row.dataset.disabled = `${opts?.disabled === true}`;
    if (opts?.disabled) {
      row.classList.add("vst-audio-disabled");
      input2.disabled = true;
    }
    return row;
  };
  var buildTextarea = (value, placeholder, focusKey, onInput, layout) => {
    const editor = document.createElement("textarea");
    editor.className = "auto-text auto-text-block vst-prompt-editor vst-detail-prompt";
    editor.value = value;
    editor.placeholder = placeholder;
    editor.setAttribute("data-vst-focus-key", focusKey);
    if (layout) {
      editor.rows = layout.rows;
      if (layout.maxRows !== void 0) {
        editor.classList.add("vst-textarea-limited");
        editor.style.setProperty(
          "--vst-textarea-max-lines",
          `${layout.maxRows}lh`
        );
      }
    }
    editor.addEventListener("input", () => onInput(editor.value));
    enhanceHostPromptEditor(editor);
    return editor;
  };
  var mediaPickCounter = 0;
  var buildMediaPickRow = (label, accept, browserTypes, name, onFile, onClear, previewData, playback) => {
    const row = document.createElement("div");
    row.className = "auto-input auto-file-box vst-detail-field vst-form-field vst-audio-upload";
    row.dataset.fieldKind = "file";
    const controls = document.createElement("div");
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
    const actions = document.createElement("span");
    actions.className = "vst-media-pick-actions";
    actions.appendChild(uploadBtn);
    controls.append(pickLabel, actions);
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
    const acceptFile = (file) => {
      void fileAsDataUri(file).then((data) => {
        if (data) {
          onFile(storeBrowserMedia(data), file.name);
        }
      });
    };
    const previewSrc = previewData ? mediaPreviewSrc(previewData) : "";
    fileInput.addEventListener("change", (event) => {
      const file = fileInput.files?.[0];
      if (file) {
        acceptFile(file);
        return;
      }
      const picked = fileInput.dataset.filedata ?? "";
      if (previewSrc && picked === previewSrc) {
        event.stopPropagation();
        return;
      }
      if (!picked) {
        if (previewSrc) onClear();
        return;
      }
      const pickedName = fileInput.dataset.filename ?? "server file";
      if (picked.startsWith("data:")) {
        onFile(storeBrowserMedia(picked), pickedName);
        return;
      }
      if (isHostReusableMediaPath(picked)) {
        onFile(picked, pickedName);
        return;
      }
      void getVideoStagesHostBridge().toDataUrl(picked).then((data) => onFile(storeBrowserMedia(data), pickedName));
    });
    row.addEventListener("dragover", (event) => {
      if (!hasDroppedFiles(event.dataTransfer)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
    });
    row.addEventListener("drop", (event) => {
      const file = event.dataTransfer?.files[0];
      if (!file) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      acceptFile(file);
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
      actions.appendChild(selectBtn);
    }
    actions.appendChild(clearBtn);
    const details = document.createElement("div");
    details.className = "vst-media-pick-details";
    details.append(controls, fileDrop);
    row.append(preview, details);
    if (previewSrc) {
      if (playback) {
        fileInput.dataset.filedata = previewSrc;
        preview.append(
          buildSidebarMediaPreview(
            playback.kind,
            previewData ?? previewSrc,
            playback.range
          )
        );
      } else {
        restoreHostMediaPreview(fileInput, previewSrc, name || "media");
      }
      makeMediaPreviewExpandable(
        preview,
        previewData ?? previewSrc,
        name || "media",
        playback
      );
    }
    row.classList.toggle(
      "vst-media-pick-has-preview",
      Boolean(preview.firstChild)
    );
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
  var buildDetailActionButton = (spec) => {
    const button2 = document.createElement("button");
    button2.type = "button";
    button2.className = `${spec.variant === "interrupt" ? "interrupt-button" : "basic-button"} ${spec.className}`.trim();
    button2.textContent = spec.label;
    button2.title = spec.title;
    button2.setAttribute("aria-label", spec.title);
    button2.disabled = spec.disabled === true;
    if (spec.active !== void 0) {
      button2.setAttribute("aria-pressed", `${spec.active}`);
      button2.classList.toggle("vst-btn-skip-active", spec.active);
    }
    button2.addEventListener("click", (event) => {
      event.preventDefault();
      if (spec.stopPropagation) {
        event.stopPropagation();
      }
      spec.onClick(button2);
    });
    return button2;
  };
  var appendSectionHeaderAction = (target, actionSpec) => {
    const action = buildDetailActionButton({
      label: actionSpec.label,
      title: actionSpec.title,
      className: `vst-btn-tiny vst-detail-repeating-group-action ${actionSpec.className ?? ""}`.trim(),
      variant: actionSpec.variant,
      active: actionSpec.active,
      stopPropagation: true,
      onClick: actionSpec.onClick
    });
    target.appendChild(action);
  };
  var appendSectionHeaderActions = (actions, spec) => {
    const headerActions = spec.headerActions ?? (spec.headerAction === void 0 ? [] : [spec.headerAction]);
    if (headerActions.length === 0) {
      return;
    }
    for (const action of headerActions) {
      appendSectionHeaderAction(actions, action);
    }
  };
  var buildSectionHeader = (spec) => {
    const header = document.createElement("span");
    header.className = spec.className;
    const labelWrap = document.createElement("span");
    labelWrap.className = "header-label-wrap";
    if (spec.open !== void 0) {
      const symbol = document.createElement("span");
      symbol.className = "auto-symbol";
      symbol.textContent = spec.open ? "⮟" : "⮞";
      labelWrap.appendChild(symbol);
    }
    const heading = document.createElement("span");
    heading.className = "header-label";
    heading.textContent = spec.label;
    const spacer = document.createElement("span");
    spacer.className = "header-label-spacer";
    labelWrap.append(heading, spacer);
    if (spec.counter !== void 0) {
      const counter = document.createElement("span");
      counter.className = "header-label-counter";
      counter.textContent = `${spec.counter}`;
      labelWrap.appendChild(counter);
    }
    const actions = document.createElement("span");
    actions.className = "vst-detail-repeating-group-actions";
    labelWrap.appendChild(actions);
    header.appendChild(labelWrap);
    return { header, heading, actions };
  };
  var rememberedAccordionSections = /* @__PURE__ */ new Set();
  var seenAccordionSections = /* @__PURE__ */ new Set();
  var resetRememberedAccordionSections = () => {
    rememberedAccordionSections.clear();
    seenAccordionSections.clear();
  };
  var buildAccordionSection = (spec) => {
    const firstBuild = !seenAccordionSections.has(spec.key);
    const open = spec.open === true || firstBuild && spec.defaultOpen === true || rememberedAccordionSections.has(spec.key);
    seenAccordionSections.add(spec.key);
    if (open) {
      rememberedAccordionSections.add(spec.key);
    }
    const section = document.createElement("div");
    section.className = `input-group vst-detail-section ${open ? "input-group-open" : "input-group-closed"} ${spec.className ?? ""}`.trim();
    section.classList.toggle(
      "vst-detail-section-has-toggle",
      spec.toggle !== void 0
    );
    section.dataset.vstAccordionKey = spec.key;
    const { header, heading, actions } = buildSectionHeader({
      label: spec.label,
      className: "input-group-header input-group-shrinkable vst-detail-section-header",
      open,
      counter: spec.counter
    });
    header.tabIndex = 0;
    header.setAttribute("role", "button");
    header.setAttribute("aria-expanded", `${open}`);
    appendSectionHeaderActions(actions, spec);
    if (spec.toggle) {
      heading.after(buildToggleSwitch(spec.toggle));
    }
    const content = document.createElement("fieldset");
    content.className = "input-group-content vst-detail-section-content";
    content.disabled = spec.toggle?.checked === false;
    content.classList.toggle("vst-field-disabled", content.disabled);
    content.hidden = !open;
    appendSectionContent(content, spec.content, spec.flattenContent === true);
    const toggle = (event) => {
      event.preventDefault();
      event.stopPropagation();
      const opening = content.hidden === true;
      setAccordionOpen(section, opening);
      if (opening) {
        rememberedAccordionSections.add(spec.key);
      } else {
        rememberedAccordionSections.delete(spec.key);
      }
    };
    header.addEventListener("click", toggle);
    header.addEventListener("keydown", (event) => {
      if (event.target === header && (event.key === "Enter" || event.key === " ")) {
        toggle(event);
      }
    });
    section.append(header, content);
    return { section, heading, content };
  };
  var tagFocus = (field, key) => {
    const control = field.querySelector("input.auto-slider-number") ?? field.querySelector("input, select") ?? (field.matches("input, select") ? field : null);
    control?.setAttribute("data-vst-focus-key", key);
    return field;
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
      return "This clip does not accept keyframe endpoints.";
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
      return "This clip does not publish a supported keyframe endpoint.";
    }
    if (!policy.bounded) {
      return void 0;
    }
    if (policy.supportsFirst && policy.supportsLast) {
      return "Off means first frame; on means final frame.";
    }
    return policy.supportsFirst ? "This clip supports only the first frame. Turn this off to repair an older final-frame value." : "This clip supports only the final frame. Turn this on to repair an older first-frame value.";
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
                  id: createEntityId("keyframe")
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
    const addRefEntries = (clipId, uploads) => {
      if (uploads.length === 0) {
        return 0;
      }
      let added = 0;
      structuralCommit((clips) => {
        const clipIdx = clips.findIndex((clip2) => clip2.id === clipId);
        const clip = clips[clipIdx];
        const { capabilities, defaults } = captureAuthoringTransaction();
        if (!clip || !capabilities.forClip(clip).decision("frameReferences").supported) {
          return null;
        }
        const refs = [...clip.frameRefs];
        const additions = uploads.flatMap((upload) => {
          const position = nextAllowedReferencePosition(
            refs,
            getReferenceFrameMax(defaults, clip),
            referenceEndpointPolicy(clip, defaults.modelCatalog).positions
          );
          if (!position) {
            return [];
          }
          const ref = {
            ...buildDefaultRef(MEDIA_SOURCE_UPLOAD),
            ...position,
            id: createEntityId("keyframe"),
            uploadFileName: upload.fileName,
            uploadedImage: upload
          };
          refs.push(ref);
          return [ref];
        });
        if (additions.length === 0) {
          return null;
        }
        added = additions.length;
        return {
          command: {
            type: "batch",
            commands: [
              ...additions.map((ref) => ({
                type: "ref.add",
                clipId,
                ref
              })),
              ...refStrengthPatches(clip, (strengths) => [
                ...strengths,
                ...additions.map(() => STAGE_REF_STRENGTH_DEFAULT)
              ])
            ]
          },
          selection: {
            kind: "ref",
            clipIdx,
            refIdx: clip.frameRefs.length + additions.length - 1
          }
        };
      });
      return added;
    };
    const addClipReference = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        const { capabilities } = captureAuthoringTransaction();
        if (!clip?.id) {
          return null;
        }
        const authoring = clipReferenceAuthoring(
          capabilities.forClip(clip)
        );
        if (authoring.mode === "none") return null;
        return {
          command: {
            type: "clip-reference.add",
            clipId: clip.id,
            reference: {
              ...buildDefaultClipReference("image"),
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
    const addClipReferences = (clipId, references) => {
      if (references.length === 0) {
        return 0;
      }
      let added = 0;
      structuralCommit((clips) => {
        const clipIdx = clips.findIndex((clip2) => clip2.id === clipId);
        const clip = clips[clipIdx];
        const { capabilities } = captureAuthoringTransaction();
        if (!clip) {
          return null;
        }
        const authoring = clipReferenceAuthoring(
          capabilities.forClip(clip)
        );
        const accepted = references.filter(
          (reference) => authoring.mode === "all" || authoring.mode === "identity-images" && reference.kind === "image"
        );
        if (accepted.length === 0) return null;
        added = accepted.length;
        return {
          command: {
            type: "batch",
            commands: accepted.map((reference) => ({
              type: "clip-reference.add",
              clipId,
              reference: {
                ...buildDefaultClipReference(reference.kind),
                ...reference,
                id: createEntityId("clip_reference")
              }
            }))
          },
          selection: {
            kind: "clip-ref",
            clipIdx,
            referenceIdx: clip.references.length + accepted.length - 1
          }
        };
      });
      return added;
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
          return {
            kind: "clip",
            clipIdx: clipIdx - 1,
            stageIdx: 0
          };
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
            clip.icLoras.map(
              (entry) => defaultLoraWeight(defaults, entry.lora)
            )
          );
          stage.skipped = last?.skipped === true;
          stage.loras = last?.loras.map((entry) => ({ ...entry })) ?? [];
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
      addRefEntries,
      deleteRefEntry,
      addClipReference,
      addClipReferences,
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

  // frontend/detailStrip/authoringSession.ts
  var createAuthoringSession = (host) => {
    let settingsMode = null;
    let boundRoot = null;
    let draftQueue;
    const focus = createDetailFocusSession({
      getDock: host.getRoot,
      isRendering: host.isRendering,
      flushPending: () => draftQueue.flush()
    });
    draftQueue = createDetailDraftQueue({
      focus,
      getDock: host.getRoot,
      isRendering: host.isRendering,
      getRenderedSelection: getSelection,
      syncValueDerivedUi: () => void 0,
      render: host.render,
      setSelectionSilently: host.setSelectionSilently ?? ((selection) => setSelection(selection))
    });
    const operations = createDetailSelectionDomainOperations(
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
      render: host.render,
      authoring: captureAuthoringTransactionSnapshot,
      ...operations,
      getBoundBody: host.getRoot,
      getSettingsMode: () => settingsMode,
      setSettingsMode: (mode) => {
        settingsMode = mode;
      }
    };
    const unbindRoot = () => {
      boundRoot?.removeEventListener("focusout", focus.onDockFocusOut);
      boundRoot?.removeEventListener("focusin", focus.onDockFocusIn);
      boundRoot?.removeEventListener("change", focus.onDockChange);
      boundRoot = null;
    };
    const bindRoot = () => {
      const root = host.getRoot();
      if (root === boundRoot) {
        return;
      }
      unbindRoot();
      boundRoot = root;
      boundRoot?.addEventListener("focusout", focus.onDockFocusOut);
      boundRoot?.addEventListener("focusin", focus.onDockFocusIn);
      boundRoot?.addEventListener("change", focus.onDockChange);
    };
    document.addEventListener("pointerdown", focus.onDocumentPointerDown, true);
    document.addEventListener("pointerup", focus.onDocumentPointerUp, true);
    document.addEventListener("pointercancel", focus.onDocumentPointerUp, true);
    return {
      context,
      flush: () => draftQueue.flush(),
      beforeRender: () => {
        bindRoot();
        draftQueue.flush();
        focus.capture();
        draftQueue.markCurrentSource();
      },
      afterRender: () => {
        const root = host.getRoot();
        if (root) {
          for (const input2 of root.querySelectorAll(
            "[data-vst-host-toggle]"
          )) {
            doToggleEnable(input2.id);
          }
          focus.restore(root);
        }
      },
      dispose: () => {
        draftQueue.dispose();
        focus.reset();
        unbindRoot();
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
      }
    };
  };

  // frontend/clipEditor/components.ts
  function element(tag, className, text2) {
    const node = document.createElement(tag);
    node.className = className;
    if (text2 !== void 0) node.textContent = text2;
    return node;
  }
  function actionButton(action) {
    const button2 = element("button", "basic-button small-button", action.label);
    button2.type = "button";
    button2.disabled = action.disabled ?? false;
    if (action.title) button2.title = action.title;
    action.configure?.(button2);
    button2.dataset.testid = action.key;
    if (action.kind) button2.dataset.action = action.kind;
    if (action.kind === "delete") {
      const icon = document.createElementNS(
        "http://www.w3.org/2000/svg",
        "svg"
      );
      icon.setAttribute("viewBox", "0 0 24 24");
      icon.setAttribute("aria-hidden", "true");
      icon.setAttribute("focusable", "false");
      icon.innerHTML = '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>';
      button2.prepend(icon);
    }
    button2.addEventListener("click", action.run);
    return button2;
  }
  function scopeFields(context, scope) {
    return {
      ...context,
      debouncedCommit: (key, mutate) => context.debouncedCommit(`${scope}/${key}`, mutate),
      debouncedCommitState: (key, mutate) => context.debouncedCommitState(`${scope}/${key}`, mutate),
      buildClampedNumber: (options) => context.buildClampedNumber({
        ...options,
        key: `${scope}/${options.key}`
      })
    };
  }
  function tagFields(root, scope) {
    const keys = /* @__PURE__ */ new Map();
    for (const control of root.querySelectorAll(
      "input, select, textarea, button"
    )) {
      const focus = control.dataset.vstFocusKey;
      if (focus && !focus.startsWith(`${scope}/`))
        control.dataset.vstFocusKey = `${scope}/${focus}`;
      const field = control.closest(
        ".vst-detail-field, .auto-input"
      );
      const label = (field?.querySelector(".vst-detail-field-label, .auto-input-name") ?? field?.querySelector("label"))?.childNodes[0]?.textContent?.trim();
      const name = focus?.split("/").at(-1) || label || control.getAttribute("aria-label") || control.textContent || control.tagName;
      const base = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
      const kind = control instanceof HTMLInputElement ? control.type : control.tagName.toLowerCase();
      const key = `field-${base}-${kind}`;
      const duplicate = keys.get(key) || 0;
      keys.set(key, duplicate + 1);
      control.dataset.testid = `${key}${duplicate ? `-${duplicate}` : ""}`;
      control.dataset.field = base;
    }
  }
  function sectionPanel(section, clipId) {
    const panel = element(
      "section",
      "input-group input-group-open vst-editor-section"
    );
    panel.dataset.section = section.key;
    panel.dataset.testid = `section-${section.key}`;
    panel.dataset.clipId = clipId;
    const header = element(
      "header",
      "input-group-header vst-editor-section-heading"
    );
    const title = element("h3", "", section.label);
    const currentItem = element("span", "vst-editor-current-item");
    currentItem.dataset.testid = "sticky-item-label";
    currentItem.hidden = true;
    title.id = `vst-editor-heading-${section.key}`;
    panel.setAttribute("aria-labelledby", title.id);
    const actions = element("div", "vst-editor-actions");
    actions.append(...(section.actions || []).map(actionButton));
    header.addEventListener("click", (event) => event.stopPropagation());
    header.append(title, actions, currentItem);
    panel.append(header);
    const rows = section.layout === "rows";
    const items = rows ? element("div", "input-group-content vst-editor-card vst-editor-rows") : panel;
    if (rows && section.items.length) panel.append(items);
    for (const item of section.items) {
      const body = element(
        "article",
        rows ? "vst-editor-item vst-editor-row" : "input-group-content vst-editor-card vst-editor-item"
      );
      body.dataset.itemId = item.key;
      body.dataset.itemLabel = item.contextLabel ?? item.label;
      body.dataset.testid = "editor-item";
      if (item.selection)
        body.dataset.selection = JSON.stringify(item.selection);
      const itemHeading = element("div", "vst-editor-item-heading");
      const itemTitle = element(
        "h4",
        "",
        rows ? item.contextLabel : item.label
      );
      if (rows) itemTitle.setAttribute("aria-label", item.label);
      itemHeading.append(itemTitle);
      if (item.toggle) {
        const action = item.toggle;
        body.dataset.enabled = String(action.checked);
        const state = element("div", "vst-editor-item-state");
        const toggle = buildToggleSwitch({
          checked: action.checked,
          title: action.label,
          onChange: action.run
        });
        const input2 = toggle.querySelector("input");
        const label = element(
          "label",
          "",
          action.checked ? "Enabled" : "Skipped"
        );
        if (input2) {
          input2.dataset.testid = action.key;
          input2.setAttribute("role", "switch");
          input2.setAttribute("aria-checked", String(action.checked));
          label.htmlFor = input2.id;
        }
        state.append(toggle, label);
        itemHeading.append(state);
      }
      const controls = element("div", "vst-editor-actions");
      controls.append(...(item.actions || []).map(actionButton));
      if (!rows) itemHeading.append(controls);
      if (section.items.length > 1 || item.actions?.length || item.label !== section.label)
        body.append(itemHeading);
      const panels = item.fields();
      const fields = Array.isArray(panels) ? element("div", "vst-detail-fields") : panels;
      if (Array.isArray(panels)) {
        for (const panel2 of panels) fields.append(...panel2.childNodes);
      }
      fields.classList.remove("input-group-content");
      fields.classList.add("vst-editor-fields");
      tagFields(fields, item.key);
      body.append(fields);
      if (rows) body.append(controls);
      items.append(body);
    }
    if (!section.items.length)
      panel.append(
        element(
          "p",
          "vst-editor-empty",
          `No ${section.label.toLowerCase()} added.`
        )
      );
    return panel;
  }

  // frontend/clipEditor/diagnostics.ts
  function renderEditorDiagnostics(root, diagnostics, selectClip) {
    const previous = root.querySelector(
      ".vst-editor-diagnostics"
    );
    if (!diagnostics.length) {
      previous?.remove();
      return;
    }
    const panel = element("details", "vst-editor-diagnostics");
    panel.open = previous?.open ?? false;
    const errors = diagnostics.filter(
      (item) => item.severity === "error"
    ).length;
    const warnings = diagnostics.length - errors;
    const counts = [
      errors ? `${errors} error${errors === 1 ? "" : "s"}` : "",
      warnings ? `${warnings} warning${warnings === 1 ? "" : "s"}` : ""
    ].filter(Boolean);
    const summary = element("summary", "", `Authoring: ${counts.join(" · ")}`);
    panel.append(summary);
    const list2 = element("ul", "vst-editor-diagnostic-list");
    for (const item of diagnostics) {
      const row = element("li", `vst-editor-diagnostic-${item.severity}`);
      row.dataset.vstDiagnostic = item.code;
      row.append(
        element(
          "strong",
          "",
          `${item.severity === "error" ? "Error" : "Warning"}: `
        )
      );
      if (item.clipIdx !== void 0) {
        const clipIdx = item.clipIdx;
        row.append(
          actionButton({
            key: `diagnostic-${item.code}-${clipIdx}`,
            label: `Clip ${clipIdx}`,
            title: `Open clip ${clipIdx} settings`,
            run: () => selectClip(clipIdx, "stages")
          })
        );
      }
      row.append(element("span", "", item.message));
      list2.append(row);
    }
    panel.append(list2);
    if (previous) previous.replaceWith(panel);
    else root.querySelector(".vst-editor-navigation")?.after(panel);
  }

  // frontend/timelineEdit.ts
  var clampClipRefsToDuration = (clip, defaults, effectiveFps) => {
    const frameMax = getKnownReferenceFrameMax(defaults, clip, effectiveFps);
    for (const ref of clip.frameRefs) {
      ref.frame = frameMax === null ? Math.max(REF_FRAME_MIN, Math.round(ref.frame)) : clamp(ref.frame, REF_FRAME_MIN, frameMax);
    }
  };
  var applyClipDurationResize = (clip, newDuration, defaults, effectiveFps) => {
    const snapped = effectiveFps === void 0 ? newDuration : snapDurationToFps(newDuration, effectiveFps);
    if (clip.duration === snapped) {
      return false;
    }
    clip.duration = snapped;
    clampClipRefsToDuration(clip, defaults, effectiveFps);
    return true;
  };

  // frontend/clipRelationships.ts
  var SOURCE_VIDEO_RELATIONSHIP = "source-video";
  var SOURCE_AUDIO_RELATIONSHIP = "source-audio";
  var clipRelationship = (clips, leftClipIdx) => {
    const left = clips[leftClipIdx];
    const right = clips[leftClipIdx + 1];
    if (!left || !right) {
      return left?.boundaryOut ?? "cut";
    }
    if (leftClipIdx === 0 && left.audioOnly === true) {
      return SOURCE_AUDIO_RELATIONSHIP;
    }
    if (right.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP) {
      return SOURCE_VIDEO_RELATIONSHIP;
    }
    return left.boundaryOut;
  };
  var setPreviousClipVideoSource = (clips, targetClipIdx, enabled, transaction, fps) => {
    const target = clips[targetClipIdx];
    const previous = clips[targetClipIdx - 1];
    if (!target || !previous) {
      return false;
    }
    if (enabled) {
      const duration = previous.duration;
      target.initVideo = {
        source: MEDIA_SOURCE_PREVIOUS_CLIP,
        data: "",
        fileName: null,
        fps,
        durationSeconds: duration,
        startSeconds: 0,
        lengthSeconds: duration
      };
      applyClipDurationResize(target, duration, transaction.defaults, fps);
    } else if (target.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP) {
      target.initVideo = null;
    } else {
      return false;
    }
    reconcileClipArchitectureIdentity(target, transaction.capabilities.catalog);
    reconcileArchitectureIncomingIcLoraDrives(
      clips,
      transaction.generatedEntryMode,
      transaction.capabilities.catalog
    );
    return true;
  };
  var setAudioOnlyLead = (clips, enabled, transaction, fps) => {
    const lead = clips[0];
    if (!lead) {
      return false;
    }
    lead.audioOnly = enabled;
    if (enabled) {
      applySkipSuffix(lead.stages, 1, true);
      setPreviousClipVideoSource(clips, 1, false, transaction, fps);
    }
    applyAudioOnlyLeadBase(clips);
    return true;
  };

  // frontend/timelineDetail.ts
  var DEFAULT_FPS = 24;
  var safeFps = (fps) => typeof fps === "number" && Number.isFinite(fps) && fps > 0 ? fps : DEFAULT_FPS;
  var formatSecondsTenth = (seconds2) => `${(Math.round((Number.isFinite(seconds2) ? seconds2 : 0) * 10) / 10).toFixed(1)}s`;
  var formatOverlapSeconds = (frames, fps) => formatSecondsTenth(frames / Math.max(1, fps));
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

  // frontend/timelinePublication.ts
  var resolveTimelinePublication = (clips) => {
    const executable = executableClipIndexes(clips);
    const output = [];
    const dependencies = [];
    for (let position = 0; position < executable.length; position++) {
      const clipIdx = executable[position];
      const clip = clips[clipIdx];
      const previousIdx = executable[position - 1];
      const previous = clips[previousIdx];
      const hasPrevious = previous !== void 0;
      const usesPreviousVideo = hasPrevious && previous.audioOnly !== true && clip.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP;
      if (hasPrevious) {
        if (usesPreviousVideo) {
          dependencies.push({
            sourceClipIdx: previousIdx,
            targetClipIdx: clipIdx,
            kind: "video-source"
          });
        }
        const selectedAudio = audioSourceKind(clip.audioSource);
        if (previous.audioOnly === true || selectedAudio === AUDIO_SOURCE_PREVIOUS_CLIP) {
          dependencies.push({
            sourceClipIdx: previousIdx,
            targetClipIdx: clipIdx,
            kind: "audio-base"
          });
        }
      }
      if (clip.audioOnly === true) {
        continue;
      }
      if (usesPreviousVideo && output.at(-1) === previousIdx) {
        output.pop();
      }
      output.push(clipIdx);
    }
    if (output.length === 0 && executable.length > 0) {
      output.push(executable.at(-1));
    }
    const published = new Set(output);
    return {
      executableClipIndexes: executable,
      outputClipIndexes: output,
      consumedClipIndexes: executable.filter(
        (index) => !published.has(index)
      ),
      dependencies
    };
  };

  // frontend/timelineTiming.ts
  var resolveTimelineTiming = (clips, rawFps, capabilities) => {
    const fps = safeFps(rawFps);
    const publication = resolveTimelinePublication(clips);
    const executable = new Set(publication.executableClipIndexes);
    const indexes = publication.outputClipIndexes;
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
    const seams = indexes.slice(0, -1).map((leftIdx, position) => ({
      position,
      leftIdx,
      rightIdx: indexes[position + 1],
      leftId: clips[leftIdx]?.id,
      rightId: clips[indexes[position + 1]]?.id
    }));
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
    const generatedFrames = publication.executableClipIndexes.reduce(
      (sum, clipIdx) => sum + clipFrames[clipIdx],
      0
    ) + boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const outputGeneratedFrames = indexes.reduce((sum, clipIdx) => sum + clipFrames[clipIdx], 0) + boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const joinFrames = boundaries.reduce(
      (sum, boundary) => sum + boundary.overlapFrames,
      0
    );
    const outputFrames = Math.max(0, outputGeneratedFrames - joinFrames);
    return {
      fps,
      executableClipIndexes: publication.executableClipIndexes,
      outputClipIndexes: publication.outputClipIndexes,
      consumedClipIndexes: publication.consumedClipIndexes,
      dependencies: publication.dependencies,
      clipFrames,
      boundaries,
      authoredSeconds: publication.executableClipIndexes.reduce(
        (sum, clipIdx) => sum + Math.max(0, clips[clipIdx].duration || 0),
        0
      ),
      generatedFrames,
      joinFrames,
      joinSeconds: joinFrames / fps,
      outputFrames,
      outputSeconds: outputFrames / fps,
      outputGeometryAvailable: publication.consumedClipIndexes.length > 0 || publication.executableClipIndexes.length === clips.length
    };
  };
  var boundaryImpactForLeftClip = (timing, leftClipIdx) => timing.boundaries.find((boundary) => boundary.leftIdx === leftClipIdx) ?? null;
  var incomingReferenceContinueForClip = (clips, rawFps, capabilities, rightClipIdx) => resolveTimelineTiming(clips, rawFps, capabilities).boundaries.find(
    (boundary) => boundary.rightIdx === rightClipIdx && boundary.effectiveMode === "continue" && capabilities.forBoundaryIndex(clips, boundary.leftIdx).windowConstraints("continue").continueMode === "reference"
  ) ?? null;

  // frontend/clipEditor/flowMap.ts
  function flowMap(state, selected, navigation, collapsed) {
    const strip = element("aside", "vst-editor-strip");
    strip.dataset.testid = "clip-strip";
    strip.setAttribute("aria-label", "Clip flow");
    const output = element("div", "vst-editor-output");
    const enabled = isVideoStagesEnabled();
    strip.dataset.enabled = String(enabled);
    const timing = resolveTimelineTiming(
      state.clips,
      state.fps,
      captureAuthoringTransactionSnapshot().capabilities
    );
    const status = element("div", "vst-editor-status");
    const statusLabel = element("label", "");
    statusLabel.append(element("strong", "", enabled ? "Enabled" : "Disabled"));
    const toggle = buildToggleSwitch({
      checked: enabled,
      title: enabled ? "Disable VideoStages" : "Enable VideoStages",
      onChange: navigation.setEnabled
    });
    const input2 = toggle.querySelector("input");
    if (input2) {
      statusLabel.htmlFor = input2.id;
      input2.dataset.testid = "toggle-enabled";
      input2.setAttribute("role", "switch");
      input2.setAttribute("aria-checked", String(enabled));
      input2.setAttribute(
        "aria-describedby",
        "vst-editor-disabled-description"
      );
    }
    status.append(statusLabel, toggle);
    const dimensions = actionButton({
      key: "output-settings",
      label: `${state.width}×${state.height} · ${state.fps} fps`,
      run: () => navigation.jump("output")
    });
    dimensions.title = "Edit output dimensions and frame rate";
    const duration = element(
      "span",
      "vst-editor-output-duration",
      `${formatSecondsTenth(timing.outputSeconds)} output`
    );
    duration.dataset.testid = "output-duration";
    duration.title = "Final output length, accounting for joins and skipped clips";
    const collapse = actionButton({
      key: "toggle-map",
      label: collapsed ? "»" : "«",
      run: navigation.toggleMap
    });
    collapse.classList.add("vst-editor-collapse");
    collapse.setAttribute(
      "aria-label",
      collapsed ? "Expand clip map" : "Collapse clip map"
    );
    collapse.setAttribute("aria-expanded", String(!collapsed));
    collapse.setAttribute("aria-controls", "vst-editor-sequence");
    status.append(collapse);
    dimensions.classList.add("vst-editor-dimensions", "btn-primary");
    const dimensionText = dimensions.textContent || "";
    dimensions.textContent = "";
    const compactDimensions = element("span", "vst-editor-dimensions-compact");
    compactDimensions.append(
      element("span", "", `${state.width}×${state.height}`),
      element("span", "", `${state.fps} fps`)
    );
    dimensions.append(
      element("span", "vst-editor-dimensions-label", dimensionText),
      compactDimensions
    );
    dimensions.setAttribute("aria-label", `Output settings: ${dimensionText}`);
    output.append(status, dimensions);
    const description = element(
      "span",
      "visually-hidden",
      enabled ? "VideoStages is enabled." : "VideoStages disabled. Use the switch above to enable. Editing is still available."
    );
    description.id = "vst-editor-disabled-description";
    const sequence = element("div", "vst-editor-sequence");
    sequence.id = "vst-editor-sequence";
    sequence.dataset.testid = "clip-sequence";
    sequence.setAttribute("aria-label", "Video clips");
    strip.append(output, description, sequence);
    state.clips.forEach((clip, index) => {
      const relationship = clipRelationship(state.clips, index);
      const sourceUsage = relationship === SOURCE_VIDEO_RELATIONSHIP ? "video" : relationship === SOURCE_AUDIO_RELATIONSHIP ? "audio" : "";
      const sourceDescription = sourceUsage ? `Source ${sourceUsage} for Clip ${index + 1}; this clip does not extend the final video` : "";
      const card = element("div", "vst-editor-clip");
      card.dataset.clipId = clip.id;
      card.dataset.clipIndex = String(index);
      card.dataset.testid = "clip-card";
      card.classList.toggle("selected", index === selected);
      card.dataset.sourceUsage = sourceUsage;
      card.dataset.mediaKind = clip.audioOnly ? "audio" : "video";
      card.title = [
        sourceDescription,
        index > 0 ? "Shift-click to delete this clip" : "The first clip cannot be deleted"
      ].filter(Boolean).join(". ");
      card.addEventListener(
        "mousedown",
        (event) => {
          if (event.shiftKey && event.button === 0)
            event.preventDefault();
        },
        { capture: true }
      );
      card.addEventListener(
        "click",
        (event) => {
          if (event.shiftKey) {
            event.preventDefault();
            event.stopPropagation();
            navigation.deleteClip(index);
          } else if (!event.target.closest("button")) {
            navigation.selectClip(index);
          }
        },
        { capture: true }
      );
      const header = element("div", "vst-editor-clip-heading");
      const role = clip.skipped ? " · skipped" : sourceUsage ? ` · ${sourceUsage} source` : timing.consumedClipIndexes.includes(index) ? " · input only" : clip.audioOnly ? " · audio only" : "";
      const choose = actionButton({
        key: "select-clip",
        label: String(index),
        run: () => navigation.selectClip(index)
      });
      choose.setAttribute("aria-current", String(index === selected));
      choose.setAttribute("aria-label", `Select Clip ${index}${role}`);
      choose.textContent = "";
      choose.append(element("span", "vst-editor-clip-index", String(index)));
      if (role)
        header.append(
          element("span", "vst-editor-clip-label", role.slice(3))
        );
      header.append(
        actionButton({
          key: "clip-stages",
          label: `${clip.stages.length} stage${clip.stages.length === 1 ? "" : "s"}`,
          run: () => navigation.selectClip(index, "stages")
        }),
        element(
          "span",
          "vst-editor-duration",
          formatSecondsTenth(clip.duration)
        )
      );
      const prompt = actionButton({
        key: "clip-prompt",
        label: `Prompt · ${clip.prompt || "Using global prompt"}`,
        run: () => navigation.selectClip(index, "prompt", true)
      });
      prompt.classList.add("vst-editor-prompt-link");
      prompt.title = clip.prompt || "Using global prompt";
      prompt.setAttribute("aria-label", `Edit clip ${index} prompt`);
      if (index > 0) {
        const remove = actionButton({
          key: "delete-clip",
          label: "×",
          run: () => navigation.deleteClip(index)
        });
        remove.classList.add("vst-editor-delete-clip");
        remove.setAttribute("aria-label", `Delete Clip ${index}`);
        remove.title = `Delete Clip ${index} · Shift-click also deletes`;
        header.append(remove);
      }
      if (state.clips.length > 1) {
        const actions = [];
        if (index > 0) {
          actions.push(
            {
              key: `Delete Clip ${index}`,
              action: () => navigation.deleteClip(index)
            },
            {
              key: "Move earlier",
              action: () => navigation.moveClip(index, index - 1)
            }
          );
        }
        if (index < state.clips.length - 1) {
          actions.push({
            key: "Move later",
            action: () => navigation.moveClip(index, index + 1)
          });
        }
        const more = actionButton({
          key: "clip-actions",
          label: "…",
          run: () => showHostActionMenu(more, actions)
        });
        more.classList.add("vst-editor-clip-actions");
        more.setAttribute("aria-label", `Actions for Clip ${index}`);
        more.setAttribute("aria-haspopup", "true");
        more.title = `Clip ${index} actions`;
        card.append(more);
      }
      const media = element(
        "small",
        "vst-editor-clip-media",
        sourceUsage ? `${sourceUsage} source` : clip.audioOnly ? "audio only" : clip.skipped ? "skipped" : "video"
      );
      card.append(choose, header, prompt, media);
      sequence.append(card);
      if (index < state.clips.length - 1) {
        const nextClipIdx = sourceUsage ? index + 1 : executableBoundaryForLeftClip(state.clips, index)?.rightIdx ?? index + 1;
        const join = actionButton({
          key: "edit-join",
          label: `↓ ${sourceUsage ? `Source ${sourceUsage}` : relationship} ↓`,
          run: () => navigation.selectClip(nextClipIdx, "join")
        });
        join.dataset.leftClipId = clip.id;
        join.dataset.sourceUsage = sourceUsage;
        join.dataset.mediaKind = sourceUsage;
        join.title = sourceDescription || "Edit how these clips join";
        join.classList.add("vst-editor-join");
        join.textContent = "";
        const joinLabel = element("span", "vst-editor-join-label");
        joinLabel.append(
          element("span", "vst-editor-join-arrow", "↓ "),
          element(
            "span",
            "",
            sourceUsage ? `Source ${sourceUsage}` : relationship
          ),
          element("span", "vst-editor-join-arrow", " ↓")
        );
        join.append(joinLabel);
        if (sourceUsage) {
          join.append(
            element("span", "vst-editor-join-hint", " Feeds next")
          );
          join.setAttribute(
            "aria-label",
            `Source ${sourceUsage}; feeds Clip ${index + 1}; excluded from output`
          );
        }
        sequence.append(join);
      }
    });
    sequence.append(
      actionButton({
        key: "add-clip",
        label: "+ Add clip",
        run: navigation.addClip
      })
    );
    const footer = element("footer", "vst-editor-result");
    footer.dataset.testid = "flow-output";
    const included = timing.outputClipIndexes.map((index) => `Clip ${index}`);
    footer.append(
      duration,
      element(
        "span",
        "vst-editor-result-clips",
        included.join(" + ") || "No output clips"
      )
    );
    strip.append(footer);
    return strip;
  }

  // frontend/clipEditor/navigation.ts
  function updateStickyItemLabels(pane) {
    const top = pane.getBoundingClientRect().top;
    for (const section of pane.querySelectorAll(
      ":scope > [data-section]"
    )) {
      const header = section.querySelector(
        ".vst-editor-section-heading"
      );
      const label = header?.querySelector(
        ".vst-editor-current-item"
      );
      if (!header || !label) continue;
      const bounds = header.getBoundingClientRect();
      let current = "";
      if (section.dataset.section === "stages" && bounds.top <= top + 1 && bounds.bottom > top) {
        for (const item of section.querySelectorAll(
          ".vst-editor-item[data-item-id]"
        )) {
          const title = item.querySelector(
            ".vst-editor-item-heading h4"
          );
          if (title && title.getBoundingClientRect().bottom <= bounds.bottom && item.getBoundingClientRect().bottom > bounds.bottom) {
            current = `· ${item.dataset.itemLabel || title.textContent}`;
            break;
          }
        }
      }
      if (label.textContent !== current) label.textContent = current;
      label.hidden = !current;
    }
  }
  function sectionAtTop(pane) {
    const top = pane.getBoundingClientRect().top + 12;
    const panels = [
      ...pane.querySelectorAll(":scope > [data-section]")
    ];
    return (panels.filter((panel) => panel.getBoundingClientRect().top <= top).at(-1) || panels[0])?.dataset.section || null;
  }
  function jumpToSection(pane, key) {
    const panel = [
      ...pane.querySelectorAll(":scope > [data-section]")
    ].find((node) => node.dataset.section === key);
    if (panel)
      pane.scrollTop += panel.getBoundingClientRect().top - pane.getBoundingClientRect().top;
  }
  function markNavigation(nav, key) {
    for (const link of nav.querySelectorAll(
      "[data-section-link]"
    )) {
      const active = link.dataset.sectionLink === key;
      link.classList.toggle("active", active);
      if (active) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    }
  }
  function revealSelectedItem(pane, selection) {
    const item = Array.from(
      pane.querySelectorAll("[data-selection]")
    ).find((node) => node.dataset.selection === JSON.stringify(selection));
    if (!item) return;
    const header = item.closest("[data-section]")?.querySelector(".vst-editor-section-heading");
    const top = pane.getBoundingClientRect().top + (header?.getBoundingClientRect().height || 0);
    const bounds = item.getBoundingClientRect();
    if (bounds.top < top || bounds.bottom > pane.getBoundingClientRect().bottom)
      pane.scrollTop += bounds.top - top;
  }
  function revealSelectedClip(root) {
    const sequence = root.querySelector(".vst-editor-sequence");
    const selected = sequence?.querySelector(
      ".vst-editor-clip.selected"
    );
    if (!sequence || !selected) return;
    const area = sequence.getBoundingClientRect();
    const bounds = selected.getBoundingClientRect();
    if (bounds.top < area.top) sequence.scrollTop += bounds.top - area.top;
    else if (bounds.bottom > area.bottom)
      sequence.scrollTop += bounds.bottom - area.bottom;
  }

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
  var createIcLoraEditor = (context, clip, clipIdx, defaults) => {
    const authoring = context.authoring();
    const buildEditor = (editorEntryIdx) => {
      const entry = clip.icLoras[editorEntryIdx];
      if (!entry) {
        return void 0;
      }
      const entryIdx = editorEntryIdx;
      const col = document.createElement("div");
      col.className = "vst-detail-fields vst-detail-col vst-detail-instance-fields vst-detail-iclora vst-detail-iclora-col";
      col.setAttribute("data-vst-iclora-idx", `${entryIdx}`);
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
              const target = entryAt(clips, entryIdx);
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
                    stage.icLoraStrengths[entryIdx] = normalizeStageControlNetStrengthValue(
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
              if (driveData && !isAcceptedIcLoraDriveMedia(
                driveData,
                target.driveMedia?.fileName ?? null,
                target.driveMediaKinds
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
                const target = entryAt(clips, entryIdx);
                if (target) {
                  target.lora = value;
                  const initialStrength = defaultLoraWeight(
                    defaults,
                    value
                  );
                  const targetClip = clips[clipIdx];
                  for (const stage of targetClip?.stages ?? []) {
                    stage.icLoraStrengths[entryIdx] = normalizeStageControlNetStrengthValue(
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
          key: `iclora-${entryIdx}-strength`,
          value: entry.strength,
          min: IC_LORA_STRENGTH_MIN,
          max: IC_LORA_STRENGTH_MAX,
          step: IC_LORA_STRENGTH_STEP,
          readBack: (clips) => entryAt(clips, entryIdx)?.strength ?? null,
          mutate: (clips, value) => {
            const target = entryAt(clips, entryIdx);
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
            key: `iclora-${entryIdx}-attention`,
            value: entry.attentionStrength,
            min: IC_LORA_ATTENTION_MIN,
            max: IC_LORA_ATTENTION_MAX,
            step: IC_LORA_ATTENTION_STEP,
            readBack: (clips) => entryAt(clips, entryIdx)?.attentionStrength ?? null,
            mutate: (clips, value) => {
              const target = entryAt(clips, entryIdx);
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
                const target = entryAt(clips, entryIdx);
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
              const target = entryAt(clips, entryIdx);
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
                const target = entryAt(clips, entryIdx);
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
                if (data && !isAcceptedIcLoraDriveMedia(
                  data,
                  target.driveMedia?.fileName ?? null,
                  target.driveMediaKinds
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
                const target = entryAt(clips, entryIdx);
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
                  const target = entryAt(clips, entryIdx);
                  if (target) {
                    target.driveMedia = { data, fileName };
                  }
                });
                context.render();
              },
              () => {
                context.commit((clips) => {
                  const target = entryAt(clips, entryIdx);
                  if (target) {
                    target.driveMedia = null;
                  }
                });
                context.render();
              },
              entry.driveMedia?.data,
              audioDriveMedia ? {
                kind: "audio",
                range: { startSeconds: 0, lengthSeconds: 0 }
              } : void 0
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
    return { buildEditor };
  };
  var buildIcLoraFields = (context, clip, clipIdx, defaults, entryIdx) => {
    const fields = createIcLoraEditor(
      context,
      clip,
      clipIdx,
      defaults
    ).buildEditor(entryIdx);
    if (!fields) {
      throw new Error(`IC-LoRA ${entryIdx} does not exist`);
    }
    return fields;
  };

  // frontend/architectures/authoringPanels.ts
  var buildPersistedIcLoraFields = (entry, index) => {
    const content = document.createElement("div");
    content.className = "vst-detail-fields vst-detail-col vst-detail-iclora-col";
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "This architecture has no IC-LoRA editor. Existing entries remain available for removal.";
    content.appendChild(note);
    const label = document.createElement("span");
    label.textContent = entry.lora || `IC-LoRA ${index}`;
    content.appendChild(label);
    return content;
  };
  var buildArchitectureIcLoraFields = (context, clip, clipIdx, defaults, entryIdx) => {
    const entry = clip.icLoras[entryIdx];
    if (!entry) {
      throw new Error(`IC-LoRA ${entryIdx} does not exist`);
    }
    const architectureId = context.authoring().capabilities.forClip(clip).architectureId;
    return architectureId === LTX2_ARCHITECTURE_ID ? buildIcLoraFields(context, clip, clipIdx, defaults, entryIdx) : buildPersistedIcLoraFields(entry, entryIdx);
  };

  // frontend/architectures/ltx2/icLoraEntries.ts
  var appendDefaultIcLora = (clip, defaults) => {
    const preset = findIcLoraPreset(IC_LORA_DEFAULT_PRESET_ID);
    if (!preset) {
      return false;
    }
    const contract = icLoraDriveMediaContract(preset);
    clip.icLoras.push(
      defaultIcLora({
        lora: IC_LORA_AUTO,
        preset: preset.id,
        driveSource: MEDIA_SOURCE_INCOMING,
        strength: preset.strength,
        controlType: preset.controlType,
        driveData: contract.driveData,
        driveMediaKinds: [...contract.acceptedKinds]
      })
    );
    appendIcLoraStrengthToClip(clip, defaultLoraWeight(defaults, IC_LORA_AUTO));
    return true;
  };
  var removeIcLoraEntryAt = (clip, index) => {
    if (!clip.icLoras[index]) {
      return false;
    }
    clip.icLoras.splice(index, 1);
    removeIcLoraStrengthAt(clip, index);
    return true;
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
      claim: (currentRevision) => {
        const current = currentRevision === revisionAtStart && currentOperations.get(key) === operationId;
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

  // frontend/mediaProbe.ts
  var initVideoFromProbe = (probe, data, fileName, clipDuration) => {
    const durationSeconds = roundToTenth(probe?.durationSeconds ?? 0);
    return {
      source: MEDIA_SOURCE_UPLOAD,
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
    video.src = mediaPreviewSrc(src);
  });
  var probeInitVideo = (src, timeoutMs = 8e3) => withProbedMedia(
    src,
    timeoutMs,
    null,
    (video, durationSeconds, finish2) => {
      const dimensions = {
        width: video.videoWidth > 0 ? video.videoWidth : null,
        height: video.videoHeight > 0 ? video.videoHeight : null
      };
      const requestFrame = video.requestVideoFrameCallback?.bind(video);
      if (!requestFrame) {
        finish2({ durationSeconds, fps: null, ...dimensions });
        return;
      }
      const mediaTimes = [];
      const step = (_now, metadata) => {
        mediaTimes.push(metadata.mediaTime);
        if (mediaTimes.length >= FPS_SAMPLE_FRAMES || metadata.mediaTime >= durationSeconds) {
          finish2({
            durationSeconds,
            fps: estimateFpsFromMediaTimes(mediaTimes),
            ...dimensions
          });
          return;
        }
        requestFrame(step);
      };
      requestFrame(step);
      video.play()?.catch(
        () => finish2({ durationSeconds, fps: null, ...dimensions })
      );
    }
  );
  var probeReferenceVideo = (src, timeoutMs = 8e3) => withProbedMedia(
    src,
    timeoutMs,
    null,
    (video, durationSeconds, finish2) => {
      const requestFrame = video.requestVideoFrameCallback?.bind(video);
      if (!requestFrame) {
        finish2({ durationSeconds, hasMultipleFrames: true });
        return;
      }
      let firstMediaTime = null;
      const inspect = (_now, metadata) => {
        if (firstMediaTime === null) {
          firstMediaTime = metadata.mediaTime;
          requestFrame(inspect);
          video.currentTime = Math.max(
            0,
            durationSeconds - Math.min(1e-3, durationSeconds / 2)
          );
          return;
        }
        finish2({
          durationSeconds,
          hasMultipleFrames: Math.abs(metadata.mediaTime - firstMediaTime) >= MIN_FRAME_DELTA_SECONDS
        });
      };
      requestFrame(inspect);
      video.play()?.catch(
        () => finish2({ durationSeconds, hasMultipleFrames: true })
      );
    }
  );
  var probeMediaDurationSeconds = (src, timeoutMs = 8e3) => withProbedMedia(
    src,
    timeoutMs,
    0,
    (_video, duration, finish2) => finish2(duration)
  );

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
    const button2 = document.createElement("button");
    button2.type = "button";
    button2.className = `interrupt-button vst-btn-tiny vst-capability-repair ${action.className ?? ""}`.trim();
    button2.textContent = action.label;
    button2.title = action.title ?? action.label;
    button2.setAttribute("aria-label", button2.title);
    button2.addEventListener("click", action.onRepair);
    return button2;
  };
  var disableCapabilityControls = (root, decision, removableSelectors = []) => {
    const removable = new Set(
      removableSelectors.flatMap(
        (selector) => Array.from(root.querySelectorAll(selector))
      )
    );
    for (const control of root.querySelectorAll("input, select, textarea, button")) {
      if (removable.has(control) || [...removable].some((element2) => element2.contains(control))) {
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

  // frontend/detailStrip/trimBar.ts
  var NUDGE_SECONDS = 0.1;
  var COARSE_NUDGE_SECONDS = 1;
  var pointerSecondsAt = (clientX, trackLeft, trackWidth, limitSeconds) => {
    if (!(trackWidth > 0) || !(limitSeconds > 0)) {
      return 0;
    }
    const ratio = (clientX - trackLeft) / trackWidth;
    return Math.min(limitSeconds, Math.max(0, ratio * limitSeconds));
  };
  var trimBarGeometry = (range, limitSeconds) => {
    if (!(limitSeconds > 0)) {
      return { leftPct: 0, widthPct: 100 };
    }
    const { inSeconds, outSeconds } = toInOut(range);
    const leftPct = Math.min(
      100,
      Math.max(0, inSeconds / limitSeconds * 100)
    );
    const rightPct = Math.min(
      100,
      Math.max(0, outSeconds / limitSeconds * 100)
    );
    return { leftPct, widthPct: Math.max(0, rightPct - leftPct) };
  };
  var applyGrip = (grip, range, seconds2, limits) => {
    if (grip === "in") {
      return setInPoint(range, seconds2, limits);
    }
    if (grip === "out") {
      return setOutPoint(range, seconds2, limits);
    }
    return slideRange(range, seconds2, limits);
  };
  var buildTrimBar = (spec) => {
    let range = spec.range;
    const { limits } = spec;
    const wrap = document.createElement("div");
    wrap.className = "vst-trim";
    const track = document.createElement("div");
    track.className = "vst-trim-track";
    wrap.appendChild(track);
    const window_ = document.createElement("div");
    window_.className = "vst-trim-window";
    window_.setAttribute("role", "slider");
    window_.setAttribute("aria-label", "Trimmed range position");
    window_.tabIndex = 0;
    track.appendChild(window_);
    const grips = {
      in: document.createElement("span"),
      out: document.createElement("span")
    };
    for (const edge of ["in", "out"]) {
      const grip = grips[edge];
      grip.className = `vst-trim-grip vst-trim-grip-${edge}`;
      grip.dataset.vstTrimGrip = edge;
      grip.setAttribute("role", "slider");
      grip.setAttribute(
        "aria-label",
        edge === "in" ? "In point" : "Out point"
      );
      grip.tabIndex = 0;
      track.appendChild(grip);
    }
    const scale = document.createElement("div");
    scale.className = "vst-trim-scale";
    const scaleStart = document.createElement("span");
    scaleStart.textContent = "0.0";
    const scaleEnd = document.createElement("span");
    scaleEnd.textContent = `${limits.limitSeconds.toFixed(1)}`;
    scale.append(scaleStart, scaleEnd);
    wrap.appendChild(scale);
    const paint = () => {
      const { leftPct, widthPct } = trimBarGeometry(
        range,
        limits.limitSeconds
      );
      window_.style.left = `${leftPct}%`;
      window_.style.width = `${widthPct}%`;
      grips.in.style.left = `${leftPct}%`;
      grips.out.style.left = `${leftPct + widthPct}%`;
      const { inSeconds, outSeconds } = toInOut(range);
      const describe = (element2, now, max, text2) => {
        element2.setAttribute("aria-valuemin", "0");
        element2.setAttribute("aria-valuemax", `${max}`);
        element2.setAttribute("aria-valuenow", `${now}`);
        element2.setAttribute("aria-valuetext", text2);
      };
      describe(
        grips.in,
        inSeconds,
        limits.limitSeconds,
        `In point, ${inSeconds.toFixed(1)} seconds`
      );
      describe(
        grips.out,
        outSeconds,
        limits.limitSeconds,
        `Out point, ${outSeconds.toFixed(1)} seconds`
      );
      describe(
        window_,
        inSeconds,
        limits.limitSeconds,
        `Trimmed range, ${inSeconds.toFixed(1)} to ${outSeconds.toFixed(1)} seconds`
      );
      grips.in.title = `In point — ${inSeconds.toFixed(1)} s`;
      grips.out.title = `Out point — ${outSeconds.toFixed(1)} s`;
      window_.title = "Drag to slide the range without changing its length";
    };
    const push = (next) => {
      if (next.startSeconds === range.startSeconds && next.lengthSeconds === range.lengthSeconds) {
        return;
      }
      range = next;
      paint();
      spec.onChange(next);
    };
    const startDrag = (grip, event) => {
      if (!(limits.limitSeconds > 0) || event.button !== 0) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      const target = event.currentTarget;
      target.setPointerCapture?.(event.pointerId);
      const rect = track.getBoundingClientRect();
      const pressed = toInOut(range);
      const pressSeconds = pointerSecondsAt(
        event.clientX,
        rect.left,
        rect.width,
        limits.limitSeconds
      );
      const grabOffset = pressSeconds - pressed.inSeconds;
      const before = range;
      const secondsFor = (moveEvent) => {
        const at = pointerSecondsAt(
          moveEvent.clientX,
          rect.left,
          rect.width,
          limits.limitSeconds
        );
        return grip === "window" ? at - grabOffset : at;
      };
      const onMove = (moveEvent) => {
        push(applyGrip(grip, range, secondsFor(moveEvent), limits));
      };
      const finish2 = (commit, moveEvent) => {
        target.removeEventListener("pointermove", onMove);
        target.removeEventListener("pointerup", onUp);
        target.removeEventListener("pointercancel", onCancel);
        document.removeEventListener("keydown", onKey, true);
        target.releasePointerCapture?.(event.pointerId);
        push(
          commit && moveEvent ? applyGrip(grip, range, secondsFor(moveEvent), limits) : before
        );
      };
      const onUp = (upEvent) => finish2(true, upEvent);
      const onCancel = () => finish2(false);
      const onKey = (keyEvent) => {
        if (keyEvent.key === "Escape") {
          keyEvent.preventDefault();
          finish2(false);
        }
      };
      target.addEventListener("pointermove", onMove);
      target.addEventListener("pointerup", onUp);
      target.addEventListener("pointercancel", onCancel);
      document.addEventListener("keydown", onKey, true);
    };
    const onKeyGrip = (grip, event) => {
      const step = event.shiftKey ? COARSE_NUDGE_SECONDS : NUDGE_SECONDS;
      const { inSeconds, outSeconds } = toInOut(range);
      const at = grip === "out" ? outSeconds : inSeconds;
      let next = null;
      if (event.key === "ArrowLeft") {
        next = at - step;
      } else if (event.key === "ArrowRight") {
        next = at + step;
      } else if (event.key === "Home") {
        next = 0;
      } else if (event.key === "End") {
        next = limits.limitSeconds;
      }
      if (next === null) {
        return;
      }
      event.preventDefault();
      push(applyGrip(grip, range, next, limits));
    };
    for (const edge of ["in", "out"]) {
      grips[edge].addEventListener(
        "pointerdown",
        (event) => startDrag(edge, event)
      );
      grips[edge].addEventListener(
        "keydown",
        (event) => onKeyGrip(edge, event)
      );
    }
    window_.addEventListener(
      "pointerdown",
      (event) => startDrag("window", event)
    );
    window_.addEventListener("keydown", (event) => onKeyGrip("window", event));
    track.addEventListener("pointerdown", (event) => {
      if (event.target !== track || !(limits.limitSeconds > 0)) {
        return;
      }
      const rect = track.getBoundingClientRect();
      const at = pointerSecondsAt(
        event.clientX,
        rect.left,
        rect.width,
        limits.limitSeconds
      );
      const { inSeconds, outSeconds } = toInOut(range);
      push(
        applyGrip(
          Math.abs(at - inSeconds) <= Math.abs(at - outSeconds) ? "in" : "out",
          range,
          at,
          limits
        )
      );
    });
    paint();
    return {
      element: wrap,
      sync: (next) => {
        range = next;
        paint();
      }
    };
  };

  // frontend/detailStrip/trimModal.ts
  var MODAL_CLASS = "vst-trim-modal";
  var BACKDROP_CLASS = "vst-trim-modal-backdrop";
  var TITLE_ID = "vst_trim_modal_title";
  var currentModal2 = null;
  var seconds = (value) => value.toFixed(1);
  var button = (label, title, dataAttribute) => {
    const element2 = document.createElement("button");
    element2.type = "button";
    element2.className = "basic-button small-button";
    element2.textContent = label;
    element2.title = title;
    if (dataAttribute) {
      element2.setAttribute(dataAttribute, "");
    }
    return element2;
  };
  var numberField = (labelText, name, readOnly = false) => {
    const wrap = document.createElement("label");
    wrap.className = "vst-trim-modal-field";
    const label = document.createElement("span");
    label.textContent = labelText;
    const input2 = document.createElement("input");
    input2.type = "number";
    input2.step = "0.1";
    input2.dataset.vstTrimField = name;
    input2.readOnly = readOnly;
    wrap.append(label, input2);
    return { wrap, input: input2 };
  };
  var buildTrimLauncher = (summary, onOpen) => {
    const row = document.createElement("div");
    row.className = "vst-trim-launcher";
    const text2 = document.createElement("span");
    text2.className = "vst-trim-launcher-summary vst-detail-field-hint";
    text2.textContent = summary;
    const open = button("Edit…", "Open the range editor");
    open.setAttribute("data-vst-open-trim", "");
    open.addEventListener("click", onOpen);
    row.append(text2, open);
    return row;
  };
  var closeTrimModal = () => {
    currentModal2?.close();
  };
  var openTrimModal = (spec) => {
    closeTrimModal();
    const isAudio = spec.mediaKind === "audio";
    let draft = { ...spec.range };
    let bar = null;
    let stopAt = null;
    const heading = document.createElement("div");
    const title = document.createElement("h5");
    title.className = "modal-title";
    title.id = TITLE_ID;
    title.textContent = spec.title;
    const fileName = document.createElement("small");
    fileName.className = "vst-trim-modal-file";
    fileName.textContent = spec.fileName;
    heading.append(title, fileName);
    const close = button("×", "Close without applying");
    close.setAttribute("aria-label", close.title);
    const playerWrap = document.createElement("div");
    playerWrap.className = "vst-trim-modal-player-wrap";
    if (isAudio) {
      playerWrap.classList.add("vst-trim-modal-player-wrap-audio");
    }
    const player = isAudio ? document.createElement("audio") : getVideoStagesHostBridge().createInitVideoElement();
    player.className = "vst-trim-modal-player";
    player.controls = true;
    player.preload = "auto";
    if (!isAudio) {
      player.setAttribute("playsinline", "");
    }
    if (spec.dataUri) {
      player.src = mediaPreviewSrc(spec.dataUri);
    }
    player.currentTime = draft.startSeconds;
    const mediaError = document.createElement("div");
    mediaError.className = "vst-trim-modal-media-error";
    mediaError.textContent = `This browser cannot preview the selected ${spec.mediaKind}.`;
    mediaError.hidden = spec.dataUri !== null;
    player.hidden = spec.dataUri === null;
    playerWrap.append(player, mediaError);
    const transport = document.createElement("div");
    transport.className = "vst-trim-modal-transport";
    const timecode = document.createElement("output");
    timecode.className = "vst-trim-modal-timecode";
    const stepLabel = isAudio ? "0.1 s" : "Frame";
    const previousFrame = button(
      `◀ ${stepLabel}`,
      isAudio ? "Step backward 0.1 seconds" : "Step backward one frame"
    );
    const preview = button("▶ Preview range", "Play the selected range");
    const nextFrame = button(
      `${stepLabel} ▶`,
      isAudio ? "Step forward 0.1 seconds" : "Step forward one frame"
    );
    const markIn = button("Mark In", "Set In at the playhead (I)");
    const markOut = button("Mark Out", "Set Out at the playhead (O)");
    transport.append(
      timecode,
      previousFrame,
      preview,
      nextFrame,
      markIn,
      markOut
    );
    const fields = document.createElement("div");
    fields.className = "vst-trim-modal-fields";
    const inField = numberField("In (s)", "in");
    const outField = numberField("Out (s)", "out");
    const durationField = numberField("Duration (s)", "duration", true);
    inField.input.min = "0";
    inField.input.max = `${spec.limits.limitSeconds}`;
    outField.input.min = `${spec.limits.minLengthSeconds}`;
    outField.input.max = `${spec.limits.limitSeconds}`;
    fields.append(inField.wrap, outField.wrap, durationField.wrap);
    const impact = document.createElement("small");
    impact.className = "vst-trim-modal-impact";
    const renderDraft = (syncBar = true) => {
      const range = toInOut(draft);
      inField.input.value = seconds(range.inSeconds);
      outField.input.value = seconds(range.outSeconds);
      durationField.input.value = seconds(draft.lengthSeconds);
      impact.textContent = spec.impactText(draft);
      if (syncBar) {
        bar?.sync(draft);
      }
    };
    const setDraft = (next, syncBar = true) => {
      draft = next;
      renderDraft(syncBar);
    };
    bar = buildTrimBar({
      range: draft,
      limits: spec.limits,
      onChange: (next) => {
        const before = toInOut(draft);
        const after = toInOut(next);
        setDraft(next, false);
        if (after.inSeconds !== before.inSeconds && after.outSeconds === before.outSeconds) {
          seek(after.inSeconds);
        } else if (after.outSeconds !== before.outSeconds && after.inSeconds === before.inSeconds) {
          seek(after.outSeconds);
        }
      }
    });
    const playhead = document.createElement("span");
    playhead.className = "vst-trim-playhead";
    playhead.setAttribute("aria-hidden", "true");
    bar.element.querySelector(".vst-trim-track")?.appendChild(playhead);
    const paintPlayhead = () => {
      const at = Math.min(
        spec.limits.limitSeconds,
        Math.max(0, player.currentTime || 0)
      );
      timecode.textContent = `${seconds(at)} / ${seconds(spec.limits.limitSeconds)} s`;
      const percent = spec.limits.limitSeconds > 0 ? at / spec.limits.limitSeconds * 100 : 0;
      playhead.style.left = `${percent}%`;
      if (stopAt !== null && at >= stopAt) {
        player.pause();
        stopAt = null;
        player.currentTime = draft.startSeconds;
        paintPlayhead();
      }
    };
    const seek = (value) => {
      player.currentTime = Math.min(
        spec.limits.limitSeconds,
        Math.max(0, value)
      );
      paintPlayhead();
    };
    const playRange = () => {
      const range = toInOut(draft);
      stopAt = range.outSeconds;
      seek(range.inSeconds);
      player.play()?.catch(() => {
        stopAt = null;
      });
    };
    const readNumber = (input2, apply2) => {
      const value = Number(input2.value);
      if (Number.isFinite(value)) {
        setDraft(apply2(value));
      }
    };
    inField.input.addEventListener(
      "input",
      () => readNumber(
        inField.input,
        (value) => setInPoint(draft, value, spec.limits)
      )
    );
    outField.input.addEventListener(
      "input",
      () => readNumber(
        outField.input,
        (value) => setOutPoint(draft, value, spec.limits)
      )
    );
    const reset = button(
      `Use full ${spec.mediaKind}`,
      "Reset In and Out to the whole source",
      "data-vst-trim-reset"
    );
    reset.addEventListener(
      "click",
      () => setDraft({
        startSeconds: 0,
        lengthSeconds: spec.limits.limitSeconds
      })
    );
    previousFrame.addEventListener(
      "click",
      () => seek(
        player.currentTime - (spec.limits.fps > 0 ? 1 / spec.limits.fps : 0.1)
      )
    );
    nextFrame.addEventListener(
      "click",
      () => seek(
        player.currentTime + (spec.limits.fps > 0 ? 1 / spec.limits.fps : 0.1)
      )
    );
    preview.addEventListener("click", playRange);
    markIn.addEventListener(
      "click",
      () => setDraft(markInPoint(draft, player.currentTime, spec.limits))
    );
    markOut.addEventListener(
      "click",
      () => setDraft(setOutPoint(draft, player.currentTime, spec.limits))
    );
    const footer = document.createElement("div");
    footer.className = "modal-footer vst-trim-modal-footer";
    const cancel = button("Cancel", "Close without applying");
    const apply = button("Apply", "Apply this range", "data-vst-trim-apply");
    apply.classList.add("vst-trim-modal-apply");
    footer.append(reset, cancel, apply);
    let managed;
    managed = createManagedModal({
      modalClass: MODAL_CLASS,
      backdropClass: BACKDROP_CLASS,
      labelledBy: TITLE_ID,
      onKeyDown: (event) => {
        if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement) {
          return;
        }
        const key = event.key.toLowerCase();
        if (key === "i") {
          event.preventDefault();
          setDraft(markInPoint(draft, player.currentTime, spec.limits));
        } else if (key === "o") {
          event.preventDefault();
          setDraft(setOutPoint(draft, player.currentTime, spec.limits));
        } else if (event.key === " ") {
          event.preventDefault();
          if (player.paused) {
            playRange();
          } else {
            player.pause();
            stopAt = null;
          }
        }
      },
      onClose: () => {
        player.removeEventListener("timeupdate", paintPlayhead);
        player.removeEventListener("seeked", paintPlayhead);
        player.pause();
        player.removeAttribute("src");
        player.load();
        if (currentModal2 === managed) {
          currentModal2 = null;
        }
      }
    });
    currentModal2 = managed;
    managed.header.append(heading, close);
    managed.body.append(playerWrap, transport, bar.element, fields, impact);
    managed.content.appendChild(footer);
    const dismiss = () => managed.close();
    const applyAndClose = () => {
      const applied = { ...draft };
      dismiss();
      spec.onApply(applied);
    };
    close.addEventListener("click", dismiss);
    cancel.addEventListener("click", dismiss);
    apply.addEventListener("click", applyAndClose);
    player.addEventListener("loadedmetadata", () => seek(draft.startSeconds));
    player.addEventListener("timeupdate", paintPlayhead);
    player.addEventListener("seeked", paintPlayhead);
    player.addEventListener("error", () => {
      player.hidden = true;
      mediaError.hidden = false;
    });
    renderDraft();
    paintPlayhead();
    managed.open(player);
  };

  // frontend/detailStrip/audioPanel.ts
  var buildBaseAudioFields = (ctx, clipIdx, state) => {
    const clips = state.clips;
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
    const usesAudioOnlyLead = clipIdx === 1 && clips[0]?.audioOnly === true;
    const options = usesAudioOnlyLead ? [{ value: AUDIO_SOURCE_PREVIOUS_CLIP, label: "Clip 0 audio" }] : buildAudioSourceOptions(clip.audioSource ?? "", {
      controlNetEnabled: capabilityView.audioSourceKinds.includes(
        AUDIO_SOURCE_CONTROLNET
      ),
      previousClipEnabled: clipIdx > 0,
      allowedKinds: capabilityView.audioSourceKinds
    });
    const source = usesAudioOnlyLead ? AUDIO_SOURCE_PREVIOUS_CLIP : options.find((option) => option.value === clip.audioSource)?.value ?? clip.audioSource ?? "";
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
        if (nextSource !== AUDIO_SOURCE_UPLOAD) {
          target.uploadedAudioDurationSeconds = 0;
          target.uploadedAudioStartSeconds = 0;
          target.uploadedAudioLengthSeconds = 0;
        }
      });
    };
    const base = document.createElement("div");
    base.className = "vst-detail-fields vst-detail-col vst-detail-audio";
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
    select2.disabled = usesAudioOnlyLead;
    const sourceField = buildField(
      "Audio Source",
      select2,
      void 0,
      usesAudioOnlyLead ? "Clip 0 is audio-only, so its output is always Clip 1's base audio." : "Where this clip's audio comes from: generated from the prompt, an uploaded file, or a connected generated-audio source."
    );
    sourceField.classList.toggle("vst-field-disabled", usesAudioOnlyLead);
    base.appendChild(sourceField);
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
    const saveRow = buildCheckbox(
      "Save audio separately",
      clip.saveAudioTrack === true && isAce,
      (value) => {
        commitAudio((c) => {
          c.saveAudioTrack = value;
        });
      },
      {
        disabled: !isAce,
        help: "Export the generated audio as its own output alongside the video. Only available for generated (AceStep) audio."
      }
    );
    base.appendChild(saveRow);
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
    if (source === AUDIO_SOURCE_UPLOAD) {
      const limitSeconds = clip.uploadedAudioDurationSeconds;
      const shown = clip.uploadedAudioLengthSeconds > 0 ? {
        startSeconds: clip.uploadedAudioStartSeconds,
        lengthSeconds: clip.uploadedAudioLengthSeconds
      } : { startSeconds: 0, lengthSeconds: limitSeconds };
      base.appendChild(
        buildMediaPickRow(
          "Audio Upload",
          "audio/*",
          ["audio"],
          clip.uploadedAudio?.fileName,
          (data, fileName) => {
            commitAudio((c) => {
              c.uploadedAudio = { data, fileName };
              c.uploadedAudioDurationSeconds = 0;
              c.uploadedAudioStartSeconds = 0;
              c.uploadedAudioLengthSeconds = 0;
            });
            ctx.render();
            if (clip.id) {
              runClipMediaProbe({
                clipId: clip.id,
                slot: "base-audio",
                probe: () => probeMediaDurationSeconds(data),
                apply: (target, duration) => {
                  if (target.uploadedAudio?.data !== data) {
                    return;
                  }
                  target.uploadedAudioDurationSeconds = roundToTenth(duration);
                },
                onApplied: () => ctx.render()
              });
            }
          },
          () => {
            commitAudio((c) => {
              c.uploadedAudio = null;
              c.uploadedAudioDurationSeconds = 0;
              c.uploadedAudioStartSeconds = 0;
              c.uploadedAudioLengthSeconds = 0;
            });
            ctx.render();
          },
          clip.uploadedAudio?.data,
          { kind: "audio", range: shown }
        )
      );
      if (clip.uploadedAudio) {
        if (limitSeconds > 0) {
          const range = toInOut(shown);
          base.appendChild(
            buildTrimLauncher(
              `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s · Uses ${shown.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
              () => openTrimModal({
                mediaKind: "audio",
                title: "Trim Base Audio",
                fileName: clip.uploadedAudio?.fileName ?? "Base audio",
                dataUri: clip.uploadedAudio?.data ?? null,
                range: shown,
                limits: {
                  limitSeconds,
                  minLengthSeconds: AUDIO_SPAN_MIN_LENGTH,
                  fps: 0
                },
                impactText: (next) => `Uses ${next.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
                onApply: (next) => {
                  const whole = next.startSeconds <= 0 && next.lengthSeconds >= limitSeconds;
                  commitAudio((target) => {
                    target.uploadedAudioStartSeconds = whole ? 0 : next.startSeconds;
                    target.uploadedAudioLengthSeconds = whole ? 0 : next.lengthSeconds;
                  });
                  ctx.render();
                }
              })
            )
          );
        }
      }
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
    return base;
  };

  // frontend/boundaryVocabulary.ts
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

  // frontend/detailStrip/boundaryPanel.ts
  var buildBoundaryFields = (ctx, sel, state) => {
    const clips = state.clips;
    const { leftClipIdx } = sel;
    const fields = document.createElement("div");
    fields.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const rightClip = clips[leftClipIdx + 1];
    const value = clip?.boundaryOut ?? "cut";
    const capabilities = ctx.authoring().capabilities;
    const capability = capabilities.forBoundaryIndex(clips, leftClipIdx);
    const seam = executableBoundaryForLeftClip(clips, leftClipIdx);
    const adjacent = !seam || seam.rightIdx === leftClipIdx + 1;
    const fps = Math.round(safeFps(state.fps));
    const overlapPolicy = capability.windowConstraints(value);
    const isReferenceContinue = value === "continue" && overlapPolicy.continueMode === "reference";
    const referenceTailLabel = clip?.boundaryOutReferenceIncludeSoundtrack === false ? "video tail" : "video and audio tail";
    const carryTargetHasStage = capability.rightClipIdx !== null && capabilities.forClip(clips[capability.rightClipIdx]).hasGenerationStage;
    const carryAudioSupported = clip !== void 0 && capabilities.forClip(clip).decision("audioBoundaryCarry").supported;
    const carryAudioActive = carryAudioSupported && !isReferenceContinue && clip?.boundaryOutCarryAudio === true && carryTargetHasStage;
    const sourceAudioAllowed = adjacent && leftClipIdx === 0 && rightClip !== void 0 && clip !== void 0 && capabilities.forClip(clip).hasGenerationStage && capabilities.forClip(clip).audioSourceKinds.includes(AUDIO_SOURCE_NATIVE);
    const detectedRelationship = clipRelationship(clips, leftClipIdx);
    const selectedRelationship = detectedRelationship === SOURCE_AUDIO_RELATIONSHIP && !sourceAudioAllowed ? value : detectedRelationship;
    const isJoin = selectedRelationship !== SOURCE_VIDEO_RELATIONSHIP && selectedRelationship !== SOURCE_AUDIO_RELATIONSHIP;
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
    if (adjacent && rightClip) {
      joinSpecs.push({
        value: SOURCE_VIDEO_RELATIONSHIP,
        label: "As source video"
      });
    }
    if (sourceAudioAllowed) {
      joinSpecs.push({
        value: SOURCE_AUDIO_RELATIONSHIP,
        label: "As source audio"
      });
    }
    const select2 = buildOptionSelect(
      joinSpecs,
      selectedRelationship,
      (next) => {
        ctx.structuralCommit((nextClips) => {
          const left = nextClips[leftClipIdx];
          if (!left || !nextClips[leftClipIdx + 1]) {
            return null;
          }
          const transaction = ctx.authoring();
          const current = clipRelationship(nextClips, leftClipIdx);
          if (next === SOURCE_VIDEO_RELATIONSHIP) {
            if (leftClipIdx === 0) {
              setAudioOnlyLead(
                nextClips,
                false,
                transaction,
                state.fps
              );
            }
            setPreviousClipVideoSource(
              nextClips,
              leftClipIdx + 1,
              true,
              transaction,
              state.fps
            );
          } else if (next === SOURCE_AUDIO_RELATIONSHIP && sourceAudioAllowed) {
            setPreviousClipVideoSource(
              nextClips,
              leftClipIdx + 1,
              false,
              transaction,
              state.fps
            );
            setAudioOnlyLead(nextClips, true, transaction, state.fps);
          } else {
            if (current === SOURCE_VIDEO_RELATIONSHIP) {
              setPreviousClipVideoSource(
                nextClips,
                leftClipIdx + 1,
                false,
                transaction,
                state.fps
              );
            }
            if (current === SOURCE_AUDIO_RELATIONSHIP && leftClipIdx === 0) {
              setAudioOnlyLead(
                nextClips,
                false,
                transaction,
                state.fps
              );
            }
            left.boundaryOut = next ?? "cut";
          }
          return "render";
        });
      }
    );
    fields.appendChild(
      buildField(
        "Join",
        select2,
        void 0,
        "How this clip joins the next one. Cut: hard concatenation. Continue: the next clip is generated from this clip's last frames so motion carries through. Crossfade: the overlap is dissolved pixel-by-pixel."
      )
    );
    if (isJoin && !capability.modes.includes(value) && capability.reason) {
      fields.appendChild(
        buildCapabilityNotice({
          supported: false,
          reason: capability.reason,
          code: ""
        })
      );
    }
    if (isJoin && value !== "cut" && capability.modes.includes(value)) {
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
    const timing = !isJoin || seam === null ? null : resolveTimelineTiming(clips, fps, capabilities);
    const impact = timing === null ? null : boundaryImpactForLeftClip(timing, leftClipIdx);
    const plannedWindow = impact === null ? 0 : value === "continue" ? impact.continuityWindowFrames : impact.overlapFrames;
    const info = document.createElement("div");
    info.className = "vst-boundary-info vst-detail-span-full";
    const effective = capability.effective(value);
    if (selectedRelationship === SOURCE_VIDEO_RELATIONSHIP) {
      info.textContent = `Clip ${leftClipIdx} is consumed as Clip ${leftClipIdx + 1}'s source video and does not extend the final video.`;
    } else if (selectedRelationship === SOURCE_AUDIO_RELATIONSHIP) {
      info.textContent = `Clip ${leftClipIdx} is consumed as Clip ${leftClipIdx + 1}'s source audio and does not extend the final video.`;
    } else if (effective !== value) {
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
      impactBlock.className = "vst-boundary-impact vst-detail-span-full";
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
    return fields;
  };

  // frontend/detailStrip/clipBasics.ts
  var DURATION_STEP = 0.5;
  var REFERENCE_LENGTH_HINT = "(derived from a reference's media length)";
  var CLIP_PROJ_REPO = "https://github.com/nicolab28/ComfyUI-ClipProj";
  var appendClipProjRepoLink = (field) => {
    const help = field.querySelector(".sui-info-popover");
    if (!help) {
      return;
    }
    const repo = document.createElement("a");
    repo.href = CLIP_PROJ_REPO;
    repo.target = "_blank";
    repo.rel = "noopener noreferrer";
    repo.textContent = "ComfyUI-ClipProj";
    help.append(repo, ".");
  };
  var buildClipColumn = (context, clip, clipIdx, referenceFramingState, h3TextEncoderState = "hidden", supportsAudioOnly = false) => {
    const column = document.createElement("div");
    column.className = "input-group-content vst-detail-section-content vst-detail-col vst-detail-clip";
    const audioOnly = clipIdx === 0 && clip.audioOnly === true;
    const initVideoClip = !!clip.initVideo;
    const lengthReferenceIdx = clipLengthReferenceIndex(clip.references);
    const lengthDerived = clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true || lengthReferenceIdx >= 0 || initVideoClip;
    const durationHint = !lengthDerived ? null : initVideoClip ? "(derived from the source video range)" : lengthReferenceIdx >= 0 ? REFERENCE_LENGTH_HINT : "(derived from audio/ControlNet source)";
    const durationField = buildSlider(
      "Duration (s)",
      clip.duration,
      CLIP_DURATION_MIN,
      CLIP_DURATION_MAX,
      DURATION_STEP,
      (value) => {
        context.debouncedCommit("duration", (clips) => {
          const target = clips[clipIdx];
          if (target && !lengthDerived) {
            const defaults = context.authoring().defaults;
            applyClipDurationResize(target, value, defaults);
          }
        });
      },
      {
        sliderMax: 20,
        hint: durationHint ?? REFERENCE_LENGTH_HINT
      }
    );
    const durationInput = durationField.querySelector(
      "input.auto-slider-number"
    );
    if (!durationInput) {
      throw new Error("duration slider number input missing");
    }
    durationInput.setAttribute("data-vst-focus-key", "duration");
    durationField.querySelector(".vst-detail-field-hint")?.classList.toggle("vst-detail-field-hint-hidden", !durationHint);
    if (lengthDerived) {
      durationField.querySelectorAll("input").forEach((input2) => {
        input2.disabled = true;
      });
      durationField.classList.add("vst-field-disabled");
    }
    column.appendChild(durationField);
    column.appendChild(
      buildCheckbox(
        "Use SeedVR",
        clip.useSeedVr,
        (value) => {
          context.commit((clips) => {
            const target = clips[clipIdx];
            if (target) {
              target.useSeedVr = value;
            }
          });
        },
        { disabled: audioOnly }
      )
    );
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
    if (supportsAudioOnly && clipIdx === 0) {
      column.appendChild(
        buildCheckbox(
          "Audio Only",
          clip.audioOnly === true,
          (value) => {
            context.structuralCommit((clips) => {
              const transaction = context.authoring();
              return setAudioOnlyLead(
                clips,
                value,
                transaction,
                transaction.defaults.fps
              ) ? "render" : null;
            });
          },
          { disabled: clip.initVideo !== null }
        )
      );
    }
    if (h3TextEncoderState === "ready") {
      const textEncoder = buildOptionSelect(
        H3_TEXT_ENCODERS.map((value) => ({ value, label: value })),
        clip.h3TextEncoder,
        (value) => {
          context.commit((clips) => {
            const target = clips[clipIdx];
            if (target) {
              target.h3TextEncoder = normalizeH3TextEncoder(value);
            }
          });
        }
      );
      textEncoder.dataset.vstH3TextEncoder = "true";
      const textEncoderField = buildField(
        "Text Encoder",
        textEncoder,
        void 0,
        "Default uses MiniMax H3's full 32B encoder. The 8B and 4B options use less VRAM; VideoStages will download the matching projection automatically. Requires "
      );
      appendClipProjRepoLink(textEncoderField);
      column.appendChild(textEncoderField);
    } else if (h3TextEncoderState === "install") {
      const install = document.createElement("button");
      install.type = "button";
      install.className = "basic-button";
      install.dataset.vstInstallClipproj = "true";
      install.textContent = "Install ComfyUI-ClipProj";
      const installField = buildField(
        "Text Encoder",
        install,
        void 0,
        "Install the custom node required for MiniMax H3's smaller text encoders. SwarmUI will restart managed ComfyUI backends. See "
      );
      installField.id = `vst_clip_${clipIdx}_clipproj_install`;
      appendClipProjRepoLink(installField);
      install.addEventListener("click", () => {
        if (!installHostFeature(H3_TEXT_ENCODER_FEATURE, installField.id)) {
          getVideoStagesHostBridge().showError(
            "SwarmUI's ComfyUI feature installer is unavailable."
          );
        }
      });
      column.appendChild(installField);
    }
    return column;
  };

  // frontend/detailStrip/clipReferencePanel.ts
  var REFERENCE_TRIM_MIN = 0.1;
  var appendReferenceTrim = (ctx, fields, reference, shown, commitReference) => {
    const mediaKind = reference.kind === "audio" ? "audio" : "video";
    const mediaLabel = CLIP_REFERENCE_KIND_INFO[reference.kind].label;
    const limitSeconds = reference.mediaDurationSeconds;
    const limits = {
      limitSeconds,
      minLengthSeconds: reference.drivesClipLength ? CLIP_DURATION_MIN : REFERENCE_TRIM_MIN,
      // Only duration-owning references must align to the clip's frame grid.
      fps: reference.drivesClipLength ? getTimelineStore().getState().fps : 0
    };
    const write = (next) => {
      const whole = next.startSeconds <= 0 && next.lengthSeconds >= limitSeconds;
      Object.assign(shown, next);
      commitReference((target, clip) => {
        target.startSeconds = whole ? 0 : next.startSeconds;
        target.lengthSeconds = whole ? 0 : next.lengthSeconds;
        if (target.drivesClipLength) {
          applyClipDurationResize(
            clip,
            Math.max(CLIP_DURATION_MIN, next.lengthSeconds),
            ctx.authoring().defaults,
            limits.fps
          );
        }
      });
    };
    const range = toInOut(shown);
    let inInput;
    let outInput;
    inInput = buildNumber(
      range.inSeconds,
      0,
      limitSeconds,
      REFERENCE_TRIM_MIN,
      (value) => {
        const next = setInPoint(shown, value, limits);
        outInput.value = `${toInOut(next).outSeconds}`;
        write(next);
      }
    );
    outInput = buildNumber(
      range.outSeconds,
      REFERENCE_TRIM_MIN,
      limitSeconds,
      REFERENCE_TRIM_MIN,
      (value) => {
        const next = setOutPoint(shown, value, limits);
        inInput.value = `${next.startSeconds}`;
        write(next);
      }
    );
    const disabled = reference.drivesClipLength === true;
    inInput.disabled = disabled;
    outInput.disabled = disabled;
    const inField = buildField("In (s)", inInput);
    const outField = buildField("Out (s)", outInput);
    inField.classList.toggle("vst-field-disabled", disabled);
    outField.classList.toggle("vst-field-disabled", disabled);
    fields.append(inField, outField);
    fields.appendChild(
      buildTrimLauncher(
        `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s · References ${shown.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
        () => openTrimModal({
          mediaKind,
          title: `Trim ${mediaLabel} Reference`,
          fileName: reference.uploadedMedia?.fileName ?? `${mediaLabel} reference`,
          dataUri: reference.uploadedMedia?.data ?? null,
          range: shown,
          limits,
          impactText: (next) => `References ${next.lengthSeconds.toFixed(1)} s of ${limitSeconds.toFixed(1)} s`,
          onApply: (next) => {
            write(next);
            ctx.render();
          }
        })
      )
    );
  };
  var claimClipLength = (clip, referenceIdx, defaults, fps) => {
    clip.references.forEach((reference, index) => {
      reference.drivesClipLength = index === referenceIdx;
    });
    if (referenceIdx < 0) {
      return;
    }
    clip.clipLengthFromAudio = false;
    clip.clipLengthFromControlNet = false;
    const claimed = clip.references[referenceIdx];
    const seconds2 = claimed ? clipReferenceUsedSeconds(claimed) : 0;
    if (seconds2 > 0) {
      applyClipDurationResize(
        clip,
        Math.max(CLIP_DURATION_MIN, seconds2),
        defaults,
        fps
      );
    }
  };
  var applyPickedReferenceMedia = (ctx, clipId, referenceId, data, fileName) => runClipMediaProbe({
    clipId,
    slot: referenceId,
    probe: () => probeMediaDurationSeconds(data),
    apply: (clip, seconds2, state) => {
      const index = clip.references.findIndex(
        (reference2) => reference2.id === referenceId
      );
      const reference = clip.references[index];
      if (!reference) {
        return;
      }
      reference.uploadedMedia = { data, fileName };
      reference.mediaDurationSeconds = roundToTenth(seconds2);
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
  var createClipReferenceEditor = (ctx, clipIdx, clips, fps) => {
    const clip = clips[clipIdx];
    const references = clip.references;
    const { capabilities, defaults } = ctx.authoring();
    const authoring = clipReferenceAuthoring(capabilities.forClip(clip));
    const incomingBoundary = authoring.mode === "all" ? incomingReferenceContinueForClip(
      clips,
      fps,
      capabilities,
      clipIdx
    ) : null;
    const incomingReference = {
      kind: "video",
      includeSoundtrack: incomingBoundary ? clips[incomingBoundary.leftIdx].boundaryOutReferenceIncludeSoundtrack : false
    };
    const tags = clipReferenceTags(
      references,
      incomingBoundary ? [incomingReference] : []
    );
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
    const buildEditor = (editorIdx) => {
      const reference = references[editorIdx];
      if (!reference) {
        return void 0;
      }
      const shownRange = clipReferenceUsedRange(reference);
      const commitReference = (mutate) => {
        ctx.commit((cs) => {
          const targetClip = cs[clipIdx];
          const target = targetClip?.references[editorIdx];
          if (target && targetClip) {
            mutate(target, targetClip);
          }
        });
      };
      const fields = document.createElement("div");
      fields.className = "vst-detail-fields vst-detail-col vst-detail-instance-fields vst-detail-clip-ref-editor";
      fields.setAttribute("data-vst-clip-ref-index", `${editorIdx}`);
      fields.appendChild(
        buildField(
          "Kind",
          buildOptionSelect(
            CLIP_REFERENCE_KINDS.filter(
              (kind) => authoring.mode === "all" || kind === "image" || kind === reference.kind
            ).map((kind) => ({
              value: kind,
              label: CLIP_REFERENCE_KIND_INFO[kind].label
            })),
            reference.kind,
            (value) => {
              commitReference((target) => {
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
        reference.source ?? "",
        clipIdx > 0
      );
      const source = resolveClipReferenceSourceValue(
        reference.source ?? "",
        options
      );
      fields.appendChild(
        buildField(
          "Source",
          buildOptionSelect(options, source, (value) => {
            commitReference((target) => {
              const resolved = resolveClipReferenceSourceValue(
                value,
                buildClipReferenceSourceOptions(
                  target.kind,
                  value,
                  clipIdx > 0
                )
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
              commitReference((target) => {
                target.uploadedMedia = {
                  data: pickedData,
                  fileName
                };
              });
              ctx.render();
            },
            () => {
              commitReference((target) => {
                target.uploadedMedia = null;
                target.mediaDurationSeconds = 0;
                target.drivesClipLength = false;
              });
              ctx.render();
            },
            data,
            reference.kind === "image" ? void 0 : { kind: reference.kind, range: shownRange }
          )
        );
      }
      if (clipReferenceCanDriveLength(reference)) {
        const seconds2 = reference.mediaDurationSeconds;
        const hint = document.createElement("small");
        hint.className = "vst-detail-field-hint";
        hint.textContent = `Detected: ${seconds2 > 0 ? `${seconds2.toFixed(1)} s` : "unknown length"}`;
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
              disabled: !(seconds2 > 0) && reference.drivesClipLength !== true,
              help: seconds2 > 0 ? "Set this clip's length to the reference's own length. Only one source can own the clip length, so this clears any other reference and the clip-level length options." : "This reference has no detected length to lend the clip. Pick a file the browser can read."
            }
          )
        );
      }
      if (reference.kind !== "image" && reference.mediaDurationSeconds > 0) {
        appendReferenceTrim(
          ctx,
          fields,
          reference,
          shownRange,
          commitReference
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
                commitReference((target) => {
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
              commitReference((target) => {
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
    return { buildEditor, buildIncomingEditor };
  };
  var buildClipReferenceFields = (ctx, clipIdx, referenceIdx, clips, fps) => {
    const fields = createClipReferenceEditor(
      ctx,
      clipIdx,
      clips,
      fps
    ).buildEditor(referenceIdx);
    if (!fields) {
      throw new Error(`Reference ${referenceIdx} does not exist`);
    }
    return fields;
  };
  var buildIncomingReferenceFields = (ctx, clipIdx, clips, fps) => {
    const fields = createClipReferenceEditor(
      ctx,
      clipIdx,
      clips,
      fps
    ).buildIncomingEditor();
    if (!fields) throw new Error(`Clip ${clipIdx} has no incoming reference`);
    fields.classList.add("vst-detail-fields");
    return fields;
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
  var buildInitVideoFields = (context, clip, clipIdx, disabled = false) => {
    const source = clip.initVideo;
    const col = document.createElement("div");
    col.className = "vst-detail-fields vst-detail-col vst-detail-source-col";
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
    const sourceKind = source?.source === MEDIA_SOURCE_PREVIOUS_CLIP ? MEDIA_SOURCE_PREVIOUS_CLIP : MEDIA_SOURCE_UPLOAD;
    if (clipIdx > 0) {
      const sourceSelect = buildOptionSelect(
        [
          { value: MEDIA_SOURCE_UPLOAD, label: "Upload" },
          {
            value: MEDIA_SOURCE_PREVIOUS_CLIP,
            label: "Previous Clip Output"
          }
        ],
        sourceKind,
        (value) => {
          context.structuralCommit((clips) => {
            const changed = setPreviousClipVideoSource(
              clips,
              clipIdx,
              value === MEDIA_SOURCE_PREVIOUS_CLIP,
              context.authoring(),
              getTimelineStore().getState().fps
            );
            return changed ? "render" : null;
          });
        }
      );
      sourceSelect.classList.add("vst-init-video-source");
      col.appendChild(buildField("Video source", sourceSelect));
    }
    const shown = {
      startSeconds: source?.startSeconds ?? 0,
      lengthSeconds: source?.lengthSeconds ?? 0
    };
    const hint = document.createElement("small");
    hint.className = "vst-detail-field-hint vst-detail-span-full";
    const sourceIsPrevious = sourceKind === MEDIA_SOURCE_PREVIOUS_CLIP;
    hint.textContent = sourceIsPrevious ? "Uses the previous clip's decoded output as this clip's starting footage." : "Use an existing video file as this clip instead of generating it.";
    col.appendChild(hint);
    if (!sourceIsPrevious) {
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
          removeSource,
          source?.data,
          {
            kind: "video",
            range: shown
          }
        )
      );
    }
    const disableSection = () => {
      if (!disabled) {
        return;
      }
      col.classList.add("vst-field-disabled");
      col.setAttribute("aria-disabled", "true");
      col.querySelectorAll("input, button, select").forEach((control) => {
        control.disabled = true;
      });
    };
    if (!source) {
      disableSection();
      return col;
    }
    const info = document.createElement("small");
    info.className = "vst-detail-field-hint";
    info.textContent = sourceIsPrevious ? `Previous output: ${source.durationSeconds.toFixed(1)} s` : `Detected: ${source.fps > 0 ? `${source.fps} fps` : "unknown fps"} · ${source.durationSeconds > 0 ? `${source.durationSeconds.toFixed(1)} s` : "unknown length"}`;
    col.appendChild(info);
    const fileLimit = sourceLimitSeconds(source);
    const fps = getTimelineStore().getState().fps;
    const limits = {
      limitSeconds: fileLimit,
      minLengthSeconds: CLIP_DURATION_MIN,
      fps
    };
    const writeRange = (target, next) => {
      const targetSource = target.initVideo;
      if (!targetSource) {
        return;
      }
      targetSource.startSeconds = next.startSeconds;
      targetSource.lengthSeconds = next.lengthSeconds;
      Object.assign(shown, next);
      applyClipDurationResize(
        target,
        Math.max(CLIP_DURATION_MIN, next.lengthSeconds),
        context.authoring().defaults,
        fps
      );
    };
    const edgeInput = (edge, apply) => context.buildClampedNumber({
      key: `source-${edge}`,
      value: toInOut(shown)[edge === "in" ? "inSeconds" : "outSeconds"],
      min: edge === "in" ? 0 : CLIP_DURATION_MIN,
      max: fileLimit,
      step: DURATION_STEP2,
      readBack: (clips) => {
        const readSource = clips[clipIdx]?.initVideo;
        return readSource ? toInOut(readSource)[edge === "in" ? "inSeconds" : "outSeconds"] : null;
      },
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        if (target?.initVideo) {
          writeRange(target, apply(target.initVideo, value, limits));
        }
      }
    });
    col.appendChild(
      buildField(
        "In (s)",
        edgeInput("in", setInPoint),
        void 0,
        "Where inside the source file this clip's footage begins. Moving it leaves Out where it is, so the clip gets shorter."
      )
    );
    col.appendChild(
      buildField(
        "Out (s)",
        edgeInput("out", setOutPoint),
        void 0,
        "Where inside the source file this clip's footage ends. This also becomes the clip's duration."
      )
    );
    if (!sourceIsPrevious && source.durationSeconds > 0) {
      const range = toInOut(shown);
      col.appendChild(
        buildTrimLauncher(
          `Range ${range.inSeconds.toFixed(1)}–${range.outSeconds.toFixed(1)} s · Uses ${shown.lengthSeconds.toFixed(1)} s of ${fileLimit.toFixed(1)} s`,
          () => openTrimModal({
            mediaKind: "video",
            title: "Trim Source Video",
            fileName: source.fileName ?? "Source video",
            dataUri: source.data,
            range: shown,
            limits,
            impactText: (next) => `Clip duration becomes ${next.lengthSeconds.toFixed(1)} s`,
            onApply: (next) => {
              context.commit((clips) => {
                const target = clips[clipIdx];
                if (target) {
                  writeRange(target, next);
                }
              });
              context.render();
            }
          })
        )
      );
    }
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "This range (conformed to the timeline fps and size) is the clip's starting point: the first stage refines it using its Control value, later stages refine or upscale it, and a retake regenerates part of it.";
    col.appendChild(note);
    disableSection();
    return col;
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
  var buildMajorPromptFields = (ctx, clip, clipIdx, layout = { rows: 8 }) => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-prompt-body";
    const prompt = buildTextarea(
      clip.prompt ?? "",
      readGlobalPrompt().trim() || "Clip prompt (blank inherits the global prompt)…",
      "prompt-major",
      (value) => {
        ctx.debouncedCommit("prompt-major", (clips) => {
          const target = clips[clipIdx];
          if (target) {
            target.prompt = value.trim();
          }
        });
      },
      layout
    );
    col.appendChild(buildField("Prompt", prompt));
    return col;
  };
  var buildRelayPromptFields = (ctx, clip, clipIdx, windowIdx) => {
    const window2 = clip.promptWindows[windowIdx];
    if (!window2) {
      throw new Error(`Relay prompt ${windowIdx} does not exist`);
    }
    const clipDuration = Math.max(
      PROMPT_WINDOW_MIN_DURATION,
      clip.duration || 0
    );
    const fields = document.createElement("div");
    fields.className = "vst-detail-col vst-detail-fields vst-detail-prompt-body vst-detail-minor-window";
    fields.setAttribute("data-vst-minor-window", `${windowIdx}`);
    const bounds = promptWindowNeighborBounds(clip, windowIdx);
    const beginInput = ctx.buildClampedNumber({
      key: `minor-${windowIdx}-begin`,
      value: roundToTenth(window2.start),
      min: bounds?.beginMin ?? 0,
      max: gridFloor(Math.max(0, clipDuration - PROMPT_WINDOW_MIN_DURATION)),
      step: 0.1,
      readBack: (clips) => {
        const target = clips[clipIdx]?.promptWindows?.[windowIdx];
        return target ? roundToTenth(target.start) : null;
      },
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        if (target) {
          applyPromptWindowBegin(target, windowIdx, value);
        }
      }
    });
    fields.appendChild(
      buildField(
        "Begin (s)",
        beginInput,
        void 0,
        "When this relay prompt starts within the clip."
      )
    );
    const endInput = ctx.buildClampedNumber({
      key: `minor-${windowIdx}-end`,
      value: roundToTenth(window2.start + window2.duration),
      min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
      max: bounds?.endMax ?? clipDuration,
      step: 0.1,
      readBack: (clips) => {
        const target = clips[clipIdx]?.promptWindows?.[windowIdx];
        return target ? roundToTenth(target.start + target.duration) : null;
      },
      mutate: (clips, value) => {
        const target = clips[clipIdx];
        if (target) {
          applyPromptWindowEnd(target, windowIdx, value);
        }
      }
    });
    fields.appendChild(
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
      `minor-${windowIdx}`,
      (value) => {
        ctx.debouncedCommit(`minor-${windowIdx}`, (clips) => {
          const target = clips[clipIdx]?.promptWindows?.[windowIdx];
          if (target) {
            target.prompt = value.trim();
          }
        });
      },
      { rows: 4 }
    );
    editor.addEventListener("focus", () => {
      setSelection({ kind: "prompt-minor", clipIdx, windowIdx });
    });
    const prompt = buildField("Prompt", editor);
    prompt.classList.add("vst-detail-span-full");
    fields.appendChild(prompt);
    return fields;
  };

  // frontend/detailStrip/refPanel.ts
  var buildKeyframeFields = (ctx, clip, clipIdx, ref, refIdx, fps) => {
    const { defaults } = ctx.authoring();
    const endpointPolicy = referenceEndpointPolicy(clip, defaults.modelCatalog);
    const hasSupportedEndpoint = endpointPolicy.available;
    const options = buildImageSourceOptions(ref.source ?? "");
    const source = resolveImageSourceValue(ref.source ?? "", options);
    const isUpload = source === MEDIA_SOURCE_UPLOAD;
    const fields = document.createElement("div");
    fields.className = "vst-detail-fields vst-detail-col vst-detail-instance-fields vst-detail-ref-row vst-detail-ref-editor";
    fields.setAttribute("data-vst-ref-index", `${refIdx}`);
    const select2 = buildOptionSelect(options, source, (value) => {
      ctx.commit((cs) => {
        const target = cs[clipIdx]?.frameRefs[refIdx];
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
        "Where this keyframe image comes from — an upload, or another clip's rendered frame. The image guides how the clip looks at its attach frame."
      )
    );
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
        ctx.debouncedCommit(`ref-${refIdx}-frame`, (cs) => {
          const target = cs[clipIdx]?.frameRefs[refIdx];
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
    frameInput.setAttribute("data-vst-focus-key", `ref-${refIdx}-frame`);
    frameInput.disabled = !hasSupportedEndpoint;
    fields.appendChild(
      buildField(
        "Attach at Frame",
        frameInput,
        void 0,
        boundedReferencePositionHelp(endpointPolicy) ?? (boundedPositions ? "This clip accepts an image only at a bounded endpoint." : "The frame within the clip where this keyframe is anchored. Frame 1 is the first frame; the image influences the clip most strongly around here.")
      )
    );
    fields.appendChild(
      buildCheckbox(
        "Count from clip end",
        ref.fromEnd === true,
        (value) => {
          ctx.commit((cs) => {
            const target = cs[clipIdx]?.frameRefs[refIdx];
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
              const target = cs[clipIdx]?.frameRefs[refIdx];
              if (target) {
                target.uploadedImage = { data, fileName };
                target.uploadFileName = fileName;
              }
            });
            ctx.render();
          },
          () => {
            ctx.commit((cs) => {
              const target = cs[clipIdx]?.frameRefs[refIdx];
              if (target) {
                target.uploadedImage = null;
                target.uploadFileName = null;
              }
            });
            ctx.render();
          },
          ref.uploadedImage?.data
        )
      );
    }
    return fields;
  };

  // frontend/detailStrip/retakePanel.ts
  var buildRetakeFields = (context, clip, clipIdx) => {
    const retake = clip.retake;
    const decision = context.authoring().capabilities.forClip(clip).decision("retake");
    const col = document.createElement("div");
    col.className = "vst-detail-fields vst-detail-col vst-detail-retake-col";
    if (!retake) {
      const hint = document.createElement("small");
      hint.className = "vst-detail-field-hint";
      hint.textContent = "Regenerates a sub-range when refining a base video.";
      col.appendChild(hint);
      const add = buildDetailActionButton({
        label: "+ Add Retake",
        title: decision.supported ? "Add a retake window" : decision.reason,
        className: "small-button vst-detail-repeating-add vst-detail-add-retake",
        disabled: !decision.supported,
        onClick: () => context.createRetake(clipIdx)
      });
      col.appendChild(add);
      return col;
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
    return col;
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
  var buildSettingsFields = (ctx, state, _selection = {
    kind: "none"
  }) => {
    const defaults = ctx.authoring().defaults;
    const core = {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
    const architectureMultiple = activeDocumentDimensionMultiple(
      state.clips,
      defaults.modelCatalog
    );
    const dimensionSnap = getTimelineAuthoringSettings().dimensionSnap;
    const multiple = dimensionSnap === "disabled" ? architectureMultiple : Math.max(architectureMultiple, dimensionSnap);
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
    const dimensionSnapSpecs = [
      { value: "disabled", label: "Disabled" },
      { value: "32", label: "Multiples of 32" },
      { value: "64", label: "Multiples of 64" }
    ];
    const dimensionSnapSelect = buildOptionSelect(
      dimensionSnapSpecs,
      `${dimensionSnap}`,
      (value) => {
        const selected = value === "32" ? 32 : value === "64" ? 64 : "disabled";
        setTimelineAuthoringSetting("dimensionSnap", selected);
        ctx.render();
      }
    );
    body.appendChild(buildField("Dimension Snap", dimensionSnapSelect));
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
          },
          { sliderMin: ROOT_DIMENSION_SLIDER_MIN }
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
          },
          { sliderMin: ROOT_DIMENSION_SLIDER_MIN }
        ),
        "settings-height"
      );
      body.append(widthSlider, heightSlider);
    } else if (!isInherited) {
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
            sliderMin: ROOT_DIMENSION_SLIDER_MIN,
            isPot: true
          }
        ),
        "settings-side-length"
      );
      calculatedDimensions = document.createElement("span");
      calculatedDimensions.className = "vst-settings-calculated-dims";
      calculatedDimensions.textContent = `${effectiveDimensions.width} × ${effectiveDimensions.height}`;
      sideLengthSlider.querySelector("label")?.appendChild(calculatedDimensions);
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
    return body;
  };

  // frontend/detailStrip/stagePanel/attentionSection.ts
  var appendStageAttentionSection = (bindings) => {
    const { context, clip, stage, stageIdx, fields, commit, slider } = bindings;
    if (!context.authoring().capabilities.forClip(clip).decision("stageAttention").supported)
      return;
    const section = document.createElement("section");
    section.className = "vst-stage-attention vst-stage-section vst-detail-span-full";
    section.setAttribute("aria-label", `Attention for stage S${stageIdx}`);
    const header = document.createElement("div");
    header.className = "vst-stage-section-header";
    const heading = document.createElement("h5");
    heading.className = "vst-detail-crumb";
    heading.textContent = "Attention";
    header.append(heading);
    const controls = document.createElement("div");
    controls.className = "vst-detail-fields";
    for (const [label, key, enabledKey, paramId] of [
      [
        "Model attention backend",
        "modelAttentionBackend",
        "modelAttentionBackendEnabled",
        "modelattentionbackend"
      ],
      [
        "Use sparse attention",
        "useSparseAttention",
        "useSparseAttentionEnabled",
        "usesparseattention"
      ]
    ]) {
      const choices = getDropdownOptions(paramId, `input_${paramId}`);
      const selected = stage[key] ?? choices.values[0] ?? "";
      const options = choices.values.map((value, index) => ({
        value,
        label: choices.labels[index] ?? value
      }));
      preserveSelectedOption(options, selected, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
        disabled: true
      }));
      let enabled = stage[enabledKey] === true;
      const select2 = tagFocus(buildOptionSelect(options, selected), key);
      select2.dataset.vstHostToggle = "true";
      const field = buildField(
        label,
        select2,
        void 0,
        key === "useSparseAttention" ? "Sol-Attn uses a training-free threshold (1.3). SLA and VSA keep 10% and require weights trained for their attention pattern; VSA is for FastH3. Sparse attention starts at 20% of sampling and stays dense below 12,288 tokens." : "Choose the attention implementation for this stage, including the dense fallback when sparse attention is active."
      );
      field.classList.add("vst-wide-select");
      const toggle = buildToggleSwitch({
        checked: enabled,
        disabled: choices.values.length === 0,
        title: `Enable ${label.toLowerCase()} for stage S${stageIdx}`,
        onChange: (nextEnabled) => {
          if (enabled === nextEnabled) return;
          enabled = nextEnabled;
          doToggleEnable(select2.id);
          commit((target) => {
            target[enabledKey] = enabled;
            target[key] = select2.value;
          });
        }
      });
      toggle.querySelector("input")?.setAttribute("id", `${select2.id}_toggle`);
      field.addEventListener("change", () => {
        commit((target) => {
          target[key] = select2.value;
          target[enabledKey] = enabled;
        });
      });
      const labelElement = field.querySelector("label");
      if (labelElement) {
        const labelRow = document.createElement("div");
        labelRow.className = "vst-field-label-row";
        labelElement.before(labelRow);
        labelRow.append(labelElement, toggle);
      }
      controls.append(field);
    }
    if (context.authoring().capabilities.forClip(clip).architectureId === "minimax" && getVideoStagesHostBridge().hasBackendFeature(
      H3_ATTENTION_WINDOW_FEATURE
    )) {
      const attentionWindow = slider(
        "Attention window (s)",
        "h3AttentionWindowSeconds",
        stage.h3AttentionWindowSeconds,
        H3_ATTENTION_WINDOW_MIN_SECONDS,
        H3_ATTENTION_WINDOW_MAX_SECONDS,
        H3_ATTENTION_WINDOW_STEP_SECONDS,
        (target, value) => {
          target.h3AttentionWindowSeconds = value;
        },
        {
          help: "Total centered temporal attention window for this stage. 0 disables JuanAttn. Dense transformer layers remain fixed at 0,9,19,29,39,49."
        }
      );
      attentionWindow.dataset.vstH3AttentionWindow = "true";
      controls.append(attentionWindow);
    }
    section.append(header, controls);
    fields.append(section);
  };

  // frontend/detailStrip/stagePanel/characterRefineSection.ts
  var updateCharacterRefineAvailability = (section, upscale) => {
    setFieldDisabled(
      section,
      upscale !== 1,
      "Set Upscale to 1× to use Make Them Bonita"
    );
  };
  var referenceLabel = (reference) => reference.uploadedMedia?.fileName?.trim() || reference.source.trim() || "Image reference";
  var textInput2 = (bindings, key, fallback) => {
    const input2 = document.createElement("input");
    input2.type = "text";
    input2.className = "auto-text";
    input2.maxLength = 120;
    input2.placeholder = fallback;
    input2.value = bindings.stage.characterRefine?.[key] ?? fallback;
    input2.addEventListener("change", () => {
      const value = input2.value.trim() || fallback;
      input2.value = value;
      bindings.commit((stage) => {
        if (stage.characterRefine) stage.characterRefine[key] = value;
      });
    });
    return input2;
  };
  var pictureRows = (bindings, imageReferences) => {
    const rows = document.createElement("div");
    rows.className = "vst-detail-span-full vst-character-refine-pictures";
    const title = document.createElement("div");
    title.className = "vst-character-refine-pictures-title";
    title.setAttribute("role", "heading");
    title.setAttribute("aria-level", "4");
    title.textContent = "Reference Pictures";
    rows.appendChild(title);
    imageReferences.forEach((reference, index) => {
      if (!reference.id) return;
      const row = document.createElement("label");
      row.className = "vst-character-refine-picture";
      row.title = "Use this picture to guide how the person should look after the repair.";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.className = "auto-checkbox";
      checkbox.checked = bindings.stage.characterRefine?.pictureReferenceIds.includes(
        reference.id
      ) ?? false;
      checkbox.addEventListener(
        "change",
        () => bindings.commit((stage) => {
          if (!stage.characterRefine || !reference.id) return;
          const selected = new Set(
            stage.characterRefine.pictureReferenceIds
          );
          if (checkbox.checked) selected.add(reference.id);
          else selected.delete(reference.id);
          stage.characterRefine.pictureReferenceIds = imageReferences.map((candidate) => candidate.id).filter(
            (id) => Boolean(id && selected.has(id))
          );
        })
      );
      const text2 = document.createElement("span");
      text2.textContent = `<picture ${index + 1}> ${referenceLabel(reference)}`;
      row.append(checkbox, text2);
      rows.appendChild(row);
    });
    return rows;
  };
  var appendCharacterRefineSection = (bindings) => {
    const {
      context,
      clip,
      clipIdx,
      stage,
      stageIdx,
      fields: stageFields,
      commit,
      slider
    } = bindings;
    const sourceVideo = clip.initVideo;
    const sourceVideoKind = sourceVideo?.source ?? MEDIA_SOURCE_UPLOAD;
    const architecture = context.authoring().capabilities.forClip(clip);
    const supportsAppearanceReferences = architecture.decision(
      "characterRefineAppearanceReferences"
    ).supported;
    const hasDecodedVideo = clip.stages.slice(0, stageIdx).some(
      (candidate) => stageProducesDecodedVideo(
        candidate,
        (feature) => architecture.decision(feature).supported
      )
    ) || sourceVideo !== null && (sourceVideoKind === MEDIA_SOURCE_UPLOAD || sourceVideoKind === MEDIA_SOURCE_PREVIOUS_CLIP);
    if (!architecture.decision("characterRefine").supported || !CHARACTER_REFINE_REQUIRED_FEATURES.every(
      (feature) => getVideoStagesHostBridge().hasBackendFeature(feature)
    ) || !hasDecodedVideo) {
      return;
    }
    const imageReferences = clip.references.filter(
      (reference) => reference.kind === "image" && reference.id && clipReferenceIsUsable(reference)
    );
    const fields = document.createElement("div");
    fields.className = "vst-detail-fields vst-character-refine-fields";
    const appendGroup = () => {
      const enabled = stage.characterRefine !== null;
      const built = buildAccordionSection({
        key: `bonita-${clip.id ?? clipIdx}-${stage.id ?? stageIdx}`,
        label: "Make Them Bonita",
        content: fields,
        defaultOpen: enabled,
        flattenContent: true,
        className: "vst-character-refine-section vst-detail-span-full",
        toggle: {
          checked: enabled,
          title: enabled ? "Turn off Make Them Bonita" : "Turn on Make Them Bonita",
          onChange: (checked) => {
            commit((target) => {
              target.characterRefine = checked ? defaultCharacterRefine(
                supportsAppearanceReferences ? imageReferences.flatMap(
                  (reference) => reference.id ? [reference.id] : []
                ) : []
              ) : null;
              if (checked) target.upscale = 1;
            });
            context.render();
          }
        }
      });
      if (stage.upscale !== 1)
        updateCharacterRefineAvailability(built.section, stage.upscale);
      appendHelp(
        built.heading,
        built.section,
        "Make Them Bonita",
        "Repair a person or character in this stage."
      );
      stageFields.appendChild(built.section);
    };
    const settings = stage.characterRefine ?? defaultCharacterRefine([]);
    fields.appendChild(
      buildField(
        "Finder",
        buildOptionSelect(
          [
            { value: "sam3", label: "SAM3" },
            { value: "yolo", label: "YOLO" }
          ],
          settings.finder,
          (value) => commit((target) => {
            if (target.characterRefine) {
              target.characterRefine.finder = value;
            }
          })
        ),
        void 0,
        "SAM3 is usually best for movement. YOLO is faster for simple shots."
      )
    );
    fields.appendChild(
      buildField(
        "Subject",
        textInput2(bindings, "subject", CHARACTER_REFINE_SUBJECT_DEFAULT),
        void 0,
        'Describe who or what to follow, such as "person" or "dog".'
      )
    );
    fields.appendChild(
      buildField(
        "Part",
        textInput2(bindings, "part", CHARACTER_REFINE_PART_DEFAULT),
        void 0,
        'Name the area you want to repair, such as "face" or "hand".'
      )
    );
    fields.appendChild(
      slider(
        "Subject Confidence",
        "character-refine-subject-confidence",
        settings.subjectConfidence,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
        CHARACTER_REFINE_DETECTION_THRESHOLD_STEP,
        (target, value) => {
          if (target.characterRefine) {
            target.characterRefine.subjectConfidence = value;
          }
        },
        {
          help: "Lower this if Bonita loses the subject. Raise it if Bonita follows the wrong one."
        }
      )
    );
    fields.appendChild(
      slider(
        "Part Confidence",
        "character-refine-part-confidence",
        settings.partConfidence,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
        CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
        CHARACTER_REFINE_DETECTION_THRESHOLD_STEP,
        (target, value) => {
          if (target.characterRefine) {
            target.characterRefine.partConfidence = value;
          }
        },
        {
          help: "Lower this if Bonita misses the area. Raise it if Bonita chooses the wrong area."
        }
      )
    );
    for (const [label, key, help] of [
      [
        "Crop Width",
        "cropWidth",
        "Choose a wider repair image to include more space around the subject."
      ],
      [
        "Crop Height",
        "cropHeight",
        "Choose a taller repair image to include more space above and below the subject."
      ]
    ]) {
      fields.appendChild(
        slider(
          label,
          `character-refine-${key}`,
          settings[key],
          CHARACTER_REFINE_CROP_DIMENSION_MIN,
          CHARACTER_REFINE_CROP_DIMENSION_MAX,
          CHARACTER_REFINE_CROP_DIMENSION_STEP,
          (target, value) => {
            if (target.characterRefine) {
              target.characterRefine[key] = value;
            }
          },
          {
            help,
            sliderMin: 256,
            sliderMax: 2048,
            isPot: true
          }
        )
      );
    }
    fields.appendChild(
      buildCheckbox(
        "Verify Identity",
        settings.verifyIdentity,
        (enabled) => commit((target) => {
          if (target.characterRefine) {
            target.characterRefine.verifyIdentity = enabled;
          }
        }),
        {
          disabled: imageReferences.length === 0,
          help: "InsightFace adds a slower identity check. Turn it on only when the scene has more than one person."
        }
      )
    );
    if (supportsAppearanceReferences) {
      fields.appendChild(pictureRows(bindings, imageReferences));
    }
    appendGroup();
  };

  // frontend/detailStrip/stagePanel/lorasSection.ts
  var appendStageLorasSection = ({
    context,
    clip,
    clipIdx,
    stage,
    stageIdx,
    defaults,
    fields,
    commit,
    debouncedCommit
  }) => {
    const section = document.createElement("section");
    section.className = "vst-stage-loras vst-stage-section vst-detail-span-full";
    section.setAttribute("aria-label", `LoRAs for stage S${stageIdx}`);
    const header = document.createElement("div");
    header.className = "vst-stage-section-header";
    const heading = document.createElement("h5");
    heading.className = "vst-detail-crumb";
    heading.textContent = `LoRAs · ${stage.loras.length}`;
    header.append(heading);
    const structural = (edit) => context.structuralCommit((clips) => {
      const target = clips[clipIdx];
      if (!target?.stages[stageIdx]) return null;
      edit(target);
      return "render";
    });
    section.append(header);
    if (stage.loras.length === 0) {
      const empty = document.createElement("p");
      empty.className = "vst-detail-note";
      empty.textContent = "No LoRAs on this stage.";
      section.append(empty);
    }
    const table = document.createElement("table");
    table.className = "vst-stage-lora-table";
    table.setAttribute("aria-label", `LoRAs for stage S${stageIdx}`);
    const columns = table.createTHead().insertRow();
    for (const label of ["LoRA model", "Strength", ""]) {
      const cell = document.createElement("th");
      cell.scope = "col";
      cell.textContent = label;
      if (!label) cell.setAttribute("aria-label", "Remove LoRA");
      columns.append(cell);
    }
    const rows = table.createTBody();
    stage.loras.forEach((lora, loraIdx) => {
      const row = rows.insertRow();
      row.className = "vst-stage-lora-row";
      const options = defaults.loraValues.flatMap(
        (value, index) => value === lora.name || !stage.loras.some((entry) => entry.name === value) ? [{ value, label: defaults.loraLabels[index] ?? value }] : []
      );
      preserveSelectedOption(options, lora.name, "start", (value) => ({
        value,
        label: `${value} (unsupported persisted value)`,
        disabled: true
      }));
      const model = tagFocus(
        buildOptionSelect(options, lora.name, (name) => {
          commit((target) => {
            if (target.loras[loraIdx])
              target.loras[loraIdx] = {
                name,
                weight: defaultLoraWeight(defaults, name)
              };
          });
          context.render();
        }),
        `lora-${loraIdx}-model`
      );
      const strength = tagFocus(
        buildUnboundedNumber(lora.weight, LORA_WEIGHT_STEP, (value) => {
          debouncedCommit(`lora-${loraIdx}-strength`, (target) => {
            if (target.loras[loraIdx])
              target.loras[loraIdx].weight = value;
          });
        }),
        `lora-${loraIdx}-strength`
      );
      strength.classList.add("lora-weight-input", "vst-stage-lora-strength");
      model.setAttribute(
        "aria-label",
        `LoRA model ${loraIdx + 1} for stage S${stageIdx}`
      );
      strength.setAttribute(
        "aria-label",
        `Strength ${loraIdx + 1} for stage S${stageIdx}`
      );
      row.insertCell().append(model);
      row.insertCell().append(strength);
      row.insertCell().append(
        actionButton({
          key: `remove-lora-${loraIdx}`,
          label: "",
          kind: "delete",
          title: `Remove LoRA ${loraIdx + 1} from stage S${stageIdx}`,
          configure: (button2) => {
            button2.classList.add("vst-stage-lora-remove");
            button2.setAttribute("aria-label", button2.title);
          },
          run: () => structural((target) => {
            target.stages[stageIdx].loras.splice(loraIdx, 1);
          })
        })
      );
    });
    if (stage.loras.length > 0) section.append(table);
    const actions = document.createElement("div");
    actions.className = "vst-editor-actions vst-stage-loras-actions";
    const available = defaults.loraValues.some(
      (name) => !stage.loras.some((entry) => entry.name === name)
    );
    const add = buildDetailActionButton({
      label: "+ Add LoRA",
      title: available ? "Add a LoRA to this stage" : "All available LoRAs are already on this stage.",
      className: "small-button vst-stage-lora-add",
      disabled: !available,
      onClick: () => structural((target) => {
        const loras = target.stages[stageIdx].loras;
        const name = defaults.loraValues.find(
          (name2) => !loras.some((entry) => entry.name === name2)
        );
        if (name)
          loras.push({
            name,
            weight: defaultLoraWeight(defaults, name)
          });
      })
    });
    add.dataset.action = "add";
    actions.append(add);
    if (stage.loras.length > 0 && clip.stages.length > 1) {
      actions.append(
        buildDetailActionButton({
          label: "Copy to other stages",
          title: `Replace LoRAs and strengths in every other stage of Clip ${clipIdx}. Later edits stay independent.`,
          className: "small-button vst-stage-loras-copy",
          onClick: () => structural((target) => {
            const source = target.stages[stageIdx].loras;
            target.stages.forEach((other, index) => {
              if (index !== stageIdx)
                other.loras = source.map((entry) => ({
                  ...entry
                }));
            });
          })
        })
      );
    }
    section.append(actions);
    fields.append(section);
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
  var shortModelName = (modelName) => {
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
    const capabilityView = context.authoring().capabilities.forClip(clip);
    if (clip.frameRefs.length > 0 && capabilityView.decision("stageReferenceStrengths").supported) {
      appendSectionHeader(fields, "Keyframe Strengths");
      const inactiveReason = "Keyframe strengths are inactive while Make Them Bonita is enabled.";
      if (stage.characterRefine !== null) {
        const note = document.createElement("p");
        note.className = "vst-detail-note vst-detail-span-full";
        note.textContent = inactiveReason;
        fields.append(note);
      }
      const setRefHover = (refIdx, on) => {
        context.getBoundBody()?.querySelector(
          `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`
        )?.classList.toggle("vst-ref-hover", on);
      };
      clip.frameRefs.forEach((ref, refIdx) => {
        const current = refIdx < stage.frameRefStrengths.length ? stage.frameRefStrengths[refIdx] : STAGE_REF_STRENGTH_MAX;
        const refSlider = buildSlider(
          `Keyframe ${refIdx}`,
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
        if (stage.characterRefine !== null)
          setFieldDisabled(refSlider, true, inactiveReason);
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
      });
    }
    const applicableIcLoras = clip.icLoras.map((entry, entryIdx) => ({ entry, entryIdx })).filter(({ entry }) => entry.stage < 0 || entry.stage === stageIdx);
    const icDecision = capabilityView.decision("icLora");
    if (applicableIcLoras.length === 0 || !icDecision.supported) return;
    appendSectionHeader(fields, "IC-LoRA Guide Strengths");
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
      const row = buildField(shortModelName(displayName), guideStrength);
      row.classList.add("vst-stage-iclora-strength-row");
      row.title = displayName;
      icGroup.appendChild(row);
    });
    fields.appendChild(icGroup);
  };

  // frontend/detailStrip/stagePanel/samplingSection.ts
  var STEPS_SLIDER_MAX = 20;
  var CFG_SCALE_SLIDER_MAX = 10;
  var appendStageDenoisingSection = (bindings) => {
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
        },
        { sliderMax: Math.min(STEPS_SLIDER_MAX, defaults.stepsMax) }
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
        },
        { sliderMax: Math.min(CFG_SCALE_SLIDER_MAX, defaults.cfgScaleMax) }
      )
    );
  };
  var appendStageRefinementControls = (bindings) => {
    const { stage, defaults, fields, slider } = bindings;
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
    const bonitaEnabled = stage.characterRefine !== null;
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
      const bonita = fields.querySelector(
        ".vst-character-refine-section"
      );
      if (bonita) updateCharacterRefineAvailability(bonita, upscale);
      const disabled = bonitaEnabled || Math.abs(upscale - 1) < UPSCALE_EPSILON;
      setFieldDisabled(
        methodField,
        disabled,
        bonitaEnabled ? "Upscale is unavailable while Make Them Bonita is on" : "Set Upscale above 1× to choose a method"
      );
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
    setFieldDisabled(
      upscaleSlider,
      bonitaEnabled,
      "Upscale is unavailable while Make Them Bonita is on"
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
        options ? {
          title: options.title,
          help: options.help,
          sliderMin: options.sliderMin,
          sliderMax: options.sliderMax,
          isPot: options.isPot
        } : void 0
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
    appendStageDenoisingSection(bindings);
    appendStageSamplerSchedulerSection(bindings);
    if (isRefine) {
      const heading = document.createElement("div");
      heading.className = "vst-detail-sec vst-detail-span-full vst-detail-crumb vst-detail-subsection-crumb";
      heading.setAttribute("role", "heading");
      heading.setAttribute("aria-level", "4");
      heading.textContent = "Refinement";
      fields.append(heading);
      appendStageRefinementControls(bindings);
      appendStageUpscaleSection(bindings, isRefine);
    }
    appendStageReferenceGuideSection(bindings);
    appendStageAttentionSection(bindings);
    appendStageLorasSection(bindings);
    appendCharacterRefineSection(bindings);
    if (initVideoStage0) {
      const note = document.createElement("p");
      note.className = "vst-detail-note vst-stage-passthrough-note";
      note.textContent = "This stage starts from the source footage — Control sets how much is re-generated (0 passes it through).";
      column.insertBefore(note, column.firstChild);
    }
    return column;
  };

  // frontend/multiSelectPicker.ts
  var pickerSequence = 0;
  var selectedValues = (picker) => Array.from(
    picker.querySelectorAll(
      "[data-vst-multi-select-option]"
    )
  ).filter((input2) => input2.checked).map((input2) => input2.value);
  var setOptionChecked = (picker, value, checked) => {
    for (const input2 of picker.querySelectorAll(
      "[data-vst-multi-select-option]"
    )) {
      if (input2.value === value) {
        input2.checked = checked;
      }
    }
  };
  var buildMultiSelectPicker = (options) => {
    const picker = document.createElement("div");
    picker.className = "vst-multi-select";
    picker.dataset.vstMultiSelect = "true";
    const nativeSelect = document.createElement("select");
    nativeSelect.className = "vst-multi-select-native";
    nativeSelect.multiple = true;
    nativeSelect.hidden = true;
    const trigger = document.createElement("div");
    trigger.className = "vst-multi-select-trigger";
    trigger.dataset.vstMultiSelectTrigger = "true";
    trigger.role = "combobox";
    trigger.tabIndex = 0;
    trigger.title = `${options.label} - click to edit`;
    trigger.setAttribute("aria-haspopup", "listbox");
    trigger.setAttribute("aria-expanded", "false");
    const pills = document.createElement("span");
    pills.className = "vst-multi-select-pills";
    const caret = document.createElement("span");
    caret.className = "vst-multi-select-caret";
    caret.textContent = "▾";
    trigger.append(pills, caret);
    const panel = document.createElement("div");
    panel.className = "vst-multi-select-panel";
    panel.dataset.vstMultiSelectPanel = "true";
    panel.hidden = true;
    panel.setAttribute("popover", "manual");
    panel.id = `vst_multi_select_panel_${++pickerSequence}`;
    trigger.setAttribute("aria-controls", panel.id);
    const filter = document.createElement("input");
    filter.type = "text";
    filter.className = "auto-text vst-multi-select-filter";
    filter.dataset.vstMultiSelectFilter = "true";
    filter.placeholder = options.filterPlaceholder;
    filter.autocomplete = "off";
    const list2 = document.createElement("div");
    list2.className = "vst-multi-select-list";
    list2.role = "listbox";
    list2.setAttribute("aria-multiselectable", "true");
    const selected = new Set(options.selected);
    for (const option of options.options) {
      const nativeOption = document.createElement("option");
      nativeOption.value = option.value;
      nativeOption.textContent = option.label;
      nativeOption.selected = selected.has(option.value);
      nativeSelect.appendChild(nativeOption);
      const row = document.createElement("label");
      row.className = "vst-multi-select-option";
      const input2 = document.createElement("input");
      input2.type = "checkbox";
      input2.className = "auto-checkbox";
      input2.dataset.vstMultiSelectOption = "true";
      input2.value = option.value;
      input2.checked = selected.has(option.value);
      const text2 = document.createElement("span");
      text2.textContent = option.label;
      row.append(input2, text2);
      list2.appendChild(row);
    }
    const actions = document.createElement("div");
    actions.className = "vst-multi-select-actions";
    const count = document.createElement("span");
    count.className = "vst-multi-select-count";
    const clear = document.createElement("button");
    clear.type = "button";
    clear.className = "basic-button small-button vst-multi-select-clear";
    clear.textContent = "Clear";
    actions.append(count, clear);
    panel.append(filter, list2, actions);
    picker.append(nativeSelect, trigger, panel);
    const renderSelection = () => {
      const values = selectedValues(picker);
      const selectedSet = new Set(values);
      for (const option of Array.from(nativeSelect.options)) {
        option.selected = selectedSet.has(option.value);
      }
      pills.replaceChildren();
      for (const option of options.options) {
        if (!selectedSet.has(option.value)) {
          continue;
        }
        const pill = document.createElement("span");
        pill.className = "vst-multi-select-pill";
        pill.append(option.label);
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "vst-multi-select-pill-remove";
        remove.dataset.vstMultiSelectRemove = option.value;
        remove.title = `Remove ${option.label}`;
        remove.setAttribute("aria-label", remove.title);
        remove.textContent = "×";
        pill.appendChild(remove);
        pills.appendChild(pill);
      }
      if (values.length === 0) {
        const placeholder = document.createElement("span");
        placeholder.className = "vst-multi-select-placeholder";
        placeholder.textContent = options.placeholder;
        pills.appendChild(placeholder);
      }
      count.textContent = `${values.length} selected`;
      return values;
    };
    const applyFilter = () => {
      const wanted = filter.value.trim().toLowerCase();
      for (const row of list2.querySelectorAll(
        ".vst-multi-select-option"
      )) {
        row.style.display = !wanted || row.textContent?.toLowerCase().includes(wanted) ? "" : "none";
      }
    };
    const positionPanel = () => {
      const rect = trigger.getBoundingClientRect();
      const edge = 8;
      const gap = 4;
      const width = Math.min(rect.width, window.innerWidth - edge * 2);
      panel.style.width = `${width}px`;
      panel.style.left = `${Math.max(edge, Math.min(rect.left, window.innerWidth - width - edge))}px`;
      panel.style.maxHeight = "none";
      const height = panel.offsetHeight;
      const below = Math.max(
        0,
        window.innerHeight - rect.bottom - gap - edge
      );
      const above = Math.max(0, rect.top - gap - edge);
      const upward = height > below && above > below;
      const available = upward ? above : below;
      panel.style.maxHeight = `${available}px`;
      panel.style.top = `${Math.max(edge, upward ? rect.top - gap - Math.min(height, available) : rect.bottom + gap)}px`;
    };
    let cleanup = null;
    const setOpen = (open) => {
      panel.hidden = !open;
      trigger.setAttribute("aria-expanded", `${open}`);
      cleanup?.();
      cleanup = null;
      if (!open) {
        panel.hidePopover?.();
        return;
      }
      filter.value = "";
      applyFilter();
      panel.showPopover?.();
      positionPanel();
      const closeOutside = (event) => {
        if (!picker.contains(event.target)) {
          setOpen(false);
        }
      };
      const closeOnScroll = (event) => {
        if (!panel.contains(event.target)) setOpen(false);
      };
      const observer = new MutationObserver(() => {
        if (!picker.isConnected) setOpen(false);
      });
      observer.observe(document.body, { childList: true, subtree: true });
      document.addEventListener("pointerdown", closeOutside);
      document.addEventListener("scroll", closeOnScroll, true);
      window.addEventListener("resize", positionPanel);
      cleanup = () => {
        observer.disconnect();
        document.removeEventListener("pointerdown", closeOutside);
        document.removeEventListener("scroll", closeOnScroll, true);
        window.removeEventListener("resize", positionPanel);
      };
      filter.focus({ preventScroll: true });
    };
    const commitSelection = () => {
      options.onChange(renderSelection());
    };
    picker.addEventListener("click", (event) => {
      const target = event.target;
      const remove = target.closest(
        "[data-vst-multi-select-remove]"
      );
      if (remove) {
        setOptionChecked(
          picker,
          remove.dataset.vstMultiSelectRemove ?? "",
          false
        );
        commitSelection();
        event.stopPropagation();
        return;
      }
      if (target.closest("[data-vst-multi-select-trigger]")) {
        setOpen(panel.hidden !== false);
        return;
      }
      if (target.closest(".vst-multi-select-clear")) {
        for (const input2 of picker.querySelectorAll(
          "[data-vst-multi-select-option]"
        )) {
          input2.checked = false;
        }
        commitSelection();
      }
    });
    picker.addEventListener("change", (event) => {
      if (event.target.matches(
        "[data-vst-multi-select-option]"
      )) {
        commitSelection();
      }
    });
    filter.addEventListener("input", () => {
      applyFilter();
      if (!panel.hidden) positionPanel();
    });
    picker.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        setOpen(false);
        trigger.focus();
        return;
      }
      if ((event.key === "Enter" || event.key === " ") && event.target === trigger) {
        event.preventDefault();
        setOpen(panel.hidden !== false);
      }
    });
    renderSelection();
    return picker;
  };

  // frontend/detailStrip/videoStagesSettings.ts
  var STORAGE_PREFIX = "videostages";
  var buildBonitaInstallSetting = () => {
    const installed = CHARACTER_REFINE_REQUIRED_FEATURES.every(
      (feature) => getVideoStagesHostBridge().hasBackendFeature(feature)
    );
    const section = document.createElement("fieldset");
    section.id = "vst_bonita_install";
    section.className = "vst-bonita-install-setting";
    const legend = document.createElement("legend");
    legend.textContent = "Make Them Bonita";
    const hint = document.createElement("small");
    if (installed) {
      hint.textContent = "ComfyUI-Bonita is installed.";
    } else {
      const repository = document.createElement("a");
      repository.href = CHARACTER_REFINE_REPOSITORY_URL;
      repository.target = "_blank";
      repository.rel = "noopener noreferrer";
      repository.textContent = "ComfyUI-Bonita";
      hint.append(
        "Install ",
        repository,
        " - tracked detail repair for supported video models"
      );
    }
    const install = buildDetailActionButton({
      label: installed ? "ComfyUI-Bonita Installed" : "Install ComfyUI-Bonita",
      title: installed ? "ComfyUI-Bonita is installed" : "Install ComfyUI-Bonita",
      className: "vst-install-bonita",
      disabled: installed,
      onClick: () => {
        if (!installHostFeature(
          CHARACTER_REFINE_INSTALL_FEATURE,
          section.id
        )) {
          getVideoStagesHostBridge().showError(
            "SwarmUI's ComfyUI feature installer is unavailable."
          );
        }
      }
    });
    install.dataset.vstInstallBonita = "true";
    section.append(legend, hint, install);
    return section;
  };
  var buildLoraFolderSetting = (onChange) => {
    const folders = availableLoraFolders(
      getDropdownOptions("loras", "input_loras").values
    );
    const saved = getTimelineAuthoringSettings().loraFolders;
    const section = document.createElement("fieldset");
    section.className = "vst-lora-folder-setting";
    const legend = document.createElement("legend");
    legend.textContent = "LoRA folders";
    const hint = document.createElement("small");
    hint.textContent = "Only checked top-level folders appear in VideoStages LoRA dropdowns.";
    const picker = buildMultiSelectPicker({
      label: "LoRA folders",
      placeholder: "No folders selected",
      filterPlaceholder: "Filter LoRA folders...",
      options: folders.map((folder) => ({
        value: folder,
        label: folder === ROOT_LORA_FOLDER ? "(Root)" : folder
      })),
      selected: saved ?? folders,
      onChange: (included) => {
        setTimelineAuthoringSetting(
          "loraFolders",
          included.length === folders.length ? null : included
        );
        onChange();
      }
    });
    section.append(legend, hint, picker);
    return section;
  };
  var resetVideoStages = () => {
    const empty = getState();
    empty.dimsExplicit = false;
    empty.clips = [];
    empty.audioTracks = [];
    saveState(empty, {
      notifyDomChange: true,
      origin: "timeline"
    });
    try {
      const keys = [];
      for (let index = 0; index < localStorage.length; index++) {
        const key = localStorage.key(index);
        if (key?.toLowerCase().startsWith(STORAGE_PREFIX)) {
          keys.push(key);
        }
      }
      for (const key of keys) {
        localStorage.removeItem(key);
      }
    } catch {
    }
    resetRememberedAccordionSections();
  };
  var buildVideoStagesSettingsFields = (onLoraFoldersChanged) => {
    const fields = document.createElement("div");
    fields.className = "vst-videostages-settings-fields";
    fields.append(
      buildLoraFolderSetting(onLoraFoldersChanged),
      buildBonitaInstallSetting()
    );
    return fields;
  };
  var buildResetVideoStagesButton = (afterReset) => buildDetailActionButton({
    label: "Reset VideoStages",
    title: "Clear all saved VideoStages data and settings",
    className: "small-button vst-reset-videostages",
    variant: "interrupt",
    onClick: () => {
      resetVideoStages();
      afterReset();
    }
  });

  // frontend/droppedReferenceMedia.ts
  var EXTENSION_KINDS = Object.fromEntries(
    Object.entries({
      image: [
        "avif",
        "bmp",
        "gif",
        "heic",
        "heif",
        "jpeg",
        "jpg",
        "png",
        "svg",
        "tif",
        "tiff",
        "webp"
      ],
      video: [
        "3g2",
        "3gp",
        "avi",
        "m4v",
        "mkv",
        "mov",
        "mp4",
        "mpeg",
        "mpg",
        "ogv",
        "webm"
      ],
      audio: [
        "aac",
        "flac",
        "m4a",
        "mp3",
        "oga",
        "ogg",
        "opus",
        "wav",
        "weba"
      ]
    }).flatMap(
      ([kind, extensions]) => extensions.map((extension) => [extension, kind])
    )
  );
  var droppedReferenceKindHint = (file) => {
    const mimeKind = file.type.split("/", 1)[0].toLowerCase();
    if (mimeKind in CLIP_REFERENCE_KIND_INFO) {
      return mimeKind;
    }
    const extension = file.name.toLowerCase().match(/\.([^.]+)$/)?.[1] ?? "";
    return EXTENSION_KINDS[extension] ?? null;
  };
  var readDroppedReferenceMedia = async (file) => {
    const hintedKind = droppedReferenceKindHint(file);
    if (!hintedKind) {
      return null;
    }
    const data = await fileAsDataUri(file);
    if (!data) {
      return null;
    }
    const uploadedMedia = {
      data: storeBrowserMedia(data),
      fileName: file.name
    };
    if (hintedKind === "image") {
      return { kind: "image", uploadedMedia, mediaDurationSeconds: 0 };
    }
    if (hintedKind === "audio") {
      return {
        kind: "audio",
        uploadedMedia,
        mediaDurationSeconds: roundToTenth(
          await probeMediaDurationSeconds(data)
        )
      };
    }
    const video = await probeReferenceVideo(data);
    return {
      kind: video?.hasMultipleFrames === false ? "image" : "video",
      uploadedMedia,
      mediaDurationSeconds: video?.hasMultipleFrames === false ? 0 : roundToTenth(video?.durationSeconds ?? 0)
    };
  };

  // frontend/clipEditor/referenceDrops.ts
  var bindReferenceDrop = (button2, clipId, kind, operations) => {
    const addFiles = async (transfer) => {
      const files = await collectDroppedFiles(transfer);
      if (kind === "keyframe") {
        const uploads = await Promise.all(
          files.map(async (file) => {
            const data = await fileAsDataUri(file);
            return data ? { data: storeBrowserMedia(data), fileName: file.name } : null;
          })
        );
        const added2 = operations.addRefEntries(
          clipId,
          uploads.flatMap((upload) => upload ? [upload] : [])
        );
        if (added2 < files.length) {
          const skipped2 = files.length - added2;
          getVideoStagesHostBridge().showError(
            `Added ${added2} of ${files.length} keyframes. ${skipped2} ${skipped2 === 1 ? "file" : "files"} could not be read or no supported keyframe position remained.`
          );
        }
        return;
      }
      const results = await Promise.all(
        files.map(async (file) => ({
          file,
          media: await readDroppedReferenceMedia(file).catch(() => null)
        }))
      );
      const references = results.flatMap(
        ({ media }) => media ? [media] : []
      );
      const added = operations.addClipReferences(clipId, references);
      if (added < references.length) {
        getVideoStagesHostBridge().showError(
          "This architecture accepts only image references for identity verification."
        );
      }
      const skipped = results.filter(({ media }) => !media).map(({ file }) => file.name);
      if (skipped.length > 0) {
        getVideoStagesHostBridge().showError(
          `Unsupported reference files: ${skipped.join(", ")}`
        );
      }
    };
    button2.addEventListener("dragover", (event) => {
      if (button2.disabled || !hasDroppedFiles(event.dataTransfer)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      button2.classList.add("vst-file-drop-hover");
      if (event.dataTransfer) {
        event.dataTransfer.dropEffect = "copy";
      }
    });
    button2.addEventListener("dragleave", (event) => {
      if (event.relatedTarget instanceof Node && button2.contains(event.relatedTarget)) {
        return;
      }
      button2.classList.remove("vst-file-drop-hover");
    });
    button2.addEventListener("drop", (event) => {
      button2.classList.remove("vst-file-drop-hover");
      if (button2.disabled || !event.dataTransfer || !hasDroppedFiles(event.dataTransfer)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      void addFiles(event.dataTransfer).catch(() => {
        getVideoStagesHostBridge().showError(
          "Could not read dropped files."
        );
      });
    });
  };

  // frontend/clipEditor/sections.ts
  function clipSections(state, clipIdx, context) {
    const clip = state.clips[clipIdx];
    const snapshot = context.authoring();
    const capabilities = snapshot.capabilities.forClip(clip);
    const singleton = (key, label, build, selection) => ({
      key,
      label,
      items: [
        {
          key: `${clip.id}/${key}`,
          label,
          selection,
          fields: () => build(scopeFields(context, `${clip.id}/${key}`))
        }
      ]
    });
    const remove = (run) => ({
      key: "remove-item",
      label: "Delete",
      kind: "delete",
      run
    });
    const repeated = (key, label, entries, build, selection, add, removeAt, visible = true, layout = "cards") => visible ? [
      {
        key,
        label,
        layout,
        actions: [
          {
            key: `add-${key}`,
            label: `+ Add ${label === "Stages" ? "stage" : label === "Keyframes" ? "keyframe" : label === "References" ? "reference" : label === "Relay prompts" ? "relay prompt" : "IC-LoRA"}`,
            kind: "add",
            run: add
          }
        ],
        items: entries.map((entry, index) => {
          const scope = `${clip.id}/${key}/${entry.id || index}`;
          const contextLabel = key === "stages" ? `S${index}` : String(index);
          return {
            key: scope,
            contextLabel,
            label: key === "stages" ? `Stage ${contextLabel}` : `${label} ${contextLabel}`,
            selection: selection(index),
            fields: () => build(
              scopeFields(context, scope),
              entry,
              index
            ),
            actions: key === "stages" && index === 0 ? [] : [remove(() => removeAt(index))]
          };
        })
      }
    ] : [];
    const structural = (edit) => context.structuralCommit((clips) => {
      const target = clips[clipIdx];
      if (!target) return null;
      edit(target);
      return "render";
    });
    const prompt = singleton(
      "prompt",
      "Prompt & clip",
      (ctx) => [
        buildMajorPromptFields(ctx, clip, clipIdx, { rows: 3 }),
        buildClipColumn(
          ctx,
          clip,
          clipIdx,
          capabilities.authoringState(
            "referenceFraming",
            clip.refFraming !== "crop"
          ),
          capabilities.architectureId !== "minimax" ? "hidden" : getVideoStagesHostBridge().hasBackendFeature(
            H3_TEXT_ENCODER_FEATURE
          ) ? "ready" : "install",
          capabilities.audioSourceKinds.includes("Native")
        )
      ],
      { kind: "prompt-major", clipIdx }
    );
    if (clipIdx > 0)
      prompt.actions = [
        {
          key: "toggle-clip",
          label: clip.skipped ? "Include clip" : "Skip clip",
          run: () => context.toggleClipSkip(clipIdx)
        }
      ];
    const stages = repeated(
      "stages",
      "Stages",
      clip.stages,
      (ctx, entry, index) => buildStageParamsColumn(
        ctx,
        clip,
        clipIdx,
        index,
        entry,
        snapshot.defaults
      ),
      (index) => ({ kind: "clip", clipIdx, stageIdx: index }),
      () => context.addStage(clipIdx),
      (index) => context.deleteStage(clipIdx, index)
    );
    for (const [index, item] of stages[0].items.entries()) {
      if (index > 0)
        item.toggle = {
          key: "toggle-stage",
          label: `Enable stage S${index}`,
          checked: !clip.stages[index].skipped,
          run: () => context.toggleStageSkip(clipIdx, index)
        };
    }
    const sections = [
      prompt,
      ...stages,
      ...repeated(
        "keyframes",
        "Keyframes",
        clip.frameRefs,
        (ctx, entry, index) => buildKeyframeFields(
          ctx,
          clip,
          clipIdx,
          entry,
          index,
          state.fps
        ),
        (index) => ({ kind: "ref", clipIdx, refIdx: index }),
        () => context.addRefEntry(clipIdx),
        (index) => context.deleteRefEntry(clipIdx, index),
        capabilities.decision("frameReferences").supported || clip.frameRefs.length > 0,
        "rows"
      ),
      ...repeated(
        "references",
        "References",
        clip.references,
        (ctx, _entry, index) => buildClipReferenceFields(
          ctx,
          clipIdx,
          index,
          state.clips,
          state.fps
        ),
        (index) => ({ kind: "clip-ref", clipIdx, referenceIdx: index }),
        () => context.addClipReference(clipIdx),
        (index) => context.deleteClipReference(clipIdx, index),
        clipReferenceAuthoring(capabilities).decision.supported || clip.references.length > 0,
        "rows"
      ),
      ...repeated(
        "ic-loras",
        "IC-LoRAs",
        clip.icLoras,
        (ctx, _entry, index) => buildArchitectureIcLoraFields(
          ctx,
          clip,
          clipIdx,
          snapshot.defaults,
          index
        ),
        (index) => ({ kind: "ic-lora", clipIdx, entryIdx: index }),
        () => structural((target) => {
          appendDefaultIcLora(target, snapshot.defaults);
        }),
        (index) => structural((target) => {
          removeIcLoraEntryAt(target, index);
        }),
        capabilities.decision("icLora").supported || clip.icLoras.length > 0,
        "rows"
      ),
      ...repeated(
        "relay-prompts",
        "Relay prompts",
        clip.promptWindows,
        (ctx, _entry, index) => buildRelayPromptFields(ctx, clip, clipIdx, index),
        (index) => ({ kind: "prompt-minor", clipIdx, windowIdx: index }),
        () => context.addPromptWindow(clipIdx),
        (index) => context.deleteWindowEntry(clipIdx, index)
      ),
      singleton(
        "source-video",
        "Source video",
        (ctx) => buildInitVideoFields(
          ctx,
          clip,
          clipIdx,
          clipIdx === 0 && clip.audioOnly === true || clipIdx === 1 && state.clips[0]?.audioOnly === true
        ),
        { kind: "clip", clipIdx, stageIdx: 0 }
      ),
      ...capabilities.clipAudio.supported || clip.uploadedAudio || clip.audioSource !== defaultAuthoringAudioSource(capabilities.audioSourceKinds) || clip.reuseAudio || clip.clipLengthFromAudio || clip.clipLengthFromControlNet || clip.saveAudioTrack ? [
        singleton(
          "audio",
          "Audio",
          (ctx) => buildBaseAudioFields(ctx, clipIdx, state),
          { kind: "audio", clipIdx }
        )
      ] : [],
      ...capabilities.decision("retake").supported || clip.retake ? [
        singleton(
          "retake",
          "Retake",
          (ctx) => buildRetakeFields(ctx, clip, clipIdx),
          { kind: "retake", clipIdx }
        )
      ] : []
    ];
    if (clipIdx > 0) {
      const preceding = clipIdx - 1;
      const relationship = clipRelationship(state.clips, preceding);
      const leftClipIdx = relationship === SOURCE_AUDIO_RELATIONSHIP || relationship === SOURCE_VIDEO_RELATIONSHIP ? preceding : executableBoundaries(state.clips).find(
        (boundary) => boundary.rightIdx === clipIdx
      )?.leftIdx ?? preceding;
      sections.push(
        singleton(
          "join",
          "Join from previous clip",
          (ctx) => buildBoundaryFields(
            ctx,
            { kind: "boundary", leftClipIdx },
            state
          ),
          { kind: "boundary", leftClipIdx }
        )
      );
    }
    sections.push(
      singleton(
        "output",
        "Output settings",
        (ctx) => buildSettingsFields(ctx, state)
      )
    );
    sections.push(
      singleton("settings", "VideoStages settings", (ctx) => {
        const fields = buildVideoStagesSettingsFields(() => {
          window.dispatchEvent(
            new Event(TIMELINE_AUTHORING_SETTINGS_CHANGED)
          );
          ctx.render();
        });
        fields.append(buildResetVideoStagesButton(ctx.render));
        return fields;
      })
    );
    const incoming = clipReferenceAuthoring(capabilities).mode === "all" ? incomingReferenceContinueForClip(
      state.clips,
      state.fps,
      snapshot.capabilities,
      clipIdx
    ) : null;
    const references = sections.find((entry) => entry.key === "references");
    if (references) {
      const preceding = incoming ? [
        {
          kind: "video",
          includeSoundtrack: state.clips[incoming.leftIdx].boundaryOutReferenceIncludeSoundtrack
        }
      ] : [];
      const tags = clipReferenceTags(clip.references, preceding);
      for (const [index, item] of references.items.entries()) {
        item.label = tags[index];
        item.contextLabel = tags[index];
      }
      if (incoming)
        references.items.unshift({
          key: `${clip.id}/incoming-reference`,
          label: `<Video 1> · From Clip ${incoming.leftIdx}`,
          contextLabel: `<Video 1> · From Clip ${incoming.leftIdx}`,
          selection: {
            kind: "boundary-ref",
            leftClipIdx: incoming.leftIdx
          },
          fields: () => buildIncomingReferenceFields(
            scopeFields(context, `${clip.id}/incoming-reference`),
            clipIdx,
            state.clips,
            state.fps
          )
        });
    }
    const retake = sections.find((entry) => entry.key === "retake");
    if (retake && clip.retake)
      retake.actions = [remove(() => context.removeRetake(clipIdx))];
    const decisions = {
      keyframes: capabilities.decision("frameReferences"),
      references: clipReferenceAuthoring(capabilities).decision,
      "ic-loras": capabilities.decision("icLora"),
      retake: capabilities.decision("retake"),
      "relay-prompts": capabilities.decision("promptRelay")
    };
    for (const section of sections) {
      const decision = decisions[section.key];
      if (decision && !decision.supported) {
        for (const action of section.actions ?? []) {
          if (action.kind === "add") {
            action.disabled = true;
            action.title = decision.reason;
          }
        }
        for (const item of section.items) {
          const build = item.fields;
          item.fields = () => {
            const fields = build();
            for (const field of Array.isArray(fields) ? fields : [fields])
              applyPersistedCapabilityRepair(field, decision);
            return fields;
          };
        }
      }
    }
    const relay = sections.findIndex((entry) => entry.key === "relay-prompts");
    if (!decisions["relay-prompts"].supported && !clip.promptWindows.length && relay >= 0)
      sections.splice(relay, 1);
    const disableAdd = (key, reason) => {
      const add = sections.find((entry) => entry.key === key)?.actions?.find((action) => action.kind === "add");
      if (add) {
        add.disabled = true;
        add.title = reason;
      }
    };
    if (clipIdx === 0 && clip.audioOnly) {
      disableAdd(
        "stages",
        "Audio-only source clips do not use video stages."
      );
      disableAdd(
        "keyframes",
        "Audio-only source clips do not use keyframes."
      );
    }
    if (!referenceEndpointPolicy(clip, snapshot.defaults.modelCatalog).available)
      disableAdd(
        "keyframes",
        "The selected models do not publish a supported keyframe endpoint."
      );
    const clipId = clip.id;
    if (context.addRefEntries && context.addClipReferences && clipId) {
      const operations = {
        addRefEntries: context.addRefEntries,
        addClipReferences: context.addClipReferences
      };
      for (const [key, kind] of [
        ["keyframes", "keyframe"],
        ["references", "clip-reference"]
      ]) {
        const add = sections.find((entry) => entry.key === key)?.actions?.find((action) => action.kind === "add");
        if (add)
          add.configure = (button2) => bindReferenceDrop(button2, clipId, kind, operations);
      }
    }
    return sections;
  }

  // frontend/clipEditor/tab.ts
  var EDITOR_BODY_ID = "videostages-editor";
  var EDITOR_TAB_ID = "VideoStages-Timeline-Tab";
  var updateEditorTabIndicator = (enabled) => {
    const link = findHostBottomTabLink(EDITOR_TAB_ID);
    if (!link) return;
    const mark = link.querySelector(".vst-tab-check");
    if (enabled && !mark) {
      const check = element("span", "vst-tab-check", "✓");
      check.setAttribute("aria-hidden", "true");
      link.append(check);
    } else if (!enabled) mark?.remove();
    link.title = enabled ? "Video Stages is enabled" : "";
  };
  var restorePreviewLink = () => {
    const hash = window.location.hash;
    if (!hash.split(",").includes("#VideoStages-Editor-Tab")) return;
    window.history.replaceState(
      null,
      "",
      hash.replace("VideoStages-Editor-Tab", EDITOR_TAB_ID)
    );
    const link = findHostBottomTabLink(EDITOR_TAB_ID);
    if (link) {
      link.click();
      link.setAttribute("aria-selected", "true");
      link.tabIndex = 0;
    }
  };
  function mountClipEditor() {
    const existing = document.getElementById(EDITOR_BODY_ID);
    if (existing) return existing;
    const host = getHostBottomTabMount();
    if (!host) return null;
    const item = element("li", "nav-item");
    item.setAttribute("role", "presentation");
    const link = element("a", "nav-link translate", "VideoStages");
    link.href = `#${EDITOR_TAB_ID}`;
    link.dataset.bsToggle = "tab";
    link.dataset.testid = "clip-editor-tab";
    link.setAttribute("role", "tab");
    link.setAttribute("aria-selected", "false");
    link.tabIndex = -1;
    item.append(link);
    host.nav.insertBefore(item, host.toolsTabItem);
    const pane = element("div", "tab-pane genpage-bottom-tab");
    pane.id = EDITOR_TAB_ID;
    pane.setAttribute("role", "tabpanel");
    const body = element("div", "vst-clip-editor");
    body.id = EDITOR_BODY_ID;
    body.dataset.testid = "clip-editor";
    pane.append(body);
    host.content.append(pane);
    registerBottomTabWithHost(link);
    window.removeEventListener("hashchange", restorePreviewLink);
    window.addEventListener("hashchange", restorePreviewLink);
    restorePreviewLink();
    return body;
  }

  // frontend/clipEditor/view.ts
  function renderEditor(root, state, selected, sections, navigation, collapsed) {
    const strip = flowMap(state, selected, navigation, collapsed);
    root.classList.toggle("vst-editor-map-collapsed", collapsed);
    const workspace = element("div", "vst-editor-workspace");
    const rail = element("nav", "vst-editor-navigation");
    rail.setAttribute("aria-label", "Clip sections");
    rail.dataset.testid = "section-navigation";
    const pane = element("main", "vst-editor-scroll");
    pane.dataset.testid = "clip-sections";
    pane.setAttribute("aria-label", "Clip settings");
    for (const section of sections) {
      const link = actionButton({
        key: `nav-${section.key}`,
        label: section.label,
        run: () => navigation.jump(section.key)
      });
      link.dataset.sectionLink = section.key;
      if ([
        "stages",
        "keyframes",
        "references",
        "ic-loras",
        "relay-prompts"
      ].includes(section.key))
        link.append(element("span", "", String(section.items.length)));
      rail.append(link);
      pane.append(sectionPanel(section, state.clips[selected]?.id || ""));
    }
    if (!state.clips.length) {
      const empty = element("div", "vst-editor-start");
      empty.append(
        element("p", "", "Add a clip to start your video."),
        actionButton({
          key: "add-first-clip",
          label: "+ Add clip",
          run: navigation.addClip
        })
      );
      pane.append(empty);
    }
    workspace.append(rail, pane);
    root.replaceChildren(strip, workspace);
  }

  // frontend/clipEditor/clipEditor.ts
  function selectionSection(selection) {
    switch (selection.kind) {
      case "clip":
        return "stages";
      case "ref":
        return "keyframes";
      case "boundary-ref":
      case "clip-ref":
        return "references";
      case "ic-lora":
        return "ic-loras";
      case "audio":
        return "audio";
      case "prompt-minor":
        return "relay-prompts";
      case "retake":
        return "retake";
      case "boundary":
        return "join";
      default:
        return "prompt";
    }
  }
  function createClipEditor() {
    let session = null;
    let unsubscribers = [];
    let clipId = null;
    let section = "prompt";
    let rendering = false;
    let selecting = false;
    let adding = false;
    let collapsed = false;
    let layoutObserver = null;
    let enabledToggle = null;
    const root = () => document.getElementById(EDITOR_BODY_ID);
    const pane = () => root()?.querySelector(".vst-editor-scroll");
    const selectedIndex = () => Math.max(
      0,
      getState().clips.findIndex((clip) => clip.id === clipId)
    );
    const silentlySelect = (selection) => {
      if ("clipIdx" in selection)
        clipId = getState().clips[selection.clipIdx]?.id || null;
      selecting = true;
      try {
        setSelection(selection);
      } finally {
        selecting = false;
      }
    };
    const mark = () => {
      const rail = root()?.querySelector(
        ".vst-editor-navigation"
      );
      if (rail) markNavigation(rail, section);
    };
    const jump = (key) => {
      session?.flush();
      const scroll = pane();
      if (!scroll) return;
      jumpToSection(scroll, key);
      section = key;
      mark();
    };
    const selectClip = (index, destination = section, focusPrompt = false) => {
      session?.flush();
      const clip = getState().clips[index];
      if (!clip) return;
      const changed = clip.id !== clipId;
      clipId = clip.id || null;
      section = destination;
      silentlySelect({ kind: "clip", clipIdx: index, stageIdx: 0 });
      if (changed) render(destination);
      else jump(destination);
      if (focusPrompt)
        root()?.querySelector(
          '[data-section="prompt"] textarea'
        )?.focus({ preventScroll: true });
    };
    const navigation = {
      selectClip,
      moveClip: (from, to) => {
        session?.flush();
        const selected = reorderClip(from, to);
        if (selected !== null) selectClip(selected);
      },
      deleteClip: (index) => {
        session?.flush();
        session?.context.deleteClip?.(index);
      },
      toggleMap: () => {
        collapsed = !collapsed;
        const body = root();
        body?.classList.toggle("vst-editor-map-collapsed", collapsed);
        const button2 = body?.querySelector(
          '[data-testid="toggle-map"]'
        );
        if (button2) {
          button2.textContent = collapsed ? "»" : "«";
          button2.setAttribute("aria-expanded", String(!collapsed));
          button2.setAttribute(
            "aria-label",
            collapsed ? "Expand clip map" : "Collapse clip map"
          );
        }
        if (body) revealSelectedClip(body);
      },
      jump,
      addClip: () => {
        session?.flush();
        adding = true;
        addVideoStagesClip();
      },
      setEnabled: (value) => {
        setVideoStagesEnabled(value);
        render();
      }
    };
    const updateDiagnostics = () => {
      const body = root();
      if (body && session)
        renderEditorDiagnostics(
          body,
          deriveAuthoringDiagnostics(
            getState().clips,
            session.context.authoring().capabilities
          ),
          selectClip
        );
    };
    function render(destination) {
      const body = root();
      if (!body || !session || rendering) return;
      updateEditorTabIndicator(isVideoStagesEnabled());
      const catalog = getArchitectureCatalogSnapshot();
      if (renderBlockingArchitectureCatalogStatus(
        body,
        catalog,
        refreshAuthoritativeArchitectureCatalog
      ))
        return;
      rendering = true;
      try {
        session.beforeRender();
        layoutObserver?.disconnect();
        const state = getState();
        const index = Math.max(
          0,
          state.clips.findIndex((clip) => clip.id === clipId)
        );
        const scrollTop = pane()?.scrollTop || 0;
        const mapTop = body.querySelector(".vst-editor-sequence")?.scrollTop || 0;
        const navLeft = body.querySelector(".vst-editor-navigation")?.scrollLeft || 0;
        clipId = state.clips[index]?.id || null;
        releaseSidebarMediaPreviews(body);
        const sections = state.clips[index] ? clipSections(state, index, session.context) : [];
        renderEditor(body, state, index, sections, navigation, collapsed);
        updateDiagnostics();
        renderRetainedArchitectureCatalogStatus(
          body,
          catalog,
          refreshAuthoritativeArchitectureCatalog
        );
        const scroll = pane();
        if (scroll) {
          scroll.scrollTop = scrollTop;
          if (destination) jumpToSection(scroll, destination);
          scroll.addEventListener(
            "scroll",
            () => {
              section = sectionAtTop(scroll) || "prompt";
              updateStickyItemLabels(scroll);
              mark();
            },
            { passive: true }
          );
        }
        const sequence = body.querySelector(
          ".vst-editor-sequence"
        );
        if (sequence) sequence.scrollTop = mapTop;
        const nav = body.querySelector(
          ".vst-editor-navigation"
        );
        if (nav) nav.scrollLeft = navLeft;
        if (destination) revealSelectedClip(body);
        if (!sections.some((entry) => entry.key === section)) {
          section = "prompt";
          if (scroll) scroll.scrollTop = 0;
        }
        mark();
        getVideoStagesHostBridge().enableSliders(body);
        if (scroll) {
          updateStickyItemLabels(scroll);
          layoutObserver?.observe(scroll);
          for (const item of scroll.querySelectorAll("[data-item-id]"))
            layoutObserver?.observe(item);
        }
      } finally {
        session.afterRender();
        rendering = false;
      }
    }
    const focusItem = (event) => {
      const item = event.target.closest(
        "[data-selection]"
      );
      if (item?.dataset.selection) {
        silentlySelect(
          JSON.parse(item.dataset.selection)
        );
        const target = event.target;
        if (target.isConnected && document.activeElement !== target)
          target.focus({ preventScroll: true });
      }
    };
    const dispose = () => {
      closeManagedModal();
      layoutObserver?.disconnect();
      layoutObserver = null;
      session?.dispose();
      session = null;
      for (const unsubscribe of unsubscribers.splice(0)) unsubscribe();
      enabledToggle?.removeEventListener("change", onEnabled);
      enabledToggle = null;
      const body = root();
      body?.removeEventListener("focusin", focusItem);
      if (body) releaseSidebarMediaPreviews(body);
    };
    const onEnabled = () => render();
    const init = () => {
      dispose();
      const body = mountClipEditor();
      if (!body) return;
      if (typeof ResizeObserver !== "undefined")
        layoutObserver = new ResizeObserver(() => {
          const scroll = pane();
          if (scroll) updateStickyItemLabels(scroll);
        });
      session = createAuthoringSession({
        render,
        isRendering: () => rendering,
        getRoot: root,
        setSelectionSilently: silentlySelect
      });
      body.addEventListener("focusin", focusItem);
      enabledToggle = getGroupToggle();
      enabledToggle?.addEventListener("change", onEnabled);
      unsubscribers = [
        getTimelineStore().subscribe((state, meta) => {
          if (rendering) return;
          const active = document.activeElement;
          const editing = active instanceof HTMLTextAreaElement || active instanceof HTMLInputElement && ["text", "number", "range"].includes(active.type);
          if (meta.hint === "value-only" && editing && body.contains(active)) {
            const strip = body.querySelector(".vst-editor-strip");
            const top = strip?.querySelector(
              ".vst-editor-sequence"
            )?.scrollTop || 0;
            strip?.replaceWith(
              flowMap(state, selectedIndex(), navigation, collapsed)
            );
            const updated = body.querySelector(
              ".vst-editor-sequence"
            );
            if (updated) updated.scrollTop = top;
            updateDiagnostics();
          } else render();
        }),
        subscribeSelection((selection2) => {
          if (selecting || rendering || selection2.kind === "none" || selection2.kind === "audio-track")
            return;
          const index = selection2.kind === "boundary" || selection2.kind === "boundary-ref" ? executableBoundaryForLeftClip(
            getState().clips,
            selection2.leftClipIdx
          )?.rightIdx : selection2.clipIdx;
          if (index === void 0) return;
          clipId = getState().clips[index]?.id || null;
          section = adding ? "prompt" : selectionSection(selection2);
          render(section);
          const scroll = pane();
          if (scroll) revealSelectedItem(scroll, selection2);
          if (adding) {
            adding = false;
            const strip = body.querySelector(
              ".vst-editor-sequence"
            );
            if (strip) revealSelectedClip(body);
            body.querySelector(
              '[data-section="prompt"] textarea'
            )?.focus({ preventScroll: true });
          }
        }),
        subscribeArchitectureCatalog(() => render())
      ];
      const selection = getSelection();
      if (getArchitectureCatalogSnapshot().catalog && "clipIdx" in selection)
        clipId = getState().clips[selection.clipIdx]?.id || null;
      render();
    };
    return {
      init,
      refresh: () => render(),
      flushPending: () => session?.flush(),
      dispose
    };
  }

  // frontend/refineVideoButton.ts
  var REFINE_SOURCE_FILE_NAME = "refine-source";
  var REFINE_STAGE_INDEX = 1;
  var REFINE_OWNED_PARAMS = /* @__PURE__ */ new Set(["images", "videostages"]);
  var COMFY_BUTTON_ID = "video_stages_refine_to_comfy_button";
  var COMFY_ROW_ID = "video_stages_refine_to_comfy_row";
  var COMFY_MARK_CLASS = "video-stages-refine-comfy-mark";
  var DONE_TIMEOUT_MS = 2500;
  var comfyButtonObserver = null;
  var markTimer = null;
  var resolvedPromptParameterOverrides = (originalPrompt, sourceParams) => {
    const sourceKeys = new Map(
      Object.keys(sourceParams).map((key) => [cleanParamName(key), key])
    );
    const overrides = {};
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
  var refineNeedsExtraStageMessage = () => `Refine Video needs another stage or clip in the current timeline. Add one after the source video's last completed stage, then click Refine Video again.`;
  var refineSourceMismatchMessage = () => `Refine Video source metadata does not match the current timeline. Restore the timeline that generated this video, then click Refine Video again.`;
  var entityId = (value) => typeof value === "string" && value.trim() ? value.trim() : null;
  var lastCompletedSourceStage = (serialized) => {
    let parsed = serialized;
    if (typeof serialized === "string") {
      try {
        parsed = JSON.parse(serialized);
      } catch {
        return null;
      }
    }
    if (!isRecord(parsed) || !Array.isArray(parsed.clips)) {
      return null;
    }
    let completed = null;
    for (let clipIndex = 0; clipIndex < parsed.clips.length; clipIndex++) {
      const clip = parsed.clips[clipIndex];
      if (!isRecord(clip)) {
        return null;
      }
      if (clip.skipped === true) {
        break;
      }
      if (!Array.isArray(clip.stages)) {
        return null;
      }
      for (let stageIndex = 0; stageIndex < clip.stages.length; stageIndex++) {
        const stage = clip.stages[stageIndex];
        if (!isRecord(stage)) {
          return null;
        }
        if (stage.skipped === true) {
          break;
        }
        completed = {
          clipIndex,
          clipId: entityId(clip.id),
          stageIndex,
          stageId: entityId(stage.id)
        };
      }
    }
    return completed;
  };
  var currentCompletedStage = (state, source) => {
    const clipIndex = source.clipId ? state.clips.findIndex((clip2) => clip2.id === source.clipId) : source.clipIndex;
    const clip = state.clips[clipIndex];
    if (!clip) {
      return null;
    }
    const stageIndex = source.stageId ? clip.stages.findIndex((stage) => stage.id === source.stageId) : source.stageIndex;
    return stageIndex >= 0 ? { clipIndex, stageIndex } : null;
  };
  var defaultRefinementTarget = (state) => {
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
      return null;
    }
    if (!clip0.audioOnly && clip0.stages.length > REFINE_STAGE_INDEX) {
      return {
        clipIndex: 0,
        stageIndex: REFINE_STAGE_INDEX
      };
    }
    return state.clips.length > 1 ? {
      clipIndex: 1,
      stageIndex: 0
    } : null;
  };
  var refinementTarget = (state, serializedSource) => {
    const source = lastCompletedSourceStage(serializedSource);
    if (!source) {
      return defaultRefinementTarget(state);
    }
    const completed = currentCompletedStage(state, source);
    if (!completed) {
      throw new Error(refineSourceMismatchMessage());
    }
    const nextStageIndex = completed.stageIndex + 1;
    const completedClip = state.clips[completed.clipIndex];
    const laterStagesCanRefine = completed.clipIndex !== 0 || completedClip.audioOnly !== true;
    if (laterStagesCanRefine && completedClip.stages.length > nextStageIndex) {
      return {
        clipIndex: completed.clipIndex,
        stageIndex: nextStageIndex
      };
    }
    const nextClipIndex = completed.clipIndex + 1;
    return state.clips.length > nextClipIndex ? {
      clipIndex: nextClipIndex,
      stageIndex: 0
    } : null;
  };
  var hasRefinementWorkToDo = (state, enabled, serializedSource) => {
    return enabled && refinementTarget(state, serializedSource) !== null;
  };
  var installRefineSource = (clip, data, probe) => {
    clip.audioOnly = false;
    clip.initVideo = initVideoFromProbe(
      probe,
      data,
      REFINE_SOURCE_FILE_NAME,
      clip.duration
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
  var applyRefineToClip = (clip, data, probe, targetStageIndex) => {
    installRefineSource(clip, data, probe);
    if (clip.stages.length > targetStageIndex) {
      applySkipSuffix(clip.stages, targetStageIndex, false);
    }
    for (let index = 0; index < targetStageIndex; index++) {
      clip.stages[index].control = 0;
      clip.stages[index].upscale = 1;
    }
    if (targetStageIndex > 0) {
      clip.retake = null;
    }
  };
  var buildRefineVideoPayload = async (src) => {
    const host = getVideoStagesHostBridge();
    let parsedMetadata = null;
    const currentMetadata = await host.getMediaMetadata(src);
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
    const initialState = getState();
    if (!hasRefinementWorkToDo(
      initialState,
      isVideoStagesEnabled(),
      sourceVideostages
    )) {
      host.showError(refineNeedsExtraStageMessage());
      return null;
    }
    const videoDataUrl = await host.toDataUrl(src);
    const probe = await probeInitVideo(videoDataUrl);
    const state = getState();
    const target = refinementTarget(state, sourceVideostages);
    if (!isVideoStagesEnabled() || !target) {
      host.showError(refineNeedsExtraStageMessage());
      return null;
    }
    const sourceClipIndex = target.stageIndex > 0 ? target.clipIndex : target.clipIndex - 1;
    const sourceClip = state.clips[sourceClipIndex];
    const bakesAceStepFunAudio = isAceStepFunAudioSource(
      sourceClip.audioSource
    );
    const clips = structuredClone(state.clips.slice(target.clipIndex));
    clips[0].skipped = false;
    applyRefineToClip(clips[0], videoDataUrl, probe, target.stageIndex);
    reconcileClipArchitectureIdentity(
      clips[0],
      captureAuthoringTransactionSnapshot().capabilities.catalog
    );
    const sourceDimensions = probe?.width && probe.height ? {
      width: probe.width,
      height: probe.height,
      dimsExplicit: true
    } : {};
    const inputOverrides = {
      videostages: serializeStateForStorage({
        ...state,
        ...sourceDimensions,
        clips
      }),
      images: 1
    };
    const sourceExtraMetadata = isRecord(parsedMetadata) ? readProp(parsedMetadata, "sui_extra_data") : void 0;
    const originalPrompt = isRecord(sourceExtraMetadata) ? readProp(sourceExtraMetadata, "original_prompt") : void 0;
    if (isRecord(params) && typeof originalPrompt === "string") {
      Object.assign(
        inputOverrides,
        resolvedPromptParameterOverrides(originalPrompt, params)
      );
    }
    if (bakesAceStepFunAudio) {
      const aceStepFunModelParam = cleanParamName("AceStepFun Model");
      if (aceStepFunModelParam) {
        inputOverrides[aceStepFunModelParam] = null;
      }
    }
    const prompt = isRecord(params) ? readProp(params, "prompt") : void 0;
    if (typeof prompt === "string") {
      inputOverrides.prompt = prompt;
    }
    const negativePrompt = isRecord(params) ? readProp(params, "negativeprompt") : void 0;
    if (typeof negativePrompt === "string") {
      inputOverrides.negativeprompt = negativePrompt;
    }
    const seed = isRecord(params) ? readProp(params, "seed") : void 0;
    if (typeof seed === "number") {
      inputOverrides.seed = seed;
    }
    if (isRecord(sourceExtraMetadata)) {
      inputOverrides.extra_metadata = Object.fromEntries(
        [
          "original_prompt",
          "original_negativeprompt",
          "videostages_source_sampler_seed"
        ].filter((key) => key in sourceExtraMetadata).map((key) => [key, structuredClone(sourceExtraMetadata[key])])
      );
    }
    return inputOverrides;
  };
  var dispatchRefineVideo = async (src, destination) => {
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
        error instanceof Error ? error.message : "Failed to prepare Refine Video."
      );
      return false;
    }
  };
  var setComfyButtonMark = (mark, timeoutMs = 0) => {
    const button2 = document.getElementById(COMFY_BUTTON_ID);
    if (!button2) {
      return;
    }
    if (markTimer !== null) {
      clearTimeout(markTimer);
      markTimer = null;
    }
    let marker = button2.querySelector(`.${COMFY_MARK_CLASS}`);
    if (!mark) {
      marker?.remove();
      return;
    }
    if (!marker) {
      marker = document.createElement("span");
      marker.className = COMFY_MARK_CLASS;
      marker.style.marginLeft = "0.3rem";
      marker.style.fontWeight = "bold";
      button2.appendChild(marker);
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
  var refineCurrentVideoToComfy = () => {
    const host = getVideoStagesHostBridge();
    const src = host.getCurrentVideoSource();
    if (!src) {
      host.showError("Select a video on the Generate tab first.");
      return;
    }
    setComfyButtonMark("…");
    void dispatchRefineVideo(
      src,
      (target, payload) => target.sendToComfyUiAndSave(payload)
    ).then((success2) => {
      setComfyButtonMark(success2 ? "✓" : "", success2 ? DONE_TIMEOUT_MS : 0);
    });
  };
  var injectRefineVideoToComfyButton = (rootDoc) => {
    if (rootDoc.getElementById(COMFY_BUTTON_ID)) {
      return true;
    }
    const whatTheDuckButton = rootDoc.getElementById(
      "wtd_comfy_save_workflow_button"
    );
    const whatTheDuckRow = whatTheDuckButton?.closest(".wtd-comfy-save-row") ?? whatTheDuckButton?.parentElement;
    if (!whatTheDuckRow) {
      return false;
    }
    const row = rootDoc.createElement("div");
    row.id = COMFY_ROW_ID;
    row.className = "comfy-second-button-row";
    row.style.clear = "both";
    row.style.overflow = "hidden";
    const button2 = rootDoc.createElement("button");
    button2.type = "button";
    button2.id = COMFY_BUTTON_ID;
    button2.className = "basic-button comfy-small-button comfy-left-button";
    button2.title = "Use the selected Generate-tab video for the next VideoStages refinement, open the workflow in ComfyUI, and save the payload and workflow to the server.";
    button2.textContent = "Refine Video to ComfyUI";
    button2.addEventListener("click", refineCurrentVideoToComfy);
    row.appendChild(button2);
    whatTheDuckRow.insertAdjacentElement("afterend", row);
    return true;
  };
  var initRefineVideoToComfyButton = () => {
    if (injectRefineVideoToComfyButton(document) || comfyButtonObserver) {
      return;
    }
    if (!document.body) {
      document.addEventListener(
        "DOMContentLoaded",
        initRefineVideoToComfyButton,
        {
          once: true
        }
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
      subtree: true
    });
  };
  var refineVideoButton = () => {
    const host = getVideoStagesHostBridge();
    const description = "Uses this video as the source for the next refinement stage or clip.";
    host.registerRefineVideoButton(
      (src) => void dispatchRefineVideo(
        src,
        (target, payload) => target.generate(payload)
      ),
      description
    );
    initRefineVideoToComfyButton();
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

  // frontend/videoStagesApplication.ts
  function createVideoStagesApplication(editor) {
    let unsubscribeStore = null;
    let unsubscribeCatalog = null;
    let historyNeedsRebase = true;
    const ready = () => getArchitectureCatalogSnapshot().catalog !== null;
    const history = createTimelineHistory({
      read: () => JSON.stringify(getState()),
      write: (value) => {
        saveState(JSON.parse(value), {
          expectedRevision: getTimelineStore().revision(),
          notifyDomChange: isVideoStagesEnabled(),
          origin: "history"
        });
      }
    });
    const rebaseHistory = () => {
      if (ready() && historyNeedsRebase) {
        history.rebase();
        historyNeedsRebase = false;
      }
    };
    const host = createTimelineHostLifecycle({
      refresh: editor.refresh,
      refreshCatalog: refreshAuthoritativeArchitectureCatalog,
      syncFromCarrier: () => {
        if (!ready()) return;
        rebaseHistory();
        getTimelineStore().syncFromCarrier();
      },
      flushPending: () => {
        if (ready()) editor.flushPending();
      },
      undo: () => ready() && history.undo(),
      redo: () => ready() && history.redo()
    });
    const init = () => {
      const store2 = getTimelineStore();
      store2.invalidate();
      historyNeedsRebase = true;
      unsubscribeStore?.();
      unsubscribeStore = store2.subscribe(() => history.capture());
      unsubscribeCatalog?.();
      unsubscribeCatalog = subscribeArchitectureCatalog((snapshot) => {
        if (snapshot.status === "ready" && snapshot.catalog) {
          store2.invalidate();
          historyNeedsRebase = true;
          rebaseHistory();
        }
      });
      rebaseHistory();
      editor.init();
      host.bind();
      window.removeEventListener(
        TIMELINE_AUTHORING_SETTINGS_CHANGED,
        editor.refresh
      );
      window.addEventListener(
        TIMELINE_AUTHORING_SETTINGS_CHANGED,
        editor.refresh
      );
      loadAuthoritativeArchitectureCatalog();
    };
    const dispose = () => {
      host.dispose();
      window.removeEventListener(
        TIMELINE_AUTHORING_SETTINGS_CHANGED,
        editor.refresh
      );
      unsubscribeStore?.();
      unsubscribeStore = null;
      unsubscribeCatalog?.();
      unsubscribeCatalog = null;
      editor.dispose();
    };
    return { init, dispose };
  }

  // frontend/main.ts
  var application = createVideoStagesApplication(createClipEditor());
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
      "Per-clip prompt sections and prompt windows for VideoStages.",
      () => [
        "\n<videoclip[0]>the first clip's prompt text — everything until the next <videoclip...> tag.",
        "\n<videoclip[0]:1.5-4>a prompt window on the first clip from 1.5s to 4s.",
        "\nVideoStages owns these; structured config (stages, references, keyframes, audio) rides in the hidden Data param."
      ],
      true
    );
  };
  var DATA_INPUT_RETRY_MS = 250;
  var dataInputRetryTimer = null;
  var initVideoStages = () => {
    if (!getVideoStagesHostBridge().hasElement(DATA_INPUT_ID)) {
      warnIfDataInputNeverAppears();
      if (!dataInputRetryTimer) {
        dataInputRetryTimer = setTimeout(() => {
          dataInputRetryTimer = null;
          initVideoStages();
        }, DATA_INPUT_RETRY_MS);
      }
      return;
    }
    try {
      application.init();
      installHostGenerationInputProjector(materializeGenerationMedia);
    } catch (error) {
      console.warn("VideoStages: failed to initialize editor", error);
    }
  };
  getVideoStagesHostBridge().addPostParamBuildStep(initVideoStages);
  registerVideoStagesPromptPrefix();
  refineVideoButton();
  mountClipEditor();
})();
//# sourceMappingURL=video-stages.js.map
