import {
    clamp,
    ROOT_DIMENSION_MAX,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_STEP,
    ROOT_FPS_MAX,
    ROOT_FPS_MIN,
} from "../constants";
import {
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
import { getVideoStagesHostBridge } from "../host";
import { getState } from "../persistence";
import { getRootDefaults } from "../rootDefaults";
import type { TimelineSelection } from "../types";
import type { DetailStripContext } from "./context";

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

const FPS_WRITE_DEBOUNCE_MS = 300;
let fpsWriteTimer: ReturnType<typeof setTimeout> | null = null;

/**
 * The panel's FPS field is a second view of the core Video FPS param: edits
 * here write through to the core input (debounced), and core edits flow back
 * via the carrier token's inherited-dims signature.
 */
const scheduleCoreFpsWrite = (value: number): void => {
    if (fpsWriteTimer !== null) {
        clearTimeout(fpsWriteTimer);
    }
    fpsWriteTimer = setTimeout(() => {
        fpsWriteTimer = null;
        const bridge = getVideoStagesHostBridge();
        const core = bridge.getRootVideoFpsInput();
        if (!core || core.value === `${value}`) {
            return;
        }
        core.value = `${value}`;
        bridge.notifyChanged(core);
    }, FPS_WRITE_DEBOUNCE_MS);
};

export const buildSettingsBody = (
    ctx: DetailStripContext,
    _selection: Extract<TimelineSelection, { kind: "none" | "audio-track" }> = {
        kind: "none",
    },
): HTMLElement => {
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

    // Width and Height only exist as inputs while the resolution is Custom;
    // presets and inherit modes fully determine the dimensions.
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
            },
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
            },
        );
        heightInput.setAttribute("data-vst-focus-key", "settings-height");

        // Width and Height share one "Dimensions" row (W × H) to keep the
        // wrapping settings flow dense.
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

    const hasCoreFps =
        getVideoStagesHostBridge().getRootVideoFpsInput() !== null;
    const fpsInput = buildNumber(
        clampFps(state.fps),
        ROOT_FPS_MIN,
        ROOT_FPS_MAX,
        1,
        (value) => {
            scheduleCoreFpsWrite(clampFps(value));
        },
    );
    fpsInput.disabled = !hasCoreFps;
    fpsInput.setAttribute("data-vst-focus-key", "settings-fps");
    body.appendChild(
        buildField(
            "FPS",
            fpsInput,
            hasCoreFps ? undefined : "(no core Video FPS parameter found)",
            "Frames per second for the whole timeline. This is the same " +
                "value as the core Video FPS parameter — editing either " +
                "updates both.",
        ),
    );
    return wrapForm("timeline-settings", "Timeline", body);
};
