using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Desktop.Graphics;

/// <summary>Draws the map, ships, bullets and particles in world space.</summary>
public sealed class WorldRenderer
{
    private static readonly Vector2[] ShipShape = [new(15, 0), new(-10, 9), new(-5, 0), new(-10, -9)];

    private readonly Map _map;
    private readonly List<(Vector2 A, Vector2 B)> _edges = [];
    private readonly List<RectangleF> _fillRects = [];
    private readonly List<Vector2[]> _fillTriangles = [];
    private readonly List<Vector2> _wrapOffsets = [];

    /// <summary>Draw other pilots' names under their ships.</summary>
    public bool ShowNames { get; set; } = true;

    public WorldRenderer(Map map)
    {
        _map = map;
        BuildGeometry();
    }

    public void Draw(PrimitiveBatch pb, Camera cam, IMatchView match, ParticleSystem particles, float alpha, float time)
    {
        var world = match.World;
        var view = cam.VisibleBounds;
        var camNum = cam.Position.ToNum();
        Vector2 ToView(System.Numerics.Vector2 p) => (camNum + _map.Delta(camNum, p)).ToXna();

        ComputeWrapOffsets(view);

        pb.Begin(cam.View, BlendState.AlphaBlend);
        foreach (var offset in _wrapOffsets) DrawWallFill(pb, offset, view);
        pb.End();

        pb.Begin(cam.View, PrimitiveBatch.Additive);
        foreach (var offset in _wrapOffsets) DrawWallEdges(pb, offset, view);
        DrawMapObjects(pb, match, ToView, view, time);
        DrawHill(pb, match, ToView, time);
        DrawBlackHole(pb, world, ToView, time);
        particles.Draw(pb, p => ToView(p.ToNum()), view);

        foreach (var b in world.Bullets)
        {
            var pos = Interpolate(b.PrevPosition, b.Position, alpha);
            var p = ToView(pos);
            if (!view.Contains(p, 20f)) continue;
            var owner = world.GetShip(b.OwnerId);
            var color = Color.Lerp(owner != null ? Palette.Ship(owner) : Color.White, Color.White, 0.5f);
            var dir = MathUtil.SafeNormalize(b.Velocity).ToXna();
            pb.GlowLine(p - dir * 7f, p, 2.5f, color);
        }

        foreach (var ball in world.Balls) DrawBall(pb, world, ball, ToView, view, alpha, time);

        foreach (var s in world.Ships)
        {
            if (!s.Alive) continue;
            var p = ToView(Interpolate(s.PrevPosition, s.Position, alpha));
            if (!view.Contains(p, 60f)) continue;
            float heading = s.PrevHeading + MathUtil.WrapAngle(s.Heading - s.PrevHeading) * alpha;
            DrawShip(pb, s, p, heading, time, s == match.Player);
        }

        if (match.Player is { Alive: true } player && Objective(match, player) is var (target, arrowColor, hideWithin))
        {
            DrawArrow(pb, ToView(Interpolate(player.PrevPosition, player.Position, alpha)), player, target, arrowColor, hideWithin);
        }
        pb.End();

        if (world.Chaos.Has(ChaosKind.Blackout))
        {
            var center = match.Player is { Alive: true } me ? ToView(Interpolate(me.PrevPosition, me.Position, alpha)) : cam.Position;
            DrawBlackout(pb, cam, center, view);
        }
    }

    /// <summary>Darkness everywhere but a small pool of light around the ship.</summary>
    private static void DrawBlackout(PrimitiveBatch pb, Camera cam, Vector2 center, RectangleF view)
    {
        const float clear = 140f, dark = 380f, darkness = 0.97f;
        float farthest = 0f;
        foreach (var corner in new[] { new Vector2(view.X, view.Y), new Vector2(view.Right, view.Y), new Vector2(view.X, view.Bottom), new Vector2(view.Right, view.Bottom) })
        {
            farthest = MathF.Max(farthest, Vector2.Distance(center, corner));
        }

        pb.Begin(cam.View, BlendState.AlphaBlend);
        // A few rings approximate a smooth fall-off.
        const int steps = 6;
        for (int i = 0; i < steps; i++)
        {
            float t0 = i / (float)steps, t1 = (i + 1) / (float)steps;
            pb.Ring(center, clear + (dark - clear) * t0, clear + (dark - clear) * t1,
                Color.Black * (darkness * Smooth(t0)), Color.Black * (darkness * Smooth(t1)), 72);
        }
        pb.Ring(center, dark, MathF.Max(dark + 10f, farthest + 20f), Color.Black * darkness, Color.Black * darkness, 72);
        pb.End();

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }

