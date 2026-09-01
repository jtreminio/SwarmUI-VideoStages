import { afterEach, describe, expect, it } from "@jest/globals";
import {
    externalizeSerializedBrowserMedia,
    isBrowserMediaHandle,
    materializeSerializedBrowserMedia,
    resetBrowserMediaStoreForTests,
    resolveBrowserMedia,
    storeBrowserMedia,
} from "./browserMediaStore";

const IMAGE = "data:image/png;base64,REF_IMAGE";
const AUDIO = "data:audio/wav;base64,CLIP_AUDIO";
const VIDEO = "data:video/mp4;base64,INIT_VIDEO";

afterEach(resetBrowserMediaStoreForTests);

describe("browser media store", () => {
    it("keeps browser media out of every serialized upload container", () => {
        const source = JSON.stringify({
            clips: [
                {
                    uploadedAudio: { data: AUDIO, fileName: "voice.wav" },
                    initVideo: {
                        data: VIDEO,
                        fileName: "source.mp4",
                    },
                    references: [
                        {
                            uploadedMedia: {
                                data: IMAGE,
                                fileName: "subject.png",
                            },
                        },
                        {
                            uploadedMedia: {
                                data: "inputs/server-subject.png",
                                fileName: "server-subject.png",
                            },
                        },
                    ],
                    keyframes: [
                        {
                            uploadedImage: {
                                data: IMAGE,
                                fileName: "subject.png",
                            },
                        },
                    ],
                    frameRefs: [
                        {
                            uploadedImage: {
                                data: IMAGE,
                                fileName: "legacy-subject.png",
                            },
                        },
                    ],
                    icLoras: [
                        {
                            driveMedia: {
                                data: AUDIO,
                                fileName: "voice.wav",
                            },
                        },
                    ],
                },
            ],
            audioTracks: [
                {
                    source: {
                        uploadedAudio: {
                            data: AUDIO,
                            fileName: "voice.wav",
                        },
                    },
                },
            ],
        });

        const externalized = externalizeSerializedBrowserMedia(source);

        expect(externalized.changed).toBe(true);
        expect(externalized.serialized).not.toContain("REF_IMAGE");
        expect(externalized.serialized).not.toContain("CLIP_AUDIO");
        expect(externalized.serialized).not.toContain("INIT_VIDEO");
        expect(externalized.serialized).toContain("inputs/server-subject.png");

        const materialized = materializeSerializedBrowserMedia(
            externalized.serialized,
        );
        expect(materialized.missing).toEqual([]);
        expect(materialized.serialized).toContain("REF_IMAGE");
        expect(materialized.serialized).toContain("CLIP_AUDIO");
        expect(materialized.serialized).toContain("INIT_VIDEO");
        expect(materialized.serialized).toContain("inputs/server-subject.png");
    });

    it("deduplicates one payload while preserving a resolvable handle", () => {
        const first = storeBrowserMedia(IMAGE);
        const second = storeBrowserMedia(IMAGE);

        expect(first).toBe(second);
        expect(isBrowserMediaHandle(first)).toBe(true);
        expect(resolveBrowserMedia(first)).toBe(IMAGE);
    });

    it("reports a browser handle whose page-local payload is gone", () => {
        const handle = storeBrowserMedia(IMAGE);
        resetBrowserMediaStoreForTests();

        const result = materializeSerializedBrowserMedia(
            JSON.stringify({
                clips: [
                    {
                        references: [
                            {
                                uploadedMedia: {
                                    data: handle,
                                    fileName: "lost.png",
                                },
                            },
                        ],
                    },
                ],
            }),
        );

        expect(result.missing).toEqual(["lost.png"]);
        expect(result.serialized).not.toContain(handle);
    });
});
