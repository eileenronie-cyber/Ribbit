namespace Ribbit.Geometry;

/// <summary>What a stroke of the Elbow dial means - the Revit side turns it into a colour.</summary>
public enum DialInk
{
    /// <summary>The plane the sideways turns lie in: a circle in the model, an ellipse on screen.</summary>
    SidewaysPlane,

    /// <summary>The plane the up/down turns lie in.</summary>
    UpDownPlane,

    /// <summary>A sideways 90 (solid).</summary>
    Sideways90,

    /// <summary>A sideways 45 (dashed).</summary>
    Sideways45,

    /// <summary>An up or down 90 (solid).</summary>
    UpDown90,

    /// <summary>An up or down 45 (dashed).</summary>
    UpDown45,

    /// <summary>The small circle on the open end: a click inside it asks again.</summary>
    ClickAgain,
}

/// <summary>One straight stroke of the dial, in model coordinates.</summary>
public readonly record struct DialStroke(Vec3 A, Vec3 B, DialInk Ink);

/// <summary>
/// One circle of the dial, in model coordinates: centre, the two unit axes of
/// its plane, radius. <paramref name="Dashed"/>: drawn as every other piece
/// (the click-again circle).
/// </summary>
public readonly record struct DialCircle(Vec3 Centre, Vec3 E1, Vec3 E2, double Radius, DialInk Ink, bool Dashed)
{
    /// <summary>The point at angle a (radians) from E1 toward E2.</summary>
    public Vec3 PointAt(double a) => Centre.Add(E1.Multiply(Radius * Math.Cos(a))).Add(E2.Multiply(Radius * Math.Sin(a)));
}

/// <summary>
/// Where one turn is clicked: (U, V) in feet from the open end along the
/// view's right / up. <paramref name="Moved"/> when it is not where the turn
/// really points on screen (pushed off a turn sharing its spot, or parked
/// because it points straight at the viewer). <paramref name="Mark"/>: +1 a
/// dot (up, or toward you), -1 a cross (down, or away from you), 0 none.
/// </summary>
public readonly record struct DialTarget(TurnCandidate Turn, double U, double V, bool Moved, int Mark);

/// <summary>
/// The direction dial Elbow (Click Dir) draws in a 3D view while it waits for
/// the direction click - and what that click means. There is no menu: EVERY
/// turn has a target of its own, and the click takes the nearest one.
///
/// Each target starts at the TIP of its turn as the view shows it: a turn
/// lying across the screen reaches the full radius, one heading into the
/// screen is foreshortened. That is what makes two turns that point the same
/// way on screen still clickable - in Revit's default 3D view "90 north" and
/// "45 up" off an east run share a screen line, but the 90's tip is further
/// out. Where two tips would still sit on top of each other (an orbit that
/// lines up two same-size turns, or turns straight at the viewer, which have
/// no screen direction at all), the targets are pushed apart until each has
/// room, and the arrow is drawn to where its target ended up.
///
/// Colour says the plane (teal sideways, orange up/down; both planes are drawn as
/// circles in the model so the view's own perspective shows which is level) and,
/// by its shade, the size: a 90 deep and solid, a 45 pale and dashed. A dot marks a turn going up
/// and a cross one going down.
///
/// Pure geometry: the Revit side (ElbowDialLines) only draws the strokes and circles.
/// </summary>
public sealed class ElbowDial
{
    /// <summary>Segments per plane circle in a picture of the dial (Segments).</summary>
    public const int CircleSegments = 48;

    /// <summary>Segments per target circle in a picture of the dial.</summary>
    public const int TargetSegments = 20;

    /// <summary>The full radius is this many dead-zone radii: clear of the click-again circle, small enough to stay local.</summary>
    public const double RingOverDeadZone = 4.0;

    /// <summary>Targets stay at least this far out (a fraction of the radius): clear of the click-again circle.</summary>
    public const double MinReach = 0.42;

    /// <summary>... and no further out than this.</summary>
    public const double MaxReach = 1.1;

    /// <summary>Two targets end up at least this far apart (a fraction of the radius), so each has room to be clicked.</summary>
    public const double Spacing = 0.28;

    /// <summary>The drawn target circle's radius, as a fraction of the dial radius.</summary>
    public const double TargetSize = 0.1;

    /// <summary>A click further out than this is pulled in along its own direction: a long throw past a tip still means that tip.</summary>
    public const double ClickClamp = 1.15;

    /// <summary>A click further than this from every target is not taken (a fraction of the radius).</summary>
    public const double MaxMiss = 0.5;

    /// <summary>How long the spreading may go on; the layouts that occur settle in a handful of passes.</summary>
    public const int SpreadPasses = 500;

