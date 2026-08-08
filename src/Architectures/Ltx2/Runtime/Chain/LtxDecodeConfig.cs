using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2.Runtime.Chain;

/// <summary>
/// The tiling geometry of one LTX decode. The numbers restate core's own, hard-coded inside
/// <c>WGNodeData.DecodeLatents</c> with nothing public to call; splicing a decode by id needs the
/// node built through the bridge, so core cannot build it for us. `Decode_tiling_geometry_matches_core`
/// asserts the two still agree.
/// </summary>
internal sealed record LtxDecodeConfig(
    bool UseTiledDecode,
    int TileSize = 0,
    int Overlap = 0,
    int TemporalSize = 0,
    int TemporalOverlap = 0)
{
    internal static LtxDecodeConfig From(WorkflowGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        bool hasExplicitTiling =
            generator.UserInput.TryGet(T2IParamTypes.VAETileSize, out _)
            || generator.UserInput.TryGet(T2IParamTypes.VAETemporalTileSize, out _);
        if (hasExplicitTiling)
        {
            return new LtxDecodeConfig(
                UseTiledDecode: true,
                TileSize: generator.UserInput.Get(T2IParamTypes.VAETileSize, 256),
                Overlap: generator.UserInput.Get(T2IParamTypes.VAETileOverlap, 64),
                TemporalSize: generator.UserInput.Get(T2IParamTypes.VAETemporalTileSize, 32),
                TemporalOverlap: generator.UserInput.Get(
                    T2IParamTypes.VAETemporalTileOverlap, 4));
        }
        if (generator.IsLTXV2()
            && generator.UserInput.Get(T2IParamTypes.ModelSpecificEnhancements, true))
        {
            return new LtxDecodeConfig(
                UseTiledDecode: true,
                TileSize: 2048,
                Overlap: 256,
                TemporalSize: 64,
                TemporalOverlap: 16);
        }
        return new LtxDecodeConfig(UseTiledDecode: false);
    }
}
