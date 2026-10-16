using Autodesk.Revit.DB;

namespace Ribbit.Native.Revit;

/// <summary>
/// Parameter reading for the clash tools. Ported from lib/clashlib/params.py.
///
/// FOUR READERS, DELIBERATELY NOT COLLAPSED. The source graph uses a different
/// lookup order for different parameters, and behaviour parity means preserving
/// that rather than "fixing" it into one better reader:
///
///   <see cref="GetParamTypeFirst"/>      type before instance, value-string-first
///   <see cref="GetParamInstanceFirst"/>  instance before type, raw-value-first
///   <see cref="GetTypeParameter"/>       full scan only, type before instance
///   <see cref="FindParameter"/>          instance before type, value-string-first
///
/// The last combination exists in neither of the others and the Clash Detect
/// button relies on it: "System Type" is commonly an ElementId-storage parameter
/// whose readable name only comes from AsValueString().
///
/// INSTANCE, NOT STATIC. The Python held these caches as module globals and
/// documented that a caller spanning several documents or several button clicks
/// in one Revit session "should call clear_caches() at the top of its own
/// main()" — a footgun that silently returns another run's values when missed.
/// One reader per run makes stale state unrepresentable, so there is nothing to
/// remember to clear.
/// </summary>
internal sealed class ClashParameterReader
{
    /// <summary>Names resolved through a BuiltInParameter rather than by name.</summary>
    private static readonly Dictionary<string, BuiltInParameter> IdentityBipMap = new()
    {
        ["description"] = BuiltInParameter.ALL_MODEL_DESCRIPTION,
        ["type comments"] = BuiltInParameter.ALL_MODEL_TYPE_COMMENTS,
    };

    // Every cache key carries the DOCUMENT, never a bare element id: a raw id
    // collides across the host and N link documents in one run, which would
    // silently hand back the wrong document's value.
    private readonly Dictionary<(Document Doc, long Id), Element?> _typeElement = [];
    private readonly Dictionary<((Document, long), string), object?> _typeValue = [];
    private readonly Dictionary<object, Dictionary<string, Parameter>> _paramMap = [];
    private readonly HashSet<((Document, long), string)> _typeFirstAbsent = [];
    private readonly Dictionary<((Document, long), string), object?> _instanceFirstType = [];
    private readonly Dictionary<((Document, long), string), object?> _typeParam = [];
    private readonly Dictionary<(Document, long, string), object?> _findParameter = [];
    private readonly Dictionary<((Document, long), string), object?> _findTypeResolve = [];

