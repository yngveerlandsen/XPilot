using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Desktop.Graphics;

/// <summary>Screen-space overlay: score, fuel, timers, radar, kill feed and center messages.</summary>
public sealed class Hud
{
    private const float FeedLifetime = 6f;

    private readonly Map _map;
    private readonly List<(RectangleF Rect, bool Diagonal)> _radarWalls = [];
    private readonly List<(string Text, Color Color, float Age)> _feed = [];
    private string? _centerText;
    private string? _centerSubtext;
    private Color _centerColor;
    private float _centerTimer;
    private float _centerDuration;

    public Hud(Map map)
    {
        _map = map;
        for (int y = 0; y < map.Height; y++)
        {
            int runStart = -1;
            for (int x = 0; x <= map.Width; x++)
            {
                var shape = x < map.Width ? map.GetTile(x, y) : TileShape.Empty;
                bool solid = shape != TileShape.Empty;
                if (solid && runStart < 0) runStart = x;
                if (!solid && runStart >= 0)
                {
                    _radarWalls.Add((new RectangleF(runStart, y, x - runStart, 1), false));
                    runStart = -1;
                }
            }
        }
    }

    public void AddFeed(string text, Color color)
    {
        _feed.Add((text, color, 0f));
        if (_feed.Count > 6) _feed.RemoveAt(0);
    }

    public void ShowCenter(string text, Color color, float seconds, string? subtext = null)
    {
        _centerText = text;
        _centerSubtext = subtext;
        _centerColor = color;
        _centerTimer = seconds;
        _centerDuration = seconds;
    }

    public void Update(float dt)
    {
        for (int i = _feed.Count - 1; i >= 0; i--)
        {
            var f = _feed[i];
            f.Age += dt;
            if (f.Age > FeedLifetime) _feed.RemoveAt(i);
            else _feed[i] = f;
        }
        if (_centerTimer > 0f) _centerTimer -= dt;
    }

    public void Draw(PrimitiveBatch pb, Viewport vp, Match match, bool showScoreboard, float time)
    {
        float s = vp.Height / 720f;
        var world = match.World;
        var player = match.Player;

        var radar = RadarRect(vp, s);
        pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
        pb.Rect(radar.X, radar.Y, radar.Width, radar.Height, Palette.Panel * 0.75f);
        DrawRadarWalls(pb, radar);
        pb.End();

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        pb.RectOutline(radar.X, radar.Y, radar.Width, radar.Height, 1f, Palette.WallEdge * 0.5f);
        DrawRadarObjects(pb, radar, match, time);
        DrawFeed(pb, vp, radar, s);

        if (world.Rules is RaceRules race) DrawRaceInfo(pb, vp, s, match, race);
        else if (world.Rules is DogfightRules dogfight) DrawDogfightInfo(pb, vp, s, match, dogfight);
        else if (world.Rules is BallRules ball) DrawBallInfo(pb, vp, s, match, ball, time);

        if (player != null) DrawShipStatus(pb, vp, s, world, player, time);
        DrawCenterMessage(pb, vp, s);
        pb.End();

        if (showScoreboard) DrawScoreboard(pb, vp, s, match);
    }

    private static RectangleF RadarRect(Viewport vp, float s)
    {
        float maxW = 230f * s, maxH = 170f * s;
        return new RectangleF(vp.Width - maxW - 14f * s, 14f * s, maxW, maxH);
    }

    private (float Scale, Vector2 Origin) RadarTransform(RectangleF radar)
    {
        float scale = MathF.Min(radar.Width / _map.Width, radar.Height / _map.Height);
        var origin = new Vector2(
            radar.X + (radar.Width - _map.Width * scale) / 2f,
            radar.Y + (radar.Height - _map.Height * scale) / 2f);
        return (scale, origin);
    }

    private void DrawRadarWalls(PrimitiveBatch pb, RectangleF radar)
    {
        var (scale, origin) = RadarTransform(radar);
        var color = new Color(30, 55, 110);
        foreach (var (r, _) in _radarWalls)
        {
            pb.Rect(origin.X + r.X * scale, origin.Y + r.Y * scale, r.Width * scale, MathF.Max(1f, r.Height * scale), color);
        }
    }

