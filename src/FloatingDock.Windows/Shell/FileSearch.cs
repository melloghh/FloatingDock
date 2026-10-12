using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Services;

namespace FloatingDock.Windows.Shell;

public sealed record FileSearchHit(string Path,string Name,bool IsFolder);
public sealed record FileSearchResult(IReadOnlyList<FileSearchHit> Hits,bool UsedFallback,bool Limited);

/// <summary>Filename-only, scoped search. Uses the Windows index; bounded enumeration handles unindexed folders.</summary>
public static class FileSearch
{
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id,uint flags,IntPtr token,out IntPtr path);
    private static string Downloads()
    {
        var id=new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if(SHGetKnownFolderPath(id,0,IntPtr.Zero,out var p)!=0)return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
        try{return Marshal.PtrToStringUni(p)??"";}finally{Marshal.FreeCoTaskMem(p);}
    }
    public static string? ValidateFolders(string value)
    {
        foreach(var token in value.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
            if(token is not ("Desktop" or "Documents" or "Downloads") && (!Path.IsPathFullyQualified(token)||token.IndexOfAny(Path.GetInvalidPathChars())>=0))
                return "Use Desktop;Documents;Downloads or absolute folder paths.";
        return null;
    }
    public static IReadOnlyList<string> ResolveFolders(string value)
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var token in value.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            var path=token switch { "Desktop"=>Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Documents"=>Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Downloads"=>Downloads(),_=>token };
            try { if(Path.IsPathFullyQualified(path)&&Directory.Exists(path))paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }catch(ArgumentException){}
        }
        return paths.ToArray();
    }
    private static bool Inside(string path,string root)=>path.StartsWith(Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public static string BuildQuery(string query,IReadOnlyList<string> roots,int limit)
    {
        // LIKE supports wildcards. Quotes are SQL-escaped; scopes are constructed from validated paths.
        string Q(string text)=>"'"+text.Replace("'","''",StringComparison.Ordinal)+"'";
        var scopes=roots.Select(path=>"SCOPE="+Q(new Uri(Path.GetFullPath(path)+Path.DirectorySeparatorChar).AbsoluteUri));
        return $"SELECT TOP {Math.Clamp(limit,1,100)} System.ItemPathDisplay, System.ItemNameDisplay FROM SYSTEMINDEX WHERE System.FileName LIKE {Q("%"+query.Replace("%","",StringComparison.Ordinal).Replace("_","",StringComparison.Ordinal)+"%")} AND ({string.Join(" OR ",scopes)}) ORDER BY System.ItemNameDisplay";
    }
    public static FileSearchResult Search(string query,string folders,int limit,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var roots=ResolveFolders(folders);
        if(query.Trim().Length<2||roots.Count==0)return new(Array.Empty<FileSearchHit>(),false,false);
        object? connection=null,recordset=null;
        var indexed=new List<FileSearchHit>();bool fallback=false;
        try
        {
            var type=Type.GetTypeFromProgID("ADODB.Connection")??throw new InvalidOperationException("Windows Search provider unavailable.");
            dynamic db=connection=Activator.CreateInstance(type)!;
            db.ConnectionTimeout=2;db.CommandTimeout=2;
            db.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            ct.ThrowIfCancellationRequested();
            object affected;dynamic rows=recordset=db.Execute(BuildQuery(query,roots,limit),out affected,0);
            if(!(bool)rows.EOF)
            {
                object[,] values=rows.GetRows(limit);
                for(int i=0;i<values.GetLength(1);i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string path=Convert.ToString(values[0,i])??"",name=Convert.ToString(values[1,i])??"";
                    if(roots.Any(root=>Inside(path,root))&&(File.Exists(path)||Directory.Exists(path)))indexed.Add(new(path,name,Directory.Exists(path)));
                }
            }
        }
        catch(OperationCanceledException){throw;}
        catch(Exception){fallback=true;}
        finally
        {
            if(recordset is not null){try{((dynamic)recordset).Close();}catch{}Marshal.FinalReleaseComObject(recordset);}
            if(connection is not null){try{((dynamic)connection).Close();}catch{}Marshal.FinalReleaseComObject(connection);}
        }
        // Unindexed folders are common. Fill remaining slots with a bounded walk, never following junctions.
        if(indexed.Count>=limit)return new(indexed,false,false);
        var found=new Dictionary<string,FileSearchHit>(StringComparer.OrdinalIgnoreCase);
        foreach(var hit in indexed)found[hit.Path]=hit;
        var queue=new Queue<string>(roots);int visited=0;var clock=Stopwatch.StartNew();
        var needle=AppSearchIndex.Normalize(query);
        bool limited=false;
        while(queue.Count>0&&found.Count<limit)
        {
            ct.ThrowIfCancellationRequested();
            if(visited>=25000||clock.ElapsedMilliseconds>=2000){limited=true;break;}
            string folder=queue.Dequeue();
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    if(++visited>=25000||clock.ElapsedMilliseconds>=2000){limited=true;break;}
                    try
                    {
                        var attr=File.GetAttributes(path);
                        if((attr&(FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Hidden))!=0)continue;
                        string name=Path.GetFileName(path);bool directory=(attr&FileAttributes.Directory)!=0;
                        if(directory&&name is ".git" or "node_modules" or "bin" or "obj" or ".validation" or ".build-tools" or ".cache")continue;
                        if(AppSearchIndex.Normalize(name).Contains(needle,StringComparison.Ordinal))found.TryAdd(path,new(path,name,directory));
                        if(found.Count>=limit)break;
                        if(directory)queue.Enqueue(path);
                    }
                    catch(IOException){}catch(UnauthorizedAccessException){}
                }
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
            if(limited)break;
        }
        return new(found.Values.OrderBy(h=>AppSearchIndex.Normalize(h.Name).StartsWith(needle,StringComparison.Ordinal)?0:1).ThenBy(h=>h.Name,StringComparer.CurrentCultureIgnoreCase).Take(limit).ToArray(),fallback||indexed.Count==0,limited);
    }
}
