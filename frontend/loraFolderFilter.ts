export const ROOT_LORA_FOLDER = "";

const topLevelPathSegment = (
    value: string,
    rootWhenUnnested: boolean,
): string => {
    const normalized = `${value}`.trim().replaceAll("\\", "/");
    const separator = normalized.indexOf("/");
    if (separator < 0) {
        return rootWhenUnnested ? ROOT_LORA_FOLDER : normalized;
    }
    return normalized.slice(0, separator);
};

export const topLevelLoraFolder = (modelName: string): string =>
    topLevelPathSegment(modelName, true);

export const isLoraNoneOption = (value: string): boolean =>
    `${value}`.replace(/\s+/g, "").toLowerCase() === "(none)";

export const availableLoraFolders = (
    modelNames: readonly string[],
): string[] => {
    const folders = new Set<string>();
    for (const modelName of modelNames) {
        if (!isLoraNoneOption(modelName)) {
            folders.add(topLevelLoraFolder(modelName));
        }
    }
    return [...folders].sort((left, right) => {
        if (left === ROOT_LORA_FOLDER) return -1;
        if (right === ROOT_LORA_FOLDER) return 1;
        return left.localeCompare(right);
    });
};

export const normalizeLoraFolders = (value: unknown): string[] | null => {
    if (!Array.isArray(value)) {
        return null;
    }
    const folders: string[] = [];
    for (const raw of value) {
        if (typeof raw !== "string") {
            continue;
        }
        const folder = topLevelPathSegment(raw, false);
        if (!folders.includes(folder)) {
            folders.push(folder);
        }
    }
    return folders;
};

export const isLoraFolderIncluded = (
    modelName: string,
    includedFolders: ReadonlySet<string> | null,
): boolean =>
    includedFolders === null ||
    includedFolders.has(topLevelLoraFolder(modelName));
