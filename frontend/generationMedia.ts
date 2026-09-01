import {
    containsBrowserMediaHandle,
    materializeSerializedBrowserMedia,
} from "./browserMediaStore";
import { isRecord } from "./utils";

export const materializeGenerationMedia = (
    input: Record<string, unknown>,
): void => {
    const serialized = input.videostages;
    if (
        typeof serialized !== "string" ||
        !containsBrowserMediaHandle(serialized)
    ) {
        return;
    }
    const materialized = materializeSerializedBrowserMedia(serialized);
    if (materialized.missing.length > 0) {
        throw new Error(
            `Reselect browser-local VideoStages media: ${materialized.missing.join(", ")}.`,
        );
    }
    input.videostages = materialized.serialized;
};

export const materializeGenerationMediaValue = (input: unknown): void => {
    if (isRecord(input)) {
        materializeGenerationMedia(input);
    }
};
