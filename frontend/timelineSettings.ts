import {
    clamp,
    ROOT_DIMENSION_MAX,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_STEP,
    ROOT_FPS_MAX,
    ROOT_FPS_MIN,
} from "./constants";
import {
    DIMENSION_PRESET_KEYS,
    presetBadgeElements,
    presetDimensions,
} from "./dimensionPresets";
import { getState, saveState } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import { isVideoStagesEnabled } from "./swarmInputs";

const INHERIT_MODE = "inherit";
const CUSTOM_MODE = "custom";
const INSPECTOR_SELECTOR = ".vst-settings-inspector";

export interface TimelineSettings {
    open(anchor: HTMLElement): void;
    close(): void;
    dispose(): void;
}

interface InheritedDims {
    width: number;
    height: number;
    fps: number;
}

const clampDimension = (value: number): number =>
    clamp(
        Math.round(value) || ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
    );

const clampFps = (value: number): number =>
    clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);

const inheritedDims = (): InheritedDims => {
    const defaults = getRootDefaults();
    return {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
};

export const createTimelineSettings = (
    refresh: () => void,
): TimelineSettings => {
    let activeWrap: HTMLElement | null = null;
    let editingAnchor: HTMLElement | null = null;
    let outsideMouseHandler: ((event: MouseEvent) => void) | null = null;

    const close = (): void => {
        if (outsideMouseHandler) {
            document.removeEventListener(
                "mousedown",
                outsideMouseHandler,
                true,
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

    const buildField = (label: string, control: HTMLElement): HTMLElement => {
        const row = document.createElement("div");
        row.className = "vst-audio-field vst-settings-field";
        const text = document.createElement("span");
        text.className = "vst-audio-field-label";
        text.textContent = label;
        row.append(text, control);
        return row;
    };

    const open = (anchor: HTMLElement): void => {
        close();

        const state = getState();
        let mode: string = !state.dimsExplicit
            ? INHERIT_MODE
            : (DIMENSION_PRESET_KEYS.find((key) => {
                  const dims = presetDimensions(key);
                  return (
                      dims &&
                      dims.width === Math.round(state.width) &&
                      dims.height === Math.round(state.height)
                  );
              }) ?? CUSTOM_MODE);
        let customWidth = clampDimension(state.width);
        let customHeight = clampDimension(state.height);
        let fpsExplicit = state.fpsExplicit;
        let fpsValue = clampFps(state.fps);

        const displayedDims = (): { width: number; height: number } => {
            if (mode === CUSTOM_MODE) {
                return { width: customWidth, height: customHeight };
            }
            if (mode === INHERIT_MODE) {
                const core = inheritedDims();
                return { width: core.width, height: core.height };
            }
            return (
                presetDimensions(mode) ?? {
                    width: customWidth,
                    height: customHeight,
                }
            );
        };

        const commit = (): void => {
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
            saveState(next, undefined, {
                notifyDomChange: isVideoStagesEnabled(),
            });
            refresh();
        };

        const rect = anchor.getBoundingClientRect();
        const viewportW =
            window.innerWidth || document.documentElement.clientWidth;
        const width = 300;
        const left = clamp(
            Math.round(rect.left),
            8,
            Math.max(8, viewportW - width - 8),
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

        const syncResolutionUi = (): void => {
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

        const syncFpsUi = (): void => {
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
            hint,
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
        const onOutside = (event: MouseEvent): void => {
            const target = event.target;
            if (!(target instanceof Element)) {
                return;
            }
            if (
                target.closest(INSPECTOR_SELECTOR) ||
                target.closest("[data-vst-settings]") ||
                target.closest(".sui-popover")
            ) {
                return;
            }
            close();
        };
        outsideMouseHandler = onOutside;
        document.addEventListener("mousedown", onOutside, true);

        document.body.appendChild(wrap);
        const viewportH =
            window.innerHeight || document.documentElement.clientHeight;
        const height = wrap.offsetHeight;
        let top = Math.round(rect.bottom + 6);
        if (top + height > viewportH - 8) {
            top = Math.round(rect.top - 6 - height);
        }
        wrap.style.top = `${clamp(top, 8, Math.max(8, viewportH - height - 8))}px`;
        activeWrap = wrap;
        resSelect.focus();
    };

    const dispose = (): void => {
        close();
    };

    return { open, close, dispose };
};
