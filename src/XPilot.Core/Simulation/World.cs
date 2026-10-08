using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Rules;

namespace XPilot.Core.Simulation;

/// <summary>
/// The authoritative game state. Advance it with <see cref="Step"/> at a fixed rate of
/// <see cref="GameConfig.TickRate"/>; it has no dependency on rendering or input devices.
/// </summary>
public sealed class World(Map map, GameConfig config, IGameRules rules, int seed = 0)
{
    private readonly List<GameEvent> _events = [];
    private readonly Dictionary<int, Ship> _shipsById = [];
    private int _nextShipId;
    private int _nextBulletId;

    public Map Map { get; } = map;
    public GameConfig Config { get; } = config;
    public IGameRules Rules { get; } = rules;
    public Random Rng { get; } = new(seed);
    public List<Ship> Ships { get; } = [];
    public List<Bullet> Bullets { get; } = [];
    /// <summary>Team balls; empty outside ball mode.</summary>
    public List<Ball> Balls { get; } = [];
    /// <summary>Random events in progress.</summary>
    public ChaosDirector Chaos { get; } = new();
    /// <summary>Events produced by the last <see cref="Start"/> or <see cref="Step"/> call.</summary>
    public IReadOnlyList<GameEvent> Events => _events;
    public float Time { get; private set; }
    public int Tick { get; private set; }

    public Ship AddShip(string name, bool isBot) => AddShip(_nextShipId, name, isBot);

    /// <summary>Adds a ship with a chosen id, e.g. to mirror a server's world. Ids must be unique.</summary>
    public Ship AddShip(int id, string name, bool isBot)
    {
        var ship = new Ship(id, name, isBot, id);
        _shipsById.Add(id, ship);
        Ships.Add(ship);
        _nextShipId = Math.Max(_nextShipId, id + 1);
        return ship;
    }

    /// <summary>
    /// Takes a ship out of the game, dropping any ball it tows and removing its bullets (team and kill credit
    /// need a living owner). Ids are never reused.
    /// </summary>
    public void RemoveShip(Ship ship)
    {
        if (!_shipsById.Remove(ship.Id)) return;
        if (CarriedBy(ship) is { } ball) DropBall(ball);
        Bullets.RemoveAll(b => b.OwnerId == ship.Id);
        ship.Alive = false;
        Ships.Remove(ship);
    }

    public Ship? GetShip(int id) => _shipsById.GetValueOrDefault(id);

    /// <summary>Sets the clock directly, for a client mirroring a server's world.</summary>
    public void SetTick(int tick)
    {
        Tick = tick;
        Time = tick * GameConfig.Dt;
    }

    public void Emit(GameEvent e) => _events.Add(e);

    public void Start()
    {
        _events.Clear();
        Rules.Initialize(this);
    }

    /// <param name="inputs">One input per ship, in the order of <see cref="Ships"/>. Missing entries mean "no input".</param>
    public void Step(ReadOnlySpan<ShipInput> inputs)
    {
        _events.Clear();
        const float dt = GameConfig.Dt;
        Chaos.Update(this, Rules.ControlsLocked || Rules.IsOver);

        foreach (var s in Ships)
        {
            s.PrevPosition = s.Position;
            s.PrevHeading = s.Heading;
        }
        foreach (var b in Bullets) b.PrevPosition = b.Position;
        foreach (var b in Balls) b.PrevPosition = b.Position;

        bool locked = Rules.ControlsLocked;
        for (int i = 0; i < Ships.Count; i++)
        {
            UpdateShip(Ships[i], i < inputs.Length ? inputs[i] : default, dt, locked);
        }

        ResolveShipCollisions();
        UpdateBalls(dt);
        UpdateBullets(dt);

        Time += dt;
        Tick++;
        Rules.Update(this);
    }

    public Vector2 GravityAt(Vector2 p)
    {
        var g = Map.Gravity;
        foreach (var src in Map.GravitySources)
        {
            var d = Map.Delta(p, src.Position);
            float dist = d.Length();
            if (dist < 1f || dist > Config.AttractorRange) continue;
            float mag = MathF.Min(Map.AttractorStrength / (dist * dist), Config.AttractorMaxAccel);
            g += d / dist * (mag * src.Sign);
        }
        g *= Config.GravityScale;
        return Chaos.Active.Count > 0 ? ChaosGravity(p, g) : g;
    }

