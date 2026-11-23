namespace Freihand.Adapters;

/// <summary>Art eines Adapterfehlers.</summary>
public enum AdapterFailureKind
{
    /// <summary>Weg nicht erreichbar, z. B. Netzausfall; ein Ausweichweg ist möglich, wenn erlaubt (WE-10).</summary>
    Unavailable,

    /// <summary>Der Adapter hat die laufende Sitzung von sich aus abgebrochen, z. B. bei einer Dauergrenze.</summary>
    Aborted,
}

/// <summary>Ein Adapter ist ausgefallen oder hat abgebrochen. Enthält nie Audio oder Text.</summary>
public sealed class AdapterFailureException : Exception
{
    public AdapterFailureException()
        : this(AdapterFailureKind.Unavailable, "Adapter ausgefallen.")
    {
    }

    public AdapterFailureException(string message)
        : this(AdapterFailureKind.Unavailable, message)
    {
    }

    public AdapterFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = AdapterFailureKind.Unavailable;
    }

    public AdapterFailureException(AdapterFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public AdapterFailureKind Kind { get; }
}
