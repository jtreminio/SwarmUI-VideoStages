import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    getTimelineAuthoringSettings,
    setTimelineAuthoringSetting,
} from "./timelineAuthoringSettings";
import { snapMovedStart, snapPoint, timelineClipEdges } from "./timelineSnap";
import type { Clip } from "./types";

describe("timeline authoring settings", () => {
    beforeEach(() => localStorage.clear());

    it("defaults authoring preferences and persists changes", () => {
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: true,
            autoCollapse: true,
            dimensionSnap: "disabled",
        });
        setTimelineAuthoringSetting("snap", false);
        setTimelineAuthoringSetting("dimensionSnap", 64);
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: false,
            autoCollapse: true,
            dimensionSnap: 64,
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
        });
        localStorage.setItem("videostages.timeline.authoringSettings", "{bad");
        expect(getTimelineAuthoringSettings()).toEqual({
            snap: true,
            autoCollapse: true,
            dimensionSnap: "disabled",
        });
    });
});

describe("timeline snapping geometry", () => {
    it("aligns the nearest moved edge and prioritizes primary targets", () => {
        expect(snapMovedStart(2.1, 2, [2, 4], [3], 0.2)).toBe(2);
        expect(snapMovedStart(2.9, 2, [], [3], 0.2)).toBe(3);
        expect(snapMovedStart(1.1, 1.95, [], [3], 0.1)).toBe(1.05);
    });

    it("snaps a point and derives every cumulative clip edge", () => {
        expect(snapPoint(3.08, [], [0, 3, 7], 0.1)).toBe(3);
        expect(
            timelineClipEdges([{ duration: 3 }, { duration: 4 }] as Clip[]),
        ).toEqual([0, 3, 7]);
    });
});
