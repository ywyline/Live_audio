namespace TikTokAudio.Domain;

public readonly record struct ContractVersion(int Major, int Minor)
{
    public static ContractVersion Current { get; } = new(1, 0);

    public override string ToString() => $"{Major}.{Minor}";
}

public readonly record struct StateSchemaVersion(int Major, int Minor)
{
    public static StateSchemaVersion Current { get; } = new(1, 0);

    public override string ToString() => $"{Major}.{Minor}";
}

public readonly record struct PlanRevision(long Value)
{
    public static PlanRevision Initial => new(0);
}

public readonly record struct EngineRevision(long Value)
{
    public static EngineRevision Initial => new(0);
}

public readonly record struct ProductEpoch(long Value)
{
    public static ProductEpoch Initial => new(0);
}
