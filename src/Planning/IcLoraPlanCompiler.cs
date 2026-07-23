using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles stage-applicable IC-LoRAs and their pure drive-media intent.</summary>
internal static class IcLoraPlanCompiler
{
    internal static ImmutableArray<IcLoraPlan> Compile(ClipSpec clip, StageSpec stage)
    {
        ImmutableArray<IcLoraPlan>.Builder plans = ImmutableArray.CreateBuilder<IcLoraPlan>();
        IReadOnlyList<IcLoraSpec> entries = clip.IcLoras ?? [];
        for (int i = 0; i < entries.Count; i++)
        {
            IcLoraSpec entry = entries[i];
            if (entry.Stage >= 0 && entry.Stage != stage.ClipStageRawIndex)
            {
                continue;
            }

            IcLoraDrivePlan drive = CompileDrive(clip, entry);
            double? guideStrength = null;
            if (drive.HasDriveMedia)
            {
                if (stage.ControlNetStrength is double stageStrength)
                {
                    guideStrength = stageStrength;
                }
                else if (drive.Kind != IcLoraDriveSourceKind.ControlNet)
                {
                    guideStrength = 1.0;
                }
            }

            plans.Add(new IcLoraPlan(
                i,
                entry.Lora,
                StringUtils.Equals(entry.Lora, Constants.IcLoraAutoModel),
                entry.Preset,
                entry.Strength,
                entry.AttentionStrength,
                CompileControlMode(entry.ControlType),
                drive,
                guideStrength));
        }
        return plans.ToImmutable();
    }

    private static IcLoraDrivePlan CompileDrive(ClipSpec clip, IcLoraSpec entry)
    {
        string raw = entry.Source?.Trim() ?? "";
        if (StringUtils.Equals(raw, Constants.IcLoraSourceUpload))
        {
            if (!string.IsNullOrWhiteSpace(entry.Video?.Data))
            {
                string data = entry.Video.Data;
                IcLoraUploadedMediaKind mediaKind = data.StartsWith(
                    "data:image/", StringComparison.OrdinalIgnoreCase)
                    ? IcLoraUploadedMediaKind.Image
                    : data.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase)
                        ? IcLoraUploadedMediaKind.Video
                        : IcLoraUploadedMediaKind.Unknown;
                return new(
                    IcLoraDriveSourceKind.UploadedMedia,
                    raw,
                    null,
                    mediaKind,
                    data,
                    HasDriveMedia: true);
            }
            if (clip.SourceVideo is not null)
            {
                return new(
                    IcLoraDriveSourceKind.SourcedClipInput,
                    raw,
                    null,
                    IcLoraUploadedMediaKind.None,
                    null,
                    HasDriveMedia: true);
            }
            return new(
                IcLoraDriveSourceKind.LoaderOnly,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: false);
        }
        if (StringUtils.Equals(raw, Constants.IcLoraSourceStageInput))
        {
            return new(
                IcLoraDriveSourceKind.StageInput,
                raw,
                null,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: true);
        }
        if (ControlNetSourcePlan.TryParseIndex(raw, out int controlNetIndex))
        {
            return new(
                IcLoraDriveSourceKind.ControlNet,
                raw,
                controlNetIndex,
                IcLoraUploadedMediaKind.None,
                null,
                HasDriveMedia: true);
        }
        return new(
            IcLoraDriveSourceKind.Unknown,
            raw,
            null,
            IcLoraUploadedMediaKind.None,
            null,
            HasDriveMedia: false);
    }

    private static IcLoraControlMode CompileControlMode(string controlType)
    {
        if (StringUtils.Equals(controlType, Constants.IcLoraControlNone))
        {
            return IcLoraControlMode.None;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlCanny))
        {
            return IcLoraControlMode.Canny;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlDepth))
        {
            return IcLoraControlMode.Depth;
        }
        if (StringUtils.Equals(controlType, Constants.IcLoraControlNormal))
        {
            return IcLoraControlMode.Normal;
        }
        return IcLoraControlMode.Unknown;
    }
}