    private void DrawRadarObjects(PrimitiveBatch pb, RectangleF radar, Match match, float time)
    {
        var (scale, origin) = RadarTransform(radar);
        Vector2 ToRadar(System.Numerics.Vector2 p) => origin + p.ToXna() / Map.TileSize * scale;

        foreach (var f in _map.FuelStations) pb.FilledCircle(ToRadar(f), 2f, Palette.Fuel * 0.8f, 8);
        foreach (var t in _map.Treasures)
        {
            var c = ToRadar(t.Position);
            pb.RectOutline(c.X - 4f, c.Y - 4f, 8f, 8f, 1f, Palette.Team(t.Team));
        }
        foreach (var ball in match.World.Balls)
        {
            if (ball.State == BallState.Loose && ((int)(time * 6f) & 1) == 1) continue;
            pb.Circle(ToRadar(ball.Position), 3f, 1.5f, Palette.Team(ball.Team), 10);
        }

        if (match.World.Rules.Mode == GameModeKind.Race)
        {
            int next = match.Player?.NextCheckpoint ?? -1;
            for (int i = 0; i < _map.Checkpoints.Count; i++)
            {
                bool isNext = i == next;
                pb.Circle(ToRadar(_map.Checkpoints[i]), isNext ? 4f : 2.5f, 1f, isNext ? Palette.Checkpoint : Palette.CheckpointDim, 10);
            }
        }

        bool blink = ((int)(time * 4f) & 1) == 0;
        foreach (var ship in match.World.Ships)
        {
            if (!ship.Alive) continue;
            var p = ToRadar(ship.Position);
            if (ship == match.Player)
            {
                if (blink) pb.FilledCircle(p, 3.5f, Color.White, 10);
                pb.Circle(p, 5f, 1f, Color.White * 0.6f, 12);
            }
            else
            {
                pb.FilledCircle(p, 2.5f, Palette.Ship(ship), 8);
            }
        }
    }

    private void DrawFeed(PrimitiveBatch pb, Viewport vp, RectangleF radar, float s)
    {
        float y = radar.Bottom + 12f * s;
        foreach (var (text, color, age) in _feed)
        {
            float fade = age > FeedLifetime - 1f ? FeedLifetime - age : 1f;
            VectorFont.Draw(pb, text, new Vector2(vp.Width - 14f * s, y), 11f * s, color * fade, TextAlign.Right);
            y += 18f * s;
        }
    }

    private static void DrawBallInfo(PrimitiveBatch pb, Viewport vp, float s, Match match, BallRules rules, float time)
    {
        var world = match.World;
        var player = match.Player;
        float cx = vp.Width / 2f;

        // "RED 1 : 2 BLUE" with each half in its team color.
        string red = $"RED {rules.TeamScore(Teams.Red)}", blue = $"{rules.TeamScore(Teams.Blue)} BLUE";
        float size = 20f * s;
        VectorFont.Draw(pb, red, new Vector2(cx - 14 * s, 16 * s), size, Palette.RedTeam, TextAlign.Right);
        VectorFont.Draw(pb, ":", new Vector2(cx, 16 * s), size, Palette.Text, TextAlign.Center);
        VectorFont.Draw(pb, blue, new Vector2(cx + 14 * s, 16 * s), size, Palette.BlueTeam);

        string clock = rules.TimeLimit > 0 ? FormatClock(rules.TimeRemaining(world)) + "   " : "";
        VectorFont.Draw(pb, $"{clock}FIRST TO {rules.CaptureLimit}", new Vector2(cx, 46 * s), 10f * s, Palette.TextDim, TextAlign.Center);

        if (player == null) return;
        var teamColor = Palette.Team(player.Team);
        VectorFont.Draw(pb, $"{Teams.Name(player.Team).ToUpperInvariant()} TEAM", new Vector2(16 * s, 16 * s), 22f * s, teamColor);
        VectorFont.Draw(pb, $"SCORE {player.Score}   KILLS {player.Kills}   DEATHS {player.Deaths}", new Vector2(16 * s, 48 * s), 11f * s, Palette.TextDim);

        float y = 70 * s;
        foreach (var ball in world.Balls)
        {
            bool ours = ball.Team == player.Team;
            var carrier = world.GetShip(ball.CarrierId);
            string state = ball.State switch
            {
                BallState.Home => ours ? "SAFE" : "AT HOME",
                BallState.Carried when carrier == player => "YOU HAVE IT - TOW IT HOME",
                BallState.Carried => $"TAKEN BY {carrier?.Name.ToUpperInvariant()}",
                _ => $"DROPPED ({MathF.Ceiling(BallRules.ReturnTime - ball.LooseTime)})",
            };
            bool alarm = ours && ball.State != BallState.Home;
            var color = alarm && ((int)(time * 4f) & 1) == 0 ? Palette.Warning : Palette.Team(ball.Team);
            VectorFont.Draw(pb, $"{(ours ? "OUR" : "ENEMY")} BALL: {state}", new Vector2(16 * s, y), 11f * s, color);
            y += 18 * s;
        }
    }

