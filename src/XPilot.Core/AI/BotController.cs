using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.AI;

public enum BotDifficulty { Easy, Normal, Hard }

/// <param name="ThinkInterval">Seconds between high-level decisions (reaction time).</param>
/// <param name="AimNoise">Max random aim error in radians.</param>
/// <param name="FireAngle">Fire when aim error is below this (radians).</param>
/// <param name="MaxSpeed">Cruise speed limit (px/s).</param>
/// <param name="ShieldSkill">Chance of raising the shield against an incoming bullet.</param>
/// <param name="Standoff">Preferred distance to a visible target.</param>
/// <param name="Gain">How aggressively velocity errors are corrected (1/s).</param>
public sealed record BotProfile(float ThinkInterval, float AimNoise, float FireAngle, float MaxSpeed, float ShieldSkill, float Standoff, float Gain)
{
    public static BotProfile For(BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Easy => new(0.25f, 0.16f, 0.12f, 260f, 0.25f, 280f, 2.5f),
        BotDifficulty.Hard => new(0.05f, 0.02f, 0.05f, 520f, 0.9f, 220f, 3.5f),
        _ => new(0.12f, 0.07f, 0.08f, 380f, 0.55f, 250f, 3f),
    };
}

/// <summary>
/// Flies one ship by producing a <see cref="ShipInput"/> each tick. High-level decisions (targets,
/// paths, shielding) happen every <see cref="BotProfile.ThinkInterval"/>; steering runs every tick.
/// </summary>
public sealed class BotController
{
    private const int LookaheadSteps = 14;
    private const float TurnAroundTime = 0.45f;

    private readonly World _world;
    private readonly NavGrid _nav;
    private readonly BotProfile _profile;
    private readonly Random _rng;
    private readonly List<Vector2> _path = [];

    private float _thinkTimer;
    private Vector2 _waypoint;
    private bool _hasWaypoint;
    private bool _waypointIsFinal;
    private float[]? _field;
    private int _fieldGoal = -1;
    private float _fieldAge;

    private Ship? _target;
    private bool _targetVisible;
    private bool _hasShot;
    private Vector2 _aimDir;

    private float _shieldTimer;
    private float _thrustDuty;
    private bool _refueling;
    private Vector2 _wanderGoal;
    private bool _hasWanderGoal;

    private float _dangerClear = float.MaxValue;
    private float _dangerLook;
    private int _tick;

    public BotController(World world, NavGrid nav, Ship ship, BotDifficulty difficulty, int seed)
    {
        _world = world;
        _nav = nav;
        Ship = ship;
        Difficulty = difficulty;
        _profile = BotProfile.For(difficulty);
        _rng = new Random(seed);
        _thinkTimer = (float)_rng.NextDouble() * _profile.ThinkInterval;
    }

    public Ship Ship { get; }
    public BotDifficulty Difficulty { get; }

    private float Accel => _world.Config.ThrustAccel;
    private float EffectiveBrake => Accel * 0.75f;

    public ShipInput Update()
    {
        var s = Ship;
        _tick++;
        if (!s.Alive)
        {
            _hasWaypoint = false;
            _field = null;
            _shieldTimer = 0f;
            _thinkTimer = 0f;
            return default;
        }

        const float dt = GameConfig.Dt;
        _thinkTimer -= dt;
        _fieldAge += dt;
        _shieldTimer -= dt;
        if (_thinkTimer <= 0f)
        {
            Think();
            _thinkTimer = _profile.ThinkInterval * (0.75f + 0.5f * (float)_rng.NextDouble());
        }

        if (_world.Rules.ControlsLocked)
        {
            float heading = _hasWaypoint ? MathUtil.ToAngle(_world.Map.Delta(s.Position, _waypoint)) : s.Heading;
            return new ShipInput { Turn = TurnToward(heading) };
        }
        return Steer();
    }

