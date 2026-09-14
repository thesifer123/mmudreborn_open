using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class MmudrebornWorkspaceFileResolverTests
{
    [Fact]
    public void ResolveOptionalFile_finds_mmudreborn_repo_file_from_sibling_bbs_tree()
    {
        string workspaceRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string gameRepo = Path.Combine(workspaceRoot, "mmudreborn");
            string bbsRepo = Path.Combine(workspaceRoot, "CWGamingServ");
            string bbsBin = Path.Combine(bbsRepo, "bin", "Debug", "net8.0");

            Directory.CreateDirectory(gameRepo);
            Directory.CreateDirectory(bbsBin);
            File.WriteAllText(Path.Combine(gameRepo, "mmudreborn.sln"), string.Empty);

            string expected = Path.Combine(gameRepo, "help_topics.json");
            File.WriteAllText(expected, "{}");

            string? resolved = MmudrebornWorkspaceFileResolver.ResolveOptionalFile(
                "help_topics.json",
                [bbsBin, bbsRepo]);

            Assert.Equal(expected, resolved);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveOptionalFile_ignores_bbs_side_help_topics_file()
    {
        string workspaceRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string gameRepo = Path.Combine(workspaceRoot, "mmudreborn");
            string bbsRepo = Path.Combine(workspaceRoot, "CWGamingServ");

            Directory.CreateDirectory(gameRepo);
            Directory.CreateDirectory(bbsRepo);
            File.WriteAllText(Path.Combine(gameRepo, "mmudreborn.sln"), string.Empty);
            File.WriteAllText(Path.Combine(bbsRepo, "help_topics.json"), "{\"bbs\":true}");

            string expected = Path.Combine(gameRepo, "help_topics.json");
            File.WriteAllText(expected, "{}");

            string? resolved = MmudrebornWorkspaceFileResolver.ResolveOptionalFile(
                "help_topics.json",
                [bbsRepo]);

            Assert.Equal(expected, resolved);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }
}