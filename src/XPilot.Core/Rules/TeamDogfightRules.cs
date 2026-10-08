using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>Red vs Blue dogfight: every enemy shot down scores for your team. Teammates can't hurt each other.</summary>
public sealed class TeamDogfightRules(int teamScoreLimit = 20, float timeLimit = 300f) : ITeamRules
{
    private readonly int[] _teamScores = new int[2];

    public int TeamScoreLimit { get; } = teamScoreLimit;
    /// <summary>Seconds; 0 means no limit.</summary>
    public float TimeLimit { get; } = timeLimit;
    public int WinningTeam { get; private set; } = Teams.None;

    public GameModeKind Mode => GameModeKind.TeamDogfight;
    public bool WeaponsEnabled => true;
    public bool ControlsLocked => false;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 4f;
    public bool IsTeamGame => true;

    public int TeamScore(int team) => team is Teams.Red or Teams.Blue ? _teamScores[team] : 0;

    public float TimeRemaining(World world) => TimeLimit > 0 ? MathF.Max(0f, TimeLimit - world.Time) : float.PositiveInfinity;

    public void Initialize(World world)
    {
        var used = new Dictionary<int, int>();
        foreach (var ship in world.Ships)
        {
            var bases = world.Map.TeamBases(ship.Team);
            int k = used.GetValueOrDefault(ship.Team);
            used[ship.Team] = k + 1;
            // Without team bases, Red takes them from the front of the list and Blue from the back.
            var pos = bases.Count == world.Map.Bases.Count && ship.Team == Teams.Blue ? bases[^(k % bases.Count + 1)] : bases[k % bases.Count];
            world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos));
        }
    }

    public void Update(World world)
    {
        if (!IsOver && TimeLimit > 0 && world.Time >= TimeLimit) End(world);
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
        if (killer != null && killer != victim && killer.Team != victim.Team)
        {
            killer.Kills++;
            killer.Score++;
            if (killer.Team is Teams.Red or Teams.Blue)
            {
                _teamScores[killer.Team]++;
                if (!IsOver && TeamScoreLimit > 0 && _teamScores[killer.Team] >= TeamScoreLimit) End(world);
            }
        }
        else
        {
            victim.Score--;
        }
    }

    /// <summary>Respawns at the team's base farthest from living enemies.</summary>
    public void Respawn(World world, Ship ship)
    {
        var pos = RuleHelpers.SafestBase(world, ship, world.Map.TeamBases(ship.Team), other => other.Team != ship.Team);
        world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos));
    }

    public IReadOnlyList<Ship> GetStandings(World world) => world.Ships
        .OrderByDescending(s => TeamScore(s.Team))
        .ThenBy(s => s.Team)
        .ThenByDescending(s => s.Score)
        .ThenByDescending(s => s.Kills)
        .ThenBy(s => s.Deaths)
        .ThenBy(s => s.Id)
        .ToList();

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(IsOver);
        writer.Write((sbyte)WinningTeam);
        writer.Write(_teamScores[Teams.Red]);
        writer.Write(_teamScores[Teams.Blue]);
    }

    public void ReadState(BinaryReader reader)
    {
        IsOver = reader.ReadBoolean();
        WinningTeam = reader.ReadSByte();
        _teamScores[Teams.Red] = reader.ReadInt32();
        _teamScores[Teams.Blue] = reader.ReadInt32();
    }

    private void End(World world)
    {
        IsOver = true;
        int red = _teamScores[Teams.Red], blue = _teamScores[Teams.Blue];
        WinningTeam = red > blue ? Teams.Red : blue > red ? Teams.Blue : Teams.None;
        world.Emit(new GameEvent(GameEventType.MatchOver, Value: WinningTeam));
    }
}
