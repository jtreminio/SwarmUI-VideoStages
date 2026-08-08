import { describe, expect, it } from "@jest/globals";

import { getRootGeneratedEntryMode } from "./swarmInputs";

describe("getRootGeneratedEntryMode", () => {
    it("takes an unmounted root model as text-to-video", () => {
        expect(getRootGeneratedEntryMode()).toBe("text-to-video");
    });
});
