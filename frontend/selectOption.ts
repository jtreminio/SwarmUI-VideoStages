export interface SelectOption {
    value: string;
    label: string;
    disabled?: boolean;
}

/**
 * Keeps a still-selected value in an options list when it is no longer offered
 * (a generated ref that left the registry, a Base2Edit stage that is gone) so
 * the select can't silently drop it. `build` returns the option to insert, or
 * null to leave the list untouched (the value isn't one this select preserves).
 * `position` places it at the list end or start to match the existing UI.
 */
export const preserveSelectedOption = <T extends SelectOption>(
    options: T[],
    selectedValue: string,
    position: "start" | "end",
    build: (value: string) => T | null,
): void => {
    const value = `${selectedValue || ""}`.trim();
    if (!value || options.some((option) => option.value === value)) {
        return;
    }
    const option = build(value);
    if (!option) {
        return;
    }
    if (position === "start") {
        options.unshift(option);
    } else {
        options.push(option);
    }
};

/**
 * Resolves a stored select value to one the built options actually contain,
 * falling back to `fallback` when the stored value is absent.
 */
export const resolveSelectValue = (
    currentValue: string,
    options: readonly { value: string }[],
    fallback: string,
): string => {
    const desired = `${currentValue || ""}`;
    return options.some((option) => option.value === desired)
        ? desired
        : fallback;
};