    /// <summary>A target further than this from its turn's true tip counts as moved.</summary>
    public const double MovedTol = 0.02;

    /// <summary>A turn this close to straight at / away from the viewer gets a dot or a cross even when it goes sideways.</summary>
    public const double FacingDepth = 0.9;

    /// <summary>Where a turn with no screen direction at all is parked, as a fraction of the radius.</summary>
    public const double ParkReach = 0.6;

    public Vec3 Origin { get; }
    public double DeadZone { get; }
    public double Radius { get; }
    public List<DialTarget> Targets { get; } = [];
    /// <summary>Every straight stroke: shafts, dashes, crosses.</summary>
    public List<DialStroke> Strokes { get; } = [];

    /// <summary>Every circle: the planes, the click-again circle, the targets, the dots.</summary>
    public List<DialCircle> Circles { get; } = [];

    private ElbowDial(Vec3 origin, double deadZone, double radius)
    {
        Origin = origin;
        DeadZone = deadZone;
        Radius = radius;
    }

    /// <summary>
    /// The dial for a run heading <paramref name="runDir"/> into its open end at
    /// <paramref name="origin"/>, seen with screen <paramref name="right"/> /
    /// <paramref name="up"/>. <paramref name="frameRad"/>: the building's
    /// rotation on plan, for a riser (<see cref="ElbowMath.DominantFrame"/>).
    /// </summary>
    public static ElbowDial Build(Vec3 origin, Vec3 runDir, Vec3 right, Vec3 up, double deadZone, double frameRad = 0.0)
    {
        ElbowDial dial = new(origin, deadZone, RingOverDeadZone * deadZone);
        Vec3 toViewer = right.CrossProduct(up);
        List<TurnCandidate> turns = ElbowMath.TurnCandidates(runDir, frameRad);

        // The run's own direction on screen: a turn pointing straight at the
        // viewer is parked beside it, BEHIND the open end, where no turn ever
        // points (every turn is at most 90 degrees off the run).
        (double ru, double rv) = ElbowMath.ScreenUv(runDir, right, up);
        double runAngle = Math.Sqrt((ru * ru) + (rv * rv)) < ElbowMath.ScreenMinProj ? -Math.PI / 2 : Math.Atan2(rv, ru);

        List<(double U, double V)> at = [];
        List<(double U, double V)> tip = [];
        List<int> marks = [];
        foreach (TurnCandidate c in turns)
        {
            (double u, double v) = ElbowMath.ScreenUv(c.Direction, right, up);
            int mark = Mark(c, toViewer);
            tip.Add((u, v));
            marks.Add(mark);
            if (Math.Sqrt((u * u) + (v * v)) < 1e-6)
            {
                double a = runAngle + (mark >= 0 ? 0.75 : -0.75) * Math.PI;
                at.Add((ParkReach * Math.Cos(a), ParkReach * Math.Sin(a)));
            }
            else
            {
                at.Add(Clamp(u, v));
            }
        }

        Spread(at);

        for (int i = 0; i < turns.Count; i++)
        {
            bool moved = Math.Sqrt(Sq(at[i].U - tip[i].U) + Sq(at[i].V - tip[i].V)) > MovedTol;
            dial.Targets.Add(new DialTarget(turns[i], at[i].U * dial.Radius, at[i].V * dial.Radius, moved, marks[i]));
        }

        dial.Draw(runDir, turns, right, up);
        return dial;
    }

    /// <summary>
    /// What a click (feet from the open end along right / up) chooses: the
    /// nearest target, or a reason to put in the next prompt when the click
    /// names none - on the open end itself, or nowhere near any arrow. The tool
    /// then asks for another click; nothing is ever guessed.
    /// </summary>
    public (TurnCandidate? Turn, string? Reason) Resolve(double u, double v)
    {
        double m = Math.Sqrt((u * u) + (v * v));
        if (m < DeadZone)
            return (null, "that click was on the open end itself - click on an arrow");

        double far = ClickClamp * Radius;
        if (m > far)
        {
            u *= far / m;
            v *= far / m;
        }

        DialTarget best = Targets[0];
        double bestDistance = double.MaxValue;
        foreach (DialTarget t in Targets)
        {
            double d = Math.Sqrt(Sq(t.U - u) + Sq(t.V - v));
            if (d < bestDistance)
            {
                best = t;
                bestDistance = d;
            }
        }

        if (bestDistance > MaxMiss * Radius)
            return (null, "that click was not near any arrow - click on the one you want");

        return (best.Turn, null);
    }

