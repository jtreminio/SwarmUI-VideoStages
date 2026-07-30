import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "../__test_helpers__/clipFixtures";
import {
    boundedReferencePositionHelp,
    referenceEndpointPolicy,
} from "./referenceEndpoints";

describe("referenceEndpointPolicy", () => {
    it("takes first support from the first stage and last support from the terminal generating stage", () => {
        const catalog = testArchitectureCatalog();
        const first = catalog.entries[0];
        first.enhancements = {
            extras: ["frame-references"],
            referencePositions: ["first"],
        };
        catalog.entries.push(
            {
                ...first,
                value: "terminal-with-last.safetensors",
                enhancements: {
                    extras: ["frame-references"],
                    referencePositions: ["last"],
                },
            },
            {
                ...first,
                value: "passthrough-without-last.safetensors",
                enhancements: {
                    extras: ["frame-references"],
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
            bounded: true,
            supportsFirst: true,
            supportsLast: true,
        });
    });

    it("does not borrow final-frame support from the first model", () => {
        const catalog = testArchitectureCatalog();
        const first = catalog.entries[0];
        first.enhancements = {
            extras: ["frame-references"],
            referencePositions: ["first", "last"],
        };
        catalog.entries.push({
            ...first,
            value: "terminal-first-only.safetensors",
            enhancements: {
                extras: ["frame-references"],
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
});
