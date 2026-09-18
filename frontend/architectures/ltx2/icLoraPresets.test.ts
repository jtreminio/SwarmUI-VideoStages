import { describe, expect, it } from "@jest/globals";
import {
    DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
    findIcLoraPreset,
    icLoraDriveMediaContract,
    icLoraDriveMediaContractForData,
} from "./icLoraPresets";

describe("LTX IC-LoRA presets", () => {
    it("uses the pre-scaled Water Simulation strength", () => {
        expect(findIcLoraPreset("water-simulation")?.strength).toBe(1);
    });

    it("keeps Custom and unknown presets on visual image/video media", () => {
        expect(icLoraDriveMediaContract(null)).toEqual(
            DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
        );
        expect(icLoraDriveMediaContract(findIcLoraPreset("unknown"))).toEqual(
            DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
        );
    });

    it("accepts audio or video for audio-consuming LoRAs", () => {
        expect(icLoraDriveMediaContractForData("audio")).toEqual({
            acceptedKinds: ["audio", "video"],
            driveData: "audio",
        });
    });

    it("makes Detailer a model-only refinement patch", () => {
        const detailer = findIcLoraPreset("detailer");
        expect(detailer).toMatchObject({
            displayName: "Detailer",
            strength: 1,
            controlType: "none",
        });
        expect(icLoraDriveMediaContract(detailer)).toEqual({
            acceptedKinds: [],
            driveData: "none",
        });
    });
});
