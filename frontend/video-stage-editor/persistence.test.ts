import { describe, expect, it } from "@jest/globals";
import type { Clip } from "../Types";
import { serializeClipsForStorage } from "./persistence";

describe("persistence", () => {
    describe("serializeClipsForStorage", () => {
        it("serializes clip tree with stable keys", () => {
            const clips: Clip[] = [
                {
                    name: "A",
                    expanded: true,
                    skipped: false,
                    duration: 3,
                    audioSource: "Native",
                    uploadedAudio: null,
                    refs: [
                        {
                            expanded: true,
                            source: "Base",
                            uploadFileName: null,
                            uploadedImage: null,
                            frame: 2,
                            fromEnd: true,
                        },
                    ],
                    stages: [
                        {
                            expanded: true,
                            skipped: false,
                            control: 1,
                            refStrengths: [0.8],
                            upscale: 1,
                            upscaleMethod: "pixel-lanczos",
                            model: "m",
                            vae: "v",
                            steps: 8,
                            cfgScale: 1,
                            sampler: "euler",
                            scheduler: "normal",
                        },
                    ],
                },
            ];
            const raw = serializeClipsForStorage(clips);
            expect(raw).toHaveLength(1);
            const row = raw[0] as Record<string, unknown>;
            expect(row.name).toBe("A");
            expect(Array.isArray(row.refs)).toBe(true);
            expect(Array.isArray(row.stages)).toBe(true);
            const stage = (row.stages as unknown[])[0] as Record<
                string,
                unknown
            >;
            expect(stage.refStrengths).toEqual([0.8]);
        });
    });
});