    /// <summary>Gravity as changed by the random events running now.</summary>
    private Vector2 ChaosGravity(Vector2 p, Vector2 g)
    {
        if (Chaos.Has(ChaosKind.ZeroGravity))
        {
            g = Vector2.Zero;
        }
        else if (Chaos.Has(ChaosKind.HeavyGravity))
        {
            // Strong, but never more than the engine can lift.
            g *= 2.5f;
            g.Y = MathF.Max(g.Y, 180f);
            g = MathUtil.ClampLength(g, Config.ThrustAccel * 0.75f);
        }
        else if (Chaos.Has(ChaosKind.GravityFlip))
        {
            g = -g;
            if (Map.Gravity.LengthSquared() < 20f * 20f) g.Y -= 150f;
        }

        if (Chaos.Get(ChaosKind.BlackHole) is { } hole)
        {
            var d = Map.Delta(p, hole.Point);
            float dist = d.Length();
            if (dist > 1f && dist < ChaosDirector.BlackHoleRange)
            {
                g += d / dist * MathF.Min(ChaosDirector.BlackHoleStrength / (dist * dist), ChaosDirector.BlackHoleMaxAccel);
            }
        }
        if (Chaos.Get(ChaosKind.SolarWind) is { } wind) g += wind.Point * ChaosDirector.WindAccel;
        return g;
    }

    /// <summary>1, or 0 while <see cref="ChaosKind.UnlimitedFuel"/> runs.</summary>
    private float FuelUse => Chaos.Has(ChaosKind.UnlimitedFuel) ? 0f : 1f;

    private float FireFuel => Chaos.Has(ChaosKind.RapidFire) ? 0f : Config.FireFuel * FuelUse;

    private float FireCooldownTime => Config.FireCooldown * (Chaos.Has(ChaosKind.RapidFire) ? 0.33f : 1f);

    public void SpawnShip(Ship s, Vector2 position, float heading)
    {
        s.Position = s.PrevPosition = position;
        s.Velocity = Vector2.Zero;
        s.Heading = s.PrevHeading = heading;
        s.Alive = true;
        s.Fuel = Config.MaxFuel;
        s.Shield = false;
        s.Thrusting = false;
        s.Refueling = false;
        s.SpawnProtection = Config.SpawnProtection;
        s.FireCooldown = 0.3f;
        s.RespawnTimer = 0f;
        Emit(new GameEvent(GameEventType.ShipSpawned, s.Id, Position: position));
    }

    /// <summary>Picks a heading with open space ahead, preferring <paramref name="preferredDirection"/>.</summary>
    public float ChooseSpawnHeading(Vector2 position, Vector2? preferredDirection = null)
    {
        var pref = preferredDirection is { } p ? MathUtil.SafeNormalize(p) : Vector2.Zero;
        float bestScore = float.MinValue, bestAngle = -MathF.PI / 2f;
        for (int k = 0; k < 16; k++)
        {
            float angle = k * MathUtil.TwoPi / 16f;
            var dir = MathUtil.FromAngle(angle);
            float clear = Map.ClearDistance(position, dir, 192f, Config.ShipRadius);
            float score = MathF.Min(clear, 128f) + Vector2.Dot(dir, pref) * 150f + (preferredDirection == null ? -dir.Y * 10f : 0f);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }
        return MathUtil.WrapAngle(bestAngle);
    }

    public void Kill(Ship victim, Ship? killer, DeathCause cause)
    {
        if (!victim.Alive) return;
        victim.Alive = false;
        victim.Deaths++;
        victim.Shield = false;
        victim.Thrusting = false;
        victim.Refueling = false;
        victim.RespawnTimer = Rules.RespawnDelay;
        if (CarriedBy(victim) is { } ball) DropBall(ball);
        Emit(new GameEvent(GameEventType.ShipDestroyed, victim.Id, killer?.Id ?? -1, victim.Position, victim.Velocity, Cause: cause));
        Rules.OnShipDestroyed(this, victim, killer, cause);
    }

    /// <summary>Takes a ship off the field without counting a death, e.g. when it changes team.</summary>
    public void Despawn(Ship s, float respawnDelay)
    {
        if (CarriedBy(s) is { } ball) DropBall(ball);
        s.Alive = false;
        s.Shield = false;
        s.Thrusting = false;
        s.Refueling = false;
        s.RespawnTimer = respawnDelay;
    }

    private void UpdateShip(Ship s, ShipInput input, float dt, bool locked)
    {
        if (!s.Alive)
        {
            s.GrabHeld = false;
            if (s.RespawnTimer > 0f) s.RespawnTimer -= dt;
            if (s.RespawnTimer <= 0f && !Rules.IsOver) Rules.Respawn(this, s);
            return;
        }

        if (!MoveShip(s, input, dt, locked, predicting: false)) return;

        if (Chaos.Get(ChaosKind.BlackHole) is { } hole && !s.IsProtected && Map.Distance(s.Position, hole.Point) < BlackHoleKillRadius)
        {
            Kill(s, null, DeathCause.BlackHole);
            return;
        }

        if (CanFire(s, input)) FireBullet(s);

        bool grabPressed = input.Grab && !s.GrabHeld;
        s.GrabHeld = input.Grab;
        if (grabPressed && Balls.Count > 0) ToggleGrab(s);
    }

