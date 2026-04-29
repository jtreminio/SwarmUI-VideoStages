const LTXV2_COMPAT_CLASS_ID = "lightricks-ltx-video-2";

const getModelCompatClassId = (modelValue: string): string | null => {
    if (
        typeof modelsHelpers === "undefined" ||
        !modelsHelpers ||
        typeof modelsHelpers.getDataFor !== "function"
    ) {
        return null;
    }

    return (
        modelsHelpers.getDataFor("Stable-Diffusion", modelValue)?.modelClass
            ?.compatClass?.id ?? null
    );
};

const matchesKnownLtxV2Name = (modelValue: string): boolean =>
    modelValue.startsWith("ltx-") ||
    modelValue.startsWith("ltxv2") ||
    modelValue.includes(LTXV2_COMPAT_CLASS_ID);

export const isLtxVideoModelValue = (modelValue: string): boolean => {
    const trimmed = `${modelValue ?? ""}`.trim();
    if (!trimmed) {
        return false;
    }

    const compatClassId = getModelCompatClassId(trimmed);

    if (compatClassId !== null) {
        return compatClassId === LTXV2_COMPAT_CLASS_ID;
    }

    // only tests arrive here
    return matchesKnownLtxV2Name(trimmed.toLowerCase());
};
