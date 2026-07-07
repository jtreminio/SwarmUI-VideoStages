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

  // frontend/constants.ts
  var ROOT_DIMENSION_MIN = 256;
  var DIMENSIONS_PRESET_CUSTOM_VALUE = "custom";

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
  var ROOT_DIMENSION_WIDTH_INPUT_ID = "input_videostageswidth";
  var ROOT_DIMENSION_HEIGHT_INPUT_ID = "input_videostagesheight";
  var DIMENSIONS_PRESET_SELECT_ID = "input_videostagesdimensions";
  var DIMENSIONS_PRESET_METADATA_INPUT_ID = "input_videostagesdimensionsmetadata";
  var getRootDimensionParamInput = (field) => utils.getInputElement(
    field === "width" ? ROOT_DIMENSION_WIDTH_INPUT_ID : ROOT_DIMENSION_HEIGHT_INPUT_ID
  );
  var getCoreDimensionInput = (field) => {
    const primaryId = field === "width" ? "input_width" : "input_height";
    const fallbackId = field === "width" ? "input_aspectratiowidth" : "input_aspectratioheight";
    return utils.getInputElement(primaryId) ?? utils.getInputElement(fallbackId);
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

  // frontend/main.ts
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
        '\nPer-clip / per-stage "prompt" and "loras" fold into this JSON — there is no more <videoclip> section.'
      ],
      true
    );
  };
  var initDimensions = () => {
    try {
      seedRegisteredDimensionsFromCore(isVideoStagesEnabled());
      wireDimensionsPreset();
    } catch (error) {
      console.warn("VideoStages: failed to init dimensions", error);
    }
  };
  var scheduleDimensionsInit = () => {
    if (!Array.isArray(postParamBuildSteps)) {
      setTimeout(scheduleDimensionsInit, 200);
      return;
    }
    postParamBuildSteps.push(initDimensions);
  };
  var wrapGenerateForDimensions = () => {
    if (typeof mainGenHandler === "undefined" || !mainGenHandler || typeof mainGenHandler.doGenerate !== "function") {
      return false;
    }
    const original = mainGenHandler.doGenerate.bind(mainGenHandler);
    mainGenHandler.doGenerate = (...args) => {
      if (isVideoStagesEnabled()) {
        applyVideoStagesPresetDimensionsBeforeGenerate();
      }
      return original(...args);
    };
    return true;
  };
  var scheduleGenerateWrap = () => {
    if (wrapGenerateForDimensions()) {
      return;
    }
    const interval = setInterval(() => {
      if (wrapGenerateForDimensions()) {
        clearInterval(interval);
      }
    }, 250);
  };
  scheduleDimensionsInit();
  scheduleGenerateWrap();
  registerVideoStagesPromptPrefix();
  audioSource();
})();
//# sourceMappingURL=video-stages.js.map
