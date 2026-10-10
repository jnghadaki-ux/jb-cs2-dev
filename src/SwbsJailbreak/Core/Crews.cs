using CounterStrikeSharp.API.Core;

namespace SwbsJailbreak.Core;

/// <summary>Crews service (`Jb.Crews`); the null object means "no crews" so other modules work without the module.</summary>
public interface ICrewService
{
    bool Available { get; }
}

public sealed class NullCrewService : ICrewService
{
    public static readonly NullCrewService Instance = new();
    public bool Available => false;
}
