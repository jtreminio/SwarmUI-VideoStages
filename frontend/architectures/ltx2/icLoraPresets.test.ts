import { describe, expect, it } from "@jest/globals";
import {
    DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
    findIcLoraPreset,
    icLoraDriveMediaContract,
} from "./icLoraPresets";

describe("LTX IC-LoRA drive-media contracts", () => {
    it("keeps Custom and unknown presets on visual image/video media", () => {
        expect(icLoraDriveMediaContract(null)).toEqual(
            DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
        );
        expect(icLoraDriveMediaContract(findIcLoraPreset("unknown"))).toEqual(
            DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
        );
    });

    it("makes LipDub consume audio from audio or video media and keep clip-entry visuals", () => {
        expect(icLoraDriveMediaContract(findIcLoraPreset("lipdub"))).toEqual({
            acceptedKinds: ["audio", "video"],
            consumes: "audio",
            visualSource: "clip-entry",
            requiresUpload: true,
        });
    });
});
