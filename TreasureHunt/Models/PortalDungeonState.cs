using System;

namespace TreasureHunt.Models;

public enum PortalDungeonPhase
{
    Idle,
    WaitingForPortal,
    EnteringPortal,
    InDungeon,
    InteractingWithObject,
    WaitingForCombat,
    OpeningChest,
    WaitingForRoll,
    MovingToNextFloor,
    InBonusRoom,
    BonusRoomComplete,
    ExitingDungeon,
    Failed
}

public enum TreasureHuntPhase
{
    Idle,
    PurchasingMap,
    DecipheringMap,
    Teleporting,
    NavigatingToSpot,
    Digging,
    WaitingForMonsterCombat,
    OpeningTreasureChest,
    CheckingPortal,
    EnteringPortal,
    InPortalDungeon,
    Done
}

public class PortalDungeonState
{
    public PortalDungeonPhase Phase { get; set; } = PortalDungeonPhase.Idle;
    public int CurrentFloor { get; set; } = 0;
    public bool PortalSpawned { get; set; } = false;
    public bool InBonusRoom { get; set; } = false;
    public DateTime? PhaseStartTime { get; set; }
    public string? LastError { get; set; }

    public void SetPhase(PortalDungeonPhase phase)
    {
        Phase = phase;
        PhaseStartTime = DateTime.Now;
        LastError = null;
    }

    public void Fail(string error)
    {
        Phase = PortalDungeonPhase.Failed;
        LastError = error;
        PhaseStartTime = DateTime.Now;
    }

    public TimeSpan GetPhaseDuration()
    {
        return PhaseStartTime.HasValue ? DateTime.Now - PhaseStartTime.Value : TimeSpan.Zero;
    }
}

public class TreasureHuntState
{
    public TreasureHuntPhase Phase { get; set; } = TreasureHuntPhase.Idle;
    public TreasureMapData? CurrentMap { get; set; }
    public PortalDungeonState PortalState { get; } = new();
    public DateTime? PhaseStartTime { get; set; }
    public string? StatusMessage { get; set; }
    public string? LastError { get; set; }

    public void SetPhase(TreasureHuntPhase phase, string? message = null)
    {
        Phase = phase;
        PhaseStartTime = DateTime.Now;
        StatusMessage = message;
        LastError = null;
    }

    public void Fail(string error)
    {
        LastError = error;
        StatusMessage = $"错误: {error}";
    }
}
