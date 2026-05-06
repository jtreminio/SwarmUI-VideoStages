using Newtonsoft.Json.Linq;

namespace VideoStages;

internal sealed class ClipAudioState
{
    public JArray ReusedAudioPath { get; private set; }

    public bool TryGetPath(out JArray path)
    {
        path = ReusedAudioPath;
        return path is not null;
    }

    public void Remember(JArray path) => ReusedAudioPath = path;

    public void Clear() => ReusedAudioPath = null;
}
