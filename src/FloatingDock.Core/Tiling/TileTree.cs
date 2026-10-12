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
        public double PreferredRatio=.5;
        public bool Leaf=>First is null;
    }
    private Node? _root;
    private static Node? Copy(Node? n)=>n is null?null:new(){Window=n.Window,Horizontal=n.Horizontal,Ratio=n.Ratio,PreferredRatio=n.PreferredRatio,First=Copy(n.First),Second=Copy(n.Second)};
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
    public bool AddBalanced(long id,long near,TileRect bounds,int gap=8,int outer=12,IReadOnlyDictionary<long,(int Width,int Height)>? minimums=null)
    {
        bool existing=Find(id) is not null;
        var layout=Layout(bounds,gap,outer);
        long target=layout.ContainsKey(near)?near:Windows.LastOrDefault();
        var cell=layout.GetValueOrDefault(target,bounds);
        if(minimums is null){if(!existing)Add(id,target,cell.Width>=cell.Height);return true;}
        var visible=minimums.Keys.ToHashSet();
        int margin=Math.Clamp(outer,0,Math.Max(0,Math.Min(bounds.Width,bounds.Height)/4));
        int space=Math.Clamp(gap,0,80);
        var area=new TileRect(bounds.X+margin,bounds.Y+margin,Math.Max(1,bounds.Width-2*margin),Math.Max(1,bounds.Height-2*margin));
        (int Width,int Height) Minimum(Node? n)
        {
            if(n is null)return (0,0);
            if(n.Leaf)return minimums.GetValueOrDefault(n.Window,(0,0));
            var a=Minimum(n.First);var b=Minimum(n.Second);
            if(a.Width==0)return b;if(b.Width==0)return a;
            return n.Horizontal?(a.Width+b.Width+space,Math.Max(a.Height,b.Height)):(Math.Max(a.Width,b.Width),a.Height+b.Height+space);
        }
        bool Fit(Node? n,TileRect r)
        {
            if(n is null)return true;
            var min=Minimum(n);if(min.Width>r.Width||min.Height>r.Height)return false;
            if(n.Leaf)return true;
            var a=Minimum(n.First);var b=Minimum(n.Second);
            if(a.Width==0)return Fit(n.Second,r);if(b.Width==0)return Fit(n.First,r);
            int available=(n.Horizontal?r.Width:r.Height)-space;
            int first=Math.Clamp((int)Math.Round(available*n.PreferredRatio),n.Horizontal?a.Width:a.Height,available-(n.Horizontal?b.Width:b.Height));
            n.Ratio=first/(double)available;
            return n.Horizontal?Fit(n.First,r with{Width=first})&&Fit(n.Second,r with{Width=available-first}):Fit(n.First,r with{Height=first})&&Fit(n.Second,r with{Height=available-first});
        }
        bool Fits()=>Fit(_root,area)&&Layout(bounds,gap,outer,visible).All(p=>!minimums.TryGetValue(p.Key,out var min)||p.Value.Width>=min.Width&&p.Value.Height>=min.Height);
        var original=Copy(_root);
        if(existing&&Fits())return true;
        if(_root is null){Add(id,target,cell.Width>=cell.Height);if(Fits())return true;Remove(id);return false;}
        if(!existing)foreach(var leaf in new[]{target}.Concat(layout.Keys).Distinct())
        {
            var rect=layout.GetValueOrDefault(leaf,bounds);bool preferred=rect.Width>=rect.Height;
            foreach(bool horizontal in new[]{preferred,!preferred})
            {
                Add(id,leaf,horizontal);
                if(Fits())return true;
                _root=Copy(original);
            }
        }
        var windows=existing?Windows.ToArray():Windows.Append(id).ToArray();
        foreach(bool horizontal in new[]{bounds.Width>=bounds.Height,bounds.Width<bounds.Height})
        {
            Node Build(int index,int length)
            {
                int count=windows.Length-index;if(count==1)return new(){Window=windows[index]};
                int first=Math.Max(1,(length-space*(count-1))/count);
                double ratio=Math.Clamp(first/(double)Math.Max(1,length-space),.001,.999);
                return new(){Horizontal=horizontal,Ratio=ratio,PreferredRatio=ratio,First=new(){Window=windows[index]},Second=Build(index+1,Math.Max(1,length-first-space))};
            }
            _root=Build(0,Math.Max(1,(horizontal?bounds.Width:bounds.Height)-2*margin));
            if(Fits())return true;
        }
        // Search slicing layouts in the same application order. Keep only the
        // non-dominated minimum envelopes, rather than enumerating permutations.
        // A tall player can share a column with a row of two shorter apps.
        var memo=new Dictionary<(int Start,int Count),List<(Node Node,int Width,int Height)>>();
        List<(Node Node,int Width,int Height)> Options(int start,int count)
        {
            if(memo.TryGetValue((start,count),out var cached))return cached;
            var candidates=new List<(Node Node,int Width,int Height)>();
            if(count==1){var m=minimums.GetValueOrDefault(windows[start],(1,1));candidates.Add((new(){Window=windows[start]},m.Item1,m.Item2));}
            else for(int split=1;split<count;split++)
                foreach(var a in Options(start,split))foreach(var b in Options(start+split,count-split))foreach(bool horizontal in new[]{true,false})
                {
                    int width=horizontal?a.Width+b.Width+space:Math.Max(a.Width,b.Width);
                    int height=horizontal?Math.Max(a.Height,b.Height):a.Height+b.Height+space;
                    if(width>area.Width||height>area.Height||candidates.Any(c=>c.Width<=width&&c.Height<=height))continue;
                    candidates.RemoveAll(c=>width<=c.Width&&height<=c.Height);
                    candidates.Add((new(){Horizontal=horizontal,First=Copy(a.Node),Second=Copy(b.Node)},width,height));
                }
            return memo[(start,count)]=candidates;
        }
        // Bound work for unusually large workspaces; ordinary layouts take a
        // few hundred integer comparisons and allocate only at admission.
        if(windows.Length<=12)
            foreach(var candidate in Options(0,windows.Length).OrderBy(c=>Math.Abs(c.Width/(double)c.Height-area.Width/(double)area.Height)))
            {_root=Copy(candidate.Node);if(Fits())return true;}
        _root=original;return false;
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
        target.PreferredRatio=target.Ratio;
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
