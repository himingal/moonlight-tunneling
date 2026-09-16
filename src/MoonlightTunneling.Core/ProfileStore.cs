namespace MoonlightTunneling.Core;

public static class ProfileStore
{
    public static List<VpnProfile> LoadAll()
    {
        var list = new List<VpnProfile>();
        if (!Directory.Exists(AppPaths.ProfilesDir)) return list;
        foreach (var f in Directory.EnumerateFiles(AppPaths.ProfilesDir, "*.json"))
        {
            var p = JsonStore.LoadOrDefault<VpnProfile>(f);
            if (!string.IsNullOrEmpty(p.PrivateKeyProtected)) list.Add(p);
        }
        return list.OrderBy(p => p.ImportedAt).ToList();
    }

    public static void Save(VpnProfile p) => JsonStore.Save(Path.Combine(AppPaths.ProfilesDir, p.Id + ".json"), p);

    public static void Delete(VpnProfile p)
    {
        var f = Path.Combine(AppPaths.ProfilesDir, p.Id + ".json");
        if (File.Exists(f)) File.Delete(f);
    }
}
