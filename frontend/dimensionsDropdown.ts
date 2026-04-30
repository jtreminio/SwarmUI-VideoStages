import { DIMENSIONS_PRESET_CUSTOM_VALUE } from "./constants";
import {
    DIMENSIONS_PRESET_METADATA_INPUT_ID,
    DIMENSIONS_PRESET_SELECT_ID,
    getRootDimensionParamInput,
    ROOT_DIMENSION_HEIGHT_INPUT_ID,
    ROOT_DIMENSION_WIDTH_INPUT_ID,
} from "./swarmInputs";

const DIMENSIONS_PRESET_INFO_ID = "vs_dimensions_preset_info";

type WidthHeight = { width: number; height: number };
let presetStopsMapCache: Record<string, string[]> | null = null;
let upscaleBadgeElementsByValueKeyCache: ReadonlyMap<
    string,
    HTMLSpanElement[]
> | null = null;

const readPresetMetadataFromDom = (): Record<string, string[]> => {
    const el = document.getElementById(DIMENSIONS_PRESET_METADATA_INPUT_ID);
    let raw = "";
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
        raw = el.value.trim();
    }
    if (!raw) {
        return {};
    }
    try {
        const obj = JSON.parse(raw) as unknown;
        if (!obj || typeof obj !== "object" || Array.isArray(obj)) {
            return {};
        }
        const out: Record<string, string[]> = {};
        const rec = obj as Record<string, unknown>;
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

const getPresetStopsMap = (): Record<string, string[]> => {
    if (!presetStopsMapCache) {
        presetStopsMapCache = readPresetMetadataFromDom();
    }
    return presetStopsMapCache;
};

type UpscaleStop = {
    width: number;
    height: number;
    controlNetFriendly: boolean;
    steps: readonly string[];
};

const splitDimensionLabel = (label: string): WidthHeight => {
    const [w, h] = label.replace("*", "").split("x");
    return { width: Math.round(Number(w)), height: Math.round(Number(h)) };
};

const parsePresetDimensions = (value: string): WidthHeight | null => {
    if (!value || value === DIMENSIONS_PRESET_CUSTOM_VALUE) {
        return null;
    }
    return splitDimensionLabel(value);
};

const parsePresets = (presetKey: string): UpscaleStop[] => {
    const presetLines = getPresetStopsMap()[presetKey];
    if (!presetLines || presetLines.length === 0) {
        return [];
    }
    const out: UpscaleStop[] = [];

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
            width: width,
            height: height,
            controlNetFriendly,
            steps: parts.slice(1),
        });
    }

    return out;
};

const buildUpscaleBadgeElementsByValueKey = (): ReadonlyMap<
    string,
    HTMLSpanElement[]
> => {
    const upscaleBadgeElementsByValueKey = new Map<string, HTMLSpanElement[]>();
    const stopsMap = getPresetStopsMap();
    const presetKeys = Object.keys(stopsMap);

    const upscaleBadgeElement = (stop: UpscaleStop): HTMLSpanElement => {
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
        const star = stop.controlNetFriendly
            ? `<span class="controlnet-friendly">*</span> `
            : "";
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
            stops.map((s) => upscaleBadgeElement(s)),
        );
    }

    return upscaleBadgeElementsByValueKey;
};

let suppressManualDimensionPresetGuard = 0;

const applyDimensionsToInputs = (width: number, height: number): void => {
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

export const applyVideoStagesPresetDimensionsBeforeGenerate = (): void => {
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

const updateUpscaleInfoPanel = (select: HTMLSelectElement): void => {
    const el = document.getElementById(DIMENSIONS_PRESET_INFO_ID);
    if (!(el instanceof HTMLElement)) {
        return;
    }
    const val = select.value;
    let badges: HTMLSpanElement[] | null = null;
    if (val && val !== DIMENSIONS_PRESET_CUSTOM_VALUE) {
        if (!upscaleBadgeElementsByValueKeyCache) {
            upscaleBadgeElementsByValueKeyCache =
                buildUpscaleBadgeElementsByValueKey();
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

const updateSliderVisibility = (select: HTMLSelectElement): void => {
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

const syncSelectFromInputs = (select: HTMLSelectElement): void => {
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
        if (
            parsed &&
            parsed.width === bw &&
            parsed.height === bh &&
            Array.from(select.options).some((o) => o.value === currentVal)
        ) {
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

const wireSelectIfNeeded = (select: HTMLSelectElement): void => {
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
    const onManualDimension = (): void => {
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
        if (
            Math.round(Number(wIn.value)) !== parsedBase.width ||
            Math.round(Number(hIn.value)) !== parsedBase.height
        ) {
            sel.value = DIMENSIONS_PRESET_CUSTOM_VALUE;
            updateSliderVisibility(sel);
            updateUpscaleInfoPanel(sel);
        }
    };
    const attachDimListeners = (el: Element | null): void => {
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
        document.getElementById(`${ROOT_DIMENSION_WIDTH_INPUT_ID}_rangeslider`),
    );
    attachDimListeners(
        document.getElementById(
            `${ROOT_DIMENSION_HEIGHT_INPUT_ID}_rangeslider`,
        ),
    );
};

const ensureInfoPanel = (dropdownBox: HTMLElement | null): void => {
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

export const wireDimensionsPreset = (): void => {
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

export const CUSTOM_DIMENSION_VALUE = DIMENSIONS_PRESET_CUSTOM_VALUE;

export const __testOnly = {
    parsePresetDimensions,
};
