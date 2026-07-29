import type {
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalClip,
    CanonicalPromptWindow,
    CanonicalRefImage,
    CanonicalRetake,
    CanonicalStage,
    CanonicalVideoStagesConfig,
} from "../types";

/** Which entity owns the collection a list command addresses. */
export type ListOwnerKind = "document" | "clip" | "track";

/** The command field naming the owner; document-owned lists have none. */
export const OWNER_ID_FIELD = {
    document: null,
    clip: "clipId",
    track: "trackId",
} as const satisfies Record<ListOwnerKind, string | null>;

/**
 * Every key of an entity is either patchable or deliberately reserved for a
 * dedicated command (identity, architecture retargeting, child collections).
 * Leaving a key out of both is a compile error, so a new canonical field can
 * never be silently dropped by the diff.
 */
type Coverage<
    TEntity,
    TPatch extends keyof TEntity,
    TReserved extends keyof TEntity,
> = [Exclude<keyof TEntity, TPatch | TReserved>] extends [never]
    ? unknown
    : {
          readonly __unclassifiedKeys: Exclude<
              keyof TEntity,
              TPatch | TReserved
          >;
      };

export interface ListEntityDescriptor<
    TOwner,
    TEntity extends { id: string },
    TPatchKey extends keyof TEntity = keyof TEntity,
> {
    /** Command-type prefix: `${prefix}.add`, `${prefix}.patch`, … */
    readonly prefix: string;
    readonly owner: ListOwnerKind;
    /** Command field carrying a whole new entity on `.add`. */
    readonly entityField: string;
    /** Command field naming the entity on `.remove`/`.move`/`.patch`. */
    readonly idField: string;
    /** Command field naming the `.add`/`.move` insertion anchor. */
    readonly beforeIdField: string;
    /** Keys a `.patch` may carry, and the exact set the diff compares. */
    readonly patchKeys: readonly TPatchKey[];
    /** Keys owned by a dedicated command instead of `.patch`. */
    readonly reservedKeys: readonly (keyof TEntity)[];
    /** True when mutating the list can change the owning clip's identity. */
    readonly reconcilesClipIdentity?: boolean;
    collection(owner: TOwner): TEntity[];
}

const defineList =
    <TOwner, TEntity extends { id: string }>() =>
    <
        const TPatch extends readonly (keyof TEntity)[],
        const TReserved extends readonly (keyof TEntity)[],
    >(
        descriptor: Omit<
            ListEntityDescriptor<TOwner, TEntity>,
            "patchKeys" | "reservedKeys"
        > & { patchKeys: TPatch; reservedKeys: TReserved } & Coverage<
                TEntity,
                TPatch[number],
                TReserved[number]
            >,
    ): ListEntityDescriptor<TOwner, TEntity, TPatch[number]> =>
        descriptor;

/** The patch-only counterpart for entities that are a slot, not a list. */
const definePatchKeys =
    <TEntity>() =>
    <
        const TPatch extends readonly (keyof TEntity)[],
        const TReserved extends readonly (keyof TEntity)[],
    >(
        keys: { patchKeys: TPatch; reservedKeys: TReserved } & Coverage<
            TEntity,
            TPatch[number],
            TReserved[number]
        >,
    ): readonly TPatch[number][] =>
        keys.patchKeys;

export const ROOT_PATCH_KEYS = definePatchKeys<CanonicalVideoStagesConfig>()({
    patchKeys: ["schemaVersion", "width", "height", "fps", "dimsExplicit"],
    reservedKeys: ["clips", "audioTracks"],
});

export const RETAKE_PATCH_KEYS = definePatchKeys<CanonicalRetake>()({
    patchKeys: ["startSeconds", "lengthSeconds", "strength"],
    reservedKeys: ["id"],
});

const CLIP_ENTITY = defineList<CanonicalVideoStagesConfig, CanonicalClip>()({
    prefix: "clip",
    owner: "document",
    entityField: "clip",
    idField: "clipId",
    beforeIdField: "beforeClipId",
    patchKeys: [
        "skipped",
        "hue",
        "boundaryOut",
        "boundaryOutCarryAudio",
        "boundaryOutOverlap",
        "duration",
        "refFraming",
        "audioSource",
        "architecturePayload",
        "loras",
        "icLoras",
        "saveAudioTrack",
        "clipLengthFromAudio",
        "clipLengthFromControlNet",
        "reuseAudio",
        "uploadedAudio",
        "prompt",
        "sourceVideo",
    ],
    reservedKeys: [
        "id",
        "architecture",
        "modelProfileId",
        "promptWindows",
        "retake",
        "refs",
        "stages",
    ],
    collection: (document) => document.clips,
});

