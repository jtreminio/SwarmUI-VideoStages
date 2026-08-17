export const hasDroppedFiles = (transfer: DataTransfer | null): boolean =>
    transfer !== null &&
    (transfer.files.length > 0 ||
        Array.from(transfer.items ?? []).some((item) => item.kind === "file") ||
        Array.from(transfer.types ?? []).includes("Files"));

interface DroppedFileSystemEntry {
    isFile: boolean;
    isDirectory: boolean;
    file?: (
        success: (file: File) => void,
        error?: (cause: DOMException) => void,
    ) => void;
    createReader?: () => {
        readEntries(
            success: (entries: DroppedFileSystemEntry[]) => void,
            error?: (cause: DOMException) => void,
        ): void;
    };
}

type EntryDataTransferItem = DataTransferItem & {
    webkitGetAsEntry?: () => DroppedFileSystemEntry | null;
};

const readDirectoryBatch = (
    reader: ReturnType<NonNullable<DroppedFileSystemEntry["createReader"]>>,
): Promise<DroppedFileSystemEntry[]> =>
    new Promise((resolve) => reader.readEntries(resolve, () => resolve([])));

const filesFromEntry = async (
    entry: DroppedFileSystemEntry,
): Promise<File[]> => {
    if (entry.isFile && entry.file) {
        return new Promise((resolve) =>
            entry.file?.(
                (file) => resolve([file]),
                () => resolve([]),
            ),
        );
    }
    const reader = entry.isDirectory ? entry.createReader?.() : undefined;
    if (!reader) {
        return [];
    }
    const files: File[] = [];
    // Directory readers return batches and require calls until the empty batch.
    for (;;) {
        const batch = await readDirectoryBatch(reader);
        if (batch.length === 0) {
            return files;
        }
        const nested = await Promise.all(batch.map(filesFromEntry));
        files.push(...nested.flat());
    }
};

export const collectDroppedFiles = async (
    transfer: DataTransfer,
): Promise<File[]> => {
    const items = Array.from(transfer.items ?? []);
    if (items.length === 0) {
        return Array.from(transfer.files);
    }
    const nested = await Promise.all(
        items.map((item) => {
            const entry = (item as EntryDataTransferItem).webkitGetAsEntry?.();
            if (entry) {
                return filesFromEntry(entry);
            }
            const file = item.getAsFile();
            return Promise.resolve(file ? [file] : []);
        }),
    );
    const files = nested.flat();
    return files.length > 0 ? files : Array.from(transfer.files);
};
