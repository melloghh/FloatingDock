using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Services;

namespace FloatingDock.Windows.Shell;

public sealed record FileSearchHit(string Path,string Name,bool IsFolder);
public sealed record FileSearchResult(IReadOnlyList<FileSearchHit> Hits,bool UsedFallback,bool Limited);

/// <summary>Small in-memory filename index for personal folders. No disk access per keystroke.</summary>
public static class FileSearch
{
    private sealed record Entry(FileSearchHit Hit,string Normalized);
    private sealed record Snapshot(Entry[] Entries,long Created,bool Limited);
    private static readonly object Gate=new();
    private static readonly Dictionary<string,Snapshot> Cache=new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Extensions=new(".lnk;.url;.txt;.md;.pdf;.doc;.docx;.xls;.xlsx;.ppt;.pptx;.odt;.ods;.rtf;.csv;.jpg;.jpeg;.png;.webp;.gif;.svg;.bmp;.heic;.mp3;.wav;.flac;.m4a;.ogg;.mp4;.mkv;.mov;.webm;.zip;.7z;.rar".Split(';'),StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Excluded=new(".git;node_modules;bin;obj;.validation;.build-tools;.cache;AppData;target;packages;vendor".Split(';'),StringComparer.OrdinalIgnoreCase);
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
            if(token is not ("Desktop" or "Downloads")&&(!Path.IsPathFullyQualified(token)||token.IndexOfAny(Path.GetInvalidPathChars())>=0))
                return "Use Desktop;Downloads or absolute folder paths.";
        return null;
    }
    public static IReadOnlyList<string> ResolveFolders(string value)
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string documents=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach(var token in value.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            if(token.Equals("Documents",StringComparison.OrdinalIgnoreCase))continue;
            var path=token switch { "Desktop"=>Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Downloads"=>Downloads(),_=>token };
            try
            {
                if(!Path.IsPathFullyQualified(path))continue;
                path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if(documents.Length>0&&(path.Equals(documents,StringComparison.OrdinalIgnoreCase)||path.StartsWith(documents+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))continue;
                paths.Add(path);
            }
            catch(ArgumentException){}
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    // Kept for callers constructing a Windows Search query; the palette uses the bounded cache instead.
    public static string BuildQuery(string query,IReadOnlyList<string> roots,int limit)
    {
        string Q(string text)=>"'"+text.Replace("'","''",StringComparison.Ordinal)+"'";
        var scopes=roots.Select(path=>"SCOPE="+Q(new Uri(Path.GetFullPath(path)+Path.DirectorySeparatorChar).AbsoluteUri));
        return $"SELECT TOP {Math.Clamp(limit,1,100)} System.ItemPathDisplay, System.ItemNameDisplay FROM SYSTEMINDEX WHERE System.FileName LIKE {Q("%"+query.Replace("%","",StringComparison.Ordinal).Replace("_","",StringComparison.Ordinal)+"%")} AND ({string.Join(" OR ",scopes)}) ORDER BY System.ItemNameDisplay";
    }
    public static void Invalidate(){lock(Gate)Cache.Clear();}
    public static FileSearchResult Search(string query,string folders,int limit,CancellationToken ct,int depth=1)
    {
        ct.ThrowIfCancellationRequested();
        if(query.Trim().Length<2)return new(Array.Empty<FileSearchHit>(),false,false);
        var roots=ResolveFolders(folders);depth=Math.Clamp(depth,0,3);
        if(roots.Count==0)return new(Array.Empty<FileSearchHit>(),false,false);
        var key=depth+"|"+string.Join('|',roots);Snapshot snapshot;
        lock(Gate)
        {
            ct.ThrowIfCancellationRequested();
            if(!Cache.TryGetValue(key,out snapshot!)||Stopwatch.GetElapsedTime(snapshot.Created).TotalSeconds>=30)
            {
                snapshot=Scan(roots,depth,ct);
                if(Cache.Count>=4)Cache.Clear();
                Cache[key]=snapshot;
            }
        }
        ct.ThrowIfCancellationRequested();var needle=AppSearchIndex.Normalize(query);
        return new(snapshot.Entries.Where(e=>e.Normalized.Contains(needle,StringComparison.Ordinal))
            .OrderBy(e=>e.Normalized.StartsWith(needle,StringComparison.Ordinal)?0:1).ThenBy(e=>e.Hit.Name,StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit,1,100)).Select(e=>e.Hit).ToArray(),false,snapshot.Limited);
    }
    private static Snapshot Scan(IReadOnlyList<string> roots,int depth,CancellationToken ct)
    {
        var entries=new List<Entry>();var queue=new Queue<(string Path,int Depth)>(roots.Select(r=>(r,0)));
        string documents=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        int visited=0;var clock=Stopwatch.StartNew();bool limited=false;
        while(queue.TryDequeue(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if((new DirectoryInfo(folder.Path).Attributes&FileAttributes.ReparsePoint)!=0)continue;
                foreach(var info in new DirectoryInfo(folder.Path).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if(++visited>3000||clock.ElapsedMilliseconds>=150){limited=true;break;}
                    if((info.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Hidden|FileAttributes.System))!=0||Excluded.Contains(info.Name))continue;
                    if(documents.Length>0&&info.FullName.Equals(documents,StringComparison.OrdinalIgnoreCase))continue;
                    bool directory=(info.Attributes&FileAttributes.Directory)!=0;
                    if((directory&&folder.Depth==0)||(!directory&&Extensions.Contains(info.Extension)))
                        entries.Add(new(new(info.FullName,info.Name,directory),AppSearchIndex.Normalize(info.Name)));
                    if(directory&&folder.Depth<depth)queue.Enqueue((info.FullName,folder.Depth+1));
                }
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
            if(limited)break;
        }
        return new(entries.ToArray(),Stopwatch.GetTimestamp(),limited);
    }
}