    private static void DrawDogfightInfo(PrimitiveBatch pb, Viewport vp, float s, Match match, DogfightRules rules)
    {
        var player = match.Player;
        if (player != null)
        {
            VectorFont.Draw(pb, $"SCORE {player.Score}", new Vector2(16 * s, 16 * s), 22f * s, Palette.Accent);
            VectorFont.Draw(pb, $"KILLS {player.Kills}   DEATHS {player.Deaths}", new Vector2(16 * s, 48 * s), 11f * s, Palette.TextDim);
            var standings = rules.GetStandings(match.World);
            int rank = standings.ToList().IndexOf(player) + 1;
            VectorFont.Draw(pb, $"RANK {rank}/{standings.Count}   FIRST TO {rules.ScoreLimit}", new Vector2(16 * s, 68 * s), 11f * s, Palette.TextDim);
        }

        if (rules.TimeLimit > 0)
        {
            float remaining = rules.TimeRemaining(match.World);
            var color = remaining < 30f ? Palette.Warning : Palette.Text;
            VectorFont.Draw(pb, FormatClock(remaining), new Vector2(vp.Width / 2f, 16 * s), 20f * s, color, TextAlign.Center);
        }
    }

    private static void DrawRaceInfo(PrimitiveBatch pb, Viewport vp, float s, Match match, RaceRules rules)
    {
        var world = match.World;
        var player = match.Player;
        if (player != null)
        {
            var standings = rules.GetStandings(world).ToList();
            int position = standings.IndexOf(player) + 1;
            int lap = Math.Min(player.Lap + 1, rules.Laps);
            VectorFont.Draw(pb, player.Finished ? "FINISHED" : $"LAP {lap}/{rules.Laps}", new Vector2(16 * s, 16 * s), 22f * s, Palette.Accent);
            VectorFont.Draw(pb, $"POS {position}/{standings.Count}", new Vector2(16 * s, 48 * s), 14f * s, Palette.Text);

            float lapTime = player.Finished || rules.ControlsLocked ? 0f : world.Time - player.LapStartTime;
            float y = 74 * s;
            if (!player.Finished)
            {
                VectorFont.Draw(pb, $"LAP  {FormatTime(lapTime)}", new Vector2(16 * s, y), 11f * s, Palette.TextDim);
                y += 18 * s;
            }
            if (player.LapTimes.Count > 0)
            {
                VectorFont.Draw(pb, $"BEST {FormatTime(player.BestLap)}", new Vector2(16 * s, y), 11f * s, Palette.TextDim);
            }
        }

        float total = MathF.Max(0f, world.Time - RaceRules.CountdownSeconds);
        if (player is { Finished: true }) total = player.FinishTime;
        VectorFont.Draw(pb, FormatTime(total), new Vector2(vp.Width / 2f, 16 * s), 20f * s, Palette.Text, TextAlign.Center);
        if (rules.GraceRemaining(world) is { } grace && !rules.IsOver)
        {
            VectorFont.Draw(pb, $"RACE ENDS IN {MathF.Ceiling(grace)}", new Vector2(vp.Width / 2f, 44 * s), 11f * s, Palette.Warning, TextAlign.Center);
        }
    }

    private static void DrawShipStatus(PrimitiveBatch pb, Viewport vp, float s, World world, Ship player, float time)
    {
        float x = 16 * s, y = vp.Height - 40 * s;
        float barW = 220 * s, barH = 10 * s;
        float fuel = player.Fuel / world.Config.MaxFuel;
        var fuelColor = fuel < 0.2f && ((int)(time * 4f) & 1) == 0 ? Palette.Warning : Palette.Fuel;
        VectorFont.Draw(pb, "FUEL", new Vector2(x, y - 18 * s), 10f * s, Palette.TextDim);
        pb.RectOutline(x, y, barW, barH, 1f, fuelColor * 0.6f);
        pb.Rect(x + 2, y + 2, (barW - 4) * fuel, barH - 4, fuelColor * 0.7f);
        if (player.Refueling) VectorFont.Draw(pb, "REFUELING", new Vector2(x + 60 * s, y - 18 * s), 10f * s, Palette.Fuel);

        if (world.Rules.WeaponsEnabled)
        {
            var shieldColor = player.Shield ? Palette.Accent : Palette.TextDim * 0.5f;
            VectorFont.Draw(pb, "SHIELD", new Vector2(x + barW + 20 * s, y - 2 * s), 12f * s, shieldColor);
        }

        if (!player.Alive && !world.Rules.IsOver)
        {
            VectorFont.Draw(pb, $"RESPAWN IN {MathF.Max(0f, player.RespawnTimer):0.0}",
                new Vector2(vp.Width / 2f, vp.Height * 0.62f), 16f * s, Palette.Text, TextAlign.Center);
        }

        float speed = player.Velocity.Length();
        VectorFont.Draw(pb, $"SPEED {speed:0}", new Vector2(vp.Width - 16 * s, vp.Height - 30 * s), 11f * s, Palette.TextDim, TextAlign.Right);
    }

