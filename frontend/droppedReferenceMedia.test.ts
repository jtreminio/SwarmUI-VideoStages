import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { readDroppedReferenceMedia } from "./droppedReferenceMedia";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";

const stubVideo = (duration: number, lastMediaTime?: number): void => {
    const video = document.createElement("video") as HTMLVideoElement & {
        requestVideoFrameCallback?: (
            callback: (now: number, metadata: { mediaTime: number }) => void,
        ) => number;
    };
    Object.defineProperty(video, "duration", { value: duration });
    video.pause = jest.fn();
    video.load = jest.fn();
    video.play = jest.fn(() => Promise.resolve());
    if (lastMediaTime !== undefined) {
        let callbackIndex = 0;
        video.requestVideoFrameCallback = (callback) => {
            const mediaTime = callbackIndex++ === 0 ? 0 : lastMediaTime;
            queueMicrotask(() =>
                callback(0, { mediaTime } as VideoFrameCallbackMetadata),
            );
            return callbackIndex;
        };
    }
    setVideoStagesHostBridgeForTests({
        ...createDefaultVideoStagesHostBridge(),
        createInitVideoElement: () => {
            queueMicrotask(() =>
                video.dispatchEvent(new Event("loadedmetadata")),
            );
            return video;
        },
    });
};

describe("dropped reference media", () => {
    afterEach(() => setVideoStagesHostBridgeForTests(null));

    it("keeps image containers, including WebP, as images", async () => {
        await expect(
            readDroppedReferenceMedia(
                new File(["webp"], "still.webp", { type: "image/webp" }),
            ),
        ).resolves.toMatchObject({
            kind: "image",
            mediaDurationSeconds: 0,
            uploadedMedia: { fileName: "still.webp" },
        });
    });

    it("treats a one-frame video container as an image", async () => {
        stubVideo(2, 0);

        await expect(
            readDroppedReferenceMedia(
                new File(["mp4"], "still.mp4", { type: "video/mp4" }),
            ),
        ).resolves.toMatchObject({
            kind: "image",
            mediaDurationSeconds: 0,
        });
    });

    it("keeps multi-frame video and audio as timed references", async () => {
        stubVideo(2.46, 2.4);
        await expect(
            readDroppedReferenceMedia(
                new File(["mp4"], "motion.mp4", { type: "video/mp4" }),
            ),
        ).resolves.toMatchObject({
            kind: "video",
            mediaDurationSeconds: 2.5,
        });

        stubVideo(3.24);
        await expect(
            readDroppedReferenceMedia(
                new File(["wav"], "sound.wav", { type: "audio/wav" }),
            ),
        ).resolves.toMatchObject({
            kind: "audio",
            mediaDurationSeconds: 3.2,
        });
    });
});
