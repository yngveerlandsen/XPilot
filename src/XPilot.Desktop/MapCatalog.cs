using XPilot.Core.Maps;

namespace XPilot.Desktop;

/// <summary>All maps found in the "maps" folder next to the executable.</summary>
public sealed class MapCatalog
{
    private MapCatalog(List<Map> maps, List<string> errors)
    {
        Maps = maps;
        Errors = errors;
    }

    public IReadOnlyList<Map> Maps { get; }
    public IReadOnlyList<string> Errors { get; }

    public static MapCatalog Load()
    {
        var maps = new List<Map>();
        var errors = new List<string>();
        var dir = Path.Combine(AppContext.BaseDirectory, "maps");
        if (Directory.Exists(dir))
        {
            foreach (var file in MapLoader.FindMaps(dir))
            {
                try
                {
                    maps.Add(MapLoader.Load(file));
                }
                catch (Exception ex) when (ex is MapFormatException or IOException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        else
        {
            errors.Add($"Map folder not found: {dir}");
        }
        return new MapCatalog(maps.OrderBy(m => m.Name).ToList(), errors);
    }

    public IReadOnlyList<Map> ForMode(GameModeKind mode) => Maps.Where(m => m.Mode == mode).ToList();
}
