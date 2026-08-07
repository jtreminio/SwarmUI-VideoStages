import { MEDIA_SOURCE_REFINER } from "./generatedMediaSource";
import { parseBase2EditStageIndex } from "./mediaSourceSyntax";
import type { Stage } from "./types";

export type TimelineUnit = "seconds" | "frames";

const DEFAULT_FPS = 24;

export const safeFps = (fps: number | null | undefined): number =>
    typeof fps === "number" && Number.isFinite(fps) && fps > 0
        ? fps
        : DEFAULT_FPS;

export const keyframeTimeSeconds = (
    frame: number,
    fromEnd: boolean,
    clipDurationSeconds: number,
    fps?: number,
): number => {
    const duration = Math.max(0, clipDurationSeconds || 0);
    const offset = Math.max(0, frame || 0) / safeFps(fps);
    const raw = fromEnd ? duration - offset : offset;
    return Math.min(Math.max(raw, 0), duration);
};

export const formatTimeLabel = (
    seconds: number,
    unit: TimelineUnit,
    fps: number,
): string => {
    if (unit === "frames") {
        return `${Math.round((seconds || 0) * safeFps(fps))}f`;
    }
    const rounded = Math.round((seconds || 0) * 10) / 10;
    return Number.isInteger(rounded) ? `${rounded}s` : `${rounded.toFixed(1)}s`;
};

/** Duration summaries always retain one decimal place: `1.0s`, not `1s`. */
export const formatSecondsTenth = (seconds: number): string =>
    `${(Math.round((Number.isFinite(seconds) ? seconds : 0) * 10) / 10).toFixed(1)}s`;

/** A frame count expressed as tenths-rounded seconds at `fps`. */
export const formatOverlapSeconds = (frames: number, fps: number): string =>
    formatSecondsTenth(frames / Math.max(1, fps));

export interface RulerTick {
    x: number;
    seconds: number;
}

export const RULER_MIN_TICK_SPACING_PX = 60;
const RULER_STEP_LADDER_SECONDS = [
    0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600,
];

export const chooseRulerStepSeconds = (
    pxPerSecond: number,
    minSpacingPx = RULER_MIN_TICK_SPACING_PX,
): number => {
    const pps = pxPerSecond > 0 ? pxPerSecond : 1;
    for (const step of RULER_STEP_LADDER_SECONDS) {
        if (step * pps >= minSpacingPx) {
            return step;
        }
    }
    return RULER_STEP_LADDER_SECONDS[RULER_STEP_LADDER_SECONDS.length - 1];
};

export const computeRulerTicks = (
    totalSeconds: number,
    pxPerSecond: number,
    minSpacingPx = RULER_MIN_TICK_SPACING_PX,
): RulerTick[] => {
    const total = Math.max(0, totalSeconds || 0);
    if (total <= 0 || pxPerSecond <= 0) {
        return [{ x: 0, seconds: 0 }];
    }
    const step = chooseRulerStepSeconds(pxPerSecond, minSpacingPx);
    const ticks: RulerTick[] = [];
    const MAX_TICKS = 1000;
    for (let i = 0; i < MAX_TICKS; i++) {
        const t = i * step;
        if (t > total + 1e-6) {
            break;
        }
        ticks.push({ x: t * pxPerSecond, seconds: t });
    }
    return ticks;
};

export const formatRulerLabel = (
    seconds: number,
    unit: TimelineUnit,
    fps: number,
): string => {
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

export const truncate = (value: string, max = 80): string => {
    const text = `${value ?? ""}`;
    return text.length <= max
        ? text
        : `${text.slice(0, Math.max(0, max - 1))}…`;
};

export const refSourceLabel = (source: string): string => {
    const value = `${source ?? ""}`.trim();
    if (!value) {
        return MEDIA_SOURCE_REFINER;
    }
    const editStage = parseBase2EditStageIndex(value);
    if (editStage != null) {
        return `Base2Edit Edit ${editStage}`;
    }
    return value;
};

export interface Badge {
    label: string;
    title: string;
}

export const audioSourceBadge = (source: string): Badge => {
    const value = `${source ?? ""}`.trim();
    if (!value || value === "Native") {
        return { label: "Native", title: "Audio source: Native" };
    }
    return { label: value, title: `Audio source: ${value}` };
};

export const shortModelName = (model: string): string => {
    const raw = `${model ?? ""}`.trim();
    if (!raw) {
        return "(default)";
    }
    const segment = raw.split(/[\\/]/).pop() ?? raw;
    return segment.replace(/\.(safetensors|ckpt|pt|pth|gguf|sft|bin)$/i, "");
};

export const stageChipLabel = (index: number): string => `S${index}`;

export const stageChipTitle = (stage: Stage, index: number): string => {
    const parts = [
        `Stage ${index}${index === 0 ? " (full gen)" : " (refine)"}`,
        `model: ${shortModelName(stage?.model ?? "")}`,
        `steps: ${stage?.steps ?? "?"}`,
        `cfg: ${stage?.cfgScale ?? "?"}`,
        `control: ${stage?.control ?? "?"}`,
    ];
    if (stage?.skipped) {
        parts.push("skipped");
    }
    return parts.join(" · ");
};
