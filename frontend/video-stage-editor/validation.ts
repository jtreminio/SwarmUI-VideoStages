import {
    type Clip,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
} from "../Types";
import { parseBase2EditStageIndex } from "./constants";
import { isAvailableBase2EditReference } from "./swarmInputs";

export const getRefSourceError = (source: string): string | null => {
    const compact = `${source || ""}`.trim().replace(/\s+/g, "");
    if (
        compact === REF_SOURCE_BASE ||
        compact === REF_SOURCE_REFINER ||
        compact === REF_SOURCE_UPLOAD
    ) {
        return null;
    }
    if (parseBase2EditStageIndex(compact) == null) {
        return `has unknown source "${source}".`;
    }
    if (!isAvailableBase2EditReference(compact)) {
        return `references missing Base2Edit stage "${source}".`;
    }
    return null;
};

export const validateClips = (clips: Clip[]): string[] => {
    const errors: string[] = [];
    if (clips.length === 0) {
        errors.push("VideoStages requires at least one clip.");
        return errors;
    }

    for (let i = 0; i < clips.length; i++) {
        const clip = clips[i];
        if (clip.skipped) {
            continue;
        }
        const clipLabel = `VideoStages: ${clip.name || `Clip ${i}`}`;
        if (clip.stages.length === 0) {
            errors.push(`${clipLabel} requires at least one stage.`);
            continue;
        }

        for (let j = 0; j < clip.stages.length; j++) {
            const stage = clip.stages[j];
            if (stage.skipped) {
                continue;
            }
            const stageLabel = `${clipLabel}: Stage ${j}`;
            if (!stage.model) {
                errors.push(`${stageLabel} is missing a video model.`);
            }
            if (!stage.sampler) {
                errors.push(`${stageLabel} is missing a sampler.`);
            }
            if (!stage.scheduler) {
                errors.push(`${stageLabel} is missing a scheduler.`);
            }
        }

        for (let j = 0; j < clip.refs.length; j++) {
            const ref = clip.refs[j];
            const refLabel = `${clipLabel}: Reference ${j}`;
            const sourceError = getRefSourceError(ref.source);
            if (sourceError) {
                errors.push(`${refLabel} ${sourceError}`);
            }
        }
    }

    return errors;
};