    /// <summary>The ink of a turn's arrow: its plane by colour, its size by dash.</summary>
    public static DialInk InkOf(TurnCandidate c) => ElbowMath.IsSideways(c)
        ? (c.AngleDeg == 90 ? DialInk.Sideways90 : DialInk.Sideways45)
        : (c.AngleDeg == 90 ? DialInk.UpDown90 : DialInk.UpDown45);

    /// <summary>
    /// +1 dot, -1 cross, 0 none. An up/down turn always carries one (dot = up),
    /// so the two halves of a pair that share a spot read apart; a sideways turn
    /// only when it points nearly at you or away from you (dot = toward you).
    /// </summary>
    private static int Mark(TurnCandidate c, Vec3 toViewer)
    {
        if (!ElbowMath.IsSideways(c))
            return c.Direction.Z > 0 ? 1 : -1;

        double depth = c.Direction.DotProduct(toViewer);
        return depth > FacingDepth ? 1 : depth < -FacingDepth ? -1 : 0;
    }

    private static double Sq(double x) => x * x;

    /// <summary>Into the band [MinReach, MaxReach] along the same direction (never called with the zero vector).</summary>
    private static (double U, double V) Clamp(double u, double v)
    {
        double m = Math.Sqrt((u * u) + (v * v));
        double to = Math.Max(MinReach, Math.Min(MaxReach, m));
        return (u * to / m, v * to / m);
    }

    /// <summary>
    /// Pushes targets apart, a pair at a time, until every two are at least
    /// <see cref="Spacing"/> apart (or the passes run out). Deterministic: the
    /// same view always gives the same dial.
    ///
    /// A pair is pushed apart along the line between them, which keeps two
    /// turns sharing a screen line ON that line, told apart by length. When
    /// that push is blocked - the pair sits on one line through the open end
    /// and the reach band holds them there (a riser seen level puts all eight
    /// flat 90s on one horizontal line), or they share the very same spot -
    /// they are pushed round the dial instead, across their shared direction.
    /// </summary>
    private static void Spread(List<(double U, double V)> at)
    {
        bool moved = true;
        for (int pass = 0; pass < SpreadPasses && moved; pass++)
        {
            moved = false;
            for (int i = 0; i < at.Count; i++)
            {
                for (int j = i + 1; j < at.Count; j++)
                {
                    double dx = at[j].U - at[i].U, dy = at[j].V - at[i].V;
                    double d = Math.Sqrt((dx * dx) + (dy * dy));
                    if (d >= Spacing)
                        continue;

                    moved = true;
                    double push = (Spacing - d) / 2;
                    (double U, double V) a = at[i], b = at[j];
                    if (d >= 1e-9)
                    {
                        a = Clamp(at[i].U - (dx / d * push), at[i].V - (dy / d * push));
                        b = Clamp(at[j].U + (dx / d * push), at[j].V + (dy / d * push));
                    }

                    // Blocked (or no line to push along): round the dial.
                    if (Math.Sqrt(Sq(b.U - a.U) + Sq(b.V - a.V)) < d + push)
                    {
                        double mu = (at[i].U + at[j].U) / 2, mv = (at[i].V + at[j].V) / 2;
                        double mm = Math.Sqrt((mu * mu) + (mv * mv));
                        (double tx, double ty) = (-mv / mm, mu / mm);
                        a = Clamp(at[i].U - (tx * push), at[i].V - (ty * push));
                        b = Clamp(at[j].U + (tx * push), at[j].V + (ty * push));
                    }

                    at[i] = a;
                    at[j] = b;
                }
            }
        }
    }

    // ------------------------------------------------------------------ drawing

    private Vec3 At(Vec3 right, Vec3 up, double u, double v) => Origin.Add(right.Multiply(u)).Add(up.Multiply(v));

    private void Line(Vec3 a, Vec3 b, DialInk ink) => Strokes.Add(new DialStroke(a, b, ink));

    /// <summary>
    /// A circle of radius r in the plane of e1 / e2 (unit, perpendicular). Kept
    /// whole, not cut into segments: the Revit side draws it as arcs, a handful of
    /// elements where segments made hundreds (and drawing them was the dial's lag).
    /// </summary>
    private void Circle(Vec3 centre, Vec3 e1, Vec3 e2, double r, DialInk ink, bool dashed = false)
        => Circles.Add(new DialCircle(centre, e1, e2, r, ink, dashed));

    /// <summary>
    /// A circle as straight segments, every one or (dashed) every other one - for
    /// a picture of the dial (DialPreview) and for checks, not for drawing in Revit.
    /// </summary>
    public static List<DialStroke> Segments(DialCircle c, int segments)
    {
        List<DialStroke> strokes = [];
        for (int i = 0; i < segments; i += c.Dashed ? 2 : 1)
        {
            double a0 = 2 * Math.PI * i / segments, a1 = 2 * Math.PI * (i + 1) / segments;
            strokes.Add(new DialStroke(c.PointAt(a0), c.PointAt(a1), c.Ink));
        }

        return strokes;
    }

