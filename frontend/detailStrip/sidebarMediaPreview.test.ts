import { afterEach, describe, expect, it, jest } from "@jest/globals";

import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { buildSidebarMediaPreview } from "./sidebarMediaPreview";

describe("sidebar media preview", () => {
    afterEach(() => {
        setVideoStagesHostBridgeForTests(null);
        document.body.innerHTML = "";
    });

    it("keeps seeking and playback inside the selected range", () => {
        const player = document.createElement("video");
        player.pause = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            createInitVideoElement: () => player,
        });
        document.body.appendChild(
            buildSidebarMediaPreview("video", "data:video/mp4;base64,AAAA", {
                startSeconds: 5,
                lengthSeconds: 10,
            }),
        );

        player.dispatchEvent(new Event("loadedmetadata"));
        expect(player.currentTime).toBe(5);

        player.currentTime = 3;
        player.dispatchEvent(new Event("seeking"));
        expect(player.currentTime).toBe(5);

        player.currentTime = 18;
        player.dispatchEvent(new Event("seeking"));
        expect(player.currentTime).toBe(5);

        player.currentTime = 15;
        player.dispatchEvent(new Event("timeupdate"));
        expect(player.pause).toHaveBeenCalledTimes(1);
        expect(player.currentTime).toBe(5);
    });

    it("uses the latest selected range after sidebar edits", () => {
        const player = document.createElement("video");
        player.pause = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            createInitVideoElement: () => player,
        });
        const range = { startSeconds: 5, lengthSeconds: 10 };
        document.body.appendChild(
            buildSidebarMediaPreview(
                "video",
                "data:video/mp4;base64,AAAA",
                range,
            ),
        );

        range.startSeconds = 20;
        range.lengthSeconds = 5;
        player.currentTime = 18;
        player.dispatchEvent(new Event("seeking"));

        expect(player.currentTime).toBe(20);
    });

    it("uses the requested audio UI for a video-container upload", () => {
        const preview = buildSidebarMediaPreview(
            "audio",
            "data:video/mp4;base64,AAAA",
            { startSeconds: 4.1, lengthSeconds: 4 },
        );

        expect(
            preview.querySelector(".vst-sidebar-audio-preview")?.tagName,
        ).toBe("AUDIO");
    });

    it.each([
        "audio",
        "video",
    ] as const)("shows an inline error when the %s cannot be previewed", (mediaKind) => {
        const preview = buildSidebarMediaPreview(
            mediaKind,
            "data:application/octet-stream;base64,AAAA",
            { startSeconds: 0, lengthSeconds: 1 },
        );
        const player = preview.querySelector<HTMLMediaElement>(
            ".vst-sidebar-media-preview",
        );
        const error = preview.querySelector<HTMLElement>(
            ".vst-sidebar-media-preview-error",
        );

        expect(error?.hidden).toBe(true);
        player?.dispatchEvent(new Event("error"));

        expect(player?.hidden).toBe(true);
        expect(error?.hidden).toBe(false);
        expect(error?.textContent).toContain(mediaKind);
    });
});
