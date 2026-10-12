namespace Fraud.Api.Services;

public static class ProjectPaths
{
    public static string Resolve(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);

        // dotnet run, IDE launches, and published binaries can each choose a different working directory.
        // Walk upward to find the repository root locally; containers normally pass absolute paths.
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "src", "Fraud.Api")))
                    return Path.GetFullPath(Path.Combine(directory.FullName, configuredPath));
                directory = directory.Parent;
            }
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
    }
}
