import { isRecord } from "./utils";

export type AuthoringMediaRecord = Record<string, unknown> & { data: string };

type ReplaceMedia = (value: unknown) => void;

export const visitAuthoringMedia = (
    root: unknown,
    visitor: (media: AuthoringMediaRecord, replace: ReplaceMedia) => void,
): void => {
    const visit = (value: unknown, replace: ReplaceMedia): void => {
        if (Array.isArray(value)) {
            value.forEach((entry, index) => {
                visit(entry, (next) => {
                    value[index] = next;
                });
            });
            return;
        }
        if (!isRecord(value)) {
            return;
        }
        let current: unknown = value;
        if (typeof value.data === "string") {
            visitor(value as AuthoringMediaRecord, (next) => {
                current = next;
                replace(next);
            });
        }
        if (!isRecord(current)) {
            return;
        }
        const record = current;
        for (const key of Object.keys(record)) {
            visit(record[key], (next) => {
                record[key] = next;
            });
        }
    };

    visit(root, () => {});
};
