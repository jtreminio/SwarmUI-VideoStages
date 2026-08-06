import { describe, expect, it } from "@jest/globals";
import {
    nextAllowedReferencePosition,
    nextAvailableReferenceFrame,
} from "./referenceAuthoring";

describe("nextAvailableReferenceFrame", () => {
    it("spaces preferred frames by rounded ten-percent increments", () => {
        const frameRefs: { frame: number; fromEnd: boolean }[] = [];
        const allocated: number[] = [];
        for (let index = 0; index < 11; index++) {
            const frame = nextAvailableReferenceFrame(frameRefs, 121);
            expect(frame).not.toBeNull();
            if (frame === null) {
                break;
            }
            allocated.push(frame);
            frameRefs.push({ frame, fromEnd: false });
        }

        expect(allocated).toEqual([
            1, 13, 25, 37, 49, 61, 73, 85, 97, 109, 121,
        ]);
    });

    it("wraps to the earliest unused frame after reaching the ceiling", () => {
        const occupied = [1, 13, 25, 37, 49, 61, 73, 85, 97, 109, 121].map(
            (frame) => ({ frame, fromEnd: false }),
        );

        expect(nextAvailableReferenceFrame(occupied, 121)).toBe(2);
    });

    it("treats from-end references as occupying their absolute frame", () => {
        expect(
            nextAvailableReferenceFrame(
                [
                    { frame: 1, fromEnd: false },
                    { frame: 109, fromEnd: true },
                ],
                121,
            ),
        ).toBe(25);
    });

    it("returns null when every frame is occupied", () => {
        expect(
            nextAvailableReferenceFrame(
                [
                    { frame: 1, fromEnd: false },
                    { frame: 2, fromEnd: false },
                    { frame: 3, fromEnd: false },
                ],
                3,
            ),
        ).toBeNull();
    });
});

describe("nextAllowedReferencePosition", () => {
    it("distinguishes unrestricted positions from no supported positions", () => {
        expect(nextAllowedReferencePosition([], 121, ["any"])).toEqual({
            frame: 1,
            fromEnd: false,
        });
        expect(nextAllowedReferencePosition([], 121, [])).toBeNull();
    });

    it("allocates only the advertised first and last positions", () => {
        expect(
            nextAllowedReferencePosition([], 121, ["first", "last"]),
        ).toEqual({ frame: 1, fromEnd: false });
        expect(
            nextAllowedReferencePosition([{ frame: 1, fromEnd: false }], 121, [
                "first",
                "last",
            ]),
        ).toEqual({ frame: 1, fromEnd: true });
        expect(
            nextAllowedReferencePosition(
                [
                    { frame: 1, fromEnd: false },
                    { frame: 1, fromEnd: true },
                ],
                121,
                ["first", "last"],
            ),
        ).toBeNull();
    });
});
