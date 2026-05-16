using SwarmUI.Text2Image;

namespace VideoStages;

internal sealed class ParamSnapshot : IDisposable
{
    private readonly T2IParamInput Input;
    private readonly Entry[] Entries;

    private readonly record struct Entry(string Id, bool Had, object Value);

    private ParamSnapshot(T2IParamInput input, Entry[] entries)
    {
        Input = input;
        Entries = entries;
    }

    public static ParamSnapshot Of(T2IParamInput input, params T2IParamType[] paramTypes)
    {
        Entry[] entries = new Entry[paramTypes.Length];
        for (int i = 0; i < paramTypes.Length; i++)
        {
            bool had = input.TryGetRaw(paramTypes[i], out object val);
            entries[i] = new Entry(paramTypes[i].ID, had, val);
        }

        return new ParamSnapshot(input, entries);
    }

    public void Restore()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Had)
            {
                Input.InternalSet.ValuesInput[entry.Id] = entry.Value;
            }
            else
            {
                Input.InternalSet.ValuesInput.Remove(entry.Id);
            }
        }
    }

    public void Remove()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Had)
            {
                Input.InternalSet.ValuesInput.Remove(entry.Id);
            }
        }
    }

    public void Reset()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Had)
            {
                Input.InternalSet.ValuesInput[entry.Id] = entry.Value;
            }
        }
    }

    public void Dispose() => Restore();
}
