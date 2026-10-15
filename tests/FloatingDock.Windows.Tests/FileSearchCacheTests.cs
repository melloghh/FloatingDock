using FloatingDock.Windows.Shell;
using Xunit;

namespace FloatingDock.Windows.Tests;
[Collection("FileSearch cache")]
public sealed class FileSearchCacheTests
{
    [Fact] public void CacheIsReusedAcrossQueriesAndRefreshIsExplicit()
    {
        var root=Path.Combine(Path.GetTempPath(),"FloatingDock-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"notes.txt"),"");Assert.Single(FileSearch.Search("notes",root,10,default).Hits);
            // A cold scan would see this file; a second query must use the same snapshot.
            File.WriteAllText(Path.Combine(root,"new-notes.txt"),"");Assert.Single(FileSearch.Search("note",root,10,default).Hits);
            FileSearch.Invalidate();Assert.Equal(2,FileSearch.Search("note",root,10,default).Hits.Count);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void DepthAndPersonalTypeFilterExcludeBuildNoise()
    {
        var root=Path.Combine(Path.GetTempPath(),"FloatingDock-filter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"sub","deep"));Directory.CreateDirectory(Path.Combine(root,"node_modules"));
            foreach(var file in new[]{"report.txt","report.dll","report.json","sub/report.pdf","sub/deep/report.docx","node_modules/report.txt"})File.WriteAllText(Path.Combine(root,file),"");
            Assert.Single(FileSearch.Search("report",root,30,default,0).Hits);
            Assert.Equal(2,FileSearch.Search("report",root,30,default,1).Hits.Count);
            Assert.Equal(3,FileSearch.Search("report",root,30,default,2).Hits.Count);
        }
        finally{Directory.Delete(root,true);}
    }
}