    /// <summary>Collapses whitespace and lowercases, for tolerant name matching.</summary>
    public static string NormalizeName(string? name)
        => name is null ? "" : string.Join(" ", name.Trim().ToLowerInvariant().Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A parameter value rendered for display or keyword matching.</summary>
    public static string AsText(object? value) => value?.ToString() ?? "";

    // ---------------------------------------------------------- extractors

    /// <summary>
    /// AsValueString()-FIRST extraction, used by the type-first,
    /// type-parameter and find-parameter readers.
    ///
    /// For Double/Integer/ElementId the readable string is tried before the raw
    /// number or id, because that is the only way to get a usable name out of an
    /// ElementId-storage parameter like "System Type".
    /// </summary>
    private static object? ExtractValueStringFirst(Parameter? param)
    {
        if (param is null)
            return null;

        try
        {
            if (!param.HasValue)
                return null;
        }
        catch (Exception)
        {
            // Some parameters throw on HasValue; fall through and try to read.
        }

        StorageType st;
        try
        {
            st = param.StorageType;
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            switch (st)
            {
                case StorageType.String:
                {
                    string? val = param.AsString();
                    return string.IsNullOrEmpty(val) ? null : val;
                }

                case StorageType.Double:
                {
                    string? vs = TryValueString(param);
                    return vs ?? (object)param.AsDouble();
                }

                case StorageType.Integer:
                {
                    string? vs = TryValueString(param);
                    return vs ?? (object)param.AsInteger();
                }

                case StorageType.ElementId:
                {
                    string? vs = TryValueString(param);
                    if (vs is not null)
                        return vs;

                    ElementId eid = param.AsElementId();
                    return eid is null ? null : RevitCompat.IdKey(eid);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string? TryValueString(Parameter param)
    {
        try
        {
            string? vs = param.AsValueString();
            return string.IsNullOrEmpty(vs) ? null : vs;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// RAW-value-first extraction, used by the instance-first reader.
    /// AsValueString() is only an outer fallback here — deliberately NOT tried
    /// first for numeric or id types, unlike <see cref="ExtractValueStringFirst"/>.
    /// </summary>
    private static object? ExtractSimple(Parameter? p)
    {
        if (p is null)
            return null;

        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString();
                case StorageType.Integer:
                    return p.AsInteger();
                case StorageType.Double:
                    return p.AsDouble();
                case StorageType.ElementId:
                {
                    ElementId eid = p.AsElementId();
                    return eid is null ? null : RevitCompat.IdKey(eid);
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the value string below.
        }

        try
        {
            return p.AsValueString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -------------------------------------------------------------- lookups

    private object? LookupValueStringFirst(Element? elem, string paramName)
    {
        if (elem is null)
            return null;

        try
        {
            return ExtractValueStringFirst(elem.LookupParameter(paramName));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every parameter on the element, keyed by normalized name. Cached.</summary>
    private Dictionary<string, Parameter> ParamNameMap(Element? elem, object cacheKey)
    {
        if (_paramMap.TryGetValue(cacheKey, out Dictionary<string, Parameter>? cached))
            return cached;

        Dictionary<string, Parameter> result = [];
        if (elem is not null)
        {
            try
            {
                foreach (Parameter p in elem.Parameters)
                {
                    try
                    {
                        Definition? d = p?.Definition;
                        if (p is null || d is null)
                            continue;

                        string norm = NormalizeName(d.Name);
                        if (norm.Length > 0 && !result.ContainsKey(norm))
                            result[norm] = p;
                    }
                    catch (Exception)
                    {
                        // Skip an unreadable definition, keep the rest.
                    }
                }
            }
            catch (Exception)
            {
                // An element that will not enumerate yields an empty map.
            }
        }

        _paramMap[cacheKey] = result;
        return result;
    }

    private object? BuiltInIdentityValue(Element? elem, string normalizedName, object cacheKey)
    {
        if (elem is null || normalizedName.Length == 0)
            return null;

        if (IdentityBipMap.TryGetValue(normalizedName, out BuiltInParameter bip))
        {
            try
            {
                object? v = ExtractValueStringFirst(elem.get_Parameter(bip));
                if (v is not null)
                    return v;
            }
            catch (Exception)
            {
                // Fall through to the name map.
            }
        }

        Dictionary<string, Parameter> map = ParamNameMap(elem, cacheKey);
        return map.TryGetValue(normalizedName, out Parameter? p) ? ExtractValueStringFirst(p) : null;
    }

    private (Element? TypeElement, (Document Doc, long Id)? Key) TypeElementAndKey(Element? elem)
    {
        if (elem is null)
            return (null, null);

        ElementId typeId;
        try
        {
            typeId = elem.GetTypeId();
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (typeId is null || typeId == ElementId.InvalidElementId)
            return (null, null);

        (Document Doc, long Id) key;
        try
        {
            key = (elem.Document, RevitCompat.IdKey(typeId));
        }
        catch (Exception)
        {
            return (null, null);
        }

        if (_typeElement.TryGetValue(key, out Element? cached))
            return (cached, key);

        Element? typeElem;
        try
        {
            typeElem = elem.Document.GetElement(typeId);
        }
        catch (Exception)
        {
            typeElem = null;
        }

        _typeElement[key] = typeElem;
        return (typeElem, key);
    }

    private object? ScanForValue(Element? elem, string normalizedName)
    {
        if (elem is null)
            return null;

        try
        {
            foreach (Parameter p in elem.Parameters)
            {
                try
                {
                    Definition? d = p?.Definition;
                    if (p is null || d is null)
                        continue;

                    if (NormalizeName(d.Name) == normalizedName)
                        return ExtractValueStringFirst(p);
                }
                catch (Exception)
                {
                    // Skip and keep scanning.
                }
            }
        }
        catch (Exception)
        {
            // Unreadable parameter set - treat as absent.
        }

        return null;
    }

    // -------------------------------------------------------------- readers

    /// <summary>
    /// Type element checked BEFORE instance, value-string-first, then a
    /// BuiltInParameter identity map, then a full parameter-name scan.
    /// </summary>
    public object? GetParamTypeFirst(Element? elem, string paramName)
    {
        if (elem is null)
            return null;

        string normalized = NormalizeName(paramName);
        (Element? typeElem, (Document, long)? typeKey) = TypeElementAndKey(elem);

        if (typeKey is not null)
        {
            ((Document, long), string) cacheKey = (typeKey.Value, normalized);
            if (_typeValue.TryGetValue(cacheKey, out object? cachedVal) && cachedVal is not null)
                return cachedVal;

            object? typeVal = null;
            if (typeElem is not null)
            {
                typeVal = LookupValueStringFirst(typeElem, paramName)
                          ?? BuiltInIdentityValue(typeElem, normalized, typeKey.Value);
            }

            _typeValue[cacheKey] = typeVal;
            if (typeVal is not null)
                return typeVal;
        }

        object? instVal = LookupValueStringFirst(elem, paramName);
        if (instVal is not null)
            return instVal;

        // A type already known to carry no parameter of this NAME skips the full
        // Parameters walk. Only reachable once LookupParameter returned nothing,
        // so a cached "absent" can never mask a real per-instance value: whether
        // a name EXISTS is a property of the type, only its VALUE is per-instance.
        ((Document, long), string)? absentKey =
            typeKey is null ? null : (typeKey.Value, normalized);

        if (absentKey is not null && _typeFirstAbsent.Contains(absentKey.Value))
            return null;

        object instanceKey;
        try
        {
            instanceKey = (elem.Document, RevitCompat.IdKey(elem.Id));
        }
        catch (Exception)
        {
            instanceKey = elem;
        }

        object? instBipVal = BuiltInIdentityValue(elem, normalized, instanceKey);
        if (instBipVal is not null)
            return instBipVal;

        // Record "name absent" only when it is genuinely not in this instance's
        // parameter set, not merely empty here. Identity parameters are fetched
        // via get_Parameter rather than the name map, so they are excluded. The
        // map was already built above, so this costs a dictionary hit.
        if (absentKey is not null && normalized.Length > 0
            && !IdentityBipMap.ContainsKey(normalized)
            && !ParamNameMap(elem, instanceKey).ContainsKey(normalized))
        {
            _typeFirstAbsent.Add(absentKey.Value);
        }

        return null;
    }

    /// <summary>
    /// Instance checked BEFORE type, raw-value-first extraction.
    ///
    /// An EXISTING instance parameter returns its value directly even when
    /// empty, and neither the type nor the scan branch runs — that is the
    /// source's exact semantics and changing it would alter which value wins.
    /// </summary>
    public object? GetParamInstanceFirst(Element? elem, string paramName)
    {
        if (elem is null)
            return null;

        string pname = paramName.Trim();
        string normalized = NormalizeName(pname);

        try
        {
            Parameter? p = elem.LookupParameter(pname);
            if (p is not null)
                return ExtractSimple(p);
        }
        catch (Exception)
        {
            // Fall through to the type branch.
        }

        (Element? typeElem, (Document, long)? typeKey) = TypeElementAndKey(elem);
        ((Document, long), string)? tkey = typeKey is null ? null : (typeKey.Value, normalized);

        if (tkey is not null && _instanceFirstType.TryGetValue(tkey.Value, out object? cached))
            return cached;

        bool typeParamFound = false;
        object? typeVal = null;

        if (typeElem is not null)
        {
            try
            {
                Parameter? p = typeElem.LookupParameter(pname);
                if (p is not null)
                {
                    typeParamFound = true;
                    typeVal = ExtractSimple(p);
                }
            }
            catch (Exception)
            {
                // Fall through to the scan.
            }
        }

        if (typeParamFound)
        {
            if (tkey is not null)
                _instanceFirstType[tkey.Value] = typeVal;
            return typeVal;
        }

        // A value found by this scan is INSTANCE-level - returned but never
        // type-cached, or a per-element value would leak across the whole type.
        try
        {
            string lower = pname.ToLowerInvariant();
            foreach (Parameter p in elem.Parameters)
            {
                try
                {
                    string? n = p?.Definition?.Name;
                    if (n is not null && n.ToLowerInvariant() == lower)
                        return ExtractSimple(p);
                }
                catch (Exception)
                {
                    // Skip and keep scanning.
                }
            }
        }
        catch (Exception)
        {
            // Unreadable parameter set.
        }

        // Only a genuine "not found anywhere" is a type property worth caching.
        if (tkey is not null)
            _instanceFirstType[tkey.Value] = null;

        return null;
    }

    /// <summary>
    /// Full parameter scan ONLY — no LookupParameter fast path — type first,
    /// then instance, value-string-first.
    /// </summary>
    public object? GetTypeParameter(Element? elem, string paramName)
    {
        string normalized = NormalizeName(paramName);
        if (normalized.Length == 0)
            return null;

        (Element? typeElem, (Document, long)? typeKey) = TypeElementAndKey(elem);

        if (typeKey is not null)
        {
            ((Document, long), string) cacheKey = (typeKey.Value, normalized);

            if (_typeParam.TryGetValue(cacheKey, out object? cached))
            {
                // A cached NON-null is the TYPE's value and is genuinely shared
                // by every instance of it.
                //
                // A cached NULL is not an answer — it only says the TYPE carries
                // nothing, which is exactly the case the instance scan below
                // exists to handle. Returning it here made the FIRST instance of
                // a type read its own value and every later one read null, so
                // the same element answered differently depending on what had
                // been looked up before it. The memo is still earned: the type
                // scan is skipped, only the per-instance scan repeats.
                if (cached is not null)
                    return cached;
            }
            else
            {
                object? value = ScanForValue(typeElem, normalized);
                _typeParam[cacheKey] = value;
                if (value is not null)
                    return value;
            }
        }

        return ScanForValue(elem, normalized);
    }

    /// <summary>
    /// Instance LookupParameter before type, but value-string-first extraction —
    /// a combination neither other reader has, and the Clash Detect button
    /// depends on it for "System Type".
    ///
    /// The per-type memo is the port of the change that cut a scan from 43s to
    /// 11s with byte-identical output: without it, every element lacking the
    /// parameter ran a full Parameters walk — tens of thousands on a large model
    /// — where now only the first instance of each type pays for it.
    /// </summary>
    public object? FindParameter(Element? elem, string paramName)
    {
        if (elem is null)
            return null;

        string pname = paramName.Trim();
        string normalized = NormalizeName(pname);

        (Document, long, string)? cacheKey;
        try
        {
            cacheKey = (elem.Document, RevitCompat.IdKey(elem.Id), normalized);
        }
        catch (Exception)
        {
            cacheKey = null;
        }

        if (cacheKey is not null && _findParameter.TryGetValue(cacheKey.Value, out object? hit))
            return hit;

        object? result = null;

        // Instance value - never type-cached, so a genuinely per-element
        // parameter cannot leak across a type.
        try
        {
            Parameter? p = elem.LookupParameter(pname);
            if (p is not null)
                result = ExtractValueStringFirst(p);
        }
        catch (Exception)
        {
            // Fall through to the type branch.
        }

        if (result is null)
        {
            (Element? typeElem, (Document, long)? typeKey) = TypeElementAndKey(elem);
            ((Document, long), string)? tkey = typeKey is null ? null : (typeKey.Value, normalized);

            if (tkey is not null && _findTypeResolve.TryGetValue(tkey.Value, out object? cachedType))
            {
                result = cachedType;
            }
            else
            {
                object? typeVal = null;
                if (typeElem is not null)
                {
                    try
                    {
                        Parameter? p = typeElem.LookupParameter(pname);
                        if (p is not null)
                            typeVal = ExtractValueStringFirst(p);
                    }
                    catch (Exception)
                    {
                        // Fall through to the scan.
                    }
                }

                if (typeVal is not null)
                {
                    // A genuine type-parameter value - safe to share per type.
                    if (tkey is not null)
                        _findTypeResolve[tkey.Value] = typeVal;
                    result = typeVal;
                }
                else
                {
                    object? scanVal = ScanForValue(elem, normalized);
                    if (scanVal is null)
                    {
                        // Absent for this type - cache the negative so siblings
                        // skip the whole Parameters walk.
                        if (tkey is not null)
                            _findTypeResolve[tkey.Value] = null;
                    }
                    else
                    {
                        // Found by the instance fallback: return it, but do NOT
                        // type-cache - it may differ per instance.
                        result = scanVal;
                    }
                }
            }
        }

        if (cacheKey is not null)
            _findParameter[cacheKey.Value] = result;

        return result;
    }
}
