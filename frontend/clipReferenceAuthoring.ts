import { MEDIA_SOURCE_UPLOAD } from "./generatedMediaSource";
import {
    REFERENCE_SCALE_FULL,
    REFERENCE_SCALES,
} from "./generatedReferenceScale";
import type { ClipReference, ClipReferenceKind } from "./types";

/**
 * Everything that varies per reference kind. `tag` is the name the model
 * presents the reference under, which the prompt has to use to address it.
 */
export const CLIP_REFERENCE_KIND_INFO = {
    image: {
        label: "Image",
        tag: "Picture",
        accept: "image/*",
        browserTypes: ["image"],
    },
    video: {
        label: "Video",
        tag: "Video",
        accept: "video/*",
        browserTypes: ["video"],
    },
    audio: {
        label: "Audio",
        tag: "Audio",
        accept: "audio/*",
        browserTypes: ["audio"],
    },
} as const satisfies Record<
    ClipReferenceKind,
    { label: string; tag: string; accept: string; browserTypes: string[] }
>;

export const CLIP_REFERENCE_KINDS = ["image", "video", "audio"] as const;

const CLIP_REFERENCE_SCALE_LABELS = {
    1: "Full",
    0.5: "Half",
    0.25: "Quarter",
} satisfies Record<(typeof REFERENCE_SCALES)[number], string>;

/**
 * How much of a video reference's resolution to keep. The model fits every
 * reference video onto its own 32-aligned canvas, so a smaller input simply
 * costs fewer reference tokens — which are re-encoded on every sampling step.
 */
export const CLIP_REFERENCE_SCALES: readonly {
    value: (typeof REFERENCE_SCALES)[number];
    label: string;
}[] = REFERENCE_SCALES.map((value) => ({
    value,
    label: CLIP_REFERENCE_SCALE_LABELS[value],
}));

export const normalizeClipReferenceScale = (value: unknown): number => {
    const numeric = Number(value);
    return REFERENCE_SCALES.some((scale) => scale === numeric)
        ? numeric
        : REFERENCE_SCALE_FULL;
};

export const normalizeClipReferenceKind = (
    value: unknown,
): ClipReferenceKind => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "video" || raw === "audio" ? raw : "image";
};

export const buildDefaultClipReference = (
    kind: ClipReferenceKind = "image",
): ClipReference => ({
    kind,
    source: MEDIA_SOURCE_UPLOAD,
    uploadedMedia: null,
    includeSoundtrack: false,
    mediaDurationSeconds: 0,
    drivesClipLength: false,
    startSeconds: 0,
    lengthSeconds: 0,
    mediaScale: REFERENCE_SCALE_FULL,
});

export const clipReferenceCanDriveLength = (
    reference: Pick<ClipReference, "kind">,
): boolean => reference.kind === "video" || reference.kind === "audio";

/**
 * `<Picture 1>`-style prompt tags, aligned by index with the authored list.
 *
 * The model presents references in a fixed order — every image, then every
 * video, then every standalone audio — and numbers each kind as it goes. An
 * included soundtrack is presented as its own audio item just before its video,
 * so it consumes an audio ordinal ahead of all of them.
 */
export const clipReferenceTags = (
    references: readonly Pick<ClipReference, "kind" | "includeSoundtrack">[],
    precedingReferences: readonly Pick<
        ClipReference,
        "kind" | "includeSoundtrack"
    >[] = [],
): string[] => {
    const allReferences = [...precedingReferences, ...references];
    const used: Record<ClipReferenceKind, number> = {
        image: 0,
        video: 0,
        audio: allReferences.filter(
            (reference) =>
                reference.kind === "video" &&
                reference.includeSoundtrack === true,
        ).length,
    };
    for (const reference of precedingReferences) {
        used[reference.kind] += 1;
    }
    return references.map((reference) => {
        used[reference.kind] += 1;
        return `<${CLIP_REFERENCE_KIND_INFO[reference.kind].tag} ${used[reference.kind]}>`;
    });
};

export const clipLengthReferenceIndex = (
    references: readonly Pick<
        ClipReference,
        "kind" | "drivesClipLength" | "mediaDurationSeconds"
    >[],
): number =>
    references.findIndex(
        (reference) =>
            reference.drivesClipLength === true &&
            clipReferenceCanDriveLength(reference) &&
            reference.mediaDurationSeconds > 0,
    );
