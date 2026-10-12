namespace FloatingDock.Core.Tiling;

/// <summary>Wait for startup geometry to settle; never hold a slow app indefinitely.</summary>
public sealed class WindowArrivalGate
{
    private sealed class Arrival(double now,TileRect rect)
    {public double First=now,Changed=now;public TileRect Last=rect;}
    private readonly Dictionary<long,Arrival> _pending=new();
    public bool Waiting { get; private set; }
    public void BeginPass(IEnumerable<long> live)
    {var ids=live.ToHashSet();foreach(var id in _pending.Keys.ToArray())if(!ids.Contains(id))_pending.Remove(id);Waiting=false;}
    public bool Ready(long id,TileRect rect,double now,int delay)
    {
        if(delay==0)return true;
        if(!_pending.TryGetValue(id,out var arrival))_pending[id]=arrival=new(now,rect);
        if(rect!=arrival.Last){arrival.Last=rect;arrival.Changed=now;}
        bool ready=now-arrival.First>=Math.Max(700,delay+100)||(now-arrival.First>=delay&&now-arrival.Changed>=90);
        if(ready)_pending.Remove(id);else Waiting=true;
        return ready;
    }
    public void Forget(long id)=>_pending.Remove(id);
    public void Clear(){_pending.Clear();Waiting=false;}
}