    /// <summary>King of the hill: the zone, in the colour of whoever holds it.</summary>
    private void DrawHill(PrimitiveBatch pb, IMatchView match, Func<System.Numerics.Vector2, Vector2> toView, float time)
    {
        if (match.World.Rules is not KingOfTheHillRules hill) return;
        var p = toView(hill.Hill);
        var holder = match.World.GetShip(hill.Holder);
        var color = hill.Holder switch
        {
            KingOfTheHillRules.Contested => ((int)(time * 6f) & 1) == 0 ? Palette.Warning : Palette.Hill,
            _ when holder == null => Palette.Hill * 0.5f,
            _ => Palette.Ship(holder),
        };
        float r = KingOfTheHillRules.HillRadius;
        pb.FilledCircle(p, r, color * 0.05f, 48);
        pb.Circle(p, r, 2f, color * (0.7f + 0.3f * MathF.Sin(time * 3f)), 64, glow: 0.8f, dashed: true, rotation: time * 0.4f);
        pb.Circle(p, r * 0.15f, 1.5f, color * 0.6f, 16);
        // A crown, so the hill reads at a glance.
        var crown = new[] { new Vector2(-14, 6), new Vector2(-14, -6), new Vector2(-7, 0), new Vector2(0, -9), new Vector2(7, 0), new Vector2(14, -6), new Vector2(14, 6) };
        for (int i = 0; i < crown.Length; i++) crown[i] += p;
        pb.Polyline(crown, true, 2f, color, 0.8f);
    }

    /// <summary>The black hole event: rings spiralling into a point.</summary>
    private static void DrawBlackHole(PrimitiveBatch pb, World world, Func<System.Numerics.Vector2, Vector2> toView, float time)
    {
        if (world.Chaos.Get(ChaosKind.BlackHole) is not { } hole) return;
        var p = toView(hole.Point);
        float fade = Math.Clamp(MathF.Min(hole.Remaining, hole.Duration - hole.Remaining) * 2f, 0f, 1f);
        for (int i = 0; i < 5; i++)
        {
            // Each ring shrinks towards the centre and starts again from the outside.
            float phase = (time * 0.5f + i / 5f) % 1f;
            float radius = 30f + (1f - phase) * 190f;
            pb.Circle(p, radius, 1.5f, Palette.Chaos * (phase * 0.6f * fade), 48, dashed: true, rotation: time * (1.5f + i * 0.3f));
        }
        pb.Circle(p, World.BlackHoleKillRadius, 3f, Palette.Chaos * fade, 24, glow: 1.5f);
        pb.Circle(p, World.BlackHoleKillRadius * 0.5f, 2f, Color.White * (0.6f * fade), 16);
    }

    /// <summary>Where the player should be heading: the next checkpoint, or the relevant ball or treasure.</summary>
    private (System.Numerics.Vector2 Target, Color Color, float HideWithin)? Objective(IMatchView match, Ship player)
    {
        var world = match.World;
        if (world.Rules.Mode == GameModeKind.Race)
        {
            if (player.Finished) return null;
            float zone = world.Rules is RaceRules race ? race.CheckpointRadius : 0f;
            return (_map.Checkpoints[player.NextCheckpoint], Palette.Checkpoint * 0.8f, zone + 60f);
        }
        if (world.Rules is KingOfTheHillRules hill) return (hill.Hill, Palette.Hill * 0.8f, KingOfTheHillRules.HillRadius * 0.8f);
        if (world.Rules.Mode != GameModeKind.Ball || _map.TreasureOf(player.Team) is not { } home) return null;

        if (world.CarriedBy(player) != null) return (home, Palette.Team(player.Team), 80f);
        var ownBall = world.Balls.FirstOrDefault(b => b.Team == player.Team);
        if (ownBall is { State: not BallState.Home }) return (ownBall.Position, Palette.Warning, 60f);
        var enemyBall = world.Balls.FirstOrDefault(b => b.Team != player.Team);
        if (enemyBall is { State: not BallState.Carried }) return (enemyBall.Position, Palette.Team(enemyBall.Team) * 0.8f, 80f);
        return null;
    }

