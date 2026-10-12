namespace FloatingDock.Core.Tiling;

public enum TileDirection { Left,Right,Up,Down }
public readonly record struct TileRect(int X,int Y,int Width,int Height)
{
    public double CenterX=>X+Width/2d;
    public double CenterY=>Y+Height/2d;
}

/// <summary>Independent binary split layout. Platform calls, timing and HWND ownership live outside this model.</summary>
public sealed class TileTree
{
    private sealed class Node
    {
        public long Window;
        public Node? First,Second;
        public bool Horizontal=true;
        public double Ratio=.5;
        public bool Leaf=>First is null;
    }
    private Node? _root;
    public IReadOnlyList<long> Windows { get { var result=new List<long>();Walk(_root,n=>{if(n.Leaf)result.Add(n.Window);});return result; } }
    private static void Walk(Node? node,Action<Node> visit){if(node is null)return;visit(node);Walk(node.First,visit);Walk(node.Second,visit);}
    private Node? Find(long id){Node? result=null;Walk(_root,n=>{if(n.Leaf&&n.Window==id)result=n;});return result;}
    public void Add(long id,long near=0,bool horizontal=true)
    {
        if(Find(id) is not null)return;
        if(_root is null){_root=new(){Window=id};return;}
        var target=Find(near)??Find(Windows.Last())!;
        target.First=new(){Window=target.Window};target.Second=new(){Window=id};target.Window=0;target.Horizontal=horizontal;target.Ratio=.5;
    }
    public bool Remove(long id)
    {
        bool removed=false;
        Node? Prune(Node? n)
        {
            if(n is null)return null;
            if(n.Leaf){if(n.Window==id){removed=true;return null;}return n;}
            n.First=Prune(n.First);n.Second=Prune(n.Second);
            return n.First is null?n.Second:n.Second is null?n.First:n;
        }
        _root=Prune(_root);return removed;
    }
    public void Swap(long a,long b){var first=Find(a);var second=Find(b);if(first is not null&&second is not null)(first.Window,second.Window)=(second.Window,first.Window);}
    public void ToggleDirection(long id)
    {
        Node? parent=null;Walk(_root,n=>{if(!n.Leaf&&(n.First!.Window==id||n.Second!.Window==id))parent=n;});
        if(parent is not null)parent.Horizontal=!parent.Horizontal;
    }
    public void Resize(long id,TileDirection direction,double amount=.05)
    {
        bool horizontal=direction is TileDirection.Left or TileDirection.Right;
        Node? target=null;bool first=false;
        bool Contains(Node n)=>n.Leaf?n.Window==id:Contains(n.First!)||Contains(n.Second!);
        Walk(_root,n=>{if(!n.Leaf&&n.Horizontal==horizontal&&Contains(n)){target=n;first=Contains(n.First!);}});
        if(target is null)return;
        double sign=direction is TileDirection.Right or TileDirection.Down?1:-1;
        target.Ratio=Math.Clamp(target.Ratio+amount*sign*(first?1:-1),.15,.85);
    }
    public IReadOnlyDictionary<long,TileRect> Layout(TileRect bounds,int gap=8,int outer=12,IReadOnlySet<long>? visible=null)
    {
        var result=new Dictionary<long,TileRect>();
        outer=Math.Clamp(outer,0,Math.Max(0,Math.Min(bounds.Width,bounds.Height)/4));gap=Math.Clamp(gap,0,80);
        void Place(Node? node,TileRect rect)
        {
            if(node is null)return;
            bool Has(Node n)=>n.Leaf?visible is null||visible.Contains(n.Window):Has(n.First!)||Has(n.Second!);
            if(!Has(node))return;
            if(node.Leaf){result[node.Window]=rect;return;}
            if(!Has(node.First!)){Place(node.Second,rect);return;}
            if(!Has(node.Second!)){Place(node.First,rect);return;}
            int length=node.Horizontal?rect.Width:rect.Height,space=Math.Min(gap,Math.Max(0,length-2));
            int first=Math.Clamp((int)Math.Round((length-space)*node.Ratio),1,Math.Max(1,length-space-1));
            if(node.Horizontal){Place(node.First,rect with{Width=first});Place(node.Second,new(rect.X+first+space,rect.Y,Math.Max(1,length-first-space),rect.Height));}
            else{Place(node.First,rect with{Height=first});Place(node.Second,new(rect.X,rect.Y+first+space,rect.Width,Math.Max(1,length-first-space)));}
        }
        Place(_root,new(bounds.X+outer,bounds.Y+outer,Math.Max(1,bounds.Width-2*outer),Math.Max(1,bounds.Height-2*outer)));return result;
    }
    public static long Neighbor(long id,TileDirection direction,IReadOnlyDictionary<long,TileRect> layout)
    {
        if(!layout.TryGetValue(id,out var from))return 0;
        long best=0;double score=double.MaxValue;
        foreach(var (window,rect) in layout)
        {
            if(window==id)continue;
            double dx=rect.CenterX-from.CenterX,dy=rect.CenterY-from.CenterY;
            double forward=direction switch{TileDirection.Left=>-dx,TileDirection.Right=>dx,TileDirection.Up=>-dy,_=>dy};
            if(forward<=.5)continue;
            double lateral=direction is TileDirection.Left or TileDirection.Right?Math.Abs(dy):Math.Abs(dx);
            double candidate=forward+lateral*2;
            if(candidate<score){best=window;score=candidate;}
        }
        return best;
    }
}
