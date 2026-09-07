import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "./timelineAuthoringSettings";

describe("timeline authoring settings", () => {
    beforeEach(() => localStorage.clear());

    it("defaults authoring preferences and persists changes", () => {
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: true,
            autoCollapse: true,
            dimensionSnap: "disabled",
            loraFolders: null,
        });
        setTimelineAuthoringSetting("snap", false);
        setTimelineAuthoringSetting("dimensionSnap", 64);
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: false,
            autoCollapse: true,
            dimensionSnap: 64,
            loraFolders: null,
        });
    });

    it("recovers missing or corrupt values with defaults", () => {
        localStorage.setItem(
            "videostages.timeline.authoringSettings",
            '{"snap":false}',
        );
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: false,
            autoCollapse: true,
            dimensionSnap: "disabled",
            loraFolders: null,
        });
        localStorage.setItem("videostages.timeline.authoringSettings", "{bad");
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: true,
            autoCollapse: true,
            dimensionSnap: "disabled",
            loraFolders: null,
        });
    });

    it("normalizes persisted LoRA folder selections", () => {
        localStorage.setItem(
            "videostages.timeline.authoringSettings",
            JSON.stringify({
                loraFolders: ["styles", " styles ", "", 7, "motion"],
            }),
        );

        expect(getTimelineAuthoringSettings().loraFolders).toEqual([
            "styles",
            "",
            "motion",
        ]);
    });
});