    private void DrawBall(PrimitiveBatch pb, World world, Ball ball, Func<System.Numerics.Vector2, Vector2> toView,
        RectangleF view, float alpha, float time)
    {
        var p = toView(Interpolate(ball.PrevPosition, ball.Position, alpha));
        var color = Palette.Team(ball.Team);
        if (ball.State == BallState.Carried && world.GetShip(ball.CarrierId) is { Alive: true } carrier)
        {
            var c = toView(Interpolate(carrier.PrevPosition, carrier.Position, alpha));
            pb.GlowLine(c, p, 1.5f, Color.Lerp(color, Color.White, 0.5f) * 0.8f, 0.6f);
        }
        if (!view.Contains(p, 40f)) return;

        float radius = world.Config.BallRadius;
        bool blink = ball.State == BallState.Loose && ((int)(time * 6f) & 1) == 0;
        pb.Circle(p, radius, 2.5f, blink ? Color.White : color, 20, glow: 1.2f);
        pb.Circle(p, radius * 0.45f, 1.5f, color * 0.7f, 12);
    }

    private void DrawArrow(PrimitiveBatch pb, Vector2 shipPos, Ship player, System.Numerics.Vector2 target, Color color, float hideWithin)
    {
        var d = _map.Delta(player.Position, target).ToXna();
        float dist = d.Length();
        if (dist < hideWithin) return;
        var dir = d / dist;
        var perp = new Vector2(-dir.Y, dir.X);
        var tip = shipPos + dir * 58f;
        var baseCenter = shipPos + dir * 46f;
        pb.GlowLine(tip, baseCenter + perp * 7f, 2f, color);
        pb.GlowLine(tip, baseCenter - perp * 7f, 2f, color);
    }

    private System.Numerics.Vector2 Interpolate(System.Numerics.Vector2 prev, System.Numerics.Vector2 current, float alpha) =>
        prev + _map.Delta(prev, current) * alpha;

    private void DrawShip(PrimitiveBatch pb, Ship s, Vector2 pos, float heading, float time, bool isPlayer)
    {
        var color = Palette.Ship(s);
        float c = MathF.Cos(heading), sn = MathF.Sin(heading);
        Vector2 Rot(Vector2 v) => pos + new Vector2(v.X * c - v.Y * sn, v.X * sn + v.Y * c);

        Span<Vector2> pts = stackalloc Vector2[ShipShape.Length];
        for (int i = 0; i < pts.Length; i++) pts[i] = Rot(ShipShape[i]);
        pb.Polyline(pts, true, 2f, color, glow: 1f);

        if (s.Thrusting)
        {
            float flicker = 10f + 6f * MathF.Abs(MathF.Sin(time * 40f + s.Id));
            var flameColor = new Color(255, 160, 60);
            pb.GlowLine(Rot(new Vector2(-7, 4)), Rot(new Vector2(-7 - flicker, 0)), 2f, flameColor, 0.8f);
            pb.GlowLine(Rot(new Vector2(-7, -4)), Rot(new Vector2(-7 - flicker, 0)), 2f, flameColor, 0.8f);
        }

        if (s.Shield)
        {
            float pulse = 0.7f + 0.3f * MathF.Sin(time * 12f);
            pb.Circle(pos, 22f, 2f, Color.Lerp(color, Color.White, 0.4f) * pulse, 28, glow: 1f);
        }
        else if (s.SpawnProtection > 0f && ((int)(time * 8f) & 1) == 0)
        {
            pb.Circle(pos, 22f, 1.5f, color * 0.6f, 24, dashed: true, rotation: time * 2f);
        }

        if (!isPlayer && ShowNames)
        {
            VectorFont.Draw(pb, s.Name, pos + new Vector2(0, 24), 8f, color * 0.55f, TextAlign.Center, 1.1f, glow: 0f);
        }
    }

