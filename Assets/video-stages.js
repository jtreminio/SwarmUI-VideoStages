"use strict";
(() => {
  // frontend/bottomTimelineTab.ts
  var TAB_ID = "VideoStages-Timeline-Tab";
  var TIMELINE_BODY_ID = "videostages-timeline-body";
  var updateTimelineTabIndicator = (enabled) => {
    const navLink = document.querySelector(`a[href="#${TAB_ID}"]`);
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
    li.innerHTML = `<a class="nav-link translate" data-bs-toggle="tab" href="#${TAB_ID}" aria-selected="false" tabindex="-1" role="tab">VideoStages</a>`;
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
    const shell = document.createElement("div");
    shell.className = "vst-timeline";
    const body = document.createElement("div");
    body.className = "vst-right";
    body.id = TIMELINE_BODY_ID;
    shell.appendChild(body);
    pane.appendChild(shell);
    content.appendChild(pane);
    const navLink = li.querySelector("a");
    if (navLink) {
      registerTabWithLayout(navLink);
    }
    return body;
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
  var AUDIO_SOURCE_VOICE_REF = "Voice Reference";
  var ACESTEPFUN_AUDIO_REF_PATTERN = /^audio(\d+)$/i;
  var isAceStepFunAudioSource = (source) => ACESTEPFUN_AUDIO_REF_PATTERN.test(`${source ?? ""}`.trim());
  var isControlNetAudioSource = (source) => `${source ?? ""}`.trim() === AUDIO_SOURCE_CONTROLNET;
  var canUseClipLengthFromAudio = (source) => {
    const normalized = `${source ?? ""}`.trim();
    return normalized === AUDIO_SOURCE_UPLOAD || isAceStepFunAudioSource(normalized) || isControlNetAudioSource(normalized);
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
      { value: AUDIO_SOURCE_UPLOAD, label: AUDIO_SOURCE_UPLOAD },
      { value: AUDIO_SOURCE_VOICE_REF, label: AUDIO_SOURCE_VOICE_REF }
    ];
    appendAceStepFunRefs(options);
    if (context.controlNetEnabled) {
      options.push({
        value: AUDIO_SOURCE_CONTROLNET,
        label: AUDIO_SOURCE_CONTROLNET
      });
    }
    appendMissingSelectedRef(options, currentValue);
    return options;
  };
  var resolveAudioSourceValue = (currentValue, options) => resolveSelectValue(currentValue, options, AUDIO_SOURCE_NATIVE);

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

  // frontend/boundaryPlan.ts
  var DEFAULT_CROSSFADE_OVERLAP_FRAMES = 8;
  var DEFAULT_CONTINUE_OVERLAP_FRAMES = 8;
  var CONTINUE_OVERLAP_MAX_FRAMES = 48;
  var CONTINUE_OVERLAP_CHOICES = [8, 16, 24, 32, 40, 48];
  var resolveContinueWindows = (frames, cont, crossfade, requested) => {
    const windows = new Array(cont.length).fill(0);
    for (let i = 0; i < cont.length; i++) {
      if (!cont[i]) {
        continue;
      }
      let k = (requested[i] ?? DEFAULT_CONTINUE_OVERLAP_FRAMES) + 1;
      const leftReserve = i > 0 && cont[i - 1] ? windows[i - 1] : i > 0 && crossfade[i - 1] ? 1 : 0;
      const rightReserve = i < cont.length - 1 && (cont[i + 1] || crossfade[i + 1]) ? 1 : 0;
      const kMax = Math.min(
        frames[i] - 1 - leftReserve,
        frames[i + 1] - 1 - rightReserve
      );
      while (k > 1 && k > kMax) {
        k -= 8;
      }
      windows[i] = Math.max(1, k);
    }
    return windows;
  };
  var crossfadePlanForClips = (clips, fps) => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const noOverlap = () => new Array(boundaryCount).fill(0);
    if (count < 2) {
      return { overlaps: noOverlap(), fallback: false };
    }
    const crossfade = [];
    const cont = [];
    let requested = 0;
    for (let i = 0; i < count - 1; i++) {
      const b = clips[i].boundaryOut ?? "cut";
      crossfade[i] = b === "crossfade";
      cont[i] = b === "continue";
      if (crossfade[i] || cont[i]) {
        requested++;
      }
    }
    if (requested === 0) {
      return { overlaps: noOverlap(), fallback: false };
    }
    const frames = clips.map((c) => framesForClip(c.duration, fps));
    const prefs = clips.map(
      (c) => c.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES
    );
    const windows = resolveContinueWindows(frames, cont, crossfade, prefs);
    const crossfadeMaxPerSide = new Array(count).fill(0);
    for (let i = 0; i < count; i++) {
      const fixedTrim = (i > 0 && cont[i - 1] ? windows[i - 1] : 0) + (i < count - 1 && cont[i] ? windows[i] : 0);
      const crossSides = (i > 0 && crossfade[i - 1] ? 1 : 0) + (i < count - 1 && crossfade[i] ? 1 : 0);
      if (fixedTrim === 0 && crossSides === 0) {
        continue;
      }
      const budget = frames[i] - 1 - fixedTrim;
      if (budget < 0 || crossSides > 0 && Math.floor(budget / crossSides) < 1) {
        return { overlaps: noOverlap(), fallback: true };
      }
      if (crossSides > 0) {
        crossfadeMaxPerSide[i] = Math.floor(budget / crossSides);
      }
    }
    const overlaps = [];
    for (let i = 0; i < count - 1; i++) {
      overlaps[i] = crossfade[i] ? Math.min(
        Math.max(1, prefs[i] ?? DEFAULT_CROSSFADE_OVERLAP_FRAMES),
        crossfadeMaxPerSide[i],
        crossfadeMaxPerSide[i + 1]
      ) : cont[i] ? windows[i] : 0;
    }
    return { overlaps, fallback: false };
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
  var CLIP_DURATION_MAX = 9999;
  var PROMPT_WINDOW_MIN_DURATION = 0.25;
  var PROMPT_WINDOW_DEFAULT_DURATION = 1.5;
  var RETAKE_MIN_DURATION = 0.1;
  var RETAKE_DEFAULT_DURATION = 2;
  var RETAKE_DURATION_STEP = 0.1;
  var RETAKE_STRENGTH_MIN = 0;
  var RETAKE_STRENGTH_MAX = 1;
  var RETAKE_STRENGTH_STEP = 0.05;
  var RETAKE_STRENGTH_DEFAULT = 1;
  var AUDIO_SEGMENT_MIN_LENGTH = 0.1;
  var AUDIO_SEGMENT_DEFAULT_LENGTH = 2;
  var AUDIO_SEGMENT_STEP = 0.1;
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
  var IC_LORA_SOURCE_UPLOAD = "Upload";
  var IC_LORA_SOURCE_STAGE_INPUT = "Stage Input";
  var IC_LORA_STAGE_ALL = -1;
  var IC_LORA_STRENGTH_MIN = 0;
  var IC_LORA_STRENGTH_MAX = 2;
  var IC_LORA_STRENGTH_STEP = 0.05;
  var IC_LORA_STRENGTH_DEFAULT = 1;
  var IC_LORA_ATTENTION_MIN = 0;
  var IC_LORA_ATTENTION_MAX = 1;
  var IC_LORA_ATTENTION_STEP = 0.05;
  var IC_LORA_ATTENTION_DEFAULT = 1;
  var IC_LORA_AUTO = "[AUTO]";
  var IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";
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

  // frontend/hostDom.ts
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

  // frontend/icLoraPresets.ts
  var IC_LORA_PRESET_CUSTOM_ID = "custom";
  var IC_LORA_PRESET_UNION_CONTROL_ID = "union-control";
  var HF = "https://huggingface.co";
  var IC_LORA_PRESETS = [
    {
      id: "union-control",
      displayName: "Union Control",
      triggerPhrase: "",
      strength: 1,
      controlType: "depth",
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
      note: "Generates new speech + lips from the prompt's words. Upload the source video as drive media and enable 'Voice ref from drive audio' (or set the clip's Audio to Voice Reference)."
    },
    {
      id: "hdr",
      displayName: "HDR",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      // The repo also ships an auxiliary hdr-scene-emb file; only the LoRA itself is fetched.
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-HDR/resolve/main/ltx-2.3-22b-ic-lora-hdr-0.9.safetensors`,
      note: "HDR generation; feed the SDR clip as the drive video. Output is auto-tonemapped to SDR (LogC3 decompressed). Suggested prompt: 'HDR footage'."
    },
    {
      id: "pixel-spatial-upscaler-x2",
      displayName: "Pixel Spatial Upscaler ×2",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors`,
      note: "Apply on a refine stage with Upscale ×2 and source Stage Input. Dims snap to multiples of 64."
    },
    {
      id: "pixel-spatial-upscaler-x4",
      displayName: "Pixel Spatial Upscaler ×4",
      triggerPhrase: "",
      strength: 1,
      controlType: "none",
      weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors`,
      note: "Apply on a refine stage with Upscale ×4 and source Stage Input. Dims snap to multiples of 128."
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

  // frontend/types.ts
  var REF_SOURCE_BASE = "Base";
  var REF_SOURCE_REFINER = "Refiner";
  var REF_SOURCE_UPLOAD = "Upload";

  // frontend/utils.ts
  var isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
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
      duration
    };
  };
  var normalizePromptWindows = (rawClip) => {
    const rawList = readProp(rawClip, "promptWindows", "PromptWindows");
    if (!Array.isArray(rawList)) {
      return [];
    }
    return rawList.map((entry) => normalizePromptWindow(isRecord(entry) ? entry : {})).filter((window2) => window2 !== null).sort((a, b) => a.start - b.start);
  };
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
  var normalizeRetake = (value, clipDuration) => {
    if (!isRecord(value)) {
      return null;
    }
    const startRaw = Math.max(
      0,
      utils.toNumber(
        `${readProp(value, "startSeconds", "StartSeconds") ?? 0}`,
        0
      )
    );
    const lengthRaw = utils.toNumber(
      `${readProp(value, "lengthSeconds", "LengthSeconds") ?? 0}`,
      0
    );
    const window2 = clampWindowInDuration(
      startRaw,
      lengthRaw,
      clipDuration,
      RETAKE_MIN_DURATION
    );
    if (!window2) {
      return null;
    }
    const strengthRaw = readProp(value, "strength", "Strength");
    const strength = strengthRaw == null ? RETAKE_STRENGTH_DEFAULT : clamp(
      utils.toNumber(`${strengthRaw}`, RETAKE_STRENGTH_DEFAULT),
      RETAKE_STRENGTH_MIN,
      RETAKE_STRENGTH_MAX
    );
    return {
      startSeconds: roundToTenth(window2.startSeconds),
      lengthSeconds: roundToTenth(window2.lengthSeconds),
      strength
    };
  };
  var resolveRootPreferredUpscaleMethod = (upscaleMethodValues) => upscaleMethodValues.find(
    (value) => value.trim().toLowerCase().startsWith("latentmodel-")
  ) ?? upscaleMethodValues[0] ?? "";
  var snapStrengthToStep = (value, fallback, min, max, step) => {
    const unitScale = 1 / step;
    return Math.round(
      clamp(utils.toNumber(`${value ?? fallback}`, fallback), min, max) * unitScale
    ) / unitScale;
  };
  var normalizeSourceVideo = (value) => {
    if (!isRecord(value)) {
      return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
      return null;
    }
    const nonNegative = (raw) => Math.max(0, utils.toNumber(`${raw ?? 0}`, 0));
    const durationSeconds = nonNegative(
      readProp(value, "durationSeconds", "DurationSeconds")
    );
    let startSeconds = nonNegative(
      readProp(value, "startSeconds", "StartSeconds")
    );
    let lengthSeconds = nonNegative(
      readProp(value, "lengthSeconds", "LengthSeconds")
    );
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
        value.fileName == null ? null : `${value.fileName}`
      ),
      fps: nonNegative(readProp(value, "fps", "Fps")),
      durationSeconds: roundToTenth(durationSeconds),
      startSeconds: roundToTenth(startSeconds),
      lengthSeconds: roundToTenth(lengthSeconds)
    };
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
  var normalizeAudioSegment = (value, clipDuration) => {
    if (!isRecord(value)) {
      return null;
    }
    const rawSource = readProp(value, "source", "Source");
    const source = typeof rawSource === "string" && isAceStepFunAudioSource(rawSource) ? rawSource.trim() : normalizeUploadedAudio(rawSource);
    const startRaw = Math.max(
      0,
      utils.toNumber(
        `${readProp(value, "startSeconds", "StartSeconds") ?? 0}`,
        0
      )
    );
    const trimStartRaw = Math.max(
      0,
      utils.toNumber(
        `${readProp(value, "trimStartSeconds", "TrimStartSeconds") ?? 0}`,
        0
      )
    );
    const lengthRaw = utils.toNumber(
      `${readProp(value, "lengthSeconds", "LengthSeconds") ?? 0}`,
      0
    );
    const window2 = clampWindowInDuration(
      startRaw,
      lengthRaw,
      clipDuration,
      AUDIO_SEGMENT_MIN_LENGTH
    );
    if (!window2) {
      return null;
    }
    return {
      source,
      startSeconds: roundToTenth(window2.startSeconds),
      trimStartSeconds: roundToTenth(trimStartRaw),
      lengthSeconds: roundToTenth(window2.lengthSeconds)
    };
  };
  var normalizeAudioSegments = (value, clipDuration) => {
    if (!Array.isArray(value)) {
      return [];
    }
    return value.map((raw) => normalizeAudioSegment(raw, clipDuration)).filter((seg) => seg !== null);
  };
  var normalizeBoundaryOut = (value) => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
  };
  var normalizeContinueOverlap = (value) => {
    const num = Math.trunc(Number(value));
    if (!Number.isFinite(num) || num < DEFAULT_CONTINUE_OVERLAP_FRAMES) {
      return DEFAULT_CONTINUE_OVERLAP_FRAMES;
    }
    return Math.min(CONTINUE_OVERLAP_MAX_FRAMES, num - num % 8);
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
  var defaultIcLora = (overrides = {}) => ({
    lora: "",
    preset: IC_LORA_PRESET_CUSTOM_ID,
    source: IC_LORA_SOURCE_UPLOAD,
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
    video: null,
    driveAudioRef: false,
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
  var normalizeIcLoraSource = (value) => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact || compact === "upload") {
      return IC_LORA_SOURCE_UPLOAD;
    }
    if (compact === "stageinput") {
      return IC_LORA_SOURCE_STAGE_INPUT;
    }
    return normalizeControlNetSource(value);
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
  var normalizeIcLora = (raw, stageCount = 0) => {
    if (!isRecord(raw)) {
      return null;
    }
    const lora = normalizeControlNetLora(readProp(raw, "lora", "Lora"));
    if (!lora) {
      return null;
    }
    const preset = `${readProp(raw, "preset", "Preset") ?? ""}`.trim();
    const stage = normalizeIcLoraStage(
      readProp(raw, "stage", "Stage"),
      stageCount
    );
    let source = normalizeIcLoraSource(readProp(raw, "source", "Source"));
    if (source === IC_LORA_SOURCE_STAGE_INPUT && stage < 1) {
      source = IC_LORA_SOURCE_UPLOAD;
    }
    return {
      lora,
      preset: preset || IC_LORA_PRESET_CUSTOM_ID,
      source,
      stage,
      strength: snapStrengthToStep(
        readProp(raw, "strength", "Strength"),
        IC_LORA_STRENGTH_DEFAULT,
        IC_LORA_STRENGTH_MIN,
        IC_LORA_STRENGTH_MAX,
        IC_LORA_STRENGTH_STEP
      ),
      attentionStrength: snapStrengthToStep(
        readProp(raw, "attentionStrength", "AttentionStrength"),
        IC_LORA_ATTENTION_DEFAULT,
        IC_LORA_ATTENTION_MIN,
        IC_LORA_ATTENTION_MAX,
        IC_LORA_ATTENTION_STEP
      ),
      controlType: normalizeIcLoraControlType(
        readProp(raw, "controlType", "ControlType")
      ),
      video: normalizeUploadedAudio(readProp(raw, "video", "Video")),
      driveAudioRef: readProp(raw, "driveAudioRef", "DriveAudioRef") === true
    };
  };
  var normalizeIcLoras = (rawClip, stageCount = 0) => {
    const raw = readProp(rawClip, "icLoras", "IcLoras");
    if (Array.isArray(raw)) {
      const entries = raw.map((entry) => normalizeIcLora(entry, stageCount)).filter((entry) => entry !== null);
      if (entries.length > 0) {
        return entries;
      }
    }
    const legacyLora = normalizeControlNetLora(
      readProp(rawClip, "controlNetLora", "ControlNetLora")
    );
    if (!legacyLora) {
      return [];
    }
    return [
      defaultIcLora({
        lora: legacyLora,
        source: normalizeControlNetSource(
          readProp(rawClip, "controlNetSource", "ControlNetSource")
        )
      })
    ];
  };
  var reconcileIcLoraStage = (entry) => {
    if (entry.stage < 1 && entry.source === IC_LORA_SOURCE_STAGE_INPUT) {
      entry.source = IC_LORA_SOURCE_UPLOAD;
    }
  };
  var hasSlotSourcedIcLora = (icLoras) => icLoras.some(
    (entry) => entry.source !== IC_LORA_SOURCE_UPLOAD && entry.source !== IC_LORA_SOURCE_STAGE_INPUT
  );
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
  var readRawStageProp = (raw, camel, pascal) => readProp(raw, camel, pascal);
  var readRawStageString = (raw, camel, pascal) => {
    const v = readRawStageProp(raw, camel, pascal);
    if (v == null) {
      return void 0;
    }
    const s = `${v}`.trim();
    return s.length > 0 ? s : void 0;
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
      const name = `${readRawStageProp(entry, "name", "Name") ?? ""}`.trim();
      if (!name) {
        continue;
      }
      const weightRaw = readRawStageProp(entry, "weight", "Weight");
      const weight = utils.toNumber(`${weightRaw ?? 1}`, 1);
      out.push({ name, weight: Number.isFinite(weight) ? weight : 1 });
    }
    return out;
  };
  var cloneStageLoras = (loras) => loras.map((lora) => ({ name: lora.name, weight: lora.weight }));
  var buildDefaultStage = (getRootDefaults2, getDefaultStageModel2, previousStage, refCount) => {
    const defaults = getRootDefaults2();
    return {
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
      scheduler: previousStage ? previousStage.scheduler : defaults.schedulerValues[0] ?? "normal",
      loras: previousStage ? cloneStageLoras(previousStage.loras) : []
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
  var buildDefaultClip = (getRootDefaults2, getDefaultStageModel2, includeDefaultRef = false, previousClip = null) => {
    const defaults = getRootDefaults2();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    return {
      skipped: false,
      hue: UNASSIGNED_HUE,
      boundaryOut: "cut",
      boundaryOutOverlap: DEFAULT_CONTINUE_OVERLAP_FRAMES,
      duration: previousClip ? previousClip.duration : snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
        defaults.fps
      ),
      audioSource: AUDIO_SOURCE_NATIVE,
      icLoras: [],
      saveAudioTrack: false,
      clipLengthFromAudio: false,
      clipLengthFromControlNet: false,
      reuseAudio: false,
      uploadedAudio: null,
      audioSegments: [],
      prompt: "",
      promptWindows: [],
      retake: null,
      sourceVideo: null,
      refs,
      stages: [
        {
          ...buildDefaultStage(
            getRootDefaults2,
            getDefaultStageModel2,
            previousClip?.stages[0] ?? null,
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
      scheduler: `${rawStage.scheduler ?? fallback.scheduler}` || fallback.scheduler,
      loras: normalizeStageLoras(
        readRawStageProp(rawStage, "loras", "Loras")
      )
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
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const icLoras = normalizeIcLoras(rawClip, stagesRaw.length);
    const audioSourceOptions = buildAudioSourceOptions(rawAudioSource, {
      controlNetEnabled: hasSlotSourcedIcLora(icLoras)
    });
    const fps = Math.max(1, defaults.fps);
    const sourceVideo = normalizeSourceVideo(
      readProp(rawClip, "sourceVideo", "SourceVideo")
    );
    const rawDuration = sourceVideo?.lengthSeconds ?? utils.toNumber(`${rawClip.duration}`, defaults.frames / fps);
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
    const audioSource = resolveAudioSourceValue(
      rawAudioSource,
      audioSourceOptions
    );
    const clipLengthFromAudio = canUseClipLengthFromAudio(audioSource) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet = hasSlotSourcedIcLora(icLoras) && !clipLengthFromAudio && !!(rawClip.clipLengthFromControlNet ?? rawClip.ClipLengthFromControlNet);
    const clip = {
      skipped: !!rawClip.skipped,
      hue: normalizeStoredHue(rawClip.hue),
      boundaryOut: normalizeBoundaryOut(
        rawClip.boundaryOut ?? rawClip.BoundaryOut
      ),
      boundaryOutOverlap: normalizeContinueOverlap(
        rawClip.boundaryOutOverlap ?? rawClip.BoundaryOutOverlap
      ),
      duration,
      audioSource,
      icLoras,
      saveAudioTrack: !!rawClip.saveAudioTrack,
      clipLengthFromAudio,
      clipLengthFromControlNet,
      reuseAudio: !!rawClip.reuseAudio,
      uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
      audioSegments: normalizeAudioSegments(
        readProp(rawClip, "audioSegments", "AudioSegments"),
        duration
      ),
      prompt: `${readProp(rawClip, "prompt", "Prompt") ?? ""}`,
      promptWindows: normalizePromptWindows(rawClip),
      retake: normalizeRetake(
        readProp(rawClip, "retake", "Retake"),
        duration
      ),
      sourceVideo,
      refs,
      stages
    };
    return clip;
  };

  // frontend/debugLog.ts
  var videoStagesDebugEnabled = () => typeof window !== "undefined" && !!window.__VIDEO_STAGES_DEBUG__;
  var videoStagesDebugLog = (area, message, ...details) => {
    if (!videoStagesDebugEnabled()) {
      return;
    }
    console.debug(`[VideoStages debug ${area}]`, message, ...details);
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
    const text = prompt ?? "";
    BOUNDARY_RE.lastIndex = 0;
    const starts = [];
    for (let match = BOUNDARY_RE.exec(text); match !== null; match = BOUNDARY_RE.exec(text)) {
      starts.push(match.index);
    }
    if (starts.length === 0) {
      return { leading: text, tags: [] };
    }
    const leading = text.slice(0, starts[0]);
    const tags = [];
    for (let i = 0; i < starts.length; i++) {
      const start = starts[i];
      const nextStart = i + 1 < starts.length ? starts[i + 1] : text.length;
      const span = text.slice(start, nextStart);
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
        const list = windows.get(tag.clip) ?? [];
        list.push({
          start: Math.max(0, tag.window.start),
          duration: tag.window.end - tag.window.start,
          prompt: tag.body.trim()
        });
        windows.set(tag.clip, list);
      }
    }
    for (const list of windows.values()) {
      list.sort((a, b) => a.start - b.start);
    }
    return { sections, windows };
  };
  var extractGlobalPrompt = (prompt) => tokenizePrompt(prompt).leading.trim();

  // frontend/swarmInputs.ts
  var DATA_INPUT_ID = "input_videostages";
  var warnedMissingDataInput = false;
  var getPromptInput = () => {
    const el = document.getElementById("input_prompt");
    return el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement ? el : null;
  };
  var getDataInput = () => {
    const el = document.getElementById(DATA_INPUT_ID);
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
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
  var withSuppressedPromptTabComplete = (fn) => {
    const tc = typeof promptTabComplete !== "undefined" ? promptTabComplete : null;
    if (!tc) {
      fn();
      return;
    }
    const prev = tc.blockInput;
    tc.blockInput = true;
    try {
      fn();
    } finally {
      tc.blockInput = prev;
    }
  };
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
      triggerChangeFor(dataEl);
    }
    const promptEl = getPromptInput();
    if (promptEl) {
      withSuppressedPromptTabComplete(() => triggerChangeFor(promptEl));
    }
  };
  var readGlobalPrompt = () => extractGlobalPrompt(getPromptInput()?.value ?? "");
  var readCarrierSnapshot = () => JSON.stringify({
    data: readDataParam(),
    prompt: getPromptInput()?.value ?? ""
  });
  var restoreCarrierSnapshot = (snapshot) => {
    let parsed;
    try {
      parsed = JSON.parse(snapshot);
    } catch {
      return;
    }
    writeDataParam(typeof parsed.data === "string" ? parsed.data : "");
    const el = getPromptInput();
    if (!el) {
      return;
    }
    el.value = typeof parsed.prompt === "string" ? parsed.prompt : "";
    triggerChangeFor(el);
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
  var WIDTH_INPUT_IDS = ["input_width", "input_aspectratiowidth"];
  var HEIGHT_INPUT_IDS = ["input_height", "input_aspectratioheight"];
  var FPS_INPUT_IDS = ["input_videofps", "input_videoframespersecond"];
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
    const width = trimDomValue(firstPresentInput(...WIDTH_INPUT_IDS));
    const height = trimDomValue(firstPresentInput(...HEIGHT_INPUT_IDS));
    const fps = trimDomValue(firstPresentInput(...FPS_INPUT_IDS));
    return `${width}|${height}|${fps}`;
  };
  var getRootDefaults = () => {
    let model = utils.getSelectElement("input_videomodel");
    if ((!model || model.options.length === 0) && isRootTextToVideoModel()) {
      model = utils.getSelectElement("input_model");
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
    const upscaleMethod = utils.getSelectElement("input_refinerupscalemethod");
    const upscaleMethodValues = utils.getSelectValues(upscaleMethod);
    const upscaleMethodLabels = utils.getSelectLabels(upscaleMethod);
    const steps = firstPresentInput("input_videosteps", "input_steps");
    const cfgScale = firstPresentInput("input_videocfg", "input_cfgscale");
    const widthInput = firstPresentInput(...WIDTH_INPUT_IDS);
    const heightInput = firstPresentInput(...HEIGHT_INPUT_IDS);
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

  // frontend/store.ts
  var createTimelineStore = (deps) => {
    let canonical = null;
    let cachedToken = null;
    let syncedToken = null;
    let lastGoodSerialized = "";
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
      if (syncedToken === null) {
        syncedToken = token;
      }
      return canonical;
    };
    const notify = (meta) => {
      const state = canonical;
      if (!state) {
        return;
      }
      const snapshot = structuredClone(state);
      for (const cb of [...subscribers]) {
        try {
          cb(snapshot, meta);
        } catch {
        }
      }
    };
    const save = (state, origin, notifyDomChange, hint) => {
      const serialized = deps.writeQuiet(state);
      lastGoodSerialized = serialized;
      canonical = deps.parse(serialized) ?? structuredClone(state);
      cachedToken = deps.readToken();
      syncedToken = cachedToken;
      if (notifyDomChange) {
        deps.notifyHost();
      }
      notify({ origin, hint });
      return serialized;
    };
    const syncFromCarrier = () => {
      const token = deps.readToken();
      if (canonical && syncedToken !== null && token === syncedToken) {
        return false;
      }
      revalidate();
      syncedToken = cachedToken;
      notify({ origin: "external" });
      return true;
    };
    return {
      getState: () => structuredClone(revalidate()),
      save,
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
        subscribers.clear();
      }
    };
  };

  // frontend/uiState.ts
  var UI_STATE_KEY = "videostages_ui_state";
  var serializeUiState = (clips) => {
    const state = {
      clips: clips.map((clip) => ({
        hue: typeof clip.hue === "number" ? clip.hue : null
      }))
    };
    return JSON.stringify(state);
  };
  var applyUiStateFrom = (raw, clips) => {
    if (!raw) {
      return;
    }
    const parsed = safeJsonParse(raw, null);
    const storedClips = isRecord(parsed) && Array.isArray(parsed.clips) ? parsed.clips : [];
    for (let i = 0; i < clips.length; i++) {
      const stored = storedClips[i];
      if (!isRecord(stored)) {
        continue;
      }
      if (typeof stored.hue === "number" && Number.isFinite(stored.hue)) {
        clips[i].hue = stored.hue;
      }
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

  // frontend/persistence.ts
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
      skipped: clip.skipped,
      boundaryOut: clip.boundaryOut,
      boundaryOutOverlap: clip.boundaryOutOverlap,
      duration: clip.duration,
      audioSource: clip.audioSource,
      icLoras: clip.icLoras.map((entry) => ({
        lora: entry.lora,
        preset: entry.preset,
        source: entry.source,
        stage: entry.stage,
        strength: entry.strength,
        attentionStrength: entry.attentionStrength,
        controlType: entry.controlType,
        video: entry.video,
        driveAudioRef: entry.driveAudioRef
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
      audioSegments: clip.audioSegments.map((seg) => ({
        source: seg.source,
        startSeconds: seg.startSeconds,
        trimStartSeconds: seg.trimStartSeconds,
        lengthSeconds: seg.lengthSeconds
      })),
      retake: clip.retake ? {
        startSeconds: clip.retake.startSeconds,
        lengthSeconds: clip.retake.lengthSeconds,
        strength: clip.retake.strength
      } : null,
      refs: clip.refs.map((ref) => ({
        source: ref.source,
        uploadFileName: ref.uploadFileName,
        uploadedImage: ref.uploadedImage,
        frame: ref.frame,
        fromEnd: ref.fromEnd
      })),
      stages: clip.stages.map((stage) => ({
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
        scheduler: stage.scheduler,
        loras: stage.loras.map((lora) => ({
          name: lora.name,
          weight: lora.weight
        }))
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
  var overlayPromptAndUiState = (clips) => {
    const { sections, windows } = parseClipPrompts(
      getPromptInput()?.value ?? ""
    );
    for (let i = 0; i < clips.length; i++) {
      clips[i].prompt = sections.get(i) ?? "";
      clips[i].promptWindows = (windows.get(i) ?? []).map((window2) => ({
        prompt: window2.prompt,
        start: window2.start,
        duration: window2.duration
      }));
    }
    applyUiState(clips);
    assignMissingHues(clips);
  };
  var parseSerializedState = (serialized, inherited) => {
    try {
      const parsed = JSON.parse(serialized);
      let clipsRaw;
      let stored = {};
      if (Array.isArray(parsed)) {
        clipsRaw = parsed;
      } else if (isRecord(parsed)) {
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
          isRecord(el) ? el : {},
          getRootDefaults,
          getDefaultStageModel
        )
      );
      overlayPromptAndUiState(clips);
      return rootConfig(resolveRootDims(inherited, stored), clips);
    } catch {
      return null;
    }
  };
  var inheritedDims = () => {
    const defaults = getRootDefaults();
    return {
      width: defaults.width,
      height: defaults.height,
      fps: defaults.fps
    };
  };
  var parseEmptyConfig = () => {
    const clips = [];
    overlayPromptAndUiState(clips);
    return rootConfig(resolveRootDims(inheritedDims(), {}), clips);
  };
  var writeQuietly = (state) => {
    assignMissingHues(state.clips);
    const serialized = serializeStateForStorage(state);
    writeDataParam(serialized);
    writeClipPrompts(
      state.clips.map((clip) => ({
        prompt: clip.prompt,
        windows: clip.promptWindows
      }))
    );
    saveUiState(state.clips);
    return serialized;
  };
  var store = createTimelineStore({
    readToken: () => `${readStateToken()}\0${readInheritedDimsSignature()}`,
    readDataParam,
    parse: (serialized) => parseSerializedState(serialized, inheritedDims()),
    parseEmpty: parseEmptyConfig,
    writeQuiet: writeQuietly,
    notifyHost: notifyCarrierChanged
  });
  var getTimelineStore = () => store;
  var getState = () => store.getState();
  var saveState = (state, options) => {
    const willNotifyDom = options?.notifyDomChange !== false;
    const serialized = store.save(
      state,
      options?.origin ?? "timeline",
      willNotifyDom,
      options?.valueOnly ? "value-only" : void 0
    );
    videoStagesDebugLog("persistence", "saveState", {
      notifyDomChange: options?.notifyDomChange,
      willNotifyDom,
      jsonChars: serialized.length
    });
  };
  var getClips = () => getState().clips;
  var saveClips = (clips, options) => {
    videoStagesDebugLog("persistence", "saveClips", {
      clipCount: clips.length
    });
    const state = getState();
    state.clips = clips;
    const notifyDomChange = options?.notifyDomChange !== void 0 ? options.notifyDomChange : isVideoStagesEnabled();
    saveState(state, { ...options, notifyDomChange });
  };

  // frontend/refineVideoButton.ts
  var refineNeedsExtraStageMessage = (skipCount) => `Refine Video needs clip 0 to have at least one active stage after Stage ${skipCount - 1} (for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;
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
    if (typeof registerMediaButton !== "function") {
      return;
    }
    registerMediaButton(
      "Refine Video",
      (src) => {
        let parsedMetadata = null;
        if (currentMetadataVal) {
          try {
            const readable = interpretMetadata(currentMetadataVal);
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
          showError(refineNeedsExtraStageMessage(skipCount));
          return;
        }
        toDataURL(src, (videoDataUrl) => {
          const inputOverrides = {
            videostagesrefinesourcevideo: videoDataUrl,
            videostagesrefineskipstages: skipCount,
            images: 1
          };
          const seed = isRecord(params) ? readProp(params, "seed") : void 0;
          if (typeof seed === "number") {
            inputOverrides.seed = seed;
          }
          mainGenHandler.doGenerate(inputOverrides, {});
        });
      },
      "Re-runs VideoStages using this video as the source for clip 0 (skips the first N stage samplers, where N is read from the source video's metadata). Requires an extra stage beyond those.",
      ["video"],
      true
    );
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

  // frontend/selection.ts
  var NO_SELECTION = { kind: "none" };
  var selection = NO_SELECTION;
  var selectionSubscribers = /* @__PURE__ */ new Set();
  var clipIdxOf = (sel) => sel.kind === "none" || sel.kind === "boundary" ? null : sel.clipIdx;
  var sameSelection = (a, b) => {
    if (a.kind !== b.kind) {
      return false;
    }
    if (a.kind === "none" || b.kind === "none") {
      return true;
    }
    if (a.kind === "boundary" || b.kind === "boundary") {
      return a.kind === "boundary" && b.kind === "boundary" && a.leftClipIdx === b.leftClipIdx;
    }
    if (a.clipIdx !== clipIdxOf(b)) {
      return false;
    }
    if (a.kind === "clip" && b.kind === "clip") {
      return a.stageIdx === b.stageIdx;
    }
    if (a.kind === "ref" && b.kind === "ref") {
      return a.refIdx === b.refIdx;
    }
    if (a.kind === "prompt-minor" && b.kind === "prompt-minor") {
      return a.windowIdx === b.windowIdx;
    }
    if (a.kind === "audio-segment" && b.kind === "audio-segment") {
      return a.segIdx === b.segIdx;
    }
    return true;
  };
  var isSameSelection = (a, b) => sameSelection(a, b);
  var getSelection = () => selection;
  var getSelectedClipIndex = () => clipIdxOf(selection);
  var setSelection = (next) => {
    if (sameSelection(selection, next)) {
      return;
    }
    selection = next;
    for (const cb of [...selectionSubscribers]) {
      try {
        cb(selection);
      } catch {
      }
    }
  };
  var subscribeSelection = (cb) => {
    selectionSubscribers.add(cb);
    return () => {
      selectionSubscribers.delete(cb);
    };
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
    const text = `${value ?? ""}`;
    return text.length <= max ? text : `${text.slice(0, Math.max(0, max - 1))}…`;
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
    const minWidthPx = DEFAULT_MIN_WIDTH_PX;
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
      const src = mediaPreviewSrc(ref.uploadedImage?.data ?? "");
      return `<div class="vst-region-thumb-cell vst-region-thumb-${side}" style="background-image:url('${escapeAttr(src)}')"></div>`;
    };
    const cells = cell(startRef, "start") + (endRef ? cell(endRef, "end") : "");
    const cellCount = endRef ? 2 : 1;
    return `<div class="vst-region-thumb" data-cells="${cellCount}" aria-hidden="true">${cells}</div>`;
  };
  var renderWindowSpan = (opts) => {
    const start = clamp(opts.startSeconds, 0, opts.durationSeconds);
    const end = clamp(
      opts.startSeconds + opts.lengthSeconds,
      start,
      opts.durationSeconds
    );
    if (end <= start) {
      return "";
    }
    const left = start / opts.durationSeconds * 100;
    const width = (end - start) / opts.durationSeconds * 100;
    return `<div class="${opts.className}" ${opts.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeAttr(opts.title)}" aria-label="${escapeAttr(opts.ariaLabel)}"><span class="${opts.className}-resize ${opts.className}-resize-l" ${opts.edgeAttr}="left" aria-hidden="true"></span><span class="${opts.labelClass}">${escapeAttr(opts.label)}</span><span class="${opts.className}-resize ${opts.className}-resize-r" ${opts.edgeAttr}="right" aria-hidden="true"></span></div>`;
  };
  var renderRetakeRegionShade = (clip, durationSeconds) => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
      return "";
    }
    const start = clamp(retake.startSeconds, 0, durationSeconds);
    const end = clamp(
      retake.startSeconds + retake.lengthSeconds,
      start,
      durationSeconds
    );
    if (end <= start) {
      return "";
    }
    const left = start / durationSeconds * 100;
    const width = (end - start) / durationSeconds * 100;
    return `<div class="vst-region-off" style="left:${left}%;width:${width}%" aria-hidden="true"></div>`;
  };
  var renderRetakeOverlay = (clip, clipIdx, durationSeconds) => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
      return "";
    }
    const start = clamp(retake.startSeconds, 0, durationSeconds);
    const end = clamp(
      retake.startSeconds + retake.lengthSeconds,
      start,
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
      const title = `${source} · frame ${ref.frame ?? 0}${isEnd ? " (from end)" : ""}${isPrimary ? " (cover)" : ""} · ${formatTimeLabel(time, unit, fps)}`;
      const kindClass = (isEnd ? " vst-key-end" : " vst-key-start") + (isPrimary ? " vst-key-primary" : "");
      return `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeAttr(title)}" aria-hidden="true"><span class="vst-key-dot" aria-hidden="true"></span></span>`;
    }).join("");
    return `<div class="vst-keys" title="Reference markers">${pips}</div>`;
  };
  var renderBadges = (clip, clipIdx) => {
    const stage0 = (clip.stages ?? [])[0];
    if (!stage0) {
      return `<div class="vst-badges"></div>`;
    }
    const model = stage0.model ?? "";
    const short = shortModelName(model);
    const full = `${model}`.trim() || "(default)";
    const title = `Clip model: ${full} — click to change (applies to Stage 0)`;
    const badge = `<span class="vst-badge vst-badge-model" data-vst-model data-clip-idx="${clipIdx}" role="button" tabindex="0" title="${escapeAttr(title)}" aria-label="${escapeAttr(title)}">${escapeAttr(short)}</span>`;
    const icCount = (clip.icLoras ?? []).length;
    const icTitle = `${icCount} IC-LoRA${icCount === 1 ? "" : "s"} on this clip — edit in the clip panel`;
    const icBadge = icCount > 0 ? `<span class="vst-badge vst-badge-iclora" title="${escapeAttr(icTitle)}" aria-label="${escapeAttr(icTitle)}">IC×${icCount}</span>` : "";
    return `<div class="vst-badges">${badge}${icBadge}</div>`;
  };
  var renderStageChips = (clip, clipIdx) => {
    const stages = clip.stages ?? [];
    const chips = stages.map((stage, stageIdx) => {
      const skipped = stage?.skipped === true;
      const skippedClass = skipped ? " vst-stage-chip-skipped" : "";
      const title = `${stageChipTitle(stage, stageIdx)}${skipped ? " (skipped)" : ""} · click to edit · Shift+click to delete`;
      const label = `${skipped ? "⊘ " : ""}${stageChipLabel(stageIdx)}`;
      return `<span class="vst-chip vst-stage-chip${skippedClass}" data-vst-stage data-clip-idx="${clipIdx}" data-stage-idx="${stageIdx}" role="button" tabindex="0" title="${escapeAttr(title)}">${escapeAttr(label)}</span>`;
    }).join("");
    return chips;
  };
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
  var renderBoundarySeams = (clips, layouts) => {
    const seams = [];
    for (let i = 1; i < layouts.length; i++) {
      const leftClipIdx = i - 1;
      const clip = clips[leftClipIdx];
      if (!clip) {
        continue;
      }
      const value = clip.boundaryOut ?? "cut";
      const glyph = BOUNDARY_GLYPH[value] ?? BOUNDARY_GLYPH.cut;
      const label = BOUNDARY_LABEL[value] ?? BOUNDARY_LABEL.cut;
      const title = `Boundary clip ${leftClipIdx} → ${i}: ${label}. Click to edit.`;
      const ariaLabel = `Clip ${leftClipIdx} outgoing boundary: ${label}. Click to edit.`;
      seams.push(
        `<button type="button" class="basic-button vst-boundary-chip vst-boundary-${value}" data-vst-boundary-chip data-left-clip-idx="${leftClipIdx}" data-boundary="${value}" style="left:${layouts[i].startPx}px" title="${escapeAttr(title)}" aria-label="${escapeAttr(ariaLabel)}"><span class="vst-boundary-glyph" aria-hidden="true">${escapeAttr(glyph)}</span></button>`
      );
    }
    return seams.join("");
  };
  var promptWindowGeom = (layout, window2, pxPerSecond) => {
    const clipDur = Math.max(0, layout.durationSeconds);
    const startSec = clamp(window2.start, 0, clipDur);
    const endSec = clamp(window2.start + window2.duration, startSec, clipDur);
    return {
      startSec,
      endSec,
      leftPx: startSec * pxPerSecond,
      widthPx: Math.max(2, (endSec - startSec) * pxPerSecond),
      active: `${window2.prompt ?? ""}`.trim() !== ""
    };
  };
  var clipInnerWidth = (widthPx) => Math.max(1, widthPx - 2);
  var headTag = (kind, label, opts) => {
    const cls = `vst-head-tag vst-head-tag-${kind}` + (opts?.active ? " vst-head-tag-active" : "") + (opts?.muted ? " vst-head-tag-muted" : "");
    const style = opts?.style ? ` style="${opts.style}"` : "";
    return `<div class="${cls}"${style} aria-hidden="true"><span class="vst-head-tag-pill">${label}</span><span class="vst-head-tag-tick"></span></div>`;
  };
  var renderTrackHead = (iconClass, icon, title, tags) => `<div class="vst-track-head"><div class="vst-head-top"><div class="vst-track-icon ${iconClass}" aria-hidden="true">${icon}</div><div class="vst-track-label"><strong>${title}</strong></div></div><div class="vst-head-tags">${tags}</div></div>`;
  var PROMPT_PLACEHOLDER = "(no prompt)";
  var renderPromptTrackRow = (clips, layouts, pxPerSecond, globalPrompt) => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const parts = [];
    for (let i = 0; i < layouts.length; i++) {
      const layout = layouts[i];
      const clip = clips[i];
      if (!clip) {
        continue;
      }
      const clipWidth = clipInnerWidth(layout.widthPx);
      const windows = clip.promptWindows ?? [];
      const ownPrompt = `${clip.prompt ?? ""}`.trim();
      const inherited = ownPrompt === "";
      const major = inherited ? globalTrimmed : ownPrompt;
      const overlays = windows.map((w) => promptWindowGeom(layout, w, pxPerSecond)).filter((g) => g.active && g.endSec > g.startSec).map(
        (g) => `<div class="vst-major-off" style="left:${g.leftPx}px;width:${g.widthPx}px" aria-hidden="true"></div>`
      ).join("");
      const majorText = major === "" ? PROMPT_PLACEHOLDER : truncate(major, 120);
      const majorClass = (major === "" ? " vst-major-empty" : "") + (inherited && major !== "" ? " vst-major-inherited" : "");
      const majorTitle = (major === "" ? PROMPT_PLACEHOLDER : major) + (inherited && major !== "" ? " — inherited from the global prompt; click to set a clip prompt" : " — click to edit");
      parts.push(
        `<div class="vst-major-seg${majorClass}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="${escapeAttr(majorTitle)}">` + overlays + `<span class="vst-major-text">${escapeAttr(majorText)}</span></div>`
      );
      const minorSegs = windows.map((w, j) => {
        const g = promptWindowGeom(layout, w, pxPerSecond);
        const text = `${w.prompt ?? ""}`.trim();
        const label = text === "" ? "(empty)" : truncate(text, 60);
        return `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${g.leftPx}px;width:${g.widthPx}px" title="${escapeAttr(`${text || "(empty minor prompt)"} · Shift+click to delete`)}"><span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span><span class="vst-minor-text">${escapeAttr(label)}</span><span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span></div>`;
      }).join("");
      parts.push(
        `<div class="vst-minor-lane" data-vst-prompt-add data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="Click empty space to add a minor prompt">${minorSegs}</div>`
      );
    }
    return `<div class="vst-track-row vst-track-prompt">` + renderTrackHead(
      "vst-track-icon-prompt",
      "✎",
      "Prompt",
      headTag("major", "Major", { active: true }) + headTag("relay", "Relay", {
        active: clips.some(
          (c) => (c.promptWindows?.length ?? 0) > 0
        )
      })
    ) + `<div class="vst-track-cell vst-prompt-cell">${parts.join("")}</div></div>`;
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
  var renderAudioSegmentBlock = (seg, clipIdx, segIdx, durationSeconds) => {
    const start = clamp(seg.startSeconds, 0, durationSeconds);
    const end = clamp(
      seg.startSeconds + seg.lengthSeconds,
      start,
      durationSeconds
    );
    const name = typeof seg.source === "string" ? seg.source : seg.source?.fileName;
    const labelText = name ? name : "audio segment";
    const label = `${roundToTenth(start)}–${roundToTenth(end)} s`;
    return renderWindowSpan({
      className: "vst-audio-seg",
      dataAttrs: `data-vst-audio-seg data-clip-idx="${clipIdx}" data-seg-idx="${segIdx}"`,
      edgeAttr: "data-vst-audio-seg-edge",
      labelClass: "vst-audio-seg-label",
      label: labelText,
      title: `${labelText} · ${label} · drag to move/resize · Shift+click to delete`,
      ariaLabel: `Edit audio segment ${segIdx} for clip ${clipIdx}`,
      startSeconds: seg.startSeconds,
      lengthSeconds: seg.lengthSeconds,
      durationSeconds
    });
  };
  var renderAudioSegmentLanes = (clip, clipIdx, durationSeconds, startPx, widthPx) => {
    const place = (laneIdx) => `left:${startPx}px;width:${widthPx}px;--vst-audio-lane-idx:${laneIdx}`;
    const blankLane = (laneIdx) => `<div class="vst-audio-seg-lane vst-audio-seg-lane-blank" data-vst-audio-seg-add data-clip-idx="${clipIdx}" style="${place(laneIdx)}" title="Click or drag to add an audio segment"></div>`;
    if (durationSeconds <= 0) {
      return blankLane(0);
    }
    const segments = clip.audioSegments ?? [];
    const lanes = segments.map(
      (seg, segIdx) => `<div class="vst-audio-seg-lane" style="${place(segIdx)}">` + renderAudioSegmentBlock(seg, clipIdx, segIdx, durationSeconds) + `</div>`
    );
    lanes.push(blankLane(segments.length));
    return lanes.join("");
  };
  var renderAudioTrackRow = (clips, layouts) => {
    const segments = layouts.map((l) => {
      const clip = clips[l.index];
      if (!clip) {
        return "";
      }
      const badge = audioSourceBadge(clip.audioSource ?? "");
      const native = badge.label === "Native";
      const width = clipInnerWidth(l.widthPx);
      const kindClass = native ? " vst-audio-native vst-audio-kind-native" : isAceStepFunAudioSource(clip.audioSource ?? "") ? " vst-audio-kind-ace" : clip.audioSource === AUDIO_SOURCE_VOICE_REF ? " vst-audio-kind-voiceref" : " vst-audio-kind-upload";
      const upload = !native && (clip.audioSource === "Upload" || clip.audioSource === AUDIO_SOURCE_VOICE_REF) ? clip.uploadedAudio?.fileName : null;
      const labelText = upload ? `${badge.label} · ${upload}` : badge.label;
      const title = native ? "Audio: Native — click to choose an audio source" : `${badge.title} — click to edit`;
      const barCount = Math.min(
        400,
        Math.max(8, Math.floor(width / 5.5))
      );
      const bars = waveBarHeights(l.index, barCount).map((h) => `<span style="height:${h}%"></span>`).join("");
      const hint = native ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>` : "";
      const body = `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>${hint}`;
      return `<div class="vst-audio-clip${kindClass}" data-vst-audio="clip" data-clip-idx="${l.index}" role="button" tabindex="0" style="left:${l.startPx}px;width:${width}px" title="${escapeAttr(title)}" aria-label="Edit audio for clip ${l.index}"><span class="vst-audio-label">${escapeAttr(labelText)}</span>` + audioFlagChips(clip) + body + `</div>` + renderAudioSegmentLanes(
        clip,
        l.index,
        clip.duration || 0,
        l.startPx,
        width
      );
    }).join("");
    const laneCount = Math.max(
      1,
      ...clips.map((clip) => (clip.audioSegments?.length ?? 0) + 1)
    );
    const laneTags = [headTag("src", "Src", { active: true })];
    for (let i = 0; i < laneCount; i++) {
      const blank = i === laneCount - 1;
      laneTags.push(
        headTag("seg", blank ? "+" : `S${i + 1}`, {
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
    ) + `<div class="vst-track-cell vst-audio-cell">${segments}</div></div>`;
  };
  var REF_EDGE_ALIGN_FRAMES = 3;
  var renderReferencesTrackRow = (clips, layouts, fps, unit) => {
    const lanes = layouts.map((l) => {
      const clip = clips[l.index];
      if (!clip) {
        return "";
      }
      const laneWidth = clipInnerWidth(l.widthPx);
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
    return `<div class="vst-track-row vst-track-refs">` + renderTrackHead(
      "vst-track-icon-refs",
      "⧉",
      "References",
      headTag("refs", "Refs", {
        active: clips.some((c) => (c.refs?.length ?? 0) > 0),
        muted: true
      })
    ) + `<div class="vst-track-cell">${lanes}</div></div>`;
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
    const settingsChip = `<button type="button" class="basic-button small-button vst-settings-chip" data-vst-settings title="${escapeAttr(settingsTip)}" aria-label="${escapeAttr(settingsTip)}"><span class="vst-settings-dims">${chipWidth}×${chipHeight}</span><span class="vst-settings-chip-sep" aria-hidden="true">·</span><span class="vst-settings-fps">${chipFps} fps</span></button>`;
    const enabled = options?.enabled !== false;
    const enableToggle = `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual."><span class="toggle-switch"><input type="checkbox" class="auto-slider-toggle vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}><div class="auto-slider-toggle-content"></div></span><span class="vst-enable-label">Enable</span></label>`;
    const header = `<div class="vst-topbar${enabled ? "" : " vst-topbar-disabled"}"><div class="vst-topbar-main"><span class="vst-title">Timeline</span>` + enableToggle + `<span class="vst-sub"><span class="vst-stat-num">${clips.length}</span> ${clipWord}</span>` + settingsChip + `</div><div class="vst-topbar-tools"><button type="button" class="basic-button small-button btn-primary vst-add-clip" data-vst-add-clip title="Add a new clip to the end of the sequence">+ Clip</button><span class="vst-tool-sep" aria-hidden="true"></span><div class="vst-zoom" role="group" aria-label="Timeline zoom (Ctrl+wheel over the track)"><button type="button" class="basic-button small-button" data-vst-zoom-out title="Zoom out (show more time)" aria-label="Zoom out">−</button><span class="vst-zoom-pct" data-vst-zoom-pct title="Zoom level (100% = default)">${zoomPct}%</span><input type="range" class="vst-zoom-slider" data-vst-zoom-slider min="${MIN_PX_PER_SECOND}" max="${MAX_PX_PER_SECOND}" step="1" value="${Math.round(pxPerSecond)}" aria-label="Zoom (pixels per second)" title="Zoom (applies on release)"><button type="button" class="basic-button small-button" data-vst-zoom-in title="Zoom in (show less time, more detail)" aria-label="Zoom in">+</button><button type="button" class="basic-button small-button" data-vst-zoom-fit title="Fit the whole sequence to the view" aria-label="Zoom to fit">Fit</button></div><span class="vst-tool-sep" aria-hidden="true"></span><button type="button" class="basic-button small-button vst-toggle-unit" data-vst-unit-toggle title="Toggle ruler units between seconds and frames (in-memory only)">${toggleLabel}</button><button type="button" class="basic-button small-button vst-hist-btn" data-vst-undo title="Undo (Ctrl+Z)" aria-label="Undo">↶</button><button type="button" class="basic-button small-button vst-hist-btn" data-vst-redo title="Redo (Ctrl+Shift+Z or Ctrl+Y)" aria-label="Redo">↷</button></div>` + readout + `</div>`;
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
      body.innerHTML = `${header}<div class="vst-empty"><div class="vst-empty-icon" aria-hidden="true">🎬</div><div class="vst-empty-title">No clips yet.</div><div class="vst-empty-hint">Add one here — or in the VideoStages panel on the left — to start building your sequence.</div><button type="button" class="basic-button btn-primary vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button></div>`;
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
      const renderWidth = clipInnerWidth(l.widthPx);
      return `<div class="vst-region${skipClass}${tinyClass}" style="left:${l.startPx}px;width:${renderWidth}px;--clip-hue:${hue}" data-clip-idx="${l.index}" title="Clip ${l.index} · ${dur} · Click to edit · Shift+click to delete">` + renderRegionThumb(clip) + renderRetakeRegionShade(clip, l.durationSeconds) + renderKeyframes(clip, l.index, l.durationSeconds, fps, unit) + `<div class="vst-region-head"><span class="vst-region-name">Clip ${l.index}</span>` + renderStageChips(clip, l.index) + `<span class="vst-chip" title="Keyframes">◆ ${l.keyframeCount}</span>` + skipChip + `<span class="vst-region-dur">${dur}</span></div>` + renderBadges(clip, l.index) + controls + rightGrip + `</div><div class="vst-retake-lane" data-vst-retake-add data-clip-idx="${l.index}" style="left:${l.startPx}px;width:${renderWidth}px" title="Click empty space to add a retake window">` + renderRetakeOverlay(clip, l.index, l.durationSeconds) + `</div>`;
    }).join("");
    const audioRow = renderAudioTrackRow(clips, layouts);
    const referencesRow = renderReferencesTrackRow(clips, layouts, fps, unit);
    const videoHead = renderTrackHead(
      "vst-track-icon-video",
      "▶",
      "Video",
      headTag("clip", "Clip", { active: true }) + headTag("retake", "Retake", {
        active: clips.some((c) => c.retake != null)
      })
    );
    const promptRow = renderPromptTrackRow(
      clips,
      layouts,
      pxPerSecond,
      `${options?.globalPrompt ?? ""}`
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML = `${header}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px"><div class="vst-ruler-row"><div class="vst-corner">Timeline</div><div class="vst-ruler">${ticks.join("")}</div></div>` + promptRow + `<div class="vst-track-row vst-track-video">${videoHead}<div class="vst-track-cell">${regions}${renderBoundarySeams(clips, layouts)}</div></div>` + referencesRow + audioRow + `</div></div>`;
    wireTopbar();
    wireScroll();
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
  var leftPct = (start, duration) => duration > 0 ? clamp(start, 0, duration) / duration * 100 : 0;
  var widthPct = (length, duration) => duration > 0 ? clamp(length, 0, duration) / duration * 100 : 0;
  var isStaleToken = (sourceToken) => readStateToken() !== sourceToken;
  var commitClipMutation = (sourceToken, origin, mutate) => {
    if (isStaleToken(sourceToken)) {
      return false;
    }
    const next = mutate(getClips());
    if (!next) {
      return false;
    }
    saveClips(next, { origin });
    return true;
  };
  var currentTimelineFps = () => {
    try {
      const fps = getRootDefaults().fps;
      return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
      return 24;
    }
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
  var createWindowTrack = (config) => {
    let boundBody = null;
    let unregister = null;
    const leftStyle = (start, clipDur, pps) => config.unit === "pct" ? `${leftPct(start, clipDur)}%` : `${start * pps}px`;
    const widthStyle = (length, clipDur, pps) => config.unit === "pct" ? `${widthPct(length, clipDur)}%` : `${Math.max(2, length * pps)}px`;
    const commitMove = (state, dxPx, pps) => {
      if (isStaleToken(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      if (!clip || !config.readSpan(clip, state.itemIdx)) {
        return;
      }
      const start = config.moveTargetStart(
        clip,
        state.itemIdx,
        state.press,
        state.press.start + dxPx / pps
      );
      config.writeMove(clip, state.itemIdx, state.press, start);
      saveClips(clips, { origin: config.origin });
      setSelection(config.selectionFor(state.clipIdx, state.itemIdx));
    };
    const commitResize = (state, dxPx, pps) => {
      if (isStaleToken(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      if (!clip || !config.readSpan(clip, state.itemIdx)) {
        return;
      }
      const geom = config.resizeTarget(
        clip,
        state.itemIdx,
        state.edge,
        state.press,
        dxPx / pps
      );
      config.writeResize(clip, state.itemIdx, state.edge, state.press, geom);
      saveClips(clips, { origin: config.origin });
      setSelection(config.selectionFor(state.clipIdx, state.itemIdx));
    };
    const commitCreate = (state, endSec) => {
      if (isStaleToken(state.sourceJson)) {
        return;
      }
      const clips = getClips();
      const clip = clips[state.clipIdx];
      if (!clip || config.canCreate && !config.canCreate(clip)) {
        return;
      }
      const selection2 = config.createSpan(
        clip,
        state.clipIdx,
        state.startSec,
        endSec
      );
      if (!selection2) {
        return;
      }
      saveClips(clips, { origin: config.origin });
      setSelection(selection2);
    };
    const laneTimeAt = (state, clientX, pps) => clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);
    const moveSession = (body, state) => {
      const restore = () => {
        state.el.style.left = state.originalLeft;
      };
      return {
        threshold: DRAG_THRESHOLD_PX,
        onMove: (ctx) => {
          body.classList.add(config.draggingClass);
          const pps = livePxPerSecond(body);
          const start = config.moveTargetStart(
            state.clipAtPress,
            state.itemIdx,
            state.press,
            state.press.start + ctx.dx / pps
          );
          state.el.style.left = leftStyle(start, state.clipDuration, pps);
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
          const geom = config.resizeTarget(
            state.clipAtPress,
            state.itemIdx,
            state.edge,
            state.press,
            ctx.dx / pps
          );
          if (state.edge === "left") {
            state.el.style.left = leftStyle(
              geom.start,
              state.clipDuration,
              pps
            );
          }
          state.el.style.width = widthStyle(
            geom.length,
            state.clipDuration,
            pps
          );
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
            state.lane.appendChild(ghost);
            state.ghost = ghost;
          }
          state.ghost.style.left = leftStyle(a, state.clipDuration, pps);
          state.ghost.style.width = widthStyle(
            b - a,
            state.clipDuration,
            pps
          );
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
        const clipIdx = parseIntAttr(span, "data-clip-idx");
        const itemIdx = itemIdxOf(span);
        if (clipIdx === null || itemIdx === null) {
          return null;
        }
        const clip = getClips()[clipIdx];
        const press = clip ? config.readSpan(clip, itemIdx) : null;
        if (!clip || !press) {
          return null;
        }
        const base = {
          clipIdx,
          itemIdx,
          el: span,
          press,
          clipAtPress: clip,
          clipDuration: clipDurationOf(clip),
          originalLeft: span.style.left,
          sourceJson: readStateToken()
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
      const lane = me.target.closest(config.laneSelector);
      if (lane instanceof HTMLElement) {
        const clipIdx = parseIntAttr(lane, "data-clip-idx");
        if (clipIdx === null) {
          return null;
        }
        const clip = getClips()[clipIdx];
        if (!clip || config.canCreate && !config.canCreate(clip)) {
          return null;
        }
        const rect = lane.getBoundingClientRect();
        const pps = livePxPerSecond(body);
        const clipDuration = clipDurationOf(clip);
        const startSec = clamp(
          (me.clientX - rect.left) / pps,
          0,
          clipDuration
        );
        me.preventDefault();
        return createSession(body, {
          clipIdx,
          lane,
          laneLeft: rect.left,
          startSec,
          clipDuration,
          ghost: null,
          sourceJson: readStateToken()
        });
      }
      return null;
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
      const clipIdx = parseIntAttr(span, "data-clip-idx");
      const itemIdx = itemIdxOf(span);
      if (clipIdx === null || itemIdx === null) {
        return;
      }
      const clips = getClips();
      const clip = clips[clipIdx];
      if (!clip || !config.readSpan(clip, itemIdx)) {
        return;
      }
      if (event.shiftKey) {
        if (config.deleteItem(clip, itemIdx)) {
          saveClips(clips, { origin: config.origin });
        }
        return;
      }
      setSelection(config.selectionFor(clipIdx, itemIdx));
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
      const clipIdx = parseIntAttr(span, "data-clip-idx");
      const itemIdx = itemIdxOf(span);
      if (clipIdx === null || itemIdx === null) {
        return;
      }
      const clip = getClips()[clipIdx];
      if (!clip || !config.readSpan(clip, itemIdx)) {
        return;
      }
      setSelection(config.selectionFor(clipIdx, itemIdx));
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
  var segmentOf = (clip, segIdx) => clip.audioSegments?.[segIdx];
  var createTimelineAudioSegmentTrack = () => createWindowTrack({
    routeId: "audio-segment",
    priority: 40,
    origin: "audio-segment-track",
    spanSelector: ".vst-audio-seg[data-clip-idx]",
    itemIdxAttr: "data-seg-idx",
    edgeSelector: "[data-vst-audio-seg-edge]",
    edgeAttr: "data-vst-audio-seg-edge",
    laneSelector: ".vst-audio-seg-lane[data-vst-audio-seg-add]",
    draggingClass: "vst-audio-seg-dragging",
    ghostClass: "vst-audio-seg-ghost",
    unit: "pct",
    keyboardSelect: true,
    // Segments sit on the audio row; their clicks must not bubble into
    // the audio clip's select handler.
    isolateClicks: true,
    readSpan: (clip, segIdx) => {
      const segment = segmentOf(clip, segIdx);
      return segment ? {
        start: segment.startSeconds,
        length: segment.lengthSeconds,
        trim: segment.trimStartSeconds
      } : null;
    },
    moveTargetStart: (clip, _segIdx, press, desiredStart) => {
      const clipDur = clipDurationOf(clip);
      const length = Math.min(press.length, clipDur);
      return clamp(desiredStart, 0, Math.max(0, clipDur - length));
    },
    writeMove: (clip, segIdx, press, start) => {
      const segment = segmentOf(clip, segIdx);
      if (!segment) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const length = Math.min(press.length, clipDur);
      segment.startSeconds = roundToTenth(start);
      segment.lengthSeconds = roundToTenth(
        Math.min(length, clipDur - segment.startSeconds)
      );
    },
    resizeTarget: (clip, _segIdx, edge, press, deltaSec) => (
      // Left edge: trim follows, lower wall dips below 0 (un-trims further).
      // Right edge: walled by clip end.
      resizeSpanEdge(
        edge,
        press,
        deltaSec,
        AUDIO_SEGMENT_MIN_LENGTH,
        Math.min(
          0,
          press.start + press.length - AUDIO_SEGMENT_MIN_LENGTH
        ),
        clipDurationOf(clip)
      )
    ),
    writeResize: (clip, segIdx, edge, press, geom) => {
      const segment = segmentOf(clip, segIdx);
      if (!segment) {
        return;
      }
      if (edge === "right") {
        segment.startSeconds = roundToTenth(press.start);
        segment.lengthSeconds = roundToTenth(geom.length);
        return;
      }
      segment.startSeconds = roundToTenth(geom.start);
      segment.trimStartSeconds = roundToTenth(
        Math.max(0, press.trim + (geom.start - press.start))
      );
      segment.lengthSeconds = roundToTenth(geom.length);
    },
    createSpan: (clip, clipIdx, startSec, endSec) => {
      const geom = createDefaultOrDraggedSpan(
        startSec,
        endSec,
        0,
        clipDurationOf(clip),
        AUDIO_SEGMENT_MIN_LENGTH,
        AUDIO_SEGMENT_DEFAULT_LENGTH
      );
      if (!geom) {
        return null;
      }
      const segment = {
        source: null,
        startSeconds: roundToTenth(geom.start),
        trimStartSeconds: 0,
        lengthSeconds: roundToTenth(geom.length)
      };
      const segments = [...clip.audioSegments ?? [], segment];
      clip.audioSegments = segments;
      return {
        kind: "audio-segment",
        clipIdx,
        segIdx: segments.length - 1
      };
    },
    deleteItem: (clip, segIdx) => {
      if (!segmentOf(clip, segIdx)) {
        return false;
      }
      clip.audioSegments = clip.audioSegments.filter(
        (_, i) => i !== segIdx
      );
      return true;
    },
    selectionFor: (clipIdx, segIdx) => ({
      kind: "audio-segment",
      clipIdx,
      segIdx
    })
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

  // frontend/detailWidgets.ts
  var sliderSeq = 0;
  var wireNumericInput = (input, fallback, min, max, onChange) => {
    const apply = (normalize) => {
      const parsed = Number.parseFloat(input.value);
      const next = clamp(
        Number.isFinite(parsed) ? parsed : fallback,
        min,
        max
      );
      onChange(next);
      if (normalize) {
        input.value = `${next}`;
      }
    };
    input.addEventListener("input", () => apply(false));
    input.addEventListener("change", () => apply(true));
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
  var buildField = (label, control, hint) => {
    const row = document.createElement("div");
    row.className = "auto-input vst-audio-field";
    const boxClass = boxClassFor(control);
    if (boxClass) {
      row.classList.add(boxClass);
    }
    const labelEl = document.createElement("label");
    const text = document.createElement("span");
    text.className = "auto-input-name vst-audio-field-label";
    text.textContent = label;
    labelEl.appendChild(text);
    row.append(labelEl, control);
    if (hint) {
      const small = document.createElement("small");
      small.className = "vst-audio-field-hint";
      small.textContent = hint;
      row.appendChild(small);
    }
    return row;
  };
  var buildOptionSelect = (specs, selected, onChange) => {
    const select = document.createElement("select");
    select.className = "auto-dropdown vst-audio-select";
    for (const spec of specs) {
      const opt = document.createElement("option");
      opt.value = spec.value;
      opt.textContent = spec.label;
      opt.dataset.cleanname = spec.label;
      opt.disabled = spec.disabled === true;
      opt.selected = spec.value === selected;
      select.appendChild(opt);
    }
    select.addEventListener("change", () => onChange(select.value));
    return select;
  };
  var buildSelect = (values, labels, selected, onChange) => buildOptionSelect(
    values.map((value, i) => ({ value, label: labels[i] ?? value })),
    selected,
    onChange
  );
  var buildNumber = (value, min, max, step, onChange) => {
    const input = document.createElement("input");
    input.type = "number";
    input.className = "auto-number vst-refs-num";
    input.min = `${min}`;
    input.max = `${max}`;
    input.step = `${step}`;
    input.value = `${value}`;
    wireNumericInput(input, value, min, max, onChange);
    return input;
  };
  var buildSlider = (label, value, min, max, step, onChange, opts) => {
    const holder = document.createElement("div");
    holder.className = "vst-stage-slider";
    const id = `vst_stage_slider_${++sliderSeq}`;
    holder.innerHTML = makeSliderInput(
      null,
      id,
      "",
      label,
      "",
      value,
      min,
      max,
      min,
      max,
      step,
      false,
      false,
      false
    );
    const number = holder.querySelector(
      "input.auto-slider-number"
    );
    if (number) {
      wireNumericInput(number, value, min, max, onChange);
    }
    if (opts?.title) {
      holder.title = opts.title;
    }
    if (opts?.hint) {
      const small = document.createElement("small");
      small.className = "vst-audio-field-hint";
      small.textContent = opts.hint;
      holder.appendChild(small);
    }
    return holder;
  };
  var buildCheckbox = (label, checked, onChange, opts) => {
    const row = document.createElement("label");
    row.className = "auto-input auto-checkbox-box auto-input-flex vst-audio-field vst-audio-field-check";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.className = "auto-checkbox";
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));
    const text = document.createElement("span");
    text.className = "auto-input-name vst-audio-field-label";
    text.textContent = label;
    row.append(input, text);
    if (opts?.disabled) {
      row.classList.add("vst-audio-disabled");
      input.setAttribute("disabled", "");
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
    if (typeof textPromptAddKeydownHandler === "function") {
      textPromptAddKeydownHandler(editor);
    }
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
  var buildUploadRow = (label, accept, name, onFile, onClear) => {
    const row = document.createElement("div");
    row.className = "auto-input vst-audio-field vst-audio-upload";
    const uploadLabel = document.createElement("span");
    uploadLabel.className = "auto-input-name vst-audio-field-label";
    uploadLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = accept;
    const fileName = document.createElement("span");
    fileName.className = "vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "basic-button small-button vst-audio-upload-clear";
    clearBtn.textContent = "Clear";
    clearBtn.hidden = !name;
    fileInput.addEventListener("change", () => {
      const file = fileInput.files?.[0];
      if (file) {
        readFileAsDataUri(file, onFile);
      }
    });
    clearBtn.addEventListener("click", () => onClear());
    row.append(uploadLabel, fileInput, fileName, clearBtn);
    return row;
  };
  var videoPickCounter = 0;
  var buildVideoPickRow = (label, name, onFile, onClear) => {
    const row = document.createElement("div");
    row.className = "auto-input vst-audio-field vst-audio-upload";
    const pickLabel = document.createElement("span");
    pickLabel.className = "auto-input-name vst-audio-field-label";
    pickLabel.textContent = label;
    const fileInput = document.createElement("input");
    fileInput.type = "file";
    fileInput.accept = "video/*";
    fileInput.id = `vst-video-pick-${++videoPickCounter}`;
    const fileName = document.createElement("span");
    fileName.className = "vst-audio-upload-name";
    fileName.textContent = name ? name : "No file chosen";
    const preview = document.createElement("div");
    preview.className = "auto-input-preview";
    preview.hidden = true;
    const previewName = document.createElement("span");
    previewName.className = "auto-file-input-filename";
    previewName.hidden = true;
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "basic-button small-button vst-audio-upload-clear";
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
      toDataURL(picked, (data) => onFile(data, pickedName));
    });
    clearBtn.addEventListener("click", () => onClear());
    row.append(pickLabel, fileInput, fileName, clearBtn, preview, previewName);
    if (typeof inputBrowserHelper !== "undefined" && inputBrowserHelper) {
      const selectBtn = document.createElement("button");
      selectBtn.type = "button";
      selectBtn.className = "basic-button small-button vst-video-pick-select";
      selectBtn.textContent = "Select";
      selectBtn.addEventListener(
        "click",
        () => inputBrowserHelper.openInputBrowser(fileInput.id, ["video"])
      );
      clearBtn.before(selectBtn);
    }
    return row;
  };
  var buildInstanceRow = (spec) => {
    const row = document.createElement("div");
    row.className = `vst-detail-instance ${spec.rowClass}`;
    row.setAttribute(spec.indexAttr, `${spec.index}`);
    if (spec.active) {
      row.classList.add("vst-detail-instance-active");
    }
    const head = document.createElement("div");
    head.className = "vst-detail-instance-head";
    const title = document.createElement("span");
    title.className = "vst-detail-instance-title";
    title.textContent = spec.title;
    const del = document.createElement("button");
    del.type = "button";
    del.className = "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-instance-delete";
    del.textContent = spec.deleteLabel;
    del.title = spec.deleteLabel;
    del.addEventListener("click", (event) => {
      event.preventDefault();
      spec.onDelete();
    });
    head.append(title, del);
    row.appendChild(head);
    const fields = document.createElement("div");
    fields.className = "vst-detail-instance-fields";
    row.appendChild(fields);
    row.addEventListener("focusin", () => spec.repoint());
    return { row, fields };
  };
  var sectionLabel = (text) => {
    const sec = document.createElement("div");
    sec.className = "vst-detail-sec vst-detail-wrap-sec";
    sec.textContent = text;
    return sec;
  };
  var clampStartLength = (start, length, clipDur, minLength) => {
    const s = clamp(start, 0, Math.max(0, clipDur - minLength));
    const l = clamp(length, minLength, Math.max(minLength, clipDur - s));
    return { start: s, length: l };
  };
  var buildGroup = (groupId, content) => {
    const group = document.createElement("div");
    group.className = "input-group input-group-open";
    group.id = `auto-group-${groupId}`;
    const contentEl = document.createElement("div");
    contentEl.className = "input-group-content";
    contentEl.id = `input_group_content_${groupId}`;
    contentEl.appendChild(content);
    group.appendChild(contentEl);
    return group;
  };
  var wrapForm = (groupId, content) => {
    const body = document.createElement("div");
    body.className = "vst-detail-body";
    body.appendChild(buildGroup(groupId, content));
    return body;
  };
  var tagFocus = (field, key) => {
    const control = field.querySelector("input.auto-slider-number") ?? field.querySelector("input, select") ?? (field.matches("input, select") ? field : null);
    control?.setAttribute("data-vst-focus-key", key);
    return field;
  };
  var buildStackSection = (label, colClass) => {
    const wrap = document.createElement("div");
    wrap.className = "vst-detail-stages-wrap";
    wrap.appendChild(sectionLabel(label));
    const col = document.createElement("div");
    col.className = `vst-detail-col ${colClass}`;
    wrap.appendChild(col);
    return { wrap, col };
  };
  var buildAddButton = (label, extraClass, onClick) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `basic-button small-button vst-detail-rail-btn ${extraClass}`;
    btn.textContent = label;
    btn.addEventListener("click", (event) => {
      event.preventDefault();
      onClick();
    });
    return btn;
  };

  // frontend/detailStrip/audioPanel.ts
  var GROUP_AUDIO = "vstdock_audio";
  var buildAudioBody = (ctx, sel, clips) => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const controlNetEnabled = hasSlotSourcedIcLora(clip.icLoras);
    const options = buildAudioSourceOptions(clip.audioSource ?? "", {
      controlNetEnabled
    });
    const source = resolveAudioSourceValue(clip.audioSource ?? "", options);
    const canLength = canUseClipLengthFromAudio(source);
    const isAce = isAceStepFunAudioSource(source);
    const commitAudio = (mutate) => {
      ctx.commit((cs) => {
        const target = cs[clipIdx];
        if (!target) {
          return;
        }
        mutate(target);
        const cnEnabled = hasSlotSourcedIcLora(target.icLoras);
        const nextSource = resolveAudioSourceValue(
          target.audioSource,
          buildAudioSourceOptions(target.audioSource, {
            controlNetEnabled: cnEnabled
          })
        );
        target.audioSource = nextSource;
        target.clipLengthFromAudio = canUseClipLengthFromAudio(nextSource) && target.clipLengthFromAudio;
        if (target.clipLengthFromAudio) {
          target.clipLengthFromControlNet = false;
        }
        target.saveAudioTrack = isAceStepFunAudioSource(nextSource) && target.saveAudioTrack;
        target.uploadedAudio = nextSource === AUDIO_SOURCE_UPLOAD || nextSource === AUDIO_SOURCE_VOICE_REF ? target.uploadedAudio : null;
      });
    };
    const body = document.createElement("div");
    body.className = "vst-detail-form-body";
    const select = buildOptionSelect(
      options.map((o) => ({ value: o.value, label: o.label })),
      source,
      (value) => {
        commitAudio((c) => {
          c.audioSource = value;
        });
        ctx.render();
      }
    );
    body.appendChild(buildField("Audio Source", select));
    body.appendChild(
      buildCheckbox("Reuse Audio", clip.reuseAudio === true, (value) => {
        commitAudio((c) => {
          c.reuseAudio = value;
        });
      })
    );
    const lengthRow = buildCheckbox(
      "Clip Length from Audio",
      clip.clipLengthFromAudio === true && canLength,
      (value) => {
        commitAudio((c) => {
          c.clipLengthFromAudio = value;
        });
      },
      { disabled: !canLength }
    );
    body.appendChild(lengthRow);
    const saveRow = buildCheckbox(
      "Save Audio Track",
      clip.saveAudioTrack === true && isAce,
      (value) => {
        commitAudio((c) => {
          c.saveAudioTrack = value;
        });
      },
      { disabled: !isAce }
    );
    body.appendChild(saveRow);
    if (source === AUDIO_SOURCE_UPLOAD || source === AUDIO_SOURCE_VOICE_REF) {
      body.appendChild(
        buildUploadRow(
          source === AUDIO_SOURCE_VOICE_REF ? "Voice Sample" : "Audio Upload",
          "audio/*",
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
    if (source === AUDIO_SOURCE_VOICE_REF) {
      const hint = document.createElement("small");
      hint.className = "vst-audio-field-hint";
      hint.textContent = "Speaker sample only — new speech is generated to match the prompt in this voice. Put the spoken words in the clip prompt.";
      body.appendChild(hint);
    }
    const segCount = clip.audioSegments?.length ?? 0;
    const addSegment = document.createElement("button");
    addSegment.type = "button";
    addSegment.className = "basic-button small-button vst-detail-add-segment";
    addSegment.textContent = "+ Add segment";
    addSegment.title = "Overlay an extra uploaded audio piece on this clip's audio lane";
    addSegment.addEventListener("click", (event) => {
      event.preventDefault();
      ctx.addAudioSegment(clipIdx);
    });
    body.appendChild(addSegment);
    if (segCount > 0) {
      const note = document.createElement("p");
      note.className = "vst-detail-note";
      note.textContent = segCount === 1 ? "1 overlay segment · mixed additively over the base audio." : `${segCount} overlay segments · mixed additively over the base audio.`;
      body.appendChild(note);
    }
    return wrapForm(GROUP_AUDIO, body);
  };

  // frontend/detailStrip/audioSegmentPanel.ts
  var GROUP_AUDIOSEG = "vstdock_audioseg";
  var buildAudioSegmentBody = (ctx, sel, clips) => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const segments = clip?.audioSegments ?? [];
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-instance-body vst-detail-seg-body";
    const clipDur = Math.max(AUDIO_SEGMENT_MIN_LENGTH, clip?.duration || 0);
    const clampSegment = (start, length) => clampStartLength(start, length, clipDur, AUDIO_SEGMENT_MIN_LENGTH);
    segments.forEach((segment, segIdx) => {
      const { row, fields } = buildInstanceRow({
        rowClass: "vst-detail-seg-row",
        indexAttr: "data-vst-seg-index",
        index: segIdx,
        active: segIdx === sel.segIdx,
        title: `S${segIdx + 1}`,
        deleteLabel: "Remove segment",
        onDelete: () => ctx.removeAudioSegment(clipIdx, segIdx),
        repoint: () => setSelection({ kind: "audio-segment", clipIdx, segIdx })
      });
      const segSourceRef = typeof segment.source === "string" ? segment.source : "";
      const segSourceValue = segSourceRef || AUDIO_SOURCE_UPLOAD;
      const segSourceSelect = buildOptionSelect(
        buildSegmentAudioSourceOptions(segSourceRef),
        segSourceValue,
        (value) => {
          ctx.commit((cs) => {
            const seg = cs[clipIdx]?.audioSegments?.[segIdx];
            if (!seg) {
              return;
            }
            if (isAceStepFunAudioSource(value)) {
              seg.source = value;
            } else if (typeof seg.source === "string") {
              seg.source = null;
            }
          });
          ctx.render();
        }
      );
      fields.appendChild(buildField("Source", segSourceSelect));
      if (!segSourceRef) {
        fields.appendChild(
          buildUploadRow(
            "Audio Upload",
            "audio/*",
            typeof segment.source === "string" ? void 0 : segment.source?.fileName,
            (data, fileName) => {
              ctx.commit((cs) => {
                const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                if (seg) {
                  seg.source = { data, fileName };
                }
              });
              ctx.render();
            },
            () => {
              ctx.commit((cs) => {
                const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                if (seg) {
                  seg.source = null;
                }
              });
              ctx.render();
            }
          )
        );
      }
      const startInput = ctx.buildClampedNumber({
        key: `seg-${segIdx}-start`,
        value: segment.startSeconds,
        min: 0,
        max: Math.max(0, clipDur - AUDIO_SEGMENT_MIN_LENGTH),
        step: AUDIO_SEGMENT_STEP,
        readBack: (cs) => cs[clipIdx]?.audioSegments?.[segIdx]?.startSeconds ?? null,
        mutate: (cs, value) => {
          const seg = cs[clipIdx]?.audioSegments?.[segIdx];
          if (seg) {
            const next = clampSegment(value, seg.lengthSeconds);
            seg.startSeconds = next.start;
            seg.lengthSeconds = next.length;
          }
        }
      });
      fields.appendChild(buildField("Start (s)", startInput));
      const trimInput = buildNumber(
        segment.trimStartSeconds,
        0,
        CLIP_DURATION_MAX,
        AUDIO_SEGMENT_STEP,
        (value) => {
          ctx.debouncedCommit(`seg-${segIdx}-trim`, (cs) => {
            const seg = cs[clipIdx]?.audioSegments?.[segIdx];
            if (seg) {
              seg.trimStartSeconds = Math.max(
                0,
                Math.round(value * 10) / 10
              );
            }
          });
        }
      );
      trimInput.setAttribute("data-vst-focus-key", `seg-${segIdx}-trim`);
      fields.appendChild(buildField("Trim start (s)", trimInput));
      const lengthInput = ctx.buildClampedNumber({
        key: `seg-${segIdx}-length`,
        value: segment.lengthSeconds,
        min: AUDIO_SEGMENT_MIN_LENGTH,
        max: clipDur,
        step: AUDIO_SEGMENT_STEP,
        readBack: (cs) => cs[clipIdx]?.audioSegments?.[segIdx]?.lengthSeconds ?? null,
        mutate: (cs, value) => {
          const seg = cs[clipIdx]?.audioSegments?.[segIdx];
          if (seg) {
            const next = clampSegment(seg.startSeconds, value);
            seg.startSeconds = next.start;
            seg.lengthSeconds = next.length;
          }
        }
      });
      fields.appendChild(buildField("Length (s)", lengthInput));
      body.appendChild(row);
    });
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "Overlaid additively over the base audio; overlapping segments mix together.";
    body.appendChild(note);
    return wrapForm(GROUP_AUDIOSEG, body);
  };

  // frontend/detailStrip/boundaryPanel.ts
  var GROUP_BOUNDARY = "vstdock_boundary";
  var buildBoundaryBody = (ctx, sel, clips) => {
    const { leftClipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-boundary";
    const clip = clips[leftClipIdx];
    const value = clip?.boundaryOut ?? "cut";
    const state = getState();
    const fps = state.fps > 0 ? Math.round(state.fps) : 24;
    const joinSpecs = ["cut", "continue", "crossfade"].map((mode) => ({
      value: mode,
      label: `${BOUNDARY_LABEL[mode]} ${BOUNDARY_GLYPH[mode]}`
    }));
    const select = buildOptionSelect(joinSpecs, value, (next) => {
      ctx.commit((cs) => {
        const c = cs[leftClipIdx];
        if (c) {
          c.boundaryOut = next ?? "cut";
        }
      });
      ctx.render();
    });
    body.appendChild(
      buildField(`Join · Clip ${leftClipIdx} → ${leftClipIdx + 1}`, select)
    );
    if (value !== "cut") {
      const overlapValue = clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES;
      const overlapSpecs = CONTINUE_OVERLAP_CHOICES.map(
        (frames) => ({
          value: `${frames}`,
          label: `${frames} frames (~${formatOverlapSeconds(frames, fps)})`
        })
      );
      const overlapSelect = buildOptionSelect(
        overlapSpecs,
        `${overlapValue}`,
        (next) => {
          ctx.commit((cs) => {
            const c = cs[leftClipIdx];
            if (c) {
              c.boundaryOutOverlap = normalizeContinueOverlap(next);
            }
          });
          ctx.render();
        }
      );
      body.appendChild(buildField("Overlap", overlapSelect));
    }
    const info = document.createElement("div");
    info.className = "vst-boundary-info";
    if (value === "cut") {
      info.textContent = "Hard cut — clips are concatenated with no overlap.";
    } else if (value === "continue") {
      const plan = crossfadePlanForClips(clips, fps);
      const window2 = plan.overlaps[leftClipIdx] ?? 0;
      if (plan.fallback || window2 <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = "This continue will fall back to a cut — a clip is too short for the overlap.";
      } else {
        const requested = (clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES) + 1;
        let text = `Continue — the next clip is generated with this clip's last ${window2} frame${window2 === 1 ? "" : "s"} (~${formatOverlapSeconds(window2, fps)}) as frozen context, and the merge collapses the duplicated frames.`;
        if (window2 < requested) {
          text += " The window was reduced because a clip is too short.";
        }
        info.textContent = text;
      }
    } else {
      const plan = crossfadePlanForClips(clips, fps);
      const overlapFrames = plan.overlaps[leftClipIdx] ?? 0;
      if (plan.fallback || overlapFrames <= 0) {
        info.classList.add("vst-boundary-warn");
        info.textContent = "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
      } else {
        const requested = clip?.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES;
        let text = `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} (~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
        if (overlapFrames < requested) {
          text += " The window was reduced because a clip is too short.";
        }
        info.textContent = text;
      }
    }
    body.appendChild(info);
    return wrapForm(GROUP_BOUNDARY, body);
  };

  // frontend/icLoraAutoDownload.ts
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
    const text = statusTextFor(preset, status);
    document.querySelectorAll(`[${IC_LORA_AUTO_HINT_ATTR}="${preset.id}"]`).forEach((el) => {
      el.textContent = text;
    });
  };
  var finish = (preset, onSettled) => {
    setStatus(preset, { state: "done" });
    if (typeof refreshParameterValues === "function") {
      refreshParameterValues(true);
    }
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
    if (typeof makeWSRequest !== "function") {
      statuses.set(preset.id, {
        state: "error",
        message: "Model downloader is unavailable."
      });
      return;
    }
    statuses.set(preset.id, { state: "downloading", percent: 0 });
    makeWSRequest(
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
      0,
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
    const video = document.createElement("video");
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

  // frontend/detailStrip/clipPanel.ts
  var GROUP_STAGES = "vstdock_stages";
  var GROUP_ICLORA = "vstdock_iclora";
  var GROUP_RETAKE = "vstdock_retake";
  var GROUP_SOURCE = "vstdock_source";
  var DURATION_STEP = 0.1;
  var UPSCALE_EPSILON = 1e-6;
  var LORA_WEIGHT_STEP = 0.05;
  var LORA_WEIGHT_DEFAULT = 1;
  var buildClipColumn = (ctx, clip, clipIdx) => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-clip";
    const sourced = !!clip.sourceVideo;
    const lengthDerived2 = clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true || sourced;
    const durationInput = buildNumber(
      clip.duration,
      CLIP_DURATION_MIN,
      CLIP_DURATION_MAX,
      DURATION_STEP,
      (value) => {
        ctx.debouncedCommit("duration", (clips) => {
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
    col.appendChild(durationField);
    col.appendChild(
      buildCheckbox("Skip this clip", clip.skipped === true, (value) => {
        ctx.commit((clips) => {
          const target = clips[clipIdx];
          if (target) {
            target.skipped = value;
          }
        });
      })
    );
    return col;
  };
  var buildStageRail = (ctx, clip, clipIdx, stageIdx) => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-rail";
    const list = document.createElement("div");
    list.className = "vst-detail-rail-list";
    clip.stages.forEach((stage, idx) => {
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "vst-chip vst-stage-tab";
      if (idx === stageIdx) {
        chip.classList.add("vst-stage-tab-active");
      }
      if (stage.skipped) {
        chip.classList.add("vst-stage-tab-skipped");
      }
      chip.textContent = stageChipLabel(idx);
      chip.title = `${stageChipTitle(stage, idx)} · click to edit · Shift+click to delete`;
      chip.addEventListener("click", (event) => {
        if (event.shiftKey) {
          ctx.deleteStage(clipIdx, idx);
        } else {
          ctx.selectStage(clipIdx, idx);
        }
      });
      list.appendChild(chip);
    });
    col.appendChild(list);
    const actions = document.createElement("div");
    actions.className = "vst-detail-rail-actions";
    const addBtn = buildAddButton(
      "Add stage",
      "vst-detail-add-stage",
      () => ctx.addStage(clipIdx)
    );
    addBtn.title = "Add a refine stage";
    const deleteBtn = document.createElement("button");
    deleteBtn.type = "button";
    deleteBtn.className = "basic-button small-button vst-refs-delete vst-detail-rail-btn vst-detail-delete-stage";
    deleteBtn.textContent = "Delete stage";
    deleteBtn.disabled = clip.stages.length <= 1;
    deleteBtn.title = deleteBtn.disabled ? "A clip always keeps at least one stage" : `Delete stage ${stageChipLabel(stageIdx)}`;
    deleteBtn.addEventListener("click", (event) => {
      event.preventDefault();
      ctx.deleteStage(clipIdx, stageIdx);
    });
    actions.append(addBtn, deleteBtn);
    col.appendChild(actions);
    return col;
  };
  var buildParamsColumn = (ctx, clip, clipIdx, stageIdx, stage, defaults) => {
    const col = document.createElement("div");
    col.className = "vst-detail-col vst-detail-params";
    const isRefine = stageIdx >= 1;
    const retakeRidesStage0 = clip.retake != null && !clip.stages.some((s, idx) => idx > 0 && s.skipped !== true);
    const sourcedStage0 = stageIdx === 0 && !!clip.sourceVideo && stage.skipped !== true;
    const sourcedPassthrough = sourcedStage0 && !retakeRidesStage0;
    const stageCommit = (mutate) => {
      ctx.commit((clips) => {
        const target = clips[clipIdx]?.stages[stageIdx];
        if (target) {
          mutate(target);
        }
      });
    };
    const stageDebounced = (key, mutate) => {
      ctx.debouncedCommit(key, (clips) => {
        const target = clips[clipIdx]?.stages[stageIdx];
        if (target) {
          mutate(target);
        }
      });
    };
    const stageSlider = (label, focusKey, value, min, max, step, assign, opts) => tagFocus(
      buildSlider(
        label,
        value,
        min,
        max,
        step,
        (v) => {
          opts?.onValue?.(v);
          stageDebounced(focusKey, (target) => assign(target, v));
        },
        opts?.title ? { title: opts.title } : void 0
      ),
      focusKey
    );
    const stageSelect = (values, labels, selected, assign) => buildSelect(
      values,
      labels,
      selected,
      (v) => stageCommit((target) => assign(target, v))
    );
    const fields = document.createElement("div");
    fields.className = "vst-detail-fields";
    const applyMute = () => {
      fields.classList.toggle(
        "vst-stage-fields-muted",
        stage.skipped === true
      );
    };
    let railSkipSync = () => {
    };
    col.appendChild(
      buildCheckbox("Skip this stage", stage.skipped === true, (value) => {
        stage.skipped = value;
        applyMute();
        railSkipSync(value);
        stageCommit((target) => {
          target.skipped = value;
        });
      })
    );
    col.appendChild(fields);
    applyMute();
    const modelField = buildField(
      "Model",
      stageSelect(
        defaults.modelValues,
        defaults.modelLabels,
        `${stage.model ?? ""}`,
        (target, value) => {
          target.model = value;
        }
      )
    );
    modelField.classList.add("vst-detail-span-2");
    fields.appendChild(modelField);
    fields.appendChild(
      stageSlider(
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
      stageSlider(
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
        stageSlider(
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
            title: "Regen strength — higher = more of the stage is re-generated"
          }
        )
      );
      const methodSelect = stageSelect(
        defaults.upscaleMethodValues,
        defaults.upscaleMethodLabels,
        `${stage.upscaleMethod ?? ""}`,
        (target, value) => {
          target.upscaleMethod = value;
        }
      );
      const methodField = buildField("Upscale Method", methodSelect);
      methodField.classList.add("vst-detail-span-2");
      const syncMethod = (upscale) => {
        const disabled = Math.abs(upscale - 1) < UPSCALE_EPSILON;
        methodSelect.disabled = disabled;
        methodField.classList.toggle("vst-field-disabled", disabled);
        methodField.title = disabled ? "Set Upscale above 1× to choose a method" : "";
      };
      fields.appendChild(
        stageSlider(
          "Upscale",
          "upscale",
          stage.upscale,
          defaults.upscaleMin,
          defaults.upscaleMax,
          defaults.upscaleStep,
          (target, value) => {
            target.upscale = value;
          },
          { onValue: syncMethod }
        )
      );
      fields.appendChild(methodField);
      syncMethod(stage.upscale);
    }
    fields.appendChild(
      buildField(
        "Sampler",
        stageSelect(
          defaults.samplerValues,
          defaults.samplerLabels,
          `${stage.sampler ?? ""}`,
          (target, value) => {
            target.sampler = value;
          }
        )
      )
    );
    fields.appendChild(
      buildField(
        "Scheduler",
        stageSelect(
          defaults.schedulerValues,
          defaults.schedulerLabels,
          `${stage.scheduler ?? ""}`,
          (target, value) => {
            target.scheduler = value;
          }
        )
      )
    );
    if (clip.refs.length > 0) {
      const refsHeader = document.createElement("div");
      refsHeader.className = "vst-detail-sec vst-detail-span-full";
      refsHeader.textContent = "Reference Strengths";
      fields.appendChild(refsHeader);
      const setRefHover = (refIdx, on) => {
        ctx.getBoundBody()?.querySelector(
          `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`
        )?.classList.toggle("vst-ref-hover", on);
      };
      clip.refs.forEach((ref, refIdx) => {
        const current = refIdx < stage.refStrengths.length ? stage.refStrengths[refIdx] : STAGE_REF_STRENGTH_MAX;
        const slider = buildSlider(
          `R${refIdx}`,
          current,
          STAGE_REF_STRENGTH_MIN,
          STAGE_REF_STRENGTH_MAX,
          STAGE_REF_STRENGTH_STEP,
          (value) => {
            stageDebounced(`refstrength-${refIdx}`, (target) => {
              if (refIdx < target.refStrengths.length) {
                target.refStrengths[refIdx] = value;
              }
            });
          },
          {
            title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`
          }
        );
        slider.classList.add("vst-stage-ref-slider");
        tagFocus(slider, `ref-${refIdx}`);
        slider.addEventListener(
          "mouseenter",
          () => setRefHover(refIdx, true)
        );
        slider.addEventListener(
          "mouseleave",
          () => setRefHover(refIdx, false)
        );
        fields.appendChild(slider);
      });
    }
    if (clip.icLoras.length > 0) {
      const controlNetSlider = buildSlider(
        "IC-LoRA Guide Strength",
        stage.controlNetStrength,
        STAGE_CONTROLNET_STRENGTH_MIN,
        STAGE_CONTROLNET_STRENGTH_MAX,
        STAGE_CONTROLNET_STRENGTH_STEP,
        (value) => {
          stageDebounced("controlnet", (target) => {
            target.controlNetStrength = value;
          });
        },
        { hint: "Drive-video conditioning strength for this stage" }
      );
      tagFocus(controlNetSlider, "controlnet");
      fields.appendChild(controlNetSlider);
    }
    fields.appendChild(
      buildLorasSection(ctx, clipIdx, stageIdx, stage, defaults)
    );
    if (sourcedStage0) {
      const note = document.createElement("p");
      note.className = "vst-detail-note vst-stage-passthrough-note";
      note.textContent = sourcedPassthrough ? "Source footage passes through this stage unchanged — add a later stage to refine it." : "This stage passes the source footage through; its settings apply only to the retake window.";
      col.insertBefore(note, fields);
      if (sourcedPassthrough) {
        fields.classList.add("vst-stage-fields-passthrough");
        for (const el of fields.querySelectorAll("input, select, button")) {
          el.disabled = true;
        }
      }
    }
    railSkipSync = (skipped) => {
      const railChip = ctx.getDockEl()?.querySelector(
        `.vst-detail-rail-list .vst-stage-tab:nth-child(${stageIdx + 1})`
      );
      railChip?.classList.toggle("vst-stage-tab-skipped", skipped);
    };
    return { col, railSkipSync };
  };
  var buildLorasSection = (ctx, clipIdx, stageIdx, stage, defaults) => {
    const section = document.createElement("div");
    section.className = "vst-audio-field vst-stage-loras vst-detail-span-full";
    const label = document.createElement("div");
    label.className = "vst-detail-sec";
    label.textContent = `LoRAs — Stage ${stageChipLabel(stageIdx)}`;
    section.appendChild(label);
    if (defaults.loraValues.length === 0) {
      const empty = document.createElement("small");
      empty.className = "vst-audio-field-hint";
      empty.textContent = "(no LoRAs available)";
      section.appendChild(empty);
    } else {
      const list = document.createElement("div");
      list.className = "vst-stage-lora-list";
      stage.loras.forEach((lora, index) => {
        const row = document.createElement("div");
        row.className = "vst-stage-lora-row";
        const select = buildSelect(
          defaults.loraValues,
          defaults.loraLabels,
          lora.name,
          (value) => {
            ctx.commit((clips) => {
              const target = clips[clipIdx]?.stages[stageIdx];
              const entry = target?.loras[index];
              if (entry) {
                entry.name = value;
              }
            });
          }
        );
        const weight = buildNumber(
          lora.weight,
          -10,
          10,
          LORA_WEIGHT_STEP,
          (value) => {
            ctx.debouncedCommit(`lora-${index}-weight`, (clips) => {
              const target = clips[clipIdx]?.stages[stageIdx];
              const entry = target?.loras[index];
              if (entry) {
                entry.weight = value;
              }
            });
          }
        );
        weight.classList.add("vst-stage-lora-weight");
        weight.setAttribute("data-vst-focus-key", `lora-${index}-weight`);
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "basic-button vst-stage-lora-remove";
        remove.textContent = "×";
        remove.title = "Remove this LoRA";
        remove.addEventListener("click", () => {
          ctx.structuralCommit((clips) => {
            const target = clips[clipIdx]?.stages[stageIdx];
            if (!target) {
              return null;
            }
            target.loras.splice(index, 1);
            return "render";
          });
        });
        row.append(select, weight, remove);
        list.appendChild(row);
      });
      section.appendChild(list);
      const addBtn = document.createElement("button");
      addBtn.type = "button";
      addBtn.className = "basic-button small-button vst-stage-lora-add";
      addBtn.textContent = "+ Add LoRA";
      addBtn.addEventListener("click", () => {
        ctx.structuralCommit((clips) => {
          const target = clips[clipIdx]?.stages[stageIdx];
          if (!target) {
            return null;
          }
          target.loras.push({
            name: defaults.loraValues[0] ?? "",
            weight: LORA_WEIGHT_DEFAULT
          });
          return "render";
        });
      });
      section.appendChild(addBtn);
    }
    return section;
  };
  var buildSourceVideoSection = (ctx, clip, clipIdx) => {
    const { wrap, col } = buildStackSection(
      "Source Video",
      "vst-detail-source-col"
    );
    const source = clip.sourceVideo;
    const applyPickedFile = (data, fileName) => {
      void probeSourceVideo(data).then((probe) => {
        const clips = getClips();
        const target = clips.includes(clip) ? clip : clips[clipIdx];
        if (!target) {
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
        applyClipDurationResize(
          target,
          Math.max(CLIP_DURATION_MIN, lengthSeconds),
          getRootDefaults
        );
        saveClips(clips, { origin: "detail-strip" });
        ctx.render();
      });
    };
    const removeSource = () => {
      ctx.structuralCommit((clips) => {
        const target = clips[clipIdx];
        if (!target?.sourceVideo) {
          return null;
        }
        target.sourceVideo = null;
        return "render";
      });
    };
    col.appendChild(
      buildVideoPickRow(
        "Video file",
        source?.fileName ?? null,
        applyPickedFile,
        removeSource
      )
    );
    if (!source) {
      const hint = document.createElement("small");
      hint.className = "vst-audio-field-hint";
      hint.textContent = "Use an existing video file as this clip instead of generating it.";
      col.appendChild(hint);
      return wrap;
    }
    const info = document.createElement("small");
    info.className = "vst-audio-field-hint";
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
        getRootDefaults
      );
    };
    const clampSource = (start, length) => clampStartLength(start, length, fileLimit, CLIP_DURATION_MIN);
    const startInput = ctx.buildClampedNumber({
      key: "source-start",
      value: source.startSeconds,
      min: 0,
      max: Math.max(0, fileLimit - CLIP_DURATION_MIN),
      step: DURATION_STEP,
      readBack: (cs) => cs[clipIdx]?.sourceVideo?.startSeconds ?? null,
      mutate: (cs, value) => {
        const target = cs[clipIdx];
        const s = target?.sourceVideo;
        if (target && s) {
          const next = clampSource(value, s.lengthSeconds);
          s.startSeconds = next.start;
          s.lengthSeconds = next.length;
          syncClipDuration(target);
        }
      }
    });
    col.appendChild(buildField("Start (s)", startInput));
    const lengthInput = ctx.buildClampedNumber({
      key: "source-length",
      value: source.lengthSeconds,
      min: CLIP_DURATION_MIN,
      max: fileLimit,
      step: DURATION_STEP,
      readBack: (cs) => cs[clipIdx]?.sourceVideo?.lengthSeconds ?? null,
      mutate: (cs, value) => {
        const target = cs[clipIdx];
        const s = target?.sourceVideo;
        if (target && s) {
          const next = clampSource(s.startSeconds, value);
          s.startSeconds = next.start;
          s.lengthSeconds = next.length;
          syncClipDuration(target);
        }
      }
    });
    col.appendChild(buildField("Length (s)", lengthInput));
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "This range (conformed to the timeline fps and size) is the clip's starting point: the first stage passes it through, further stages refine or upscale it, and a retake regenerates part of it.";
    col.appendChild(note);
    const del = document.createElement("button");
    del.type = "button";
    del.className = "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-rail-btn";
    del.textContent = "Remove source video";
    del.addEventListener("click", (event) => {
      event.preventDefault();
      removeSource();
    });
    col.appendChild(del);
    return wrap;
  };
  var buildRetakeSection = (ctx, clip, clipIdx) => {
    const { wrap, col } = buildStackSection("Retake", "vst-detail-retake-col");
    const retake = clip.retake;
    if (!retake) {
      const hint = document.createElement("small");
      hint.className = "vst-audio-field-hint";
      hint.textContent = "Regenerates a sub-range when refining a base video.";
      col.append(
        hint,
        buildAddButton(
          "Add retake",
          "vst-detail-add-retake",
          () => ctx.createRetake(clipIdx)
        )
      );
      return wrap;
    }
    const clipDur = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);
    const clampRetake = (start, length) => clampStartLength(start, length, clipDur, RETAKE_MIN_DURATION);
    const startInput = ctx.buildClampedNumber({
      key: "retake-start",
      value: retake.startSeconds,
      min: 0,
      max: Math.max(0, clipDur - RETAKE_MIN_DURATION),
      step: RETAKE_DURATION_STEP,
      readBack: (cs) => cs[clipIdx]?.retake?.startSeconds ?? null,
      mutate: (cs, value) => {
        const r = cs[clipIdx]?.retake;
        if (r) {
          const next = clampRetake(value, r.lengthSeconds);
          r.startSeconds = next.start;
          r.lengthSeconds = next.length;
        }
      }
    });
    col.appendChild(buildField("Start (s)", startInput));
    const lengthInput = ctx.buildClampedNumber({
      key: "retake-length",
      value: retake.lengthSeconds,
      min: RETAKE_MIN_DURATION,
      max: clipDur,
      step: RETAKE_DURATION_STEP,
      readBack: (cs) => cs[clipIdx]?.retake?.lengthSeconds ?? null,
      mutate: (cs, value) => {
        const r = cs[clipIdx]?.retake;
        if (r) {
          const next = clampRetake(r.startSeconds, value);
          r.startSeconds = next.start;
          r.lengthSeconds = next.length;
        }
      }
    });
    col.appendChild(buildField("Length (s)", lengthInput));
    col.appendChild(
      buildSlider(
        "Strength",
        retake.strength,
        RETAKE_STRENGTH_MIN,
        RETAKE_STRENGTH_MAX,
        RETAKE_STRENGTH_STEP,
        (value) => {
          ctx.debouncedCommit("retake-strength", (cs) => {
            const r = cs[clipIdx]?.retake;
            if (r) {
              r.strength = clamp(
                value,
                RETAKE_STRENGTH_MIN,
                RETAKE_STRENGTH_MAX
              );
            }
          });
        }
      )
    );
    const note = document.createElement("p");
    note.className = "vst-detail-note";
    note.textContent = "Applies when refining a base video; audio inside the window regenerates with the frames.";
    col.appendChild(note);
    const del = document.createElement("button");
    del.type = "button";
    del.className = "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-rail-btn";
    del.textContent = "Remove retake";
    del.addEventListener("click", (event) => {
      event.preventDefault();
      ctx.removeRetake(clipIdx);
    });
    col.appendChild(del);
    return wrap;
  };
  var buildIcLorasSection = (ctx, clip, clipIdx, defaults) => {
    const { wrap, col } = buildStackSection(
      "IC-LoRAs",
      "vst-detail-iclora-col"
    );
    const entryField = (clips, entryIdx) => clips[clipIdx]?.icLoras[entryIdx];
    clip.icLoras.forEach((entry, entryIdx) => {
      const { row, fields } = buildInstanceRow({
        rowClass: "vst-detail-iclora",
        indexAttr: "data-vst-iclora-idx",
        index: entryIdx,
        active: false,
        title: `IC-LoRA ${entryIdx + 1}`,
        deleteLabel: "Remove",
        onDelete: () => {
          ctx.structuralCommit((clips) => {
            const target = clips[clipIdx];
            if (!target || entryIdx >= target.icLoras.length) {
              return null;
            }
            target.icLoras.splice(entryIdx, 1);
            return "render";
          });
        },
        repoint: () => {
        }
      });
      const presetSelect = buildOptionSelect(
        [
          { value: IC_LORA_PRESET_CUSTOM_ID, label: "Custom" },
          ...IC_LORA_PRESETS.map((preset2) => ({
            value: preset2.id,
            label: preset2.displayName
          }))
        ],
        entry.preset,
        (value) => {
          ctx.commit((clips) => {
            const target = entryField(clips, entryIdx);
            if (!target) {
              return;
            }
            target.preset = value;
            const preset2 = findIcLoraPreset(value);
            if (preset2) {
              target.strength = preset2.strength;
              target.controlType = preset2.controlType;
            }
          });
          clearIcLoraAutoFailure(value);
          ctx.render();
        }
      );
      fields.appendChild(buildField("Preset", presetSelect));
      const loraSelect = buildSelect(
        [IC_LORA_AUTO, ...defaults.loraValues],
        [IC_LORA_AUTO, ...defaults.loraLabels],
        entry.lora,
        (value) => {
          ctx.commit((clips) => {
            const target = entryField(clips, entryIdx);
            if (target) {
              target.lora = value;
            }
          });
          if (value === IC_LORA_AUTO) {
            clearIcLoraAutoFailure(entry.preset);
          }
          ctx.render();
        }
      );
      fields.appendChild(buildField("LoRA", loraSelect));
      const strength = ctx.buildClampedNumber({
        key: `iclora-${entryIdx}-strength`,
        value: entry.strength,
        min: IC_LORA_STRENGTH_MIN,
        max: IC_LORA_STRENGTH_MAX,
        step: IC_LORA_STRENGTH_STEP,
        readBack: (cs) => entryField(cs, entryIdx)?.strength ?? null,
        mutate: (cs, value) => {
          const target = entryField(cs, entryIdx);
          if (target) {
            target.strength = value;
          }
        }
      });
      fields.appendChild(buildField("Strength", strength));
      const attention = ctx.buildClampedNumber({
        key: `iclora-${entryIdx}-attention`,
        value: entry.attentionStrength,
        min: IC_LORA_ATTENTION_MIN,
        max: IC_LORA_ATTENTION_MAX,
        step: IC_LORA_ATTENTION_STEP,
        readBack: (cs) => entryField(cs, entryIdx)?.attentionStrength ?? null,
        mutate: (cs, value) => {
          const target = entryField(cs, entryIdx);
          if (target) {
            target.attentionStrength = value;
          }
        }
      });
      fields.appendChild(buildField("Attention", attention));
      const preset = findIcLoraPreset(entry.preset);
      if (!preset || preset.id === IC_LORA_PRESET_UNION_CONTROL_ID) {
        const controlSelect = buildOptionSelect(
          [
            { value: "none", label: "None (raw video)" },
            { value: "canny", label: "Canny edges" },
            { value: "depth", label: "Depth map" },
            { value: "normal", label: "Normal map" }
          ],
          entry.controlType,
          (value) => {
            ctx.commit((clips) => {
              const target = entryField(clips, entryIdx);
              if (target) {
                target.controlType = value;
              }
            });
          }
        );
        fields.appendChild(buildField("Control", controlSelect));
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
          ctx.commit((clips) => {
            const target = entryField(clips, entryIdx);
            if (!target) {
              return;
            }
            const stage = Number(value);
            target.stage = Number.isInteger(stage) && stage >= 0 ? stage : IC_LORA_STAGE_ALL;
            reconcileIcLoraStage(target);
          });
          ctx.render();
        }
      );
      fields.appendChild(buildField("Apply on", applySelect));
      if (entry.stage >= 1 && (entry.source === IC_LORA_SOURCE_UPLOAD || entry.source === IC_LORA_SOURCE_STAGE_INPUT)) {
        const sourceSelect = buildOptionSelect(
          [
            { value: IC_LORA_SOURCE_UPLOAD, label: "Upload" },
            {
              value: IC_LORA_SOURCE_STAGE_INPUT,
              label: "Stage input"
            }
          ],
          entry.source,
          (value) => {
            ctx.commit((clips) => {
              const target = entryField(clips, entryIdx);
              if (target) {
                target.source = value;
              }
            });
            ctx.render();
          }
        );
        fields.appendChild(buildField("Source", sourceSelect));
      }
      if (entry.source === IC_LORA_SOURCE_STAGE_INPUT) {
        const hint = document.createElement("small");
        hint.className = "vst-audio-field-hint";
        hint.textContent = `Driven by stage ${entry.stage}'s input (the previous stage's output).`;
        fields.appendChild(hint);
      } else if (entry.source === IC_LORA_SOURCE_UPLOAD) {
        fields.appendChild(
          buildUploadRow(
            "Drive Media",
            "video/*,image/*",
            entry.video?.fileName,
            (data, fileName) => {
              ctx.commit((clips) => {
                const target = entryField(clips, entryIdx);
                if (target) {
                  target.video = { data, fileName };
                }
              });
              ctx.render();
            },
            () => {
              ctx.commit((clips) => {
                const target = entryField(clips, entryIdx);
                if (target) {
                  target.video = null;
                }
              });
              ctx.render();
            }
          )
        );
        if (entry.video?.data?.startsWith("data:video/")) {
          const voiceRow = buildCheckbox(
            "Voice ref from drive audio",
            entry.driveAudioRef === true,
            (value) => {
              ctx.commit((clips) => {
                const target = entryField(clips, entryIdx);
                if (target) {
                  target.driveAudioRef = value;
                }
              });
            }
          );
          voiceRow.title = "Use this video's audio as the speaker sample (LipDub): new speech matching the prompt is generated in that voice.";
          fields.appendChild(voiceRow);
        }
      } else {
        const slot = document.createElement("small");
        slot.className = "vst-audio-field-hint";
        slot.textContent = `Driven by ${entry.source} (legacy source)`;
        fields.appendChild(slot);
      }
      const hintText = [preset?.note ?? "", icLoraTriggerHint(preset)].filter(Boolean).join(" ");
      if (hintText || preset) {
        const hint = document.createElement("small");
        hint.className = "vst-audio-field-hint";
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
      ensureIcLoraAutoWeights(entry, defaults.loraValues, ctx.render);
      const autoText = icLoraAutoHint(entry, defaults.loraValues);
      if (autoText) {
        const autoHint = document.createElement("small");
        autoHint.className = "vst-audio-field-hint";
        if (preset) {
          autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
        }
        autoHint.textContent = autoText;
        fields.appendChild(autoHint);
      }
      col.appendChild(row);
    });
    col.appendChild(
      buildAddButton("+ Add IC-LoRA", "vst-detail-add-iclora", () => {
        ctx.structuralCommit((clips) => {
          const target = clips[clipIdx];
          if (!target) {
            return null;
          }
          target.icLoras.push(
            defaultIcLora({
              lora: defaults.loraValues[0] ?? IC_LORA_AUTO
            })
          );
          return "render";
        });
      })
    );
    return wrap;
  };
  var buildClipBody = (ctx, sel, clips) => {
    const body = document.createElement("div");
    body.className = "vst-detail-body vst-detail-clip-body";
    const clip = clips[sel.clipIdx];
    const stage = clip.stages[sel.stageIdx];
    const defaults = getRootDefaults();
    body.appendChild(buildClipColumn(ctx, clip, sel.clipIdx));
    const params = buildParamsColumn(
      ctx,
      clip,
      sel.clipIdx,
      sel.stageIdx,
      stage,
      defaults
    );
    const stagesWrap = document.createElement("div");
    stagesWrap.className = "vst-detail-stages-wrap";
    stagesWrap.append(
      sectionLabel("Stages"),
      buildStageRail(ctx, clip, sel.clipIdx, sel.stageIdx),
      params.col
    );
    body.appendChild(buildGroup(GROUP_STAGES, stagesWrap));
    body.appendChild(
      buildGroup(
        GROUP_ICLORA,
        buildIcLorasSection(ctx, clip, sel.clipIdx, defaults)
      )
    );
    body.appendChild(
      buildGroup(
        GROUP_SOURCE,
        buildSourceVideoSection(ctx, clip, sel.clipIdx)
      )
    );
    body.appendChild(
      buildGroup(GROUP_RETAKE, buildRetakeSection(ctx, clip, sel.clipIdx))
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
  var GROUP_PROMPTMAJOR = "vstdock_promptmajor";
  var GROUP_PROMPTMINOR = "vstdock_promptminor";
  var buildPromptMajorBody = (ctx, sel, clips) => {
    const { clipIdx } = sel;
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-prompt-body";
    body.appendChild(
      buildTextarea(
        clips[clipIdx].prompt ?? "",
        "Clip prompt (blank inherits the global prompt)…",
        "prompt-major",
        (value) => {
          ctx.debouncedCommit("prompt-major", (cs) => {
            const c = cs[clipIdx];
            if (c) {
              c.prompt = value.trim();
            }
          });
        }
      )
    );
    return wrapForm(GROUP_PROMPTMAJOR, body);
  };
  var buildPromptMinorBody = (ctx, sel, clips) => {
    const { clipIdx, windowIdx } = sel;
    const clip = clips[clipIdx];
    const windows = clip?.promptWindows ?? [];
    const clipDur = Math.max(PROMPT_WINDOW_MIN_DURATION, clip?.duration || 0);
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-prompt-body vst-detail-minor-body";
    windows.forEach((w, idx) => {
      const row = document.createElement("div");
      row.className = "vst-detail-minor-window";
      row.setAttribute("data-vst-minor-window", `${idx}`);
      if (idx === windowIdx) {
        row.classList.add("vst-detail-minor-active");
      }
      const head = document.createElement("div");
      head.className = "vst-detail-minor-head";
      const title = document.createElement("span");
      title.className = "vst-detail-minor-title";
      title.textContent = `W${idx + 1}`;
      const del = document.createElement("button");
      del.type = "button";
      del.className = "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-minor-delete";
      del.textContent = "Delete";
      del.title = "Delete this prompt window";
      del.addEventListener("click", (event) => {
        event.preventDefault();
        ctx.deleteWindowEntry(clipIdx, idx);
      });
      head.append(title, del);
      row.appendChild(head);
      const range = document.createElement("div");
      range.className = "vst-detail-minor-range";
      const bounds = clip ? promptWindowNeighborBounds(clip, idx) : null;
      const beginInput = ctx.buildClampedNumber({
        key: `minor-${idx}-begin`,
        value: roundToTenth(w.start),
        min: bounds?.beginMin ?? 0,
        max: gridFloor(Math.max(0, clipDur - PROMPT_WINDOW_MIN_DURATION)),
        step: 0.1,
        readBack: (cs) => {
          const win = cs[clipIdx]?.promptWindows?.[idx];
          return win ? roundToTenth(win.start) : null;
        },
        mutate: (cs, value) => {
          const c = cs[clipIdx];
          if (c) {
            applyPromptWindowBegin(c, idx, value);
          }
        }
      });
      range.appendChild(buildField("Begin (s)", beginInput));
      const endInput = ctx.buildClampedNumber({
        key: `minor-${idx}-end`,
        value: roundToTenth(w.start + w.duration),
        min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
        max: bounds?.endMax ?? clipDur,
        step: 0.1,
        readBack: (cs) => {
          const win = cs[clipIdx]?.promptWindows?.[idx];
          return win ? roundToTenth(win.start + win.duration) : null;
        },
        mutate: (cs, value) => {
          const c = cs[clipIdx];
          if (c) {
            applyPromptWindowEnd(c, idx, value);
          }
        }
      });
      range.appendChild(buildField("End (s)", endInput));
      row.appendChild(range);
      const editor = buildTextarea(
        w.prompt ?? "",
        "Minor prompt for this window…",
        `minor-${idx}`,
        (value) => {
          ctx.debouncedCommit(`minor-${idx}`, (cs) => {
            const win = cs[clipIdx]?.promptWindows?.[idx];
            if (win) {
              win.prompt = value.trim();
            }
          });
        }
      );
      editor.addEventListener("focus", () => {
        setSelection({ kind: "prompt-minor", clipIdx, windowIdx: idx });
      });
      row.appendChild(editor);
      body.appendChild(row);
    });
    return wrapForm(GROUP_PROMPTMINOR, body);
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
  var GROUP_REF = "vstdock_ref";
  var buildRefBody = (ctx, sel, clips) => {
    const { clipIdx } = sel;
    const clip = clips[clipIdx];
    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-instance-body vst-detail-ref-body";
    const frameMax = getReferenceFrameMax(getRootDefaults, clip);
    clip.refs.forEach((ref, refIdx) => {
      const options = buildImageSourceOptions(ref.source ?? "");
      const source = resolveImageSourceValue(ref.source ?? "", options);
      const isUpload = source === REF_SOURCE_UPLOAD;
      const { row, fields } = buildInstanceRow({
        rowClass: "vst-detail-ref-row",
        indexAttr: "data-vst-ref-index",
        index: refIdx,
        active: refIdx === sel.refIdx,
        title: `R${refIdx + 1}`,
        deleteLabel: "Delete",
        onDelete: () => ctx.deleteRefEntry(clipIdx, refIdx),
        repoint: () => setSelection({ kind: "ref", clipIdx, refIdx })
      });
      const select = buildOptionSelect(options, source, (value) => {
        ctx.commit((cs) => {
          const r = cs[clipIdx]?.refs[refIdx];
          if (!r) {
            return;
          }
          const resolved = resolveImageSourceValue(
            value,
            buildImageSourceOptions(value)
          );
          r.source = resolved;
          if (resolved !== REF_SOURCE_UPLOAD) {
            r.uploadedImage = null;
            r.uploadFileName = null;
          }
        });
        ctx.render();
      });
      fields.appendChild(buildField("Image Source", select));
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
      const frameInput = buildNumber(
        ref.frame,
        REF_FRAME_MIN,
        frameMax,
        1,
        (value) => {
          ctx.debouncedCommit(`ref-${refIdx}-frame`, (cs) => {
            const r = cs[clipIdx]?.refs[refIdx];
            if (r) {
              r.frame = clamp(
                Math.round(value),
                REF_FRAME_MIN,
                frameMax
              );
            }
          });
        }
      );
      frameInput.setAttribute("data-vst-focus-key", `ref-${refIdx}-frame`);
      fields.appendChild(
        buildField(`Attach at Frame (1–${frameMax})`, frameInput)
      );
      fields.appendChild(
        buildCheckbox(
          "Count from clip end",
          ref.fromEnd === true,
          (value) => {
            ctx.commit((cs) => {
              const r = cs[clipIdx]?.refs[refIdx];
              if (r) {
                r.fromEnd = value;
              }
            });
          }
        )
      );
      if (isUpload) {
        fields.appendChild(
          buildUploadRow(
            "Image Upload",
            "image/*",
            ref.uploadedImage?.fileName,
            (data, fileName) => {
              ctx.commit((cs) => {
                const r = cs[clipIdx]?.refs[refIdx];
                if (r) {
                  r.uploadedImage = { data, fileName };
                  r.uploadFileName = fileName;
                }
              });
              ctx.render();
            },
            () => {
              ctx.commit((cs) => {
                const r = cs[clipIdx]?.refs[refIdx];
                if (r) {
                  r.uploadedImage = null;
                  r.uploadFileName = null;
                }
              });
              ctx.render();
            }
          )
        );
      }
      body.appendChild(row);
    });
    return wrapForm(GROUP_REF, body);
  };

  // frontend/detailStrip/settingsPanel.ts
  var GROUP_SETTINGS = "vstdock_settings";
  var SETTINGS_INHERIT = "inherit";
  var SETTINGS_CUSTOM = "custom";
  var clampDimension = (value) => clamp(
    Math.round(value) || ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_MAX
  );
  var clampFps = (value) => clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);
  var buildSettingsBody = (ctx) => {
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
    widthInput.classList.add("vst-settings-num");
    widthInput.disabled = !isCustom;
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
    heightInput.classList.add("vst-settings-num");
    heightInput.disabled = !isCustom;
    heightInput.setAttribute("data-vst-focus-key", "settings-height");
    const dimsPair = document.createElement("div");
    dimsPair.className = "vst-settings-dims";
    const dimsSep = document.createElement("span");
    dimsSep.className = "vst-settings-dims-sep";
    dimsSep.textContent = "×";
    dimsPair.append(widthInput, dimsSep, heightInput);
    const dimsField = buildField("Dimensions", dimsPair);
    if (!isCustom) {
      dimsField.classList.add("vst-audio-disabled");
    }
    body.appendChild(dimsField);
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
    const fpsRow = buildCheckbox(
      "Custom FPS",
      state.fpsExplicit === true,
      (value) => {
        ctx.commitState((next) => {
          next.fpsExplicit = value;
          if (value) {
            next.fps = clampFps(next.fps);
          }
        });
        ctx.render();
      }
    );
    body.appendChild(fpsRow);
    const fpsInput = buildNumber(
      state.fpsExplicit ? clampFps(state.fps) : core.fps,
      ROOT_FPS_MIN,
      ROOT_FPS_MAX,
      1,
      (value) => {
        ctx.debouncedCommitState("settings-fps", (next) => {
          next.fpsExplicit = true;
          next.fps = clampFps(value);
        });
      }
    );
    fpsInput.classList.add("vst-settings-num");
    fpsInput.disabled = state.fpsExplicit !== true;
    fpsInput.setAttribute("data-vst-focus-key", "settings-fps");
    const fpsField = buildField("FPS", fpsInput);
    if (state.fpsExplicit !== true) {
      fpsField.classList.add("vst-audio-disabled");
    }
    body.appendChild(fpsField);
    return wrapForm(GROUP_SETTINGS, body);
  };

  // frontend/timelineDetailStrip.ts
  var STAGE_SELECTOR = "[data-vst-stage]";
  var MODEL_SELECTOR = "[data-vst-model]";
  var INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;
  var DETAIL_CLASS = "vst-detail";
  var DEBOUNCE_MS = 200;
  var clampSelection = (sel, clips) => {
    if (sel.kind === "none") {
      return sel;
    }
    if (sel.kind === "boundary") {
      return sel.leftClipIdx >= 0 && sel.leftClipIdx <= clips.length - 2 ? sel : { kind: "none" };
    }
    if (sel.clipIdx < 0 || sel.clipIdx >= clips.length) {
      return { kind: "none" };
    }
    if (sel.kind === "clip") {
      const stageCount = clips[sel.clipIdx].stages.length;
      if (stageCount === 0) {
        return { kind: "none" };
      }
      const stageIdx = clamp(sel.stageIdx, 0, stageCount - 1);
      return stageIdx === sel.stageIdx ? sel : { kind: "clip", clipIdx: sel.clipIdx, stageIdx };
    }
    if (sel.kind === "ref") {
      return sel.refIdx >= 0 && sel.refIdx < clips[sel.clipIdx].refs.length ? sel : { kind: "none" };
    }
    if (sel.kind === "prompt-minor") {
      const windows = clips[sel.clipIdx].promptWindows ?? [];
      return sel.windowIdx >= 0 && sel.windowIdx < windows.length ? sel : { kind: "none" };
    }
    if (sel.kind === "retake") {
      return clips[sel.clipIdx].retake ? sel : { kind: "none" };
    }
    if (sel.kind === "audio-segment") {
      const segments = clips[sel.clipIdx].audioSegments ?? [];
      return sel.segIdx >= 0 && sel.segIdx < segments.length ? sel : { kind: "none" };
    }
    return sel;
  };
  var createTimelineDetailStrip = (options) => {
    let boundBody = null;
    let dockEl = null;
    let unsubscribe = null;
    let sourceToken = "";
    let pendingTimer = null;
    let flushing = false;
    let rendering = false;
    let suppressSelectionRender = false;
    let sliderDragActive = false;
    let settingsMode = null;
    let pendingFocus = null;
    let focusLeftDock = false;
    let renderedSel = null;
    const isTypingInDock = () => {
      if (!dockEl) {
        return false;
      }
      const active = document.activeElement;
      if (!(active instanceof HTMLElement) || !dockEl.contains(active)) {
        return false;
      }
      if (active instanceof HTMLTextAreaElement) {
        return true;
      }
      if (active instanceof HTMLInputElement) {
        return active.type === "text" || active.type === "number";
      }
      return false;
    };
    const isSliderGesture = () => {
      if (sliderDragActive) {
        return true;
      }
      if (!dockEl) {
        return false;
      }
      const active = document.activeElement;
      if (!(active instanceof HTMLInputElement) || !dockEl.contains(active)) {
        return false;
      }
      return active.type === "range" || active.classList.contains("auto-slider-number");
    };
    const captureFocus = () => {
      if (focusLeftDock) {
        pendingFocus = null;
        return;
      }
      const active = document.activeElement;
      if (!(active instanceof HTMLElement) || !dockEl?.contains(active)) {
        pendingFocus = null;
        return;
      }
      const holder = active.closest("[data-vst-focus-key]");
      if (!holder || !dockEl.contains(holder)) {
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
    const restoreFocus = (detail) => {
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
    const isStale = () => readStateToken() !== sourceToken;
    const pending = /* @__PURE__ */ new Map();
    const flushPending = () => {
      if (pendingTimer) {
        clearTimeout(pendingTimer);
        pendingTimer = null;
      }
      if (flushing || pending.size === 0) {
        return;
      }
      const entryList = [...pending.entries()];
      const entries = entryList.map(([, e]) => e);
      pending.clear();
      captureFocus();
      if (isStale()) {
        return;
      }
      const clipMutates = entries.filter((e) => e.kind === "clips").map((e) => e.mutate);
      const stateMutates = entries.filter((e) => e.kind === "state").map((e) => e.mutate);
      flushing = true;
      let flushedClips = null;
      try {
        if (clipMutates.length > 0) {
          const clips = getClips();
          for (const m of clipMutates) {
            m(clips);
          }
          saveClips(clips, {
            origin: "detail-strip",
            valueOnly: true
          });
          flushedClips = clips;
        }
        if (stateMutates.length > 0) {
          const state = getState();
          for (const m of stateMutates) {
            m(state);
          }
          saveState(state, {
            notifyDomChange: isVideoStagesEnabled(),
            origin: "detail-strip",
            valueOnly: true
          });
        }
        sourceToken = readStateToken();
      } finally {
        flushing = false;
      }
      writeBackClamped(entryList, flushedClips);
      syncValueDerivedUI(renderedSel);
    };
    const writeBackClamped = (entryList, clips) => {
      if (!dockEl || !clips) {
        return;
      }
      for (const [key, entry] of entryList) {
        if (!entry.readBack) {
          continue;
        }
        const input = dockEl.querySelector(
          `input[data-vst-focus-key="${key}"]`
        );
        if (!input) {
          continue;
        }
        const display = entry.readBack(clips);
        if (display == null) {
          continue;
        }
        const next = `${display}`;
        if (input.value !== next) {
          input.value = next;
        }
      }
    };
    const schedulePending = (key, entry) => {
      if (rendering) {
        return;
      }
      pending.set(key, entry);
      if (pendingTimer) {
        clearTimeout(pendingTimer);
        pendingTimer = null;
      }
      if (isTypingInDock() || isSliderGesture()) {
        return;
      }
      pendingTimer = setTimeout(() => {
        pendingTimer = null;
        flushPending();
      }, DEBOUNCE_MS);
    };
    const debouncedCommit = (key, mutate) => {
      schedulePending(key, { kind: "clips", mutate });
    };
    const debouncedCommitState = (key, mutate) => {
      schedulePending(key, { kind: "state", mutate });
    };
    const buildClampedNumber = (opts) => {
      const input = buildNumber(
        opts.value,
        opts.min,
        opts.max,
        opts.step,
        (value) => {
          schedulePending(opts.key, {
            kind: "clips",
            mutate: (clips) => opts.mutate(clips, value),
            readBack: opts.readBack
          });
        }
      );
      input.setAttribute("data-vst-focus-key", opts.key);
      return input;
    };
    const commit = (mutate) => {
      flushPending();
      captureFocus();
      if (isStale()) {
        render();
        return;
      }
      const clips = getClips();
      mutate(clips);
      saveClips(clips, {
        origin: "detail-strip",
        valueOnly: true
      });
      sourceToken = readStateToken();
      syncValueDerivedUI(renderedSel);
    };
    const commitState = (mutate) => {
      flushPending();
      captureFocus();
      if (isStale()) {
        render();
        return;
      }
      const state = getState();
      mutate(state);
      saveState(state, {
        notifyDomChange: isVideoStagesEnabled(),
        origin: "detail-strip",
        valueOnly: true
      });
      sourceToken = readStateToken();
      syncValueDerivedUI(renderedSel);
    };
    const structuralCommit = (apply, opts) => {
      flushPending();
      if (isStale()) {
        render();
        return;
      }
      const clips = getClips();
      const outcome = apply(clips);
      if (outcome === null) {
        return;
      }
      saveClips(clips, { origin: "detail-strip" });
      sourceToken = readStateToken();
      if (outcome === "render") {
        render();
        return;
      }
      if (opts?.rebuildAfterSelect) {
        suppressSelectionRender = true;
        setSelection(outcome);
        suppressSelectionRender = false;
        render();
        return;
      }
      setSelection(outcome);
    };
    const commitRemoval = (remove, index, neighbour, fallback) => structuralCommit((clips) => {
      const remaining = remove(clips);
      if (remaining === null) {
        return null;
      }
      return remaining > 0 ? neighbour(Math.min(index, remaining - 1)) : fallback;
    });
    const deleteRefEntry = (clipIdx, refIdx) => {
      commitRemoval(
        (clips) => {
          const clip = clips[clipIdx];
          return clip && removeRefAt(clip, refIdx) ? clip.refs.length : null;
        },
        refIdx,
        (idx) => ({ kind: "ref", clipIdx, refIdx: idx }),
        { kind: "clip", clipIdx, stageIdx: 0 }
      );
    };
    const deleteWindowEntry = (clipIdx, windowIdx) => {
      commitRemoval(
        (clips) => {
          const windows = clips[clipIdx]?.promptWindows;
          if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
            return null;
          }
          windows.splice(windowIdx, 1);
          return windows.length;
        },
        windowIdx,
        (idx) => ({ kind: "prompt-minor", clipIdx, windowIdx: idx }),
        { kind: "prompt-major", clipIdx }
      );
    };
    const createRetake = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        if (!clip || clip.retake) {
          return null;
        }
        const clipDur = Math.max(0, clip.duration || 0);
        const lengthSeconds = Math.max(
          RETAKE_MIN_DURATION,
          Math.min(
            RETAKE_DEFAULT_DURATION,
            clipDur || RETAKE_DEFAULT_DURATION
          )
        );
        clip.retake = {
          startSeconds: 0,
          lengthSeconds,
          strength: RETAKE_STRENGTH_DEFAULT
        };
        return { kind: "retake", clipIdx };
      });
    };
    const removeRetake = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        if (!clip?.retake) {
          return null;
        }
        clip.retake = null;
        return { kind: "clip", clipIdx, stageIdx: 0 };
      });
    };
    const addAudioSegment = (clipIdx) => {
      structuralCommit((clips) => {
        const clip = clips[clipIdx];
        if (!clip) {
          return null;
        }
        const clipDur = Math.max(0, clip.duration || 0);
        if (clipDur < AUDIO_SEGMENT_MIN_LENGTH) {
          return null;
        }
        const segment = {
          source: null,
          startSeconds: 0,
          trimStartSeconds: 0,
          lengthSeconds: roundToTenth(
            Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, clipDur)
          )
        };
        const segments = [...clip.audioSegments ?? [], segment];
        clip.audioSegments = segments;
        return {
          kind: "audio-segment",
          clipIdx,
          segIdx: segments.length - 1
        };
      });
    };
    const removeAudioSegment = (clipIdx, segIdx) => {
      commitRemoval(
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip?.audioSegments?.[segIdx]) {
            return null;
          }
          clip.audioSegments = clip.audioSegments.filter(
            (_, i) => i !== segIdx
          );
          return clip.audioSegments.length;
        },
        segIdx,
        (idx) => ({ kind: "audio-segment", clipIdx, segIdx: idx }),
        { kind: "audio", clipIdx }
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
          const last = clip.stages[clip.stages.length - 1] ?? null;
          clip.stages.push(
            buildDefaultStage(
              getRootDefaults,
              getDefaultStageModel,
              last,
              clip.refs.length
            )
          );
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
          if (!clip || clip.stages.length <= 1) {
            return null;
          }
          if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return null;
          }
          clip.stages.splice(stageIdx, 1);
          for (const entry of clip.icLoras) {
            if (entry.stage === stageIdx) {
              entry.stage = IC_LORA_STAGE_ALL;
            } else if (entry.stage > stageIdx) {
              entry.stage -= 1;
            }
            reconcileIcLoraStage(entry);
          }
          return {
            kind: "clip",
            clipIdx,
            stageIdx: clamp(stageIdx, 0, clip.stages.length - 1)
          };
        },
        { rebuildAfterSelect: true }
      );
    };
    const handleActivation = (target, shiftKey) => {
      const stageChip = target.closest(STAGE_SELECTOR);
      if (stageChip instanceof HTMLElement) {
        const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
        const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
        if (clipIdx === null || stageIdx === null) {
          return;
        }
        if (shiftKey) {
          deleteStage(clipIdx, stageIdx);
        } else {
          selectStage(clipIdx, stageIdx);
        }
        return;
      }
      const modelBadge = target.closest(MODEL_SELECTOR);
      if (modelBadge instanceof HTMLElement) {
        const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
        if (clipIdx !== null) {
          selectStage(clipIdx, 0);
        }
      }
    };
    const onMouseDownCapture = (event) => {
      if (event.target instanceof Element && event.target.closest(INTERACTIVE_SELECTOR)) {
        event.stopPropagation();
      }
    };
    const onClickCapture = (event) => {
      if (!(event.target instanceof Element) || !event.target.closest(INTERACTIVE_SELECTOR)) {
        return;
      }
      event.stopPropagation();
      handleActivation(event.target, event.shiftKey);
    };
    const onKeyDownCapture = (event) => {
      if (event.key !== "Enter" && event.key !== " ") {
        return;
      }
      if (!(event.target instanceof Element) || !event.target.closest(INTERACTIVE_SELECTOR)) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      handleActivation(event.target, event.shiftKey);
    };
    const onStripKeyDown = (event) => {
      if (event.key !== "Escape") {
        return;
      }
      if (event.target instanceof Element && event.target.closest(".sui-popover")) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      setSelection({ kind: "none" });
    };
    const onDockFocusOut = (event) => {
      if (rendering) {
        return;
      }
      const next = event.relatedTarget;
      if (next instanceof Node && dockEl?.contains(next)) {
        return;
      }
      focusLeftDock = true;
      pendingFocus = null;
      flushPending();
    };
    const onDockFocusIn = () => {
      focusLeftDock = false;
    };
    const onDockChange = (event) => {
      const target = event.target;
      if (target instanceof HTMLInputElement && target.type === "number" && document.activeElement === target) {
        flushPending();
      }
    };
    const onDocPointerDown = (event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }
      if (!dockEl?.contains(target)) {
        flushPending();
        return;
      }
      if (target.closest('input[type="range"]')) {
        sliderDragActive = true;
      }
    };
    const onDocPointerUp = () => {
      if (!sliderDragActive) {
        return;
      }
      sliderDragActive = false;
      flushPending();
    };
    const ensureDetail = () => {
      if (!dockEl) {
        throw new Error("detail strip not attached");
      }
      return dockEl;
    };
    const breadcrumbFor = (sel) => {
      switch (sel.kind) {
        case "clip":
          return `Clip ${sel.clipIdx} · ${stageChipLabel(sel.stageIdx)}`;
        case "ref":
          return `Ref ${sel.refIdx} · Clip ${sel.clipIdx}`;
        case "audio":
          return `Audio · Clip ${sel.clipIdx}`;
        case "audio-segment": {
          const seg = getClips()[sel.clipIdx]?.audioSegments?.[sel.segIdx];
          if (!seg) {
            return `Audio segment · Clip ${sel.clipIdx}`;
          }
          const start = roundToTenth(seg.startSeconds);
          const end = roundToTenth(seg.startSeconds + seg.lengthSeconds);
          return `Audio segment · Clip ${sel.clipIdx} · ${start}–${end} s`;
        }
        case "boundary":
          return `Boundary · Clip ${sel.leftClipIdx} → ${sel.leftClipIdx + 1}`;
        case "prompt-major":
          return `Prompt · Clip ${sel.clipIdx}`;
        case "prompt-minor": {
          const w = getClips()[sel.clipIdx]?.promptWindows?.[sel.windowIdx];
          if (!w) {
            return `Relay · Clip ${sel.clipIdx}`;
          }
          const start = roundToTenth(w.start);
          const end = roundToTenth(w.start + w.duration);
          return `Relay ${start}–${end}s · Clip ${sel.clipIdx}`;
        }
        case "retake": {
          const r = getClips()[sel.clipIdx]?.retake;
          if (!r) {
            return `Retake · Clip ${sel.clipIdx}`;
          }
          const start = roundToTenth(r.startSeconds);
          const end = roundToTenth(r.startSeconds + r.lengthSeconds);
          return `Retake · Clip ${sel.clipIdx} · ${start}–${end} s`;
        }
        default:
          return "Timeline settings";
      }
    };
    const buildHeader = (sel, collapsed) => {
      const head = document.createElement("div");
      head.className = "vst-detail-head";
      const crumb = document.createElement("span");
      crumb.className = "vst-detail-crumb";
      crumb.textContent = breadcrumbFor(sel);
      const clear = document.createElement("button");
      clear.type = "button";
      clear.className = "basic-button small-button vst-detail-clear";
      clear.textContent = "Clear";
      clear.title = "Clear selection (show timeline settings)";
      clear.setAttribute("aria-label", clear.title);
      clear.hidden = sel.kind === "none";
      clear.addEventListener("click", () => {
        setSelection({ kind: "none" });
      });
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "basic-button small-button vst-detail-collapse";
      toggle.textContent = collapsed ? "▸" : "▾";
      toggle.title = collapsed ? "Expand detail strip" : "Collapse detail strip";
      toggle.setAttribute("aria-label", toggle.title);
      toggle.addEventListener("click", () => {
        options.setCollapsed(!options.isCollapsed());
        render();
      });
      head.append(crumb, clear, toggle);
      return head;
    };
    const ctx = {
      commit,
      commitState,
      debouncedCommit,
      debouncedCommitState,
      buildClampedNumber,
      structuralCommit,
      render: () => render(),
      deleteRefEntry,
      deleteWindowEntry,
      createRetake,
      removeRetake,
      addAudioSegment,
      removeAudioSegment,
      addStage,
      deleteStage,
      selectStage,
      getBoundBody: () => boundBody,
      getDockEl: () => dockEl,
      getSettingsMode: () => settingsMode,
      setSettingsMode: (mode) => {
        settingsMode = mode;
      }
    };
    const buildBody = (sel, clips) => {
      switch (sel.kind) {
        case "clip":
          return buildClipBody(ctx, sel, clips);
        case "ref":
          return buildRefBody(ctx, sel, clips);
        case "audio":
          return buildAudioBody(ctx, sel, clips);
        case "audio-segment":
          return buildAudioSegmentBody(ctx, sel, clips);
        case "prompt-major":
          return buildPromptMajorBody(ctx, sel, clips);
        case "prompt-minor":
          return buildPromptMinorBody(ctx, sel, clips);
        case "retake":
          return buildClipBody(
            ctx,
            { kind: "clip", clipIdx: sel.clipIdx, stageIdx: 0 },
            clips
          );
        case "boundary":
          return buildBoundaryBody(ctx, sel, clips);
        default:
          return buildSettingsBody(ctx);
      }
    };
    const syncValueDerivedUI = (sel) => {
      if (!dockEl || !sel) {
        return;
      }
      const crumb = dockEl.querySelector(".vst-detail-crumb");
      if (crumb) {
        crumb.textContent = breadcrumbFor(sel);
      }
    };
    const render = (meta) => {
      if (!dockEl) {
        return;
      }
      if (meta?.origin === "detail-strip" && meta.hint === "value-only" && renderedSel && !options.isCollapsed() && isSameSelection(getSelection(), renderedSel)) {
        sourceToken = readStateToken();
        syncValueDerivedUI(renderedSel);
        return;
      }
      flushPending();
      rendering = true;
      try {
        sourceToken = readStateToken();
        const detail = ensureDetail();
        const clips = getClips();
        const raw = getSelection();
        const sel = clampSelection(raw, clips);
        if (!isSameSelection(raw, sel)) {
          setSelection(sel);
          return;
        }
        const collapsed = options.isCollapsed();
        const prevBody = detail.querySelector(".vst-detail-body");
        const savedScroll = prevBody ? prevBody.scrollTop : 0;
        captureFocus();
        detail.className = `${DETAIL_CLASS}${collapsed ? " vst-detail-collapsed" : ""}`;
        detail.innerHTML = "";
        detail.appendChild(buildHeader(sel, collapsed));
        if (!collapsed) {
          const body = buildBody(sel, clips);
          detail.appendChild(body);
          if (sel.kind === "clip" || sel.kind === "retake") {
            enableSlidersIn(body);
          }
        }
        restoreFocus(detail);
        const newBody = detail.querySelector(".vst-detail-body");
        if (newBody && savedScroll > 0) {
          newBody.scrollTop = savedScroll;
        }
        if (!collapsed) {
          autoFocusSelection(detail, sel);
        }
        renderedSel = sel;
      } finally {
        rendering = false;
      }
    };
    const focusKeyForSelection = (sel) => {
      switch (sel.kind) {
        case "prompt-major":
          return "prompt-major";
        case "prompt-minor":
          return `minor-${sel.windowIdx}`;
        default:
          return null;
      }
    };
    const autoFocusSelection = (detail, sel) => {
      if (focusLeftDock) {
        return;
      }
      const active = document.activeElement;
      if (active instanceof HTMLElement && detail.contains(active)) {
        return;
      }
      const wantKey = focusKeyForSelection(sel);
      if (!wantKey) {
        return;
      }
      const editor = detail.querySelector(
        `textarea[data-vst-focus-key="${wantKey}"]`
      );
      if (!editor) {
        return;
      }
      editor.focus();
      const len = editor.value.length;
      try {
        editor.setSelectionRange(len, len);
      } catch {
      }
      if (typeof editor.scrollIntoView === "function") {
        editor.scrollIntoView({ block: "nearest" });
      }
    };
    const targetedReselect = (sel) => {
      if (!dockEl || !renderedSel || options.isCollapsed()) {
        return false;
      }
      const prev = renderedSel;
      if (prev.kind !== sel.kind) {
        return false;
      }
      const active = document.activeElement;
      const fromOutside = !(active instanceof HTMLElement && dockEl.contains(active));
      const swap = (rowSelector, activeClass, index) => {
        const rows = Array.from(
          dockEl?.querySelectorAll(rowSelector) ?? []
        );
        if (index < 0 || index >= rows.length) {
          return false;
        }
        rows.forEach((row, i) => {
          row.classList.toggle(activeClass, i === index);
        });
        const crumb = dockEl?.querySelector(".vst-detail-crumb");
        if (crumb) {
          crumb.textContent = breadcrumbFor(sel);
        }
        if (fromOutside && typeof rows[index].scrollIntoView === "function") {
          rows[index].scrollIntoView({ block: "nearest" });
        }
        renderedSel = sel;
        return true;
      };
      if (sel.kind === "prompt-minor" && prev.kind === "prompt-minor") {
        if (sel.clipIdx !== prev.clipIdx) {
          return false;
        }
        const ok = swap(
          ".vst-detail-minor-window",
          "vst-detail-minor-active",
          sel.windowIdx
        );
        if (ok) {
          if (fromOutside) {
            const editor = dockEl?.querySelector(
              `.vst-detail-minor-window[data-vst-minor-window="${sel.windowIdx}"] textarea`
            );
            if (editor) {
              editor.focus();
              const len = editor.value.length;
              try {
                editor.setSelectionRange(len, len);
              } catch {
              }
            }
          }
        }
        return ok;
      }
      if (sel.kind === "ref" && prev.kind === "ref") {
        if (sel.clipIdx !== prev.clipIdx) {
          return false;
        }
        return swap(
          ".vst-detail-ref-row",
          "vst-detail-instance-active",
          sel.refIdx
        );
      }
      if (sel.kind === "audio-segment" && prev.kind === "audio-segment") {
        if (sel.clipIdx !== prev.clipIdx) {
          return false;
        }
        return swap(
          ".vst-detail-seg-row",
          "vst-detail-instance-active",
          sel.segIdx
        );
      }
      return false;
    };
    const onSelectionChanged = (sel) => {
      if (suppressSelectionRender) {
        return;
      }
      if (targetedReselect(sel)) {
        return;
      }
      pendingFocus = null;
      focusLeftDock = false;
      settingsMode = null;
      if (sel.kind !== "none" && options.isCollapsed()) {
        options.setCollapsed(false);
      }
      render();
    };
    const attach = (body, dock) => {
      if (boundBody === body && dockEl === dock) {
        return;
      }
      dispose();
      boundBody = body;
      dockEl = dock;
      body.addEventListener("mousedown", onMouseDownCapture, true);
      body.addEventListener("click", onClickCapture, true);
      body.addEventListener("keydown", onKeyDownCapture, true);
      dock.addEventListener("keydown", onStripKeyDown);
      dock.addEventListener("focusout", onDockFocusOut);
      dock.addEventListener("focusin", onDockFocusIn);
      dock.addEventListener("change", onDockChange);
      document.addEventListener("pointerdown", onDocPointerDown, true);
      document.addEventListener("pointerup", onDocPointerUp, true);
      document.addEventListener("pointercancel", onDocPointerUp, true);
      unsubscribe = subscribeSelection(onSelectionChanged);
      render();
    };
    const dispose = () => {
      flushPending();
      if (pendingTimer) {
        clearTimeout(pendingTimer);
        pendingTimer = null;
      }
      pending.clear();
      sliderDragActive = false;
      focusLeftDock = false;
      document.removeEventListener("pointerdown", onDocPointerDown, true);
      document.removeEventListener("pointerup", onDocPointerUp, true);
      document.removeEventListener("pointercancel", onDocPointerUp, true);
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      if (boundBody) {
        boundBody.removeEventListener(
          "mousedown",
          onMouseDownCapture,
          true
        );
        boundBody.removeEventListener("click", onClickCapture, true);
        boundBody.removeEventListener("keydown", onKeyDownCapture, true);
        boundBody = null;
      }
      if (dockEl) {
        dockEl.removeEventListener("keydown", onStripKeyDown);
        dockEl.removeEventListener("focusout", onDockFocusOut);
        dockEl.removeEventListener("focusin", onDockFocusIn);
        dockEl.removeEventListener("change", onDockChange);
        dockEl.className = DETAIL_CLASS;
        dockEl.innerHTML = "";
        dockEl = null;
      }
      renderedSel = null;
    };
    return { attach, render, dispose };
  };

  // frontend/timelineHistory.ts
  var createTimelineHistory = (deps) => {
    const max = deps.maxDepth ?? 50;
    const undoStack = [];
    let redoStack = [];
    let last = null;
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

  // frontend/timelineLinking.ts
  var REGION_SELECTOR = ".vst-region[data-clip-idx]";
  var REGION_ACTION_SELECTOR = "[data-vst-region-action]";
  var REGION_RESIZE_SELECTOR = ".vst-region-resize";
  var CLIP_SHIFT_SELECTOR = ".vst-region[data-clip-idx], .vst-audio-clip[data-clip-idx]";
  var REGION_SELECTED_CLASS = "vst-region-selected";
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
    const markSelection = (body) => {
      for (const region of body.querySelectorAll(
        `.${REGION_SELECTED_CLASS}`
      )) {
        region.classList.remove(REGION_SELECTED_CLASS);
      }
      const idx = selectedClip();
      if (idx === null) {
        return;
      }
      findRegion(body, idx)?.classList.add(REGION_SELECTED_CLASS);
    };
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
    const applySkip = (idx) => {
      const clips = getClips();
      if (idx < 0 || idx >= clips.length) {
        return;
      }
      clips[idx].skipped = !clips[idx].skipped;
      saveClips(clips, { origin: "linking" });
    };
    const applyDelete = (idx) => {
      const clips = getClips();
      if (idx < 0 || idx >= clips.length) {
        return;
      }
      clips.splice(idx, 1);
      const sel = getSelection();
      if (sel.kind === "clip") {
        if (sel.clipIdx === idx) {
          setSelection({ kind: "none" });
        } else if (sel.clipIdx > idx) {
          setSelection({ ...sel, clipIdx: sel.clipIdx - 1 });
        }
      }
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
            state.sourceJson,
            "linking",
            (clips) => {
              const clip = clips[state.idx];
              if (state.idx < 0 || state.idx >= clips.length || clip.clipLengthFromAudio || clip.clipLengthFromControlNet || clip.sourceVideo) {
                return null;
              }
              const newDuration = pxToDuration(
                width,
                livePxPerSecond(body),
                currentTimelineFps()
              );
              if (!applyClipDurationResize(
                clip,
                newDuration,
                getRootDefaults
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
            markSelection(body);
            return;
          }
          commitClipMutation(state.sourceJson, "linking", (clips) => {
            if (from < 0 || from >= clips.length) {
              return null;
            }
            const destIdx = finalIndexAfterMove(from, gap);
            selectClip(destIdx, stageForClip(from));
            return moveItem(clips, from, gap);
          });
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
          sourceJson: readStateToken()
        });
      }
      const target = me.target.closest(REGION_SELECTOR);
      const idx = parseIntAttr(target, "data-clip-idx");
      if (idx === null) {
        return null;
      }
      return dragSession(body, {
        sourceIdx: idx,
        sourceJson: readStateToken()
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
      markSelection(body);
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
  var createTimelinePromptTrack = () => createWindowTrack({
    routeId: "prompt-track",
    priority: 20,
    origin: "prompt-track",
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
    readSpan: (clip, windowIdx) => {
      const window2 = clip.promptWindows?.[windowIdx];
      return window2 ? { start: window2.start, length: window2.duration, trim: 0 } : null;
    },
    moveTargetStart: (clip, windowIdx, press, desiredStart) => {
      const clipDur = clipDurationOf(clip);
      const [lo, hi] = wallsFor(clip, windowIdx, press);
      const dur = Math.min(press.length, clipDur);
      return clamp(desiredStart, lo, Math.max(lo, hi - dur));
    },
    writeMove: (clip, windowIdx, press, start) => {
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
    resizeTarget: (clip, windowIdx, edge, press, deltaSec) => {
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
    writeResize: (clip, windowIdx, _edge, _press, geom) => {
      const window2 = clip.promptWindows?.[windowIdx];
      if (!window2) {
        return;
      }
      window2.start = roundToTenth(geom.start);
      window2.duration = roundToTenth(geom.length);
    },
    createSpan: (clip, clipIdx, startSec, endSec) => {
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
    deleteItem: (clip, windowIdx) => {
      if (!clip.promptWindows?.[windowIdx]) {
        return false;
      }
      clip.promptWindows.splice(windowIdx, 1);
      return true;
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
  var createTimelineReferencesTrack = () => {
    let boundBody = null;
    let unregister = null;
    const findArrow = (clipIdx, refIdx) => boundBody?.querySelector(
      `.vst-region[data-clip-idx="${clipIdx}"] .vst-key[data-ref-idx="${refIdx}"]`
    ) ?? null;
    const positionRefMarker = (mark, arrow, frame, fromEnd, durationSeconds, fps) => {
      const time = keyframeTimeSeconds(frame, fromEnd, durationSeconds, fps);
      const leftPct2 = `${keyframeLeftPercent(time, durationSeconds)}%`;
      mark.style.left = leftPct2;
      if (arrow) {
        arrow.style.left = leftPct2;
      }
      const ph = mark.querySelector(".vst-refs-ph");
      if (ph) {
        ph.textContent = `R ${fromEnd ? "-" : ""}${frame}`;
      }
    };
    const addRefAtFrame = (clipIdx, frame, sourceJson) => {
      let newRefIdx = -1;
      const saved = commitClipMutation(
        sourceJson,
        "references-track",
        (clips) => {
          const clip = clips[clipIdx];
          if (!clip) {
            return null;
          }
          const frameMax = getReferenceFrameMax(getRootDefaults, clip);
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
    const deleteRef = (clipIdx, refIdx, sourceJson) => {
      commitClipMutation(sourceJson, "references-track", (clips) => {
        const clip = clips[clipIdx];
        return clip && removeRefAt(clip, refIdx) ? clips : null;
      });
    };
    const dragFrameAt = (state, clientX) => {
      const rect = state.lane.getBoundingClientRect();
      return pxToFrame(
        clientX - rect.left,
        rect.width,
        state.durationSeconds,
        state.fps,
        state.fromEnd
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
          state.sourceJson,
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
      const arrow = findArrow(clipIdx, refIdx);
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
        fps: currentTimelineFps(),
        fromEnd: ref.fromEnd === true,
        sourceJson: readStateToken()
      });
    };
    const selectRef = (clipIdx, refIdx) => {
      setSelection({ kind: "ref", clipIdx, refIdx });
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
            deleteRef(clipIdx2, refIdx, readStateToken());
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
      const rect = lane.getBoundingClientRect();
      const frame = pxToFrame(
        event.clientX - rect.left,
        rect.width,
        clip.duration,
        currentTimelineFps(),
        false
      );
      addRefAtFrame(clipIdx, frame, readStateToken());
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
  var createTimelineRetakeTrack = () => createWindowTrack({
    routeId: "retake",
    priority: 50,
    origin: "retake-track",
    spanSelector: ".vst-retake[data-clip-idx]",
    itemIdxAttr: null,
    edgeSelector: "[data-vst-retake-edge]",
    edgeAttr: "data-vst-retake-edge",
    laneSelector: ".vst-retake-lane[data-vst-retake-add]",
    draggingClass: "vst-retake-dragging",
    ghostClass: "vst-retake-ghost",
    unit: "pct",
    keyboardSelect: true,
    // The retake sits inside the clip region; its clicks must not bubble
    // into the region's clip-select handler.
    isolateClicks: true,
    readSpan: (clip) => clip.retake ? {
      start: clip.retake.startSeconds,
      length: clip.retake.lengthSeconds,
      trim: 0
    } : null,
    canCreate: (clip) => !clip.retake,
    moveTargetStart: (clip, _itemIdx, press, desiredStart) => {
      const clipDur = clipDurationOf(clip);
      const length = Math.min(press.length, clipDur);
      return clamp(desiredStart, 0, Math.max(0, clipDur - length));
    },
    writeMove: (clip, _itemIdx, press, start) => {
      if (!clip.retake) {
        return;
      }
      const clipDur = clipDurationOf(clip);
      const length = Math.min(press.length, clipDur);
      clip.retake.startSeconds = roundToTenth(start);
      clip.retake.lengthSeconds = roundToTenth(
        Math.min(length, clipDur - clip.retake.startSeconds)
      );
    },
    resizeTarget: (clip, _itemIdx, edge, press, deltaSec) => resizeSpanEdge(
      edge,
      press,
      deltaSec,
      RETAKE_MIN_DURATION,
      0,
      clipDurationOf(clip)
    ),
    writeResize: (clip, _itemIdx, _edge, _press, geom) => {
      if (!clip.retake) {
        return;
      }
      clip.retake.startSeconds = roundToTenth(geom.start);
      clip.retake.lengthSeconds = roundToTenth(geom.length);
    },
    // A plain tap places a default-length window at the pressed time; a
    // drag sizes it.
    createSpan: (clip, clipIdx, startSec, endSec) => {
      const geom = createDefaultOrDraggedSpan(
        startSec,
        endSec,
        0,
        clipDurationOf(clip),
        RETAKE_MIN_DURATION,
        RETAKE_DEFAULT_DURATION
      );
      if (!geom) {
        return null;
      }
      clip.retake = {
        startSeconds: roundToTenth(geom.start),
        lengthSeconds: roundToTenth(geom.length),
        strength: RETAKE_STRENGTH_DEFAULT
      };
      return { kind: "retake", clipIdx };
    },
    deleteItem: (clip) => {
      if (!clip.retake) {
        return false;
      }
      clip.retake = null;
      return true;
    },
    selectionFor: (clipIdx) => ({ kind: "retake", clipIdx })
  });

  // frontend/timelineSelectionView.ts
  var SELECTED = "vst-selected";
  var REGION_SELECTED = "vst-region-selected";
  var applySelectionHighlight = (body) => {
    const sel = getSelection();
    for (const el of body.querySelectorAll(`.${SELECTED}`)) {
      el.classList.remove(SELECTED);
    }
    if (sel.kind !== "clip") {
      for (const el of body.querySelectorAll(`.${REGION_SELECTED}`)) {
        el.classList.remove(REGION_SELECTED);
      }
    }
    let selector = null;
    switch (sel.kind) {
      case "ref":
        selector = `.vst-refs-mark[data-clip-idx="${sel.clipIdx}"][data-ref-idx="${sel.refIdx}"]`;
        break;
      case "audio":
        selector = `.vst-audio-clip[data-clip-idx="${sel.clipIdx}"]`;
        break;
      case "audio-segment":
        selector = `.vst-audio-seg[data-clip-idx="${sel.clipIdx}"][data-seg-idx="${sel.segIdx}"]`;
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
      if (typeof parsed.stripCollapsed === "boolean") {
        state.stripCollapsed = parsed.stripCollapsed;
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

  // frontend/videoStagesTimeline.ts
  var INPUT_SYNC_INTERVAL_MS = 200;
  var videoStagesTimeline = () => {
    let boundInput = null;
    let boundToggle = null;
    let inputSyncInterval = null;
    let paramRefreshHook = null;
    let storeUnsub = null;
    let unit = "seconds";
    let pxPerSecond = DEFAULT_PX_PER_SECOND;
    let lastRenderedPxPerSecond = 0;
    let stripCollapsed = false;
    let selectionUnsub = null;
    const detailStrip = createTimelineDetailStrip({
      isCollapsed: () => stripCollapsed,
      setCollapsed: (collapsed) => {
        stripCollapsed = collapsed;
        saveViewState2();
      }
    });
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack();
    const promptTrack = createTimelinePromptTrack();
    const audioTrack = createTimelineAudioTrack();
    const audioSegmentTrack = createTimelineAudioSegmentTrack();
    const boundaryTrack = createTimelineBoundaryTrack();
    const referencesTrack = createTimelineReferencesTrack();
    const openSettings = () => {
      stripCollapsed = false;
      saveViewState2();
      setSelection({ kind: "none" });
      detailStrip.render();
    };
    const history = createTimelineHistory({
      read: () => readCarrierSnapshot(),
      write: (value) => restoreCarrierSnapshot(value)
    });
    const loadViewState2 = () => {
      const stored = loadViewState();
      if (!stored) {
        return;
      }
      if (stored.pxPerSecond !== void 0) {
        pxPerSecond = clampPxPerSecond(stored.pxPerSecond);
      }
      if (stored.unit) {
        unit = stored.unit;
      }
      if (stored.stripCollapsed !== void 0) {
        stripCollapsed = stored.stripCollapsed;
      }
    };
    const saveViewState2 = () => {
      saveViewState({ pxPerSecond, unit, stripCollapsed });
    };
    const toggleUnit = () => {
      unit = unit === "seconds" ? "frames" : "seconds";
      saveViewState2();
      refresh();
    };
    const timelineBody = () => document.getElementById(TIMELINE_BODY_ID);
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
    const scrollEl = () => timelineBody()?.querySelector(".vst-scroll") ?? null;
    const setZoom = (value) => {
      pxPerSecond = clampPxPerSecond(value);
      saveViewState2();
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
      const prev = clips[clips.length - 1] ?? null;
      if (prev && clips.length >= 2) {
        const prevJoin = clips[clips.length - 2];
        prev.boundaryOut = prevJoin.boundaryOut;
        prev.boundaryOutOverlap = prevJoin.boundaryOutOverlap;
      }
      clips.push(
        buildDefaultClip(
          getRootDefaults,
          getDefaultStageModel,
          false,
          prev
        )
      );
      saveClips(clips, { origin: "timeline" });
    };
    const renderAll = (meta) => {
      const enabled = isVideoStagesEnabled();
      updateTimelineTabIndicator(enabled);
      const body = document.getElementById(TIMELINE_BODY_ID);
      if (!body) {
        return;
      }
      const prevScrollLeft = meta?.origin === "external" ? 0 : scrollEl()?.scrollLeft ?? 0;
      try {
        const state = getState();
        const clips = state.clips;
        renderTimeline(body, clips, {
          fps: safeFps(state.fps),
          width: state.width,
          height: state.height,
          dimsExplicit: state.dimsExplicit,
          fpsExplicit: state.fpsExplicit,
          unit,
          pxPerSecond,
          selectedIndex: linking.getSelectedIndex(),
          enabled,
          onToggleEnabled: setVideoStagesEnabled,
          onOpenSettings: () => openSettings(),
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
        if (prevScrollLeft > 0) {
          const target = lastRenderedPxPerSecond > 0 && lastRenderedPxPerSecond !== pxPerSecond ? zoomAnchorScrollLeft(
            zoomAnchorTime(
              TRACK_HEADER_W_PX,
              prevScrollLeft,
              lastRenderedPxPerSecond
            ),
            pxPerSecond,
            TRACK_HEADER_W_PX
          ) : prevScrollLeft;
          const fresh = scrollEl();
          if (fresh) {
            fresh.scrollLeft = target;
          }
        }
        lastRenderedPxPerSecond = pxPerSecond;
        linking.reapplySelection(body, clips.length);
        detailStrip.render(meta);
        applySelectionHighlight(body);
      } catch (error) {
        console.warn("VideoStages: timeline render failed", error);
      }
    };
    const refresh = () => renderAll();
    const onInputChanged = () => {
      getTimelineStore().syncFromCarrier();
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
      inputSyncInterval = setInterval(() => {
        getTimelineStore().syncFromCarrier();
      }, INPUT_SYNC_INTERVAL_MS);
    };
    const registerParamRefreshHook = () => {
      if (paramRefreshHook || typeof refreshParamsExtra === "undefined" || !Array.isArray(refreshParamsExtra)) {
        return;
      }
      paramRefreshHook = () => {
        setTimeout(() => refresh(), 0);
      };
      refreshParamsExtra.push(paramRefreshHook);
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
      loadViewState2();
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
      bindInputListener();
      bindToggleListener();
      history.syncBaseline();
      document.removeEventListener("keydown", onKeydown);
      document.addEventListener("keydown", onKeydown);
      startInputSync();
      registerParamRefreshHook();
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
      if (paramRefreshHook && typeof refreshParamsExtra !== "undefined" && Array.isArray(refreshParamsExtra)) {
        const idx = refreshParamsExtra.indexOf(paramRefreshHook);
        if (idx !== -1) {
          refreshParamsExtra.splice(idx, 1);
        }
      }
      paramRefreshHook = null;
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
      document.removeEventListener("keydown", onKeydown);
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
      if (!document.getElementById(DATA_INPUT_ID)) {
        console.warn(
          `VideoStages: Data param input (#${DATA_INPUT_ID}) never appeared — is the VideoStages backend extension loaded?`
        );
      }
    }, 1e4);
  };
  var registerVideoStagesPromptPrefix = () => {
    if (typeof promptTabComplete === "undefined") {
      return;
    }
    promptTabComplete.registerPrefix(
      "videoclip",
      "Per-clip prompt sections and prompt windows for the VideoStages timeline.",
      () => [
        "\n<videoclip[0]>clip 0's prompt text — everything until the next <videoclip...> tag.",
        "\n<videoclip[0]:1.5-4>a prompt window on clip 0 from 1.5s to 4s.",
        "\nThe timeline owns these; structured config (stages, refs, audio) rides in the hidden Data param."
      ],
      true
    );
  };
  var DATA_INPUT_RETRY_MS = 250;
  var dataInputRetryTimer = null;
  var initTimeline = () => {
    if (!document.getElementById(DATA_INPUT_ID)) {
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
  var scheduleTimelineInit = () => {
    if (!Array.isArray(postParamBuildSteps)) {
      setTimeout(scheduleTimelineInit, 200);
      return;
    }
    postParamBuildSteps.push(initTimeline);
  };
  scheduleTimelineInit();
  registerVideoStagesPromptPrefix();
  refineVideoButton();
  injectTimelineTab();
})();
//# sourceMappingURL=video-stages.js.map
