import { CLIP_REFERENCE_KIND_INFO } from "./clipReferenceAuthoring";
import { fileAsDataUri } from "./fileDataUri";
import { probeMediaDurationSeconds, probeReferenceVideo } from "./mediaProbe";
import type { ClipReference, ClipReferenceKind } from "./types";
import { roundToTenth } from "./utils";

type DroppedReferenceMedia = Pick<
    ClipReference,
    "kind" | "uploadedMedia" | "mediaDurationSeconds"
>;

const EXTENSION_KINDS: Record<string, ClipReferenceKind> = Object.fromEntries(
    Object.entries({
        image: [
            "avif",
            "bmp",
            "gif",
            "heic",
            "heif",
            "jpeg",
            "jpg",
            "png",
            "svg",
            "tif",
            "tiff",
            "webp",
        ],
        video: [
            "3g2",
            "3gp",
            "avi",
            "m4v",
            "mkv",
            "mov",
            "mp4",
            "mpeg",
            "mpg",
            "ogv",
            "webm",
        ],
        audio: [
            "aac",
            "flac",
            "m4a",
            "mp3",
            "oga",
            "ogg",
            "opus",
            "wav",
            "weba",
        ],
    } satisfies Record<ClipReferenceKind, string[]>).flatMap(
        ([kind, extensions]) =>
            extensions.map((extension) => [extension, kind]),
    ),
) as Record<string, ClipReferenceKind>;

const droppedReferenceKindHint = (
    file: Pick<File, "name" | "type">,
): ClipReferenceKind | null => {
    const mimeKind = file.type.split("/", 1)[0].toLowerCase();
    if (mimeKind in CLIP_REFERENCE_KIND_INFO) {
        return mimeKind as ClipReferenceKind;
    }
    const extension = file.name.toLowerCase().match(/\.([^.]+)$/)?.[1] ?? "";
    return EXTENSION_KINDS[extension] ?? null;
};

export const readDroppedReferenceMedia = async (
    file: File,
): Promise<DroppedReferenceMedia | null> => {
    const hintedKind = droppedReferenceKindHint(file);
    if (!hintedKind) {
        return null;
    }
    const data = await fileAsDataUri(file);
    if (!data) {
        return null;
    }
    const uploadedMedia = { data, fileName: file.name };
    if (hintedKind === "image") {
        return { kind: "image", uploadedMedia, mediaDurationSeconds: 0 };
    }
    if (hintedKind === "audio") {
        return {
            kind: "audio",
            uploadedMedia,
            mediaDurationSeconds: roundToTenth(
                await probeMediaDurationSeconds(data),
            ),
        };
    }
    const video = await probeReferenceVideo(data);
    return {
        kind: video?.hasMultipleFrames === false ? "image" : "video",
        uploadedMedia,
        mediaDurationSeconds:
            video?.hasMultipleFrames === false
                ? 0
                : roundToTenth(video?.durationSeconds ?? 0),
    };
};
