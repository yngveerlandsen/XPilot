using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Desktop.Graphics;

namespace XPilot.Desktop.Screens;

public sealed class ResultsScreen(XPilotGame game, Match match, MatchSetup setup) : Screen(game)
{
    private float _time;

    public override void Update(float dt)
    {
        _time += dt;
        if (_time < 0.5f) return;
        if (Input.WasPressed(Keys.R) || Input.WasPressed(Buttons.Y))
        {
            Game.SetScreen(new PlayScreen(Game, setup));
        }
        else if (Input.MenuSelect || Input.MenuBack)
        {
            Game.SetScreen(new MainMenuScreen(Game));
        }
    }

    public override void Draw(float dt)
    {
        var vp = Game.GraphicsDevice.Viewport;
        float s = vp.Height / 720f;
        var pb = Primitives;
        var world = match.World;
        var standings = world.Rules.GetStandings(world);
        var winner = standings.FirstOrDefault();
        bool playerWon = winner != null && winner == match.Player;

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        Game.Starfield.Draw(pb, new Vector2(_time * 20f, _time * 6f), vp.Width, vp.Height, _time);
        float cx = vp.Width / 2f;
        string title = world.Rules.Mode == GameModeKind.Race ? "RACE RESULTS" : "MATCH RESULTS";
        VectorFont.Draw(pb, title, new Vector2(cx, 50 * s), 32f * s, Palette.Accent, TextAlign.Center, 3f * s);
        if (world.Rules is ITeamRules teams)
        {
            int team = teams.WinningTeam;
            string headline = team == Teams.None ? "DRAW"
                : match.Player?.Team == team ? "YOUR TEAM WINS!"
                : $"{Teams.Name(team).ToUpperInvariant()} TEAM WINS";
            VectorFont.Draw(pb, headline, new Vector2(cx, 100 * s), 18f * s, Palette.Team(team), TextAlign.Center);
        }
        else if (winner != null)
        {
            string headline = playerWon ? "VICTORY!" : $"{winner.Name.ToUpperInvariant()} WINS";
            VectorFont.Draw(pb, headline, new Vector2(cx, 100 * s), 18f * s, Palette.Ship(winner), TextAlign.Center);
        }
        VectorFont.Draw(pb, $"{GameModes.Name(world.Rules.Mode).ToUpperInvariant()}   {world.Map.Name.ToUpperInvariant()}   {setup.Difficulty.ToString().ToUpperInvariant()} BOTS",
            new Vector2(cx, 132 * s), 10f * s, Palette.TextDim, TextAlign.Center);
        VectorFont.Draw(pb, "ENTER  MAIN MENU      R  PLAY AGAIN", new Vector2(cx, vp.Height - 50 * s), 12f * s, Palette.TextDim, TextAlign.Center);
        pb.End();

        Hud.DrawScoreboard(pb, vp, s, match);
    }
}