    private void DrawCenterMessage(PrimitiveBatch pb, Viewport vp, float s)
    {
        if (_centerTimer <= 0f || _centerText == null) return;
        float fade = MathF.Min(1f, _centerTimer / MathF.Min(0.5f, _centerDuration));
        var pos = new Vector2(vp.Width / 2f, vp.Height * 0.3f);
        VectorFont.Draw(pb, _centerText, pos, 40f * s, _centerColor * fade, TextAlign.Center, 3f * s);
        if (_centerSubtext != null)
        {
            VectorFont.Draw(pb, _centerSubtext, pos + new Vector2(0, 56 * s), 14f * s, Palette.Text * fade, TextAlign.Center);
        }
    }

    public static void DrawScoreboard(PrimitiveBatch pb, Viewport vp, float s, Match match)
    {
        var world = match.World;
        var standings = world.Rules.GetStandings(world);
        bool race = world.Rules.Mode == GameModeKind.Race;
        float rowH = 26f * s;
        float w = 560f * s, h = (standings.Count + 2) * rowH + 30f * s;
        float x = (vp.Width - w) / 2f, y = (vp.Height - h) / 2f;

        pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
        pb.Rect(x, y, w, h, Palette.Panel * 0.9f);
        pb.End();

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        pb.RectOutline(x, y, w, h, 1.5f, Palette.WallEdge * 0.7f);
        float ty = y + 18f * s;
        float size = 12f * s;
        var header = Palette.TextDim;
        VectorFont.Draw(pb, "#", new Vector2(x + 20 * s, ty), size, header);
        VectorFont.Draw(pb, "PILOT", new Vector2(x + 60 * s, ty), size, header);
        if (race)
        {
            VectorFont.Draw(pb, "LAPS", new Vector2(x + 300 * s, ty), size, header, TextAlign.Right);
            VectorFont.Draw(pb, "BEST", new Vector2(x + 410 * s, ty), size, header, TextAlign.Right);
            VectorFont.Draw(pb, "TIME", new Vector2(x + 530 * s, ty), size, header, TextAlign.Right);
        }
        else
        {
            VectorFont.Draw(pb, "SCORE", new Vector2(x + 330 * s, ty), size, header, TextAlign.Right);
            VectorFont.Draw(pb, "KILLS", new Vector2(x + 430 * s, ty), size, header, TextAlign.Right);
            VectorFont.Draw(pb, "DEATHS", new Vector2(x + 530 * s, ty), size, header, TextAlign.Right);
        }
        ty += rowH * 1.3f;

        for (int i = 0; i < standings.Count; i++)
        {
            var ship = standings[i];
            var color = Palette.Ship(ship);
            if (ship == match.Player)
            {
                pb.Rect(x + 8 * s, ty - 6 * s, w - 16 * s, rowH - 2 * s, color * 0.12f);
            }
            string rank = world.Rules is BallRules ball
                ? $"{Teams.Name(ship.Team)[0]}{ball.TeamScore(ship.Team)}"
                : (i + 1).ToString();
            VectorFont.Draw(pb, rank, new Vector2(x + 20 * s, ty), size, color);
            VectorFont.Draw(pb, ship.Name, new Vector2(x + 60 * s, ty), size, color);
            if (race)
            {
                VectorFont.Draw(pb, ship.Lap.ToString(), new Vector2(x + 300 * s, ty), size, color, TextAlign.Right);
                VectorFont.Draw(pb, ship.LapTimes.Count > 0 ? FormatTime(ship.BestLap) : "-", new Vector2(x + 410 * s, ty), size, color, TextAlign.Right);
                VectorFont.Draw(pb, ship.Finished ? FormatTime(ship.FinishTime) : "DNF", new Vector2(x + 530 * s, ty), size, color, TextAlign.Right);
            }
            else
            {
                VectorFont.Draw(pb, ship.Score.ToString(), new Vector2(x + 330 * s, ty), size, color, TextAlign.Right);
                VectorFont.Draw(pb, ship.Kills.ToString(), new Vector2(x + 430 * s, ty), size, color, TextAlign.Right);
                VectorFont.Draw(pb, ship.Deaths.ToString(), new Vector2(x + 530 * s, ty), size, color, TextAlign.Right);
            }
            ty += rowH;
        }
        pb.End();
    }

    public static string FormatClock(float seconds)
    {
        int total = (int)MathF.Ceiling(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    public static string FormatTime(float seconds)
    {
        if (float.IsNaN(seconds)) return "-";
        int minutes = (int)(seconds / 60f);
        float rest = seconds - minutes * 60f;
        return $"{minutes}:{rest:00.00}".Replace(',', '.');
    }
}
