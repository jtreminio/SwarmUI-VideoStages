using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2.Runtime.Chain;

internal sealed record LtxVaeTilingConfig(
    bool Enabled,
    int TileSize = 0,
    int Overlap = 0,
    int TemporalSize = 0,
    int TemporalOverlap = 0)
{
    internal static LtxVaeTilingConfig ForDecode(WorkflowGenerator generator)
    {
        LtxVaeTilingConfig selected = FromUserSelection(generator);
        if (selected.Enabled)
        {
            return selected;
        }
        if (generator.IsLTXV2()
            && generator.UserInput.Get(T2IParamTypes.ModelSpecificEnhancements, true))
        {
            return new LtxVaeTilingConfig(
                Enabled: true,
                TileSize: 2048,
                Overlap: 256,
                TemporalSize: 64,
                TemporalOverlap: 16);
        }
        return selected;
    }

    internal static LtxVaeTilingConfig FromUserSelection(WorkflowGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        bool enabled =
            generator.UserInput.TryGet(T2IParamTypes.VAETileSize, out _)
            || generator.UserInput.TryGet(T2IParamTypes.VAETemporalTileSize, out _);
        return enabled
            ? new LtxVaeTilingConfig(
                Enabled: true,
                TileSize: generator.UserInput.Get(T2IParamTypes.VAETileSize, 256),
                Overlap: generator.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
                TemporalSize: generator.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 32),
                TemporalOverlap: generator.UserInput.Get(
                    T2IParamTypes.VAETemporalTileOverlap, 4))
            : new LtxVaeTilingConfig(Enabled: false);
    }
}
