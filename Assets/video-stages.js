"use strict";
(() => {
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

  // frontend/audioSource.ts
  var AUDIO_SOURCE_NATIVE = "Native";
  var AUDIO_SOURCE_UPLOAD = "Upload";
  var AUDIO_SOURCE_SWARM = "Swarm Audio";
  var TEXT2AUDIO_TOGGLE_ID = "input_group_content_texttoaudio_toggle";
  var ACESTEPFUN_EVENT = "acestepfun:tracks-changed";
  var SOURCE_SELECT_SELECTOR = '[data-clip-field="audioSource"]';
  var getSourceSelects = () => Array.from(document.querySelectorAll(SOURCE_SELECT_SELECTOR)).filter(
    (elem) => elem instanceof HTMLSelectElement
  );
  var isSourceSelect = (target) => target instanceof HTMLSelectElement && target.matches(SOURCE_SELECT_SELECTOR);
  var isTextToAudioEnabled = () => {
    const toggle = utils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
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
  var audioSource = () => {
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
          elem.textContent = option.label;
          elem.selected = option.value === desired;
          select.appendChild(elem);
        }
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
      const toggle = utils.getInputElement(TEXT2AUDIO_TOGGLE_ID);
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

  // frontend/constants.ts
  var REF_FRAME_MIN = 1;
  var DEFAULT_CLIP_DURATION_SECONDS = 5;
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

  // frontend/normalization.ts
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.includes("pixel-lanczos") ? "pixel-lanczos" : upscaleMethodValues[0] ?? "pixel-lanczos";
  var resolveFirstStageControl = (defaults) => clamp(defaults.control, defaults.controlMin, defaults.controlMax);
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
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
  var normalizeRootDimension = (value, fallback) => Math.max(
    ROOT_DIMENSION_MIN,
    Math.round(utils.toNumber(`${value ?? fallback}`, fallback))
  );
  var normalizeRootFps = (value, fallback) => Math.max(
    ROOT_FPS_MIN,
    Math.round(utils.toNumber(`${value ?? fallback}`, fallback))
  );
  var normalizeStageRefStrengthValue = (value) => Math.round(
    clamp(
      utils.toNumber(
        `${value ?? STAGE_REF_STRENGTH_DEFAULT}`,
        STAGE_REF_STRENGTH_DEFAULT
      ),
      STAGE_REF_STRENGTH_MIN,
      STAGE_REF_STRENGTH_MAX
    ) * 10
  ) / 10;
  var buildDefaultStageRefStrengths = (refCount) => {
    const strengths = [];
    for (let i = 0; i < refCount; i++) {
      strengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
    return strengths;
  };
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
  var buildDefaultRef = () => ({
    expanded: true,
    source: REF_SOURCE_BASE,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false
  });
  var buildDefaultClip = (getRootDefaults2, getDefaultStageModel2) => {
    const defaults = getRootDefaults2();
    return {
      expanded: true,
      skipped: false,
      duration: snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
        defaults.fps
      ),
      audioSource: AUDIO_SOURCE_NATIVE,
      uploadedAudio: null,
      refs: [],
      stages: [
        buildDefaultStage(getRootDefaults2, getDefaultStageModel2, null, 0)
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
    const firstStageUpscale = stageIndexInClip === 0 ? {
      upscale: defaults.upscale,
      upscaleMethod: resolveRootPreferredUpscaleMethod(
        defaults.upscaleMethodValues
      )
    } : {
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
    const control = stageIndexInClip === 0 ? resolveFirstStageControl(defaults) : clamp(
      utils.toNumber(
        `${readRawStageProp(rawStage, "control", "Control") ?? fallback.control}`,
        fallback.control
      ),
      defaults.controlMin,
      defaults.controlMax
    );
    const stage = {
      expanded: rawStage.expanded === void 0 ? true : !!rawStage.expanded,
      skipped: !!rawStage.skipped,
      control,
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
      expanded: rawRef.expanded === void 0 ? true : !!rawRef.expanded,
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
    const audioSourceOptions = buildAudioSourceOptions();
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
    const refs = refsRaw.map(
      (rawRef) => normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax)
    );
    const stages = [];
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
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
    return {
      expanded: rawClip.expanded === void 0 ? true : !!rawClip.expanded,
      skipped: !!rawClip.skipped,
      duration,
      audioSource: resolveAudioSourceValue(
        `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`,
        audioSourceOptions
      ),
      uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
      refs,
      stages
    };
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
  var applyStageField = (stage, field, target, getRootDefaults2) => {
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

  // frontend/swarmInputs.ts
  var getClipsInput = () => {
    const el = document.getElementById("input_videostages");
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
      return el;
    }
    return null;
  };
  var getRootDimensionParamInput = (field) => utils.getInputElement(
    field === "width" ? "input_vswidth" : "input_vsheight"
  );
  var getRootFpsParamInput = () => utils.getInputElement("input_vsfps");
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

  // frontend/rootDefaults.ts
  var getDefaultStageModel = (modelValues) => {
    if (isRootTextToVideoModel()) {
      const modelName = `${getRootModelInput()?.value ?? ""}`.trim();
      if (modelName) {
        return modelName;
      }
    }
    return modelValues[0] ?? "";
  };
  var getRootDefaults = () => {
    let model = utils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
      model = utils.getSelectElement("input_model");
    }
    const vae = utils.getSelectElement("input_vae");
    const sampler = getDropdownOptions("sampler", "input_sampler");
    const scheduler = getDropdownOptions("scheduler", "input_scheduler");
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const allUpscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const allUpscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
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
    const steps = utils.getInputElement("input_videosteps") ?? utils.getInputElement("input_steps");
    const cfgScale = utils.getInputElement("input_videocfg") ?? utils.getInputElement("input_cfgscale");
    const widthInput = utils.getInputElement("input_width") ?? utils.getInputElement("input_aspectratiowidth");
    const heightInput = utils.getInputElement("input_height") ?? utils.getInputElement("input_aspectratioheight");
    const fpsInput = utils.getInputElement("input_videofps") ?? utils.getInputElement("input_videoframespersecond");
    const framesInput = utils.getInputElement("input_videoframes") ?? utils.getInputElement("input_text2videoframes");
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
      vaeValues: utils.getSelectValues(vae),
      vaeLabels: utils.getSelectLabels(vae),
      samplerValues: sampler.values,
      samplerLabels: sampler.labels,
      schedulerValues: scheduler.values,
      schedulerLabels: scheduler.labels,
      upscaleMethodValues: upscaleMethodValues.length > 0 ? upscaleMethodValues : fallbackUpscaleMethods,
      upscaleMethodLabels: upscaleMethodLabels.length > 0 ? upscaleMethodLabels : fallbackUpscaleMethods,
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
  var isFieldTarget = (value) => value instanceof HTMLInputElement || value instanceof HTMLSelectElement || value instanceof HTMLTextAreaElement;
  var isStageFieldTarget = (value) => value instanceof HTMLInputElement || value instanceof HTMLSelectElement;
  var isSliderNumericInput = (value) => value instanceof HTMLInputElement && (value.type === "number" || value.type === "range");
  var isDurationInput = (value) => isSliderNumericInput(value) && value.dataset.clipField === "duration";
  var toggleClipExpanded = (clipIdx, deps) => {
    const clips = deps.getClips();
    if (clipIdx < 0 || clipIdx >= clips.length) {
      return;
    }
    clips[clipIdx].expanded = !clips[clipIdx].expanded;
    deps.saveClips(clips);
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
    deps.saveClips(clips);
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
    deps.saveClips(clips);
  };
  var handleAction = (elem, deps) => {
    const target = elem;
    const clips = deps.getClips();
    const clipAction = target.dataset.clipAction;
    const stageAction = target.dataset.stageAction;
    const refAction = target.dataset.refAction;
    if (clipAction === "add-clip") {
      clips.push(buildDefaultClip(getRootDefaults, getDefaultStageModel));
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    const clipIdx = parseInt(target.dataset.clipIdx ?? "-1", 10);
    if (clipIdx < 0 || clipIdx >= clips.length) {
      deps.scheduleClipsRefresh();
      return;
    }
    const clip = clips[clipIdx];
    if (clipAction === "delete") {
      clips.splice(clipIdx, 1);
      deps.refUploadCache.reindexAfterClipDelete(clipIdx);
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    if (clipAction === "skip") {
      clip.skipped = !clip.skipped;
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    if (clipAction === "add-stage") {
      const previousStage = clip.stages.length > 0 ? clip.stages[clip.stages.length - 1] : null;
      clip.stages.push(
        buildDefaultStage(
          getRootDefaults,
          getDefaultStageModel,
          previousStage,
          clip.refs.length
        )
      );
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    if (clipAction === "add-ref") {
      clip.refs.push(buildDefaultRef());
      for (const stage of clip.stages) {
        stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
      }
      deps.refUploadCache.delete(refUploadKey(clipIdx, clip.refs.length - 1));
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    if (refAction) {
      const refIdx = parseInt(target.dataset.refIdx ?? "-1", 10);
      if (refIdx < 0 || refIdx >= clip.refs.length) {
        deps.scheduleClipsRefresh();
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
        deps.refUploadCache.reindexAfterRefDelete(clipIdx, refIdx);
      } else if (refAction === "toggle-collapse") {
        ref.expanded = !ref.expanded;
      }
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
      return;
    }
    if (stageAction) {
      const stageIdx = parseInt(target.dataset.stageIdx ?? "-1", 10);
      if (stageIdx < 0 || stageIdx >= clip.stages.length) {
        deps.scheduleClipsRefresh();
        return;
      }
      const stage = clip.stages[stageIdx];
      if (stageAction === "delete") {
        clip.stages.splice(stageIdx, 1);
      } else if (stageAction === "skip") {
        stage.skipped = !stage.skipped;
      } else if (stageAction === "toggle-collapse") {
        stage.expanded = !stage.expanded;
      }
      deps.saveClips(clips);
      deps.scheduleClipsRefresh();
    }
  };
  var handleFieldChange = (elem, deps, fromInputEvent = false) => {
    if (!isFieldTarget(elem) || !deps.getEditor()?.contains(elem)) {
      return;
    }
    const state = deps.getState();
    const clips = state.clips;
    const defaults = getRootDefaults();
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
    if (clipField === "duration") {
      const value = parseFloat(elem.value);
      if (Number.isFinite(value) && value >= CLIP_DURATION_MIN) {
        clip.duration = snapDurationToFps(value, defaults.fps);
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        for (const ref of clip.refs) {
          ref.frame = clamp(ref.frame, REF_FRAME_MIN, frameMax);
        }
      }
    } else if (clipField === "audioSource") {
      clip.audioSource = elem.value || AUDIO_SOURCE_NATIVE;
    } else if (clipField === CLIP_AUDIO_UPLOAD_FIELD) {
      if (!(elem instanceof HTMLInputElement) || elem.type !== "file") {
        return;
      }
      if (elem.dataset.filedata) {
        clip.uploadedAudio = {
          data: elem.dataset.filedata,
          fileName: normalizeUploadFileName(
            elem.dataset.filename ?? elem.files?.[0]?.name ?? null
          )
        };
      } else if (elem.files?.length) {
        return;
      } else {
        clip.uploadedAudio = null;
      }
    } else if (refField) {
      const refIdx = parseInt(elem.dataset.refIdx ?? "-1", 10);
      if (refIdx < 0 || refIdx >= clip.refs.length) {
        return;
      }
      applyRefField(
        clip,
        clip.refs[refIdx],
        refField,
        elem,
        fieldBindingDeps
      );
      if (refField === "source") {
        syncRefUploadFieldVisibility(elem, elem.value, deps.refUploadCache);
      }
    } else if (stageField) {
      const stageIdx = parseInt(elem.dataset.stageIdx ?? "-1", 10);
      if (stageIdx < 0 || stageIdx >= clip.stages.length) {
        return;
      }
      if (!isStageFieldTarget(elem)) {
        return;
      }
      const stage = clip.stages[stageIdx];
      const stageCard = elem.closest("section[data-stage-idx]");
      const methodSelect = stageCard?.querySelector(
        '[data-stage-field="upscaleMethod"]'
      );
      const preservedUpscaleMethod = stageField === "upscale" ? methodSelect?.value ?? stage.upscaleMethod : null;
      applyStageField(stage, stageField, elem, getRootDefaults);
      if (stageField === "upscale") {
        if (preservedUpscaleMethod != null) {
          stage.upscaleMethod = preservedUpscaleMethod;
        }
        syncStageUpscaleMethodDisabled(elem, stage.upscale);
        if (methodSelect && preservedUpscaleMethod != null) {
          methodSelect.value = preservedUpscaleMethod;
        }
      }
    } else {
      return;
    }
    deps.saveState(state);
    if (clipField === "audioSource") {
      syncClipAudioUploadFieldVisibility(elem, clip.audioSource);
    }
    if (clipField === "duration" && !fromInputEvent) {
      deps.scheduleClipsRefresh();
    }
  };
  var latestDomEventDeps = null;
  var stageEditorDocumentClickBound = false;
  var stageEditorsWithFieldListeners = /* @__PURE__ */ new WeakSet();
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
    const host = target.closest("#videostages_stage_editor");
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
    if (stageEditorsWithFieldListeners.has(editor)) {
      return;
    }
    stageEditorsWithFieldListeners.add(editor);
    editor.addEventListener("change", (event) => {
      handleFieldChange(event.target, deps);
    });
    editor.addEventListener(
      "change",
      (event) => {
        const inputTarget = event.target;
        if (!isFieldTarget(inputTarget)) {
          return;
        }
        if (!(inputTarget instanceof HTMLInputElement) || inputTarget.type !== "range" || event.bubbles) {
          return;
        }
        handleFieldChange(inputTarget, deps, true);
      },
      true
    );
    editor.addEventListener("input", (event) => {
      const inputTarget = event.target;
      if (!isFieldTarget(inputTarget)) {
        return;
      }
      if (isSliderNumericInput(inputTarget)) {
        handleFieldChange(inputTarget, deps, true);
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
  var toParsedConfig = (value) => {
    if (!isRecord2(value)) {
      return null;
    }
    return {
      width: value.width,
      height: value.height,
      fps: value.fps,
      clips: Array.isArray(value.clips) ? value.clips : void 0
    };
  };
  var serializeClipsForStorage = (clips) => clips.map(
    (clip) => ({
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
    })
  );
  var getEffectiveRootDimension = (persistedValue, fallback) => normalizeRootDimension(persistedValue, fallback);
  var lastSerializedState = "";
  var getSerializedStateSource = () => {
    const inputValue = getClipsInput()?.value ?? "";
    return inputValue || lastSerializedState;
  };
  var parseSerializedState = (serialized, fallbackDefaults) => {
    try {
      const parsed = JSON.parse(serialized);
      const parsedConfig = toParsedConfig(parsed);
      let clipsRaw = [];
      if (Array.isArray(parsed)) {
        clipsRaw = parsed;
      } else if (Array.isArray(parsedConfig?.clips)) {
        clipsRaw = parsedConfig.clips;
      }
      const firstClip = clipsRaw.length > 0 && isRecord2(clipsRaw[0]) ? clipsRaw[0] : null;
      const clips = [];
      for (let i = 0; i < clipsRaw.length; i++) {
        const el = clipsRaw[i];
        const record = isRecord2(el) ? el : {};
        clips.push(
          normalizeClip(record, getRootDefaults, getDefaultStageModel)
        );
      }
      return {
        width: getEffectiveRootDimension(
          parsedConfig?.width ?? firstClip?.width,
          fallbackDefaults.width
        ),
        height: getEffectiveRootDimension(
          parsedConfig?.height ?? firstClip?.height,
          fallbackDefaults.height
        ),
        fps: normalizeRootFps(parsedConfig?.fps, fallbackDefaults.fps),
        clips
      };
    } catch {
      return null;
    }
  };
  var getState = () => {
    const defaults = getRootDefaults();
    const serialized = getSerializedStateSource();
    if (!serialized) {
      return {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
        clips: []
      };
    }
    const parsedState = parseSerializedState(serialized, defaults);
    if (parsedState) {
      lastSerializedState = serialized;
      return parsedState;
    }
    if (serialized !== lastSerializedState && lastSerializedState) {
      const fallbackState = parseSerializedState(
        lastSerializedState,
        defaults
      );
      if (fallbackState) {
        return fallbackState;
      }
    }
    return {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps,
      clips: []
    };
  };
  var saveState = (state, callbacks, options) => {
    const serialized = JSON.stringify({
      width: state.width,
      height: state.height,
      fps: state.fps,
      clips: serializeClipsForStorage(state.clips)
    });
    lastSerializedState = serialized;
    const input = getClipsInput();
    if (input) {
      input.value = serialized;
    }
    callbacks?.onAfterSerialize?.(serialized);
    if (input && options?.notifyDomChange !== false) {
      triggerChangeFor(input);
    }
  };
  var getClips = () => getState().clips;
  var saveClips = (clips, callbacks) => {
    const state = getState();
    state.clips = clips;
    saveState(state, callbacks);
  };
  var ensureClipsSeeded = (callbacks, options) => {
    const state = getState();
    if (state.clips.length > 0) {
      return;
    }
    state.clips = [buildDefaultClip(getRootDefaults, getDefaultStageModel)];
    saveState(state, callbacks, options);
  };

  // frontend/observers.ts
  var ROOT_VIDEO_TIMING_INPUT_IDS = /* @__PURE__ */ new Set([
    "input_videoframes",
    "input_text2videoframes",
    "input_videofps",
    "input_videoframespersecond",
    "input_vsfps"
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
        deps.scheduleRefresh();
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
        const source = utils.getSelectElement(sourceId);
        if (!source || observedDropdownIds.has(sourceId)) {
          continue;
        }
        observedDropdownIds.add(sourceId);
        observer.observe(source, { childList: true });
        source.addEventListener("change", () => deps.scheduleRefresh());
        hasObservedSource = true;
      }
      if (!hasObservedSource) {
        observer.disconnect();
        return;
      }
      sourceDropdownObserver = observer;
    };
    const handleRootVideoTimingCommittedChange = () => {
      const input = getClipsInput();
      if (!input) {
        return;
      }
      const state = deps.getState();
      const rootDefaults = getRootDefaults();
      state.width = rootDefaults.width;
      state.height = rootDefaults.height;
      state.fps = rootDefaults.fps;
      const serialized = JSON.stringify({
        width: state.width,
        height: state.height,
        fps: state.fps,
        clips: serializeClipsForStorage(state.clips)
      });
      if (serialized !== input.value) {
        deps.saveState(state, { notifyDomChange: false });
      }
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
        handleRootVideoTimingCommittedChange();
      });
    };
    const installBase2EditStageChangeListener = () => {
      if (base2EditListenerInstalled) {
        return;
      }
      base2EditListenerInstalled = true;
      document.addEventListener("base2edit:stages-changed", () => {
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
          createEditor();
          handleFieldChange2(target);
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
  var decorateAutoInputWrapper = (html, className, hidden = false) => html.replace(
    /<div class="([^"]*)"([^>]*)>/,
    (_match, classes, attrs) => `<div class="${classes} ${className}"${attrs}${hidden ? ' style="display: none;"' : ""}>`
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
  var buildNativeDropdownStrict = (id, paramId, label, options, selected) => {
    const escapedLabel = escapeAttr(label);
    const optionHtml = renderOptionList(options, selected);
    const baseHtml = `
    <div class="auto-input auto-dropdown-box auto-input-flex">
        <label>
            <span class="auto-input-name">${escapedLabel}</span>
        </label>
        <select class="auto-dropdown" id="${escapeAttr(id)}" data-name="${escapedLabel}" data-param_id="${escapeAttr(paramId)}" autocomplete="off" onchange="autoSelectWidth(this)">
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
      false
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
  var renderRefRow = (ref, clip, clipIdx, refIdx, getRootDefaults2) => {
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
                <button type="button" class="basic-button vs-btn-tiny vs-btn-collapse" data-stage-action="toggle-collapse" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${collapseTitle}">${collapseGlyph}</button>
                <div class="vs-card-title">Stage ${stageIdx}</div>
                <div class="vs-card-actions">
                    <button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-stage-action="skip" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="${skipTitle}">&#x23ED;&#xFE0E;</button>
                    <button type="button" class="interrupt-button vs-btn-tiny" data-stage-action="delete" data-stage-idx="${stageIdx}" data-clip-idx="${clipIdx}" title="Remove stage">&times;</button>
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
      return html.replace(
        /<input class="auto-slider-number nogrow"/g,
        '<input class="auto-slider-number nogrow" disabled'
      ).replace(
        /<input class="auto-slider-range nogrow"/g,
        '<input class="auto-slider-range nogrow" disabled'
      );
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
    const refStrengthFields = clip.refs.map(
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
                ${refStrengthFields}
            </div>
        </section>`;
  };
  var renderClipCard = (clip, clipIdx, getRootDefaults2) => {
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
    const head = `<span id="input_group_vsclip${clipIdx}" class="input-group-header input-group-shrinkable"><span class="header-label-wrap"><span class="auto-symbol">${collapseGlyph}</span><span class="header-label">Clip ${clipIdx}</span><span class="header-label-spacer"></span><span class="vs-clip-card-actions"><button type="button" class="basic-button vs-btn-tiny ${skipBtnVariant}" data-clip-action="skip" data-clip-idx="${clipIdx}" title="${skipBtnTitle}">&#x23ED;&#xFE0E;</button><button type="button" class="interrupt-button vs-btn-tiny" data-clip-action="delete" data-clip-idx="${clipIdx}" title="Remove clip">&times;</button></span></span></span>`;
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
    const audioSource2 = resolveAudioSourceValue(
      clip.audioSource,
      audioSourceOptions
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
    const audioUploadField = renderClipAudioUploadField(
      clip,
      clipIdx,
      audioSource2
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
                    <div class="vs-card-list">${clip.refs.map((ref, refIdx) => renderRefRow(ref, clip, clipIdx, refIdx, getRootDefaults2)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-ref" data-clip-idx="${clipIdx}">+ Add Reference Image</button>
                </div>

                <div class="vs-section-block">
                    <div class="vs-section-block-head">
                        <div class="vs-section-block-title">Stages &middot; ${stagesCount}</div>
                    </div>
                    <div class="vs-card-list">${clip.stages.map((stage, stageIdx) => renderStageRow(clip, stage, clipIdx, stageIdx, getRootDefaults2)).join("")}</div>
                    <button type="button" class="vs-add-btn" data-clip-action="add-stage" data-clip-idx="${clipIdx}">+ Add Video Stage</button>
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
    const saveEditorClips = (clips) => saveClips(clips, persistenceCallbacks);
    const scheduleClipsRefresh = () => {
      if (clipsRefreshTimer) {
        clearTimeout(clipsRefreshTimer);
      }
      clipsRefreshTimer = setTimeout(() => {
        clipsRefreshTimer = null;
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
          "#videostages_stage_editor"
        );
        el = existingEditors && existingEditors.length > 0 ? existingEditors[existingEditors.length - 1] : null;
      }
      if (!el) {
        el = document.createElement("div");
        el.id = "videostages_stage_editor";
        el.className = "videostages-stage-editor keep_group_visible";
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
      refUploadCache
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
      observers.installRefSourceFallbackListener(createEditor, (target) => {
        handleFieldChange(target, getDomDeps());
      });
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
  stageEditor.startGenerateWrapRetry();
  audioSource();
})();
//# sourceMappingURL=video-stages.js.map
