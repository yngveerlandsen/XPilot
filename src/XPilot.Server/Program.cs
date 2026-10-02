using System.Globalization;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Net;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
    XPilot dedicated server

      XPilot.Server [options]

    Options:
      --name <text>          Server name shown in server lists (default "XPilot")
      --port <n>             UDP port (default 15345)
      --mode <mode>          dogfight, race or ball: rotate through every map of that mode (default dogfight).
                             random: every map of every mode, in random order
      --shuffle              Play the maps in random order instead of in turn
      --map <name>           Play only these maps, in order (repeat or comma-separate). Overrides --mode.
      --maps <dir>           Folder with .xpm maps (default: "maps" next to the server)
      --bots <n>             Bots filling free seats (default 3)
      --difficulty <d>       easy, normal or hard (default normal)
      --score-limit <n>      Dogfight kills to win (default 10, 0 = none)
      --capture-limit <n>    Ball captures to win (default 3)
      --time-limit <sec>     Match length in seconds (0 = none)
      --laps <n>             Race laps (default: the map's)
      --master <host:port>   Register with a master server so internet players can find this server

      --run-master [port]    Run a master server instead of a game server (default port 15346)
    """;

var options = new ServerOptions();
var mapNames = new List<string>();
string mode = "dogfight";
string mapsDir = Path.Combine(AppContext.BaseDirectory, "maps");
int? masterPort = null;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i].ToLowerInvariant())
        {
            case "--name": options.Name = Protocol.CleanServerName(Next()); break;
            case "--port": options.Port = int.Parse(Next()); break;
            case "--mode": mode = Next().ToLowerInvariant(); break;
            case "--map": mapNames.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
            case "--maps": mapsDir = Next(); break;
            case "--shuffle": options.ShuffleMaps = true; break;
            case "--bots": options.BotCount = int.Parse(Next()); break;
            case "--difficulty": options.Difficulty = Enum.Parse<BotDifficulty>(Next(), true); break;
            case "--score-limit": options.ScoreLimit = int.Parse(Next()); break;
            case "--capture-limit": options.CaptureLimit = int.Parse(Next()); break;
            case "--time-limit": options.TimeLimit = float.Parse(Next()); break;
            case "--laps": options.Laps = int.Parse(Next()); break;
            case "--master": options.MasterServer = Next(); break;
            case "--run-master":
                masterPort = Protocol.DefaultMasterPort;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    masterPort = p;
                    i++;
                }
                break;
            case "-h" or "--help" or "/?":
                Console.WriteLine(Usage);
                return 0;
            default:
                throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Run with --help for the list of options.");
    return 1;
}

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
// systemd and docker stop services with SIGTERM rather than Ctrl+C.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stop.Set();
    });
void Log(string line) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");

if (masterPort is { } port)
{
    using var master = new MasterServer();
    master.Log += Log;
    if (!master.Start(port))
    {
        Console.Error.WriteLine($"Could not open UDP port {port}.");
        return 1;
    }
    Log($"Master server listening on UDP port {port}. Ctrl+C stops it.");
    while (!stop.Wait(10)) master.Poll();
    return 0;
}

if (!Directory.Exists(mapsDir))
{
    Console.Error.WriteLine($"Map folder not found: {mapsDir}");
    return 1;
}
var available = new List<(string Name, string File, Map Map)>();
foreach (var file in MapLoader.FindMaps(mapsDir))
{
    try
    {
        available.Add((Path.GetFileNameWithoutExtension(file), file, MapLoader.Load(file)));
    }
    catch (Exception ex) when (ex is MapFormatException or IOException)
    {
        Console.Error.WriteLine($"Skipping {Path.GetFileName(file)}: {ex.Message}");
    }
}

List<string> rotation;
if (mapNames.Count > 0)
{
    rotation = [];
    foreach (var name in mapNames)
    {
        var match = available.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Map.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match.File == null)
        {
            Console.Error.WriteLine($"Unknown map '{name}'. Available: {string.Join(", ", available.Select(m => m.Name))}");
            return 1;
        }
        rotation.Add(match.File);
    }
}
else
{
    if (mode is "random" or "all")
    {
        rotation = available.Select(m => m.File).ToList();
        options.ShuffleMaps = true;
    }
    else if (!Enum.TryParse<GameModeKind>(mode, true, out var kind))
    {
        Console.Error.WriteLine($"Unknown mode '{mode}'. Use dogfight, race, ball or random.");
        return 1;
    }
    else
    {
        rotation = available.Where(m => m.Map.Mode == kind).Select(m => m.File).ToList();
    }
    if (rotation.Count == 0)
    {
        Console.Error.WriteLine($"No {mode} maps in {mapsDir}.");
        return 1;
    }
}

var server = new GameServer(options, rotation.Select(MapLoader.ReadText));
server.Log += Log;
using var host = new ServerHost(server);
host.Log += Log;
if (!host.Start())
{
    Console.Error.WriteLine($"Could not open UDP port {options.Port}. Is another server running?");
    return 1;
}
Log($"'{options.Name}' listening on UDP port {host.Port} with {rotation.Count} map(s). Ctrl+C stops it.");
if (options.MasterServer != null) Log($"Registering with master server {options.MasterServer}");

while (!stop.Wait(500))
{
    if (host.Error != null) return 2;
}
Log("Shutting down");
return 0;
