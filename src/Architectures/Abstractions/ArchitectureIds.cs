namespace VideoStages.Architectures.Abstractions;

internal readonly record struct ArchitectureId
{
    public ArchitectureId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Architecture id cannot be empty.", nameof(value))
            : value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

internal readonly record struct ModelProfileId
{
    public ModelProfileId(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Model profile id cannot be empty.", nameof(value))
            : value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}
