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
                Assert.IsNull(ProjectExport.Needed(project));
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }
    }
}
