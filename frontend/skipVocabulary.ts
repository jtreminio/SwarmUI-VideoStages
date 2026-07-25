/**
 * The one skip/unskip vocabulary. Every skip control — the clip region button,
 * the clip section header, the stage tabs — uses these, so the same action
 * never appears under two glyphs.
 */

/** Skipped items are re-enabled; everything else can be skipped. */
export const skipGlyph = (skipped: boolean): string => (skipped ? "⟲" : "⏭︎");

/** `subject` names the thing acted on, e.g. "clip" or "stage A". */
export const skipTitle = (subject: string, skipped: boolean): string =>
    `${skipped ? "Re-enable" : "Skip"} ${subject}`;
