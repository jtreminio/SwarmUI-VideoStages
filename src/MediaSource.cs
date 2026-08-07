using System.Globalization;

namespace VideoStages;

/// <summary>Every persisted spelling an authored document can name a source by — audio tracks,
/// host images, and stage references — with the indexed syntax that parses and formats them.
/// Shared by authoring, planning, and runtime resolution.</summary>
public static class MediaSource
{
    public const string Upload = "Upload";
    public const string Native = "Native";
    public const string Incoming = "Incoming";

    /// <summary>What documents written before the rename spell <see cref="Incoming"/>.</summary>
    public const string IncomingLegacy = "Stage Input";

    public const string ControlNet = "ControlNet";
    public const string ControlNetOne = "ControlNet 1";
    public const string ControlNetTwo = "ControlNet 2";
    public const string ControlNetThree = "ControlNet 3";
    public const string AceStepFun = "AceStepFun";
    public const string Base = "Base";
    public const string Refiner = "Refiner";
    public const string Generated = "Generated";
    public const string PreviousStage = "PreviousStage";

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this byte for byte
    /// with <c>frontend/generatedMediaSource.ts</c>.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        System.Text.StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from MediaSource.cs. Do not edit by hand.");
        Line();
        Line($"export const MEDIA_SOURCE_UPLOAD = \"{Upload}\";");
        Line($"export const MEDIA_SOURCE_NATIVE = \"{Native}\";");
        Line($"export const MEDIA_SOURCE_INCOMING = \"{Incoming}\";");
        Line($"export const MEDIA_SOURCE_INCOMING_LEGACY = \"{IncomingLegacy}\";");
        Line($"export const MEDIA_SOURCE_CONTROLNET = \"{ControlNet}\";");
        Line($"export const MEDIA_SOURCE_ACE_STEP_FUN = \"{AceStepFun}\";");
        Line($"export const MEDIA_SOURCE_BASE = \"{Base}\";");
        Line($"export const MEDIA_SOURCE_REFINER = \"{Refiner}\";");
        Line();
        Line("/** The per-slot spellings, in slot order. */");
        Line("export const CONTROLNET_SOURCE_OPTIONS = [");
        for (int index = 0; index < ControlNetSlotCount; index++)
        {
            Line($"    \"{FormatControlNet(index)}\",");
        }
        Line("] as const;");
        Line();
        Line("// Prefixes of the indexed spellings. ExplicitStagePrefix stays backend-only: no");
        Line("// frontend code parses it, and a generated constant nothing reads is a liability.");
        Line($"export const MEDIA_SOURCE_ACE_STEP_FUN_PREFIX = \"{AceStepFunPrefix}\";");
        Line($"export const MEDIA_SOURCE_BASE_2_EDIT_PREFIX = \"{Base2EditPrefix}\";");
        Line();
        Line("/**");
        Line(" * The audio-track sources the backend recognises. Anything else an authored");
        Line(" * document carries normalizes to \"Unrecognized\" and is dropped at planning.");
        Line(" */");
        Line("export type AudioTrackSourceKind =");
        Line($"    | \"{Upload}\"");
        Line($"    | \"{AceStepFun}\"");
        Line($"    | \"{Native}\"");
        Line($"    | \"{ControlNet}\"");
        Line("    | \"Unrecognized\";");

        return result.ToString();
    }

    private const string AceStepFunPrefix = "audio";
    private const string Base2EditPrefix = "edit";
    private const string ExplicitStagePrefix = "Stage";

    /// <summary>ControlNet slots are numbered from one and the host exposes exactly this many.</summary>
    public const int ControlNetSlotCount = 3;

    public static bool TryParseControlNetIndex(string source, out int index)
    {
        if (TryParseNonNegativeIndex(source, ControlNet, out int oneBased)
            && oneBased is >= 1 and <= ControlNetSlotCount)
        {
            index = oneBased - 1;
            return true;
        }
        index = -1;
        return false;
    }

    public static string FormatControlNet(int index) => index switch
    {
        0 => ControlNetOne,
        1 => ControlNetTwo,
        2 => ControlNetThree,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static bool TryParseAceStepFunIndex(string source, out int index) =>
        TryParseNonNegativeIndex(source, AceStepFunPrefix, out index);

    public static string FormatAceStepFun(int index) =>
        FormatNonNegativeIndex(AceStepFunPrefix, index);

    public static bool TryParseBase2EditIndex(string source, out int index) =>
        TryParseNonNegativeIndex(source, Base2EditPrefix, out index);

    public static string FormatBase2Edit(int index) =>
        FormatNonNegativeIndex(Base2EditPrefix, index);

    public static bool TryParseExplicitStageIndex(string source, out int index) =>
        TryParseNonNegativeIndex(source, ExplicitStagePrefix, out index);

    public static string FormatExplicitStageIndex(int index) =>
        FormatNonNegativeIndex(ExplicitStagePrefix, index);

    /// <summary>The "stage" here is a pass of the host workflow, not a timeline stage.</summary>
    public static bool IsHostStageSource(string source) =>
        StringUtils.Equals(source, Base)
        || StringUtils.Equals(source, Refiner)
        || TryParseBase2EditIndex(source, out _);

    /// <summary>The grammar is exactly digits after the prefix. NumberStyles.None is what makes
    /// that true: int.TryParse's default allows a sign and surrounding whitespace, which no
    /// producer emits and the frontend mirror does not accept.</summary>
    private static bool TryParseNonNegativeIndex(
        string source,
        string prefix,
        out int index)
    {
        string compact = StringUtils.Compact(source);
        index = -1;
        if (compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                compact.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed))
        {
            index = parsed;
            return true;
        }
        return false;
    }

    private static string FormatNonNegativeIndex(string prefix, int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return $"{prefix}{index}";
    }
}
