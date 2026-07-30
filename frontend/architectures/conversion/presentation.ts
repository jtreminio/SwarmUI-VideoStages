export const architectureConversionMessage = (
    fromLabel: string,
    toLabel: string,
    removals: readonly string[],
): string => {
    const impact =
        removals.length === 0
            ? "Stage models will be retargeted. Unsupported settings stay saved but dormant."
            : `This removes: ${removals.join(", ")}.`;
    return (
        `Convert this clip from ${fromLabel} to ${toLabel}?\n\n` +
        `${impact}\n\nThe conversion is one undoable change.`
    );
};

/** UI-only confirmation wrapper; pure conversion planning never imports it. */
export const confirmArchitectureConversion = (
    message: string,
    apply: () => boolean,
    confirm: (message: string) => boolean = (value) => window.confirm(value),
): boolean => confirm(message) && apply();
