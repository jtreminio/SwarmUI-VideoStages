using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.MiniMax;

/// <summary>
/// A host stage's media captured for use as a keyframe reference, together with the VAE that was
/// current at capture time.
/// <para>
/// The base capture runs before the host decodes its base latent, so the captured media is often
/// a LATENT while the keyframe chain needs an IMAGE. Keeping the VAE alongside is what lets the
/// reference be decoded later; capturing media alone leaves no way to convert it.
/// </para>
/// </summary>
internal sealed record CapturedHostReference(WGNodeData Media, WGNodeData Vae)
{
    public static CapturedHostReference From(WorkflowGenerator g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return g.CurrentMedia is null
            ? null
            : new CapturedHostReference(g.CurrentMedia.Duplicate(), g.CurrentVae?.Duplicate());
    }

    /// <summary>
    /// Null when the capture was a latent and no VAE came with it.
    /// </summary>
    public WGNodeData AsImage()
    {
        bool isLatent = Media.DataType == WGNodeData.DT_LATENT_IMAGE
            || Media.DataType == WGNodeData.DT_LATENT_VIDEO;
        if (!isLatent)
        {
            return Media.Duplicate();
        }
        return Vae is null ? null : Media.Duplicate().AsRawImage(Vae);
    }
}