    /// <summary>
    /// Advances one ship (and the ball it tows) by a tick on its own, for client-side prediction. Firing only
    /// uses up fuel and starts the cooldown, and crashes bounce instead of killing; the server decides those.
    /// </summary>
    /// <returns>Whether the ship would have fired.</returns>
    public bool PredictShip(Ship s, ShipInput input, Ball? towed)
    {
        const float dt = GameConfig.Dt;
        if (!s.Alive) return false;
        s.PrevPosition = s.Position;
        s.PrevHeading = s.Heading;
        if (towed != null) towed.PrevPosition = towed.Position;

        bool fired = false;
        if (MoveShip(s, input, dt, Rules.ControlsLocked, predicting: true))
        {
            fired = CanFire(s, input);
            if (fired)
            {
                s.FireCooldown = FireCooldownTime;
                s.Fuel -= FireFuel;
                s.SpawnProtection = 0f;
            }
            s.GrabHeld = input.Grab;
        }

        if (towed != null)
        {
            MoveBall(towed, dt);
            ApplyRope(s, towed);
            ResolveBallWalls(towed);
        }
        return fired;
    }

    /// <summary>Ships this close to a black hole's centre are torn apart.</summary>
    public const float BlackHoleKillRadius = 22f;

    private bool CanFire(Ship s, ShipInput input) =>
        input.Fire && !s.Shield && Rules.WeaponsEnabled && s.FireCooldown <= 0f && s.Fuel >= FireFuel;

    /// <summary>Turning, shield, thrust, gravity, movement, walls and refueling.</summary>
    /// <returns>False if the controls are locked or the ship crashed.</returns>
    private bool MoveShip(Ship s, ShipInput input, float dt, bool locked, bool predicting)
    {
        var cfg = Config;
        float turn = Math.Clamp(input.Turn, -1f, 1f);
        if (Chaos.Has(ChaosKind.ReversedControls)) turn = -turn;
        s.Heading = MathUtil.WrapAngle(s.Heading + turn * cfg.TurnSpeed * dt);
        if (locked)
        {
            s.Thrusting = false;
            s.Shield = false;
            return false;
        }

        s.SpawnProtection = MathF.Max(0f, s.SpawnProtection - dt);
        s.FireCooldown = MathF.Max(0f, s.FireCooldown - dt);

        float fuelUse = FuelUse;
        s.Shield = input.Shield && s.Fuel > 0f && !Chaos.Has(ChaosKind.ShieldJam);
        if (s.Shield) s.Fuel -= cfg.ShieldFuelPerSec * fuelUse * dt;

        bool turbo = Chaos.Has(ChaosKind.Turbo);
        s.Thrusting = input.Thrust && s.Fuel > 0f;
        var accel = GravityAt(s.Position);
        if (s.Thrusting)
        {
            accel += MathUtil.FromAngle(s.Heading) * (cfg.ThrustAccel * (turbo ? 2f : 1f));
            s.Fuel -= cfg.ThrustFuelPerSec * fuelUse * dt;
        }

        // Above the top speed (when turbo ends), slow down over a moment rather than all at once.
        float previousSpeed = s.Velocity.Length();
        float limit = MathF.Max(cfg.MaxSpeed * (turbo ? 1.5f : 1f), MathF.Min(previousSpeed, cfg.MaxSpeed * 1.5f) - 600f * dt);
        s.Velocity = MathUtil.ClampLength(s.Velocity + accel * dt, limit);
        if (Chaos.Has(ChaosKind.ThickAir)) s.Velocity *= 1f - 1.6f * dt;
        s.Position = Map.WrapPosition(s.Position + s.Velocity * dt);

        ResolveWallCollisions(s, predicting);
        if (!s.Alive) return false;

        s.Refueling = false;
        foreach (var station in Map.FuelStations)
        {
            if (Map.Distance(s.Position, station) < cfg.RefuelRange)
            {
                s.Refueling = true;
                break;
            }
        }
        s.Fuel = Math.Clamp(s.Fuel + (s.Refueling ? cfg.RefuelPerSec : cfg.PassiveRefuelPerSec) * dt, 0f, cfg.MaxFuel);
        return true;
    }