    private void DrawMapObjects(PrimitiveBatch pb, IMatchView match, Func<System.Numerics.Vector2, Vector2> toView, RectangleF view, float time)
    {
        var world = match.World;

        for (int i = 0; i < _map.Bases.Count; i++)
        {
            var p = toView(_map.Bases[i]);
            if (!view.Contains(p, 40f)) continue;
            int team = i < _map.BaseTeams.Count ? _map.BaseTeams[i] : Teams.None;
            var color = team == Teams.None ? Palette.Base : Palette.Team(team) * 0.6f;
            pb.GlowLine(p + new Vector2(-13, 15), p + new Vector2(13, 15), 2f, color, 0.6f);
        }

        foreach (var t in world.Rules.Mode == GameModeKind.Ball ? _map.Treasures : [])
        {
            var p = toView(t.Position);
            if (!view.Contains(p, 60f)) continue;
            var color = Palette.Team(t.Team);
            const float h = 24f;
            // An open-topped box, like XPilot's treasure.
            pb.GlowLine(p + new Vector2(-h, -h * 0.4f), p + new Vector2(-h, h), 2.5f, color);
            pb.GlowLine(p + new Vector2(-h, h), p + new Vector2(h, h), 2.5f, color);
            pb.GlowLine(p + new Vector2(h, h), p + new Vector2(h, -h * 0.4f), 2.5f, color);
            pb.Circle(p, BallRules.CaptureRadius, 1f, color * (0.25f + 0.1f * MathF.Sin(time * 3f)), 32, dashed: true, rotation: -time * 0.5f);
        }

        foreach (var f in _map.FuelStations)
        {
            var p = toView(f);
            if (!view.Contains(p, world.Config.RefuelRange + 40f)) continue;
            bool active = false;
            foreach (var s in world.Ships)
            {
                if (!s.Alive || !s.Refueling || _map.Distance(s.Position, f) > world.Config.RefuelRange) continue;
                active = true;
                var sp = toView(s.Position);
                float flow = (time * 3f) % 1f;
                pb.GlowLine(p, Vector2.Lerp(p, sp, flow), 1.5f, Palette.Fuel * 0.6f, 0.5f);
            }
            float brightness = active ? 1f : 0.7f;
            pb.RectOutline(p.X - 12, p.Y - 12, 24, 24, 2f, Palette.Fuel * brightness);
            VectorFont.Draw(pb, "F", p - new Vector2(0, 6), 12f, Palette.Fuel * brightness, TextAlign.Center);
        }

        foreach (var g in _map.GravitySources)
        {
            var p = toView(g.Position);
            if (!view.Contains(p, 80f)) continue;
            var color = g.Sign > 0 ? Palette.Attractor : Palette.Repeller;
            for (int k = 0; k < 3; k++)
            {
                float phase = (time * 0.6f + k / 3f) % 1f;
                float t = g.Sign > 0 ? 1f - phase : phase;
                float radius = 8f + t * 48f;
                pb.Circle(p, radius, 1.5f, color * (0.8f * (1f - t * 0.8f)), 28, glow: 0.6f);
            }
            pb.FilledCircle(p, 4f, color);
        }

        if (world.Rules.Mode == GameModeKind.Race)
        {
            int next = match.Player is { Finished: false } player ? player.NextCheckpoint : -1;
            for (int i = 0; i < _map.Checkpoints.Count; i++)
            {
                var p = toView(_map.Checkpoints[i]);
                float radius = world.Rules is RaceRules race ? race.CheckpointRadius : 0f;
                if (!view.Contains(p, radius + 20f)) continue;
                bool isNext = i == next;
                var color = isNext ? Palette.Checkpoint * (0.75f + 0.25f * MathF.Sin(time * 6f)) : Palette.CheckpointDim;
                pb.Circle(p, radius, isNext ? 2.5f : 1.5f, color, 40, glow: isNext ? 1f : 0.3f, dashed: true, rotation: time * 0.4f);
                VectorFont.Draw(pb, (i + 1).ToString(), p - new Vector2(0, 11), 22f, color, TextAlign.Center);
                if (i == _map.Checkpoints.Count - 1)
                {
                    VectorFont.Draw(pb, "FINISH", p + new Vector2(0, 18), 9f, color * 0.8f, TextAlign.Center);
                }
            }
        }
    }

    private void ComputeWrapOffsets(RectangleF view)
    {
        _wrapOffsets.Clear();
        if (!_map.Wrap)
        {
            _wrapOffsets.Add(Vector2.Zero);
            return;
        }
        float w = _map.PixelWidth, h = _map.PixelHeight;
        for (int oy = -1; oy <= 1; oy++)
        for (int ox = -1; ox <= 1; ox++)
        {
            var bounds = new RectangleF(ox * w, oy * h, w, h);
            if (bounds.Intersects(view)) _wrapOffsets.Add(new Vector2(ox * w, oy * h));
        }
    }

    private void DrawWallFill(PrimitiveBatch pb, Vector2 offset, RectangleF view)
    {
        foreach (var r in _fillRects)
        {
            var moved = r with { X = r.X + offset.X, Y = r.Y + offset.Y };
            if (moved.Intersects(view)) pb.Rect(moved.X, moved.Y, moved.Width, moved.Height, Palette.WallFill);
        }
        foreach (var t in _fillTriangles)
        {
            if (!view.Contains(t[0] + offset, Map.TileSize)) continue;
            pb.Triangle(t[0] + offset, t[1] + offset, t[2] + offset, Palette.WallFill);
        }
    }

