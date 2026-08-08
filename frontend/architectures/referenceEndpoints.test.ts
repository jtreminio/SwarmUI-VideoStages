import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "../__test_helpers__/clipFixtures";
import {
    boundedReferencePositionHelp,
    boundedReferenceToggleHelp,
    referenceEndpointPolicy,
} from "./referenceEndpoints";

describe("referenceEndpointPolicy", () => {
    it("takes first support from the first stage and last support from the terminal generating stage", () => {
        const catalog = testArchitectureCatalog();
        const first = catalog.entries[0];
        first.enhancements = {
            referencePositions: ["first"],
        };
        catalog.entries.push(
            {
                ...first,
                value: "terminal-with-last.safetensors",
                enhancements: {
                    referencePositions: ["last"],
                },
            },
            {
                ...first,
                value: "passthrough-without-last.safetensors",
                enhancements: {
                    referencePositions: ["first"],
                },
            },
        );
        const clip = minimalClip({
            stages: [
                minimalStage({ model: first.value }),
                minimalStage({
                    model: "terminal-with-last.safetensors",
                    control: 0.5,
                }),
                minimalStage({
                    model: "passthrough-without-last.safetensors",
                    control: 0,
                }),
            ],
        });

        expect(referenceEndpointPolicy(clip, catalog)).toEqual({
            positions: ["first", "last"],
            available: true,
            bounded: true,
            supportsFirst: true,
            supportsLast: true,
        });
    });

    it("does not borrow final-frame support from the first model", () => {
        const catalog = testArchitectureCatalog();
        const first = catalog.entries[0];
        first.enhancements = {
            referencePositions: ["first", "last"],
        };
        catalog.entries.push({
            ...first,
            value: "terminal-first-only.safetensors",
            enhancements: {
                referencePositions: ["first"],
            },
        });
        const clip = minimalClip({
            stages: [
                minimalStage({ model: first.value }),
                minimalStage({
                    model: "terminal-first-only.safetensors",
                    control: 0.5,
                }),
            ],
        });

        const policy = referenceEndpointPolicy(clip, catalog);
        expect(policy.positions).toEqual(["first"]);
        expect(boundedReferencePositionHelp(policy)).toBe(
            "This clip accepts an image only at the first frame.",
        );
    });

    it("treats an empty published position set as unavailable, not unrestricted", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries[0].enhancements = { referencePositions: [] };
        const policy = referenceEndpointPolicy(minimalClip(), catalog);

        expect(policy).toEqual({
            positions: [],
            available: false,
            bounded: false,
            supportsFirst: false,
            supportsLast: false,
        });
        expect(boundedReferencePositionHelp(policy)).toBe(
            "This clip does not accept keyframe endpoints.",
        );
        expect(boundedReferenceToggleHelp(policy)).toBe(
            "This clip does not publish a supported keyframe endpoint.",
        );
    });
});