    private void Think()
    {
        if (_world.Rules.Mode == GameModeKind.Race) ThinkRace();
        else ThinkDogfight();
        if (_world.Rules.WeaponsEnabled) ThinkShield();
    }

    private void ThinkRace()
    {
        var s = Ship;
        var map = _world.Map;
        _hasShot = false;
        _targetVisible = false;
        _waypointIsFinal = false;
        if (s.Finished)
        {
            _hasWaypoint = false;
            return;
        }

        _path.Clear();
        var (ux, uy) = Map.TileOf(s.Position);
        float stopValue = MathF.Max(0.5f, map.CheckpointRadius / Map.TileSize - 1.5f);
        int checkpoint = s.NextCheckpoint;
        int stepsLeft = LookaheadSteps;
        int firstLegEnd = -1;
        for (int leg = 0; leg < 3 && stepsLeft > 0; leg++)
        {
            var field = _nav.CheckpointField(checkpoint);
            stepsLeft -= _nav.Trace(field, ref ux, ref uy, stepsLeft, _path, stopValue);
            if (leg == 0) firstLegEnd = _path.Count - 1;
            if (_nav.FieldValue(field, ux, uy) > stopValue) break;
            checkpoint = (checkpoint + 1) % map.Checkpoints.Count;
        }

        var next = s.Position + map.Delta(s.Position, map.Checkpoints[s.NextCheckpoint]);
        float r = _world.Config.ShipRadius;
        for (int i = _path.Count - 1; i >= 0; i--)
        {
            // Points past the next checkpoint only count if the straight line still goes through it.
            if (i > firstLegEnd && MathUtil.DistanceToSegment(next, s.Position, _path[i]) > map.CheckpointRadius * 0.6f) continue;
            if (map.SegmentClear(s.Position, _path[i], r))
            {
                SetWaypoint(_path[i], false);
                return;
            }
        }
        SetWaypoint(_path.Count > 0 ? _path[0] : next, false);
    }

    private void ThinkDogfight()
    {
        var s = Ship;
        var map = _world.Map;
        var cfg = _world.Config;

        if (_refueling)
        {
            if (s.Fuel > cfg.MaxFuel * 0.9f || map.FuelStations.Count == 0) _refueling = false;
        }
        else if (s.Fuel < cfg.MaxFuel * 0.3f && map.FuelStations.Count > 0)
        {
            _refueling = true;
        }

        _target = PickTarget(out _targetVisible);
        _hasShot = false;
        if (_target != null && _targetVisible
            && map.Distance(s.Position, _target.Position) < cfg.BulletSpeed * cfg.BulletLife * 0.85f
            && TryIntercept(_target, out var aim))
        {
            float noise = ((float)_rng.NextDouble() * 2f - 1f) * _profile.AimNoise;
            _aimDir = MathUtil.Rotate(aim, noise);
            _hasShot = true;
        }

        Vector2 goal;
        bool final;
        if (_refueling)
        {
            goal = Nearest(map.FuelStations);
            final = true;
        }
        else if (_target != null)
        {
            goal = _target.Position;
            final = false;
        }
        else
        {
            if (!_hasWanderGoal || map.Distance(s.Position, _wanderGoal) < 64f)
            {
                _wanderGoal = _nav.RandomOpenPosition(_rng);
                _hasWanderGoal = true;
            }
            goal = _wanderGoal;
            final = true;
        }

        float r = cfg.ShipRadius;
        if (map.SegmentClear(s.Position, goal, r))
        {
            SetWaypoint(s.Position + map.Delta(s.Position, goal), final);
            return;
        }

        int goalIndex = _nav.IndexOf(goal);
        if (_field == null || goalIndex != _fieldGoal || _fieldAge > 0.75f)
        {
            _field = _nav.ComputeField(goalIndex);
            _fieldGoal = goalIndex;
            _fieldAge = 0f;
        }

        _path.Clear();
        var (ux, uy) = Map.TileOf(s.Position);
        _nav.Trace(_field, ref ux, ref uy, LookaheadSteps, _path);
        for (int i = _path.Count - 1; i >= 0; i--)
        {
            if (map.SegmentClear(s.Position, _path[i], r))
            {
                SetWaypoint(_path[i], false);
                return;
            }
        }
        if (_path.Count > 0) SetWaypoint(_path[0], false);
        else _hasWaypoint = false;
    }

