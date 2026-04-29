const WAN_COMPAT_CLASS_IDS: ReadonlySet<string> = new Set([
    "wan-21",
    "wan-21-14b",
    "wan-21-1_3b",
    "wan-22-5b",
]);

const WAN_22_I2V_14B_MODEL_CLASS_ID = "wan-2_2-image2video-14b";

const getStableDiffusionModelClass = (modelValue: string) => {
    if (
        typeof modelsHelpers === "undefined" ||
        !modelsHelpers ||
        typeof modelsHelpers.getDataFor !== "function"
    ) {
        return undefined;
    }

    return modelsHelpers.getDataFor("Stable-Diffusion", modelValue)?.modelClass;
};

const getModelCompatClassId = (modelValue: string): string | null => {
    return getStableDiffusionModelClass(modelValue)?.compatClass?.id ?? null;
};

const getModelClassId = (modelValue: string): string | null => {
    return getStableDiffusionModelClass(modelValue)?.id ?? null;
};

const matchesKnownWanName = (modelValue: string): boolean => {
    const lower = modelValue.toLowerCase();
    return (
        lower.includes("wan-2_2-image2video-14b") ||
        lower.includes("wan22") ||
        lower.startsWith("wan-2_1-image2video") ||
        lower.startsWith("wan-2_1-text2video") ||
        lower.startsWith("wan-2_2-ti2v") ||
        lower.startsWith("wan-2_1-flf2v") ||
        lower.startsWith("wan-2_1-vace") ||
        lower.includes("wan-21-14b") ||
        lower.includes("wan-21-1_3b") ||
        lower.includes("wan-22-5b")
    );
};

export const clipHasWanStage = (clip: {
    stages: ReadonlyArray<{ skipped?: boolean; model: string }>;
}): boolean => {
    for (let i = 0; i < clip.stages.length; i++) {
        const stage = clip.stages[i];
        if (!stage.skipped && isWanVideoModelValue(stage.model)) {
            return true;
        }
    }
    return false;
};

export const isWanVideoModelValue = (modelValue: string): boolean => {
    const trimmed = `${modelValue ?? ""}`.trim();
    if (!trimmed) {
        return false;
    }

    const compatClassId = getModelCompatClassId(trimmed);
    if (compatClassId !== null && WAN_COMPAT_CLASS_IDS.has(compatClassId)) {
        return true;
    }

    const modelClassId = getModelClassId(trimmed);
    if (modelClassId === WAN_22_I2V_14B_MODEL_CLASS_ID) {
        return true;
    }

    return matchesKnownWanName(trimmed);
};

export const rawStageListContainsWanModel = (
    stagesRaw: ReadonlyArray<unknown>,
): boolean => {
    for (let i = 0; i < stagesRaw.length; i++) {
        const raw = stagesRaw[i];
        if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
            continue;
        }
        const rec = raw as Record<string, unknown>;
        if (rec.skipped) {
            continue;
        }
        const m = `${rec.model ?? ""}`.trim();
        if (m.length > 0 && isWanVideoModelValue(m)) {
            return true;
        }
    }
    return false;
};