const STAGE_ENTITY = defineList<CanonicalClip, CanonicalStage>()({
    prefix: "stage",
    owner: "clip",
    entityField: "stage",
    idField: "stageId",
    beforeIdField: "beforeStageId",
    patchKeys: [
        "skipped",
        "control",
        "controlNetStrength",
        "icLoraStrengths",
        "loraWeights",
        "refStrengths",
        "upscale",
        "upscaleMethod",
        "steps",
        "cfgScale",
        "sampler",
        "scheduler",
    ],
    reservedKeys: ["id", "model", "modelProfileId"],
    reconcilesClipIdentity: true,
    collection: (clip) => clip.stages,
});

const REF_ENTITY = defineList<CanonicalClip, CanonicalRefImage>()({
    prefix: "ref",
    owner: "clip",
    entityField: "ref",
    idField: "refId",
    beforeIdField: "beforeRefId",
    patchKeys: [
        "source",
        "uploadFileName",
        "uploadedImage",
        "frame",
        "fromEnd",
    ],
    reservedKeys: ["id"],
    collection: (clip) => clip.refs,
});

const PROMPT_WINDOW_ENTITY = defineList<CanonicalClip, CanonicalPromptWindow>()(
    {
        prefix: "prompt-window",
        owner: "clip",
        entityField: "window",
        idField: "windowId",
        beforeIdField: "beforeWindowId",
        patchKeys: ["prompt", "start", "duration"],
        reservedKeys: ["id"],
        collection: (clip) => clip.promptWindows,
    },
);

const AUDIO_TRACK_ENTITY = defineList<
    CanonicalVideoStagesConfig,
    CanonicalAudioTrack
>()({
    prefix: "audio-track",
    owner: "document",
    entityField: "track",
    idField: "trackId",
    beforeIdField: "beforeTrackId",
    patchKeys: ["source", "volume"],
    reservedKeys: ["id", "spans"],
    collection: (document) => document.audioTracks,
});

const AUDIO_SPAN_ENTITY = defineList<
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan
>()({
    prefix: "audio-span",
    owner: "track",
    entityField: "span",
    idField: "spanId",
    beforeIdField: "beforeSpanId",
    patchKeys: [
        "timelineStartSeconds",
        "timelineLengthSeconds",
        "sourceStartSeconds",
    ],
    reservedKeys: ["id"],
    collection: (track) => track.spans,
});

/** The one description of every ID-addressed list the commands can edit. */
export const LIST_ENTITIES = {
    clip: CLIP_ENTITY,
    stage: STAGE_ENTITY,
    ref: REF_ENTITY,
    promptWindow: PROMPT_WINDOW_ENTITY,
    audioTrack: AUDIO_TRACK_ENTITY,
    audioSpan: AUDIO_SPAN_ENTITY,
} as const;

type PatchOf<D> =
    D extends ListEntityDescriptor<infer _Owner, infer TEntity, infer TPatchKey>
        ? Partial<Pick<TEntity, TPatchKey>>
        : never;

export type ClipPatch = PatchOf<typeof CLIP_ENTITY>;
export type StagePatch = PatchOf<typeof STAGE_ENTITY>;
export type RefPatch = PatchOf<typeof REF_ENTITY>;
export type PromptWindowPatch = PatchOf<typeof PROMPT_WINDOW_ENTITY>;
export type AudioTrackPatch = PatchOf<typeof AUDIO_TRACK_ENTITY>;
export type AudioSpanPatch = PatchOf<typeof AUDIO_SPAN_ENTITY>;
export type RootSettingsPatch = Partial<
    Pick<CanonicalVideoStagesConfig, (typeof ROOT_PATCH_KEYS)[number]>
>;
export type RetakePatch = Partial<
    Pick<CanonicalRetake, (typeof RETAKE_PATCH_KEYS)[number]>
>;
