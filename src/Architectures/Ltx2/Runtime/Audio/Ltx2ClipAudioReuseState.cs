using Newtonsoft.Json.Linq;

namespace VideoStages.Architectures.Ltx2.Runtime.Audio;

internal sealed class Ltx2ClipAudioReuseState
{
    private JArray reusedAudioPath;

    public bool TryGetPath(out JArray path)
    {
        path = reusedAudioPath;
        return path is not null;
    }

    public void Remember(JArray path) => reusedAudioPath = path;

    public void Clear() => reusedAudioPath = null;
}
