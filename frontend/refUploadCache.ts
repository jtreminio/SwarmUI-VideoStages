import {
    type CachedRefUpload,
    normalizeUploadFileName,
    parseRefUploadKey,
    refUploadKey,
} from "./constants";
import type { Clip } from "./Types";

export type RefUploadCacheApi = {
    get: (key: string) => CachedRefUpload | undefined;
    delete: (key: string) => void;
    reindexAfterClipDelete: (deletedClipIdx: number) => void;
    reindexAfterRefDelete: (clipIdx: number, deletedRefIdx: number) => void;
    restorePreviews: (editor: HTMLElement, clips: Clip[]) => void;
    cacheSelection: (args: {
        clipIdx: number;
        refIdx: number;
        fileInput: HTMLInputElement;
        getClips: () => Clip[];
        saveClips: (clips: Clip[]) => void;
    }) => void;
};

export const createRefUploadCache = (): RefUploadCacheApi => {
    let cache = new Map<string, CachedRefUpload>();

    const reindexAfterClipDelete = (deletedClipIdx: number): void => {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of cache.entries()) {
            const parsed = parseRefUploadKey(key);
            if (!parsed) {
                continue;
            }
            if (parsed.clipIdx === deletedClipIdx) {
                continue;
            }
            const clipIdx =
                parsed.clipIdx > deletedClipIdx
                    ? parsed.clipIdx - 1
                    : parsed.clipIdx;
            nextCache.set(refUploadKey(clipIdx, parsed.refIdx), cached);
        }
        cache = nextCache;
    };

    const reindexAfterRefDelete = (
        clipIdx: number,
        deletedRefIdx: number,
    ): void => {
        const nextCache = new Map<string, CachedRefUpload>();
        for (const [key, cached] of cache.entries()) {
            const parsed = parseRefUploadKey(key);
            if (!parsed) {
                continue;
            }
            if (parsed.clipIdx !== clipIdx) {
                nextCache.set(key, cached);
                continue;
            }
            if (parsed.refIdx === deletedRefIdx) {
                continue;
            }
            const refIdx =
                parsed.refIdx > deletedRefIdx
                    ? parsed.refIdx - 1
                    : parsed.refIdx;
            nextCache.set(refUploadKey(clipIdx, refIdx), cached);
        }
        cache = nextCache;
    };

    const restorePreviews = (editor: HTMLElement, clips: Clip[]): void => {
        const uploadInputs = editor.querySelectorAll(
            '.vs-ref-upload-field .auto-file[data-ref-field="uploadFileName"]',
        );
        for (const input of uploadInputs) {
            if (!(input instanceof HTMLInputElement)) {
                continue;
            }
            const clipIdx = parseInt(input.dataset.clipIdx ?? "-1", 10);
            const refIdx = parseInt(input.dataset.refIdx ?? "-1", 10);
            const persisted =
                clipIdx >= 0 && clipIdx < clips.length
                    ? clips[clipIdx].refs[refIdx]?.uploadedImage
                    : null;
            const cached = cache.get(refUploadKey(clipIdx, refIdx));
            const src = persisted?.data ?? cached?.src;
            const name = persisted?.fileName ?? cached?.name;
            if (!src) {
                continue;
            }
            setMediaFileDirect(
                input,
                src,
                "image",
                name ?? "Upload Image",
                name ?? undefined,
            );
        }
    };

    const cacheSelection = ({
        clipIdx,
        refIdx,
        fileInput,
        getClips,
        saveClips,
    }: {
        clipIdx: number;
        refIdx: number;
        fileInput: HTMLInputElement;
        getClips: () => Clip[];
        saveClips: (clips: Clip[]) => void;
    }): void => {
        const file = fileInput.files?.[0];
        const key = refUploadKey(clipIdx, refIdx);
        if (!file) {
            cache.delete(key);
            return;
        }

        const reader = new FileReader();
        reader.addEventListener("load", () => {
            if (typeof reader.result !== "string") {
                return;
            }
            cache.set(key, {
                src: reader.result,
                name: file.name,
            });
            const clips = getClips();
            if (clipIdx < 0 || clipIdx >= clips.length) {
                return;
            }
            const ref = clips[clipIdx].refs[refIdx];
            if (!ref) {
                return;
            }
            ref.uploadedImage = {
                data: reader.result,
                fileName: normalizeUploadFileName(file.name),
            };
            saveClips(clips);
        });
        reader.readAsDataURL(file);
    };

    return {
        get: (key: string) => cache.get(key),
        delete: (key: string) => {
            cache.delete(key);
        },
        reindexAfterClipDelete,
        reindexAfterRefDelete,
        restorePreviews,
        cacheSelection,
    };
};
