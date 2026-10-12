using FloatingDock.Windows.Shell;
using Xunit;

namespace FloatingDock.Windows.Tests;
public sealed class FileSearchTests
{
    [Fact] public void QueryEscapesQuotesAndKeepsScopes()
    {
        var sql=FileSearch.BuildQuery("note' OR 1=1",new[]{@"C:\Docs' test"},10000);
        Assert.Contains("SELECT TOP 100",sql);Assert.Contains("note'' OR 1=1",sql);Assert.Contains("SCOPE='file:",sql);Assert.DoesNotContain("note' OR",sql);
    }
    [Fact] public void RelativeRootsAreRejected()
    { Assert.NotNull(FileSearch.ValidateFolders("Documents;../../"));Assert.Null(FileSearch.ValidateFolders(@"Desktop;Downloads;C:\Custom"));Assert.Empty(FileSearch.ResolveFolders("Documents")); }
    [Fact] public void SearchFindsUnicodeFilesInOnlyTheSelectedRoot()
    {
        var root=Path.Combine(Path.GetTempPath(),"FloatingDock-search-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var folder=Path.Combine(root,"nested");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"Смета-project.txt"),"ignored content");
            Directory.CreateDirectory(Path.Combine(root,".git"));File.WriteAllText(Path.Combine(root,".git","Смета-hidden.txt"),"");
            var result=FileSearch.Search("смета",root,10,CancellationToken.None);
            Assert.Single(result.Hits);Assert.Equal("Смета-project.txt",result.Hits[0].Name);Assert.False(result.Hits[0].IsFolder);
            Assert.Empty(FileSearch.Search("ignored content",root,10,CancellationToken.None).Hits);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void CancelledQueriesNeverScanFolders()
    { using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>FileSearch.Search("anything","Desktop",30,cancel.Token)); }
}