    private void SetWaypoint(Vector2 waypoint, bool final)
    {
        _waypoint = waypoint;
        _waypointIsFinal = final;
        _hasWaypoint = true;
    }

    private Ship? PickTarget(out bool visible)
    {
        var s = Ship;
        var map = _world.Map;
        Ship? best = null;
        float bestScore = float.MaxValue;
        visible = false;
        foreach (var other in _world.Ships)
        {
            if (other == s || !other.Alive) continue;
            float dist = map.Distance(s.Position, other.Position);
            bool canSee = dist < 900f && map.SegmentClear(s.Position, other.Position, 0f);
            float score = dist * (canSee ? 0.6f : 1f) + (other.SpawnProtection > 0f ? 300f : 0f);
            if (score < bestScore)
            {
                bestScore = score;
                best = other;
                visible = canSee;
            }
        }
        return best;
    }

    /// <summary>Direction to fire so a bullet meets the target, assuming both keep their velocity.</summary>
    private bool TryIntercept(Ship target, out Vector2 aim)
    {
        var s = Ship;
        var cfg = _world.Config;
        aim = Vector2.Zero;
        var p = _world.Map.Delta(s.Position, target.Position);
        var w = target.Velocity - s.Velocity;
        float speed = cfg.BulletSpeed;
        float a = Vector2.Dot(w, w) - speed * speed;
        float b = 2f * Vector2.Dot(p, w);
        float c = Vector2.Dot(p, p);

        float t;
        if (MathF.Abs(a) < 1e-3f)
        {
            if (MathF.Abs(b) < 1e-3f) return false;
            t = -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float sq = MathF.Sqrt(disc);
            float t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
            t = t1 > 0f && (t1 < t2 || t2 <= 0f) ? t1 : t2;
        }
        if (t <= 0f || t > cfg.BulletLife) return false;
        aim = MathUtil.SafeNormalize(p + w * t);
        return aim != Vector2.Zero;
    }

    private void ThinkShield()
    {
        var s = Ship;
        var map = _world.Map;
        if (s.Fuel < 60f) return;
        foreach (var b in _world.Bullets)
        {
            if (b.OwnerId == s.Id) continue;
            var p = map.Delta(s.Position, b.Position);
            var w = b.Velocity - s.Velocity;
            float w2 = w.LengthSquared();
            if (w2 < 1f) continue;
            float t = -Vector2.Dot(p, w) / w2;
            if (t < 0f || t > 0.4f) continue;
            if ((p + w * t).Length() < 30f)
            {
                if (_rng.NextDouble() < _profile.ShieldSkill) _shieldTimer = 0.4f;
                return;
            }
        }
    }

