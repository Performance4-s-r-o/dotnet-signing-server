namespace DotNetSigningServer.Services.Consents;

/// <summary>Result of comparing the version a form showed with the version in force.</summary>
public enum ConsentVersionVerdict
{
    /// <summary>The form showed the version in force.</summary>
    Current,

    /// <summary>
    /// The form showed an older version and the new one came into force less than
    /// <see cref="ConsentVersionCheck.Grace"/> ago (the form was open during the publication).
    /// The older version is recorded.
    /// </summary>
    PreviousWithinGrace,

    /// <summary>Anything else: the form is reloaded with <c>LegalVersionOutdated</c>.</summary>
    Outdated,
}

/// <summary>
/// Checks the versions a sign-up or consent form showed (hidden fields) against the versions in
/// force from the local snapshot (<c>docs:meta</c>, <c>LegalDocuments</c>). Pure.
/// </summary>
public static class ConsentVersionCheck
{
    /// <summary>How long after a new version comes into force a form showing the previous one is still accepted.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <param name="shown">Version in the form; null when the form did not send one.</param>
    /// <param name="current">Version in force now.</param>
    /// <param name="currentSince">When <paramref name="current"/> came into force; null when unknown.</param>
    public static ConsentVersionVerdict Check(int? shown, int current, DateTimeOffset? currentSince, DateTimeOffset now)
    {
        if (shown == current) return ConsentVersionVerdict.Current;
        if (shown is { } s && s > 0 && s < current
            && currentSince is { } since
            && now >= since && now - since <= Grace)
        {
            return ConsentVersionVerdict.PreviousWithinGrace;
        }
        return ConsentVersionVerdict.Outdated;
    }

    /// <summary>
    /// Checks every document; returns the version to record per document, or null when one
    /// of them is outdated (the whole form is then refused).
    /// </summary>
    public static IReadOnlyDictionary<string, int>? Accept(
        IReadOnlyDictionary<string, int>? shown,
        IEnumerable<ConsentDocumentVersion> current,
        DateTimeOffset now)
    {
        var accepted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in current)
        {
            int? shownVersion = shown != null && shown.TryGetValue(document.Document, out var v) ? v : null;
            switch (Check(shownVersion, document.Version, document.Since, now))
            {
                case ConsentVersionVerdict.Current:
                    accepted[document.Document] = document.Version;
                    break;
                case ConsentVersionVerdict.PreviousWithinGrace:
                    accepted[document.Document] = shownVersion!.Value;
                    break;
                default:
                    return null;
            }
        }
        return accepted;
    }
}