    /// <summary>A plane seen this close to edge-on is a line through the dial that says nothing: not drawn.</summary>
    public const double EdgeOnPlane = 0.2;

    private void Draw(Vec3 runDir, List<TurnCandidate> turns, Vec3 right, Vec3 up)
    {
        Vec3 toViewer = right.CrossProduct(up);

        // The planes, as true circles in the model: whatever the orbit, the view
        // itself shows which one is level. A riser has no up/down plane of its
        // own (each of its 45s climbs in a different one).
        if (ElbowMath.IsDeadVertical(runDir))
        {
            Plane(turns[0].Direction, turns[2].Direction, toViewer, DialInk.SidewaysPlane);
        }
        else
        {
            Vec3 side = turns.First(c => c.Kind == TurnKind.Plan && c.AngleDeg == 90).Direction;
            Vec3 lift = turns.First(c => c.Kind == TurnKind.Vertical && c.AngleDeg == 90).Direction;
            Plane(runDir, side, toViewer, DialInk.SidewaysPlane);
            Plane(runDir, lift, toViewer, DialInk.UpDownPlane);
        }

        Circle(Origin, right, up, DeadZone, DialInk.ClickAgain, dashed: true);

        double ring = TargetSize * Radius;
        foreach (DialTarget t in Targets)
        {
            DialInk ink = InkOf(t.Turn);
            double reach = Math.Sqrt(Sq(t.U) + Sq(t.V));
            (double du, double dv) = (t.U / reach, t.V / reach);

            // The shaft, from the click-again circle to this target's own circle:
            // one stroke for a 90, four dashes for a 45. Where it would run
            // through ANOTHER target's circle - the 90 whose line a 45 shares -
            // it stops short and starts again beyond, so each circle reads as
            // its own target and not as a bead on someone else's arrow.
            List<(double A, double B)> cuts = [];
            foreach (DialTarget o in Targets)
            {
                double across = Math.Abs((du * o.V) - (dv * o.U));
                if (o.Equals(t) || across >= ring)
                    continue;
                double along = (du * o.U) + (dv * o.V), half = Math.Sqrt(Sq(ring) - Sq(across));
                cuts.Add((along - half, along + half));
            }

            double start = DeadZone, end = reach - ring;
            const int dashes = 4;
            int pieces = t.Turn.AngleDeg == 90 ? 1 : dashes;
            for (int i = 0; i < pieces; i++)
            {
                double s0 = pieces == 1 ? start : start + ((end - start) * (2 * i) / ((2 * dashes) - 1));
                double s1 = pieces == 1 ? end : start + ((end - start) * ((2 * i) + 1) / ((2 * dashes) - 1));
                foreach ((double a, double b) in Without((s0, s1), cuts))
                    Line(At(right, up, du * a, dv * a), At(right, up, du * b, dv * b), ink);
            }

            Vec3 centre = At(right, up, t.U, t.V);
            Circle(centre, right, up, ring, ink);
            if (t.Mark > 0)
            {
                Circle(centre, right, up, ring * 0.3, ink);
            }
            else if (t.Mark < 0)
            {
                double x = ring * 0.55;
                Line(At(right, up, t.U - x, t.V - x), At(right, up, t.U + x, t.V + x), ink);
                Line(At(right, up, t.U - x, t.V + x), At(right, up, t.U + x, t.V - x), ink);
            }
        }
    }

    /// <summary>A plane through the open end as a circle of the dial's radius, unless it is seen nearly edge-on.</summary>
    private void Plane(Vec3 e1, Vec3 e2, Vec3 toViewer, DialInk ink)
    {
        if (Math.Abs(e1.CrossProduct(e2).DotProduct(toViewer)) < EdgeOnPlane)
            return;
        Circle(Origin, e1, e2, Radius, ink);
    }

    /// <summary>The parts of the interval (A, B) that no cut covers.</summary>
    private static List<(double A, double B)> Without((double A, double B) span, List<(double A, double B)> cuts)
    {
        List<(double A, double B)> pieces = [span];
        foreach ((double c0, double c1) in cuts)
        {
            List<(double A, double B)> next = [];
            foreach ((double a, double b) in pieces)
            {
                if (c1 <= a || c0 >= b)
                {
                    next.Add((a, b));
                    continue;
                }

                if (c0 > a)
                    next.Add((a, c0));
                if (c1 < b)
                    next.Add((c1, b));
            }

            pieces = next;
        }

        return pieces;
    }
}
