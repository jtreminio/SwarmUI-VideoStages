import { storeBrowserMedia } from "../browserMediaStore";
import type { DetailSelectionDomainOperations } from "../detailStrip/selectionDomainOperations";
import { readDroppedReferenceMedia } from "../droppedReferenceMedia";
import { fileAsDataUri } from "../fileDataUri";
import { collectDroppedFiles, hasDroppedFiles } from "../fileDrop";
import { getVideoStagesHostBridge } from "../host";

export const bindReferenceDrop = (
    button: HTMLButtonElement,
    clipId: string,
    kind: "keyframe" | "clip-reference",
    operations: Pick<
        DetailSelectionDomainOperations,
        "addRefEntries" | "addClipReferences"
    >,
): void => {
    const addFiles = async (transfer: DataTransfer): Promise<void> => {
        const files = await collectDroppedFiles(transfer);
        if (kind === "keyframe") {
            const uploads = await Promise.all(
                files.map(async (file) => {
                    const data = await fileAsDataUri(file);
                    return data
                        ? { data: storeBrowserMedia(data), fileName: file.name }
                        : null;
                }),
            );
            const added = operations.addRefEntries(
                clipId,
                uploads.flatMap((upload) => (upload ? [upload] : [])),
            );
            if (added < files.length) {
                const skipped = files.length - added;
                getVideoStagesHostBridge().showError(
                    `Added ${added} of ${files.length} keyframes. ` +
                        `${skipped} ${skipped === 1 ? "file" : "files"} could not be read or no supported keyframe position remained.`,
                );
            }
            return;
        }
        const results = await Promise.all(
            files.map(async (file) => ({
                file,
                media: await readDroppedReferenceMedia(file).catch(() => null),
            })),
        );
        const references = results.flatMap(({ media }) =>
            media ? [media] : [],
        );
        const added = operations.addClipReferences(clipId, references);
        if (added < references.length) {
            getVideoStagesHostBridge().showError(
                "This architecture accepts only image references for identity verification.",
            );
        }
        const skipped = results
            .filter(({ media }) => !media)
            .map(({ file }) => file.name);
        if (skipped.length > 0) {
            getVideoStagesHostBridge().showError(
                `Unsupported reference files: ${skipped.join(", ")}`,
            );
        }
    };

    button.addEventListener("dragover", (event) => {
        if (button.disabled || !hasDroppedFiles(event.dataTransfer)) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        button.classList.add("vst-file-drop-hover");
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = "copy";
        }
    });
    button.addEventListener("dragleave", (event) => {
        if (
            event.relatedTarget instanceof Node &&
            button.contains(event.relatedTarget)
        ) {
            return;
        }
        button.classList.remove("vst-file-drop-hover");
    });
    button.addEventListener("drop", (event) => {
        button.classList.remove("vst-file-drop-hover");
        if (
            button.disabled ||
            !event.dataTransfer ||
            !hasDroppedFiles(event.dataTransfer)
        ) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        void addFiles(event.dataTransfer).catch(() => {
            getVideoStagesHostBridge().showError(
                "Could not read dropped files.",
            );
        });
    });
};
