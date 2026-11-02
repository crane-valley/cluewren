namespace Cluewren.Kit;

public readonly record struct SonarWay(double Turn, double Clear, string? By, double? Drop = null, Vec? Normal = null, bool HasClear = true);

public sealed class NavMap
{
    public const double DefaultCell = 25.0;
    public const double Storey = 250.0;
    public const double SameFace = 0.94;
    public const double FaceGap = 30.0;
    public const double MaxFaceSpan = 450.0;
    public const double SolidSpan = 100.0;
    public const double Depth = 60.0;
    public const double GuessCost = 6.0;
    public const double LingerCell = 100.0;
    public const double LingerSeconds = 2.0;
    public const double LingerMax = 6.0;
    public const double LingerHalfLife = 60.0;
    public const double HitOutlives = 2.0;
    public const double NearCost = 1.5;
    public const double HeuristicWeight = 1.2;
    public const int MaxExpansions = 120000;

    readonly record struct Stop(double Angle, Vec Hit, Vec? Normal, string? By);

    readonly record struct QueueOrder(double F, double G, int I, int J) : IComparable<QueueOrder>
    {
        public int CompareTo(QueueOrder o)
        {
            var c = F.CompareTo(o.F);
            if (c != 0)
                return c;
            c = G.CompareTo(o.G);
            if (c != 0)
                return c;
            c = I.CompareTo(o.I);
            return c != 0 ? c : J.CompareTo(o.J);
        }
    }

    public NavMap(double? cell = null) => Cell = cell ?? DefaultCell;

    public double Cell { get; }

    public Dictionary<(int, int, int), double> Hits { get; set; } = [];

    public Dictionary<(int, int, int), double> Solid { get; set; } = [];

    public Dictionary<(int, int, int), double> Guesses { get; set; } = [];

    public Dictionary<(int, int, int), double> Passes { get; set; } = [];

    public Dictionary<(int, int, int), (double Spent, double When)> Lingered { get; set; } = [];

    public double LingerT { get; set; }

    public int Version { get; set; }