    private void DrawWallEdges(PrimitiveBatch pb, Vector2 offset, RectangleF view)
    {
        foreach (var (a, b) in _edges)
        {
            var pa = a + offset;
            var pb2 = b + offset;
            var box = new RectangleF(MathF.Min(pa.X, pb2.X), MathF.Min(pa.Y, pb2.Y), MathF.Abs(pa.X - pb2.X) + 1, MathF.Abs(pa.Y - pb2.Y) + 1);
            if (!box.Intersects(view)) continue;
            pb.GlowLine(pa, pb2, 2f, Palette.WallEdge, 1f);
        }
    }

    private void BuildGeometry()
    {
        const float T = Map.TileSize;
        var horizontal = new Dictionary<float, List<(float From, float To)>>();
        var vertical = new Dictionary<float, List<(float From, float To)>>();

        void AddH(float y, float x0, float x1) => GetList(horizontal, y).Add((x0, x1));
        void AddV(float x, float y0, float y1) => GetList(vertical, x).Add((y0, y1));
        Span<System.Numerics.Vector2> poly = stackalloc System.Numerics.Vector2[4];

        for (int y = 0; y < _map.Height; y++)
        {
            int runStart = -1;
            for (int x = 0; x <= _map.Width; x++)
            {
                var shape = x < _map.Width ? _map.GetTile(x, y) : TileShape.Empty;
                if (shape == TileShape.Full && runStart < 0) runStart = x;
                if (shape != TileShape.Full && runStart >= 0)
                {
                    _fillRects.Add(new RectangleF(runStart * T, y * T, (x - runStart) * T, T));
                    runStart = -1;
                }
                if (x >= _map.Width || shape == TileShape.Empty) continue;

                float px = x * T, py = y * T;
                if (shape != TileShape.Full)
                {
                    Map.GetShapePolygon(shape, px, py, poly);
                    _fillTriangles.Add([poly[0].ToXna(), poly[1].ToXna(), poly[2].ToXna()]);
                    var (h0, h1) = shape switch
                    {
                        TileShape.SolidTopLeft or TileShape.SolidBottomRight => (new Vector2(px + T, py), new Vector2(px, py + T)),
                        _ => (new Vector2(px, py), new Vector2(px + T, py + T)),
                    };
                    _edges.Add((h0, h1));
                }

                if (Exposed(shape, x, y, TileSide.Top, 0, -1)) AddH(py, px, px + T);
                if (Exposed(shape, x, y, TileSide.Bottom, 0, 1)) AddH(py + T, px, px + T);
                if (Exposed(shape, x, y, TileSide.Left, -1, 0)) AddV(px, py, py + T);
                if (Exposed(shape, x, y, TileSide.Right, 1, 0)) AddV(px + T, py, py + T);
            }
        }

        foreach (var (y, spans) in horizontal)
        {
            foreach (var (from, to) in Merge(spans)) _edges.Add((new Vector2(from, y), new Vector2(to, y)));
        }
        foreach (var (x, spans) in vertical)
        {
            foreach (var (from, to) in Merge(spans)) _edges.Add((new Vector2(x, from), new Vector2(x, to)));
        }
    }

    private bool Exposed(TileShape shape, int x, int y, TileSide side, int dx, int dy)
    {
        if (!Map.IsSideSolid(shape, side)) return false;
        var opposite = side switch
        {
            TileSide.Top => TileSide.Bottom,
            TileSide.Bottom => TileSide.Top,
            TileSide.Left => TileSide.Right,
            _ => TileSide.Left,
        };
        return !Map.IsSideSolid(_map.GetTile(x + dx, y + dy), opposite);
    }

    private static List<(float, float)> GetList(Dictionary<float, List<(float, float)>> dict, float key)
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = [];
        return list;
    }

    private static IEnumerable<(float From, float To)> Merge(List<(float From, float To)> spans)
    {
        spans.Sort((a, b) => a.From.CompareTo(b.From));
        float start = spans[0].From, end = spans[0].To;
        for (int i = 1; i < spans.Count; i++)
        {
            if (spans[i].From <= end + 0.01f)
            {
                end = MathF.Max(end, spans[i].To);
                continue;
            }
            yield return (start, end);
            (start, end) = spans[i];
        }
        yield return (start, end);
    }
}