    private ShipInput Steer()
    {
        var s = Ship;
        var map = _world.Map;
        var cfg = _world.Config;
        var g = _world.GravityAt(s.Position);

        // Desired velocity: follow the waypoint, or keep a standoff from a visible target.
        Vector2 desiredVelocity = Vector2.Zero;
        if (_world.Rules.Mode == GameModeKind.Dogfight && _targetVisible && !_refueling && _target is { Alive: true } target)
        {
            var toTarget = map.Delta(s.Position, target.Position);
            float dist = toTarget.Length();
            float approach = Math.Clamp((dist - _profile.Standoff) * 0.8f, -150f, _profile.MaxSpeed);
            desiredVelocity = target.Velocity * 0.5f + MathUtil.SafeNormalize(toTarget) * approach;
            desiredVelocity = MathUtil.ClampLength(desiredVelocity, _profile.MaxSpeed);
        }
        else if (_hasWaypoint)
        {
            var toWaypoint = map.Delta(s.Position, _waypoint);
            float dist = toWaypoint.Length();
            float speed = _waypointIsFinal ? BrakeSpeed(MathF.Max(0f, dist - 8f)) : BrakeSpeed(dist + 64f);
            speed = MathF.Min(speed, _profile.MaxSpeed);
            if (_waypointIsFinal && dist < 16f) speed = 0f;
            desiredVelocity = MathUtil.SafeNormalize(toWaypoint) * speed;
        }

        // Look ahead along the current velocity and slow down if a wall is coming.
        float currentSpeed = s.Velocity.Length();
        if ((_tick % 3) == 0)
        {
            if (currentSpeed > 30f)
            {
                _dangerLook = StopDistance(currentSpeed) + 24f;
                _dangerClear = map.ClearDistance(s.Position, s.Velocity / currentSpeed, _dangerLook, cfg.ShipRadius + 2f);
            }
            else
            {
                _dangerClear = float.MaxValue;
            }
        }
        bool urgent = false;
        if (currentSpeed > 30f && _dangerClear < _dangerLook)
        {
            var vdir = s.Velocity / currentSpeed;
            float limit = BrakeSpeed(MathF.Max(0f, _dangerClear - 8f));
            float along = Vector2.Dot(desiredVelocity, vdir);
            if (along > limit) desiredVelocity -= vdir * (along - limit);
            urgent = _dangerClear < _dangerLook * 0.75f;
        }

        var desiredAccel = (desiredVelocity - s.Velocity) * _profile.Gain - g;
        float need = desiredAccel.Length();
        bool navUrgent = urgent || need > Accel * 0.9f;

        float desiredHeading = need > 1e-3f ? MathUtil.ToAngle(desiredAccel) : s.Heading;
        bool shield = _shieldTimer > 0f && s.Fuel > cfg.MaxFuel * 0.08f;
        if (_hasShot && !navUrgent && !shield) desiredHeading = MathUtil.ToAngle(_aimDir);

        var input = new ShipInput { Turn = TurnToward(desiredHeading), Shield = shield };

        // Pulse the thrust so the average acceleration matches what is needed.
        if (need > 15f)
        {
            float alignment = Vector2.Dot(MathUtil.FromAngle(s.Heading), desiredAccel / need);
            if (alignment > 0.8f || (urgent && alignment > 0.5f))
            {
                _thrustDuty += MathF.Min(1f, need / Accel) * alignment;
                if (_thrustDuty >= 1f)
                {
                    input.Thrust = true;
                    _thrustDuty -= 1f;
                }
            }
        }
        else
        {
            _thrustDuty = 0f;
        }

        if (_hasShot && !shield && _target is { Alive: true })
        {
            float aimError = MathF.Abs(MathUtil.WrapAngle(s.Heading - MathUtil.ToAngle(_aimDir)));
            input.Fire = aimError < _profile.FireAngle;
        }
        return input;
    }

    private float TurnToward(float heading)
    {
        float diff = MathUtil.WrapAngle(heading - Ship.Heading);
        return Math.Clamp(diff / (_world.Config.TurnSpeed * GameConfig.Dt), -1f, 1f);
    }

    /// <summary>Distance needed to turn around and stop from the given speed.</summary>
    private float StopDistance(float speed) => speed * TurnAroundTime + speed * speed / (2f * EffectiveBrake);

    /// <summary>Highest speed from which the ship can still stop within <paramref name="distance"/>.</summary>
    private float BrakeSpeed(float distance)
    {
        float a = EffectiveBrake, t = TurnAroundTime;
        return -a * t + MathF.Sqrt(a * a * t * t + 2f * a * distance);
    }

    private Vector2 Nearest(IReadOnlyList<Vector2> points)
    {
        var map = _world.Map;
        var best = points[0];
        float bestDist = float.MaxValue;
        foreach (var p in points)
        {
            float d = map.Distance(Ship.Position, p);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }
}