    public Ball? CarriedBy(Ship s)
    {
        foreach (var b in Balls)
        {
            if (b.State == BallState.Carried && b.CarrierId == s.Id) return b;
        }
        return null;
    }

    /// <summary>Releases the towed ball, or attaches to the nearest free enemy ball in range.</summary>
    private void ToggleGrab(Ship s)
    {
        if (CarriedBy(s) is { } carried)
        {
            DropBall(carried);
            return;
        }

        Ball? best = null;
        float bestDist = Config.GrabRange;
        foreach (var b in Balls)
        {
            if (b.State == BallState.Carried || b.Team == s.Team) continue;
            float d = Map.Distance(s.Position, b.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = b;
            }
        }
        if (best == null) return;

        if (best.State == BallState.Home) best.Velocity = Vector2.Zero;
        best.State = BallState.Carried;
        best.CarrierId = s.Id;
        best.LooseTime = 0f;
        Emit(new GameEvent(GameEventType.BallGrabbed, s.Id, Position: best.Position, Value: best.Team));
    }

    private void DropBall(Ball ball)
    {
        int carrier = ball.CarrierId;
        ball.State = BallState.Loose;
        ball.CarrierId = -1;
        ball.LooseTime = 0f;
        Emit(new GameEvent(GameEventType.BallDropped, carrier, Position: ball.Position, Value: ball.Team));
    }

    private void UpdateBalls(float dt)
    {
        foreach (var ball in Balls)
        {
            if (ball.State == BallState.Home) continue;

            MoveBall(ball, dt);

            if (ball.State == BallState.Carried)
            {
                var carrier = GetShip(ball.CarrierId);
                if (carrier is { Alive: true }) ApplyRope(carrier, ball);
                else DropBall(ball);
            }

            ResolveBallWalls(ball);
            if (ball.State == BallState.Loose) ball.LooseTime += dt;
        }
    }

    private void MoveBall(Ball ball, float dt)
    {
        ball.Velocity += GravityAt(ball.Position) * dt;
        ball.Velocity *= MathF.Max(0f, 1f - 0.15f * dt);
        ball.Position = Map.WrapPosition(ball.Position + ball.Velocity * dt);
    }

    /// <summary>An inextensible rope: when taut it pulls ship and ball together, sharing the correction by mass.</summary>
    private void ApplyRope(Ship ship, Ball ball)
    {
        var d = Map.Delta(ship.Position, ball.Position);
        float dist = d.Length();
        float length = Config.BallRopeLength;
        if (dist <= length || dist < 1e-4f) return;

        var n = d / dist;
        float wShip = 1f, wBall = 1f / Config.BallMass, wSum = wShip + wBall;
        float excess = dist - length;
        ship.Position = Map.WrapPosition(ship.Position + n * (excess * wShip / wSum));
        ball.Position = Map.WrapPosition(ball.Position - n * (excess * wBall / wSum));

        float separating = Vector2.Dot(ball.Velocity - ship.Velocity, n);
        if (separating > 0f)
        {
            float impulse = separating / wSum;
            ship.Velocity += n * (impulse * wShip);
            ball.Velocity -= n * (impulse * wBall);
        }
    }