    static (int, int) Ij(double x, double y, double cell) => ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));

    public static int K(double z) => (int)Math.Floor(z / Storey);

    public (int, int, int) Key(Vec p, double? z = null)
    {
        var (i, j) = Ij(p.X, p.Y, Cell);
        return (i, j, K(z ?? p.Z));
    }

    public Vec Center(int i, int j, double z = 0) => new((i + 0.5) * Cell, (j + 0.5) * Cell, z);

    public bool BlockedKey((int, int, int) key)
    {
        if (!Hits.TryGetValue(key, out var h))
            return !Passes.ContainsKey(key) && Solid.ContainsKey(key);
        if (!Passes.TryGetValue(key, out var p))
            return true;
        return h > p + HitOutlives || Solid.ContainsKey(key);
    }

    public bool GuessedKey((int, int, int) key) => Guesses.ContainsKey(key) && !Passes.ContainsKey(key);

    public bool Blocked(Vec p, double? z = null) => BlockedKey(Key(p, z));

    void Hit(Vec p, double z, double t)
    {
        var key = Key(p, z);
        if (!BlockedKey(key))
            Version++;
        Hits[key] = t;
    }

    void Guess(Vec p, double z, double t, bool solid = false)
    {
        var key = Key(p, z);
        if (Passes.ContainsKey(key) || Hits.ContainsKey(key))
            return;
        var marks = solid ? Solid : Guesses;
        if (!marks.ContainsKey(key))
            Version++;
        marks[key] = t;
    }

    void Pass(Vec a, Vec direction, double length, double z, double t)
    {
        var c = Cell;
        var k = K(z);
        var s = 0.0;
        while (s <= length)
        {
            var p = a + direction * s;
            var (i, j) = Ij(p.X, p.Y, c);
            var m = Center(i, j);
            var off = Math.Abs((m.X - a.X) * direction.Y - (m.Y - a.Y) * direction.X);
            if (off <= c * 0.5)
                Passes[(i, j, k)] = t;
            s += c * 0.5;
        }
    }

    public void Observe(Vec at, double yaw, IReadOnlyList<SonarWay> ways, double t, bool avoidDrops = true,
        double maxDrop = 300.0, double? feet = null)
    {
        var z = feet ?? at.Z;
        var stops = new List<Stop>();
        var flat = new Vec(at.X, at.Y);
        foreach (var w in ways)
        {
            var clear = w.Clear;
            var ang = Geom.Radians(yaw + w.Turn);
            var d = new Vec(Math.Cos(ang), Math.Sin(ang));
            var by = w.By;
            var deep = by == "drop" && (w.Drop is null || w.Drop > maxDrop);
            var stop = by is "wall" or "steep" || (avoidDrops && deep);
            Pass(flat, d, stop ? clear - Cell * 0.5 : clear, z, t);
            if (!stop)
                continue;
            var hp = flat + d * (clear + Cell * 0.6);
            Hit(hp, z, t);
            var marks = (int)(Depth / (Cell * 0.5));
            for (var s = 1; s <= marks; s++)
                Guess(hp + d * (s * Cell * 0.5), z, t, solid: true);
            stops.Add(new Stop(Geom.ToDegrees(ang), hp, w.Normal is { } n ? new Vec(n.X, n.Y) : null, by));
        }
        stops = stops.Select((stop, index) => (stop, index)).OrderBy(pair => pair.stop.Angle).ThenBy(pair => pair.index)
            .Select(pair => pair.stop).ToList();
        for (var index = 0; index < stops.Count; index++)
        {
            var a = stops[index];
            var b = stops[(index + 1) % stops.Count];
            if (index == (index + 1) % stops.Count || Modulo(b.Angle - a.Angle, 360) > 50)
                continue;
            var span = b.Hit.Dist2d(a.Hit);
            if (span > MaxFaceSpan || span < Cell)
                continue;
            if (a.By == "wall" && b.By == "wall")
            {
                if (a.Normal is not { } na || b.Normal is not { } nb || na.Dot(nb) < SameFace
                    || Math.Abs((b.Hit - a.Hit).Dot(na)) > FaceGap)
                    continue;
            }
            else if (a.By != b.By)
            {
                continue;
            }
            var steps = (int)(span / (Cell * 0.5));
            Vec? inward = a.Normal is { } normal ? normal * -1.0 : null;
            var solid = span < SolidSpan;
            for (var s = 1; s < steps; s++)
            {
                var q = a.Hit + (b.Hit - a.Hit) * ((double)s / steps);
                Guess(q, z, t, solid);
                if (inward is { } into)
                    Guess(q + into * Cell, z, t, solid);
            }
        }
    }

    static double Modulo(double value, double by) => value - by * Math.Floor(value / by);

    public void BlockAhead(Vec at, double yawDeg, double t, double? feet = null, double? distance = null)
    {
        var d = new Vec(Math.Cos(Geom.Radians(yawDeg)), Math.Sin(Geom.Radians(yawDeg)));
        var side = new Vec(-d.Y, d.X);
        var z = feet ?? at.Z;
        var ahead = new Vec(at.X, at.Y) + d * (distance is > 0 or < 0 ? distance.Value : Cell * 1.5);
        for (var k = -1; k <= 1; k++)
            Hit(ahead + side * (k * Cell), z, t);
    }

    public void Linger(Vec at, double seconds, double t, double? feet = null)
    {
        var key = ((int)Math.Floor(at.X / LingerCell), (int)Math.Floor(at.Y / LingerCell), K(feet ?? at.Z));
        var (spent, when) = Lingered.TryGetValue(key, out var was) ? was : (0.0, t);
        Lingered[key] = (spent * Math.Pow(0.5, (t - when) / LingerHalfLife) + seconds, t);
        LingerT = Math.Max(LingerT, t);
    }

    public double LingerCost(int i, int j, int k, double t)
    {
        var (x, y) = ((i + 0.5) * Cell, (j + 0.5) * Cell);
        if (!Lingered.TryGetValue(((int)Math.Floor(x / LingerCell), (int)Math.Floor(y / LingerCell), k), out var hit))
            return 1.0;
        var spent = hit.Spent * Math.Pow(0.5, Math.Max(0.0, t - hit.When) / LingerHalfLife);
        return Math.Min(LingerMax, 1.0 + spent / LingerSeconds);
    }

    public void Forget(double before, bool guesses = true)
    {
        Hits = Hits.Where(pair => pair.Value >= before).ToDictionary();
        if (guesses)
        {
            Guesses = [];
            Solid = [];
        }
        Version++;
    }

    public bool LineFree(Vec a, Vec b, double z, bool guesses = true)
    {
        var d = b.Dist2d(a);
        if (d < 1e-6)
            return true;
        var n = (int)(d / (Cell * 0.5)) + 1;
        var k = K(z);
        var start = Ij(a.X, a.Y, Cell);
        for (var s = 1; s <= n; s++)
        {
            var p = a + (b - a) * ((double)s / n);
            var (i, j) = Ij(p.X, p.Y, Cell);
            if ((i, j) != start && (BlockedKey((i, j, k)) || (guesses && GuessedKey((i, j, k)))))
                return false;
        }
        return true;
    }

    public List<Vec>? Plan(Vec start, Vec goal, double within, double z, double? margin = null)
    {
        if (margin is { } given)
            return PlanWithin(start, goal, within, z, given);
        var @base = Math.Max(1500.0, 0.6 * goal.Dist2d(start));
        foreach (var m in new[] { @base, @base * 3, @base * 8 })
        {
            var way = PlanWithin(start, goal, within, z, m);
            if (way is not null)
                return way;
        }
        return null;
    }

    static readonly (int, int)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    List<Vec>? PlanWithin(Vec start, Vec goal, double within, double z, double margin)
    {
        var c = Cell;
        var k = K(z);
        var (loI, loJ) = Ij(Math.Min(start.X, goal.X) - margin, Math.Min(start.Y, goal.Y) - margin, c);
        var (hiI, hiJ) = Ij(Math.Max(start.X, goal.X) + margin, Math.Max(start.Y, goal.Y) + margin, c);
        var s = Ij(start.X, start.Y, c);
        var reach = Math.Max(within * 0.9, c * 0.75);
        var blocked = new Dictionary<(int, int), bool>();
        var lingered = Lingered.Count > 0;

        bool IsBlocked(int i, int j)
        {
            if (!blocked.TryGetValue((i, j), out var v))
                blocked[(i, j)] = v = (i, j) != s && BlockedKey((i, j, k));
            return v;
        }

        bool Near(int i, int j)
        {
            for (var di = -1; di <= 1; di++)
                for (var dj = -1; dj <= 1; dj++)
                    if ((di != 0 || dj != 0) && IsBlocked(i + di, j + dj))
                        return true;
            return false;
        }

        double H(int i, int j)
        {
            var dx = Math.Abs((i + 0.5) * c - goal.X);
            var dy = Math.Abs((j + 0.5) * c - goal.Y);
            return HeuristicWeight * Math.Max(0.0, Hypot(dx, dy) - reach);
        }

        bool Arrived(int i, int j) => Hypot((i + 0.5) * c - goal.X, (j + 0.5) * c - goal.Y) <= reach;

        var frontier = new PriorityQueue<(int, int), QueueOrder>();
        frontier.Enqueue(s, new QueueOrder(H(s.Item1, s.Item2), 0, s.Item1, s.Item2));
        var came = new Dictionary<(int, int), (int, int)?> { [s] = null };
        var cost = new Dictionary<(int, int), double> { [s] = 0 };
        (int, int)? end = null;
        var expansions = 0;
        while (frontier.TryDequeue(out var current, out var order))
        {
            if (order.G > cost.GetValueOrDefault(current, 1e18))
                continue;
            var (ci, cj) = current;
            if (Arrived(ci, cj))
            {
                end = current;
                break;
            }
            expansions++;
            if (expansions > MaxExpansions)
                break;
            foreach (var (di, dj) in Neighbours)
            {
                var (ni, nj) = (ci + di, cj + dj);
                if (ni < loI || ni > hiI || nj < loJ || nj > hiJ || IsBlocked(ni, nj))
                    continue;
                if (di != 0 && dj != 0 && (IsBlocked(ci + di, cj) || IsBlocked(ci, cj + dj)))
                    continue;
                var step = c * (di != 0 && dj != 0 ? 1.4142 : 1.0);
                if (GuessedKey((ni, nj, k)))
                    step *= GuessCost;
                else if (Near(ni, nj))
                    step *= NearCost;
                if (lingered)
                    step *= LingerCost(ni, nj, k, LingerT);
                var ng = order.G + step;
                if (ng < cost.GetValueOrDefault((ni, nj), 1e18))
                {
                    cost[(ni, nj)] = ng;
                    came[(ni, nj)] = current;
                    frontier.Enqueue((ni, nj), new QueueOrder(ng + H(ni, nj), ng, ni, nj));
                }
            }
        }
        if (end is null)
            return null;
        var cells = new List<(int, int)>();
        for (var cell = end; cell is { } at; cell = came[at])
            cells.Add(at);
        cells.Reverse();
        var points = cells.Skip(1).Select(cell => Center(cell.Item1, cell.Item2, z)).ToList();
        if (points.Count == 0)
            points.Add(new Vec(goal.X, goal.Y, z));
        return Smooth(start, points, z);
    }

    static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    public List<Vec> Smooth(Vec start, List<Vec> points, double z)
    {
        var way = new List<Vec>();
        var (at, i) = (start, 0);
        while (i < points.Count)
        {
            var j = points.Count - 1;
            while (j > i && !LineFree(at, points[j], z))
                j--;
            way.Add(points[j]);
            (at, i) = (points[j], j + 1);
        }
        return way;
    }

    public bool WayBlocked(Vec at, IReadOnlyList<Vec> path, double z, double? ahead = null)
    {
        var (prev, gone) = (at, 0.0);
        foreach (var point in path)
        {
            var p = point;
            var seg = p.Dist2d(prev);
            if (ahead is { } limit && gone + seg > limit)
                p = prev + (p - prev) * ((limit - gone) / Math.Max(seg, 1e-6));
            if (!LineFree(prev, p, z, guesses: false))
                return true;
            gone += seg;
            if (ahead is { } reached && gone >= reached)
                return false;
            prev = p;
        }
        return false;
    }

    public static async ValueTask<NavMap> Of(Game game, CancellationToken ct = default)
    {
        string world;
        try
        {
            world = await game.World(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            world = "";
        }
        if (!game.NavMaps.TryGetValue(world, out var map))
            game.NavMaps[world] = map = new NavMap();
        return map;
    }
}
