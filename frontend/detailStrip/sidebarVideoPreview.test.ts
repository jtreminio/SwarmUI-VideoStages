import { afterEach, describe, expect, it, jest } from "@jest/globals";

import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import { buildSidebarVideoPreview } from "./sidebarVideoPreview";

describe("sidebar video preview", () => {
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
            buildSidebarVideoPreview("data:video/mp4;base64,AAAA", {
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
            buildSidebarVideoPreview("data:video/mp4;base64,AAAA", range),
        );

        range.startSeconds = 20;
        range.lengthSeconds = 5;
        player.currentTime = 18;
        player.dispatchEvent(new Event("seeking"));

        expect(player.currentTime).toBe(20);
    });
});
