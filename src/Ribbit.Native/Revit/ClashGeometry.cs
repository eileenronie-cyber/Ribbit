using Autodesk.Revit.DB;
using Ribbit.Geometry;
using Ribbit.Geometry.Clash;

namespace Ribbit.Native.Revit;

/// <summary>
/// Revit-side geometry for clash detection. Ported from lib/clashlib/geometry.py.
///
/// Everything here needs a Document or a live GeometryObject; the pure box and
/// grid maths lives in <see cref="Ribbit.Geometry.Clash"/> and is unit tested.
/// </summary>
internal static class ClashGeometry
{
    private const double MinSolidVolume = 1e-10;

    public static Options MakeGeometryOptions() => new()
    {
        ComputeReferences = false,
        DetailLevel = ViewDetailLevel.Fine,
        // Symbolic and non-visible geometry still occupies space; excluding it
        // would silently under-report clashes against placeholder families.
        IncludeNonVisibleObjects = true,
    };

    public static View? GetSafeActiveView(Document doc)
    {
        try
        {
            View av = doc.ActiveView;
            return av is not null && av.IsValidObject ? av : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Wraps a Revit Transform as the Revit-free delegate <see cref="Aabb"/> expects.</summary>
    public static Func<Vec3, Vec3> AsMapper(Transform tr)
        => v => tr.OfPoint(v.ToXYZ()).ToVec3();

    // ---------------------------------------------------------- bounding box

    /// <summary>
    /// The element's box in its OWN document's coordinates.
    ///
    /// Model box first, then the active view's, then a walk of the actual
    /// geometry. The last one matters for families whose model box comes back
    /// null but which genuinely occupy space.
    /// </summary>
    public static Aabb? GetBoundingBox(Element e, View? activeView, Options geomOpts)
    {
        Aabb? bb = TryBox(e, null);
        if (bb is not null)
            return bb;

        if (activeView is not null)
        {
            bb = TryBox(e, activeView);
            if (bb is not null)
                return bb;
        }

        return GeometryBounds(e, geomOpts);
    }

    private static Aabb? TryBox(Element e, View? view)
    {
        try
        {
            BoundingBoxXYZ? bb = e.get_BoundingBox(view);
            if (bb?.Min is null || bb.Max is null)
                return null;

            return new Aabb(bb.Min.ToVec3(), bb.Max.ToVec3());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Aabb? GeometryBounds(Element e, Options geomOpts)
    {
        GeometryElement? geom;
        try
        {
            geom = e.get_Geometry(geomOpts);
        }
        catch (Exception)
        {
            return null;
        }

        if (geom is null)
            return null;

        List<Vec3> points = [];

        void Walk(GeometryObject obj, Transform accTr)
        {
            try
            {
                switch (obj)
                {
                    case Solid solid when solid.Volume > MinSolidVolume:
                    {
                        // A solid's box is expressed in ITS OWN frame: Min/Max
                        // are relative to sbb.Transform, which is not identity
                        // (for a pipe, its origin sits at the solid's centre).
                        // Using Min/Max raw put this fallback box tens of feet
                        // from the element - measured 28-78 ft on 16 of 16
                        // Snowdon elements. All eight corners go through the
                        // box's transform, because it can rotate.
                        BoundingBoxXYZ? sbb = solid.GetBoundingBox();
                        if (sbb is not null)
                        {
                            Transform toModel = accTr.Multiply(sbb.Transform);
                            foreach (double x in new[] { sbb.Min.X, sbb.Max.X })
                            foreach (double y in new[] { sbb.Min.Y, sbb.Max.Y })
                            foreach (double z in new[] { sbb.Min.Z, sbb.Max.Z })
                                points.Add(toModel.OfPoint(new XYZ(x, y, z)).ToVec3());
                        }
                        break;
                    }

                    case Mesh mesh:
                    {
                        for (int i = 0; i < mesh.Vertices.Count; i++)
                            points.Add(accTr.OfPoint(mesh.Vertices[i]).ToVec3());
                        break;
                    }

                    case GeometryInstance gi:
                    {
                        // GetInstanceGeometry() already returns geometry in the
                        // enclosing (eventually host) coordinate system, so
                        // gi.Transform must NOT be applied again on top - accTr
                        // passes through UNCHANGED. See ElementSolids for the
                        // live-caught symptom of getting this wrong.
                        GeometryElement? inst = gi.GetInstanceGeometry();
                        if (inst is not null)
                        {
                            foreach (GeometryObject ig in inst)
                                Walk(ig, accTr);
                        }
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // One unreadable geometry object must not lose the rest of the
                // element's extent.
            }
        }

        foreach (GeometryObject g in geom)
            Walk(g, Transform.Identity);

        return points.Count == 0 ? null : Aabb.FromPoints(points);
    }

    // ----------------------------------------------------------------- solids

    public static List<Solid> TransformSolids(IReadOnlyList<Solid> solids, Transform? tr)
    {
        if (tr is null)
            return [.. solids];

        List<Solid> outp = [];
        foreach (Solid s in solids)
        {
            try
            {
                outp.Add(SolidUtils.CreateTransformed(s, tr));
            }
            catch (Exception)
            {
                // A solid that refuses to transform is dropped rather than
                // failing the element; its siblings still get tested.
            }
        }
        return outp;
    }

    private static void TryAddSolid(List<Solid> outp, Solid? s, Transform? tr)
    {
        try
        {
            if (s is null || s.Volume <= MinSolidVolume)
                return;
        }
        catch (Exception)
        {
            return;
        }

        if (tr is null)
        {
            outp.Add(s);
            return;
        }

        try
        {
            outp.Add(SolidUtils.CreateTransformed(s, tr));
        }
        catch (Exception)
        {
            // As above - drop this one, keep going.
        }
    }

    /// <summary>
    /// A mesh converted to solids so it can take part in Boolean intersection.
    /// Meshes cannot be Boolean'd directly, and an element that only has mesh
    /// geometry would otherwise fall through to the bounding-box-only path.
    /// </summary>
    private static List<Solid> MeshToSolids(Mesh mesh, Transform? tr)
    {
        List<Solid> solids = [];

        try
        {
            if (mesh.NumTriangles <= 0)
                return solids;

            TessellatedShapeBuilder tsb = new();
            tsb.OpenConnectedFaceSet(true);
            tsb.Target = TessellatedShapeBuilderTarget.Solid;
            tsb.Fallback = TessellatedShapeBuilderFallback.Mesh;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                try
                {
                    MeshTriangle tri = mesh.get_Triangle(i);
                    XYZ p0 = tri.get_Vertex(0);
                    XYZ p1 = tri.get_Vertex(1);
                    XYZ p2 = tri.get_Vertex(2);

                    if (tr is not null)
                    {
                        p0 = tr.OfPoint(p0);
                        p1 = tr.OfPoint(p1);
                        p2 = tr.OfPoint(p2);
                    }

                    TessellatedFace face = new([p0, p1, p2], ElementId.InvalidElementId);
                    if (face.IsValidObject)
                        tsb.AddFace(face);
                }
                catch (Exception)
                {
                    // Skip a degenerate triangle rather than losing the mesh.
                }
            }

            tsb.CloseConnectedFaceSet();
            tsb.Build();

            TessellatedShapeBuilderResult res = tsb.GetBuildResult();
            foreach (GeometryObject go in res.GetGeometricalObjects())
            {
                if (go is Solid s && s.Volume > MinSolidVolume)
                    solids.Add(s);
            }
        }
        catch (Exception)
        {
            // A mesh that will not tessellate yields no solids; the caller then
            // falls back to the bounding-box-only clash path.
        }

        return solids;
    }

    /// <summary>
    /// Every solid of the element, in its OWN document's coordinates — never
    /// link-transformed. The caller applies the link transform.
    /// </summary>
    public static List<Solid> ElementSolids(Element e, Options geomOpts)
    {
        List<Solid> solids = [];

        GeometryElement? geom;
        try
        {
            geom = e.get_Geometry(geomOpts);
        }
        catch (Exception)
        {
            return solids;
        }

        if (geom is null)
            return solids;

        void Walk(GeometryObject obj, Transform accTr)
        {
            try
            {
                switch (obj)
                {
                    case Solid solid:
                        TryAddSolid(solids, solid, accTr);
                        break;

                    case Mesh mesh:
                        foreach (Solid s in MeshToSolids(mesh, accTr))
                            TryAddSolid(solids, s, null);
                        break;

                    case GeometryInstance gi:
                    {
                        // GetInstanceGeometry() already returns geometry
                        // pre-transformed into the enclosing coordinate system -
                        // that is the whole reason to call it rather than
                        // GetSymbolGeometry(). Applying gi.Transform again on
                        // top double-transforms every solid: correctly
                        // rotated/scaled relative to itself, but positioned
                        // nowhere near the element. Live-caught - solids were
                        // landing centred on the origin at the family's local
                        // dimensions while get_BoundingBox() was correct.
                        //
                        // So accTr passes through UNCHANGED here, and
                        // gi.Transform is applied ONLY in the GetSymbolGeometry
                        // fallback, which genuinely does return untransformed
                        // local geometry.
                        GeometryElement? inst = null;
                        try
                        {
                            inst = gi.GetInstanceGeometry();
                        }
                        catch (Exception)
                        {
                            inst = null;
                        }

                        if (inst is not null)
                        {
                            foreach (GeometryObject ig in inst)
                                Walk(ig, accTr);
                            break;
                        }

                        Transform t2;
                        try
                        {
                            t2 = accTr.Multiply(gi.Transform);
                        }
                        catch (Exception)
                        {
                            t2 = accTr;
                        }

                        GeometryElement? sym = null;
                        try
                        {
                            sym = gi.GetSymbolGeometry();
                        }
                        catch (Exception)
                        {
                            sym = null;
                        }

                        if (sym is not null)
                        {
                            foreach (GeometryObject sg in sym)
                                Walk(sg, t2);
                        }
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // One bad geometry object must not lose the element's other solids.
            }
        }

        foreach (GeometryObject g in geom)
            Walk(g, Transform.Identity);

        return solids;
    }

    /// <summary>
    /// Intersection volume of two solids, or 0 when they do not overlap.
    ///
    /// FAILS OPEN, deliberately: if the Boolean throws — which Revit does on
    /// certain degenerate or self-intersecting geometry — this returns the
    /// threshold, which the caller reads as a clash. Reporting a pair that might
    /// not overlap is a nuisance; silently dropping one that does is a
    /// coordination failure that reaches site.
    /// </summary>
    public static double SolidIntersectionVolume(Solid a, Solid b, double volThreshold = 1e-6)
        => SolidIntersection(a, b, wantDepth: false, volThreshold).Volume;

    /// <summary>
    /// At or above this, a pair is called a DUPLICATE rather than a clash.
    ///
    /// 0.9 rather than 1.0 because real duplicates are rarely bit-identical: a
    /// copy nudged a millimetre, or re-created from the same family, differs
    /// slightly in both position and computed volume. Below 0.9 the two are
    /// genuinely different objects that happen to overlap heavily, which is a
    /// clash and should be reported as one.
    /// </summary>
    public const double DuplicateThreshold = 0.9;

    /// <summary>What one solid-vs-solid test found.</summary>
    public readonly record struct SolidHit(
        double Volume, double MinDepthFt, XYZ? Point, double DuplicateRatio);

    /// <summary>
    /// The intersection volume, and optionally how DEEP the overlap is.
    ///
    /// Depth is the shortest edge of the intersection solid's own bounding box.
    /// The overlap between two elements is typically a thin slab, and its
    /// thinnest dimension is how far one penetrates the other — which is what a
    /// tolerance should be measured in. A volume threshold cannot tell a 1 mm
    /// graze across a whole duct face from a deep hit on a small pipe, because
    /// the two can have identical volumes. Depth can, and depth is what
    /// Navisworks' tolerance means.
    ///
    /// Depth is computed ONLY when asked for: it costs an extra bounding-box
    /// call per intersecting pair, and the three older tools have no tolerance,
    /// so they must not pay for it.
    ///
    /// FAILS OPEN: a Boolean that throws returns the threshold volume — read by
    /// the caller as a clash — and an INFINITE depth, so a tolerance can never
    /// silently discard a pair the geometry engine could not evaluate.
    ///
    /// (This text sat orphaned above DuplicateThreshold, where two summaries
    /// stacked and the compiler kept the second. No warning, and the reasoning
    /// for the whole depth/fails-open design was attached to a constant about
    /// duplicates.)
    /// </summary>
    public static (double Volume, double MinDepthFt, XYZ? Point) SolidIntersection(
        Solid a, Solid b, bool wantDepth, double volThreshold = 1e-6, bool wantPoint = false)
    {
        SolidHit hit = Intersect(a, b, wantDepth, volThreshold, wantPoint, wantDuplicate: false);
        return (hit.Volume, hit.MinDepthFt, hit.Point);
    }

    /// <summary>
    /// The full result, including how much the two look like the same object.
    ///
    /// The duplicate ratio is the SMALLER of two fractions: how much of the
    /// smaller solid the overlap accounts for, and how alike the two solids'
    /// volumes are. Taking the smaller means one number can only be high when
    /// BOTH are — a small pipe buried inside a big duct scores high on overlap
    /// and low on similarity, and is correctly not a duplicate.
    ///
    /// Judged on the FIRST intersecting solid pair, like the volume beside it.
    /// For a multi-solid family that is an approximation, and a deliberate one:
    /// the alternative is Booleaning every solid against every other on a pair
    /// that has already been reported.
    ///
    /// <paramref name="depthCutoffFt"/>: when the caller only compares the depth
    /// with a tolerance, the depth returned is always on the correct side of
    /// the cutoff but measured no more finely than it takes to tell which side.
    /// Null measures it exactly.
    /// </summary>
    public static SolidHit Intersect(
        Solid a, Solid b, bool wantDepth, double volThreshold = 1e-6,
        bool wantPoint = false, bool wantDuplicate = false, double? depthCutoffFt = null)
    {
        try
        {
            Solid? inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                a, b, BooleanOperationsType.Intersect);

            if (inter is null)
                return new SolidHit(0.0, 0.0, null, 0.0);

            double v;
            try
            {
                v = inter.Volume;
            }
            catch (Exception)
            {
                return new SolidHit(volThreshold, double.PositiveInfinity, null, 0.0);
            }

            if (v <= volThreshold)
                return new SolidHit(0.0, 0.0, null, 0.0);

            return new SolidHit(
                v,
                wantDepth ? MinBoxDimension(inter, depthCutoffFt) : double.PositiveInfinity,
                wantPoint ? Centroid(inter) : null,
                wantDuplicate ? DuplicateRatio(a, b, v) : 0.0);
        }
        catch (Exception)
        {
            return new SolidHit(volThreshold, double.PositiveInfinity, null, 0.0);
        }
    }

    /// <summary>
    /// How much the two solids look like the same object: 0 when they do not,
    /// approaching 1 when they are the same size AND almost entirely overlapping.
    ///
    /// Returns 0 rather than guessing when either volume will not read — an
    /// unmeasurable pair is reported as an ordinary clash, which is the
    /// direction that shows more rather than less.
    /// </summary>
    private static double DuplicateRatio(Solid a, Solid b, double intersectionVolume)
    {
        try
        {
            double va = a.Volume;
            double vb = b.Volume;

            if (va <= 0.0 || vb <= 0.0)
                return 0.0;

            double smaller = Math.Min(va, vb);
            double larger = Math.Max(va, vb);

            double overlap = intersectionVolume / smaller;
            double similarity = smaller / larger;

            return Math.Min(overlap > 1.0 ? 1.0 : overlap, similarity);
        }
        catch (Exception)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// The middle of the overlap — WHERE two elements clash, not merely that
    /// they do.
    ///
    /// The element id already selects the whole duct; the point says which end
    /// of it. It matters most once results are grouped: "this pipe has 12
    /// clashes" reads very differently when they are at 12 places than when they
    /// are all at one.
    ///
    /// Null rather than a guess when the centroid cannot be read — a wrong
    /// coordinate sends someone to the wrong part of the model, which is worse
    /// than no coordinate.
    /// </summary>
    private static XYZ? Centroid(Solid solid)
    {
        try
        {
            return solid.ComputeCentroid();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How thin the overlap is: the shortest edge of the box around the
    /// intersection solid's SURFACE, in feet.
    ///
    /// Infinity when it cannot be measured, so an unmeasurable overlap is kept
    /// rather than suppressed — the same fail-open direction as the Boolean.
    ///
    /// FROM THE SURFACE, NOT FROM Solid.GetBoundingBox. That box bounds the
    /// curves' control points, not the curves, so around anything round it is
    /// loose: two 80 mm pipes grazing by 2 mm measured 5.2 mm deep, and a 5 mm
    /// tolerance kept a 2 mm graze it promised to hide. Triangulation puts every
    /// vertex ON the surface, so its extents are the overlap's own.
    ///
    /// The finest triangulation is the expensive part, so with a cutoff it runs
    /// only when the cheap bounds cannot settle the comparison:
    ///   - the loose box CONTAINS the solid, so its extent is never smaller than
    ///     the true one: loose below the cutoff means the overlap is too.
    ///   - a coarse triangulation's vertices lie on the surface, so its extent is
    ///     never larger than the true one: coarse at or above the cutoff means
    ///     the overlap is too.
    /// Most overlaps are settled by one of the two - a deep hit by the coarse
    /// mesh, a near-miss by the box.
    /// </summary>
    private static double MinBoxDimension(Solid solid, double? cutoffFt = null)
    {
        double loose = LooseMinExtent(solid);

        if (cutoffFt is double cut)
        {
            if (loose < cut)
                return loose;

            double coarse = SurfaceMinExtent(solid, 0.0);
            if (!double.IsInfinity(coarse) && coarse >= cut)
                return coarse;
        }

        double tight = SurfaceMinExtent(solid, 1.0);
        return double.IsInfinity(tight) ? loose : tight;
    }

    /// <summary>
    /// The shortest edge of Revit's own box for the solid, or infinity. Only
    /// read when the box is axis-aligned: a rotated one measures in other axes,
    /// and comparing its edges with the surface extents would prove nothing.
    /// </summary>
    private static double LooseMinExtent(Solid solid)
    {
        try
        {
            BoundingBoxXYZ? bb = solid.GetBoundingBox();
            if (bb is null || !bb.Transform.IsIdentity)
                return double.PositiveInfinity;

            double dx = bb.Max.X - bb.Min.X;
            double dy = bb.Max.Y - bb.Min.Y;
            double dz = bb.Max.Z - bb.Min.Z;

            return Math.Min(dx, Math.Min(dy, dz));
        }
        catch (Exception)
        {
            return double.PositiveInfinity;
        }
    }

    /// <summary>
    /// The smallest edge of the box around the solid's triangulated surface, in
    /// model coordinates - or infinity when it cannot be measured, so a
    /// tolerance never discards a pair it could not size.
    /// </summary>
    private static double SurfaceMinExtent(Solid solid, double levelOfDetail)
    {
        try
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int seen = 0;

            foreach (Face face in solid.Faces)
            {
                Mesh mesh = face.Triangulate(levelOfDetail);
                foreach (XYZ p in mesh.Vertices)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                    seen++;
                }
            }

            if (seen < 4)
                return double.PositiveInfinity;

            return Math.Min(maxX - minX, Math.Min(maxY - minY, maxZ - minZ));
        }
        catch (Exception)
        {
            return double.PositiveInfinity;
        }
    }
}
