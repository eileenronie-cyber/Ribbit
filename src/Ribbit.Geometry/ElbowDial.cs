namespace Ribbit.Geometry;

/// <summary>What a stroke of the Elbow dial means - the Revit side turns it into a colour.</summary>
public enum DialInk
{
    /// <summary>The outer ring.</summary>
    Ring,

    /// <summary>The dead zone: a click inside it opens the menu of every turn.</summary>
    MenuZone,

    /// <summary>A turn a click in that direction makes: a 90.</summary>
    Ninety,

    /// <summary>A turn a click in that direction makes: a 45.</summary>
    FortyFive,

    /// <summary>A click in that direction cannot choose (two turns share it on screen): it opens the menu.</summary>
    Ask,

    /// <summary>Turns straight into / out of the screen exist: only the menu reaches them.</summary>
    EdgeOn,
}

/// <summary>One straight stroke of the dial, in model coordinates, lying in the screen plane.</summary>
public readonly record struct DialStroke(Vec3 A, Vec3 B, DialInk Ink);

/// <summary>One arrow of the dial: the turn a click along it makes (null for an Ask arrow).</summary>
public readonly record struct DialSpoke(double U, double V, TurnCandidate? Turn, DialInk Ink);

/// <summary>
/// The direction dial Elbow (Click Dir) draws on the open end while it waits
/// for the direction click: a ring, one arrow per direction a click can
/// choose, and the inner circle where a click opens the menu instead.
///
/// THE ARROWS COME FROM THE CLICK RESOLVER ITSELF. For every turn visible on
/// screen, a click straight along it is put through
/// <see cref="ElbowMath.PickTurnDirection"/>, and the arrow shows what that
/// click would really give - so the dial cannot promise a turn the click
/// would not make. A turn that is hidden behind another on screen (the 45 up
/// behind a riser's flat 90 in plan) gets no arrow of its own; a direction
/// where the click cannot decide is drawn grey (Ask) - clicking there opens
/// the menu.
///
/// Pure geometry: the Revit side (ElbowDialServer) only paints the strokes.
/// </summary>
public sealed class ElbowDial
{
    /// <summary>Segments per circle: smooth at any zoom the ring is drawn at.</summary>
    public const int CircleSegments = 48;

    /// <summary>The ring is this many dead-zone radii out: clear of the menu zone, small enough to stay local.</summary>
    public const double RingOverDeadZone = 4.0;

    /// <summary>A 45's arrow is this much of a 90's, so the two read apart even without colour.</summary>
    public const double FortyFiveLength = 0.75;

    public Vec3 Origin { get; }
    public double DeadZone { get; }
    public double Radius { get; }
    public List<DialSpoke> Spokes { get; } = [];
    public List<DialStroke> Strokes { get; } = [];

    /// <summary>True when some turn points straight into or out of the screen (menu only).</summary>
    public bool HasEdgeOn { get; }

    private ElbowDial(Vec3 origin, double deadZone, double radius, bool edgeOn)
    {
        Origin = origin;
        DeadZone = deadZone;
        Radius = radius;
        HasEdgeOn = edgeOn;
    }

