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
  var ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
  var SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
  var ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;
  var isAceStepFunAudioSource = (source) => ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());
  var canUseClipLengthFromAudio = (source) => {
    const normalized = `${source ?? ""}`.trim();
    return normalized === AUDIO_SOURCE_UPLOAD || isAceStepFunAudioSource(normalized);
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
  var buildAudioSourceOptions = (currentValue = "") => {
    const options = [
      { value: AUDIO_SOURCE_NATIVE, label: AUDIO_SOURCE_NATIVE },
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD }
    ];
    for (const ref of getAceStepFunRefs()) {
      options.push({ value: ref, label: getAceStepFunRefLabel(ref) });
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
        const options = buildAudioSourceOptions(select.value);
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

  // frontend/constants.ts
  var REF_FRAME_MIN = 1;
  var DEFAULT_CLIP_DURATION_SECONDS = 5;
  var CLIP_DURATION_MIN = 1;
  var CLIP_DURATION_MAX = 9999;
  var CLIP_DURATION_SLIDER_MAX = 60;
  var CLIP_DURATION_SLIDER_STEP = 0.5;
  var ROOT_DIMENSION_MIN = 256;
  var DIMENSIONS_PRESET_CUSTOM_VALUE = "custom";
  var ROOT_FPS_MIN = 4;
  var CLIP_AUDIO_UPLOAD_FIELD = "uploadedAudio";
  var CLIP_AUDIO_UPLOAD_LABEL = "Audio Upload";
  var CLIP_AUDIO_UPLOAD_DESCRIPTION = "Audio file to attach to this clip. Used when Audio Source is set to Upload.";
  var CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3"
  ];
  var STAGE_REF_STRENGTH_MIN = 0.1;
  var STAGE_REF_STRENGTH_MAX = 1;
  var STAGE_REF_STRENGTH_STEP = 0.1;
  var STAGE_REF_STRENGTH_DEFAULT = 0.8;
  var IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;
  var STAGE_REF_STRENGTH_FIELD_PREFIX = "refStrength_";
  var STAGE_CONTROLNET_STRENGTH_MIN = 0;
  var STAGE_CONTROLNET_STRENGTH_MAX = 1;
  var STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
  var STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;
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
  var parseBase2EditStageIndex = (value) => {
    const match = `${value || ""}`.trim().replace(/\s+/g, "").match(/^edit(\d+)$/i);
    if (!match) {
      return null;
    }
    return parseInt(match[1], 10);
  };
  var refUploadKey = (clipIdx, refIdx) => `${clipIdx}:${refIdx}`;
  var parseRefUploadKey = (key) => {
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
  var normalizeUploadFileName = (value) => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
      return null;
    }
    const slashIndex = Math.max(raw.lastIndexOf("/"), raw.lastIndexOf("\\"));
    return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
  };
  var clamp = (value, min, max) => Math.min(Math.max(value, min), max);

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

  // frontend/swarmInputs.ts
  var getClipsInput = () => {
    const el = document.getElementById("input_videostages");
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
      return el;
    }
    return null;
  };
  var ROOT_DIMENSION_WIDTH_INPUT_ID = "input_videostageswidth";
  var ROOT_DIMENSION_HEIGHT_INPUT_ID = "input_videostagesheight";
  var DIMENSIONS_PRESET_SELECT_ID = "input_videostagesdimensions";
  var DIMENSIONS_PRESET_METADATA_INPUT_ID = "input_videostagesdimensionsmetadata";
  var ROOT_FPS_INPUT_ID = "input_videostagesfps";
  var getRootDimensionParamInput = (field) => utils.getInputElement(
    field === "width" ? ROOT_DIMENSION_WIDTH_INPUT_ID : ROOT_DIMENSION_HEIGHT_INPUT_ID
  );
  var getRootFpsParamInput = () => utils.getInputElement(ROOT_FPS_INPUT_ID);
  var getCoreDimensionInput = (field) => {
    const primaryId = field === "width" ? "input_width" : "input_height";
    const fallbackId = field === "width" ? "input_aspectratiowidth" : "input_aspectratioheight";
    return utils.getInputElement(primaryId) ?? utils.getInputElement(fallbackId);
  };
  var getRegisteredRootDimension = (field) => {
    const input = getRootDimensionParamInput(field);
    if (!input) {
      return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
  };
  var getRegisteredRootFps = () => {
    const input = getRootFpsParamInput();
    if (!input) {
      return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_FPS_MIN ? value : null;
  };
  var getCoreDimension = (field) => {
    const input = getCoreDimensionInput(field);
    if (!input) {
      return null;
    }
    const value = Math.round(utils.toNumber(input.value, 0));
    return value >= ROOT_DIMENSION_MIN ? value : null;
  };
  var seedRegisteredDimensionsFromCore = (notifyDomChange = true) => {
    const fields = ["width", "height"];
    for (const field of fields) {
      const ourInput = getRootDimensionParamInput(field);
      if (!ourInput) {
        continue;
      }
      const ourValue = Math.round(utils.toNumber(ourInput.value, 0));
      if (ourValue >= ROOT_DIMENSION_MIN) {
        continue;
      }
      const coreValue = getCoreDimension(field);
      if (coreValue === null) {
        continue;
      }
      ourInput.value = `${coreValue}`;
      if (notifyDomChange) {
        triggerChangeFor(ourInput);
      }
    }
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
  var isAvailableBase2EditReference = (value) => {
    const stageIndex = parseBase2EditStageIndex(value);
    if (stageIndex == null) {
      return false;
    }
    return getBase2EditStageRefs().includes(`edit${stageIndex}`);
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
  var isImageToVideoWorkflow = () => {
    if (isRootTextToVideoModel()) {
      return false;
    }
    const videoModel = utils.getSelectElement("input_videomodel");
    return !!`${videoModel?.value ?? ""}`.trim();
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

  // frontend/dimensionsDropdown.ts
  var DIMENSIONS_PRESET_INFO_ID = "vs_dimensions_preset_info";
  var presetStopsMapCache = null;
  var upscaleBadgeElementsByValueKeyCache = null;
  var readPresetMetadataFromDom = () => {
    const el = document.getElementById(DIMENSIONS_PRESET_METADATA_INPUT_ID);
    let raw = "";
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
      raw = el.value.trim();
    }
    if (!raw) {
      return {};
    }
    try {
      const obj = JSON.parse(raw);
      if (!obj || typeof obj !== "object" || Array.isArray(obj)) {
        return {};
      }
      const out = {};
      const rec = obj;
      for (const k of Object.keys(rec)) {
        const v = rec[k];
        if (Array.isArray(v)) {
          out[k] = v.map((x) => `${x}`);
        }
      }
      return out;
    } catch {
      return {};
    }
  };
  var getPresetStopsMap = () => {
    if (!presetStopsMapCache) {
      presetStopsMapCache = readPresetMetadataFromDom();
    }
    return presetStopsMapCache;
  };
  var splitDimensionLabel = (label) => {
    const [w, h] = label.replace("*", "").split("x");
    return { width: Math.round(Number(w)), height: Math.round(Number(h)) };
  };
  var parsePresetDimensions = (value) => {
    if (!value || value === DIMENSIONS_PRESET_CUSTOM_VALUE) {
      return null;
    }
    return splitDimensionLabel(value);
  };
  var parsePresets = (presetKey) => {
    const presetLines = getPresetStopsMap()[presetKey];
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
  var buildUpscaleBadgeElementsByValueKey = () => {
    const upscaleBadgeElementsByValueKey = /* @__PURE__ */ new Map();
    const stopsMap = getPresetStopsMap();
    const presetKeys = Object.keys(stopsMap);
    const upscaleBadgeElement = (stop) => {
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
    for (let i = 0; i < presetKeys.length; i++) {
      const presetKey = presetKeys[i];
      const stops = parsePresets(presetKey);
      const { width, height } = splitDimensionLabel(presetKey);
      upscaleBadgeElementsByValueKey.set(
        `${width}x${height}`,
        stops.map((s) => upscaleBadgeElement(s))
      );
    }
    return upscaleBadgeElementsByValueKey;
  };
  var suppressManualDimensionPresetGuard = 0;
  var applyDimensionsToInputs = (width, height) => {
    const wIn = getRootDimensionParamInput("width");
    const hIn = getRootDimensionParamInput("height");
    suppressManualDimensionPresetGuard++;
    try {
      if (wIn) {
        wIn.value = `${width}`;
      }
      if (hIn) {
        hIn.value = `${height}`;
      }
      if (wIn) {
        triggerChangeFor(wIn);
      }
      if (hIn) {
        triggerChangeFor(hIn);
      }
    } finally {
      suppressManualDimensionPresetGuard--;
    }
  };
  var applyVideoStagesPresetDimensionsBeforeGenerate = () => {
    const sel = document.getElementById(DIMENSIONS_PRESET_SELECT_ID);
    if (!(sel instanceof HTMLSelectElement)) {
      return;
    }
    const parsed = parsePresetDimensions(sel.value);
    if (!parsed) {
      return;
    }
    applyDimensionsToInputs(parsed.width, parsed.height);
  };
  var updateUpscaleInfoPanel = (select) => {
    const el = document.getElementById(DIMENSIONS_PRESET_INFO_ID);
    if (!(el instanceof HTMLElement)) {
      return;
    }
    const val = select.value;
    let badges = null;
    if (val && val !== DIMENSIONS_PRESET_CUSTOM_VALUE) {
      if (!upscaleBadgeElementsByValueKeyCache) {
        upscaleBadgeElementsByValueKeyCache = buildUpscaleBadgeElementsByValueKey();
      }
      badges = upscaleBadgeElementsByValueKeyCache.get(val) ?? null;
    }
    if (!badges || badges.length === 0) {
      el.replaceChildren();
      el.hidden = true;
      return;
    }
    el.replaceChildren(...badges);
    el.hidden = false;
  };
  var updateSliderVisibility = (select) => {
    const widthIn = getRootDimensionParamInput("width");
    const heightIn = getRootDimensionParamInput("height");
    if (!widthIn || !heightIn) {
      return;
    }
    const widthBox = findParentOfClass(widthIn, "auto-slider-box");
    const heightBox = findParentOfClass(heightIn, "auto-slider-box");
    if (!widthBox || !heightBox) {
      return;
    }
    if (select.value === DIMENSIONS_PRESET_CUSTOM_VALUE) {
      widthBox.style.display = "block";
      heightBox.style.display = "block";
      delete widthBox.dataset.visible_controlled;
      delete heightBox.dataset.visible_controlled;
    } else {
      widthBox.style.display = "none";
      heightBox.style.display = "none";
      widthBox.dataset.visible_controlled = "true";
      heightBox.dataset.visible_controlled = "true";
    }
  };
  var syncSelectFromInputs = (select) => {
    const wIn = getRootDimensionParamInput("width");
    const hIn = getRootDimensionParamInput("height");
    if (!wIn || !hIn) {
      return;
    }
    const bw = Math.round(Number(wIn.value));
    const bh = Math.round(Number(hIn.value));
    const currentVal = select.value;
    if (currentVal && currentVal !== DIMENSIONS_PRESET_CUSTOM_VALUE) {
      const parsed = parsePresetDimensions(currentVal);
      if (parsed && parsed.width === bw && parsed.height === bh && Array.from(select.options).some((o) => o.value === currentVal)) {
        updateSliderVisibility(select);
        updateUpscaleInfoPanel(select);
        return;
      }
    }
    const vk = `${bw}x${bh}`;
    if (Array.from(select.options).some((o) => o.value === vk)) {
      select.value = vk;
    } else {
      select.value = DIMENSIONS_PRESET_CUSTOM_VALUE;
    }
    updateSliderVisibility(select);
    updateUpscaleInfoPanel(select);
  };
  var wireSelectIfNeeded = (select) => {
    if (select.dataset.vsDimPresetWired === "1") {
      return;
    }
    select.dataset.vsDimPresetWired = "1";
    select.addEventListener("change", () => {
      if (select.value !== DIMENSIONS_PRESET_CUSTOM_VALUE) {
        const parsed = parsePresetDimensions(select.value);
        if (parsed) {
          applyDimensionsToInputs(parsed.width, parsed.height);
        }
      }
      updateSliderVisibility(select);
      updateUpscaleInfoPanel(select);
    });
    const onManualDimension = () => {
      if (suppressManualDimensionPresetGuard > 0) {
        return;
      }
      const sel = document.getElementById(DIMENSIONS_PRESET_SELECT_ID);
      if (!(sel instanceof HTMLSelectElement)) {
        return;
      }
      if (sel.value === DIMENSIONS_PRESET_CUSTOM_VALUE) {
        return;
      }
      const wIn = getRootDimensionParamInput("width");
      const hIn = getRootDimensionParamInput("height");
      if (!wIn || !hIn) {
        return;
      }
      const parsedBase = parsePresetDimensions(sel.value);
      if (!parsedBase) {
        return;
      }
      if (Math.round(Number(wIn.value)) !== parsedBase.width || Math.round(Number(hIn.value)) !== parsedBase.height) {
        sel.value = DIMENSIONS_PRESET_CUSTOM_VALUE;
        updateSliderVisibility(sel);
        updateUpscaleInfoPanel(sel);
      }
    };
    const attachDimListeners = (el) => {
      if (!el || !(el instanceof HTMLElement)) {
        return;
      }
      if (el.dataset.vsDimFieldListen === "1") {
        return;
      }
      el.dataset.vsDimFieldListen = "1";
      el.addEventListener("input", onManualDimension);
      el.addEventListener("change", onManualDimension);
    };
    attachDimListeners(getRootDimensionParamInput("width"));
    attachDimListeners(getRootDimensionParamInput("height"));
    attachDimListeners(
      document.getElementById(`${ROOT_DIMENSION_WIDTH_INPUT_ID}_rangeslider`)
    );
    attachDimListeners(
      document.getElementById(
        `${ROOT_DIMENSION_HEIGHT_INPUT_ID}_rangeslider`
      )
    );
  };
  var ensureInfoPanel = (dropdownBox) => {
    if (!dropdownBox) {
      return;
    }
    let infoEl = document.getElementById(DIMENSIONS_PRESET_INFO_ID);
    if (!(infoEl instanceof HTMLDivElement)) {
      if (infoEl) {
        infoEl.remove();
      }
      infoEl = document.createElement("div");
      infoEl.id = DIMENSIONS_PRESET_INFO_ID;
      infoEl.className = "vs-dimensions-info-body";
      infoEl.setAttribute("aria-live", "polite");
    }
    dropdownBox.insertAdjacentElement("afterend", infoEl);
  };
  var wireDimensionsPreset = () => {
    const select = document.getElementById(DIMENSIONS_PRESET_SELECT_ID);
    if (!(select instanceof HTMLSelectElement)) {
      return;
    }
    presetStopsMapCache = null;
    upscaleBadgeElementsByValueKeyCache = null;
    const dropdownBox = findParentOfClass(select, "auto-dropdown-box");
    if (dropdownBox) {
      dropdownBox.classList.add("vs-dimensions-dropdown");
    }
    ensureInfoPanel(dropdownBox);
    syncSelectFromInputs(select);
    wireSelectIfNeeded(select);
    updateSliderVisibility(select);
    updateUpscaleInfoPanel(select);
    autoSelectWidth(select);
  };

  // frontend/ltxModel.ts
  var LTXV2_COMPAT_CLASS_ID = "lightricks-ltx-video-2";
  var getModelCompatClassId = (modelValue) => {
    if (typeof modelsHelpers === "undefined" || !modelsHelpers || typeof modelsHelpers.getDataFor !== "function") {
      return null;
    }
    return modelsHelpers.getDataFor("Stable-Diffusion", modelValue)?.modelClass?.compatClass?.id ?? null;
  };
  var matchesKnownLtxV2Name = (modelValue) => modelValue.startsWith("ltx-") || modelValue.startsWith("ltxv2") || modelValue.includes(LTXV2_COMPAT_CLASS_ID);
  var isLtxVideoModelValue = (modelValue) => {
    const trimmed = `${modelValue ?? ""}`.trim();
    if (!trimmed) {
      return false;
    }
    const compatClassId = getModelCompatClassId(trimmed);
    if (compatClassId !== null) {
      return compatClassId === LTXV2_COMPAT_CLASS_ID;
    }
    return matchesKnownLtxV2Name(trimmed.toLowerCase());
  };

  // frontend/renderUtils.ts
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

  // frontend/types.ts
  var REF_SOURCE_BASE = "Base";
  var REF_SOURCE_REFINER = "Refiner";
  var REF_SOURCE_UPLOAD = "Upload";

  // frontend/wanModel.ts
  var WAN_COMPAT_CLASS_IDS = /* @__PURE__ */ new Set([
    "wan-21",
    "wan-21-14b",
    "wan-21-1_3b",
    "wan-22-5b"
  ]);
  var WAN_22_I2V_14B_MODEL_CLASS_ID = "wan-2_2-image2video-14b";
  var getStableDiffusionModelClass = (modelValue) => {
    if (typeof modelsHelpers === "undefined" || !modelsHelpers || typeof modelsHelpers.getDataFor !== "function") {
      return void 0;
    }
    return modelsHelpers.getDataFor("Stable-Diffusion", modelValue)?.modelClass;
  };
  var getModelCompatClassId2 = (modelValue) => {
    return getStableDiffusionModelClass(modelValue)?.compatClass?.id ?? null;
  };
  var getModelClassId = (modelValue) => {
    return getStableDiffusionModelClass(modelValue)?.id ?? null;
  };
  var matchesKnownWanName = (modelValue) => {
    const lower = modelValue.toLowerCase();
    return lower.includes("wan-2_2-image2video-14b") || lower.includes("wan22") || lower.startsWith("wan-2_1-image2video") || lower.startsWith("wan-2_1-text2video") || lower.startsWith("wan-2_2-ti2v") || lower.startsWith("wan-2_1-flf2v") || lower.startsWith("wan-2_1-vace") || lower.includes("wan-21-14b") || lower.includes("wan-21-1_3b") || lower.includes("wan-22-5b");
  };
  var clipHasWanStage = (clip) => {
    for (let i = 0; i < clip.stages.length; i++) {
      const stage = clip.stages[i];
      if (!stage.skipped && isWanVideoModelValue(stage.model)) {
        return true;
      }
    }
    return false;
  };
  var isWanVideoModelValue = (modelValue) => {
    const trimmed = `${modelValue ?? ""}`.trim();
    if (!trimmed) {
      return false;
    }
    const compatClassId = getModelCompatClassId2(trimmed);
    if (compatClassId !== null && WAN_COMPAT_CLASS_IDS.has(compatClassId)) {
      return true;
    }
    const modelClassId = getModelClassId(trimmed);
    if (modelClassId === WAN_22_I2V_14B_MODEL_CLASS_ID) {
      return true;
    }
    return matchesKnownWanName(trimmed);
  };
  var rawStageListContainsWanModel = (stagesRaw) => {
    for (let i = 0; i < stagesRaw.length; i++) {
      const raw = stagesRaw[i];
      if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
        continue;
      }
      const rec = raw;
      if (rec.skipped) {
        continue;
      }
      const m = `${rec.model ?? ""}`.trim();
      if (m.length > 0 && isWanVideoModelValue(m)) {
        return true;
      }
    }
    return false;
  };

  // frontend/normalization.ts
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.includes("pixel-lanczos") ? "pixel-lanczos" : upscaleMethodValues[0] ?? "pixel-lanczos";
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
      vae: previousStage ? previousStage.vae : defaults.vaeValues[0] ?? "",
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
  var buildDefaultClip = (getRootDefaults2, getDefaultStageModel2, includeDefaultRef = false) => {
    const defaults = getRootDefaults2();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    return {
      expanded: true,
      skipped: false,
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
      vae: `${rawStage.vae ?? fallback.vae ?? ""}`,
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
      stage.upscaleMethod = stageIndexInClip === 0 ? defaults.upscaleMethodValues[0] ?? "pixel-lanczos" : stage.upscaleMethod || fallback.upscaleMethod;
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
  var normalizeWanClipStructuralRefs = (clip) => {
    if (!clipHasWanStage(clip)) {
      return;
    }
    const wanStructuralRefMax = 2;
    if (clip.refs.length > wanStructuralRefMax) {
      clip.refs = clip.refs.slice(0, wanStructuralRefMax);
      for (let s = 0; s < clip.stages.length; s++) {
        clip.stages[s].refStrengths = clip.stages[s].refStrengths.slice(
          0,
          wanStructuralRefMax
        );
      }
    }
    if (clip.refs.length > 0) {
      clip.refs[0] = {
        ...clip.refs[0],
        frame: REF_FRAME_MIN,
        fromEnd: false
      };
    }
    if (clip.refs.length > 1) {
      clip.refs[1] = {
        ...clip.refs[1],
        frame: REF_FRAME_MIN,
        fromEnd: true
      };
    }
  };
  var normalizeClip = (rawClip, getRootDefaults2, getDefaultStageModel2) => {
    const defaults = getRootDefaults2();
    const rawAudioSource = `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`;
    const audioSourceOptions = buildAudioSourceOptions(rawAudioSource);
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
    const refsSource = rawStageListContainsWanModel(stagesRaw) ? refsRaw.slice(0, 2) : refsRaw;
    const refs = refsSource.map(
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
    const controlNetLora = normalizeControlNetLora(
      rawClip.controlNetLora ?? rawClip.ControlNetLora
    );
    const clipLengthFromAudio = canUseClipLengthFromAudio(audioSource2) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet = controlNetLora !== "" && !clipLengthFromAudio && !!(rawClip.clipLengthFromControlNet ?? rawClip.ClipLengthFromControlNet);
    const clip = {
      expanded: normalizeExpanded(rawClip),
      skipped: !!rawClip.skipped,
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
      refs,
      stages
    };
    normalizeWanClipStructuralRefs(clip);
    return clip;
  };

  // frontend/fieldBinding.ts
  var handleUploadFileName = (ref, target, deps) => {
    const { refUploadCache, getClips: getClips2, saveClips: saveClips2 } = deps;
    const clearUpload = (clipIdx2, refIdx2) => {
      ref.uploadFileName = null;
      ref.uploadedImage = null;
      refUploadCache.delete(refUploadKey(clipIdx2, refIdx2));
    };
    if (!(target instanceof HTMLInputElement) || target.type !== "file") {
      ref.uploadFileName = normalizeUploadFileName(target.value);
      if (!ref.uploadFileName) {
        ref.uploadedImage = null;
      }
      return;
    }
    const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
    const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
    if (target.dataset.filedata) {
      const fileName2 = normalizeUploadFileName(
        target.dataset.filename ?? target.files?.[0]?.name ?? null
      );
      ref.uploadedImage = {
        data: target.dataset.filedata,
        fileName: fileName2
      };
      ref.uploadFileName = fileName2;
      return;
    }
    const fileName = normalizeUploadFileName(target.files?.[0]?.name ?? null);
    if (!fileName) {
      clearUpload(clipIdx, refIdx);
      return;
    }
    ref.uploadFileName = fileName;
    ref.uploadedImage = null;
    refUploadCache.cacheSelection({
      clipIdx,
      refIdx,
      fileInput: target,
      getClips: getClips2,
      saveClips: saveClips2
    });
  };
  var syncStageControlNetStrengthDisabled = (target, stage, clip) => {
    const stageCard = target.closest("section[data-stage-idx]");
    if (!(stageCard instanceof HTMLElement)) {
      return;
    }
    const disabled = normalizeControlNetLora(clip.controlNetLora) !== "" && !isLtxVideoModelValue(stage.model);
    const sliders = stageCard.querySelectorAll(
      '[data-stage-field="controlNetStrength"]'
    );
    for (let i = 0; i < sliders.length; i++) {
      sliders[i].disabled = disabled;
    }
  };
  var syncStageUpscaleMethodDisabled = (target, upscale) => {
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
  };
  var syncRefUploadFieldVisibility = (target, source, refUploadCache) => {
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
      refUploadCache.delete(refUploadKey(clipIdx, refIdx));
      clearMediaFileInput(uploadInput);
    }
  };
  var syncClipAudioUploadFieldVisibility = (target, source) => {
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
    const saveAudioTrackField = clipCard.querySelector(
      ".vs-clip-save-audio-track-field"
    );
    if (saveAudioTrackField) {
      saveAudioTrackField.style.display = isAceStepFunAudioSource(source) ? "" : "none";
    }
    const canUseAudioLength = canUseClipLengthFromAudio(source);
    const lengthFromAudioField = clipCard.querySelector(
      ".vs-clip-length-from-audio-field"
    );
    if (lengthFromAudioField) {
      lengthFromAudioField.style.display = canUseAudioLength ? "" : "none";
    }
    const lengthFromAudio = clipCard.querySelector(
      '[data-clip-field="clipLengthFromAudio"]'
    );
    if (lengthFromAudio && !canUseAudioLength) {
      lengthFromAudio.checked = false;
    }
    const controlNetLora = clipCard.querySelector('[data-clip-field="controlNetLora"]');
    const lengthFromControlNet = clipCard.querySelector(
      '[data-clip-field="clipLengthFromControlNet"]'
    );
    const controlNetLengthDisabled = normalizeControlNetLora(controlNetLora?.value ?? "") === "" || !!lengthFromAudio?.checked;
    syncClipAudioLengthDisabled(
      clipCard,
      !canUseAudioLength || !!lengthFromControlNet?.checked
    );
    syncClipControlNetLengthDisabled(clipCard, controlNetLengthDisabled);
    syncClipDurationDisabled(
      clipCard,
      canUseAudioLength && !!lengthFromAudio?.checked || !controlNetLengthDisabled && !!lengthFromControlNet?.checked
    );
  };
  var syncClipAudioLengthDisabled = (clipCard, disabled) => {
    const lengthFromAudio = clipCard.querySelector(
      '[data-clip-field="clipLengthFromAudio"]'
    );
    if (lengthFromAudio) {
      lengthFromAudio.disabled = disabled;
      if (disabled) {
        lengthFromAudio.checked = false;
      }
    }
    const lengthField = clipCard.querySelector(
      ".vs-clip-length-from-audio-field"
    );
    lengthField?.classList.toggle("vs-audio-length-disabled", disabled);
  };
  var syncClipControlNetLengthDisabled = (clipCard, disabled) => {
    const lengthFromControlNet = clipCard.querySelector(
      '[data-clip-field="clipLengthFromControlNet"]'
    );
    if (lengthFromControlNet) {
      lengthFromControlNet.disabled = disabled;
      if (disabled) {
        lengthFromControlNet.checked = false;
      }
    }
    const lengthField = clipCard.querySelector(
      ".vs-clip-length-from-controlnet-field"
    );
    lengthField?.classList.toggle("vs-controlnet-length-disabled", disabled);
  };
  var syncClipDurationDisabled = (clipCard, disabled) => {
    const durationInputs = clipCard.querySelectorAll(
      '.vs-clip-duration-field [data-clip-field="duration"]'
    );
    for (const durationInput of durationInputs) {
      durationInput.disabled = disabled;
    }
  };
  var applyRefField = (clip, ref, field, target, deps) => {
    const { getRootDefaults: getRootDefaults2, refUploadCache, getClips: getClips2, saveClips: saveClips2 } = deps;
    const frameMax = () => getReferenceFrameMax(getRootDefaults2, clip);
    if (field === "source") {
      ref.source = target.value || REF_SOURCE_BASE;
      if (ref.source !== REF_SOURCE_UPLOAD) {
        ref.uploadFileName = null;
        ref.uploadedImage = null;
      }
      return;
    }
    if (field === "frame") {
      const value = parseInt(target.value, 10);
      if (Number.isFinite(value)) {
        ref.frame = clamp(value, REF_FRAME_MIN, frameMax());
      }
      return;
    }
    if (field === "fromEnd") {
      ref.fromEnd = target instanceof HTMLInputElement ? !!target.checked : false;
      return;
    }
    if (field === "uploadFileName") {
      handleUploadFileName(ref, target, {
        refUploadCache,
        getClips: getClips2,
        saveClips: saveClips2
      });
      return;
    }
  };
  var applyStageField = (stage, field, target, getRootDefaults2, clip = null) => {
    const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
    const refStrengthIdx = parseStageRefStrengthIndex(field);
    if (refStrengthIdx != null) {
      const value = parseFloat(target.value);
      if (Number.isFinite(value)) {
        stage.refStrengths[refStrengthIdx] = normalizeStageRefStrengthValue(value);
      }
      return;
    }
    if (field === "model") {
      stage.model = target.value;
      if (clip != null) {
        normalizeWanClipStructuralRefs(clip);
      }
      return;
    }
    if (field === "vae") {
      stage.vae = target.value;
      return;
    }
    if (field === "sampler") {
      stage.sampler = target.value;
      return;
    }
    if (field === "scheduler") {
      stage.scheduler = target.value;
      return;
    }
    if (field === "upscaleMethod") {
      stage.upscaleMethod = target.value;
      return;
    }
    if (field === "control") {
      if (stageIdx === 0) {
        const defaults = getRootDefaults2();
        stage.control = clamp(
          defaults.control,
          defaults.controlMin,
          defaults.controlMax
        );
        return;
      }
      const value = parseFloat(target.value);
      if (Number.isFinite(value)) {
        const defaults = getRootDefaults2();
        stage.control = clamp(
          value,
          defaults.controlMin,
          defaults.controlMax
        );
      }
      return;
    }
    if (field === "controlNetStrength") {
      if (target.disabled) {
        return;
      }
      const value = parseFloat(target.value);
      if (Number.isFinite(value)) {
        stage.controlNetStrength = normalizeStageControlNetStrengthValue(value);
      }
      return;
    }
    if (field === "upscale") {
      if (stageIdx === 0) {
        stage.upscale = getRootDefaults2().upscale;
        return;
      }
      const value = parseFloat(target.value);
      if (Number.isFinite(value)) {
        const defaults = getRootDefaults2();
        stage.upscale = clamp(
          value,
          defaults.upscaleMin,
          defaults.upscaleMax
        );
      }
      return;
    }
    if (field === "steps") {
      const value = parseInt(target.value, 10);
      if (Number.isFinite(value)) {
        const defaults = getRootDefaults2();
        stage.steps = Math.round(
          clamp(value, defaults.stepsMin, defaults.stepsMax)
        );
      }
      return;
    }
    if (field === "cfgScale") {
      const value = parseFloat(target.value);
      if (Number.isFinite(value)) {
        const defaults = getRootDefaults2();
        stage.cfgScale = clamp(
          value,
          defaults.cfgScaleMin,
          defaults.cfgScaleMax
        );
      }
      return;
    }
  };

  // frontend/rootDefaults.ts
  var STAGE_UPSCALE_PREFIXES = ["pixel-", "model-", "latent-", "latentmodel-"];
  var isStageUpscaleMethod = (value) => STAGE_UPSCALE_PREFIXES.some((prefix) => value.startsWith(prefix));
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
  var getRootDefaults = () => {
    let model = utils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
      model = utils.getSelectElement("input_model");
    }
    const vae = utils.getSelectElement("input_vae");
    const loras = getDropdownOptions("loras", "input_loras");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const allUpscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const allUpscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
    const stageUpscaleValues = [];
    const stageUpscaleLabels = [];
    for (let i = 0; i < allUpscaleMethodValues.length; i++) {
      const value = allUpscaleMethodValues[i];
      if (isStageUpscaleMethod(value)) {
        stageUpscaleValues.push(value);
        stageUpscaleLabels.push(allUpscaleMethodLabels[i]);
      }
    }
    const fallbackUpscaleMethods = [
      "pixel-lanczos",
      "pixel-bicubic",
      "pixel-area",
      "pixel-bilinear",
      "pixel-nearest-exact"
    ];
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
    const fps = Math.max(
      1,
      getRegisteredRootFps() ?? Math.round(utils.toNumber(fpsInput?.value, 24))
    );
    const frames = Math.max(
      1,
      Math.round(utils.toNumber(framesInput?.value, 24))
    );
    return {
      modelValues: utils.getSelectValues(model),
      modelLabels: utils.getSelectLabels(model),
      loraValues: loras.values,
      loraLabels: loras.labels,
      vaeValues: utils.getSelectValues(vae),
      vaeLabels: utils.getSelectLabels(vae),
      samplerValues: sampler.values,
      samplerLabels: sampler.labels,
      schedulerValues: scheduler.values,
      schedulerLabels: scheduler.labels,
      upscaleMethodValues: stageUpscaleValues.length > 0 ? stageUpscaleValues : fallbackUpscaleMethods,
      upscaleMethodLabels: stageUpscaleLabels.length > 0 ? stageUpscaleLabels : fallbackUpscaleMethods,
      width: getRegisteredRootDimension("width") ?? Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(utils.toNumber(widthInput?.value, 1024))
      ),
      height: getRegisteredRootDimension("height") ?? Math.max(
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

  // frontend/domEvents.ts
  var changeFieldEventsHandled = /* @__PURE__ */ new WeakMap();
  var resolveHostNotifyForHandleFieldChange = (deps, sourceEvent) => {
    if (deps.shouldSuppressClipsHostNotify?.() === true) {
      return false;
    }
    if (isVideoStagesEnabled()) {
      return true;
    }
    const ev = sourceEvent;
    if (ev?.__videoStagesSimulateUserFieldChange === true) {
      return true;
    }
    return ev?.isTrusted === true;
  };
  var isFieldTarget = (value) => value instanceof HTMLInputElement || value instanceof HTMLSelectElement || value instanceof HTMLTextAreaElement;
  var isStageFieldTarget = (value) => value instanceof HTMLInputElement || value instanceof HTMLSelectElement;
  var isSliderNumericInput = (value) => value instanceof HTMLInputElement && (value.type === "number" || value.type === "range");
  var isDurationInput = (value) => isSliderNumericInput(value) && value.dataset.clipField === "duration";
  var isClipAudioUploadInput = (value) => value instanceof HTMLInputElement && value.type === "file" && value.dataset.clipField === CLIP_AUDIO_UPLOAD_FIELD;
  var cacheClipAudioSelection = (clipIdx, fileInput, deps) => {
    const file = fileInput.files?.[0];
    if (!file) {
      return;
    }
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      if (typeof reader.result !== "string") {
        return;
      }
      const clips = deps.getClips();
      if (clipIdx < 0 || clipIdx >= clips.length) {
        return;
      }
      clips[clipIdx].uploadedAudio = {
        data: reader.result,
        fileName: normalizeUploadFileName(file.name)
      };
      deps.saveClips(clips);
    });
    reader.readAsDataURL(file);
  };
  var toggleClipExpanded = (clipIdx, deps) => {
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return;
    }
    clips[clipIdx].expanded = !clips[clipIdx].expanded;
    deps.saveClips(clips, { notifyDomChange: true });
    deps.scheduleClipsRefresh();
  };
  var handleRefUploadRemove = (elem, deps) => {
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
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return;
    }
    if (refIdx < 0 || refIdx >= clips[clipIdx].refs.length) {
      return;
    }
    clips[clipIdx].refs[refIdx].uploadFileName = null;
    clips[clipIdx].refs[refIdx].uploadedImage = null;
    deps.refUploadCache.delete(refUploadKey(clipIdx, refIdx));
    deps.saveClips(clips, { notifyDomChange: true });
  };
  var handleClipAudioUploadRemove = (elem, deps) => {
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
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return;
    }
    clips[clipIdx].uploadedAudio = null;
    deps.saveClips(clips, { notifyDomChange: true });
  };
  var saveClipsAndRefresh = (clips, deps) => {
    deps.saveClips(clips, { notifyDomChange: true });
    deps.scheduleClipsRefresh();
  };
  var addClip = (clips) => {
    clips.push(
      buildDefaultClip(
        getRootDefaults,
        getDefaultStageModel,
        isImageToVideoWorkflow()
      )
    );
  };
  var applyClipAction = (action, { clips, clip, clipIdx, deps }) => {
    if (action === "delete") {
      clips.splice(clipIdx, 1);
      deps.refUploadCache.reindexAfterClipDelete(clipIdx);
      return true;
    }
    if (action === "skip") {
      clip.skipped = !clip.skipped;
      return true;
    }
    if (action === "add-stage") {
      const previousStage = clip.stages.length > 0 ? clip.stages[clip.stages.length - 1] : null;
      clip.stages.push(
        buildDefaultStage(
          getRootDefaults,
          getDefaultStageModel,
          previousStage,
          clip.refs.length
        )
      );
      return true;
    }
    if (action === "add-ref") {
      if (clipHasWanStage(clip) && clip.refs.length >= 2) {
        return false;
      }
      clip.refs.push(buildDefaultRef());
      for (const stage of clip.stages) {
        stage.refStrengths.push(
          isImageToVideoWorkflow() ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH : STAGE_REF_STRENGTH_DEFAULT
        );
      }
      deps.refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
      normalizeWanClipStructuralRefs(clip);
      return true;
    }
    return false;
  };
  var applyRefAction = (action, elem, { clip, clipIdx, deps }) => {
    const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
    if (refIdx < 0 || refIdx >= clip.refs.length) {
      return false;
    }
    if (action === "delete") {
      clip.refs.splice(refIdx, 1);
      for (const stage of clip.stages) {
        if (refIdx < stage.refStrengths.length) {
          stage.refStrengths.splice(refIdx, 1);
        }
      }
      deps.refUploadCache.reindexAfterRefDelete(clipIdx, refIdx);
      normalizeWanClipStructuralRefs(clip);
    }
    if (action === "toggle-collapse") {
      const ref = clip.refs[refIdx];
      ref.expanded = !ref.expanded;
    }
    return true;
  };
  var applyStageAction = (action, elem, { clip }) => {
    const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
    if (stageIdx < 0 || stageIdx >= clip.stages.length) {
      return false;
    }
    if (action === "delete") {
      clip.stages.splice(stageIdx, 1);
    }
    const stage = clip.stages[stageIdx];
    if (action === "skip") {
      stage.skipped = !stage.skipped;
    }
    if (action === "toggle-collapse") {
      stage.expanded = !stage.expanded;
    }
    return true;
  };
  var handleAction = (elem, deps) => {
    const clips = deps.getClips();
    const clipAction = elem.dataset.clipAction;
    const stageAction = elem.dataset.stageAction;
    const refAction = elem.dataset.refAction;
    if (clipAction === "add-clip") {
      addClip(clips);
      saveClipsAndRefresh(clips, deps);
      return;
    }
    const clipIdx = parseInt(elem.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
      deps.scheduleClipsRefresh();
      return;
    }
    const clip = clips[clipIdx];
    const actionContext = { clips, clip, clipIdx, deps };
    if (clipAction && applyClipAction(clipAction, actionContext)) {
      saveClipsAndRefresh(clips, deps);
      return;
    }
    if (refAction) {
      if (!applyRefAction(refAction, elem, actionContext)) {
        deps.scheduleClipsRefresh();
        return;
      }
      saveClipsAndRefresh(clips, deps);
      return;
    }
    if (stageAction) {
      if (!applyStageAction(stageAction, elem, actionContext)) {
        deps.scheduleClipsRefresh();
        return;
      }
      saveClipsAndRefresh(clips, deps);
    }
  };
  var FIELD_CHANGE_NOT_HANDLED = {
    handled: false,
    applied: false
  };
  var FIELD_CHANGE_IGNORED = {
    handled: true,
    applied: false
  };
  var fieldChangeApplied = (result = {}) => ({
    handled: true,
    applied: true,
    ...result
  });
  var setRelatedClipCheckbox = (elem, field, checked, disabled) => {
    const checkbox = elem.closest(".vs-clip-card")?.querySelector(`[data-clip-field="${field}"]`);
    if (!checkbox) {
      return;
    }
    checkbox.checked = checked;
    if (disabled !== void 0) {
      checkbox.disabled = disabled;
    }
  };
  var syncClipLengthControls = (elem, clip) => {
    const clipCard = elem.closest(".vs-clip-card");
    if (!(clipCard instanceof HTMLElement)) {
      return;
    }
    syncClipDurationDisabled(
      clipCard,
      clip.clipLengthFromAudio || clip.clipLengthFromControlNet
    );
    syncClipAudioLengthDisabled(
      clipCard,
      !canUseClipLengthFromAudio(clip.audioSource) || clip.clipLengthFromControlNet
    );
    syncClipControlNetLengthDisabled(
      clipCard,
      clip.controlNetLora === "" || clip.clipLengthFromAudio
    );
  };
  var applyClipDurationChange = ({
    elem,
    clip
  }) => {
    const value = parseFloat(elem.value);
    if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
      const rootDefaults = getRootDefaults();
      clip.duration = snapDurationToFps(value, rootDefaults.fps);
      const frameMax = getReferenceFrameMax(getRootDefaults, clip);
      for (const ref of clip.refs) {
        ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
      }
    }
    return fieldChangeApplied({ refreshClips: "change-only" });
  };
  var applyClipAudioSourceChange = ({
    elem,
    clip
  }) => {
    clip.audioSource = elem.value || AUDIO_SOURCE_NATIVE;
    if (!isAceStepFunAudioSource(clip.audioSource)) {
      clip.saveAudioTrack = false;
      setRelatedClipCheckbox(elem, "saveAudioTrack", false);
    }
    if (!canUseClipLengthFromAudio(clip.audioSource)) {
      clip.clipLengthFromAudio = false;
      setRelatedClipCheckbox(elem, "clipLengthFromAudio", false);
    }
    return fieldChangeApplied({ syncAudioUploadVisibility: true });
  };
  var applyClipControlNetLoraChange = ({
    elem,
    clip
  }) => {
    clip.controlNetLora = normalizeControlNetLora(elem.value);
    if (clip.controlNetLora === "") {
      clip.clipLengthFromControlNet = false;
      setRelatedClipCheckbox(elem, "clipLengthFromControlNet", false, true);
    }
    return fieldChangeApplied({
      refreshClips: "always",
      syncClipLengthControls: true
    });
  };
  var applyClipLengthFromAudioChange = ({
    elem,
    clip
  }) => {
    clip.clipLengthFromAudio = elem instanceof HTMLInputElement && canUseClipLengthFromAudio(clip.audioSource) && !clip.clipLengthFromControlNet ? !!elem.checked : false;
    if (elem instanceof HTMLInputElement && !clip.clipLengthFromAudio) {
      elem.checked = false;
    }
    if (clip.clipLengthFromAudio) {
      clip.clipLengthFromControlNet = false;
      setRelatedClipCheckbox(elem, "clipLengthFromControlNet", false, true);
    }
    return fieldChangeApplied({ syncClipLengthControls: true });
  };
  var applyClipLengthFromControlNetChange = ({
    elem,
    clip
  }) => {
    clip.clipLengthFromControlNet = elem instanceof HTMLInputElement && clip.controlNetLora !== "" && !clip.clipLengthFromAudio ? !!elem.checked : false;
    if (elem instanceof HTMLInputElement && !clip.clipLengthFromControlNet) {
      elem.checked = false;
    }
    if (clip.clipLengthFromControlNet) {
      clip.clipLengthFromAudio = false;
      setRelatedClipCheckbox(elem, "clipLengthFromAudio", false, true);
    }
    return fieldChangeApplied({ syncClipLengthControls: true });
  };
  var applyClipAudioUploadChange = ({
    elem,
    clip,
    clipIdx,
    fieldBindingDeps
  }) => {
    if (!(elem instanceof HTMLInputElement) || elem.type !== "file") {
      return FIELD_CHANGE_IGNORED;
    }
    if (elem.dataset.filedata) {
      clip.uploadedAudio = {
        data: elem.dataset.filedata,
        fileName: normalizeUploadFileName(
          elem.dataset.filename ?? elem.files?.[0]?.name ?? null
        )
      };
      return fieldChangeApplied();
    }
    if (elem.files?.length) {
      cacheClipAudioSelection(clipIdx, elem, {
        getClips: fieldBindingDeps.getClips,
        saveClips: fieldBindingDeps.saveClips
      });
      return FIELD_CHANGE_IGNORED;
    }
    clip.uploadedAudio = null;
    return fieldChangeApplied();
  };
  var applyClipFieldChange = (ctx) => {
    const { elem, clip, field } = ctx;
    if (field === "duration") {
      return applyClipDurationChange(ctx);
    }
    if (field === "audioSource") {
      return applyClipAudioSourceChange(ctx);
    }
    if (field === "controlNetSource") {
      clip.controlNetSource = normalizeControlNetSource(elem.value);
      return fieldChangeApplied();
    }
    if (field === "controlNetLora") {
      return applyClipControlNetLoraChange(ctx);
    }
    if (field === "saveAudioTrack") {
      clip.saveAudioTrack = elem instanceof HTMLInputElement && isAceStepFunAudioSource(clip.audioSource) ? !!elem.checked : false;
      if (elem instanceof HTMLInputElement && !clip.saveAudioTrack) {
        elem.checked = false;
      }
      return fieldChangeApplied();
    }
    if (field === "reuseAudio") {
      clip.reuseAudio = elem instanceof HTMLInputElement && !!elem.checked;
      return fieldChangeApplied();
    }
    if (field === "clipLengthFromAudio") {
      return applyClipLengthFromAudioChange(ctx);
    }
    if (field === "clipLengthFromControlNet") {
      return applyClipLengthFromControlNetChange(ctx);
    }
    if (field === CLIP_AUDIO_UPLOAD_FIELD) {
      return applyClipAudioUploadChange(ctx);
    }
    return FIELD_CHANGE_NOT_HANDLED;
  };
  var applyRefDatasetFieldChange = ({
    elem,
    clip,
    refField,
    fieldBindingDeps
  }) => {
    if (!refField) {
      return FIELD_CHANGE_NOT_HANDLED;
    }
    const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
    if (refIdx < 0 || refIdx >= clip.refs.length) {
      return FIELD_CHANGE_IGNORED;
    }
    applyRefField(clip, clip.refs[refIdx], refField, elem, fieldBindingDeps);
    if (refField === "source") {
      syncRefUploadFieldVisibility(
        elem,
        elem.value,
        fieldBindingDeps.refUploadCache
      );
    }
    return fieldChangeApplied();
  };
  var applyStageDatasetFieldChange = ({
    elem,
    clip,
    stageField
  }) => {
    if (!stageField) {
      return FIELD_CHANGE_NOT_HANDLED;
    }
    const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
    if (stageIdx < 0 || stageIdx >= clip.stages.length) {
      return FIELD_CHANGE_IGNORED;
    }
    if (!isStageFieldTarget(elem)) {
      return FIELD_CHANGE_IGNORED;
    }
    const stage = clip.stages[stageIdx];
    const stageCard = elem.closest("section[data-stage-idx]");
    const methodSelect = stageCard?.querySelector(
      '[data-stage-field="upscaleMethod"]'
    );
    const preservedUpscaleMethod = stageField === "upscale" ? methodSelect?.value ?? stage.upscaleMethod : null;
    applyStageField(stage, stageField, elem, getRootDefaults, clip);
    if (stageField === "upscale") {
      if (preservedUpscaleMethod != null) {
        stage.upscaleMethod = preservedUpscaleMethod;
      }
      syncStageUpscaleMethodDisabled(elem, stage.upscale);
      if (methodSelect && preservedUpscaleMethod != null) {
        methodSelect.value = preservedUpscaleMethod;
      }
    }
    if (stageField === "model") {
      syncStageControlNetStrengthDisabled(elem, stage, clip);
      return fieldChangeApplied({ refreshClips: "always" });
    }
    return fieldChangeApplied();
  };
  var applyDatasetFieldChange = (ctx) => {
    const { elem, clip, clipIdx, clipField, fieldBindingDeps } = ctx;
    if (clipField != null) {
      const clipResult = applyClipFieldChange({
        elem,
        clip,
        clipIdx,
        field: clipField,
        fieldBindingDeps
      });
      if (clipResult.handled) {
        return clipResult;
      }
    }
    const refResult = applyRefDatasetFieldChange(ctx);
    if (refResult.handled) {
      return refResult;
    }
    return applyStageDatasetFieldChange(ctx);
  };
  var finishFieldChange = (result, elem, clip, deps, fromInputEvent) => {
    if (result.syncAudioUploadVisibility) {
      syncClipAudioUploadFieldVisibility(elem, clip.audioSource);
    }
    if (result.syncClipLengthControls) {
      syncClipLengthControls(elem, clip);
    }
    if (result.refreshClips === "always" || result.refreshClips === "change-only" && !fromInputEvent) {
      deps.scheduleClipsRefresh();
    }
  };
  var handleFieldChange = (elem, deps, fromInputEvent = false, sourceEvent = void 0) => {
    if (!isFieldTarget(elem) || !deps.getEditor()?.contains(elem)) {
      return;
    }
    if (sourceEvent instanceof Event && sourceEvent.type === "change" && changeFieldEventsHandled.has(sourceEvent)) {
      return;
    }
    const state = deps.getState();
    const clips = state.clips;
    const clipField = elem.dataset.clipField;
    const stageField = elem.dataset.stageField;
    const refField = elem.dataset.refField;
    const clipIdx = parseInt(elem.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return;
    }
    const clip = clips[clipIdx];
    const fieldBindingDeps = {
      getRootDefaults,
      refUploadCache: deps.refUploadCache,
      getClips: deps.getClips,
      saveClips: deps.saveClips
    };
    const fieldChangeResult = applyDatasetFieldChange({
      elem,
      clip,
      clipIdx,
      clipField,
      stageField,
      refField,
      fieldBindingDeps
    });
    if (!fieldChangeResult.applied) {
      return;
    }
    videoStagesDebugLog("domEvents", "handleFieldChange → saveState", {
      clipIdx,
      clipField: clipField ?? null,
      stageField: stageField ?? null,
      refField: refField ?? null,
      tag: elem instanceof HTMLElement ? elem.tagName : null,
      fromInputEvent
    });
    const notifyDomChange = resolveHostNotifyForHandleFieldChange(
      deps,
      sourceEvent
    );
    deps.saveState(state, { notifyDomChange });
    if (sourceEvent instanceof Event && sourceEvent.type === "change") {
      changeFieldEventsHandled.set(sourceEvent);
    }
    finishFieldChange(fieldChangeResult, elem, clip, deps, fromInputEvent);
  };
  var latestDomEventDeps = null;
  var stageEditorDocumentClickBound = false;
  var stageEditorsWithFieldListeners = /* @__PURE__ */ new WeakSet();
  var stageEditorsWithUploadObservers = /* @__PURE__ */ new WeakSet();
  var getClickTargetElement = (event) => {
    if (event.target instanceof Element) {
      return event.target;
    }
    if (event.target instanceof Node) {
      return event.target.parentElement;
    }
    const path = event.composedPath();
    for (const entry of path) {
      if (entry instanceof Element) {
        return entry;
      }
      if (entry instanceof Node && entry.parentElement) {
        return entry.parentElement;
      }
    }
    return null;
  };
  var handleStageEditorDocumentClick = (event) => {
    const deps = latestDomEventDeps;
    if (!deps) {
      return;
    }
    const target = getClickTargetElement(event);
    if (!target) {
      return;
    }
    const host = target.closest(".vs-clips-container");
    if (!(host instanceof HTMLElement) || !host.isConnected) {
      return;
    }
    deps.ensureEditorRoot(host);
    const refUploadRemoveButton = target.closest(
      ".vs-ref-upload-field .auto-input-remove-button"
    );
    if (refUploadRemoveButton) {
      handleRefUploadRemove(refUploadRemoveButton, deps);
      return;
    }
    const clipUploadRemoveButton = target.closest(
      ".vs-clip-audio-upload-field .auto-input-remove-button"
    );
    if (clipUploadRemoveButton) {
      handleClipAudioUploadRemove(clipUploadRemoveButton, deps);
      return;
    }
    const actionElem = target.closest(
      "[data-clip-action], [data-stage-action], [data-ref-action]"
    );
    if (actionElem) {
      event.preventDefault();
      event.stopPropagation();
      handleAction(actionElem, deps);
      return;
    }
    const clipHeader = target.closest(
      ".vs-clip-card > .input-group-shrinkable"
    );
    if (clipHeader) {
      event.stopPropagation();
      const group = clipHeader.closest(".vs-clip-card");
      const clipIdx = parseInt(group?.dataset.clipIdx ?? "-1", 10);
      toggleClipExpanded(clipIdx, deps);
    }
  };
  var observeMediaFileDatasetChanges = (editor, deps) => {
    if (stageEditorsWithUploadObservers.has(editor) || typeof MutationObserver === "undefined") {
      return;
    }
    stageEditorsWithUploadObservers.add(editor);
    new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (!isClipAudioUploadInput(mutation.target)) {
          continue;
        }
        if (!editor.contains(mutation.target)) {
          continue;
        }
        handleFieldChange(mutation.target, deps, false, void 0);
      }
    }).observe(editor, {
      subtree: true,
      attributes: true,
      attributeFilter: ["data-filedata", "data-filename"]
    });
  };
  var attachEventListeners = (deps) => {
    latestDomEventDeps = deps;
    deps.ensureEditorRoot();
    const editor = deps.getEditor();
    if (!editor) {
      return;
    }
    if (!stageEditorDocumentClickBound) {
      stageEditorDocumentClickBound = true;
      document.addEventListener(
        "click",
        handleStageEditorDocumentClick,
        true
      );
    }
    observeMediaFileDatasetChanges(editor, deps);
    if (stageEditorsWithFieldListeners.has(editor)) {
      return;
    }
    stageEditorsWithFieldListeners.add(editor);
    editor.addEventListener("change", (event) => {
      handleFieldChange(event.target, deps, false, event);
    });
    editor.addEventListener(
      "change",
      (event) => {
        const inputTarget = event.target;
        if (!isFieldTarget(inputTarget)) {
          return;
        }
        if (event.bubbles) {
          return;
        }
        handleFieldChange(inputTarget, deps, true, event);
      },
      true
    );
    editor.addEventListener("input", (event) => {
      const inputTarget = event.target;
      if (!isFieldTarget(inputTarget)) {
        return;
      }
      if (isSliderNumericInput(inputTarget)) {
        handleFieldChange(inputTarget, deps, true, event);
      }
    });
    editor.addEventListener("focusout", (event) => {
      const inputTarget = event.target;
      if (!isDurationInput(inputTarget)) {
        return;
      }
      if (inputTarget.value === inputTarget.defaultValue) {
        return;
      }
      deps.scheduleClipsRefresh();
    });
  };

  // frontend/focusRestore.ts
  var captureFocus = () => {
    const el = document.activeElement;
    if (!(el instanceof HTMLInputElement) && !(el instanceof HTMLSelectElement)) {
      return null;
    }
    if (el instanceof HTMLInputElement && el.type === "range") {
      return null;
    }
    const dataset = el.dataset;
    const typeQualifier = el instanceof HTMLInputElement ? `[type="${el.type}"]` : "";
    let selector = null;
    if (dataset.clipField && dataset.clipIdx) {
      selector = `[data-clip-field="${dataset.clipField}"][data-clip-idx="${dataset.clipIdx}"]${typeQualifier}`;
    } else if (dataset.stageField && dataset.stageIdx && dataset.clipIdx) {
      selector = `[data-stage-field="${dataset.stageField}"][data-stage-idx="${dataset.stageIdx}"][data-clip-idx="${dataset.clipIdx}"]${typeQualifier}`;
    } else if (dataset.refField && dataset.refIdx && dataset.clipIdx) {
      selector = `[data-ref-field="${dataset.refField}"][data-ref-idx="${dataset.refIdx}"][data-clip-idx="${dataset.clipIdx}"]${typeQualifier}`;
    }
    if (!selector) {
      return null;
    }
    let start = null;
    let end = null;
    if (el instanceof HTMLInputElement) {
      start = el.selectionStart;
      end = el.selectionEnd;
      if ((start == null || end == null) && el.type === "number") {
        const len = el.value.length;
        start = len;
        end = len;
      }
    }
    return { selector, start, end };
  };
  var restoreFocus = (snapshot) => {
    if (!snapshot) {
      return;
    }
    const el = document.querySelector(snapshot.selector);
    if (!(el instanceof HTMLInputElement) && !(el instanceof HTMLSelectElement)) {
      return;
    }
    el.focus();
    if (el instanceof HTMLInputElement && snapshot.start != null && snapshot.end != null) {
      try {
        el.setSelectionRange(snapshot.start, snapshot.end);
      } catch {
      }
    }
  };

  // frontend/validation.ts
  var getRefSourceError = (source) => {
    const compact = `${source || ""}`.trim().replace(/\s+/g, "");
    if (compact === REF_SOURCE_BASE || compact === REF_SOURCE_REFINER || compact === REF_SOURCE_UPLOAD) {
      return null;
    }
    if (parseBase2EditStageIndex(compact) == null) {
      return `has unknown source "${source}".`;
    }
    if (!isAvailableBase2EditReference(compact)) {
      return `references missing Base2Edit stage "${source}".`;
    }
    return null;
  };
  var validateClips = (clips) => {
    const errors = [];
    for (let i = 0; i < clips.length; i++) {
      const clip = clips[i];
      if (clip.skipped) {
        continue;
      }
      const clipLabel = `VideoStages: Clip ${i}`;
      if (clip.stages.length === 0) {
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

  // frontend/generateWrap.ts
  var createGenerateWrap = (deps) => {
    let genButtonWrapped = false;
    let genWrapInterval = null;
    const tryWrap = () => {
      if (genButtonWrapped) {
        return;
      }
      if (typeof mainGenHandler === "undefined" || !mainGenHandler || typeof mainGenHandler.doGenerate !== "function") {
        return;
      }
      const original = mainGenHandler.doGenerate.bind(mainGenHandler);
      mainGenHandler.doGenerate = (...args) => {
        const clipsInput = getClipsInput();
        if (!clipsInput) {
          return original(...args);
        }
        if (!isVideoStagesEnabled()) {
          return original(...args);
        }
        const clips = deps.getClips();
        const errors = validateClips(clips);
        if (errors.length > 0) {
          showError(errors[0]);
          return;
        }
        applyVideoStagesPresetDimensionsBeforeGenerate();
        return original(...args);
      };
      mainGenHandler.doGenerate.__videoStagesWrapped = true;
      genButtonWrapped = true;
    };
    const startRetry = (intervalMs = 250) => {
      if (genWrapInterval) {
        return;
      }
      const runTryWrap = () => {
        try {
          tryWrap();
          if (typeof mainGenHandler !== "undefined" && mainGenHandler && typeof mainGenHandler.doGenerate === "function" && mainGenHandler.doGenerate.__videoStagesWrapped) {
            if (genWrapInterval) {
              clearInterval(genWrapInterval);
              genWrapInterval = null;
            }
          }
        } catch {
        }
      };
      runTryWrap();
      genWrapInterval = setInterval(runTryWrap, intervalMs);
    };
    return { tryWrap, startRetry };
  };

  // frontend/persistence.ts
  var isRecord2 = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
  var rootConfig = (dims, clips) => ({
    ...dims,
    clips
  });
  var serializeClipsForStorage = (clips) => clips.map(
    (clip) => ({
      expanded: clip.expanded,
      skipped: clip.skipped,
      duration: clip.duration,
      audioSource: clip.audioSource,
      controlNetSource: clip.controlNetSource,
      controlNetLora: clip.controlNetLora,
      saveAudioTrack: clip.saveAudioTrack,
      clipLengthFromAudio: clip.clipLengthFromAudio,
      clipLengthFromControlNet: clip.clipLengthFromControlNet,
      reuseAudio: clip.reuseAudio,
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
        controlNetStrength: stage.controlNetStrength,
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
    })
  );
  var serializeStateForStorage = (state) => JSON.stringify({
    clips: serializeClipsForStorage(state.clips)
  });
  var lastSerializedState = "";
  var parseSerializedState = (serialized, fallbackDefaults) => {
    try {
      const parsed = JSON.parse(serialized);
      let clipsRaw;
      if (Array.isArray(parsed)) {
        clipsRaw = parsed;
      } else if (isRecord2(parsed) && Array.isArray(parsed.clips)) {
        clipsRaw = parsed.clips;
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
      return rootConfig(fallbackDefaults, clips);
    } catch {
      return null;
    }
  };
  var getState = () => {
    const defaults = getRootDefaults();
    const serialized = (getClipsInput()?.value ?? "") || lastSerializedState;
    if (!serialized) {
      return rootConfig(defaults, []);
    }
    let parsedState = parseSerializedState(serialized, defaults);
    if (parsedState) {
      lastSerializedState = serialized;
      return parsedState;
    }
    if (serialized !== lastSerializedState && lastSerializedState) {
      parsedState = parseSerializedState(lastSerializedState, defaults);
      if (parsedState) {
        return parsedState;
      }
    }
    return rootConfig(defaults, []);
  };
  var saveState = (state, callbacks, options) => {
    const serialized = serializeStateForStorage(state);
    lastSerializedState = serialized;
    const input = getClipsInput();
    if (input) {
      input.value = serialized;
    }
    callbacks?.onAfterSerialize?.(serialized);
    const willNotifyDom = !!(input && options?.notifyDomChange !== false);
    videoStagesDebugLog("persistence", "saveState", {
      notifyDomChange: options?.notifyDomChange,
      willNotifyDom,
      jsonChars: serialized.length
    });
    if (willNotifyDom && input) {
      triggerChangeFor(input);
    }
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
  var ensureClipsSeeded = (callbacks, options) => {
    const state = getState();
    if (state.clips.length > 0) {
      return;
    }
    state.clips = [
      buildDefaultClip(
        getRootDefaults,
        getDefaultStageModel,
        isImageToVideoWorkflow()
      )
    ];
    saveState(state, callbacks, options);
  };

  // frontend/observers.ts
  var ROOT_VIDEO_TIMING_INPUT_IDS = /* @__PURE__ */ new Set([
    "input_videoframes",
    "input_text2videoframes",
    "input_videofps",
    "input_videoframespersecond",
    "input_videostagesfps"
  ]);
  var createObservers = (deps) => {
    let clipsInputSyncInterval = null;
    let lastKnownClipsJson = "";
    const observedDropdownIds = /* @__PURE__ */ new Set();
    let sourceDropdownObserver = null;
    let base2EditListenerInstalled = false;
    let rootVideoTimingChangeListenerInstalled = false;
    let refSourceFallbackListenerInstalled = false;
    const markPersisted = (serialized) => {
      lastKnownClipsJson = serialized;
    };
    const startClipsInputSync = () => {
      if (clipsInputSyncInterval) {
        return;
      }
      lastKnownClipsJson = getClipsInput()?.value ?? "";
      clipsInputSyncInterval = setInterval(() => {
        const currentValue = getClipsInput()?.value ?? "";
        if (currentValue === lastKnownClipsJson) {
          return;
        }
        videoStagesDebugLog(
          "observers",
          "clips input JSON drift → scheduleRefresh",
          {
            prevChars: lastKnownClipsJson.length,
            nextChars: currentValue.length
          }
        );
        lastKnownClipsJson = currentValue;
        deps.scheduleRefresh();
      }, 150);
    };
    const installSourceDropdownObserver = () => {
      if (sourceDropdownObserver || typeof MutationObserver === "undefined") {
        return;
      }
      const observer = new MutationObserver((mutations) => {
        if (!mutations.some((mutation) => mutation.type === "childList")) {
          return;
        }
        const first = mutations.find((m) => m.type === "childList");
        const targetId = first?.target instanceof HTMLElement ? first.target.id || "(no id)" : null;
        videoStagesDebugLog(
          "observers",
          "source dropdown childList mutation → scheduleRefresh",
          { targetId, mutationCount: mutations.length }
        );
        deps.scheduleRefresh();
      });
      const observableIds = [
        "input_videomodel",
        "input_model",
        "input_vae",
        "input_sampler",
        "input_scheduler",
        "input_refinerupscalemethod",
        "input_loras"
      ];
      let hasObservedSource = false;
      for (const sourceId of observableIds) {
        const source = utils.getSelectElement(sourceId);
        if (!source || observedDropdownIds.has(sourceId)) {
          continue;
        }
        observedDropdownIds.add(sourceId);
        observer.observe(source, { childList: true });
        source.addEventListener("change", () => {
          videoStagesDebugLog(
            "observers",
            "observed source select change → scheduleRefresh",
            { sourceId }
          );
          deps.scheduleRefresh();
        });
        hasObservedSource = true;
      }
      if (!hasObservedSource) {
        observer.disconnect();
        return;
      }
      sourceDropdownObserver = observer;
    };
    const handleRootVideoTimingCommittedChange = (inputId) => {
      const input = getClipsInput();
      if (!input) {
        return;
      }
      const state = deps.getState();
      const rootDefaults = getRootDefaults();
      state.width = rootDefaults.width;
      state.height = rootDefaults.height;
      state.fps = rootDefaults.fps;
      const serialized = serializeStateForStorage(state);
      if (serialized !== input.value) {
        videoStagesDebugLog(
          "observers",
          "root video timing change → saveState (notifyDomChange: false)",
          { inputId }
        );
        deps.saveState(state, { notifyDomChange: false });
      }
      videoStagesDebugLog(
        "observers",
        "root video timing change → scheduleRefresh",
        { inputId }
      );
      deps.scheduleRefresh();
    };
    const installRootVideoTimingChangeListener = () => {
      if (rootVideoTimingChangeListenerInstalled) {
        return;
      }
      rootVideoTimingChangeListenerInstalled = true;
      document.addEventListener("change", (event) => {
        if (!(event.target instanceof HTMLInputElement)) {
          return;
        }
        const target = event.target;
        if (!ROOT_VIDEO_TIMING_INPUT_IDS.has(target.id)) {
          return;
        }
        handleRootVideoTimingCommittedChange(target.id);
      });
    };
    const installBase2EditStageChangeListener = () => {
      if (base2EditListenerInstalled) {
        return;
      }
      base2EditListenerInstalled = true;
      document.addEventListener("base2edit:stages-changed", () => {
        videoStagesDebugLog(
          "observers",
          "base2edit:stages-changed → scheduleRefresh"
        );
        deps.scheduleRefresh();
      });
    };
    const installRefSourceFallbackListener = (createEditor, handleFieldChange2) => {
      if (refSourceFallbackListenerInstalled) {
        return;
      }
      refSourceFallbackListenerInstalled = true;
      document.addEventListener(
        "change",
        (event) => {
          if (!(event.target instanceof HTMLSelectElement)) {
            return;
          }
          const target = event.target;
          const isRefSourceChange = target.dataset.refField === "source";
          const clipField = target.dataset.clipField;
          const isClipAudioSourceChange = clipField === "audioSource";
          const isControlNetSourceChange = clipField === "controlNetSource";
          const isControlNetLoraChange = clipField === "controlNetLora";
          if (!isRefSourceChange && !isClipAudioSourceChange && !isControlNetSourceChange && !isControlNetLoraChange) {
            return;
          }
          const liveEditor = target.closest(".vs-clips-container");
          if (!(liveEditor instanceof HTMLElement)) {
            return;
          }
          videoStagesDebugLog(
            "observers",
            "ref-source fallback capture change → createEditor + handleFieldChange",
            {
              refField: target.dataset.refField ?? null,
              clipField: clipField ?? null,
              selectId: target.id || null
            }
          );
          createEditor();
          handleFieldChange2(target, event);
        },
        true
      );
    };
    return {
      markPersisted,
      startClipsInputSync,
      installSourceDropdownObserver,
      installBase2EditStageChangeListener,
      installRootVideoTimingChangeListener,
      installRefSourceFallbackListener
    };
  };

  // frontend/refUploadCache.ts
  var createRefUploadCache = () => {
    let cache = /* @__PURE__ */ new Map();
    const reindexAfterClipDelete = (deletedClipIdx) => {
      const nextCache = /* @__PURE__ */ new Map();
      for (const [key, cached] of cache.entries()) {
        const parsed = parseRefUploadKey(key);
        if (!parsed) {
          continue;
        }
        if (parsed.clipIdx === deletedClipIdx) {
          continue;
        }
        const clipIdx = parsed.clipIdx > deletedClipIdx ? parsed.clipIdx - 1 : parsed.clipIdx;
        nextCache.set(refUploadKey(clipIdx, parsed.refIdx), cached);
      }
      cache = nextCache;
    };
    const reindexAfterRefDelete = (clipIdx, deletedRefIdx) => {
      const nextCache = /* @__PURE__ */ new Map();
      for (const [key, cached] of cache.entries()) {
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
        const refIdx = parsed.refIdx > deletedRefIdx ? parsed.refIdx - 1 : parsed.refIdx;
        nextCache.set(refUploadKey(clipIdx, refIdx), cached);
      }
      cache = nextCache;
    };
    const restorePreviews = (editor, clips) => {
      const uploadInputs = editor.querySelectorAll(
        '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]'
      );
      for (const input of uploadInputs) {
        if (!(input instanceof HTMLInputElement)) {
          continue;
        }
        const clipIdx = parseInt(input.dataset.clipIdx ?? "-1", 10);
        const refIdx = parseInt(input.dataset.refIdx ?? "-1", 10);
        const persisted = clipIdx >= 0 && clipIdx < clips.length ? clips[clipIdx].refs[refIdx]?.uploadedImage : null;
        const cached = cache.get(refUploadKey(clipIdx, refIdx));
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
    };
    const cacheSelection = ({
      clipIdx,
      refIdx,
      fileInput,
      getClips: getClips2,
      saveClips: saveClips2
    }) => {
      const file = fileInput.files?.[0];
      const key = refUploadKey(clipIdx, refIdx);
      if (!file) {
        cache.delete(key);
        return;
      }
      const reader = new FileReader();
      reader.addEventListener("load", () => {
        if (typeof reader.result !== "string") {
          return;
        }
        cache.set(key, {
          src: reader.result,
          name: file.name
        });
        const clips = getClips2();
        if (clipIdx < 0 || clipIdx >= clips.length) {
          return;
        }
        const ref = clips[clipIdx].refs[refIdx];
        if (!ref) {
          return;
        }
        ref.uploadedImage = {
          data: reader.result,
          fileName: normalizeUploadFileName(file.name)
        };
        saveClips2(clips);
      });
      reader.readAsDataURL(file);
    };
    return {
      get: (key) => cache.get(key),
      delete: (key) => {
        cache.delete(key);
      },
      reindexAfterClipDelete,
      reindexAfterRefDelete,
      restorePreviews,
      cacheSelection
    };
  };

  // frontend/renderHtml.ts
  var CONTROLNET_SOURCE_DROPDOWN_OPTIONS = CONTROLNET_SOURCE_OPTIONS.map((value) => ({ value, label: value }));
  var decorateAutoInputWrapper = (html, className, hidden = false) => html.replace(
    /<div class="([^"]*\bauto-input\b[^"]*)"([^>]*)>/,
    (_match, classes, attrs) => `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`
  );
  var RE_AUTO_INPUT_NAME_TRANSLATE_Q = new RegExp(
    '(<span class="auto-input-name">)(<span class="translate"[^>]*>[^<]*</span>)(<span class="auto-input-qbutton[^>]*>\\?</span>)'
  );
  var moveQButtonBeforeLabelText = (html) => html.replace(RE_AUTO_INPUT_NAME_TRANSLATE_Q, "$1$3$2").replace(
    /(<span class="auto-input-name">)([^<]*)(<span class="auto-input-qbutton[^>]*>\?<\/span>)/,
    "$1$3$2"
  );
  var disableSliderInputs = (html) => html.replace(
    /<input\b([^>]*\sclass="[^"]*\bauto-slider-(?:number|range)\b[^"]*"[^>]*)>/g,
    (match, attrs) => /\sdisabled(?:[\s=>]|$)/.test(match) ? match : `<input${attrs} disabled>`
  );
  var disableCheckboxInput = (html) => html.replace(
    /<input\b([^>]*\sclass="[^"]*\bauto-checkbox\b[^"]*"[^>]*)>/,
    (match, attrs) => /\sdisabled(?:[\s=>]|$)/.test(match) ? match : `<input${attrs} disabled>`
  );
  var hideFirstStageField = (html, stageIdx) => stageIdx === 0 ? decorateAutoInputWrapper(html, "vs-first-stage-field-hidden", true) : html;
  var dropdownOptions = (values, labels, selected) => {
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
  };
  var dedupeEmptyValueDropdownOptions = (options) => {
    let sawEmpty = false;
    const out = [];
    for (let i = 0; i < options.length; i++) {
      const opt = options[i];
      if (opt.value === "") {
        if (sawEmpty) {
          continue;
        }
        sawEmpty = true;
      }
      out.push(opt);
    }
    return out;
  };
  var buildNativeDropdownStrict = (id, paramId, label, options, selected) => {
    const escapedLabel = escapeAttr(label);
    const optionHtml = renderOptionList(options, selected);
    const baseHtml = `
    <div class="auto-input auto-dropdown-box auto-input-flex">
        <label>
            <span class="auto-input-name">${escapedLabel}</span>
        </label>
        <select class="auto-dropdown" id="${escapeAttr(id)}"
            data-name="${escapedLabel}" data-param_id="${escapeAttr(paramId)}"
            autocomplete="off" onchange="autoSelectWidth(this)">
${optionHtml}
        </select>
    </div>`;
    return options.filter((o) => o.disabled).reduce((acc, option) => {
      const optionValue = escapeAttr(option.value);
      return acc.replace(
        new RegExp(`(<option [^>]*value="${optionValue}")`),
        "$1 disabled"
      );
    }, baseHtml);
  };
  var buildNativeDropdown = (id, paramId, label, options, selected) => {
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
      true
    );
    return options.filter((o) => o.disabled).reduce((acc, option) => {
      const optionValue = escapeAttr(option.value);
      return acc.replace(
        new RegExp(`(<option [^>]*value="${optionValue}")`),
        "$1 disabled"
      );
    }, html);
  };
  var buildRefSourceOptions = (currentValue) => {
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
    if (currentValue && !options.some((o) => o.value === currentValue)) {
      const isBase2Edit = parseBase2EditStageIndex(currentValue) != null;
      options.unshift({
        value: currentValue,
        label: isBase2Edit ? `Missing Base2Edit ${currentValue}` : currentValue,
        disabled: isBase2Edit
      });
    }
    return options;
  };
  var renderClipAudioUploadField = (clip, clipIdx, audioSource2) => {
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
          true
        ),
        {
          "data-clip-field": CLIP_AUDIO_UPLOAD_FIELD,
          "data-clip-idx": String(clipIdx),
          "data-has-uploaded-audio": clip.uploadedAudio?.data ? "true" : "false"
        }
      ),
      "vs-clip-audio-upload-field",
      audioSource2 !== AUDIO_SOURCE_UPLOAD
    );
  };
  var renderLeftTooltipCheckboxField = (html, className, hidden) => moveQButtonBeforeLabelText(
    decorateAutoInputWrapper(html, className, hidden)
  );
  var renderRefRow = (ref, clip, clipIdx, refIdx, getRootDefaults2) => {
    const wanClip = clipHasWanStage(clip);
    const collapseTitle = ref.expanded ? "Collapse" : "Expand";
    const collapseGlyph = ref.expanded ? "&#x2B9F;" : "&#x2B9E;";
    const head = `
            <div class="vs-card-head">
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse"
                    data-ref-action="toggle-collapse" data-ref-idx="${refIdx}"
                    data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Ref Image ${refIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="interrupt-button vs-btn-tiny" data-ref-action="delete"
                        data-ref-idx="${refIdx}" data-clip-idx="${clipIdx}" title="Remove reference">&times;</button>
                </div>
            </div>
        `;
    if (!ref.expanded) {
      return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">${head}</section>`;
    }
    const sourceOptions = buildRefSourceOptions(ref.source);
    const frameCount = getReferenceFrameMax(getRootDefaults2, clip);
    const sourceError = getRefSourceError(ref.source);
    const errorHtml = sourceError ? `<div class="vs-field-error">${escapeAttr(sourceError)}</div>` : "";
    const sourceField = injectFieldData(
      buildNativeDropdown(
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
        "Frame",
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
    const frameFieldRendered = wanClip ? decorateAutoInputWrapper(frameField, "vs-wan-ref-frame-hidden", true) : frameField;
    const fromEndFieldRendered = wanClip ? decorateAutoInputWrapper(
      fromEndField,
      "vs-wan-ref-fromend-hidden",
      true
    ) : fromEndField;
    return `<section class="vs-card vs-ref-card input-group" data-ref-idx="${refIdx}">
            ${head}
            <div class="vs-card-body input-group-content">
                ${sourceField}
                ${uploadField}
                ${frameFieldRendered}
                ${fromEndFieldRendered}
                ${errorHtml}
            </div>
        </section>`;
  };
  var renderStageRow = (clip, stage, clipIdx, stageIdx, getRootDefaults2) => {
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
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse"
                    data-stage-action="toggle-collapse" data-stage-idx="${stageIdx}"
                    data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Stage ${stageIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}"
                        data-stage-action="skip" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}"
                        title="${skipTitle}">&#x23ED;&#xFE0E;</button>
                    <button type="button" class="interrupt-button vs-btn-tiny" data-stage-action="delete"
                        data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="Remove stage">&times;</button>
                </div>
            </div>
        `;
    if (!stage.expanded) {
      return `<section class="${cardClasses.join(" ")}" data-stage-idx="${stageIdx}">${head}</section>`;
    }
    const defaults = getRootDefaults2();
    const stageSliderField = (field, label, value, min, max, step, disabled = false) => {
      const html = injectFieldData(
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
      if (!disabled) {
        return html;
      }
      return disableSliderInputs(html);
    };
    const stageDropdownField = (field, label, values, labels, selected, disabled = false) => {
      const html = injectFieldData(
        buildNativeDropdown(
          stageFieldId(clipIdx, stageIdx, field),
          field,
          label,
          dropdownOptions(values, labels, selected),
          selected
        ),
        {
          "data-stage-field": field,
          "data-stage-idx": String(stageIdx),
          "data-clip-idx": String(clipIdx)
        }
      );
      if (!disabled) {
        return html;
      }
      return html.replace(/<select /, "<select disabled ");
    };
    const modelField = stageDropdownField(
      "model",
      "Model",
      defaults.modelValues,
      defaults.modelLabels,
      stage.model
    );
    const controlField = hideFirstStageField(
      stageSliderField(
        "control",
        "Control",
        stage.control,
        defaults.controlMin,
        defaults.controlMax,
        defaults.controlStep,
        false
      ),
      stageIdx
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
    const upscaleField = hideFirstStageField(
      stageSliderField(
        "upscale",
        "Upscale",
        stage.upscale,
        defaults.upscaleMin,
        defaults.upscaleMax,
        defaults.upscaleStep,
        false
      ),
      stageIdx
    );
    const selectedUpscaleMethod = `${stage.upscaleMethod ?? ""}`;
    const upscaleMethodFieldBase = injectFieldData(
      buildNativeDropdownStrict(
        stageFieldId(clipIdx, stageIdx, "upscaleMethod"),
        "upscaleMethod",
        "Upscale Method",
        dropdownOptions(
          defaults.upscaleMethodValues,
          defaults.upscaleMethodLabels,
          selectedUpscaleMethod
        ),
        selectedUpscaleMethod
      ),
      {
        "data-stage-field": "upscaleMethod",
        "data-stage-idx": String(stageIdx),
        "data-clip-idx": String(clipIdx)
      }
    );
    const upscaleMethodField = hideFirstStageField(
      stageIdx === 0 ? upscaleMethodFieldBase : stage.upscale === 1 ? upscaleMethodFieldBase.replace(/<select /, "<select disabled ") : upscaleMethodFieldBase,
      stageIdx
    );
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
    const controlNetLoraActive = normalizeControlNetLora(clip.controlNetLora) !== "";
    const controlNetStrengthDisabled = controlNetLoraActive && !isLtxVideoModelValue(stage.model);
    const controlNetStrengthField = controlNetLoraActive ? stageSliderField(
      "controlNetStrength",
      "ControlNet Strength",
      stage.controlNetStrength,
      STAGE_CONTROLNET_STRENGTH_MIN,
      STAGE_CONTROLNET_STRENGTH_MAX,
      STAGE_CONTROLNET_STRENGTH_STEP,
      controlNetStrengthDisabled
    ) : "";
    const wanClip = clipHasWanStage(clip);
    const refStrengthFields = wanClip ? "" : clip.refs.map(
      (_, refIdx) => stageSliderField(
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
                ${controlNetStrengthField}
                ${refStrengthFields}
            </div>
        </section>`;
  };
  var renderClipCard = (clip, clipIdx, getRootDefaults2) => {
    const defaults = getRootDefaults2();
    const stagesCount = clip.stages.length;
    const refsCount = clip.refs.length;
    const wanClip = clipHasWanStage(clip);
    const addRefDisabled = wanClip && refsCount >= 2;
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
    const head = `<span id="input_group_vsclip${clipIdx}" class="input-group-header input-group-shrinkable"><span class="header-label-wrap"><span class="auto-symbol">${collapseGlyph}</span><span class="header-label">Clip ${clipIdx}</span><span class="header-label-spacer"></span><span class="vs-clip-card-actions"><button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-clip-action="skip" data-clip-idx="${clipIdx}" title="${skipBtnTitle}">&#x23ED;&#xFE0E;</button><button type="button" class="interrupt-button vs-btn-tiny" data-clip-action="delete" data-clip-idx="${clipIdx}" title="Remove clip">&times;</button></span></span></span>`;
    const audioSourceOptions = buildAudioSourceOptions(clip.audioSource);
    const audioSource2 = resolveAudioSourceValue(
      clip.audioSource,
      audioSourceOptions
    );
    const normalizedControlNetLora = normalizeControlNetLora(
      clip.controlNetLora
    );
    const controlNetLoraActive = normalizedControlNetLora !== "";
    const canUseAudioLength = canUseClipLengthFromAudio(audioSource2);
    const clipLengthFromAudio = canUseAudioLength && !!clip.clipLengthFromAudio;
    const canUseControlNetLength = controlNetLoraActive && !clipLengthFromAudio;
    const clipLengthFromControlNet = canUseControlNetLength && !!clip.clipLengthFromControlNet;
    const dynamicClipLength = clipLengthFromAudio || clipLengthFromControlNet;
    const audioLengthDisabled = !canUseAudioLength || clipLengthFromControlNet;
    const lengthInputHtml = makeSliderInput(
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
    );
    const lengthFieldWithSteps = injectFieldData(
      overrideSliderSteps(
        dynamicClipLength ? disableSliderInputs(lengthInputHtml) : lengthInputHtml,
        {
          numberStep: "any",
          rangeStep: CLIP_DURATION_SLIDER_STEP
        }
      ),
      { "data-clip-field": "duration", "data-clip-idx": String(clipIdx) }
    );
    const decoratedLengthField = decorateAutoInputWrapper(
      lengthFieldWithSteps,
      "vs-clip-duration-field"
    );
    const audioSourceField = injectFieldData(
      buildNativeDropdown(
        clipFieldId(clipIdx, "audioSource"),
        "audioSource",
        "Audio Source",
        audioSourceOptions,
        audioSource2
      ),
      {
        "data-clip-field": "audioSource",
        "data-clip-idx": String(clipIdx)
      }
    );
    const reuseAudioField = renderLeftTooltipCheckboxField(
      injectFieldData(
        makeCheckboxInput(
          "",
          clipFieldId(clipIdx, "reuseAudio"),
          "reuseAudio",
          "Reuse Audio",
          "Use the first stage's produced audio latent for later stages in this clip.",
          clip.reuseAudio,
          false,
          true,
          true
        ),
        {
          "data-clip-field": "reuseAudio",
          "data-clip-idx": String(clipIdx)
        }
      ),
      "vs-clip-reuse-audio-field",
      false
    );
    let clipLengthFromAudioHtml = injectFieldData(
      makeCheckboxInput(
        "",
        clipFieldId(clipIdx, "clipLengthFromAudio"),
        "clipLengthFromAudio",
        "Clip Length from Audio",
        "Sets the video clip length to be the same length as the selected audio track.",
        clipLengthFromAudio,
        false,
        true,
        true
      ),
      {
        "data-clip-field": "clipLengthFromAudio",
        "data-clip-idx": String(clipIdx)
      }
    );
    if (audioLengthDisabled) {
      clipLengthFromAudioHtml = disableCheckboxInput(clipLengthFromAudioHtml);
    }
    const clipLengthFromAudioField = renderLeftTooltipCheckboxField(
      clipLengthFromAudioHtml,
      audioLengthDisabled ? "vs-clip-length-from-audio-field vs-audio-length-disabled" : "vs-clip-length-from-audio-field",
      !canUseAudioLength
    );
    const saveAudioTrackField = renderLeftTooltipCheckboxField(
      injectFieldData(
        makeCheckboxInput(
          "",
          clipFieldId(clipIdx, "saveAudioTrack"),
          "saveAudioTrack",
          "Save Audio Track",
          "Keep a standalone MP3 output for AceStepFun audio selected as this clip's Audio Source.",
          clip.saveAudioTrack,
          false,
          true,
          true
        ),
        {
          "data-clip-field": "saveAudioTrack",
          "data-clip-idx": String(clipIdx)
        }
      ),
      "vs-clip-save-audio-track-field",
      !isAceStepFunAudioSource(audioSource2)
    );
    const audioUploadField = renderClipAudioUploadField(
      clip,
      clipIdx,
      audioSource2
    );
    const controlNetLoraOptions = dedupeEmptyValueDropdownOptions(
      dropdownOptions(
        defaults.loraValues,
        defaults.loraLabels,
        normalizedControlNetLora
      ).map((opt) => ({
        ...opt,
        value: normalizeControlNetLora(opt.value)
      }))
    );
    let controlNetSourceFieldHtml = injectFieldData(
      buildNativeDropdown(
        clipFieldId(clipIdx, "controlNetSource"),
        "controlNetSource",
        "Source",
        CONTROLNET_SOURCE_DROPDOWN_OPTIONS,
        clip.controlNetSource
      ),
      {
        "data-clip-field": "controlNetSource",
        "data-clip-idx": String(clipIdx)
      }
    );
    if (!controlNetLoraActive) {
      controlNetSourceFieldHtml = controlNetSourceFieldHtml.replace(
        /<select /,
        "<select disabled "
      );
    }
    const controlNetSourceField = decorateAutoInputWrapper(
      controlNetSourceFieldHtml,
      controlNetLoraActive ? "vs-controlnet-source-field" : "vs-controlnet-source-field vs-controlnet-source-disabled",
      false
    );
    const controlNetLoraField = injectFieldData(
      buildNativeDropdown(
        clipFieldId(clipIdx, "controlNetLora"),
        "controlNetLora",
        "LoRA",
        controlNetLoraOptions,
        normalizedControlNetLora
      ),
      {
        "data-clip-field": "controlNetLora",
        "data-clip-idx": String(clipIdx)
      }
    );
    let clipLengthFromControlNetHtml = injectFieldData(
      makeCheckboxInput(
        "",
        clipFieldId(clipIdx, "clipLengthFromControlNet"),
        "clipLengthFromControlNet",
        "Clip Length from ControlNet",
        "Sets the video clip length to match the selected ControlNet video's frame count.",
        clipLengthFromControlNet,
        false,
        true,
        true
      ),
      {
        "data-clip-field": "clipLengthFromControlNet",
        "data-clip-idx": String(clipIdx)
      }
    );
    if (!canUseControlNetLength) {
      clipLengthFromControlNetHtml = disableCheckboxInput(
        clipLengthFromControlNetHtml
      );
    }
    const clipLengthFromControlNetField = renderLeftTooltipCheckboxField(
      clipLengthFromControlNetHtml,
      canUseControlNetLength ? "vs-clip-length-from-controlnet-field" : "vs-clip-length-from-controlnet-field vs-controlnet-length-disabled",
      false
    );
    const refRowsHtml = clip.refs.map(
      (ref, refIdx) => renderRefRow(ref, clip, clipIdx, refIdx, getRootDefaults2)
    ).join("");
    const stageRowsHtml = clip.stages.map(
      (stage, stageIdx) => renderStageRow(clip, stage, clipIdx, stageIdx, getRootDefaults2)
    ).join("");
    const body = `
            <div class="input-group-content vs-clip-card-body"
                id="input_group_content_vsclip${clipIdx}" data-do_not_save="1"${contentStyle}>
                ${decoratedLengthField}

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">AUDIO</div>
                    </div>
                    ${audioSourceField}
                    ${reuseAudioField}
                    ${clipLengthFromAudioField}
                    ${saveAudioTrackField}
                    ${audioUploadField}
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">CONTROLNET</div>
                    </div>
                    ${controlNetLoraField}
                    ${controlNetSourceField}
                    ${clipLengthFromControlNetField}
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Reference Images &middot; ${refsCount}</div>
                    </div>
                    <div class="vs-card-list">${refRowsHtml}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-ref" data-clip-idx="${clipIdx}"${addRefDisabled ? " disabled" : ""}>+ Add Reference Image</button>
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Stages &middot; ${stagesCount}</div>
                    </div>
                    <div class="vs-card-list">${stageRowsHtml}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-stage"
                        data-clip-idx="${clipIdx}">+ Add Video Stage</button>
                </div>
            </div>
        `;
    return `<div class="${groupClasses.join(" ")}" id="auto-group-vsclip${clipIdx}" data-clip-idx="${clipIdx}">${head}${body}</div>`;
  };

  // frontend/videoStageEditor.ts
  function videoStageEditor() {
    let editor = null;
    let clipsRefreshTimer = null;
    const refUploadCache = createRefUploadCache();
    const persistenceCallbacks = {};
    const getEditorState = () => getState();
    const saveEditorState = (state, options) => saveState(state, persistenceCallbacks, options);
    const getEditorClips = () => getClips();
    const saveEditorClips = (clips, options) => saveClips(clips, persistenceCallbacks, options);
    let suppressClipsHostNotifyForRender = false;
    const scheduleClipsRefresh = () => {
      if (clipsRefreshTimer) {
        clearTimeout(clipsRefreshTimer);
      }
      videoStagesDebugLog(
        "videoStageEditor",
        "scheduleClipsRefresh (debounced renderClips)"
      );
      clipsRefreshTimer = setTimeout(() => {
        clipsRefreshTimer = null;
        videoStagesDebugLog(
          "videoStageEditor",
          "renderClips run (from scheduleClipsRefresh)"
        );
        try {
          renderClips();
        } catch {
        }
      }, 0);
    };
    const observers = createObservers({
      scheduleRefresh: scheduleClipsRefresh,
      getState: getEditorState,
      saveState: saveEditorState
    });
    persistenceCallbacks.onAfterSerialize = (serialized) => {
      observers.markPersisted(serialized);
    };
    const generateWrap = createGenerateWrap({ getClips: getEditorClips });
    const createEditor = (preferredRoot) => {
      let el = preferredRoot instanceof HTMLElement && preferredRoot.isConnected ? preferredRoot : editor?.isConnected ? editor : null;
      if (!el) {
        const groupContent = document.getElementById(
          "input_group_content_videostages"
        );
        const existingEditors = groupContent?.querySelectorAll(
          ".vs-clips-container"
        );
        el = existingEditors && existingEditors.length > 0 ? existingEditors[existingEditors.length - 1] : null;
      }
      if (!el) {
        el = document.createElement("div");
        el.className = "videostages-stage-editor keep_group_visible vs-clips-container";
        document.getElementById("input_group_content_videostages")?.appendChild(el);
      }
      el.style.width = "100%";
      el.style.maxWidth = "100%";
      el.style.minWidth = "0";
      el.style.flex = "1 1 100%";
      el.style.overflow = "visible";
      editor = el;
    };
    const getDomDeps = () => ({
      ensureEditorRoot: createEditor,
      getEditor: () => editor,
      getClips: getEditorClips,
      saveClips: saveEditorClips,
      getState: getEditorState,
      saveState: saveEditorState,
      scheduleClipsRefresh,
      refUploadCache,
      shouldSuppressClipsHostNotify: () => suppressClipsHostNotifyForRender
    });
    const restoreClipAudioUploadPreviews = (clips) => {
      if (!editor) {
        return;
      }
      for (let clipIdx = 0; clipIdx < clips.length; clipIdx++) {
        const upload = clips[clipIdx].uploadedAudio;
        if (!upload?.data) {
          continue;
        }
        const input = editor.querySelector(
          `.auto-file[data-clip-field="${CLIP_AUDIO_UPLOAD_FIELD}"][data-clip-idx="${clipIdx}"]`
        );
        if (!input) {
          continue;
        }
        if (input.dataset.filedata === upload.data && normalizeUploadFileName(input.dataset.filename) === upload.fileName) {
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
    };
    const renderClips = () => {
      createEditor();
      if (!editor) {
        return [];
      }
      suppressClipsHostNotifyForRender = true;
      try {
        seedRegisteredDimensionsFromCore(isVideoStagesEnabled());
        const state = getEditorState();
        const clips = state.clips;
        const focusSnapshot = captureFocus();
        editor.innerHTML = "";
        const stack = document.createElement("div");
        stack.className = "vs-clip-stack";
        stack.setAttribute("data-vs-clip-stack", "true");
        editor.appendChild(stack);
        if (clips.length === 0) {
          stack.insertAdjacentHTML(
            "beforeend",
            `<div class="vs-empty-card">No video clips. Click "+ Add Video Clip" below.</div>`
          );
        } else {
          for (let i = 0; i < clips.length; i++) {
            stack.insertAdjacentHTML(
              "beforeend",
              renderClipCard(clips[i], i, getRootDefaults)
            );
          }
        }
        const addClipButton = document.createElement("button");
        addClipButton.type = "button";
        addClipButton.className = "vs-add-btn vs-add-btn-clip";
        addClipButton.dataset.clipAction = "add-clip";
        addClipButton.innerText = "+ Add Video Clip";
        editor.appendChild(addClipButton);
        enableSlidersIn(editor);
        restoreClipAudioUploadPreviews(clips);
        refUploadCache.restorePreviews(editor, clips);
        attachEventListeners(getDomDeps());
        restoreFocus(focusSnapshot);
        return validateClips(clips);
      } finally {
        suppressClipsHostNotifyForRender = false;
      }
    };
    const init = () => {
      createEditor();
      observers.startClipsInputSync();
      ensureClipsSeeded(persistenceCallbacks, {
        notifyDomChange: isVideoStagesEnabled()
      });
      generateWrap.tryWrap();
      renderClips();
      observers.installSourceDropdownObserver();
      observers.installBase2EditStageChangeListener();
      observers.installRootVideoTimingChangeListener();
      observers.installRefSourceFallbackListener(
        createEditor,
        (target, ev) => {
          handleFieldChange(target, getDomDeps(), false, ev);
        }
      );
      wireDimensionsPreset();
    };
    const startGenerateWrapRetry = (intervalMs = 250) => {
      generateWrap.startRetry(intervalMs);
    };
    return {
      init,
      startGenerateWrapRetry
    };
  }

  // frontend/main.ts
  var stageEditor = videoStageEditor();
  var registerVideoClipPromptPrefix = () => {
    if (typeof promptTabComplete === "undefined") {
      return;
    }
    promptTabComplete.registerPrefix(
      "videoclip",
      "Add a prompt section that applies to VideoStages clips.",
      () => [
        '\nUse "<videoclip>..." to apply to ALL VideoStages clips (including LoRAs inside the section).',
        '\nUse "<videoclip[0]>..." to apply to clip 0, "<videoclip[1]>..." for clip 1, etc.',
        '\nIf no "<videoclip>" / "<videoclip[0]>" section exists for a clip, VideoStages falls back to the global prompt.'
      ],
      true
    );
  };
  var tryRegisterStageEditor = () => {
    if (!Array.isArray(postParamBuildSteps)) {
      return false;
    }
    postParamBuildSteps.push(() => {
      try {
        stageEditor.init();
      } catch (error) {
        console.warn("VideoStages: failed to build stage editor", error);
      }
    });
    return true;
  };
  if (!tryRegisterStageEditor()) {
    const interval = setInterval(() => {
      if (tryRegisterStageEditor()) {
        clearInterval(interval);
      }
    }, 200);
  }
  registerVideoClipPromptPrefix();
  stageEditor.startGenerateWrapRetry();
  audioSource();
})();
//# sourceMappingURL=video-stages.js.map
