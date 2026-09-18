import { MEDIA_SOURCE_INCOMING } from "../../generatedMediaSource";
import { defaultLoraWeight } from "../../loraAuthoring";
import {
    appendIcLoraStrengthToClip,
    removeIcLoraStrengthAt,
} from "../../normalizationStage";
import type { Clip, RootDefaults } from "../../types";
import { defaultIcLora } from "./icLoraNormalization";
import {
    findIcLoraPreset,
    IC_LORA_AUTO,
    IC_LORA_DEFAULT_PRESET_ID,
    icLoraDriveMediaContract,
} from "./icLoraPresets";

export const appendDefaultIcLora = (
    clip: Clip,
    defaults: RootDefaults,
): boolean => {
    const preset = findIcLoraPreset(IC_LORA_DEFAULT_PRESET_ID);
    if (!preset) {
        return false;
    }
    const contract = icLoraDriveMediaContract(preset);
    clip.icLoras.push(
        defaultIcLora({
            lora: IC_LORA_AUTO,
            preset: preset.id,
            driveSource: MEDIA_SOURCE_INCOMING,
            strength: preset.strength,
            controlType: preset.controlType,
            driveData: contract.driveData,
            driveMediaKinds: [...contract.acceptedKinds],
        }),
    );
    appendIcLoraStrengthToClip(clip, defaultLoraWeight(defaults, IC_LORA_AUTO));
    return true;
};

export const removeIcLoraEntryAt = (clip: Clip, index: number): boolean => {
    if (!clip.icLoras[index]) {
        return false;
    }
    clip.icLoras.splice(index, 1);
    removeIcLoraStrengthAt(clip, index);
    return true;
};
