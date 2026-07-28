import {
    ROOT_DIMENSION_MAX,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_STEP,
} from "./constants";
import { snapDimensions } from "./dimensionSnap";

export interface WidthHeight {
    width: number;
    height: number;
}

export interface AspectRatio {
    id: string;
    label: string;
    reference: WidthHeight | null;
}

export const ASPECT_RATIOS: readonly AspectRatio[] = [
    {
        id: "1:1",
        label: "1:1 (Square)",
        reference: { width: 512, height: 512 },
    },
    {
        id: "4:3",
        label: "4:3 (Old PC)",
        reference: { width: 576, height: 448 },
    },
    {
        id: "3:2",
        label: "3:2 (Semi-wide)",
        reference: { width: 608, height: 416 },
    },
    {
        id: "8:5",
        label: "8:5",
        reference: { width: 608, height: 384 },
    },
    {
        id: "16:9",
        label: "16:9 (Standard Widescreen)",
        reference: { width: 672, height: 384 },
    },
    {
        id: "21:9",
        label: "21:9 (Ultra-Widescreen)",
        reference: { width: 768, height: 320 },
    },
    { id: "3:4", label: "3:4", reference: null },
    {
        id: "2:3",
        label: "2:3 (Semi-tall)",
        reference: { width: 416, height: 608 },
    },
    {
        id: "5:8",
        label: "5:8",
        reference: { width: 384, height: 608 },
    },
    {
        id: "9:16",
        label: "9:16 (Tall)",
        reference: { width: 384, height: 672 },
    },
    {
        id: "9:21",
        label: "9:21 (Ultra-Tall)",
        reference: { width: 320, height: 768 },
    },
];

const roundHalfToEven = (value: number): number => {
    const floor = Math.floor(value);
    const fraction = value - floor;
    if (Math.abs(fraction - 0.5) <= Number.EPSILON * Math.abs(value) * 2) {
        return floor % 2 === 0 ? floor : floor + 1;
    }
    return Math.round(value);
};

export const dimensionsFor = (
    ratioId: string,
    sideLength: number,
): WidthHeight | null => {
    const ratio = ASPECT_RATIOS.find((candidate) => candidate.id === ratioId);
    if (!ratio?.reference) {
        return null;
    }
    const side = Math.max(1, Number.isFinite(sideLength) ? sideLength : 1);
    return {
        width: roundHalfToEven((ratio.reference.width * side) / 512 / 16) * 16,
        height:
            roundHalfToEven((ratio.reference.height * side) / 512 / 16) * 16,
    };
};

const declaredRatioMatches = (
    ratioId: string,
    width: number,
    height: number,
): boolean => {
    const [numerator, denominator] = ratioId.split(":").map(Number);
    return (
        Number.isFinite(numerator) &&
        Number.isFinite(denominator) &&
        Math.abs(width * denominator - height * numerator) < 0.5
    );
};

export const sideLengthForDimensions = (
    ratioId: string,
    width: number,
    height: number,
): number => {
    const ratio = ASPECT_RATIOS.find((candidate) => candidate.id === ratioId);
    if (!ratio?.reference) {
        return Math.min(
            ROOT_DIMENSION_MAX,
            Math.max(
                ROOT_DIMENSION_MIN,
                Math.round(Math.sqrt(width * height) / ROOT_DIMENSION_STEP) *
                    ROOT_DIMENSION_STEP,
            ),
        );
    }
    const scale = Math.sqrt(
        (Math.max(1, width) * Math.max(1, height)) /
            (ratio.reference.width * ratio.reference.height),
    );
    return Math.min(
        ROOT_DIMENSION_MAX,
        Math.max(
            ROOT_DIMENSION_MIN,
            Math.round((scale * 512) / ROOT_DIMENSION_STEP) *
                ROOT_DIMENSION_STEP,
        ),
    );
};

export const matchAspectRatio = (
    width: number,
    height: number,
    multiple = ROOT_DIMENSION_STEP,
): string | null => {
    const roundedWidth = Math.round(width);
    const roundedHeight = Math.round(height);
    const exact = ASPECT_RATIOS.find((ratio) =>
        declaredRatioMatches(ratio.id, roundedWidth, roundedHeight),
    );
    if (exact) {
        return exact.id;
    }
    for (const ratio of ASPECT_RATIOS) {
        if (!ratio.reference) {
            continue;
        }
        const estimated = sideLengthForDimensions(
            ratio.id,
            roundedWidth,
            roundedHeight,
        );
        for (const offset of [-64, -32, 0, 32, 64]) {
            const raw = dimensionsFor(ratio.id, estimated + offset);
            if (!raw) {
                continue;
            }
            const snapped = snapDimensions(raw.width, raw.height, multiple);
            if (
                snapped.width === roundedWidth &&
                snapped.height === roundedHeight
            ) {
                return ratio.id;
            }
        }
    }
    return null;
};
