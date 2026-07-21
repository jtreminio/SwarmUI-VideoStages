import {
    clamp,
    ROOT_DIMENSION_MAX,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_STEP,
    ROOT_FPS_MAX,
    ROOT_FPS_MIN,
} from "../constants";
import {
    buildCheckbox,
    buildField,
    buildNumber,
    buildOptionSelect,
    type OptionSpec,
    wrapForm,
} from "../detailWidgets";
import {
    DIMENSION_PRESET_KEYS,
    matchPresetKey,
    presetBadgeElements,
    presetDimensions,
} from "../dimensionPresets";
import { getState } from "../persistence";
import { getRootDefaults } from "../rootDefaults";
import type { DetailStripContext } from "./context";

const GROUP_SETTINGS = "vstdock_settings";
const SETTINGS_INHERIT = "inherit";
const SETTINGS_CUSTOM = "custom";

const clampDimension = (value: number): number =>
    clamp(
        Math.round(value) || ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
    );

const clampFps = (value: number): number =>
    clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);

export const buildSettingsBody = (ctx: DetailStripContext): HTMLElement => {
    const state = getState();
    const defaults = getRootDefaults();
    const core = {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
    const defaultMode = !state.dimsExplicit
        ? SETTINGS_INHERIT
        : (matchPresetKey(state.width, state.height) ?? SETTINGS_CUSTOM);
    const mode = ctx.getSettingsMode() ?? defaultMode;
    const isCustom = mode === SETTINGS_CUSTOM;
    const displayed =
        mode === SETTINGS_CUSTOM
            ? {
                  width: clampDimension(state.width),
                  height: clampDimension(state.height),
              }
            : mode === SETTINGS_INHERIT
              ? { width: core.width, height: core.height }
              : (presetDimensions(mode) ?? {
                    width: clampDimension(state.width),
                    height: clampDimension(state.height),
                });

    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-settings";

    const resSpecs: OptionSpec[] = [
        {
            value: SETTINGS_INHERIT,
            label: `Use image resolution (${core.width}×${core.height})`,
        },
        ...DIMENSION_PRESET_KEYS.map((key) => ({
            value: key,
            label: key.replace("x", " × "),
        })),
        { value: SETTINGS_CUSTOM, label: "Custom" },
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
        },
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
        },
    );
    heightInput.classList.add("vst-settings-num");
    heightInput.disabled = !isCustom;
    heightInput.setAttribute("data-vst-focus-key", "settings-height");

    // Width and Height share one "Dimensions" row (W × H) to keep the
    // wrapping settings flow dense.
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
        },
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
        },
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
