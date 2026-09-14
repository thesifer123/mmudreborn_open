namespace mmudreborn.Server;

public static class MmudrebornWorkspaceFileResolver
{
    private const string RepoMarkerFileName = "mmudreborn.sln";

    public static string? ResolveOptionalFile(string fileName)
    {
        return ResolveOptionalFile(fileName, [AppContext.BaseDirectory, Directory.GetCurrentDirectory()]);
    }

    public static string? ResolveOptionalFile(string fileName, IEnumerable<string> searchRoots)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in EnumerateSearchDirectories(searchRoots, seenDirectories))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories(IEnumerable<string> searchRoots, HashSet<string> seenDirectories)
    {
        foreach (string root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string? repoRoot = FindNearestRepoRoot(root);
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                if (seenDirectories.Add(repoRoot))
                    yield return repoRoot;

                continue;
            }

            string current = Path.GetFullPath(root);
            while (!string.IsNullOrWhiteSpace(current))
            {
                foreach (string siblingRepoRoot in EnumerateSiblingRepoRoots(current, seenDirectories))
                    yield return siblingRepoRoot;

                string? parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.Ordinal))
                    break;

                current = parent ?? string.Empty;
            }
        }
    }

    private static string? FindNearestRepoRoot(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, RepoMarkerFileName)))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.Ordinal))
                break;

            current = parent ?? string.Empty;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSiblingRepoRoots(string directory, HashSet<string> seenDirectories)
    {
        IEnumerable<string> children;
        try
        {
            if (!Directory.Exists(directory))
                yield break;

            children = Directory.EnumerateDirectories(directory);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string child in children)
        {
            if (!File.Exists(Path.Combine(child, RepoMarkerFileName)))
                continue;

            if (seenDirectories.Add(child))
                yield return child;
        }
    }
}