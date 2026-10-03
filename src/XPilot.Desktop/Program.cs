using System.Globalization;

// Must come first: when the installer, uninstaller or an update starts the game with special arguments,
// Velopack does its work here and exits. A normal start just carries on.
Velopack.VelopackApp.Build().Run();

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

using var game = new XPilot.Desktop.XPilotGame();
game.Run();
