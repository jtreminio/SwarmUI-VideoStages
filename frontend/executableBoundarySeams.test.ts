import { describe, expect, it } from "@jest/globals";

import { minimalClip } from "./__test_helpers__/clipFixtures";
import { executableBoundaries } from "./clipSemantics";
import type { Clip } from "./types";

// The first skipped clip truncates execution.
const clips = (): Clip[] => [
    minimalClip({ id: "a" }),
    minimalClip({ id: "b", skipped: true }),
    minimalClip({ id: "c" }),
];

describe("executable boundary seams", () => {
    it("truncates boundaries at the first skipped clip", () => {
        expect(executableBoundaries(clips())).toEqual([]);
    });
});