    private void ResolveBallWalls(Ball ball)
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            if (!Map.FindDeepestContact(ball.Position, Config.BallRadius, out var n, out float depth)) break;
            ball.Position = Map.WrapPosition(ball.Position + n * (depth + 0.01f));
            float vn = Vector2.Dot(ball.Velocity, n);
            if (vn < 0f) ball.Velocity -= n * (vn * 1.5f);
        }
    }

    private void FireBullet(Ship s)
    {
        var cfg = Config;
        var dir = MathUtil.FromAngle(s.Heading);
        var position = Map.WrapPosition(s.Position + dir * (cfg.ShipRadius + 3f));
        s.FireCooldown = FireCooldownTime;
        s.Fuel -= FireFuel;
        s.SpawnProtection = 0f;
        Emit(new GameEvent(GameEventType.ShipFired, s.Id, Position: position));

        if (Map.PointInWall(position))
        {
            Emit(new GameEvent(GameEventType.BulletHitWall, s.Id, Position: position));
            return;
        }
        Bullets.Add(new Bullet
        {
            Id = _nextBulletId++,
            Position = position,
            PrevPosition = position,
            Velocity = s.Velocity + dir * cfg.BulletSpeed,
            OwnerId = s.Id,
        });
    }

    /// <param name="predicting">Bounce off instead of crashing, and emit no events.</param>
    private void ResolveWallCollisions(Ship s, bool predicting)
    {
        var cfg = Config;
        bool rubber = Chaos.Has(ChaosKind.RubberWalls);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            if (!Map.FindDeepestContact(s.Position, cfg.ShipRadius, out var n, out float depth)) break;
            s.Position = Map.WrapPosition(s.Position + n * (depth + 0.01f));

            float vn = Vector2.Dot(s.Velocity, n);
            if (vn >= 0f) continue;
            float impact = -vn;
            if (impact > cfg.CrashSpeed && !rubber)
            {
                if (!s.IsProtected && !predicting)
                {
                    Kill(s, null, DeathCause.Wall);
                    return;
                }
                if (s.Shield) s.Fuel = MathF.Max(0f, s.Fuel - impact * cfg.ShieldImpactFuel);
            }

            var tangential = s.Velocity - n * vn;
            s.Velocity = rubber ? tangential - n * (vn * 0.95f) : tangential * cfg.WallFriction - n * (vn * cfg.WallRestitution);
            if (impact > 40f && !predicting)
            {
                Emit(new GameEvent(GameEventType.WallBounce, s.Id, Position: s.Position - n * cfg.ShipRadius, Value: impact));
            }
        }
    }

    private void ResolveShipCollisions()
    {
        var cfg = Config;
        float minDist = cfg.ShipRadius * 2f;
        for (int i = 0; i < Ships.Count; i++)
        {
            var a = Ships[i];
            if (!a.Alive) continue;
            for (int j = i + 1; j < Ships.Count; j++)
            {
                var b = Ships[j];
                if (!b.Alive) continue;
                var d = Map.Delta(a.Position, b.Position);
                float dist2 = d.LengthSquared();
                if (dist2 >= minDist * minDist) continue;

                float dist = MathF.Sqrt(dist2);
                var n = dist > 1e-4f ? d / dist : Vector2.UnitX;
                float overlap = minDist - dist;
                a.Position = Map.WrapPosition(a.Position - n * (overlap / 2f));
                b.Position = Map.WrapPosition(b.Position + n * (overlap / 2f));

                float relVn = Vector2.Dot(b.Velocity - a.Velocity, n);
                if (relVn >= 0f) continue;

                bool teammates = a.Team != Teams.None && a.Team == b.Team;
                if (cfg.ShipCollisionsKill && !teammates && -relVn > cfg.CrashSpeed)
                {
                    bool aDies = !a.IsProtected, bDies = !b.IsProtected;
                    if (aDies) Kill(a, bDies ? null : b, DeathCause.Collision);
                    if (bDies) Kill(b, aDies ? null : a, DeathCause.Collision);
                    if (aDies || bDies) continue;
                }

                // Equal masses, partially elastic.
                float impulse = -(1f + cfg.WallRestitution) * relVn / 2f;
                a.Velocity -= n * impulse;
                b.Velocity += n * impulse;
            }
        }
    }

    private void UpdateBullets(float dt)
    {
        const int substeps = 2;
        float subDt = dt / substeps;
        foreach (var b in Bullets)
        {
            if (b.Dead) continue;
            b.Age += dt;
            if (b.Age > Config.BulletLife)
            {
                b.Dead = true;
                continue;
            }
            for (int k = 0; k < substeps && !b.Dead; k++)
            {
                b.Position = Map.WrapPosition(b.Position + b.Velocity * subDt);
                if (Map.PointInWall(b.Position))
                {
                    b.Dead = true;
                    Emit(new GameEvent(GameEventType.BulletHitWall, b.OwnerId, Position: b.Position));
                    break;
                }
                CheckBulletHit(b);
            }
        }
        Bullets.RemoveAll(b => b.Dead);
    }

    private void CheckBulletHit(Bullet b)
    {
        var cfg = Config;
        float hitRadius = cfg.ShipRadius + cfg.BulletRadius;
        var owner = GetShip(b.OwnerId);
        foreach (var s in Ships)
        {
            if (!s.Alive) continue;
            if (s.Id == b.OwnerId && b.Age < cfg.SelfHitGrace) continue;
            if (s.Id != b.OwnerId && s.Team != Teams.None && owner?.Team == s.Team) continue;
            if (Map.Delta(s.Position, b.Position).LengthSquared() >= hitRadius * hitRadius) continue;

            b.Dead = true;
            if (s.IsProtected)
            {
                s.Velocity += (b.Velocity - s.Velocity) * 0.03f;
                Emit(new GameEvent(GameEventType.ShieldHit, s.Id, b.OwnerId, b.Position));
            }
            else
            {
                Kill(s, owner, DeathCause.Bullet);
            }
            return;
        }
    }
}
