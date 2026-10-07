namespace Vesper.Core.Rfb;

/// <summary>The server sent something that breaks the protocol, or that we refuse to process.</summary>
public class ProtocolException(string message) : Exception(message);

/// <summary>The server closed the connection.</summary>
public sealed class ConnectionClosedException() : Exception("The Mac closed the connection.");

/// <summary>The Mac rejected the credentials, or offered no authentication method we accept.</summary>
public sealed class AuthenticationFailedException(string message, bool credentialsRejected) : Exception(message)
{
    /// <summary>True when the Mac said the user name or password is wrong (as opposed to "can't negotiate").</summary>
    public bool CredentialsRejected { get; } = credentialsRejected;

    /// <summary>The sign-in the Mac refused, when one was attempted.</summary>
    public SignInMethod? Method { get; init; }

    /// <summary>Whether the Mac also offered Apple's Diffie-Hellman sign-in.</summary>
    public bool DiffieHellmanOffered { get; init; }
}

public enum RefusalReason
{
    /// <summary>The server doesn't speak Apple's 003.889, so its session wouldn't be encrypted.</summary>
    NotApple,

    /// <summary>The only sign-in on offer can't prove the Mac's identity, and the path doesn't either.</summary>
    OnlyUnverifiableSignIn,

    /// <summary>The Mac's first encrypted record didn't check out: it never proved it holds its identity key.</summary>
    IdentityNotProven,
}

/// <summary>Vesper stopped because the connection couldn't be made secure. Nothing secret was sent in a readable form.</summary>
public sealed class SecurityRefusedException(RefusalReason reason, string message) : Exception(message)
{
    public RefusalReason Reason { get; } = reason;
}

/// <summary>The Mac turned the connection away before sign-in.</summary>
public sealed class ConnectionRefusedException(string serverReason) : Exception(
    "The Mac turned the connection away before sign-in. It may be limiting attempts after failed sign-ins: wait a minute and try again.")
{
    /// <summary>
    /// The server's own words, sanitized. Unauthenticated (anyone on the path could have written
    /// them), so they're for the diagnostic log, not for showing as if Vesper said them.
    /// </summary>
    public string ServerReason { get; } = serverReason;
}
