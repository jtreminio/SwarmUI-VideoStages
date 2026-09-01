import { visitAuthoringMedia } from "./authoringMedia";

const HANDLE_PREFIX = "vst-browser-media:";

interface SerializedMediaTransform {
    serialized: string;
    changed: boolean;
    missing: string[];
}

const assets = new Map<string, string>();
const handlesByData = new Map<string, string>();
let nextHandle = 0;

const createHandle = (): string => {
    const randomId = globalThis.crypto?.randomUUID?.();
    return `${HANDLE_PREFIX}${randomId ?? ++nextHandle}`;
};

export const isBrowserMediaHandle = (value: string): boolean =>
    value.startsWith(HANDLE_PREFIX);

export const containsBrowserMediaHandle = (value: string): boolean =>
    value.includes(HANDLE_PREFIX);

export const storeBrowserMedia = (data: string): string => {
    if (!data.startsWith("data:")) {
        return data;
    }
    const existing = handlesByData.get(data);
    if (existing) {
        return existing;
    }
    const handle = createHandle();
    assets.set(handle, data);
    handlesByData.set(data, handle);
    return handle;
};

export const resolveBrowserMedia = (value: string): string =>
    assets.get(value) ?? value;

const transformSerializedMedia = (
    serialized: string,
    transform: (
        data: string,
        fileName: string | null,
    ) => { data: string; missing?: boolean },
): SerializedMediaTransform => {
    let parsed: unknown;
    try {
        parsed = JSON.parse(serialized);
    } catch {
        return { serialized, changed: false, missing: [] };
    }
    let changed = false;
    const missing: string[] = [];
    visitAuthoringMedia(parsed, (media) => {
        if (!media.data) {
            return;
        }
        const fileName =
            typeof media.fileName === "string" ? media.fileName : null;
        const result = transform(media.data, fileName);
        if (result.missing) {
            missing.push(fileName ?? "unnamed media");
        }
        if (result.data !== media.data) {
            media.data = result.data;
            changed = true;
        }
    });
    return {
        serialized: changed ? JSON.stringify(parsed) : serialized,
        changed,
        missing,
    };
};

export const externalizeSerializedBrowserMedia = (
    serialized: string,
): SerializedMediaTransform =>
    transformSerializedMedia(serialized, (data) => ({
        data: storeBrowserMedia(data),
    }));

export const materializeSerializedBrowserMedia = (
    serialized: string,
): SerializedMediaTransform =>
    transformSerializedMedia(serialized, (data) => {
        if (!isBrowserMediaHandle(data)) {
            return { data };
        }
        const materialized = assets.get(data);
        return materialized
            ? { data: materialized }
            : { data: "", missing: true };
    });

export const resetBrowserMediaStoreForTests = (): void => {
    assets.clear();
    handlesByData.clear();
    nextHandle = 0;
};
