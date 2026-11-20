using System.IO;
using NUnit.Framework;
using Cluewren.Editor;

namespace Cluewren.Tests
{
    public class ExportTests
    {
        [Test]
        public void TheExportGoesToTheProjectsDataFolderWhichGitIgnores()
        {
            var project = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(project);
            try
            {
                var folder = Path.Combine(project, ".cluewren", "export");
                Assert.AreEqual(folder, ProjectExport.Folder(project));
                Assert.AreEqual("missing", ProjectExport.Needed(project));
                Assert.AreEqual(folder, ProjectExport.MakeFolder(project));
                Assert.IsTrue(Directory.Exists(folder));
                Assert.AreEqual("*\n", File.ReadAllText(Path.Combine(project, ".cluewren", ".gitignore")));
                File.WriteAllText(Path.Combine(folder, "summary.txt"), "");
                Assert.AreEqual("stale", ProjectExport.Needed(project), "an older export has no asset inventory");
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }

        [Test]
        public void DeletingTheLastAssetInAFolderInvalidatesTheExport()
        {
            var project = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var assets = Path.Combine(project, "Assets", "Nest");
            Directory.CreateDirectory(assets);
            try
            {
                var prefab = Path.Combine(assets, "Removed.prefab");
                File.WriteAllText(prefab, "");
                File.SetLastWriteTimeUtc(prefab, new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
                ProjectExport.Export(project);
                Assert.IsNull(ProjectExport.Needed(project));

                File.Delete(prefab);
                Directory.Delete(assets);
                Assert.AreEqual("stale", ProjectExport.Needed(project));
                ProjectExport.Export(project);
                Assert.IsNull(ProjectExport.Needed(project));
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }
    }
}
