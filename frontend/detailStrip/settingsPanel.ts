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
    buildSlider,
    type OptionSpec,
    tagFocus,
    wrapForm,
} from "../detailWidgets";
import {
    ASPECT_RATIOS,
    dimensionsFor,
    matchAspectRatio,
    sideLengthForDimensions,
} from "../dimensionPresets";
import { snapDimensions } from "../dimensionSnap";
import { activeDocumentDimensionMultiple } from "../documentDimensionSnap";
import { getVideoStagesHostBridge } from "../host";
import {
    type DimensionSnapSetting,
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "../timelineAuthoringSettings";
import type { AuthoringDocument, TimelineSelection } from "../types";
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

const clampSideLength = (value: number): number =>
    clamp(
        Math.round((Math.round(value) || 1024) / ROOT_DIMENSION_STEP) *
            ROOT_DIMENSION_STEP,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
    );

const setSliderDisabled = (slider: HTMLElement, disabled: boolean): void => {
    slider.querySelectorAll<HTMLInputElement>("input").forEach((input) => {
        input.disabled = disabled;
    });
    const box = slider.querySelector<HTMLElement>(".auto-slider-box");
    if (box) {
        box.dataset.disabled = `${disabled}`;
    }
};

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
    state: AuthoringDocument,
    _selection: Extract<TimelineSelection, { kind: "none" | "audio-track" }> = {
        kind: "none",
    },
): HTMLElement => {
    const defaults = ctx.authoring().defaults;
    const core = {
        width: defaults.width,
        height: defaults.height,
        fps: defaults.fps,
    };
    const architectureMultiple = activeDocumentDimensionMultiple(
        state.clips,
        defaults.modelCatalog,
    );
    const dimensionSnap = getTimelineAuthoringSettings().dimensionSnap;
    const multiple =
        dimensionSnap === "disabled"
            ? architectureMultiple
            : Math.max(architectureMultiple, dimensionSnap);
    const defaultMode = !state.dimsExplicit
        ? SETTINGS_INHERIT
        : (matchAspectRatio(state.width, state.height, multiple) ??
          SETTINGS_CUSTOM);
    const mode = ctx.getSettingsMode() ?? defaultMode;
    const isCustom = mode === SETTINGS_CUSTOM;
    const isInherited = mode === SETTINGS_INHERIT;
    const fallbackSideLength =
        defaults.sideLength ??
        (defaults.aspectRatio
            ? sideLengthForDimensions(
                  defaults.aspectRatio,
                  core.width,
                  core.height,
              )
            : 1024);
    const selectedSideLength =
        !isInherited && !isCustom
            ? sideLengthForDimensions(mode, state.width, state.height)
            : clampSideLength(fallbackSideLength);
    const rawDimensions =
        isInherited || isCustom
            ? {
                  width: clampDimension(isInherited ? core.width : state.width),
                  height: clampDimension(
                      isInherited ? core.height : state.height,
                  ),
              }
            : (dimensionsFor(mode, selectedSideLength) ?? {
                  width: clampDimension(state.width),
                  height: clampDimension(state.height),
              });
    const effectiveDimensions = snapDimensions(
        rawDimensions.width,
        rawDimensions.height,
        multiple,
    );

    const body = document.createElement("div");
    body.className = "vst-detail-form-body vst-detail-settings";

    const ratioSpecs: OptionSpec[] = [
        {
            value: SETTINGS_INHERIT,
            label: `Use image resolution (${core.width}×${core.height})`,
        },
        ...ASPECT_RATIOS.map((ratio) => ({
            value: ratio.id,
            label: ratio.label,
        })),
        { value: SETTINGS_CUSTOM, label: "Custom" },
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
                    height: effectiveDimensions.height,
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

    const dimensionSnapSpecs: OptionSpec[] = [
        { value: "disabled", label: "Disabled" },
        { value: "32", label: "Multiples of 32" },
        { value: "64", label: "Multiples of 64" },
    ];
    const dimensionSnapSelect = buildOptionSelect(
        dimensionSnapSpecs,
        `${dimensionSnap}`,
        (value) => {
            const selected: DimensionSnapSetting =
                value === "32" ? 32 : value === "64" ? 64 : "disabled";
            setTimelineAuthoringSetting("dimensionSnap", selected);
            ctx.render();
        },
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
                            multiple,
                        );
                        next.dimsExplicit = true;
                        next.width = snapped.width;
                        next.height = snapped.height;
                    });
                },
            ),
            "settings-width",
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
                            multiple,
                        );
                        next.dimsExplicit = true;
                        next.width = snapped.width;
                        next.height = snapped.height;
                    });
                },
            ),
            "settings-height",
        );
        body.append(widthSlider, heightSlider);
    } else if (!isInherited) {
        const ratioHasReference =
            dimensionsFor(mode, selectedSideLength) !== null;
        let calculatedDimensions: HTMLSpanElement | null = null;
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
                        multiple,
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
                    hint: !ratioHasReference
                        ? "(the host has no 3:4 reference; current dimensions are retained)"
                        : undefined,
                    isPot: true,
                },
            ),
            "settings-side-length",
        );
        calculatedDimensions = document.createElement("span");
        calculatedDimensions.className = "vst-settings-calculated-dims";
        calculatedDimensions.textContent = `${effectiveDimensions.width} × ${effectiveDimensions.height}`;
        sideLengthSlider
            .querySelector("label")
            ?.appendChild(calculatedDimensions);
        setSliderDisabled(sideLengthSlider, !ratioHasReference);
        body.appendChild(sideLengthSlider);
    }

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
