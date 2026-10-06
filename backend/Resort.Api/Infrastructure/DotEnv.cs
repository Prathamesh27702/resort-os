namespace Resort.Api.Infrastructure;

/// <summary>
/// Development convenience: loads KEY=VALUE pairs from the repo-root <c>.env</c> file (gitignored) into environment
/// variables before configuration is built. Never used outside Development — staging/production get real environment
/// variables from the host's secret store.
/// Rules: full-line comments (#) only, existing environment variables win, blank values are ignored.
/// </summary>
public static class DotEnv
{
    public static string? Load()
    {
        var file = Find(Directory.GetCurrentDirectory()) ?? Find(AppContext.BaseDirectory);
        if (file is null) return null;

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (value.Length == 0 || Environment.GetEnvironmentVariable(key) is not null) continue;
            Environment.SetEnvironmentVariable(key, value);
        }
        return file;
    }

    private static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "ResortOS.sln"))) return null; // stop at repo root
        }
        return null;
    }
}
