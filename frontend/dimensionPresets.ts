export const DIMENSION_PRESET_KEYS: readonly string[] = [
    "256x384",
    "384x512",
    "384x640",
    "512x768",
    "512x896",
    "512x1024",
    "768x1024",
    "384x256",
    "512x384",
    "640x384",
    "768x512",
    "896x512",
    "1024x512",
    "1024x768",
];

export const DIMENSION_PRESET_METADATA: Readonly<Record<string, string[]>> = {
    "256x384": [
        "384x576,1.5",
        "576x864,1.5,1.5",
        "*768x1152,1.5,2",
        "1152x1728,1.5,1.5,2",
    ],
    "384x512": [
        "576x768,1.5",
        "864x1152,1.5,1.5",
        "*1152x1536,1.5,2",
        "1728x2304,1.5,1.5,2",
    ],
    "384x640": [
        "576x960,1.5",
        "864x1440,1.5,1.5",
        "1152x1920,1.5,2",
        "1728x2880,1.5,1.5,2",
    ],
    "512x768": [
        "768x1152,1.5",
        "*1152x1728,1.5,1.5",
        "*1536x2304,1.5,2",
        "2304x3456,1.5,1.5,2",
    ],
    "512x896": ["*1536x2688,1.5,2"],
    "512x1024": ["*1152x2304,1.5,1.5", "*1536x3072,1.5,2"],
    "768x1024": ["*1728x2304,1.5,1.5", "*2304x3072,1.5,2"],
    "384x256": [
        "576x384,1.5",
        "864x576,1.5,1.5",
        "*1152x768,1.5,2",
        "1728x1152,1.5,1.5,2",
    ],
    "512x384": [
        "768x576,1.5",
        "1152x864,1.5,1.5",
        "*1536x1152,1.5,2",
        "2304x1728,1.5,1.5,2",
    ],
    "640x384": [
        "960x576,1.5",
        "1440x864,1.5,1.5",
        "1920x1152,1.5,2",
        "2880x1728,1.5,1.5,2",
    ],
    "768x512": [
        "1152x768,1.5",
        "*1728x1152,1.5,1.5",
        "*2304x1536,1.5,2",
        "3456x2304,1.5,1.5,2",
    ],
    "896x512": ["*2688x1536,1.5,2"],
    "1024x512": ["*2304x1152,1.5,1.5", "*3072x1536,1.5,2"],
    "1024x768": ["*2304x1728,1.5,1.5", "*3072x2304,1.5,2"],
};

export interface WidthHeight {
    width: number;
    height: number;
}

export interface UpscaleStop {
    width: number;
    height: number;
    controlNetFriendly: boolean;
    steps: readonly string[];
}

const splitDimensionLabel = (label: string): WidthHeight => {
    const [w, h] = label.replace("*", "").split("x");
    return { width: Math.round(Number(w)), height: Math.round(Number(h)) };
};

export const presetDimensions = (presetKey: string): WidthHeight | null => {
    if (!presetKey || !DIMENSION_PRESET_METADATA[presetKey]) {
        return null;
    }
    return splitDimensionLabel(presetKey);
};

export const matchPresetKey = (
    width: number,
    height: number,
): string | null => {
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

export const parsePresetStops = (presetKey: string): UpscaleStop[] => {
    const presetLines = DIMENSION_PRESET_METADATA[presetKey];
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
            width,
            height,
            controlNetFriendly,
            steps: parts.slice(1),
        });
    }
    return out;
};

export const upscaleBadgeElement = (stop: UpscaleStop): HTMLSpanElement => {
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

export const presetBadgeElements = (presetKey: string): HTMLSpanElement[] =>
    parsePresetStops(presetKey).map((stop) => upscaleBadgeElement(stop));
