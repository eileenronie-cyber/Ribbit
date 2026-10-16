namespace Ribbit.Geometry.Clash;

/// <summary>
/// The statuses known for one model and product.
///
/// TWO KEY SPACES, on purpose. Status is keyed by CLASH PAIR now, because
/// keying it by element flags every clash an element takes part in. But reports
/// written before that change carry only an element id, so those load into a
/// legacy element map and are still honoured on lookup. That is what stops the
/// upgrade silently discarding months of triage.
///
/// Nothing writes the legacy map any more — it only ever shrinks, as pairs get
/// re-flagged against the new key.
/// </summary>
public sealed class ClashLedger
{
    private readonly Dictionary<string, string> _byPair = [];
    private readonly Dictionary<StatusKey, string> _byElement = [];

    public IReadOnlyDictionary<string, string> ByPair => _byPair;
    public IReadOnlyDictionary<StatusKey, string> ByElement => _byElement;

    public int Count => _byPair.Count + _byElement.Count;

    /// <summary>Records a pair status. Blank clears nothing — see <see cref="Lookup"/>.</summary>
    public void SetPair(string clashKey, string status)
    {
        if (!string.IsNullOrWhiteSpace(clashKey) && !string.IsNullOrWhiteSpace(status))
            _byPair[clashKey.Trim()] = status.Trim();
    }

    public void SetElement(StatusKey key, string status)
    {
        if (key.ElementId.Length > 0 && !string.IsNullOrWhiteSpace(status))
            _byElement[key] = status.Trim();
    }

    /// <summary>
    /// The status for a row: its PAIR first, then the legacy element entry.
    ///
    /// The order matters. Once a pair has been flagged under the new key, that
    /// is the authoritative answer — an older element-wide flag must not
    /// override it, or re-flagging one clash of a three-clash element would
    /// appear not to take.
    /// </summary>
    public string Lookup(string? clashKey, StatusKey elementKey)
    {
        if (!string.IsNullOrEmpty(clashKey)
            && _byPair.TryGetValue(clashKey!, out string? byPair))
        {
            return byPair;
        }

        return _byElement.TryGetValue(elementKey, out string? byElement) ? byElement : "";
    }

    /// <summary>True when this pair is permanently allowed and should not nag.</summary>
    public bool IsAccepted(string? clashKey, StatusKey elementKey)
        => ClashStatus.Classify(Lookup(clashKey, elementKey)) == StatusBucket.Accepted;
}