    public static ElbowDial Build(Vec3 origin, Vec3 runDir, Vec3 right, Vec3 up, double deadZone)
    {
        double radius = RingOverDeadZone * deadZone;
        List<TurnCandidate> candidates = ElbowMath.TurnCandidates(runDir);
        bool edgeOn = candidates.Exists(c => ElbowMath.IsEdgeOn(c.Direction, right, up));
        ElbowDial dial = new(origin, deadZone, radius, edgeOn);

        foreach (TurnCandidate c in candidates)
        {
            (double u, double v) = ElbowMath.ScreenUv(c.Direction, right, up);
            double mag = Math.Sqrt((u * u) + (v * v));
            if (mag < ElbowMath.ScreenMinProj)
                continue;

            u /= mag;
            v /= mag;
            if (dial.Spokes.Exists(s => (s.U * u) + (s.V * v) > 1.0 - 1.0e-9))
                continue;   // this screen direction already has its arrow

            (TurnCandidate? chosen, _) = ElbowMath.PickTurnDirection(runDir, (u * radius, v * radius), right, up, deadZone);
            DialSpoke spoke = chosen is null
                ? new DialSpoke(u, v, null, DialInk.Ask)
                : new DialSpoke(u, v, chosen, chosen.Value.AngleDeg == 90 ? DialInk.Ninety : DialInk.FortyFive);

            // A click along this line gives a turn whose OWN screen direction is
            // elsewhere (possible only in a close contest): draw the arrow where
            // that turn really points instead, once.
            if (chosen is not null)
            {
                (double cu, double cv) = ElbowMath.ScreenUv(chosen.Value.Direction, right, up);
                double cm = Math.Sqrt((cu * cu) + (cv * cv));
                spoke = spoke with { U = cu / cm, V = cv / cm };
                if (dial.Spokes.Exists(s => (s.U * spoke.U) + (s.V * spoke.V) > 1.0 - 1.0e-9))
                    continue;
            }

            dial.Spokes.Add(spoke);
        }

        dial.Draw(right, up);
        return dial;
    }

    private Vec3 At(Vec3 right, Vec3 up, double u, double v) => Origin.Add(right.Multiply(u)).Add(up.Multiply(v));

    private void Circle(Vec3 right, Vec3 up, double r, DialInk ink)
    {
        for (int i = 0; i < CircleSegments; i++)
        {
            double a0 = 2 * Math.PI * i / CircleSegments, a1 = 2 * Math.PI * (i + 1) / CircleSegments;
            Strokes.Add(new DialStroke(At(right, up, r * Math.Cos(a0), r * Math.Sin(a0)), At(right, up, r * Math.Cos(a1), r * Math.Sin(a1)), ink));
        }
    }

    private void Draw(Vec3 right, Vec3 up)
    {
        Circle(right, up, Radius, DialInk.Ring);
        Circle(right, up, DeadZone, DialInk.MenuZone);

        if (HasEdgeOn)
        {
            // An X in the menu zone: there are turns straight into/out of the screen, and it is where they are.
            double x = DeadZone * 0.5;
            Strokes.Add(new DialStroke(At(right, up, -x, -x), At(right, up, x, x), DialInk.EdgeOn));
            Strokes.Add(new DialStroke(At(right, up, -x, x), At(right, up, x, -x), DialInk.EdgeOn));
        }

        foreach (DialSpoke s in Spokes)
        {
            double tip = s.Ink == DialInk.FortyFive ? Radius * FortyFiveLength : Radius;
            double head = Radius * 0.12;
            (double pu, double pv) = (-s.V, s.U);   // perpendicular, on screen
            Vec3 a = At(right, up, s.U * DeadZone, s.V * DeadZone);
            Vec3 b = At(right, up, s.U * tip, s.V * tip);

            if (s.Ink == DialInk.Ask)
            {
                // Dashed: a direction that asks rather than decides.
                const int dashes = 5;
                for (int i = 0; i < dashes; i++)
                {
                    double t0 = (double)(2 * i) / ((2 * dashes) - 1), t1 = (double)((2 * i) + 1) / ((2 * dashes) - 1);
                    Strokes.Add(new DialStroke(Lerp(a, b, t0), Lerp(a, b, t1), DialInk.Ask));
                }

                continue;
            }

            Strokes.Add(new DialStroke(a, b, s.Ink));
            Strokes.Add(new DialStroke(b, At(right, up, (s.U * (tip - head)) + (pu * head * 0.6), (s.V * (tip - head)) + (pv * head * 0.6)), s.Ink));
            Strokes.Add(new DialStroke(b, At(right, up, (s.U * (tip - head)) - (pu * head * 0.6), (s.V * (tip - head)) - (pv * head * 0.6)), s.Ink));
        }
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a.Add(b.Subtract(a).Multiply(t));
}
