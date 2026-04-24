import { describe, expect, it } from "@jest/globals";
import { VideoStageUtils } from "./UtilsTemp";

describe("VideoStageUtils", () => {
    describe("getInputElement", () => {
        it("returns null when the element does not exist", () => {
            expect(VideoStageUtils.getInputElement("missing-id")).toBeNull();
        });

        it("returns the matching HTMLInputElement when present", () => {
            const input = document.createElement("input");
            input.id = "video-stage-utils-test-input";
            document.body.appendChild(input);
            try {
                expect(
                    VideoStageUtils.getInputElement(
                        "video-stage-utils-test-input",
                    ),
                ).toBe(input);
            } finally {
                input.remove();
            }
        });
    });

    describe("getSelectValues / getSelectLabels", () => {
        it("returns empty arrays for null", () => {
            expect(VideoStageUtils.getSelectValues(null)).toEqual([]);
            expect(VideoStageUtils.getSelectLabels(null)).toEqual([]);
        });

        it("collects values and labels from a select element", () => {
            const select = document.createElement("select");
            for (const [value, label] of [
                ["a", "Alpha"],
                ["b", "Beta"],
            ]) {
                const option = document.createElement("option");
                option.value = value;
                option.label = label;
                option.text = label;
                select.appendChild(option);
            }
            expect(VideoStageUtils.getSelectValues(select)).toEqual(["a", "b"]);
            expect(VideoStageUtils.getSelectLabels(select)).toEqual([
                "Alpha",
                "Beta",
            ]);
        });
    });

    describe("toNumber", () => {
        it("parses numeric strings", () => {
            expect(VideoStageUtils.toNumber("42", 0)).toBe(42);
            expect(VideoStageUtils.toNumber("3.14", 0)).toBeCloseTo(3.14);
        });

        it("falls back when the value cannot be parsed", () => {
            expect(VideoStageUtils.toNumber("not a number", 7)).toBe(7);
            expect(VideoStageUtils.toNumber(undefined, 13)).toBe(13);
        });
    });
});
